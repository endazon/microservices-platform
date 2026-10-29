---
title: 作業仕様書 — BFF に経路の無い後段の端点を分類し、通信仕様書に記録する（#2135）
type: spec
status: done
related_ids: [FR-19, FR-06, FR-08, FR-10, SC-05, SC-10, SC-17, ADR-0119, ADR-0071, NFR-27]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2135"
---

# 作業仕様書 — BFF に経路の無い後段の端点の扱い（#2135）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `1f69f44d`）。親 #2047（指摘 L-2）。

## 現状（develop で確かめた呼び出し元）

走査: 各端点のパスの文字列で `src/`（AST の submodule を含む）・`scripts/`・`deploy/` を引いた（試験を除く）。エッジ（`deploy/local/edge-istio/virtualservice-app.yaml`）が後段へ直接通すのは `/private-notes/sync/` 配下と MCP だけで、下の端点はどれもエッジから届かない。

| 端点 | 呼び出し元 | 計画の扱い |
| --- | --- | --- |
| `PATCH /documents/{id}/metadata` | 機械クライアント（自分が `owner` の組織文書に限る。計画 ADR-0119 決定 2）。AST は**意図して使わない**（属性の全置換で露出のキーを落とす危険。AST `CatalogReportDraftKnowledgeCopy.cs`）。人の画面（SC-05）は `PUT /documents/{id}` を使う | 機械クライアントの口 |
| `PUT /documents/{id}/body` | AST の KB の書き手（`HttpKnowledgeDocumentCatalog.cs`。本文の投入） | 機械クライアントの口 |
| `GET /documents/page` | 呼び出し元なし。#1575 が AST の KB 入れ直しの迂回を解くために置いたが、AST はまだ `GET /documents` を使う | 機械クライアントの口（未採用） |
| `GET /private-notes/quotas/{ownerId}`・`PUT …` | 呼び出し元なし。BFF の注記は「載せる画面が計画に無い」と書く | **［2026-10-23］計画が SC-17 の欄として確定した**（使用量と上限を示し、上限を変えられる。NFR-27。planning#835）。計画の 3 点セットは「BFF の経路と本画面の欄は無い。画面からの変更は配備後」「配備までは運用の手順書で後段を直接呼ぶ」と書く |
| `GET /feedback/` | 呼び出し元なし。管理者限定（自由記述と利用者 ID を返す）。FR-08 の品質改善（低評価回答のレビュー）用 | 画面は計画に無い |
| `GET /dashboard/trends`・`GET /dashboard/usage` | 呼び出し元なし。BFF（SC-10）は `GET /dashboard/summary` だけを使い、summary は同じ集計関数（`AggregateTrendsAsync`・`AggregateUsageAsync`）で両方を含む。集計規則の試験（しきい値・合成トラフィックの除外）がこの 2 口を叩く | summary が画面の正 |

## 方針（分類）

1. **後段専用（s2s。機械クライアント）として記録する**: `PATCH /documents/{id}/metadata`・`PUT /documents/{id}/body`・`GET /documents/page`。BFF には通さない（人の画面に要らない）。
2. **BFF に通す（計画が確定済み）**: `/private-notes/quotas/{ownerId}`。
   - 🔴 **本 PR では経路を足さない。** 計画が定めたのは SC-17 の欄（使用量と上限・変更・監査）であり、BFF の経路だけを足しても使い手が無い。欄と経路は一緒に作る。加えて計画は上限 0 を許すと裁定した（2026-10-24）が後段は 0 を拒む。いずれも本 issue（分類の記録）の射程を越えるので、**別の issue に起票する**。
   - BFF の注記（`PrivateNoteBffEndpoints.cs`「載せる画面が計画に無い」）は事実でなくなったので直す（規則 10）。
3. **運用の口（BFF に通さない）として記録する**: `GET /feedback/`（画面が計画に無い。管理者が API で直接使う）・`GET /dashboard/trends`・`GET /dashboard/usage`（summary の部分集合。外部の呼び出し元は無い）。
   - 撤去は**しない**。summary の部分集合の 2 口は集計規則の試験の口でもあり、撤去には試験 10 本の書き換えが伴う。害（外部へ露出していない・認可は summary と同じ）が無いので、記録に留める。
4. **記録先**: 通信仕様書 `docs/api/BFF_bff-surface.md` §生成対象と対象外 に「BFF に経路の無い後段の端点」の表を足す（生きた文書に置く。新しい IADR は作らない —— 決定は既存の方針〔機械クライアントの口・画面の無い口を BFF に載せない〕の当てはめである）。

## 母集合（規則 9・10）

- **規則 9**: 上の表の端点ごとにパスの文字列で走査した（呼び出し元の列）。issue の列挙のほかに、BFF に経路の無い後段の口を網羅的に数えることはしていない（issue の射程は列挙された端点）。
- **規則 10**: 「載せる画面が計画に無い」（`PrivateNoteBffEndpoints.cs`）が、計画の 2026-10-23 の追加で誤りになっていた。直す。

## 受け入れ基準 → 確かめ方

| 基準 | 確かめ方 |
| --- | --- |
| 各端点の分類が記録されている | 通信仕様書の表 |
| BFF に通すものは経路と試験がある | 容量の口は別 issue へ（上の方針 2）。本 PR では足さない |
