---
title: IADR-0053 Wiki コネクタは設定駆動の汎用 REST 契約で実装し、製品固有アダプタは後続とする
type: impl-adr
status: Accepted
related_ids:
  - FR-01
  - UC-04
  - IADR-0051
author: claude
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/09_datasource-connectors.md (fixed・優先2 Wiki)"
---

# IADR-0053: Wiki コネクタは設定駆動の汎用 REST 契約で実装する

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-01／UC-04／`09_datasource-connectors.md`（fixed・優先2 Wiki: 取得=API/エクスポート、
  変更検知=更新通知/ポーリング、認証=API トークン）
- 関連 ADR: [[IADR-0051]]（コネクタ抽象 Discover/Fetch・同期基盤）
- 関連仕様書: `docs/specs/20260710_issue-217_wiki-connector.md`
- Issue: #217（親 #195）

## コンテキストと課題

計画は Wiki を優先2 データソースとし「API／エクスポート」で取得すると定めるが、社内 Wiki の実体は製品により
API が異なる（Confluence／MediaWiki／DokuWiki／独自 Wiki 等）。単一の標準 Wiki API は存在しない。コネクタを
どの API に対して実装するかを決める必要がある。

## 検討した選択肢

1. **特定製品（例 Confluence）の API に直接実装**: 実運用に即すが、他製品では使えず、最初の 1 製品に強く依存する。
2. **設定駆動の汎用 REST 契約に実装（本決定）**: 最小の JSON 契約（ページ一覧＋ページ本文）を定義し、接続先・
   パス・トークンを `Config`／`ConnectionUri` で与える。製品固有 API はこの契約へ薄いアダプタ（エクスポート/中継）で
   合わせるか、製品固有コネクタを別途追加する（プラグイン方式）。
3. **エクスポートファイル（ZIP 等）を filesystem コネクタで取り込む**: 変更検知・増分がファイル依存になり、
   Wiki の「更新通知/ポーリング」に合わない。

## 決定

**選択肢 2 を採用する。** `WikiConnector`（`Composable/Adapters`）は以下の**汎用 Wiki REST 契約**を用いる。

- **ページ一覧（Discover）**: `GET {ConnectionUri}{listPath}`（既定 `/api/pages`）。応答は JSON 配列で、各要素は
  `{ id: string, title?: string, updatedAt: ISO8601 }`。`updatedAt > since` で増分（初回=全件）。URL は
  `ConnectionUri`（末尾スラッシュ除去）＋`listPath` の文字列連結で組み立てる（URI 解決の意外な挙動を避ける）。
- **ページ本文（Fetch）**: `GET {ConnectionUri}{contentPath}`（既定 `/api/pages/{id}/content`、`{id}` を置換）。
  応答本文を原本バイトとし、content-type は**応答ヘッダ→既定 `text/markdown`**で決定する。
- **認証**: `Authorization: Bearer {Config["apiToken"]}`（存在時）。トークンはログ出力しない（将来 Vault へ移行）。
  かつ、`GET /datasources` 系の API 応答では `Config` 内の秘密キー（`token`/`password`/`secret`/`credential` を
  含むキー名）の値を `***` にマスクして返す（admin/operator であっても平文露出させない。claude-review #222）。
- **失敗時**: HTTP 失敗（ネットワーク/4xx/5xx）は Discover/Fetch が**例外を送出**する。オーケストレータ
  （[[IADR-0051]] 決定3a）が discover 失敗として watermark を進めず、継続失敗アラート経路に載せる（UC-04 再試行）。
- Map（ソースメタ→ABAC 属性/タグ）は既存 `DataSourceSyncService` が担う（コネクタは Discover/Fetch に専念）。

**製品固有アダプタ（Confluence/MediaWiki/DokuWiki 等）は本 PR の対象外**とし、必要に応じて別 child issue で追加する。

## 理由

- **プラグイン方針との一貫性**: 「新規ソースは `IDataSourceConnector` 追加のみ」の方針（[[IADR-0051]]）に沿い、
  汎用契約で 1 つの Wiki コネクタを提供しつつ、製品固有は追加コネクタ/アダプタで拡張できる。
- **CI 緑と実測の切り分け**: 汎用契約は fake HttpMessageHandler で単体テスト可能（実サーバ不要＝CI 緑）。実 Wiki 製品への
  結合は環境依存のため follow-up（実コンテナ前提）に切り出す。
- **失敗時挙動の再利用**: 例外送出により、既存の watermark 非前進・継続失敗アラート（[[IADR-0051]]）をそのまま活かす。

## 影響

- `DataSourceService.Api`: `Composable/Adapters/WikiConnector.cs`（新規）、`Program.cs`（`AddHttpClient`＋DI 登録）。
- テスト: `WikiConnectorTests`（fake HttpMessageHandler）。
- ドキュメント: 本 IADR・作業仕様書・FR-01 機能/テスト仕様。

## フォローアップ

- 製品固有 Wiki アダプタ（Confluence/MediaWiki/DokuWiki 等）。
- Webhook（プッシュ更新通知）による低遅延な変更検知。
- 一覧 API のページネーション対応（大規模 Wiki での一括取得回避）。本 PR は最小契約のため未対応。
- 実 Wiki 製品に対する統合テスト（実コンテナ前提）。
- Vault 連携（API トークンの集中管理。現状は `Config` から取得＋GET 応答マスク）。**一元追跡: #310**（`docs/security/security.md` §データソースのコネクタ資格情報）。

## 関連

- Supersedes: なし
- Superseded by: なし
