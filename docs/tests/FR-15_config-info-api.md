---
title: FR-15 構成情報 API（実効構成・ドリフト検出） テスト仕様書
type: test-spec
status: draft
created: 2026-07-08
updated: 2026-08-23
author: claude
---
<!-- trace:
ids: [FR-15, SC-11]
adrs: [ADR-0018]
iadrs: [IADR-0009, IADR-0029, IADR-0030, IADR-0268]
specs: [20260707_FR-15_config-info-api-introspection-drift, 20260708_issue-113_sc11-open-items-operator-role]
issues: [#444]
-->

# テスト仕様書: 構成情報 API（実効構成・ドリフト検出）

> Issue #118 監査で欠落が判明したため後追いで作成（テスト実装は PR #116 / #117 で完了済み）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: 実効構成・構成バージョンの読み取り専用 API、ドリフト検出、管理者・運用者限定
- 画面: 構成ビューア（API 契約の検証。画面実装は後続フェーズ）
- 関連 ADR（実装）: BFF 配下への同居とドリフト判定粒度／運用者ロール `platform-operator` と ConfigViewer ポリシー／権限外の存在秘匿

## テスト対象・範囲

- 対象: `ConfigBffEndpoints`（認可・404 秘匿・監査）、`ConfigInspectionService` / `DriftDetector`
  （集約・突合）、`ConfigViewer` ポリシー（ロール OR 判定）、`KeycloakRolesClaimsTransformation`
  （realm_access.roles → ClaimTypes.Role 展開）。
- 対象外: 構成ビューアの画面（フロントエンド未着手）、GitOps 構成バージョン注入（実装 ADR のフォローアップ）。

## テスト観点

- 認可: platform-admin / platform-operator は 200、一般ユーザー・無認証は **404**（401/403 を返さない）。
- 存在秘匿: 非権限応答がエンドポイントの存在を推測させない（権限外は 404 とする方針と整合）。
- 監査: granted / denied の両方が監査ログへ記録される。
- ドリフト: 宣言なし・宣言との不一致・自己申告到達不能（Unverifiable）が Findings に反映される。
  **検証不能の 2 原因（収集対象に未登録＝Warning／登録済みで応答なし＝Info）を対照条件つきで固定する。**
- 突合の基準: 宣言のパスを指定していて段が 0 件なら起動しない（対照条件として、パス未設定・段ありは起動する）。
- 宣言の実効性: 正の宣言の有効な段の担当サービスが、compose・Helm の収集対象設定に実在する。
  無効にした段が実効構成のイベント接続（購読者・発行者）から消える。
- 契約: 応答が `EffectiveConfigDto` / `DriftReportDto`（Shared.Contracts）に一致する。

## テストケース（実装済みテストへの写像）

| # | 観点 | ケース | 実装 |
| --- | --- | --- | --- |
| 1 | 認可・秘匿 | admin/operator=200、一般・無認証=404、監査記録 | `KnowledgePlatform.Bff.Tests/ConfigBffEndpointTests` |
| 2 | ドリフト | 宣言との突合・Unverifiable 縮退・Findings 粒度 | `KnowledgePlatform.Bff.Tests/DriftDetectorTests` |
| 2b | ドリフト | 検証不能の 2 原因の区別・値域が 5 分類 / 2 値に閉じる | `Platform.Shared.Infrastructure.Tests/Foundation/Introspection/DriftServiceCoverageTests` |
| 2c | 基準の健全性 | 宣言のパス設定ありで段 0 件なら起動失敗（＋対照 2 件） | `Platform.Shared.Infrastructure.Tests/Foundation/Introspection/ConfigInspectionDeclarationGuardTests` |
| 2d | 宣言の実効性 | 正の宣言の束縛・収集対象の網羅・無効化がイベント接続へ届く | `Platform.Shared.Infrastructure.Tests/Foundation/Pipeline/PipelineDeclarationEffectivenessTests` |
| 2e | ポート差し替え | 構成でポート実装が入れ替わり、段登録・実効構成は不変 | `Platform.Shared.Infrastructure.Tests/Foundation/Pipeline/PortSwapCompositionTests` |
| 3 | ポリシー | ConfigViewer の OR 判定・AdminOnly 非侵食 | `AuthorizationService.Api.Tests/ConfigViewerPolicyTests` |
| 4 | ロール展開 | realm_access.roles → Role クレーム変換 | `AuthorizationService.Api.Tests/KeycloakRolesClaimsTransformationTests` |
| 5 | E2E（手動） | compose 実環境で operator=200 / 一般・無認証=404、実効構成の集約を確認 | Issue #118 監査で実測済み（poc-operator） |

## 合否判定

- `dotnet test`（該当テスト全緑）。E2E は compose 起動後に poc-operator（realm 同梱）で
  `/bff/admin/config`・`/drift` の 200 応答、poc-user・無認証で 404 を確認する。
