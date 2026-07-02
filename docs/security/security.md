---
title: セキュリティ仕様書
type: security-spec
status: draft
related_ids:
  - FR-09
  - ADR-0004
author: claude
created: 2026-07-02
updated: 2026-07-02
plan_refs: []
---

# セキュリティ仕様書

> 必須ドキュメント（リポジトリ単位）。本リポジトリのセキュリティを定める。雛形は `docs/templates/security_spec_template.md`。
> **未記入のまま放置しない**。認証・認可・データ保護・秘密情報管理・監査ログを埋めること。

## 起点となる計画書（トレーサビリティ）

- 非機能要件（NFR・セキュリティ）:
- 関連 ADR:

## 認証・認可

- **認証**: Keycloak（OIDC/JWT）による Bearer トークン認証（ADR-0004）。各サービスは `AddKnowledgePlatformAuth` で JWT を検証する。
- **認可（サービス内 RBAC）**: FR-09 の管理系エンドポイント（属性辞書・ABAC ポリシーの CRUD／有効無効切替／削除）は
  `AdminOnly` ポリシー（`platform-admin` ロール必須）で保護する。ロール未保持は 403。ロール名・ポリシー名は
  `KnowledgePlatformAuthPolicies` に定義。サービス間呼び出しの `POST /authz/scope`・`POST /authz/attributes/validate`
  は本ポリシーの対象外（認証のみ）。
- **ロールクレームの取得経路**: Keycloak はレルムロールを JWT の `realm_access.roles`（ネストした JSON クレーム）に
  格納する。標準の `JwtBearerHandler` はこれを `ClaimTypes.Role` へ展開しないため、`KeycloakRolesClaimsTransformation`
  （`IClaimsTransformation`）でトークン検証後に展開し、`RequireRole("platform-admin")` を成立させる。展開ロジックは
  単体テスト（`KeycloakRolesClaimsTransformationTests`）で検証。不正 JSON は fail-closed（ロール無し）で扱う。
- **認可（ABAC 本体）**: 文書アクセスの属性ベース認可は `AbacEvaluator`（deny-by-default）が担う（FR-05, ADR-0004）。
- 未対応: 全サービス横断のエンドポイント認可（P2 で拡充予定。ADR-0004）。

## データ保護

| 区分 | 対象 | 方式 |
| --- | --- | --- |
| 保存時暗号化 |  |  |
| 通信時暗号化 |  |  |
| 個人情報 / 機微情報 |  |  |

## 秘密情報管理

<!-- 鍵・トークンの保管・ローテーション・コミット禁止 -->

## 監査ログ

| 対象イベント | 記録項目 | 保管期間 |
| --- | --- | --- |
|  |  |  |

## 脅威と対策

| 脅威 | 影響 | 対策 |
| --- | --- | --- |
|  |  |  |

## 未決事項
