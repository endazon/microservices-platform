---
title: 作業仕様書 — FR-01 データソース同期→カタログ化パイプラインの接続
type: spec
status: completed
related_ids:
  - FR-01
  - UC-04
author: claude
created: 2026-06-27
updated: 2026-06-27
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-01)"
  - "../../planning/projects/microservices-platform/03_usecases/ (UC-04)"
related_specs:
  - ./20260626_P0_infrastructure-skeleton.md
  - ../functional/FR-01_data-source-catalog.md
  - ../adr/IADR-0001_document-service-owns-catalog.md
related_adrs:
  - ADR-0002 (サービス境界・DB per Service)
  - ADR-0003 (MassTransit + RabbitMQ)
---

# 作業仕様書: FR-01 データソース同期→カタログ化パイプラインの接続

## 目的

FR-01「複数の社内データソース（ファイルサーバー／Wiki／業務DB／SaaS）を登録・同期し、カタログ化する」の
イベント駆動パイプラインのうち、**未接続だった「正規化文書→カタログ登録」区間を接続**し、
データソース同期から検索インデックス化までの一連の流れを成立させる。

## 背景・現状（調査結果）

P0 基盤整備（[20260626_P0_infrastructure-skeleton](./20260626_P0_infrastructure-skeleton.md)）により、
FR-01 関連サービスの骨格と大半のロジックは実装済みである。現状のイベント連鎖は以下のとおり。

```
POST /datasources/{id}/sync (DataSourceService)
  └─ publish RawDocumentFetched
       └─ ConversionService.RawDocumentFetchedConsumer
            └─ publish DocumentNormalized
                 └─ ❌ 購読者なし（カタログ登録されない）
```

`ConversionService` は変換完了後に `DocumentNormalized` を発行するが、これを購読して
`DocumentService`（カタログ）へ文書を登録する Consumer が存在しないため、
**同期した文書がカタログに現れず、後続の取り込み（IngestionService）・検索（RetrievalService）にも到達しない**。
これは FR-01 の中核要件「カタログ化する」が機能していないことを意味する。

現状、文書は `POST /documents` API 経由でのみ登録され、自動同期フローが途切れている。

## 作業範囲

### 含むもの（本 PR）

- `DocumentService` に `DocumentNormalizedConsumer` を新設し、`DocumentNormalized` を購読してカタログへ登録する。
  - イベントの `DocumentId` を用いてカタログ文書を作成（同一 ID で再配信されても冪等に upsert）。
  - 登録後 `DocumentUpdated` を発行し、後続の取り込み（IngestionService）・Wiki 同期（WikiService）へ連鎖させる。
- `DocumentService` の `Document` ドメインに、ID 指定で正規化文書を生成する `CreateNormalized` ファクトリを追加。
- `Program.cs` に Consumer を登録。
- 統合テスト（実 PostgreSQL / RabbitMQ）で「`DocumentNormalized` 発行→カタログ登録」を検証。

### 含まないもの（後続タスク）

- 各データソースの実コネクタ（FTP/ファイルサーバー、Confluence/Wiki、業務DB ドライバ、SaaS API）。
  現状 `POST /datasources/{id}/sync` は固定のダミー文書を発行するスタブ。
- オブジェクトストレージからの実ファイル取得・pandoc 実変換・Markdown 実取得（各サービスでスタブ）。
- 同期ジョブの進捗・状態管理エンドポイント。
- 検索結果への属性・タグ復元（`QdrantVectorStore` が空配列を返す既知の欠陥）。
- 負荷試験による p95 レイテンシ確認。
- ABAC による「権限の無い文書は一切現れない」の網羅検証（AuthorizationService 側で別途）。

これらは別 Issue/PR として `docs/specs/` に作業仕様書を起票して進める。

## 受け入れ基準（本 PR の範囲）

- [ ] `DocumentNormalized` を発行すると、`DocumentService` のカタログに当該文書が
      `status=normalized`・`MarkdownUri` 付きで登録される。
- [ ] 同一 `DocumentId` の `DocumentNormalized` を再配信しても重複登録されない（冪等）。
- [ ] カタログ登録後 `DocumentUpdated` が発行され、既存の取り込み・Wiki 同期フローへ連鎖する。
- [ ] 既存テストが壊れない（`dotnet build` / 既存ユニット・統合テスト pass）。

## Issue 受け入れ基準との対応

| Issue 受け入れ基準 | 本 PR | 備考 |
| --- | --- | --- |
| 横断検索・出典付与 | 部分前進 | カタログ接続により同期文書が検索対象に乗る。横断検索 UI/出典整形は別途。 |
| 権限外文書の非表示 | 範囲外 | ABAC（FR-05 / AuthorizationService）で対応。 |
| 更新の N 分以内反映 | 基盤前進 | パイプライン接続が前提。コネクタ・スケジューラは後続。 |
| 個別デプロイ・ロールバック | 既達 | サービス分割（ADR-0002）で担保済み。 |
| p95 レイテンシ | 範囲外 | 負荷試験は後続タスク。 |

## 実装方針

[IADR-0001](../adr/IADR-0001_document-service-owns-catalog.md) を参照。
カタログ（正規化文書の正本）の所有権は `DocumentService` に置き、`DocumentNormalized` の購読を
`DocumentService` が担う。文書 ID はパイプライン全体で一貫させるため、`DocumentNormalized.DocumentId` を採用する。

## テスト方針

- 統合テスト `DocumentNormalizedSyncTests`（`src/Tests/KnowledgePlatform.IntegrationTests/DocumentService/`）。
  - `DocumentNormalized` を bus へ発行 → `GET /documents/{id}` でカタログ登録を確認。
  - 既存 `WikiSyncTests` / `DocumentCrudTests` のパターン（TestContainers + `DockerFact`）に準拠。
- `IntegrationTestFactory` の `DocumentServiceFactory` に Consumer を登録。

## リスク・注意事項

- `ConversionService` は変換のたびに新しい `DocumentId` を採番するため、現状の再同期では新規文書として
  カタログに追加される（重複の業務的判定は実コネクタ整備時に `SourceId`＋出自キーで対応する）。
  本 PR の冪等性は「同一イベントの再配信」に対するもの。
- スキーマ変更（マイグレーション）は行わない。`Document` 既存カラムのみで実装する。

## 完了条件（Definition of Done 参照）

`docs/DEFINITION_OF_DONE.md` 準拠。ビルド成功・テスト pass・トレーサビリティ ID 付与。
