---
title: データソース管理 画面仕様書
type: screen-spec
status: completed
related_ids:
  - SC-06
  - UC-04
  - FR-01
  - FR-02
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../adr/IADR-0039_datasource-management-bff-and-role-gating.md"
  - "../screens/SC-07_conversion-jobs.md"
  - "../specs/20260709_issue-132_sc06-datasource-management.md"
  - "../tests/SC-06_datasource-management.md"
---

# 画面仕様書: データソース管理（SC-06）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-06 データソース管理**（[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md) §画面一覧・遷移図 `SC06 → SC07`）
- 関連ユースケース（UC）: **UC-04**（データソース登録・同期）
- 関連機能要求（FR）: **FR-01**（データソースカタログ）、**FR-02**（取り込み）、FR-05（ABAC 属性）

## 画面概要・目的

データソース（コネクタ）の登録・一覧・同期状態確認・無効化を行う運用画面。取り込み→変換の運用フローとして SC-07（変換ジョブ）への導線を持つ。

- アクセス: **platform-admin もしくは platform-operator 限定**（[IADR-0039](../adr/IADR-0039_datasource-management-bff-and-role-gating.md)）。権限外はルート・ナビとも非表示（`RequireRole`→NotFound で存在秘匿）。サーバ側 `/bff/datasources` も同ロールに限定（実効境界）。

## データソース（BFF 境界）

| 用途 | エンドポイント | 認可 | 応答 |
| --- | --- | --- | --- |
| 一覧 | `GET /bff/datasources` | admin/operator（403/401） | `DataSourceDto[]` |
| 個別取得 | `GET /bff/datasources/{id}` | 同上 | `DataSourceDto` / 404 |
| 登録 | `POST /bff/datasources` | 同上 | `DataSourceDto`（201） |
| 手動同期 | `POST /bff/datasources/{id}/sync` | 同上 | 202 `{ fetchId, status }` |
| 無効化 | `DELETE /bff/datasources/{id}` | 同上 | 204 |

- `DataSourceDto = { id, name, sourceType, connectionUri, status, lastSyncedAt?, config{}, defaultAttributes{}, createdAt }`
- 登録リクエスト: `{ name, sourceType, connectionUri, defaultAttributes: { confidentiality } }`。既定機密区分未指定時はサービス側が `internal` をフェイルセーフ補完する（FR-05, IADR-0019）。

## 入力 / バリデーション

| 項目 | 必須 | 形式 | バリデーション |
| --- | --- | --- | --- |
| 名前 | 必須 | テキスト | 空・空白のみ不可 |
| 種別 | 必須 | 選択 | filesystem / wiki / saas / db |
| 接続先 URI | 必須 | テキスト | 空・空白のみ不可 |
| 既定機密区分 | 必須 | 選択 | public / internal / confidential / restricted（既定 internal） |

## 主要素・振る舞い

- 登録フォーム（名前・種別・接続先・既定機密区分）→ `POST` 後に一覧再取得。
- 一覧テーブル（名前・種別・接続先・状態・機密区分・最終同期・操作）。
- 操作: 「同期」（手動同期トリガ）・「無効化」（active のときのみ表示。DELETE=論理削除）。
- 通知（`role="status"`）／エラー（`role="alert"`）。0 件は中立表示。

## 実装

- BFF: `src/Bff/KnowledgePlatform.Bff/Foundation/Endpoints/DataSourceBffEndpoints.cs`
- フロント: `frontend/src/features/sc06-datasources/DataSourceManagementPage.tsx` / `index.tsx`
- 契約: `KnowledgePlatform.Shared.Contracts/Dtos/DataSourceDto.cs`
- テスト観点は [tests/SC-06_datasource-management.md](../tests/SC-06_datasource-management.md)。
