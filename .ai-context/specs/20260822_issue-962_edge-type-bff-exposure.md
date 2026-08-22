---
title: 辺の型辞書を BFF から公開する — 権限の非対称を解く
type: spec
status: draft
related_ids: [FR-17, SC-18, SC-09, SC-10, ADR-0033, ADR-0039, IADR-0242]
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0033_knowledge-graph-data-model-and-store.md
  - planning:projects/microservices-platform/07_adr/ADR-0039_sc18-graph-rendering-library.md
---

# 仕様書: 辺の型辞書の BFF 公開（#962 / 親 #450）

> 本書は**着手前**に作成した。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-17**
- 画面（SC）: **SC-18**（グラフビュー。辺の型の描き分け・型フィルタ）/ SC-09・SC-10（辞書管理。既存）
- 関連 ADR: **ADR-0033**（決定 3・4・9）/ **ADR-0039**（`Accepted`。2026-08-22 実測）

## なぜ今なのか

#916a（#952）で**意図的に対象外にした**。当時 SC-09 / SC-10 / SC-18 が **ADR-0039 の `Proposed` ゲート下**にあり、口だけ先に置く理由が無かったためである。

**ADR-0039 は 2026-08-22 に `Accepted` へ移行した**（planning 原文を実測）。その理由は消滅した。

> 🔴 **この issue は「誤った前提から派生した設計判断」の後始末である。** ゲートの状態を根拠に射程を狭め、その判断が実装の形（BFF に辞書の口が無い）として残った。ラベル 1 枚の誤りではなく、**派生判断が実装に残る**のが誤った前提の本当の費用である。

## 🔴 設計の核心: 既存の読み取り口は一般利用者に使えない

`EdgeTypeEndpoints` の読み取りは **admin / operator 限定**である。

```csharp
var read = app.MapGroup("/graph/edge-types")
    .RequireAuthorization(p => p.RequireRole(AdminRole, OperatorRole));
```

**SC-18 は一般利用者の画面である。** この口をそのまま BFF へ出しても、一般利用者は **403** を受け取る。**「BFF に出す」だけでは #917 の必要を満たさない。**

### では読み取りのロールを緩めればよいか —— 🔴 それは情報漏れになる

`EdgeTypeDto` は `UsageCount`（その型を使う辺の本数）を含み、その数え方は:

```csharp
private static async Task<int> UsageOfAsync(GraphDbContext db, Guid typeId, CancellationToken ct)
    => await db.Edges.CountAsync(e => e.EdgeTypeId == typeId, ct);
```

**全辺を数えている。ABAC で絞っていない**（同ファイルに `scope` の語は 0 件。実測）。

admin / operator に見せるぶんには設計どおりだが、**一般利用者に見せると「自分に見えない辺を含む総数」が漏れる**。ホップごと ABAC（IADR-0242）が個々のノード・辺を隠しているのに、**集計値が総量を漏らす**形になる。

### 採る案: 描画用の口を分ける

| 口 | 認可 | 返すもの | 消費者 |
| --- | --- | --- | --- |
| `GET /graph/edge-types`（既存・不変） | admin / operator | `EdgeTypeDto`（**UsageCount あり**） | SC-09 / SC-10 |
| **`GET /graph/edge-types/catalog`（新設）** | **認証のみ**（ロール不問） | `EdgeTypeCatalogItemDto`（Id / Name / Layer / IsSymmetric。**UsageCount なし**） | **SC-18** |

**却下した案:**

- **既存口のロールを緩める**: 上記のとおり集計値が漏れる。**最小公開の原則に反する**
- **応答の形を呼び出し元の権限で変える**（権限があれば UsageCount を足す）: 型付きクライアント（orval 生成物）にとって**同じ口が 2 つの形を返す**のは扱いづらく、テストでも「どちらの形か」を毎回判定する必要が出る
- **BFF がサービスアカウントで既存口を叩く**: 方式 B 相当の信頼面を新設することになり、#952 で退けた判断と矛盾する。**利用者の資格情報で通らないものを、BFF の資格情報で通してはならない**

### 権限伝播は方式 A（`Authorization` の伝播）

#952 で記録した判断規則をそのまま適用する。**GraphService は自分で ABAC / ロールを解決する型**なので、利用者の JWT を伝播する。

## 対象範囲

### 対象

- GraphService: `GET /graph/edge-types/catalog`（認証のみ・UsageCount なし）
- `Knowledge.Contracts`: `EdgeTypeCatalogItemDto`
- BFF: `GET /bff/graph/edge-types`（上を中継。`Authorization` を伝播）
- `docs/api/openapi.yaml` ＋ orval 生成物 ＋ `x-roles: []`
- BFF 合成 ratchet の更新

### 対象外

- 書き込み系（POST / PUT / DELETE）の BFF 公開。SC-09 / SC-10 の画面が未着手であり、**過剰な公開面を先に作らない**
- SC-18 の画面そのもの（#917）

## 受け入れ基準

- [ ] 一般利用者（admin / operator でない）が `/bff/graph/edge-types` から辞書を引ける
- [ ] 🔴 **応答に `usageCount` が含まれない**（漏れの防止）
- [ ] 未認証は 401
- [ ] 既存の `/graph/edge-types`（admin / operator 限定・UsageCount あり）は**一切変わらない**
- [ ] openapi と orval 生成物が整合（CI の再生成差分検査）

## テスト方針

🔴 **否定形だけでは測れない。**#952 の変異 B-1 で実測したとおり、権限伝播が壊れると**全部 403 / 404 になり、否定形のテスト群は緑のまま**になる。**陽性対照を対で置く。**

| ケース | 内容 |
| --- | --- |
| C-01 | **一般利用者（ロールなし）で 200 ＋ 辞書が返る（陽性対照）** |
| C-02 | 🔴 応答本文に `usageCount` が**現れない**（漏れの固定） |
| C-03 | 未認証 → 401 |
| C-04 | 後段が 403 を返したらそのまま透過（BFF が握り潰さない） |
| C-05 | 後段へ到達できない → 502（**空配列へ縮退しない**。「型が無い」と「引けない」は別） |
| C-06 | GraphService 側: `/graph/edge-types/catalog` は admin でなくても 200 |
| C-07 | GraphService 側: 既存 `/graph/edge-types` は一般利用者で 403（**変えていないことの固定**） |

### 変異試験の設計

| 変異 | 落ちるべき | 🔴 注意 |
| --- | --- | --- |
| **E-1** `Authorization` の伝播を外す | **C-01（陽性対照）** | C-03 は落ちない。伝播が切れても 401 のままだからである |
| **E-2** カタログの DTO に `UsageCount` を足す | C-02 | 漏れの検出力 |
| **E-3** 新設口に admin ロール要求を付ける | C-01 / C-06 | 「一般利用者が使える」ことの固定 |

**E-1 が最重要である。** 陽性対照が無ければ「常に 401 を返す実装」が変異を通る。

## 計画書との差異

- 差異: なし。ADR-0033 は辞書の値集合を定めるが、**読み取りの認可粒度は定めていない**。SC-09 / SC-10（管理）と SC-18（閲覧）で消費者が異なるため口を分ける、というのは実装側の設計判断であり、計画の裁定を先取りしない

## 未決事項

1. 書き込み系の BFF 公開（SC-09 / SC-10 着手時に判断する）
2. カタログを ABAC で絞るべきか —— **絞らない。**型名は文書ではなく語彙であり、タグ辞書（SC-09）と同じ扱いである。**絞るべきは集計値であって語彙ではない**、というのが本書の立場である
