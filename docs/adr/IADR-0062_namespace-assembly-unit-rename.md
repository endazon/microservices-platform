---
title: IADR-0062 KnowledgePlatform ブランドの .NET 名前空間・アセンブリとフロント package をユニット構成へ改名する
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - IADR-0027
  - IADR-0056
  - IADR-0059
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
---

# IADR-0062: KnowledgePlatform ブランドの改名（ユニット構成整合）

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-14（構成変更で完結する疎結合ユニット）
- 関連 ADR: [[IADR-0027]]（名前空間＝フォルダ階層一致）／[[IADR-0056]]（ユニット第一構成）／[[IADR-0059]]（契約階層化・URN 固定）
- 関連仕様書: `docs/specs/20260711_issue-227_namespace-assembly-rename.md`
- Issue: #227（IADR-0056 フォローアップ 1）

## コンテキストと課題

再編（IADR-0056）で物理構成は `src/<unit>/{backend,frontend}` になったが、**基盤（platform ユニット）のコードが
`KnowledgePlatform` ブランドの下にある**（`KnowledgePlatform.Shared.*` / `KnowledgePlatform.Bff` / 共通拡張メソッド
`Add/Use/MapKnowledgePlatform*`）。knowledge ユニットの統合テストも `KnowledgePlatform.IntegrationTests` を名乗る。
フロントの package 名 `@microservices-platform/frontend-*` は暫定命名。これらが「主=プラットフォーム基盤」の
位置づけ（#209）およびユニット構成と不整合。

**制約（後方互換）**: [[IADR-0059]] で 6 イベントの MassTransit URN を `[MessageUrn("KnowledgePlatform.Shared.Contracts.Events:<Name>")]`
に固定した。この **URN 文字列は wire 契約であり改名してはならない**（改名すると RabbitMQ ルーティングが破綻）。

## 決定（命名体系）

`KnowledgePlatform.*` ブランドをユニット構成へ改名する。

| 現名 | 新名 | 対象 |
| --- | --- | --- |
| `KnowledgePlatform.Shared.Contracts` | `Platform.Shared.Contracts` | 名前空間・アセンブリ・ディレクトリ・csproj |
| `KnowledgePlatform.Shared.Infrastructure` | `Platform.Shared.Infrastructure` | 同上 |
| `KnowledgePlatform.Bff` | `Platform.Bff` | 同上 |
| `KnowledgePlatform.Bff.Tests` | `Platform.Bff.Tests` | 同上 |
| `KnowledgePlatform.IntegrationTests` | `Knowledge.IntegrationTests` | knowledge ユニットの統合テスト |
| 拡張 `Add/Use/MapKnowledgePlatform*`・`KnowledgePlatformAuthPolicies` 等 | `Add/Use/MapPlatform*`・`PlatformAuthPolicies` | platform 提供の共通ヘルパ識別子 |
| `@microservices-platform/frontend-platform` | `@platform/frontend` | フロント package 名 |
| `@microservices-platform/frontend-knowledge` | `@knowledge/frontend` | フロント package 名 |

追随（アセンブリ/DLL 名の帰結）:
- Bff の DLL 名 `KnowledgePlatform.Bff.dll` → `Platform.Bff.dll`（Dockerfile ENTRYPOINT・restore/publish パス・
  `deploy/docker-compose.yml` の dockerfile パス）。
- 各サービスの csproj `ProjectReference` パス・`backend.slnx` のプロジェクトパス。

**改名しないもの**:
- `[MessageUrn("KnowledgePlatform.Shared.Contracts.Events:<Name>")]` の URN 文字列（wire 契約。7 ファイル＝
  `Knowledge.Contracts/Events/*.cs` 6 件と `Knowledge.Contracts.Tests/EventMessageUrnBackwardCompatibilityTests.cs`）。
- サービスの名前空間（`DocumentService.*` / `AuthorizationService.*` 等）— `KnowledgePlatform` ブランドを持たず、
  本 issue のスコープ（`KnowledgePlatform.*` / `@microservices-platform/*`）外。
- helm/k8s/realm の小文字 `knowledge-platform`（デプロイ資産の命名＝#228 / IADR-0061 の領域）。
- 過去の spec/IADR の時点記録（点在する `KnowledgePlatform.*` 言及は執筆時点の記録として据え置く。living docs
  〔`src/README.md`〕のみ更新）。

## 理由

- **ブランド整合**: platform 基盤コードから `KnowledgePlatform` ブランドを外し、`Platform.*`（基盤）/ `Knowledge.*`
  （可変ユニット）へ揃えることで IADR-0056 のユニット第一構成・IADR-0027 の名前空間＝フォルダ一致に適合。
- **後方互換の厳守**: URN 文字列を保護対象として機械改名から除外し、[[IADR-0059]] の wire 契約を不変に保つ。
  回帰は `Knowledge.Contracts.Tests`（URN が旧値のままであることを固定）で検証する。
- **スコープの明確化**: サービス名前空間やデプロイ小文字名は別 issue の領域として除外し、単一クリーン PR の
  レビュー可能性を保つ。

## 結果

- `KnowledgePlatform` トークンを（保護 7 ファイルを除く）コードから機械置換：`KnowledgePlatform.IntegrationTests`→
  `Knowledge.IntegrationTests` を先に、残りを `Platform` へ。
- 5 プロジェクトのディレクトリ・csproj を改名し、`ProjectReference` / `backend.slnx` を追随。
- Bff Dockerfile・docker-compose のパス/DLL 名を追随。
- フロント package 名 `@platform/frontend` / `@knowledge/frontend`（lock 再生成）。
- `src/README.md` を新名称へ更新。
- 検証: platform / knowledge のビルド・全テスト緑、URN 不変（回帰テスト）、`dotnet format` 差分なし、
  依存方向検査・doc-links 緑。

## フォローアップ

- Grafana/pipeline スキーマ等の表示ブランド文字列（`KnowledgePlatform Overview` 等）は表示名のため据え置き（必要なら別途）。
- デプロイ資産の小文字 `knowledge-platform` 改名は #228 / IADR-0061。

## 関連

- Supersedes: なし
- Superseded by: なし
