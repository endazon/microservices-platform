---
title: 試験アカウント e2e-* を realm の宣言と後追い Job に足し、資格情報を OpenBao → ESO の Secret e2e-accounts から差し込む（値はリポジトリに置かない。#2024 / E-30）
type: spec
status: done
related_ids: [NFR-10, NFR-18, ADR-0026, ADR-0124, IADR-0294, IADR-0369, IADR-0518, IADR-0546, IADR-0570]
author: claude
created: 2026-10-21
updated: 2026-10-22
issue: "#2024"
---

# 仕様書: 試験アカウント e2e-* の宣言と secret の供給経路（#2024 / E-30）

> 本仕様書は実装着手前に作成した（着手 2026-10-21）。実装の基点は MSP `origin/develop` `7297af58`。
> 設計の正本は #2013 のコメント 3（稼働中の PoC で E2E を流すための設計）§1。判断は IADR-0570 に置く。

## 起点

- #2024（親 #2013 の分割表 E-30。波 2）。計画 ID: NFR-10（MFA 必須）、NFR-18（秘密の扱い）。計画 ADR: ADR-0026（認証は IdP・TOTP 必須）、ADR-0124（対になる秘密）。
- 関連 IADR: IADR-0294（MFA の検証器と TOTP）・IADR-0369（realm の後追い Job と境界）・IADR-0518（取り込み元の Secret）・IADR-0546（自己管理のロール）。

## 要件（#2013 コメント 3 §1 から）

- realm `platform` に 6 アカウントを宣言する。

  | ユーザー名 | realm ロール | グループ（＝属性） |
  |---|---|---|
  | `e2e-user` | なし | `/clearance/internal`, `/department/engineering` |
  | `e2e-operator` | `platform-operator` | `/clearance/internal`, `/department/engineering` |
  | `e2e-admin` | `platform-admin`, `platform-operator` | `/clearance/restricted`, `/department/engineering` |
  | `e2e-noaccess` | なし | `/clearance/public` のみ |
  | `e2e-revocable` | なし | `/clearance/internal` のみ |
  | `e2e-trading-owner` | `trading-owner` | `/clearance/internal`, `/department/engineering` |

- `requiredActions` に `CONFIGURE_TOTP` を置かず、OTP 資格情報を宣言で持たせる（方式 A）。MFA は外さない。
- 資格情報の実値（パスワード・TOTP の秘密）をリポジトリに入れない。realm JSON はプレースホルダ `${E2E_<ROLE>_PASSWORD}` / `${E2E_<ROLE>_TOTP_SECRET}` だけを持つ。
- 値は OpenBao（KV）→ ESO → Secret `e2e-accounts` から差し込む。`ESO=1` で Secret が出来る。
- `reconcile-realm.js` が既存 realm に 6 ユーザーを足す。
- 回転手順を `deploy/local/README.md` の e2e 節に書く（#2013 の「issues.md E-02」は分割表に無い。本 issue で扱う）。

## 読み取った事実（着手前）

- **AST のロール `trading-owner` は realm `platform` に在る**（`roles.realm` に `trading-owner` / `trading-service`。`developer` と `service-account-ai-stock-trading-owner` が既に持つ）。連結配備の認証は `platform` に一本化済み（ADR-0038 / IADR-0324）。よって `e2e-trading-owner` へ realm ロールとして宣言で付与できる。AST の realm（`infra/keycloak/realm-export.json`）には足さない（設計 §1.1）。`e2e-user` には付けない。
- ESO の ExternalSecret は `deploy/local/vault/eso/` に在る（#2024 本文の `deploy/local/eso/` は存在しない）。新規ファイルは既存の置き場へ置く。KV のパスの慣習は `secret/msp/<name>`（remoteRef `msp/<name>`。policy `eso-read` は `secret/data/msp/*` の read を許す）。設計 §1.4 の `microservices-platform/e2e-accounts` ではなく **`msp/e2e-accounts`** とする（policy を広げずに済む）。
- Secret の読み手（Keycloak の取り込み元を作る [3/7]・後追い Job）は namespace `platform-infra` に居る。Job の `secretKeyRef` / `envFrom` は同じ namespace しか読めないので、Secret `e2e-accounts` は **`platform-infra`** に置く（設計 §1.4 は `microservices-platform` と書くが、読み手の側に寄せる）。runner は `kubectl -n platform-infra get secret e2e-accounts` で読む。
- **順序**: `k8s-local-up.sh` は [3/7]（取り込み元の Secret）→ [4/7]（Keycloak）→ 後追い Job（fail-closed）→ … → ESO ブロックの順に進む。新規クラスタでは [3/7] と最初の後追いの時点で `e2e-accounts` はまだ無い。ESO ブロックで `e2e-accounts` が同期したあとに後追いを**もう一度**走らせないと、初回の up では e2e が出来ない。
- **`--import-realm` は `${...}` を Keycloak の env で置き換える**（`dev-client-secret-guard.sh` の注記。PR #1836 監査）。解決できないプレースホルダはそのまま残るので、未解決の `${E2E_…}` を取り込み元へ渡すと**公知の文字列がパスワードになる**。差し込めない利用者は取り込み元から**外す**。
- 後追い Job は宣言（ConfigMap `keycloak-realms`＝プレースホルダのまま）を `POST /users` へそのまま送る（`pick(user, …)`）。手を入れないと、同じく公知の文字列で利用者を作る。
- Keycloak の `RepresentationToModel.createCredentials` は、資格情報に `value` があればパスワードとして、無ければ `secretData` / `credentialData` の保存資格情報として作る（import と管理 REST の作成で同じ経路）。OTP は `value` を持たせず `secretData` に `{"value":"<生の秘密>"}`、`credentialData` に `{"subType":"totp","digits":6,"counter":0,"period":30,"algorithm":"HmacSHA1"}`（realm の otpPolicy と同じ）を置く。
- Keycloak が保存する OTP の秘密は**生の文字列**で、認証アプリへ見せる base32 はその UTF-8 バイト列の base32 である（`scripts/lib/totp.js` の `base32Encode` の注記）。E2E 側は `totp(base32Encode(<生の秘密>))` で 6 桁を出す。
- `check-realm-constraints.js` の検査 5（1）は「対話ログインする利用者は全員 `requiredActions` に `CONFIGURE_TOTP`」を要求する。宣言で OTP を持つ利用者は、既定 browser フローの Conditional OTP が「設定済み」と判定して必ず OTP を問うので、MFA は効いている。検査 5 を「`CONFIGURE_TOTP` を持つ **か**、宣言で TOTP の OTP 資格情報を持つ」へ広げる（陽性・変異を自己試験に足す）。
- 同じく検査（`ACCOUNT_SELF_SERVICE_ROLES`）は import で作る利用者に `account` の `view-profile`・`manage-account` を求める。6 名にも付ける。
- realm の `passwordPolicy` は `length(12)` ＋ 3-of-4 の `regexPattern`。ポリシーに反するパスワードは `--import-realm` 自体を失敗させ（README の #780 の注記）、`POST /users` は 400 になる（後追いは fail-closed なので up が止まる）。
- `VERIFY_PROFILE` が有効なので、`email` / `firstName` / `lastName` を欠く利用者は初回ログインでプロフィール画面が挟まる。6 名にも持たせる。
- Vault の種（`bootstrap.sh`）の `vkv_create_if_absent` の集合は `Platform.Bff.Tests` の `SecretItemBootstrapSeedTests` が SC-22 の許可リスト（deferred ∪ excluded）との一致で固定している。`e2e-accounts` は SC-22 の項目ではないので、種には入れない（運用者が入れる。生成は下の `gen-e2e-accounts.js`）。

## 方法

1. **realm JSON**: 6 名を `developer` の後へ足す。資格情報はプレースホルダだけ。プレースホルダ名は `E2E_<ROLE>_PASSWORD` / `E2E_<ROLE>_TOTP_SECRET`（`<ROLE>` はユーザー名の `e2e-` を除いて大文字・`-`→`_`）。
2. **差し込みの単一の口**（`reconcile-realm.js` の `resolveE2eCredentials(desired, env)`。純関数）:
   - `users[].credentials[]` の `value` / `secretData` に現れる `${E2E_[A-Z0-9_]+}` だけを置き換える。`E2E_` で始まらない名前は置き換えない（`${KC_ADMIN_PASSWORD}` のような宣言で Job の env を利用者へ漏らさない）。
   - 利用者ごとに、必要な名前が全部 env に在り、値が検査（下）を通るときだけ置き換えた利用者を残す。1 つでも欠ける・検査に落ちる利用者は**宣言から外す**（作らない・drift にも数えない）。外した利用者と欠けた名前（値ではない）を返す。
   - 検査: 空でない・制御文字なし・`${` を含まない（取り込みが置き換えるため）。パスワードは realm の `passwordPolicy` の `length(N)` と `regexPattern(…)` を満たす。TOTP の秘密は `^[A-Za-z0-9]{20,64}$`（Keycloak が自分で作る秘密と同じ英数。JSON の文字列に入れても壊れない）。
   - プレースホルダが資格情報の外（例: 属性・client）にあれば例外（宣言の誤り）。
3. **後追い Job**: マニフェストに `envFrom: [{secretRef: {name: e2e-accounts, optional: true}}]` を足す。`main()` が読み込んだ宣言ごとに `resolveE2eCredentials(desired, process.env)` を通してから計画する。外した利用者は `skipped   user <name> — e2e-accounts に無い: <名前…>` を 1 行出す（値は出さない）。
4. **取り込み元（[3/7]）**: `reconcile-realm.js --render-import` を足す。標準入力の realm JSON に 2 と同じ差し込みを当て、標準出力へ書く（外した利用者は標準エラー）。`k8s-local-up.sh` は `platform-infra/e2e-accounts` が在ればそのキーを**関数内の変数**へ読み（`kubectl get secret -o jsonpath` → `base64 -d`。値はどのプロセスの引数にも載らない）、env として node へ渡す（envsubst と同じ働き。外部の `envsubst` には依存しない —— 未解決のまま残す挙動を避け、外す判断と検査を後追いと 1 つの口で共有するため）。無ければ e2e の 6 名を外した取り込み元になる。
5. **ESO**: `deploy/local/vault/eso/externalsecret-e2e-accounts.yaml`（`platform-infra`・`creationPolicy: Owner`・`msp/e2e-accounts` の 12 プロパティ `e2e-<role>-password` / `e2e-<role>-totp-secret` → Secret のキー `E2E_<ROLE>_PASSWORD` / `E2E_<ROLE>_TOTP_SECRET`）。`k8s-local-up.sh` の ESO ブロックで apply し、`infra_es` の確認・同期待ちに入れる。同期したら後追い（`reconcile-realm.sh`）をもう一度走らせる（失敗は WARN。G9 が落とす）。
6. **値の生成**: `scripts/gen-e2e-accounts.js` が realm JSON のプレースホルダから 12 プロパティを導き、乱数の値で KV の JSON を標準出力へ書く（パスワードはポリシーを満たす形、TOTP は英数 32 文字）。端末へ出さず `vault kv put … -` へ流す使い方を README に書く。
7. **README の e2e 節**: OpenBao へ入れる・反映・確かめ方・回転（四半期／漏えいの疑いで即時: KV を作り直す → ESO を force-sync → Keycloak の e2e 利用者を消す → 後追いを走らせる）。

## 受け入れ基準

- [x] realm JSON に実値が無い（`guard-secrets.js` と `check-static-egress` が緑。プレースホルダだけ）。
- [x] `ESO=1` で Secret `e2e-accounts`（platform-infra）が出来る（ExternalSecret を apply し同期を待つ。KV が在ること）。
- [x] `reconcile-realm.js` が既存 realm に 6 ユーザーを足す（env が揃えば `user.create` が 6 件、揃わなければ `skipped` 6 件で drift 0）。dry-run（`plan` を fixture へ当てた出力）を PR の証跡に載せる。
- [x] 未解決・`E2E_` 以外・ポリシー違反・検査に落ちる値では利用者を作らない（変異で確かめる）。
- [x] 取り込み元に `${E2E_` が残らない（Secret が無ければ 6 名を外す）。
- [x] `e2e-trading-owner` だけが `trading-owner` を持ち、`e2e-user` は持たない。
- [x] 検査 5 が宣言の OTP を MFA として数え、OTP も `CONFIGURE_TOTP` も無い利用者は従来どおり落とす。

## 母集合（規則 9）

- 宣言の利用者を数える・形を固定する試験と検査器: `grep -rn "users" scripts/*.js`（`keycloak-realm-reconcile.test.js` の `HUMAN_SEEDS`・`check-realm-constraints.js` の検査 5 と自己管理ロール・`measure-cutover-inventory.js` の `humanUsers`・`check-realm-copy-drift.js`（SA だけ）・`seed-abac-policies.js`）。実行して赤になるものを直す。
- `infra_es` の数え・確認コマンド（`k8s-local-up.sh` と `k8s-local-up.test.js`）。
- 文書: `deploy/local/README.md`（dev ログインユーザー・機密情報の節）・`deploy/local/keycloak-setup/README.md`（境界の表）。

## 検証

- `node scripts/keycloak-realm-reconcile.test.js` / `node scripts/k8s-local-up.test.js` / `node scripts/check-realm-constraints.js`（自己試験込み）/ `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` / 文書系の検査。
- `node .claude/hooks/guard-secrets.js` 相当（変更ファイル）と `node scripts/check-static-egress.js`。

## 結果（2026-10-22）

- `ESO=1` の Secret は ExternalSecret の apply と同期待ちまでを試験で固定した（KV が在れば出来る。稼働のクラスタでの実測はしていない）。
- 母集合（規則 9）を引いた結果、赤になって直したもの: `k8s-local-up.test.js` の取り込み元のバイト等価 2 件（期待を「宣言から e2e を外したもの」へ）、
  `check-realm-constraints.js` の検査 5（宣言の TOTP を数える）、`scripts.repo.test.js` の検査器の母集合（生成器を NOT_CHECKERS へ）と
  `live-scripts.json` の閉包（生成器を offline へ）。`measure-cutover-inventory.js` の「seed 利用者がそろっている」（作り直し後に e2e が無いと fail になる。
  Secret が無ければ作られないのが正しいので母集合から外した）。`check-realm-copy-drift.js` はサービスアカウントだけを見るので影響なし。
- 変異: 外す処理を消す・`E2E_` の制限を外す・ポリシー検査を消す・main で差し込みを当てない・[3/7] の差し込みを外す・ESO 後の後追いを消す、の 6 つとも試験が赤になった。
- #2013 の設計からの変更点（IADR-0570 決定 3）: KV のパスは `msp/e2e-accounts`、Secret の namespace は `platform-infra`。
- 後続（E-31）への申し送り: `E2E_<ROLE>_TOTP_SECRET` は Keycloak が持つ**生の文字列**で、6 桁は `totp(base32Encode(値))` で出す（設計 §1.3 の「base32」とは違う）。
- ［2026-10-22 / PR #2048 の CI］`Secret scan (gitleaks)` が `deploy/local/vault/eso/externalsecret-e2e-accounts.yaml:75`（コミット `6dad64a0`）を
  `generic-api-key` で検出した。手元の gitleaks v8.24.3（リポジトリの `.gitleaksignore` を読む既定の設定。`.gitleaks.toml` は無い）で
  `git --log-opts=origin/develop..HEAD` を走らせて特定した。該当は ExternalSecret の secretKey 欄に書いた E2E_TRADING_OWNER_TOTP_SECRET で、Secret の**キー名**であって値ではない
  （**実値の混入は無い。回転は不要**）。単発の誤検知なので、`.gitleaksignore` の既存の流儀（fingerprint を 1 行・理由のコメントつき）で
  そのコミット・パス・行だけを無効化した（パスや規則ごとの広い除外にはしていない）。同ファイルを後で編集して行が動けば再び検出されるが、
  そのときは同じ形で足す（同型が 2 回続けば `.gitleaks.toml` の allowlist を「secretKey 欄の E2E_ で始まる大文字の名前」だけに当たる正規表現に絞って置く。なお、この節と .gitleaksignore の注記もキー名を「鍵名: 値」の形で引用して同じ誤検知を起こしたので、引用の形を改め、その 2 コミットぶんも fingerprint で無効化した）。
- ［2026-10-22 / PR #2048 の AI レビュー 🟡］[3/7] の `IFS='=' read` が base64 の末尾の単独の `=` を落としていた。行をキーと残りへ分ける形に直し、
  試験で stderr に `invalid input` が出ないことを固定した（旧の形で赤になることを確認）。
