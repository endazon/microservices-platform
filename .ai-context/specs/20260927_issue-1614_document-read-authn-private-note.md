---
title: DocumentService の読み取りの全ての口に認証を求め、個人資料を所有者・共有先以外へ返さない（#1614）
type: spec
status: in-progress
related_ids: [FR-05, FR-06, FR-19, UC-03, SC-03, SC-05, NFR-09, ADR-0119, IADR-0476, ADR-0034, ADR-0036, ADR-0054, ADR-0056, ADR-0086, ADR-0109, IADR-0012, IADR-0041, IADR-0045, IADR-0379, IADR-0402, IADR-0447]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0119_document-machine-client-own-docs-and-read-abac.md 決定 3・決定 4
  - planning:projects/microservices-platform/10_feedback/20260927_document-machine-client-and-read-abac.md §残るもの
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md §所有者ベースの read 規則（2026-09-27 補完）
issue: "#1614"
---

# 仕様書: DocumentService の読み取りに認証を求め、個人資料を所有者・共有先以外へ返さない（#1614）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **NFR-09**（全 API で文書・データ単位の認可）、**FR-06** / **UC-03**（文書の閲覧）、**FR-19**（個人資料）、FR-05（ABAC）
- 画面: SC-03（文書詳細。BFF の詳細・本文・版）、SC-05（文書管理の一覧。BFF の管理一覧）
- 関連 ADR:
  - **ADR-0119 決定 3**（読み取りの全ての口で認証を求める。個人資料は所有者と共有先にだけ返す。読めない文書は一覧から除き、個別は 404）
  - **ADR-0119 決定 4**（3 点セット。**認証の要求と個人資料の除外は、内容の ABAC より先に入れてよい**）
  - ADR-0034 決定 9（サービスアカウントは個人資料を一律に対象外）・ADR-0036 D-05/D-06/D-08（所有者・共有先・管理者も見ない）・
    ADR-0054（`doc_scope`）・ADR-0056（存在秘匿は 404）・ADR-0086 決定 1（east-west は利用者文脈を本文で運ぶ）・
    ADR-0109 決定 3（エッジが中継した利用者の資格情報を後段が自ら検証する）
- 起点 issue: #1614（planning#680 の実装側の残作業のうち「先に入れてよい」部分）。**内容の ABAC（認可サービスへの問い合わせで組織文書を絞る）は #1615**

## 目的・背景

planning#680 の実測 10: develop の DocumentService の `GET /documents`・`GET /documents/{id}`・版の取得は認証を求めない群（`g`）に属し、
個人資料も除かない。個人資料の表題・属性・owner・共有先が、メッシュ内の任意の呼び出し元へ返っている。
gRPC 面（`DocumentRead`）は `ServiceCaller` を要求するが、個人資料は除かない。

## 対象範囲

- 対象:
  - REST の読み取り 5 口（`GET /documents`・`GET /documents/page`・`GET /documents/{id}`・`GET /documents/{id}/versions`・
    `GET /documents/{id}/versions/{version}`）と gRPC `DocumentRead` の 4 rpc。
  - 呼び出し側の BFF（REST の読み取りへ利用者の資格情報を中継する／gRPC の読み取りへ利用者文脈を本文で運ぶ）。
  - OpenAPI（DocumentService の読み取り 5 口の security・401・404）、proto（`UserContext` の追加。非破壊）。
- 対象外:
  - **内容の ABAC**（組織文書の機密・部門・ライフサイクルによる絞り込み）—— #1615。本件は差し込み口（下記「#1615 の差し込み口」）だけを用意する。
  - 機械クライアントの自分の文書の更新・削除（ADR-0119 決定 2）、`IADR-0075` の改訂。
  - `edge.privateNotesSync.enabled` の AuthorizationPolicy（PR #1618。chart は触らない）。
  - 書き込みの口・共有台帳・`/private-notes/*`（既に認証を要する）。
  - 健全性の口（`/health/*`）は匿名のまま（変えない）。

## 呼び出し元の母集合（着手前に自分で引いた）

<!-- 規則 9: 記憶で挙げず、走査してから挙げる。 -->

（下記「呼び出し元の棚卸し」節に、走査の方法・結果・除外理由を記す。）

## 設計

### 1. 主体（`DocumentReadPrincipal`）

判定の主体は ADR-0119 決定 3 の 3 種のいずれかである。

| 経路 | 主体 | 決め方 |
| --- | --- | --- |
| REST（利用者の資格情報を中継） | その利用者 | `HttpContext.User`。`MachinePrincipal.IsMachine` が偽なら利用者、`Identity.Name`（`preferred_username`）が主体 |
| REST（機械クライアント自身の資格情報） | そのサービスアカウント | `MachinePrincipal.IsMachine` が真 |
| gRPC（本文で運ばれた利用者文脈） | その利用者 | 要求の `user`（`UserContext`）が在れば `user.user_id`。空文字は `INVALID_ARGUMENT` |
| gRPC（利用者文脈なし） | 呼び出し側サービス自身 | `user` が無ければ機械の主体 |

- 🔴 **判定は既存の `MachinePrincipal.IsMachine` ただ 1 つに委ねる**（新しい述語を作らない）。gRPC の本文の `user_id` が
  `service-account-` で始まるときも機械として扱う（同じ接頭辞の定数）。
- 利用者名を持たない人（`preferred_username` も `azp` も無い）は「主体が分からない」として個人資料を 1 件も見ない（組織文書は見る）。

### 2. 可視性（`DocumentReadAccess`）

- **組織文書**（`DocumentScopes.IsPrivateNote` が偽。キー欠落を含む）: 認証済みの全主体に返す（内容の ABAC は #1615）。
- **個人資料**（`DocumentScopes.IsPrivateNote` が真。値は大文字小文字を区別しない）: 次のいずれかを満たす**利用者**にだけ返す。
  機械の主体には一律に返さない（ADR-0034 決定 9）。管理者ロールでも返さない（ADR-0036 D-08）。
  1. **所有者**: `owner` 属性 == 主体（序数一致。`DocumentBodyIntake.CanWrite` の動的束縛と同じ比較）
  2. **利用者の共有先**: 主体 ∈ 共有台帳の `SubjectId`（`ResolveSharedWithAsync` と同じ集合）
  3. **グループの共有先**: 上の 2 つで決まらず、共有台帳に 1 件以上あるときだけ、**認可サービスへ問う**
     （`AuthzScope/Resolve`、action=`read`。所属は認可サービスが IdP から引く ―― トークンの `groups` は使わない）。
     許可の根拠は BFF・検索・グラフと同じ述語（`AttributeFilterMatch.MatchesAll` ∧ `PrivateNoteVisibility.BranchMayGrant`）を、
     共有先を重ねた像（`DocumentAttributeEncoding.WithSharedWith`）に対して評価する。**判定器を新設しない。**
     - 問い合わせは**要求ごとに高々 1 回**（利用者ごとに memo）。所有者・利用者の共有先・組織文書だけの読み取りでは 1 回も問わない。
     - 🔴 **引けない・未構成・許可なしは「読めない」へ倒す**（fail-closed。その資料だけが見えなくなり、組織文書と所有者の読み取りは止まらない）。
- **読めない文書**: 一覧から除く（件数にも含めない）。個別・版の一覧・特定版は **404**（REST）／**`found=false`**（gRPC）。
  「無い」と区別しない（ADR-0056）。特定版は**現在の文書**の可視性で判定する（版のスナップショットの属性では判定しない ―― `doc_scope` は不変）。
- `GET /documents/page` は従前どおり個人資料を**値にも主体にも依らず返さない**（組織文書の口。所有者でも空）。認証の要求も従前どおり。

### 3. REST

- 読み取り 5 口を**認証を要する 1 つの群**へ寄せる（従前の匿名の `g` 群を廃す）。ロールは積まない（SC-03 の一般利用者）。
- 各口は `HttpContext` から主体を作って use case へ渡す。応答の形・状態コードは読めるときは従前どおり。

### 4. gRPC

- `ServiceCaller` を要求するのは従前どおり（利用者のトークンは面を通らない）。
- 4 つの要求へ `UserContext user`（`user_id` / `user_attributes` / `action`。retrieval・graph と同じ 3 項目）を**新しい番号で**足す（非破壊）。
  `user_attributes` / `action` は本件では読まない（#1615 で内容の ABAC を足すときに契約を破らずに効かせるため。ADR-0086 決定 1 の形）。

### 5. BFF（呼び出し側）

- **REST の読み取り（一覧・詳細・版・特定版・書き込みの事前確認）**: 書き込みと同じ `Forwarding`（利用者の `Authorization` を中継）で呼ぶ。
  セッション方式では `SessionTokenPropagationMiddleware` が昇格したアクセストークンが載る。
- **gRPC の読み取り**: 呼び出し元の利用者を `UserContext` で運ぶ（`AttributeValuesGrpcClient.ToUserContext` と同じ抽出）。
  🔴 BFF の呼び出し元が機械（Bearer の無人主体）のときは**運ばない**（BFF 自身の機械の主体として読まれ、個人資料は返らない）。
- BFF の判定（`BffScopeResolver` ＋ `IsManageable` / `IsReadable`）は**変えない**。DocumentService の判定は BFF の判定より**広いか等しい**
  （所有者・共有先は通す）ので、BFF の応答は変わらない（AND 合成）。

### 6. #1615 の差し込み口

- 可視性の判定は `DocumentReadAccess` ただ 1 か所であり、REST 5 口と gRPC 4 rpc はすべてここを通る。
- 認可サービスへの問い合わせは `IDocumentReadScopeSource`（ポート）に閉じる。#1615 はこのポートの結果を**組織文書にも**適用する
  （所有者・共有先の分岐は同じ結果に含まれる）ことで、判定点を増やさずに入る。
- gRPC の `UserContext.user_attributes` / `action` は #1615 のために先に契約へ置く。

### 7. 実装 ADR

- 新規 IADR を 1 本起こす（主体の決め方・グループの共有先を認可サービスへ問う形・fail-closed の向き・BFF の中継の形）→ **IADR-0476**。
  番号は push 時点の origin/develop の最大 + 1（欠番を作らない）。

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | 認証の無い読み取りは **401**（REST 5 口）／**UNAUTHENTICATED**（gRPC 4 rpc）。**本物の JwtBearer** で測る | `DocumentReadAuthenticationTests`（Kestrel・実 JWT） |
| AC-2 | 他人の個人資料は、一覧（REST `GET /documents`・`/documents/page`・gRPC `ListDocuments`）に出ず、個別（REST `GET /documents/{id}`・版の一覧・特定版／gRPC `GetDocument`・`ListVersions`・`GetVersion`）は 404／`found=false` | `DocumentReadPrivateNoteTests`・`DocumentReadAuthenticationTests` |
| AC-3 | 所有者と共有先（利用者・グループ）は読める（対照）。グループは認可サービスが許したときだけ | `DocumentReadPrivateNoteTests`（スタブのポート） |
| AC-4 | 機械クライアントは組織文書を読めるが、個人資料は読めない（自分が所有者を名乗っても・共有先に居ても） | 同上・gRPC は利用者文脈なしで |
| AC-5 | 管理者ロールの利用者でも他人の個人資料は読めない（ADR-0036 D-08） | `DocumentReadPrivateNoteTests` |
| AC-6 | 認可サービスを引けないとき、グループの共有先の資料だけが見えなくなり、所有者・組織文書の読み取りは止まらない（fail-closed） | 同上 |
| AC-7 | gRPC の `user.user_id` が空なら `INVALID_ARGUMENT` | `DocumentReadAuthenticationTests` |
| AC-8 | BFF は REST の読み取りへ利用者の `Authorization` を中継し、gRPC の読み取りへ利用者文脈を載せる（機械の呼び出し元では載せない） | `Platform.Bff.Tests` |
| AC-9 | 既存の消費者の試験（BFF・DocumentService・結合）が緑 | `dotnet test` 両 slnx |
| AC-10 | 健全性の口は匿名のまま | 既存 `HealthEndpointTests` |

## 呼び出し元の棚卸し

### 引き方（規則 9: 記憶で挙げず走査した）

- MSP（origin/develop `45932b52`）: `"/documents` / `$"/documents` / `DocumentRead.DocumentReadClient` / `DocumentReadGrpcClient` /
  `CreateClient("DocumentService")` を C#（本体・試験）・TS（frontend・e2e）・`scripts/`・`deploy/`・`.github/workflows/` で走査。
  拡張子で絞らずパスの除外（`node_modules`・`obj`・`bin`）だけで引いた。
- AST: submodule は未初期化だったため隣接クローン `ai-stock-trading` の **`origin/develop` = `209ae4ce`**（2026-09-27）を `git grep` で読んだ
  （作業ツリーは `e6c8f781` で 20 コミット遅れ・`HttpKnowledgeDocumentCatalog` を含まない）。`KnowledgeBase__Documents__BaseUrl`・
  `KnowledgeBase:Auth`・`HttpKnowledgeDocumentCatalog`・`/documents` で引いた。

### 結果（読み取りの呼び出し元）

| # | 呼び出し元 | 口 | 輸送 | 今日の資格情報 | 本件の後 |
| --- | --- | --- | --- | --- | --- |
| 1 | BFF `DocumentBffEndpoints.FetchAuthorizedAsync`（詳細・本文・版・書き込みの事前確認） | `GET /documents/{id}` | REST（`Services:DocumentServiceGrpc` が無いときだけ） | **無し** | **本件で利用者の `Authorization` を中継**（`Forwarding`）。個人資料は所有者・共有先に返る |
| 2 | 同・版の一覧 | `GET /documents/{id}/versions` | REST | 無し | 同上 |
| 3 | 同・特定版 | `GET /documents/{id}/versions/{v}` | REST | 無し | 同上 |
| 4 | 同・一覧（SC-05。admin / operator） | `GET /documents` | REST | 無し | 同上（BFF の `IsManageable` が個人資料を外すので応答は不変） |
| 1g〜4g | 同じ 4 箇所の gRPC 経路（`DocumentReadGrpcClient`）。**compose・helm はこちらで動く** | `DocumentRead` 4 rpc | gRPC | BFF 自身の s2s（`bff`・`platform-service`） | **本件で本文の `UserContext` に利用者を載せる**（呼び出し元が機械なら載せない）。s2s は従前どおり |
| 5 | AST ReportService `HttpKnowledgeDocumentCatalog.ListAsync`（KB の入れ直し。`ReportKnowledgeReingestService`） | `GET /documents` | REST | client credentials（`ai-stock-trading-kb-writer`・`platform-operator` のみ。`AddAiStockTradingKnowledgeBaseAuth` の `ServiceTokenHandler`） | **通る**（認証済みの機械の主体。組織文書は返る。個人資料は探していない）。**AST の変更は不要** |
| 6 | `scripts/seed-search-documents.js` の到達確認（`waitReachable`） | `GET /documents` | REST | 無し | 401 になるが「何か応答があれば到達」なので影響しない |
| 7 | 同・冪等確認（`getJson`） | `GET /documents` | REST | client credentials（`abac-seeder`）の Bearer | **通る**（機械の主体。組織文書の題名で冪等を見る） |

- AST の補足: KB の書き込み（`POST /documents`）は従前から認証を要し、同じ名前付きクライアント・同じ資格情報で送られる。
  AST の helm は `KnowledgeBase__Documents__BaseUrl` と `KnowledgeBase__Auth__*` を対で描く（`values-local.yaml`）。
  秘密が空の配備では書き込みが既に 401（README「空=401→未保存（fail-safe）」）であり、本件で一覧も同じ 401 になる ——
  AST の入れ直しは「一覧を引けなければ 1 件も書かない」（AST/IADR-0436）ので、失敗の向きは安全側である。**段階導入は不要**と判断した。

### 除外したもの（呼び出し元ではない）と理由

- **frontend**（`src/platform/frontend`・`src/knowledge/frontend`・`src/packages`・`src/obsidian-plugin`）: `/bff/documents*` だけを呼ぶ（orval 生成フック）。
  e2e の `'GET /documents'` は `/bff` を剥いたキー。BFF の契約は変えていない（orval の再生成で差分なしを確認）。
- **GraphService**: 名前付き `DocumentService` は書き込み `POST /documents/{id}/tags`（承認者の資格情報を中継）と `/internal/tags/names` だけ。
  gRPC は TagWrite / TagDictionary だけ。文書は `DocumentUpdated` / `DocumentDeleted` の購読で得る。
- **IngestionService / RetrievalService / WikiService**: 事象の購読だけ（Wiki は本文をオブジェクトストレージから読む）。
- **AiAnalysisService**: `/documents/{id}` はリンク文字列の組み立てだけ。
- **McpServer**: `/documents` を呼ばない（DocumentService が申告するツールの口は `/internal/mcp/*` で、本件の対象外）。
- **BFF の他のファイル**: `PrivateNoteBffEndpoints` は `/private-notes/*` と `/documents/{id}/shares`（共有台帳。既に認証必須で利用者を中継）、
  `TagDictionaryBffEndpoints`・`SearchBffEndpoints` は `/tags`。いずれも本件の読み取りの口ではない。
- **合成監視**: `/bff/analysis/ask`・`/bff/analysis/ask/stream` だけ。k6・helm の test hook・cronjob・compose の healthcheck は `/documents` を触らない
  （healthcheck は `/health/ready`）。`.github/workflows` は `POST /documents` を注記で挙げるだけ。

### 既存の試験で直したもの（漏れていた前提）

- `PrivateNoteLifecycleTests`・`ObsidianSyncProtocolTests`・`ObsidianSyncMoveTests`: 個人資料を**他人（既定の `test-user`）として**読んでいた
  （＝漏れを前提にしていた）。所有者として読むように直した。完全削除の確認は所有者として 404 を見る（他人としてでは消えていなくても 404 で何も測れない）
  ＋保管中の資料は所有者に 200（陽性対照）。
- `DocumentPageTests`: 「既存の一覧は認証を要らない」を固定していた試験を「読み取りの 5 口はすべて認証を要する」へ改めた。陽性対照の
  「既存の一覧には個人資料が居る」は所有者の一覧で見るように直した。
- `GrpcDocumentReadTests.Rest_and_grpc_report_the_same_documents`: REST を無認可で引いていた。REST・gRPC とも同じ機械の主体で比べる。

## 検証

- `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes`（両 slnx）、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- 変異: ①読み取りの 1 口から認証の要求を外す → AC-1 の試験が落ちる ②1 口から個人資料の除外を外す → AC-2 の試験が落ちる
