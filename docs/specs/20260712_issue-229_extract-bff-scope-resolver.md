---
title: BffScopeResolver を Shared.Infrastructure へ切り出す（Document/Search 移設の基盤・Issue #229・IADR-0063 step3）
type: spec
status: done
related_ids:
  - FR-05
  - FR-14
  - IADR-0009
  - IADR-0056
  - IADR-0063
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
related_specs:
  - "../adr/IADR-0063_bff-unit-endpoint-composition.md"
  - "./20260711_issue-229_migrate-datasource-bff.md"
  - "../../src/README.md"
---

# 仕様書: BffScopeResolver を Shared.Infrastructure へ切り出す（Issue #229・IADR-0063 step3 基盤）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-05（ABAC 権限・deny-by-default）／FR-14（疎結合ユニット）／IADR-0009（ABAC）／IADR-0056（依存方向）
- 実装判断: [[IADR-0063]]（BFF 合成方式 A・段階実装 step3。Document/Search 移設の前提となる共通ヘルパ切り出し）
- Issue: #229（step3・Document/Search 移設の**基盤 PR**）

## 目的・背景

Document・Search の BFF エンドポイントは `BffScopeResolver`（現 `Platform.Bff.Foundation.Authz`）に依存する。
両ドメインを knowledge ユニットへ移設するには、この共通 ABAC ヘルパを**ユニット外から参照可能な Shared** へ
先に切り出す必要がある（[[IADR-0063]] のテスト戦略・段階実装で予告済み）。本 PR は挙動不変の切り出しに閉じ、
Document/Search の移設は後続 PR で行う。

## 配置の決定（実装判断）

`BffScopeResolver` は `IHttpClientFactory`・`HttpContext`（ASP.NET Core）と `AccessScope`/`AccessScopeRequest`/
`AccessScopeResponse`（`Platform.Shared.Contracts`）に依存する。したがって契約専用の `Platform.Shared.Contracts`
ではなく、ASP.NET Core と Contracts の双方を参照できる **`Platform.Shared.Infrastructure`** に置く。
名前空間は既存の `Foundation/*` 慣習に合わせ **`Platform.Shared.Infrastructure.Foundation.Authz`**（新規 `Foundation/Authz/`）。
knowledge の BFF エンドポイントは Shared.Infrastructure を参照済み（フォワーディング拡張）なので、移設後は
knowledge→Shared（許可）で参照できる。

## 対象範囲

- 移設: `BffScopeResolver.cs`: `Platform.Bff/Foundation/Authz/` → `Platform.Shared.Infrastructure/Foundation/Authz/`。
  namespace `Platform.Bff.Foundation.Authz` → `Platform.Shared.Infrastructure.Foundation.Authz`（実装は不変）。
- 参照追随: `DocumentBffEndpoints.cs` / `SearchBffEndpoints.cs`（本 PR では platform 残置）の using を新名前空間へ。
- テスト（TDD・新規）: `BffScopeResolverTests`（`Platform.Bff.Tests`）で純ロジック `Matches` / `ExtractUserAttributes`
  を直接検証（deny-by-default・AND/OR・大文字小文字非依存・claim 抽出）。`ResolveAsync`（HTTP）は既存の
  Document/Search BFF エンドポイントテストが引き続き回帰保証する。

## 依存方向

- `Platform.Bff`（Document/Search 残置）→ `Platform.Shared.Infrastructure`（既存参照）で解決。
- 後続の移設で `Knowledge.Bff.Endpoints` → `Platform.Shared.Infrastructure`（knowledge→Shared・許可）。

## 受け入れ基準（Issue #229）との対応

- [~] 可変ユニット追加時に platform 契約・BFF を改修せず拡張できる → 本 PR は Document/Search 移設の前提となる
  共通 ABAC ヘルパを Shared へ切り出す基盤。`Refs #229`。

## 検証

- `dotnet build`（platform / knowledge）→ 0 エラー。
- `dotnet test Platform.Bff.Tests` → 既存 + 新規 BffScopeResolver 単体テストが緑。
- `dotnet format --verify-no-changes`（両 slnx）→ 差分なし。
- `node scripts/check-unit-dependencies.js` → 違反 0。`node scripts/check-doc-links.js` → 破損 0。
- API 経路（`/bff/documents`・`/bff/search`）は不変（本 PR は helper の所在のみ移動）。

## フォローアップ

- 本 PR マージ後、Document・Search を `Knowledge.Bff.Endpoints` へ移設（`AiAnswerDto`/`SearchResultDto`/`DocumentDto`/
  `SearchDto`/`ChunkDto` を `Knowledge.Contracts` へ同時移設。Analysis の platform 残置分〔AiAnswerDto/SearchResultDto〕も
  knowledge 内参照へ収束）。全移設完了の最終 PR で `Closes #229`。
