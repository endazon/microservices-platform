---
title: SC-09 管理者設定（ABAC） テスト仕様書
type: test-spec
status: completed
related_ids:
  - SC-09
  - UC-05
  - FR-09
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - "../screens/SC-09_admin-abac-settings.md"
  - "../specs/20260709_issue-135_sc09-admin-abac-settings.md"
---

# テスト仕様書: 管理者設定（ABAC）（SC-09）

## バックエンド（BFF・xUnit）

対象: `src/platform/backend/Bff/KnowledgePlatform.Bff/Foundation/Endpoints/AuthzBffEndpoints.cs`
テスト: `src/platform/backend/Bff/KnowledgePlatform.Bff.Tests/BffAuthzEndpointTests.cs`

| # | 観点 | 起点 | 検証内容 | ケース |
| --- | --- | --- | --- | --- |
| 1 | ポリシー一覧 | FR-09 | admin で一覧が返る | `ListPolicies_AsAdmin_ReturnsPolicies` |
| 2 | 属性一覧 | FR-09 | admin で属性辞書が返る | `ListAttributes_AsAdmin_ReturnsAttributes` |
| 3 | ロール制限 | IADR-0040 | operator も 403（admin 専用） | `ListPolicies_AsNonAdmin_IsForbidden` |
| 4 | 無認証 | IADR-0040 | 匿名は 401 | `ListPolicies_WhenAnonymous_IsUnauthorized` |
| 5 | ポリシー登録 | FR-09 | 201 で登録 | `CreatePolicy_AsAdmin_Returns201` |
| 6 | 検証透過 | FR-09/IADR-0040 | 保存前検証 400 を透過 | `CreatePolicy_WhenValidationFails_Passes400Through` |
| 7 | 属性登録 | FR-09 | 201 で登録 | `CreateAttribute_AsAdmin_Returns201` |
| 8 | 競合透過 | IADR-0006 | 参照中削除 409 を透過 | `DeleteAttribute_WhenReferenced_Passes409Through` |
| 9 | 有効切替 | FR-09 | PATCH で有効／無効切替 | `SetPolicyActive_AsAdmin_Succeeds` |
| 10 | 後段不達 | IADR-0040 | 後段ダウン時に 502 へ縮退（例外フロー・レビュー #170） | `ListPolicies_WhenBackendUnreachable_Returns502` |

## フロントエンド（Vitest + Testing Library）

対象: `src/knowledge/frontend/src/features/sc09-admin-abac/AdminAbacSettingsPage.tsx`
テスト: `src/knowledge/frontend/src/features/sc09-admin-abac/AdminAbacSettingsPage.test.tsx`

| # | 観点 | 起点 | 検証内容 | ケース |
| --- | --- | --- | --- | --- |
| 1 | 一覧表示 | FR-09 | 属性辞書・ポリシーを表示 | `lists attribute definitions and policies` |
| 2 | 属性登録 | FR-09 | 許可値パース＋ペイロード | `creates an attribute with parsed allowed values` |
| 3 | 矛盾検証表示 | FR-09 | 保存時 400 の詳細を表示 | `shows server-side validation errors (policy contradiction) on save` |
| 4 | 構文検証 | FR-09 | 不正 JSON をローカルで拒否（API 未呼出） | `rejects malformed condition JSON locally before calling the API` |
| 5 | 競合表示 | IADR-0006 | 参照中削除 409 の理由を表示 | `shows a 409 conflict message when deleting a referenced attribute` |

## foundation（Vitest）

対象: `src/platform/frontend/src/foundation/api/apiClient.ts` / `ApiError.ts`
テスト: `src/platform/frontend/src/foundation/api/apiClient.test.ts`

| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| 1 | 検証詳細抽出 | 400→`validation`・`details` にメッセージ抽出 | `maps 400 to validation and extracts ValidationProblem detail messages` |
| 2 | 競合詳細抽出 | 409→`conflict`・`details` に detail | `maps 409 to conflict and extracts the problem detail` |

## ロール・存在秘匿の担保

- BFF は AdminOnly（operator も 403）。フロントは `RequireRole([Admin])`→NotFound。UI は表示制御専用、サーバが実効境界。

## 実行

- `dotnet test src/platform/backend/Bff/KnowledgePlatform.Bff.Tests --filter BffAuthzEndpointTests`
- `npm run test -- src/features/sc09-admin-abac src/foundation/api` / `npm run test:coverage`
