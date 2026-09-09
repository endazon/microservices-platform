---
title: IADR-0417 RetrievalService に east-west gRPC の受け口を開き、権限内属性値の照会を利用者文脈だけで運ぶ
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-05, NFR-09, NFR-16, UC-01, SC-01, SC-08, ADR-0004, ADR-0029, ADR-0034, ADR-0043, ADR-0075, ADR-0086, ADR-0087, ADR-0088, IADR-0044, IADR-0141, IADR-0151, IADR-0152, IADR-0253, IADR-0272, IADR-0379, IADR-0401, IADR-0402, IADR-0410, IADR-0411, IADR-0412, IADR-0415, IADR-0416]
author: claude
created: 2026-09-09
updated: 2026-09-09
---

# IADR-0417: 受け口として立つ最初の一枚

## 状況

#1255 の残る east-west 経路のうち、RetrievalService 宛は 2 つある。

| # | 経路 | 実体 |
| --- | --- | --- |
| ② | AiAnalysis → Retrieval `POST /search` | 検索本体 |
| ③ | BFF → Retrieval `POST /search/attribute-values` | 権限内属性値の照会 |

**RetrievalService は今まで gRPC の呼び出し元でしかなかった**（LlmGateway 宛・Graph 宛・認可宛）。
**受け口として立つのはこの ADR が最初である。**

### 壁は 2 つあり、1 つは既に取り除かれている

1. 🔴 **どちらの経路も「呼び出し元が解決した scope を本文で送る」形だった。**
   そのまま gRPC へ移すと [[IADR-0410]] が明示的に拒んだ口を開くことになる。
   ⇒ **#1339（[[IADR-0416]]）が受け口側を「自分で解決する」形へ変えたので、この壁は消えた。**
   面が運ぶのは**利用者文脈だけ**でよい。
2. 🔴 **経路 ② には連鎖がある。** Retrieval の `GrpcGraphNeighborExpander` は `HttpContext.User` を読み、
   それが埋まるのは**呼び出し元が利用者トークンを転送しているから**である（`ADR-0087` 決定 1 の
   「経路 1 は利用者の同一性の唯一の供給路」）。s2s だけで移すと**近傍展開が黙って空になる**。
   ⇒ **この壁は残っている。**

## 決定

### 決定 1: **スライスを 2 つに割り、③ を先に移す**

**A（本 ADR）**: proto 新設 ＋ h2c リスナ ＋ 試験の足場 ＋ deploy ＋ `AttributeValues` rpc（呼び出し元は BFF）。
**B（次）**: `Search` rpc（AiAnalysis ＋ BFF）＋ `IGraphNeighborExpander` の文脈受け渡し。

理由: ③ は**近傍展開が絡まない純粋な輸送の差し替え**である。壁 2 を抱えたまま受け口を開くと、
「面が動いたのか、文脈が落ちたのか」が切り分けられなくなる。**先に受け口の器を確かめる。**

🔴 **`/search` の rpc をこの proto へ足さない。** 足すと「この面に無い」という宣言が嘘になる
（[[IADR-0401]] 決定 2・[[IADR-0412]] 決定 1 と同じ理由）。スライス B は**別の proto**を新設する。

### 決定 2: 🔴 **面が運ぶのは利用者文脈だけであり、解決済みのスコープを受ける口は開かない**

`ListValuesRequest` は `key` / `user` / `narrow_to` の 3 つだけを持つ。
**`scope` という項目は存在しない**（[[IADR-0410]] / [[IADR-0416]]）。
受け口は受け取った `user` で `AuthzScope/Resolve` を**自分で**呼ぶ。

**判定の位置は動かない** —— 従前も RetrievalService が実施点であり（#1339 以降）、
移行で変わるのは**文脈の運び方だけ**である。

### 決定 3: 🔴 **絞り込み（`narrow_to`）は権限とは別項目で運ぶ**

混ぜたものが REST 面の `Scope` であり、**それが信じられてしまった原因**である。
別項目にすると、受け口は「これは権限ではない」と型で知る ——
`ScopeNarrowing.Resolve` へ渡すだけで、**書いた値がどれだけ広くても許可は広がらない**
（[[IADR-0415]] の narrowing-only）。

🔴 **BFF はこの項目を使わない。** BFF が持っている「解決済みスコープ」を `narrow_to` へ写すと、
**分岐（`Branches`）を平たいキー単位の集合へ潰す**ことになる ——
[[IADR-0253]] 決定 2 の非包含により、**分岐単独で到達できる文書の値が候補から落ちる**。
利用者が指定した絞り込みは、この口には元から無い（REST 面も持っていない）。

**それでも項目を置く**のは、受け口の意味論を「権限 ∩ 絞り込み」として固定するためである ——
呼び出し元が現れたときに**口を増やさずに済む**（[[IADR-0401]] 決定 2 の「要らないものを面へ出さない」
に見えるが、ここで出しているのは**呼び出し元の要求**ではなく**受け口の規則**である）。

### 決定 4: 🔴 **面は `ServiceCaller` を要求する。REST の口は残す**

REST の受け口は認可を持たない（#1318 欠陥 B。`/search` も `/search/attribute-values` も
`RequireAuthorization` を持たない）が、gRPC 面は [[IADR-0379]] 決定 4 に従い realm ロール
`platform-service` を要求する —— **権限が狭まる向き**である。

🔴 **利用者トークンでは開かない**（confused deputy の防止）。
「認証さえあれば通る」形にすると、転送された管理者トークンで s2s の面が開く。

**REST の口は残す**（並走中の正は REST。[[IADR-0379]] 決定 5）。
切替も戻しも呼び出し元の構成（`Services:RetrievalServiceGrpc`）1 つで行い、**コードは変えない**。

### 決定 5: 🔴 **REST と gRPC は同じ問い合わせ関数を通る**

`AttributeValuesEndpoint.ListAsync(store, key, scope, ct)` を両面が呼ぶ。
写すと、**片方だけ分岐の扱いが変わった状態**が作れる —— 本リポジトリが繰り返し踏んでいる形である
（[[IADR-0412]] 決定 6 が同じ理由で 1 つに寄せた）。

同じ理由で、解決も**入口 2 つ・本体 1 つ**にする ——
`ISearchAccessResolver.ResolveAsync(HttpContext)`（REST）と
`ResolveForUserAsync(userId, attributes)`（gRPC）が同じ後段を通る。

### 決定 6: 🔴 **「利用者が分からない」は `INVALID_ARGUMENT` であり deny へ畳まない**

畳むと、呼び出し元の配線誤りが「候補が 1 件も無い」と**見分けられなくなる**
（呼び出し元は資格情報が無ければ**呼ばない**）。`graph_neighbors.proto` と同じ姿勢である。

**「候補が無い」と「権限が無い」は区別させない**（[[IADR-0151]] 決定 5）——
どちらも**空の配列**で返る。**引けなかったのは gRPC status である**（この 3 つ目だけが別の面に出る）。

### 決定 7: 🔴 **辞書は面に出さない**

REST の `AttributeValuesResponse` は管理者向けの `Dictionary` 欄を持つが、
**それを添えるのは BFF であり後段ではない**（[[IADR-0152]] 決定 3）。
`ListValuesResponse` は `values` だけを持つ（[[IADR-0401]] 決定 2）。

### 決定 8: **試験は実 Kestrel で起こす**

`TestServer` は in-memory であり、**h2c のポートが実際に bind され、HTTP/1.1 のポートが
消えていない**ことを観測できない。DocumentService / GraphService / AuthorizationService の
同名の器と**同型**の `GrpcKestrelFactory` を置く。

🔴 **ABAC の解決だけを差し替える**（認可サービスへの実通信を持たないため）。
差し替えるのは**後段への往復**であって**判定の位置ではない** ——
器は `ResolveForUserAsync` に渡された利用者文脈を**記録し**、試験が
「呼び出し先が本文の文脈で自分の判定を行っている」ことを観測できるようにする。
**記録が無ければ「呼び出し元のスコープを信じている」実装でも面の試験は緑になり得る。**

### 決定 9: 🔴 **呼び出し元の縮退は、REST が持っている 2 つの枝を潰さない**

`/bff/attribute-values` の REST 経路は**縮退の向きが 2 つに割れている**。

| 事象 | REST の扱い |
| --- | --- |
| 後段が返した非 2xx | **透過**（`Results.StatusCode`）。障害を 200 空応答で隠さない |
| 後段へ到達できない | **空配列**（存在秘匿を崩さない） |

gRPC は**どちらも `RpcException` に畳む**ので、**status で分け直す**。

- `UNAVAILABLE` / `DEADLINE_EXCEEDED`、および s2s トークンの取得失敗
  （`InvalidOperationException`。後段へ 1 バイトも届いていない）→ **空配列**
- それ以外の status（`INTERNAL` / `PERMISSION_DENIED` / `INVALID_ARGUMENT` 等）→ **502**

🔴 **全 status を「不達」へ畳まない。** 畳むと**透過の枝が静かに消え**、
運用側が後段の不調に気づけなくなる（[[IADR-0151]] 決定 5 の射程は「候補の有無を利用者へ
区別させない」ことであって、**障害を隠すこと**ではない）。

🔴 **元の HTTP 状態番号は輸送を跨いで再現できない。** REST は後段の番号をそのまま返すが、
gRPC の status に対応する番号は無い。**守るのは「200 空応答にしない」という不変条件**であり、
番号の一致ではない。

**`DocumentBffEndpoints` は全 status を畳んでいるが、それと矛盾しない** ——
あちらの REST 経路は `GetFromJsonAsync` が非 2xx でも例外を投げるため、**元から 1 つの枝**だった。
**縮退の向きは呼び出し元の call site ごとに違う**（[[IADR-0400]] 決定 5 / [[IADR-0402]]）。

## 帰結

- RetrievalService の Service に `grpc` ポート（8081）が出る。**readiness は 8080 のまま**である。
- BFF は 3 つ目の gRPC 宛先を持つ（認可＝キー無し／文書／検索）。
  チャネルは**キー付き ＋ `TryAdd`**（[[IADR-0402]] 決定 6 / [[IADR-0412]] 決定 5）。
- `/search` は**移らない**。スライス B の先行条件（近傍展開の文脈受け渡し）が残っている。
- REST の 2 端点は無認可のままである（#1318 欠陥 B）。**本 ADR はそれを変えない** ——
  掛けると SPA / McpServer への影響を測る必要がある。gRPC 面だけが狭い、という非対称は
  **並走の期間だけ**続く。
  - ［2026-09-09 追記 / #1318］**REST の 2 端点は [[IADR-0418]] で認証を要するようになった**
    （ポリシー無しの `RequireAuthorization()`）。SPA / McpServer への影響は実測で消えた。
    **gRPC 面が `ServiceCaller`・REST 面が利用者トークンという非対称は残り**、
    決定 4 のとおり並走の期間だけ続く。
