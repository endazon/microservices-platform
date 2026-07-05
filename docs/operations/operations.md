---
title: 運用仕様書
type: operations-spec
status: draft
related_ids:
  - FR-13
  - UC-07
  - ADR-0011
author: <作成者>
created: <YYYY-MM-DD>
updated: <YYYY-MM-DD>
plan_refs: []
---

# 運用仕様書

> 必須ドキュメント（リポジトリ単位）。本リポジトリの運用を定める。雛形は `docs/templates/operations_spec_template.md`。
> **未記入のまま放置しない**。デプロイ・監視・バックアップ・障害対応を埋めること。

## 起点となる計画書（トレーサビリティ）

- 非機能要件（NFR・運用/可用性）:
- 関連 ADR / 技術検討:

## デプロイ

| 項目 | 内容 |
| --- | --- |
| 環境 | dev / stg / prod |
| 手順 |  |
| ロールバック |  |

### サービス構成に関する運用注記

- **WikiService と Wiki.js**（FR-13 / UC-07 / [IADR-0020](../adr/IADR-0020_wiki-js-deployment-abac-gateway.md)、
  [IADR-0021](../adr/IADR-0021_wiki-js-sync-graphql-push.md)）:
  閲覧・編集 UI の実体は **Wiki.js**（`ghcr.io/requarks/wiki:2`、専用 DB `wikijs`）が担う。`WikiService` は
  「**同期・統合・ABAC ゲートウェイ**」に責務を縮退する。認可（ABAC）は本システムが単一の真実源であり、
  WikiService が Wiki.js の**前段**で deny-by-default の属性フィルタと 404 存在秘匿（[IADR-0009]）を強制する。
  Wiki.js 側のページ/グループ権限は補助的な表示制御に留める。
  （旧 [IADR-0013] の「Wiki.js 非配備・自前閲覧 API」は Issue #66 の (a) 選択により Superseded。）
  - **ネットワーク分離**: Wiki.js への ABAC は WikiService ゲートウェイに集約するため、共有/stg/prod では
    Wiki.js を host 公開せず、到達を WikiService 経由に限定する（[IADR-0017]。compose の `expose`、k8s の
    NetworkPolicy）。dev の compose は開発便宜で `3001:3000` を公開する。
  - **段階導入（現状）**: 本 PR は Wiki.js の配備・OIDC 構成・意思決定記録まで。`DocumentSyncConsumer` の
    Wiki.js 同期への置換・閲覧経路の認可プロキシ化・`wiki_svc` 撤去は後続 PR（要 PoC/ビルド環境。IADR-0020 段2）。

### Wiki.js の起動・初期セットアップ・ヘルスチェック（FR-13 / UC-07 / IADR-0020）

- **起動**: `docker compose -f deploy/docker-compose.yml up -d` で `postgres` → `keycloak`（`--import-realm` で
  realm `knowledge-platform` と `wiki-js` クライアントを取り込む）→ `wiki-js` の順に起動する。
- **ヘルスチェック**: Wiki.js は `GET /healthz`（コンテナ内 3000）を返す。compose の healthcheck は node で
  `/healthz` を叩く。dev では `http://localhost:3001/healthz`。
- **管理者ブートストラップ**: 初回アクセス（`http://localhost:3001`）で管理者アカウントのセットアップ画面が出る。
  管理者メール/パスワードを設定してセットアップを完了する（初回のみ。この初期管理者は保守用）。
- **OIDC 連携（Keycloak）**: 管理 UI → Administration → Authentication で **Generic OpenID Connect / OAuth2** を
  追加し、以下を設定する。Keycloak 側クライアントは realm import 済み（`wiki-js`、confidential、
  redirect `http://localhost:3001/*`）。
  - Client ID: `wiki-js` / Client Secret: realm import の値（dev は `wiki-js-dev-secret-change-me`。**本番は必ず変更**）。
  - Authorization Endpoint URL: `http://localhost:8080/realms/knowledge-platform/protocol/openid-connect/auth`
  - Token Endpoint URL: `http://keycloak:8080/realms/knowledge-platform/protocol/openid-connect/token`
    （サーバ間はコンテナ名 `keycloak`、ブラウザ経路は `localhost:8080`。issuer 整合のためホスト名の扱いに注意）
  - User Info / Logout: 同 realm の対応エンドポイント。Scope: `openid profile email`（`abac-attributes` で
    `clearance`/`department`/`groups` クレームを付与）。
- **ローカルログイン無効化（OIDC 単一経路）**: OIDC が疎通したら、Administration → Authentication で
  **Local** ストラテジを無効化し、OIDC のみを有効にする。これで受け入れ基準①「ローカルログイン不可」を満たす。
  （Wiki.js の OIDC 設定は管理 UI/DB シードで確定するため、Keycloak 側は本 PR で用意し、Wiki.js 側は本手順で実施する。）

## 監視・アラート

| 監視対象 | 指標 | 閾値 | 通知先 |
| --- | --- | --- | --- |
|  |  |  |  |

## バックアップ・リストア

<!-- 対象・頻度・保管期間・リストア手順・RPO/RTO -->

## 障害対応（Runbook）

| 事象 | 検知 | 一次対応 | エスカレーション |
| --- | --- | --- | --- |
|  |  |  |  |

## 未決事項
