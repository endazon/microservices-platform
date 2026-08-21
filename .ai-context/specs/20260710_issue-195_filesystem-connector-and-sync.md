---
title: 実データソースコネクタ（ファイルサーバー）と同期基盤（Issue #195）
type: spec
status: in-progress
related_ids:
  - FR-01
  - UC-04
  - ADR-0003
  - IADR-0019
  - IADR-0051
author: claude
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-01)
  - planning:projects/microservices-platform/03_usecases (UC-04)
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md (fixed: コネクタ設計)
---

# 仕様書: 実データソースコネクタ（ファイルサーバー）と同期基盤（Issue #195）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-01（データソース登録・同期・カタログ化。Must）
- ユースケース(UC): UC-04（定期同期・手動同期・接続失敗の再試行/アラート）
- 技術検討: `06_technical/09_datasource-connectors.md`（fixed）— 共通インタフェース `Discover/Fetch/Watch|Poll/Map`、
  優先順位 ファイルサーバー→Wiki→SaaS→業務DB、増分同期（初回フルスキャン）、RawFetched でパイプラインへ送出
- 関連 ADR: ADR-0003（MassTransit）、[IADR-0019](../adr/IADR-0019_datasource-default-attributes.md)（データソース既定属性）、[IADR-0051](../adr/IADR-0051_datasource-connector-port-and-filesystem.md)（本 PR で作成）
- Issue: #195

## 目的・背景

現状 `POST /datasources/{id}/sync` は固定パス `/sample/path/document.docx` のダミー `RawDocumentFetched` を発行するのみで、
**実データ取得を行わない**。FR-01/UC-04 の受け入れ条件（実ソースからの取得・定期/手動同期・失敗時アラート）を満たさない。
確定設計（`09_datasource-connectors.md`）に沿ってコネクタ抽象と優先順位第 1 位の**ファイルサーバー（filesystem）コネクタ**を
実装し、同期を実データ経路にする。

## 対象範囲（本 PR = 増分 1）

- 対象:
  - **コネクタポート** `IDataSourceConnector`（`Foundation/Ports`）: `DiscoverAsync`（変更検知付き列挙）＋
    `FetchAsync`（原本取得）。設計の `Discover/Fetch/Map` に対応（`Map` は同期オーケストレータが担当）。
  - **FileSystemConnector**（`Composable/Adapters`）: ルート（`ConnectionUri` / `Config["rootPath"]`）配下の対応拡張子
    （.md/.txt/.html/.htm/.pdf/.docx）を再帰列挙。増分は更新日時（`since = LastSyncedAt`。初回=null=フルスキャン）。
  - **ConnectorRegistry**（`Foundation/Services`）: `SourceType` でコネクタを解決（未登録型は「コネクタ未対応」で縮退）。
  - **DataSourceSyncService**（`Foundation/Services`）: Discover→Fetch→オブジェクトストレージへ原本 `PutBytesAsync`→
    `RawDocumentFetched` 発行（実 `OriginalPath`/`StorageUri`/`ContentType`/既定属性/タグ）。`Map`（パス→タグ・属性）を実施。
    連続失敗の追跡と継続失敗アラート（UC-04 例外フロー）をインメモリで行う。
  - **DataSourceSyncHostedService**（`Foundation/Services`）: 定期同期（`DataSourceSync:IntervalSeconds` 既定 300、
    `Enabled` 既定 false）。有効時に active データソースをコネクタ経由で同期（UC-04 基本フロー「定期取得」）。
  - **/sync 実配線**: ダミー発行を `DataSourceSyncService.SyncAsync` へ置換し、同期サマリ（取得件数・エラー）を返す。
  - オブジェクトストレージ登録（`AddKnowledgePlatformObjectStorage`）。未設定時は `NullObjectStorageClient` で縮退。
  - テスト: FileSystemConnector（一時ディレクトリ）／DataSourceSyncService（fake storage + TestHarness）／/sync エンドポイント。
  - ドキュメント: 本仕様書・[IADR-0051](../adr/IADR-0051_datasource-connector-port-and-filesystem.md)・`docs/functional/FR-01`・`docs/tests/FR-01`。
- 対象外（follow-up・子 issue）:
  - **Wiki / SaaS / 業務DB コネクタ**（優先 2〜4）。子 issue に分割。
  - **Vault 連携**（接続情報の集中管理）。現状は `Config` からの取得に留める（秘密はコミットしない前提）。
  - **連続失敗状態の DB 永続化**（`ConsecutiveFailures`/`LastError` を DataSource に持たせる）。本 PR はインメモリ追跡。
  - **15 分以内反映の実測**（#196 の負荷/実測環境に依存）。経路は実データ化するが数値実測は別。

## 実装方針

1. ポートは Discover（変更検知）と Fetch（取得）に分離し、`Map`（メタ→属性/タグ）はオーケストレータへ集約する
   （コネクタはソース固有 I/O に専念。[IADR-0051](../adr/IADR-0051_datasource-connector-port-and-filesystem.md)）。
2. FileSystemConnector は `since` 未満（含む同時刻）を除外して増分化。ルート未存在・アクセス不可は例外にせず空列挙＋警告。
3. 同期はストレージ縮退（`NullObjectStorageClient`）でも `RawDocumentFetched` を発行する（URI は決定的・実体は未永続）。
   実ストレージ構成時のみ原本を永続化し、ConversionService が取得できる。
4. 連続失敗が閾値（既定 3）を超えたら構造化アラートログ（`Alert=true`）を出す（UC-04 例外フロー）。
5. 定期同期は既定無効（テスト・dev の意図せぬ走査を避ける）。本番は config で有効化する。

## 受け入れ基準（Issue #195）との対応

- [ ] `IDataSourceConnector`（Discover/Fetch）を `09_datasource-connectors.md` に沿って定義。
- [ ] FileSystemConnector が実ディレクトリの対応ファイルを列挙・取得し、増分（更新日時）で差分のみ返す。
- [ ] `/sync` が実コネクタ経由で原本をストレージへ格納し、実メタデータ付き `RawDocumentFetched` を発行する。
- [ ] 定期同期（HostedService）が active データソースを同期できる（有効化時）。
- [ ] 接続失敗の継続でアラートログを出す（UC-04 例外フロー）。
- [ ] 未登録 SourceType（wiki/saas/db）は縮退（「コネクタ未対応」）で 5xx を出さない。
- [ ] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が通る。
- [ ] Wiki/SaaS/DB コネクタと失敗状態の永続化を子 issue／follow-up に切る。
