---
title: 計画 ADR-0006 改名への参照追随（夜間 doc-links 復旧）とクロスリポ issue 修飾規約の明文化
type: spec
status: done
related_ids:
  - NFR
  - ADR-0006
  - IADR-0110
  - IADR-0114
author: claude
created: 2026-08-01
updated: 2026-08-01
related_specs:
  - "../adr/IADR-0110_llm-completion-stop-reason-metrics.md"
  - "./20260728_issue-395_refusal-metrics.md"
  - "../adr/IADR-0114_anthropic-unknown-content-block-sanitizing.md"
---

# 仕様書: 計画 ADR 改名への参照追随とクロスリポ issue 修飾規約（issue #430）

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#430](https://github.com/endazon/microservices-platform/issues/430)
  （夜間 `doc-links (planning)` が 2026-07-29 から 3 夜連続で失敗）。
- 原因: planning 側で `ADR-0006_observability.md` が
  `ADR-0006_observability-otel-prom-loki.md` へ改名され、本リポの参照 2 件が未追随。
- 関連: PR [#431](https://github.com/endazon/microservices-platform/pull/431) レビューでの
  クロスリポ issue 番号（`Refs #290` が AST#290 のつもりで MSP の無関係 PR #290 を指した）
  誤帰属の実例（IADR-0114 の作業）。

## 対応内容

1. **破損リンク 2 件の是正**（#430 の受け入れ基準）:
   - `docs/adr/IADR-0110_llm-completion-stop-reason-metrics.md` の `plan_refs`
   - `docs/specs/20260728_issue-395_refusal-metrics.md` の計画リンク
   いずれも `ADR-0006_observability.md` → `ADR-0006_observability-otel-prom-loki.md`。
   リポ全体を走査し、旧ファイル名への参照が他に無いことを確認済み。
2. **クロスリポ issue / PR 番号の修飾規約**を `.claude/rules/traceability.md` へ追記する。
   裸の `#NNN` は GitHub 上で本リポの issue / PR へ自動リンクされるため、他リポの番号は
   `AST#NNN` / `planning#NNN` と修飾する（PR #431 でオーナーが表明した運用の規約化）。

## 対象外（#430 の任意項目・別途判断）

- 夜間ジョブ失敗時の通知導線（issue 自動起票等）: `.github/workflows/` は GitHub App 権限で
  編集不可のため本 PR では扱わない（CLAUDE.md「CI」節）。
- ファイル名依存参照の恒久対策（ID ベース解決）: #430 自身が「要るかは別途判断」と保留中。
  計画側の改名運用ルールは planning へフィードバック済み
  （`draft/feedback/20260801_adr-file-rename-downstream-refs.md`）。

## 受け入れ基準

- [ ] リポ内に `ADR-0006_observability.md`（旧名）への参照が残っていない。
- [ ] 修正後の参照先が planning に実在する（`ADR-0006_observability-otel-prom-loki.md`）。
- [ ] `.claude/rules/traceability.md` にクロスリポ issue 修飾の規約が明文化されている。
