---
title: キットの検査器・規約の汎用改善 5 件を取り込みたい（リンク検査・pin 鮮度・検証順序・母集合の規則・status 突合）
type: plan-feedback
status: accepted
category: その他
related_ids: [NFR, IADR-0115, IADR-0170, IADR-0183, IADR-0193]
source_repo: microservices-platform
source_ref: "claude/issue-response-handoff-2hl25v / docs/specs/20260814_issue-736_kit-reflux.md（実装側 issue #736）"
author: Claude（実装）
created: 2026-08-14
dispatched: true
planning_issue: 337
---

# 環流: 検査器・規約の汎用改善 5 件

いずれも `IADR-0115` 決定 2 の固有デルタ 4 種に当たらず、どのリポでも効く改善である
（同 決定 3「汎用的な改善は本リポに留めず環流する」）。

| # | 対象 | 内容 | 出所 |
| --- | --- | --- | --- |
| 1 | `scripts/check-doc-links.js` | ベアファイル名も相対リンクとして検査（`docs/adr/` の §関連 がほぼ無検査だった） | #609 |
| 2 | `scripts/setup.sh` | 計画 pin の鮮度検査（`PIPESTATUS` の罠のコメント付き） | #589 / `IADR-0170` |
| 3 | `docs/DEFINITION_OF_DONE.md` | 検証の順序（staging 前だと新規ファイルが走査から外れる／`check-doc-updated` は HEAD を読む） | `IADR-0183` |
| 4 | `.claude/agents/spec-implementer.md` | 母集合の規則の表（キットは表そのものを持たない。#735 で向きを訂正） | #594 |
| 5 | `scripts/check-feedback-status-sync.js`（新規） | `status` を計画側と突合（同型 2 回目。AST#477 と #737） | #737 / `IADR-0193` |

## 5 の補足

`feedback/README.md` 自身が「この語彙を検査する機械は無い。値の誤りは沈黙する」と明記していた。
写しを持たない記録は対象外（両経路は等価。planning#319）。`--self-test` を持ち fixture で駆動するため、
**`planning` 未 populate の CI でも実効する。**
