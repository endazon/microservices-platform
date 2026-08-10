---
title: 文書CRUD・バージョン管理 テスト仕様書
type: test-spec
status: in-progress
related_ids:
  - FR-06
  - UC-03
author: claude
created: 2026-07-04
updated: 2026-08-10
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
---

# テスト仕様書: 文書CRUD・バージョン管理

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-06
- ユースケース（UC）: UC-03
- 受け入れ基準の所在（02_requirements）: `02_requirements/01_requirements.md`
- 計画書リンク: 同上 / `07_adr/ADR-0002`, `07_adr/ADR-0014`

## テスト対象・範囲

- 対象: `Document` 集約の版管理ドメインロジック（`Create` / `Update` / `UpdateMetadata` / `Publish` とスナップショット追記）、
  文書 CRUD・版・メタデータエンドポイント（`/documents` 系）、`DocumentNormalized` 購読によるカタログ登録（冪等 upsert）。
- 対象外: 横断検索・出典付与（FR-03/FR-04）、ABAC 権限フィルタの網羅（FR-05）、更新反映時間・p95 レイテンシ・負荷試験、画面。

## テスト観点

- 正常系: 作成・取得・一覧・更新、版履歴の増加と新しい順、特定版スナップショットの保持、公開状態遷移、メタデータのみ更新。
- 境界/異常系: タイトル空白（400）、存在しない版（404）、古い期待版による並行制御（409）。
- 不変性: 過去版スナップショットが後続更新で書き換わらない（append-only・防御的コピー）。
- 連携（冪等性）: `DocumentNormalized` 受信でカタログ登録され、同一 `DocumentId` 再配信でも重複しない。

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分（自動/手動） |
| --- | --- | --- | --- | --- | --- |
| T-01 | 属性・タグ付きで作成 | `Document.Create` | 版 1・`Versions` 1 件・版 1 のタイトル一致 | 版1記録 | 自動（単体） |
| T-02 | 作成済み文書 | `Update` を呼ぶ | `Version`=2・スナップショット 2 件・旧版は初版タイトルを保持・`ChangeNote` 記録 | 版追記/append-only | 自動（単体） |
| T-03 | 作成時に属性 `k=v1` | `UpdateMetadata` で `k=v2` | 版 1 スナップショットは `k=v1`・初版タグを保持 | append-only | 自動（単体） |
| T-04 | 作成済み文書 | `UpdateMetadata` を呼ぶ | タイトル不変・`Version`=2 | メタデータのみ更新 | 自動（単体） |
| T-05 | 作成済み文書 | `Publish` を呼ぶ | `Status`=published・最新版スナップショットも published | 公開遷移/版追記 | 自動（単体） |
| T-06 | 起動済み API | `POST /documents`→`PUT`→`GET /versions` | 版 1→2 に増加・一覧 2 件・新しい順（[0]=2,[1]=1） | 版履歴/新しい順 | 自動（エンドポイント） |
| T-07 | 更新済み文書 | `GET /versions/1` と `/versions/99` | 版 1 は初版タイトル・存在しない版は 404 | 特定版取得 | 自動（エンドポイント） |
| T-08 | 作成済み文書 | `PATCH /{id}/metadata` | 200・タイトル維持・属性/タグ更新・`Version`=2 | メタデータのみ更新 | 自動（エンドポイント） |
| T-09 | 現在版 1 の文書 | `PUT` に `expectedVersion=5` | 409 Conflict | 並行制御 | 自動（エンドポイント） |
| T-10 | 作成済み文書 | `POST /{id}/publish` | 200・`Status`=published | 公開遷移 | 自動（エンドポイント） |
| T-11 | 起動済み API | `POST /documents` に空タイトル | 400 BadRequest | タイトル必須 | 自動（エンドポイント） |
| T-12 | 実 PostgreSQL | `POST /documents`→`GET /{id}` | 201・タイトル一致・`Status`=draft | CRUD | 自動（統合） |
| T-13 | 実 PostgreSQL | 2 件作成後 `GET /documents` | 200・件数 >= 2 | CRUD 一覧 | 自動（統合） |
| T-14 | 実 PostgreSQL | 作成→`PUT` でタイトル変更→`GET` | 新タイトルが反映 | CRUD 更新 | 自動（統合） |
| T-15 | 実 PostgreSQL | 作成→`PATCH metadata`（版2）→`PUT`（版3）→`GET /versions` | 一覧 3 件・[0]=版3・版 1 は作成時属性/タイトルを保持 | 版履歴/append-only | 自動（統合） |
| T-16 | 実 PostgreSQL | `PUT` に `expectedVersion=99` | 409 Conflict | 並行制御 | 自動（統合） |
| T-17 | 実 PostgreSQL / RabbitMQ | `DocumentNormalized` を発行 | カタログに `status=normalized`・タイトル/URI 一致で登録 | 正規化取込 | 自動（統合） |
| T-18 | 実 PostgreSQL / RabbitMQ | 同一 `DocumentId` を 2 回発行 | 件数 1 件・タイトルが更新（冪等 upsert） | 正規化取込冪等 | 自動（統合） |
| T-19 | 起動済み API（admin） | `POST /documents` に `attributes` 未指定／`confidentiality` 欠落 | 400 BadRequest | 機密区分必須（UC-03/SC-05, #199） | 自動（エンドポイント） |
| T-20 | 起動済み API（admin） | `POST /documents` に未知の `confidentiality`（例 `secret`・空・大文字） | 400 BadRequest | 機密区分の正準値検証（#199） | 自動（エンドポイント） |
| T-21 | 起動済み API（admin） | `POST /documents` に正準値 `public`/`internal`/`confidential`/`restricted` | 201・属性が保存される | 機密区分受理（#199） | 自動（エンドポイント） |
| T-22 | 作成済み文書 | `PUT`／`PATCH metadata` に `confidentiality` 欠落 | 400／正準値なら 200 | 更新経路も必須検証（#199） | 自動（エンドポイント） |
| T-23 | — | `DocumentAttributes.ValidateConfidentiality`（null／欠落／未知／正準値） | 欠落・未知は NG、正準値は OK | 検証ヘルパー単体（#199） | 自動（単体） |

対応テスト実装:

- 単体（ドメイン）: `src/knowledge/backend/Services/DocumentService/tests/DocumentService.Api.Tests/DocumentVersioningTests.cs`（T-01〜T-05）、`DocumentAttributesTests.cs`（T-23）
- 単体（エンドポイント, InMemory）: `.../DocumentEndpointVersioningTests.cs`（T-06〜T-11）、`DocumentConfidentialityValidationTests.cs`（T-19〜T-22）
- 統合（実 PostgreSQL）: `src/knowledge/backend/Tests/Knowledge.IntegrationTests/DocumentService/DocumentCrudTests.cs`（T-12〜T-14）、`DocumentVersioningTests.cs`（T-15〜T-16）
- 統合（実 PostgreSQL / RabbitMQ）: `.../DocumentNormalizedSyncTests.cs`（T-17〜T-18）

## テストデータ

- 作成リクエスト: `title`（必須）＋任意の `originalUri` / `contentType` / `attributes`（例 `department=engineering`, `dept=sales`）/ `tags`（例 `v1`, `q3`）。
- 更新リクエスト: `title`, `attributes`, `tags`, `expectedVersion`（並行制御確認用に古い値 5 / 99）, `changeNote`（例「見直し」）。
- `DocumentNormalized` イベント: `DocumentId`（新規 GUID）, `Title`, `MarkdownUri`（`storage://normalized/doc.md`）, `Attributes`, `Tags`。
- 統合テストは `PostgresFixture` / `RabbitMqFixture` が利用可能な場合のみ実行（`DockerFact`）。非同期消費は最大約 30 秒ポーリングで待機。

## 関連仕様

- 機能仕様書: `../functional/FR-06_document-crud-versioning.md`
- 作業仕様書: `../specs/20260627_FR-06_document-versioning-metadata.md`
- 通信仕様書: `../api/openapi.yaml`
- データ仕様書: `../data/document-and-version.md`

## 未決事項

- 版ロールバック（復元）・版間 diff の受け入れ基準は機能追加後に本書へ追記する。
- 楽観的並行制御の高並行下での競合（実 DB での同時 `PUT`）は現状ユニット/単一シナリオのみで、負荷試験は別タスク。
