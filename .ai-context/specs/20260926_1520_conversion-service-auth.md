---
title: "ConversionService が BFF の中継した利用者の資格情報を自ら検証し、変換ジョブの 5 口すべてに端点の門を張る（#1520）"
type: spec
status: in-progress
related_ids: [NFR-09, NFR-16, FR-12, UC-06, SC-07, ADR-0004, ADR-0029, ADR-0084, ADR-0109, IADR-0029, IADR-0042, IADR-0128, IADR-0154, IADR-0379, IADR-0403, IADR-0424, IADR-0458, IADR-0462]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0109_bff-user-credential-relay-is-edge.md 決定 1・2・3・4
  - planning:projects/microservices-platform/07_adr/ADR-0084_nfr09-judgment-unit-and-interim-clause-release.md 決定 1（端点単位・門の定義）・決定 4
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md 決定 1（射程は east-west。ADR-0109 決定 2 で確認）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-09
---

# 仕様書: ConversionService が中継された利用者の資格情報を検証する（#1520）

## 起点となる計画書（トレーサビリティ）

- 非機能要件: **NFR-09**（恒久: 全 API で OIDC/JWT 認証。暫定: エッジ（BFF）で担保）／NFR-16（サービス間 mTLS。変更なし）
- 機能・画面: FR-12・UC-06・SC-07（変換ジョブの照会・再変換・人手補正。閲覧は管理者・運用者、再変換と人手補正は管理者限定）
- 計画 ADR: **ADR-0109**（Accepted・利用者裁定 2026-09-26・planning#651）決定 3「エッジの後段は、中継された利用者の資格情報を自ら検証する。ConversionService も検証する（他の 14 サービスと同じ形）」／**ADR-0084** 決定 1（端点単位で判定。門は端点または端点群に付くもの。`FallbackPolicy` は門ではない）・決定 4（暫定条項での追認。本件の着地まで有効）
- 実装 IADR: IADR-0403（決定 3〜5・フォローアップ 3）／IADR-0458（決定 1・残るもの 4）／IADR-0042 決定 3・IADR-0128 決定 3・IADR-0154 決定 6（ワーカーに認可を課さない）／IADR-0379 決定 4（`ServiceCaller`）／IADR-0424（本物の JwtBearer を通す試験）
- 起票: #1520（参照: #1397・#1505・#458）
- **新規 IADR: IADR-0462**（`IADR-0403` 決定 5 を ConversionService について覆すため。develop の最大 0460、開いている PR #1513 が 0461 を使用 → 0462）

## 目的・背景

ConversionService は認証を持たない（`AddPlatformAuth` / `RequireAuthorization` / `AddAuthentication` が 0 件）。BFF は利用者の資格情報を付けて
中継するが後段は読まず、門は BFF の 1 枚だけだった。IADR-0403 が当てにしていた east-west gRPC 化の経路は ADR-0109 決定 1 で消え、
決定 3 が「後段が自ら検証する」に置き換えた。これを実装する。

## 🔴 着手前に確認した制約

- **ADR-0084 決定 1**: `AddPlatformAuth` だけでは端点は閉じない。**5 口すべてに端点（群）の門を掛ける。** `FallbackPolicy` は使わない（門に数えない）。
- **ADR-0109 決定 2**: エッジの中継は利用者トークンをそのまま運ぶ。ADR-0086 決定 1（本文で運ぶ）は適用しない。
- **IADR-0379 決定 4**: `ServiceCaller` は利用者のトークンを通さない別軸。変換の口に s2s を相乗りさせない（呼び出し元が無い）。
- **audience は検証しない**: `AddPlatformAuth` は全サービス共通で `ValidateAudience = false`。ConversionService だけ変えると BFF の中継トークンで閉じる。
  依頼文の「wrong audience → 403」は**ロール違い → 403** と、**署名・発行元・期限の不正 → 401** で置き換えて試験する（差異として報告する）。
- ADR-0109 は計画 ADR レンジ（`ADR-0001..0107`）の外である（#1519 が 0110 へ開ける）。**コミット件名・PR タイトル・`docs/` の trace ブロックの `adrs:` には書かない。**
  コード内コメントと `.ai-context/` の本文には書く（検査対象外）。

## 母集合（着手時に自分で引いた）

基点 `origin/develop` `e434eba5`。`git rev-parse --is-shallow-repository` = `false`。

### 軸 1: ConversionService の HTTP の口（`git grep -E "\.Map(Get|Post|Put|Delete|Patch|Methods|Group|HealthChecks|Grpc|OpenApi)\b|MapPlatform[A-Za-z]+\("`、Tests 除外）

| # | 口 | 扱い |
| --- | --- | --- |
| 1 | `GET /jobs` | 門（群: admin ＋ operator） |
| 2 | `GET /jobs/{id}` | 門（群） |
| 3 | `POST /jobs/{id}/retry` | 門（群 ∧ `AdminOnly`） |
| 4 | `GET /jobs/{id}/figures` | 門（群 ∧ `AdminOnly`） |
| 5 | `POST /jobs/{id}/figures/{figureId}/correction` | 門（群 ∧ `AdminOnly`） |
| — | `MapPlatformHealthChecks`（`/health/live`・`/health/ready`） | **門を持たない**（除外理由: プローブ。他サービスと同じ） |
| — | `MapPlatformIntrospection`（`/internal/introspection`） | **門を持たない**（除外理由: `HttpEffectiveConfigCollector` は利用者の資格情報を持たない。他サービスと同じ。保護はネットワーク分離と mTLS） |

gRPC 面は持たない（`MapGrpcService` 0 件）。IADR-0403 の「5 口」と一致する。

### 軸 2: 呼び出し元（`git grep -i "conversion-service:8080|Services:ConversionService|Services__ConversionService|conversion-service/jobs|:8080/jobs"` ＋ `CreateClient("ConversionService")` ＋ AST submodule 内の `git grep` ＋ フロントの `/jobs` 呼び出し）

| 呼び出し元 | 口 | 資格情報 | 扱い |
| --- | --- | --- | --- |
| BFF `ConversionBffEndpoints.Forwarding`（6 口: 一覧・個別・再変換・図の一覧・図の画像〔後段は図の一覧〕・人手補正） | `/jobs*` | **利用者の資格情報を全口で中継**（`Forwarding` が `Authorization` を転写。受信側は `SessionTokenPropagationMiddleware` がセッションから付ける） | 後段で検証（本件）。中継の試験を追加 |
| BFF の introspection 収集（`Introspection__Services__conversion-service`） | `/internal/introspection` | 付けない | 影響なし（門を持たない口） |
| Kubelet（Helm は `worker: true` で HTTP プローブを描かない）／compose | `/health/*` | — | 影響なし |
| Wolverine 購読 `RawDocumentFetchedConsumer` | （HTTP ではない） | — | 影響なし（ミドルウェアは HTTP パイプラインだけ） |
| AST submodule（pin `471cbf31`） | — | — | **0 件**（`git grep` のヒットは記録文書の語だけ） |
| フロント（SC-07 画面・生成クライアント） | `/bff/conversion/jobs*` | BFF セッション | 後段を直接叩かない（BFF 経由） |
| スクリプト・e2e・ワークフロー | — | — | **0 件**（`/jobs` を直接叩くものは無い） |

**サービス間の呼び出し元は 0 件** → `ServiceCaller` は通さない（IADR-0462 決定 2）。

### 軸 3: 配備の構成（`Auth__Authority` 等）

| 面 | 実測 | 扱い |
| --- | --- | --- |
| Helm `templates/deployment.yaml` | 全サービスへ `global.auth` から `Auth__Authority`（任意で `Auth__MetadataAddress`・`Auth__ValidIssuers`）を描く | **変更なし**。オフライン描画（`helm template`・base と `values-local.yaml`）で conversion-service の Deployment に 3 変数が出ることを確認 |
| compose | `conversion-service` は `x-common-env`（`Auth__Authority`）を継承 | 変更なし |
| NetworkPolicy | ConversionService は既に Keycloak へ出ている（s2s トークンの client credentials）。JWKS も同じ宛先 | 変更なし |

### 軸 4: 誤りの側の記述（「ConversionService は認証／認可を持たない・課さない」）

`git grep -i -E "(conversion|変換).{0,80}(認可を課さない|認証を持たない|無認証|無認可|層なし|authn|認可なし|認証なし|認証を課さない|門は BFF|門が無い|アプリ層.{0,10}(認証|認可))|…逆順"`（`src/ai-stock-trading`・`.ai-context/specs`・`.ai-context/superpowers` を除く）＋ `AddPlatformAuth|FallbackPolicy|OIDC/JWT|全 API` を `docs`・README で。

| ヒット | 扱い |
| --- | --- |
| `IADR-0042` 決定 3 の［2026-08-05 追記］「ワーカー自身に認可を課さない点は変更していない」 | 日付つき追記で IADR-0462 を指す |
| `IADR-0128` フォローアップ 1「アプリ層認証の要否を別 issue で判断」 | 日付つき追記（決着） |
| `IADR-0154` 決定 6「ワーカー自身に認可を課さない点も変えない」 | 日付つき追記 |
| `IADR-0403` 決定 3 表 14 行目・決定 4・決定 5・フォローアップ 3 | 日付つき追記（決定 3 要約の直後・決定 5 末尾・フォローアップ 3） |
| `IADR-0458` §計画 ADR の文面と食い違う・決定 1 例外・理由（14 本）・残るもの 4 | 日付つき追記（§食い違い・決定 1・残るもの 4）。理由節の「14 本にしか当たらない」は当時の記述として残す（決定 1 の追記が 15 本になったと言う） |
| `docs/api/east-west-grpc.md:313`「変換のワーカーだけは後段が認証を持たず」 | 日付つき追記 |
| `docs/security/security.md:62`「未対応: ConversionService /jobs の後段認可」 | 二重化の一覧へ移し、未対応から外す |
| `docs/tests/SC-07_conversion-jobs.md` デプロイ表 1 行目「認可を課さない前提」 | 過去形へ直し、後段の門の節を追加 |
| `NetworkIsolationTests.cs:34` のコメント「アプリ層の認可を課さない」 | 過去形＋日付つき注記 |
| `ConversionJobEndpoints.cs` のコメント「ここでは認可を課さない」 | 本件で書き換え（コード変更と同時） |
| `docs/screens/SC-07`・`docs/tests/UC-06`・`BffConversionEndpointTests.cs:9` | **除外**: BFF の門の記述であり後段の有無を言っていない |
| `docs/security/security.md:131・322・337`（「内部 API での OIDC/JWT 検証は残課題」） | **除外**: 全 east-west の恒久像の記述（未移行の REST east-west は ADR-0084 決定 5 の暫定のまま）。ConversionService 固有ではない |
| `docs/tests/NFR-09_bff-edge-authentication.md` | **除外**: エッジ（BFF）の担保の仕様書。変換に触れるのは画面設計の引用 1 行だけ |
| `.ai-context/adr/README.md` の IADR-0458 行「計画 ADR への反映は環流待ち」 | **除外**: 索引は表題の写しであり表題は書き換えない（本文の追記が解消を言う） |
| 確定済みの `.ai-context/specs/`（#501・#543・#1397 等） | **除外**: 凍結記録（traceability.repo.md） |

### 軸 5: 既存の試験で門の手前を通っていたもの

`WebApplicationFactory<Program>` を使う ConversionService の試験 3 クラス: `ConversionJobEndpointTests`・`ConversionFigureCorrectionTests`（`/jobs` を無資格で叩く → 401 で壊れる）、
`IntrospectionEndpointTests`（門を持たない口 → 影響なし）。前 2 者は `ConfigureClient` で管理者の利用者トークンを既定で載せる（門は新クラスが測る）。
`Knowledge.IntegrationTests` の `ConversionServiceFactory` はどのテストからも参照されていない（同ファイルの注記）→ 影響なし。

## 設計

- `Program.cs`: `AddPlatformAuth` と `UsePlatformMiddleware`（端点の登録より前）。
- `ConversionJobEndpoints`: 群に `RequireRole(platform-admin, platform-operator)`。`Retry`・`ListFigures`・`CorrectFigure` に `AdminOnly`（AND 合成）。**BFF の門と同じ。**
- 試験用の JWT は `TestUserTokens`（LlmGateway の `TestServiceTokens` と同じ作法。metadata を静的構成へ・検証鍵をテスト用へ差し替え、本物の JwtBearer を通す）。
- **試験のホストはソケットを開かない**（`WebApplicationFactory` の TestServer はメモリ内。bind しない）。

## 受け入れ基準 → テスト

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 資格情報なしは 5 口すべてで 401 | `ConversionJobAuthorizationTests.EveryRoute_WithoutCredential_Returns401` |
| 2 | 偽造・他の発行元・期限切れは 401 | `InvalidToken_EvenClaimingAdmin_Returns401` |
| 3 | 門のロールを持たない利用者は 403 | `EveryRoute_UserWithoutGateRole_Returns403` |
| 4 | サービス間トークンは 403 | `EveryRoute_ServiceAccountToken_Returns403` |
| 5 | 中継された運用者トークンで照会 200・管理者限定 3 口 403 | `Queries_WithRelayedOperatorToken_Return200` / `AdminOnlyRoutes_WithRelayedOperatorToken_Return403` |
| 6 | 中継された管理者トークンで 5 口が門を通る | `EveryRoute_WithRelayedAdminToken_PassesTheGate` |
| 7 | プローブと自己申告は門を持たない | `ProbeAndIntrospection_WithoutCredential_Return200` / `Readiness_WithoutCredential_IsNotGated` |
| 8 | BFF は 6 口すべてで資格情報を中継する | `BffConversionEndpointTests.EveryRoute_RelaysTheUsersCredential_ToConversionService` |
| 9 | 購読は影響を受けない | 既存 `RawDocumentFetchedConsumerTests` / `PipelineStepRegistrationTests` が緑のまま |
| 10 | 配備に `Auth__Authority` が届く | オフライン `helm template`（base・local）で確認（上の軸 3） |

## 対象範囲外

- BFF の門の変更（変えない）。audience の検証（共通設定の射程）。ABAC（変換ジョブは運用資産）。IngestionService・LlmGateway（ADR-0084 決定 4 末尾の別件）。
- 実配備での疎通（クラスタに触れない）。
- 計画側の記録（ADR-0084 決定 4 への充足の記録は ADR-0109 フォローアップ 3 として計画側が行う）。
