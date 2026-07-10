---
title: 文書管理 画面仕様書
type: screen-spec
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
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../adr/IADR-0041_document-write-bff-abac-scoped.md"
  - "../screens/SC-03_document-detail.md"
  - "../specs/20260709_issue-131_sc05-document-management.md"
  - "../tests/SC-05_document-management.md"
---

# 画面仕様書: 文書管理（SC-05）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-05 文書管理画面**（[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md) §SC-05・遷移図 `SC05 → SC03`）
- 関連ユースケース（UC）: **UC-03**（文書管理）
- 関連機能要求（FR）: **FR-06**（文書管理）、FR-05（ABAC 属性）、FR-09（属性整合）

## 画面概要・目的

正規化文書の CRUD・属性／タグ設定・公開／アーカイブを行う管理画面。詳細・本文・版履歴は SC-03（`/documents/:id`）へ委譲する。保存（公開）で取り込み・Wiki 同期がトリガされる。

- アクセス: **platform-admin/operator 限定**（[IADR-0041](../adr/IADR-0041_document-write-bff-abac-scoped.md)）。権限外はルート・ナビとも非表示。既存文書への操作はさらに ABAC スコープ内に限定（閲覧できない文書は変更不可・404 秘匿）。

## データソース（BFF 境界）

| 用途 | エンドポイント | 認可 | 応答 |
| --- | --- | --- | --- |
| 一覧 | `GET /bff/documents` | ABAC（読み取り・SC-03 と共通） | `DocumentDto[]` |
| 作成 | `POST /bff/documents` | admin/operator＋scope 解決 | 201 / 400（タイトル必須） / 403 |
| 更新 | `PUT /bff/documents/{id}` | admin/operator＋スコープ内 | 200 / 404 / 409（版競合） |
| 公開 | `POST /bff/documents/{id}/publish` | 同上 | 200 / 404 / 409（archived からの再公開は不正遷移） |
| アーカイブ | `POST /bff/documents/{id}/archive` | 同上 | 200 / 404 |
| 削除 | `DELETE /bff/documents/{id}` | 同上 | 204 / 404 |

- 書き込みは対象文書がスコープ内のときのみ実行される（[[IADR-0041]]）。更新は楽観ロック（`expectedVersion`）。
- 公開は未公開状態（draft / normalized）のみ許可する。archived からの再公開は状態遷移の意図に反するため、UI はボタンを出さず、サーバ（DocumentService）もドメイン不変条件として 409 で拒否する（多層防御・レビュー #171 指摘対応）。

## 入力 / バリデーション

| 項目 | 必須 | 形式 | バリデーション |
| --- | --- | --- | --- |
| タイトル | 必須 | テキスト | 空・空白のみ不可（クライアント＋サーバ 400） |
| 機密区分 | 必須 | 選択 | public / internal / confidential / restricted |
| タグ | 任意 | カンマ区切り | 任意 |
| 変更メモ | 任意 | テキスト | 更新時のみ |

## 主要素・振る舞い

- 作成フォーム（タイトル・機密区分・タグ）。必須未設定は保存不可。
- 一覧テーブル（タイトル→SC-03 リンク・状態・版・機密区分・更新・操作）。
- 操作: 編集（楽観ロック PUT。版競合 409 は通知＋再読込）・公開（draft / normalized のみ）・アーカイブ（archived 以外）・削除。
- 通知（`role="status"`）／エラー（`role="alert"`）。

## 実装

- BFF: `src/platform/backend/Bff/KnowledgePlatform.Bff/Foundation/Endpoints/DocumentBffEndpoints.cs`（書き込みサブグループ）
- フロント: `src/knowledge/frontend/src/features/sc05-documents/DocumentManagementPage.tsx` / `index.tsx`
- テスト観点は [tests/SC-05_document-management.md](../tests/SC-05_document-management.md)。
