---
title: 取引判断用途（trade-decision）のモデルを claude-opus-4-8 でピン留めする（AST/ADR-0011 追従）
type: spec
status: done
related_ids:
  - FR-11
  - ADR-0010
  - ADR-0025
  - IADR-0022
  - IADR-0101
  - IADR-0102
author: claude
created: 2026-07-25
updated: 2026-07-25
related_specs:
  - "../adr/IADR-0102_trade-decision-model-pinning.md"
  - "../adr/IADR-0101_default-model-opus-5.md"
  - "./20260724_adr-0025_default-model-opus-5.md"
  - "../functional/FR-11_llm-egress-routing.md"
  - "../tests/FR-11_llm-egress-routing.md"
---

# 仕様書: 取引判断用途のモデルピン留め（AST/ADR-0011 追従）

## 起点となる計画書（トレーサビリティ）

- 計画根拠: `AST/ADR-0011`
  （[取引判断の LLM はモデルバージョンを固定し、基盤のモデル改定に自動追随しない](../../planning/projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md)・**Accepted**・2026-07-23）。
  同 §決定は「取引判断サービスの本判断モデルと、二段判断のスクリーニングモデルは、**明示的なモデルバージョン
  （モデル ID）でピン留め**する。基盤 LLM ゲートウェイの取引用途区分（`PurposeModels` の取引判断用エントリ）
  として固定指定し、基盤の定型 RAG 層のモデル改定には**自動追随しない**」と定める。
  §フォローアップ「基盤 LLM ゲートウェイの取引用途区分に固定モデル ID を設定する実装（IADR 起票）」が本作業である。
- 併走する決定: [ADR-0025](../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md)
  （グローバル既定を Opus 5 へ改定）と [[IADR-0101]]（その実装追従）。本作業は ADR-0025 の適用範囲から
  **取引判断用途を除外する**ことで、両 Accepted ADR を同時に満たす。
- 要求: FR-11（LLM 送信可否の統制・用途別ルーティング）。
- 本作業の実装判断は [[IADR-0102]]。

## 背景と問題

`AST/ADR-0011` のフォローアップは**未実施**であり、`Llm:Routing:PurposeModels` に取引用途のエントリが無い。
一方 AST の `TradeDecisionService` は `purpose = "trade-decision"` を送信し、`PrimaryModel` /
`SecondaryModel` は未設定（`null`）である。

ルーターのモデル解決優先順位は次のとおり（`LlmRouter.ResolveModel`）。

1. 明示 `Model` 要求が**適格**なら採用
2. `PurposeModels[purpose]` が**適格**なら採用
3. エンドポイントの `DefaultModel` が適格なら採用
4. 適格モデルの先頭

したがって取引判断は現在 ③ の `DefaultModel` に着地する。[[IADR-0101]] がその `DefaultModel` を
`claude-opus-4-8` → `claude-opus-5` に改定したため、**取引判断のモデルが基盤の改定に自動追随してしまい、
`AST/ADR-0011` に反する**。Stage 0（バックテスト）で検証したモデルと本番モデルが乖離し、再現性・監査可能性
（`AST/ADR-0003`・`AST/ADR-0008`）が失われる。

なお報告書生成（`purpose = "report-narrative"`）は `AST/ADR-0011` §決定「**報告書生成の LLM は別扱い**。
基盤の既定モデルを用いてよい」により `default` 追随が仕様上正しく、**ピン留めの対象外**である。

## 重要な制約: `PurposeModels` の値は `Models` 許可一覧に含まれる必要がある

`ResolveModel` の ② は `eligible.Contains(purposeModel)` を条件とし、`eligible` は
エンドポイントの `Models`（ZDR 要件区分では `NonZdrModels` を除外したもの）である。

つまり **`claude-opus-4-8` を claude エンドポイントの `Models` に含めない限り、`trade-decision` の
ピン留めは無効化され、③ の `DefaultModel`（= `claude-opus-5`）へ黙って落ちる**。
[[IADR-0101]] は `Models` から `claude-opus-4-8` を除去したため、本作業で**差し戻す**必要がある。

これは `docs/specs/20260724_adr-0025_default-model-opus-5.md` の受け入れ基準 3
（「`claude-opus-4-8` は含まれない」）を意図的に改める変更である（同仕様書に注記を追加する）。
`Models` は「そのエンドポイントで**利用を許可する**モデル集合」であり、グローバル既定が何かとは独立である。

## 受け入れ基準

1. `PurposeModels` に `trade-decision` = `claude-opus-4-8` が追加されている。
2. claude エンドポイントの `Models` に `claude-opus-4-8` が含まれる（①の制約）。
3. 用途 `trade-decision` のルーティングが `claude-opus-4-8` を返す（`default` へ落ちない）。
4. ZDR 要件区分（`confidential` / `restricted`）の `trade-decision` でも `claude-opus-4-8` が選択される
   （Opus 4.8 は ZDR 対応であり `NonZdrModels` に含まれないため）。
5. 他用途（`default` / `rag-answer` / `diagram-coding` / `analysis`）の解決結果が不変である。
6. `report-narrative` はエントリを追加せず `default`（= `claude-opus-5`）に着地する（`AST/ADR-0011` §決定どおり）。
7. `dotnet build` / `dotnet test` / `dotnet format` が通る。

## 対応方針（変更範囲）

- `src/platform/.../LlmGateway.Api/appsettings.json`
  - `Llm:Routing:PurposeModels` に `"trade-decision": "claude-opus-4-8"` を追加
  - claude エンドポイントの `Models` に `claude-opus-4-8` を差し戻し
- テスト `LlmRouterTests`: フィクスチャ（`Claude()` の `Models`・`Opts()` の `PurposeModels`）を実設定に追従させ、
  用途 `trade-decision` のピン留めを public / confidential の両区分で固定する。
- 仕様書: `docs/functional/FR-11_llm-egress-routing.md`（用途別モデル解決・既定設定）、
  `docs/tests/FR-11_llm-egress-routing.md`（T-15）、
  `docs/specs/20260724_adr-0025_default-model-opus-5.md`（受け入れ基準 3 の改定注記）。

## リスクと自己チェック

- **ピン留めが黙って無効化される罠**: `Models` への差し戻しを忘れると `default` へ落ちる。テスト（T-15）で固定する。
- **二段判断のモデル分離**: `AST/ADR-0011` は本判断とスクリーニングの**両方**をピン留め対象とするが、AST 実装は
  両段とも同一 purpose（`trade-decision`）で送信し `PrimaryModel`/`SecondaryModel` は未設定である。
  よって現状は 1 エントリで両段を固定できる。段ごとに別モデルを充てる場合は AST 側で purpose を分けるか
  `PrimaryModel`/`SecondaryModel` を設定する必要がある（[[IADR-0102]] フォローアップ）。
- **モデル更新の経路**: 本エントリの更新は `AST/ADR-0011` §決定により **Stage 0 再検証を経る**。設定値の
  書き換えだけで更新しない旨を [[IADR-0102]] に明記する。
- **ZDR**: Opus 4.8 は `NonZdrModels` に含まれず、ZDR 要件区分でも選択可能。T-13 の意味は不変。

## 非対象・除外

- `report-narrative` のピン留め（`AST/ADR-0011` §決定により `default` 追随が正しい）。
- 取引判断モデルの**版数変更**（Stage 0 再検証を要する意思決定であり、本作業は現行挙動の固定のみ）。
- `rag-answer` の Sonnet 5 追随（`ADR-0022` フォローアップ・別作業）。
- AST 側の実装変更（本リポジトリ外。`MaxTokens` 引き上げは `AST/IADR-0101` で対応済み）。

## 検証

- `dotnet build src/platform/backend/backend.slnx` / `dotnet test src/platform/backend/backend.slnx`
- `dotnet format --verify-no-changes`
- `LlmRouterTests` の `trade-decision` 系（T-15）と既存の用途別・ZDR 系が通ること
