---
title: セキュリティ仕様書
type: security-spec
status: draft
related_ids:
  - FR-05
  - FR-09
  - NFR
  - ADR-0004
  - ADR-0005
author: claude
created: 2026-07-02
updated: 2026-07-04
plan_refs: []
related_adrs:
  - ../adr/IADR-0017_internal-service-auth-network-isolation.md
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

## 未決事項

- サービス間認証の恒久対策: Istio mTLS（ADR-0005）の導入、または client credentials による
  サービス間 JWT の全呼び出し元（トークン非保持ワーカー含む）への実装。IADR-0017 のフォローアップ。
- Helm/k8s の NetworkPolicy（デフォルト拒否）追補。
- インフラ系（postgres/rabbitmq/keycloak/qdrant/grafana 等）の公開は開発環境限定。共有・ステージング・本番では公開しない運用の明文化。
- RetrievalService `/search` の ABAC 取り扱い（#55）。
