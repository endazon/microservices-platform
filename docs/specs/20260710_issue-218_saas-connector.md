---
title: SaaS データソースコネクタ（優先3）（Issue #218）
type: spec
status: done
related_ids:
  - FR-01
  - UC-04
  - IADR-0051
  - IADR-0054
author: claude
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/09_datasource-connectors.md (fixed・優先3 SaaS)"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-01)"
  - "../../planning/projects/microservices-platform/03_usecases (UC-04)"
---

# 仕様書: SaaS データソースコネクタ（優先3）（Issue #218）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-01 ／ ユースケース(UC): UC-04
- 技術検討: `09_datasource-connectors.md`（fixed・優先3 SaaS）— 取得=各 SaaS の API、変更検知=Webhook/ポーリング、
  認証=OAuth/API キー、**レート制限・ページングに対応**する。
- 関連 ADR: [[IADR-0051]]（コネクタ抽象・同期基盤）、[[IADR-0053]]（Wiki 汎用 REST 契約の先例）、
  [[IADR-0054]]（本 PR で作成・SaaS 汎用契約＋ページング＋429 バックオフ）
- Issue: #218（親 #195）

## 目的・背景

コネクタ抽象・同期基盤（[[IADR-0051]]）と Wiki 汎用契約（[[IADR-0053]]）の上に、優先3 SaaS コネクタを追加する。
SaaS API は製品ごとに異なるため、**設定駆動の汎用 REST 契約**（カーソルページング）で 1 コネクタを提供し、
**レート制限（HTTP 429）バックオフ**と**ページング**に対応する（[[IADR-0054]]）。製品固有アダプタは後続。

## 対象範囲（本 PR）

- 対象:
  - **SaaSConnector**（`Composable/Adapters`・`SourceType="saas"`）:
    - Discover: `GET {list}?{cursorParam}={cursor}` を**カーソルページング**でたどり `{ items:[{id,updatedAt,title?}], nextCursor }` を集約、`updatedAt > since` で増分。ページ数は安全上限で打ち切り。
    - Fetch: `GET {content}`（`{id}` 置換）→ 本文バイト＋content-type。
    - 認証: `Authorization: Bearer {Config["apiToken"]}`（OAuth アクセストークン/API キー・ログ非出力）。
    - **レート制限**: HTTP 429 を `Retry-After`（秒/日時）に従い待機して再試行（無ければ指数バックオフ・上限あり）。`maxRetries` 超過は例外送出→継続失敗アラート・watermark 非前進（[[IADR-0051]] 決定3a）。
  - DI 登録（既存 `AddHttpClient("SaaSConnector")` 追加＋`AddSingleton<IDataSourceConnector, SaaSConnector>`）。
  - 単体テスト（fake HttpMessageHandler）: ページング集約・増分・取得・認証・429 バックオフ再試行・429 上限超過で例外・未設定縮退。
  - ドキュメント: 本仕様書・[[IADR-0054]]・`docs/functional/FR-01`・`docs/tests/FR-01`。
- 対象外（follow-up）:
  - **製品固有 SaaS（Salesforce/Notion/Slack 等）アダプタ**・OAuth トークンの更新（refresh）フロー。
  - **Webhook（プッシュ更新通知）**。本 PR はポーリング（一覧 `updatedAt` 差分）で増分。
  - Vault 連携。API 応答の秘密マスクは既存（[[IADR-0053]] の `RedactSecrets`）を共用。
  - **実 SaaS API に対する統合テスト**（実 API/コンテナ前提）。

## CI で緑にできる範囲 / 実 API・コンテナ前提の切り分け

- **CI 緑（本 PR）**: SaaSConnector 単体テスト（fake HttpMessageHandler。429 は `Retry-After: 0` で高速化）。実 API 不要。
- **実 API/コンテナ前提（follow-up）**: 実 SaaS API への結合・OAuth 更新・Webhook は環境依存のため切り出し。

## 受け入れ基準（Issue #218）との対応

- [x] `sourceType=saas` の同期が対象 SaaS（汎用契約）から文書を取得し `RawDocumentFetched` を発行する。
- [x] ページング（カーソル）で全ページを集約し、`updatedAt > since` で増分同期する。
- [x] レート制限（429）を `Retry-After`／指数バックオフで再試行し、上限超過は継続失敗アラート経路（[[IADR-0051]]）に載る。
- [x] `IDataSourceConnector` 追加のみでコア改修不要（プラグイン方式）。
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が通る。
