# deploy/cloud — 取引ユニットを動かすための MSP の最小構成（クラウドの単一ノード）

取引ユニット（AST）をクラウドの単一ノード（k3s。Hetzner の 8 GB 帯）で動かすために、MSP から要るものだけを置く
（[IADR-0674](../../.ai-context/adr/IADR-0674_deploy-cloud-minimal.md)）。

| 置くもの | 宣言 |
| --- | --- |
| Postgres（取引ユニットの 7 つの DB・Keycloak の DB。TLS） | `infra/postgres.yaml` |
| RabbitMQ（取引ユニットのサービスごとのユーザー。TLS） | `infra/rabbitmq.yaml` |
| Keycloak（本番モード `start`・realm `platform`） | `infra/keycloak.yaml`・`realm-for-cloud.js` |
| OpenTelemetry の collector（受けて捨てる） | `infra/otel-collector.yaml` |
| LLM ゲートウェイ（MSP の chart。ほかのサービスは描かない） | `values-cloud.yaml` |
| 到達の制限（入り・出の既定拒否と要る組だけの許可） | `infra/networkpolicy.yaml`（MSP の名前空間の入り・出も。chart は NetworkPolicy を描かない） |

置かないもの: valkey・qdrant・mailpit・mail-relay・knowledge ユニット・BFF・frontend・認可・MCP・通知・オブジェクトストレージ・
Wiki.js・エッジ・メッシュ・秘匿管理（OpenBao）・観測の保存先。描画に現れないことは `scripts/deploy-cloud.test.js` が固定する。

🔴 **リポジトリの既定の資格情報を 1 つも使わない。** Secret は描画に含めず、`bootstrap-secrets.sh` が初回に乱数で作る
（値は表示しない）。秘密の値はリポジトリに置かない。

## 1. ノードの準備（一度だけ）

### 1-1. ファイアウォール（Hetzner Cloud Firewall）

- 受けるのは **SSH（TCP 22）だけ**。送り元は管理する端末の固定 IP に絞れるなら絞る。
- **80・443・6443・50000 は開けない。** 画面を公開しないので、外から受ける口は SSH のほかに無い。
- SSH は**鍵認証だけ**にする（`/etc/ssh/sshd_config.d/10-hardening.conf`）:

  ```
  PasswordAuthentication no
  KbdInteractiveAuthentication no
  PermitRootLogin prohibit-password
  ```

### 1-2. データの暗号化（LUKS）

k3s のデータ（`/var/lib/rancher`）を、暗号化した領域に置く。Hetzner の Volume を足して LUKS で暗号化し、そこへ置く:

```sh
cryptsetup luksFormat /dev/disk/by-id/scsi-0HC_Volume_<id>      # パスフレーズは対話で入れる（引数に書かない）
cryptsetup open /dev/disk/by-id/scsi-0HC_Volume_<id> rancher
mkfs.ext4 /dev/mapper/rancher
mkdir -p /var/lib/rancher && mount /dev/mapper/rancher /var/lib/rancher
```

- 再起動の後は `cryptsetup open` と `mount` を人が行い、それから k3s を起こす（パスフレーズはノードに置かない。手順は 1-5）。
- **k3s は暗号化した領域をマウントする前に起動させない**（起動すると `/var/lib/rancher` がルートのディスクの上に平文で作られる）。1-4 の drop-in と `systemctl disable k3s` で止める。

### 1-3. 時刻同期・更新の時間帯

- 時刻同期は **chrony** を入れる: `apt-get install -y chrony && systemctl enable --now chrony`（`chronyc tracking` で確かめる）。
- OS の自動更新（`unattended-upgrades`）は入れるが、**再起動を伴う更新は米国の場の時間外（JST 07:00〜21:00）だけ**にする:
  - `/etc/apt/apt.conf.d/52unattended-upgrades-local`:

    ```
    Unattended-Upgrade::Automatic-Reboot "false";
    ```

  - タイムゾーンは `timedatectl set-timezone Asia/Tokyo`。`apt-daily-upgrade.timer` の `OnCalendar` も JST 07:00〜21:00 の中に置く。
  - **自動の再起動はしない**（`Automatic-Reboot "false"`）。無人で再起動すると、k3s が上がらないまま止まる（取引も止まる。平文は出ない）。
    再起動が要る更新は `/var/run/reboot-required` が在ることで分かる（`ls /var/run/reboot-required` と、理由は `/var/run/reboot-required.pkgs`）。
    人が JST 07:00〜21:00（米国の場の時間外）に `systemctl reboot` し、1-5 の手順で解錠して k3s を起こす。
  - k3s が上がらないまま止まったことは、監視の外からの生存の確かめ（heartbeat。MSP#2402）で気付ける。

### 1-4. k3s

```sh
curl -sfL https://get.k3s.io | INSTALL_K3S_EXEC="server \
  --disable traefik --disable servicelb \
  --secrets-encryption \
  --bind-address 127.0.0.1 --advertise-address 127.0.0.1 --node-ip 127.0.0.1 \
  --write-kubeconfig-mode 0600" sh -
```

インストールの**直後に**、k3s が暗号化した領域のマウントの前に起動しないようにする（二重に止める）:

```sh
mkdir -p /etc/systemd/system/k3s.service.d
cat > /etc/systemd/system/k3s.service.d/10-require-encrypted-data.conf <<'EOF'
[Unit]
# /var/lib/rancher が LUKS の領域のマウント点でなければ起動しない（ルートのディスクに平文で作らない）。
ConditionPathIsMountPoint=/var/lib/rancher
RequiresMountsFor=/var/lib/rancher
EOF
systemctl daemon-reload
systemctl disable k3s      # 起動時に自動で起こさない。マウントの後に人が systemctl start k3s する（1-5）
```

- `--disable traefik --disable servicelb`: 入口（Ingress・LoadBalancer）を持たない。描画にも LoadBalancer の Service と hostPort は無い（試験で固定）。
- `--secrets-encryption`: Secret を保存時に暗号化する（`k3s secrets-encrypt status` で確かめる）。
- **API サーバーは公開 IP に出さない**（`--bind-address 127.0.0.1`）。管理（kubectl・helm）は SSH のトンネル越しに行う:

  ```sh
  ssh -N -L 6443:127.0.0.1:6443 <ノード>      # 手元の kubeconfig は https://127.0.0.1:6443 を向ける
  ```

### 1-5. 再起動の後の解錠と k3s の起動（人が行う）

```sh
cryptsetup open /dev/disk/by-id/scsi-0HC_Volume_<id> rancher     # パスフレーズは対話で入れる
mount /dev/mapper/rancher /var/lib/rancher
mountpoint /var/lib/rancher                                       # 「is a mountpoint」を確かめてから起こす
systemctl start k3s
kubectl -n platform-infra get pods                                # すべてが Ready に戻るまで見る
```

- マウントせずに `systemctl start k3s` しても、drop-in の条件で起動しない（`systemctl status k3s` に条件が満たされない旨が出る）。

## 2. イメージ

最初の稼働ではレジストリを使わない。手元でビルドして SSH で取り込み、**digest を記録して固定する**:

```sh
TAG="$(git rev-parse --short=12 HEAD)"
docker build -t cloud-local/microservices-platform/llm-gateway:"$TAG" -f src/platform/backend/Services/LlmGateway/Dockerfile .   # 文脈はリポジトリの根（scripts/k8s-local-images.sh と同じ）
docker save cloud-local/microservices-platform/llm-gateway:"$TAG" | ssh <ノード> 'k3s ctr images import -'
ssh <ノード> "k3s ctr images ls name==cloud-local/microservices-platform/llm-gateway:$TAG"   # DIGEST の列を記録する（運用の記録）
```

- chart へは `--set-string global.image.tag="$TAG"` で渡す（`latest` は描画で落ちる）。
- 外部のイメージ（Postgres・RabbitMQ・Keycloak・collector）は宣言の digest で固定してある（`deploy/local/infra` と同じ digest。試験が突き合わせる）。

## 3. 配備の順序

```sh
kubectl apply -f deploy/cloud/infra/namespaces.yaml
ANTHROPIC_API_KEY_FILE=<鍵の 0600 のファイル> bash deploy/cloud/bootstrap-secrets.sh --live   # 値は表示しない。在る Secret は触らない
kubectl apply -k deploy/cloud/infra
kubectl -n platform-infra rollout status deploy/postgres deploy/rabbitmq deploy/keycloak deploy/otel-collector --timeout=600s
kubectl -n platform-infra wait --for=condition=Ready pod -l app=keycloak --timeout=600s   # realm の取り込みが終わるまで Ready にならない
helm upgrade --install msp deploy/helm/microservices-platform -n microservices-platform \
  -f deploy/cloud/values-cloud.yaml --set-string global.image.tag="$TAG"
```

- **初回の適用では、Keycloak の Pod が Ready になるまで見る。** realm の取り込みに失敗すると Ready にならない（`kubectl -n platform-infra logs deploy/keycloak` で理由を読む）。
- 取引ユニットの側（values-cloud・OpenD・移行の手順）は AST#1525。取引ユニットが読む Secret（`ast-db`・`rabbitmq-users`・
  `keycloak-client-secrets`）と CA（ConfigMap `platform-infra-ca`）の写し方も、取引ユニットの側の手順が持つ。

## 4. 資格情報

- `bootstrap-secrets.sh` は、DB の管理者・取引ユニットの DB の利用者・Keycloak の DB の利用者・Keycloak の管理者・ブローカの既定のユーザー・
  取引ユニットのサービスごとのユーザー・取引ユニットの 5 つの機密クライアントの secret を、**初回に乱数で作る**。
  利用者名も乱数の接尾辞を付ける。在る Secret は触らない（作り直しで稼働中の値と食い違わせない）。
- 取り込む realm は、宣言から取引ユニットの 5 つの機密クライアントと組み込みのクライアントだけを残し、secret を乱数へ差し替え、
  人の利用者・試験の利用者・SMTP を外して組む（`realm-for-cloud.js`。宣言の開発用の値が残れば止まる）。
- Keycloak の管理者・ブローカの既定のユーザーは、初回の起動の後は使わない（管理者は Keycloak の DB が持ち、ブローカの既定の
  ユーザーはサービスごとのユーザーが揃うと消える）。
- 証明書: `bootstrap-secrets.sh` がその場で CA を作り、Postgres・RabbitMQ・LLM ゲートウェイ（取引ユニットからの https の受け口）の
  サーバ証明書を発行する。**CA の鍵は保存しない。**
  作り直すときは組（`platform-infra` の `postgres-tls`・`rabbitmq-tls`・ConfigMap `platform-infra-ca`、`microservices-platform` の
  `llmgateway-tls`・ConfigMap `platform-infra-ca`）を消してから `bootstrap-secrets.sh` を流し、Postgres・RabbitMQ・Keycloak・
  LLM ゲートウェイと取引ユニットの消費側を作り直す。**組が一部だけ在るときは、bootstrap は作り直さずに止まる**（在る証明書と別の CA にしない）。
- **LLM の鍵**: 鍵を 1 行で書いた 0600 のファイルを `ANTHROPIC_API_KEY_FILE` に渡す。後から入れるときは
  `kubectl -n microservices-platform delete secret llm-provider-credentials` の後に同じように流し、
  `kubectl -n microservices-platform rollout restart deploy/llmgateway-service` で作り直す。
- 秘匿管理（OpenBao・ESO）は持ち込まない。理由と残余は IADR-0674。

## 5. 確かめ方（クラスタに触れない）

```sh
kubectl kustomize deploy/cloud/infra
helm template msp deploy/helm/microservices-platform -n microservices-platform -f deploy/cloud/values-cloud.yaml --set-string global.image.tag=0123456789ab
node scripts/deploy-cloud.test.js    # 上の 2 つを描いて、持ち込まない資源・外への口・既定の資格情報の字面・start-dev が無いことを固定する
```

## 6. 運用: 証明書の更新

- `bootstrap-secrets.sh` が発行する CA とサーバ証明書（Postgres・RabbitMQ・LLM ゲートウェイ）の有効期限は **825 日**（`CERT_DAYS` の既定）。
  CA の鍵を保存しないので、更新は**組ごとの作り直し**になる（1 枚だけを延ばすことはできない）。
- 発行した日と期限を記録しておく。期限は次で読める（値ではなく証明書の公開の部分だけを読む）:
  `kubectl -n platform-infra get configmap platform-infra-ca -o jsonpath='{.data.ca\.crt}' | openssl x509 -noout -enddate`
- **期限の 30 日前まで**に、場の時間帯（JST 22:30〜05:00）を避けて、次の順で作り直す:
  1. 組を消す: `platform-infra` の Secret `postgres-tls`・`rabbitmq-tls` と ConfigMap `platform-infra-ca`、
     `microservices-platform` の Secret `llmgateway-tls` と ConfigMap `platform-infra-ca`。
  2. `bootstrap-secrets.sh --live` を流す（在る資格情報は触らず、証明書の組だけを作る）。
  3. 取引ユニットの名前空間の CA の写しを新しいものへ差し替える（AST#1525 の手順）。
  4. Postgres・RabbitMQ・Keycloak・LLM ゲートウェイ、取引ユニットのサービスの順に作り直す（`kubectl rollout restart`）。
- 組が一部だけ在る状態で `bootstrap-secrets.sh` を流すと、作り直さずに止まる（在る証明書と別の CA にしない）。
