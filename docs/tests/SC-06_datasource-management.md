---
title: SC-06 データソース管理 テスト仕様書
type: test-spec
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
related_specs:
  - "../screens/SC-06_datasource-management.md"
  - "../specs/20260709_issue-132_sc06-datasource-management.md"
---

# テスト仕様書: データソース管理（SC-06）

## バックエンド（BFF・xUnit）

対象: `src/Bff/KnowledgePlatform.Bff/Foundation/Endpoints/DataSourceBffEndpoints.cs`
テスト: `src/Bff/KnowledgePlatform.Bff.Tests/BffDataSourceEndpointTests.cs`

| # | 観点 | 起点 | 検証内容 | ケース |
| --- | --- | --- | --- | --- |
| 1 | 一覧（管理者） | FR-01 | admin で一覧が返る | `GetList_AsAdmin_ReturnsDataSources` |
| 2 | 一覧（運用者） | IADR-0039 | operator も許可 | `GetList_AsOperator_IsAllowed` |
| 3 | ロール制限 | IADR-0039 | 非特権ロールは 403 | `GetList_AsNonPrivilegedRole_IsForbidden` |
| 4 | 無認証 | IADR-0039 | 匿名は 401 | `GetList_WhenAnonymous_IsUnauthorized` |
| 5 | 不在 | FR-01 | 後段 404 を透過 | `GetById_WhenMissing_Returns404` |
| 6 | 登録 | FR-01 | 201 で登録 | `Create_AsAdmin_Returns201` |
| 7 | 同期 | FR-01/FR-02 | 202 で同期トリガ中継 | `Sync_AsAdmin_Returns202` |
| 8 | 無効化 | FR-01 | 204 で論理削除 | `Delete_AsAdmin_Returns204` |

## フロントエンド（Vitest + Testing Library）

対象: `frontend/src/features/sc06-datasources/DataSourceManagementPage.tsx`
テスト: `frontend/src/features/sc06-datasources/DataSourceManagementPage.test.tsx`

| # | 観点 | 起点 | 検証内容 | ケース |
| --- | --- | --- | --- | --- |
| 1 | 一覧表示 | FR-01 | 名前・接続先・機密区分を表示 | `lists registered data sources` |
| 2 | 登録 | FR-01/FR-05 | 既定機密区分を含むペイロードで POST | `creates a data source with the default confidentiality attribute` |
| 3 | 同期 | FR-02 | `/{id}/sync` を POST | `triggers a manual sync` |
| 4 | 無効化 | FR-01 | `/{id}` を DELETE | `disables a data source` |
| 5 | 異常系 | FR-01 | 取得失敗で alert | `shows an alert when the list fails to load` |

## ロール・存在秘匿の担保

- BFF はグループ全体を admin/operator に限定（3/4 で 403/401 を検証）。
- フロントはルート／ナビを `RequireRole` で出し分け、権限外は NotFound（画面テストは page 直描画で機能検証、ルートガードは `RequireRole` 側の既存テストで担保）。

## 実行

- `dotnet test src/Bff/KnowledgePlatform.Bff.Tests --filter BffDataSourceEndpointTests`
- `npm run test -- src/features/sc06-datasources` / `npm run test:coverage`
