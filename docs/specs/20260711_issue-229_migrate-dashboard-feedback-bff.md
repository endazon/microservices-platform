---
title: Dashboard＋Feedback ドメインの BFF エンドポイント＋DTO を knowledge へ移設する（Issue #229・IADR-0063 step3）
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

# 仕様書: Dashboard＋Feedback ドメインの BFF 移設（Issue #229・IADR-0063 step3）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-08（フィードバック）／FR-10（ダッシュボード）／FR-14／IADR-0056（依存方向・例外3）／IADR-0059
- 実装判断: [[IADR-0063]]（BFF 合成方式 A・段階実装 step3「ドメイン単位移設」）
- Issue: #229（step3・第4スライス=Dashboard＋Feedback）。DataSource=#247、Analysis=#248、Conversion=#249。

## 目的・背景

[[IADR-0063]] step3 の 4 スライス目。**Dashboard と Feedback を 1 PR で同時移設**する。理由は DTO 結合:
`DashboardSummaryDto` が `FeedbackStatsDto Quality` を内包し、`DashboardBffEndpoints` も `FeedbackStatsDto` を読む。
片方だけ knowledge へ移すと `Platform.Shared.Contracts`（platform）が `Knowledge.Contracts`（knowledge）を参照する
platform→knowledge 違反になるため、両ドメインの DTO を同時に `Knowledge.Contracts` へ移す必要がある。
いずれも `BffScopeResolver` に依存しない。

## 対象範囲

- 移設:
  - `DashboardBffEndpoints.cs` / `FeedbackBffEndpoints.cs`: → `Knowledge.Bff.Endpoints`（namespace・ASP.NET Core
    明示 using。Dashboard は `Platform.Shared.Infrastructure`〔フォワーディング〕保持）。
  - `DashboardDto.cs`（`UsageEventRequest`/`UsagePointDto`/`SearchTrendDto`/`DashboardUsageDto`/`DashboardSummaryDto`）
    / `FeedbackDto.cs`（`FeedbackRequest`/`FeedbackDto`/`FeedbackStatsDto`）→ `Knowledge.Contracts/Dtos/`。
- 合成点（`BffEndpointComposition`・`Platform.Bff.csproj` 例外3）は #247 で整備済みのため不変。
- 参照追随（いずれも移設型のみ利用のため `Platform.Shared.Contracts.Dtos` → `Knowledge.Contracts.Dtos` へ差し替え）:
  - 後段 `DashboardService.Api`（`DashboardEndpoints`＋test）／`FeedbackService.Api`（`FeedbackEndpoints`＋test）。
    両サービスは `Platform.Shared.Contracts` を DTO でしか参照していないため、**ProjectReference を
    `Platform.Shared.Contracts` → `Knowledge.Contracts` へ差し替え**（knowledge 内参照へ是正）。
  - `Platform.Bff.Tests`（`DashboardBffEndpointTests`/`FeedbackBffEndpointTests`）の using を更新。
    `BffTestFactory` は platform 横断 DTO（`AbacPolicyDto`/`AccessScopeResponse`/`AiAnswerDto`/`DocumentDto`/
    `SearchResponse`/`SearchResultDto`）を併用し両 using を持つため**不変**（移設型は推移参照で解決）。

## 依存方向

- `Platform.Bff` → `Knowledge.Bff.Endpoints`（例外3）。`DashboardService`/`FeedbackService` → `Knowledge.Contracts`
  （ユニット内）へ是正（platform Shared 参照を解消）。違反 0。

## 受け入れ基準（Issue #229）との対応

- [~] 可変ユニット追加時に platform 契約・BFF を改修せず（または合成点 1 箇所のみで）拡張できる
  → Dashboard＋Feedback について BFF エンドポイント＋DTO を knowledge へ移し合成点経由に。残（Document/Search）は後続。`Refs #229`。

## 検証

- `dotnet build`（platform / knowledge）→ 0 エラー。
- `dotnet test Platform.Bff.Tests` → 96 pass / 1 skip。`DashboardService.Api.Tests` → 11 pass。`FeedbackService.Api.Tests` → 15 pass。
- `dotnet format --verify-no-changes`（両 slnx）→ 差分なし。
- `node scripts/check-unit-dependencies.js` → 違反 0。`node scripts/check-doc-links.js` → 破損 0。
- API 経路（`/bff/dashboard`・`/bff/feedback`）は不変。

## 実装判断・フォローアップ

- **DTO 結合ドメインは 1 PR にまとめる**方針を確立（`FeedbackStatsDto` を介した Dashboard↔Feedback 結合）。
- Dashboard/Feedback は platform 横断 DTO を使わないため、後段サービスの Contracts 参照を Knowledge へ**差し替え**
  （Analysis のような併記ではなく置換）。
- 残ドメイン: Document・Search（`BffScopeResolver` の Shared 切り出しが前提。基盤 PR を先に確定）。
  全移設完了の最終 PR で `Closes #229`。
