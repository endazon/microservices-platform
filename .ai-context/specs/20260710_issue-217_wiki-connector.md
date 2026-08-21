---
title: Wiki データソースコネクタ（優先2）（Issue #217）
type: spec
status: done
related_ids:
  - FR-01
  - UC-04
  - IADR-0051
  - IADR-0053
author: claude
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md (fixed・優先2 Wiki)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-01)
  - planning:projects/microservices-platform/03_usecases (UC-04)
---

# 仕様書: Wiki データソースコネクタ（優先2）（Issue #217）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-01（データソース登録・同期・カタログ化）
- ユースケース(UC): UC-04（定期/手動同期・接続失敗の再試行/アラート）
- 技術検討: `09_datasource-connectors.md`（fixed・優先2 Wiki）— 取得方式=API/エクスポート、変更検知=API 更新通知/ポーリング、
  認証=API トークン、Markdown/HTML を正規化対象に。「Wiki」はデータソース（既存社内Wiki）で閲覧基盤 Wiki.js とは別物。
- 関連 ADR: [IADR-0051](../adr/IADR-0051_datasource-connector-port-and-filesystem.md)（コネクタ抽象・同期基盤）、[IADR-0053](../adr/IADR-0053_wiki-connector-generic-rest-contract.md)（本 PR で作成・Wiki コネクタの汎用契約）
- Issue: #217（親 #195）

## 目的・背景

#195/[IADR-0051](../adr/IADR-0051_datasource-connector-port-and-filesystem.md) でコネクタ抽象（`IDataSourceConnector`: Discover/Fetch）と優先1 filesystem・同期基盤を実装済み。
本 PR は優先2の **Wiki（既存社内Wiki）コネクタ**をプラグインとして追加する（`IDataSourceConnector` 追加のみ、コア改修なし）。

## 対象範囲（本 PR）

- 対象:
  - **WikiConnector**（`Composable/Adapters`）: 設定駆動の汎用 Wiki REST 契約（[IADR-0053](../adr/IADR-0053_wiki-connector-generic-rest-contract.md)）でページを列挙・取得する。
    - Discover: `GET {ConnectionUri}{listPath}` → JSON ページ一覧（`id`/`title`/`updatedAt`。content-type は Fetch 応答ヘッダ→既定）→ `updatedAt > since` で増分。
    - Fetch: `GET {ConnectionUri}{contentPath}`（`{id}` を置換）→ 原本バイト＋content-type（Markdown/HTML）。
    - 認証: `Authorization: Bearer {Config["apiToken"]}`（秘密はログ出力しない・将来 Vault）。
  - DI 登録（`AddSingleton<IDataSourceConnector, WikiConnector>` ＋ `AddHttpClient`）。レジストリが `SourceType="wiki"` で解決。
  - 単体テスト（fake HttpMessageHandler）: 列挙・増分・取得・認証ヘッダ・HTTP 失敗時の例外（→ orchestrator が watermark 非前進）。
  - ドキュメント: 本仕様書・[IADR-0053](../adr/IADR-0053_wiki-connector-generic-rest-contract.md)・`docs/functional/FR-01`・`docs/tests/FR-01`。
- 対象外（follow-up）:
  - **製品固有 Wiki（Confluence/MediaWiki/DokuWiki 等）向けアダプタ**。本 PR は汎用契約を提供し、製品固有は別 child とする。
  - **実 Wiki コンテナに対する統合テスト**（CI で実コンテナを起こす対象が無い）。契約は文書化し、実測は環境準備後。
  - Webhook（プッシュ型更新通知）。本 PR はポーリング（一覧の `updatedAt` 差分）で増分する。
  - Vault 連携（秘密の集中管理）。現状は `Config` から取得。

## CI で緑にできる範囲 / 実コンテナ前提の切り分け

- **CI 緑（本 PR）**: WikiConnector 単体テスト（fake HttpMessageHandler で汎用契約の応答を模す）。実サーバ不要。
- **実コンテナ前提（follow-up）**: 実 Wiki 製品 API に対する結合検証。対象 Wiki を用意できる環境で `DockerFact` 相当として実施。

## 受け入れ基準（Issue #217）との対応

- [x] `sourceType=wiki` の同期が Wiki（汎用契約）から Markdown/HTML を取得し `RawDocumentFetched` を発行する
      （既存オーケストレータ `DataSourceSyncService` の Map・発行経路を共用）。
- [x] 変更検知（一覧の `updatedAt` を `since` と比較）で増分同期する。
- [x] 接続失敗時は Discover が例外を投げ、既存の継続失敗アラート・watermark 非前進（[IADR-0051](../adr/IADR-0051_datasource-connector-port-and-filesystem.md) 決定3a）に載る。
- [x] `IDataSourceConnector` 追加のみでコア改修不要（プラグイン方式）。
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が通る。
