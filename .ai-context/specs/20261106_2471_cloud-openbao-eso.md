---
title: 作業仕様書 — クラウドの最小構成（deploy/cloud）に秘匿管理（OpenBao）と ESO を持ち込み、クラスタの中で作る資格情報と TLS の鍵を OpenBao から供給する（PR ①）
type: spec
status: in-progress
related_ids: [NFR-05, NFR-18, IADR-0674, IADR-0682, IADR-0689]
author: claude
created: 2026-11-06
updated: 2026-11-06
issue: "#2471"
---

# 作業仕様書 — クラウドの秘匿管理（OpenBao・ESO）と、クラスタの中で作る資格情報・TLS の鍵（PR ①）

> 着手 2026-11-06（develop `206836b7`）。判断の記録は **IADR-0689**（新規）。issue は #2471。稼働中のクラスタには何もしない。秘密の値は扱わない（手順の実行は利用者）。

## 起点

- 要求 **NFR-05**（秘匿は Vault）・**NFR-18**。
- 計画: AST の計画 ADR-0080（ADR-0078 決定 5・6・9 の部分改定。2026-11-06 の利用者裁定）。本 PR は同 ADR のフォローアップ 1 の MSP の分。
- 覆す実装の決定: **IADR-0674 決定 3**（資格情報を `bootstrap-secrets.sh` が k8s の Secret に作る）と **決定 4**（秘匿管理を持ち込まない）。
- 形の先例: **IADR-0682**（秘密を引数・環境・履歴・ディスクに置かない手順の形）。

## 裁定と決定（2026-11-06。統括から受けたもの）

| # | 決定 | 出どころ |
|---|---|---|
| 1 | unseal の鍵は 1 分割・閾値 1。age で暗号化したファイルを、オーナーが別々の 2 か所に保管する（置き場所はリポジトリに書かない） | 利用者の裁定 |
| 2 | root トークンは configure の 1 回だけ使って失効させ、要るときは作り直す | 利用者の裁定 |
| 3 | OpenBao 自身の受け口は平文。NetworkPolicy で ESO だけに絞る（オーナーは Pod の中から操作する）。OpenBao 自身の鍵は ADR-0080 の「TLS の鍵」の範囲の外として理由を書く | 利用者の裁定 |
| 4 | 再起動のときのずれ（ESO が作った Secret は残り、unseal を待たずに動く）は受け入れて IADR に書く。計画への環流は統括が起票する | 利用者の裁定 |
| 5 | LLM ゲートウェイの段 2 の 3 つは強制にする（PR ②） | 統括 |
| 6 | AST との境界: MSP は ESO の本体・基盤の側の `SecretStore openbao`・OpenBao の role と policy・`cloud/shared/*`・`seed-secrets.sh`・`gen` の口・`put-external-secret.sh`（PR ②）を持つ。AST の名前空間の SecretStore・ServiceAccount・ExternalSecret は AST が置く | 統括 |
| 7 | PR を 2 本に分ける。① OpenBao・ESO・7 種・TLS（本書）、② 外から持ち込む 4 つ・段 2 の強制。IADR-0689 は ① に置く | 統括 |
| 8 | 機密クライアントは 3 つ（`-svc`・`-llm-caller`・`-owner`）に減らす。`kb-writer`・`kb-reader` は AST がクラウドで使わない | AST（a2808）の確認 |
| 9 | `gen` は標準の base64 で、指定のバイト数の乱数を出す（AST は 32 バイト未満を拒む）。自己試験で固定する | AST |
| 10 | `moomoo-rsa`（`cloud/ast/moomoo-rsa` の `opend_rsa.pem`。PKCS#1）は区分 2。Pod の中で作る口（`gen-rsa`）を推奨。作れないときの代わりを IADR に書く | AST |

## 実測（着手前。本書を書く前に確かめた）

| # | 事実 | 確かめ方 |
|---|---|---|
| 1 | OpenBao 2.7.1 の root の作り直し（`operator generate-root`）は**認証が要る**（`sys/generate-root-token/*`）。認証の要らない旧の口は既定で閉じている（変更履歴: 認証なしの取り消しの問題のため） | 公開の配布物（sha256 を確かめた）の変更履歴と、手元の使い捨ての OpenBao（`/tmp` の raft・ループバック）で実行した |
| 2 | → root を失効させた後に作り直すには、別の認証（権限を絞ったもの）が要る。`sys/generate-root-token/*` だけを持つ token と unseal の鍵で root を作り直せる | 同上（作り直し → `policies=["root"]` → 失効） |
| 3 | `bao write sys/unseal -` は標準入力の JSON（`{"key":…}`）で unseal できる。`bao operator unseal -` は `-` を鍵の字面として扱い、引数なしは端末を要する | 同上 |
| 4 | `bao kv put -mount=… <path> -` は標準入力の JSON を書き、`bao kv patch … key=-` は 1 つの値を標準入力で受ける。在らないパスへの patch は 404 | 同上 |
| 5 | PKI の `keys/generate/exported`（rsa）は PKCS#1 の PEM（`-----BEGIN RSA PRIVATE KEY-----`）を返す。**2048 未満は拒む**（`RSA keys < 2048 bits are unsafe and not supported`） | 同上 |
| 6 | OpenBao のイメージ（宣言と同じ digest。amd64）は busybox（`sh`・`head`・`od`・`base64`・`tr`）を持ち、**openssl の実行ファイルを持たない** | レジストリの層を取得して一覧した |
| 7 | moomoo の公開の資料は、OpenD の RSA の鍵を「1024 ビット」「512・1024 を選べる」と書き、2048 に触れない | `openapi.moomoo.com` の Protocol Introduction |
| 8 | ESO 2.8.0（固定の chart）は `VaultDynamicSecret`（v1alpha1）の生成器と、`ExternalSecret`（v1）の `target.template.templateFrom` を持つ | 固定の chart（sha256 を確かめた）の CRD |
| 9 | `realm-for-cloud.js` を通したクラウドの realm に、クライアントスコープ `ai-stock-trading-api-audience`（`ai-stock-trading-owner` の既定のスコープ）と realm ロール `trading-bot`（owner のサービスアカウント）は残る。試験は固定していない | 宣言から実際に組んだ |
| 10 | k3s の API サーバーはループバック（`--bind-address 127.0.0.1`）。ESO の controller と OpenBao の TokenReview が Pod から API サーバーへ届くかは、クラスタでしか確かめられない | `deploy/cloud/README.md` §1-4 |

## 母集合（規則 9）

`deploy/cloud` の描画が参照する Secret を全件引いた（`secretKeyRef`・`secretName`・`secret:`）。

| Secret | 区分（ADR-0080 決定 1） | 本 PR | OpenBao のパス |
|---|---|---|---|
| `platform-infra/postgres` | 2（DB の管理者） | ① | `cloud/platform/postgres`（username・password） |
| `platform-infra/ast-db` | 2（取引ユニットの DB の利用者） | ① | `cloud/shared/ast-db`（username・password） |
| `platform-infra/keycloak-db` | 2 | ① | `cloud/platform/keycloak-db` |
| `platform-infra/keycloak-admin` | 2 | ① | `cloud/platform/keycloak-admin` |
| `platform-infra/rabbitmq` | 2（既定のユーザー） | ① | `cloud/platform/rabbitmq` |
| `platform-infra/rabbitmq-users` | 2（取引ユニットの 11 ユーザー） | ① | `cloud/shared/ast-rabbitmq-users`（users.txt の利用者名） |
| `platform-infra/keycloak-realm-import` | 2 と同じ種類（client の secret を含む） | ① | 雛形（ConfigMap）＋ `cloud/shared/ast-client-secrets`（3 つの clientId）を ESO の template で組む |
| `platform-infra/postgres-tls`・`rabbitmq-tls`・`microservices-platform/llmgateway-tls` | 3（TLS の秘密鍵） | ① | PKI `pki_cloud` の role `postgres`・`rabbitmq`・`llmgateway-service`（ESO の `VaultDynamicSecret`） |
| ConfigMap `platform-infra-ca`（両方の名前空間） | 公開してよい | ① | `configure.sh` が PKI の CA の証明書から作る（`cloud/shared/platform-infra-ca` にも置き、AST が写せるようにする） |
| `microservices-platform/llm-provider-credentials` | 1（外から持ち込む） | ② | `cloud/msp/llm-provider-credentials` |
| `platform-infra/platform-backup-s3`・`alertmanager-discord`・`alertmanager-heartbeat` | 1 | ② | `cloud/platform/…` |

- AST の側で要る秘密（a2808 の一覧）のうち、区分 2 は DB・ブローカ・3 つの client の secret（`cloud/shared/*`。①が作る）と HMAC の鍵・`moomoo-rsa`（`cloud/ast/*`。AST の手順書が ① の `gen`・`gen-rsa` を呼ぶ）。
- 除外: Pod の中の emptyDir・ConfigMap（設定）・PVC。値を持たない。

## 方式

1. `deploy/cloud/vault/`（新規）:
   - 名前空間 `openbao`、OpenBao の Deployment（宣言と同じ digest・raft・PVC・本番の init。dev モードは使わない）、Service、ServiceAccount と TokenReview の権限、NetworkPolicy（入りは ESO の controller から 8200 だけ、出は DNS と API サーバーだけ）。
   - ESO の値（固定の chart）と、基盤の側の `SecretStore openbao`（`platform-infra`・`microservices-platform`。kubernetes 認証）・ExternalSecret・VaultDynamicSecret（`deploy/cloud/vault/eso/`。CRD の後に当てる）。
   - policy（HCL）: `platform-infra-eso`・`msp-eso`・`ast-eso`・`cloud-operator`。
2. スクリプト（`deploy/cloud/vault/*.sh`。`--live` の opt-in。値は**引数・環境変数・ディスクに置かない**。手元では `age` の復号をパイプの先へだけ流し、Pod の中では標準入力で受ける）:
   - `init.sh`: `operator init`（1 分割・閾値 1）の出力をそのまま手元の `age -R` へ流し、暗号化したファイルだけを作る。
   - `unseal.sh`: 復号した鍵を `bao write sys/unseal -` の標準入力へ流す。
   - `configure.sh`: 初回の root を標準入力で受け、KV（`cloud/`）・PKI（`pki_cloud`。CA の鍵は外へ出さない）・kubernetes 認証と 3 つの role・policy・オーナー用の AppRole（`cloud-operator`。ループバックからだけ）を作り、AppRole の資格を手元の `age -R` へ流し、**最後に root を失効させる**。
   - `seed-secrets.sh`: オーナー用の資格（AppRole）で入り、7 種を **Pod の中で**乱数から作る（在れば触らない）。`gen <path> <key> <bytes>`（標準の base64）と `gen-rsa <path> <key> <bits>`（PKI で作り、作った鍵は PKI から消す。2048 以上）の口を持つ。
   - `root-token.sh`（作り直し）: オーナー用の資格と unseal の鍵で root を作り直し、渡された 1 つの操作の後に失効させる。
3. `deploy/cloud/realm-for-cloud.js`: 機密クライアントを 3 つにする。ESO の template の雛形を出す `--template` を足す（secret の代わりに印を置く）。雛形と値から組んだ結果が `buildCloudRealm` と一致することを試験で固定する。
4. `deploy/cloud/bootstrap-secrets.sh`: Secret を作る部分を外す（**名前は残さず消す**。手順書の旧の行が誤って流されないため）。代わりに `deploy/cloud/secret-inventory.js`（ADR-0080 フォローアップ 4 の突き合わせ）が、描画の参照する Secret の全件を、ExternalSecret の行き先と区分に照らし、供給元の無いものがあれば止まる（② の 4 つは ② で埋めるまで「未供給」として出る）。
5. 文書: `deploy/cloud/README.md`（§3〜§6）・新規の手順書 `docs/operations/cloud-openbao-init-unseal-runbook.md`（初期化・unseal・作り直し・再起動のときに止まるもの・突き合わせ）。
6. 試験:
   - `scripts/deploy-cloud.test.js`: OpenBao・ESO の宣言（digest・dev モードでない・平文の受け口は NetworkPolicy の内側だけ・Secret を描かない）、クラウドの realm に audience のスコープと `trading-bot` が残ること、3 つの機密クライアント、雛形の一致。
   - `deploy/cloud/vault/openbao-scripts.test.sh`（新規。CI の scripts-tests に段を足す）: 固定の OpenBao の配布物（sha256 を確かめる）を手元で起こし、`kubectl` をスタブにして、`init → unseal → configure → seed → gen → gen-rsa → root の作り直し` を実物の OpenBao で通す。値が引数に載らないこと・root が失効していること・policy の境界・`gen` のバイト数と形式・在れば触らないことを固定する。
   - `scripts/scripts.repo.test.js` の #2401 の節（bootstrap の削除の追随）・`scripts/cloud-ops.test.js`（bootstrap の名前の突き合わせの追随）・`scripts/live-scripts.json`（新しいスクリプトの分類）。

## 受け入れ基準

- AC-1: `deploy/cloud` の描画に Secret が 1 つも無く、描画の参照する Secret のうち区分 2・3 のものは、すべて ExternalSecret（OpenBao）が供給する（`secret-inventory.js`。試験）。
- AC-2: OpenBao は dev モードでなく、宣言と同じ digest で、受け口に入れるのは ESO の controller だけ（NetworkPolicy。試験）。
- AC-3: スクリプトは秘密の値を引数・環境変数・ディスクに置かない。unseal の鍵・初回の root・オーナー用の資格は、age で暗号化したファイルとしてだけ手元に残る（実物の OpenBao での試験。引数の記録に値が無いこと）。
- AC-4: configure の後に初回の root は失効している。作り直した root も、操作の後に失効する（同上）。
- AC-5: `seed-secrets.sh` は 7 種を作り、2 回目は何も変えない。`gen` は指定のバイト数の乱数を標準の base64 で書き、在るキーは上書きしない。`gen-rsa` は PKCS#1 の PEM を書き、PKI に鍵を残さない（同上）。
- AC-6: policy の境界: 基盤の側は `cloud/ast/*` を読めない。AST の側は `cloud/platform/*` を読めず、`cloud/shared/*` は名指しの 4 つだけを読める（同上）。
- AC-7: クラウドの realm に、クライアントスコープ `ai-stock-trading-api-audience`（owner の既定のスコープ）と realm ロール `trading-bot`（owner のサービスアカウントだけ）が残る。機密クライアントは 3 つ（試験）。
- AC-8: IADR-0689 が、unseal の鍵・root トークン・OpenBao 自身の受け口・再起動のときに止まるもの・ノードのメモリ・`gen-rsa` の代わりの案を決める。IADR-0674 に覆したことを追記する。

## 範囲の外

- PR ②: 外から持ち込む 4 つ（`put-external-secret.sh` を含む）と段 2 の強制。
- AST の名前空間の SecretStore・ServiceAccount・ExternalSecret・手順書（a2808）。
- 稼働中のクラスタでの確かめ（移行の門の条件 5）。

## 残余

1. Pod から API サーバー（ループバック）へ届くかは、条件 5 で確かめる（実測 10）。届かなければ k3s の起動のフラグを改める別の作業になる。
2. OpenD の RSA の鍵の長さ: 公開の資料は 1024 と書く（実測 7）。OpenBao は 2048 未満を作れない（実測 5）。2048 で OpenD が動くかは**未確認**。
