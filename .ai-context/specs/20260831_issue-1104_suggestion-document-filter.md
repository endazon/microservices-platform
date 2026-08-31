---
title: 作業仕様書 — AI 提案一覧に documentId の絞り込みを足し、SC-03 のクライアント側間引きをやめる（#1104）
type: spec
status: done
related_ids:
  - FR-18
  - UC-10
  - SC-03
  - SC-21
  - ADR-0033
  - ADR-0034
  - IADR-0009
  - IADR-0242
  - IADR-0276
  - IADR-0300
  - IADR-0323
author: claude
created: 2026-08-31
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0033_knowledge-graph-data-model-and-store.md
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md
---

# 作業仕様書: AI 提案一覧の `documentId` 絞り込み（#1104）

## 起点となる計画書（逐語）

- **FR-18**（`02_requirements/01_requirements.md:46`）: 「…承認 UI は **SC-03（文書詳細）を主、
  SC-21（AI 提案一覧）を従**とする 2 か所に置く。**一括承認の手段は提供しない**…**リンク提案と
  タグ提案は同一の一覧に同居させ、画面を分けない**」
- **SC-03 §AI 提案の承認欄**（`05_screens/01_screens.md:242-244`）: 「**当該文書を両端のいずれかと
  する提案**（リンク候補・タグ候補）を本文の下部に表示し、**その場で承認／却下できる**」
  「**提案が 0 件のときは欄自体を表示しない**」「本欄に既定で表示するのは `pending` の提案である」
- **SC-21 §アクセス制御**（同 :801）: 「全利用者（ABAC の権限内）。**権限のない文書に関する提案は
  一覧に現れない**（「知識グラフ表示の共通規則」に従い、**件数にも現れない**）」
- **SC-21 §入力/バリデーション**: 状態フィルタ（`pending` 既定 / `approved` / `rejected` / すべて）・
  種類フィルタ（すべて既定 / リンク / タグ）。**文書フィルタは SC-21 の要求に無い。**
- **知識グラフ表示の共通規則**（同 :823-826）: 「**閲覧権のない文書への辺は完全に隠す**」
  「**フィルタはホップごとに適用する**」
- **ADR-0034 決定 1**: ホップごとに ABAC 判定を行う。終端でまとめてフィルタしない。
  **決定 2**: 見えない辺は完全に隠す（件数を含む）。**決定 2 の具体化**: 権限外は 404 に倒す。

## 着手前の実測（`develop` `9a4d1a9a` / 2026-08-31）

### ① ABAC は誰が評価しているか — **サーバ（GraphService）である。クライアントは一切評価していない。**

`Features/AiSuggestions/List/Endpoint.cs` の順序:

1. `IGraphAccessResolver.ResolveAsync(http, GraphAccessAction.Read, ct)` でスコープを 1 回解決。
   `!scope.Granted` なら**空配列**（fail-closed）。
2. DB で `state` / `kind` を絞る（**属性を持たない提案そのものへの絞り**）。
3. 🔴 **1 件ずつ `AiSuggestionEndpoints.ResolveEndpointsAsync` を呼び、両端の `GraphDocument` を
   引いて `AuthorizedNode.Authorize(doc, scope)`（＝`AbacNodeFilter.Matches`）で可視性を判定する。**
   端点が 1 つでも引けない／見えない提案は落とす（deny-closed）。表示名も可視性を通った側しか返さない。

**クライアント（`useDocumentSuggestions.ts`）が行っているのは `touches()` による表示対象の限定だけ**で、
権限判定ではない。**したがってサーバへ絞りを移しても ABAC の判定点は 1 つも動かない** ——
動かしてはならないのは**順序**であり、`documentId` の `Where` は手順 2（DB の絞り）へ足し、
手順 3 の可視性解決は**そのまま後段に残す**。

### ② 件数の上限 — **どの層にも無い。「あるはずの提案が出ない」状態は起きていない。**

- GraphService: `query.OrderBy(s => s.CreatedAt).ToListAsync(ct)`。**`Take` が無い**
  （`grep -n "\.Take(" GraphService` の一致は `GraphTraversal`〔ノード 200 / 辺 500〕と
  `AiSuggestionGenerator`〔`MaxCandidates`〕だけで、いずれも一覧の経路ではない）。
- BFF: `ProxyAsync<List<AiSuggestionDto>>` は `ReadFromJsonAsync` で全件読む。切り詰めない。
- SPA: `useDocumentSuggestions` は `filter` のみ。`slice` / `take` は無い。

**よって欠陥は「取りこぼし」ではなく規模**である（無制限の転送 ＋ 全件に対する N+1）。
issue の「秘匿の欠陥ではなく規模の欠陥」という整理は実測と一致する。
**上限を新設しない** —— SC-21 は「件数にも現れない」を要求しており、上限の導入は別の設計判断
（打ち切りの表示）を伴う。本 issue の射程外。

### ③ `documentId` は何の ID か — **文書（DocumentService の文書 ID）である。**

`AiSuggestion.SourceDocumentId` / `TargetDocumentId` はいずれも `GraphDocument.DocumentId` を指し、
`GraphDocument` は `GraphDocumentSyncConsumer` が `DocumentCreated` / `DocumentUpdated` から作る
**DocumentService の文書 ID の複製**である（ADR-0033 決定 2）。**辺の ID でも提案の ID でもない。**
SC-03 が渡す `doc.id`（文書詳細の文書 ID）と同じ名前空間であることは、
現行のクライアント側比較 `suggestion.sourceDocumentId === documentId` が実際に効いていることが示す。

- リンク提案は `SourceDocumentId`（起点）と `TargetDocumentId`（終点）の 2 端。
- **タグ提案は `SourceDocumentId` のみ**（`TargetDocumentId` は `null`）。
  したがって述語は `Source == id || Target == id` であり、**タグ提案も自然に拾える**。

## 母集合（自分で引いた。[[IADR-0141]] 決定 1 / `traceability.repo.md` 規則 9・10）

**誤りの側の文字列**（「文書での絞り込みが無い」「絞りは画面側にある」）と、
**ルート文字列のパス走査**の両方で引いた。行フィルタではなくパスから引いている。

| 軸 | 検索語 | 一致 |
| --- | --- | --- |
| 1 | `文書での絞り込み` / `しか受け` / `state.*kind.*しか` | `IADR-0300:158,160`・`specs/20260829_issue-450:169,252`・`docs/screens/SC-03:286,363`・`useDocumentSuggestions.ts:25,26`・`DocumentDetailPage.test.tsx:358` |
| 2 | `touches` / `画面側で行う` / `画面側にある` / `client 側` | `useDocumentSuggestions.ts:39,55`・`DocumentDetailPage.test.tsx:358`・`docs/tests/SC-03:67`・`specs/20260829_issue-450:170` |
| 3 | `git grep -l "graph/suggestions"`（拡張子で絞らずパスで） | 22 ファイル（下表） |
| 4 | `docs/api/BFF_bff-surface.md` の直接確認 | **一致 0**。`/bff/graph/*` 群ごと落ちている（[[IADR-0300]] フォローアップ 4 の既知欠落。本 issue で埋めない） |
| 5 | e2e（`src/platform/frontend/e2e`）・統合テスト（`src/knowledge/backend/Tests`） | `sc21-ai-suggestions.smoke.spec.ts` は未認証リダイレクトのみで一覧の形に触れない。統合テストの一致は `EdgeTypeDbGuardTests` のみ（辺の型・無関係） |

軸 3 の 22 ファイルの内訳と処遇:

| 反映する（10 件） | 反映しない（12 件）と理由 |
| --- | --- |
| `GraphService/Features/AiSuggestions/List/Endpoint.cs` | `IADR-0272` / `IADR-0276` / `IADR-0300` — **凍結記録**。本文プロズを後から書き換えない（`.ai-context/adr/`）。後継は本作業の IADR-0323 が持つ |
| `GraphService/Features/AiSuggestions/AiSuggestionEndpoints.cs`（**変更不要と確認**） | `.ai-context/specs/2026082*`・`20260829_issue-450` 4 件 — **確定済みの作業仕様書**。書き換えない |
| `Knowledge.Bff.Endpoints/GraphBffEndpoints.cs` | `SuggestionPromptGateTests.cs`・`WriteActionAuthorizationTests.cs` — 生成・書き込み経路であり一覧の形に触れない |
| `docs/api/openapi.yaml` | `sc21-ai-suggestions/api/useAiSuggestions.ts` — SC-21 は文書で絞らない。**`documentId` を送らない**（従来どおり全件）。変更しない |
| `GraphService/Tests/AiSuggestionEndpointsTests.cs` | `sc21-ai-suggestions/components/AiSuggestionListPage.test.tsx` — 同上 |
| `Platform.Bff.Tests/BffGraphSuggestionTests.cs` | `graph.msw.ts` / `graph.ts` — **orval 生成物**。手で書かず `pnpm run codegen` で再生成する（結果はコミットする） |
| `useDocumentSuggestions.ts` | |
| `DocumentDetailPage.test.tsx` | |
| `docs/screens/SC-03_document-detail.md`（§絞り込みの位置・§未決事項 6） | |
| `docs/tests/SC-03_document-detail.md`（ケース 15） | |

規則 10（**この変更で新たに誤りになる自分の記述**）で追加に引いたもの:
`docs/screens/SC-03_document-detail.md:363`（未決事項 6「サーバ側へ…足す追随が要る」）は
本作業で解消するため**未決事項から外す**。`docs/tests/SC-03:67` の理由欄「絞りは画面側にある」も
本作業で誤りになる。

## 決めたこと

### D-1 述語は DB の絞りへ足す。可視性解決の**前段**である

```csharp
if (documentId is not null)
    query = query.Where(s => s.SourceDocumentId == documentId
                          || s.TargetDocumentId == documentId);
```

可視性解決（`ResolveEndpointsAsync`）のループは**一切変えない**。判定を迂回する経路を作らない。

### D-2 権限外・不存在の文書 ID は **200 ＋ 空配列**（存在秘匿と整合）

- **404 に倒さない。** ここで 404 を返すと「その文書は無い（または見えない）」と
  「その文書の提案は 0 件」が区別できてしまう。**空配列なら両者は同一の応答**である。
- **文書の存在確認を先に行わない。** 述語は提案の行に対する絞りであり、
  存在しない ID なら一致 0 件、見えない文書なら可視性解決で全件落ちる —— **どちらも空配列**になる。
- ADR-0034 決定 2 が単票（承認・却下）で 404 を選んでいるのと矛盾しない: 単票は
  「1 件の資源への操作」であり、**一覧は集合への問い合わせ**である。集合の応答で存在を隠す形は空集合である
  （[[IADR-0009]] と同じ向き）。

### D-3 値域の検査は `Guid?` の束縛に任せ、**400 は形式不正のときだけ**

`Guid? documentId` で受け、パースできない値は ASP.NET Core が 400 を返す。
**`invalid_document_id` という独自の error コードを足さない** —— 形式不正は存在を漏らさず、
`state` / `kind` のような**値域**（列挙）を持たないためである。

### D-4 BFF は**そのまま渡すだけ**（既定の補完も検証もしない）

`BuildSuggestionQuery(state, kind, documentId)` に 3 つ目を足す。`string?` のまま透過する
（BFF が Guid へ束縛すると、形式不正の 400 の出所が 2 か所になる）。

### D-5 SC-03 は `touches()` を**撤去**し、`documentId` をサーバへ送る

- パラメータは `{ state: 'pending', documentId }`。**クエリキーは文書ごとに分かれる。**
- したがって `useSuggestionActions` の無効化対象も文書ごとになる ——
  **`useSuggestionActions(documentId)` へ引数を足し、パネルが見ているキーちょうどを無効化する。**
  （プレフィックス無効化にしない: SC-21 は本 mutation を使わず、広げる利得が無い。）
- **SC-21 は変更しない。** `documentId` を送らない＝従来どおり権限内の全件が返る。

### D-6 一括承認の不変条件に触れない

足すのは `MapGet("/")` のクエリパラメータだけで、**ルートは 1 本も増減しない**。
`No_bulk_approval_route_exists` / `No_bulk_approval_route_for_suggestions_is_exposed_by_the_bff` は
緑のままであることを実行して確かめる。

## 受け入れ基準 → テスト写像

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | `?documentId=<id>` でその文書を端点に持つ提案だけが返る | `AiSuggestionEndpointsTests.Filtering_by_document_returns_only_suggestions_that_touch_it`（陽性: 起点一致・終点一致・タグ提案。陰性: 無関係な提案） |
| 2 | `documentId` 無しは従来どおり全件 | 同上の陽性対照（同じ種を `documentId` 無しで引くと無関係な提案も返る） |
| 3 | 🔴 **ABAC が落ちていない**（陽性 ＋ 陰性の対） | `Filtering_by_document_still_hides_invisible_endpoints`: 権限内の文書の提案は返り（陽性対照）、**権限外の端点を持つ提案は `documentId` で名指ししても返らない**（陰性対照） |
| 4 | 権限外の文書 ID は存在秘匿と整合 | `An_unauthorized_document_id_is_indistinguishable_from_one_with_no_suggestions`: 権限外の文書 ID と、実在するが提案 0 件の文書 ID と、実在しない ID の 3 つが**同じ 200 ＋ 空配列**を返す |
| 5 | 形式不正は 400 | `A_malformed_document_id_is_rejected` |
| 6 | BFF がそのまま渡す | `BffGraphSuggestionTests.Filters_are_forwarded_verbatim` に `?documentId=…` の行を足す |
| 7 | SC-03 が client 側で絞らない | `DocumentDetailPage.test.tsx`: ケース 15 を**要求の観測**へ置き換える（`apiRequest` の呼び出しパスに `documentId=<DOC_ID>` が載る）＋ **サーバが返したものはそのまま描く**（間引かない） |
| 8 | 契約と生成物 | `docs/api/openapi.yaml` にクエリパラメータを追記 → `pnpm run codegen` の生成物をコミット |
| 9 | 変異テスト | `Where` を消すと #1・#3 の陰性側が落ちることを実測し、戻して残渣 0 を確認する |

## 未決事項（本作業で決めないこと）

1. **一覧の件数上限**（実測②）。SC-21 の「件数にも現れない」と衝突しない打ち切りの表示を
   決める必要があり、別の裁定が要る。本 issue では上限を導入しない。
2. **`docs/api/BFF_bff-surface.md` の `/bff/graph/*` 群の欠落**（[[IADR-0300]] フォローアップ 4）。
   本 issue の宣言ファイル領域の外であり、別 issue のままにする。
3. **タグ提案の承認経路**（planning#495 の裁定待ち）。本作業は一覧の絞り込みだけを扱う。
