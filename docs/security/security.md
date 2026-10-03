---
title: セキュリティ仕様書
type: security-spec
status: in-progress
created: 2026-07-02
updated: 2026-10-03
author: claude
---
<!-- trace:
ids: [FR-06, FR-04, FR-17, FR-01, FR-02, FR-03, FR-05, FR-09, FR-11, FR-13, FR-15, FR-19, FR-20, FR-22, NFR-11, NFR-18, SC-05, SC-10, SC-11, SC-17, SC-19, SC-20, SC-22, UC-07, UC-11, NFR-14, NFR-09]
adrs: [ADR-0124, ADR-0121, ADR-0086, ADR-0063, ADR-0119, ADR-0034, ADR-0054, ADR-0002, ADR-0004, ADR-0005, ADR-0011, ADR-0016, ADR-0021, ADR-0026, ADR-0036, ADR-0037, ADR-0045, ADR-0057, ADR-0082, ADR-0095, ADR-0096, ADR-0106, ADR-0109, ADR-0092, ADR-0115, ADR-0088, ADR-0114, ADR-0084, ADR-0116]
iadrs: [IADR-0495, IADR-0493, IADR-0492, IADR-0486, IADR-0483, IADR-0481, IADR-0456, IADR-0410, IADR-0417, IADR-0413, IADR-0426, IADR-0476, IADR-0475, IADR-0009, IADR-0012, IADR-0017, IADR-0020, IADR-0021, IADR-0023, IADR-0025, IADR-0026, IADR-0029, IADR-0030, IADR-0039, IADR-0041, IADR-0042, IADR-0044, IADR-0047, IADR-0048, IADR-0049, IADR-0051, IADR-0053, IADR-0054, IADR-0055, IADR-0066, IADR-0075, IADR-0077, IADR-0080, IADR-0197, IADR-0206, IADR-0216, IADR-0220, IADR-0294, IADR-0295, IADR-0301, IADR-0329, IADR-0338, IADR-0348, IADR-0352, IADR-0296, IADR-0401, IADR-0422, IADR-0428, IADR-0431, IADR-0433, IADR-0453, IADR-0454, IADR-0461, IADR-0465, IADR-0467, IADR-0473, IADR-0474, IADR-0364]
specs: [20261003_458_connector-secret-vault-reference, 20260928_issue-1683_vault-audit-to-observability, 20260928_issue-1615_content-abac-document-reads, 20260927_issue-1665_owner-read-policy-guard-and-content-abac-gate, 20260927_1472_audit-sync-action-extraction, 20260927_issue-1610_sc17-department-edits-group-membership, 20260927_issue-1616_machine-client-own-document-write, 20260927_issue-1636_addtag-admin-role-from-authz, 20260927_issue-1636_grpc-trusted-user-context-relays, 20260927_issue-1635_document-search-trusted-user-context-relay, 20260927_issue-1628_document-read-trusted-user-context-relay, 20260927_issue-1614_document-read-authn-private-note, 20260927_issue-1606_private-notes-sync-edge-authz, 20260926_issue-1575_document-page-and-fingerprint, 20260926_1520_conversion-service-auth, 20260925_1472_audit-failed-extraction, 20260915_issue-1467_sc22-audit-followups, 20260914_issue-1411_sc22-secret-injection-screen, 20260911_issue-1409_private-note-disposal-after-window, 20260911_issue-1392_departure-retention-anchor, 20260910_issue-1372_ast-s2s-clients-platform-realm, 20260902_issue-1098_obsidian-plugin-pull-stage1, 20260903_issue-1153_obsidian-plugin-push-delete-conflict-stage2, 20260903_issue-1154_private-notes-sync-edge-route, 20260909_issue-336_ndcg-harness-and-query-embedding-profile, 20260925_1499_object-storage-seaweedfs, 20260926_issue-336_multi-collection-rrf-fusion, 20260926_issue-1573_department-attribute-follows-group, 20260926_issue-1532_sync-token-rejected-after-disable, 20260927_issue-1629_admin-write-private-note-scope, 20260927_issue-1609_department-clear-and-dictionary-from-realm]
issues: [#1696, #1683, #1615, #1665, #1610, #1616, #1636, #1635, #1628, #1629, #1609, #1614, #1606, #1575, #1573, #1520, #1499, #1472, #55, #100, #1392, #1409, #1411, #1467, #198, #336, #199, #201, #211, #212, #222, #271, #310, #438, #458, #628, #629, #1098, #1101, #1153, #1154, #1372, #1532, AST#18, AST#24, AST#727, planning#383, planning#672, planning#700, AST#1078]
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
  - `ConversionService` `/jobs`（照会・再変換・図の一覧・人手補正）: 照会は admin/operator、**再変換・図の一覧・人手補正は admin 必須**
    （BFF の変換ジョブ集約と同じ境界）。**［2026-09-26］追加。** 従前はワーカーの最小 HTTP サーフェスとして認証を持たず、門は BFF だけだった。
    BFF が中継した利用者の資格情報を他の後段と同じ JwtBearer で検証する。**`platform-service` だけを持つサービス間トークンは通らない**
    （呼び出し元が BFF の中継しか無く、サービス間の面は置かない）。🔴 **門はロールで判定し、主体の種別は見ない** —— 門のロールを持つ
    realm のサービスアカウント（管理者ロールを持つ ABAC 投入用・運用者ロールを持つ AST の KB 書き込み用）は通る。
    これは BFF の門・DataSourceService の門と同じ性質であり、それらより緩くはない。
  - `DocumentService` 書き込み: **更新・メタデータ・公開・アーカイブ・削除は、人については admin 必須**。
    **作成（`POST`）だけ admin/operator** —— `ai-stock-trading` の KB 書き込みが
    BFF を経由せず直接叩いており、その service-account は `platform-operator` しか持たないためである
    （**［2026-09-27］機械クライアントの作成は計画の裁定で追認された**）。**人間に対する境界は BFF 側（`AdminOnly`）で閉じている。**
    読み取り（GET）は一般利用者の文書閲覧のためロールでは塞がない（組織文書の機密制御は取得段の ABAC が担う）。
    **［2026-09-27］読み取りの全ての口（一覧・ページ・単一取得・版履歴・特定版。east-west gRPC の読み取り面も）が認証を要する**
    （従前は一覧・単一取得・版が無認証で、個人資料の表題・所有者・共有先がメッシュ内の呼び出し元へ返っていた）。
    **個人資料は所有者と共有先の利用者にだけ返り**、それ以外（機械クライアント・管理者ロールの利用者を含む）には一覧から除き、個別は 404 とする。
    主体は BFF が中継した利用者、gRPC の要求が運ぶ利用者文脈、または機械クライアント自身のいずれかである。
    **gRPC の要求が運ぶ利用者文脈を信じるのは、許可集合の中継者（既定は BFF の client `bff` だけ。
    `DocumentRead:TrustedUserContextClients` で置き換えられる）の機械クライアントが運んだときだけ**で、
    それ以外の呼び出し元が利用者文脈を付けると拒否する（`PERMISSION_DENIED`）。サービス間トークンのロールは
    別プロジェクトのものを含む多数のサービスアカウントが持つので、ロールだけでは任意の利用者を名乗れないようにする。
    グループへの共有は認可サービスへ問い、引けなければ読めない側へ倒す。
    組織文書の内容による絞り込みを文書サービス自身が行うのは別の作業として残る（それまでの実施点は BFF）。
    **組織文書の絞り込み・ページングの口（`GET /documents/page`）は認証を要する**（ロールは問わない）。
    結果は既存の一覧の部分集合に限り、**個人資料は絞り込みの値にも呼び出し元にも依らず返さない**。
    **［2026-09-27］機械クライアントは、自分が所有者の組織文書に限り、メタデータ更新と削除を行える**（所有者の動的束縛。
    ロールは足さない。書き込みの口の下限 admin/operator はそのまま）。人の運用者は従前どおり 403。機械クライアントが作る文書の
    所有者はそのサービスアカウントである。他の主体の文書は読めれば 403・読めなければ 404、個人資料は 404。
    **所有者（`owner`）と文書スコープ（`doc_scope`）は、属性を書き換える口で主体を問わず変えられない**（人の管理者でも移管できない。
    所有者で書き込みを許す判定の前提を、判定の対象の側から崩させないため）。
    **［2026-09-27］管理の書き込み口（更新・メタデータ・公開・アーカイブ・削除）は個人資料を対象外とする**。
    主体を問わず（管理者ロールの人・管理者ロールを持つ機械クライアント・所有者本人でも）不在と同じ 404 を返し、
    応答に表題・所有者・共有先を出さず、何も書き換えない。従前は管理者ロールがあれば他人の個人資料を書き換え・公開・
    アーカイブ・削除でき、更新では所有者を自分へ書き換えることさえできた。個人資料を扱うのは所有者の経路
    （個人資料の口・同期の口・本文の投入・共有台帳）だけである。タグの反映口の「管理者なら書ける」分岐も個人資料には及ばず、
    個人資料へタグを足せるのは所有者だけである。
  - `RetrievalService` の east-west gRPC 検索面（AI 分析の文脈収集が呼ぶ）: **［2026-09-27］要求が運ぶ利用者文脈を信じるのは、
    許可集合の中継者（既定は AI 分析の client `aianalysis-service` だけ。`DocumentSearch:TrustedUserContextClients` で置き換えられる）の
    機械クライアントが運んだときだけ**で、それ以外の呼び出し元が利用者文脈を付けると拒否する（`PERMISSION_DENIED`）。
    従前はサービス間トークンのロール（`platform-service`）を持つどのサービスアカウント（別プロジェクトのものを含む）も、任意の利用者
    （管理者を含む）を名乗り、その利用者の権限（個人資料を含む）で検索結果の**本文**を読めた。クライアント識別は序数一致で照合し
    （接頭辞・大小文字の変種は別のクライアント）、`azp` が人のトークンにも付くので機械であることを併せて求める。
    利用者文脈の無い要求は従来どおり誰にも受け付けない。AI 分析の client 名を変える配備では、先に検索サービスの許可集合へ足すこと。
  - 利用者の権限で動く残りの east-west gRPC 面 —— タグの反映（グラフが AI タグ提案の承認を文書へ反映する）・近傍展開（検索がグラフの辺を引く）・
    属性値の照会（BFF が検索の対象範囲フィルタの候補を引く）: **［2026-09-27］要求が運ぶ利用者文脈を信じるのは、面ごとの許可集合の中継者
    （既定はそれぞれ `graph-service`・`retrieval-service`・`bff` だけ。`DocumentTagWrite:` / `GraphNeighbors:` / `AttributeValues:` の
    `TrustedUserContextClients` で置き換えられる）の機械クライアントが運んだときだけ**で、それ以外が利用者文脈を付けると拒否する（`PERMISSION_DENIED`）。
    従前はサービス間トークンのロールを持つどのサービスアカウントも任意の利用者を名乗れた。とくにタグの反映は要求が運ぶ realm ロールも信じたため、
    管理者を名乗ると**管理者の上書きで任意の組織文書へタグを書けた**。判定の形は検索面と同じ（序数一致・機械であることを併せて求める）。
    構成を 1 つの値で書くと起動時に止まる。
    **［2026-09-27］さらにタグの反映は要求が運ぶ realm ロールを評価に用いない。** 承認者が管理者かは、要求の利用者について
    認可サービスの名簿（「この 1 人はこのロールを持つか」の問い。実効ロール・無効化された利用者は持たない）から引き直す。
    許可集合の中継者が侵害されても任意の利用者を管理者にはできず、名乗った利用者が IdP 上で有効な管理者であることが要る。
    名簿を引けなければ要求は失敗し（書けないへ畳まない）、所有者の承認は名簿を引かずに通る。
  - 権限スコープ解決の面（認可サービス）には許可集合を**置かない**。サービス間トークンのロールを持つ主体は任意の利用者のスコープ（属性の形）を
    引けるが、属性は認可サービスが引き直すので偽の属性は通らず、残る「他人の利用者名を名乗る」は計画が受け入れたリスクである。
    スコープを使って資源を引く面はすべて許可集合の中継者に限られるので、漏れるのはスコープの記述に留まる。
  - 利用者トークンは BFF が後段へ伝播する（各 *BffEndpoints の `CreateForwardingClient`）。非権限は 403。
    否定テストは各サービスの `*AuthorizationTests` で検証。
- **認可（ABAC 本体）**: 文書アクセスの属性ベース認可は `AbacEvaluator`（deny-by-default）が担う。
- 未対応（多層防御のフォローアップ）: 文書作成時の付与属性が呼び出し者 ABAC スコープ内かの厳密検証（文書管理の BFF 集約で見送った分）。
  **［2026-09-26］`ConversionService` `/jobs` の後段認可は解消した**（上の一覧へ移した）。

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
「サービス間呼び出しのため認証対象外」として無認証で提供されている。
**［2026-09-27 追記］DocumentService の `/documents` の読み取りは認証を要するようになった**（上の「後段認可」の項。書き込みは従前から認証を要する）。
これは **Istio mTLSを前提**にした
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
  - **［2026-09-03 追記］例外は個人資料の同期プロトコル 1 前置（`/private-notes/sync/`）だけである。**
    従前の「一本化」は本追記で例外つきに置き換わる。資格情報がブラウザセッション（HttpOnly Cookie ＋
    CSRF ヘッダ）と**別系統の不透明トークン**であり、BFF に載せると「BFF は Cookie セッションだけ」という
    境界が崩れるため、エッジから文書サービスへ直接通す。エッジで JWT 検証はしない（**端点が自前で
    ハッシュ照合し、欠落・不正・期限切れ・失効をすべて 401 にする** deny-by-default）。
    外へ出るのはこの 1 前置だけで、個人資料の一覧・端末登録・上限管理・組織文書はエッジから届かない
    （画面配信へ落ちるので **404 ではなく画面**が返る。API に届いていないことは、同じ端点を直に叩くと
    401 になるのと**対で**確かめた）。
    本番像は**既定で出さない opt-in**（`edge.privateNotesSync.enabled`）で、有効時のみ NetworkPolicy が
    エッジ → 文書サービスの穴を当該ポートに限って開ける。
  - **［2026-09-27 追記］その穴を経路でも絞る。** NetworkPolicy はゲートウェイの Namespace から当該ポート**全体**を
    開けるため、経路の制限が VirtualService の振り分け 1 枚だけに頼っていた（共有のゲートウェイへ別の振り分けが
    付けば、JWT で守られた文書 API へ届き得る）。有効時は同じ条件で Istio の `AuthorizationPolicy`
    （`document-service-edge-sync-only`・**DENY**）を置き、ゲートウェイの Namespace の主体からは
    `/private-notes/sync/*` 以外を 403 で落とす。**ALLOW にしない**のは、ワークロードを選ぶ ALLOW が
    当たらない要求をすべて拒否に変え、名前空間の中の呼び出し元（BFF・グラフ・MCP）・別名前空間の連携システム・
    プローブまで巻き込むためである。mTLS が STRICT（既定）のときに穴が閉じる —— PERMISSIVE の間は、
    ゲートウェイの Namespace に居るサイドカー無しの Pod からの平文がこの門を素通りする（受け入れた限界）。

**恒久像への残課題**: 全 API の OIDC/JWT 認証（内部 API でのトークン検証）は継続課題として別 Issue で追跡する
（STRICT mTLS の実装 ADR §4）。RetrievalService `/search` の ABAC 取り扱いは #55 で別管理。

## データ保護

| 区分 | 対象 | 方式 |
| --- | --- | --- |
| 保存時暗号化 | PostgreSQL（業務 DB）・SeaweedFS（本文/資産）・Qdrant（ベクトル） | **アプリ層の暗号化は未実装（現状=なし）**。保存時暗号化はインフラ層（ストレージ/ボリューム暗号化・k8s Secret 暗号化）に委ねる方針で、実クラスタでの有効化・鍵管理は運用整備（未決事項・#198 と連動）。機微文書の機密性は ABAC（取得段 fail-closed）＋ Wiki `isPrivate` で担保する |
| 通信時暗号化（外部→BFF） | クライアント〜エッジ | TLS（リバースプロキシ/Ingress で終端）。**ローカル検証環境（経路B）も含めて平文 HTTP を残さない** —— `NFR-11` の適用範囲は環境を問わない（利用者裁定 2026-08-16・裁定依頼は計画側へ提出済み。証明書は計画 `ADR-0047` の selfsigned CA と、経路 B のエッジ TLS 終端の実装 ADR による） |
| 通信時暗号化（サービス間） | 内部サービス間 | Istio STRICT mTLS で相互認証＋暗号化。NetworkPolicy を多層防御として併用 |
| 個人情報 / 機微情報 | 文書本文・属性（機密区分）・利用者クレーム（clearance/department） | 文書の機密区分（`confidentiality`）は必須（サーバー側検証）。ABAC で区分×利用者資格を deny-by-default 評価（検索段の fail-closed な ABAC 強制）。高機密本文の外部 LLM への越境は egress ポリシーで遮断（confidential/restricted はセルフホスト固定）。個人情報の専用マスキング/匿名化は現状スコープ外（本システムは社内文書が対象。取り込み対象データの PII 取り扱いは各データソース側の責務） |

> **注（実装 ADR の参照）**: 上表が参照する**文書の機密区分のサーバー側検証は PR #211（Issue #199）で新設され
> develop へマージ済み**（本ブランチも develop を取り込み済み）。本仕様書群は他に、.NET 10 採用と
> コンポーザビリティ標準の段階適用（PR #212、未マージ）を参照する箇所があり、これは #212 マージ後に実体が揃う。

### 利用者の部門属性を部門グループへ合わせる定期処理 — 既定無効

ABAC が判定に使う利用者の部門は IdP の利用者属性 `department` だが、部門の正本は部門グループ（`/department/<コード>`）への所属である。
認可サービスの定期処理が両者の食い違いを検知し、**属性をグループへ合わせて直す**（グループは変えない）。

| 項目 | 決めごと |
| --- | --- |
| 有効化 | 構成 `DepartmentAttributeSync:Mode`（`Off` 既定 / `Report` / `Fix`）。🔴 **既定は `Off` で、IdP へ問い合わせもしない。値域外の宣言は起動時に落ちる** |
| 書く主体と権限 | 利用者アカウント管理と同じ機密クライアント（`view-users` / `manage-users`）。**主体・ロールは増やさない** |
| 書く範囲 | 部門グループに**ちょうど 1 つ**属する利用者の属性 `department`（グループのコードへ直す）と、［2026-09-27］部門グループに**1 つも属さない**利用者の属性 `department`（消す）の 1 キーだけ。他の属性・ロール・グループ・クライアント・secret には触れない |
| 管理画面との競合 | 書く直前に読み直し、有効状態か部門以外の属性が変わっていれば見送る。**読み直しから書き込みまでの 1 往復の窓は残る**（Keycloak に条件付き更新が無い）。窓の中の無効化は上書きされ得る |
| 書かない相手 | 部門グループが 2 個以上の利用者（未解決。消しもしない）。サービスアカウント（部門グループに属さなくても消さない）。［2026-09-27］🔴 **全利用者の列挙を最後まで読めなかった周期は、部門グループ 0 個の利用者も消さない**（読めなかった人を「属さない」と推定しない。消す向きの誤りはその人から部門の資料を奪う）。消す直前にその人の所属を読み直し、部門グループが見つかれば消さない |
| ログ | 件数と、食い違い・未解決の利用者の IdP 内部 ID と値（制御文字を落とす）。利用者名は出さない |
| 失敗の検知 | 利用者の書き込みの失敗・周期ごとの中断・全員の見送り・全利用者の列挙の未完了を計器に数え、アラート（warning）で知らせる。1 人の失敗で周期は止まらない |
| 管理画面の部門欄 | ［2026-09-27］利用者アカウント管理画面（システム管理者限定）の部門欄は**部門グループの所属を変える**（同じ機密クライアントの `manage-users` の範囲。主体・ロールは増やさない）。**属性 `department` は画面から書かない**（属性の差し替えは部門を拒み、現在の部門を持ち越す）ので、属性を変えるのはこの定期処理だけである。定期処理はグループの所属を変えない。所属の変更は先に入れてから外し、途中の失敗は元に戻す（部門グループ 0 個にしない）。所属の変更は認可基盤の管理イベントに残る |

### 所有者の読み取りのポリシーの消失の検知と、内容の ABAC の門 — 未確認なら開かない（fail-closed）

所有者が自分の文書を読めるのは、所有者の読み取りのポリシー（有効・`read`・利用者の条件なし・文書の条件は `owner` が利用者自身に一致することだけ）が
在るからである。評価器は組み込みの「所有者は読める」を持たない。このポリシーは管理者が自由に削除・無効化できる。

| 項目 | 決めごと |
| --- | --- |
| 在ることの判定 | 上の形に**完全に**合う有効なポリシーだけを数える。形が近いが違うもの（利用者の条件あり・文書の条件に別のキー・キーの大文字小文字違い・値に他の利用者やグループの束縛が混ざる・値が空）は数えない（数え違いは門を誤って開き、警報を黙らせる） |
| 消失の検知と通知 | 認可サービスが起動時と 1 分ごとに数え、計器と警報（critical）で知らせる。数えられないときは計器の系列を止め、「見ていない」として別の警報（warning）で知らせる（0 を出して「無い」と偽らない。古い値を出して「在る」と偽らない） |
| 削除の扱い | 🔴 **止めない。** 管理画面・API の削除と無効化の口は変えない（ポリシーの誤りを直す操作を塞がない）。消したことは知らせる |
| 内容の ABAC の門 | 文書サービスの構成 `ContentAbac:Mode`（`Off` 既定 / `On`。値域外は起動時に落ちる）。`On` でも、認可サービスでこのポリシーが 1 件以上あると確かめるまで開かない。数えられない（宛先の未構成・通信の失敗・時間切れ・サービス間の資格情報の失敗）も開かない。閉じている理由はログと計器に残す |
| 開いた後に消えたとき | 門は閉じない（1 度開いたら実行の間は開いたまま）。閉じ直すと内容の ABAC が外れ、機械の主体に対する読み取りの許可が**広がる**向きに倒れるためである。消えたことは上の警報が知らせ、所有者は自分の文書を読めなくなる（受け入れたトレードオフ） |
| 残るもの | 誰が消したかをポリシーの API は記録しない。消えてから警報が鳴るまで最大およそ 6 分。門は古い写しの削除を確かめない（有効化の前に運用で済ませる） |
| 門が開いたとき | 文書サービスの読み取りの全ての口（REST・gRPC）が、認可サービスの読み取りの許可だけで判定する（所有者・共有先も含む。判定器をひとつにする）。機械の主体は個人資料を読まない。主体の名前が分からない・認可サービスが答えないときは何も返さない（fail-closed）。門は要求の中で最初に読んだ状態に固定する。閉じている間は従前の判定のまま |

### 退職時の個人資料の保持起点 — 未供給なら数えない（fail-safe）

退職・アカウント無効化のあと、個人資料に対する管理者閲覧の窓は**起点から 30 日間**である。
🔴 **その起点となる日付を、実装は自分で持つ必要がある** —— 退職日を持つのは人事システムだけであり、
人事連携は未実装で、IdP の無効化フラグ（`enabled`）に日付は無い。

| 項目 | 決めごと |
| --- | --- |
| 起点の置き場 | **IdP の利用者属性（予約キー）**。`account_disabled_at`（暫定）/ `hr_leave_date`（恒久・人事連携。**書き手は未実装**） |
| 出所の切替え | 構成 `RetentionAnchor:Source`（`account-disabled-at` 既定 / `hr-leave-date`）。**値域外の宣言は起動時に落ちる** |
| 期間 | **30 日。構成にしない**（計画の決定であり、配備が弱められてはならない） |
| 書き手 | 利用者アカウント管理の**無効化端点だけ**（既にある起点は上書きしない／再有効化で消す） |
| 起点が無い・読めないとき | 🔴 **「数えていない」を返し、削除の対象にしない。**「該当 0 件」と「起点が未供給」を型で分ける（`"0"` はエポックではない） |
| 予約キーの扱い | 利用者アカウント管理の応答へ**出さない**（画面が属性辞書に無いキーを送り返して 400 になるため）。ABAC 属性の差し替えでも**消えない** |

### 窓が閉じた後 — 定期処理が完全削除し、曖昧なものは消さない

**窓が閉じた（起点から 30 日が過ぎた）個人資料は完全削除する。**削除は日次の定期処理が行い、
**管理者の明示操作による削除経路は設けない**（押されないまま残る形にしないため）。

| 項目 | 決めごと |
| --- | --- |
| 削除の条件 | **所有者が無効化済み** ∧ **起点から 30 日が経過**。**この 1 通りだけ**が削除される |
| 🔴 削除しないもの | 窓の中・**起点が無い／読めない**・所有者が有効・名簿に居ない・**名簿を引けなかった**。曖昧さは常に「消さない」へ倒す（残余を置かないため**誤削除は取り返せない**） |
| 削除の射程 | DB 上の記録・本文の実体（オブジェクトストレージ）・索引（ベクトルストアのチャンク）。**新しい削除の意味論を作らない** |
| 判定の場所 | **認可サービスが解き、線に載せるのは 3 値の答えだけ**。期間・書式・構成キーはサービス間の面に出さない |
| 通知 | 🔴 **送らない。**完全削除の通知は宛先が「所有者本人のみ」と定まっており、本経路の所有者は無効化済みである。**届かない通知を送る設計にしない** |
| 監査 | 🔴 **「いつ・誰の資料を・何件」だけ。**資料のタイトル・本文は残さない —— 残余を置かないという決定を、ログ経由で破らない |
| 失敗したとき | 行を残して次周期で再入する。**「消したことにして実体を残す」形にしない** |
| 口が構成されていない配備 | **1 件も削除しない**（口の不在を「窓が閉じた」へ倒さない） |

**窓の間、管理者の権能は閲覧に限る**（持ち出し・移管の経路は無い）。
**本人の側の持ち出しの経路（同期トークン）も、無効化の後の最初の同期要求から閉じる。**
［2026-09-26 更新］従前ここは「同期トークンが無効化で失効するかは未解決」と書いていた。実測すると
失効しておらず、本人は管理者が閲覧できるのとちょうど同じ 30 日、資料を同期し続けられた。いまは次のとおりである。

| 項目 | 決めごと |
| --- | --- |
| 方式 | トークンは失効させない。文書サービスが**同期要求ごとに**利用者名簿で所有者が有効かを確かめ、有効と確かめられたときだけ通す（結果を持ち越さない） |
| 🔴 判定できないとき | 名簿を読めない・応答が 5 秒を超える・名簿に居ない・名簿の口が構成されていない —— **いずれも通さない**（同じ 401）。名簿の障害の間は有効な利用者の同期も止まる。**持ち出しの経路を障害で開かない**ことを同期の可用性より優先する |
| 依存の向き | ナレッジ機能 → 基盤（名簿の狭い読み口）だけ。無効化の端点は同期トークンを知らない（基盤から可変機能への依存を作らない） |
| 再有効化 | 期限内で本人が失効させていないトークンは再び通る（無効化は端末の記録を書き換えない） |
| 残るもの | 管理者が本人の端末を失効させる経路は無い（拒否で塞いでおり、失効の記録は残らない） |

実 IdP への書き込みと本経路の発火（退職者削除・同期の拒否とも）は稼働クラスタで未実測である。

## 秘密情報管理

<!-- 鍵・トークンの保管・ローテーション・コミット禁止 -->

### Obsidian プラグインの同期トークン — 端末ローカル保管・設定ファイルに置かない

個人資料の同期トークン（有効期限 30 日・手動再発行のみ・端末ごとに発行し個別／一括で失効できる資格情報）は、
自作 Obsidian プラグイン（`src/obsidian-plugin/`）が **Obsidian の Vault 固有 localStorage（端末ローカル）** に
保管する。**プラグイン設定ファイル `data.json` には置かない** —— `data.json` は Vault の一部で Obsidian Sync や
git により**他の端末へ複製される**ため、端末単位の失効と矛盾する。保存後は画面へ戻さず（再表示不可）、
削除の操作を持つ。暗号化はしていない（OS のキーチェーンはプラグイン API から届かない）。露出は当該端末の
プロファイル内に閉じ、端末の紛失・盗難は計画の egress ポリシーが「受け入れたリスク」と明記する範囲である。
Bearer で平文のまま載るため、接続先は https に限る（loopback のみ http 可）。
`data.json` に持つのは接続先・同期フォルダ・同期状態・**未送信の編集列（本文つき）**であり、資格情報は含まない
（未送信の本文は同じ Vault の中にある資料の写しで、複製先を増やすものではない）。

### 開発専用（dev-only）の平文認証情報 — 本番流用禁止

`deploy/keycloak/microservices-platform-realm.json` の realm import には、開発・E2E 検証用の dev ユーザーが
平文パスワードで含まれる（`poc-user`／`poc-operator`／`developer`、および OIDC クライアントシークレット
`wiki-js-dev-secret-change-me` / `ai-stock-trading-kb-writer-dev-secret-change-me` / `ai-stock-trading-kb-reader-dev-secret-change-me` / `headlamp-dev-secret-change-me` /
`abac-seeder-dev-secret-change-me` / `identity-admin-dev-secret-change-me`）。これらは **dev 環境限定**の便宜であり、以下を守る。

> **🔴 ［2026-08-28 / #438］パスワードだけではログインできない。** 計画が確定した「TOTP による多要素認証を必須」を
> realm で実効化したため、この 3 名は初回ログインで `CONFIGURE_TOTP` を求められ、以後は毎回 6 桁を要求される。
> 併せて **realm の全 client で直接付与（password grant）を無効にした** —— 開けたままだと
> パスワードだけでトークンが出て、MFA を迂回できるからである。
> その結果 dev の投入器（`seed-abac-policies.js` / `seed-search-documents.js`）は**人の資格情報を借りるのをやめ**、
> サービスアカウント `abac-seeder` の client_credentials で名乗るようになった。

- **用途**: ローカル compose / dev の初回起動から、ABAC 属性ユーザー（`poc-user`）と運用者ロール検証
  （`poc-operator`、`platform-operator` ロール保持。運用者ロールの `ConfigViewer` を再現）を、
  手動セットアップ無しで再現するためのシード。
- **`developer`（ローカル k8s dev 用）**: `platform-admin`＋`platform-operator`＋`wiki-editor` の
  全ロールと clearance=`restricted` を束ねた dev 用スーパーユーザー。1 アカウントで全機能の疎通確認を行う
  ための便宜であり、**権限分離（ロール別挙動）の検証には使わない**（それは `poc-*` の役割）。
  他の dev ユーザーと同様、共有／ステージング／本番の realm には含めない。
- **`ai-stock-trading-kb-writer`（ai-stock-trading からのクロスユニット s2s 用）**: AST ユニットが本レルムの
  DocumentService へ KB 書き込み（`POST /documents`）を行うための機密クライアント（service-account に
  `platform-operator`・client_credentials のみ）。realm import 内の `ai-stock-trading-kb-writer-dev-secret-change-me`
  は **dev 専用**で、本番シークレットは環境変数／Secret（Vault）経由で AST 環境へ注入し、realm import へは
  コミットしない。AST 側は空既定なら no-op（トークンを付けない）。
- **`ai-stock-trading-kb-reader`（ai-stock-trading からのクロスユニット s2s 用・読み取り専用）**: AST ユニットの取引判断が
  本レルムの RetrievalService で KB を検索する（`POST /search`）ための機密クライアント。**書き手（上の `kb-writer`）とは別の主体**で、
  service-account には**ロールを 1 つも与えない**（文書の作成・更新の口はロールで閉じているので書けない）。既定スコープは `profile` だけで、
  トークンに `preferred_username`（`service-account-ai-stock-trading-kb-reader`）が載り、認可サービスはその名前で属性を引き直す。
  service-account の属性は `projects = ai-stock-trading` だけで、`clearance` は**与えない**（与えると基盤全体の `internal` が読める）。
  ABAC の読み取りポリシー 1 本（`projects ∋ ai-stock-trading` の主体に `project = ai-stock-trading` の文書だけを許す）が
  読める範囲を決める（[運用手順](../operations/operations.md) の「AST の KB の読み手のポリシーの投入」）。realm import 内の
  `ai-stock-trading-kb-reader-dev-secret-change-me` は **dev 専用**で、本番シークレットは Vault 経由で AST 環境へ注入する。
- **`ai-stock-trading-svc`／`ai-stock-trading-owner`（ai-stock-trading のユニット内 s2s と Discord Bot 制御の owner 認証）**:
  基盤連結の k8s では AST サービスが**本レルム**で JWT を検証する（統合 SPA の身元は本レルムでしか成立しないため。
  AST 側の `values-local.yaml` が `global.authAuthority` を本レルムへ向ける）。その配備で AST の s2s
  （サービス間の同期照会・run-once・Discord Bot 制御）も本レルムで発行されるので、AST レルムと同名の
  機密クライアント 2 つと realm ロール `trading-service`（読み取り専用 s2s）をここへ写す。いずれも
  client_credentials のみ（standard flow と直接付与は無効）で、service-account に与えるのは
  `trading-service`（svc）／`trading-owner`（owner）の 1 つずつ。realm import 内の dev secret は AST レルムの
  dev export と**同値**（稼働中の `ast-secrets` を変えずに移すため）で **dev 専用**。本番シークレットは
  Vault 経由で AST 環境へ注入し、realm import へはコミットしない。
- **`identity-admin`（利用者アカウント管理の反映先）**: 管理画面の「ロール割当・ABAC 属性割当・
  無効化」を認可基盤の管理 API へ反映するための機密クライアント（client_credentials のみ・
  standard flow と直接付与は無効）。**service-account へ与えるのはレルム管理の 3 つだけ**
  （利用者の参照・利用者の管理・レルムの参照）で、**レルムロールは 1 つも与えない**。
  レルムの管理・クライアントの作成・なりすましは与えない —— 与えていないことは稼働クラスタで
  **403 になることを陰性対照として実測**してある。取り込み経路の投入器（`abac-seeder`）とは
  **別のクライアント**にする（同じ資格情報を共用すると、投入の資格情報が漏れた時点で利用者の
  権限まで書き換えられる）。realm import 内の `identity-admin-dev-secret-change-me` は **dev 専用**で、
  本番は Vault → ExternalSecret → Secret 経由で注入する（`bff-oidc` と同型）。
  🔴 **この資格情報が無いと認可サービスは起動しない**（非 optional な参照）——
  注入漏れが「偽の身元プロバイダで起動し、変更が実は届いていない」へ倒れないようにするためである。
- **`headlamp`（#271・dev の k8s 管理 UI 用）**: Headlamp（[headlamp.dev](https://headlamp.dev/)）を
  Keycloak OIDC でログインさせる confidential クライアント。Headlamp backend が authorization code を server-side で
  交換するため client secret を要する。realm import 内の `headlamp-dev-secret-change-me` は **dev 専用**で、`k8s-local-up.sh`
  の `HEADLAMP=1` が Secret `headlamp-oidc`（`platform-infra`）へ dev 既定値として投入する（`HEADLAMP_OIDC_CLIENT_SECRET`
  で上書き可・manifest に平文で置かない）。Headlamp 資産は `deploy/local/`（dev 専用・opt-in・既定オフ）に閉じ、
  本番像へは同梱しない。ログインは `developer` を流用し新規資格情報を増やさず、認可は OIDC token passthrough で
  API server の RBAC が担う（Headlamp SA には広域権限を bind しない＝fail-safe）。
- **Vault dev root トークン（経路B の opt-in）**: 可観測性/Vault オーバーレイを opt-in で立てる際、
  Vault **dev モード**の root トークンを Secret `vault-dev-token`（`platform-infra`）へ入れる。既定は dev 値 `devroot`
  （`VAULT_DEV_ROOT_TOKEN` 環境変数で上書き可）で、**manifest に平文で置かず** `k8s-local-up.sh` の `VAULT=1` が
  `apply_secret` で生成する（postgres/rabbitmq の dev secret と同位置づけ）。経路B の Vault は既定で file ストレージを
  PVC に置き、Pod 内ラッパーが unseal 鍵を PVC 上の平文ファイルから読んで自動 unseal する（`PERSIST=0` ならインメモリ）。
  root トークンが既知の dev 値である以上、鍵をローカルディスクに置いても守りの水準は変わらない。いずれも
  **dev 専用**であり、本番の Vault 化（unseal/監査/HA/ローテーション）充足ではない（Tier 3）。
- **本番流用の禁止**: 共有／ステージング／本番の realm には **PoC ユーザーを含めない**。運用ユーザーは
  Keycloak 管理画面／IaC で個別に作成し、パスワードは realm import にコミットしない。クライアント
  シークレット（`wiki-js` / `ai-stock-trading-kb-writer` / `ai-stock-trading-kb-reader`）は環境ごとに必ず変更し、環境変数／Secret 経由で注入する
  （上記「Wiki.js 前段」§秘密情報を参照）。
- **リスク受容の根拠**: dev realm は host 公開されるが、格納データは合成のテスト属性のみで機密を含まず、
  ネットワークもローカルに閉じる。平文値は「変更前提の既知シード」であり、秘密として扱わない。

### データソースのコネクタ資格情報 — DB 平文保存（Vault 移行までの暫定）

データソースのコネクタ接続設定（`apiToken` / `password` 等）は、`datasource_svc` DB の `DataSources.Config`
に**平文で保存**されている（realm の dev シードとは別系統。実運用データを含み得る）。これは Vault / External Secrets
導入までの**暫定状態**であり、現状の緩和策と残余リスク・移行条件を以下に明記する。

- **暫定状態（As-Is）**:
  - **保存**: `Config` と `ConnectionUri` は平文（DB per Service に閉じるが、暗号化は未適用）。
  - **緩和（実装済み）**: **平文が外へ出る経路をすべてマスク経由にしている**（#458）。
    秘密とみなすキーの集合は 1 箇所に持ち（`SecretMask.KeyMarkers`）、応答の投影
    （`DataSourceEndpoints` の `ToResponse`）・同期エラーの保存・手動同期 API の応答・
    例外ログがそこを共有する。**行番号では引かない**——実体は移動するが節とメンバ名は残る。
    - 応答の `config` は秘密キーの値を伏せる（Wiki コネクタの実装判断 / claude-review #222）。
    - 応答の `connectionUri` は資格情報つき URI・接続文字列の秘密を伏せる。
      **書き込み時は資格情報つきの `connectionUri` を 400 で拒否する**（`ConnectionUriPolicy`）。
    - 例外は**オブジェクトのまま**ログへ渡さない（`Exception.ToString()` が内部例外のメッセージごと
      ログレコードへ入るため。共通ログ基盤にスクラビングは無い）。
    - admin/operator であっても API 応答で平文の資格情報を露出させない。
  - **読む経路（実装済み・2026-10-03）**: コネクタ（Wiki / SaaS / 業務DB）は資格情報を `Config` から直接読まない。
    同期の開始時に資格情報の解決器（`IConnectorSecretResolver`）で 1 回だけ解決し、探索と全取得へ同じ値を渡す。
    解決器は配備の構成で決まる。Vault の所在（helm の `services.datasource.vault.address`）が**空**（既定・compose）なら移送期間用で、
    平文はそのまま通し、`vault:<path>#<key>` の参照は**解決できないものとして外部へ要求を出さずに同期を失敗させる**。
    所在が**在る**なら Vault の解決器で、平文は同じく移送期間として通し、参照は Vault の KV v2 から読む（名乗りは Pod の
    ServiceAccount `datasource-service`、Vault のロールは `datasource-connector-reader`）。🔴 **Vault が読めないとき
    （不達・権限なし・パスや版が無い・削除済みの版）も平文へは倒さず、同期を失敗させる。**
    失敗の記録（ログ・直近エラー・手動同期 API の応答）に出るのは資格情報の項目の番号と理由の符号だけで、項目のキー名も出さない。
    Vault の解決器のログも HTTP の状態コードと例外の型名だけで、値・参照のパス・キー名・例外文を出さない。
  - **Vault の権限（実装済み・2026-10-03）**: 参照のパスは専用接頭辞 `datasource/` に限る（接頭辞の外・`..` を含む参照は Vault へ送らずに止める）。
    datasource-service のロールの policy（`deploy/local/vault/eso/policy-datasource-connector-read.hcl`）は `secret/data/datasource/*` の
    `read` だけで、書き込み・一覧・削除を持たない。🔴 **ESO の policy（`secret/data/msp/*` ほか）はこの接頭辞を読めず、
    datasource-service は `msp/*` を読めない**（コネクタの資格情報を k8s Secret へ材料化させない）。一致は DataSourceService の試験が policy の字面で固定する。
    参照を書き込む面はまだ無い（投入の面の型は計画の裁定待ち）。
- **残余リスク**: DB 直接アクセス・バックアップ流出・DB 侵害時に平文資格情報が露出し得る。鍵ローテーション・
  アクセス監査も未整備。**マスクは「アプリ層の露出」を塞ぐのみで、保存時の平文そのものは残る。**
- **移行条件（To-Be）**: 実環境のシークレット設計（k8s Secret → External Secrets Operator / Vault）確定後、
  `Config` を平文値から**秘密ストア参照キー**へ移行する。保存時暗号化（データ保護表）の有効化と、鍵ローテーション・
  監査運用（`docs/operations/`）を併せて整備する。
- **一元追跡**: 従前ここは **#310 に集約**すると書いていたが、**#310 は 2026-08-02 に `duplicate` で
  close された**（取り込んだのは #447、横断は **#458**）。**現在の追跡先は #458 である**
  （`blocked`。Vault 集中管理は実クラスタを要するため go-live 条件に載る）。
  実環境構築前の着手を推奨（go-live はブロックしない `priority:should`）。

## 監査ログ

機微な取得・管理操作を構造化ログ（`Audit=true` プロパティ付与）として記録し、可観測性基盤
（`ILogger` → OTel Logging SDK → OTLP。ログの出口を OTel Logging SDK へ移す実装判断）で
監査として抽出可能にする（`IAuditLogger`・`Shared.Infrastructure/Foundation/Audit`。構成情報 API の要求と、認可＝ABAC の計画 ADR による）。
`Audit=true` を含む構造化プロパティが `LogRecord` の属性として保たれることは
`Platform.Bff.Tests/PlatformLoggingTests.cs` が実測する（`ParseStateValues = true` による写像）。

| 対象イベント | 記録項目 | 保管期間 |
| --- | --- | --- |
| 構成情報 API アクセス（構成ビューア。`/bff/admin/config` 系） | `action`（`config.read` / `config.drift.read` / `config.history.read`）・`subject`（利用者名）・`outcome`（`granted` / `denied`）・`detail` | 可観測性基盤（OTLP 収集先）の保持設定に従う（アプリ側で固定保管期間は持たない） |
| 秘密情報の一覧・投入（秘密情報・接続設定の管理。`/bff/secrets` 系） | `action`（`secret.item.list` / `secret.item.update` / `secret.item.sync`〔書き込みが成立した後の、同期先の ExternalSecret への即時同期の依頼。**書き込みの行とは別の行**〕）・`subject`（利用者名）・`outcome`（`granted` / `denied` / `failed`〔一覧・投入では保管先が未構成・不達・拒否・項目の現在の版が削除済み。同期の依頼では同期が未構成・依頼が通らない〕）・`detail`（項目名・プロパティ名・書き込み後の版・更新の理由・同期先の名前空間と名前、または拒否・失敗の理由）。🔴 **値・値の長さ・値のハッシュは記録しない**（テストが値の不在を監査・ログの両方で固定する） | 同上 |
| 保管先（Vault）への秘密の書き込み（画面経由・画面以外の両方） | Vault の audit（1 要求につき request と response の 2 行の JSON）。抽出するのは response の行で、残るのは `time`（いつ）・`auth.display_name` と `auth.metadata`（誰が。経路の見分けに使う）・`request.path`（どの項目）・`request.data.data` の**キー**（どのプロパティ）・`error`（拒否・失敗）。🔴 **値は記録しない** —— 値・トークン・accessor は HMAC（`hmac-sha256:…`）で置き換わる（下の「保管先の audit」） | Loki の保持設定に従う（`deploy/local/observability/loki.yaml`。削除の設定は無く、容量は Loki の PVC で縛られる）。**同じ内容の完全な写しが Vault のコンテナログ（標準出力）にもある** |
| LLM egress ルーティング判断（送信先切替・越境統制） | 構造化ログ（`sensitivity`・`purpose`（log-forging 対策でサニタイズ）・`allowedTiers`／拒否理由。`LlmRouter` / `EmbeddingRouter`） | 同上。※ 形式監査（`IAuditLogger`）ではなく越境統制の観測ログ。将来的な `IAuditLogger` 化はフォローアップ |

- **`outcome` の値域は 2 値ではない。** `granted` / `denied` に加え、秘密情報の投入の `failed`、同期競合の `recorded`、
  通知の送信上限の `reached` などがある（値域は呼び出し側が決め、記録側では閉じない）。**監査を抽出するときは `Audit=true`
  で絞り、`outcome` を `granted` / `denied` の 2 値で列挙しない** —— 列挙すると `failed` の記録が抽出から黙って落ちる。
  2026-09-27 時点で、本リポジトリには監査を抽出するクエリ・ルール・ダッシュボードは無く、収集器のログ経路
  （`memory_limiter` と `batch` だけ）も値で落とさないため、`failed` は他の値と同じ経路で可観測性基盤へ届く。
  抽出クエリを新設するときは本項に従うこと。
- **秘密情報の投入の抽出は `action` を 2 つで列挙しない。** 書き込みが成立しても同期の依頼が通らなければ、`secret.item.update` は
  `granted` のまま、`secret.item.sync` の `failed` が**別の行**に残る（書き込みの成否と同期の成否を分けるため）。
  `secret.item.list` / `secret.item.update` だけで絞ると「書けたのに Pod へ届かない」記録が抽出から黙って落ちる。
  接頭辞 `secret.item.` で絞るか、上の表の 3 つをすべて列挙する（表とコードの一致は `Platform.Bff.Tests` が固定する）。
- ［2026-09-28 追記］上の「抽出するクエリは無い」は、アプリの監査（`Audit=true`）については今も同じである。**保管先（Vault）の audit にだけ、
  抽出の条件を下の「保管先の audit」に置いた**（アプリの監査とは別の記録であり、`Audit=true` を持たない）。

### 保管先（Vault）の audit

秘密情報の書き込みは、**画面を経由したものも画面以外（コンソールからの直接投入・一括投入）のものも**、保管先の audit に残る。
画面の監査（上の表の `/bff/secrets` 系の行）は画面を経由した操作しか残せないので、画面以外の書き込みを辿れるのはこちらだけである。

**配備の宣言**（経路B の Vault。永続化した既定の構成）:

| 項目 | 宣言 | 理由 |
| --- | --- | --- |
| audit device | `stdout/`（file。コンテナの標準出力）と `otel-collector/`（socket・tcp。collector の `tcplog/vault-audit` → Loki）の **2 つ** | Vault は有効な audit device の**少なくとも 1 つ**に書けなければ要求を拒む。socket 1 つだけだと、collector や Loki が止まった瞬間に Vault が止まる（画面の書き込みも同期も止まる）。止まらない方（標準出力）を並べる |
| 起動時の扱い | 標準出力の device を有効にできなければ Vault を起動しない。socket の device は起動を止めずに裏で再試行する | audit の無い Vault を動かさない。collector より先に Vault が上がっても立つ |
| 値の扱い | 両方に `log_raw=false`・`hmac_accessor=true` を明示する | 値・トークン・accessor を HMAC で置き換える（Vault の既定と同じ値を**書いて**固定する）。🔴 **`log_raw=true` と、mount の `audit_non_hmac_request_keys` / `audit_non_hmac_response_keys` を置かない**（平文が残る）。`deploy/`・`scripts/` に無いことは `scripts/scripts.repo.test.js` が固定する |
| socket の待ち | `write_timeout=2s` | collector が受け取らずに詰まったとき、要求 1 本が待つ上限 |

宣言の実体は `deploy/local/vault-persistence/vault-entrypoint.sh`（起動器）と、collector の 2 つの設定
（`deploy/local/infra/otel-collector.yaml`〔既定。外へ出さない〕・`deploy/local/observability/otel-collector-forward.yaml`〔Loki へ出す〕）である。
**`PERSIST=0`（インメモリの `-dev`）の Vault は audit を持たない**（起動器を通らない使い捨ての構成）。本番の Vault は未配備である。

**抽出の条件**（Grafana の Explore で Loki を選んで投げる。可観測性の転送を opt-in した構成だけで引ける）:

```logql
{job="vault-audit"} | json
  | type="response"
  | request_operation=~"create|update|patch|delete"
  | request_path=~"secret/(data|metadata|delete|undelete|destroy)/.+|sys/(audit|config/auditing|policy|policies/acl|mounts)/.+"
```

- **監査を弱める操作も同じ条件で出る。** `sys/audit/…`（audit device の有効化・変更・無効化）、`sys/config/auditing/…`（HMAC しない要求ヘッダの指定）、
  `sys/policy/…`・`sys/policies/acl/…`（権限の変更）、`sys/mounts/…`（mount の変更。`…/tune` で HMAC しないキーを指定できる）である。
  秘密の書き込みを残しても、これらを残さなければ「記録を止めてから書く」を辿れない。`sys/audit-hash/…`（ハッシュの計算）は書き込みではないので出ない。
  🔴 **ただし、Loki へ送る側の device（`otel-collector/`）を外す操作そのものは Loki に届かない**（外された device は自分の無効化の行を受け取らない。実測）。
  その行は Vault のコンテナログ（`stdout/`）にだけ残り、Loki では**その時刻から行が途絶える**ことが手がかりになる。
- **経路の見分け**: `auth_metadata_role="bff-secret-writer"` の行が**画面（境界層の BFF）**、それ以外が**画面以外**である。
  画面以外の主体は `auth_display_name` で分かる（`token-local-dev-root` は共有の root トークン、`oidc-<利用者>` は人のログイン）。
  ロール名は `deploy/local/vault/eso/bootstrap.sh` が作るものと同じで、一致は `scripts/scripts.repo.test.js` が固定する。
- 🔴 **共有の root トークンで書いた行は「画面以外で書かれた」までしか言えない。** 誰が（人）・なぜ画面を使わなかったかは残らないので、
  退避の Runbook の記録（手順 5）は引き続き要る。
- **`error` で絞らない。** 拒否（`permission denied`）・失敗の行も同じ条件で出る。上のアプリの監査で `outcome` を 2 値で列挙しないのと同じ理由である。
- `request_operation` を `update` と `patch` だけにしない。KV の作成・全置換は `create` / `update`、部分更新は `patch`、削除は `delete`（`secret/data/`・`secret/metadata/`）
  と `update`（`secret/delete/`・`secret/undelete/`・`secret/destroy/`）で来る。
- **読み取りは抽出に出ない**（同期の読み取りは Loki には入っているが、この条件で落ちる）。collector の `logs/vault-audit` に値で落とす段は無い。
- Loki に届かなかった間（collector や Loki の停止）の行は、Vault のコンテナログ（標準出力）に残る。コンテナログは kubelet が回すので、
  **止まっていた時間の行を後から Loki へ入れ直す手段は無い**（残余。起動器は socket の device を有効にできないと WARN を出す）。

- 監査ログの保持期間・改ざん防止・エクスポートは可観測性基盤側の運用設定で定める（`docs/operations/operations.md` の
  監視・アラート／バックアップと連動。#198）。NFR「監査ログ保持」の具体的な保管期間は運用整備で確定する。

## 脅威と対策

| 脅威 | 影響 | 対策 |
| --- | --- | --- |
| 内部 API へのホストからの無認証到達 | 全文書メタデータ＋ABAC 属性の列挙、無認証 LLM 呼び出し | 内部サービスを host 公開しない。エッジ(BFF)で JWT 認証。回帰は `NetworkIsolationTests` で担保。**［2026-09-03 追記］唯一の例外は個人資料の同期プロトコル 1 前置**で、公開するのはサービスではなく端点群である（端点が同期トークンを自前検証し、所有者の個人資料に構造的に閉じる）。露出範囲は末尾スラッシュ込みの前置 1 本に閉じ、静的検査（`k8s-local-up.test.js`）と、実測の**対**（エッジ経由は画面配信へ落ちる／同じ端点を直に叩くと 401）で固定した |
| 同一ネットワーク内からの内部 API 無認証到達（残余リスク） | ネットワーク内の侵害があれば内部 API へ到達可能 | **Istio STRICT mTLS 配備済み**（サービスメッシュの計画 ADR は Accepted・#100）でサイドカー未注入クライアントの平文到達を拒否し相互認証。k8s NetworkPolicy を多層防御として併用。残課題は内部 API での OIDC/JWT 検証（別 Issue で追跡） |
| NetworkPolicy 退行・誤設定による Wiki.js への直接到達 | 機密文書が Wiki.js 上で無条件閲覧可能に | ABAC ゲートウェイ＋ネットワーク分離に加え、機密区分由来の `isPrivate`（public 以外は非公開）を多層防御として付与。稼働 Wiki.js での分離検証は PoC フォロー |
| 削除・非公開化された文書が Wiki.js に残存 | 撤回済み社内文書が外部システム（Wiki.js）に残り続ける | **削除・アーカイブ同期経路を実装済み**。`DocumentDeleted` 新設と `status=archived` 拡張で下流 WikiService が Wiki.js ページの撤去・非公開化・メタデータ Archived 化を伝播する。加えて `isPrivate`（public 以外は非公開）を多層防御として維持 |
| 高機密文書本文の外部埋め込み API への送信 | 取り込み時は本文全量を送るため露出が最大。confidential/restricted が外部（Voyage）へ出ると越境統制を破る | 埋め込み専用の越境ポリシー `EmbeddingEgress` で confidential/restricted を**ティアA（セルフホスト）固定**とし、外部（ティアB）を候補から除外。セルフホスト未有効なら**送信せず索引もしない（fail-closed）**。回帰は `EmbeddingEndpointTests`（外部プロバイダ未呼び出し）/ `DocumentUpdatedConsumerTests`（索引スキップ）で担保 |
| 機密区分変更時の旧コレクション残存（ABAC バイパス） | 例 public→confidential 変更後、旧 voyage コレクションに本文が残り機密扱いの文書が低区分コレクションで検索ヒット | 取り込み冒頭で全モデル別コレクションから当該文書を削除してから再索引する（`DeleteByDocumentFromAllAsync`）。回帰は `DocumentUpdatedConsumerTests` で担保 |
| Voyage AI のデータ保持・学習利用 | 送信本文が外部で保持・学習に利用される | 契約でゼロ保持（学習利用オプトアウト）を設定・確認してから本番データを流す（運用仕様書に記録）。未認定の間は Voyage 経路を無効化できる |
| 検索クエリ文の外部埋め込み API への送信 | 検索クエリの埋め込みは機密区分に依らず、**既定では**既定外部経路（Voyage/1024次元）へ固定される（`Purpose=Query`。送信先はゲートウェイの構成 `Embedding:Routing:QueryProfile` で名指しでき、**指定できるのは越境ポリシーが既に許したエンドポイントだけ**である）。検索対象コレクション（voyage/1024）と整合させるための意図的設計だが、利用者が入力するクエリ文自体に機密情報が含まれ得る | クエリ文は本文全量ではなく利用者入力の短文に限られ、Voyage 側のゼロ保持（学習利用オプトアウト）契約が本文と同じく適用される。［2026-09-26 更新］高機密（ruri/768）コレクションは検索が**束ねて読む**ようになり、そのコレクションを引くクエリは**セルフホスト（社外送信なし）で埋める**（検索サービスが読み先コレクションを名乗り、ゲートウェイが越境判定の後で絞る。取り込みの越境判定は変えない）。**主コレクションのクエリの既定は外部経路のまま**と計画が確定したため、クエリ文の越境の再評価は閉じた。ゼロ保持未認定の間は Voyage 経路自体を無効化して受容する |

## 未決事項

> **解消済み（2026-07-10 追従・#201）**: 以下は本節から解消した。
> - サービス間 mTLS の導入→ **STRICT mTLS 配備済み**（#100）。認可・サービスメッシュの計画 ADR も **Accepted 確定**（2026-07-06）。
> - Helm/k8s の NetworkPolicy（デフォルト拒否）追補 → **配備済み**（`templates/networkpolicy.yaml`）。
> - Wiki.js 同期の削除・アーカイブ経路 → **実装済み**。

- サービス間認証の**恒久像**（内部 API での OIDC/JWT 検証。トークン非保持ワーカー含む全呼び出し元）。
  現状は mTLS（相互認証・暗号化）＋ NetworkPolicy を第一/多層防御とし、アプリ層の JWT 検証は残課題として
  別 Issue で追跡（STRICT mTLS の実装 ADR §4）。暫定運用と非機能要件の草案との相違・フェーズ分けは
  `projects/microservices-platform/10_feedback/20260705_internal-service-auth-nfr-deviation.md` で計画側へ環流済み。
- インフラ系（postgres/rabbitmq/keycloak/qdrant/grafana 等）の公開は開発環境限定。共有・ステージング・本番では公開しない運用の明文化。
- RetrievalService `/search` の ABAC 取り扱い。
- 稼働 Wiki.js での GraphQL PoC（スキーマ整合・`isPrivate` ページのサービスアカウント本文取得可否・
  ネットワーク分離の CI/E2E 検証）。GraphQL push 同期の実装 ADR のフォロー。
- 保存時暗号化（PostgreSQL/SeaweedFS/Qdrant）のインフラ層有効化・鍵管理（データ保護表参照。運用整備・#198 連動）。
- コネクタ資格情報の Vault / External Secrets 移行（現状は DB 平文保存＋露出経路のマスクの暫定。上記「§データソースのコネクタ資格情報」参照）。**一元追跡: #458**（旧 #310 は 2026-08-02 に `duplicate` で close）。
- 監査ログの保管期間・改ざん防止・エクスポートの運用設定（可観測性基盤側。#198 連動）。NFR「監査ログ保持」の具体化。
- ~~検索クエリ側の機密区分ルーティング~~ ［2026-09-26 解消］計画が「クエリの埋め込みは取り込みと別に扱い、既定は外部経路。
  高機密コレクションを引くときだけセルフホストで埋める」と確定し、検索は高機密コレクションを束ねて読むようになった。
  残るのは Voyage のゼロ保持契約の認定（上の行）であり、**その認定までクエリ文の外部送信の保護は書面上のもの**である。
