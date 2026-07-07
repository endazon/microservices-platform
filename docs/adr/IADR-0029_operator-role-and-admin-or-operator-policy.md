---
title: IADR-0029 運用者ロールは platform-operator を新設し AdminOrOperator ポリシーで判定する
type: impl-adr
status: Accepted
related_ids:
  - FR-15
  - SC-11
  - ADR-0018
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
---

# IADR-0029: 運用者ロールは platform-operator を新設し AdminOrOperator ポリシーで判定する

- 状態: Accepted
- 日付: 2026-07-08
- 決定者: ステークホルダー（運用者ロールの要否、2026-07-08）＋ claude（issue #113 実装）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: FR-15・SC-11・ADR-0018・ADR-0004
- 関連する実装仕様書: [作業仕様書](../specs/20260708_issue-113_sc11-open-items-operator-role.md)・
  [SC-11 画面仕様書](../screens/SC-11_configuration-viewer.md)・
  [セキュリティ仕様書](../security/security.md)

## コンテキストと課題

FR-15（構成情報取得 API）と SC-11（構成ビューア）は「閲覧は**管理者・運用者ロール**に限定する」と
定めるが、現行実装の RBAC は `AdminOnly` ポリシー（ロール `platform-admin`）のみで運用者ロールが
未定義だった（SC-11 画面仕様書の未決事項 1）。決める点:
(1) 運用者ロールを新設するか管理者ロールで代用するか、(2) ロール名、(3) ポリシーの形、
(4) 既存 `AdminOnly` 保護エンドポイント（SC-10 ダッシュボード等）への波及。

## 検討した選択肢

1. **運用者ロール `platform-operator` を新設し、`AdminOrOperator` ポリシー（いずれかのロールで許可）を追加（採用）**
   - 計画（FR-15・SC-11）の文言「管理者・運用者」をそのまま実装に写像できる。最小権限の原則に沿い、
     構成閲覧のためだけに管理者権限（ABAC ポリシー管理等の変更権限を含む）を配る必要がなくなる。
2. 管理者ロールで代用（運用者を新設しない）
   - 実装は不要だが、計画の Must 要求（運用者ロール限定の明記）から逸脱する。運用担当者へ
     `platform-admin` を配ることになり、閲覧目的に対して過剰権限となる。
3. 汎用の階層ロール制度（admin > operator > viewer 等）を導入
   - 現時点で operator を要求するのは FR-15 のみ。計画外の抽象化（CLAUDE.md 禁止事項）にあたる。

## 決定

選択肢 1 を採用する。ステークホルダー判断（2026-07-08、issue #113）により運用者ロールは
**必要とする**方針が確定した。

- **ロール名**: `platform-operator`（既存 `platform-admin` と対称の命名。Keycloak レルムロール）。
- **ポリシー**: `AdminOrOperator` = `RequireRole("platform-admin", "platform-operator")`
  （ASP.NET Core の `RequireRole` は複数指定でいずれか一致＝OR 判定）。
  定数は `KnowledgePlatformAuthPolicies` に置き、全サービスが共有する。
- **クレーム経路**: 既存の `KeycloakRolesClaimsTransformation`（`realm_access.roles` →
  `ClaimTypes.Role`）はロール名非依存のため変更しない。
- **レルム定義**: `deploy/keycloak/knowledge-platform-realm.json` にロール定義のみ追加する。
  実ユーザーへの割当は運用作業（Keycloak 管理画面／IaC）とし、レルム import には含めない。
- **既存エンドポイントは変更しない**: SC-10 ダッシュボード（FR-10）・フィードバック一覧（FR-08）・
  ABAC 管理（FR-09）は計画上「管理者」向けで「運用者」の明記がなく、`AdminOnly` のまま維持する。
  運用者への開放が必要になった場合は計画側（要求・画面）の更新を経て個別に判断する。

## 影響・結果

- #112（構成情報 API）・#113（構成ビューア）は `AdminOrOperator` ポリシーを
  `RequireAuthorization(KnowledgePlatformAuthPolicies.AdminOrOperator)` で利用できる（存在秘匿・
  監査ログは各実装側の責務）。
- 運用者は構成閲覧（FR-15）のみ可能で、管理系操作（FR-08/09/10 の AdminOnly）は不可のまま。
- SC-11 画面仕様書の未決事項 1 はこれで解決。ロール運用（誰に operator を割り当てるか）は
  運用仕様書の管轄とする。

## 却下した場合の再考条件

- operator を要求する要求（FR）が増え、ロールの組合せが複雑化した場合は選択肢 3（体系的なロール
  設計）を計画側（ADR）として再検討する。
