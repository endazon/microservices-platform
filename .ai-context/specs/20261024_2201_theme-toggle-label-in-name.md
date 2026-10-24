---
title: 作業仕様書 — テーマ切替ボタンの名前を表示文言で始める（WCAG 2.5.3。axe 4.14 で a11y の E2E が全面赤）
type: spec
status: done
related_ids: [NFR-30, ADR-0031, IADR-0442]
author: claude
created: 2026-10-24
updated: 2026-10-24
issue: "#2201"
---

# 作業仕様書 — テーマ切替ボタンの名前（#2201）

> 本仕様書は実装着手前に作成した（着手 2026-10-24）。

## 事象

- develop の `17211125` は、`@axe-core/playwright` を 4.13.0 → 4.14.0 へ上げた（#2157）。この版から、`a11y.smoke.spec.ts` が 21 件すべて赤になる（手元で再現した）。
- 違反は `label-content-name-mismatch`（WCAG 2.5.3 Label in Name）で、対象は共通シェルのヘッダのテーマ切替ボタンである。
  - 名前は「表示テーマの切替。現在は システム、押すと ダーク になります」、見えている文言は「表示: システム」で、名前が見えている文言を含まない。
  - 全画面にヘッダがあるので、全面が赤になる。

## 受け入れ基準

- [x] 違反そのものを消す。既知違反の台帳（指紋）には逃がさない。
- [x] 名前は見えている文言で始まる。「何のボタンか」と「押した後」は後ろに足す。
- [x] Lingui の ja / en を追随させ、単体試験を追随させる（3 状態それぞれで、名前が表示文言で始まる）。
- [x] 直す前は赤（21 件）、直した後は `a11y.smoke.spec.ts` を含む全 Playwright が緑。vitest も通る。

## 判断

- 名前は `{表示文言}（表示テーマの切替。押すと {次} になります）` とする。
  - 現在の状態は、見えている文言（「表示: システム」）が既に示している。「現在は …」を重ねて言わない。
  - en は `{visibleLabel} (switch the display theme; press to change it to {upcomingName})` とした。表示文言（Theme: System 等）で始まる。
- 初期ロードは 20 B 減った（644,788 → 644,768）。ラチェットの床を下げた（理由は `$comment`）。

## 母集合（規則 9・10）

- 誤りの側の文字列は「表示テーマの切替。現在は」「現在は システム」とした。src・docs を走査した。
  - 該当は `ThemeToggle.tsx`・その単体試験・en / ja のカタログだけで、いずれも追随した。E2E は名前を引いていない。

## 検証

- 全 Playwright（CI=1）120 passed。vitest 2026 passed。
- lint はエラー 0（警告 12 件は develop と同数）。typecheck・format:check・`check-i18n-catalogs` も通った。
- `check-chunk-budget.js --require` OK。`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 1180 passed。
