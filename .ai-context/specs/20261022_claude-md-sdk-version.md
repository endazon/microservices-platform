---
title: 作業仕様書 — CLAUDE.md と README の global.json の SDK 版を実物（8.0.100）に合わせる
type: spec
status: done
related_ids: [NFR, IADR-0581]
author: claude
created: 2026-10-22
updated: 2026-10-22
---

# 作業仕様書 — CLAUDE.md と README の global.json の SDK 版を実物に合わせる

> 本仕様書は実装着手前に作成した（2026-10-22）。文書の版の表記だけを直し、`global.json` 自体は変えない。

## 起点

- #2067（IADR-0581 決定 4）で `global.json` の `sdk.version` は `8.0.0` から `8.0.100` へ変わった（`git show origin/develop:global.json` で確認: `"version": "8.0.100"`, `"rollForward": "latestMajor"`）。
- CLAUDE.md「技術スタック別ルール > C# / .NET」は `8.0.0` のまま残っている。**CLAUDE.md の編集は利用者が 2026-10-22 に明示承認済み（範囲はこの 1 語のみ）。**

## 母集合（規則 9・10）

`git grep -n '8\.0\.0'`（`.ai-context/specs`・`.ai-context/superpowers`・lock ファイル・`Version="…"` を除く）で引いた。

| 箇所 | 扱い | 理由 |
| --- | --- | --- |
| `CLAUDE.md` 113 行 | 直す（`8.0.0` → `8.0.100` の 1 語のみ） | live。利用者承認の範囲 |
| `README.md` 109 行 | 直す（同） | live な文書で同じ誤り |
| `.ai-context/adr/IADR-0048` 39・49 行 | 直さない | 凍結記録（当時の事実として正しい。後継の判断は IADR-0581 が持つ） |
| `.ai-context/adr/IADR-0581` 42 行 | 対象外 | 変更前の値としての引用 |
| `docs/how-to/local-development.md`・`docs/tech/tech-requirements.md` | 対象外 | 既に `8.0.100` |

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| AC1 | CLAUDE.md の差分が `8.0.0` → `8.0.100` の 1 語だけ | `git diff --word-diff CLAUDE.md` |
| AC2 | live な文書に `global.json` の旧版 `8.0.0` が残らない | 上の母集合の grep |
| AC3 | 文書系検査（必読予算を含む）が緑 | `check-reading-budget.js` ほか |
