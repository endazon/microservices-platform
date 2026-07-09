---
title: SC-06 データソース管理画面実装（Issue #132）
type: spec
status: completed
related_ids:
  - SC-06
  - UC-04
  - FR-01
  - FR-02
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
---

# 仕様書: SC-06 データソース管理（Issue #132）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-06 データソース管理
- ユースケース（UC）: UC-04（データソース登録・同期）
- 機能要求（FR）: FR-01（データソースカタログ）、FR-02（取り込み）、FR-05（ABAC 属性）
- 関連 ADR: [[IADR-0039]]（本 PR で作成・BFF 集約とロールゲーティング）、[[IADR-0035]]（存在秘匿ナビ）、[[IADR-0019]]（機密区分フェイルセーフ）
- Issue: #132（親 #121）

## 目的・背景

SPA 上に SC-06 を実装する。DataSourceService（`/datasources`）は実装済みだが BFF 未プロキシのため、Wave B 方針に従い本 PR で BFF 集約（`/bff/datasources`）も併せて実装する。データソースは文書 ABAC のスコープ対象ではなく運用資産のため、管理者・運用者に限定する（[[IADR-0039]]）。

## 対象範囲

- 対象:
  - 契約: `Shared.Contracts` に `DataSourceDto` / `CreateDataSourceRequest`。
  - BFF: `DataSourceBffEndpoints`（一覧・取得・登録・同期・無効化）。グループを `RequireRole(admin, operator)` で保護、Authorization 後段伝播、後段障害の縮退。`Program.cs` に named client 登録・マッピング。
  - フロント: `features/sc06-datasources`（`/datasources` ルート・ナビ、`RequireRole(admin, operator)`）。登録フォーム＋一覧＋同期／無効化。SC-07 への導線。
  - テスト: BFF（xUnit：ロール可否 403/401・CRUD・同期）、Vitest（一覧・登録ペイロード・同期・無効化・異常系）。
  - ドキュメント: 本仕様書・画面仕様書・テスト仕様書・IADR-0039。
- 対象外:
  - コネクタ個別の詳細設定 UI（Config の任意 key/value 編集）。最小限（既定属性＝機密区分）に留める。
  - DataSourceService 自体の認可強制（現状は BFF ゲートに依存。IADR-0039 フォローアップ）。
  - 変換ジョブ画面本体（SC-07・#133。導線のみ設置）。

## 受け入れ基準（Issue #132）との対応

- [x] 画面仕様書を作成（[SC-06_datasource-management.md](../screens/SC-06_datasource-management.md)）— 計画・UC-04 と整合。
- [x] データソースの登録・一覧・同期状態・コネクタ設定（既定属性）が画面から行える。
- [x] 権限外の情報が表示されない（admin/operator 限定・RequireRole 存在秘匿・BFF 403/401）。
- [x] テスト観点を `docs/tests/SC-06_datasource-management.md` へ展開。

## 実装判断

- 認可は BFF を実効境界とし、UI は表示制御専用（[[IADR-0035]]）。権限外は 403/401（データソースは存在秘匿対象外。[[IADR-0039]] §決定 3）。
- BFF ローカルのインラインロールポリシーを用い、共有 `AuthExtensions` を変更しない（サービス横断の副作用回避）。
