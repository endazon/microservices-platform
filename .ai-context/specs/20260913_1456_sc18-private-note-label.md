---
title: "SC-18 のラベルから「（自分のみ）」を外す（#1456。計画 ADR-0102 決定 5 の反映）"
type: spec
status: done
related_ids: [FR-19, SC-01, SC-18, SC-19, ADR-0036, ADR-0102]
author: Claude
created: 2026-09-13
updated: 2026-09-13
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0102_private-note-display-to-non-owner-viewers.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# 仕様書: SC-18 のラベルから「（自分のみ）」を外す

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-19`（個人資料）
- 非機能要件（NFR）: なし（表示文言の是正。個別番号を当てない）
- ユースケース（UC）: なし（表示のみ）
- 画面（SC）: `SC-18`（ノードのホバー・側パネル・凡例）。**`SC-19` は本人の資料しか扱わないため不変**、`SC-01` の出典行は未実装
- 関連 ADR: `ADR-0102` 決定 5（括弧書き「（自分のみ）」は所有者にだけ付す）／`ADR-0036` D-06（共有された相手が読める）
- 計画書リンク: `planning:projects/microservices-platform/07_adr/ADR-0102_private-note-display-to-non-owner-viewers.md`

## 目的・背景

ナレッジグラフは**他の利用者が所有する個人資料も描き得る**（所有者がグラフ表示を ON にし、かつ閲覧者が読める場合）。
実装は個人資料を一律「**個人資料（自分のみ）**」と表示しており、**共有された相手には事実と異なる**。
計画 ADR-0102 決定 5 は「括弧書きは所有者にだけ付す。所有者以外は『個人資料』」と定めた。

## 実装の現状（着手前の実測）

| 箇所 | 現状 |
| --- | --- |
| `components/NodeSidePanel.tsx:81` | 側パネルの「種別」= `個人資料（自分のみ）` |
| `components/GraphViewPage.tsx:70` | `labels.privateNote` = `個人資料（自分のみ）`（ホバーの種別ラベル） |
| `components/GraphLegend.tsx:50` | 凡例 = `角丸四角（破線の輪郭）= 個人資料（自分のみ）` |
| `types/graphOption.test.ts:15`・`components/GraphViewPage.test.tsx:297` | 文言を固定するテスト |

🔴 **本画面は所有者を判別できない。** グラフのノード契約 `GraphNodeItem` が持つのは
`documentId` / `title` / `isPrivateNote` の 3 項目だけで、**所有者を運ぶ項目が無い**（`docs/api/openapi.yaml` 実測）。

## 設計（決定）

1. **本画面では括弧書きを一律で外す。** 表示は「**個人資料**」とする。
   - 決定 5 は「**所有者以外に付さない**」ことを求めており、**所有者に付けることを義務づけていない**。
     所有者を判別できない面で一律に外すことは決定への適合であり、**契約に所有者を足す必要は無い**。
   - **アイコン 👤・形（角丸四角＋破線）・色以外の手掛かりは現行のまま**（色だけで意味を持たせない規律は不変）。
2. **凡例はもともと所有関係に依らない**ため、「角丸四角（破線の輪郭）= 個人資料」とする。
3. **契約・バックエンドは変更しない。** 変わるのは表示文言だけである。
4. **`SC-19` は変更しない**（本人の資料しか扱わず、「（自分のみ）」は事実と合う）。**`SC-01` の出典行は未実装**であり、
   実装時に決定 5 へ従う（本作業の範囲外。画面仕様書に注記だけ残す）。

## 影響範囲（専有領域）

- `src/knowledge/frontend/src/features/sc18-graph/**`（3 コンポーネント ＋ 2 テスト）
- `src/platform/frontend/src/locales/**`（Lingui カタログの再生成分）
- `docs/screens/SC-18_knowledge-graph.md`（規則の明記）・`docs/screens/SC-01_search-chat.md`・`docs/screens/SC-19_private-notes.md`（下記 母集合）

### 母集合（規則 1〜10）

走査語: `自分のみ` を追跡下の全ファイルへ（`git grep`。submodule `src/ai-stock-trading` を除く）。

| 出た箇所 | 扱い |
| --- | --- |
| `sc18-graph` の 3 コンポーネント ＋ 2 テスト | **対象**（本作業） |
| `src/platform/frontend/src/locales/**`（`.po` / `.ts`） | **対象**（`pnpm run i18n` で再生成。手で触らない） |
| `docs/screens/SC-18_knowledge-graph.md` | **対象**。種別ラベルの規則を明記する（本画面は所有者を判別できないため一律「個人資料」） |
| `docs/screens/SC-19_private-notes.md` 主要素 2 | **対象**（規則 10）。「（検索結果・グラフのノードと同じ記号）」が**本変更で不正確になる** —— グラフ側は括弧書きを外すため、注記で書き分ける。**表示そのものは変えない** |
| `docs/screens/SC-01_search-chat.md`（モック #11・§実装しない要素） | **対象**（規則 9）。未実装の要素だが、**実装時に誤らないよう**決定 5 の注記を足す。表示の実装はしない |
| `sc19-private-notes` の実装・テスト・E2E（`👤 個人資料（自分のみ）`） | 対象外。本人の資料のみを扱い、事実と合う |
| `.ai-context/specs/20260804_*`・`20260828_*`・`20260831_*` | 対象外。**凍結された作業記録**（point-in-time） |
| `docs/how-to/plan-id-range-history-annex.md` | 対象外。レンジ引き直しの記録であり表示の規則ではない |

## 受け入れ基準（issue #1456 の写像）

- [x] ホバーの種別ラベルが「個人資料」（括弧書き無し）
- [x] 側パネルの種別が「個人資料」
- [x] 凡例が「角丸四角（破線の輪郭）= 個人資料」
- [x] 組織文書の表示は従来どおり（回帰なし）
- [x] 文言を固定する既存テストが新しい文言へ更新され、緑
- [x] `node scripts/check-i18n-catalogs.js` が緑（カタログを再生成して差分をコミット）

## 検証（`/verify` 相当）

`pnpm run lint` / `pnpm run typecheck` / `pnpm --filter @knowledge/frontend run test`（または `src` 直下の `pnpm run test`）/
`node scripts/check-i18n-catalogs.js` / `node scripts/check-trace-blocks.js` / `node scripts/check-doc-links.js` /
`node scripts/check-commit-messages.js --range origin/develop..HEAD`。
