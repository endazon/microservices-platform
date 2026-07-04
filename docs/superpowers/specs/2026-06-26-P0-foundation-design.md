---
title: P0 基盤整備フェーズ — 設計ドキュメント
type: design
status: approved
author: claude
created: 2026-06-26
updated: 2026-06-26
plan_refs:
  - "../../../planning/projects/microservices-platform/06_technical/01_architecture-overview.md"
  - "../../../planning/projects/microservices-platform/06_technical/02_service-decomposition.md"
  - "../../../planning/projects/microservices-platform/06_technical/03_tech-stack-selection.md"
  - "../../../planning/projects/microservices-platform/06_technical/06_migration-roadmap.md"
related_adrs:
  - ADR-0001 マイクロサービスアーキテクチャの採用
  - ADR-0002 サービス境界とDatabase per Service
  - ADR-0003 MassTransit+RabbitMQ
  - ADR-0004 Keycloak + ABAC
  - ADR-0005 Istio/mTLS
  - ADR-0006 可観測性スタック
  - ADR-0007 ArgoCD+Helm+Harbor
  - ADR-0008 Kubernetes/k3s
---

# P0 基盤整備フェーズ — 設計ドキュメント

## 概要

本書は、社内ナレッジ活用プラットフォームの移行ロードマップ P0 フェーズの実装設計を定義する。
P0 のゴールは「実行・運用基盤を用意し、サービスを 1 つデプロイ・ロールバックでき、メトリクス／ログ／トレースが収集される」状態を実現することである。

## スコープ

P0 フェーズで実装・設定する内容：
1. .NET 8 モノリポジトリ構成（全サービスのスケルトン）
2. 共有ライブラリ（`Shared.Contracts` / `Shared.Infrastructure`）
3. docker-compose.yml によるローカル開発環境
4. CI/CD ワークフローの有効化
5. Helm チャートの基本骨格
6. 仕様書類の充填（技術要件書・セキュリティ仕様書・運用仕様書）

P0 スコープ外（P1 以降で対応）：
- 各サービスのビジネスロジック実装
- Qdrant / LLM ゲートウェイの本実装
- ABAC ポリシーエンジン実装
- k3s 本番環境の構成

## アーキテクチャ設計

### リポジトリ構成

```
microservices-platform/
├── src/
│   ├── KnowledgePlatform.sln
│   ├── Shared/
│   │   ├── KnowledgePlatform.Shared.Contracts/   # イベント・DTO
│   │   └── KnowledgePlatform.Shared.Infrastructure/ # 横断インフラ
│   ├── Services/
│   │   ├── DocumentService/                        # 文書管理
│   │   ├── DataSourceService/                      # データソース連携
│   │   ├── ConversionService/                      # 変換（pandoc + LLM）
│   │   ├── IngestionService/                       # 取り込み（埋め込み・索引）
│   │   ├── RetrievalService/                       # 検索（ハイブリッド）
│   │   ├── AiAnalysisService/                      # AI 分析・RAG
│   │   ├── AuthorizationService/                   # 認可 ABAC
│   │   └── WikiService/                            # Wiki.js 連携
│   ├── Bff/
│   │   └── KnowledgePlatform.Bff/                 # BFF（Web/モバイル集約）
│   └── Gateway/
│       └── LlmGateway/                             # LLM ゲートウェイ
├── deploy/
│   ├── docker-compose.yml                          # ローカル開発環境
│   ├── docker-compose.override.yml                 # 開発用上書き
│   └── helm/
│       ├── knowledge-platform/                     # 親 Helm チャート
│       └── charts/                                 # サービス別チャート
├── docs/                                           # 仕様書（既存）
└── scripts/                                        # セットアップスクリプト（既存）
```

### 設計上の選択肢と決定

#### 選択肢 A: モノリポジトリ（採用）
- 全サービスを 1 つの git リポジトリ・1 つの .NET ソリューションで管理
- 共有ライブラリの参照が容易、変更の影響範囲が一目で把握できる
- チーム規模（3〜4 チーム）に適した調整コスト
- **ADR-0001 準拠**（マイクロサービスだが、リポジトリはモノリポが選択可能）

#### 選択肢 B: サービスごとに別リポジトリ
- より強い独立性、ただし契約変更時の調整コストが増大
- 現段階では過剰（P3 で必要に応じて分割を検討）

#### 選択肢 C: モジュラーモノリスから開始
- ADR-0001 でマイクロサービス採用が決定済みのため却下

**採用: 選択肢 A（モノリポジトリ）**

### サービス構成

各サービスは以下の統一構造を持つ：

```
{ServiceName}/
  src/
    {ServiceName}.Api/          # Web API（REST または Worker）
      Controllers/              # REST エンドポイント（API の場合）
      Consumers/                # MassTransit メッセージハンドラ（Worker の場合）
      Program.cs
      appsettings.json
      Dockerfile
      {ServiceName}.Api.csproj
```

**サービス種別分類：**
| サービス | 種別 | 主要通信 |
|---|---|---|
| DocumentService | REST API | 同期 REST + イベント発行 |
| DataSourceService | REST API + Worker | REST + イベント発行 |
| ConversionService | Worker | イベント購読（MassTransit） |
| IngestionService | Worker | イベント購読（MassTransit） |
| RetrievalService | REST API | 同期 REST |
| AiAnalysisService | REST API（ストリーム） | 同期 REST + LLM GW |
| AuthorizationService | REST API | 同期 REST |
| WikiService | REST API + Worker | REST + Wiki.js GraphQL |
| LlmGateway | REST API | プロバイダ抽象化 |
| Bff | REST API | 集約 + Redis キャッシュ |

### 共有ライブラリ設計

#### `KnowledgePlatform.Shared.Contracts`
MassTransit が使うイベント・メッセージ型と共通 DTO を定義する。

```csharp
// 主要イベント
namespace KnowledgePlatform.Shared.Contracts.Events;

record RawDocumentFetched(Guid SourceId, string SourceType, string OriginalPath, string StorageUri, DateTimeOffset FetchedAt);
record DocumentNormalized(Guid DocumentId, string MarkdownStorageUri, DateTimeOffset NormalizedAt);
record DocumentUpdated(Guid DocumentId, string Title, DateTimeOffset UpdatedAt);
record IngestionRequested(Guid DocumentId, Guid JobId, DateTimeOffset RequestedAt);
record IngestionCompleted(Guid DocumentId, Guid JobId, int ChunkCount, DateTimeOffset CompletedAt);
```

#### `KnowledgePlatform.Shared.Infrastructure`
全サービスで使う横断的インフラの拡張メソッドを定義する。

```csharp
// OpenTelemetry + HealthChecks + Auth の一括設定
services.AddKnowledgePlatformObservability(config);
services.AddKnowledgePlatformAuth(config);
services.AddKnowledgePlatformHealthChecks();
```

### docker-compose ローカル環境

ローカル開発で必要なインフラサービス：

| サービス | イメージ | 用途 |
|---|---|---|
| postgres | postgres:16-alpine | 各サービスの業務 DB（スキーマ分割） |
| rabbitmq | rabbitmq:3-management-alpine | メッセージング（MassTransit） |
| redis | redis:7-alpine | BFF キャッシュ・セッション |
| qdrant | qdrant/qdrant | ベクトル DB |
| keycloak | quay.io/keycloak/keycloak:24 | 認証 ID 基盤 |
| grafana | grafana/grafana | メトリクス可視化 |
| prometheus | prom/prometheus | メトリクス収集 |
| loki | grafana/loki | ログ収集 |
| tempo | grafana/tempo | 分散トレース |
| otel-collector | otel/opentelemetry-collector-contrib | OTel コレクター |

### 可観測性（OpenTelemetry）

全サービスで統一的に実装する：
- **メトリクス**: Prometheus エクスポーター経由
- **ログ**: Serilog + OTLP エクスポーター → Loki
- **トレース**: OpenTelemetry SDK + OTLP エクスポーター → Tempo
- **サービス名**: `knowledge-platform.{service-name}` で統一

### ヘルスチェック

全サービスで ASP.NET Core HealthChecks を標準実装：
- `/health/live` — Liveness（プロセス生死）
- `/health/ready` — Readiness（依存サービスの疎通確認）

### CI/CD

既存の `.example` ワークフローを有効化：
- `.github/workflows/ci.yml` — lint/build/test/coverage
- `.github/workflows/security.yml` — gitleaks/dependency-review
- `.github/workflows/changelog.yml` — CHANGELOG 自動生成
- `.github/workflows/openapi.yml` — OpenAPI スケルトン生成

## データ管理方針（ADR-0002 準拠）

- 各サービスは専用 PostgreSQL スキーマを持つ（`document_svc`, `datasource_svc` 等）
- ローカル開発では 1 つの PostgreSQL インスタンスにスキーマ分割
- 本番（k3s）では DB-per-service を徹底（P2 以降でセットアップ）

## エラー処理・再試行（ADR-0003 準拠）

- MassTransit の組み込み再試行ポリシーを使う
- デッドレターキュー（DLQ）に失敗メッセージを転送
- サーキットブレーカーは BFF の Polly で実装

## セキュリティ方針（ADR-0004, ADR-0005 準拠）

- P0 では JWT 検証のみ（Keycloak OIDC）
- ABAC は P2 で実装。P0・P1 では認証のみ
- サービス間通信は P0 では HTTP（ローカル docker-compose）。k3s 環境では Istio mTLS

## 実装言語・バージョン

| 項目 | 採用 |
|---|---|
| 言語 | C# 12 |
| ランタイム | .NET 8 (LTS) |
| Web フレームワーク | ASP.NET Core 8 Minimal APIs |
| ORM | Entity Framework Core 8 |
| メッセージング | MassTransit 8 |
| DI コンテナ | Microsoft.Extensions.DependencyInjection |
| ログ | Serilog |
| HTTP クライアント | Refit / HttpClientFactory |
| テスト | xUnit + FluentAssertions + Testcontainers |

## 実装順序

以下の順序で実装する：
1. 仕様書充填（tech-requirements, security, operations）
2. 作業仕様書作成（docs/specs/）
3. 共有ライブラリ（Contracts, Infrastructure）
4. 各サービススケルトン（10 サービス）
5. docker-compose.yml
6. Helm チャート骨格
7. CI/CD 有効化

## 成功基準（P0 完了条件）

- [ ] `docker-compose up` でインフラが起動する
- [ ] 各サービスの `/health/ready` が 200 を返す
- [ ] BFF の `/health/ready` が全サービスの疎通確認を行い 200 を返す
- [ ] Grafana でメトリクス・ログ・トレースが確認できる
- [ ] `dotnet build` がエラーなく完了する
- [ ] CI ワークフローが pass する
