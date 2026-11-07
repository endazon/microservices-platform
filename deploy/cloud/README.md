# deploy/cloud — 取引ユニットを動かすための MSP の最小構成（クラウドの単一ノード）

取引ユニット（AST）をクラウドの単一ノード（k3s。Hetzner の 8 GB 帯）で動かすために、MSP から要るものだけを置く
（[IADR-0674](../../.ai-context/adr/IADR-0674_deploy-cloud-minimal.md)。秘匿管理は [IADR-0689](../../.ai-context/adr/IADR-0689_cloud-openbao-eso-in-cluster-credentials.md)）。

| 置くもの | 宣言 |
| --- | --- |
| Postgres（取引ユニットの 7 つの DB・Keycloak の DB。TLS） | `infra/postgres.yaml` |
| 取引ユニットの DB のサービスごとの利用者（各利用者が自分の DB だけを持つ。[IADR-0695](../../.ai-context/adr/IADR-0695_cloud-db-per-service-users.md)） | `infra/postgres-ast-db-users.yaml`・`db-users/` |
| RabbitMQ（取引ユニットのサービスごとのユーザー。TLS） | `infra/rabbitmq.yaml` |
| Keycloak（本番モード `start`・realm `platform`・HTTPS 8443 だけ。[IADR-0693](../../.ai-context/adr/IADR-0693_cloud-keycloak-tls-openbao-pki.md)） | `infra/keycloak.yaml`・`realm-for-cloud.js` |
| 稼働中の realm へ、取り込み元に在って稼働に無い機密クライアントだけを足す Job（[IADR-0700](../../.ai-context/adr/IADR-0700_cloud-ast-per-service-s2s-clients.md)） | `keycloak-clients/` |
| OpenTelemetry の collector（メトリクスは Prometheus へ。受けるのは OTLP を送る Pod だけ・gRPC だけ。[IADR-0697](../../.ai-context/adr/IADR-0697_cloud-infra-pod-least-privilege-and-collector-ingress.md)） | `infra/otel-collector.yaml` |
| LLM ゲートウェイ（MSP の chart。ほかのサービスは描かない） | `values-cloud.yaml` |
| LLM ゲートウェイの当日の費用の枠（再起動を越えて残す。PVC `llmgateway-service-state`。[IADR-0698](../../.ai-context/adr/IADR-0698_llm-daily-cost-state-persistence.md)） | `values-cloud.yaml`（`services.llmgateway.persistence`） |
| 秘匿管理（OpenBao。本番の init・raft）と、OpenBao から Secret へ写す ESO | `vault/`（`vault/eso/`・`vault/*.sh`） |
| 到達の制限（入り・出の既定拒否と要る組だけの許可） | `infra/networkpolicy.yaml`（MSP の名前空間の入り・出も。chart は NetworkPolicy を描かない） |

置かないもの: valkey・qdrant・mailpit・mail-relay・knowledge ユニット・BFF・frontend・認可・MCP・通知・オブジェクトストレージ・
Wiki.js・エッジ・メッシュ・観測の保存先。描画に現れないことは `scripts/deploy-cloud.test.js` が固定する。

🔴 **リポジトリの既定の資格情報を 1 つも使わない。** Secret は描画に含めない。資格情報と TLS の鍵は **OpenBao の中で作り**、
ESO が Secret へ写す（k8s の Secret に作ってから移す形は採らない）。秘密の値はリポジトリにもノードにも平文で置かない。
unseal の鍵とオーナー用の資格は、オーナーが age で暗号化したファイルとして手元の 2 か所に持つ
（[手順書](../../docs/operations/cloud-openbao-init-unseal-runbook.md)）。

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

**版を固定し、取得物の sha256 を確かめてから入れる**（取得のたびに中身が変わるスクリプトを照合せずに root で流さない）。
版・取得元・sha256 は `scripts/lib/pinned-artifacts.sh` の表（`pinned_tool_entry` の `k3s` と `k3s-install`）が 1 つに持つ。
入れるのは公式のリリースのバイナリ（`k3s`）と、同じ版のタグのインストーラ（`install.sh`）である。

手元（リポジトリの根）で取得して照合し、照合に使った値と一緒にノードへ運ぶ:

```sh
K3S_VERSION=v1.35.10+k3s1
. scripts/lib/pinned-artifacts.sh && pinned_work_init
for t in k3s k3s-install; do
  pinned_tool "$t" "$K3S_VERSION" "$PINNED_WORK" >/dev/null || break   # sha256 が表と違えば消して止まる
  e="$(pinned_tool_entry "$t" "$K3S_VERSION")" && printf '%s  %s\n' "${e#* }" "$t-$K3S_VERSION"
done > "$PINNED_WORK/k3s.sha256"
( cd "$PINNED_WORK" && sha256sum -c k3s.sha256 )                    # 2 行とも OK を確かめる（macOS は shasum -a 256 -c）
scp "$PINNED_WORK/k3s-$K3S_VERSION" "$PINNED_WORK/k3s-install-$K3S_VERSION" "$PINNED_WORK/k3s.sha256" <ノード>:/root/
scp deploy/cloud/k3s/audit-policy.yaml deploy/cloud/k3s/config.yaml.d/20-apiserver-audit.yaml <ノード>:/root/   # API サーバーの監査（下）
```

ノードで、運んだものをもう一度照合してから入れる（インストーラには取得をさせない）:

```sh
cd /root && sha256sum -c k3s.sha256                                  # 2 行とも OK でなければ先へ進まない
install -m 0755 -o root -g root k3s-v1.35.10+k3s1 /usr/local/bin/k3s
mountpoint /var/lib/rancher                                          # 暗号化した領域（1-2）がマウントされていることを確かめる
install -d -m 0700 /var/lib/rancher/k3s/server/logs
install -m 0600 -o root -g root audit-policy.yaml /var/lib/rancher/k3s/server/audit-policy.yaml
install -d -m 0755 /etc/rancher/k3s/config.yaml.d
install -m 0600 -o root -g root 20-apiserver-audit.yaml /etc/rancher/k3s/config.yaml.d/20-apiserver-audit.yaml
INSTALL_K3S_SKIP_DOWNLOAD=true INSTALL_K3S_EXEC="server \
  --disable traefik --disable servicelb \
  --secrets-encryption \
  --bind-address 127.0.0.1 --advertise-address 127.0.0.1 --node-ip 127.0.0.1 \
  --write-kubeconfig-mode 0600" sh ./k3s-install-v1.35.10+k3s1
k3s --version                                                        # v1.35.10+k3s1 を確かめ、版と sha256 を運用の記録に残す
test -s /var/lib/rancher/k3s/server/logs/audit.log && echo audit-ok  # API サーバーの監査の記録が書かれ始めたこと
```

- 版は、試験の k3d が使う k3s（`scripts/k8s-local-up.sh` の `K3S_IMAGE`）と同じ minor の、最新の patch に置いた。
- **版を上げるときは**、表の 2 行（URL と sha256）と、上の 2 つの手順の版を対で改める（`scripts/deploy-cloud.test.js` が突き合わせる）。
  sha256 は、バイナリは同じリリースの `sha256sum-amd64.txt` と、インストーラは 2 回取って一致することを確かめてから書く。

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
- **API サーバーの監査の記録**（[IADR-0703](../../.ai-context/adr/IADR-0703_cloud-egress-proxy-audit-log-cert-expiry.md)）: `deploy/cloud/k3s/config.yaml.d/20-apiserver-audit.yaml`
  （`kube-apiserver-arg`）を k3s が起動の引数と同じに読む。方針 `deploy/cloud/k3s/audit-policy.yaml` は Metadata まで（要求・応答の本文を残さない）で、
  Secret・トークン・exec・RBAC の操作は必ず残す。方針と記録は `/var/lib/rancher` の下（暗号化した領域）。インストーラは k3s を起こすので、
  **方針と設定の追加はインストーラの前に置く**。稼働中のノードへ足すときは[手順書](../../docs/operations/cloud-apiserver-audit-log-runbook.md)。
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
- **`systemctl start k3s` の終了コードで成否を判断しない。** 条件が満たされずに起動しなかったときも 0 で終わる。`mountpoint` の確かめを先に行い、起動の後は `systemctl is-active k3s` と Pod の Ready で確かめる。

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

### 前提条件（稼働の前にオーナーが行う）

- 🔴 **LLM の提供元のコンソールで、組織（API キーの属するワークスペース）の支出上限を設定する。** ゲートウェイの当日の費用の上限
  （§4「段 2 の切り替え」）はゲートウェイを通る呼び出しにしか効かず、ゲートウェイの不具合・鍵の漏れ・単価表の誤りには効かない。
  提供元の支出上限は、それらに対する最後の歯止めである。**値はオーナーが決める**（ここには書かない）。設定したこと（日付・設定した人）は運用の記録に残す。
  支出上限に達すると提供元が呼び出しを拒むので、ゲートウェイは上流の失敗として `Sent=false` を返す。
- 設定するまで、クラウドの LLM ゲートウェイを稼働させない（API キーを OpenBao へ入れない）。

手順の詳細（鍵の受け渡し・再起動・作り直し）は[秘匿管理の手順書](../../docs/operations/cloud-openbao-init-unseal-runbook.md)。
スクリプトはどれも `--live` が無ければ何もしない。**秘密の値を引数・環境変数・ディスクに置かない**（age の復号はパイプで Pod の標準入力へだけ流す）。

```sh
kubectl apply -f deploy/cloud/infra/namespaces.yaml
kubectl apply -k deploy/cloud/vault                                   # OpenBao（受け口の証明書の Secret を待つ）
bash deploy/cloud/vault/tls-cert.sh --live issue                      # 受け口（TLS の 8443）の証明書と CA の ConfigMap → OpenBao が封印された状態で起きる（Ready にならないのが正しい）
bash deploy/cloud/vault/init.sh --live --recipients <age の受取人> --out <init>.age              # 一度だけ
bash deploy/cloud/vault/unseal.sh --live --init-file <init>.age --identity <age の識別子>
bash deploy/cloud/vault/configure.sh --live --init-file <init>.age --identity <age の識別子> \
  --recipients <age の受取人> --operator-out <operator>.age          # 初回の root は最後に失効する
bash deploy/cloud/vault/seed-secrets.sh --live --operator-file <operator>.age --identity <age の識別子>   # 7 種（DB はサービスごとの 7 組も）を OpenBao の中で作る
. scripts/lib/pinned-artifacts.sh && pinned_work_init
kubectl apply -k deploy/cloud/external-secrets                       # ESO の名前空間（PSA restricted・到達の制限）を chart より先に作る
helm upgrade --install external-secrets "$(pinned_chart external-secrets 2.8.0 "$PINNED_WORK")" \
  -n external-secrets -f deploy/cloud/vault/external-secrets-values.yaml --wait
node deploy/cloud/realm-for-cloud.js --template deploy/keycloak/microservices-platform-realm.json \
  | kubectl -n platform-infra create configmap keycloak-realm-template --from-file=platform-realm.json=/dev/stdin --dry-run=client -o yaml \
  | kubectl apply -f -                                                 # 雛形（secret を持たない）
kubectl apply -k deploy/cloud/vault/eso                               # SecretStore・ExternalSecret（値は OpenBao から写る）
kubectl wait --for=condition=Ready externalsecret --all -n platform-infra --timeout=300s
kubectl wait --for=condition=Ready externalsecret --all -n microservices-platform --timeout=300s
node deploy/cloud/secret-inventory.js                                 # 参照する Secret の全件と供給元を照らす（区分 2・3 に穴があれば止まる）
kubectl apply -k deploy/cloud          # 基盤（infra）・バックアップ（backup）・観測（observability）と、collector の結線（patch）を一緒に当てる
kubectl -n platform-infra rollout status deploy/postgres deploy/rabbitmq deploy/keycloak deploy/otel-collector --timeout=600s
kubectl -n platform-infra wait --for=condition=Ready pod -l app=keycloak --timeout=600s   # realm の取り込みが終わるまで Ready にならない
bash deploy/cloud/db-users/ast-db-users.sh --live apply              # 取引ユニットの DB のサービスごとの利用者（Job。値は手元へ来ない）
. scripts/lib/recreate-strategy.sh && reconcile_recreate_strategy microservices-platform llmgateway-service   # 既存の RollingUpdate の値を消す（冪等。新規は何もしない）
helm upgrade --install msp deploy/helm/microservices-platform -n microservices-platform \
  -f deploy/cloud/values-cloud.yaml --set-string global.image.tag="$TAG"
```

- `deploy/cloud/infra` だけを当てない。collector の remote write と自己テレメトリの口（patch）、バックアップと観測の宣言、
  外向きの中継（`egress/`）・証明書の期限の測り（`tls-cert-probe/`）が入らない。
- **クラスタの外の 443 へ宛先を名指して出るのは外向きの中継（`egress/`。[IADR-0703](../../.ai-context/adr/IADR-0703_cloud-egress-proxy-audit-log-cert-expiry.md)）だけ**
  （ESO・OpenBao の API サーバー向けの出はポートだけで、宛先を問わず 443・6443 へ出られる。同 IADR の残余 5）。
  LLM ゲートウェイ・バックアップ・Alertmanager は中継を HTTPS_PROXY として使い、中継は名前の許可表に在る宛先の 443 だけへ繋ぐ。
  許可表は宣言（LLM の提供元・Discord）・バックアップの保管先（ConfigMap `platform-backup-s3-target` の `endpoint`）・オーナーが埋める表
  （ConfigMap `egress-proxy-extra-hosts`。heartbeat の宛先など）の和。**新しく作る構成では `kubectl apply -k deploy/cloud` の前に**
  `egress-proxy-extra-hosts` を作る（無いと heartbeat が届かず、外の監視が知らせる）。**稼働中の構成へ入れるとき**は、この順ではなく
  [外向きの中継の手順書](../../docs/operations/cloud-egress-proxy-runbook.md)の移す段（`deploy/cloud-transition/egress-proxy-report`）を経る。
- master realm の一時管理者は常には置かない（`infra/keycloak.yaml` は `KC_BOOTSTRAP_ADMIN_*` を持たない。realm の取り込みに管理者は要らない）。
  稼働中の realm へ手を入れるときだけ `deploy/cloud/keycloak-admin/keycloak-admin.sh --live open` で開き、作業の後に `… --live close` で消して閉じる
  （[手順書](../../docs/operations/cloud-keycloak-admin-runbook.md)。稼働中の構成で初回の起動に作られた一時管理者も、同じ手順書で消す）。
- 基盤の Pod はどれも root でなく capability を持たない（[IADR-0697](../../.ai-context/adr/IADR-0697_cloud-infra-pod-least-privilege-and-collector-ingress.md)）。
  名前空間 `platform-infra`・`microservices-platform` の Pod Security は restricted で、宣言の外の Pod（`kubectl run` ほか）は受け付けで拒まれる。
  **稼働中の構成へ初めて当てるとき**（前の宣言は root の起動器で動いていた）は、当てる前に、データの PVC に root の所有の物が無いことを読みだけで確かめる
  （出力が空でなければ当てない。新しい宣言は所有を付け替えないので、その物を読めずに起動が止まる）:

  ```sh
  kubectl -n platform-infra exec deploy/postgres -- find /var/lib/postgresql/data/pgdata ! -user postgres
  kubectl -n platform-infra exec deploy/rabbitmq -- find /var/lib/rabbitmq ! -user rabbitmq
  ```

  当てると Postgres・RabbitMQ・Keycloak・Prometheus は作り直される（Recreate。取引ユニットは繋ぎ直すまで止まる。Prometheus の保持の値は消える）。
  米国の場の時間外に当て、`rollout status`（上）と、RabbitMQ の起動の直後の復元の行（`kubectl -n platform-infra logs deploy/rabbitmq | grep rabbitmq-boot`）を確かめる。
  15 分の後に警報 `OtlpSendersMissing` が出ていなければ、取引ユニットの 11 のサービスと LLM ゲートウェイの系列が届いている。
  戻すときは前の版の `deploy/cloud` を当てる（名前空間の Pod Security も baseline へ戻る）。
  バックアップの保管先・Discord・heartbeat の資格情報は、[運用手順書](../../docs/operations/cloud-backup-alerts-heartbeat-runbook.md)の準備で作る（作るまで Alertmanager は起動しない）。
- **初回の適用では、Keycloak の Pod が Ready になるまで見る。** realm の取り込みに失敗すると Ready にならない（`kubectl -n platform-infra logs deploy/keycloak` で理由を読む）。
- Keycloak は HTTPS（8443）だけで受ける（証明書は ESO が写す `keycloak-tls`。発行元は `https://keycloak:8443/realms/platform`）。LLM ゲートウェイは
  https を必須にし、基盤の CA で検証する（values-cloud の `global.auth`）。**平文で動いている構成を移すときは、この順ではなく**
  [認可サーバーの TLS の手順書](../../docs/operations/cloud-keycloak-tls-runbook.md)の併存の段（`deploy/cloud-transition/keycloak-tls-coexist`）を経る。
- OpenBao は TLS の 8443 だけで受ける（受け口の証明書は `vault/tls-cert.sh` がオーナーの手元で作る。ESO とオーナーの手順は CA で検証する）。
  **平文の 8200 で動いている構成を移すときは、この順ではなく**[秘匿管理の受け口の TLS の手順書](../../docs/operations/cloud-openbao-tls-runbook.md)の
  併存の段（`deploy/cloud-transition/openbao-tls-coexist`）を経る。取引ユニットのストアを https にするのは、MSP の併存の段と基盤のストアの後。
- 取引ユニットの DB の利用者はサービスごとに分ける（[手順書](../../docs/operations/cloud-db-per-service-users-runbook.md)）。共有の利用者は移行の間だけ残り、
  取引ユニットを入れた後に `ast-db-users.sh --live drop-shared` で消す。稼働中の構成を移すときは手順書の 3 段の順。
- 取引ユニットの側（values-cloud・OpenD・移行の手順）は AST#1525。取引ユニットの名前空間の SecretStore・ExternalSecret は取引ユニットの側が置き、
  `cloud/shared/*`（DB・ブローカ・機密クライアントの secret・CA の証明書）と `cloud/ast/*` を読む（読める範囲は policy `ast-eso`）。
- 取引ユニットのサービス間の認証は、サービスごとの機密クライアント（`ai-stock-trading-svc-<サービス>` の 7 つ。[IADR-0700](../../.ai-context/adr/IADR-0700_cloud-ast-per-service-s2s-clients.md)）で行う。
  **稼働中の構成へ入れるとき**は、`seed-secrets.sh`（無いキーだけを足す）→ ESO の同期 → `bash deploy/cloud/keycloak-admin/keycloak-admin.sh --live open` →
  `bash deploy/cloud/keycloak-clients/keycloak-clients.sh --live check` → `… --live apply` → `keycloak-admin.sh --live close` の順で、取引ユニットの chart より先に当てる（[手順書](../../docs/operations/cloud-s2s-per-service-clients-runbook.md)）。新しく作る構成は realm の取り込みが最初から含む。

## 4. 資格情報

| 値 | 作り手 | OpenBao | 写す先 |
| --- | --- | --- | --- |
| DB の管理者・Keycloak の DB の利用者・Keycloak の管理者・ブローカの既定のユーザー | `seed-secrets.sh`（Pod の中の乱数。利用者名も乱数の接尾辞） | `cloud/platform/*` | `platform-infra` の Secret `postgres`・`keycloak-db`・`keycloak-admin`・`rabbitmq` |
| 取引ユニットの DB の利用者・ブローカの 11 ユーザー・10 の機密クライアント（宣言の 3 つとサービスごとの 7 つ）の secret | 同上 | `cloud/shared/ast-db`・`ast-rabbitmq-users`・`ast-client-secrets` | `platform-infra` の `ast-db`・`rabbitmq-users`・`keycloak-realm-import`（雛形に差し込む）。取引ユニットの側も同じパスを読む |
| 取引ユニットの DB のサービスごとの利用者（7 組。共有の `ast-db` は移行の後に外す） | 同上（利用者名は `ast_<サービス>_<乱数>`） | `cloud/shared/ast-db-<サービス>` | `platform-infra` の `ast-db-<サービス>`（利用者を作る Job が読む）。取引ユニットの側も同じパスを読む |
| サーバ証明書と鍵（Postgres・RabbitMQ・Keycloak・LLM ゲートウェイ） | OpenBao の PKI（`pki_cloud`。CA の鍵は OpenBao の外へ出ない。role の一覧は `vault/lib.sh` の `OB_PKI_ROLES`） | — | `postgres-tls`・`rabbitmq-tls`・`keycloak-tls`・`llmgateway-tls`（ESO の `VaultDynamicSecret`） |
| CA の証明書（公開してよい） | `configure.sh` | `cloud/shared/platform-infra-ca` | ConfigMap `platform-infra-ca`（両方の名前空間） |
| OpenBao 自身の受け口の証明書と鍵 | `vault/tls-cert.sh`（オーナーの手元。OpenBao を開ける側の値なので OpenBao の外。CA の鍵は署名の後に捨てる） | — | Secret `openbao/openbao-tls`。CA の証明書（公開してよい）は ConfigMap `openbao-ca`（基盤の 2 つと取引ユニットの名前空間。ESO のストアが検証に使う） |

- 在る値は作り直さない（`seed-secrets.sh` の 2 回目は何も変えない）。
- 取り込む realm は、宣言から取引ユニットの 3 つの機密クライアント（`ai-stock-trading-svc`・`-llm-caller`・`-owner`）と組み込みの
  クライアントだけを残し、人の利用者・試験の利用者・SMTP を外す（`realm-for-cloud.js`。宣言の開発用の値が残れば止まる）。
  加えて、取引ユニットのサービスごとの 7 つ（`ai-stock-trading-svc-<サービス>`。宣言の `ai-stock-trading-svc` と同じ作り・宛先は取引ユニットの API だけ）を組んで足す。
- 在るパスには無いキーだけを足す（`seed-secrets.sh` の `perkey`。機密クライアントの secret とブローカの利用者の両方に及ぶ。`users.txt` に行が増えれば稼働中の `ast-rabbitmq-users` にもキーが足される。在るキーの値は触らず、稼働の側との不一致は見ない。DB の `userpass` はパスが在れば触らない）。
- Keycloak の管理者は、稼働中の realm へ手を入れる間だけ在る（`keycloak-admin.sh` が開いて消す。値は OpenBao の `cloud/platform/keycloak-admin` のまま）。
  ブローカの既定のユーザーは、サービスごとのユーザーが揃うと消える。
- 取引ユニットだけの値を OpenBao の中で作る口: `seed-secrets.sh gen <cloud/パス> <キー> <バイト数>`（標準の base64）。
- 外から持ち込む値（LLM の鍵・バックアップの保管先・Discord・heartbeat・取引ユニットの外部の資格）は、オーナーが
  `vault/put-external-secret.sh` で標準入力から OpenBao の `cloud/{msp,platform,ast}/*` に書き、ESO が写す
  （[手順書](../../docs/operations/cloud-openbao-init-unseal-runbook.md)の「外から持ち込む値」。在るキーは `--overwrite` が無ければ止まる）。

### 段 2 の切り替え（クラウドでは強制）

| 切り替え | 値 | 置き場 |
| --- | --- | --- |
| ブローカの権限 | narrow（生成物 `infra/rabbitmq-permissions.yaml` の行だけ。行の無いユーザーには権限を付けない）。購読側には購読する（自分が発行しない）exchange の configure を与えず、購読される exchange は同じ表の `exchanges.json` を利用者の権限より先に宣言する | `infra/rabbitmq.yaml` の起動の直後の復元 |
| LLM ゲートウェイの経路の設定の検査 | `Llm__RouteValidation__Mode=Fail`（不足があれば起動しない） | `values-cloud.yaml` |
| LLM ゲートウェイの呼び手ごとの許可表 | `Llm__CallerPolicy__Mode=Enforce`（表の外は 403） | 同上 |
| LLM ゲートウェイの入力・要求数・費用の上限 | `Llm__CallLimits__Mode=Enforce`（超えたら送らない）。当日の費用の上限は全呼び手の合計で 1 日 20 USD（取引ユニットの呼び手 18・既定 0.5。UTC の日）。既定の金額が無ければ起動しない | 同上 |
| LLM ゲートウェイの当日の費用の枠の置き場 | `Llm__CallLimits__SpendStore__Path`（PVC `llmgateway-service-state` の上のファイル。再起動の後は前回の値を読み込んでから判定する。読めない・書けない間は費用を数える呼び出しを送らない）。1 レプリカ・Recreate | 同上 |

- 既定の描画とローカル（`values-local.yaml`・`deploy/local`）は段 1 のまま（`scripts/deploy-cloud.test.js` が固定する）。
- 🔴 **購読側の exchange の configure を外した表の適用の順序**: 取引ユニットの「購読側は購読の exchange を受動で宣言する」変更
  （取引ユニットの chart の `global.rabbitmq.subscribedExchanges: Passive`）のマージと、**取引ユニットのイメージの更新の後に**、
  ConfigMap `rabbitmq-permissions`・`rabbitmq-boot` を当ててブローカを作り直す（`kubectl -n platform-infra rollout restart deploy/rabbitmq`）。
  先に当てると、従来の（非受動で宣言する）購読側の Pod は起動できない（configure が拒まれる）。戻すときは、この表を前の版へ戻してブローカを作り直す。
- 🔴 **共有のデッドレターを外した表の適用の順序**（[IADR-0699](../../.ai-context/adr/IADR-0699_rabbitmq-per-service-dead-letter.md)）: 取引ユニットの
  「デッドレターをサービスごと（`<ServiceName>.dead-letter`）にする」変更を含むイメージを**全 11 サービスに当てた後に**、同じ手順で表を当ててブローカを作り直す。
  先に当てると、旧のイメージの Pod は共有のデッドレター（`wolverine-dead-letter-queue`）の宣言が拒まれ、起動できない。デッドレターは各サービスが自分の
  接頭辞の中で宣言する（定義で先に作らない）。旧の共有のデッドレターのキューの扱い（件数を読みだけで確かめ、空なら消す）は取引ユニットの手順書
  （`docs/operations/broker-per-service-dead-letter-runbook.md`）。戻すときは、この表を前の版へ戻してブローカを作り直す。

## 5. 確かめ方（クラスタに触れない）

```sh
kubectl kustomize deploy/cloud/infra
helm template msp deploy/helm/microservices-platform -n microservices-platform -f deploy/cloud/values-cloud.yaml --set-string global.image.tag=0123456789ab
node scripts/deploy-cloud.test.js    # 上の 2 つを描いて、持ち込まない資源・外への口・既定の資格情報の字面・start-dev が無いことを固定する
```

## 6. 運用: 証明書の更新

- サーバ証明書は OpenBao の PKI が発行する（有効期限 1 年）。ESO が**半年ごとに**作り直して Secret を書き換える。
- 書き換わった後に、消費側（Postgres・RabbitMQ・LLM ゲートウェイ）を作り直して読み直させる（`kubectl rollout restart`）。
  Keycloak は 1 時間ごとに証明書のファイルを読み直すので、作り直さなくてよい（`KC_HTTPS_CERTIFICATES_RELOAD_PERIOD`）。期限は次で読める:
  `kubectl -n platform-infra get secret postgres-tls -o jsonpath='{.data.tls\.crt}' | base64 -d | openssl x509 -noout -enddate`
  （証明書の公開の部分だけを読む。鍵 `tls.key` は読まない）と、CA（10 年）は
  `kubectl -n platform-infra get configmap platform-infra-ca -o jsonpath='{.data.ca\.crt}' | openssl x509 -noout -enddate`。
- すぐに作り直すとき: `kubectl -n platform-infra annotate externalsecret postgres-tls force-sync="$(date +%s)" --overwrite`（RabbitMQ・Keycloak・LLM ゲートウェイも同じ）の後に消費側を作り直す。
- **期限の警報**（[IADR-0703](../../.ai-context/adr/IADR-0703_cloud-egress-proxy-audit-log-cert-expiry.md)）: `tls-cert-probe/` が 4 つの受け口の**出している**証明書の
  期限を 10 分ごとに測り、Prometheus の `cloud-tls-expiry` が鳴らす（残り 150 日で warning・30 日で critical。作り直しの漏れも、消費側の作り直しの漏れも
  残りの減りとして見える）。CA（`platform-infra-ca`）の残り 400 日と、OpenBao の受け口の証明書（`openbao-ca` の notAfter − 365 日で導く）の残り 150 日も鳴らす。
- **封印のあいだは発行できない。** 作り直しは unseal の後に行う。
- OpenBao 自身の受け口の証明書（1 年）は PKI の外で、`bash deploy/cloud/vault/tls-cert.sh --live renew` で半年ごとに作り直す（封印しない。
  CA を併記してから読み直させるので、ESO が届かない時間を作らない。[手順書](../../docs/operations/cloud-openbao-tls-runbook.md) §4）。
