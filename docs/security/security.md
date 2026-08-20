---
title: セキュリティ仕様書
type: security-spec
status: in-progress
created: 2026-07-02
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: [FR-01, FR-02, FR-03, FR-05, FR-09, FR-11, FR-13, FR-15, NFR-11, SC-05, SC-11, UC-07]
adrs: [ADR-0002, ADR-0004, ADR-0005, ADR-0011, ADR-0016]
iadrs: [IADR-0009, IADR-0012, IADR-0017, IADR-0020, IADR-0021, IADR-0023, IADR-0025, IADR-0026, IADR-0029, IADR-0030, IADR-0039, IADR-0041, IADR-0042, IADR-0044, IADR-0047, IADR-0048, IADR-0049, IADR-0051, IADR-0053, IADR-0054, IADR-0055, IADR-0066, IADR-0075, IADR-0077, IADR-0080, IADR-0206, IADR-0216, IADR-0220]
specs: [01_requirements, ADR-0004_authz-abac, ADR-0005_service-mesh-istio, IADR-0009_wiki-browsing-404-hides-existence, IADR-0017_internal-service-auth-network-isolation, IADR-0020_wiki-js-deployment-abac-gateway, IADR-0026_mesh-mtls-supersedes-network-isolation]
issues: [#55, #100, #198, #199, #201, #211, #212, #222, #271, #310, #628, #629, planning#383]
-->

# セキュリティ仕様書

> 必須ドキュメント（リポジトリ単位）。本リポジトリのセキュリティを定める。雛形は `docs/templates/security_spec_template.md`。
> **未記入のまま放置しない**。認証・認可・データ保護・秘密情報管理・監査ログを埋めること。

## 起点となる計画書（トレーサビリティ）

- 非機能要件（NFR・セキュリティ）: 認証（Keycloak OIDC）／認可（ABAC）／データ越境統制（LLM egress）／
  監査ログ保持／通信暗号化（mTLS）
- 関連 ADR（計画）: 認可＝ABAC / Keycloak OIDC（**Accepted 2026-07-06**）／サービスメッシュ Istio の mTLS（**Accepted 2026-07-06**）／
  Wiki エンジンの権限。実装 ADR: STRICT mTLS の第一防御化／削除・アーカイブの伝播／権限外は 404 とする存在秘匿／
  Wiki.js の `isPrivate` 付与／埋め込みの機密区分ルーティング／運用者ロールの新設／後段サービスの多層防御

## 認証・認可

- **認証**: Keycloak（OIDC/JWT）による Bearer トークン認証。各サービスは `AddPlatformAuth` で JWT を検証する。
- **認可（サービス内 RBAC）**: 属性・ポリシー管理の管理系エンドポイント（属性辞書・ABAC ポリシーの CRUD／有効無効切替／削除）は
  `AdminOnly` ポリシー（`platform-admin` ロール必須）で保護する。ロール未保持は 403。ロール名・ポリシー名は
  `PlatformAuthPolicies` に定義。サービス間呼び出しの `POST /authz/scope`・`POST /authz/attributes/validate`
  は本ポリシーの対象外（認証のみ）。
- **運用者ロール**: 構成情報の閲覧（構成情報 API `/bff/admin/config`・
  構成ビューア #113）は `ConfigViewer` ポリシー（`platform-admin` **または** `platform-operator`）で
  保護する。非権限（無認証を含む）には 404 で応答自体を秘匿する（構成情報 API の実装判断と、権限外は 404 とする存在秘匿）。
  運用者（`platform-operator`）は構成閲覧のみ可能で、管理系操作（`AdminOnly`）は不可。ロールは
  Keycloak レルム（`deploy/keycloak/microservices-platform-realm.json`）に定義し、実ユーザーへの割当は
  運用作業とする。ポリシー判定は単体テスト（`ConfigViewerPolicyTests`）で検証（詳細:
  運用者ロール `platform-operator` を新設し `ConfigViewer` ポリシーで判定する、という実装判断による）。
- **ロールクレームの取得経路**: Keycloak はレルムロールを JWT の `realm_access.roles`（ネストした JSON クレーム）に
  格納する。標準の `JwtBearerHandler` はこれを `ClaimTypes.Role` へ展開しないため、`KeycloakRolesClaimsTransformation`
  （`IClaimsTransformation`）でトークン検証後に展開し、`RequireRole("platform-admin")` を成立させる。展開ロジックは
  単体テスト（`KeycloakRolesClaimsTransformationTests`）で検証。不正 JSON は fail-closed（ロール無し）で扱う。
- **認可（後段サービスの多層防御。属性・ポリシー管理の要求と、書き込み/管理 API への認可強制）**: 管理系画面の認可は BFF 集約点でロールを強制する
  （**［2026-08-09 / #628・#629］データソースの登録・無効化と、文書書き込みのうち 5 口は
  `platform-admin` 限定へ狭めた**。計画側の文書管理画面の裁定 Q19「破壊的操作は管理者限定」に合わせたものである。
  読み取りと手動同期は `platform-admin` または `platform-operator`。データソース管理・文書管理の BFF 集約の実装判断による）が、
  BFF 迂回のメッシュ内部直呼びに備え、**後段サービスにも同一のロール要件を二重化**する（サービスが最終防衛線）。
  - `DataSourceService` `/datasources`（一覧・登録・sync・無効化）: admin/operator 必須。
  - `DocumentService` 書き込み: **更新・メタデータ・公開・アーカイブ・削除は admin 必須**。
    **作成（`POST`）だけ admin/operator のまま据え置く** —— `ai-stock-trading` の KB 書き込みが
    BFF を経由せず直接叩いており、その service-account は `platform-operator` しか持たないためである
    （KB 書き込み用クライアントの実装判断。計画へ裁定を依頼中）。**人間に対する境界は BFF 側（`AdminOnly`）で閉じている。**
    読み取り（GET）は一般利用者の文書閲覧のため据え置き（機密制御は取得段の ABAC が担う）。
  - 利用者トークンは BFF が後段へ伝播する（各 *BffEndpoints の `CreateForwardingClient`）。非権限は 403。
    否定テストは各サービスの `*AuthorizationTests` で検証。
- **認可（ABAC 本体）**: 文書アクセスの属性ベース認可は `AbacEvaluator`（deny-by-default）が担う。
- 未対応（多層防御のフォローアップ）: `ConversionService` `/jobs` の後段認可（認証基盤未導入・ingress 非公開で緩和。
  変換ジョブ読み取りモデルの実装判断 §決定 3）、文書作成時の付与属性が呼び出し者 ABAC スコープ内かの厳密検証（文書管理の BFF 集約で見送った分）。

### Wiki.js 前段の ABAC 強制点— ⚠️ 機密性の要点

閲覧・編集 UI の実体を **Wiki.js** に委譲する（Wiki.js を配備し `WikiService` を「同期・ABAC ゲートウェイ」へ縮退する実装判断）。
Wiki.js の権限モデルは**ページ／グループ単位**であり、属性ベース（ABAC）の細粒度判定・deny-by-default・
存在秘匿を代替できない（Wiki エンジンの計画 ADR も明記）。したがって ABAC は**本システムが単一の真実源**とし、
**WikiService を Wiki.js の前段ゲートウェイ**として強制点を集約する。

- **強制内容**: 利用者 JWT 属性（`clearance` / `department`）× `/authz/scope` から許可スコープを解決し、
  Wiki.js の閲覧要求に deny-by-default で適用する。一覧は権限内ページのみ、個別アクセスは**権限外／不存在とも
  404 相当で存在秘匿**する（権限外は 404 とする方針の意味論を継承。403 で存在を漏らさない）。判定は既存 `AbacPageFilter`
  （検索側 `InMemoryVectorStore.MatchesFilters` と同一意味論）を到達可否へ転用する。
- **直接到達の遮断**: 強制点をゲートウェイに集約するため、Wiki.js への**直接到達を塞ぐ**ネットワーク分離が
  前提（mesh 導入までのネットワーク分離）。共有/stg/prod では Wiki.js を host 公開せず、到達を WikiService 経由に限定する
  （compose の `expose`、k8s の NetworkPolicy／Ingress 無効）。dev のみ開発便宜で Wiki.js を公開する。
- **Wiki.js 側の権限**: 補助的な表示制御に留め、機密性の担保には用いない。Keycloak realm import の
  `wiki-js` クライアントは `clearance`/`department`/`groups` クレームを付与するが、これは表示制御の補助であり
  ABAC の正本ではない。
- **多層防御（表示制御 `isPrivate`）**: ゲートウェイ経由 ABAC を第 1 防御・ネットワーク分離（mesh 導入までの暫定措置）を
  第 2 防御としつつ、同期時に機密区分由来の粗粒度な非公開設定を Wiki.js へも伝える（第 3 防御）。
  `confidentiality=public` **以外（属性欠落を含む）は Wiki.js 上でも非公開**（`isPrivate=true`, deny-closed。
  Wiki.js への GraphQL push 同期の実装判断）。NetworkPolicy が退行・誤設定されても public 以外の文書が Wiki.js 上で無条件公開に
  ならないための保険であり、細粒度の認可判定は引き続き本システムが単一真実源として担う。
- **秘密情報**: Wiki.js の OIDC クライアントシークレット・同期用 API キーは環境変数／Secret 経由で
  注入し、リポジトリにコミットしない。同期用 API キーは compose の `WIKIJS_API_KEY`、Helm の Secret `wikijs-sync`
  （key=`apiKey`）で投入する。realm import 内の dev 値（`wiki-js-dev-secret-change-me`）は開発専用で、
  共有/stg/prod では必ず変更する。
- **回帰防止**: `WikiEndpointsAbacTests` / `AbacPageFilterTests` が担保する受け入れ基準（一覧=権限内のみ・
  個別=404）を**新構成（認可プロキシ）で再充足**した（Wiki.js 配備の段 2 = 本 PR）。認可プロキシは ABAC 通過時のみ
  Wiki.js 本文を取得し、権限外・不存在・Wiki.js 未反映はいずれも 404 で存在秘匿する。稼働 Wiki.js を要する
  結合検証（GraphQL PoC）はフォローとして残る。

### サービス間（内部 API）の認証 — Istio STRICT mTLS を第一防御とする

内部サービス API（例: DocumentService `/documents`、LlmGateway `/complete`・`/embed`、
DataSourceService `/datasources`、AuthorizationService `/authz/scope`・`/authz/attributes/validate`）は
「サービス間呼び出しのため認証対象外」として無認証で提供されている。これは **Istio mTLSを前提**にした
設計であり、サービスメッシュの計画 ADR の確定（2026-07-06）と Issue #100 の本番実行基盤配備により mTLS が実体化した。

**方針**: サービス間認証の**第一防御は Istio STRICT mTLS**とする。

- `PeerAuthentication`（`mtls.mode: STRICT`）と `DestinationRule`（`ISTIO_MUTUAL`）を Helm で宣言し、
  ArgoCD が継続的に同期する（`deploy/helm/microservices-platform/templates/istio-mtls.yaml`）。
  STRICT により平文フォールバックが無く、サイドカー未注入クライアントからの平文到達を拒否する。
- サイドカー自動注入は Namespace ラベル `istio-injection: enabled` で行う。
- mTLS がワークロード ID を保証するため、トークン非保持ワーカーを含むサービス間呼び出しでも
  暗号化・相互認証が成立する（アプリ層の client credentials 実装は不要）。
- 回帰防止として、STRICT mTLS の宣言を `MeshMtlsTests` で機械的に担保する。

**多層防御（旧・ネットワーク分離の第一防御。defense-in-depth へ格下げして維持）**:

- Kubernetes では ClusterIP + NetworkPolicy（デフォルト拒否）を維持する
  （`deploy/helm/microservices-platform/templates/networkpolicy.yaml`）。
- `docker-compose.yml`（ローカル開発）は BFF=エッジのみ host 公開、他は `expose` を維持。
  回帰は `NetworkIsolationTests` で担保する。
- 外部からの入口は **BFF（エッジ）に一本化**し、BFF が Keycloak JWT で認証する。

**恒久像への残課題**: 全 API の OIDC/JWT 認証（内部 API でのトークン検証）は継続課題として別 Issue で追跡する
（STRICT mTLS の実装 ADR §4）。RetrievalService `/search` の ABAC 取り扱いは #55 で別管理。

## データ保護

| 区分 | 対象 | 方式 |
| --- | --- | --- |
| 保存時暗号化 | PostgreSQL（業務 DB）・MinIO（本文/資産）・Qdrant（ベクトル） | **アプリ層の暗号化は未実装（現状=なし）**。保存時暗号化はインフラ層（ストレージ/ボリューム暗号化・k8s Secret 暗号化）に委ねる方針で、実クラスタでの有効化・鍵管理は運用整備（未決事項・#198 と連動）。機微文書の機密性は ABAC（取得段 fail-closed）＋ Wiki `isPrivate` で担保する |
| 通信時暗号化（外部→BFF） | クライアント〜エッジ | TLS（リバースプロキシ/Ingress で終端）。**ローカル検証環境（経路B）も含めて平文 HTTP を残さない** —— `NFR-11` の適用範囲は環境を問わない（利用者裁定 2026-08-16・裁定依頼は計画側へ提出済み。証明書は計画 `ADR-0047` の selfsigned CA と、経路 B のエッジ TLS 終端の実装 ADR による） |
| 通信時暗号化（サービス間） | 内部サービス間 | Istio STRICT mTLS で相互認証＋暗号化。NetworkPolicy を多層防御として併用 |
| 個人情報 / 機微情報 | 文書本文・属性（機密区分）・利用者クレーム（clearance/department） | 文書の機密区分（`confidentiality`）は必須（サーバー側検証）。ABAC で区分×利用者資格を deny-by-default 評価（検索段の fail-closed な ABAC 強制）。高機密本文の外部 LLM への越境は egress ポリシーで遮断（confidential/restricted はセルフホスト固定）。個人情報の専用マスキング/匿名化は現状スコープ外（本システムは社内文書が対象。取り込み対象データの PII 取り扱いは各データソース側の責務） |

> **注（実装 ADR の参照）**: 上表が参照する**文書の機密区分のサーバー側検証は PR #211（Issue #199）で新設され
> develop へマージ済み**（本ブランチも develop を取り込み済み）。本仕様書群は他に、.NET 10 採用と
> コンポーザビリティ標準の段階適用（PR #212、未マージ）を参照する箇所があり、これは #212 マージ後に実体が揃う。

## 秘密情報管理

<!-- 鍵・トークンの保管・ローテーション・コミット禁止 -->

### 開発専用（dev-only）の平文認証情報 — 本番流用禁止

`deploy/keycloak/microservices-platform-realm.json` の realm import には、開発・E2E 検証用の dev ユーザーが
平文パスワードで含まれる（`poc-user`／`poc-operator`／`developer`、および OIDC クライアントシークレット
`wiki-js-dev-secret-change-me` / `ai-stock-trading-kb-writer-dev-secret-change-me` / `headlamp-dev-secret-change-me`）。これらは **dev 環境限定**の便宜であり、以下を守る。

- **用途**: ローカル compose / dev の初回起動から、ABAC 属性ユーザー（`poc-user`）と運用者ロール検証
  （`poc-operator`、`platform-operator` ロール保持。運用者ロールの `ConfigViewer` を再現）を、
  手動セットアップ無しで再現するためのシード。
- **`developer`（ローカル k8s dev 用）**: `platform-admin`＋`platform-operator`＋`wiki-editor` の
  全ロールと clearance=`restricted` を束ねた dev 用スーパーユーザー。1 アカウントで全機能の疎通確認を行う
  ための便宜であり、**権限分離（ロール別挙動）の検証には使わない**（それは `poc-*` の役割）。
  他の dev ユーザーと同様、共有／ステージング／本番の realm には含めない。
- **`ai-stock-trading-kb-writer`（AST#18 のクロスユニット s2s 用）**: AST ユニットが本レルムの
  DocumentService へ KB 書き込み（`POST /documents`）を行うための機密クライアント（service-account に
  `platform-operator`・client_credentials のみ）。realm import 内の `ai-stock-trading-kb-writer-dev-secret-change-me`
  は **dev 専用**で、本番シークレットは環境変数／Secret（Vault）経由で AST 環境へ注入し、realm import へは
  コミットしない。AST 側は空既定なら no-op（トークンを付けない）。
- **`headlamp`（#271・dev の k8s 管理 UI 用）**: Headlamp（[headlamp.dev](https://headlamp.dev/)）を
  Keycloak OIDC でログインさせる confidential クライアント。Headlamp backend が authorization code を server-side で
  交換するため client secret を要する。realm import 内の `headlamp-dev-secret-change-me` は **dev 専用**で、`k8s-local-up.sh`
  の `HEADLAMP=1` が Secret `headlamp-oidc`（`platform-infra`）へ dev 既定値として投入する（`HEADLAMP_OIDC_CLIENT_SECRET`
  で上書き可・manifest に平文で置かない）。Headlamp 資産は `deploy/local/`（dev 専用・opt-in・既定オフ）に閉じ、
  本番像へは同梱しない。ログインは `developer` を流用し新規資格情報を増やさず、認可は OIDC token passthrough で
  API server の RBAC が担う（Headlamp SA には広域権限を bind しない＝fail-safe）。
- **Vault dev root トークン（AST#24 の経路B opt-in）**: 可観測性/Vault オーバーレイを opt-in で立てる際、
  Vault **dev モード**の root トークンを Secret `vault-dev-token`（`platform-infra`）へ入れる。既定は dev 値 `devroot`
  （`VAULT_DEV_ROOT_TOKEN` 環境変数で上書き可）で、**manifest に平文で置かず** `k8s-local-up.sh` の `VAULT=1` が
  `apply_secret` で生成する（postgres/rabbitmq の dev secret と同位置づけ）。Vault dev はインメモリで再起動で揮発する
  **dev 専用**であり、本番の Vault 化（unseal/監査/HA/ローテーション）充足ではない（Tier 3）。
- **本番流用の禁止**: 共有／ステージング／本番の realm には **PoC ユーザーを含めない**。運用ユーザーは
  Keycloak 管理画面／IaC で個別に作成し、パスワードは realm import にコミットしない。クライアント
  シークレット（`wiki-js` / `ai-stock-trading-kb-writer`）は環境ごとに必ず変更し、環境変数／Secret 経由で注入する
  （上記「Wiki.js 前段」§秘密情報を参照）。
- **リスク受容の根拠**: dev realm は host 公開されるが、格納データは合成のテスト属性のみで機密を含まず、
  ネットワークもローカルに閉じる。平文値は「変更前提の既知シード」であり、秘密として扱わない。

### データソースのコネクタ資格情報 — DB 平文保存（Vault 移行までの暫定）

データソースのコネクタ接続設定（`apiToken` / `password` 等）は、`datasource_svc` DB の `DataSources.Config`
に**平文で保存**されている（realm の dev シードとは別系統。実運用データを含み得る）。これは Vault / External Secrets
導入までの**暫定状態**であり、現状の緩和策と残余リスク・移行条件を以下に明記する。

- **暫定状態（As-Is）**:
  - **保存**: `Config` は平文（DB per Service に閉じるが、暗号化は未適用）。
  - **緩和（実装済み）**: API 応答は秘密キーの値を**マスク**して返す（`DataSourceEndpoints.cs:23,79` の `ToResponse`。
    Wiki コネクタの実装判断 / claude-review #222）。admin/operator であっても API 応答で平文の資格情報を露出させない。
- **残余リスク**: DB 直接アクセス・バックアップ流出・DB 侵害時に平文資格情報が露出し得る。鍵ローテーション・
  アクセス監査も未整備。API 応答マスクは「アプリ層の露出」を塞ぐのみで、保存時の平文そのものは残る。
- **移行条件（To-Be）**: 実環境のシークレット設計（k8s Secret → External Secrets Operator / Vault）確定後、
  `Config` を平文値から**秘密ストア参照キー**へ移行する。保存時暗号化（データ保護表）の有効化と、鍵ローテーション・
  監査運用（`docs/operations/`）を併せて整備する。
- **一元追跡**: Vault 移行は、各コネクタの実装 ADR に分散していたフォローアップ（データソース登録・同期の未決事項）を
  **#310 に集約**して追跡する。実環境構築前の着手を推奨（go-live はブロックしない `priority:should`）。

## 監査ログ

機微な取得・管理操作を構造化ログ（`Audit=true` プロパティ付与）として記録し、可観測性基盤
（`ILogger` → OTel Logging SDK → OTLP。ログの出口を OTel Logging SDK へ移す実装判断）で
監査として抽出可能にする（`IAuditLogger`・`Shared.Infrastructure/Foundation/Audit`。構成情報 API の要求と、認可＝ABAC の計画 ADR による）。
`Audit=true` を含む構造化プロパティが `LogRecord` の属性として保たれることは
`Platform.Bff.Tests/PlatformLoggingTests.cs` が実測する（`ParseStateValues = true` による写像）。

| 対象イベント | 記録項目 | 保管期間 |
| --- | --- | --- |
| 構成情報 API アクセス（構成ビューア。`/bff/admin/config` 系） | `action`（`config.read` / `config.drift.read` / `config.history.read`）・`subject`（利用者名）・`outcome`（`granted` / `denied`）・`detail` | 可観測性基盤（OTLP 収集先）の保持設定に従う（アプリ側で固定保管期間は持たない） |
| LLM egress ルーティング判断（送信先切替・越境統制） | 構造化ログ（`sensitivity`・`purpose`（log-forging 対策でサニタイズ）・`allowedTiers`／拒否理由。`LlmRouter` / `EmbeddingRouter`） | 同上。※ 形式監査（`IAuditLogger`）ではなく越境統制の観測ログ。将来的な `IAuditLogger` 化はフォローアップ |

- 監査ログの保持期間・改ざん防止・エクスポートは可観測性基盤側の運用設定で定める（`docs/operations/operations.md` の
  監視・アラート／バックアップと連動。#198）。NFR「監査ログ保持」の具体的な保管期間は運用整備で確定する。

## 脅威と対策

| 脅威 | 影響 | 対策 |
| --- | --- | --- |
| 内部 API へのホストからの無認証到達 | 全文書メタデータ＋ABAC 属性の列挙、無認証 LLM 呼び出し | 内部サービスを host 公開しない。エッジ(BFF)で JWT 認証。回帰は `NetworkIsolationTests` で担保 |
| 同一ネットワーク内からの内部 API 無認証到達（残余リスク） | ネットワーク内の侵害があれば内部 API へ到達可能 | **Istio STRICT mTLS 配備済み**（サービスメッシュの計画 ADR は Accepted・#100）でサイドカー未注入クライアントの平文到達を拒否し相互認証。k8s NetworkPolicy を多層防御として併用。残課題は内部 API での OIDC/JWT 検証（別 Issue で追跡） |
| NetworkPolicy 退行・誤設定による Wiki.js への直接到達 | 機密文書が Wiki.js 上で無条件閲覧可能に | ABAC ゲートウェイ＋ネットワーク分離に加え、機密区分由来の `isPrivate`（public 以外は非公開）を多層防御として付与。稼働 Wiki.js での分離検証は PoC フォロー |
| 削除・非公開化された文書が Wiki.js に残存 | 撤回済み社内文書が外部システム（Wiki.js）に残り続ける | **削除・アーカイブ同期経路を実装済み**。`DocumentDeleted` 新設と `status=archived` 拡張で下流 WikiService が Wiki.js ページの撤去・非公開化・メタデータ Archived 化を伝播する。加えて `isPrivate`（public 以外は非公開）を多層防御として維持 |
| 高機密文書本文の外部埋め込み API への送信 | 取り込み時は本文全量を送るため露出が最大。confidential/restricted が外部（Voyage）へ出ると越境統制を破る | 埋め込み専用の越境ポリシー `EmbeddingEgress` で confidential/restricted を**ティアA（セルフホスト）固定**とし、外部（ティアB）を候補から除外。セルフホスト未有効なら**送信せず索引もしない（fail-closed）**。回帰は `EmbeddingEndpointTests`（外部プロバイダ未呼び出し）/ `DocumentUpdatedConsumerTests`（索引スキップ）で担保 |
| 機密区分変更時の旧コレクション残存（ABAC バイパス） | 例 public→confidential 変更後、旧 voyage コレクションに本文が残り機密扱いの文書が低区分コレクションで検索ヒット | 取り込み冒頭で全モデル別コレクションから当該文書を削除してから再索引する（`DeleteByDocumentFromAllAsync`）。回帰は `DocumentUpdatedConsumerTests` で担保 |
| Voyage AI のデータ保持・学習利用 | 送信本文が外部で保持・学習に利用される | 契約でゼロ保持（学習利用オプトアウト）を設定・確認してから本番データを流す（運用仕様書に記録）。未認定の間は Voyage 経路を無効化できる |
| 検索クエリ文の外部埋め込み API への送信 | 検索クエリの埋め込みは機密区分に依らず既定外部経路（Voyage/1024次元）へ固定される（`Purpose=Query`）。検索対象コレクション（voyage/1024）と整合させるための意図的設計だが、利用者が入力するクエリ文自体に機密情報が含まれ得る | クエリ文は本文全量ではなく利用者入力の短文に限られ、Voyage 側のゼロ保持（学習利用オプトアウト）契約が本文と同じく適用される。高機密（ruri/768）コレクションの横断検索はハイブリッド検索側の後続課題であり、その設計時にクエリ側の機密区分ルーティング要否を再評価する（下記「未決事項」）。ゼロ保持未認定の間は Voyage 経路自体を無効化して受容する |

## 未決事項

> **解消済み（2026-07-10 追従・#201）**: 以下は本節から解消した。
> - サービス間 mTLS の導入→ **STRICT mTLS 配備済み**（#100）。認可・サービスメッシュの計画 ADR も **Accepted 確定**（2026-07-06）。
> - Helm/k8s の NetworkPolicy（デフォルト拒否）追補 → **配備済み**（`templates/networkpolicy.yaml`）。
> - Wiki.js 同期の削除・アーカイブ経路 → **実装済み**。

- サービス間認証の**恒久像**（内部 API での OIDC/JWT 検証。トークン非保持ワーカー含む全呼び出し元）。
  現状は mTLS（相互認証・暗号化）＋ NetworkPolicy を第一/多層防御とし、アプリ層の JWT 検証は残課題として
  別 Issue で追跡（STRICT mTLS の実装 ADR §4）。暫定運用と非機能要件の草案との相違・フェーズ分けは
  `feedback/20260705_internal-service-auth-nfr-deviation.md` で計画側へ環流済み。
- インフラ系（postgres/rabbitmq/keycloak/qdrant/grafana 等）の公開は開発環境限定。共有・ステージング・本番では公開しない運用の明文化。
- RetrievalService `/search` の ABAC 取り扱い。
- 稼働 Wiki.js での GraphQL PoC（スキーマ整合・`isPrivate` ページのサービスアカウント本文取得可否・
  ネットワーク分離の CI/E2E 検証）。GraphQL push 同期の実装 ADR のフォロー。
- 保存時暗号化（PostgreSQL/MinIO/Qdrant）のインフラ層有効化・鍵管理（データ保護表参照。運用整備・#198 連動）。
- コネクタ資格情報の Vault / External Secrets 移行（現状は DB 平文保存＋API 応答マスクの暫定。上記「§データソースのコネクタ資格情報」参照）。**一元追跡: #310**。
- 監査ログの保管期間・改ざん防止・エクスポートの運用設定（可観測性基盤側。#198 連動）。NFR「監査ログ保持」の具体化。
- 検索クエリ側の機密区分ルーティング。現状クエリ埋め込みは既定外部
  （Voyage/1024）へ固定。高機密（ruri/768）コレクションの横断検索をハイブリッド検索側で実装する際に、クエリ文の
  機密区分に応じたセルフホスト経路への切り替え要否（クエリ文自体の越境抑止）を再評価する。
