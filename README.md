# Knowledge Platform — microservices-platform

社内ナレッジ（文書・Wiki）を横断的に検索し、AI による回答・出典提示・データ分析を提供する
社内向けナレッジプラットフォームの実装リポジトリ。ABAC（属性ベースアクセス制御）で機密区分に
応じたアクセス制御を行う、.NET マイクロサービス構成のシステムである。

このリポジトリは、上流の計画リポジトリ（`project-planning`、`planning/` に git submodule として
参照）で確定した計画書（要求・ユースケース・画面・ADR）を実装する。**実装の進め方・トレーサビリティ
規約・仕様書の作成規約は [`CLAUDE.md`](CLAUDE.md) / [`AGENTS.md`](AGENTS.md) を参照。**

## アーキテクチャ概要

フロントエンド（SPA）→ BFF → 各マイクロサービス、というエッジ集約構成。サービス間は同期 API
（内部専用・ホスト非公開）またはイベント（RabbitMQ / MassTransit）で疎結合に連携する。

```mermaid
flowchart LR
  User[ブラウザ] --> FE[frontend (SPA)]
  FE -->|/bff| BFF[BFF]
  BFF --> DOC[DocumentService]
  BFF --> RET[RetrievalService]
  BFF --> AI[AiAnalysisService]
  BFF --> FB[FeedbackService]
  BFF --> DASH[DashboardService]
  BFF --> DS[DataSourceService]
  BFF --> WIKI[WikiService]
  RET --> LLM[LlmGateway]
  AI --> LLM
  AI --> AUTHZ[AuthorizationService]
  AI --> RET
  DOC -->|event| RMQ[(RabbitMQ)]
  RMQ --> ING[IngestionService]
  RMQ --> CONV[ConversionService]
  ING --> QD[(Qdrant)]
  RET --> QD
  WIKI --> WJS[Wiki.js]
  DOC --> PG[(Postgres, per service)]
  DOC --> MINIO[(MinIO)]
```

- **フロントエンド** (`frontend/`): React 18 + TypeScript + Vite の SPA（SC-01..11）。Keycloak
  OIDC（Authorization Code + PKCE）でログインし、バックエンドへは必ず BFF 経由（`/bff/*`）でアクセスする。
- **BFF** (`src/Bff/KnowledgePlatform.Bff`): フロントエンドの唯一の入口（エッジ）。Keycloak JWT を検証し、
  各サービスへリクエストを集約・転送する。構成情報 API（`/bff/admin/config`）や構成ドリフト検出もここが担う。
- **バックエンドサービス** (`src/Services/`): サービスごとに独立した DB（DB per Service）を持つ .NET
  マイクロサービス。サービス間のコード参照は禁止し、連携は同期 API またはイベントに限る
  （[`src/Services/README.md`](src/Services/README.md)）。
- **共有ライブラリ** (`src/Shared/`): `KnowledgePlatform.Shared.Contracts`（イベント/DTO 契約）・
  `KnowledgePlatform.Shared.Infrastructure`（認証・ObjectStorage 等の横断基盤）。サービス外部から参照
  してよいのはこの 2 プロジェクトのみ。

### サービス一覧（`src/Services/`）

| サービス | 役割 |
| --- | --- |
| `DocumentService` | 文書カタログ・バージョン管理（DB を所有する正） |
| `DataSourceService` | データソース登録・同期管理 |
| `ConversionService` | 文書の正規化・変換パイプライン（Worker） |
| `IngestionService` | 埋め込み生成・Qdrant への索引投入（Worker） |
| `RetrievalService` | ハイブリッド検索・AI 回答生成の編成 |
| `AiAnalysisService` | データ範囲分析 |
| `AuthorizationService` | ABAC 属性ポリシー管理・認可判定 |
| `WikiService` | Wiki.js の同期・ABAC ゲートウェイ（実閲覧/編集 UI は Wiki.js に委譲） |
| `LlmGateway` | LLM/埋め込みプロバイダへのエグレス集約・機密区分別ルーティング |
| `FeedbackService` | AI 回答へのフィードバック収集 |
| `DashboardService` | 利用状況・検索傾向ダッシュボード集計 |

## リポジトリ構成

```text
.
├── frontend/           # SPA（React + TypeScript + Vite）。詳細は frontend/README.md
├── src/
│   ├── Bff/             # BFF（フロントエンドの唯一の入口）
│   ├── Services/        # マイクロサービス群（サービスごとに DB per Service）
│   ├── Shared/          # 共有契約・共有基盤（Contracts / Infrastructure）
│   └── Tests/           # 統合テスト（KnowledgePlatform.IntegrationTests）
├── deploy/              # デプロイ定義: docker-compose（dev）、helm/argocd/istio/keycloak（stg/prod）
├── docs/                # 実装仕様書（機能/画面/API/データ/技術/テスト/運用/セキュリティ/ADR）と how-to
├── scripts/             # 補助スクリプト（CHANGELOG/OpenAPI 生成、doc リンク検査、環境セットアップ）
├── planning/            # 計画リポジトリ project-planning（git submodule）
├── CLAUDE.md / AGENTS.md / AI_SETUP.md  # AI 実装エージェント向けの運用規約
└── KnowledgePlatform.slnx  # .NET ソリューション（src/ 配下）
```

## 前提ツール

| ツール | バージョン | 用途 |
| --- | --- | --- |
| .NET SDK | 10.0.x（`global.json` は `8.0.0` + `rollForward: latestMajor`） | バックエンドのビルド・テスト（[`src/Directory.Build.props`](src/Directory.Build.props)） |
| Node.js | 22（CI と同一。フロントは `frontend-tests.yml`/`frontend.yml` で node 22） | フロントエンドのビルド・テスト、`scripts/` の補助スクリプト |
| Docker / Docker Compose | 任意（compose v2 相当） | ローカルのインフラ・全サービス起動（`deploy/docker-compose.yml`） |
| git | — | `planning/` submodule の取得を含む |

## ローカル起動手順

詳細な手順（サービス別エンドポイント確認・よくある詰まりを含む）は
[`docs/how-to/local-development.md`](docs/how-to/local-development.md) を参照。ここでは要点のみ示す。

```bash
# 1. clone（計画リポジトリの submodule を含める）
git clone --recurse-submodules <this-repo-url>
# 既に clone 済みの場合:
git submodule update --init --recursive

# 2. バックエンド: ビルド・テスト（.NET 10 / src/KnowledgePlatform.slnx）
dotnet restore
dotnet build --configuration Release
dotnet test

# 3. フロントエンド: 依存関係・型チェック・lint・テスト・ビルド
cd frontend
npm install
npm run typecheck
npm run lint
npm run test
npm run build
cd ..

# 4. インフラ + 全サービスを起動（dev）。scripts/compose-up.sh は実 Git コミット ID を
#    構成情報 API 用に自動注入する compose ラッパ（docker compose への薄いラッパ）。
bash scripts/compose-up.sh up -d

# 5. 起動確認
#   - フロントエンド: http://localhost:3100
#   - BFF:            http://localhost:5000
#   - Keycloak:       http://localhost:8080
#   - Wiki.js（dev限定の管理UI直接アクセス）: http://localhost:3001
#   - Grafana:        http://localhost:3000
```

内部サービス（DocumentService 等）は `expose` のみでホスト非公開（[IADR-0017](docs/adr/IADR-0017_internal-service-auth-network-isolation.md)）。
外部からの入口は BFF とフロントエンドのみである。

## 仕様書・ドキュメントの入口

- **仕様書の全体像**: [`docs/README.md`](docs/README.md)（作業仕様書・機能/画面/API/データ/技術/テスト/運用/セキュリティ仕様書・実装ADR の配置規約）
- **作業仕様書**（作業/PR 単位）: [`docs/specs/`](docs/specs/)
- **機能仕様書**（FR 単位）: [`docs/functional/`](docs/functional/)
- **画面仕様書**（SC 単位）: [`docs/screens/`](docs/screens/)
- **実装ADR**（`IADR-XXXX`、重要な実装判断の記録）: [`docs/adr/`](docs/adr/)
- **技術要件書**: [`docs/tech/tech-requirements.md`](docs/tech/tech-requirements.md)
- **運用仕様書**（デプロイ・監視・障害対応）: [`docs/operations/operations.md`](docs/operations/operations.md)
- **セキュリティ仕様書**（認証・認可・データ保護）: [`docs/security/security.md`](docs/security/security.md)
- **使い方・デプロイの how-to**: [`docs/how-to/local-development.md`](docs/how-to/local-development.md)・[`docs/how-to/deployment.md`](docs/how-to/deployment.md)
- **完了の定義**（PR を出す前のチェックリスト）: [`docs/DEFINITION_OF_DONE.md`](docs/DEFINITION_OF_DONE.md)
- **AI 駆動の実装ワークフロー全体**: [`docs/ai-workflow.md`](docs/ai-workflow.md)
- **計画リポジトリの参照**: `planning/`（submodule、既定パス）。要求・ユースケース・画面設計・ADR の一次情報。

## 技術スタック（要約）

バックエンドは .NET 10 / C# 13、フロントエンドは React 18 + TypeScript 5.6 + Vite 5。詳細な規約
（命名規則・パッケージ管理・lint/format・サービス境界等）は [`CLAUDE.md`](CLAUDE.md) の
「技術スタック別ルール」を参照。

## Git 運用

`develop` を安定版とし、直接コミットしない。作業ブランチ → プルリクエスト経由でマージする。
コミットメッセージ・PR タイトルは `種別(起点ID): 要約`（例: `feat(FR-12): ...`）の規約に従う
（詳細は [`.claude/rules/traceability.md`](.claude/rules/traceability.md)）。

## ライセンス

[MIT License](LICENSE)
