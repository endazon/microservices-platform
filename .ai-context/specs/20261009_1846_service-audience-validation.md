---
title: 作業仕様書 — 全サービスの JWT 検証で audience を検証する（ValidateAudience = false の是正。NFR-09・ADR-0036。#1846）
type: spec
status: done
related_ids: [NFR-09, ADR-0036, ADR-0032, ADR-0086, ADR-0134, ADR-0119, IADR-0523, IADR-0516, IADR-0086, IADR-0379, IADR-0429]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md（audience の定めは無い）
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md（audience の定めは無い）
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md（token exchange は採らない）
  - planning:projects/microservices-platform/07_adr/ADR-0134_mcp-client-keycloak-template-and-secret-one-time-display.md 決定 1（audience を MCP サーバーに限る）
  - planning:projects/microservices-platform/10_feedback/20261009_mcp-client-template-and-secret.md 実測 6
issue: "#1846"
---

# 作業仕様書 — 全サービスの audience の検証（#1846）

> 本仕様書は実装着手前に作成した（2026-10-09）。基点は MSP `origin/develop` `1e6cfa58`（#1854 のマージ直後。`AuthExtensions.PlatformJwtBearer` と `McpAudience` スキームが在る）。
> 計画は project-planning の隣接クローン（`origin/main` `142c3e44`。読み取り専用）で読んだ。
> ［2026-10-09 追記 / #1846］**利用者裁定: 方式 C**（下記「裁定」）。計画への環流は **planning#770**（`feedback`・`decision-needed`）。
> 実装は `origin/develop` `842b970f`（#1854・#1855・#1856 のマージ後）へ追随してから行った。記録は **IADR-0523**（0522 は並走 PR #1860 が取る。#1863 は新しい IADR を取らない）。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: **#1846**（planning#751 の裁定コメントの実測 6「全サービスが audience を検証していない」）。
- 計画 ADR: **ADR-0036**・**ADR-0032**（issue の受け入れ基準 4 が整合を求める 2 本）。**ADR-0086**（east-west は token exchange を採らず、呼び出し側サービス自身のトークンで呼ぶ）。**ADR-0134 決定 1**（MCP クライアントの audience を MCP サーバーに限る。#1844 で実装済み）。**ADR-0119 実測 11**（DocumentService が audience を検証していないことを記録）。
- NFR: **NFR-09**（サービス間の認証・認可）。
- 実装 ADR: IADR-0516（#1844 の追記。残余 3 が本件）・IADR-0086（発行元の検証）・IADR-0379 決定 4（`ServiceCaller`）・IADR-0429（BFF の Bearer 腕は無人の主体だけ）。
- 新しい IADR は **IADR-0523**（0521 は #1855 でマージ済み、0522 は並走 PR #1860〔#1839〕が取る）。

## 計画の読み（受け入れ基準 4 の前提）

- 計画の `projects/microservices-platform/` を `audience` で全文検索した（`git grep -i audience origin/main`）。**audience の方針を定めた箇所は ADR-0134（MCP サーバーに限る）だけ**である。
  ADR-0036・ADR-0032 に audience の語は無い。ADR-0119 と 10_feedback の 2 件は「検証していない」という実測の記録で、方針ではない。
- したがって受け入れ基準 4（「計画に audience の方針が無ければ、計画へ環流する」）の条件は**成立している**。

## 母集合（規則 9・10。誤りの側の文字列で走査した）

### 1. 検証の設定（`ValidateAudience` / `AddJwtBearer` / `AddPlatformAuth`）

| 箇所 | 内容 |
| --- | --- |
| `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/AuthExtensions.cs` `PlatformJwtBearer` | 🔴 `ValidateAudience = false`（全サービスの既定スキーム） |
| `src/platform/backend/Services/McpServer/Infrastructure/Authentication/McpAudienceAuthentication.cs` | `/mcp` だけ `ValidateAudience = true`・`ValidAudience = "mcp-server"`（#1844） |
| `src/platform/backend/Bff/Platform.Bff/Foundation/Session/BackchannelLogoutProcessor.cs` | ログアウトトークンの検証（`ValidAudience = ClientId`）。アクセストークンではない＝対象外 |
| `AddPlatformAuth` の呼び出し 15 件 | platform: AuthorizationService・LlmGateway・NotificationService・McpServer・BFF／knowledge: Feedback・Conversion・Retrieval・Document・Wiki・Ingestion・AiAnalysis・Dashboard・Graph・DataSource |
| 試験 4 件 | `AuthExtensionsTests`・`PlatformAuthJwtBearerOptionsTests`・`McpAudienceAuthenticationTests`（`BeFalse` を固定している＝是正で書き換える）・`RegistrantDepartmentTests`（試験内の手組みの検証。対象外） |

### 2. トークンの発行元（realm `deploy/keycloak/microservices-platform-realm.json`。25 クライアント）

- **audience の写像（`oidc-audience-mapper`）を持つクライアントは 0 件**。realm 独自の `roles` スコープは `realm roles` の写像だけで、Keycloak 既定の `audience resolve` も無い。
  → **現行のトークンの `aud` は空か、Keycloak の既定が入れる値だけ**と読める（稼働で未実測。実装の初手で integration-stack の evaluate-scopes で測る）。
  🔴 **写像を足さずに `ValidateAudience = true` にすると全経路が 401 になる。**
- 一次の呼び出し元（platform のサービスを呼ぶクライアント）: `bff`（利用者のセッショントークンの発行元を兼ねる）・サービスアカウント 10 件（document / datasource / conversion / ingestion / retrieval / aianalysis / wiki / graph / mcp-server / ＋ bff）・`synthetic-monitor`（`/bff/analysis/ask` を Bearer）・`abac-seeder`（`scripts/seed-abac-policies.js` 等）・**AST の 5 件**（`ai-stock-trading-kb-writer` / `-kb-reader` / `-llm-caller` / `-svc` / `-owner`）。
- 拒否されるべき発行元: 運用ツールの OIDC クライアント 5 件（wiki-js / headlamp / grafana / argocd / vault）・realm 管理用 3 件（identity-admin / mcp-client-admin / reset-gate。Keycloak の管理 API 専用）・SC-12 が動的に作る MCP クライアント（`aud=mcp-server` だけ）。
- 稼働中の realm への反映: `deploy/local/keycloak-setup/reconcile-realm.js` がクライアントスコープ・写像・既定スコープの割り当てを差分適用できる（L298〜354）。realm の import は初回だけ。

### 3. 呼び出しの行列（helm `values.yaml` の宛先から引いた。REST:8080 / gRPC:8081）

| 呼び出し元（クライアント） | 呼び出し先 |
| --- | --- |
| bff（利用者トークンの中継＋自身の SA。gRPC の自己申告の収集） | **全 13 サービス**（document・wiki・conversion・ingestion・retrieval・aianalysis・authorization・dashboard・datasource・feedback・graph・llmgateway・notification）＋ McpServer の管理 API |
| document-service | notification・authorization |
| datasource-service | authorization |
| conversion-service / ingestion-service | llmgateway |
| retrieval-service | llmgateway・graph・authorization |
| aianalysis-service | authorization・retrieval・llmgateway |
| wiki-service | authorization |
| graph-service | authorization・llmgateway・dashboard・document |
| mcp-server | authorization・document・retrieval・graph |
| synthetic-monitor | bff |
| abac-seeder（scripts） | authorization（ほか seed の宛先） |
| AST kb-writer / kb-reader / llm-caller | document / retrieval（検索）/ llmgateway |

- **利用者の経路**: ADR-0086 により token exchange は採らない。BFF は利用者のセッショントークン（`bff` クライアントの発行）をそのまま後段へ中継する。
  **したがって `bff` のトークンは BFF が中継する全サービスの audience を持たねばならず、利用者の経路ではサービスごとに絞れない**（どの方式でも同じ）。
- **east-west**: 呼び出し側サービス自身のトークン（`ServiceCaller` ＝ `platform-service` ロール）。ここは呼び出し先ごとに絞れる。

### 4. 実走の門・試験でトークンを作る箇所

- `.github/workflows/integration-stack.yml` ＋ `scripts/check-mcp-client-provisioning.js`（M1〜M10。M9 が evaluate-scopes で `aud` を測る）。
- `deploy/local/synthetic-monitor/probe.js` / `deploy/helm/.../files/synthetic-monitor/probe.js`（client credentials）。
- `scripts/seed-*.js`・`measure-*.js`・`republish-document-updated.js`（client credentials で各サービスを呼ぶ）。
- `scripts/verify-oidc-edge-flow.sh`（`--live` のみ）。
- 単体の試験は各サービスの WebApplicationFactory が `PostConfigure<JwtBearerOptions>` で鍵を差し替える型（`McpAudienceAuthenticationTests` が雛形）。

### 5. AST への影響（AST は編集しない）

- AST のサービスは自前の realm で検証し、MSP の `AddPlatformAuth` を使っていない（AST `40d992ee` で `AddPlatformAuth|PlatformJwtBearer` は 0 件）。
- AST が MSP を呼ぶ 3 経路（KB の保存・検索・LLM）は **MSP realm のクライアント**で取ったトークンを使う。**写像は MSP の realm に足せば足り、AST のコードは変わらない**。
  ただし AST の配備が MSP realm の写像の反映より先に新しい MSP を受けると 401 になる（順序の残余）。`ai-stock-trading-kb-reader` は既定スコープが `profile` だけで、写像の付け方に注意が要る。

### 6. 衝突（実装の前に必ず解く）

- 🔴 **`mcp-server` の値は「MCP クライアントのトークン」の意味で既に使っている**（`/mcp` の `McpAudience`）。McpServer の管理 API・gRPC の audience を同じ `mcp-server` にすると、
  BFF・サービスのトークンが `/mcp` を通ってしまう（#1844 の統制を壊す）。**McpServer の既定スキームには別の値を当てる**（例: 方式 A なら共有の値、方式 B なら `mcp-server-api`）。

## 裁定が要る点（ここで止めた）

計画には audience の方針が無く（上記）、issue 題の「サービスごとの audience」は方式を決めていない —— 利用者の経路はどの方式でも絞れず（ADR-0086）、差は east-west と第三者のトークンの扱いに出る。

| | A. 共有の platform audience（例 `platform-api`） | B. サービスごとの audience（issue 題の字義） | C. A ＋ 構成の口だけサービスごと |
| --- | --- | --- | --- |
| realm | クライアントスコープ 1 個（写像 1）を一次の呼び出し元 約 19 件の既定スコープへ | 呼び出し先ごとの写像を、上の行列どおり呼び出し元ごとに（bff は 14 個） | A と同じ |
| サービス | 全サービス `ValidAudiences=[platform-api]`（McpServer の `/mcp` は `mcp-server` のまま） | 各サービス `ValidAudience=<自分の名前>` | `Auth:Audiences` を各サービスの構成に持ち、既定値を `platform-api` にする |
| 拒否できるもの | 運用ツール・realm 管理用・MCP クライアントのトークン（issue の実害の全部） | 左に加え、行列に無い east-west（例: conversion の SA で authorization を呼ぶ） | A と同じ（後で B へ値を差し替えられる） |
| 利用者の経路 | 絞れない | 絞れない（bff が全宛先を持つ） | 絞れない |
| 保守 | 宛先の追加で realm は変わらない | **gRPC・REST の配線を足すたびに realm の写像も足す**。漏れは稼働で初めて 401 になる（行列と realm を突き合わせる検査器が要る） | A と同じ |
| 計画との整合 | ADR-0036・0032 に「platform の API は共有の audience で検証する」を足す環流 | 同「サービスごとの audience」を足す環流 | A と同じ環流＋「サービスごとへの絞り込みは将来」 |

**推奨: C**（実装は A の realm 変更の小ささで、構成の口をサービスごとに持たせる）。理由:

1. issue が挙げる実害（どのクライアントに発行されたトークンでも受け付ける）は A で全部塞がる。運用ツール・MCP クライアントのトークンが後段に届かなくなる。
2. B の追加の効果は east-west の絞り込みだけで、そこは既に `ServiceCaller`（ロール）・NetworkPolicy・STRICT mTLS（NFR-09）で守られている。一方で B は配線のたびに realm の写像を要し、漏れが稼働でしか見えない（検査器の新設を伴う）。
3. 利用者の経路は ADR-0086（token exchange を採らない）のため、どの方式でもサービスごとに絞れない。B の「サービスごと」は east-west に限られる。

**いずれの方式でも計画への環流（受け入れ基準 4）が要る**（project-planning へ `feedback.yml`・`decision-needed` で起票。起票前に同件の検索）。

## 裁定（2026-10-09・利用者。オーケストレーター経由）

- **方式 C** を採る。共有の audience は `platform-api`。1 つのクライアントスコープ（audience の写像）を正当な呼び出し元へ割り当て、各サービスは構成 `Auth:Audiences`（既定 `platform-api`）を検証する。サービスごとの audience へは構成だけで移れるようにする。
- **`mcp-server` を共有の audience に流用しない**（#1854 で「MCP クライアントに発行したトークン」の意味）。MCP クライアント・運用ツール（grafana・argocd・headlamp・vault・wiki-js）・realm 管理用のクライアントのトークンは platform のサービスで拒否する。`/mcp` の `McpAudience` スキームはそのまま。
- 計画へ環流する（planning#770 を起票した。起票前に planning の issue 200 件を audience / ADR-0036 / 1846 で検索し、同件が無いことを確かめた）。

## 実装の段取り（方式 C）

1. 稼働の現状測定: integration-stack で `bff` / サービス SA / AST クライアントのトークンの `aud` を evaluate-scopes で測る（現状の `aud` が空であることの確認）。
2. realm: クライアントスコープ `platform-api-audience`（`oidc-audience-mapper`・`included.custom.audience=platform-api`・access のみ）を足し、一次の呼び出し元の `defaultClientScopes` へ割り当てる。`reconcile-realm.js` で稼働 realm へも写す。`check-realm-constraints.js` に「一次の呼び出し元は全員このスコープを持つ」「運用ツールのクライアントは持たない」を足す。
3. `AuthExtensions.PlatformJwtBearer`: `ValidateAudience = true`・`ValidAudiences = Auth:Audiences`（未設定なら `platform-api`。空にはさせない＝fail-closed）。`McpAudience` は `ValidAudience = mcp-server` のまま上書きで残す。
4. 試験: 共有の器の単体（既定値・構成の上書き・空を許さない）、代表のサービス（BFF・AuthorizationService・McpServer・Document）で「`aud=platform-api` は 200／`aud=mcp-server` だけ・`aud` 無し・`aud=grafana` は 401」、McpServer で「`aud=platform-api` のトークンは `/mcp` で 401」。
5. integration-stack の門に否定形を 1 つ（運用ツールのクライアントのトークンで後段が 401）と、既存の門が通ること。
6. docs（trace ブロック）・IADR-0523・CHANGELOG は生成物のため手で書かない。

## 受け入れ基準（issue の 4 項目の写し）

- [x] サービスごとに期待する audience を決め、realm の宣言でトークンに載せる。（裁定 C: 全サービス既定 `platform-api`。realm のスコープ `platform-api-audience` を呼び出し元 17 件へ）
- [x] `ValidateAudience = true` と `ValidAudiences` をサービスごとに設定し、BFF の Token Handler と east-west の経路で既存の呼び出しが通る。（`Auth:Audiences`。稼働の通過は integration-stack の既存の門＋M11 で測る）
- [x] 他のサービス向けのトークンを拒否する否定形の試験を置く。（ConversionService REST・AuthorizationService gRPC・McpServer `/mcp`・構成の器）
- [x] 計画に audience の方針が無いので計画へ環流する（ADR-0036・ADR-0032 との整合）。（planning#770）

## 実装の結果（2026-10-09 追記）

- 試験の器でトークンを作る箇所（母集合 4 の単体側）を `IssuerSigningKey|SecurityTokenDescriptor|JsonWebTokenHandler|ValidateAudience` で引き直した: 16 ファイル。うち audience を載せたもの 11（gRPC の Kestrel 8・LlmGateway・ConversionService・BFF の Bearer 腕）、変えないもの 5（`McpAudienceAuthenticationTests`＝自前で audience を指定・`BackchannelLogoutTests`＝ログアウトトークン・`RegistrantDepartmentTests`＝試験内の手組みの検証・`AuthExtensionsTests` / `PlatformAuthJwtBearerOptionsTests`＝期待値の更新）。
- 実走の門でトークンを作る箇所（母集合 4 の稼働側）: `check-mcp-client-provisioning.js` の使い捨ての登録者（既定スコープを自前で割り当てる）にスコープを足した。`seed-*.js`・`synthetic-monitor`・`verify-oidc-edge-flow.sh` は realm のクライアント（`abac-seeder`・`synthetic-monitor`・`bff`）を使うので realm の変更で足りる。
- `McpAudience` の `ValidAudiences` を `["mcp-server"]` へ置き換えた（共有の設定の `platform-api` が残ると和集合で照合される）。

## 残余

- 本仕様書の時点で、現行トークンの `aud` の実値は稼働で測っていない（realm の宣言の読みだけ）。
- AST の配備順序（MSP realm の写像が先に入っていないと AST の 3 経路が 401）。AST 側の作業は無いが、配備の告知が要る。
