---
title: 運用仕様書
type: operations-spec
status: in-progress
related_ids:
  - NFR
  - FR-13
  - FR-15
  - UC-07
  - ADR-0006
  - ADR-0011
author: claude
created: 2026-07-04
updated: 2026-07-10
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)"
  - "../../planning/projects/microservices-platform/06_technical/05_observability-ops.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md"
---

# 運用仕様書

> 必須ドキュメント（リポジトリ単位）。本リポジトリの運用を定める。雛形は `docs/templates/operations_spec_template.md`。
> **未記入のまま放置しない**。デプロイ・監視・バックアップ・障害対応を埋めること。

## 起点となる計画書（トレーサビリティ）

- 非機能要件（NFR・運用/可用性）: 運用・保守（障害検出 5 分以内・MTTR 30 分以内・アラート/Runbook 整備）、
  可用性 99.9%、スケーラビリティ（HPA で水平スケール）、独立デプロイ。計画: `02_requirements/01_requirements.md`、
  技術検討 `06_technical/05_observability-ops.md`。
- 関連 ADR / 技術検討: ADR-0006（可観測性 OTel/Prometheus/Loki/Tempo）／ADR-0007（ArgoCD + Helm）／
  ADR-0008（k3s）／ADR-0005・[IADR-0026]（mTLS）／ADR-0011・[IADR-0020]（Wiki）。実装 ADR: [IADR-0028]（fail-fast）／
  [IADR-0029]（ドリフト検出）。

## デプロイ

| 項目 | 内容 |
| --- | --- |
| 環境 | dev（docker-compose） / stg・prod（k3s + Istio + ArgoCD） |
| 実行基盤 | k3s（ADR-0008）。Helm チャート `deploy/helm/knowledge-platform`。Namespace `knowledge-platform`（Istio 注入有効） |
| 配備方式 | GitOps（ADR-0007）。ArgoCD が Git を単一の真実源として同期（`deploy/argocd/`）。レジストリは Harbor（`harbor.internal`） |
| サービス間通信 | Istio STRICT mTLS（ADR-0005 / IADR-0026）。手順 `deploy/istio/README.md` |
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
    Wiki.js を host 公開せず、到達を WikiService 経由に限定する（[IADR-0017]。k8s の Ingress 無効・NetworkPolicy）。
    **dev の compose は管理 UI セットアップ便宜のため 3001 を公開する（[IADR-0032](../adr/IADR-0032_wikijs-dev-exposure-opt-in.md)・#124）**が、
    **本番系（Helm）は `wikijs.ingress.enabled: false` で公開しない**。
    「本番系構成では 3001（ゲートウェイ迂回の外部到達）が公開されない」ことは `NetworkIsolationTests`
    （Helm `wikijs.ingress.enabled: false` の検証＋dev 公開が wiki-js に限定され他内部サービスへ波及しないこと）が回帰ガードする。
  - **段階導入（現状）**: 段1（配備・OIDC 構成・意思決定記録）に続き、**段2（本 PR）で実コードを実装**した ──
    `DocumentSyncConsumer` を Wiki.js への **GraphQL push 同期**（[IADR-0021]）へ置換し、`/wiki/pages` 系を
    Wiki.js 前段の**認可プロキシ**へ改修（ABAC 通過時のみ Wiki.js 本文をプロキシ）。`wiki_svc` は同期メタデータに
    限定した。フォロー作業（Issue #88）は**完了**: 稼働 Wiki.js での GraphQL PoC 実測・OIDC ローカルログイン
    無効化の稼働検証は [PoC 実測記録](../tech/20260707_wikijs-poc-record.md)、API キーの発行/投入手順は
    後述「Wiki.js 同期シークレットの発行・投入」を参照。削除・アーカイブの同期経路は
    [IADR-0023](../adr/IADR-0023_document-delete-archive-wikijs-propagation.md) で実装済み。

### 適用直後のドリフト即時検出（FR-15 / IADR-0029 フォローアップ 4 / #145）

宣言（`pipeline.json`）と実効構成のドリフトは、BFF が **定期（既定 5 分・`Drift:IntervalSeconds`）** に加え
**適用直後にも即時検出**する。不一致は構造化ログ `ConfigDrift=true`（`IDriftAlertSink`）で運用アラート経路へ流れる。

- **起動時即時検出**: `DriftDetectionHostedService` は起動直後に 1 回検出する。宣言（`pipeline.json`）変更時は
  BFF がロールアウト（#146 の checksum アノテーション）するため、宣言の適用直後はこの起動時検出で捕捉される。
- **ArgoCD PostSync フック**: `templates/drift-postsync-job.yaml` が各同期の完了後に BFF の
  `POST /internal/config/drift-run`（メッシュ内部限定・応答 202）を叩き、任意の同期後にも即時検出を起動する。
  無効化は `--set drift.postSyncHook.enabled=false`。
  - **Istio STRICT mTLS 下の到達性**: STRICT mTLS（PeerAuthentication STRICT・IADR-0026）では、サイドカー
    未注入 Pod からの `bff-service` 到達が Envoy に拒否される。そこで本 Job は `mesh.enabled` のとき
    **サイドカーを注入**し（`sidecar.istio.io/inject: "true"` ＋ `holdApplicationUntilProxyStarts`）、curl 実行前に
    Envoy 起動を待つ。処理後は `POST http://127.0.0.1:15020/quitquitquit` で Envoy を終了させて **Job を完了**させる
    （サイドカーが残って Job が完了しない既知事象を回避。native sidecar 非対応の Istio でも完了する）。`mesh.enabled=false`
    の場合はサイドカーを注入しない。
  - **失敗時の扱い**: BFF へ到達できない場合は Job が非ゼロ終了し、PostSync の失敗として顕在化する
    （`hook-delete-policy: BeforeHookCreation,HookSucceeded` により**失敗 Job は次回同期前まで残置**し調査可能）。
    ドリフト検出は起動時検出（BFF ロールアウト）でも行われるため、フック失敗＝「即時検出が一度実行できなかった」
    ことを意味し、ドリフト自体の有無とは独立。
  - **手動起動**: `kubectl run drift-trigger --rm -it --image=curlimages/curl --restart=Never -- \
    curl -fsS -X POST http://bff-service:8080/internal/config/drift-run`（mesh 有効時はサイドカー注入に留意）
- **手動確認（権限者）**: 運用者・管理者は `GET /bff/admin/config/drift` でドリフト結果を取得できる
  （`ConfigViewer` ポリシー。非権限者は 404 で秘匿）。

### 構成バージョンの注入（FR-15 / IADR-0029 フォローアップ 3 / #144）

BFF の構成情報 API（`GET /bff/admin/config`）は、適用中の構成定義の**構成バージョン**
（`Version.GitCommit` / `AppliedAt` / `AppliedBy`）を返す。値は環境変数
`Config__GitCommit` / `Config__AppliedAt` / `Config__AppliedBy`（`ConfigVersionOptions`）から取得する。

- **k8s（stg/prod）**: Helm values `config.gitCommit` / `config.appliedAt` / `config.appliedBy` を
  BFF Deployment へ注入する（`bff.configVersion: true`）。既定は `appliedBy: argocd`、gitCommit/appliedAt は空。
  **実値の供給**は GitOps（ADR-0007）側で行う:
  - ArgoCD Application（`deploy/argocd/application.yaml`）の `helm.parameters` が `config.appliedBy=argocd` を固定。
  - **適用リビジョン（コミット ID）と適用日時**は、ArgoCD ネイティブ Helm がビルド変数をパラメータへ
    自動展開しないため、CD が同期時に上書きする:
    `argocd app set knowledge-platform --helm-set config.gitCommit=$(git rev-parse HEAD) --helm-set config.appliedAt=$(date -u +%Y-%m-%dT%H:%M:%SZ)`
    （または release automation が `values-<env>.yaml` の `config.*` を更新して Git にコミットする）。
  - 手動確認: `helm template deploy/helm/knowledge-platform --set config.gitCommit=deadbeef` で
    BFF env に `Config__GitCommit=deadbeef` が反映される。
- **dev（compose）**: compose 起動時に**環境変数で実 Git コミット ID を渡す**。BFF は
  `Config__GitCommit=${GIT_COMMIT:-dev-local}` / `Config__AppliedAt=${GIT_COMMIT_DATE:-}` /
  `Config__AppliedBy=${GIT_COMMIT_BY:-compose}` を参照する。
  - **ヘルパ**: `scripts/compose-up.sh up -d` が `GIT_COMMIT`（`git rev-parse --short HEAD`）・
    `GIT_COMMIT_DATE`・`GIT_COMMIT_BY` を自動注入して起動する。これで dev の構成ビューアでも実コミット ID が返る。
  - 手動指定も可: `GIT_COMMIT=$(git rev-parse --short HEAD) docker compose -f deploy/docker-compose.yml up -d`。
  - 環境変数未設定時は `dev-local`（実適用リビジョンではないダミー）へフォールバックする。

### Wiki.js の起動・初期セットアップ・ヘルスチェック（FR-13 / UC-07 / IADR-0020）

- **起動**: `docker compose -f deploy/docker-compose.yml up -d` で `postgres` → `keycloak`（`--import-realm` で
  realm `knowledge-platform` と `wiki-js` クライアントを取り込む）→ `wiki-js` の順に起動する。
- **管理 UI への直接アクセス（dev のみ）**: 下記の初期セットアップ（OIDC 構成・ja ロケール導入・API キー発行）は
  ブラウザから Wiki.js 管理 UI（`http://localhost:3001`）へアクセスする。dev の compose は 3001 を公開している
  （[IADR-0032](../adr/IADR-0032_wikijs-dev-exposure-opt-in.md)・#124）。**本番系（Helm）は Wiki.js を公開しない**ため、
  管理 UI の直接操作は dev でのみ行う（本番系の到達は ABAC ゲートウェイ経由に限定）。
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

### 埋め込みプロバイダの設定・ゼロ保持・再索引（FR-02 / ADR-0016 / ADR-0017 / IADR-0025 / Issue #98）

埋め込みは取り込み時に**全文書本文**を送信するため、LLM 呼び出しよりデータ露出が大きい。機密区分で
送信先・モデル・コレクションが分かれる（`Embedding:Routing`）。

| 機密区分 | 送信先ティア | モデル / 次元 | コレクション | 既定状態 |
| --- | --- | --- | --- | --- |
| public / internal | ティアB（Voyage・保護契約） | voyage-3.5 / 1024 | `knowledge_chunks_voyage_3_5` | 有効（要 API キー） |
| confidential / restricted | ティアA（セルフホスト固定） | ruri-v3 / 768 | `knowledge_chunks_ruri_v3` | 無効＝**fail-closed** |

- **Voyage AI（ティアB）のゼロ保持設定（必須・受け入れ基準）**: 本番データを流す前に、Voyage AI の
  組織設定で**学習利用のオプトアウト（ゼロ保持 / zero-day retention）を有効化**する。08_data-egress-policy
  のティアB要件（ゼロ保持・学習不使用・レジデンシー）を契約で確認し、確認できるまで本番文書を索引しない。
  未認定の間は `Embedding__Routing__Endpoints__0__Enabled=false` で Voyage 経路を止められる。
  - API キーは Secret 経由で投入する（コミットしない）。compose: `.env` の `VOYAGE_API_KEY`
    （`Embedding__Voyage__ApiKey`）。k8s は Secret（例 `embedding-voyage`、key=`api-key`）。
  - キー未設定でも起動する（fail-open しない）。Voyage 呼び出しが失敗した文書は索引されないだけで、
    高機密文書の本文が外部へ出ることはない（ルーティングで候補にならないため）。
- **セルフホスト（ティアA / Ruri v3）の有効化**: 基盤（TEI / vLLM 等の OpenAI 互換 `/v1/embeddings`）を
  構築後、`SELFHOSTED_EMBEDDING_URL`（`Embedding__SelfHosted__BaseUrl`）と
  `SELFHOSTED_EMBEDDING_ENABLED=true`（`Embedding__Routing__Endpoints__1__Enabled`）を設定して有効化する。
  有効化まで confidential/restricted 文書は**索引されない**（fail-closed。設計どおり）。
  - 有効化後、社内文書サンプルで検索精度（nDCG@10）を実測し、voyage-3.5 比で大幅劣化しないことを確認する
    （ADR-0017 の事前 PoC 代替）。劣る場合は BGE-M3 へ切替（モデル別コレクション分離のため影響は局所）。
  - **⚠️ 配列インデックス依存の環境変数に注意（Issue #98）**: 上記 `Endpoints__0__Enabled`（Voyage）/
    `Endpoints__1__Enabled`（セルフホスト）は `appsettings.json` の `Embedding:Routing:Endpoints` 配列の
    並び順に依存する。エンドポイントの追加・並び替え時はインデックスを必ず見直すこと。取り違え
    （例 Voyage を誤って無効化し、セルフホストも無効のまま＝全 public 取り込み・検索クエリが黙って
    fail-closed）は起動時バリデーション（`EmbeddingRoutingOptionsValidator` / `ValidateOnStart`）が
    fail-fast で検知し、LlmGateway は起動に失敗する（ログに不整合内容を出力）。ティア↔プロバイダの
    取り違え・必須項目欠落も同時に検証される。
- **一時障害と fail-closed（意図的拒否）の区別（Issue #98）**: `/embed` は応答に `Retryable` を返す。
  - **一時障害**（送信先の不調・タイムアウト・予期しない空応答など、`Retryable=true`）: 取り込み消費側
    （`DocumentUpdatedConsumer`）は当該メッセージを**恒久スキップにせず例外を送出**し、MassTransit の
    受信リトライ／(枯渇後) DLQ に回す。一括再索引中に Voyage が一時的に不調でもチャンクを取りこぼさない。
    → 運用: DLQ（`*_error` キュー）を監視し、滞留があれば原因（Voyage 障害・URL 誤設定等）を解消して再投入する。
    → 注意（削除後・再構築前の空白期間）: 取り込みは冒頭で当該文書の既存チャンクを全モデル別コレクションから
    削除してから再索引する（機密区分変更時の残存防止）。一時障害でリトライ枯渇→DLQ 送りとなった文書は、
    **削除済み・未索引（0 チャンク）の状態で一時的に検索不可**となる（恒久欠落ではなく DLQ 再投入で回復する）。
    このため DLQ 滞留は検索網羅性に直結する運用指標として監視し、速やかに再投入すること。
  - **fail-closed / 恒久的理由**（高機密でセルフホスト未有効・次元不整合・プロバイダ未登録、`Retryable=false`）:
    設計どおり当該チャンクを**索引スキップ**し、`IngestionCompleted` は索引できた件数で発行する
    （警告ログに機密区分を記録）。再試行では解消しないため DLQ には回さない。
- **再索引手順（次元 1536→1024・モデル別コレクション移行）**:
  1. 取り込みサービスは起動時に不足コレクション（`knowledge_chunks_voyage_3_5` / `_ruri_v3`）を
     実次元で自動作成する（`QdrantBootstrapHostedService`）。旧 `knowledge_chunks`（1536 次元）は使用しない。
  2. 全文書に対し `DocumentUpdated` を再発行する（原本→正規化→取り込みを再走）。取り込み冒頭で全モデル別
     コレクションから当該文書を削除してから再索引するため、決定的チャンク ID により冪等に再構築される。
  3. 旧コレクション `knowledge_chunks` は移行完了後に手動削除する（`DELETE /collections/knowledge_chunks`）。
  - モデル差し替え（例 ruri-v3→BGE-M3）時も、当該コレクションを作り直し同手順で再索引する。

## 可用性・水平スケール（HPA / PDB）（NFR / #197）

計画 NFR「スケーラビリティ: HPA で水平スケール」「可用性: 99.9% 以上（月間ダウンタイム約 43 分以内）」の
実現手段を Helm チャート（`deploy/helm/knowledge-platform/`）の構成で提供する。適用は GitOps（ArgoCD）
経由で、構成変更のみで完結する。

### 実現手段

- **HorizontalPodAutoscaler（`templates/hpa.yaml`）**: CPU 使用率（`requests.cpu` に対する平均）で `minReplicas`〜
  `maxReplicas` に自動スケールする。metrics-server が必要（k3s は既定同梱）。既定は min=2 / max=4 /
  目標 CPU 70%（`values.yaml` の `scaling.hpa`）。
- **PodDisruptionBudget（`templates/pdb.yaml`）**: 自発的中断（ノードドレイン・ローリング更新）時に
  `minAvailable`（既定 1）レプリカを維持し、瞬断を防ぐ。
- **レプリカ所有権**: HPA 対象サービスは Deployment に静的 `replicas` を持たず HPA が所有する
  （`deployment.yaml` は `scaling.services` に含まれるサービスの `replicas` を出力しない。値の綱引きを避ける）。
- **ヘルスプローブ / ロールアウト**: 各 HTTP サービスは `readinessProbe`（`/health/ready`）・`livenessProbe`
  （`/health/live`）を持ち、Deployment 既定の RollingUpdate（maxUnavailable による無停止更新）と PDB で
  更新時の可用性を担保する。

### 適用対象（段階適用）

| 区分 | サービス | HPA/PDB |
| --- | --- | --- |
| 要求処理（ステートレス） | bff / retrieval / authorization / aianalysis / document / datasource / dashboard / feedback / wiki / llmgateway | **有効**（min 2 / max 4 / PDB minAvailable 1） |
| キュー駆動ワーカー | conversion / ingestion（`worker: true`） | 対象外（replicas 1 のまま） |
| ステートフル | minio / wikijs / postgres / qdrant | 対象外（各自の可用性方針） |

- ワーカー（conversion/ingestion）は RabbitMQ 競合コンシューマで水平化自体は可能だが、CPU ベース HPA が
  不適（キュー滞留がスケール指標）なため本段では対象外とし、負荷実測（#196）後に KEDA 等のキュー長ベース
  スケールを別途検討する。
- 対象の増減は `values.yaml` の `scaling.services` リストの変更（＋ GitOps 適用）のみで行う。

### 前提・確認事項

- **metrics-server** がクラスタに導入済みであること（HPA の CPU 指標に必須。k3s は既定同梱）。
- 全対象サービスに `resources.requests.cpu` が定義済みであること（HPA の利用率計算の分母。定義済み）。
- **Istio サイドカーの CPU 算入（既知の考慮事項）**: 本チャートは `mesh.enabled: true`（Envoy サイドカー自動注入）で、
  HPA の `metrics` は `type: Resource`（Pod 内**全コンテナ横断**の平均使用率）である。そのため Envoy サイドカーの
  CPU request/使用量も利用率計算の分母・分子に混入し、目標 70% の判定精度がアプリコンテナ実使用率からずれ得る
  （過小/過大スケール）。必要に応じて `autoscaling/v2` の `ContainerResource` 型でアプリコンテナ（`<name>-service`）
  のみを対象にする選択肢がある。この妥当性は負荷試験（#196）の確認項目とし、乖離が大きければ `ContainerResource`
  への切替を検討する（[IADR-0050] フォローアップ）。
- 実クラスタでの HPA スケール挙動・目標 CPU 値の妥当性は負荷試験（#196）で検証し、`scaling.hpa` を調整する。

## 監視・アラート（NFR / #198）

可観測性スタック（OTel Collector → Prometheus / Loki / Tempo → Grafana。ADR-0006）を配備済み。アプリは OTLP で
メトリクス/ログ/トレースを送出し、Collector が Prometheus（remote write）/ Loki / Tempo へ振り分ける。NFR
「障害検出 5 分以内・MTTR 30 分以内」に対し、SLO ベースのアラートルールを Prometheus に定義する。

- **アラートルール**: [`deploy/prometheus/alerts.yml`](../../deploy/prometheus/alerts.yml)（`prometheus.yml` の
  `rule_files` で読み込む）。**通知経路**は Alertmanager（`prometheus.yml` の `alerting`。受信先＝メール/チャットは
  運用環境ごとに配備・設定）。未配備でもルール評価は行われ Prometheus UI / Grafana から発火を確認できる。
- **ダッシュボード**: `deploy/grafana/provisioning/dashboards/knowledge-platform-overview.json`（サービス別
  スループット・5xx 率・p99・RAG レイテンシ）。
- **適用範囲（現状）**: Prometheus/アラートルール（`deploy/prometheus/alerts.yml`）と可観測性スタックは
  現状 **dev（docker-compose）にのみ配線**されている（`deploy/helm/knowledge-platform/` 配下に Prometheus/
  Alertmanager リソースは無い）。stg/prod（k3s）への Prometheus（Operator/rule 配備）・Alertmanager 通知の
  展開は follow-up（下記「未決事項」）。本節のアラート定義・閾値は環境非依存に流用できる。

| 監視対象 | 指標（メトリクス） | 閾値 | 通知先 | 対応 NFR |
| --- | --- | --- | --- | --- |
| 可観測性パイプライン | `up{job="otel-collector"}`（唯一の scrape 対象） | ==0 が 2 分 | Alertmanager（critical） | 検出 5 分以内 |
| サービス応答断（近似） | `rate(http_server_duration_milliseconds_count)` の途絶（直近まで受信有） | 0 が 5 分 | Alertmanager（warning） | 可用性 99.9% |
| HTTP エラー率 | 5xx 率 = `http_server_duration_milliseconds_count{http_status_code=~"5.."}` 比率 | > 5% が 5 分 | Alertmanager（critical） | 可用性 99.9% |
| 検索レイテンシ | retrieval-service p95（`http_server_duration_milliseconds_bucket`） | > 1.5s が 10 分 | Alertmanager（warning） | 検索 p95 1.5s |
| RAG レイテンシ | aianalysis `/analysis/ask` p95 | > 5s が 10 分 | Alertmanager（warning） | RAG 初回 5s |

- **push モデルの制約**: メトリクスは remote write（push）のため、古典的な per-service `up` は無い。サービスダウンは
  「直近まで受信していたリクエストメトリクスの途絶」で近似検知する（アイドル時の誤検知を避けるため `for` を長めに設定）。
  厳密なダウン検知は blackbox exporter / k8s の liveness による補完を follow-up とする。
- **follow-up（exporter/カスタムメトリクス配線後に有効化）**: RabbitMQ キュー滞留・デッドレター（RabbitMQ
  Prometheus プラグイン）、構成ドリフト Warning（ドリフト検出のカスタムメトリクス化。現状は監査/警告ログで表出）。
  `alerts.yml` 末尾にコメントで雛形を用意。

## バックアップ・リストア（NFR / #198）

状態を持つデータストアを対象にバックアップを取得し、復旧手順を定める。RPO/RTO は運用要件に応じて確定する
（初期目安 RPO ≤ 24h・RTO ≤ MTTR 30 分。重要度に応じ日次〜時間次へ調整）。

| 対象 | 内容 | 方式（例） | 頻度 | 保管 |
| --- | --- | --- | --- | --- |
| PostgreSQL（各サービス DB） | 業務データ（DB per Service。document / datasource / authorization / feedback / dashboard 等） | `pg_dump`／論理レプリケーション／ボリュームスナップショット | 日次（重要 DB は時間次） | 世代管理（例 7 日 + 週次 4） |
| Qdrant | ベクトル索引 | Qdrant スナップショット API（コレクション単位）。※ 索引は再取り込みで再構築可能（決定的チャンク ID・IADR-0002）＝ RPO 緩め | 日次 or 再構築前提 | 直近数世代 |
| MinIO | 正規化本文・資産（`knowledge-normalized` バケット） | バケットレプリケーション／`mc mirror`／ボリュームスナップショット | 日次 | 世代管理 |
| Wiki.js DB（PostgreSQL `wikijs`） | Wiki 閲覧コンテンツ（同期の従。正本は本システム側） | `pg_dump`。※ DocumentUpdated 再同期で再構築可能 | 日次 | 直近数世代 |
| Keycloak realm | 認証設定（クライアント・ロール・マッパー） | realm export（`deploy/keycloak/*-realm.json` を単一の真実源に、IaC で再適用） | 変更時（Git 管理） | Git 履歴 |

- **リストア手順（概略）**: ①対象データストアを停止/隔離 → ②該当バックアップからリストア（Postgres は
  `pg_restore`、MinIO は mirror 復元、Qdrant はスナップショット復元）→ ③依存サービスを再起動しヘルス確認 →
  ④整合確認（Qdrant/Wiki は必要なら `DocumentUpdated` 再発行で再構築。埋め込み再索引は本書「埋め込みプロバイダ」節参照）。
- **リストア演習**: ステージング整備（IADR-0049 / #207）後に定期実施し、RTO の実測と手順の妥当性を検証する（follow-up）。

## 障害対応（Runbook）（NFR / #198）

| 事象 | 検知 | 一次対応 | エスカレーション |
| --- | --- | --- | --- |
| LLM ゲートウェイ/外部 LLM 不調 | RAG レイテンシ/5xx アラート、`LlmGateway` 縮退ログ | RAG は縮退応答（送信せず縮退・fail-closed）。検索（非 LLM）は継続。エンドポイント設定/疎通確認 | 外部プロバイダ障害なら egress 設定でセルフホスト/別ティアへ切替（IADR-0025） |
| 埋め込みプロバイダ停止 | 取り込み失敗ログ、`EmbeddingEndpointTests` 相当の縮退 | 高機密はセルフホスト固定・未有効なら索引スキップ（fail-closed。IADR-0025）。プロバイダ復旧後に再索引（本書「埋め込み」節） | セルフホスト基盤の起動、モデル/次元整合の確認 |
| RabbitMQ 停止 | サービス接続エラー、パイプライン滞留 | ブローカ再起動。MassTransit は再接続。未処理は再配信（冪等消費のため重複安全） | 永続化ボリューム/ディスク確認。デッドレター滞留は原因メッセージを調査 |
| Qdrant 停止 | 検索 5xx/エラーログ | Qdrant 再起動。索引は再取り込みで再構築可能（決定的チャンク ID） | ボリューム障害時はスナップショットからリストア（バックアップ節） |
| PostgreSQL 停止 | サービス起動失敗/DB 接続エラー | DB 再起動・接続確認。書き込み不可の間は該当サービスを縮退 | データ破損時はバックアップからリストア（RPO/RTO 節） |
| サービス 5xx スパイク | `HighHttp5xxRate` アラート | 対象サービスのログ/トレース（Tempo）で原因特定。必要ならロールバック（Git revert → ArgoCD 同期） | 依存（DB/ブローカ/外部）起因の切り分け。HPA 上限到達なら `scaling` 見直し（#197） |
| 構成ドリフト検出 | ドリフト検出 Warning（監査/警告ログ。IADR-0029） | 宣言（`pipeline.json`）と実効の差分を確認。意図せぬ差分は Git を正として再同期 | 起動時 fail-fast（IADR-0028）で不整合構成の反映は阻止済み。恒常化は宣言の是正 |

- **エスカレーション/通知**: Alertmanager の受信先（メール/チャット）と担当・当番は運用体制に応じて定める（環境ごと）。
- **MTTR 目標（30 分）**: アラート（検出 5 分以内）→ Runbook 一次対応 → 復旧、の各段を Grafana/Tempo/Loki で追跡する。

## 未決事項

- **Alertmanager の受信先設定**: メール/チャット通知経路（`prometheus.yml` の `alerting.alertmanagers`）は
  運用環境ごとに配備・設定する（現状はターゲット未設定でルール評価のみ）。
- **監視の stg/prod（k3s）展開**: Prometheus/Alertmanager を Helm（Operator 等）で配備し、`alerts.yml` 相当の
  ルールと通知を k3s にも展開する（現状は dev/compose のみ配線）。
- **RabbitMQ キュー滞留・デッドレター・構成ドリフトのアラート**: それぞれ RabbitMQ Prometheus プラグインの
  exporter メトリクスと、ドリフト検出のカスタムメトリクス化が必要（`alerts.yml` 末尾に雛形をコメントで用意）。
- **サービスダウンの厳密検知**: push（remote write）モデルのため per-service `up` が無く、メトリクス途絶での
  近似検知に留まる。blackbox exporter / k8s liveness による補完を検討する。
- **保存時暗号化**: PostgreSQL/MinIO/Qdrant のインフラ層暗号化の有効化・鍵管理（`docs/security/security.md`
  データ保護表と連動）。
- **監査ログの保管期間・改ざん防止・エクスポート**: 可観測性基盤側の保持設定で確定する（NFR「監査ログ保持」の具体化）。
- **バックアップの RPO/RTO 確定とリストア演習**: ステージング整備（[IADR-0049] / #207）後に定期実施し実測する。
