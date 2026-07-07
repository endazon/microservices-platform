---
title: IADR-0024 MinIO のバケット/キー設計・バージョニング・アクセス制御と共有クライアント
type: impl-adr
status: Accepted
related_ids:
  - FR-06
  - FR-12
  - UC-03
  - UC-06
author: claude
created: 2026-07-07
updated: 2026-07-07
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0015_object-storage-minio.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0014_object-storage.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-06, FR-12)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-03, UC-06)"
related_specs:
  - ../specs/20260707_FR-06_object-storage-minio.md
  - ./IADR-0008_conversion-ports-deny-by-default-and-idempotent-id.md
  - ./IADR-0017_internal-service-auth-network-isolation.md
---

# IADR-0024: MinIO のバケット/キー設計・バージョニング・アクセス制御と共有クライアント

- 状態: Accepted
- 日付: 2026-07-07
- 決定者: claude（実装）
- 関連: FR-06（文書管理）、FR-12（正規化変換）、UC-03/UC-06、ADR-0015（MinIO 確定）、
  ADR-0014（オブジェクトストレージ／メタデータ＋参照）、IADR-0008（決定的 DocumentId）、
  IADR-0017（ネットワーク分離）

## コンテキストと課題

[ADR-0015](../../planning/projects/microservices-platform/07_adr/ADR-0015_object-storage-minio.md) が
製品を MinIO に確定し、実装リポへ「MinIO 配備＋`IObjectStore` アダプタ実装」が引き継がれた（Issue #99）。
従来は `StorageObjectStore` が `storage://` の決定的 URI を発行するだけの開発用スタブで、本文・資産が
永続化されず、読み取り側もプレースホルダーを返していた。実装にあたり、(1) 参照 URI とバケット/キー設計、
(2) バージョニングとバックアップ方針、(3) アクセス制御、(4) クライアントの配置と未配備時の縮退、を決める。

## 検討した選択肢

### A. クライアントライブラリ
1. **`AWSSDK.S3`（本決定）** — ADR-0015 が想定する「AWS SDK 等の標準クライアント」。`ServiceURL`＋
   `ForcePathStyle` で MinIO に接続でき、将来のマネージド S3 へも差し替えやすい。
2. `Minio`（公式 .NET SDK） — MinIO 専用で API は簡潔だが、S3 標準からは外れ差し替え余地が狭い。

### B. 参照 URI とキー設計
1. **`storage://<bucket>/<key>`（本決定）** — 既存の `storage://` スキームを継続（読み取り側の分岐を保持）。
   ただしバケット名・パス構造は旧スタブの発行形式から変更する（下記「互換性に関する注記」）。バケットを host、
   キーを path に写す。キーは IADR-0008 の決定的 `DocumentId` を基点に
   `"<documentId:N>/document.md"`・`"<documentId:N>/assets/<figureId>.<ext>"`（既存の `NormalizationService`）。
2. 生の `s3://` URI — S3 慣習に近いが、既存発行 URI・読み取り側の分岐を作り直す必要がある。

### C. バージョン管理
1. **バケットバージョニング有効＋同一キー上書き（本決定）** — 決定的キー（IADR-0008）で再変換は同一キーを
   上書きし、履歴はバージョニングが保持する。ADR-0014「オブジェクトのバージョニング／キー設計で管理」に合致。
2. キーにバージョン連番を付与 — 参照の一意性が崩れ、冪等な再登録（UC-06 代替フロー）と衝突する。

### D. アクセス制御
1. **直接非公開＋ABAC 強制サービス経由（本決定）** — MinIO API を host/Ingress へ公開せず、読み取りは
   ABAC を強制する Ingestion/Wiki のサーバサイド読み取りに限定（IADR-0017 のネットワーク分離を踏襲）。
   ABAC 判定後の一時 DL 用に署名付き URL 発行 API を用意する（現時点で公開経路へは未結線）。
2. バケットを公開／匿名読み取り — ADR-0014「直接公開しない」に反するため却下。

## 決定

- **A-1 / B-1 / C-1 / D-1 を採用**。共有クライアントを `KnowledgePlatform.Shared.Infrastructure/Storage/`
  に置き、書き込み側（ConversionService）と読み取り側（IngestionService/WikiService）で共有する。
  - `IObjectStorageClient`：`PutTextAsync` / `PutBytesAsync` / `GetTextAsync` / `GetBytesAsync` /
    `CanResolve` / `CreatePresignedGetUrl`。
  - `S3ObjectStorageClient`：`AWSSDK.S3` 実装。起動時に `EnsureBucketAsync`（バケット作成＋バージョニング有効化）。
  - `NullObjectStorageClient`：`ObjectStorage:Endpoint` 未設定の dev/test 向け縮退。保存は決定的 URI を返し、
    `CanResolve=false` で読み取り側をプレースホルダーへ縮退させる（従来挙動を保持）。
- **バケット**: 既定 `knowledge-normalized`（正規化本文＋資産）。設定 `ObjectStorage:Bucket` で上書き可。
  - **互換性に関する注記**: 旧スタブ（本 PR 以前）は保存を伴わず `Storage:NormalizedBaseUri`
    （既定 `storage://knowledge/normalized`）を基点に `storage://knowledge/normalized/<key>` を発行して
    いた。本実装ではバケットを host に写す `storage://<bucket>/<key>`（既定バケット `knowledge-normalized`）
    へ変更したため、発行される URI のバケット名・パス構造は旧形式と非互換である。旧スタブは実体を
    永続化していなかったため既存オブジェクトの破損は生じない。加えて develop 時点で旧形式 URI を DB に
    永続化した文書は存在しない（旧実装は永続化せず、実クライアントは本 PR で初めて有効化される）ため、
    実クライアント有効化に伴う旧バケットへの読み取り失敗も発生しない。以降のデータは新形式で一貫する。
- **配備**: docker-compose と Helm に MinIO を追加。資格情報は compose=`.env`、helm=Secret（`minio-credentials`）。
- **バックアップ・保持方針（運用）**: バケットバージョニングで論理削除・上書き履歴を保持し、実体バックアップは
  MinIO バケット複製（`mc mirror` もしくはボリューム／PVC スナップショット）を定期実行する。保持期間・
  ライフサイクル（古いバージョンの失効）は運用仕様で環境別に定める（本 IADR は既定＝バージョニング有効まで）。

## 理由

- S3 標準クライアントにより実装が局所化し、MinIO↔マネージド S3 の差し替え余地を残せる（ADR-0015 の理由と一致）。
- `storage://` スキーム継続でイベント（`DocumentNormalized.MarkdownUri`）・読み取り分岐を壊さない
  （バケット名・パス構造は変更。既存永続化データが無いため影響なし＝上記「互換性に関する注記」）。
- 決定的キー＋バージョニングは IADR-0008 の冪等性と ADR-0014 の版管理方針を同時に満たす。
- 直接非公開＋サービス経由は ADR-0014 のアクセス制御方針と IADR-0017 のネットワーク分離に整合する。

## 結果

- 良い影響: 本文・資産が実体として永続化され、取り込み・Wiki 同期が実本文で動作する（プレースホルダ解消）。
- トレードオフ: 運用対象ミドルウェア（MinIO）が 1 つ増える（容量監視・バックアップが必要。ADR-0015 記載）。
- フォローアップ（本 IADR に含まないもの）:
  - 署名付き URL の BFF 経由ダウンロード経路への結線（ABAC 判定と統合した一時 DL）。
  - ライフサイクルポリシー（古いバージョンの失効）・バックアップ自動化の運用仕様化。
  - 削除・アーカイブ（IADR-0023）に伴うオブジェクト実体の削除／版整理の同期。

## 関連

- Supersedes: なし（ADR-0015 の実装レベル具体化）
- Superseded by: なし
