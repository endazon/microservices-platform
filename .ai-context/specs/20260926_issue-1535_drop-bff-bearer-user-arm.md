---
title: "BFF の Bearer 受理に残る移行期の腕（azp=bff の利用者トークン）を、外形確認を Cookie 方式へ移したうえで落とす（#1535）"
type: spec
status: in-progress
related_ids: [NFR-09, SC-13, ADR-0032, ADR-0076, IADR-0251, IADR-0273, IADR-0330, IADR-0378, IADR-0420, IADR-0429]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md §決定（BFF がコンフィデンシャルクライアント・SPA はトークンを扱わない・CSRF は SameSite ＋ カスタムヘッダ）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-09
---

# 仕様書: BFF の Bearer 受理から「BFF 自身の client 名義の利用者トークン」の腕を落とす（#1535）

## 起点となる計画書（トレーサビリティ）

- 非機能要件: **NFR-09**（全 API で OIDC/JWT 認証。暫定はエッジ（BFF）で担保）
- 画面: **SC-13**（全コンポーネント共通の OIDC 認証入口。ブラウザ由来のトークンで入口を迂回できないこと）
- 計画 ADR: **ADR-0032**（Accepted）—— BFF が confidential client として Keycloak と通信し、ブラウザには HttpOnly / Secure / SameSite=Lax の
  セッション Cookie だけを渡す。**SPA はトークンを扱わない**
- 実装 IADR: **IADR-0251 決定 9**（振り分けスキームと「狭める条件」）／**IADR-0429 決定 3・4**（Bearer 腕を A: 無人の主体・B: `azp`=BFF の client に絞った。
  B は `verify-oidc-edge-flow.sh` のための移行期の腕）／IADR-0273 決定 4（セッション → 下流の資格情報の橋）／IADR-0378（合成監視の主体）／IADR-0420（`MachinePrincipal`）／IADR-0330（認証済み E2E は BFF スタブ）
- 起票: #1535（#439 から分離）

## 目的・背景

`/bff/*` の Bearer 受理は IADR-0429 決定 3 で 2 腕に絞られた。腕 B（`azp` が BFF 自身の confidential client `bff`）は
**非ブラウザの外形確認 `scripts/verify-oidc-edge-flow.sh` のためだけ**に残した移行期の腕であり、IADR-0429 は
「同スクリプトが Cookie 方式へ移ったら腕 B を落とす」と条件を書いた。ブラウザは client_secret を持てないので ADR-0032 の禁則には
当たらないが、**利用者の資格情報で `/bff/*` を Bearer で叩ける口**であることに変わりはない。外形確認を Cookie 方式へ移し、腕 B を落とす。

## 🔴 着手前に確認した制約

- **ADR-0032 §決定**: CSRF は「SameSite ＋ カスタムヘッダ」。Cookie で状態を変える要求は `X-MSP-CSRF`（`BffSessionOptions.CsrfHeaderName`）が要る。
  検証器も POST にこのヘッダを付ける（付けないと 403）。
- **IADR-0251 決定 9 条件 1** は「既定を `BffSession` 単体へ狭める」を求めるが、**腕 A（無人の主体）は恒久**である（IADR-0429 決定 3。
  合成監視 `synthetic-monitor` が `/bff/analysis/ask` を client credentials の Bearer で叩く。IADR-0378 / 計画 ADR-0076 決定 4）。
  既定を `BffSession` 単体にすると腕 A が 401 になる（`DefaultSchemeRoutingTests` が実測で固定している形）。
  **よって既定は振り分けスキームのまま据え置く**（issue の「可能なら」に当たらない）。IADR-0251 に日付つき追記で記録する。
- **AST の稼働中 PoC を壊さない**（依頼）。AST が BFF を Bearer（`azp=bff` の利用者トークン）で叩いていれば止めて記録する。→ 下の母集合 #7 で
  **依存なし**と判定した。
- 稼働クラスタでは走らせない（issue §🔴）。`verify-oidc-edge-flow.sh` の書き換えは統合スタック CI（`integration-stack.yml`）で確かめる。

## 母集合（[[IADR-0141]] 決定 1。着手時に自分で引いた。issue の「反映先」は転記していない）

腕 B に依存し得るのは「`/bff/*` に `Authorization: Bearer` で**利用者**トークンを送る呼び出し」だけである。そのトークンを得るには
`bff` の client_secret で認可コードを交換するしかない（realm の `bff` は `directAccessGrantsEnabled=false`・token exchange 属性なし。
realm JSON を node で読んで確認）。**誤りの側（Bearer を送る側・`bff` の secret で交換する側）から引いた。**

### 引いた軸（拡張子で絞らず `git ls-files` 全体。行フィルタは軸ごとの 2 段目にだけ使った）

| 軸 | 検索語 | 件数 |
| --- | --- | --- |
| 1 | `bearer`（大小無視・追跡下の全ファイル） | 716 行 / 250 余ファイル。うち `/bff` への呼び出しを持つ非 C# ファイルを個別に読んだ |
| 2 | トークン取得の形（`client_id` / `clientId` / `grant_type` / `client_secret` / `authorization_code`） | `bff` の secret で認可コードを交換するのは `verify-oidc-edge-flow.sh` の 1 件だけ |
| 3 | `azp`（`.ai-context` 以外） | BFF の判定は `BearerCallerPolicy` のみ。他は `MachinePrincipal` / 合成監視 / DocumentService の計上（腕 B と無関係） |
| 4 | BFF を叩く側（`bff-service` / `BFF_BASE_URL` / `bffBaseUrl` / `perf` 内の `/bff`） | 合成監視・k6・nDCG 計測・frontend Caddy |
| 5 | 🔴 **BFF 内部の隠れた依存**: セッション → `Authorization` 昇格後に既定スキーム（`BffSmart`）で再認証する呼び出し（`AuthenticateAsync(` / `GetTokenAsync(` / `AuthenticationSchemes`） | MSP BFF・knowledge BFF・AST BFF モジュールに**0 件**（昇格は `SessionTokenPropagationMiddleware` の `SessionScheme` 指定だけ） |
| 6 | E2E とフロントの `Authorization` | Playwright は BFF スタブ（IADR-0330）。SPA は `Authorization` を付けないことを単体テストが固定 |
| 7 | AST（submodule pin `7a7a8a14` と AST develop `e6c8f781` の両方） | `/bff` / `bff-service` / `Bearer` / `client_id` |
| 8 | 腕 B・検証器の Bearer を前提に書いた文書（`verify-oidc-edge-flow` / `Bearer 腕` / `移行期` / `振り分け`） | コード内コメント 5 件・`docs/api/BFF_bff-surface.md`・`scripts/README.md` |

### 判定表

| # | 該当 | 腕 B との関係 | 扱い |
| --- | --- | --- | --- |
| 1 | `scripts/verify-oidc-edge-flow.sh`（`/bff/*` へ Bearer 16 箇所・`bff` の secret で自前交換） | **直接の依存** | **Cookie 方式へ移す**（BFF のログイン往復でセッション Cookie を得る。POST は CSRF ヘッダ付き）。**client_secret を読まなくなる** |
| 2 | `perf/k6/lib/config.js`（＋`search-load.js` / `rag-load.js` / README） | `TOKEN` で `/bff/*` を叩く。BFF が受理する利用者トークンは `azp=bff` しか無く、`config.js` 自身が「検証器の認可コード導線で取る」と案内していた＝**潜在依存**。パスワードグラントは realm の全 client で無効（`check-realm-constraints.js` 検査 5）で元から通らない | **Cookie 方式の口（`SESSION_COOKIE`）を足す**。`TOKEN` は無人の主体のトークンに限る旨を明記 |
| 3 | `scripts/measure-search-ndcg.js`（既定の経路 `/bff/search`）と `perf/ndcg/README.md`（`NDCG_TOKEN=<jwt>` の実行例 3 箇所） | #2 と同じ潜在依存 | **`NDCG_SESSION_COOKIE` を足す**。トークンの経路は RetrievalService を直に叩く（`NDCG_SEARCH_PATH=/search`）ときのものとして残す |
| 4 | 合成監視 `deploy/local/synthetic-monitor/probe.js` と Helm 側の写し | client credentials（`service-account-synthetic-monitor`）＝**腕 A** | 移さない（恒久の腕）。陽性対照で固定する |
| 5 | SPA（`apiClient.ts` / `orvalMutator.ts`） | Cookie のみ。`Authorization` を付けないことをテストが固定 | 対象外 |
| 6 | Playwright E2E（`src/platform/frontend/e2e/**`） | BFF スタブ（`support/bffSession.ts`）。Bearer 0 件 | 対象外 |
| 7 | **AST**（pin・develop とも） | AST のサービス・CronJob は **client credentials で AST 自身のサービスと MSP の後段サービスを直接**叩く（`/bff` を経由しない。CronJob の宛先は `information-collection-service`）。AST の BFF 端点モジュールは MSP BFF に同居し、ブラウザの **Cookie セッション**を受けて昇格済みの `Authorization` を下流へ透過する —— 認証・認可は昇格より**前**に終わり、昇格後の再認証は軸 5 のとおり 0 件。AST の E2E は `page.route` で `/bff` を横取りする | **依存なし。AST は変更しない**（PR を絞る必要なし）。軸 5 の性質をパイプライン試験で固定する |
| 8 | `scripts/seed-*.js` | client credentials で後段サービスを直接（`/bff` ではない） | 対象外 |
| 9 | `scripts/measure-abac-combinations.js` / `deploy/mail-relay/reset-gate.js` / `deploy/local/wikijs-setup` / Obsidian プラグイン | Keycloak 管理 API・Wiki.js・DocumentService 同期（BFF ではない） | 対象外 |
| 10 | `SessionTokenPropagationMiddleware`（昇格するトークンは `azp=bff` の利用者トークン） | 🔴 **昇格後に `BffSmart` で再認証されれば、腕 B を落とした瞬間に Cookie 経路が全滅する**。軸 5 で 0 件と確認 | **本物の JwtBearer を通すパイプライン試験で固定する**（Cookie は通る・昇格は起きる・同じトークンを Bearer で直接送ると 401） |
| 11 | `BearerCallerPolicy` / `BffSessionExtensions` / `Program.cs` / `DefaultSchemeRoutingTests` のコメント | 腕 B・検証器の Bearer を前提に書いてある | 追随する |
| 12 | `BearerCallerPolicyTests`（腕 B の陽性 1 件・空 ClientId の陰性 3 件） | 腕 B の存在を固定している | 陽性を陰性へ反転し、`bff` の service account（腕 A）の陽性を足す |
| 13 | `docs/api/BFF_bff-surface.md`（「現在は Bearer で付与する・移行予定」） | 陳腐化（移行は完了済み） | 追随する |
| 14 | `scripts/README.md` の検証器の説明（「トークン交換 → クレーム」） | 陳腐化 | 追随する |
| 15 | `docs/tests/NFR-09_bff-edge-authentication.md` | Bearer 受理の主体の試験が載っていない | 試験ケースを足す |

### 除外したもの（理由つき）

- **凍結記録**: `.ai-context/specs/20260911_issue-1393_*`・`20260822_issue-439_*`・`20260823_issue-439_*` ほか、IADR-0449・IADR-0086 等の腕 B / 検証器への言及 ——
  確定済みの記録であり本文を書き換えない（traceability.repo.md）。IADR-0251・IADR-0429 には**日付つき追記**だけを足す（issue の受け入れ基準 3）。
- `scripts/test-traceability-allowlist.json` の `［2026-09-11 / #1393］` 注記（「陰性対照 11 件＋陽性 4 件」）—— 日付つきの経緯の記録であり、当時の件数として正しい。
- `CHANGELOG.md` —— 自動生成物。手で書き足さない。
- `DashboardService` / `DocumentService` のテスト器の `azp` ヘッダ、`MachinePrincipal` の「腕 B」 —— 名前が同じだけの別物（無人の主体の第 2 形）。
- `scripts/check-realm-constraints.js` の `bff` 節のコメント —— public client の再追加を禁じる話で、腕 B には触れていない。

## 対象範囲

1. `scripts/verify-oidc-edge-flow.sh` を Cookie 方式へ移す（段数は現在と同じ 11 ＋ 加算。段 3〜6 の中身を差し替える）
   - 段 3: `/bff/auth/login` の 302 が issuer の認可端点を指すこと → その先のログイン画面
   - 段 4: 資格情報（＋TOTP）を POST し、`/bff/auth/callback` への redirect を得る（従来どおり）
   - 段 5: コールバックを BFF に渡し、**BFF がコードを交換してセッション Cookie（HttpOnly・Secure）を発行する**こと
   - 段 6: `/bff/auth/me` で身元（利用者名・ロール）を確かめ、**応答にトークンが無い**こと
     （従来の「トークンのクレーム（`clearance` / `department`）」は検証器がトークンを持たなくなるので見られない。ABAC の入力が載っていることは
     `ABAC_POSITIVE=1` の段 13〜15 が結果で示す —— CI は常にこのモードで走らせている）
   - 段 7 以降: `Authorization: Bearer` を `-b <セッション Cookie の jar>` ＋ CSRF ヘッダへ置き換える
2. `BearerCallerPolicy` から腕 B を落とす（利用者トークンの Bearer は `azp` に関わらず拒否）。既定スキームは据え置く（上記制約）
3. k6 と nDCG 計測にセッション Cookie の口を足す
4. 文書・コメント・IADR 追記の追随

## 受け入れ基準 → テスト

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | `azp=bff` の利用者トークンを Bearer で送ると 401 | `BearerCallerPolicyTests.User_token_minted_for_the_bff_confidential_client_is_refused` ／配線 `Wiring_fails_authentication_for_a_bff_client_user_token` ／パイプライン `BearerArmPipelineTests.Bff_client_user_token_sent_as_bearer_is_401` |
| 2 | 無人の主体（合成監視・BFF 自身の service account）は通る | `Service_account_token_is_accepted` ／`Bff_own_service_account_token_is_accepted` ／パイプライン `Machine_token_sent_as_bearer_is_accepted` |
| 3 | Cookie セッションは通り、下流へは昇格したトークンが渡る（同じ `azp=bff` のトークンでも） | パイプライン `Cookie_session_is_accepted_and_promotes_the_same_token_downstream` |
| 4 | 資格情報なしは 401（陰性対照の対照） | パイプライン `No_credential_is_401` |
| 5 | 変異試験（腕 B を戻す）で 1 が赤 | 手で変異させて実測し、IADR-0429 追記に記録する |
| 6 | `verify-oidc-edge-flow.sh` が Bearer を使わずに現在と同じ段数を確かめる | `bash -n`・`scripts.repo.test.js`（TOTAL の加算式）・統合スタック CI（`integration-stack.yml` を本ブランチで `workflow_dispatch`） |

## 実施結果（2026-09-26）

- **差異 1（issue §やること 2 の「可能なら既定スキームを `BffSession` 単体へ」）**: 実施しない。合成監視が腕 A で `/bff/*` を
  Bearer で叩く恒久の呼び出し元であるため（上記制約）。IADR-0251 決定 9 に日付つき追記。
- **差異 2（段 6 の中身）**: 「トークンのクレーム 4 件」→「`/bff/auth/me` の 4 判定（200・利用者名・ロール・応答にトークンが無い）」。
  段数（基底 11）は同じ。`clearance` / `department` の在否は検証器から直接は見えなくなった（トークンを持たないため。ADR-0032 の要件そのもの）。
- **k6 のパスワードグラントの経路を撤去した**（得られるのは利用者トークンで、`/bff/*` しか叩かない k6 では 401 にしかならない）。
  nDCG は RetrievalService を直に叩く用途があるので残した。
- 索引（`.ai-context/adr/README.md`）の IADR-0429 行へ追記を足しかけたが、索引タイトルセルの長さの門（`scripts.repo.test.js`）に当たるので戻した。
  追記は IADR 本文にだけ置く。
- **テスト**: `Platform.Bff.Tests` 全体 765 合格・1 スキップ（既存のベンチマーク）。新規・改修 19 件（`BearerCallerPolicyTests` 15・`BearerArmPipelineTests` 4）。
  `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 合格（`test-spec-coverage-baseline.json` の床を `--update` で上げた）。
  `check-trace-blocks`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-test-traceability`・
  `check-bff-authz-docs` 合格。`dotnet format --verify-no-changes`（変更ファイル）合格。`bash -n scripts/verify-oidc-edge-flow.sh` 合格。
- **変異試験**（戻して残渣 0）:

  | 変異 | 落ちたテスト |
  | --- | --- |
  | 腕 B を戻す | 4 件（判定 1・配線 2・パイプライン 1）。陽性は全て緑 |
  | 昇格の後に既定スキームで再認証する | 1 件（`Cookie_session_is_accepted_and_promotes_the_same_token_downstream`） |

- **稼働クラスタでは走らせていない**（`--live` / `LIVE=1` を渡していない）。`verify-oidc-edge-flow.sh` の外形は統合スタック CI で確かめる。

## 未決事項・計画への環流

- 計画に影響する新たな制約は無い（ADR-0032 は非ブラウザの Bearer に言及しておらず、腕 A の恒久性は IADR-0429 が既に記録している）。環流なし。
