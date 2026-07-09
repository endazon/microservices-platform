---
title: SC-09 管理者設定画面（ABAC）実装（Issue #135）
type: spec
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
---

# 仕様書: SC-09 管理者設定画面（ABAC）（Issue #135）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-09 管理者設定画面（ABAC）
- ユースケース（UC）: UC-05（ABAC 属性・ポリシー管理）
- 機能要求（FR）: FR-09（ABAC 属性・ポリシー管理）、FR-05（ABAC）
- 関連 ADR: [[IADR-0040]]（本 PR で作成・透過中継と AdminOnly）、[[IADR-0006]]（属性参照中削除 409）、[[IADR-0035]]（存在秘匿ナビ）
- Issue: #135（親 #121）

## 目的・背景

SPA 上に SC-09 を実装する。AuthorizationService の管理 API（`/authz/policies`・`/authz/attributes`。AdminOnly 強制済み）は BFF 未プロキシのため、Wave B 方針に従い本 PR で BFF 集約（`/bff/admin/authz`）も併せて実装する。ABAC 設定は認可の根幹のため **platform-admin 限定**（[[IADR-0040]]）。保存前検証（矛盾・構文）と参照中削除競合を画面へ表示する。

## 対象範囲

- 対象:
  - 契約: `Shared.Contracts` に `AbacPolicyDto` / `AttributeDefinitionDto`。
  - BFF: `AuthzBffEndpoints`（属性辞書・ポリシー CRUD／有効切替の透過中継）。グループを `AdminOnly` で保護、Authorization 後段伝播、応答（status・本文）透過。AuthorizationService named client は既存を再利用。`Program.cs` にマッピング追加。
  - foundation: `ApiError.details` と `validation`/`conflict` 種別、`apiFetch` の 400/409 Problem 本文抽出（後方互換・追加のみ）。
  - フロント: `features/sc09-admin-abac`（`/admin/abac`・`RequireRole([Admin])`・ナビ）。属性辞書＋ポリシー管理、検証結果表示。
  - テスト: BFF（xUnit：AdminOnly 403/401・一覧・登録・検証 400 透過・競合 409 透過・有効切替）、Vitest（一覧・登録・検証エラー表示・ローカル JSON 検証・409 表示）、apiClient（400/409 詳細抽出）。
  - ドキュメント: 本仕様書・画面仕様書・テスト仕様書・IADR-0040。
- 対象外:
  - ポリシー条件のリッチ GUI ビルダ（`{key:[値]}` の JSON 入力に留める。構文はローカル、矛盾はサーバ検証）。
  - タグ辞書を属性辞書と別管理する UI（タグは属性辞書 scope=document の一種として扱う。計画の「タグ辞書」を包含）。

## 受け入れ基準（Issue #135）との対応

- [x] 画面仕様書を作成（[SC-09_admin-abac-settings.md](../screens/SC-09_admin-abac-settings.md)）— 計画・UC-05 と整合。
- [x] 属性・タグ・ポリシーの管理が画面から行える（属性辞書 CRUD・ポリシー CRUD／有効切替）。
- [x] platform-admin 以外はアクセスできない（`RequireRole([Admin])` 存在秘匿・BFF AdminOnly 403/401）。
- [x] 権限外の情報が表示されない（ABAC・存在秘匿の画面適用）。
- [x] テスト観点を `docs/tests/SC-09_admin-abac-settings.md` へ展開。

## 実装判断

- 透過中継により保存前検証・競合の詳細を画面へ確実に届ける（[[IADR-0040]] §決定 2/3）。DTO 変換だと検証本文が欠落するため採らない。
- operator を含めない（Issue #135 の明示）。SC-06/07 の admin+operator（[[IADR-0039]]）とは対象ロールが異なる。
