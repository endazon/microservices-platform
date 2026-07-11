---
title: DataSource ドメインの BFF エンドポイント＋DTO を knowledge へ移設する（Issue #229・IADR-0063 step3 第1ドメイン）
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
  - "../../src/README.md"
---

# 仕様書: DataSource ドメインの BFF 移設（Issue #229・IADR-0063 step3 第1ドメイン）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-14／IADR-0056（依存方向・例外3）／IADR-0059（契約階層化）
- 実装判断: [[IADR-0063]]（BFF 合成方式 A・段階実装 step3「ドメイン単位移設」）
- Issue: #229（フォローアップ 3・段階実装 step3・第1ドメイン=DataSource）

## 目的・背景

[[IADR-0063]] の step3（ドメイン単位移設）の第1ドメイン。**DataSource** の BFF エンドポイント集約と DTO を
platform から knowledge ユニットへ移し、platform BFF の合成点から例外3 で参照する。DataSource は BFF
エンドポイントが `Platform.Bff` 内部ヘルパ（BffScopeResolver 等）に依存せず、DTO も他ドメインと共有しないため
最初のドメインとして最小リスク。後方互換は持たせない（旧名は残さない）。

## 対象範囲

- 新規: `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/`（knowledge の BFF エンドポイントライブラリ。
  ASP.NET Core FrameworkReference ＋ `Platform.Shared.Infrastructure`（フォワーディング・認可ポリシー）＋
  `Knowledge.Contracts`（移設 DTO）を参照。knowledge `backend.slnx` に登録）。
- 移設:
  - `DataSourceBffEndpoints.cs`: `Platform.Bff/Foundation/Endpoints/` → `knowledge/backend/Bff/Knowledge.Bff.Endpoints/`。
    namespace `Platform.Bff.Foundation.Endpoints` → `Knowledge.Bff.Endpoints`。ASP.NET Core の明示 using を追加
    （非 Web SDK のため暗黙 using が入らない）。
  - `DataSourceDto.cs`（`DataSourceDto` / `CreateDataSourceRequest`）: `Platform.Shared.Contracts/Dtos/` →
    `Knowledge.Contracts/Dtos/`。namespace `Platform.Shared.Contracts.Dtos` → `Knowledge.Contracts.Dtos`。
- 合成点: `Platform.Bff.csproj` に `Knowledge.Bff.Endpoints` の ProjectReference を追加（例外3）。
  `Bff/Composition/BffEndpointComposition.cs` に `using Knowledge.Bff.Endpoints;` を追加（登録簿の DataSource
  モジュールが移設先の拡張メソッドを解決）。登録簿の内容・順序は不変。
- 参照追随（using のみ・直接 ProjectReference は不要＝推移参照）:
  - `Platform.Bff.Tests`（`BffDataSourceEndpointTests` / `BffTestFactory`）: 移設 DTO の using を
    `Knowledge.Contracts.Dtos` へ。テストは `Platform.Bff` 経由の推移参照で移設 DTO を解決（直接 ProjectReference
    が無いため依存検査違反にならない）。
  - `DataSourceService` は自前の `CreateDataSourceRequest`（別型・JSON 互換）を使うため影響なし。

## 依存方向（例外3 の初回行使）

- `Platform.Bff` → `Knowledge.Bff.Endpoints`（`<unit>/backend/Bff/`）は**例外3**で許可（前スライスで整備）。
  検査で `bff-composition-exception` に分類されることを確認。
- `Knowledge.Bff.Endpoints` → `Platform.Shared.Infrastructure`（ユニット外は Shared のみ）・`Knowledge.Contracts`
  （ユニット内）はいずれも許可。

## 受け入れ基準（Issue #229）との対応

- [~] 可変ユニット追加時に platform 契約・BFF を改修せず（または合成点 1 箇所のみで）拡張できる
  → DataSource について、BFF エンドポイント＋DTO を knowledge へ移し合成点経由に。残 6 ドメインは後続スライス。`Refs #229`。

## 検証

- `dotnet build`（platform / knowledge）→ 0 エラー。
- `dotnet test Platform.Bff.Tests` → 97 pass（DataSource BFF・合成点・全 BFF。移設 DTO を推移参照で解決）。
- `dotnet test DataSourceService.Api.Tests` → 56 pass（移設影響なし）。
- `dotnet format --verify-no-changes`（両 slnx）→ 差分なし。
- `node scripts/check-unit-dependencies.js` → 違反 0。例外3 が `Platform.Bff → Knowledge.Bff.Endpoints` を許可
  （`bff-composition-exception`）。self-test 13。`node scripts/check-doc-links.js` → 破損 0。
- API 経路（`/bff/datasources`）は不変（コード所在のみ移動）。

## 実装判断・フォローアップ

- 移設パターン（新 BFF エンドポイントプロジェクト・例外3・推移参照でのテスト解決）を確立。次ドメイン以降は
  `Knowledge.Bff.Endpoints` へ追加する形で反復する。
- 残ドメイン: Conversion / Dashboard / Feedback / Analysis（Platform.Bff 内部依存なし）→ Document / Search
  （`BffScopeResolver` 依存のため Shared への切り出しが要る。順序は後続で判断）。全移設完了の最終 PR で `Closes #229`。
