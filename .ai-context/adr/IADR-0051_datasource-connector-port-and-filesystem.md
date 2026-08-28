---
title: IADR-0051 データソースコネクタのポート分離（Discover/Fetch）と filesystem コネクタ・同期基盤
type: impl-adr
status: Accepted
related_ids:
  - FR-01
  - UC-04
  - ADR-0003
  - ADR-0027
  - IADR-0019
author: claude
created: 2026-07-10
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md (fixed)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-01)
  - planning:projects/microservices-platform/03_usecases (UC-04)
---

# IADR-0051: データソースコネクタのポート分離と filesystem コネクタ・同期基盤

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-01（データソース登録・同期・カタログ化。Must）／UC-04（定期/手動同期・失敗再試行/アラート）
- 技術検討: `06_technical/09_datasource-connectors.md`（fixed）— 共通 IF `Discover/Fetch/Watch|Poll/Map`、優先順位
  ファイルサーバー→Wiki→SaaS→業務DB、増分同期、RawFetched でパイプラインへ送出
- 関連 ADR: ADR-0003（MassTransit。Superseded by ADR-0027・注記は #580）／[IADR-0019](./IADR-0019_datasource-default-attributes.md)（データソース既定属性のフェイルセーフ）
- 関連仕様書: `docs/specs/20260710_issue-195_filesystem-connector-and-sync.md`、`docs/functional/FR-01`、`docs/tests/FR-01`
- Issue: #195（本体）／子 issue: Wiki/SaaS/業務DB コネクタ

## コンテキストと課題

`POST /datasources/{id}/sync` は固定パスのダミー `RawDocumentFetched` を発行するだけで**実データ取得を行わない**
（コネクタ種類ゼロ）。FR-01/UC-04 の受け入れ条件（実ソースからの取得・定期/手動同期・失敗時アラート）を満たさない。
確定設計に沿ってコネクタ抽象と優先順位第 1 位の filesystem コネクタを実装し、同期を実データ経路にする必要がある。

## 検討した選択肢

1. **コネクタ IF を単一の `Sync(source)` にまとめる**: 実装は単純だが、変更検知（Discover）と取得（Fetch）が密結合し、
   増分同期の watermark 制御やストレージ格納・イベント発行（Map）の共通化がコネクタ側に散る。
2. **Discover/Fetch をポートに分離し、Map（属性/タグ・ストレージ・発行）はオーケストレータへ集約（本決定）**:
   コネクタはソース固有 I/O に専念、属性方針（[IADR-0019](./IADR-0019_datasource-default-attributes.md) フェイルセーフ）・ストレージ格納・イベント発行・
   失敗アラートの一貫性はオーケストレータで担保する。設計の `Discover/Fetch/Map` に素直に対応する。

## 決定

1. **コネクタポート `IDataSourceConnector`（`Foundation/Ports`）を `DiscoverAsync`（変更検知付き列挙）と
   `FetchAsync`（原本取得）に分離する。** `Map` はオーケストレータ（`DataSourceSyncService`）が担い、
   ソースメタ→タグ・ABAC 属性の写像と機密区分フェイルセーフ（[IADR-0019](./IADR-0019_datasource-default-attributes.md)）を一元化する。
   新規ソースは本ポートを実装したコネクタを DI 登録するだけで対応する（`ConnectorRegistry` が SourceType で解決）。

2. **優先順位第 1 位の `FileSystemConnector`（`Composable/Adapters`）を実装する。** ルート
   （`Config["rootPath"]` 優先、無ければ `ConnectionUri` の `file://`／素のパス）配下の対応拡張子
   （.md/.txt/.html/.htm/.pdf/.docx）を再帰列挙。増分は更新日時（`since = LastSyncedAt`、初回=フルスキャン）。
   ルート未存在・アクセス不可・リモート URI（smb:// 等で `rootPath` 未指定）は**例外にせず空列挙で縮退**する。

3. **同期オーケストレータ `DataSourceSyncService` が Discover→Fetch→オブジェクトストレージ格納→
   `RawDocumentFetched` 発行を束ねる。** 原本は `IObjectStorageClient.PutBytesAsync` で格納
   （未構成時は `NullObjectStorageClient` が決定的 URI を返し縮退）。手動同期（/sync）と定期同期（HostedService）が共用する。

3a. **増分 watermark（`LastSyncedAt`）は完全成功時のみ前進させる（UC-04 再試行の担保・claude-review #220）。**
   `SyncResult.ShouldAdvanceWatermark`（`ConnectorAvailable && DiscoverSucceeded && Failed==0`）が真のときのみ
   `RecordSync()` を**オーケストレータ内で**呼ぶ（手動/定期で共通・呼び出し側は分岐しない）。discover 失敗
   （`DiscoverSucceeded=false`）・一部 fetch 失敗（`Failed>0`）時は watermark を進めない。進めると失敗/未取得
   ファイル（更新日時 <= 失敗時刻）が次回増分から漏れて恒久欠落するため。再取得は決定的 DocumentId で下流が
   冪等 upsert するため、成功済みファイルの再発行も安全。`FileSystemConnector` はフォルダ単位のアクセス
   エラーを握りつぶして列挙を継続する（1 フォルダの権限エラーでサイクル全体を失敗させない）。

4. **未対応 SourceType（wiki/saas/db）は 5xx にせず縮退**（`ConnectorAvailable=false`・発行 0 件）。子 issue で順次追加する。

5. **定期同期 `DataSourceSyncHostedService` は既定無効**（`DataSourceSync:Enabled=false`）。有効時に一定間隔
   （既定 300 秒・最短 30 秒に丸め）で active データソースを同期する（UC-04 基本フロー）。dev/test の意図せぬ走査を避ける。

6. **連続失敗はインメモリで追跡し、閾値（既定 3）超過で継続失敗アラート**（構造化ログ `Alert=true`）を出す
   （UC-04 例外フロー）。DB 永続化（`ConsecutiveFailures`/`LastError` 列）は follow-up とする。

## 理由

- **ポート分離**は増分 watermark 制御・ストレージ格納・属性方針・失敗アラートをオーケストレータへ集約でき、
  コネクタの実装コストを I/O に限定する（プラグイン追加を容易にする）。
- **縮退設計**（未存在ルート・未対応型・未構成ストレージ）は定期同期サイクルや API を 5xx で止めず、段階導入
  （filesystem 先行、他ソース後続）を安全に進められる。既存の縮退方針（Null クライアント）と一貫。
- **定期同期の既定無効**は、テスト・dev で実ファイル走査が走らないための安全既定。手動 /sync は常に有効。

## 影響

- `DataSourceService.Api`: `Foundation/Ports/IDataSourceConnector.cs`、`Composable/Adapters/FileSystemConnector.cs`、
  `Foundation/Services/{ConnectorRegistry,DataSourceSyncService,DataSourceSyncHostedService,SyncFailureTracker,DataSourceSyncOptions}.cs`、
  `DataSourceEndpoints`（/sync 実配線）、`Program.cs`（DI・オブジェクトストレージ登録）。
- デプロイ: `datasource-service` に ObjectStorage 接続を付与（compose の `objectstorage-env`、Helm `datasource.objectStorage: true`）。
- テスト: `FileSystemConnectorTests`（列挙/増分/取得/縮退）、`DataSourceSyncEndpointTests`（実 temp dir・属性 Map・未対応型縮退）。

## フォローアップ

- Wiki / SaaS / 業務DB コネクタ（優先 2〜4。子 issue）。
- 連続失敗状態・最終エラーの DB 永続化（SC-06 データソース管理 UI で可視化）。
- Vault 連携（接続情報の集中管理）。現状は `Config` からの取得（DB 平文保存・API 応答マスク）に留める。**一元追跡: #310**（`docs/security/security.md` §データソースのコネクタ資格情報）。
- 実 filesystem 同期の対象ファイル共有（SMB/NFS）マウント手順（PVC）と、増分 watermark をスキャン開始時刻へ厳密化。
- 15 分以内反映の実測（#196 の実測環境に依存）。

> **［2026-08-28 追記 / #458］上の「一元追跡: #310」は失効している。追跡先は #458 である。**
>
> **#310 は 2026-08-02 に `duplicate` で close された**（取り込んだのは #447、横断は **#458**）。
> **旧番号は消さない** —— 当時この追跡先を選んだことは史実であり、消すと「なぜ変わったのか」を
> 後から追えない。**新旧を並べて置く。**
>
> 本文が「Vault 移行までの暫定」と呼んでいた状態のうち、**平文が外へ出る経路は #458 で塞いだ**
> （応答・ログの 4 経路。マーカー集合の統合を含む。[IADR-0295](./IADR-0295_connector-credential-exposure-paths.md)）。
> **保存の平文そのものは残っている** —— それが #458（`blocked`。実クラスタが要る）の射程である。
