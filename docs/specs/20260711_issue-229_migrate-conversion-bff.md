---
title: Conversion ドメインの BFF エンドポイント＋DTO を knowledge へ移設する（Issue #229・IADR-0063 step3）
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
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
related_specs:
  - "../adr/IADR-0063_bff-unit-endpoint-composition.md"
  - "./20260711_issue-229_migrate-datasource-bff.md"
  - "../../src/README.md"
---

# 仕様書: Conversion ドメインの BFF 移設（Issue #229・IADR-0063 step3）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-12（変換ジョブ）／FR-14（疎結合ユニット）／IADR-0056（依存方向・例外3）／IADR-0059
- 実装判断: [[IADR-0063]]（BFF 合成方式 A・段階実装 step3「ドメイン単位移設」）
- Issue: #229（step3・第3ドメイン=Conversion）。DataSource=#247、Analysis=#248。

## 目的・背景

[[IADR-0063]] step3 の 3 ドメイン目。**Conversion** の BFF エンドポイント集約と DTO を knowledge へ同時移設する。
Conversion は `BffScopeResolver` に依存せず、DTO（`ConversionJobDto`・`ConversionJobStatus`）が他ドメインと結合しない
ため独立に移設可能（Analysis と非競合＝並行 PR 可）。

## 対象範囲

- 移設:
  - `ConversionBffEndpoints.cs`: → `Knowledge.Bff.Endpoints`（namespace 変更・ASP.NET Core 明示 using 追加・
    `Platform.Shared.Infrastructure.Foundation.Extensions`〔フォワーディング〕は保持）。
  - `ConversionJobDto.cs`（`ConversionJobDto` record ＋ `ConversionJobStatus` 状態値クラス）→ `Knowledge.Contracts/Dtos/`。
- 合成点（`BffEndpointComposition`・`Platform.Bff.csproj` 例外3）は #247 で整備済みのため不変。
- 参照追随:
  - **後段 `ConversionService.Worker`**: `ConversionJobDto`/`ConversionJobStatus` を参照する src 3 ファイル
    （`ConversionJobEndpoints`/`ConversionJob`/`ConversionJobStore`）＋ test 3 ファイルの using を
    `Platform.Shared.Contracts.Dtos` → `Knowledge.Contracts.Dtos` へ差し替え（いずれも当該型のみ利用）。
    `LlmGatewayDiagramCoder`（`Completion*` 利用）は不変。csproj は既に `Knowledge.Contracts` を参照済み。
  - `Platform.Bff.Tests`（`BffConversionEndpointTests`）の using を `Knowledge.Contracts.Dtos` へ。
    `BffTestFactory` は既に両 using を持つため不変（`ConversionJobDto`/`ConversionJobStatus` を推移参照で解決）。

## 依存方向

- `Platform.Bff` → `Knowledge.Bff.Endpoints`（例外3）。`ConversionService.Worker` → `Knowledge.Contracts`（ユニット内）／
  `Platform.Shared.Contracts`（Shared 例外・`Completion*` 用に保持）はいずれも許可。

## 受け入れ基準（Issue #229）との対応

- [~] 可変ユニット追加時に platform 契約・BFF を改修せず（または合成点 1 箇所のみで）拡張できる
  → Conversion について BFF エンドポイント＋DTO を knowledge へ移し合成点経由に。残ドメインは後続。`Refs #229`。

## 検証

- `dotnet build`（platform / knowledge）→ 0 エラー。
- `dotnet test Platform.Bff.Tests` → 96 pass / 1 skip。`dotnet test ConversionService.Worker.Tests` → 52 pass。
- `dotnet format --verify-no-changes`（両 slnx）→ 差分なし。
- `node scripts/check-unit-dependencies.js` → 違反 0。`node scripts/check-doc-links.js` → 破損 0。
- API 経路（`/bff/conversion/jobs`）は不変。

## 実装判断・フォローアップ

- `ConversionJobStatus`（`static class`）が同一 DTO ファイルに同居していたため、`ConversionJobDto` 非参照でも
  `ConversionJobStatus` のみ参照するファイル（`ConversionJobStoreTests`/`RawDocumentFetchedConsumerJobTests`）の
  using 追随が必要だった（型単位再精査の知見）。
- 残ドメイン: Dashboard+Feedback（`FeedbackStatsDto` 結合のため 1 PR）／Document・Search（`BffScopeResolver`
  の Shared 切り出しが前提）。全移設完了の最終 PR で `Closes #229`。
