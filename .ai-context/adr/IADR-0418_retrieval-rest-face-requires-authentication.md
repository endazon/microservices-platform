---
title: IADR-0418 RetrievalService の REST 受け口は認証を要し、権限の無い主体には空を返す
type: impl-adr
status: Accepted
related_ids: [FR-03, FR-04, FR-05, NFR-09, UC-01, UC-02, SC-01, SC-08, ADR-0004, ADR-0029, ADR-0032, ADR-0034, ADR-0043, ADR-0075, ADR-0084, ADR-0086, ADR-0088, IADR-0009, IADR-0012, IADR-0044, IADR-0151, IADR-0160, IADR-0335, IADR-0379, IADR-0403, IADR-0410, IADR-0413, IADR-0415, IADR-0416, IADR-0417]
author: claude
created: 2026-09-09
updated: 2026-09-09
---

# IADR-0418: #1318 欠陥 B の閉鎖

## 状況

`RetrievalService` の REST 受け口 `POST /search` / `POST /search/attribute-values` は
**`RequireAuthorization` を持たない**（#1318 欠陥 B「認可の不在」）。

```
Features/Search/SearchEndpoints.cs:16
    var g = app.MapGroup("/search").WithTags("Search");   ← 門が無い
```

🔴 **器は既に在る。** `Program.cs` は `UsePlatformMiddleware()`（`UseAuthentication` ＋
`UseAuthorization`）を積んでおり、**掛かっていないのは門だけ**である。

### 直前の 2 つの IADR がどちらもここで止まった

- [[IADR-0416]] 決定 5: 「`/search` は `RequireAuthorization` を持たない。**本 IADR で認可は
  掛けない** —— 掛けると SPA / McpServer への影響を測る必要がある」
- [[IADR-0417]] §帰結: 「REST の 2 端点は無認可のままである。**本 ADR はそれを変えない**」

🔴 **どちらも「測っていない」ことを理由に留保した。本 IADR は測った。**

### 実測 —— 非テストの呼び出し元は 3 つで、**3 つとも利用者トークンを転送している**

母集合は 2 軸で引いた（路の文字列 `"/search` と、名前つきクライアント `RetrievalService`）。
**2 軸が同じ 3 件へ収束した。**

| # | 呼び出し元 | 路 | 利用者トークン |
| --- | --- | --- | --- |
| 1 | `SearchBffEndpoints.cs:74`（`/bff/search`） | `POST /search` | **転送する**（`:68-70`。群 `:36` は `RequireAuthorization()` 済み） |
| 2 | `SearchBffEndpoints.cs:209`（`/bff/attribute-values`） | `POST /search/attribute-values` | **転送する**（`:200-202`） |
| 3 | `RagOrchestrator.cs:232`（AiAnalysis） | `POST /search` | **転送する**（`:227-229`。匿名は検索前に短絡） |

**SPA は BFF 経由でしか届かない**（`foundation/api` 以外の `fetch` を ESLint が禁じている）。
**McpServer は `/search` を呼ばない** —— `HttpToolInvoker` が叩くのは申告 endpoint
`/internal/mcp/search_documents` だが、**RetrievalService はその路を Map していない**
（`McpTools/Declare/Endpoint.cs` が Map するのは `/internal/mcp-tools` だけ）。
`deploy/` にも直接呼び出しは無い。

🔴 **留保の理由だった 2 つの懸念（SPA / McpServer）は、どちらも実測で消えた。**

## 決定

### 決定 1: 🔴 `/search` 群に **`RequireAuthorization()`（ポリシー無し）** を掛ける

`MapGroup("/search")` へ 1 つだけ足す。**群の外は触らない** ——
`/health/*`・`/internal/mcp-tools`・introspection・OpenAPI はそのままである。

### 決定 2: 🔴 **ポリシーは付けない。呼び出し元 3 つの契約は不変である**

`ServiceCaller` を掛けると**呼び出し元 3 つが全滅する** —— 運んでいるのは
**利用者トークンであってサービス資格情報ではない**。realm の任意の認証済み主体を通し、
**見えるものは ABAC が決める**（[[IADR-0044]] の多層防御。門は 1 枚目であって最後ではない）。

ロール要求も足さない —— 計画 `05_screens` は SC-01 / SC-08 を「ABAC の権限内で全利用者が
利用できる」と定めており、**書かれていない制限を足さない**（`SearchBffEndpoints` が
`/bff/search` へ同じ判断を書いている）。

🔴 **呼び出し元のコードは 1 行も変わらない。** 3 つとも既にトークンを載せているためである。

### 決定 3: 🔴 未認証は **401**。**200 ＋ 空は認証済み・無権限にだけ残る**

| 主体 | 本 IADR の前 | 本 IADR の後 |
| --- | --- | --- |
| 未認証 | 200 ＋ 空（[[IADR-0416]] 決定 5） | **401** |
| 認証済み・権限なし | 200 ＋ 空 | **200 ＋ 空（不変）** |
| 認証済み・権限あり | 200 ＋ 結果 | **200 ＋ 結果（不変）** |

🔴 **存在秘匿は壊れない。** 秘匿すべきは「**その文書が在るかどうか**」であり、
それが読めるのは「権限の有無で応答が変わる」ときだけである。**認証の有無で応答が変わっても
文書の存在は漏れない** —— 未認証者はどの文書についても等しく 401 を得る。
権限の有無を区別させない契約（[[IADR-0009]] / [[IADR-0151]] 決定 5）は**門の内側にそのまま残る**。

**これは `/bff/*` と同じ形である**（[[IADR-0160]]）—— エッジで 401、内側で存在秘匿。

### 決定 4: 🔴 gRPC 面（`ServiceCaller`）との非対称は**並走期間だけ**続く

REST は利用者トークンで通り、gRPC は s2s だけを通す（[[IADR-0417]] 決定 4）。
**この差は輸送の差ではなく、運ぶ文脈の差である** —— REST は利用者の `Authorization` を運び、
gRPC は本文で利用者文脈を運んで s2s で認証する（`ADR-0086` / `ADR-0087`）。
**並走が終われば REST の口ごと消える**ので、非対称を解消するための追加の統制は置かない。

🔴 **REST 面へ `ServiceCaller` を掛けてはならない。** 掛けると呼び出し元 3 つが全滅し、
かつ**転送された利用者トークンで s2s の面が開く**形（confused deputy）へ寄る。

### 決定 5: 🔴 試験の器へテスト認証を足す。**既存 25 箇所は書き換えない**

REST の呼び出しは試験に **25 箇所**あり、器は **3 種類**である。

| 器 | 箇所 | 足したもの |
| --- | --- | --- |
| `RetrievalService.Tests.TestWebApplicationFactory` | 23 | ヘッダ `X-Test-User` で切り替わる認証ハンドラ ＋ `ConfigureClient` が既定でヘッダを載せる ＋ `CreateAnonymousClient()` |
| `RetrievalService.Tests.Grpc.GrpcKestrelFactory` | 1 | **本物の JwtBearer** を通すので、REST の 1 本に発行済みトークンを載せた |
| `Knowledge.IntegrationTests` の `RetrievalHost` | 1 | 常に認証済みのハンドラ（未認証の契約はここでは測らない） |

🔴 **「常に認証済み」の器を既定にしなかった**（`RetrievalService.Tests` 側）——
そうすると**未認証の契約（401）が測れず、門を外す変異が生き残る**。
器を作った理由そのものが失われる形である。

## 結果

- **未認証で `/search` へ到達できない。** #1318 欠陥 B が閉じた。
- **契約は 1 バイトも変わらない**（呼び出し元 3 つ・DTO・状態コードのいずれも）。
- **`NFR-09` の暫定条項がこの経路について解除された**（`ADR-0084`: 端点単位で判定し、
  解除は経路ごとに行う）。`AuthorizationService` の `/authz/scope`（[[IADR-0413]]）に続く 2 本目である。

### 実測した変異（ビルドし直して実走。戻したことは緑で確認した）

| # | 変異 | 赤になった試験 |
| --- | --- | --- |
| M-1 | `SearchEndpoints.cs` の `.RequireAuthorization()` を外す | **3** —— 未認証 401 の 2 本 ＋ 構造の門 1 本 |

```
（変異あり）Failed: 3, Passed: 4, Total: 7
  Unauthenticated_search_is_rejected_and_leaks_no_document
  Unauthenticated_attribute_values_is_rejected_and_leaks_no_value
  The_search_group_carries_authorization_metadata
（戻した）  Failed: 0, Passed: 7, Total: 7
```

🔴 **陰性対照（認証済み・無権限 → 200 ＋ 空）と陽性対照（認証済み・許可 → 結果）は
M-1 で赤にならない。それが正しい。** あの 2 対が測るのは
「**門を掛けても存在秘匿と正常経路が変わらないこと**」であり、門の有無ではない。
**門の有無を測るのは未認証の 2 本と構造の門だけ**である ——
[[IADR-0416]] の M-3 / M-4 が「試験は在るのに経路を通っていない」で 1 度ずつ生存した反省から、
**どの試験がどの変異を殺すのかを先に書き出してから撃った。**

### 残るもの

- **REST 面が `ServiceCaller` を持たない**（決定 2・4）。並走が終わるまで意図的にそうする。
- **`docs/api/east-west-grpc.md` の 9 つ目の面の記述**を追随させた（「REST の受け口は認可を
  持たない」→ 認証を要する）。**live な権威文書は日付つきで是正し、凍結記録（[[IADR-0416]] /
  [[IADR-0417]]）は本文を書き換えず追記だけ足した。**
- **[[IADR-0403]] の姿勢表（行 10）は更新していない** —— 2026-09-06 時点の評価の凍結記録であり、
  かつ同表の判定（「ロール門は要さない」）は本 IADR の後も変わらない。
