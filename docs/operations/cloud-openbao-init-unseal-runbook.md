---
title: 運用 Runbook — クラウドの秘匿管理（OpenBao）の初期化・unseal・値の作成・root の作り直し
type: runbook
status: draft
author: claude
created: 2026-11-06
updated: 2026-11-07
---
<!-- trace:
ids: [NFR-05, NFR-18]
adrs: [ADR-0132]
iadrs: [IADR-0704, IADR-0689, IADR-0674, IADR-0682, IADR-0525, IADR-0693, IADR-0695, IADR-0696, IADR-0700]
specs: [20261107_backup-owner-ops-hardening, 20261107_2471_cloud-external-secrets-stage2, 20261106_2471_cloud-openbao-eso, 20261107_cloud-keycloak-tls, 20261107_cloud-db-per-service-users, 20261107_cloud-openbao-tls, 20261107_s2s-per-service-clients]
issues: [#2471, #2401]
-->

# 運用 Runbook: クラウドの秘匿管理（OpenBao）

> **運用仕様書（[`operations.md`](operations.md)）の「秘匿管理」の下位にあたる手順書である。**
> 対象はクラウドの最小構成（単一ノードの k3s・`deploy/cloud/`）の `deploy/cloud/vault/`。ローカルの PoC の秘匿管理の手順とは別である。
>
> **状態を `draft` に留めている理由**: クラウドのノードで初期化と再起動の後の unseal をまだ記録していない（移行の門の条件 5 で行う）。

## 前提

- **手順を流すのはオーナーである。** 秘密の値は画面に出ない。エージェントは値を扱わない。
- 手元（オーナーの端末）に `kubectl`（SSH のトンネル越しに API サーバーへ届く）・`age`・`node` がある。
- age の鍵の組をオーナーが持つ。受取人（公開鍵）のファイルと、識別子（秘密鍵）のファイルである。識別子はノードに置かない。
- スクリプトは秘密の値を**引数・環境変数・ディスクに置かない**。手元では age の復号の出力をパイプで Pod の標準入力へだけ流し、
  Pod の中では標準入力で受ける。コマンド行に値は無いので、シェルの履歴にも残らない。

## 手元に残るもの（2 つのファイル）

| ファイル | 中身 | 作る手順 |
| --- | --- | --- |
| `<init>.age` | unseal の鍵（1 分割・閾値 1）と初回の root（configure の後は失効している） | §1 |
| `<operator>.age` | オーナー用の資格（AppRole。Pod の中のループバックからだけ入れる） | §3 |

- 🔴 **オーナー用の資格（`cloud-operator`）では、`cloud/` の全部の値を読める**（値を作る・足すときに在るかを確かめるため）。漏れれば全部の値が読まれる。unseal の鍵と同じ扱いで保管する。漏れた疑いがあれば §6 の `rotate-operator` で作り直す。

- **どちらも、別々の 2 か所に保管する**（場所はリポジトリに書かない）。片方を失っても、もう片方から戻せる。
- `<init>.age` を両方とも失うと、封印を開けられない。データを捨てて初期化からやり直す（§7）。

## 識別子を守る（2 つのファイルは同じ識別子で開く）

`<init>.age` と `<operator>.age` は、どちらもオーナーの同じ age の識別子で開く（鍵の保持はオーナーが持つ）。2 か所の保管は失くしたときの備えで、
識別子が漏れれば両方が開く。そのため、**識別子そのものを平文で置かない**:

- **パスフレーズで包む**（既定の形）。識別子を `age -p` で暗号化したファイルにし、平文の識別子は消す。手順は `--identity` に包んだファイルを与え、
  復号のたびにパスフレーズを尋ねる（`root-token.sh` は 2 つのファイルを開くので 2 回尋ねる）:
  ```sh
  age -p -o <識別子>.age <識別子のファイル>     # パスフレーズは対話で入れる（引数に書かない）
  age -d -i <識別子>.age <init>.age >/dev/null && echo OK   # 包んだ識別子で開けることを確かめる（中身は捨てる）
  shred -u <識別子のファイル>                    # 平文の識別子を消す（包んだファイルを 2 か所に保管してから）
  ```
- **鍵の装置で持つ**（使えるとき）。age のプラグイン（例: YubiKey の PIV を使うもの）の識別子にし、受取人をその装置の受取人に替えて
  `<init>.age`・`<operator>.age` を作り直す（§6 の rekey・rotate-operator の後に、新しい受取人で書き出す）。
- 手順は、平文の識別子（`AGE-SECRET-KEY-1` で始まる行を持つファイル）を受け取ると警告を出す（中身は表示しない）。包んだ後は
  `OB_IDENTITY_POLICY=enforce` を与えて流す（平文の識別子を受け取ると止まる）。
- 手順は流す前に宛先のクラスタを確かめる（違えば外から持ち込む値を書かずに止まる）。印の記録は[宛先のクラスタの手順書](cloud-owner-target-cluster-runbook.md)。

## 1. 初期化（一度だけ）

```sh
kubectl apply -k deploy/cloud/vault
bash deploy/cloud/vault/tls-cert.sh --live issue   # 受け口（TLS）の証明書。これが無いと Pod は起きない
kubectl -n openbao get pods                    # Running・Ready 0/1（初期化前・封印中は Ready にならない）
bash deploy/cloud/vault/init.sh --live --recipients <受取人のファイル> --out <init>.age
```

- 受け口は TLS だけである。手順（`deploy/cloud/vault/*.sh`）は Pod の中でも https と受け口の CA で話す。証明書の作り直しと、
  平文の構成から移す順序は[受け口の TLS の手順書](cloud-openbao-tls-runbook.md)。

- 出力は Pod の中から手元の `age -R` へそのまま流れ、暗号化したファイル（0600）だけができる。平文はどこにも残らない。
- 受取人で暗号化できることを先に確かめてから初期化する。途中で失敗したら書き出し先を消す。
  **初期化だけが済んで出力を受け取れなかった**（ネットワークが切れた）ときは、鍵を誰も持たない。§7 でデータを捨ててやり直す。

## 2. unseal（初期化の後と、再起動のたび）

```sh
bash deploy/cloud/vault/unseal.sh --live --init-file <init>.age --identity <識別子のファイル>
kubectl -n openbao get pods                    # Ready 1/1 になる
```

- `unseal.sh` は、開けた後に書き込みを受ける状態（active）になるまで待ってから OK を出す（上限 60 秒）。開けた直後は読み取りだけが通り、
  書き込み（configure・seed・put）が拒まれる間がある。60 秒で active にならなければ 1 で止まる。OpenBao のログを確かめてから流し直す。

### 再起動のときに止まるもの

- 解錠の順は **LUKS → k3s → unseal → ESO の同期**である。unseal はノードの再起動と、OpenBao の Pod の作り直しのたびに要る。
- 封印のあいだ止まるもの: ESO の同期と更新・新しい値の取得・証明書の発行・値の作成（§4）。
- **止まらないもの**: ESO が既に写した Secret は k8s に残る（保存時の暗号化と LUKS の下）。Postgres・RabbitMQ・Keycloak・LLM ゲートウェイと
  取引ユニットは、unseal を待たずに起動できる。封印が止めるのは「値を新しく得る・変える」ことである。
- 封印のまま半年を越えると、証明書の作り直しが止まる（期限は 1 年）。再起動の後は、先に unseal する。

## 3. 設定（初期化の直後に一度だけ）

```sh
bash deploy/cloud/vault/configure.sh --live --init-file <init>.age --identity <識別子のファイル> \
  --recipients <受取人のファイル> --operator-out <operator>.age
```

- 初回の root を 1 回だけ使い、**最後に失効させる**（出力の「初回の root を失効させた」を確かめる）。
  失効は、オーナー用の資格を書き出して確かめた後にだけ行う。
- 作るもの: 値の置き場（KV `cloud/`）・基盤との通信の CA（PKI `pki_cloud/`。CA の鍵は OpenBao の外へ出ない）・ESO の認証の role と policy・
  オーナー用の資格。CA の証明書（公開してよい）を ConfigMap `platform-infra-ca` に置く。

### 途中で止まったとき（3 つの経路）

| 止まった段 | そのときの状態 | すること |
| --- | --- | --- |
| 資格の書き出しより前（置き場・CA・認証・資格の作成） | `--operator-out` のファイルは消えている。初回の root は残っている | 理由を直して、**同じコマンドをそのまま流し直す**（済んだものは作り直さない。資格は流し直しのたびに 1 組へ作り直す） |
| ConfigMap `platform-infra-ca` の段 | 同上（ファイルは消す。資格はまだ誰にも渡っていない。流し直しで資格は作り直すので、消したファイルの資格はどのみち無効になる） | 同上 |
| 失効の段（終了コード 3） | `--operator-out` のファイルは**有効なので残っている**（2 か所に保管してよい）。初回の root は残っているか、失効が効いたのに結果が届かなかったかのどちらか | **失効だけを流し直す**: `bash deploy/cloud/vault/configure.sh --live --revoke-only --init-file <init>.age --identity <識別子のファイル>`（既に失効していれば何もせずに終わる。「失効した」と読むのは、unseal 済みの OpenBao が root を拒んだ〔permission denied〕ときだけ。封印中・届かないときは判定せずに終了コード 3 で止まるので、§2 で開けてから流し直す）。全体を流し直さない（ファイルが在るので止まる） |

- 成功して初回の root を失効させた後にもう一度流すと、root で入れないので止まる（何も変えない）。オーナー用の資格を作り直すときは §6。

## 4. 値を作る（configure の後に一度。足すとき）

```sh
bash deploy/cloud/vault/seed-secrets.sh --live --operator-file <operator>.age --identity <識別子のファイル>
```

- DB・Keycloak・ブローカの資格情報（取引ユニットの DB はサービスごとの 7 組も。[DB の利用者の手順書](cloud-db-per-service-users-runbook.md)）と、取引ユニットの機密クライアント（宣言の 3 つとサービスごとの 7 つ。[サービスごとの機密クライアントの手順書](cloud-s2s-per-service-clients-runbook.md)）の secret を、**OpenBao の Pod の中で**乱数から作る。
  値は Pod の外へ出ない。在る値は触らない（2 回目は何も変えない）。機密クライアントの secret とブローカの取引ユニットの利用者は、在るパスに無いキーだけを足す（稼働中の構成へクライアントを足すとき・`users.txt` に行が増えたとき）。DB などの利用者名とパスワードの組は、パスが在れば触らない。
- 取引ユニットだけの値を作る口（取引ユニットの手順書から呼ぶ）:

  ```sh
  bash deploy/cloud/vault/seed-secrets.sh --live --operator-file <operator>.age --identity <識別子のファイル> \
    gen cloud/ast/<名前> <キー> <バイト数>          # 指定のバイト数の乱数を標準の base64 で書く。在るキーは上書きしない
  ```

- OpenD の RSA の鍵（1024 ビット）は OpenBao の中では作れない（OpenBao は 2048 ビット未満を作らない）。オーナーが手元で作り、下の「外から持ち込む値」の口で直に入れる。

### 外から持ち込む値（`put-external-secret.sh`）

外から持ち込む値（LLM の鍵・バックアップの保管先・警報の宛先・取引ユニットの外部の資格）は、オーナーが手元で読み、**標準入力からだけ**流して書く。
値は引数・環境変数・ファイルに置かない（`read -rs` で読み、パイプで渡し、`unset` する）。値の例はここに書かない。

```sh
P="bash deploy/cloud/vault/put-external-secret.sh --live --operator-file <operator>.age --identity <識別子のファイル>"
read -rs v; printf '%s' "$v" | $P cloud/<領域>/<名前> <キー>; unset v          # 1 つのキーを書く
openssl genrsa -traditional 1024 | $P cloud/ast/moomoo-rsa opend_rsa.pem       # OpenD の RSA の鍵（手元のメモリだけを通る）
```

- 書けるのは `cloud/ast`・`cloud/platform`・`cloud/msp` の下だけ（`cloud/shared` は §4 の `seed-secrets.sh` が作る）。
- **キーが在れば止まる**（終了コード 5。何も変えない）。作り直すときだけ `--overwrite` を付ける（同じパスのほかのキーは残す）。
- 終了コード: 0 書いた・1 失敗（パスやキーの有無を読めないときも、書かずに 1）・2 引数の誤りか値が空・3 `--live` が無い・5 キーが在る。
- 書いた後は ESO が 1 時間以内に写す。すぐに写すときは `kubectl -n <名前空間> annotate externalsecret <名前> force-sync="$(date +%s)" --overwrite`。

| 値 | パス | キー | 写す先（名前空間 / Secret） |
| --- | --- | --- | --- |
| LLM の提供元の鍵 | `cloud/msp/llm-provider-credentials` | `anthropic-api-key`（埋め込みを使うなら `voyage-api-key` も） | `microservices-platform` / `llm-provider-credentials` |
| バックアップの保管先の鍵 | `cloud/platform/platform-backup-s3` | `access-key-id`・`secret-access-key` | `platform-infra` / `platform-backup-s3` |
| 警報の Discord の webhook | `cloud/platform/alertmanager-discord` | `webhook-url` | `platform-infra` / `alertmanager-discord` |
| 外の監視の ping の URL | `cloud/platform/alertmanager-heartbeat` | `ping-url`・`discord-ping-url`・`discord-fail-url` | `platform-infra` / `alertmanager-heartbeat` |
| 取引ユニットの外部の値 | `cloud/ast/moomoo-credentials`・`cloud/ast/moomoo-rsa`・`cloud/ast/ast-owner-totp`・`cloud/ast/ast-secrets` | 取引ユニットの手順書が決める | `ai-stock-trading`（取引ユニットの側の ExternalSecret） |

## 5. 突き合わせ（移行の門の条件 3）

```sh
node deploy/cloud/secret-inventory.js --strict
```

- 宣言が参照する Secret の全件を、供給元（OpenBao から写す ExternalSecret）と区分（外から持ち込むもの・クラスタの中で作るもの・TLS の鍵）に照らす。
  1 つでも供給元が無ければ止まる。表に無い名前の Secret が増えたときも止まる（区分を決めてから足す）。
- 外から持ち込む値も OpenBao から写すので、`--strict` は全件に供給元があるときに通る。

## 6. root が要る操作（root を作り直し、操作の後に失効させる）

```sh
bash deploy/cloud/vault/root-token.sh --live --init-file <init>.age --operator-file <operator>.age --identity <識別子のファイル> apply-policies
bash deploy/cloud/vault/root-token.sh --live --init-file <init>.age --operator-file <operator>.age --identity <識別子のファイル> apply-pki-roles
bash deploy/cloud/vault/root-token.sh --live --init-file <init>.age --operator-file <operator>.age --identity <識別子のファイル> \
  rotate-operator --recipients <受取人のファイル> --operator-out <新しい operator>.age
```

- OpenBao 2.x の root の作り直しには認証が要る。オーナー用の資格で入り、unseal の鍵を加えて作る。作った root は操作の後に失効させる。
- `apply-policies`: policy（`deploy/cloud/vault/policies/`）を改めたときに当て直す。
- `apply-pki-roles`: PKI のサーバ証明書の role（`deploy/cloud/vault/lib.sh` の `OB_PKI_ROLES`）を足したときに書き直す。在る role は同じ値で書き直されるだけ。
  role を足したら、発行を許す policy も `apply-policies` で当て直す（例: [認可サーバーの TLS の手順書](cloud-keycloak-tls-runbook.md)の段 0）。
- `rotate-operator`: オーナー用の資格のファイルを失った・漏れた疑いのときに作り直す。古い資格はすべて消える。2 か所の保管を差し替える。

## 7. 初期化からやり直す（鍵を失ったとき）

1. OpenBao を止め、データ（PVC `openbao-data`）を消す: `kubectl delete -k deploy/cloud/vault`（名前空間ごと消える）。
2. §1 から流す。値は作り直しになる。基盤と取引ユニットの消費側（DB の利用者・ブローカの利用者・Keycloak の secret）も、新しい値で作り直す。
