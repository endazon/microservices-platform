---
title: KnowledgePlatform ブランドの .NET 名前空間・アセンブリ・フロント package をユニット構成へ改名する（Issue #227）
type: spec
status: done
related_ids:
  - FR-14
  - IADR-0027
  - IADR-0056
  - IADR-0059
  - IADR-0062
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
related_specs:
  - "../adr/IADR-0062_namespace-assembly-unit-rename.md"
  - "../../src/README.md"
---

# 仕様書: KnowledgePlatform ブランドの改名（Issue #227）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-14／関連 IADR-0027（名前空間＝フォルダ一致）・IADR-0056（ユニット第一構成）・IADR-0059（URN 固定）
- 実装判断: [[IADR-0062]]（命名体系・URN 保護）
- Issue: #227（フォローアップ 1）

## 目的・背景

基盤（platform）コードが `KnowledgePlatform` ブランド下にある不整合（#209 / IADR-0056）を解消し、`Platform.*`（基盤）
/ `Knowledge.*`（可変ユニット）へ機械改名する。**IADR-0059 で固定した MassTransit URN 文字列は wire 契約のため
改名しない**（保護）。

## 対象範囲

- 改名（[[IADR-0062]] の命名体系）:
  - `KnowledgePlatform.Shared.Contracts` / `.Infrastructure` → `Platform.Shared.Contracts` / `.Infrastructure`
  - `KnowledgePlatform.Bff` / `.Bff.Tests` → `Platform.Bff` / `.Bff.Tests`
  - `KnowledgePlatform.IntegrationTests` → `Knowledge.IntegrationTests`
  - 拡張メソッド `Add/Use/MapKnowledgePlatform*`・`KnowledgePlatformAuthPolicies` 等 → `*Platform*`
  - フロント package `@microservices-platform/frontend-{platform,knowledge}` → `@platform/frontend` / `@knowledge/frontend`
  - 帰結: 5 プロジェクトのディレクトリ/csproj/ProjectReference/slnx、Bff の DLL 名（Dockerfile ENTRYPOINT・
    docker-compose の dockerfile パス）。
- 改名しない（保護/スコープ外）:
  - `[MessageUrn("KnowledgePlatform.Shared.Contracts.Events:<Name>")]` の URN 文字列（wire 契約・7 ファイル）。
  - サービス名前空間（`DocumentService.*` 等・`KnowledgePlatform` ブランド外）。
  - helm/k8s/realm の小文字 `knowledge-platform`（#228 / IADR-0061）。
  - Grafana/pipeline スキーマの表示ブランド文字列（表示名。据え置き）。
  - 過去 spec/IADR の時点記録（living doc `src/README.md` のみ更新）。

## 実装方針

- 機械置換（保護 7 ファイル・`obj`/`bin` を除外）: `KnowledgePlatform.IntegrationTests`→`Knowledge.IntegrationTests`
  を先に、残り `KnowledgePlatform`→`Platform`。csproj/slnx は `.Shared`/`.Bff`/`.IntegrationTests` を個別置換し、
  ディレクトリ・csproj を `git mv`。
- フロント package 名を更新し `package-lock.json` を再生成。
- **URN 不変を回帰テスト（`Knowledge.Contracts.Tests`）で保証**。

## 受け入れ基準（Issue #227）との対応

- [x] 名前空間・アセンブリ名がユニット構成と一致し、IADR-0027 の規約に適合する
  → `KnowledgePlatform.*` を `Platform.*`/`Knowledge.*` へ改名（ディレクトリ＝名前空間＝アセンブリ一致）。
- [x] 全ユニットのビルド・テスト・デプロイ定義が通る
  → platform/knowledge ビルド 0 エラー、テスト緑、Bff Dockerfile/compose のパス・DLL 追随、フロント lint/typecheck 緑。

## 検証

- `dotnet build`（platform / knowledge）→ 0 エラー。
- **URN 後方互換**: `Knowledge.Contracts.Tests` 6/6 pass（`urn:message:KnowledgePlatform.Shared.Contracts.Events:*` が不変）。
- テスト（サンプル）: Platform.Bff.Tests 93+1skip / AuthorizationService 51 / LlmGateway 58 / Conversion 52 /
  Document 53 / Wiki 38 いずれも緑。
- `dotnet format --verify-no-changes`（両 slnx）→ 差分なし。
- `node scripts/check-unit-dependencies.js` → 違反 0。`node scripts/check-doc-links.js` → 破損 0。
- フロント: `npm run lint` / `npm run typecheck` 緑（`@platform/frontend` へ改名反映）。
- 統合テスト（`Knowledge.IntegrationTests`・Testcontainers 前提）は CI/コンテナ環境で実行（切り分け）。

## 実装判断・フォローアップ

- 命名体系・URN 保護は [[IADR-0062]] に記録。
- 表示ブランド文字列（Grafana/schema title）は表示名のため据え置き（必要なら別途）。
- デプロイ小文字 `knowledge-platform` は #228 / IADR-0061。
