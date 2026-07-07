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
| 環境 | dev（docker-compose） / stg・prod（k3s + Istio + ArgoCD） |
| 実行基盤 | k3s（ADR-0008）。Helm チャート `deploy/helm/knowledge-platform`。Namespace `knowledge-platform`（Istio 注入有効） |
| 配備方式 | GitOps（ADR-0007）。ArgoCD が Git を単一の真実源として同期（`deploy/argocd/`）。レジストリは Harbor（`harbor.internal`） |
| サービス間通信 | Istio STRICT mTLS（ADR-0005 / IADR-0024）。手順 `deploy/istio/README.md` |
| 手順 | ① Secret 投入（`deploy/bootstrap/README.md`）② Istio 導入（`deploy/istio/README.md`）③ ArgoCD 登録（`deploy/argocd/README.md`）。以降は Git 更新で自動同期 |
| デプロイ（サービス単位） | `values.yaml` の `services.<name>.tag` を Git 更新 → ArgoCD 自動同期（NFR: 独立デプロイ） |
| ロールバック | `argocd app rollback knowledge-platform <revision>` もしくは Git revert（GitOps 原則） |

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
  - **段階導入（現状）**: 段1（配備・OIDC 構成・意思決定記録）に続き、**段2（本 PR）で実コードを実装**した ──
    `DocumentSyncConsumer` を Wiki.js への **GraphQL push 同期**（[IADR-0021]）へ置換し、`/wiki/pages` 系を
    Wiki.js 前段の**認可プロキシ**へ改修（ABAC 通過時のみ Wiki.js 本文をプロキシ）。`wiki_svc` は同期メタデータに
    限定した。フォロー作業（Issue #88）は**完了**: 稼働 Wiki.js での GraphQL PoC 実測・OIDC ローカルログイン
    無効化の稼働検証は [PoC 実測記録](../tech/20260707_wikijs-poc-record.md)、API キーの発行/投入手順は
    後述「Wiki.js 同期シークレットの発行・投入」を参照。削除・アーカイブの同期経路は
    [IADR-0023](../adr/IADR-0023_document-delete-archive-wikijs-propagation.md) で実装済み。

### Wiki.js の起動・初期セットアップ・ヘルスチェック（FR-13 / UC-07 / IADR-0020）

- **起動**: `docker compose -f deploy/docker-compose.yml up -d` で `postgres` → `keycloak`（`--import-realm` で
  realm `knowledge-platform` と `wiki-js` クライアントを取り込む）→ `wiki-js` の順に起動する。
- **ヘルスチェック**: Wiki.js は `GET /healthz`（コンテナ内 3000）を返す。compose の healthcheck は node で
  `/healthz` を叩く。dev では `http://localhost:3001/healthz`。
- **管理者ブートストラップ**: 初回アクセス（`http://localhost:3001`）で管理者アカウントのセットアップ画面が出る。
  管理者メール/パスワードを設定してセットアップを完了する（初回のみ。この初期管理者は保守用）。
- **ja ロケールのインストール（必須・Issue #88 実測）**: 素の Wiki.js は `en` のみで、同期
  （GraphQL push）はロケール `ja` でページを作成するため、未インストールだと **FK 違反
  （`pages_localecode_foreign`）で全同期が失敗する**。管理 UI → Administration → Locale で Japanese を
  ダウンロードする（GraphQL では `mutation { localization { downloadLocale(locale: "ja") { ... } } }`。
  Wiki.js のロケール配信サーバへの外向き通信が必要）。
- **OIDC 連携（Keycloak）**: 管理 UI → Administration → Authentication で **Generic OpenID Connect / OAuth2** を
  追加し、以下を設定する。Keycloak 側クライアントは realm import 済み（`wiki-js`、confidential、
  redirect `http://localhost:3001/*`）。
  - Client ID: `wiki-js` / Client Secret: realm import の値（dev は `wiki-js-dev-secret-change-me`。**本番は必ず変更**）。
  - Authorization Endpoint URL: `http://localhost:8080/realms/knowledge-platform/protocol/openid-connect/auth`
  - Token Endpoint URL: `http://keycloak:8080/realms/knowledge-platform/protocol/openid-connect/token`
    （サーバ間はコンテナ名 `keycloak`、ブラウザ経路は `localhost:8080`）。
  - **Issuer: `http://localhost:8080/realms/knowledge-platform`**。issuer はブラウザ経路のホストに
    固定される（compose の `KC_HOSTNAME_URL` で固定済み）。`keycloak:8080` を設定すると ID トークン
    検証と userinfo が失敗する（「Failed to fetch user profile」。Issue #88 実測）。
  - User Info / Logout: 同 realm の対応エンドポイント（User Info はコンテナ内経路 `keycloak:8080`）。
    Scope は Wiki.js 固定の `openid profile email`（realm 側は `profile`/`email` スコープを定義済み。
    `abac-attributes` が default scope のため `clearance`/`department`/`groups` クレームは自動付与）。
  - Email Claim: `email` / Display Name Claim: `name` / Map Groups: 有効・Groups Claim `groups`
    （Keycloak サブグループ名に一致する Wiki.js グループへ自動割当。Self Registration 有効で
    初回ログイン時にユーザー自動作成）。
- **ローカルログイン無効化（OIDC 単一経路）**: OIDC が疎通したら、Administration → Authentication で
  **Local** ストラテジを無効化し、OIDC のみを有効にする。これで受け入れ基準①「ローカルログイン不可」を満たす。
  **稼働検証済み（Issue #88）**: 無効化後のローカルログインは `errorCode 1003（Invalid authentication
  provider）` で拒否され、OIDC 経路のみ有効となることを実測確認した。設定・検証の詳細は
  [Wiki.js 稼働 PoC 実測記録](../tech/20260707_wikijs-poc-record.md) を参照。

### Wiki.js 同期シークレットの発行・投入（FR-13 / IADR-0021 / Issue #88）

同期（GraphQL push）用のサービスアカウント API キーと、Wiki.js 専用 DB のパスワードは
**コミットせず**、以下の手順で発行・投入する。

- **API キーの発行（Wiki.js 管理 UI）**:
  1. 管理者で Wiki.js にログインし、Administration → **API Access** を開き、API を **Enabled** にする。
  2. **New API Key** で作成する。名前は `wiki-service-sync`、有効期限は運用ポリシーに合わせる
     （既定 3 年。ローテーション手順を後述）。権限グループは**ページの read/write/manage/delete を持つ
     グループ**を割り当てる（同期は `pages.create/update/delete` と `pages.singleByPath` を呼ぶ）。
  3. 表示されたキー（JWT）を安全な場所（シークレットマネージャ）へ控える。**再表示はできない**。
- **compose（dev）への投入**: リポジトリ直下または `deploy/` の `.env`（gitignore 済み）に
  `WIKIJS_API_KEY=<キー>` を記載し、`docker compose -f deploy/docker-compose.yml up -d wiki-service`
  で反映する（compose は `WikiJs__ApiKey: ${WIKIJS_API_KEY:-}` を参照）。
- **Helm（共有/stg/prod）への投入**: チャートは Secret を**参照のみ**するため、事前に作成する。
  ```bash
  # 同期用 API キー（wiki サービスの WikiJs__ApiKey が secretKeyRef で参照。key=apiKey）
  kubectl create secret generic wikijs-sync -n <namespace> \
    --from-literal=apiKey='<Wiki.js で発行した API キー>'
  # Wiki.js 専用 DB のパスワード（wikijs Deployment が参照。key=password）
  kubectl create secret generic wikijs-db -n <namespace> \
    --from-literal=password='<wikijs DB ユーザのパスワード>'
  ```
  ArgoCD 等の GitOps では SealedSecret / ExternalSecret で同名 Secret を供給する。
- **ローテーション**: Wiki.js 管理 UI で新キーを発行 → Secret を更新
  （`kubectl create secret ... --dry-run=client -o yaml | kubectl apply -f -`）→
  `kubectl rollout restart deployment/wiki` → 旧キーを Wiki.js 側で Revoke する。
  dev は `.env` を書き換えて `docker compose up -d wiki-service`。
- **注意**: API キーは Wiki.js の管理 GraphQL 全体に及ぶ強い権限を持つ。付与グループは最小権限とし、
  キーは wiki-service 以外へ配布しない（認可は本システムの ABAC ゲートウェイが単一真実源であり、
  キー漏えい時は Wiki.js 全ページの読み書きが可能になるため即時 Revoke する）。

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
