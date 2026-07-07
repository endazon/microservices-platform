---
title: セキュリティ仕様書
type: security-spec
status: draft
related_ids:
  - FR-05
  - FR-09
  - FR-13
  - UC-07
  - NFR
  - ADR-0004
  - ADR-0005
  - ADR-0011
author: claude
created: 2026-07-02
updated: 2026-07-05
plan_refs: []
related_adrs:
  - ../adr/IADR-0017_internal-service-auth-network-isolation.md
  - ../adr/IADR-0020_wiki-js-deployment-abac-gateway.md
  - ../adr/IADR-0009_wiki-browsing-404-hides-existence.md
---

# セキュリティ仕様書

> 必須ドキュメント（リポジトリ単位）。本リポジトリのセキュリティを定める。雛形は `docs/templates/security_spec_template.md`。
> **未記入のまま放置しない**。認証・認可・データ保護・秘密情報管理・監査ログを埋めること。

## 起点となる計画書（トレーサビリティ）

- 非機能要件（NFR・セキュリティ）:
- 関連 ADR:

## 認証・認可

- **認証**: Keycloak（OIDC/JWT）による Bearer トークン認証（ADR-0004）。各サービスは `AddKnowledgePlatformAuth` で JWT を検証する。
- **認可（サービス内 RBAC）**: FR-09 の管理系エンドポイント（属性辞書・ABAC ポリシーの CRUD／有効無効切替／削除）は
  `AdminOnly` ポリシー（`platform-admin` ロール必須）で保護する。ロール未保持は 403。ロール名・ポリシー名は
  `KnowledgePlatformAuthPolicies` に定義。サービス間呼び出しの `POST /authz/scope`・`POST /authz/attributes/validate`
  は本ポリシーの対象外（認証のみ）。
- **ロールクレームの取得経路**: Keycloak はレルムロールを JWT の `realm_access.roles`（ネストした JSON クレーム）に
  格納する。標準の `JwtBearerHandler` はこれを `ClaimTypes.Role` へ展開しないため、`KeycloakRolesClaimsTransformation`
  （`IClaimsTransformation`）でトークン検証後に展開し、`RequireRole("platform-admin")` を成立させる。展開ロジックは
  単体テスト（`KeycloakRolesClaimsTransformationTests`）で検証。不正 JSON は fail-closed（ロール無し）で扱う。
- **認可（ABAC 本体）**: 文書アクセスの属性ベース認可は `AbacEvaluator`（deny-by-default）が担う（FR-05, ADR-0004）。
- 未対応: 全サービス横断のエンドポイント認可（P2 で拡充予定。ADR-0004）。

### Wiki.js 前段の ABAC 強制点（FR-13 / UC-07 / IADR-0020）— ⚠️ 機密性の要点

閲覧・編集 UI の実体を **Wiki.js** に委譲する（[IADR-0020](../adr/IADR-0020_wiki-js-deployment-abac-gateway.md)）。
Wiki.js の権限モデルは**ページ／グループ単位**であり、属性ベース（ABAC）の細粒度判定・deny-by-default・
存在秘匿を代替できない（ADR-0011 も明記）。したがって ABAC は**本システムが単一の真実源**とし、
**WikiService を Wiki.js の前段ゲートウェイ**として強制点を集約する。

- **強制内容**: 利用者 JWT 属性（`clearance` / `department`）× `/authz/scope` から許可スコープを解決し、
  Wiki.js の閲覧要求に deny-by-default で適用する。一覧は権限内ページのみ、個別アクセスは**権限外／不存在とも
  404 相当で存在秘匿**する（[IADR-0009] の意味論を継承。403 で存在を漏らさない）。判定は既存 `AbacPageFilter`
  （検索側 `InMemoryVectorStore.MatchesFilters` と同一意味論）を到達可否へ転用する。
- **直接到達の遮断**: 強制点をゲートウェイに集約するため、Wiki.js への**直接到達を塞ぐ**ネットワーク分離が
  前提（[IADR-0017]）。共有/stg/prod では Wiki.js を host 公開せず、到達を WikiService 経由に限定する
  （compose の `expose`、k8s の NetworkPolicy／Ingress 無効）。dev のみ開発便宜で Wiki.js を公開する。
- **Wiki.js 側の権限**: 補助的な表示制御に留め、機密性の担保には用いない。Keycloak realm import の
  `wiki-js` クライアントは `clearance`/`department`/`groups` クレームを付与するが、これは表示制御の補助であり
  ABAC の正本ではない。
- **多層防御（表示制御 `isPrivate`）**: ゲートウェイ経由 ABAC を第 1 防御・ネットワーク分離（[IADR-0017]）を
  第 2 防御としつつ、同期時に機密区分由来の粗粒度な非公開設定を Wiki.js へも伝える（第 3 防御）。
  `confidentiality=public` **以外（属性欠落を含む）は Wiki.js 上でも非公開**（`isPrivate=true`, deny-closed。
  [IADR-0021]）。NetworkPolicy が退行・誤設定されても public 以外の文書が Wiki.js 上で無条件公開に
  ならないための保険であり、細粒度の認可判定は引き続き本システムが単一真実源として担う。
- **秘密情報**: Wiki.js の OIDC クライアントシークレット・同期用 API キー（[IADR-0021]）は環境変数／Secret 経由で
  注入し、リポジトリにコミットしない。同期用 API キーは compose の `WIKIJS_API_KEY`、Helm の Secret `wikijs-sync`
  （key=`apiKey`）で投入する。realm import 内の dev 値（`wiki-js-dev-secret-change-me`）は開発専用で、
  共有/stg/prod では必ず変更する。
- **回帰防止**: `WikiEndpointsAbacTests` / `AbacPageFilterTests` が担保する受け入れ基準（一覧=権限内のみ・
  個別=404）を**新構成（認可プロキシ）で再充足**した（IADR-0020 段2 = 本 PR）。認可プロキシは ABAC 通過時のみ
  Wiki.js 本文を取得し、権限外・不存在・Wiki.js 未反映はいずれも 404 で存在秘匿する。稼働 Wiki.js を要する
  結合検証（GraphQL PoC）はフォローとして残る。

### サービス間（内部 API）の認証 — mesh 導入までの暫定方針（IADR-0017 / #62）

内部サービス API（例: DocumentService `/documents`、LlmGateway `/complete`・`/embed`、
DataSourceService `/datasources`、AuthorizationService `/authz/scope`・`/authz/attributes/validate`）は
「サービス間呼び出しのため認証対象外」として無認証で提供されている。これは **Istio mTLS（ADR-0005）を前提**にした
設計だが、Istio は未実装のため防御に空白がある。加えて、内部呼び出し（`RagOrchestrator`・`WikiAccessResolver`・
取り込み/変換ワーカー）は現状いずれも JWT を付与しておらず、特にバックグラウンドワーカーは
ユーザーコンテキストを持たないため素朴な JWT 必須化は成立しない。

**方針（IADR-0017）**: mesh（mTLS, ADR-0005）導入までは **「ネットワーク分離」を第一防御**とする。

- 内部サービス API を **host へ公開しない**（`docker-compose.yml` は BFF=エッジのみ host 公開、他は `expose`）。
  Kubernetes では ClusterIP + NetworkPolicy（デフォルト拒否）を前提とする。
- 外部からの入口は **BFF（エッジ）に一本化**し、BFF が Keycloak JWT で認証する。
- アプリ層のサービス間 JWT（client credentials）は全呼び出し元（トークン非保持ワーカー含む）対応が必要で
  規模が大きく、mTLS 導入で不要になるため**本 IADR では見送り**、残余リスクをネットワーク分離で受容して
  フォローアップで追跡する。
- 回帰防止として、内部サービスが host ポートを公開していないことを `NetworkIsolationTests` で機械的に担保する。
- RetrievalService `/search` の ABAC 取り扱いは #55 で別管理（host 公開停止のみ一律適用）。

## データ保護

| 区分 | 対象 | 方式 |
| --- | --- | --- |
| 保存時暗号化 |  |  |
| 通信時暗号化（外部→BFF） | クライアント〜エッジ | TLS（リバースプロキシ/Ingress で終端。ローカルは平文） |
| 通信時暗号化（サービス間） | 内部サービス間 | 現状は平文。ネットワーク分離で保護（IADR-0017）。将来 Istio mTLS（ADR-0005）で相互認証＋暗号化 |
| 個人情報 / 機微情報 |  |  |

## 秘密情報管理

<!-- 鍵・トークンの保管・ローテーション・コミット禁止 -->

## 監査ログ

| 対象イベント | 記録項目 | 保管期間 |
| --- | --- | --- |
|  |  |  |

## 脅威と対策

| 脅威 | 影響 | 対策 |
| --- | --- | --- |
| 内部 API へのホストからの無認証到達 | 全文書メタデータ＋ABAC 属性の列挙、無認証 LLM 呼び出し | 内部サービスを host 公開しない（IADR-0017）。エッジ(BFF)で JWT 認証。回帰は `NetworkIsolationTests` で担保 |
| 同一ネットワーク内からの内部 API 無認証到達（残余リスク） | ネットワーク内の侵害があれば内部 API へ到達可能 | ネットワーク分離で受容。k8s は NetworkPolicy、将来 mTLS（ADR-0005）で相互認証。フォローアップで追跡 |
| NetworkPolicy 退行・誤設定による Wiki.js への直接到達 | 機密文書が Wiki.js 上で無条件閲覧可能に | ABAC ゲートウェイ＋ネットワーク分離に加え、機密区分由来の `isPrivate`（public 以外は非公開）を多層防御として付与（IADR-0021）。稼働 Wiki.js での分離検証は PoC フォロー |
| 削除・非公開化された文書が Wiki.js に残存 | 撤回済み社内文書が外部システム（Wiki.js）に残り続ける | 現状は削除/アーカイブ同期経路が未実装（フォロー課題。IADR-0021）。`isPrivate` により public 以外は非公開だが、実体撤去・メタデータ Archived 化は別途対応 |
| 高機密文書本文の外部埋め込み API への送信（FR-02 / ADR-0016 / IADR-0025） | 取り込み時は本文全量を送るため露出が最大。confidential/restricted が外部（Voyage）へ出ると越境統制を破る | 埋め込み専用の越境ポリシー `EmbeddingEgress` で confidential/restricted を**ティアA（セルフホスト）固定**とし、外部（ティアB）を候補から除外。セルフホスト未有効なら**送信せず索引もしない（fail-closed）**。回帰は `EmbeddingEndpointTests`（外部プロバイダ未呼び出し）/ `DocumentUpdatedConsumerTests`（索引スキップ）で担保 |
| 機密区分変更時の旧コレクション残存（ABAC バイパス） | 例 public→confidential 変更後、旧 voyage コレクションに本文が残り機密扱いの文書が低区分コレクションで検索ヒット | 取り込み冒頭で全モデル別コレクションから当該文書を削除してから再索引する（`DeleteByDocumentFromAllAsync`）。回帰は `DocumentUpdatedConsumerTests` で担保 |
| Voyage AI のデータ保持・学習利用 | 送信本文が外部で保持・学習に利用される | 契約でゼロ保持（学習利用オプトアウト）を設定・確認してから本番データを流す（運用仕様書に記録）。未認定の間は Voyage 経路を無効化できる |

## 未決事項

- サービス間認証の恒久対策: Istio mTLS（ADR-0005）の導入、または client credentials による
  サービス間 JWT の全呼び出し元（トークン非保持ワーカー含む）への実装。IADR-0017 のフォローアップ。
  なお前提の ADR-0004/0005 は計画リポでは `Proposed`（未 Accepted）であり、暫定運用と NFR 草案
  （全 API OIDC/JWT・サービス間 mTLS）の相違・フェーズ分けは `feedback/20260705_internal-service-auth-nfr-deviation.md`
  で計画側へ環流済み（ADR-0005 確定が残余リスク解消の律速）。
- Helm/k8s の NetworkPolicy（デフォルト拒否）追補。
- インフラ系（postgres/rabbitmq/keycloak/qdrant/grafana 等）の公開は開発環境限定。共有・ステージング・本番では公開しない運用の明文化。
- RetrievalService `/search` の ABAC 取り扱い（#55）。
- Wiki.js 同期の**削除・アーカイブ経路**（削除/非公開化された文書の Wiki.js 側ページ撤去・非公開化、
  wiki_svc メタデータの `Archived` 化）。本 PR で Wiki.js が実コンテンツを保持するようになったため優先度が上昇。
  多層防御の `isPrivate`（public 以外は非公開）で緩和済みだが実体撤去は未対応（IADR-0021 フォロー）。
- 稼働 Wiki.js での GraphQL PoC（スキーマ整合・`isPrivate` ページのサービスアカウント本文取得可否・
  ネットワーク分離の CI/E2E 検証）。IADR-0021 フォロー。
