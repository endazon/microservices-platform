---
title: Analysis ドメインの BFF エンドポイント＋DTO を knowledge へ移設する（Issue #229・IADR-0063 step3）
type: spec
status: done
related_ids:
  - FR-14
  - IADR-0056
  - IADR-0059
  - IADR-0063
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
related_specs:
  - "../adr/IADR-0063_bff-unit-endpoint-composition.md"
  - "./20260711_issue-229_migrate-datasource-bff.md"
  - "../../src/README.md"
---

# 仕様書: Analysis ドメインの BFF 移設（Issue #229・IADR-0063 step3）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-04 / FR-07（AI 分析）／FR-14（疎結合ユニット）／IADR-0056（依存方向・例外3）／IADR-0059（契約階層化）
- 実装判断: [IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md)（BFF 合成方式 A・段階実装 step3「ドメイン単位移設」）
- Issue: #229（step3・第2ドメイン=Analysis）。第1ドメイン DataSource は #247（[20260711_issue-229_migrate-datasource-bff](./20260711_issue-229_migrate-datasource-bff.md)）で完了。

## 目的・背景

[IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md) step3 の 2 ドメイン目。**Analysis** の BFF エンドポイント集約と DTO を platform から knowledge
ユニットへ同時移設し、合成点から例外3 で参照する（登録簿は不変）。Analysis は `BffScopeResolver` に依存せず、
自ドメイン DTO（`AnalysisTaskType`/`AnalysisDataRange`/`AnalysisTaskRequest`）が他ドメインと結合しないため独立に移設可能。

## 対象範囲

- 移設（`Knowledge.Bff.Endpoints` は #247 で新設済み。本 PR は既存プロジェクトへ追加）:
  - `AnalysisBffEndpoints.cs`: `Platform.Bff/Foundation/Endpoints/` → `knowledge/backend/Bff/Knowledge.Bff.Endpoints/`。
    namespace `Platform.Bff.Foundation.Endpoints` → `Knowledge.Bff.Endpoints`。ASP.NET Core 明示 using 追加。
    BFF ローカル record `AnalysisRequest`（ファイル末尾）も同梱移設。
  - `AnalysisDto.cs`（`AnalysisTaskType`/`AnalysisDataRange`/`AnalysisTaskRequest`）: `Platform.Shared.Contracts/Dtos/`
    → `Knowledge.Contracts/Dtos/`。namespace `Platform.Shared.Contracts.Dtos` → `Knowledge.Contracts.Dtos`。
- 合成点: `BffEndpointComposition.cs` の登録簿は不変（`using Knowledge.Bff.Endpoints;` は #247 で追加済み。
  `MapAnalysisBffEndpoints` が移設先の拡張メソッドを解決）。`Platform.Bff.csproj` の例外3 参照も #247 で追加済み。
- 参照追随:
  - **後段サービス `AiAnalysisService`**: 移設 DTO を参照する 9 ファイルの using を更新（Analysis 型のみ利用の
    ファイルは `Platform.Shared.Contracts.Dtos` → `Knowledge.Contracts.Dtos` へ差し替え、`AiAnswerDto`/`AccessScope*`/
    `Completion*` 等 platform 横断 DTO も併用するファイルは `Knowledge.Contracts.Dtos` を追加し双方を残す）。
    `AiAnalysisService.Api.csproj` に `Knowledge.Contracts` の ProjectReference を追加（ユニット内参照）。
  - `Platform.Bff.Tests`（`AnalysisBffEndpointTests`）は匿名オブジェクト POST＋`AiAnswerDto`（platform 残置）
    のみ利用のため**変更不要**。`BffTestFactory` も Analysis 型を参照しないため不変。

## 依存方向

- `Platform.Bff` → `Knowledge.Bff.Endpoints`（例外3・#247 で行使済み）。本 PR で Analysis モジュールも同経路に載る。
- `Knowledge.Bff.Endpoints`（AnalysisBffEndpoints）→ `Platform.Shared.Contracts`（`AiAnswerDto`）は
  knowledge→platform Shared＝許可。`AiAnswerDto`/`SearchResultDto` は Search 移設時に `Knowledge.Contracts` へ移り、
  以降は knowledge 内参照へ収束する。
- `AiAnalysisService` → `Knowledge.Contracts`（ユニット内）／`Platform.Shared.Contracts`（Shared 例外）はいずれも許可。

## 受け入れ基準（Issue #229）との対応

- [~] 可変ユニット追加時に platform 契約・BFF を改修せず（または合成点 1 箇所のみで）拡張できる
  → Analysis について BFF エンドポイント＋DTO を knowledge へ移し合成点経由に。残ドメイン（Conversion/Dashboard+Feedback/
  Document/Search）は後続スライス。`Refs #229`。

## 検証

- `dotnet build`（platform / knowledge）→ 0 エラー。
- `dotnet test Platform.Bff.Tests` → 96 pass / 1 skip（合成点・Analysis BFF 含む）。
- `dotnet test AiAnalysisService.Api.Tests` → 32 pass（DTO 参照追随後も緑）。
- `dotnet format --verify-no-changes`（両 slnx）→ 差分なし。
- `node scripts/check-unit-dependencies.js` → 違反 0。`node scripts/check-doc-links.js` → 破損 0。
- API 経路（`/bff/analysis/*`）は不変（コード所在のみ移動）。

## 実装判断・フォローアップ

- 移設パターン（後段サービスの DTO 参照を `Knowledge.Contracts` へ追随・BFF テストは推移参照）を Analysis へ適用。
- 残ドメイン: Conversion（独立）／Dashboard+Feedback（`FeedbackStatsDto` 結合のため 1 PR）／Document・Search
  （`BffScopeResolver` の Shared 切り出しが前提）。全移設完了の最終 PR で `Closes #229`。
