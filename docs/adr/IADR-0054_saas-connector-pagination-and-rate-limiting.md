---
title: IADR-0054 SaaS コネクタは設定駆動の汎用 REST 契約＋カーソルページング＋429 バックオフで実装する
type: impl-adr
status: Accepted
related_ids:
  - FR-01
  - UC-04
  - IADR-0051
  - IADR-0053
author: claude
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/09_datasource-connectors.md (fixed・優先3 SaaS)"
---

# IADR-0054: SaaS コネクタは設定駆動の汎用 REST 契約＋ページング＋429 バックオフで実装する

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-01／UC-04／`09_datasource-connectors.md`（fixed・優先3 SaaS: API 取得・Webhook/ポーリング・
  OAuth/API キー・**レート制限/ページング対応**）
- 関連 ADR: [[IADR-0051]]（コネクタ抽象・同期基盤）／[[IADR-0053]]（Wiki 汎用 REST 契約の先例）
- 関連仕様書: `docs/specs/20260710_issue-218_saas-connector.md`
- Issue: #218（親 #195）

## コンテキストと課題

SaaS は製品ごとに API が異なり単一標準が無い。計画は「レート制限・ページングに対応」を明示する。優先2 Wiki
（[[IADR-0053]]）と同じく汎用契約で 1 コネクタを提供しつつ、SaaS 特有の**ページング**と**レート制限（429）**を
どう扱うかを決める必要がある。

## 検討した選択肢

1. **設定駆動の汎用 REST 契約＋カーソルページング＋429 バックオフ（本決定）**: 最小 JSON 契約（items＋nextCursor）を定義し、
   接続先・パス・トークン・カーソルパラメータ・再試行回数を `Config`／`ConnectionUri` で与える。429 は `Retry-After`
   に従い再試行する。製品固有は別途アダプタ/コネクタ（プラグイン）で対応。
2. **特定 SaaS（例 Notion）の API に直接実装**: 実運用に即すが 1 製品依存で汎用性が無い。
3. **オフセットページング固定**: カーソルより移植性が低い（多くの SaaS はカーソル/トークン方式）。

## 決定

**選択肢 1 を採用する。** `SaaSConnector`（`Composable/Adapters`・`SourceType="saas"`）は以下の**汎用 SaaS REST 契約**を用いる。

- **一覧（Discover・ページング）**: `GET {ConnectionUri}{listPath}`（既定 `/api/items`）を、応答 `{ items: [{ id, title?,
  updatedAt: ISO8601 }], nextCursor?: string }` の `nextCursor` が尽きるまで `?{cursorParam}={cursor}`（既定 `cursor`）で
  たどり集約する。ページ数は安全上限（既定 1000）で打ち切る（無限ループ防止）。`updatedAt > since` で増分（初回=全件）。
- **本文（Fetch）**: `GET {ConnectionUri}{contentPath}`（既定 `/api/items/{id}`、`{id}` 置換）→ 応答本文を原本バイト、
  content-type は応答ヘッダ→既定 `text/markdown`。
- **認証**: `Authorization: Bearer {Config["apiToken"]}`（OAuth アクセストークン/API キー。ログ非出力。GET 応答は
  既存 [[IADR-0053]] の `RedactSecrets` でマスク）。
- **レート制限（429）**: HTTP 429 を受けたら `Retry-After`（秒 or 日時）に従い待機して再試行する。`Retry-After` 不在時は
  指数バックオフ（上限つき）。`Config["maxRetries"]`（既定 3）超過は例外送出。
- **失敗時**: 429 上限超過・その他 HTTP/JSON 失敗は Discover/Fetch が**例外を送出**し、オーケストレータ（[[IADR-0051]]
  決定3a）が watermark を進めず継続失敗アラート経路に載せる（UC-04 再試行）。
- Map（属性/タグ付与）・格納・発行・定期同期は既存 `DataSourceSyncService` を共用（コネクタは Discover/Fetch に専念）。

**製品固有 SaaS アダプタ（Salesforce/Notion/Slack 等）・OAuth 更新・Webhook は本 PR の対象外**（後続）。

## 理由

- **プラグイン方針との一貫性**（[[IADR-0051]]/[[IADR-0053]]）: 汎用契約で 1 コネクタ、製品固有は拡張。
- **計画要件の充足**: ページング（カーソル）とレート制限（429 バックオフ）を汎用契約に組み込む。
- **CI 緑と実測の切り分け**: fake HttpMessageHandler で単体テスト可能（429 は `Retry-After: 0` で高速）。実 SaaS 結合は follow-up。

## 影響

- `DataSourceService.Api`: `Composable/Adapters/SaaSConnector.cs`（新規）、`Program.cs`（`AddHttpClient("SaaSConnector")`＋DI 登録）。
- テスト: `SaaSConnectorTests`（ページング集約・増分・取得・認証・429 再試行/上限・未設定縮退）。
- ドキュメント: 本 IADR・作業仕様書・FR-01 機能/テスト仕様。

## フォローアップ

- 製品固有 SaaS アダプタ・OAuth トークン更新（refresh）・Webhook（プッシュ更新通知）。
- HTTP コネクタ共通処理（認証ヘッダ・BaseUrl・Config 解決）の Wiki/SaaS 間での共通化（重複整理）。
- Polly 等による宣言的リトライ/サーキットブレーカへの置換。
- 実 SaaS API に対する統合テスト（実 API/コンテナ前提）／Vault 連携（`apiToken` の集中管理。**一元追跡: #310** — `docs/security/security.md` §データソースのコネクタ資格情報）。

## 関連

- Supersedes: なし
- Superseded by: なし
