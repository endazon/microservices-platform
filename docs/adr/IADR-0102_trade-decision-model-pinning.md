---
title: IADR-0102 取引判断用途（trade-decision）を claude-opus-4-8 でピン留めし、基盤の既定モデル改定から切り離す
type: impl-adr
status: Accepted
related_ids:
  - FR-11
  - ADR-0010
  - ADR-0025
  - IADR-0022
  - IADR-0101
author: claude
created: 2026-07-25
updated: 2026-07-25
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md (取引判断の LLM モデル固定・Accepted)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md (グローバル既定を Opus 5 へ改定・Accepted)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (LLM ゲートウェイ設計・Accepted・本文凍結)"
---

# IADR-0102: 取引判断用途のモデルピン留め

- 状態: Accepted
- 日付: 2026-07-25
- 決定者: claude（実装）

## 起点・関連

- `AST/ADR-0011`（Accepted）の §フォローアップ「基盤 LLM ゲートウェイの取引用途区分に固定モデル ID を
  設定する実装（IADR 起票）」の消化。
- [[IADR-0101]]（ADR-0025 追従・グローバル既定を Opus 5 へ）と対になる決定。本 IADR は
  **ADR-0025 の適用範囲から取引判断用途を除外**することで、2 つの Accepted ADR を同時に満たす。
- 仕様書: `docs/specs/20260725_ast-adr-0011_trade-decision-model-pinning.md`。

## コンテキストと課題

`AST/ADR-0011` は取引判断の再現性・監査可能性のため、取引用途のモデルを `PurposeModels` に
固定指定し基盤のモデル改定に自動追随しないことを決めている。しかしフォローアップが未実施で
エントリが存在せず、AST も `PrimaryModel` / `SecondaryModel` を設定していないため、取引判断は
`DefaultModel` に着地していた。

[[IADR-0101]] がその `DefaultModel` を `claude-opus-4-8` → `claude-opus-5` へ改定したことで、
**取引判断のモデルが基盤の改定に追随してしまう**状態になった。Stage 0（バックテスト）で検証した
モデルと本番モデルが乖離し、`AST/ADR-0011` が守ろうとした性質そのものが失われる。

## 検討した選択肢

1. **`PurposeModels` に `trade-decision` = `claude-opus-4-8` を追加し、`Models` へ同モデルを差し戻す（採用）**
   — 現行の実効モデルを固定するだけで挙動は変わらない。`AST/ADR-0011` の設計（用途別エントリで固定）に忠実。
2. 取引判断も Opus 5 に追随させる — `AST/ADR-0011`（Accepted）に反する。採用するなら Stage 0 再検証と
   新 ADR が必要であり、本作業の範囲を超える。
3. AST 側で `PrimaryModel` / `SecondaryModel` を設定してピン留めする — 明示 `Model` 要求（優先順位 ①）で
   固定できるが、`AST/ADR-0011` §決定が指定する方法（基盤の**取引用途区分**として固定）と異なる。
   また AST の設定に固定モデル ID が散らばり、基盤の `Models` 許可一覧との整合を運用で担保しにくい。

## 決定

- `Llm:Routing:PurposeModels` に **`"trade-decision": "claude-opus-4-8"`** を追加する。
- claude エンドポイントの **`Models` に `claude-opus-4-8` を差し戻す**。
- `report-narrative` はエントリを追加せず `default` 追随のままとする。

## 理由

- **`Models` への差し戻しは必須である。** `LlmRouter.ResolveModel` の用途別解決は
  `eligible.Contains(purposeModel)` を条件とし、`eligible` は `Models`（ZDR 要件区分では `NonZdrModels`
  を除外）である。`Models` に無いモデルをピン留めしても**黙って `DefaultModel` へ落ちる**ため、
  ピン留めが無効化される。[[IADR-0101]] は `Models` から `claude-opus-4-8` を除去していたため差し戻す。
  これは [[IADR-0101]] の受け入れ基準 3 を意図的に改めるものだが、`Models` は「そのエンドポイントで
  **利用を許可する**モデル集合」であり、グローバル既定が何かとは独立の概念である。
- **`report-narrative` を対象外とするのは仕様どおり**である。`AST/ADR-0011` §決定は「報告書生成の LLM は
  別扱い。基盤の既定モデルを用いてよい」と明記しており、`default` 追随が正しい。
- 現行の実効モデル（`claude-opus-4-8`）をそのまま固定するため、**取引の挙動は変わらず Stage 0 再検証は不要**である。
  版数を上げる場合のみ再検証を要する（下記）。

## 結果

- 良い影響: `AST/ADR-0011`（Accepted）の未実施フォローアップが消化され、取引判断が基盤のモデル改定から
  切り離される。ADR-0025（既定 Opus 5）と `AST/ADR-0011`（取引は固定）が両立する。
- 悪い影響 / トレードオフ:
  - 取引判断は基盤の最新モデルの性能向上を即時には享受できない（`AST/ADR-0011` が受け入れたトレードオフ）。
  - `Models` に Opus 4.8 と Opus 5 が併存し、許可一覧が 1 つ増える。用途別エントリと許可一覧の
    整合を運用で維持する必要がある（不整合時は黙って `DefaultModel` へ落ちるため、テスト T-15 で固定した）。
  - **本エントリの更新には Stage 0 再検証が要る。** `AST/ADR-0011` §決定「モデルを更新する場合は、新モデルで
    Stage 0（コスト2倍・ウォークフォワード・DSR/PBO 補正）を再実行し、エッジが維持されることを確認してから
    採用する。更新は月報レビュー時のみ」に従う。**設定値の書き換えだけで更新してはならない。**
- フォローアップ:
  1. **二段判断のモデル分離**: `AST/ADR-0011` は本判断とスクリーニングの両方を対象とするが、AST 実装は
     両段とも同一 purpose（`trade-decision`）で送信するため、現状は 1 エントリで両段が同一モデルに固定される。
     段ごとに別モデルを充てるなら AST 側で purpose を分けるか `PrimaryModel`/`SecondaryModel` を設定する。
  2. Opus 4.8 の提供終了時期の監視（固定である以上、陳腐化・提供終了への追従計画が要る）。
     `AST/ADR-0011` は「モデルを一切更新しない」選択肢を長期的に運用不能として退けている。

## 関連

- Supersedes: なし（[[IADR-0101]] の受け入れ基準 3〔`Models` から opus-4-8 を除外〕のみを改める）
- Superseded by: なし
- 関連要求 / UC: FR-11（LLM 送信可否の統制）、`AST/ADR-0011`・`AST/FR-04`（AI 判断のガードレール）
