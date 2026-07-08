---
title: IADR-0030 運用者ロールは platform-operator を新設し ConfigViewer ポリシーで判定する
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

# IADR-0030: 運用者ロールは platform-operator を新設し ConfigViewer ポリシーで判定する

- 状態: Accepted
- 日付: 2026-07-08
- 決定者: ステークホルダー（運用者ロールの要否、2026-07-08）＋ claude（issue #113 実装）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: FR-15・SC-11・ADR-0018・ADR-0004
- 関連する実装仕様書: [作業仕様書](../specs/20260708_issue-113_sc11-open-items-operator-role.md)・
  [SC-11 画面仕様書](../screens/SC-11_configuration-viewer.md)・
  [IADR-0029](./IADR-0029_config-info-api-placement-and-drift-granularity.md)（構成情報 API の配置・ドリフト判定）・
  [セキュリティ仕様書](../security/security.md)

## コンテキストと課題

FR-15（構成情報取得 API）と SC-11（構成ビューア）は「閲覧は**管理者・運用者ロール**に限定する」と
定めるが、着手時点の RBAC は `AdminOnly` ポリシー（ロール `platform-admin`）のみで運用者ロールが
未定義だった（SC-11 画面仕様書の未決事項 1）。決める点:
(1) 運用者ロールを新設するか管理者ロールで代用するか、(2) ロール名、(3) ポリシーの形、
(4) 既存 `AdminOnly` 保護エンドポイント（SC-10 ダッシュボード等）への波及。

> **経緯（並行実装の整合）**: 本決定は issue #113 で `AdminOrOperator` というポリシー名で起草されたが、
> 並行して進んだ issue #112（PR #116、[IADR-0029]）が同一セマンティクスのポリシーを **`ConfigViewer`**
> の名で導入し先に develop へマージされた。同一の認可要件に対して 2 つのポリシーを併存させる冗長を
> 避けるため、本 IADR は稼働中の `ConfigViewer` へ名称を統一して確定する（ロール名 `platform-operator`
> は両実装で一致しており変更なし）。

## 検討した選択肢

1. **運用者ロール `platform-operator` を新設し、`ConfigViewer` ポリシー（管理者・運用者のいずれかで許可）を追加（採用）**
   - 計画（FR-15・SC-11）の文言「管理者・運用者」をそのまま実装に写像できる。最小権限の原則に沿い、
     構成閲覧のためだけに管理者権限（ABAC ポリシー管理等の変更権限を含む）を配る必要がなくなる。
     ポリシー名は用途（構成閲覧）を表し、判定内容（admin OR operator）はポリシー定義側に閉じる。
2. 管理者ロールで代用（運用者を新設しない）
   - 実装は不要だが、計画の Must 要求（運用者ロール限定の明記）から逸脱する。運用担当者へ
     `platform-admin` を配ることになり、閲覧目的に対して過剰権限となる。
3. 汎用の階層ロール制度（admin > operator > viewer 等）を導入
   - 現時点で operator を要求するのは FR-15 のみ。計画外の抽象化（CLAUDE.md 禁止事項）にあたる。

## 決定

選択肢 1 を採用する。ステークホルダー判断（2026-07-08、issue #113）により運用者ロールは
**必要とする**方針が確定した。

- **ロール名**: `platform-operator`（既存 `platform-admin` と対称の命名。Keycloak レルムロール）。
- **ポリシー**: `ConfigViewer` = `RequireRole("platform-admin", "platform-operator")`
  （ASP.NET Core の `RequireRole` は複数指定でいずれか一致＝OR 判定）。
  定数は `KnowledgePlatformAuthPolicies` に置き、全サービスが共有する。利用実体は
  構成情報 API（`/bff/admin/config`・`/bff/admin/config/drift`、[IADR-0029] の存在秘匿 404 フロー）。
- **クレーム経路**: 既存の `KeycloakRolesClaimsTransformation`（`realm_access.roles` →
  `ClaimTypes.Role`）はロール名非依存のため変更しない。
- **レルム定義**: `deploy/keycloak/knowledge-platform-realm.json` にロール定義のみ追加する。
  実ユーザーへの割当は運用作業（Keycloak 管理画面／IaC）とし、レルム import には含めない。
  ※レルムにロール定義が無いと、正規の運用者に `platform-operator` を配れず `ConfigViewer` が
  実運用で機能しないため、レルム定義は本ロール新設と不可分の変更である。
- **既存エンドポイントは変更しない**: SC-10 ダッシュボード（FR-10）・フィードバック一覧（FR-08）・
  ABAC 管理（FR-09）は計画上「管理者」向けで「運用者」の明記がなく、`AdminOnly` のまま維持する。
  運用者への開放が必要になった場合は計画側（要求・画面）の更新を経て個別に判断する。

## 影響・結果

- #113（構成ビューア画面）の実装は `ConfigViewer` ポリシーを利用する（存在秘匿・監査ログの
  意味論は [IADR-0029] が規定済み）。
- 運用者は構成閲覧（FR-15）のみ可能で、管理系操作（FR-08/09/10 の AdminOnly）は不可のまま。
- SC-11 画面仕様書の未決事項 1 はこれで解決。ロール運用（誰に operator を割り当てるか）は
  運用仕様書の管轄とする。
- ポリシー判定は単体テスト（`ConfigViewerPolicyTests`）で検証する（許可: admin／operator／両方、
  拒否: 無関係ロール・匿名、および operator が `AdminOnly` を通過しないこと）。

## 却下した場合の再考条件

- operator を要求する要求（FR）が増え、ロールの組合せが複雑化した場合は選択肢 3（体系的なロール
  設計）を計画側（ADR）として再検討する。

[IADR-0029]: ./IADR-0029_config-info-api-placement-and-drift-granularity.md
