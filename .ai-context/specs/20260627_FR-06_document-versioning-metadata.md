---
title: 作業仕様書 — FR-06 文書のCRUD・バージョン管理・メタデータ管理
type: spec
status: completed
related_ids:
  - FR-06
  - UC-03
author: claude
created: 2026-06-27
updated: 2026-06-27
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-06)
  - planning:projects/microservices-platform/03_usecases/ (UC-03)
related_specs:
  - ./20260627_FR-01_data-source-catalog-pipeline.md
  - ../adr/IADR-0001_document-service-owns-catalog.md
related_adrs:
  - ADR-0002 (サービス境界・DB per Service)
  - ADR-0014 (関連 ADR)
---

# 作業仕様書: FR-06 文書のCRUD・バージョン管理・メタデータ管理

## 目的

FR-06「文書の CRUD・バージョン管理・メタデータ管理を行う」のうち、既存 `DocumentService` に
不足している **バージョン履歴（version history）管理** と **メタデータ更新 API** を実装し、
文書ライフサイクル（作成→更新→公開→削除）の各時点を追跡・参照できるようにする。

## 背景・現状（調査結果）

FR-01 のカタログ化パイプライン整備（[20260627_FR-01](./20260627_FR-01_data-source-catalog-pipeline.md)）に
より `DocumentService` には基本 CRUD（`GET`/`POST`/`PUT`/`DELETE /documents`）と
メタデータ（`Attributes` / `Tags`）保持が実装済みである。一方で以下が未実装だった。

- `Document.Version` は更新のたびに加算される `int` カウンタに過ぎず、**過去版のスナップショットが残らない**。
  「バージョン管理」要件（どの時点でどのメタデータ・本文だったか）を満たさない。
- 版を参照する API（履歴一覧・特定版取得）が存在しない。
- メタデータのみを更新する API が無く、本文・タイトルと一括の `PUT` のみ。
- 同時更新に対する**楽観的並行制御**（lost update 防止）が無い。
- `DocumentDto` に `Version` が露出していない。

## 作業範囲

### 含むもの（本 PR）

- **ドメイン**: `DocumentVersion`（版スナップショット）エンティティを追加。`Document` を集約ルートとし、
  作成・更新・正規化適用・公開の各操作で版スナップショットを追記する（版番号は単調増加）。
- **永続化**: `DocumentDbContext` に `DocumentVersions` を追加（`Attributes`/`Tags` は jsonb）。
  EF Core マイグレーション `AddDocumentVersions` を追加。
- **API**:
  - `GET /documents/{id}/versions` — 版履歴一覧（新しい順）。
  - `GET /documents/{id}/versions/{version}` — 特定版の取得。
  - `PATCH /documents/{id}/metadata` — メタデータ（属性・タグ）のみ更新。
  - `POST /documents/{id}/publish` — 公開（status=published、版を追記）。
  - `PUT /documents/{id}` に **楽観的並行制御**（任意の `ExpectedVersion`）を追加。不一致は 409。
- **DTO**: `DocumentDto.Version` を露出。`DocumentVersionDto` を追加。
- **テスト**: ユニット（ドメイン・エンドポイント）＋統合（実 PostgreSQL）で版追記・履歴取得・
  メタデータ更新・並行制御を検証。

### 含まないもの（後続タスク）

- 版間の差分（diff）表示・特定版へのロールバック（復元）API。
- 本文（Markdown 本体）のオブジェクトストレージ実保存（現状 URI 参照のみ）。
- 横断検索・出典付与（FR-03/FR-04）、ABAC による権限フィルタ（FR-05）。
- 負荷試験による p95 レイテンシ確認。

## 受け入れ基準（本 PR の範囲）

- [ ] `POST /documents` で作成すると版 1 のスナップショットが記録される。
- [ ] `PUT /documents/{id}` / `PATCH /documents/{id}/metadata` / `POST /documents/{id}/publish` の
      各更新で `Version` が加算され、その時点のスナップショットが版履歴へ追記される。
- [ ] `GET /documents/{id}/versions` が版履歴を新しい順で返し、各版のタイトル・状態・属性・タグを保持する。
- [ ] `GET /documents/{id}/versions/{version}` が指定版を返す（存在しない版は 404）。
- [ ] `PUT` に `ExpectedVersion` を付与し現在版と不一致なら 409（lost update 防止）。
- [ ] 既存テストが壊れない（`dotnet build` / 既存ユニット・統合テスト pass）。

## Issue 受け入れ基準との対応

| Issue 受け入れ基準 | 本 PR | 備考 |
| --- | --- | --- |
| 横断検索・出典付与 | 範囲外 | FR-03/FR-04 で対応。版管理は検索の前提となるメタデータを整備。 |
| 権限外文書の非表示 | 範囲外 | ABAC（FR-05 / AuthorizationService）で対応。 |
| 更新の N 分以内反映 | 基盤前進 | 更新時 `DocumentUpdated` 発行で索引化へ連鎖（既存）。 |
| 個別デプロイ・ロールバック | 既達 | サービス分割（ADR-0002）で担保済み。 |
| p95 レイテンシ | 範囲外 | 負荷試験は後続タスク。 |

## 実装方針

- バージョン履歴は **追記専用（append-only）**。各 `DocumentVersion` は確定した 1 版の完全スナップショット
  （タイトル・状態・本文 URI・属性・タグ）を保持し、現在版も履歴に含める。これにより任意時点の
  メタデータ・状態を ID＋版番号で一意に再構成できる。
- 版番号は `Document.Version`（集約が単調増加で採番）と一致させる。スナップショット生成はドメイン操作
  （`Update` / `ApplyNormalized` / `UpdateMetadata` / `Publish` / 各 `Create`）の内部で行い、
  呼び出し側が版を取り違えないようにする。
- 楽観的並行制御は API 層で `ExpectedVersion` を検査（軽量。DB 行ロックは導入しない）。

## テスト方針

- ユニット（`DocumentService.Api.Tests`）: ドメインの版追記（`Document.Update` 等で版が増えスナップショットが残る）、
  エンドポイント（履歴取得・メタデータ PATCH・並行制御 409）を InMemory で検証。
- 統合（`KnowledgePlatform.IntegrationTests/DocumentService/DocumentVersioningTests`）: 実 PostgreSQL で
  作成→更新→履歴取得→特定版取得→メタデータ更新を検証（既存 `DocumentCrudTests` のパターンに準拠）。

## リスク・注意事項

- `DocumentDto.Version` 追加は加算のみで既存利用箇所（検索・Wiki 同期）に破壊的影響なし（既定値 1）。
- スナップショットの `Attributes`/`Tags` は本体と同じ jsonb 変換・ValueComparer を用いる。

## 完了条件（Definition of Done 参照）

`docs/DEFINITION_OF_DONE.md` 準拠。ビルド成功・テスト pass・トレーサビリティ ID 付与。
