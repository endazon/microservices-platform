---
title: 作業仕様書 — AST の採番宣言を SC-01..04 へ前進させる（planning#594 で SC-04 が新設されたことへの追随）
type: spec
status: done
related_ids: [NFR, ADR-0093, ADR-0048, IADR-0423, IADR-0228]
author: endazon (with Claude Code)
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
  - planning:projects/ai-stock-trading/10_feedback/20260909_opend-auth-screen-sc04.md
  - planning:tools/doc-checks/kg-ranges.json
  - planning:projects/microservices-platform/07_adr/ADR-0093_plan-id-ranges-derived-and-published.md
---

# 作業仕様書 — AST の採番宣言を `SC-01..04` へ前進させる

> 対象: 計画リポジトリの環流 planning#594（利用者裁定 2026-09-09）で **AST に `SC-04`（OpenD 認証操作画面）が新設された**ことへの追随。**本リポジトリ側は宣言の 1 行だけである。**

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（メタ作業。NFR）
- ユースケース（UC）/ 画面（SC）: なし（**他プロジェクト（AST）の SC 採番の宣言であり、本リポジトリの画面ではない**）
- 関連 ADR: `ADR-0093`（計画 ID レンジは実物から導出して公開し、実装リポジトリはそれへ追随する）／`ADR-0048` 決定 2（planning 依存の禁止）
- 関連 IADR: `IADR-0423`（公開 `kg-ranges.json` との突合）／`IADR-0228`（planning 依存の撤去。pin は復活させない）

## 目的・背景

`.claude/rules/traceability.repo.md`「複数プロジェクトを跨ぐ場合の ID 修飾（固有設定）」節は、**AST の採番レンジを宣言している**。計画側で `SC-04` が採番されたため、この宣言が実物より遅れた。

**宣言が遅れている間、`AST/SC-04` を引く記述は「実在しない ID」に見える。** `ADR-0093` 決定 1 が定めるとおり、**実物を持つのは planning 側であり、実装リポジトリは公開 `kg-ranges.json` へ追随する側である。**

### 実測（2026-09-09。計画リポの `node tools/doc-checks/gen-plan-ranges.js`）

| 種別 | 本リポの宣言 | 計画側の実物 |
| --- | --- | --- |
| FR | `FR-01..21` | [1, 21] ✅ |
| UC | `UC-01..07` | [1, 7] ✅ |
| **SC** | **`SC-01..03`** | **[1, 4]** ❌ |
| ADR（AST） | 宣言なし | [1, 37]（本リポは AST の ADR レンジを宣言していない） |

## 母集合の引き直し（規則 9・10）

**誤りの側の文字列で全文書を走査した**（`--exclude-dir` のみで絞り、拡張子・行フィルタは使わない）。

```
grep -rn 'SC-01\.\.03\|UC-01\.\.07' --include=* .   （.git / node_modules を除外）
```

| ヒット | 扱い | 理由 |
| --- | --- | --- |
| `.claude/rules/traceability.repo.md:15` | **是正する** | **live な宣言そのもの**（`check-trace-blocks.js` / `check-commit-messages.js` の一次情報） |
| `scripts/check-trace-blocks.js:38` ／ `scripts/check-test-traceability.js:76` | 是正しない | **経緯を述べる doc コメント**であり、当時の宣言を史実として引いている |
| `scripts/scripts.repo.test.js:1490` ／ `scripts/check-test-traceability.js:442` | 是正しない | **パーサの試験固定値**（`FR-01..20` 系の古い文字列）。「AST のレンジを拾わないこと」を固定する入力であり、**実物へ揃える対象ではない**。揃えると試験の意図が消える |
| `.ai-context/specs/` の 4 件 | 是正しない | **確定済みの凍結記録**（本文を後から書き換えない） |

**軸を 1 本で終わらせていない** —— 上記に加え `SC-04` / `opend-auth` / `OpenD 認証` でも走査し、**本リポジトリに追随先は他に無い**ことを確認した（0 件）。

## 変更内容

- `.claude/rules/traceability.repo.md`: AST の採番宣言を `SC-01..03` → **`SC-01..04`** へ。

## 受け入れ基準

- [x] 宣言が計画側の実物（`kg-ranges.json` の `ai-stock-trading.SC = [1, 4]`）と一致する
- [x] `node scripts/check-trace-blocks.js --self-test` が緑（AST のレンジを計画 ADR レンジとして拾わない性質が保たれている）
- [x] `node scripts/check-test-traceability.js --self-test` が緑
- [x] `node scripts/check-reading-budget.js` が予算内（変更は 1 文字）

## 射程外

- **`kg-ranges.json` そのものは計画リポジトリの成果物である。** 本リポジトリからは書かない（`ADR-0093` 決定 1）。
- **planning 依存を復活させない**（`ADR-0048` 決定 2 / `IADR-0228`）。本作業は**手で追随する 1 行**であり、pin も submodule も増やさない。
