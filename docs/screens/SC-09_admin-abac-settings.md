---
title: 管理者設定（ABAC） 画面仕様書
type: screen-spec
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
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../adr/IADR-0040_admin-abac-bff-passthrough-and-admin-only.md"
  - "../specs/20260709_issue-135_sc09-admin-abac-settings.md"
  - "../tests/SC-09_admin-abac-settings.md"
---

# 画面仕様書: 管理者設定（ABAC）（SC-09）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-09 管理者設定画面（ABAC）**（[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md) §SC-09）
- 関連ユースケース（UC）: **UC-05**（ABAC 属性・ポリシー管理）
- 関連機能要求（FR）: **FR-09**（ABAC 属性・ポリシー管理）、FR-05（ABAC）

## 画面概要・目的

利用者属性・文書属性／タグ（属性辞書）とアクセスポリシー（利用者属性 × 文書属性 → 許可アクション）を管理する画面。保存前にポリシーを検証し、矛盾・構文エラーを表示する。

- アクセス: **platform-admin のみ**（Issue #135。operator も不可。[IADR-0040](../adr/IADR-0040_admin-abac-bff-passthrough-and-admin-only.md)）。権限外はルート・ナビとも非表示（`RequireRole`→NotFound で存在秘匿）。サーバ側 `/bff/admin/authz` も AdminOnly（BFF・後段の二重ゲート）。

## データソース（BFF 境界。透過中継）

| 用途 | エンドポイント | 応答 |
| --- | --- | --- |
| 属性辞書一覧 | `GET /bff/admin/authz/attributes` | `AttributeDefinitionDto[]` |
| 属性登録 | `POST /bff/admin/authz/attributes` | 201 / 400（検証） |
| 属性削除 | `DELETE /bff/admin/authz/attributes/{id}` | 204 / 409（参照中・IADR-0006） |
| ポリシー一覧 | `GET /bff/admin/authz/policies` | `AbacPolicyDto[]` |
| ポリシー登録 | `POST /bff/admin/authz/policies` | 201 / 400（矛盾検証） |
| 有効／無効切替 | `PATCH /bff/admin/authz/policies/{id}/active` | 200 |
| ポリシー削除 | `DELETE /bff/admin/authz/policies/{id}` | 204 |

- BFF は要求本文・Authorization を AuthorizationService へ透過し、応答（status・本文）をそのまま返す。400/409 の詳細は `ApiError.details` に抽出され画面に表示される。

## 入力 / バリデーション

| 項目 | 必須 | 形式 | バリデーション |
| --- | --- | --- | --- |
| 属性キー | 必須 | テキスト | 空不可。キー重複・許可値はサーバ検証 |
| 属性スコープ | 必須 | 選択 | document / user |
| ポリシー名 | 必須 | テキスト | 空不可 |
| ポリシー条件 | 任意 | JSON（`{"key":["値"]}`） | 構文はローカル検証、矛盾はサーバ検証（400 表示） |
| 対象アクション | 必須 | 選択 | read / analyze / manage |

## 主要素・振る舞い

- 属性辞書: 一覧（キー・ラベル・スコープ・許可値・必須）＋登録フォーム＋削除。参照中削除は 409 を理由付きで表示。
- ポリシー: 一覧（名前・アクション・条件・状態）＋登録フォーム＋有効／無効切替＋削除。保存前検証（矛盾）を 400 詳細で表示。
- 検証結果・エラーは `role="alert"` で表示。

## 実装

- BFF: `src/platform/backend/Bff/KnowledgePlatform.Bff/Foundation/Endpoints/AuthzBffEndpoints.cs`
- フロント: `src/knowledge/frontend/src/features/sc09-admin-abac/AdminAbacSettingsPage.tsx` / `index.tsx`
- 契約: `KnowledgePlatform.Shared.Contracts/Dtos/AbacManagementDto.cs`
- foundation: `ApiError.details`（400/409 の詳細抽出）
- テスト観点は [tests/SC-09_admin-abac-settings.md](../tests/SC-09_admin-abac-settings.md)。
