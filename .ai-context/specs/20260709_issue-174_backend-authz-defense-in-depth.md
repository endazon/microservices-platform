---
title: バックエンドサービスの書き込み/管理APIへの認可強制（Issue #174）
type: spec
status: done
related_ids:
  - FR-01
  - FR-06
  - FR-09
  - UC-03
  - UC-04
  - IADR-0044
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-09)
---

# 仕様書: バックエンドサービスの書き込み/管理APIへの認可強制（Issue #174）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-09（認可）／FR-01（データソース）／FR-06（文書）
- ユースケース(UC): UC-03（文書）／UC-04（データソース）
- 関連 ADR: [IADR-0044](../adr/IADR-0044_backend-service-authorization-defense-in-depth.md)（本 PR で作成）、ADR-0004（Keycloak）、[IADR-0017](../adr/IADR-0017_internal-service-auth-network-isolation.md)（ネットワーク分離）、
  [IADR-0039](../adr/IADR-0039_datasource-management-bff-and-role-gating.md)（DataSource BFF ロール）、[IADR-0041](../adr/IADR-0041_document-write-bff-abac-scoped.md)（Document write BFF）、[IADR-0042](../adr/IADR-0042_conversion-job-read-model.md)（Conversion）
- Issue: #174

## 目的・背景

管理系画面は BFF でロールを強制していたが、**後段サービス（DataSourceService/DocumentService）の
書き込み/管理エンドポイントは認可を課しておらず BFF ゲートに依存**していた。BFF を迂回してメッシュ内部
から直接叩かれると認可が効かない。本 PR で後段サービスにも `RequireAuthorization` を付与し、多層防御
（サービスを最終防衛線）とする（[IADR-0044](../adr/IADR-0044_backend-service-authorization-defense-in-depth.md)）。

## 対象範囲

- 対象:
  - `DataSourceService`: `/datasources` グループに `RequireRole(admin, operator)`（[IADR-0039](../adr/IADR-0039_datasource-management-bff-and-role-gating.md) と一致）。
  - `DocumentService`: 書き込み（POST/PUT/PATCH metadata/publish/archive/DELETE）を別グループへ分離し
    `RequireRole(admin, operator)`（[IADR-0041](../adr/IADR-0041_document-write-bff-abac-scoped.md) と一致）。読み取り（GET）は据え置き。
  - テスト: 両サービスに `TestAuthHandler`（既定 admin・`X-Test-Roles` で上書き）を追加。既存テストを
    認証下で通す。権限外 403 の否定テストを追加。
  - ドキュメント: 本仕様書・[IADR-0044](../adr/IADR-0044_backend-service-authorization-defense-in-depth.md)・`docs/security/security.md` の追記。
- 対象外:
  - `ConversionService` `/jobs`（[IADR-0042](../adr/IADR-0042_conversion-job-read-model.md) §決定3。認証基盤未導入。follow-up）。
  - `AuthorizationService` `/authz/scope`（内部呼び出し。無認可維持）。管理系 `/authz` は既に AdminOnly。
  - 文書作成時の付与属性 ABAC スコープ厳密検証（[IADR-0041](../adr/IADR-0041_document-write-bff-abac-scoped.md) 見送り分。follow-up）。

## 受け入れ基準（Issue #174）との対応

- [x] DataSourceService `/datasources`（CRUD・sync）が admin/operator 以外に 403。
- [x] DocumentService 書き込み（POST/PUT/PATCH/publish/archive/DELETE）が admin/operator 以外に 403。
- [x] DocumentService 読み取り（GET）は一般利用者でも従来どおり可能（回帰なし）。
- [x] BFF→後段の正常系（トークン伝播）が壊れない。
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が通る。

## 実装判断・計画フィードバック

- ロール要件は BFF ゲートと一致させ、後段でインライン `RequireRole` により二重化（[IADR-0044](../adr/IADR-0044_backend-service-authorization-defense-in-depth.md)）。
- ConversionService 除外と属性 ABAC 厳密検証は follow-up として [IADR-0044](../adr/IADR-0044_backend-service-authorization-defense-in-depth.md) に明記。

## テスト観点

- DataSourceService: admin/operator で CRUD・sync が 2xx、非権限ロールで 403、（TestAuthHandler は常時認証のため）
  401 は対象外。
- DocumentService: 書き込みは admin/operator で 2xx・非権限で 403、読み取りは非権限（一般）でも 2xx。
