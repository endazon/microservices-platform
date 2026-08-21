---
title: 作業仕様書 — P0 基盤整備・スケルトン構築
type: spec
status: completed
related_ids:
  - FR-01
  - FR-02
  - FR-03
  - FR-04
  - FR-05
  - FR-06
author: claude
created: 2026-06-26
updated: 2026-06-26
plan_refs:
  - planning:projects/microservices-platform/06_technical/06_migration-roadmap.md
  - planning:projects/microservices-platform/06_technical/01_architecture-overview.md
  - planning:projects/microservices-platform/06_technical/02_service-decomposition.md
related_specs:
  - ../../docs/tech/tech-requirements.md
  - ../../docs/security/security.md
  - ../../docs/operations/operations.md
  - ../adr/IADR-0000_record-implementation-decisions.md
related_adrs:
  - ADR-0001 (マイクロサービス採用)
  - ADR-0002 (サービス境界・DB per Service)
  - ADR-0003 (MassTransit + RabbitMQ)
  - ADR-0004 (Keycloak + ABAC)
  - ADR-0005 (Istio/mTLS)
  - ADR-0006 (可観測性スタック)
  - ADR-0007 (ArgoCD + Helm + Harbor)
  - ADR-0008 (Kubernetes / k3s)
---

# 作業仕様書: P0 基盤整備・スケルトン構築

## 目的

社内ナレッジ活用プラットフォームの移行ロードマップ P0「基盤整備」フェーズを実施する。
本作業により、以降の P1〜P3 実装に必要なプロジェクト骨格・ローカル開発環境・CI/CD を確立する。

## 計画書リンク

- 移行ロードマップ: `06_migration-roadmap.md`（計画リポ）（P0 フェーズ〜Week 4）
- アーキテクチャ概要: `01_architecture-overview.md`（計画リポ）
- サービス分割設計: `02_service-decomposition.md`（計画リポ）

## 実装 ID トレーサビリティ

- ブランチ: `init-setup`（初期セットアップブランチとして使用）
- 起点: P0 基盤整備（計画書フェーズ P0 直接）

## 作業範囲

### 含むもの
- `docs/` 仕様書の充填（技術要件書・セキュリティ・運用）
- .NET 8 ソリューション + 全サービスプロジェクトのスケルトン
- 共有ライブラリ（Shared.Contracts, Shared.Infrastructure）
- docker-compose.yml（ローカル開発環境）
- Helm チャート基本骨格
- CI/CD ワークフロー有効化（`.example` → 有効）

### 含まないもの（P1 以降）
- 各サービスのビジネスロジック実装
- Qdrant / LLM ゲートウェイの本実装
- ABAC ポリシーエンジン実装
- k3s 本番環境の Kubernetes マニフェスト

## 受け入れ基準

- [ ] `dotnet build src/KnowledgePlatform.sln` がエラーなく完了する
- [ ] `docker-compose up -d` でインフラ（PostgreSQL, RabbitMQ, Redis, Qdrant, Keycloak, 可観測性）が起動する
- [ ] 各サービスの `/health/live` が 200 を返す
- [ ] BFF の `/health/ready` が 200 を返す
- [ ] CI ワークフローが有効化されている（`.example` が削除されている）
- [ ] Grafana ダッシュボードが起動し、メトリクス/ログ/トレースのエンドポイントに到達できる

## チェックリスト

### 仕様書
- [ ] `docs/tech/tech-requirements.md` — 技術スタック充填
- [ ] `docs/security/security.md` — セキュリティ方針充填
- [ ] `docs/operations/operations.md` — 運用方針充填

### ソリューション構造
- [ ] `src/KnowledgePlatform.sln` 作成
- [ ] `src/Shared/KnowledgePlatform.Shared.Contracts/` 作成
- [ ] `src/Shared/KnowledgePlatform.Shared.Infrastructure/` 作成
- [ ] `src/Services/DocumentService/` スケルトン
- [ ] `src/Services/DataSourceService/` スケルトン
- [ ] `src/Services/ConversionService/` スケルトン（Worker）
- [ ] `src/Services/IngestionService/` スケルトン（Worker）
- [ ] `src/Services/RetrievalService/` スケルトン
- [ ] `src/Services/AiAnalysisService/` スケルトン
- [ ] `src/Services/AuthorizationService/` スケルトン
- [ ] `src/Services/WikiService/` スケルトン
- [ ] `src/Bff/KnowledgePlatform.Bff/` スケルトン
- [ ] `src/Gateway/LlmGateway/` スケルトン

### インフラ
- [ ] `deploy/docker-compose.yml` 作成
- [ ] `deploy/docker-compose.override.yml` 作成（ローカル開発設定）
- [ ] 各サービスの `Dockerfile` 作成
- [ ] `deploy/helm/` Helm チャート骨格

### CI/CD
- [ ] `.github/workflows/ci.yml` 有効化
- [ ] `.github/workflows/security.yml` 確認（既存）

## 依存関係・前提条件

- 計画リポジトリ（サブモジュール `planning/`）が利用可能であること（✓ 確認済み）
- .NET 8 SDK がインストールされていること
- Docker が利用可能であること（ローカル実行）

## リスク・注意事項

- P0 スコープはスケルトンのみ。ビジネスロジックは P1 以降で実装する。
- docker-compose でのサービス間通信はコンテナ名 DNS を使う（`rabbitmq`, `postgres` 等）。
- 認証（Keycloak）は P0 では初期設定のみ。本番 Realm 設定は P2 で整備する。

## 完了条件（Definition of Done 参照）

`docs/DEFINITION_OF_DONE.md` 準拠。特に：
- ビルド成功
- ヘルスチェック疎通
- CI pass
