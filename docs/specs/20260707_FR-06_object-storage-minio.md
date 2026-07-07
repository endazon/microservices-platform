---
title: 作業仕様書 — FR-06/FR-12 オブジェクトストレージ実体（MinIO）配備と IObjectStore 本実装
type: work-spec
status: completed
related_ids:
  - FR-06
  - FR-12
  - UC-03
  - UC-06
author: claude
created: 2026-07-07
updated: 2026-07-07
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-06, FR-12)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-03, UC-06)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0015_object-storage-minio.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0014_object-storage.md"
related_specs:
  - ./20260703_FR-12_document-normalization-pipeline.md
  - ../adr/IADR-0024_object-storage-minio-buckets-and-access.md
  - ../adr/IADR-0008_conversion-ports-deny-by-default-and-idempotent-id.md
related_adrs:
  - ADR-0015 (オブジェクトストレージ製品を MinIO に確定)
  - ADR-0014 (本文・資産はオブジェクトストレージ、メタデータ＋参照方式)
  - IADR-0024 (バケット/キー設計・バージョニング・アクセス制御)
---

# 作業仕様書: FR-06/FR-12 オブジェクトストレージ実体（MinIO）配備と `IObjectStore` 本実装

## 目的（Issue #99）

計画 [ADR-0015](../../planning/projects/microservices-platform/07_adr/ADR-0015_object-storage-minio.md)
（Accepted、製品を MinIO に確定）の決定に従い、オブジェクトストレージの実体を配備・結線する。
現状は `StorageObjectStore` が保存せず `storage://` 形式の決定的 URI を発行するだけの開発用スタブで、
本文 Markdown・変換資産が永続化されず、読み取り側（`StorageDocumentContentReader` /
`StorageMarkdownReader`）もプレースホルダー本文を返していた（計画リポ精査 乖離2）。本作業でこれを解消する。

## 背景・現状（調査結果）

- 書き込み側 `ConversionService.Worker.Services.StorageObjectStore` は URI を発行するだけで永続化しない。
- 読み取り側は `http(s)` のみ実取得し、`storage://` はプレースホルダーへ縮退していた。
- 参照側（DB 設計・ポート抽象 `IObjectStore`、決定的 `DocumentId` / キー設計 [IADR-0008](../adr/IADR-0008_conversion-ports-deny-by-default-and-idempotent-id.md)）は実装済み。

## 実装方針

1. **共有クライアント**: `KnowledgePlatform.Shared.Infrastructure/Storage/` に S3 互換
   （`AWSSDK.S3`）クライアント `IObjectStorageClient` / `S3ObjectStorageClient` を新設する。
   参照 URI は `storage://<bucket>/<key>`（`StorageUri`）で表す（IADR-0008 の決定的キーと整合）。
   未配備（`ObjectStorage:Endpoint` 未設定）の dev/test では縮退 `NullObjectStorageClient` を登録する。
2. **書き込み側の結線**: `StorageObjectStore` を共有クライアントへ委譲する実装へ置き換える。
   ConversionService 起動時にバケット存在・バージョニングを保証する（`ObjectStorageBootstrapHostedService`）。
3. **読み取り側の結線**: `StorageDocumentContentReader`（Ingestion）・`StorageMarkdownReader`（Wiki）が
   `storage://` を `IObjectStorageClient` で実解決する。`http(s)` の実取得・未配備時の縮退は維持する。
4. **配備**: `deploy/docker-compose.yml` と `deploy/helm/` に MinIO を追加する。資格情報は
   compose は `.env`（`MINIO_ROOT_USER`/`MINIO_ROOT_PASSWORD`）、helm は Secret（`minio-credentials`）経由。
5. **アクセス制御**（ADR-0014）: MinIO API は host 非公開（compose は `expose`、helm は Ingress なし）。
   読み取りは ABAC を強制するサービス（Ingestion/Wiki）経由のサーバサイド読み取りに限定する
   （[IADR-0017](../adr/IADR-0017_internal-service-auth-network-isolation.md) と整合）。
   ABAC 判定後の一時 DL 用に署名付き URL 発行 API を用意する（現時点で公開経路には未結線）。

詳細な設計判断（バケット/キー設計・バージョニング・バックアップ・アクセス制御）は
[IADR-0024](../adr/IADR-0024_object-storage-minio-buckets-and-access.md) に記録する。

## 受け入れ基準（Issue #99）と対応

- [x] 変換結果（本文 Markdown・資産）が MinIO に永続化され、取り込み・閲覧が実本文で動作する
  （プレースホルダの解消）→ 書き込み/読み取り両側を実クライアントへ結線。ラウンドトリップ統合テスト。
- [x] オブジェクトへの直接アクセス経路が公開されない（ABAC 経由のみ）→ MinIO API を host/Ingress 非公開、
  読み取りは ABAC 強制サービス経由。署名付き URL は認可済み呼び出し元にのみ払い出す設計。
- [x] バケット／キー設計・バージョニング・バックアップ方針が `docs/` に記録される → IADR-0024 起票。

## テスト

- 単体（`ConversionService.Worker.Tests/ObjectStorageTests`）: `storage://` URI 往復・縮退・書き込み委譲。
- 統合（`IntegrationTests/Storage/ObjectStorageRoundTripTests`, MinIO Testcontainers）:
  保存→取得ラウンドトリップ、資産バイナリ、冪等な再変換（同一キー上書き）、署名付き URL 発行。

## 影響範囲

- 追加: `Shared.Infrastructure/Storage/*`, `Extensions/ObjectStorageExtensions.cs`, MinIO 配備、テスト、IADR-0024。
- 変更: `StorageObjectStore` / `StorageDocumentContentReader` / `StorageMarkdownReader` と各 `Program.cs`、
  `docker-compose.yml`、`helm/values.yaml`・`templates/`、`Directory.Packages.props`。
