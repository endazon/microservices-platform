---
title: データソース登録・同期・カタログ化 テスト仕様書
type: test-spec
status: in-progress
related_ids:
  - FR-01
  - UC-04
author: claude
created: 2026-07-04
updated: 2026-07-04
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
---

# テスト仕様書: データソース登録・同期・カタログ化

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-01
- ユースケース（UC）: UC-04
- 関連 ADR: ADR-0002（DB per Service）、ADR-0003（MassTransit + RabbitMQ）
- 実装 ADR: IADR-0001（カタログ正本は DocumentService が所有）
- 計画書リンク: `02_requirements/01_requirements.md`, `07_adr/ADR-0003`

## テスト対象・範囲

- 対象: `DataSourceService` のデータソース CRUD（`/datasources`）、手動同期トリガ（`POST /datasources/{id}/sync`）、同期時の `RawDocumentFetched` 発行。
- 対象: 登録エンティティのライフサイクル（`DataSource.Create` / `RecordSync` / 既定 `Status=active`）。
- 対象外: 実コネクタ（FTP/Confluence/DB/SaaS API）による原本取得、pandoc 実変換、Markdown 実取得（現状スタブ）。
- 対象外: 正規化文書→カタログ登録の連鎖（`DocumentService` の `DocumentNormalizedConsumer` 側で検証）、ABAC による権限外文書の非表示（AuthorizationService 側）、負荷/p95。

## テスト観点

- 正常系: データソース登録後に一覧へ現れること、同期トリガが受理（202）されること、一覧取得が 200 を返すこと。
- 異常系: （後続）存在しないデータソースへの同期は 404。現状の統合テストは既知（登録済み）のデータソースのみを対象とする。
- 境界値: 空 `config` での登録、日本語名称（`社内 Confluence`）の登録・往復。
- 非機能: 統合テストは実 PostgreSQL / RabbitMQ（TestContainers）を用い、コンテナ非利用環境では `DockerFact` によりスキップされる。

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分（自動/手動） |
| --- | --- | --- | --- | --- | --- |
| T-01 | PostgreSQL / RabbitMQ 稼働、DB 初期化済 | `POST /datasources`（name=社内 Confluence, sourceType=Confluence, config={spaceKey:PROJ}）→ `GET /datasources` | 登録が 201 Created、`Name` 一致、`Status` が active、一覧に当該 `Id` を含む | データソース登録・カタログ化 | 自動 |
| T-02 | 同上 | `POST /datasources`（sourceType=SharePoint, config={}）で登録後、`POST /datasources/{id}/sync` | 同期が 202 Accepted（`RawDocumentFetched` 発行・`LastSyncedAt` 記録） | 同期トリガ | 自動 |
| T-03 | サービス起動（実バックエンド不要、TestWebApplicationFactory） | `GET /datasources` | 200 OK（一覧取得の配線確認） | データソース一覧 | 自動 |
| T-04 | 同上 | `GET /health/live` | 200 OK（稼働性・個別デプロイの前提） | 個別デプロイ・稼働性 | 自動 |

## テストデータ

- 登録リクエスト: `{ name: "社内 Confluence", sourceType: "Confluence", connectionUri: "https://confluence.example.com", config: { spaceKey: "PROJ" } }`（T-01）。
- 登録リクエスト: `{ name: "Sync テスト", sourceType: "SharePoint", connectionUri: "https://sp.example.com", config: {} }`（T-02）。
- 同期時に発行される `RawDocumentFetched`（スタブ）: 固定パス `/sample/path/document.docx`、`storage://{dataSourceId}/{fetchId}/raw`、MIME=Word 文書。
- レスポンス DTO: `DataSourceResponse(Id, Name, SourceType, ConnectionUri, Status)`。

## 関連仕様

- 機能仕様書: `../functional/FR-01_data-source-catalog.md`
- 作業仕様書: `../specs/20260627_FR-01_data-source-catalog-pipeline.md`
- データ仕様書: `../data/data-source.md`
- 実装 ADR: `../adr/IADR-0001_document-service-owns-catalog.md`
- テストコード: `src/Tests/KnowledgePlatform.IntegrationTests/DataSourceService/DataSourceTests.cs`, `src/Services/DataSourceService/tests/DataSourceService.Api.Tests/HealthEndpointTests.cs`

## 未決事項

- 存在しないデータソースへの同期（404）・無効化（`DELETE /datasources/{id}` → `disabled`）のケースは実装済みだが統合テスト未整備。
- 正規化文書→カタログ登録の end-to-end 検証は `DocumentService` の統合テスト（`DocumentNormalizedSyncTests`）で担保。
- 実コネクタ・実変換・同期ジョブ進捗管理、負荷試験による p95 レイテンシは後続タスク。
