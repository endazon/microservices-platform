---
title: Document ドメインの BFF エンドポイント＋DTO を knowledge へ移設する（Issue #229・IADR-0063 step3）
type: spec
status: done
related_ids:
  - FR-06
  - FR-14
  - IADR-0009
  - IADR-0056
  - IADR-0063
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
related_specs:
  - "../adr/IADR-0063_bff-unit-endpoint-composition.md"
  - "./20260712_issue-229_extract-bff-scope-resolver.md"
  - "../../src/README.md"
---

# 仕様書: Document ドメインの BFF 移設（Issue #229・IADR-0063 step3）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-06（文書閲覧・管理）／FR-14／IADR-0009（ABAC 存在秘匿）／IADR-0056（依存方向）
- 実装判断: [IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md)（BFF 合成方式 A・段階実装 step3「ドメイン単位移設」）
- Issue: #229（step3・Document。`BffScopeResolver` の Shared 切り出しは #251 で完了）

## 目的・背景

[IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md) step3 の Document 移設。BFF エンドポイント集約と自ドメイン DTO を knowledge へ同時移設する。
Document の ABAC 集約（`BffScopeResolver`）は #251 で Shared.Infrastructure へ切り出し済みのため、knowledge から
参照できる。Document DTO は Search と共有しない（独立移設可能）。

## 対象範囲

- 移設:
  - `DocumentBffEndpoints.cs`: → `Knowledge.Bff.Endpoints`（namespace・ASP.NET Core 明示 using。`BffScopeResolver`＝
    `Platform.Shared.Infrastructure.Foundation.Authz`、`IObjectStorageClient`＝`...Ports.Storage`、`PlatformAuthPolicies`＝
    `...Extensions` は Shared 参照で保持。BFF ローカル record `DocumentCreateRequest`/`DocumentUpdateRequest` も同梱）。
  - `DocumentDto.cs`（`DocumentDto`/`DocumentContentDto`/`DocumentVersionDto`）→ `Knowledge.Contracts/Dtos/`。
- 合成点（`BffEndpointComposition`・`Platform.Bff.csproj` 例外3）は #247 で整備済みのため不変。
- 参照追随:
  - 後段 `DocumentService.Api`（`DocumentEndpoints`＋test 4 ファイル）: Document 型のみ利用のため using を
    `Knowledge.Contracts.Dtos` へ**差し替え**。`DocumentService.Api` は既に `Knowledge.Contracts` を参照（イベント契約）
    しており、Platform.Shared.Contracts は Document DTO でしか使っていなかったため **ProjectReference を除去**。
  - `Platform.Bff.Tests`（`BffDocumentEndpointTests`）: `DocumentDto` は `Knowledge.Contracts.Dtos`、`AttributeFilter`
    （platform 横断・`AccessScopeDto.cs`）は `Platform.Shared.Contracts.Dtos` を併記。`BffTestFactory` は両 using 保持で不変。

## 依存方向

- `Platform.Bff` → `Knowledge.Bff.Endpoints`（例外3）。`Knowledge.Bff.Endpoints`（DocumentBffEndpoints）→
  `Platform.Shared.Infrastructure`（BffScopeResolver/Storage/Extensions）は knowledge→Shared＝許可。
- `DocumentService.Api` → `Knowledge.Contracts`（ユニット内）／`Platform.Shared.Infrastructure`（Shared）。違反 0。

## 受け入れ基準（Issue #229）との対応

- [~] 可変ユニット追加時に platform 契約・BFF を改修せず拡張できる → Document について BFF エンドポイント＋DTO を
  knowledge へ移し合成点経由に。残 Search は次の最終 PR。`Refs #229`。

## 検証

- `dotnet build`（platform / knowledge）→ 0 エラー。
- `dotnet test Platform.Bff.Tests` → 106 pass / 1 skip。`DocumentService.Api.Tests` → 53 pass。
- `dotnet format --verify-no-changes`（両 slnx）→ 差分なし。
- `node scripts/check-unit-dependencies.js` → 違反 0。`node scripts/check-doc-links.js` → 破損 0。
- API 経路（`/bff/documents/*`）は不変。

## 実装判断・フォローアップ

- `AttributeFilter`（`AccessScopeDto.cs`・platform 横断）は移設対象外のため、BFF テストは Knowledge/Platform 双方の
  using を併記（型単位再精査）。
- 残ドメイン: **Search**（`SearchDto`/`SearchResultDto`〔`AiAnswerDto`/`CitationDto` 同居〕/`ChunkDto` を
  `Knowledge.Contracts` へ。RetrievalService・AiAnalysisService の参照追随。Analysis の platform 残置
  `AiAnswerDto`/`SearchResultDto` も knowledge 内参照へ収束）。全移設完了の**最終 PR で `Closes #229`**。
