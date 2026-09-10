---
title: IADR-0425 east-west gRPC 第 11 面: RAG の検索を s2s へ移し、利用者文脈を器から拾わず引数で段まで運ぶ
type: impl-adr
status: Accepted
related_ids: [FR-03, FR-04, FR-05, FR-07, FR-17, NFR-02, NFR-09, NFR-16, UC-01, UC-02, UC-10, SC-01, SC-08, ADR-0004, ADR-0029, ADR-0034, ADR-0035, ADR-0036, ADR-0043, ADR-0075, ADR-0086, ADR-0087, ADR-0088, ADR-0089, IADR-0009, IADR-0012, IADR-0044, IADR-0149, IADR-0151, IADR-0242, IADR-0253, IADR-0259, IADR-0263, IADR-0272, IADR-0283, IADR-0358, IADR-0379, IADR-0397, IADR-0400, IADR-0401, IADR-0402, IADR-0408, IADR-0410, IADR-0411, IADR-0412, IADR-0415, IADR-0416, IADR-0417, IADR-0418, IADR-0419]
author: claude
created: 2026-09-11
updated: 2026-09-11
---

# IADR-0425: 利用者を「その場に在るもの」から拾うのをやめる

## 状況

`ADR-0089` §結果 が **「経路 1（`AiAnalysis → Retrieval`）の移行は着手できる状態になったが、
まだ着手されていない」**と記録した経路である。`ADR-0087` 決定 2 の先行条件
（経路 2・3 が利用者文脈を本文で運ぶ形へ移ること）は [[IADR-0410]] / [[IADR-0416]] で満たされた。

基点 `origin/develop` `7d125950`（`git rev-parse --is-shallow-repository` = `false`）。
着手時に `AddHttpClient` と `CreateClient` の 2 軸で数え直したところ、
**gRPC の面をまだ 1 つも持たない east-west 同期の経路は 3 つ**であった。

| # | 経路 | 性質 | 本 PR |
| --- | --- | --- | --- |
| ① | `AiAnalysisService` → `RetrievalService` `POST /search` | 1 対 1。**利用者トークンを転送している**（方式 A） | 🔴 **移す** |
| ④ | `McpServer` → 各サービス `GET /internal/mcp-tools` | **扇形**（宛先集合が `Mcp__Services__*` で開く） | 移さない |
| ⑤ | 各サービス → `GET /internal/introspection` | **扇形**（`Introspection__Services__*`） | 移さない |

### 🔴 この経路が最後まで残った理由は「輸送」ではない

`RetrievalService` の二段検索（`GraphExpandingSearchService`）は、近傍展開の主体を
**`IHttpContextAccessor` から拾っていた**。REST の入口ではそれで正しい。
**east-west gRPC の入口を足した瞬間に、同じコードが別の意味になる。**

| 実装 | 器から拾っていたもの | REST 入口 | 🔴 gRPC 入口では |
| --- | --- | --- | --- |
| `GraphServiceNeighborExpander`（REST） | `HttpContext.Request.Headers.Authorization` | 利用者の JWT | **呼び出し元サービスの s2s トークン**が入っており、それを GraphService へ転送する |
| `GrpcGraphNeighborExpander`（gRPC） | `HttpContext.User` | 利用者の主体 | **`service-account-aianalysis-service` が ABAC の主体として本文に載る** |

🔴 **どちらも例外にならない。** 前者は confused deputy、後者は主体のすり替えであり、
運用から見えるのは「**グラフ展開が常に空**」という静かな故障だけである
（[[IADR-0263]] 残件 2 が同じ形で 1 度実測されている —— 段を有効にしても展開が 0 件だった）。

**⇒ 面を足す前に、利用者を「その場に在るもの」から拾う構造を先に外す必要があった。**
これが本 PR の主眼であり、proto と輸送はその帰結である。

## 決定

### 決定 1: 面は `knowledge.retrieval.v1.DocumentSearch/Search` の 1 口だけ

`Knowledge.Contracts/Protos/knowledge/retrieval/v1/document_search.proto`。
`attribute_values.proto` と**同じ package の別ファイル**であり、`UserContext` / `NarrowTo` は
**import して共有する**（写しを作らない。同じ意味の型が 2 つあると片方だけが直る）。

- 🔴 **利用者文脈を運ぶ。解決済みスコープを受ける口は開かない**（`ADR-0086` 決定 1 / [[IADR-0416]]）。
- 🔴 **絞り込みは `narrow_to` で別項目に運ぶ。** 呼び出し元が送るのは
  **利用者が指定したデータ範囲そのもの**（交差前）であり、交差は呼び出し先が
  `ScopeNarrowing` で行う —— 交差済みの `AccessScope` を送る形にしない。
  **送ると分岐を平坦化して運ぶことになり、[[IADR-0253]] 決定 2 が退けた「キー単位 union」で
  分岐の混成を許す。**
- **面に出さないもの**: `mode` / `sort_by` / `total_hits` / `elapsed_ms`
  （呼び出し元が 1 つも使っていない。[[IADR-0401]] 決定 2）。後から足すのは非破壊である。

### 決定 2: 🔴 **利用者文脈は器から拾わず、既定値の無い必須引数で段まで運ぶ**

```text
REST  POST /search   → SearchUserContext.FromRequest(http)   （利用者の JWT つき）
gRPC  DocumentSearch → SearchUserContext.FromBody(user)      （🔴 転送可能な資格情報は null）
                     ↓（必須引数。既定値を置かない）
IHybridSearchService.SearchAsync(request, user, ct)
                     ↓
IGraphNeighborExpander.ExpandAsync(seeds, hops, user, ct)
```

- **`IHttpContextAccessor` を両実装から外す。** 残すと「入口が決めた文脈」と
  「たまたま器に在るもの」の 2 つの出所が並び、**次に読む人がどちらが正か判断できない。**
- **既定値を置かない。** 渡し忘れは**コンパイルで止まる** ——
  上表の 2 つの静かな故障は、拾える場所が在るかぎり必ず復活する。
- `SearchUserContext.ForwardableCredential`（`string?`）は **REST 入口でだけ非 null** である。
  REST 実装の近傍展開はこれを転送し、null なら**呼ばずに警告する**（現行と同じ値・同じ副作用）。
  🔴 **手元の s2s トークンで代用しない** —— 代用すると GraphService は
  「そのサービスが読めるもの」を返し、利用者の権限で絞られていない近傍が再ランクへ混ざる。
- **試験の都合でこの性質を緩めない。** 段を持たない試験のための既定値は
  試験側（`TestSearchUser.Any`）に置く。**ポート側へ既定値を置くと本番の渡し忘れも通る。**

### 決定 3: 受け口は REST と**同じ関数**（`SearchEndpoint.ExecuteAsync`）を通る

判定の位置は動かない —— 受け口が `ISearchAccessResolver` で自分で引き、`ScopeNarrowing` で交差させる。

| 事象 | REST | gRPC |
| --- | --- | --- |
| 権限の根拠 | `ResolveAsync(HttpContext)` | `ResolveForUserAsync(user_id, attrs)` |
| 呼び出し元の主張 | 本文 `Scope`（**絞り込みとしてのみ**） | `narrow_to`（同じ関数へ渡る） |
| 主張が無い | `GrantsAccess != true` → 空（[[IADR-0416]] 決定 5。**変えない**） | **絞り込み無し**＝権威をそのまま使う |
| 利用者が分からない | 未認証は 401（[[IADR-0418]]） | `INVALID_ARGUMENT`（[[IADR-0417]] と同型） |
| 認可 | realm の認証済み主体 | `ServiceCaller`（`platform-service`） |

🔴 **「主張が要る」という REST 面の短絡を gRPC 面へ写さない。** REST でそれが残っているのは
[[IADR-0012]] の経緯（`Scope` を送らない呼び出し元が今日より広く見えてはならない）によるもので、
**gRPC 面には `Scope` という項目が存在しない** —— 絞り込みの不在は「絞らない」であって
「解決していない」ではない。

### 決定 4: 呼び出し元は輸送を分離し、縮退の枝を増やさず・減らさない

[[IADR-0400]]（`ILlmCompletionTransport`）と同じ形で `IRagSearchTransport` を置く。

- `HttpRagSearchTransport`: 現行の実装をそのまま移す（**利用者トークンの転送も含めて 1 バイトも変えない**）。
- `GrpcRagSearchTransport`: `Services:RetrievalServiceGrpc` が在るときだけ登録される。
- 🔴 REST の「非 2xx」「不達」と gRPC の `RpcException`（全 status）・s2s トークン取得失敗は
  **同じ枝**（空の検索結果）へ落とす。**ただし必ず警告を出す** ——
  配線漏れがまさにこの枝に出るからである。
- 🔴 **チャネルはキー付きで登録する。** 本サービスは認可サービス宛を**キー無し**で
  （`AuthzScopeGrpcClient.AddChannel` の `TryAddSingleton`）、LlmGateway 宛をキー付きで持つ ——
  3 本目をキー無しで足すと、**検索が認可サービスへ繋がる**。

### 決定 5: 🔴 「利用者トークンの転送を落とす」の射程は gRPC 輸送だけである

`ADR-0086` 実装側残作業 2 が求めるのは経路 1 の転送を落とすことだが、
**REST 輸送では落とせない** —— `POST /search` は `RequireAuthorization()` を持つ（[[IADR-0418]]）ので、
落とすと 401 になる。`ADR-0089` 決定 1 のとおり **REST 実装の退役をもって「解けた」と数える**ため、
**本 PR は「経路 1 が解けた」を主張しない**（`Services:RetrievalServiceGrpc` を配備へ入れて並走させる段である）。

## 理由

- **決定 2 を「規律」ではなく「型」にしたのは、規律が 1 度破れた実績があるからである**
  （[[IADR-0263]] 残件 2）。**拾える場所が在るかぎり、次に段を足す人はまた拾う。**
- **決定 1 で交差前の値を送るのは、[[IADR-0415]] の narrowing-only を輸送で崩さないためである。**
  交差済みの値を送っても結果は同じに見えるが、**分岐を平坦化した瞬間に意味が変わる。**
- **決定 4 で枝を増やさないのは、移行の不変条件が「挙動を変えない」だからである**
  （[[IADR-0400]] 決定 5 と同じ）。

## 結果

### 得られたもの

- 経路 1 に gRPC の面ができ、**利用者の JWT が east-west を通らなくなった**（gRPC 輸送を選んだとき）。
- 二段検索の段が**入口を選ばなくなった** —— REST でも gRPC でも同じ主体で動く。

### 残るもの

- 🔴 **④⑤（扇形）は移らない。** 宛先集合が公開構成で開くため本リポジトリだけでは完結しない
  （[[IADR-0419]] 決定 1 と同じ判断）。**#1255 は閉じない。**
- 🔴 **並走中の正は REST である**（[[IADR-0379]] 決定 5）。反転する IADR はまだ起こしていない。
- 🔴 **稼働 k3s での h2c 往復は本 PR でも測っていない**（Pod 再構築を要する）。
- 🔴 **BFF → 各サービスの利用者資格情報を運ぶ経路**は `ADR-0086` 決定 3 の対象 2 経路に含まれず、
  扱いは未定のままである。
- 🔴 **`ADR-0089` 決定 2 フォローアップ 1（`POST /authz/attributes/validate` の `ServiceCaller`）は
  本 PR の射程外であり、拾い手が居ない。** #1255 のコメントで名指しした。

### フォローアップ

1. **実装**: ④⑤ の扇形をどう畳むかを決める（宛先集合が構成で開く面の設計）。
2. **実装 → 計画**: REST 実装を退役させたら環流する。**その環流が `NFR-09` 恒久条項の
   経路ごとの達成記録を起こす**（`ADR-0084` 決定 3 / `ADR-0089` 決定 1）。
3. **実装**: `ADR-0089` 決定 2 フォローアップ 1 を別 issue で拾う。

## 関連

- Supersedes: なし
- Superseded by: なし
- 作業仕様書: `.ai-context/specs/20260911_issue-1255_aianalysis-to-retrieval-search-grpc.md`
- 通信仕様書: `docs/api/east-west-grpc.md` §11 つ目の面
