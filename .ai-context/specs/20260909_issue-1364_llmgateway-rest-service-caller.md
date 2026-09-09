---
title: 作業仕様書 — LlmGateway の REST 3 口（/complete・/complete/stream・/embed）へ ServiceCaller の門を掛ける
type: spec
status: done
related_ids: [NFR-09, FR-02, FR-04, FR-11, ADR-0084, ADR-0004, ADR-0010, ADR-0016, ADR-0029, ADR-0075, IADR-0379, IADR-0397, IADR-0400, IADR-0413, IADR-0418, IADR-0424]
author: endazon (with Claude Code)
created: 2026-09-09
updated: 2026-09-10
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0084_nfr09-judgment-unit-and-interim-clause-release.md
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
---

# 作業仕様書 — LlmGateway の REST 3 口へ `ServiceCaller` の門を掛ける（#1364）

> 起点: issue #1364「LlmGateway の REST 3 口が無認可のまま並走している」。
> 計画側の根拠は `ADR-0084` 決定 1（**NFR-09 は端点単位で判定する**）と同 決定 4
> （`IngestionService` / `LlmGateway` も 3 点を示せなければ**未達**）である。

## 起点となる計画書（トレーサビリティ）

- 非機能要件: `NFR-09`（全 API で OIDC/JWT 認証）
- 機能要求: `FR-02` / `FR-03`（埋め込み）・`FR-04` / `FR-11`（テキスト生成と越境ルーティング）
- 関連 ADR: `ADR-0084`（判定単位＝端点）／`ADR-0004`（Keycloak OIDC・ポリシー）／
  `ADR-0029`・`ADR-0075`（east-west の輸送と移行順）／`ADR-0010`・`ADR-0016`（ゲートウェイの役割）
- 関連 IADR: `IADR-0379` 決定 4（`ServiceCaller` の定義・利用者トークンを通さない）／
  `IADR-0397`・`IADR-0400`（gRPC 面が既に `[Authorize(Policy = ServiceCaller)]` を持つ）／
  `IADR-0413`（**認可サービスの REST 面へ同じ門を掛けた先例**。呼び出し側は `ServiceTokenHandler`）／
  `IADR-0418`（REST 面へ門を掛けたときの**試験の作法**＝既定クライアントを有資格にし陰性対照を別に置く）

## 実測（2026-09-09・着手時）

### 欠陥の再確認

```
$ grep -rn "RequireAuthorization" src/platform/backend/Services/LlmGateway --include=*.cs
（0 件）
$ grep -rn "FallbackPolicy" src/platform/backend/Shared --include=*.cs
（0 件。ヒットするのは LlmGateway の LlmFallbackPolicy＝無関係）
```

3 口（`Features/Completions/Complete/Endpoint.cs:16`・`Features/Completions/CompleteStream/Endpoint.cs:28`・
`Features/Embeddings/Embed/EmbeddingEndpoints.cs:19`）はいずれも門を持たない。
`Program.cs:52` の `AddPlatformAuth` は**器を用意するだけ**であり（`ADR-0084` 実測 1）、
`Program.cs:155` が「REST（/complete・/complete/stream）は並走したまま残る」と自認している。

### 母集合の引き直し（`.claude/rules/traceability.md` 規則 1〜8 ／ `traceability.repo.md` 規則 9・10）

**issue 本文の 3 ファイルは「直す対象」であって母集合ではない。** 門を掛けると壊れ得るのは
**呼び出し側**であるから、母集合は「**この 3 パスを叩く側**」から引く。

**軸 1 — 3 パスの文字列を追跡下の全ファイルから引く**（拡張子で絞らない・行フィルタを継がない）:

```
$ git ls-files | grep -v "^src/ai-stock-trading/" \
    | xargs grep -l -E '"/complete"|/complete/stream|"/embed"'
→ **61 ファイル**（`.ai-context/` 23 ＝ adr 9 ＋ specs 14、`docs/` 6、`src/` 32）
```

**軸 2 — 宛先の構成キーと HTTP クライアント登録から引く**（軸 1 は文字列を持たない登録点を落とす）:

```
$ git ls-files | grep -v "^src/ai-stock-trading/" \
    | xargs grep -n -E 'Services:LlmGateway"|Services__LlmGateway|AddHttpClient.*LlmGateway'
→ 登録点 5・構成 2 ファイル（compose / helm）・試験の構成 8 箇所
```

**軸 3 — AST（別リポジトリ・submodule）を明示的に引く**（軸 1・2 で除外したため別に引く）:

```
$ grep -rn '"/complete"' src/ai-stock-trading --include=*.cs | grep -v /obj/
src/ai-stock-trading/.../ReportService/Infrastructure/ExternalServices/HttpReportNarrativeDrafter.cs:78
src/ai-stock-trading/.../TradeDecisionService/Infrastructure/ExternalServices/HttpLlmCompletionClient.cs:80
$ grep -rn "LlmGateway__BaseUrl" src/ai-stock-trading/deploy
values-local.yaml:149,191 → http://llmgateway-service.microservices-platform:8080
values.yaml:355,410 → ""（既定は空＝呼ばない）
```

#### 非テストの呼び出し元（＝門を掛けると壊れ得る全体）

| # | 呼び出し元 | 叩く口 | s2s の資格情報 | realm ロール |
| --- | --- | --- | --- | --- |
| 1 | `AiAnalysisService/…/HttpLlmCompletionTransport.cs` | `/complete`・`/complete/stream` | compose・helm に有り | `platform-service` 有り |
| 2 | `ConversionService/…/LlmGatewayDiagramCoder.cs` | `/complete` | 同上 | 同上 |
| 3 | `GraphService/…/LlmGatewaySuggestionClient.cs` | `/complete` | 同上 | 同上 |
| 4 | `IngestionService/…/LlmGatewayEmbeddingService.cs` | `/embed` | 同上 | 同上 |
| 5 | `RetrievalService/…/LlmGatewayEmbeddingService.cs` | `/embed` | 同上 | 同上 |
| 6 | **AST** `ReportService/…/HttpReportNarrativeDrafter.cs` | `/complete` | **無し**（同ファイルが「匿名エンドポイントゆえ s2s トークンは付けない」と明記） | **realm に client 自体が無い** |
| 7 | **AST** `TradeDecisionService/…/HttpLlmCompletionClient.cs` | `/complete` | **無し** | **無し** |

- **BFF は呼び出し元ではない。** 両ユニットの BFF で `LlmGateway|llm-gateway` は **4 件**引けるが
  **4 件ともコメント**であり（`Platform.Bff.Tests` 2・`AnalysisBffEndpoints.cs:176`・
  `DocumentReadGrpcClient.cs:94`）、`AddHttpClient` の登録も呼び出しも無い。
  **したがって BFF の門は前段に無い**（issue 本文と一致）。
- **1〜5 の資格情報は既に配線済みである**（`ServiceToken__ClientId/ClientSecret` が compose 5 箇所・
  helm 5 箇所、realm の service account 5 つに `platform-service`）。**新設は要らない。**

#### 母集合から除外したものと理由

| 除外 | 理由 |
| --- | --- |
| `docs/` 6 ファイル・`.ai-context/` 23 ファイル（軸 1） | パス名を**説明として**引いているだけで、呼び出し・門のいずれも持たない。ただし **`docs/api/east-west-grpc.md` は REST 面の認可を述べているか**を個別に確認する（後述の追随判断） |
| `Shared.Contracts/Dtos/CompletionDto.cs`・`completion.proto` | 契約の宣言であり呼び出し元ではない |
| `LlmGateway` 自身の `Domain/Ports/ILlmProvider.cs`・`ClaudeProvider.cs` の言及 | **上流プロバイダ**（`/v1/embeddings` 等）へのコメントであり本件の 3 口ではない |
| `scripts/check-integration-config-timing.js:269` | 検査器が**例として埋め込んでいる文字列**（実行時の呼び出しではない） |
| AST（`src/ai-stock-trading`） | **本作業では変更しない**（他エージェントが編集中・親からの明示指示）。**ただし壊れるので報告する**（下記「破壊的影響」） |

## 決めること（実装ADR: `IADR-0424`）

1. **門は `PlatformAuthPolicies.ServiceCaller` を使う。** 新しい認可軸・ポリシー名・ミドルウェアを作らない
   （issue 受け入れ基準・`IADR-0379` 決定 4）。
2. **端点ごとに付ける**（`ADR-0084` 決定 1 は端点単位。サービス既定＝`FallbackPolicy` は使わない ——
   同 決定 1 補完節が「`FallbackPolicy` は門ではない」と明記している）。
3. **呼び出し側 5 サービスは既存の `ServiceTokenHandler` を噛ませる**（`IADR-0413` と同型）。
   噛ませ方の宣言は **1 か所**（`Foundation/Llm/LlmGatewayHttpClientExtensions`）に閉じる。
4. **AST は緩めない。** 門を掛けたまま、AST 側の追随を別途要する事実として報告する。

## 実装範囲

| # | 変更 | ファイル |
| --- | --- | --- |
| 1 | `/complete` へ `RequireAuthorization(ServiceCaller)` | `Features/Completions/Complete/Endpoint.cs` |
| 2 | `/complete/stream` へ同上 | `Features/Completions/CompleteStream/Endpoint.cs` |
| 3 | `/embed` へ同上 | `Features/Embeddings/Embed/EmbeddingEndpoints.cs` |
| 4 | 自認コメントの是正 | `Services/LlmGateway/Program.cs` |
| 5 | 呼び出し側の資格情報付与（宣言 1 か所） | `Shared/…/Foundation/Llm/LlmGatewayHttpClientExtensions.cs`（新規） |
| 6 | 5 サービスの登録を 5 へ差し替え | `AiAnalysis`/`Conversion`/`Graph`/`Ingestion`/`Retrieval` の `Program.cs` |
| 7 | 試験の器（既定クライアントを有資格に・陰性対照用の口を足す） | `LlmGateway/tests/TestWebApplicationFactory.cs`・`tests/TestServiceTokens.cs`（新規） |
| 8 | 陰性／陽性対照の試験（3 口 × 3 ケース） | `LlmGateway/tests/Features/…/RestAuthorizationTests.cs`（新規） |
| 9 | 既存の Kestrel 経由 REST 呼び出しへ資格情報を足す | `tests/Features/Completions/Grpc*Tests.cs`・`tests/Features/Embeddings/Embed/GrpcEmbedTests.cs` |
| 10 | 呼び出し側の試験でトークン発行器を差し替える | 影響が出た試験のみ（`IServiceTokenProvider` を `FixedServiceTokenProvider` へ） |

## 受け入れ基準 → 試験の写像

| # | 受け入れ基準（issue #1364） | 試験 |
| --- | --- | --- |
| A1 | 3 口すべてが端点単位で認可を要求する | T-A-01/04/07（陽性対照。有資格で 200） |
| A2 | 認可の主体は既存の `ServiceCaller` | T-A-03/06/09（**利用者トークン（platform-admin）でも 403**） |
| A3 | **陰性対照** —— 資格情報なしは 401 | T-A-02/05/08（3 口それぞれ） |
| A4 | `Program.cs` の自認コメントを直す | 目視（差分） |
| A5 | 既存の呼び出し元が壊れない | 既存 `dotnet test` 全緑（両ユニット）＋ 上表の実測 |

## 破壊的影響（緩めずに報告する）

**AST の 2 呼び出し元（`ReportService` / `TradeDecisionService`）は壊れる。**
`LlmGateway__BaseUrl` を設定した配備（AST の `values-local.yaml`）でのみ発火し、既定（空文字）では呼ばない。
壊れ方は**安全側へ倒れる**が、**無音ではない**:

- `ReportService`: 401 → 非 2xx → **プレースホルダ散文**（数値には関与しない）
- `TradeDecisionService`: 401 → `LlmFailureClassification.Classify(401)` は **`ModelUnavailable`** →
  **全判断が Hold** ＋ **「割当モデルが使えない」という誤った運用シグナル**が積み上がる

AST 側の追随（realm へ confidential client を足し、`ServiceTokenHandler` 相当を噛ませる。
AST には `TestSupport.PlatformShim` に同名の部品が既にある）は**別 issue**である。

## 非目標

- `FallbackPolicy` の導入（`ADR-0084` 決定 1 補完節が「門ではない」と定めている）
- 既定アドレスの不揃い（`GraphService` だけ `5010`・他は `5007`）の是正 —— 別件。**本作業で触らない**
- gRPC 面の変更（既に `ServiceCaller` を持つ）


## 結果（2026-09-10）

### 実施した変更

| # | 変更 | 実体 |
| --- | --- | --- |
| 1〜3 | 3 口へ `RequireAuthorization(PlatformAuthPolicies.ServiceCaller)` | 端点ごと（群には掛けない） |
| 4 | 自認コメントの是正 | `Program.cs` ＋ **gRPC 面 2 ファイルのコメントも**（規則 10 の引き直しで出た） |
| 5 | 資格情報の付け方（1 か所） | `Shared/…/Foundation/Llm/LlmGatewayHttpClient.cs`（`ServiceTokenHandler` を再利用） |
| 6 | 呼び出し側 5 サービス | `AddLlmGatewayServiceToken(builder.Configuration)` を 1 行ずつ |
| 7 | 試験の器 | `TestServiceTokens.cs`（新規。gRPC の器と発行器を共有）＋ `TestWebApplicationFactory` |
| 8 | 陰性／陽性対照 | `tests/Features/Authorization/RestServiceCallerGateTests.cs`（13 ケース） |
| 9 | Kestrel 経由の REST 呼び出し 5 箇所 | `GrpcKestrelFactory.CreateRestClient()` へ集約 |
| 10 | 文書の追随 | `docs/api/east-west-grpc.md` 2 箇所・`docs/api/openapi.yaml`（401 / 403 と主体の明記） |

**10（呼び出し側の試験）は 1 件も要らなかった** —— 5 サービスの単体試験はいずれも
`IEmbeddingService` / `IDiagramCoder` / `ILlmCompletionTransport` を差し替えており、
HTTP クライアントの経路そのものを通していないためである（両ユニット全緑で確認）。

### 規則 10 の引き直しで出た追随先（是正前の語では捕まらない）

`git ls-files | xargs grep -n "匿名エンドポイント|認可を掛けていない|無認可|並走したまま残る|サービス間呼び出し専用"`

| 追随した | 追随しなかった（理由） |
| --- | --- |
| `LlmGateway/Program.cs:155` | `.ai-context/adr/IADR-0397`・`IADR-0403`・`.ai-context/specs/20260905_*`: **凍結記録**。その時点の実測であり本文を書き換えない（後継は `IADR-0424`） |
| `Features/Completions/GrpcService.cs:16`（「REST は認可を掛けていない」「この面は REST より強い」） | `deploy/docker-compose.yml:624`「サービス間呼び出し専用」: **依然として真**（host 非公開の話であり門の話ではない） |
| `Features/Embeddings/Embed/GrpcService.cs:16`（同上） | 他サービスの「無認可」記述（`DocumentService` / `AuthorizationService` の別端点）: **別件** |
| `docs/api/east-west-grpc.md:144`・`:178` | — |

### 検証（実測）

- `dotnet build platform/backend/backend.slnx` / `knowledge/backend/backend.slnx`: **成功（0 エラー）**
- `dotnet test platform/backend/backend.slnx`: **全緑**（LlmGateway 301 / Bff 543 / Authz 242 / McpServer 175 /
  Notification 102 / Shared.Infrastructure 340 / Shared.Kernel 42）
- `dotnet test knowledge/backend/backend.slnx`: **全緑**（Graph 540 / Document 471 / DataSource 255 /
  Retrieval 249 / Conversion 162 / AiAnalysis 138 / Wiki 102 / Dashboard 92 / Ingestion 82 /
  Feedback 38 / IntegrationTests 55）
- **変異試験**: 3 口の `RequireAuthorization` を外すと、13 ケース中**陰性対照 6 件が落ちる**
  （陽性 7 件は緑のまま＝陽性だけの試験では門を守れないことの実証）。

### 環境について（本作業とは無関係の先行事象）

着手時、この作業ツリーは `dotnet build` が **32 エラー**で落ちていた（`CS0579` 属性の重複）。
`git stash` で変更を退避しても同じ 32 エラーが出るため**先行事象**である。原因は旧レイアウトの
残骸ディレクトリ 22 個（`Services/*/src/*.Api`・`tests/*.Api.Tests`・`*.Worker` 等。**中身は bin/obj だけ**で
追跡下に 1 ファイルも無い）が、親 csproj の既定 glob に入って生成済み `AssemblyInfo.cs` を
二重にコンパイルしていたことである。**追跡下のファイルは 1 つも動かしていない**（退避しただけ）。
