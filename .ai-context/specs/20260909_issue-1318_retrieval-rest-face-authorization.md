---
title: RetrievalService の REST 受け口（/search 群）に認証を要求し、#1318 欠陥 B を閉じる
type: spec
status: done
related_ids: [FR-03, FR-04, FR-05, NFR-09, UC-01, UC-02, SC-01, SC-08, ADR-0004, ADR-0029, ADR-0032, ADR-0034, ADR-0043, ADR-0075, ADR-0084, ADR-0086, ADR-0088, IADR-0009, IADR-0012, IADR-0044, IADR-0151, IADR-0160, IADR-0335, IADR-0379, IADR-0403, IADR-0410, IADR-0413, IADR-0415, IADR-0416, IADR-0417, IADR-0418]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: REST の受け口に認証を要求する（#1318 欠陥 B の残り）

## 起点

- FR-03（横断検索）／FR-04（RAG・属性値照会）／FR-05（ABAC）／NFR-09（全 API で OIDC/JWT 認証）／
  UC-01・UC-02／SC-01・SC-08
- 計画 ADR: `ADR-0084`（**NFR-09 は端点単位で判定し、暫定条項の解除は経路ごとに行う**）／
  `ADR-0004`（認証・ABAC）／`ADR-0032`（エッジは BFF セッション）／`ADR-0034` 決定 1（ホップごと判定）／
  `ADR-0088`（認可サービスの REST 面に `ServiceCaller`。[[IADR-0413]] で着地済み）
- 実装 ADR: [[IADR-0044]]（多層防御・最終防衛線）／[[IADR-0160]]（BFF 全端点に認証）／
  [[IADR-0416]] 決定 5（**本 IADR で認可は掛けない**と留保した点）／[[IADR-0417]] 決定 4（gRPC 面は
  `ServiceCaller`。非対称は並走期間だけ）／[[IADR-0418]]（本 PR で新設）
- issue: #1318（欠陥 B「認可の不在」）

## 🔴 現状（実測。作業ツリー `c7a1faa`。**`git log` / `git blame` は使っていない** ——
`git rev-parse --is-shallow-repository` が `true` を返すため出典に使えない）

```
src/knowledge/backend/Services/RetrievalService/Features/Search/SearchEndpoints.cs:16
    var g = app.MapGroup("/search").WithTags("Search");   ← RequireAuthorization が無い
```

`Program.cs:208` は `UsePlatformMiddleware()`（`UseAuthentication` ＋ `UseAuthorization`）を積んでいるので
**認証の器はある**。**掛かっていないのは門だけ**である。

[[IADR-0416]] が「呼び出し元の主張を信じない」形へ変えたので**悪用可能性は既に塞がっている**が、
**認可そのものは掛かっていない** —— 同 決定 5 が「SPA / McpServer への影響を測る必要がある」として
明示的に留保した残件である。本 PR はその測定を済ませて門を掛ける。

## 母集合の引き方（`.claude/rules/traceability.repo.md` 規則 2・9・10）

**記憶で挙げていない。誤りの側の文字列（`/search` そのもの）で全追跡ファイルを 2 軸で走査した。**

### 軸 1: 路の文字列

```
$ git grep -n '"/search'            # 文字列リテラルとしての /search
$ git grep -n '/search' -- '*.cs'   # コメント・散文も含めた広い網
```

### 軸 2: 名前つき HTTP クライアント（路を変数で組む呼び出しを取りこぼさないため）

```
$ git grep -in 'Retrieval' -- '*.cs' | grep -iE 'AddHttpClient|CreateClient\('
```

### 結果 —— 非テストの呼び出し元は **3 つだけ**（2 軸が一致した）

| # | 呼び出し元 | 路 | 利用者トークン | 呼び出し元自身の門 |
| --- | --- | --- | --- | --- |
| 1 | `Knowledge.Bff.Endpoints/SearchBffEndpoints.cs:74` | `POST /search` | **転送する**（`:68-70`） | 群 `:36` が `RequireAuthorization()` |
| 2 | `Knowledge.Bff.Endpoints/SearchBffEndpoints.cs:209` | `POST /search/attribute-values` | **転送する**（`:200-202`。#1343 で入った） | 同上 |
| 3 | `AiAnalysisService/…/RagOrchestrator.cs:232` | `POST /search` | **転送する**（`:227-229`） | 匿名は検索前に短絡（`AnalysisEndpoints.cs:49` / `RagOrchestrator.cs:129-137`） |

🔴 **3 つとも利用者トークンを運んでいる。** よって**ポリシー無しの `RequireAuthorization()`** なら
**呼び出し元は 1 行も変わらない**。

### 除外したもの（同じ文字列に当たるが対象外。理由つき）

| 除外 | 理由 |
| --- | --- |
| `Knowledge.Bff.Endpoints/WikiBffEndpoints.cs:84` の `MapGet("/search")` | 群が `/bff/wiki` であり、宛先は WikiService。RetrievalService の路ではない |
| `WikiService/Features/Wiki/SearchPages/Endpoint.cs:29` | WikiService 自身の `/wiki/search`。別サービス・別 ABAC |
| `knowledge/frontend/…/SearchChatPage.tsx`・`platform/frontend/…/renderUnitRoute.tsx` | SPA の**画面ルート** `/search`。BFF 経由でしか後段へ届かない |
| `RetrievalService/Features/McpTools/Declare/McpToolContracts.cs:67` | 申告する endpoint は `/internal/mcp/search_documents`。**RetrievalService はこの路を Map していない**（`McpTools/Declare/Endpoint.cs` が Map するのは `/internal/mcp-tools` だけ）。McpServer の `HttpToolInvoker` は今日も 404 を得る —— **本 PR で壊れるものが無い** |
| `AiAnalysisService/Tests/…/Rag*Tests.cs`（3 箇所） | 偽ハンドラ内の `path == "/search"` 比較。実ホストを起こしていない |
| `deploy/` 配下 | `git grep -n '/search' -- deploy` は 0 件（直接呼び出しなし） |

### 壊れるのは試験だけ —— **実測 25 箇所**（ブリーフの申告 23 と差がある。自分で引き直した）

| ファイル | 箇所 | 器 |
| --- | --- | --- |
| `RetrievalService/Tests/Features/Search/HybridSearchEndpointTests.cs` | 13 | `TestWebApplicationFactory` |
| `RetrievalService/Tests/Features/Search/ScopeIsNotWidenedByCallerTests.cs` | 6 | 同上 |
| `RetrievalService/Tests/Features/Search/Hybrid/GraphExpansionTwoStageSearchTests.cs` | **3** | `GraphExpansionFactory`（同基底） |
| `RetrievalService/Tests/HealthEndpointTests.cs` | 1 | `TestWebApplicationFactory` |
| 🔴 `RetrievalService/Tests/Features/Search/GrpcAttributeValuesTests.cs:210` | **1** | `GrpcKestrelFactory`（**実 Kestrel ＋ 本物の JwtBearer**） |
| `Knowledge.IntegrationTests/Search/IngestToSearchInProcessTests.cs:203` | 1 | `RetrievalHost`（独立した器） |
| **計** | **25** | 器は **3 種類** |

🔴 **申告との差 2 件は、どちらも取りこぼすと赤くなる箇所だった** ——
`GraphExpansionTwoStageSearchTests` は 3 箇所（274 / 293 / 314）で、
`GrpcAttributeValuesTests.cs:210` は**申告に無かった** REST 呼び出しである（gRPC の試験ファイルの中に
REST の呼び出しが 1 本だけ埋まっている）。**器が 3 種類ある**ことも、この引き直しで初めて出た。

### 規則 10（是正で新たに誤りになる自分の記述）を引き直した

`grep -rn "認可を持たない\|無認可"` で全文書を走査した結果:

| 記述 | 扱い |
| --- | --- |
| `docs/api/east-west-grpc.md:447`（「REST の受け口は**認可を持たない**（#1318 欠陥 B）」） | 🔴 **live な権威文書。日付つきで是正した**（本 PR） |
| [[IADR-0416]] 決定 5・§残るもの | **凍結記録。本文は書き換えず**、`［2026-09-09 追記 / #1318］` を 1 行足した |
| [[IADR-0417]] 決定 4・§帰結 | 同上（同じ非対称を宣言しているため 1 行ずつ足した） |
| [[IADR-0403]] 表 行 10（RetrievalService「認可端点 0」） | **凍結記録かつ 2026-09-06 時点の姿勢評価**である。本 PR はロール門を足さないので同表の判定（「ロール門は要さない」）自体は変わらない。**追記しない**（記録の凍結。planning#387） |
| `docs/tests/NFR-09_bff-edge-authentication.md:31`（「対象外: 後段サービスの認可」） | **対象外の宣言であり、後段が認可を持つようになっても偽にならない**。変更なし |
| `docs/api/east-west-grpc.md:144`・`:178`（LlmGateway の REST は無認可） | **別サービス**。本 PR の射程外 |
| `scripts/check-bff-authz-docs.js` | 走査対象は BFF だけ（`openapi.yaml` の `/bff/` 配下）。RetrievalService を見ていない |

## 決定（詳細は [[IADR-0418]]）

1. `SearchEndpoints.MapSearchEndpoints` の群へ **`RequireAuthorization()`（ポリシー無し）** を掛ける。
   **群の外（`/health/*`・`/internal/mcp-tools`・introspection・OpenAPI）は触らない。**
2. `ServiceCaller` は掛けない —— 呼び出し元 3 つは**利用者トークンしか運んでいない**ので全滅する。
3. 未認証は **401**。[[IADR-0416]] 決定 5 の「空応答へ倒す」契約は**認証済みだが権限の無い主体**にだけ残る。
4. 試験の器（3 種類）へテスト認証を足す。**既存 25 箇所は 1 行も書き換えない**
   （器の `ConfigureClient` が既定でヘッダを載せる。`GrpcKestrelFactory` 経由の 1 本だけは
   本物の JwtBearer なので Bearer を明示する）。

## 受け入れ基準 → 試験の写像

| # | 基準 | 試験 |
| --- | --- | --- |
| a | 未認証の `POST /search` / `POST /search/attribute-values` は **401** で、本文に文書を含まない | `SearchEndpointsAuthorizationTests`（`Unauthenticated_*`）2 本 |
| b | 認証済みで解決器が deny なら **200 ＋ 空**（存在秘匿は保たれる。陰性対照） | 同（`AuthenticatedButDenied_*`）2 本 |
| c | 認証済み・許可なら従来どおり結果が返る（陽性対照） | 同（`AuthenticatedAndAllowed_*`）2 本 |
| d | 構造の門: `/search` 群の終端が認可メタデータ（`IAuthorizeData`）を持つ | 同（`The_search_group_carries_authorization_metadata`）1 本 |
| e | 既存 25 箇所が認証済みで従来どおり通る | 既存試験（無変更） |

🔴 **d が要る理由**: a は「401 になること」しか測らない。器を差し替えた別の理由（例えば
テスト認証の既定が変わる）で通ってしまう余地を、**配線そのものを読む門**で塞ぐ。
陰性対照として **`/health/live` が認可メタデータを持たないこと**を同じ試験で主張する
（全端点が認可を持つ実装なら d は無意味に緑になる）。

## 実測した変異（すべてビルドし直して実走。戻したことは緑で確認した）

| # | 変異 | 赤になった試験 |
| --- | --- | --- |
| M-1 | `SearchEndpoints.cs` の `.RequireAuthorization()` を外す | **3**（a×2 ＋ d）——下記のとおり実走で確認 |

```
$ dotnet test src/knowledge/backend/Services/RetrievalService/Tests/RetrievalService.Tests.csproj \
    --filter "FullyQualifiedName~SearchEndpointsAuthorizationTests"

（変異あり）Failed: 3, Passed: 4, Skipped: 0, Total: 7
  Failed … Unauthenticated_attribute_values_is_rejected_and_leaks_no_value
  Failed … The_search_group_carries_authorization_metadata
  Failed … Unauthenticated_search_is_rejected_and_leaks_no_document
（戻した）  Failed: 0, Passed: 7, Skipped: 0, Total: 7
```

🔴 **b・c は M-1 で赤にならない**（未認証でも通れば b・c は認証済みの経路しか触らない）。
**それが正しい** —— b・c が測るのは「認可を掛けても存在秘匿と陽性の経路が変わらないこと」であり、
門の有無ではない。**門の有無を測るのは a と d だけ**である。

## 検証（実走した結果）

| コマンド | 結果 |
| --- | --- |
| `dotnet build src/knowledge/backend/backend.slnx` | **Build succeeded. 0 Warning(s) / 0 Error(s)** |
| `dotnet test src/knowledge/backend/backend.slnx --filter "Category!=Integration"` | 12 アセンブリすべて **Failed: 0**（Passed 2222 / Skipped 9）。`RetrievalService.Tests` 241・`Knowledge.IntegrationTests` 45 を含む |
| `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` | 差分なし |
| `dotnet format src/platform/backend/backend.slnx --verify-no-changes` | 差分なし |
| `node scripts/check-adr-numbering.js` | OK（重複・欠番なし・索引と双方向一致） |
| `check-trace-blocks` / `check-doc-links` / `check-doc-updated --base origin/develop` | いずれも OK |
| `check-test-traceability` / `check-test-spec-coverage` / `check-cross-repo-refs` / `check-plan-id-qualification` | いずれも OK |
| `check-backend-libraries` / `check-unit-dependencies` / `check-doc-type-vocabulary` / `gen-knowledge-graph --check` | いずれも OK |

🔴 **`dotnet build src/platform/backend/backend.slnx` は本作業環境では通らない** ——
submodule `src/ai-stock-trading` が未 populate であり、`Platform.Bff/Composition/BffEndpointComposition.cs:1`
の `using AiStockTrading.Bff.Endpoints;` が `CS0246` になる。**本 PR は platform のファイルを 1 つも
触っていない**ので、この失敗は変更に起因しない（`check-doc-links` も同 submodule を「未 populate ゆえ
検査対象外」と報告している）。`dotnet format` は通る。

## やらないこと

- **`ServiceCaller` を掛けない**（呼び出し元が利用者トークンしか運ばないため。上の決定 2）。
- **ロール要求を足さない** —— 計画 `05_screens` は SC-01 / SC-08 を「ABAC の権限内で全利用者が
  利用できる」と定めており、書かれていない制限を足さない（`SearchBffEndpoints.cs:32-33` と同じ理由）。
- **gRPC 面（[[IADR-0417]]）は触らない。** `ServiceCaller` のままである。
- **`/health/*`・`/internal/mcp-tools`・introspection を触らない**（群の外）。
