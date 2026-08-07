---
title: データソース登録・同期・カタログ化 テスト仕様書
type: test-spec
status: implemented
related_ids:
  - FR-01
  - UC-04
author: claude
created: 2026-07-04
updated: 2026-08-07
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
---

# テスト仕様書: データソース登録・同期・カタログ化

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-01
- ユースケース（UC）: UC-04
- 関連 ADR: ADR-0002（DB per Service）、ADR-0003（MassTransit + RabbitMQ。Superseded by ADR-0027・注記は #580）
- 実装 ADR: IADR-0001（カタログ正本は DocumentService が所有）
- 計画書リンク: `02_requirements/01_requirements.md`, `07_adr/ADR-0003`（Superseded by `07_adr/ADR-0027`・注記は #580）

## テスト対象・範囲

- 対象: `DataSourceService` のデータソース CRUD（`/datasources`）、手動同期トリガ（`POST /datasources/{id}/sync`）、同期時の `RawDocumentFetched` 発行。
- 対象: 登録エンティティのライフサイクル（`DataSource.Create` / `RecordSync` / 既定 `Status=active`）。
- 対象: filesystem/wiki/saas/db 各コネクタの列挙・取得・増分・縮退（単体・fake HTTP／ADO.NET。T-05〜T-25。#195/#217/#218/#219）。
- 対象外: 製品固有アダプタ（Confluence/Salesforce/Notion 等）と実 API/コンテナ統合、実オブジェクトストレージ（ADR-0014 製品未確定）経由の pandoc 実変換・Markdown 本文取得の end-to-end。
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
| T-02 | 同上 | `POST /datasources`（sourceType=SharePoint, config={}）で登録後、`POST /datasources/{id}/sync` | 同期が 202 Accepted・`LastSyncedAt` 記録（未対応 SourceType は縮退。発行は行われない） | 同期トリガ（配線） | 自動 |
| T-03 | サービス起動（実バックエンド不要、TestWebApplicationFactory） | `GET /datasources` | 200 OK（一覧取得の配線確認） | データソース一覧 | 自動 |
| T-04 | 同上 | `GET /health/live` | 200 OK（稼働性・個別デプロイの前提） | 個別デプロイ・稼働性 | 自動 |
| T-05 | 一時ディレクトリに対応ファイル（.md/.txt/.docx）＋非対応（.bin/.png） | `FileSystemConnector.DiscoverAsync`（since=null） | 対応形式のみ列挙・非対応は除外（フルスキャン） | 実コネクタ列挙（#195/IADR-0051） | 自動（単体） |
| T-06 | 一時ディレクトリに新旧ファイル（更新日時差） | `DiscoverAsync(since=watermark)` | watermark 以前（含む同時刻）を除外し差分のみ返す | 増分同期（#195） | 自動（単体） |
| T-07 | 列挙済み対象 | `FetchAsync` | 原本バイト列と content-type を返す | 原本取得（#195） | 自動（単体） |
| T-08 | ルート未存在／smb:// で rootPath 未指定 | `DiscoverAsync` | 例外にせず空列挙で縮退 | 縮退（#195） | 自動（単体） |
| T-09 | filesystem データソース＋一時 dir に実ファイル | `POST /{id}/sync` | 202・実 `OriginalPath`/`ContentType`・既定属性（confidentiality）付き `RawDocumentFetched` 発行 | 実同期・属性 Map（#195/FR-05） | 自動（エンドポイント） |
| T-10 | 未登録 SourceType（架空種別 `unknown-source`。filesystem/wiki/saas/db は登録済みのため恒久的に未登録の値を用いる） | `POST /{id}/sync` | 202・`connectorAvailable=false`・`fetched=0`・発行なし（縮退） | 未登録型の縮退（#195） | 自動（エンドポイント） |
| T-11 | Wiki（汎用契約）一覧 API がページ配列を返す | `WikiConnector.DiscoverAsync`（since=null / since=watermark） | 全件列挙／`updatedAt>since` で増分 | Wiki 列挙・増分（#217/IADR-0053） | 自動（単体・fake HTTP） |
| T-12 | Wiki 本文 API が Markdown を返す | `WikiConnector.FetchAsync` | 本文バイト＋content-type（応答ヘッダ） | Wiki 取得（#217） | 自動（単体・fake HTTP） |
| T-13 | `Config.apiToken` 設定・`listPath` 設定 | `DiscoverAsync` | `Authorization: Bearer` 送出／設定パスへ GET | Wiki 認証・設定駆動（#217） | 自動（単体・fake HTTP） |
| T-14 | 一覧 API が 5xx／ConnectionUri 未設定 | `DiscoverAsync` | 5xx は例外送出（watermark 非前進）／未設定は空列挙で縮退 | Wiki 失敗時挙動（#217/IADR-0051 決定3a） | 自動（単体・fake HTTP） |
| T-15 | SaaS 一覧 API が nextCursor で複数ページを返す | `SaaSConnector.DiscoverAsync` | 全ページをカーソルで集約・`updatedAt>since` で増分 | SaaS ページング・増分（#218/IADR-0054） | 自動（単体・fake HTTP） |
| T-16 | SaaS 一覧 API が 429（Retry-After:0）→200 | `DiscoverAsync` | Retry-After に従い再試行して成功（2 リクエスト） | SaaS レート制限バックオフ（#218） | 自動（単体・fake HTTP） |
| T-17 | SaaS 一覧 API が 429 継続（maxRetries=1） | `DiscoverAsync` | 上限超過で例外送出（watermark 非前進） | SaaS 上限超過（#218/IADR-0051 決定3a） | 自動（単体・fake HTTP） |
| T-18 | SaaS 本文 API が Markdown／`Config.apiToken`／未設定 | `FetchAsync`/`DiscoverAsync` | 本文＋content-type／`Bearer` 送出／未設定は空列挙 | SaaS 取得・認証・縮退（#218） | 自動（単体・fake HTTP） |
| T-19 | 業務DB クエリが行を返す（fake ADO.NET） | `DatabaseConnector.DiscoverAsync`（since=null / watermark / ISO8601文字列） | 全行を id/updated へマッピング・`updated>since` で増分・文字列日時も正規化 | DB 行→文書・増分（#219/IADR-0055） | 自動（単体・fake ADO.NET） |
| T-20 | 業務DB 本文スカラを返す | `DatabaseConnector.FetchAsync` | 本文バイト＋content-type・id は `@id` パラメータで渡す | DB 取得・パラメータ化（#219） | 自動（単体・fake ADO.NET） |
| T-21 | `Config.query`／`ConnectionUri` 未設定 | `DiscoverAsync` | 空列挙で縮退（接続しない） | DB 縮退（#219） | 自動（単体・fake ADO.NET） |
| T-22 | 業務DB がエラーを返す | `DiscoverAsync` | 例外送出（watermark 非前進） | DB 失敗時挙動（#219/IADR-0051 決定3a） | 自動（単体・fake ADO.NET） |
| T-23 | `updated` 列が NULL の行を含む | `DiscoverAsync` | 当該行のみスキップ＋警告・同期全体は成功 | DB 不正行の縮退（#219・claude-review #224） | 自動（単体・fake ADO.NET） |
| T-24 | Fetch 対象 id が存在しない（消えた行） | `FetchAsync` | 例外にせず空本文へ縮退 | DB 該当なしの縮退（#219・claude-review #224） | 自動（単体・fake ADO.NET） |
| T-25 | `Config.password` に `;`/`'` を含む | `DiscoverAsync` | 接続文字列がクオート合成され特殊文字が往復 | DB パスワードエスケープ（#219・claude-review #224） | 自動（単体・fake ADO.NET） |

## テストデータ

- 登録リクエスト: `{ name: "社内 Confluence", sourceType: "Confluence", connectionUri: "https://confluence.example.com", config: { spaceKey: "PROJ" } }`（T-01）。
- 登録リクエスト: `{ name: "Sync テスト", sourceType: "SharePoint", connectionUri: "https://sp.example.com", config: {} }`（T-02）。
- 同期時に発行される `RawDocumentFetched`（実コネクタ・#195/IADR-0051）: 実 `OriginalPath`（列挙ファイルの絶対パス）、
  `StorageUri`（`IObjectStorageClient` の格納 URI。未構成時は決定的 URI へ縮退）、拡張子由来の `ContentType`、
  データソース既定 ABAC 属性（機密区分フェイルセーフ含む・IADR-0019）、フォルダ名タグ。
- filesystem データソースのルート指定: `config.rootPath`（優先）または `connectionUri`（`file://`／素のパス）。
- レスポンス DTO: `DataSourceResponse(Id, Name, SourceType, ConnectionUri, Status)`。`/sync` 応答: `{ fetched, failed, connectorAvailable, message }`。

## 関連仕様

- 機能仕様書: `../functional/FR-01_data-source-catalog.md`
- 作業仕様書: `../specs/20260627_FR-01_data-source-catalog-pipeline.md`
- データ仕様書: `../data/data-source.md`
- 実装 ADR: `../adr/IADR-0001_document-service-owns-catalog.md`
- テストコード: `src/knowledge/backend/Tests/KnowledgePlatform.IntegrationTests/DataSourceService/DataSourceTests.cs`, `src/knowledge/backend/Services/DataSourceService/tests/DataSourceService.Api.Tests/HealthEndpointTests.cs`
- コネクタ/同期テスト（#195）: `.../DataSourceService.Api.Tests/FileSystemConnectorTests.cs`（T-05〜T-08）、`.../DataSourceSyncEndpointTests.cs`（T-09〜T-10）、`.../DataSourceSyncServiceTests.cs`（watermark 非前進）
- Wiki コネクタテスト（#217）: `.../DataSourceService.Api.Tests/WikiConnectorTests.cs`（T-11〜T-14・fake HttpMessageHandler）
- SaaS コネクタテスト（#218）: `.../DataSourceService.Api.Tests/SaaSConnectorTests.cs`（T-15〜T-18・fake HttpMessageHandler）
- 業務DB コネクタテスト（#219）: `.../DataSourceService.Api.Tests/DatabaseConnectorTests.cs`（T-19〜T-25・ハンドロール ADO.NET フェイク）
- 実装 ADR（追加）: `../adr/IADR-0051_datasource-connector-port-and-filesystem.md`

## 未決事項

- 存在しないデータソースへの同期（404）・無効化（`DELETE /datasources/{id}` → `disabled`）のケースは実装済みだが統合テスト未整備。
- 正規化文書→カタログ登録の end-to-end 検証は `DocumentService` の統合テスト（`DocumentNormalizedSyncTests`）で担保。
- 実オブジェクトストレージ（ADR-0014 製品未確定）経由の pandoc 実変換・Markdown 本文取得の end-to-end 検証、製品固有アダプタの実 API 統合、同期ジョブ進捗管理、負荷試験による p95 レイテンシは後続タスク。
