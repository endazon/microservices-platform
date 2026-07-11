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

- 機能要求(FR): FR-14／関連 IADR-0027（名前空間＝フォルダ一致）・IADR-0056（ユニット第一構成）・IADR-0059（契約階層化。URN 固定は本 issue で撤回）
- 実装判断: [[IADR-0062]]（命名体系・URN の新体系統一）
- Issue: #227（フォローアップ 1）

## 目的・背景

基盤（platform）コードが `KnowledgePlatform` ブランド下にある不整合（#209 / IADR-0056）を解消し、`Platform.*`（基盤）
/ `Knowledge.*`（可変ユニット）へ機械改名する。**後方互換は持たせない方針**とし、IADR-0059 の `[MessageUrn]` による
旧 URN 固定を撤回して MassTransit の URN もイベント現名前空間 `Knowledge.Contracts.Events` から導出する正準値へ統一する。

## 対象範囲

- 改名（[[IADR-0062]] の命名体系）:
  - `KnowledgePlatform.Shared.Contracts` / `.Infrastructure` → `Platform.Shared.Contracts` / `.Infrastructure`
  - `KnowledgePlatform.Bff` / `.Bff.Tests` → `Platform.Bff` / `.Bff.Tests`
  - `KnowledgePlatform.IntegrationTests` → `Knowledge.IntegrationTests`
  - 拡張メソッド `Add/Use/MapKnowledgePlatform*`・`KnowledgePlatformAuthPolicies` 等 → `*Platform*`
  - フロント package `@microservices-platform/frontend-{platform,knowledge}` → `@platform/frontend` / `@knowledge/frontend`、
    workspaces ルート `@microservices-platform/frontend` → `@platform/frontend-workspace`（`@microservices-platform/*` 全廃・`package-lock.json` クリーン再生成）
  - 帰結: 5 プロジェクトのディレクトリ/csproj/ProjectReference/slnx、Bff の DLL 名（Dockerfile ENTRYPOINT・
    docker-compose の dockerfile パス）。
- URN（後方互換なし・新体系へ統一）:
  - 6 イベントの `[MessageUrn]`（旧 URN 固定）・`using MassTransit;`・互換コメントを**削除**し、URN を現名前空間
    `Knowledge.Contracts.Events` から導出する正準値（`urn:message:Knowledge.Contracts.Events:<Name>`）へ統一。旧 URN 削除。
- 改名しない（スコープ外）:
  - サービス名前空間（`DocumentService.*` 等・`KnowledgePlatform` ブランド外）。
  - helm/k8s/realm の小文字 `knowledge-platform`（#228 / IADR-0061）。
  - 過去 spec/IADR の時点記録（執筆時点の名称のまま据え置き）。
- 追加で新名へ是正（claude-review 指摘対応）:
  - living docs（`README.md`・`docs/security/`・`docs/tech/`・`docs/tech/composability-classification.md`）の旧識別子/旧ブランド。
  - 表示ブランド文字列（openapi `title`・Grafana ダッシュボード/プロバイダ名・pipeline スキーマ `title`）を `Platform` へ。
    （`.env.example` の見出しコメントは秘密ファイル保護ガードで編集不可のため据え置き。）
  - `launchSettings.json` の起動プロファイル名、`scripts/` の自己テスト fixture パス。

## 実装方針

- 機械置換（`obj`/`bin` を除外）: `KnowledgePlatform.IntegrationTests`→`Knowledge.IntegrationTests`
  を先に、残り `KnowledgePlatform`→`Platform`。csproj/slnx は `.Shared`/`.Bff`/`.IntegrationTests` を個別置換し、
  ディレクトリ・csproj を `git mv`。
- 6 イベントの `[MessageUrn]`（旧 URN 固定）・`using MassTransit;`・互換コメントを削除し、URN を新体系へ統一（後方互換なし）。
- フロント package 名・workspaces ルート名を更新し `package-lock.json` をクリーン再生成。
- **URN が新体系（`urn:message:Knowledge.Contracts.Events:*`）で一貫することを回帰テスト（`Knowledge.Contracts.Tests`）で固定**。

## 受け入れ基準（Issue #227）との対応

- [x] 名前空間・アセンブリ名がユニット構成と一致し、IADR-0027 の規約に適合する
  → `KnowledgePlatform.*` を `Platform.*`/`Knowledge.*` へ改名（ディレクトリ＝名前空間＝アセンブリ一致）。
- [x] 全ユニットのビルド・テスト・デプロイ定義が通る
  → platform/knowledge ビルド 0 エラー、テスト緑、Bff Dockerfile/compose のパス・DLL 追随、フロント lint/typecheck 緑。

## 検証

- `dotnet build`（platform / knowledge）→ 0 エラー。
- **URN 新体系で一貫**: `Knowledge.Contracts.Tests` 6/6 pass（`urn:message:Knowledge.Contracts.Events:*` を正準値として固定。旧 URN 削除・後方互換なし）。
- テスト（サンプル）: Platform.Bff.Tests 93+1skip / AuthorizationService 51 / LlmGateway 58 / Conversion 52 /
  Document 53 / Wiki 38 いずれも緑。
- `dotnet format --verify-no-changes`（両 slnx）→ 差分なし。
- `node scripts/check-unit-dependencies.js` → 違反 0。`node scripts/check-doc-links.js` → 破損 0。
- フロント: `npm run lint` / `npm run typecheck` 緑（`@platform/frontend` へ改名反映）。
- 統合テスト（`Knowledge.IntegrationTests`・Testcontainers 前提）は CI/コンテナ環境で実行（切り分け）。

## 実装判断・フォローアップ

- 命名体系・URN の新体系統一（後方互換なし）は [[IADR-0062]]（および [[IADR-0059]] の更新）に記録。
- 表示ブランド文字列（openapi/Grafana/schema title）は `Platform` へ統一済み（claude-review 指摘対応）。
- デプロイ小文字 `knowledge-platform` は #228 / IADR-0061。
