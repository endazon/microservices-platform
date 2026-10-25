---
title: 作業仕様書 — 左ナビ「管理」グループの並びを計画（ABAC設定／ユーザー管理／MCP管理）に揃え、E2E M-012 で並びを固定する（#2189）
type: spec
status: done
related_ids:
  - SC-05
  - SC-09
  - SC-12
  - SC-17
  - ADR-0031
  - IADR-0337
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md §共通シェル（左ナビ 4 グループ構成）
  - planning:projects/microservices-platform/05_screens/mockups/hi-fi/sc-01.html（左ナビ）
related_specs:
  - 20261024_2016_shell-e2e
issue: "#2189"
---

# 作業仕様書 — 左ナビ「管理」グループの並び

## 目的と射程

左ナビ「管理」グループの並びが計画と違う。計画は `… ABAC設定／ユーザー管理／MCP管理` で、実装は `ABAC設定 → MCP管理 → ユーザー管理` である。
実装の並びを計画に揃え、E2E `shell.smoke.spec.ts` の M-012 にグループの並びとグループ内の並びの検査を足す。

**射程外**: グループの集合・項目の集合・役割ごとの出し分け（MSP#2187 で固定済み）。パンくず（SC 番号順で、描画順に影響しない）。

## 計画が正しいことの確認（読み取り専用）

計画 `origin/main`（`aa27f28`）で確かめた。

- `05_screens/01_screens.md` §共通シェルの記述は「管理」= SC-05 文書管理／SC-06 データソース／SC-07 変換ジョブ／SC-09 ABAC設定／SC-17 ユーザー管理／SC-12 MCP管理 である。SC 番号順ではなく、SC-17 が SC-12 の前に来る。
- 同節は「モックアップ準拠」である。hi-fi と wireframe の `sc-01.html` の左ナビは、どちらも `ABAC設定 → ユーザー管理 → MCP管理` の順だった。
  `git show origin/main:…/mockups/{hi-fi,wireframe}/sc-01.html | grep -o "ABAC設定\|ユーザー管理\|MCP管理"` で確かめた。
- 本文と 2 種のモックは一致しており、計画の側に誤りは無い。planning への環流は不要である。

## 原因

左ナビの描画順は、`navGroups()`（`src/platform/frontend/src/app/routing/nav.ts`）が `planNavItems` をグループで絞ったときの**配列の順**である。
`planNavItems` は `knowledgeNavItems`（`src/knowledge/frontend/src/features/index.ts`）の写しであり、そこでは `sc12McpClientsNav` が `sc17UsersNav` の前にあった。

## 母集合（規則 9）

`grep -rn "sc12McpClientsNav\|sc17UsersNav\|MCP管理"`（`node_modules` を除く）で引いた。

| ヒット | 扱い |
| --- | --- |
| `knowledge/frontend/src/features/index.ts` の `knowledgeNavItems` | **対象**。並びを入れ替える |
| `platform/frontend/e2e/shell.smoke.spec.ts` の `ADMIN_ONLY_ITEMS` | **対象**。計画の並びに直し、並びの検査を足す |
| 各 feature の nav 宣言・単体試験（`sc17UsersNav.to` など）・`sc12-mcp-clients.smoke.spec.ts` | 対象外。並びに依存しない |
| `locales/*/messages.*` | 対象外。文言は不変（抽出元の行番号だけで、カタログの差分は出ない） |

他のグループ（利用者・個人・運用）とグループの並び（利用者 → 個人 → 管理 → 運用）は、計画と一致していた。上の M-012 の検査で、全グループを一緒に固定する。

## 受け入れ基準

- [x] 3 役割（ロール無し・運用者・システム管理者）で、左ナビのグループの並びと、グループ内の並びが計画と一致する（E2E M-012）
- [x] 直す前の実装では、システム管理者の M-012 が並びの検査で赤になる（陽性対照）
- [x] lint・typecheck・format:check・vitest・該当の Playwright が通る
