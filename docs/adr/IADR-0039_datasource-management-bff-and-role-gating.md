---
title: IADR-0039 データソース管理の BFF 集約と管理系画面のロールゲーティング
type: impl-adr
status: Accepted
related_ids:
  - SC-06
  - UC-04
  - FR-01
  - FR-02
  - ADR-0004
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0004_abac-authorization-model.md"
---

# IADR-0039: データソース管理の BFF 集約と管理系画面のロールゲーティング

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: SC-06（データソース管理）／ UC-04 ／ FR-01（データソースカタログ）・FR-02（取り込み）
- 関連 ADR: ADR-0004（ABAC 認可モデル）／ [[IADR-0009]]（存在秘匿）／ [[IADR-0035]]（フロントのロール別ナビ・存在秘匿）／ [[IADR-0030]]（ConfigViewer ロール）
- 関連仕様書: `docs/screens/SC-06_datasource-management.md`

## コンテキストと課題

SC-06 はデータソースの登録・一覧・同期・無効化を行う運用画面である。DataSourceService（`/datasources`）は実装済みだが **BFF 未プロキシ**であり、また文書 ABAC のスコープ対象（機密区分による文書可視性）とは異なる「運用資産（コネクタ・接続先）」を扱う。

決めること:
1. データソース管理は **どのロール**に許可するか（一般社員に見せてよいか）。
2. 認可を **どこで**強制するか（BFF/フロント）。
3. 権限外への応答（403 か 404 秘匿か）。

## 決定

1. **管理系 Wave B 画面（SC-06 データソース管理・SC-07 変換ジョブ・SC-05 文書管理）は `platform-admin` もしくは `platform-operator` に限定する。** データソース・変換ジョブ・文書 CRUD はいずれも運用／コンテンツ管理者の職務であり、一般社員（閲覧者）には露出しない。SC-09（管理者設定・ABAC）は計画（Issue #135）の明示により **`platform-admin` のみ**とする（本 IADR の対象外。SC-09 側 IADR で記録）。
2. **サーバ側（BFF）を実効境界とする。** `/bff/datasources/*` はグループ全体を `RequireRole(platform-admin, platform-operator)` で保護する（インラインポリシー。共有 `AuthExtensions` に新ポリシーを追加せず、BFF ローカルに宣言してサービス横断の副作用を避ける）。フロントは `RequireRole`（[[IADR-0035]]）でルート／ナビを出し分け、権限外は NotFound を描画して**画面の存在を示さない**（UI は表示制御専用）。
3. **権限外は 403（無認証は 401）**とする。データソースは文書のような「存在自体の秘匿」対象ではなく（機密文書のタイトルが漏れる懸念が主眼の [[IADR-0009]] とは性質が異なる）、管理 API としては標準的な 403/401 が適切。画面の存在秘匿はフロントの `RequireRole`→NotFound で担保する。

## 根拠 / 代替案

- **ConfigViewer ポリシーの流用は採らない**: `ConfigViewer`（admin+operator）はロール集合は一致するが意味論が「構成情報の閲覧」であり、データソース管理へ流用すると監査・変更時に意図が不明瞭になる。BFF ローカルのインラインポリシーで自己記述的にする。
- **文書 ABAC スコープ（BffScopeResolver）は適用しない**: データソースは機密区分による文書可視性の対象ではなく、運用資産である。ロールで守るのが妥当。
- **後段への Authorization 伝播**: DataSourceService 現状は無認可だが、後段認可・監査の一貫性のため BFF は資格情報を伝播する（将来の後段強制に備える）。

## 影響

- `KnowledgePlatform.Shared.Contracts` に `DataSourceDto` / `CreateDataSourceRequest`（BFF↔SPA 契約）を追加。
- BFF に `DataSourceBffEndpoints` と `DataSourceService` named client を追加。
- フロント `features/sc06-datasources`（`/datasources` ルート・ナビ、admin/operator 限定）。
- SC-05・SC-07 も本方針（管理系＝admin/operator）に従う（各 IADR で個別記録）。SC-07 への導線（`/conversions`）を本画面に置く。

## フォローアップ

- DataSourceService 自体の認可強制（現状 BFF ゲートに依存）は将来の課題（[[merge-workflow-constraint]] とは無関係の運用強化）。
