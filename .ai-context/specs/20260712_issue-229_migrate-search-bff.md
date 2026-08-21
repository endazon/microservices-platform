---
title: Search ドメインの BFF エンドポイント＋DTO を knowledge へ移設する（Issue #229 完了・IADR-0063 step3 最終）
type: spec
status: done
related_ids:
  - FR-03
  - FR-04
  - FR-05
  - FR-14
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
  - "./20260712_issue-229_migrate-document-bff.md"
  - "./20260712_issue-229_extract-bff-scope-resolver.md"
  - "../../src/README.md"
---

# 仕様書: Search ドメインの BFF 移設（Issue #229 完了・IADR-0063 step3 最終）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-03（ハイブリッド検索）／FR-04（RAG 回答・出典）／FR-05（ABAC）／FR-14／IADR-0056（依存方向）
- 実装判断: [IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md)（BFF 合成方式 A・段階実装 step3「ドメイン単位移設」の**最終スライス**）
- Issue: #229（step3・Search＝最終ドメイン。本 PR で `Closes #229`）

## 目的・背景

[IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md) step3 の最終ドメイン。**Search** の BFF エンドポイント集約と DTO を knowledge へ同時移設し、
ナレッジ固有 DTO の platform 残置を解消する。これにより `Platform.Shared.Contracts/Dtos` は platform 横断 5 型のみ、
`Platform.Bff/Foundation/Endpoints/` は platform 固有 2（Config/Authz）のみになる。

## 対象範囲

- 移設:
  - `SearchBffEndpoints.cs`: → `Knowledge.Bff.Endpoints`（namespace・ASP.NET Core 明示 using。`BffScopeResolver`＝
    Shared 参照で保持）。
  - `SearchDto.cs`（`SearchRequest`/`SearchResponse`）・`SearchResultDto.cs`（`SearchResultDto`/`CitationDto`/`AiAnswerDto`）・
    `ChunkDto.cs`（全域未参照）→ `Knowledge.Contracts/Dtos/`。
- **`Knowledge.Contracts` → `Platform.Shared.Contracts` 参照を追加**: `SearchRequest.Scope` が platform 横断の ABAC 契約
  `AccessScope`（`AccessScopeDto.cs`・移設対象外）を内包するため。knowledge→platform Shared は許可（Shared 参照）。
  移設した `SearchDto.cs` は `using Platform.Shared.Contracts.Dtos;`（AccessScope）を持つ。
- 合成点（`BffEndpointComposition`・`Platform.Bff.csproj` 例外3）は #247 で整備済みのため不変。
- 参照追随（型単位精査で swap/add/remove を判定）:
  - **後段 `RetrievalService.Api`**: `Knowledge.Contracts` の ProjectReference を追加。移設型のみ利用のファイルは using を
    `Knowledge.Contracts.Dtos` へ差し替え、`AttributeFilter`/`AccessScope`（staying）併用ファイルは併記。
  - **後段 `AiAnalysisService.Api`**（既に `Knowledge.Contracts` 参照済み）: 移設型（`SearchResultDto`/`CitationDto`/
    `AiAnswerDto`/`SearchRequest`/`SearchResponse`）の using を追随。`AiAnswerDto` のみ利用だったファイル
    （`AnalysisEndpoints`/`IRagOrchestrator`/`TestWebApplicationFactory`/統合 `RagOrchestratorTests`）は
    不要になった `Platform.Shared.Contracts.Dtos` using を**除去**（`RagOrchestrator` は `Completion*`/`AccessScope*`
    併用のため両 using 保持）。
  - **移設済み `AnalysisBffEndpoints`（Knowledge.Bff.Endpoints）**: `AiAnswerDto` が knowledge へ移ったため
    `Platform.Shared.Contracts.Dtos` using を除去（Analysis の platform 残置が knowledge 内参照へ**収束**）。
  - `Platform.Bff.Tests`: `BffSearchEndpointTests`（`AccessScope` 併用＝併記）、`AnalysisBffEndpointTests`（swap）。
    `BffTestFactory` は platform 横断型併用のため両 using 保持で不変。
  - `BffEndpointCompositionTests`: 全 7 ナレッジドメイン移設完了を反映してコメント更新（登録簿件数 9 は不変）。

## 依存方向

- `Knowledge.Contracts` → `Platform.Shared.Contracts`（AccessScope）／`RetrievalService`・`AiAnalysisService` →
  `Knowledge.Contracts`（ユニット内）＋`Platform.Shared.Contracts`（Shared）はいずれも許可。`check-unit-dependencies` 違反 0。

## 受け入れ基準（Issue #229）との対応

- [x] 可変ユニット追加時に platform 契約・BFF を改修せず（または合成点 1 箇所のみで）拡張できる
  → **全 7 ナレッジドメインの BFF エンドポイント＋DTO を `Knowledge.Bff.Endpoints`／`Knowledge.Contracts` へ移設完了**。
  platform BFF は合成点（例外3）経由でのみ参照し、`Program.cs`・契約のハードコードは無い。`Closes #229`。

## 検証

- `dotnet build`（platform / knowledge）→ 0 エラー。
- `dotnet test Platform.Bff.Tests` → 106 pass / 1 skip。`RetrievalService.Api.Tests` → 17 pass。`AiAnalysisService.Api.Tests` → 32 pass。
- `dotnet format --verify-no-changes`（両 slnx）→ 差分なし。
- `node scripts/check-unit-dependencies.js` → 違反 0。`node scripts/check-doc-links.js` → 破損 0。
- API 経路（`/bff/search`）は不変。
- **移設完了の確認**: `Platform.Shared.Contracts/Dtos` は 5 型（`AbacManagementDto`/`AccessScopeDto`/`CompletionDto`/
  `ConfigInfoDto`/`EmbedDto`）のみ。`Platform.Bff/Foundation/Endpoints/` は `Config`/`Authz` のみ。

## 実装判断・フォローアップ

- ナレッジ固有 DTO でも platform 横断契約（`AccessScope`）を内包する場合は、当該契約を platform Shared に残し
  `Knowledge.Contracts` から参照する（型を分割せず、依存方向は knowledge→Shared に閉じる）。
- Issue #229（IADR-0063 段階実装）は本 PR で完了。追加ユニットの「合成点 1 行」拡張の通し確認は #230（submodule 運用）
  のサンプルユニットと連携する（別 issue）。
