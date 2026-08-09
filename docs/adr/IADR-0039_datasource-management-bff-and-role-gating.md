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
  - IADR-0127
  - IADR-0128
author: claude
created: 2026-07-09
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md"
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

   > **［2026-08-05 追記・計画との差異は裁定待ち（#503 / [IADR-0127](IADR-0127_sc07-retry-admin-only-and-derived-states.md) 決定 1、#501 / [IADR-0128](IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) 決定 2）］**
   > 計画 05_screens は §共通シェル（`01_screens.md:115`）に加え、
   > **§SC-05（`:234`）・§SC-06（`:242`）・§SC-07（`:250`）**
   > の**各節でも独立して**「管理者ロール限定」と定める。本決定 1（admin **または** operator）と正面から食い違う。
   > **閲覧ロールの差異は planning#198（提案 8）で裁定待ちである。**
   > どちらに決まっても計画側の改訂か実装の是正が要る（現状は計画と実装の双方が確定済みのまま食い違っている）。
   > **一方、再変換（`retry`）の実行権限は裁定を要さず、両側で `platform-admin` へ是正済みである**——
   > 画面のボタンは #503（PR #508。[[IADR-0127]] 決定 1）、
   > API（`POST /bff/conversion/jobs/{id}/retry`）は #501（[[IADR-0128]] 決定 1）が担った。
   > **照会（`GET /bff/conversion/jobs` 系）は本決定 1 のまま admin/operator で据え置いてある**
   > ——ここで併せて絞ると裁定を待たずに実装が先に答えを出すためである（[[IADR-0128]] 決定 2）。
   > 据え置きは `GetList_AsOperator_IsAllowed` / `GetById_AsOperator_IsAllowed` で回帰ガードしている。
   > **本 IADR は裁定まで `Accepted` のまま有効**であり、[[IADR-0127]] / [[IADR-0128]] は本 IADR を置換しない。

   > **［2026-08-09 追記 / #628］上の「裁定待ち」は解消した。本決定 1 は閲覧について計画と一致し、
   > 書き込みについては計画が正である。**
   >
   > planning#198 提案 8 の裁定（**Q19**・2026-08-05 確定）は
   > 「**閲覧は管理者・運用者に開く。破壊的操作は管理者限定を維持する**」であり、
   > **計画側が改訂されて本決定 1 の閲覧範囲（admin **または** operator）を採った。**
   > すなわち割れていたのは閲覧ではなく**書き込み**であった。
   >
   > - **閲覧**（`GET /datasources` 系・`GET /bff/conversion/jobs` 系・SC-05 の照会）:
   >   **本決定 1 のまま有効**である。上の追記が「据え置き」と呼んでいたものは、裁定によって
   >   **据え置きではなく確定**になった。回帰ガード（`GetList_AsOperator_IsAllowed` 等）もそのまま活きる。
   > - **書き込み**（登録・更新・無効化）: **計画が正であり、実装を狭めた。**
   >   `POST /datasources` と `DELETE /datasources/{id}` は BFF・後段の両方で `AdminOnly` を積む
   >   （[[IADR-0128]] 決定 1 と同じ形・[[IADR-0044]] の多層防御）。`PUT` / `PATCH` は #534 が既に同形で作っている。
   > - **手動同期**（`POST /datasources/{id}/sync`）: **破壊的操作に含めない**（planning#299・2026-08-09 裁定）。
   >   admin ＋ operator のままであり、**現行実装を追認したものである**。
   >
   > **SC-05 の文書書き込み（`POST` / `PUT` / `PATCH` / `DELETE` / `publish` / `archive`）は
   > 同型の逸脱が残っている**（グループ既定 admin ＋ operator のまま）。#628 の射程外であり、
   > **#629 として起票した**（[[IADR-0116]] 規約 4）。
   >
   > > **［2026-08-09 追記 / #629］★ この逸脱は解消した。** SC-05 の 6 口（サービス側）と
   > > 5 口（BFF 側。**`PATCH /{id}/metadata` に BFF の口は無い**——実測）へ `AdminOnly` を積み、
   > > 画面も 5 つのボタンを管理者だけに出すようにした。**閲覧は運用者に開いたままである。**
   > > **`publish` / `archive` は計画の列挙に名前が無いが、planning#299 の基準を当てはめて
   > > 管理者限定と判断した**（作業仕様書 [20260809_issue-629_document-write-admin-only.md](../specs/20260809_issue-629_document-write-admin-only.md) §判断 1）。
   > > **これで管理系 3 画面（SC-05 / SC-06 / SC-07）の同型の逸脱は残っていない。**
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
