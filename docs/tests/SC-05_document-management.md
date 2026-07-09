---
title: SC-05 文書管理 テスト仕様書
type: test-spec
status: completed
related_ids:
  - SC-05
  - UC-03
  - FR-06
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - "../screens/SC-05_document-management.md"
  - "../specs/20260709_issue-131_sc05-document-management.md"
---

# テスト仕様書: 文書管理（SC-05）

## バックエンド（BFF・xUnit）

対象: `src/Bff/KnowledgePlatform.Bff/Foundation/Endpoints/DocumentBffEndpoints.cs`（書き込み）
テスト: `src/Bff/KnowledgePlatform.Bff.Tests/BffDocumentWriteEndpointTests.cs`

| # | 観点 | 起点 | 検証内容 | ケース |
| --- | --- | --- | --- | --- |
| 1 | 作成 | FR-06 | admin で 201 | `Create_AsAdmin_Returns201` |
| 2 | ロール制限 | IADR-0041 | 非特権は 403 | `Create_AsNonPrivilegedRole_IsForbidden` |
| 3 | 無認証 | IADR-0041 | 匿名は 401 | `Create_WhenAnonymous_IsUnauthorized` |
| 4 | 作成 deny | IADR-0041 | scope 無しは 403（deny-by-default） | `Create_WhenScopeNotGranted_IsForbidden_DenyByDefault` |
| 5 | 検証透過 | FR-06 | タイトル必須 400 透過 | `Create_WhenTitleMissing_Passes400Through` |
| 6 | 更新 | FR-06 | スコープ内で 200 | `Update_AsAdminInScope_Returns200` |
| 7 | スコープ外 | IADR-0041/0009 | スコープ外更新は 404 秘匿 | `Update_WhenOutOfScope_Returns404` |
| 8 | 楽観ロック | FR-06 | 版競合 409 透過 | `Update_WhenVersionConflict_Passes409Through` |
| 9 | 公開 | FR-06 | スコープ内で 200 | `Publish_AsAdminInScope_Returns200` |
| 10 | 削除 | FR-06 | スコープ内で 204 | `Delete_AsAdminInScope_Returns204` |
| 11 | 削除スコープ外 | IADR-0041 | スコープ外削除は 404 | `Delete_WhenOutOfScope_Returns404` |

## フロントエンド（Vitest + Testing Library）

対象: `frontend/src/features/sc05-documents/DocumentManagementPage.tsx`
テスト: `frontend/src/features/sc05-documents/DocumentManagementPage.test.tsx`

| # | 観点 | 起点 | 検証内容 | ケース |
| --- | --- | --- | --- | --- |
| 1 | 一覧・遷移 | FR-06 | 一覧＋SC-03 リンク | `lists documents linking to SC-03 detail` |
| 2 | 作成 | FR-06/FR-05 | 機密区分（必須）を含む POST | `creates a document with the required confidentiality attribute` |
| 3 | 公開 | FR-06 | `/{id}/publish` を POST | `publishes a draft document` |
| 4 | 楽観ロック | FR-06 | `expectedVersion` 付き PUT | `edits a document with optimistic concurrency` |
| 5 | 競合 | FR-06 | 409 で通知＋再読込 | `shows a conflict notice and reloads on 409 version conflict` |
| 6 | 異常系 | FR-06 | 取得失敗で alert | `shows an alert when the list fails to load` |
| 7 | 状態遷移(archived) | FR-06/UC-03 | archived は公開ボタン非表示 | `does not show the publish button for archived documents` |
| 8 | 状態遷移(normalized) | FR-06 | normalized は公開ボタン表示 | `shows the publish button for normalized (pipeline-produced) documents` |

## バックエンド（DocumentService・状態遷移ガード・xUnit）

対象: `Document.Publish()`（`Foundation/Domain/Document.cs`）・`POST /documents/{id}/publish`
テスト: `DocumentVersioningTests.cs` / `DocumentEndpointVersioningTests.cs`

| # | 観点 | 起点 | 検証内容 | ケース |
| --- | --- | --- | --- | --- |
| 1 | 不正遷移(ドメイン) | UC-03 | archived からの公開は例外・状態不変 | `Publish_FromArchived_Throws` |
| 2 | 許可遷移(ドメイン) | FR-06 | normalized からの公開は許可 | `Publish_FromNormalized_IsAllowed` |
| 3 | 不正遷移(API) | UC-03 | archive 後の再公開は 409 | `Publish_AfterArchive_Returns409` |

## ロール・存在秘匿の担保

- 書き込みは BFF で admin/operator 限定（2/3/4 で 403/401 検証）。既存文書操作はスコープ外を 404 秘匿（7/11）。
- フロントは `RequireRole` で `/documents` を出し分け（page テストは機能検証、ルートガードは `RequireRole` 既存テストで担保）。

## 実行

- `dotnet test src/Bff/KnowledgePlatform.Bff.Tests --filter BffDocumentWriteEndpointTests`
- `npm run test -- src/features/sc05-documents` / `npm run test:coverage`
