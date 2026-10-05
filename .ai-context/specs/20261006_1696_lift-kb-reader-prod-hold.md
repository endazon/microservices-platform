---
title: 作業仕様書 — AST の KB の読み手のポリシーの本番への投入の保留を解く（計画 ADR-0125 フォローアップ 2。#1696）
type: spec
status: done
related_ids:
  - NFR-09
  - FR-05
  - ADR-0125
  - IADR-0500
  - IADR-0492
author: claude
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0125_ast-kb-reader-static-project-policy-with-confidentiality-ceiling.md 決定 4・決定 5（暫定手段）・フォローアップ 2
related_specs:
  - 20261006_1755_ast-kb-reader-confidentiality-cap
issue: "#1696"
---

# 作業仕様書 — AST の KB の読み手のポリシーの本番への投入の保留を解く（#1696）

## 目的と射程

計画 ADR-0125 決定 5 の暫定手段は「本番へは、決定 2 の上限を入れた改定が入るまで投入しない」であり、フォローアップ 2 は
「上の改定が入った後で、本番への投入の保留を解く」である。上限の改定は #1757（IADR-0500。`11c13cae`）で develop へ入った。
本作業は、運用仕様書の保留の注記を解き、本番へ投入するときの前提（順序）を書く。

**射程**: 運用仕様書の注記の書き換えと、保留に言及する凍結記録（IADR-0492 残余 5・IADR-0500 残余 1）への日付つき追記。
**射程外**: 本番への実際の投入（システム管理者の配備作業。本セッションは稼働中の環境に触れない）、本番の NetworkPolicy（#1756。ADR-0125 決定 4）、PoC（#1696）。

## 解く条件の確認

| 条件（ADR-0125） | 状態 | 根拠 |
| --- | --- | --- |
| 決定 2 の上限を入れた改定が入る | 満たす | #1757 マージ（`11c13cae`）。seed・手順書の本文・保存時の検証の例外（IADR-0500 決定 1〜4） |
| 決定 4（NetworkPolicy）は別件 | 前提にしない | 決定 4 は本 ADR で定めない。投入しても ingress が無ければ読み手の検索は届かない（害は無い）。#1756 |
| PoC | 前提にしない | フォローアップ 4 は独立。dev の seed で確かめる（#1696） |

## 書く前提（投入の順序）

1. authorization-service が `11c13cae` 以降であること。より前の版では 2 キーの文書の条件が保存時に 400 になる（上限なしの形で入れ直さない）。
2. 投入する本文は手順書の JSON だけ（上限を外した形で入れない）。
3. 投入しても、本番の NetworkPolicy（#1756）が入るまでは読み手の検索は届かない。

## 母集合（規則 9・10）

- 誤りの側の文字列で走査: `git grep -n "保留" -- docs deploy .ai-context/adr` のうち本件に関わるもの →
  `docs/operations/operations.md`（注記本体）、`.ai-context/adr/IADR-0500_*.md`（残余 1・統制の表）、`.ai-context/adr/IADR-0492_*.md`（残余 5）。
  `deploy/local/abac-seed/README.md`・`docs/security/security.md` は本件の保留に言及しない（実測）。
- 新たに誤りになる自分の記述: IADR-0500 統制の表の「本番は保留」→ 日付つき追記で解除を記す（本文は凍結）。

## 受け入れ基準

- 運用仕様書の保留の注記が、解除の記録と投入の前提 3 点へ置き換わっている。
- IADR-0492 残余 5・IADR-0500 残余 1 に日付つき追記があり、本文は改変していない。
- `check-trace-blocks`・`check-doc-updated`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`scripts.test.js` が通る。
