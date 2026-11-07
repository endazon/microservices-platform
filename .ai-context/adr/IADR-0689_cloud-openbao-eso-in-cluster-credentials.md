---
title: IADR-0689 クラウドの最小構成に秘匿管理（OpenBao）と ESO を持ち込み、クラスタの中で作る資格情報を OpenBao の中で作り、TLS の鍵を OpenBao の PKI で発行する。unseal の鍵とオーナー用の資格は age で暗号化してオーナーが手元に持ち、root は使うたびに作り直して失効させる（IADR-0674 決定 3・4 を覆す。#2471）
type: impl-adr
status: Accepted
related_ids: [NFR-05, NFR-18, IADR-0674, IADR-0682, IADR-0525, IADR-0591, IADR-0678, IADR-0684, IADR-0692, IADR-0696]
author: claude
created: 2026-11-06
updated: 2026-11-07
related_specs:
  - ../specs/20261106_2471_cloud-openbao-eso.md
  - ../specs/20261107_2471_cloud-external-secrets-stage2.md
---

# IADR-0689: クラウドの秘匿管理（OpenBao・ESO）と、クラスタの中で作る資格情報・TLS の鍵

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-06
- 決定者: 利用者の裁定（2026-11-06。unseal の鍵・root・OpenBao 自身の受け口・再起動のときのずれ）・統括の決定（境界・PR の分け方・段 2 の強制）・claude（方式）

## 起点・関連

- 要求 **NFR-05**（秘匿は Vault。Vault は OpenBao と読む）・**NFR-18**。issue #2471。
- 計画: AST の計画 ADR-0080（ADR-0078 決定 5・6・9 の部分改定）のフォローアップ 1 の MSP の分。「本物の秘密」は、外から持ち込む資格情報・
  **クラスタの中で乱数で作る資格情報**（IADR-0674 決定 3 の 7 種と同じ種類の値）・**TLS のサーバ証明書の秘密鍵**の 3 つであり、
  OpenBao から供給されるまでクラウドへ移さない。k8s の Secret に作ってから移す形は採らない。
- 覆す: **IADR-0674 決定 3**（`bootstrap-secrets.sh` が資格情報と証明書を k8s の Secret に作る）・**決定 4**（秘匿管理を持ち込まない）。
- 前提: IADR-0525（OpenBao・raft・監査は設定ファイルで宣言）・IADR-0682（秘密を引数・環境・履歴・ディスクに置かない）。
- 本 IADR は PR ①（OpenBao・ESO・7 種・TLS）に置く。外から持ち込む 4 つと段 2 の強制は PR ② が本 IADR への追記で引く。

## コンテキストと課題

- クラウドの構成は秘匿管理を持たず、`bootstrap-secrets.sh` が資格情報と証明書を k8s の Secret に作っていた（IADR-0674 決定 3・4）。計画の裁定（ADR-0080）で、これでは移行の門（条件 3）を満たさない。
- 実測（作業仕様書 §実測。手元の使い捨ての OpenBao 2.7.1 で確かめた）:
  1. OpenBao 2.x の root の作り直し（`generate-root`）は**認証が要る**。認証の要らない旧の口は既定で閉じている（変更履歴: 認証なしの取り消しの問題）。
  2. `bao write sys/unseal -` は標準入力で unseal できる。`bao kv put … -`・`bao kv patch … key=-` は値を標準入力で受ける。
  3. PKI の `keys/generate/exported` は RSA の鍵を PKCS#1 の PEM で返すが、2048 ビット未満を拒む（決定 7）。
  4. OpenBao のイメージは busybox を持ち、openssl の実行ファイルを持たない。
  5. moomoo の公開の資料は OpenD の RSA の鍵を 1024 ビットと書く（2048 に触れない）。

## 決定

### 決定 1 — unseal の鍵は 1 分割・閾値 1。初期化の出力は Pod の中から手元の age へ直に流す

- `init.sh` は `bao operator init -key-shares=1 -key-threshold=1 -format=json` の出力を、`kubectl exec` の標準出力から手元の `age -R <受取人>` へ
  そのままパイプで流し、暗号化したファイル（0600）だけを作る（利用者の裁定）。平文はノードのディスク・手元のディスク・端末・シェルの履歴のどれにも残らない。
- オーナーはこのファイルを別々の 2 か所に保管する。場所はリポジトリに書かない（利用者の裁定）。
- 受取人で暗号化できることを先に確かめてから初期化し、途中で失敗したら書き出し先を消す（壊れた鍵のファイルを残さない）。
- `unseal.sh` は復号した鍵を `bao write sys/unseal -` の標準入力へだけ流す。
- ［2026-11-07 追記 / #2471］`unseal.sh` は、開けた（sealed=false）後に書き込みを受ける状態（active。`bao status` の `"is_self": true`）になるまで待ってから戻る（上限 60 秒。
  lib.sh の `wait_active`）。OpenBao 2.7.1 の raft は開けた直後にまず読み取りだけを受ける standby に入り、active になるまで書き込みを
  `local node not active but active cluster node not found`（500）で拒む（実測）。この間も token lookup は通るため、開けた直後の root の失効だけが落ちていた
  （CI の実物の OpenBao の試験で観測。ストレージの fsync を遅らせると手元でも同じ所で落ちる）。configure の失効の段（`revoke_root`）も同じ待ちを持ち、
  active にならなければ失効させず判定もせずに止まる（終了コード 3）。

### 決定 2 — root は configure の 1 回だけ使って失効させる。作り直しはオーナー用の AppRole と unseal の鍵で行う

- `configure.sh` は初回の root を標準入力で受け、設定の最後に `token revoke -self` で失効させる（利用者の裁定）。
- 🔴 **実測 1 により、root を失効させた後に作り直すには、別の認証が要る。** オーナー用の AppRole `cloud-operator` を作る（この形は利用者の裁定 2026-11-06。オーナーが持つファイルは init と operator の 2 つ）。
  - 入れるのはループバックからだけ（`token_bound_cidrs`・`secret_id_bound_cidrs` = `127.0.0.1/32`。オーナーは `kubectl exec` で Pod の中から使う）。トークンは 15 分。
  - 権限（policy `cloud-operator`）は、値の作成と足し込み（`cloud/` の KV の作成・読み取り・patch。消さない）・root の作り直し（`sys/generate-root-token/*`）だけ。
    policy・認証・マウントは変えられない。
  - 資格（role_id・secret_id）は、決定 1 と同じく Pod の中から手元の `age -R` へ流し、暗号化したファイルとしてだけ持つ（2 か所に保管）。
- root が要る操作（policy の当て直し・オーナー用の資格の作り直し）は `root-token.sh` が行う。オーナー用の資格で入り、unseal の鍵を加えて root を作り、
  決まった操作の後に失効させる。作り直しの OTP と符号化した root は Pod の中だけにあり、手元へ返さない。
  - OTP と nonce は Pod の中の `bao` の引数に一時に載る（`generate-root` の CLI が引数でしか受けない）。Pod の外へは出ない。OpenBao の Pod の中を読める者は、OpenBao の記憶域も読める（得るものが増えない）。
- 採らなかった: 認証の要らない旧の口を開ける（`disable_unauthed_generate_root_endpoints=false`）— 閉じた理由（認証なしの取り消し）を戻すことになる。
  root を失効させずに持つ — 利用者の裁定（失効させる）に反する。

### 決定 3 — OpenBao 自身の受け口は平文。届く相手を NetworkPolicy で ESO だけに絞る

- 受け口は平文の 8200。入りは名前空間 `external-secrets` の controller の Pod からだけ、出は名前解決と API サーバー（TokenReview）だけ（`deploy/cloud/vault/networkpolicy.yaml`。段 1 を持たず、最初から強制）。
  オーナーは `kubectl exec` で Pod の中のループバックを使う（kubelet を経るので、外へ口を開けない）。
- **OpenBao 自身の受け口の鍵は、ADR-0080 の「TLS の鍵」の範囲の外とする**（利用者の裁定）。理由:
  - ADR-0080 決定 1 の 3. は、`bootstrap-secrets.sh` がその場の CA で発行していたサーバ証明書の鍵（Postgres・RabbitMQ・LLM ゲートウェイ）を指す。OpenBao の受け口の鍵はそこに無い。
  - OpenBao の受け口の鍵は OpenBao から供給できない。封印のあいだは OpenBao が自分で発行できず、OpenBao を開ける前に要る値だからである（unseal の鍵と同じく「OpenBao を開ける側」の値にあたる）。
  - 鍵を置かない（平文にして届く相手を 1 つに絞る）ほうが、ノードに置く鍵が 1 つ減る。メッシュの無い単一ノードで、ESO の controller と OpenBao の間はノードの中の通信である。
- 残る危険（ノードの中の通信路を読める者は ESO のトークンと値を読める）は §残余 3。
- ［2026-11-07 追記 / IADR-0696］**本決定の「受け口は平文」を改めた。** 所有者の裁定（2026-11-07「OpenBao も TLS にする」）が、2026-11-06 の裁定
  （受け口は平文＋NetworkPolicy）を改めたためである。受け口は TLS の 8443 だけになり、ESO とオーナーの手順は受け口の CA で検証する。
  受け口の証明書は OpenBao の PKI では作らず、オーナーが流す `tls-cert.sh` が OpenBao の外で作る（上の「OpenBao から供給できない」理由はそのまま、
  出どころの決定の理由になった）。「鍵を置かない」は改め、受け口の鍵は Secret `openbao/openbao-tls` に置く。
  入りの送り元を ESO の controller だけに絞る NetworkPolicy は残す（TLS と重ねる。ポートは 8443）。詳細と移る順序は IADR-0696。

### 決定 4 — TLS の鍵は OpenBao の PKI で発行する（CA の鍵は外へ出さない）

- PKI `pki_cloud` の root を `internal` で作る（CA の鍵は OpenBao の中だけ。従来の「CA の鍵を捨てる」より強い: 鍵が手元のディスクを一度も通らない）。CA は 10 年。
- サーバ証明書は ESO の `VaultDynamicSecret` が `pki_cloud/issue/<role>` を呼んで発行し、Secret `postgres-tls`・`rabbitmq-tls`・`llmgateway-tls` に写す。
  鍵は OpenBao の中で作られる。role は名指しの名前（短い名前・名前空間つき・`.svc`・`.svc.cluster.local`）だけを許し、発行した証明書を保存しない（`no_store`）。
- 有効期限は 1 年、ESO の作り直しは半年ごと。書き換わった後は消費側を作り直して読み直させる（README §6）。
- CA の証明書（公開してよい）は `configure.sh` が ConfigMap `platform-infra-ca`（両方の名前空間）と `cloud/shared/platform-infra-ca` に置く（取引ユニットの側が写す）。
- 採らなかった: 手元で鍵を作って OpenBao へ書く — 鍵が手元を通る。PKI なら OpenBao の外を通らない。

### 決定 5 — 再起動のときは、unseal まで「値を新しく得る・変える」が止まる。ESO が写した Secret は残り、消費側は unseal を待たずに動く

- 解錠の順は LUKS → k3s → unseal → ESO の同期。封印のあいだ止まるもの: ESO の同期と更新・新しい値の取得・証明書の発行・値の作成。
- **止まらないもの**: ESO が既に写した Secret は k8s に残る（保存時の暗号化と LUKS の下。`deletionPolicy: Retain`）。Postgres・RabbitMQ・Keycloak・
  LLM ゲートウェイと取引ユニットは unseal を待たずに起動できる。
- 🔴 **ADR-0080 §結果 の「再起動の後の解錠の順（LUKS → k3s → unseal → ESO の同期）が済むまで、DB・ブローカ・Keycloak も資格情報を得られない」とずれる**
  （利用者の裁定: ずれを受け入れて本 IADR に書く）。計画への環流は planning#873（下の文案で起票した）。

  > ADR-0080 §結果 の「再起動の後の解錠の順（LUKS → k3s → unseal → ESO の同期）が済むまで、DB・ブローカ・Keycloak も資格情報を得られない。
  > 取引ユニットだけでなく基盤の最小構成も、unseal を待つ」は、実装（MSP の IADR-0689）と合わない。ESO は OpenBao の値を k8s の Secret へ写し、
  > 写した Secret は k8s の保存時の暗号化と LUKS の下に残る。そのため再起動の後は、LUKS の解錠と k3s の起動で DB・ブローカ・Keycloak・取引ユニットは
  > 既に写した資格情報で動く。unseal まで止まるのは、ESO の同期と更新・新しい値の取得・証明書の発行・値の作成である。
  > unseal で「起動」まで止める形（写した Secret を残さない形）は、再起動のたびに全サービスの起動が人の unseal を待つ運用の重さに対して、
  > LUKS の解錠が既に人を要することとの差が小さいので採らなかった（利用者の裁定 2026-11-06）。
  > ADR-0080 §結果 の当該の文を、上の実装に合わせて改めることを求める。

### 決定 6 — 値の置き場と読み手の境界（取引ユニットとの取り決め）

- ストアは**名前空間ごとの `SecretStore openbao`**（kubernetes 認証）。ESO は ClusterSecretStore・ClusterExternalSecret・PushSecret を処理しない（values で切る）。
  1 つの名前空間から別の名前空間の値を引く口を持たない。
- KV v2 のマウントは `cloud/`。パスは `cloud/<読み手の領域>/<名前>`。property の名前は k8s の Secret のキーの名前と 1 対 1。

| パス | 中身 | 読める role（ServiceAccount） |
| --- | --- | --- |
| `cloud/platform/*` | 基盤だけが使う値（postgres・keycloak-db・keycloak-admin・rabbitmq。`username`・`password`） | `platform-infra-eso`（`platform-infra/eso-openbao`）。名指しの 4 つだけ |
| `cloud/shared/ast-db` | 取引ユニットの DB の利用者（`username`・`password`） | `platform-infra-eso` と `ast-eso` |
| `cloud/shared/ast-rabbitmq-users` | ブローカの取引ユニットの 11 ユーザー（キーは users.txt の利用者名） | 同上 |
| `cloud/shared/ast-client-secrets` | 3 つの機密クライアントの secret（キーは clientId） | 同上 |
| `cloud/shared/platform-infra-ca` | CA の証明書（`ca.crt`。公開してよい） | `ast-eso` |
| `cloud/ast/*` | 取引ユニットだけの値（キーは取引ユニットが決める） | `ast-eso`（`ai-stock-trading/ast-eso-reader`） |
| `cloud/msp/*` | MSP の名前空間だけの値（PR ②） | `msp-eso`（`microservices-platform/eso-openbao`） |

- MSP が持つもの: ESO の本体・基盤の側の `SecretStore openbao`・OpenBao の role と policy・`cloud/shared/*`・`seed-secrets.sh`・`gen`・`put-external-secret.sh`（PR ②）。
- 取引ユニットが持つもの: 取引ユニットの名前空間の SecretStore・ServiceAccount `ast-eso-reader`・ExternalSecret・手順書。

### 決定 7 — クラスタの中で作る値は OpenBao の Pod の中で作る（`seed-secrets.sh`・`gen`）

- 7 種（IADR-0674 決定 3 の範囲。機密クライアントは 3 つに減らした〔決定 9〕）を、Pod の中の `/dev/urandom` から作り、`bao kv put … -` の標準入力で書く。
  値は Pod の外へ出ない。在れば触らない。利用者名も乱数の接尾辞を付ける（IADR-0674 決定 3 の性質を保つ）。
- `gen <cloud/パス> <キー> <バイト数>`: 指定のバイト数（16〜1024）の乱数を**標準の base64**（パディングつき・1 行）で書く。在るキーは上書きしない（取引ユニットの HMAC の鍵が使う。取引ユニットは 32 バイト未満を拒む）。
- 🔴 **OpenD の RSA の鍵（`cloud/ast/moomoo-rsa` の `opend_rsa.pem`。区分 2）は 1024 ビットとし、OpenBao の中では作らない。**
  - 根拠（取引ユニットの側が確かめた）: 手元の PoC は 1024 ビットで、2048 で動いた証拠は無い。取引ユニットの手順書、MSP の BFF の `GeneratedRsaKeySizeBits = 1024`、IADR-0456 決定 3（鍵長 1024 は OpenD の要件）、公開の資料（実測 5）が同じ値を指す。
  - OpenBao の PKI は 2048 ビット未満を作らず、OpenBao のイメージは openssl を持たない（実測 3・4）。そのため OpenBao の中では作れない。
  - **形**: オーナーが手元で `openssl genrsa -traditional 1024` の出力を `put-external-secret.sh cloud/ast/moomoo-rsa opend_rsa.pem` へ**直にパイプで流す**（鍵はディスクに書かない。手元のプロセスのメモリを通る）。`put-external-secret.sh` は PR ② が入れる。
  - PKI で RSA の鍵を作る口（当初の `gen-rsa`）は、使う相手が無いので置かない（`keygen/` のマウントと policy の権限も置かない）。

### 決定 8 — `bootstrap-secrets.sh` を消し、参照と供給元の突き合わせを `secret-inventory.js` に置く

- `bootstrap-secrets.sh` は残さない（手順書の旧の行が誤って流されないため）。証明書の組の作り直しの手順（README §6 の旧版）も消した。
- `deploy/cloud/secret-inventory.js`: 宣言が参照する Secret の全件を、供給元（ExternalSecret）と区分（ADR-0080 決定 1 の 1〜3）に照らす。区分 2・3 に供給元が無ければ止まる。
  `--strict`（移行の判定）は区分 1 の未供給も数える。表に無い名前の Secret が増えたら止まる（ADR-0080 フォローアップ 4 の手段）。

### 決定 9 — クラウドの realm の機密クライアントは 3 つ。realm は ESO の template で組む

- `realm-for-cloud.js` の `CLOUD_CLIENTS` を `ai-stock-trading-svc`・`-llm-caller`・`-owner` の 3 つにした（`kb-writer`・`kb-reader` はニュースの RAG を持ち込まないクラウドで取引ユニットが使わない。取引ユニットの側が values-cloud の描画を全件引いて確かめた）。
- `--template` は secret の代わりに Go の template の印（`{{ index . \`<clientId>\` }}`）を置いた雛形を出す。雛形は secret を持たないので ConfigMap `keycloak-realm-template` に置き、
  ESO が `cloud/shared/ast-client-secrets` の値を差し込んで Secret `keycloak-realm-import` を作る。
- クラウドの realm に、クライアントスコープ `ai-stock-trading-api-audience`（owner の既定のスコープ）と realm ロール `trading-bot`（owner のサービスアカウントだけ）が残ることを試験で固定した
  （取引ユニットがクラウドで owner のトークンの aud / azp を強制する前提）。

### 決定 10 — ノードのメモリ

- 常駐が増える分（要求 / 上限）: OpenBao 96Mi / 256Mi、ESO の controller 48Mi / 160Mi・webhook 32Mi / 96Mi・cert-controller 32Mi / 96Mi。合計で要求 208Mi・上限 608Mi。
- IADR-0674 決定 4 の懸念（8 GB のノードで取引ユニットから数百 MiB を取る）は、この幅で受け入れる。稼働での実測（条件 5）で上限を見直す。

### 決定 11 — ［2026-11-07 追記 / #2471］外から持ち込む値は、オーナーが `put-external-secret.sh` で標準入力から書く（PR ②）

- 呼び方: `<値> | put-external-secret.sh --live --operator-file <operator>.age --identity <識別子> [--overwrite] cloud/<ast|platform|msp>/<名前> <キー>`。
  - 値は**標準入力からだけ**受け、そのまま 1 つのキーの値にする（末尾の改行も変えない。PEM をそのまま流せる）。空なら書かない。
  - オーナー用の資格は age の復号からパイプで Pod の標準入力へ流し、値はその後ろへ続ける（手元のディスクに置かない）。Pod の中では表示しない。
  - キーが在れば止まる（終了コード 5。何も変えない）。上書きは `--overwrite` のときだけ（同じパスのほかのキーは残す）。
  - 🔴 パスとキーの有無は、OpenBao が「無い」と明示したとき（パス: `No value found at cloud/metadata/<パス>`・キー: `Field "<キー>" not present in`。2.7.1 の CLI で確かめた）だけ「無い」と読む。
    権限・通信・封印で読めないときは書かずに 1 で止まる（「無い」と読むと `kv put` がパスの既存のキーを全部置き換える）。パスが在れば常に `patch`、`put` はパスが無いと確かめたときだけ。
    同じ判定を `seed-secrets.sh`（7 種の作成と `gen`）にもそろえた（lib.sh の `OB_POD_KV_STATE`）。
  - `cloud/shared` へは書かない（基盤と取引ユニットが共有する値は `seed-secrets.sh` が OpenBao の中で作る。区分 2）。
  - 終了コード: 0 書いた・1 失敗・2 引数の誤りか値が空・3 `--live` が無い・5 キーが在る。
  - 取引ユニットの呼び方（前置きを空白で分けて読み、末尾に「パス」「キー」を足し、値は標準入力）とそのまま合う。`--overwrite` は前置きの後ろ（パスの前）に置ける。
- 書いた値は ESO が写す。基盤の側の 4 つ（`llm-provider-credentials`・`platform-backup-s3`・`alertmanager-discord`・`alertmanager-heartbeat`）の ExternalSecret を足し、
  `dataFrom.extract` で在るキーを全部写す（任意のキー〔`voyage-api-key`〕を許す）。policy は名指しのパスの read だけを足した（`msp-eso` に LLM の鍵、`platform-infra-eso` に 3 つ）。
  これで `secret-inventory.js --strict`（区分 1 を含む全件）が通る。
- OpenD の RSA の鍵（決定 7）は `openssl genrsa -traditional 1024 | put-external-secret.sh … cloud/ast/moomoo-rsa opend_rsa.pem` で入れる。

### 決定 12 — ［2026-11-07 追記 / #2471］クラウドの段 2 の切り替えを強制にする（PR ②）

| 切り替え | 値 | 置き場 | 前提から外れること |
| --- | --- | --- | --- |
| ブローカの権限（IADR-0681） | narrow。起動の直後の復元が、生成物 `deploy/cloud/infra/rabbitmq-permissions.yaml` の行だけを当てる。行の無いユーザーには権限を付けない。表は非 optional | `deploy/cloud/infra/rabbitmq.yaml` | クラウドは段 1 を持たない（vhost 全体へ倒さない）。生成物は `gen-rabbitmq-permissions.js --write` がローカルの表の取引ユニットの行から作り、既定の照合が食い違いを止める（`deploy/cloud/infra` は変換を持たないので生成物をコミットする。IADR-0674 決定 1） |
| 経路の設定の検査（IADR-0677） | `Llm__RouteValidation__Mode=Fail` | `values-cloud.yaml` の `extraEnvAppend` | 配備の `appsettings.json` を検査の規則に当てると、不足は 0 件である（未知のモデル・空の宛先・単価の欠落のどれも無い。2026-11-07 に宣言から引いて確かめた） |
| 呼び手ごとの許可表（IADR-0684） | `Llm__CallerPolicy__Mode=Enforce` | 同上 | IADR-0684 決定 4 の前提（稼働で段 1 の違反を確かめる・gRPC で段 2 の拒否を一度は実測する）を経ない。クラウドは REST だけを使い（gRPC の受け口を外した。IADR-0674 決定 2a）、取引ユニットの呼び手は既定の表に在る。移行の門（段 1 の直しはクラウドでは段 2）が前提に優先する（統括の決定 2026-11-06） |
| 入力・要求数・費用の上限（IADR-0678） | `Llm__CallLimits__Mode=Enforce` | 同上 | IADR-0678 の前提（報告を所有者が確かめてから切り替える）を経ない。上限は既定の値（費用の上限は呼び手ごとに設定していない）。理由は上と同じ |

- 入れないもの: 内部の REST の門（IADR-0686。AI 分析・Wiki を置かない）・BFF の流量の上限（IADR-0687。BFF を置かない）・ローカルの起動器と調停の Job の切り替え。
- 既定の描画とローカル（`values-local.yaml`）は段 1 のまま。`scripts/deploy-cloud.test.js` が、クラウドの描画は段 2・既定とローカルは段 1 であることを固定する。
- ［2026-11-07 追記 / IADR-0692］上の表の「費用の上限は呼び手ごとに設定していない」は、段 2 で費用の判定が働かない継ぎ目だった。
  IADR-0692 が当日の費用の上限（合計 1 日 20 USD）を `values-cloud.yaml` に置き、段 2 で金額が無ければ起動しないようにした。

## 結果

- 良い影響: クラスタの中の資格情報と TLS の鍵が NFR-05 の秘匿管理の下に入る。CA の鍵は OpenBao の外へ出ない。unseal の鍵・root・オーナー用の資格は、平文でリポジトリにもノードにも無い。
- 悪い影響: 解錠の段が 1 つ増える（unseal）。鍵のファイルを 2 つ（init・operator）、それぞれ 2 か所に保管する手間が増える。封印のあいだは値の作り直し・証明書の発行が止まる。

## 残余

1. **Pod から API サーバー（ループバックに bind）へ届くか**: ESO の controller と OpenBao の TokenReview は API サーバーを使う。k3s の API サーバーはループバックに bind している（README §1-4）。
   クラスタでしか確かめられない（移行の門の条件 5 で確かめる）。届かなければ k3s の起動のフラグを改める別の作業になる。
2. OpenBao の出の NetworkPolicy は、API サーバーの宛先の IP を名指せないため、ポート（443・6443）だけで絞る。
3. OpenBao の受け口は平文（決定 3）。ノードの中の通信路を読める者は ESO のトークンと値を読める（その者はノードの root であり、k8s の Secret も読める）。
   ［2026-11-07 追記 / IADR-0696］受け口を TLS にしたので、この残余は閉じた（受け容れの理由に頼らない）。代わりに受け口の鍵が Secret に在ること（IADR-0696 残余 1）が残る。
4. ESO の名前空間（`external-secrets`）に NetworkPolicy を置いていない（chart の既定のまま）。
5. OpenD の RSA の鍵（決定 7）は、オーナーの手元のプロセスのメモリを通る（OpenBao の中で作れないため）。ディスクには書かない。
6. 外から持ち込む 4 つ（LLM の鍵・バックアップの保管先・Discord・heartbeat）は PR ② まで「供給元なし」（`secret-inventory.js`）。移行の門は満たさない。
   ［2026-11-07 追記 / #2471］決定 11 で解消した（`secret-inventory.js --strict` が全件で通る）。

## ［2026-11-07 追記 / IADR-0700］決定 6・決定 9 の「3 つの機密クライアント」と「在れば触らない」を改めた

- クラウドの realm の機密クライアントは、宣言から残す 3 つ（`DECLARED_CLOUD_CLIENTS`）に、取引ユニットのサービスごとの 7 つ（`SERVICE_CLIENTS`。宣言には置かず
  realm-for-cloud.js が組む）を足した 10 である（`CLOUD_CLIENTS`）。`cloud/shared/ast-client-secrets` のキーも 10。
- `seed-secrets.sh` は、在るパスには無いキーだけを足す（在るキーの値は触らない）。「在れば触らない」はキーの単位になった。
- 稼働中の realm へは `deploy/cloud/keycloak-clients/` の Job で、無い機密クライアントだけを足す（`--import-realm` は在る realm を飛ばすため）。
