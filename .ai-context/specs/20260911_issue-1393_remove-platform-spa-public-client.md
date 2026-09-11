---
title: 作業仕様書 — platform-spa public client を realm から撤去し、BFF の Bearer 受理を「ブラウザが取得し得ないトークン」だけに絞る
type: spec
status: done
related_ids:
  - NFR
  - SC-13
  - SC-16
  - ADR-0026
  - ADR-0031
  - ADR-0032
author: claude
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - "ADR-0032（SPA 認証は BFF セッション方式 / Token Handler。SPA はトークンを扱わない）"
  - "NFR（セキュリティ｜認証・認可: 全 API で OIDC/JWT 認証・無効化時の即時失効）"
  - "SC-13（ログイン画面。認証入口）"
related_adrs:
  - IADR-0429
  - IADR-0273
  - IADR-0251
  - IADR-0363
  - IADR-0328
  - IADR-0197
  - IADR-0420
---

# 作業仕様書: `platform-spa` public client の撤去と BFF の Bearer 受理の絞り込み（#1393）

> 起点は #1393（親 #439 の残射程 1）。親 #439 の 2026-09-11 監査コメントが
> 「AST 追随待ち」というブロッカーの消滅を実測し、MSP 内に残る 4 箇所の参照を名指しした。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（認証そのものは非機能要求。`02_requirements` §セキュリティ）
- ユースケース（UC）: なし
- 画面（SC）: SC-13（ログイン）、SC-16（アカウント設定 —— `oidc.authority` は残す）
- 関連 ADR: ADR-0032（BFF セッション方式）、ADR-0026（認証ポリシー）、ADR-0031（SPA スタック）
- 関連 IADR: IADR-0251（3a の内部設計・決定 9 の狭める条件）、IADR-0273（3b の完了・
  フォローアップに「`platform-spa` public client の撤去」を明記）、IADR-0197（改名）、
  IADR-0363 / IADR-0328（ツール OIDC の母集合）、IADR-0420（`MachinePrincipal`）

## 目的・背景

ADR-0032 の移行（#439）で SPA はトークンを扱わなくなった（`oidc-client-ts` は撤去済み・ESLint が
再導入を禁止）。にもかかわらず realm には **`platform-spa`（public client・PKCE・
`standardFlowEnabled: true`）** が残っており、**ブラウザから利用者トークンを取得できる口**が開いている。
BFF は `BffSmart` 振り分けスキームにより **`Authorization: Bearer` が在れば realm の任意の有効な
トークンを受理する**（IADR-0251 決定 9）ので、この 2 つが揃うと
「ブラウザが public client でトークンを取り、`/bff/*` を Bearer で直接叩く」経路が成立する ——
**セッション方式（HttpOnly Cookie ＋ CSRF ヘッダ）を丸ごと迂回できる。**

本作業は口の側（realm）と受理の側（BFF）を同時に閉じる。

### 撤去できなかった理由が消えていること（再実測）

```console
$ git ls-tree origin/develop src/ai-stock-trading
160000 commit 1da636de1b56dbe8147e86933b7ed0ad50cf6d35  src/ai-stock-trading

$ git grep -n "platform-spa" -- src/ai-stock-trading
（0 件）
```

submodule 配下に参照は無い。IADR-0273 決定 7 の「AST 互換フォールバック」を狭める条件は
**本作業の射程外**（`roles.ts` のフォールバック撤去は別作業。#1393 の射程にも入っていない）。

## 走査した母集合

**走査コマンド**（追跡下の全ファイル。submodule は gitlink なので中身は当たらない）:

```console
$ git grep -n "platform-spa" | wc -l
84
$ git grep -c "platform-spa" | wc -l
47
```

陽性対照: 同じ走査で `git grep -c "\"clientId\": \"bff\"" deploy/keycloak/microservices-platform-realm.json`
が 1 件当たる（走査は空振りしていない）。

補助の走査（`platform-spa` の文字列を持たないが同じ配線に属する箇所を捕まえるため。
規則 10「是正のたびに、この変更で新たに誤りになる自分の記述を引き直す」）:

```console
$ git grep -n "OIDC_CLIENT_ID" -- deploy scripts src/platform/frontend
$ git grep -n "oidc\.clientId\|clientId" -- src/platform/frontend/src deploy/helm
```

### 47 ファイルの処置（全件）

| # | ファイル | 処置 | 理由 |
| --- | --- | --- | --- |
| 1 | `deploy/keycloak/microservices-platform-realm.json` | **変更**（client 定義を削除） | 本件の主目的 |
| 2 | `scripts/verify-oidc-edge-flow.sh` | **変更**（`OIDC_CLIENT_ID` 既定を `bff` へ／`client_secret` を送る／`redirect_uri` 既定を `/bff/auth/callback` へ）。**`:332` の `client_id=platform-spa` を含む行は変更しない** —— 稼働 CI 実行（run 33200749231）の `Location` を引用した実測ログだからである | 撤去すると認可端点が `invalid client` で落ち、**CI の門（`integration-stack.yml`）が壊れる**。confidential client へ移す |
| 3 | `scripts/check-realm-constraints.js` | **変更**（`REQUIRED_CLIENT_URLS` から削除・自己試験の期待値を追随） | 消えた client の URL を必須と宣言し続けると常時 FAIL |
| 4 | `scripts/measure-search-ndcg.js` | **変更**（既定 client_id を撤去し、`NDCG_KC_CLIENT_ID` 必須に） | `platform-spa` は `directAccessGrantsEnabled: false` なので**この既定は元から動いていない**。死んだ既定を消す |
| 5 | `perf/k6/lib/config.js` | **変更**（既定 client_id を撤去） | 同上（#438 の作業仕様書が「変更前から動いていない」と実測済み） |
| 6 | `perf/k6/README.md` | **変更**（既定の記述を追随） | 上と対 |
| 7 | `deploy/docker-compose.yml` | **変更**（`OIDC_CLIENT_ID` 行を削除） | SPA は読まない（下記「死んだ構成の判定」） |
| 8 | `deploy/helm/microservices-platform/values.yaml` | **変更**（`frontend.oidc.clientId` を削除） | 同上 |
| 9 | `deploy/helm/microservices-platform/templates/frontend.yaml` | **変更**（`OIDC_CLIENT_ID` env を削除） | 同上（values 削除で `nil` 参照になるため必須） |
| 10 | `src/platform/frontend/src/config/runtimeConfig.ts` | **変更**（`OidcConfig.clientId` を削除） | 読み手が 0 件 |
| 11 | `src/platform/frontend/src/config/runtimeConfig.test.ts` | **変更**（期待から `clientId` を削除） | 上と対 |
| 12 | `src/platform/frontend/src/app/Layout.test.tsx` | **変更**（構成スタブから `clientId` を削除） | 型が消えるため |
| 13 | `src/platform/frontend/public/config.js` | **変更**（`clientId` を削除） | 同上 |
| 14 | `src/platform/frontend/config.js.template` | **変更**（`clientId` を削除） | 同上 |
| 15 | `src/platform/frontend/docker-entrypoint.sh` | **変更**（既定・export・envsubst 列挙から `OIDC_CLIENT_ID` を削除） | 同上 |
| 16 | `src/platform/frontend/README.md` | **変更**（前提の記述を BFF セッションへ） | 誤った手順が残る |
| 17 | `docs/how-to/local-development.md` | **変更**（同上） | 同上 |
| 18 | `docs/screens/SC-13_login.md` | **変更**（ログイン開始が `bff` client であることへ） | 画面設計の実装対応表が事実と違う |
| 19 | `docs/tech/tech-requirements.md` | **変更**（「AST 追随まで残す」の記述を撤去済みへ） | 残る理由が消えた |
| 20 | `docs/tech/composable-component-guide.md` | **変更**（`platform-spa` の直接 OIDC → BFF セッションへ） | 同上 |
| 21 | `README.md` | **変更**（realm / client の現行値の記述） | 同上 |
| 22 | `deploy/local/README.md` | **変更**（7 箇所。port-forward の origin 登録先を `bff` へ） | 手順が成立しなくなる |
| 23 | `deploy/local/edge/README.md` | **変更**（1 箇所） | 同上 |
| 24 | `scripts/lib/tool-oidc-login.js` | **変更**（母集合の注記のみ。7 件は不変） | `standardFlowEnabled` が 8 → 7 件になり注記の算式が古くなる |
| 25 | `scripts/scripts.repo.test.js` | **変更**（母集合注記の追随／`buildTokenForm` の合成フィクスチャを `no-such-client` へ／**realm に public client が 0 件であることのラチェットを新設**） | 上と対。ラチェットは「口が再び開く」ことを機械で止める |
| 25b | `scripts/test-traceability-allowlist.json` | **変更**（`pending` から `SC-13` を削除） | 🔴 **ratchet が要求した削除**。`BearerCallerPolicyTests.cs` が `src/` で初めて `SC-13` を参照したため、残すと「写像済みの残置」で fail する（実測。§実測 3） |
| 26 | `src/platform/backend/Bff/Platform.Bff/Foundation/Session/BffSessionExtensions.cs` | **変更**（Bearer 受理の門を掛ける） | 本件の主目的の 2 つ目 |
| 27 | `src/platform/backend/Bff/Platform.Bff/Foundation/Session/BearerCallerPolicy.cs` | **新規** | 判定を純粋関数へ出して変異試験に掛ける |
| 28 | `src/platform/backend/Bff/Platform.Bff.Tests/BearerCallerPolicyTests.cs` | **新規** | 陰性対照（public client 名義の利用者トークンが通らない） |
| 29 | `.ai-context/adr/IADR-0429_*.md` / `.ai-context/adr/README.md` | **新規 / 変更** | 決定の記録 |
| 30 | `.ai-context/specs/20260911_issue-1393_*.md` | **新規** | 本書 |
| 31 | `scripts/scripts.test.js`（2 件） | **変更しない** | Keycloak が返した **HTML の実サンプル**（`CONFIGURE_TOTP` フォーム）を固定したパーサ検査であり、`client_id` の値は判定に使わない。凍結したバイト列を書き換えると「実際に観測した応答」でなくなる |
| 32 | `src/knowledge/backend/.../UnitProjectEndpointMetricsTests.cs`・`UnitProjectMetricsTests.cs` | **変更しない** | `MachinePrincipal` の**人間側の対照**として `azp` に任意の非サービスアカウント名を置いているだけ。realm の実在とは無関係（IADR-0420 は「構成の許可リストを持たない」＝ realm と結合しない設計） |
| 33 | `src/platform/backend/Shared/.../MachinePrincipalTests.cs`・`SyntheticTrafficTests.cs` | **変更しない** | 同上 |
| 34 | `.ai-context/adr/*.md`（IADR-0197 / 0243 / 0273 / 0328 / 0329 / 0363 の 6 ファイル） | **変更しない** | **凍結記録**（`.ai-context/` の本文プロズは後から書き換えない。`traceability.repo.md` §Superseded の凍結の射程） |
| 35 | `.ai-context/adr/README.md` の IADR-0197 行 | **変更しない** | 索引は本体の要約であり、当時の決定内容そのもの。IADR-0429 の行を足すことで現行値の所在は追える |
| 36 | `.ai-context/specs/*.md`（12 ファイル） | **変更しない** | 凍結記録（同上） |

## 死んだ構成の判定 —— `frontend.oidc.clientId` は SPA から読まれていない（#439 監査の未測定事項）

親 #439 の 2026-09-11 監査は「`values.yaml:1126` が実行時に読まれているかは静的に追っていない」と
明示した。**本作業で追った。**

```console
$ git grep -n "clientId" -- src/platform/frontend/src src/knowledge/frontend/src
src/platform/frontend/src/app/Layout.test.tsx:190        （テストのスタブ）
src/platform/frontend/src/config/runtimeConfig.test.ts:24,30,37,45  （テスト）
src/platform/frontend/src/config/runtimeConfig.ts:7,48,95           （型・既定・合成）
```

**`oidc.clientId` を読む製品コードは 0 件である。** 同じ `oidc` の下でも `authority` は
`Layout.tsx:145` の `accountConsoleUrl()`（SC-16 のアカウントコンソール導線）が読む ——
**陽性対照**であり、走査が「`oidc` を丸ごと見落とした」のではないことを示す。

したがって `clientId` は **config → 型 → env → helm → compose の 5 層を貫いて死んでいる**。
値を `bff` へ書き換えるのではなく**層ごと落とす**（生きていない設定は、次の書き手に
「ここを直せば効く」と誤読させる）。

## 設計

### 決定 1: `platform-spa` を realm から削除する

`deploy/keycloak/microservices-platform-realm.json` の client 定義（1 件）を削除する。
`clients` は 25 件 → 24 件になる。

### 決定 2: `verify-oidc-edge-flow.sh` は `bff`（confidential）で認可コードを取る

**Cookie 方式への書き換え（IADR-0251 決定 9 の狭める条件 1）は本作業では行わない。**
本スクリプトは 20 段超・700 行で、CI の門（`integration-stack.yml`）が依存している。
`platform-spa` の撤去に必要なのは「public client を使わないこと」だけであり、
**必要最小の変更で足りる**（過剰な射程を取らない）。

- `OIDC_CLIENT_ID` 既定: `platform-spa` → **`bff`**
- `OIDC_REDIRECT_URI` 既定: `${EDGE_URL}/callback` → **`${EDGE_URL}/bff/auth/callback`**
  （`bff` の `redirectUris` に登録済み。スクリプトは Location を自分で読むだけで、
  実際にその URL を開かない）
- トークン交換に **`client_secret` を添える**。値は
  `OIDC_CLIENT_SECRET` → `BFF_OIDC_CLIENT_SECRET` → **realm JSON の `clients[bff].secret`** の順で解決する。
  🔴 **スクリプトへ秘密のリテラルを書かない**（realm ファイルが dev 既定の単一情報源であり、
  そこを変えれば検証器も自動で追随する）。

### 決定 3: BFF の Bearer 受理を「ブラウザが取得し得ないトークン」に絞る

`BffSmart` の Bearer 腕（`JwtBearer`）に `OnTokenValidated` の門を掛け、**次のいずれでもない
主体を拒否する**（`ctx.Fail` → 401）。

1. **無人の主体**（`MachinePrincipal.IsMachine`。サービスアカウント / `profile` を持たない機械クライアント）
2. **`azp` が BFF 自身の OIDC クライアント ID（`BffSession:ClientId`。既定 `bff`）と一致する**トークン

**なぜ 2 を残すか。** `bff` は confidential client であり、**ブラウザはその名義のトークンを
取得できない**（client_secret を持たない）。したがって「SPA が直接トークンを扱う」という
ADR-0032 が禁じた形は成立しない。一方、非ブラウザの外形確認（決定 2）は成立し続ける。
**狭める条件**は IADR-0251 決定 9 条件 1 のまま —— `verify-oidc-edge-flow.sh` が Cookie 方式へ
移ったら 2 を落として `ServiceCaller` 相当（機械のみ）へ寄せる。

判定は純粋関数 `BearerCallerPolicy.IsAcceptedCaller(principal, bffClientId)` に置く
（シェル / 配線の grep では逆転を検出できない、という #992 / #1124 の教訓と同型）。

**この門が閉じるもの**（realm 撤去と二重化する理由）: realm は複数環境で運用され、
**同型の public client が再び足される**ことがあり得る（`platform-spa` は #126 以来 8 か月残った）。
口だけ閉じると、口が開いた瞬間に迂回が復活する。

## 受け入れ基準

1. `deploy/keycloak/microservices-platform-realm.json` に `platform-spa` が 0 件。
2. 🔴 **陰性対照**: `azp: platform-spa` かつ人間の利用者名（`preferred_username: developer`）を持つ
   トークンは Bearer として**拒否**される。
3. **陽性対照 2 本**: (a) サービスアカウント（`service-account-retrieval-service`）は通る、
   (b) `azp: bff` の利用者トークンは通る。**陽性が無いと「常に拒否」が 2 を満たす。**
4. `node scripts/check-realm-constraints.js --self-test` が緑。
5. `node scripts/keycloak-realm-reconcile.test.js` が緑（G9 の drift 判定に退行が無い）。
6. `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes`（platform slnx）が緑。
7. `pnpm run lint` / `typecheck` / `test`（`src/`）が緑。
8. `node scripts/scripts.test.js` / `scripts.repo.test.js` が緑。
9. 文書系検査器（trace ブロック・doc-links・cross-repo-refs・plan-id-qualification・
   knowledge-graph・test-traceability・reading-budget・adr-numbering）が緑。

## 変異試験（検出力の実測）

門を「常に受理」へ変異させ、陰性対照だけが落ちることを確認する（§実測 に生出力）。

## 実測（証跡）

### 1. 変異試験 — 門を外すと陰性対照だけが落ちる

`BearerCallerPolicy.IsAcceptedCaller` を「常に受理」へ変異させた（＝#1393 以前の姿）。

```console
$ dotnet test Bff/Platform.Bff.Tests/Platform.Bff.Tests.csproj --filter "FullyQualifiedName~BearerCallerPolicyTests"
失敗!   -失敗:    11、合格:     4、スキップ:     0、合計:    15
  失敗 … Public_client_user_token_is_refused
  失敗 … User_tokens_minted_for_other_browser_clients_are_refused(azp: "grafana" / "argocd" / "headlamp" / "wiki-js")
  失敗 … Unauthenticated_principal_is_refused
  失敗 … Empty_bff_client_id_does_not_open_the_gate(clientId: null / "" / "   ")
  失敗 … User_token_without_a_client_claim_is_refused
  失敗 … Wiring_fails_authentication_for_a_public_client_user_token
     Expected ctx.Result not to be <null> because 受理しない主体は認証を失敗させる（fail-closed）.
```

**陽性 4 件（サービスアカウント・機械クライアント・`azp: bff`・配線の陽性）は緑のまま**であり、
陰性対照が無ければ「常に受理」が通っていた。変異を戻し、`git diff` が空・`grep -c 変異` が 0 で残渣 0 を確認した。

### 2. 死んだ構成の判定（陽性対照つき）

`oidc.clientId` の読み手は 0 件、`oidc.authority` の読み手は 1 件（`Layout.tsx:145`）。
走査が `oidc` を丸ごと見落としたのではないことを、後者が示す。

### 3. ratchet が 1 件要求した（allowlist の縮小）

`node scripts/scripts.test.js` が `allowlist の減らし忘れ: SC-13` で落ちた。`src/` 側に
`SC-13` を起点 ID とするテストが初めて置かれたためであり、`pending` から削除して緑になった。
**検査器が是正を要求した形であり、こちらから緩めていない。**

### 4. スイート

| 実行 | 結果 |
| --- | --- |
| `dotnet build`（platform slnx） | 成功・警告 0 |
| `dotnet test`（platform slnx） | **1765 合格 / 0 失敗**（Bff 558 ＋ Shared 343 ＋ Authz 244 ＋ LlmGateway 301 ＋ McpServer 175 ＋ Notification 102 ＋ Kernel 42。スキップ 1） |
| `dotnet format --verify-no-changes` | exit 0 |
| `pnpm run typecheck` / `lint` / `format:check`（`src/`） | いずれも緑（lint は既存の warning 10 件のみ・error 0） |
| `pnpm run test`（`src/`） | **1470 合格 / 1 失敗**。失敗は `orvalMutator.test.ts` の `res.data.arrayBuffer is not a function` で、**変更を stash した clean tree でも同じ 1 件が落ちる**（本作業と無関係の環境差） |
| `node scripts/scripts.test.js` | **771 件合格**（IADR-0428 の欠番だけは一時プレースホルダを置いて計測。§5） |
| `node scripts/keycloak-realm-reconcile.test.js` | **34 件合格**（G9 の drift 判定に退行なし） |
| `node scripts/check-realm-constraints.js` / `--self-test` | 実データ OK / 自己試験 **117 件 OK** |

### 5. 検査器（`node scripts/<name>.js`）

trace-blocks / doc-type-vocabulary / doc-links / cross-repo-refs / plan-id-qualification /
test-traceability / reading-budget / secret-injected-options / realm-constraints /
bff-downstreams / doc-status-vocabulary / gen-knowledge-graph --check ── **すべて exit 0**。

落ちた 2 件と理由:

- 🔴 `check-adr-numbering`: `[missing-number] IADR-0428 が欠番`。**IADR-0428 / 0430 は並行エージェントの
  採番であり、本ブランチには存在しない。** 一時プレースホルダを置くと本検査も `scripts.test.js` も
  全件緑になることを確認し、プレースホルダは削除した。**先着尊重（`traceability.md`「採番衝突時の
  改番手順」）に従い、本 PR が 0428 の PR より先に着地する場合は 0429 → 0428 へ改番する。**
- `check-deploy-manifests`: `kubeconform が PATH にありません`（ローカル環境にツールが無いだけ。
  変更内容とは無関係）。

## 未決事項・残作業

- `verify-oidc-edge-flow.sh` の **Cookie 方式化**（IADR-0251 決定 9 条件 1）は残る。
- IADR-0273 決定 7 の **AST 互換 JWT フォールバック**（`roles.ts`）の撤去は別作業。
- 稼働 Keycloak の clients は読んでいない（realm export のみ）。**live は触らない。**
- 🔴 **ローカル port-forward `8081` での OIDC ログインは成立しない。** `bff` client の redirect は
  3100 / 5000 / エッジ https の 3 つで、8081 は入っていない。**これは #1393 以前から
  BFF セッション移行によって成立していなかった**もの（`platform-spa` 時代の 8081 登録は
  SPA 自身の redirect のためだった）で、本作業は `deploy/local/README.md` を実態へ揃えただけである。
  8081 でも通したいなら `bff` へ `http://localhost:8081/bff/auth/callback` を足す —— **別 issue。**
