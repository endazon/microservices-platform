---
title: リポジトリ再編 — platform/knowledge ユニット分離と位置づけ是正（issue #209/#210）
type: spec
status: draft
related_ids:
  - FR-14
  - ADR-0018
  - IADR-0027
  - IADR-0056
author: claude
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md"
---

# 仕様書: リポジトリ再編 — platform/knowledge ユニット分離と位置づけ是正

> issue #209（位置づけ是正）と #210（最終フォルダ構成への再編）を 1 PR で実施する作業仕様。
> フォルダ構成の確定目標は **issue #210 記載の構成を正** とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-14（コンポーザビリティ）
- ユースケース（UC）: —（構成・運用要求）
- 画面（SC）: —
- 関連 ADR: ADR-0018（コンポーザブルアーキテクチャ）、IADR-0027（Foundation/Composable 規約）、
  IADR-0056（本作業で新規: ユニット分割と合成の決定）
- 計画書リンク: `planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md`、
  `planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md`（基盤再利用の前提）
- 関連 issue: #209・#210

## 目的・背景

本リポジトリの主たる成果物は**マイクロサービスプラットフォームの基盤部分**であり、
KnowledgePlatform（ナレッジ活用機能）は基盤に付随する**必須の可変機能セット**である（オーナー確定）。
現状はドキュメント（README 等）が「ナレッジプラットフォームの実装リポジトリ」という逆の位置づけで
記述され（#209）、物理構成も基盤と可変機能が最上位で混在している（#210）。
本作業で位置づけの是正と物理再編を同時に行い、可変機能ユニットを submodule 追加のみで
組み込める構成（FR-14 の最終形）に到達させる。

なお本再編の内容は上流の `project-planning` にも適用・展開される予定であるため、
用語（platform / knowledge / ユニット）と構成図は計画側へそのまま環流できる形で記述する
（`/plan-feedback` で環流する）。

## 対象範囲

- 対象:
  - バックエンドの物理再編（`src/backend/{platform,knowledge}`、ユニット別 slnx、参照修正）
  - フロントエンドの物理再編（`src/frontend/{platform,knowledge}`、npm workspaces 化）
  - deploy（docker-compose・Dockerfile）、CI（.github/workflows）、scripts、.claude の追随
  - ドキュメントの位置づけ是正（#209）とパス参照の更新（リンク切れゼロ）
  - 重要判断の IADR 化（IADR-0056）と docs/adr/README 更新
- 対象外（フォローアップ issue として起票する）:
  - .NET 名前空間・アセンブリ名の改名（`KnowledgePlatform.*` → ユニット別）
  - Helm チャート名（`knowledge-platform`）・k8s リソース名の改名
  - Shared.Contracts 内のナレッジドメインイベント契約の分離
  - 追加可変機能ユニットの submodule 運用整備（テンプレートリポジトリ・CI 連携）

## 設計

### 最終フォルダ構成（issue #210 を正とする）

```text
src/
├── frontend/                      # npm workspaces ルート（package.json / lock / eslint / vitest）
│   ├── platform/                  # 基盤: アプリホスト + foundation（vite build・e2e・nginx/config テンプレート）
│   │   └── package.json
│   ├── knowledge/                 # 可変: ナレッジ画面 features（home, sc01..sc11）
│   │   └── package.json
│   └── ...etc                     # 追加可変機能ユニット（git submodule でリンク）
└── backend/                       # Directory.Build.props / Directory.Packages.props（単一情報源）
    ├── platform/
    │   ├── platform.slnx
    │   ├── Shared/                # KnowledgePlatform.Shared.Contracts / .Infrastructure
    │   ├── Bff/                   # KnowledgePlatform.Bff (+ .Tests)
    │   └── Services/              # AuthorizationService / LlmGateway
    ├── knowledge/
    │   ├── knowledge.slnx
    │   ├── Services/              # Document / DataSource / Conversion / Ingestion /
    │   │                          # Retrieval / AiAnalysis / Wiki / Feedback / Dashboard
    │   └── Tests/                 # KnowledgePlatform.IntegrationTests
    └── ...etc                     # 追加可変機能ユニット（git submodule でリンク）
```

### ユニット振り分け（詳細は IADR-0056）

- **backend/platform（基盤）**: Shared.Contracts・Shared.Infrastructure・Bff・AuthorizationService・LlmGateway。
  根拠: 認証/認可（ABAC）・LLM エグレス統制・エッジ集約・契約/横断基盤は、
  `ai-stock-trading` ADR-0001 が再利用対象とする基盤能力そのものである。
- **backend/knowledge（可変機能）**: DocumentService・DataSourceService・ConversionService・
  IngestionService・RetrievalService・AiAnalysisService・WikiService・FeedbackService・
  DashboardService・IntegrationTests。文書パイプライン〜検索〜AI 回答〜Wiki〜利用集計はナレッジ機能ドメイン。
- **frontend/platform**: `foundation/`（config/auth/api/routing/ui）＋アプリホスト（`main.tsx`/`App.tsx`/
  `index.html`/vite・tsconfig・playwright・nginx/config テンプレート・Dockerfile 入力）。
- **frontend/knowledge**: `features/`（home・sc01..sc11）一式。

### ビルド・合成

- **バックエンド**: ユニット直下の slnx（`platform.slnx` / `knowledge.slnx`）。ルート集約
  `KnowledgePlatform.slnx`（ルート・src 直下の 2 つ）は廃止し、CI・スクリプトはユニット毎に
  restore/build/test/format を実行する。knowledge → platform の参照は
  `src/backend/platform/Shared/` の 2 プロジェクトのみ許可（ProjectReference の相対パス）。
- **フロントエンド**: `src/frontend/` を npm workspaces ルートとし、単一 lock で管理。
  合成点は platform 側 `src/features/index.ts`（可変ユニットの features を束ねる。ユニット追加＝
  import 1 行）。`@foundation` → platform、`@knowledge` → knowledge を alias で解決し、
  feature 実装のソース import は変更しない。単体テスト＋カバレッジはワークスペースルートの
  vitest 設定で両パッケージを横断計測し、既存しきい値（ラチェット）を維持する。
- **submodule 境界**: 追加可変機能はユニット単位（`src/backend/<unit>/`・`src/frontend/<unit>/`）で
  リンクする（IADR-0027 の「サービス単位のサブモジュール」から変更。サービスユニット規約自体は
  ユニット内レイアウトとして存続）。

### 追随が必要な箇所（棚卸し結果）

| 領域 | 変更 |
| --- | --- |
| csproj | knowledge 各サービスの Shared 参照を `..\..\..\..\..\platform\Shared\...` へ。IntegrationTests の Shared 参照更新（Services 参照は相対のまま不変） |
| Dockerfile（13 個） | `COPY src/ .` → `COPY src/backend/ .`、restore/publish パスへ `platform/`・`knowledge/` を反映。frontend は workspaces 対応 |
| deploy/docker-compose.yml | 14 サービスの `dockerfile:` パス更新 |
| .github/workflows | ci.yml（lint/build-test をユニット毎に）、security.yml（slnx×2）、frontend.yml / frontend-tests.yml（`src/frontend` へ）、copilot-setup-steps.yml |
| scripts | setup.sh（restore をユニット毎に） |
| .claude | commands/verify.md 等の検証コマンド記述（該当があれば） |
| docs | README・CLAUDE.md・AGENTS.md・docs/README・tech/how-to・src/Services/README（→ src/backend/README へ）ほか、`check-doc-links.js` でリンク切れゼロを確認 |

## 受け入れ基準

- [ ] フォルダ構成が issue #210 記載の形になっている（`src/frontend/{platform,knowledge}`・`src/backend/{platform,knowledge}`、ユニット直下に slnx / package.json）
- [ ] platform ユニットが knowledge ユニットへ依存していない（一方向依存）
- [ ] `dotnet build` / `dotnet test` が両 slnx で成功、`dotnet format --verify-no-changes` が両 slnx で成功
- [ ] frontend の `typecheck` / `lint` / `test`（カバレッジしきい値維持）/ `build` / e2e スモークが成功
- [ ] CI ワークフローが新パスで動作する定義になっている（paths フィルタ・working-directory・キャッシュパス）
- [ ] docker-compose のビルド定義が新パスを指す（compose config で検証）
- [ ] `scripts/check-doc-links.js` がリンク切れゼロで通る
- [ ] README 等の位置づけが「主=プラットフォーム基盤、KnowledgePlatform=付随する必須の可変機能」で統一されている（#209）
- [ ] 重要判断が IADR-0056 に記録され、フォローアップが issue 化されている

## テスト方針

- 既存の単体・統合テストを新構成でそのまま全通過させる（挙動変更なしの機械的再編であることの確認）。
- フロントエンドはカバレッジしきい値（lines 78 / statements 78 / functions 68 / branches 74）を維持する。
- 構成の検証: `docker compose -f deploy/docker-compose.yml config` によるパス整合確認、
  `scripts/check-doc-links.js` / `scripts/validate-pipeline-config.js --self-test` の通過。

## 計画書との差異

- 差異: あり。上流（vision・ADR-0018 等）は「ナレッジ活用プラットフォーム構築」を主として記述しており、
  「主=基盤・ナレッジ=付随可変機能」の位置づけとリポジトリ最上位のユニット構成は計画側に未反映。
  対応: 本再編の内容（位置づけ・最終フォルダ構成・ユニット境界）を `/plan-feedback` で計画リポジトリへ
  環流する（オーナーが project-planning へ適用・展開する前提）。

## 未決事項

- .NET 名前空間改名・Helm チャート改名・契約分離・submodule 運用整備の実施時期（フォローアップ issue で管理）。
