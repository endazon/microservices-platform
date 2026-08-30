---
title: データソース登録・同期・カタログ化 機能仕様書
type: functional-spec
status: completed
created: 2026-06-27
updated: 2026-08-30
author: claude
---
<!-- trace:
ids: [FR-01, FR-05, SC-06, UC-04]
adrs: [ADR-0002, ADR-0003, ADR-0014, ADR-0027]
iadrs: [IADR-0001, IADR-0051, IADR-0053, IADR-0054, IADR-0055, IADR-0148, IADR-0295, IADR-0304]
specs: [20260627_FR-01_data-source-catalog-pipeline]
issues: [#195, #217, #218, #219, #458, #537, #546, #580, planning#200]
-->

# 機能仕様書: データソース登録・同期・カタログ化

> 機能単位の仕様。計画リポジトリの要求・ユースケースを実装向けに詳細化する。

## 起点となる計画書（トレーサビリティ）

- 機能要求: 社内データソースの登録・同期とカタログ化
- ユースケース: データソースを登録・同期する
- 関連 ADR: DB per Service、メッセージング基盤（MassTransit + RabbitMQ。後継の Wolverine 採用により Superseded・注記は #580）
- 実装 ADR: カタログの正本所有と `DocumentNormalized` の購読責務
- 作業仕様書: データソース同期→カタログ化パイプラインの接続

## 概要

複数の社内データソース（ファイルサーバー／Wiki／業務DB／SaaS）を登録し、同期によって取得した
文書を正規化（Markdown 化）してカタログに登録し、検索インデックスへ取り込む。
利用者は権限内の全データソースを横断検索でき、結果に出典が付く。

本機能はイベント駆動マイクロサービス（メッセージング基盤の決定。後継の Wolverine 採用により Superseded・注記は #580）で構成され、各サービスが疎結合に連携する。

## 機能詳細

| 項目 | 内容 |
| --- | --- |
| 入力 | データソース定義（名称・種別・接続URI・接続設定）、同期トリガ |
| 処理 | 登録 → 同期（原本取得）→ 変換（Markdown 正規化）→ **カタログ登録** → チャンク化・埋め込み → ベクトルストア登録 |
| 出力 | カタログ文書（正規化済）、検索可能なチャンク、横断検索結果（出典付き） |
| 業務ルール | 権限外文書は検索結果・AI回答に一切現れない（ABAC アクセス制御）。更新は定義時間内に検索へ反映する。 |

## サービス構成とイベントフロー

```mermaid
flowchart TB
  U[利用者/スケジューラ] -->|POST /datasources/:id/sync| DSS[DataSourceService]
  DSS -->|RawDocumentFetched| CONV[ConversionService]
  CONV -->|DocumentNormalized| DOC[DocumentService（カタログ正本）]
  DOC -->|DocumentUpdated| ING[IngestionService]
  DOC -->|DocumentUpdated| WIKI[WikiService]
  ING -->|Upsert| QD[(Qdrant)]
  ING -->|IngestionCompleted| DOC
  CLI[利用者] -->|POST /search| RET[RetrievalService]
  RET -->|vector + ABAC filter| QD
```

## 処理フロー（同期→カタログ化）

1. `POST /datasources` でデータソースを登録（種別: `filesystem` / `wiki` / `db` / `saas`）。
2. `POST /datasources/{id}/sync` で同期を起動 → `RawDocumentFetched` を発行。
3. `ConversionService` が原本を Markdown へ正規化 → `DocumentNormalized` を発行。
4. **`DocumentService` が `DocumentNormalized` を購読し、カタログへ登録**（`status=normalized`、`MarkdownUri` 付き）→ `DocumentUpdated` を発行。
5. `IngestionService` が `DocumentUpdated` を購読し、チャンク化・埋め込み・Qdrant 登録 → `IngestionCompleted`。
6. `WikiService` が `DocumentUpdated` を購読し Wiki ページへ同期。
7. `RetrievalService` の `POST /search` がベクトル検索＋ABAC 属性フィルタで横断検索結果を返す。

## 実装状況（2026-07-19 時点）

| 区間 | 状態 | 備考 |
| --- | --- | --- |
| データソース CRUD | ✅ 実装済 | DataSourceService |
| 同期トリガ（`/sync`） | ✅ **実コネクタ経由（filesystem / wiki / saas / db）** | コネクタのポート分離と filesystem 実装（#195）／Wiki＝設定駆動の汎用 REST 契約（#217）／SaaS＝汎用契約＋カーソルページング＋429 バックオフ（#218）／業務DB＝参照専用 SQL による行→文書化（#219）。実データを列挙・取得しストレージ格納＋実メタ付き `RawDocumentFetched` を発行。 |
| 定期同期（スケジューラ） | ✅ **HostedService（既定無効）** | `DataSourceSyncHostedService`（`DataSourceSync:Enabled`）。データソース同期の基本フロー。 |
| 同期健全性（連続失敗回数・再試行上限・直近エラー） | ✅ **エンティティへ永続化し `DataSourceDto` で返す** | #537 / 裁定 Q14。**継続失敗のしきい値は再試行上限（5）に達した時点**。直近エラーは保存時点でマスクする。同期の例外フロー「継続失敗はアラートする」の表示側の土台である（健全性はエンティティへ永続化する、という実装判断）。発報は構造化ログ（`Alert=true`）である。**［2026-08-30 更新 / #546］Alertmanager 自体は配備済みだが、この事象に対応する Prometheus のアラートルールが無い**ため、依然として自動では届かない（配線されていないのは通知基盤ではなくルールの側である）。 |
| 更新（`PUT` 全置換 / `PATCH` 部分更新） | ✅ **管理者限定** | #534 / 裁定 Q16。従前は「削除→再登録」しかなく **ID と履歴が切れた**（認証情報のローテーションのたびに文書の出所の追跡が切れる）。**更新は `Id` / `CreatedAt` / `LastSyncedAt` / 健全性を変えない**。 |
| 接続失敗の継続アラート | ✅ **インメモリ追跡** | 連続失敗閾値超過で構造化アラートログ（同期の例外フロー）。DB 永続化は follow-up。 |
| 変換（pandoc） | ✅ **実装済（pandoc 実変換・`--extract-media` 図抽出）** | `PandocConversionService`。pandoc 未導入／原本がローカル解決不能（実オブジェクトストレージ未接続）の dev 環境ではプレースホルダ本文へグレースフルデグレード。 |
| **正規化文書→カタログ登録** | ✅ 実装済 | `DocumentNormalizedConsumer`。 |
| カタログ CRUD | ✅ 実装済 | DocumentService |
| チャンク化・埋め込み・Qdrant | ✅ 実装済 | IngestionService（Markdown 本文は `StorageDocumentContentReader` が取得。実オブジェクトストレージ未接続時はプレースホルダへデグレード） |
| 検索＋ABAC フィルタ | ✅ 実装済 | RetrievalService（結果の属性復元に既知欠陥） |

## 未決事項 / 後続タスク

- filesystem・Wiki・SaaS・業務DBの 4 コネクタは実装済み（優先1〜4）。
- 業務DB の実 SQL 正当性・参照専用ユーザー権限は実 PostgreSQL 統合テスト（DockerFact）で確認する follow-up。
- 他 DB プロバイダ（SQL Server/MySQL 等）アダプタ・CDC は follow-up。
- 製品固有アダプタ（Wiki=Confluence/MediaWiki 等／SaaS=Salesforce/Notion 等。いずれも汎用 REST 契約の後続）・OAuth 更新・Webhook・
  実 API/コンテナ統合テストは follow-up。
- 実 filesystem 同期の対象ファイル共有（SMB/NFS）マウント手順（PVC）と、増分 watermark のスキャン開始時刻厳密化。
- 接続失敗状態・最終エラーの DB 永続化（データソース管理画面での可視化）。
- Vault 連携（接続情報の集中管理）。現状は `Config` / `ConnectionUri` からの取得（DB 平文保存・**露出経路はマスク**）に留める。
  Vault / External Secrets 移行の一元追跡は **#458** である（従前ここは #310 と書いていたが、**#310 は 2026-08-02 に `duplicate` で close** され、#447 が取り込み、横断は #458 が持つ）。
- 実オブジェクトストレージ（製品未確定）クライアントの接続。pandoc 実変換（`PandocConversionService`）・
  Markdown 本文取得（`StorageDocumentContentReader`）は実装済みだが、実ストレージ未接続時（`file://` 以外）はプレースホルダへデグレードする。
- 同期ジョブの進捗・状態管理。
- 検索結果への属性・タグ復元（`QdrantVectorStore`）。
- 出典（出自データソース）の永続化と検索結果への整形表示。
- 負荷試験による p95 レイテンシ確認。
