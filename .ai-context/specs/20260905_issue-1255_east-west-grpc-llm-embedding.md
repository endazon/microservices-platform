---
title: east-west gRPC の展開（第 1 スライス）— LlmGateway の埋め込み面を gRPC で開き、Retrieval / Ingestion の 2 呼び出し元を兄弟クラスで移す
type: spec
status: draft
related_ids:
  - FR-02
  - FR-03
  - FR-05
  - NFR-09
  - NFR-16
  - ADR-0010
  - ADR-0013
  - ADR-0016
  - ADR-0017
  - ADR-0029
  - ADR-0030
  - ADR-0075
  - IADR-0117
  - IADR-0256
  - IADR-0313
  - IADR-0316
  - IADR-0379
  - IADR-0397
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md §決定・2026-08-04 追記
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 1・3・4・5・6
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md（越境・fail-closed）
  - planning:projects/microservices-platform/07_adr/ADR-0017_selfhosted-embedding-ruri.md
  - planning:projects/microservices-platform/07_adr/ADR-0013_embedding-model.md
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md（Grpc.* は採用済み）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md FR-02 / FR-03 / FR-05 / NFR-09 / NFR-16
---

# 仕様書: LlmGateway 埋め込み面の east-west gRPC 化（#1255 第 1 スライス）

> 本書は #1255（east-west gRPC の 31 呼び出しの展開）の**最初のスライス**の作業仕様である。
> 先行する設計（`IADR-0379` の 4 決定と `docs/api/east-west-grpc.md` §1〜§4）は**変えない**。
> 本スライスは `/embed` の 1 端点・2 呼び出し元だけを対象とし、`/complete` 系と認可の名簿は
> 後続 PR に送る（下記「対象範囲」）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-02（取り込み文書の埋め込み生成・索引）／FR-03（ハイブリッド検索のクエリ埋め込み）／
  FR-05（機密区分に基づく越境判定＝ `/embed` の fail-closed ルーティング）
- 非機能要求（NFR）: NFR-09（全 API で OIDC/JWT 認証。gRPC 面は `ServiceCaller`）／NFR-16（サービス間 mTLS。h2c ＋ サイドカー終端）
- ユースケース（UC）: UC-01（横断検索）／UC-04（データソース同期→取り込み）
- 画面（SC）: 直接の画面は無い（サービス間経路）
- 関連 ADR: ADR-0029（east-west 同期は gRPC・所有者は呼ばれる側・キャッシュ等は呼び出し元）／
  ADR-0075（移行順序＝基盤先行。決定 4 で AST は MSP の proto 公開に追随）／ADR-0010（LLM ゲートウェイ）／
  ADR-0013・ADR-0016・ADR-0017（埋め込みモデル・プロバイダ・セルフホスト／越境と fail-closed）／
  ADR-0030（`Grpc.*` は採用済みライブラリ。新規追加ではない）
- 実装 ADR: `IADR-0379`（先行条件の 4 決定。**本作業はその適用であり改定しない**）／
  `IADR-0256` 決定 3（設計上の縮退は続行・本当の故障は上げる）／`IADR-0117`（ユニット外参照は `Shared/` の 3 プロジェクト）／
  `IADR-0313`（決定的ローカル埋め込み）／`IADR-0316`（Secret 注入の宣言と配備の突合）／
  本作業で新設する `IADR-0397`
- 計画書リンク: 隣接クローン `../project-planning/projects/microservices-platform/`（読み取り専用）

## 目的・背景

`IADR-0379` が east-west gRPC の先行条件（置き場・versioning・h2c・s2s）を 1 経路の参照実装で満たした。
#1255 はその形を残り 31 呼び出しへ写す。本スライスはそのうち**最小で完結する 1 端点**、すなわち
LlmGateway の `POST /embed` と、それを呼ぶ 2 サービス（RetrievalService / IngestionService）を担当する。

なぜ埋め込みを最初に切るか:

- `/embed` は**利用者の文脈を一切持たない**（要求本文は `text` / `confidentiality` / `purpose` のみ）。
  したがって `IADR-0379` 決定 4 の 🔴「利用者トークンをメタデータへ載せない」と正面衝突する論点が無い。
- 縮退の形が 1 つ（`Embedded=false` の**応答**）に閉じており、REST↔gRPC の同値を機械で示しやすい。
- s2s の資格情報を**実際に配備へ配線する最初の PR**になる（下記「母集合」の観測 3 を参照）。

## 対象範囲

### 対象

1. `Protos/platform/llmgateway/v1/embedding.proto`（`Platform.Shared.Contracts`）
2. LlmGateway の gRPC 面 `Features/Embeddings/Embed/GrpcService.cs`（`[Authorize(Policy = ServiceCaller)]`）と
   `Grpc.AspNetCore` 参照・`AddPlatformGrpcListener()` ＋ `MapGrpcService<>()`
3. 🔴 REST と gRPC が**同じハンドラ本体**を通るよう、`/embed` の本体を `EmbedUseCase` へ括り出して
   両面が呼ぶ（`IADR-0379` 決定 5「評価器を 2 つにしない」と同じ向き）
4. 共通の呼び出し側部品 `Platform.Shared.Infrastructure/Foundation/Llm/`
   （`AddLlmGatewayGrpcClient(config)` と DTO↔proto の写像のみ。キャッシュ・タイムアウト・リトライ・
   fail-safe は呼び出し元の Infrastructure。ADR-0029 2026-08-04 追記）
5. 2 呼び出し元の兄弟クラス（`LlmGatewayGrpcEmbeddingService`）と Program.cs の登録切替
6. 配備: helm `services.llm-gateway.grpcPort: 8081` / compose `expose` ＋ `Grpc__Port` / readiness は HTTP 8080 のまま
7. Keycloak realm: `retrieval-service` / `ingestion-service` の confidential client（service account ＋ realm ロール
   `platform-service` のみ。**ABAC ポリシーは与えない**）と `ServiceToken__*` / `Services__LlmGatewayGrpc` の配線
8. `scripts/proto-contract-baseline.json` の更新（`--update`）
9. `IADR-0397`（本スライスで実際に決めたことだけ。後続スライスの決定は書かない）

### 対象外（後続 PR）

| 対象外 | 送り先 | 理由 |
| --- | --- | --- |
| `completion.proto`（`Complete` / `CompleteStream`）と 4 呼び出し箇所（AiAnalysis ×2・Graph・Conversion） | 次の PR | server-streaming と TTFT 計器の論点を含み、レビュー単位として独立している |
| `user_directory.proto` と認可サービスの 2 呼び出し元（DataSource・McpServer） | その次の PR | 利用者トークン転送の置き換えという別の論点（読み口を狭める判断） |
| `/authz/scope` の 3 呼び出し元（AiAnalysis・Graph・Wiki） | 同上 | 既存 proto の利用であり、認可スライスへまとめる方が差分が読める |
| BFF → 各サービス 14 本ほか | #1255 の別 PR | 呼び出し先ごとに切る最小単位 |
| REST `/embed` の撤去 | 撤去段の IADR | **並走中の正は REST**（`IADR-0379` 決定 5）。切替は構成だけで戻せる |
| 参照実装（BFF → AuthorizationService）の未配線の是正 | 認可スライス | 下記「母集合」の観測 3・4 を参照。**本 PR では意図的に触らない** |
| `OpenTelemetry.Instrumentation.GrpcNetClient` / gRPC ヘルスプロトコル | #1255 やること 6 | CPM 追加を伴う別判断 |

## 母集合の再導出（自分で引いた。設計書・issue の数字を転記していない）

基点: `origin/develop` `6138a7ad`。`git rev-parse --is-shallow-repository` = **`false`**
（履歴の打ち切りが無いので `git log` を出典に引ける。planning#410）。

🔴 **軸を 1 本で終わらせない**（`traceability.repo.md` 規則 5）。**名前（型名・クライアント名）で引く軸は
呼び出し箇所を落とす**ことが判っているので、**端点の文字列**を第 1 軸に据え、独立な 3 軸で交差させた。

### 軸 1（採用）: 端点の文字列 `"/embed"`

```console
$ grep -rn --include=*.cs '"/embed"' src | grep -v /obj/ | grep -v /Tests/
src/knowledge/backend/Services/IngestionService/Infrastructure/ExternalServices/LlmGatewayEmbeddingService.cs:15:            "/embed",
src/knowledge/backend/Services/RetrievalService/Infrastructure/ExternalServices/LlmGatewayEmbeddingService.cs:15:            "/embed",
src/platform/backend/Services/LlmGateway/Features/Embeddings/Embed/EmbeddingEndpoints.cs:18:        g.MapPost("/embed", async (
```

**陽性対照（対になる走査）**: 同じパターンから `Tests/` の除外だけを外すと **9 行**（3 → 9）。
除外が効いており、かつ走査そのものが生きている（0 件走査を緑にしない）。除外した 6 行はいずれも
テストの偽サーバ・偽応答であり呼び出し元ではない。

### 軸 2: 宛先構成キー `Services:LlmGateway` / `Services__LlmGateway` の消費者

`AddHttpClient` の登録側を**行単位ではなく構成キーで**引く。製品コードは 6 箇所
（AiAnalysis ×2〔health probe と補完〕・Conversion・Graph・Ingestion・Retrieval）。
このうち `/embed` を呼ぶのは **Ingestion と Retrieval の 2 つだけ**で、残り 4 つは `/complete` 系（対象外）。
配備側は compose 4 行・helm 5 行に現れ、`llmgateway` を含めた 5 サービスが宛先を持つ。

### 軸 3: ポート実装 `: IEmbeddingService`

製品コードの実装は **2 件**（Ingestion / Retrieval の `LlmGatewayEmbeddingService`）。
残り 12 件はすべて `Tests/` 配下のスタブ（陽性対照: スタブが 12 件見つかることで、
「実装が 2 件しかない」が走査漏れではなく事実であることが裏づけられる）。

### 軸 4: 契約 DTO `EmbedApiRequest` / `EmbedApiResponse` の利用者

**URL の組み立て方に依存しない**最も強い軸。5 ファイル: 呼び出し元 2・呼び出し先 1・
呼び出し先の試験 1・DTO 定義 1。**4 軸すべてが同じ 2 呼び出し元へ収束した。**

### 読み（本スライスの母集合）

| 集合 | 件数 | 内訳 |
| --- | --- | --- |
| 移す呼び出し元 | **2**（各 1 箇所） | Retrieval（`Purpose=Query`）／Ingestion（`Purpose=Index`） |
| 移す呼び出し先 | **1** | LlmGateway `POST /embed` |
| 追加する proto | **1** | `platform/llmgateway/v1/embedding.proto`（既存 proto は `authz_scope.proto` の 1 本のみ） |
| 新設する confidential client | **2** | `retrieval-service` / `ingestion-service` |
| 除外（同じ走査に掛かるが対象外） | 4 | `/complete` 系 4 箇所（次 PR）／AiAnalysis の `/health/live` プローブ（readiness は HTTP のまま。ガイド §3） |

### 観測（走査で判ったこと。設計書の主張を自分で確かめた）

1. **観測 1（確認）**: `git ls-files "*.proto"` = 1 件（`authz_scope.proto`）。LlmGateway の proto は 0。
2. **観測 2（確認）**: `Grpc__Port` は compose の `authorization-service` に 1 行のみ。
   helm は `grpcPort: 8081` を `authorization` にだけ宣言。
3. 🔴 **観測 3（確認・重要）**: `ServiceToken__*` と `Services__AuthorizationServiceGrpc` は
   **helm・compose に 1 件も無い**。すなわち**参照実装は配備上 1 度も走っていない**。
   本 PR が s2s の資格情報を実際に配線する最初の PR である。
4. 🔴 **観測 4（設計書に無い新規の発見）**: realm の service account に `platform-service` を持つ主体は **0**。
   `platform-service` ロールは realm に**定義されている**（`roles.realm`）が、`users[]` の
   service account 3 件（`service-account-abac-seeder` / `-identity-admin` / `-ai-stock-trading-kb-writer`）の
   `realmRoles` にも、`bff` の service account（そもそも `users[]` に**存在しない**）にも付いていない。
   `IADR-0379` 決定 4 の散文「realm の `bff` に service account と `platform-service` を付けてある」は
   **realm export の実体と一致していない**（`serviceAccountsEnabled: true` はあるが、ロール割当が無い）。
   本 PR では自分の 2 client について `service-account-<name>` を `users[]` へ追加して
   `realmRoles: ["platform-service"]` を与える。**`bff` 側は意図的に触らない**（認可スライスの射程）。
5. **観測 5**: `ServiceTokenOptions.ClientSecret` は `//` 注記だけで、`check-secret-injected-options.js` の
   宣言マーカ（`k8s Secret から環境変数で注入する`）を持たない。したがって同検査器の母集合に**入っていない**。
   同検査器は宣言を**リポジトリ全体で 1 度だけ**突合する（サービスごとではない。`computeViolations` は
   `helmSecretEnvs` / `composeEnvs` の**集合**を見る）ので、宣言を足したうえで本 PR の 2 サービスに
   `ServiceToken__ClientSecret` を配線すれば検査は通る。**足す**（判断と根拠は下記「設計」§7）。

### 除外の理由（母集合から落としたもの）

- `Tests/` 配下: テストの偽サーバ・スタブであり呼び出し元ではない（軸 1・軸 3 の陽性対照で件数を明示した）。
- `/obj/`: 生成物。
- `src/ai-stock-trading`（submodule）: 別プロジェクトの所有。`ADR-0075` 決定 4 により
  **MSP が proto を公開した時点で AST が追随する**（本リポジトリからは起票しない）。
- AiAnalysis の `AddUrlGroup(... "/health/live")`: readiness / liveness は HTTP の規約（ガイド §3）。

## 設計

### 1. proto（`Platform.Shared.Contracts/Protos/platform/llmgateway/v1/embedding.proto`）

`IADR-0379` 決定 1・2 に従い、所有サービス（LlmGateway＝platform）のユニットの共有契約プロジェクトへ置く。
package `platform.llmgateway.v1`、`csharp_namespace = "Platform.Shared.Contracts.Grpc.LlmGateway.V1"`。
`service LlmEmbedding { rpc Embed(EmbedRequest) returns (EmbedResponse); }`。
フィールドは REST の `EmbedApiRequest` / `EmbedApiResponse` と 1 対 1。

🔴 **proto3 に null は無い**（`IADR-0397` 決定 3）。`EmbedPurpose` の 0 は `EMBED_PURPOSE_UNSPECIFIED` であり、
REST の DTO 既定は `EmbedPurpose.Index` である。**サーバ側で `UNSPECIFIED → Index` を明示的に写す。**
写し漏れは「用途が Query として routing される」形でも「例外」でもなく、**Index として routing されなくなる**
静かな取り違えになるため、T-S-07 で固定する。
`confidentiality` の空文字は既存の `SensitivityClasses.Parse` が restricted（安全側）へ倒すので写し不要（実測）。

### 2. 呼び出し先（LlmGateway）

- `LlmGateway.csproj` に `Grpc.AspNetCore`（CPM のため版は書かない。`AuthorizationService.csproj` と同じ注記）。
- `Program.cs`: `builder.AddPlatformGrpcListener()`（`AddGrpc()` は常に呼ばれ、`Grpc:Port` 未設定なら
  リスナは立たない）と `app.MapGrpcService<LlmEmbeddingGrpcService>()`。
- 🔴 **REST と gRPC は同じハンドラ本体を通す。** 現行 `EmbeddingEndpoints` のラムダ本体（越境判定・
  プロバイダ解決・次元照合・例外時の縮退）を `Features/Embeddings/Embed/EmbedUseCase.cs` へ括り出し、
  REST の `MapPost` と gRPC の `Embed` の**両方がこれを呼ぶ**。判定器を 2 つにしない。
  縮退の 4 経路（越境拒否／プロバイダ未登録／次元不整合／上流不調）は use-case の中に閉じ、
  gRPC でも `RpcException` にせず `embedded=false` の**応答**で表す（REST の 200 ＋ `Embedded=false` と同値）。
- `[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]` を gRPC サービス型に付ける。
  REST `/embed` は現行どおり無認可のまま（**gRPC 面のほうが強い**向きの変更であり、緩めていない）。

### 3. 共通の呼び出し側部品（`Platform.Shared.Infrastructure/Foundation/Llm/`）

- `LlmGatewayGrpcClientExtensions.AddLlmGatewayGrpcClient(config)`:
  `Services:LlmGatewayGrpc` が構成されたときだけ `AddPlatformServiceToken` ＋ `CreatePlatformChannel` ＋
  `LlmEmbedding.LlmEmbeddingClient` を singleton 登録する。未設定なら**何も登録しない**
  （`AddAuthzScopeGrpcClient` と同型）。チャネルは `TryAddSingleton` の名前付きにせず、
  `AuthzScopeGrpcClient` の既存チャネル登録と**衝突しないよう別の型で包む**（同一プロセスで両方を
  構成した場合に `GrpcChannel` の singleton が取り合いにならないようにする）。
- `LlmGrpcMapping`: `EmbedApiRequest ↔ EmbedRequest` / `EmbedResponse → EmbedApiResponse` の**写像だけ**。
  キャッシュ・タイムアウト・リトライ・fail-safe は置かない（ADR-0029 2026-08-04 追記）。

### 4. 呼び出し元（2 サービス）

**兄弟クラス**を足し、Program.cs が構成で選ぶ（既存の REST クラスは 1 文字も変えない ——
戻すときに構成を外すだけで済ませるため）。

| # | 呼び出し元 | 新クラス | 切替 | 縮退の写し |
| --- | --- | --- | --- | --- |
| 4 | Retrieval `LlmGatewayEmbeddingService.EmbedAsync`（`Purpose=Query`） | `LlmGatewayGrpcEmbeddingService : IEmbeddingService` | `Services:LlmGatewayGrpc` | 🔴 **`RpcException`・s2s トークン取得失敗は例外のまま上げる**（現行 `EnsureSuccessStatusCode` と同じ。`IADR-0256` 決定 3「故障を『該当なし』に化けさせない」）。`embedded=false` は現行どおり `[]` |
| 5 | Ingestion `LlmGatewayEmbeddingService.EmbedAsync`（`Purpose=Index`） | 同上 | 同上 | 🔴 同じく例外（MassTransit の再試行／DLQ へ回す）。`Retryable` はゲートウェイの値をそのまま運ぶ。**「応答欠落」相当の枝は proto では起こらない**ので、REST 実装の `result is null → Retryable: true` に当たる分岐は gRPC 側に持たない（起こり得ないケースへの防御的実装をしない） |

🔴 **空ベクトルへ縮退させない。** 輸送の失敗（`UNAVAILABLE` ほか）を `[]` にすると、Retrieval では
「意味検索の該当なし」に、Ingestion では「索引スキップ」に化ける。どちらも `IADR-0256` 決定 3 が
名指しで禁じた形である。

### 5. 配備

- helm `values.yaml`: `services.llmgateway.grpcPort: 8081`（既存テンプレートが containerPort・Service ポート
  〔`appProtocol: grpc`〕・`Grpc__Port` を描画する。**宣言しないサービスは 1 バイトも変わらない**）。
  readiness は 8080 の `/health/ready` のまま（1 プロセスが両ポートを起動時に bind する）。
- compose `llm-gateway`: `expose` に `"8081"` を足し `Grpc__Port: "8081"`。host へは公開しない。
- 呼び出し元 2 サービス（helm `extraEnv` / compose `environment`）:
  `Services__LlmGatewayGrpc`（helm は `http://llmgateway-service:8081`、compose は `http://llm-gateway:8081`。
  🔴 **k8s と compose でサービス名が違う**ことは既存の `Services__LlmGateway` と同じ罠である）、
  `ServiceToken__ClientId`、`ServiceToken__ClientSecret`（helm は `secretKeyRef`、compose は `${...}` 展開）。

### 6. Keycloak realm

`retrieval-service` / `ingestion-service` の confidential client を新設する。

- `serviceAccountsEnabled: true`・`standardFlowEnabled: false`・`directAccessGrantsEnabled: false`
  （`synthetic-monitor` と同型。browser フローを迂回する `directAccessGrants` は与えない）。
- `defaultClientScopes: ["roles"]`（`abac-attributes` は**与えない**。文書を読む主体ではない）。
- `users[]` へ `service-account-<clientId>` を追加し `realmRoles: ["platform-service"]` **のみ**。
  🔴 **ABAC ポリシーも属性も与えない** —— これらは「サービスが呼んだ」と名乗るためだけの主体である。
- dev の secret は既存の `*-dev-secret-change-me` と同じ置き場の規約に従う（本番は Secret / Vault）。

### 7. `ServiceTokenOptions.ClientSecret` へ Secret 注入の宣言を足すか（観測 5 の判断）

**足す。** 理由:

1. 検査器の突合は**リポジトリ全体で 1 度**であり（`computeViolations` は集合を見る）、宣言を足すことで
   要求されるのは「`ServiceToken__ClientSecret` が helm のどこかに `secretKeyRef` で、compose のどこかに
   `${...}` で現れること」だけである。本 PR は 2 サービスにそれを配線するので、**その場で満たせる**。
2. 満たさないまま宣言だけ足すのは 🔴 **禁止**（検査が赤になる）。逆に、宣言を足さないまま配線すると
   `BffSessionOptions` と同じ穴（#1107）が `ServiceToken` について再発しうる —— 実際、参照実装は
   **すでにその穴の中に居る**（観測 3）。宣言はその穴を機械で塞ぐ唯一の手段である。
3. **BFF を含む他のバインド先を本 PR で配線する義務は生じない**（検査器の母集合はプロパティ単位であり
   サービス単位ではない）。ただし「BFF が `ServiceToken` を構成する日には Secret 由来でなければならない」
   という規範は宣言によって固定される。これは望ましい向きである。

## 受け入れ基準

- [ ] `embedding.proto` が `check-proto-contracts.js` の R1〜R4 を満たし、baseline との差分が
      **非破壊の file 追加 1 件**であること（`--update` の差分を PR に載せる）
- [ ] LlmGateway の gRPC 面が `ServiceCaller` を要求し、資格情報無しは `UNAUTHENTICATED`、
      **管理者の利用者トークンでも `PERMISSION_DENIED`** であること（T-S-01 / T-S-02 / T-S-03）
- [ ] REST と gRPC が**同じ入力に同じ答え**を返すこと（T-S-04）。ハンドラ本体が 1 つであること
- [ ] `EMBED_PURPOSE_UNSPECIFIED` が Index として routing されること（T-S-07）
- [ ] Retrieval / Ingestion の gRPC 実装が REST 実装と**同じ戻り**を返し、
      **輸送の失敗では例外を上げる**こと（T-P1-06 / T-P1-07）
- [ ] `Grpc:Port` 未設定のサービスは 1 バイトも変わらないこと（既存テストが緑のまま）
- [ ] 配備 3 経路（helm / compose / realm）が揃っていること（`check-deploy-manifests.js` /
      `check-secret-injected-options.js` / `check-realm-constraints.js` が緑）
- [ ] **テスト総数が減らないこと**（変異試験で「既定の写しを壊すと赤くなる」ことを実測して示す）

## テスト方針

### 呼び出し先（LlmGateway。`GrpcResolveScopeTests` と同型・**実 Kestrel**）

`TestServer` は in-memory であり h2c ポートの実 bind を観測できないため、
`WebApplicationFactory.UseKestrel()` ＋ 環境変数でポートを与える器を LlmGateway 側にも置く
（`GrpcKestrelFactory` / `GrpcTestConfiguration` と同じ形。`ConfigureAppConfiguration` は
`AddPlatformGrpcListener` の読み取りに間に合わない）。

| ID | 内容 |
| --- | --- |
| T-S-01 | 陽性対照。s2s トークン（`platform-service`）を付けた h2c チャネルで往復し、ベクトルと `embedded=true` を得る |
| T-S-02 | 陰性対照。資格情報無し → `UNAUTHENTICATED` |
| T-S-03 | 🔴 陰性対照。**管理者の利用者トークンを転送しても `PERMISSION_DENIED`**（決定 3〔利用者トークンを載せない〕を機械で守る唯一の点） |
| T-S-04 | REST と gRPC が同じ入力に同じ答え（vector / dimensions / model / collection / embedded / retryable） |
| T-S-07 | 🔴 `EMBED_PURPOSE_UNSPECIFIED` が **Index** として routing される（proto3 の既定 0 を REST の既定へ写す） |
| T-S-10 | 構造の門。gRPC サービス型が `ServiceCaller` を宣言している（リフレクション。T-12 と同型） |
| T-S-11 | 縮退（越境拒否）は `RpcException` ではなく `embedded=false` の応答で返る |

### 呼び出し元

| ID | 内容 |
| --- | --- |
| T-P1-06 | Retrieval: `Embedded=true` / `false` の**両方**で REST 実装と gRPC 実装の戻りが一致。🔴 `UNAVAILABLE` で例外（`[]` に化けない） |
| T-P1-07 | Ingestion: `EmbeddingResult(vector, collection, embedded, retryable)` が一致。既存 `LlmGatewayEmbeddingServiceTests` の 3 ケースを gRPC 実装でも回す。🔴 `UNAVAILABLE` で例外 |

生成クライアント（`LlmEmbeddingClient`）のメソッドは virtual なので偽物を作れる（`GrpcResolveScopeTests` と同じ手）。

### 変異試験（受け入れ基準の最後の 1 行）

`EmbedUseCase` の `UNSPECIFIED → Index` の写しを削って **T-S-07 が赤くなる**ことを実測し、
出力を PR に載せる。写しが無いときに落ちるテストが 1 本も無いなら、その写しは守られていない。

## 計画書との差異

- 差異: なし。`ADR-0029`（east-west 同期は gRPC）・`ADR-0075`（基盤先行）に沿った移行であり、
  REST は並走で残す（`IADR-0379` 決定 5。REST 継続の自認ではない）。
- `ADR-0075` 決定 4 に基づき、本 PR で LlmGateway の proto の**一部**（埋め込み）が公開される。
  AST が追随する対象は `POST /complete` であり本スライスには含まれないため、**AST への通知は
  補完 proto が着地する次の PR で行う**（本リポジトリからは起票しない）。

## 未決事項

- 稼働クラスタでの h2c 往復は**未実測**（新イメージの配備＝Pod 再起動を要する。`IADR-0379` §結果 と同じ制約）。
  実 Kestrel の往復（T-S-01 / T-S-02）で代替する。
- 参照実装（BFF → AuthorizationService）の未配線（観測 3・観測 4）は**本 PR では直さない**。
  認可スライスの PR で `bff` の service account への `platform-service` 付与と
  `Services__AuthorizationServiceGrpc` の配線をまとめて行う。**PR 本文で明示する。**
