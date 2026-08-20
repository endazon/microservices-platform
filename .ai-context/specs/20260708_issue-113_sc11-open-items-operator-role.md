---
title: 作業仕様書 — SC-11 未決事項の対応（運用者ロール新設・ConfigViewer ポリシー）
type: spec
status: completed
related_ids:
  - FR-15
  - SC-11
  - ADR-0018
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
related_specs:
  - ../../docs/screens/SC-11_configuration-viewer.md
  - ../../docs/security/security.md
  - ../adr/IADR-0030_operator-role-and-config-viewer-policy.md
---

# 作業仕様書: SC-11 未決事項の対応（運用者ロール新設）

Issue: #113（親: #102 ／ 依存: #112）。SC-11 画面仕様書（`docs/screens/SC-11_configuration-viewer.md`、
ブランチ `claude/issue-113-20260707-2305` で作成済み・未マージ）の「未決事項」のうち、
本リポジトリで先行して確定・実装できる項目を対応する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-15** — 「閲覧は**管理者・運用者ロール**に限定する」（Must）
- 画面（SC）: **SC-11** — 「アクセス制御: 管理者・運用者ロール限定」
- 関連 ADR: ADR-0018（Composable Architecture）・ADR-0004（認証認可 Keycloak）

## 目的・背景

SC-11 仕様書の未決事項 1 が指摘するとおり、現行コードの認可は `AdminOnly`（`platform-admin`）のみで
**運用者ロールが未定義**。計画（FR-15・SC-11）は「管理者・運用者」の 2 ロールを明示的に要求しており、
ステークホルダー判断（2026-07-08）でも**運用者ロールを必要とする**方針が確定した。
#112（構成情報 API）・#113（構成ビューア画面）が同ロールを前提とするため、認可基盤側を先行整備する。

> **経緯（2026-07-08 追記）**: 本作業は当初ポリシー名を `AdminOrOperator` として起草したが、並行して
> 進んだ #112（PR #116）が同一セマンティクスのポリシー **`ConfigViewer`** を先に develop へマージした。
> 重複ポリシーを避けるため `ConfigViewer` へ統一し、IADR の採番も #116 側が IADR-0029 を使用したため
> 本決定は **IADR-0030** とした。以下は統一後の最終内容で記載する。

## 未決事項の処置一覧

| # | 未決事項 | 処置 |
| --- | --- | --- |
| 1 | 運用者ロールの新設 | **本作業で対応**（下記「設計」）。IADR-0030 に決定を記録 |
| 2 | 構成情報 API の実装配置 | 解決済み（#112 / PR #116・IADR-0029: BFF 配下 `/bff/admin/config` へ同居） |
| 3 | バージョン履歴のデータ源・保持範囲 | 画面実装時に確定（適用履歴の正データ選定が残る。仕様書に明記済み） |
| 4 | グラフのレイアウト方針 | 画面実装時に決定（変更なし・仕様書に明記済み） |
| 5 | ワイヤーフレーム（sc-11.drawio） | 計画リポジトリ側の作業。本リポでは対応不可（仕様書に明記） |
| 6 | フロントエンド基盤 | 後続フェーズ（他 SC 画面群と足並み）。本作業の対象外 |

## 対象範囲

- 対象:
  1. **ロール定数・ポリシー**: `KnowledgePlatformAuthPolicies` に運用者ロール `platform-operator` と
     ポリシー `ConfigViewer`（`platform-admin` **または** `platform-operator` を要求）を追加し、
     `AddKnowledgePlatformAuth` で登録する
  2. **Keycloak レルム定義**: `deploy/keycloak/knowledge-platform-realm.json` に
     レルムロール `platform-operator` を追加する
  3. **テスト**: `ConfigViewer` ポリシーの許可/拒否（admin ○ / operator ○ / 両方 ○ / その他ロール × / 匿名 ×）
  4. **文書更新**: SC-11 画面仕様書の未決事項 1 を解決済みに更新、`docs/security/security.md` に
     ロール・ポリシーの追記、決定の記録として `docs/adr/IADR-0030` を新設
- 対象外:
  - `ConfigViewer` を使う実エンドポイント（#112 の構成情報 API が使用済み。#113 の画面実装でも使用する。
    本作業ではポリシー登録まで）
  - 既存 `AdminOnly` エンドポイント（SC-10 ダッシュボード・FR-08 一覧・FR-09 管理系）の権限変更
    （計画上それぞれ管理者向けであり、FR-15 のような「運用者」明示がない。変更は行わない — IADR-0030 参照）
  - Keycloak への運用者ユーザーの追加（実ユーザー割当は運用作業。レルムにロール定義のみ追加）

## 設計

### ロール名・ポリシー（`src/Shared/KnowledgePlatform.Shared.Infrastructure/Foundation/Extensions/AuthExtensions.cs`）

```csharp
public const string ConfigViewer = "ConfigViewer";          // ポリシー名
public const string OperatorRole = "platform-operator";     // 運用者ロール
```

- `ConfigViewer` は `RequireRole(AdminRole, OperatorRole)`（いずれか一方で許可）で登録する。
- 既存の `KeycloakRolesClaimsTransformation`（`realm_access.roles` → `ClaimTypes.Role` 展開）は
  ロール名に依存しないため変更不要。
- 命名は既存 `platform-admin` と対称の `platform-operator` とする（根拠は IADR-0030）。

### Keycloak レルム

`roles.realm` に `{ "name": "platform-operator", "description": "FR-15: 構成情報の閲覧（ConfigViewer ポリシー）…" }` を追加。
クライアントスコープ `roles`（`realm_access.roles` 発行）は既存定義がそのまま適用される。

## 受け入れ基準

- [x] `KnowledgePlatformAuthPolicies` に `ConfigViewer` / `OperatorRole` が定義され、ポリシーが登録される
- [x] `platform-admin` のみ・`platform-operator` のみ・両方保持のいずれでも `ConfigViewer` を通過する
- [x] どちらのロールも持たない認証済みユーザー・匿名は `ConfigViewer` を通過しない（fail-closed）
- [x] Keycloak レルム定義に `platform-operator` が追加されている
- [x] SC-11 画面仕様書の未決事項 1 が解決済みとして更新され、IADR-0030 が作成されている
- [x] 既存の `AdminOnly` 保護エンドポイントの挙動が変わらない（既存テストがすべてパス）
- [x] `/verify`（ビルド・テスト・lint）がパスする（2026-07-08: build 0 エラー / 全 13 テストプロジェクト
      333 件パス / `dotnet format --verify-no-changes` パス / `check-doc-links` OK）

## テスト方針

- `AuthorizationService.Api.Tests` に `ConfigViewer` ポリシーの単体テスト（`ConfigViewerPolicyTests`）を追加する
  （`AddKnowledgePlatformAuth` で構築した `IAuthorizationService` に対しロール別 ClaimsPrincipal で評価。
  既存 `KeycloakRolesClaimsTransformationTests` と同じ配置・流儀）。
- 既存サービスのエンドポイントテスト（AdminOnly 系）は変更しない＝リグレッション確認として全実行する。
