---
title: AI 分析 → 検索の RAG 検索を east-west gRPC へ移し、利用者トークンの転送を落とす（#1255 経路 1）
type: spec
status: draft
related_ids: [FR-03, FR-04, FR-05, FR-07, FR-17, NFR-02, NFR-09, NFR-16, UC-01, UC-02, UC-10, SC-01, SC-08, ADR-0004, ADR-0029, ADR-0034, ADR-0035, ADR-0036, ADR-0043, ADR-0075, ADR-0086, ADR-0087, ADR-0088, ADR-0089, IADR-0009, IADR-0012, IADR-0044, IADR-0151, IADR-0242, IADR-0253, IADR-0259, IADR-0272, IADR-0283, IADR-0379, IADR-0397, IADR-0400, IADR-0401, IADR-0402, IADR-0408, IADR-0410, IADR-0411, IADR-0412, IADR-0415, IADR-0416, IADR-0417, IADR-0418, IADR-0419, IADR-0426]
author: Claude（実装）
created: 2026-09-11
updated: 2026-09-11
plan_refs: []
---

# 仕様書: RAG の検索呼び出しを s2s の gRPC へ移す（#1255 経路 1）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-03（ハイブリッド検索）／FR-04（RAG 回答）／FR-05（ABAC）／FR-07（データ範囲）／
  FR-17（グラフ）／NFR-02（性能・観測）／NFR-09（認可）／NFR-16（east-west の統一）
- ユースケース（UC）: UC-01（質問）／UC-02（分析）／UC-10（グラフ二段検索）
- 画面（SC）: SC-01（検索・質問）／SC-08（AI 分析）
- 関連 ADR: `ADR-0029`（east-west は gRPC）／`ADR-0075` 決定 2〜6（移行順・一括移行）／
  `ADR-0034` 決定 1（ホップごと ABAC）／`ADR-0035` 決定 2（二段検索）／
  `ADR-0086` 決定 1（**利用者文脈は本文で運ぶ**。token exchange は採らない）／
  `ADR-0087` 決定 2・3（経路 1 の先行条件と「解けた」の数え方）／
  `ADR-0088`（認可サービスが属性を引き直す）／`ADR-0089` 決定 1（退役をもって解けたと数える）
- 実装 ADR: [[IADR-0379]]（proto の置き場・versioning・h2c・s2s・並走）／[[IADR-0410]]（利用者文脈を
  本文で運ぶ 2 経路）／[[IADR-0415]]（narrowing-only）／[[IADR-0416]]（検索が自分でスコープを解決する）／
  [[IADR-0417]]（検索の gRPC 受け口と属性値照会）／[[IADR-0418]]（REST 面の認証）／
  [[IADR-0426]]（**本 PR で新設**）
- issue: #1255（**閉じない**。残り ④⑤ は扇形）

## 起点の確認（`git rev-parse --is-shallow-repository` = `false`）

基点 `origin/develop` `7d125950`。**履歴は打ち切られていない**ので `git log` を出典に引ける。

## 母集合（着手前に自分で引き直した。転記ではない）

走査は `grep -rn "AddHttpClient" src --include=*.cs`（試験を除く）と `grep -rn "CreateClient("` の
**2 本立て**で行った（登録単位と呼び出し箇所単位は一致しない。#1255 のコメント 1 が同じ理由で
呼び出し箇所単位へ切り替えている）。分類の軸は「同期 ∧ east-west ∧ 応答を待つ」である。

**gRPC の面をまだ 1 つも持たない east-west 同期の経路は 3 つである。**

| # | 経路 | 性質 | 本 PR |
| --- | --- | --- | --- |
| ① | `AiAnalysisService` → `RetrievalService` `POST /search`（`RagOrchestrator.SearchAsync`） | 1 対 1。**利用者トークンを転送している**（方式 A） | 🔴 **移す** |
| ④ | `McpServer` → 各サービス `GET /internal/mcp-tools`（`ToolDeclarationSource` / `HttpToolInvoker`） | **扇形**（宛先集合が `Mcp__Services__*` で開く） | 移さない |
| ⑤ | 各サービス → `GET /internal/introspection`（`HttpEffectiveConfigCollector`） | **扇形**（`Introspection__Services__*`） | 移さない |

これ以外の east-west 同期は**いずれも gRPC の面を持ち、構成で切り替わる並走状態**である
（`AuthzScope` / `UserDirectory` / `LlmEmbedding` / `LlmCompletion` / `DocumentRead` /
`DocumentTagWrite` / `TagDictionary` / `KnowledgeHealthReport` / `GraphNeighbors` /
`AttributeValues` / `NotificationIngress` の 11 面）。
**BFF → 各サービスの利用者資格情報を運ぶ経路**（`Knowledge.Bff.Endpoints` / `Platform.Bff` の
`CreateClient` 群）は `ADR-0086` 決定 1 の対象 2 経路に含まれず、**本 PR の射程外**である。

**除外理由**: 外部 SaaS・IdP・オブジェクトストレージ（`WikiConnector` / `SaaSConnector` /
`WikiJsGraphQlClient` / `Storage*Reader` / `KeycloakIdentityAdminClient` / LLM プロバイダ各種 /
`ClientCredentialsServiceTokenProvider`）は east-west ではない。

## 目的・背景

`ADR-0089` §結果 が **「経路 1（`AiAnalysis → Retrieval`）の移行は着手できる状態になった が、
まだ着手されていない」**と記録した経路である。`ADR-0087` 決定 2 の先行条件（経路 2・3 が
利用者文脈を本文で運ぶ形へ移ること）は [[IADR-0410]] / [[IADR-0416]] で満たされた。

### 🔴 この経路が最後まで残った理由（＝落とし穴の在り処）

`RetrievalService` の二段検索（`GraphExpandingSearchService`）は、近傍展開の資格情報を
**`IHttpContextAccessor` から取っている**。

| 実装 | 読むもの | REST 入口 | 🔴 gRPC 入口だと |
| --- | --- | --- | --- |
| `GraphServiceNeighborExpander`（REST） | `HttpContext.Request.Headers.Authorization` | 利用者の JWT | **呼び出し元サービスの s2s トークン**が入っており、それを GraphService へ転送してしまう |
| `GrpcGraphNeighborExpander`（gRPC） | `HttpContext.User` | 利用者の主体 | **`service-account-aianalysis-service` が ABAC の主体として本文へ載る** |

どちらも例外にならない。**片方は confused deputy、片方は「主体のすり替え」であり、
観測できるのは「グラフ展開が常に空」という静かな故障だけである。**

⇒ **輸送を足すだけでは移せない。** 二段検索の段に、**入口が決めた利用者文脈を型で運ばせる**
必要がある。これが本 PR の主眼であり、proto と輸送はその帰結である。

## 対象範囲

- 対象:
  1. `knowledge.retrieval.v1.DocumentSearch/Search` の proto と受け口（`RetrievalService`）
  2. 二段検索の段へ**利用者文脈を引数で運ぶ**（`IHybridSearchService` / `IGraphNeighborExpander`）
  3. 呼び出し元（`AiAnalysisService`）の輸送分離（REST ／ gRPC）と DI 登録
  4. helm / compose の `Services__RetrievalServiceGrpc`（AI 分析）
  5. `docs/api/east-west-grpc.md` の追記・`scripts/proto-contract-baseline.json` の更新
- 対象外:
  - ④⑤（扇形）。**宛先集合が公開構成で開くので本リポジトリだけでは完結しない**（[[IADR-0419]] 決定 1）
  - REST 端点 `POST /search` の撤去（`IADR-0379` 決定 5 / `ADR-0089` 決定 1。**並走中の正は REST**）
  - BFF → 検索の 2 箇所（`SearchBffEndpoints`）。利用者資格情報を運ぶ側であり別の裁定に属する
  - `POST /authz/attributes/validate` の `ServiceCaller`（`ADR-0089` 決定 2 フォローアップ 1。**別 issue**）

## 設計

### D-1: 面は `knowledge.retrieval.v1.DocumentSearch/Search` の 1 口だけ

`Knowledge.Contracts/Protos/knowledge/retrieval/v1/document_search.proto`。
**`attribute_values.proto` と同じ `package` に別ファイルで置く**（1 service 1 ファイル）。

- 🔴 **利用者文脈を運ぶ。解決済みスコープを受ける口は開かない**（`ADR-0086` 決定 1 / [[IADR-0416]]）。
  `UserContext`（`user_id` / `user_attributes` / `action`）は `attribute_values.proto` と**同じ 3 項目**。
- 🔴 **絞り込みは `narrow_to`（`map<string, NarrowTo>`）で別項目に運ぶ** ——
  呼び出し元が送るのは**利用者が指定したデータ範囲そのもの**であって、交差済みのスコープではない。
- 面に出さないもの: `mode` / `sort_by` / `total_hits` / `elapsed_ms`。
  **呼び出し元（RAG）が 1 つも使っていない**（[[IADR-0401]] 決定 2）。後から足すのは非破壊である。
- 応答は `SearchResult` の並び。`SearchResultDto` の 10 項目を写す。
  🔴 **`has_body` は proto3 の既定（`false`）と DTO の既定（`true`）が逆である**
  （`docs/api/east-west-grpc.md` §proto3 の「未指定」を写す の `sent` と同型）。**両側で明示的に書く。**
  `markdown_uri` は `optional`（null と空文字を区別する）、`updated_at` は
  `google.protobuf.Timestamp`（未設定＝「まだ索引に無い」）。

### D-2: 🔴 利用者文脈は**引数で**段へ運ぶ（`SearchUserContext`）

```text
REST  POST /search   → SearchUserContext.FromRequest(http)   （利用者の JWT・転送可能な資格情報つき）
gRPC  DocumentSearch → SearchUserContext.FromBody(user)      （🔴 転送可能な資格情報は null）
                     ↓（必須引数。既定値を置かない）
IHybridSearchService.SearchAsync(request, user, ct)
                     ↓
IGraphNeighborExpander.ExpandAsync(seeds, hops, user, ct)
```

- **既定値を持たない必須引数にする。** 渡し忘れは**コンパイルで止まる** ——
  周辺の器（`IHttpContextAccessor`）から黙って拾える形を残すと、上表の 2 つの静かな故障が復活する。
- `SearchUserContext.ForwardableCredential`（`string?`）は **REST 入口でだけ非 null** である。
  `GraphServiceNeighborExpander`（REST 実装）はこれを使い、null なら**呼ばずに警告する**
  （現行の「資格情報が無ければ呼ばない」と**同じ値・同じ副作用**）。
  🔴 **`IHttpContextAccessor` は両実装から外す** —— 残すと「入口が決めた文脈」と
  「たまたま在るヘッダ」の 2 つの出所が並び、次に読む人がどちらが正か判断できない。
- `GrpcGraphNeighborExpander` は `SearchUserContext` から `UserContext` を組む。
  **属性の抽出点は `BffScopeResolver.ExtractUserAttributes` のまま**（[[IADR-0411]]）。

### D-3: 受け口は REST と**同じ関数**を通る

`SearchEndpoint.ExecuteAsync(search, req, authoritative, user, ct)` を 1 つ置き、
REST 端点と `SearchGrpcService` の両方がそれを呼ぶ（[[IADR-0412]] 決定 6・[[IADR-0417]] と同じ姿勢）。
判定の位置は動かない —— 受け口が `ISearchAccessResolver` で**自分で**引き、`ScopeNarrowing` で交差させる。

| 事象 | REST | gRPC |
| --- | --- | --- |
| 権限の根拠 | `ResolveAsync(HttpContext)` | `ResolveForUserAsync(user_id, attrs)` |
| 呼び出し元の主張 | 本文 `Scope`（**絞り込みとしてのみ**） | `narrow_to`（同じ関数へ渡る） |
| 主張が無い | `GrantsAccess != true` → 空（[[IADR-0416]] 決定 5。**変えない**） | **絞り込み無し**＝権威をそのまま使う |
| 利用者が分からない | 未認証は 401（[[IADR-0418]]） | `INVALID_ARGUMENT`（[[IADR-0417]] と同型） |
| 認可 | realm の認証済み主体 | `ServiceCaller`（`platform-service`） |

### D-4: 呼び出し元は輸送を分離する（`IRagSearchTransport`）

[[IADR-0400]]（`ILlmCompletionTransport`）と**同じ形**にする。

- `HttpRagSearchTransport`: 現行の `SearchAsync` をそのまま移す（**利用者トークンの転送も含めて 1 バイトも変えない**）。
- `GrpcRagSearchTransport`: `Services:RetrievalServiceGrpc` が在るときだけ登録される。
  **利用者の JWT はメタデータへ載せない。** 載るのは `aianalysis-service` の s2s トークンだけである。
- 🔴 **fail-closed の枝を増やさず・減らさない。** REST の「非 2xx」「不達」は gRPC では
  `RpcException` と s2s トークン取得失敗に畳まれる ——**いずれも空の検索結果**へ倒す
  （現行と同じ値・同じ副作用。回答は「閲覧権限のある文書が見つかりませんでした。」へ落ちる）。
- `RagOrchestrator` は `AccessScope` ではなく `RagSearchContext`（利用者 ID・属性・実効スコープ・
  **利用者が指定した絞り込み**）を段へ渡す。**REST は実効スコープを、gRPC は絞り込みを送る** ——
  受け口が `ScopeNarrowing` で同じ結果へ収束することは §テスト方針 T-11 が固定する。

### D-5: 🔴 「利用者トークンの転送を落とす」の射程

`ADR-0086` 実装側残作業 2 が求めるのは**経路 1 の転送を落とす**ことである。
**落ちるのは gRPC 輸送を選んだときだけである** —— REST 輸送は `POST /search` が
`RequireAuthorization()`（[[IADR-0418]]）を持つため、転送を落とすと 401 になる。
`ADR-0089` 決定 1 のとおり、**REST 実装の退役をもって「解けた」と数える**ので、
本 PR は「解けた」を主張しない（`Services:RetrievalServiceGrpc` を配備へ入れて並走させる段である）。

### D-6: 配備は 4 経路のうち 1 つだけ動く

realm の `aianalysis-service`（confidential client ＋ `platform-service`）と
helm / compose の `ServiceToken__*` は**既に在る**（[[IADR-0400]] / [[IADR-0401]] が入れた）。
足すのは**宛先 1 行**である（`Services__RetrievalServiceGrpc`）。

🔴 **前提: `RetrievalService` 側に `Services__GraphServiceGrpc` が在ること。**
無いと二段検索は REST 実装のまま残り、gRPC 入口では
`ForwardableCredential == null` により**展開を呼ばずに警告**する（静かに空にはならないが、
グラフ再ランクは効かない）。helm・compose のどちらにも既に在ることを実測した。

## 受け入れ基準

- [ ] AC-1 `RagOrchestrator` の検索が `Services:RetrievalServiceGrpc` の有無で輸送を選び、**無ければ REST のまま**である
- [ ] AC-2 gRPC 面は `ServiceCaller` を要求し、**利用者トークン（管理者を含む）では `PERMISSION_DENIED`** になる（陰性対照）。s2s トークンでは通る（陽性対照）
- [ ] AC-3 受け口は**本文の利用者文脈で自分でスコープを解決する**（記録で観測する）。呼び出し元が解決したスコープを受ける口が**面に存在しない**
- [ ] AC-4 `narrow_to` は**権限を広げない**（陰性対照）／**実際に絞る**（陽性対照）
- [ ] AC-5 二段検索の近傍展開が、**gRPC 入口では s2s の主体ではなく本文の利用者**を GraphService へ渡す
- [ ] AC-6 gRPC 入口で REST 実装の近傍展開が選ばれているとき、**s2s トークンを転送しない**（呼ばずに警告する）
- [ ] AC-7 REST と gRPC が**同じ結果**を返す（陽性対照つき —— 両方が空で一致したのではないこと）
- [ ] AC-8 輸送の失敗（`RpcException` 全 status・トークン取得失敗）は**空の検索結果**へ縮退し、例外を伝播しない
- [ ] AC-9 `node scripts/check-proto-contracts.js` が成功する（baseline 更新済み）
- [ ] AC-10 `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が両ユニットで通る（0 警告・Failed=0）
- [ ] AC-11 helm / compose の両方に `Services__RetrievalServiceGrpc` が在る

## テスト方針

**器は既存の `RetrievalService.Tests/Grpc/GrpcKestrelFactory`（実 Kestrel の h2c）を再利用する。**
新しい器を作らない —— 器がプロセスで 1 つでなければならない理由は `GrpcServerCollection` に在る。

| # | 位置 | 何を固定するか | 対照 |
| --- | --- | --- | --- |
| T-01 | 受け口 | s2s トークンつき h2c 往復で結果が返る | ★ 陽性 |
| T-02 | 受け口 | 資格情報なし → `UNAUTHENTICATED` | 陰性 |
| T-03 | 受け口 | **管理者の利用者トークン** → `PERMISSION_DENIED` | 陰性 |
| T-04 | 受け口 | `user_id` 空 → `INVALID_ARGUMENT` | 陰性 |
| T-05 | 契約 | 要求の項目が `query` / `top_k` / `user` / `narrow_to` **だけ**（`scope` が無い） | 構造 |
| T-06 | 受け口 | `ResolveForUserAsync` が**本文の文脈**で呼ばれた記録 | 観測 |
| T-07 | 受け口 | `narrow_to` は権限を広げない | 陰性 |
| T-08 | 受け口 | `narrow_to` は実際に絞る | ★ 陽性 |
| T-09 | 受け口 | REST と gRPC が同じ結果（片方だけ空ではない） | ★ 陽性 |
| T-10 | 受け口 | gRPC サービス型が `ServiceCaller` を宣言 | 構造 |
| T-11 | 段 | gRPC 入口の近傍展開が**本文の利用者**を運ぶ（s2s の主体ではない） | 🔴 中核 |
| T-12 | 段 | gRPC 入口 ＋ REST 実装の近傍展開 → **呼ばずに警告**（s2s を転送しない） | 🔴 中核 |
| T-13 | 呼び出し元 | 構成が在るとき gRPC 輸送が選ばれる／無ければ REST | 分岐 |
| T-14 | 呼び出し元 | `RpcException` → 空の検索結果（回答は中立文言） | 陰性 |
| T-15 | 呼び出し元 | メタデータに**利用者の JWT が載らない** | 🔴 中核 |

**変異試験（最低 1 本ずつ）**: ①`[Authorize]` を外す ②本文の文脈を無視して `ResolveAsync(HttpContext)` を
呼ぶ ③`narrow_to` を読み捨てる ④`ForwardableCredential` の代わりに `IHttpContextAccessor` を読む
⑤`has_body` を写さない。**すべて赤になること**を確かめる。

## 計画書との差異

- 差異: なし。`ADR-0086` 決定 1（本文で運ぶ）・`ADR-0087` 決定 2（先行条件）・`ADR-0089` 決定 1
  （退役をもって数える）にそのまま従う。**新しい例外 ADR は起こさない**（`ADR-0075` 決定 3・5）。

## 未決事項

- **稼働 k3s での h2c 往復は本 PR でも測らない**（Pod 再構築を要する）。`docs/api/east-west-grpc.md`
  §未決事項の記載を変えない。
- ④⑤（扇形）の扱い。**本 issue は閉じない。**
- `ADR-0089` 決定 2 フォローアップ 1（`/authz/attributes/validate` の `ServiceCaller`）は
  **本 PR の射程外**であり、拾い手が居ない。PR 本文と #1255 のコメントで名指しする。
