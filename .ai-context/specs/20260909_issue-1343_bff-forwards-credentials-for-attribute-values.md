---
title: /bff/attribute-values が利用者の資格情報を後段へ運び、対象範囲フィルタの候補が戻る
type: spec
status: done
related_ids: [FR-04, FR-05, FR-09, NFR-09, UC-01, SC-01, SC-05, SC-08, SC-09, ADR-0034, ADR-0043, IADR-0044, IADR-0151, IADR-0152, IADR-0410, IADR-0416]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: 資格情報が届かない口は、何を送っても空である（#1343）

## 起点

- FR-04（RAG・属性値照会）／FR-05（ABAC）／FR-09（辞書）／NFR-09（認可）／SC-01・SC-08（対象範囲フィルタ）
- 計画 ADR: `ADR-0034` 決定 1（ホップごと判定）／`ADR-0043`（権限内属性値の照会）
- 実装 ADR: [[IADR-0044]]（最終防衛線）／[[IADR-0151]] 決定 5（候補が無いことと権限が無いことを
  区別させない）／[[IADR-0152]] 決定 3（辞書は BFF が添える）／[[IADR-0416]]（受け口が自分で解決する）
- issue: #1343（回帰。#1342 が着地した直後から）

## 🔴 現状（実測。`develop` `c57fa8b6`）

`POST /bff/attribute-values` が **すべての利用者に対して常に空の候補**を返す。
SC-01 / SC-08 の対象範囲フィルタ（「権限内のタグ／部門／プロジェクトのみ選択可」）には
**選択肢が 1 つも出ない**。

### 因果

1. #1342（[[IADR-0416]]）で `RetrievalService` の受け口が **自分で ABAC スコープを解決する**形になった。
   解決器 `SearchAccessResolver` は **未認証なら認可サービスへ問い合わせずに deny** する
   （[[IADR-0044]] の多層防御。条件を持たないポリシーが 1 件でも active なら匿名にも許可が下りるため）。
2. ところが BFF は `/search/attribute-values` を呼ぶとき **利用者の `Authorization` を伝播していない**。
   `/bff/search` は #970 から伝播しているが、属性値照会の枝には無かった。
3. 受け口は要求を未認証として扱う → deny → 空配列 → BFF は 200 でそのまま返す。
   **存在秘匿の縮退と見分けが付かない**ので、利用者にも運用側にも障害として見えない。

🔴 **`CreateClient` は毎回新しい `HttpClient` を返す。** 検索の枝で付けたヘッダはここへは来ない ——
**同じ後段（`"RetrievalService"`）でも口ごとに付ける必要がある。**

### なぜ CI をすり抜けたか（**本件の要点**）

**両側に試験は在るのに、その経路を誰も通っていない。**

| 層 | 試験 | なぜ見えなかったか |
| --- | --- | --- |
| BFF | `BffSearchEndpointTests` | 後段はスタブ。伝播の観測点 `LastSearchForwardedAuthorization` は**検索の枝でしか記録されず**、属性値の枝は**その行の手前で早期 return する** |
| Retrieval | `RetrievalService.Tests` / `Knowledge.IntegrationTests` | `ISearchAccessResolver` を**全許可のスタブへ差し替えている**（#1342 が明示的にそうした） |
| 結合 | —— | BFF と Retrieval を本物どうしで繋ぐ試験は無い |

⇒ #1342 の「呼び出し元 2 か所は既に利用者トークンを転送している」という前提が、
**属性値照会について誤っていた**。転送しているのは検索の 1 か所だけである。

これは #1342 自身が記録した M-3 / M-4 の生存（「試験は在るのに、その経路を通っていない」）と
**同じ形**である —— 器がスタブする対象が、まさに測りたい振る舞いを含んでいた。

## 母集合（規則 1・2・9。**誤りの側**＝「受け口が自分で解決する口を、資格情報を運ばずに呼ぶ」で引いた）

走査は `Authorization` の**扱いの側**から引いた（`grep -rn "Authorization"` を BFF 3 群へ。
「伝播している」側の語だけで引くと、**伝播していない箇所は構造的に落ちる**）。

| 呼び出し元 | 呼び出し先 | 伝播 | 判定 |
| --- | --- | --- | --- |
| Knowledge BFF `/bff/search` | `/search` | あり（`SearchBffEndpoints.cs:64`） | 影響なし |
| **Knowledge BFF `/bff/attribute-values`** | **`/search/attribute-values`** | **なし** | 🔴 **本件** |
| AiAnalysis `RagOrchestrator` | `/search` | あり | 影響なし |
| AST `HttpKnowledgeBaseSearch` | `/search` | s2s トークン（利用者ではない） | 影響なし。**`Scope` を送らないので #1342 の前から空**である（挙動は変わっていない） |

### 除外したものと理由（規則 6）

- **AST（`src/ai-stock-trading`）**: 別リポジトリの submodule であり、かつ上表のとおり挙動が変わっていない。
- **`/authz/scope` を呼ぶ経路**: 資格情報は BFF 自身の s2s トークンであり利用者の JWT ではない
  （[[IADR-0410]]）。本件の類型ではない。
- **gRPC 面**: 本件の口には gRPC 面がまだ無い（#1255 スライス A で新設する）。

### 同型の穴の引き直し（規則 10。受け入れ基準 4）

**「スタブが資格情報を見ずに 200 を返す枝」**を `BffTestFactory` の全スタブ器で引き直した。
観測点を**先頭**に置いている器（`StubHandler` / `Feedback` / `Graph` / `PrivateNote` / `Assumptions` /
`RiskControls` / `Monitor` / `Mcp` / `Notification` / `Wiki`）は同型ではない。
**分岐の後ろに観測点がある器は `RetrievalStubHandler` ただ 1 つ**であり、それが本件である。

ただし**観測点が最初から無い枝**が 1 つ見つかった —— `DocumentStubHandler` の `/tags`
（辞書の取得）。**現に伝播は行われており欠陥ではない**が、落としても緑のままになる構造は同じである。
本 PR で観測点と対の試験を足した（[[IADR-0141]] の「2 回目」に当たる）。
`DataSource` / `Conversion` の器も観測点を持たないが、**本 PR の射程（属性値照会の口）の外**なので
足していない —— 射程を広げず、ここに記録する（1 回目）。

## 決定

1. `/bff/attribute-values` でも利用者の `Authorization` を後段へ伝播する（`/bff/search` と**同形**）。
   **無ければ付けない** —— BFF がトークンを捏造せず、縮退の判断と警告は受け口側が一元で持つ。
2. **新しい枝を作らない。** 受け口・応答・縮退の規則はいずれも変えない（[[IADR-0151]] 決定 5 のまま）。
3. **観測点を口ごとに置く。** 「同じ後段だから測れている」は成り立たない ——
   スタブがパスで分岐する以上、**分岐ごとに観測しなければ緑のまま落とせる**。
4. 辞書の取得（`/tags`）にも同じ観測点を置く（決定 3 の適用。上記「同型の穴の引き直し」）。

## 変異試験（すべて実走・着地を `grep` で確認・戻したことは緑で確認）

| # | 変異 | 赤 |
| --- | --- | --- |
| M-1 | 属性値照会の伝播を外す | **1**（`PostAttributeValues_ForwardsTheCallersAuthorizationToRetrieval`） |
| M-2 | 無条件に付ける（無いヘッダを捏造する） | **1**（`PostAttributeValues_DoesNotInventAnAuthorizationHeader`） |
| M-3 | 辞書取得の伝播を外す | **1**（`PostAttributeValues_ForwardsTheCallersAuthorizationToTheTagDictionary`） |

🔴 **M-1 と M-2 が別々の試験しか殺す**ことが「両方向を対で置く」の根拠である。
🔴 **M-3 が M-1 の試験を殺さない**ことが、口ごとに観測点が要ることの根拠である
（同じ `HttpContext` から同じ形で取っていても、**別の後段・別のクライアント**である）。

## 受け入れ基準

- [x] `/bff/attribute-values` が利用者の `Authorization` を後段へ伝播する（陽性）
- [x] 受信要求にヘッダが無ければ付けない（陰性）
- [x] 変異試験: 伝播を外すと赤になることを実測する（M-1 / M-2 / M-3）
- [x] 同型の穴（スタブの枝が観測点の手前で return する）が他の枝に無いか引き直す

## 変えていないもの

- 受け口（`RetrievalService`）のコード・契約・応答形。**1 バイトも触っていない。**
- 縮退の規則（[[IADR-0151]] 決定 5）。空配列は今も「候補が無い」と「権限が無い」を区別させない。
- 辞書の可視性（[[IADR-0152]] 決定 3・5）。一般利用者には `null` のままである。
- deploy / helm / compose / realm / secret。**不変**。
