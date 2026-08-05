---
title: 図コード化の LLM 一時障害時の扱い（再試行 vs 画像保持へ縮退）
type: plan-feedback
status: accepted
category: UC/画面の差異
related_ids:
  - FR-12
  - UC-06
  - ADR-0012
  - ADR-0010
source_repo: microservices-platform
source_ref: "PR #49 / branch claude/issue-26-20260703-0230 / docs/specs/20260703_FR-12_document-normalization-pipeline.md"
author: claude
created: 2026-07-03
updated: 2026-08-05
---

# フィードバック: 図コード化の LLM 一時障害は再試行か、画像保持へ縮退か

## 種別

UC/画面の差異（例外フローの実装乖離）。計画書（業務フロー draft）と実装の縮退方針が一致していない。

## 起点となる計画書

- 機能要求（FR）: FR-12（原本の正規化変換）
- ユースケース（UC）: UC-06（変換）
- 画面（SC）: なし
- 関連 ADR: ADR-0012（変換パイプライン）、ADR-0010（LLMゲートウェイ）
- 計画書リンク:
  - `planning/projects/microservices-platform/04_workflows/03_conversion-flow.md`（「補足・例外処理」）
  - `planning/projects/microservices-platform/07_adr/ADR-0012_conversion-pipeline.md`

## 現状（計画書の記述 / As-Is）

`04_workflows/03_conversion-flow.md` の「補足・例外処理」に次の記述がある。

> **再試行**: 変換失敗（pandocエラー・LLM一時障害）は再試行する。継続失敗はデッドレターキューへ送り、管理者に通知する。

すなわち、**LLM の一時障害も再試行対象**として一律に扱っている。

## 問題点 / あるべき姿（To-Be）

図コード化（LLM 呼び出し）は本質的に「できなくても画像保持でパイプラインを完了できる」処理であり、
pandoc／ストレージ保存の恒久失敗（＝本文そのものが作れない）とは失敗の意味が異なる。

実装（[IADR-0008](../docs/adr/IADR-0008_conversion-ports-deny-by-default-and-idempotent-id.md)）では、
ADR-0012 の「段階的に全面コード化（当面は画像保持を許容）」方針を優先し、図コード化の
**呼び出し失敗・送信拒否・コード化不能をすべて画像保持へ縮退（deny-by-default）**させ、
その図についてはメッセージ再試行を発火させない設計にした。デッドレター送りは pandoc／保存の恒久失敗に限定する。

あるべき姿の候補は次のいずれか。

- (a) 計画書を実装に合わせ、「**図コード化の LLM 障害は画像保持へ縮退**（再試行しない）／再試行・デッドレターは
  本文変換（pandoc）・資産保存の恒久失敗に限定」と明確化する。
- (b) 「図コード化も一定回数まで再試行し、使い切ったら画像保持へ縮退」とする折衷案を計画側で採るなら、
  再試行境界（図単位 or メッセージ単位）と最大回数を定義する。

## 実装で判明した経緯

PR #49（FR-12 正規化変換パイプライン）の実装・AIコードレビューで、計画書の「LLM 一時障害は再試行」と
実装の「画像保持へ縮退」の乖離が指摘された（`LlmGatewayDiagramCoder.CodeAsync` は例外を送出せず縮退）。
作業仕様書（`docs/specs/20260703_FR-12_...md`）には意図的な設計判断として記載済みだが、計画書側は未更新。

## 提案（計画への反映案）

- 反映先候補: UC・業務フロー更新（`04_workflows/03_conversion-flow.md` の例外処理）＋必要なら ADR-0012 追記
- 提案内容: 上記 (a) を推奨。例外処理を「本文変換・保存の恒久失敗＝再試行→デッドレター」「図コード化の
  一時障害・拒否・不能＝画像保持へ縮退（人手補正で後日再登録）」に分けて明記する。

## 影響範囲

- ConversionService の再試行／デッドレター設計（`Program.cs` の `UseMessageRetry`）。
- UC-06 代替フロー（人手補正・再登録）の位置づけ（縮退した図の後日コード化）。
- 監視・アラート方針（画像保持へ縮退した図の可観測性）。

## ［2026-08-05 追記 / #497］計画側の実態へ status を同期した

**判定: accepted。** 提案 (a)（再試行と縮退を分けて明記する）が計画書へ反映済みであり、本記録を根拠に ADR-0012 が `Accepted` 化されている。

確認は planning submodule pin `d980a01` に対して行った（**行番号は pin が動くとずれるため内容で特定する**）。

| 確認先（計画リポジトリ） | 確認した記述 |
| --- | --- |
| [draft/feedback/20260703_conversion-retry-vs-image-fallback.md](../planning/draft/feedback/20260703_conversion-retry-vs-image-fallback.md) | `status: accepted`（「トリアージ結果」節が判定と反映先を列挙） |
| [04_workflows/03_conversion-flow.md](../planning/projects/microservices-platform/04_workflows/03_conversion-flow.md) `:65-67` | §補足・例外処理 が **再試行（本文変換・資産保存）／縮退（図コード化）／人手補正** の 3 項へ分割済み |
| [07_adr/ADR-0012_conversion-pipeline.md](../planning/projects/microservices-platform/07_adr/ADR-0012_conversion-pipeline.md) `:41` | §結果 に「**失敗時の縮退方針**」として同内容を明記 |
| 同 `:43` | 「確定の経緯」が**本記録を相対リンクで参照**し、縮退方針の明確化をもって `Accepted` 化したと記す |
| [03_usecases/01_usecases.md](../planning/projects/microservices-platform/03_usecases/01_usecases.md) `:163` | UC-06 例外フローが「図コード化（LLM）の失敗は画像保持へ縮退」へ整合済み（変更履歴 `:272` が本記録を根拠に挙げる） |

作業仕様書: [docs/specs/20260805_issue-497_feedback-status-sync.md](../docs/specs/20260805_issue-497_feedback-status-sync.md)（#497）
