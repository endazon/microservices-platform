---
title: 利用者の権限で動く east-west 2 経路（Retrieval → Graph・Graph → Document）を利用者文脈の本文搬送へ移し、east-west gRPC へ載せる
type: spec
status: in-progress
related_ids:
  - FR-04
  - FR-05
  - FR-17
  - FR-18
  - NFR-09
  - NFR-16
  - UC-10
  - SC-03
  - SC-05
  - SC-18
  - ADR-0004
  - ADR-0029
  - ADR-0034
  - ADR-0035
  - ADR-0036
  - ADR-0049
  - ADR-0063
  - ADR-0065
  - ADR-0075
  - ADR-0080
  - ADR-0086
  - IADR-0044
  - IADR-0242
  - IADR-0272
  - IADR-0364
  - IADR-0379
  - IADR-0395
  - IADR-0397
  - IADR-0400
  - IADR-0401
  - IADR-0402
  - IADR-0408
  - IADR-0410
author: claude
created: 2026-09-07
updated: 2026-09-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md 決定 1・3・5
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md 決定 1・2
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md §決定・2026-08-04 追記
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 3・5・6
  - planning:projects/microservices-platform/07_adr/ADR-0063_tag-suggestion-reflection-and-approval-authz.md 決定 1〜3
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md §利用者属性・§ポリシー評価モデル
---

# 作業仕様書: 利用者文脈を本文で運ぶ 2 経路（#1255 残作業 1 / ADR-0086 決定 1・3）

## 起点

- issue: #1255（east-west 同期の gRPC 展開）の**残作業 1**
- 計画の裁定: `ADR-0086`（2026-09-07 Accepted）
  - **決定 1**: 利用者の権限で動く east-west は利用者文脈（`user_id` / `user_attributes` / `action`）を**本文で運ぶ**。
    利用者トークンは面に通さない。各ホップは受け取った文脈で**自分の判定を行う**（`ADR-0034` 決定 1 と両立する）。
  - **決定 2**: token exchange（RFC 8693）は今は採らない。🔴 **理由は preview だからではなく、入れても
    閉じないからである** —— `ResolveScopeRequest` は呼び出し元の主張をそのまま評価する。
  - **決定 3**: 対象は **2 経路**（`Retrieval → Graph`・`Graph → Document`）。
    `AiAnalysis → Retrieval` は**中継**であり本裁定の対象外（経路 3 が移れば不要になる）。
  - **決定 5**: 例外 ADR は起こさず `ADR-0075` の一括移行の射程内で移す。
- 設計の正本: [[IADR-0379]] 決定 1〜4（proto の置き場・versioning・h2c ポート・s2s トークン）。**変えない。**
- 直前 5 スライスの先例: [[IADR-0397]]／[[IADR-0400]]／[[IADR-0401]]／[[IADR-0402]]／[[IADR-0408]]。**同じ形を写す。**

## 1. 母集合（自分で引いた。issue 本文・ADR の数字を転記していない）

基点 `origin/develop` = `7fe9570a`。`git rev-parse --is-shallow-repository` = **`false`**
（`git log` を出典に引く前の確認。planning#410）。

### 1.1 引き方

利用者の `Authorization` ヘッダを east-west の後段へ転送している箇所を、**転送の実装**で引いた
（`grep -rn "Headers.Authorization" --include=*.cs src/*/backend/Services/*/Infrastructure`）。

| # | 転送している箇所 | 行 | ADR-0086 の分類 | 本 PR |
| --- | --- | --- | --- | --- |
| 1 | `AiAnalysisService/.../RagOrchestrator.cs` → Retrieval `/search` | :228 付近 | **中継**（決定 3） | **触らない**（残作業 2） |
| 2 | `RetrievalService/.../GraphServiceNeighborExpander.cs` → Graph | :41-52 | 本裁定の対象 | **移す** |
| 3 | `GraphService/.../HttpDocumentTagWriter.cs` → Document | :34-36 | 本裁定の対象 | **移す** |

**除外理由（全数）**

- **#1（`RagOrchestrator`）**: `ADR-0086` 決定 3 が中継と裁定し、**移行後に落とす**ものと定めた（フォローアップ 1）。
  本 PR で落とすと、経路 2 が gRPC で走らない構成（`Services:GraphServiceGrpc` 未設定＝並走中の既定）では
  REST 経路が資格情報を失って**全ホップ 404 になる**。**落とすのは経路 2 が gRPC へ切り替わってからである。**
- **`/search` が本文の `scope` をそのまま信じる構造**（#1318 欠陥 B / planning#564）: 🔴 **別の裁定待ちである。触らない。**
  `ADR-0086` 決定 4 も「本 ADR では改めない」と明記し、フォローアップ 3 で計画側の別 issue とした。
- **BFF の資格情報搬送 23 箇所・introspection の扇形**: [[IADR-0402]] が「移せない」と判定済み。射程外。
- **AST の 4 本**: `ADR-0075` 決定 4 により AST#584 が受け皿。

### 1.2 呼び出し先が転送トークンを**何に使っているか**（1 箇所ずつ実測した）

🔴 `ADR-0086` は経路 1 についてだけこの確認をしている。**2 経路それぞれについて自分で引いた。**

| 呼び出し先の口 | 門（`RequireAuthorization`） | 主体（`User`）を読むか | 何に使うか |
| --- | --- | --- | --- |
| Graph `GET /graph/{id}/neighbors` | **在る**（`Neighbors/Endpoint.cs:114`） | **読む** | `GraphAccessResolver.ResolveAsync(http, read)` が `ctx.User.Identity.Name`（`:67`）と claim `clearance` / `department`（`:102-105`）を取り、`AuthzScope/Resolve` へ**本文で**渡す |
| Graph `GET /graph/edge-types/catalog` | **在る**（`EdgeTypes/Catalog/Endpoint.cs:22`） | 🔴 **読まない** | ハンドラの引数は `GraphDbContext` と `CancellationToken` だけ（`:20`）。**認証の門にしか使っていない** |
| Document `POST /documents/{id}/tags` | **在る**（`DocumentEndpoints.cs:68` の `tagReflection` group） | **読む** | `http.User.Identity?.Name`（`AddTag/Endpoint.cs:58`）で所有者束縛、`http.User.IsInRole(platform-admin)`（`:60`）で管理者経路 |

**帰結**

- 経路 2 の 2 口のうち**辞書（catalog）は利用者文脈を要しない** —— `ServiceCaller` の門が現行の
  「認証済みであること」の門を**より狭く**置き換える（[[IADR-0401]] 決定 1 / [[IADR-0402]] 決定 3 と同じ向き）。
- 経路 3 の口は**主体と realm ロールの両方**を読む。ロールは ABAC 属性ではない
  （`07_abac-attribute-model` §利用者属性 の 2026-09-05 追記が「`roles` は現時点で ABAC の文書単位判定には
  用いていない。ロール判定は ABAC とは別の経路である」と明記し、`UserAttributeEncoding.SetValuedKeys` も
  `tags` / `projects` の 2 つだけで `roles` を含まない）。**したがって `user_attributes` へ混ぜない。**

### 1.3 `user_attributes` に実際に載る属性（`ADR-0086` §残るもの／フォローアップ 2）

引き方: `grep -rn 'FindFirst("clearance")' --include=*.cs src/`（非テスト・非 bin）。

| 抽出箇所 | 載せる属性 | 数 |
| --- | --- | --- |
| `Platform.Shared.Infrastructure/Foundation/Authz/BffScopeResolver.cs:67-69` | clearance / department | 2 |
| `GraphService/.../GraphAccessResolver.cs:102-105` | clearance / department | 2 |
| `WikiService/.../WikiAccessResolver.cs:76-78` | clearance / department | 2 |
| `AiAnalysisService/Features/Analysis/AnalysisEndpoints.cs:53-55` | clearance / department | 2 |

**4 箇所すべてが 2 属性である。プラットフォーム全体で 2 であり、GraphService が絞っているのではない。**

計画（`07_abac-attribute-model` §利用者属性）が定める利用者属性は **5 つ** ——
`roles` / `department` / `clearance` / `projects` / `tags`。**運ばれていないのは `projects` / `tags` の 2 つ**
（`roles` は ABAC 判定に用いない旨が同書に明記されており、欠落ではない）。

🔴 **`AbacEvaluator.MatchesUserConditions`（`AbacEvaluator.cs:72-78`）はキーを固定していない** ——
`userAttrs.TryGetValue(key, …)` が失敗した条件は**マッチしない**。すなわち **`tags` / `projects` を
利用者条件に持つポリシーは、配備されても 1 度もマッチしない**。

★［2026-09-07 追記 / #1323］**解消した。** realm の多値マッパー・抽出点の集約・評価器の交差判定を
1 本の PR で着地させた（[[IADR-0411]]）。**本仕様書の当時の記述は当時の事実として残す。**倒れる向きは deny（fail-closed）なので
情報は漏れないが、**SC-17 で割り当てたタグは文書単位の判定に一切効かない**。
**本 PR ではこれを直さない**（本 PR の射程は経路の形であり、属性の搬送範囲を広げるのは
`ADR-0086` フォローアップ 2 の報告事項である）。**PR 本文で報告する。**

### 1.4 是正の母集合（規則 9・10）

- 規則 9（誤りの側の文字列で走査）: `Authorization` ヘッダ転送を「方式 A」と記す注記を
  `grep -rn "方式 A" --include=*.cs src/` で引いた。**#2・#3 の注記は本 PR で書き換える。
  `RagOrchestrator` 側は射程外なので触らない**（経路 1 は中継のまま残る）。
- 規則 10（この変更で新たに誤りになる自分の記述）: `docs/api/east-west-grpc.md` §4 の
  「**将来** 呼び出し先が利用者自身の権限で動く必要が出たら RFC 8693 token exchange へ進む。今は採らない」は、
  `ADR-0086` 決定 2 により**「今は採らない」の理由が変わった**（preview だからではなく、入れても閉じないから）。
  🔴 **同節を書き換え、7 つ目の面として本スライスを追記する。**

## 2. 決めること（[[IADR-0410]] へ落とす）

1. 利用者文脈の運び方（`user_id` / `user_attributes` / `action` ＋ **realm ロールは別欄**）
2. 面の粒度（近傍展開と辺の型の重みを 2 rpc に割り、利用者文脈を持つのは前者だけ）
3. 判定の位置を動かさないことの表明（本体を REST と共有し、gRPC 面は写しだけを持つ）
4. 縮退の枝の対応（現行の非 2xx / 不達と同じ値・同じ副作用）

## 3. やること

### 3.1 契約（proto）

- `Knowledge.Contracts/Protos/knowledge/graph/v1/graph_neighbors.proto`
  - `service GraphNeighbors`
    - `rpc ExpandNeighbors(ExpandNeighborsRequest) returns (ExpandNeighborsResponse)` —— 利用者文脈を持つ
    - `rpc ListEdgeTypeWeights(ListEdgeTypeWeightsRequest) returns (ListEdgeTypeWeightsResponse)` —— 持たない
- `Knowledge.Contracts/Protos/knowledge/document/v1/document_tag_write.proto`
  - `service DocumentTagWrite` / `rpc AddTag(AddTagRequest) returns (AddTagResponse)`
  - 🔴 **`document_read.proto` へ足さない** —— 同 proto は「書き込みの口はこの面に存在しない」と宣言している。

### 3.2 呼び出し先（面を開く側）

- GraphService: `AddPlatformGrpcListener()` ＋ `MapGrpcService<GraphNeighborsGrpcService>()`、
  `Grpc.AspNetCore` の PackageReference、helm `graph.grpcPort: 8081`、compose `expose: 8081` ＋ `Grpc__Port`
- DocumentService: `MapGrpcService<DocumentTagWriteGrpcService>()`（リスナ・helm・compose は [[IADR-0402]] で在る）
- 両方とも `[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]`

### 3.3 本体の共有（判定器を 2 つにしない）

- `GraphService/Features/Graph/Neighbors/ExpandNeighborsUseCase.cs` —— REST と gRPC が**同じ関数**を通る
- `DocumentService/Features/Documents/AddTag/AddDocumentTagUseCase.cs` —— 同上
- `IGraphAccessResolver` に `ResolveForUserAsync(GraphUserContext, action, ct)` を足し、
  既存の `ResolveAsync(HttpContext, …)` はそれへ委譲する。
  🔴 **`ResolveAsync` の多重定義を作らない** —— `GraphTypeGateArchitectureTests:89-90` が
  `GetMethod(nameof(ResolveAsync))` で引いており、多重定義は `AmbiguousMatchException` になる。

### 3.4 呼び出し元（切替）

- Retrieval: `Services:GraphServiceGrpc` が在れば `GrpcGraphNeighborExpander`、無ければ現行の REST 実装
- Graph: `Services:DocumentServiceGrpc` が在れば `GrpcDocumentTagWriter`、無ければ現行の REST 実装
- **並走中の正は REST。** 戻すのは構成を外すだけ。

## 4. 受け入れ基準（Given-When-Then）

- [ ] Given 2 経路 / When gRPC で呼ぶ / Then **利用者の JWT がメタデータに載らず**、s2s トークンだけが載る
- [ ] Given 呼び出し先 / When 利用者（管理者を含む）のトークンで呼ぶ / Then `PERMISSION_DENIED`
      （陽性対照として s2s トークンでは通る）
- [ ] Given 呼び出し先 / When 本文の利用者文脈を変える / Then **呼び出し先自身の ABAC 判定が結果を変える**
      （＝判定の位置が動いていない）
- [ ] Given `RpcException` / トークン取得失敗 / When 起きる / Then **現行の非 2xx 枝と同じ値・同じ副作用**
      （近傍展開は空 ＋ 警告、タグ反映は `Unavailable`）
- [ ] Given helm と compose / When 読む / Then `graph.grpcPort` と `Grpc__Port` が両方に在る
- [ ] Given realm / When 読む / Then `retrieval-service` / `graph-service` の confidential client と
      **`users[]` の service account ＋ `platform-service`** が在る
- [ ] Given `node scripts/check-proto-contracts.js` / When 実行する / Then 成功する（baseline 更新済み）
- [ ] Given `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` 両ユニット / Then 0 警告・Failed=0
- [ ] Given 試験件数 / When 前後を数える / Then **1 本も減っていない**

## 5. 射程外（触ったら差し戻し）

- `RagOrchestrator` の Retrieval へのトークン転送の除去（残作業 2）
- `/search` が本文の `scope` を信じる構造（別の裁定待ち）
- BFF の資格情報搬送 14 本・扇形 2 経路
- `deploy/mail-relay/**`・`scripts/check-stack-ready.js`・`.github/workflows/**`
