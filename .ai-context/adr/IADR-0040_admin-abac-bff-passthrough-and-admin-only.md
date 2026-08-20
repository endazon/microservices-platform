---
title: IADR-0040 管理者設定（ABAC）の BFF 透過中継と AdminOnly ゲーティング
type: impl-adr
status: Accepted
related_ids:
  - SC-09
  - UC-05
  - FR-09
  - ADR-0004
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md
---

# IADR-0040: 管理者設定（ABAC）の BFF 透過中継と AdminOnly ゲーティング

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: SC-09（管理者設定・ABAC）／ UC-05 ／ FR-09（ABAC 属性・ポリシー管理）
- 関連 ADR: ADR-0004（ABAC 認可モデル）／ [IADR-0006](./IADR-0006_abac-management-validation.md)（属性参照中削除の 409）／ [IADR-0035](./IADR-0035_frontend-role-based-nav-and-existence-hiding.md)（存在秘匿ナビ）／ [IADR-0039](./IADR-0039_datasource-management-bff-and-role-gating.md)（管理系画面のロールゲーティング）
- 関連仕様書: `docs/screens/SC-09_admin-abac-settings.md`

## コンテキストと課題

SC-09 は属性辞書・ABAC ポリシーを管理する。AuthorizationService の管理 API（`/authz/policies`・`/authz/attributes`）は実装済み・**AdminOnly 強制済み**だが BFF 未プロキシである。保存前検証（矛盾・構文）は AuthorizationService が 400 ValidationProblem で返し、属性が参照中の削除は 409 で拒否する（[IADR-0006](./IADR-0006_abac-management-validation.md)）。画面は「保存前にポリシーを検証し、矛盾はエラー表示」する必要がある（計画 SC-09）。

決めること:
1. 対象ロール（operator を含めるか）。
2. BFF は応答をどう扱うか（型付き変換か透過か）。検証エラー・競合の詳細を画面へどう届けるか。

## 決定

1. **`platform-admin` のみに限定する**（operator も不可）。Issue #135 の受け入れ基準「platform-admin 以外はアクセスできない」に従う。BFF は `/bff/admin/authz/*` グループを `AdminOnly` ポリシーで保護し、フロントは `RequireRole([Admin])`→NotFound で存在秘匿する。SC-06/07（運用管理）が admin+operator（[IADR-0039](./IADR-0039_datasource-management-bff-and-role-gating.md)）であるのと対照的に、ABAC 設定は認可の根幹のため admin 専用とする。
2. **BFF は透過中継（passthrough）する。** 要求本文と Authorization をそのまま AuthorizationService へ引き継ぎ、応答は **status・content-type・本文をそのまま返す**。これにより保存前検証エラー（400 の `{ errors: { errors: [...] } }`）・参照競合（409）・不在（404）が失われずに SPA へ届き、画面が検証結果・矛盾を再現できる。型付き変換（DTO 経由）ではエラー本文が欠落し検証結果を表示できないため採らない。
3. **SPA 側 `apiFetch` を拡張し、400/409 の Problem 本文から詳細メッセージを抽出**して `ApiError.details` に載せる（`validation` / `conflict` 種別を追加）。既存の 401/403/404/5xx 挙動は不変（追加のみ・後方互換）。

## 根拠 / 代替案

- **二重ゲート（BFF + 後段）**: BFF の AdminOnly は早期拒否と一貫性のため。後段 AuthorizationService も AdminOnly を強制しており、資格情報を伝播することで後段の認可・監査が正しく機能する（BFF ゲートを外しても後段が守る＝多層防御）。
- **透過中継 vs DTO 変換**: 管理系は検証エラーの詳細提示が要件のため透過が適切。読み取り（一覧・取得）も統一的に透過し、OpenAPI 用に `Produces<AbacPolicyDto/AttributeDefinitionDto>` を注記する。
- **`ApiError.details` の追加**: 検証メッセージ表示は複数画面（将来 SC-05 等）で有用な横断機能。foundation への追加は最小・後方互換。

## 影響

- `Shared.Contracts` に `AbacPolicyDto` / `AttributeDefinitionDto`（参照・OpenAPI 用）。
- BFF に `AuthzBffEndpoints`（透過中継。AuthorizationService named client は既存を再利用）。
- SPA `foundation/api`（`ApiError.details` + `validation`/`conflict` 種別、`apiFetch` の Problem 抽出）。
- フロント `features/sc09-admin-abac`（`/admin/abac`、admin 限定・属性辞書＋ポリシー管理・検証結果表示）。
