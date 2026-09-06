---
title: east-west gRPC の展開（第 3 スライス）— AuthorizationService の 5 呼び出し元を移し、利用者トークンの転送は名簿の読み口を狭めて置き換える
type: spec
status: done
related_ids:
  - FR-01
  - FR-04
  - FR-05
  - FR-13
  - FR-16
  - FR-17
  - NFR-09
  - NFR-16
  - UC-01
  - UC-02
  - UC-04
  - UC-07
  - UC-09
  - UC-10
  - SC-06
  - SC-12
  - SC-17
  - ADR-0004
  - ADR-0011
  - ADR-0029
  - ADR-0034
  - ADR-0036
  - ADR-0062
  - ADR-0064
  - ADR-0074
  - ADR-0075
  - IADR-0009
  - IADR-0253
  - IADR-0272
  - IADR-0299
  - IADR-0316
  - IADR-0329
  - IADR-0335
  - IADR-0378
  - IADR-0379
  - IADR-0384
  - IADR-0385
  - IADR-0397
  - IADR-0400
  - IADR-0401
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md §決定・2026-08-04 追記
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 3・5・6
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md §決定
  - planning:projects/microservices-platform/07_adr/ADR-0062_unattended-account-attribute-subset.md 決定 2・3
  - planning:projects/microservices-platform/07_adr/ADR-0074_owner-mapping-table-container-in-sc06.md 決定 1・4
  - planning:projects/microservices-platform/07_adr/ADR-0064_sc17-backend-belongs-to-authorization-service.md 決定 4
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md 決定 8
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md D-07
  - planning:projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md
---

# 仕様書: AuthorizationService の east-west gRPC 化（#1255 第 3 スライス）

> 本書は #1255（east-west gRPC の展開）の**第 3 スライス**の作業仕様である。
> 第 1 スライス（#1290 / `IADR-0397`。埋め込み）と第 2 スライス（#1295 / `IADR-0400`。テキスト生成）が
> 着地させた形を**そのまま写す**のが基本方針であり、`IADR-0379` の 4 決定と
> `docs/api/east-west-grpc.md` §1〜§4 は**変えない**。
>
> 🔴 本スライス固有の論点はただ 1 つである ——
> **2 呼び出し元（DataSourceService / McpServer）が利用者の `Authorization` を認可サービスへ転送している。**
> ガイド §4 の 🔴「利用者トークンはメタデータへ載せない」と正面から衝突する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（ABAC による認可）／FR-04（RAG 回答）／FR-13（Wiki 閲覧）／
  FR-16（MCP サーバ）／FR-17（ナレッジグラフ）／FR-01（データソース登録）／
  NFR-09（全 API で OIDC/JWT。gRPC 面は `ServiceCaller`）／NFR-16（サービス間 mTLS。h2c ＋ サイドカー終端）
- ユースケース（UC）: UC-01・UC-02（横断検索・分析）／UC-04（データソース登録）／UC-07（Wiki 閲覧）／
  UC-09（MCP クライアント登録）／UC-10（グラフ探索）
- 画面（SC）: SC-06（データソース登録の写像表）／SC-12（MCP クライアント登録）／SC-17（利用者管理）
- 関連 ADR: `ADR-0029`（east-west 同期は gRPC。例外は対象経路を明記した新 ADR に限る）／
  `ADR-0075` 決定 3・5・6（移行順序＝基盤先行。**IADR で REST 継続を自認しない**）／
  `ADR-0004`（ABAC）／`ADR-0034` 決定 8（ホップごと判定）／`ADR-0036` D-07（所有者ベースの裁量制御）／
  `ADR-0062` 決定 2・3（無人アカウントの属性は登録者の部分集合。**判定は後段が行う**）／
  `ADR-0064` 決定 4（SC-17 の後段は認可サービス）／`ADR-0074` 決定 1・4（写像先の実在検証）／
  `ADR-0011`（Wiki エンジン）
- 実装 ADR: `IADR-0379`（先行条件の 4 決定。**本作業はその適用であり改定しない**）／
  `IADR-0397`・`IADR-0400`（第 1・第 2 スライス。**本作業はこの鎖を延ばす**）／
  `IADR-0009`（存在秘匿）／`IADR-0253`（分岐つきスコープ）／`IADR-0272` 決定 4（action は必須引数）／
  `IADR-0299`（`/internal/*` の残余リスクの受容）／`IADR-0316`（Secret 注入の宣言と配備の突合）／
  `IADR-0329` 決定 1（`view-users` を持つ主体は 1 つ）／`IADR-0335`（Wiki の未認証短絡）／
  `IADR-0378`（合成標識の 2 段）／`IADR-0384` 決定 1（`ReadAssignableConfidentiality` に読み方を閉じる）／
  `IADR-0385` 決定 2（集合値属性のカンマ連結）／本作業で新設する `IADR-0401`
- 計画書リンク: 隣接クローン `../project-planning/projects/microservices-platform/`（読み取り専用）

🔴 **引用した計画 ADR は実ファイル名で実在と主題を確認した**（レンジ検査は主題の誤りを通す）:
`ADR-0029_grpc-rest-usage-criteria.md` / `ADR-0075_east-west-grpc-migration-order.md` /
`ADR-0004_authz-abac.md` / `ADR-0034_graph-traversal-abac-enforcement.md` /
`ADR-0036_ownership-based-discretionary-access.md` / `ADR-0062_unattended-account-attribute-subset.md` /
`ADR-0064_sc17-backend-belongs-to-authorization-service.md` /
`ADR-0074_owner-mapping-table-container-in-sc06.md` / `ADR-0011_wiki-engine.md`。

## 母集合の再導出（自分で引いた。issue・設計書の数字は転記していない）

基点 `origin/develop` `32724227`。`git rev-parse --is-shallow-repository` = **`false`**（履歴の打ち切り無し。
`git log` / `git blame` を出典に引ける）。

### 軸 1: 端点の文字列

```console
$ grep -rn --include=*.cs -E '"/authz/scope"|"/authz/users"' src | grep -v /obj/
（37 行。うち Tests/ を除いた非テストは 9 行）
```

非テストの 9 行の内訳:

| 行 | 位置づけ |
| --- | --- |
| `AiAnalysisService/…/RagOrchestrator.cs:296` | **対象（行 7）** |
| `GraphService/…/GraphAccessResolver.cs:34` | **対象（行 8）** |
| `WikiService/…/WikiAccessResolver.cs:40` | **対象（行 9）** |
| `DataSourceService/…/AuthorizationServiceUserDirectory.cs:43` | **対象（行 10）** |
| `McpServer/…/AuthorizationServiceRegistrarAttributes.cs:98, 126` | **対象（行 11。2 箇所）** |
| `Platform.Bff/…/UserAdminBffEndpoints.cs:32` | 対象外（BFF の 15 本の側。#1255 の別 PR） |
| `AuthorizationService/…/UserAdminEndpoints.cs:35` | 呼び出し**先**（端点の定義） |
| `Platform.Shared.Infrastructure/…/BffScopeResolver.cs:44` | 対象外（参照実装。gRPC 済み・#1201） |

**陽性対照**: 同じ走査で `"/authz/…"` のリテラルは全体で **78 行**ある（テスト・端点定義を含む）。
9/78 が非テストの呼び出し側であることを目視で確認した。

### 軸 2: 要求 DTO の構築（軸 1 が落とすものを拾う）

```console
$ grep -rn --include=*.cs "new AccessScopeRequest(" src | grep -v /obj/ | grep -v "/Tests/"
src/knowledge/backend/Services/AiAnalysisService/…/RagOrchestrator.cs:297
src/knowledge/backend/Services/GraphService/…/GraphAccessResolver.cs:35
src/knowledge/backend/Services/WikiService/…/WikiAccessResolver.cs:41
src/platform/backend/Services/AuthorizationService/…/ResolveScope/GrpcService.cs:34   ← 呼び出し先
src/platform/backend/Services/McpServer/…/AuthorizationServiceRegistrarAttributes.cs:127
src/platform/backend/Shared/…/Authz/BffScopeResolver.cs:45                              ← gRPC 済み
```

**陽性対照**: テストを含めた全構築は **32 箇所**。軸 1 と軸 2 は `/authz/scope` の呼び出し元について
**同じ 4 集合**（AiAnalysis / Graph / Wiki / McpServer）を指した。

🔴 **軸 1 を名前（`grep "AddHttpClient" | grep AuthorizationService`）だけで引くと 2 件落ちる** ——
`DataSourceService/Program.cs:88` と `McpServer/Program.cs:47` は `AddHttpClient(` の**次の行**で
定数 `…UserDirectory.HttpClientName` / `…RegistrarAttributes.HttpClientName` を渡すためである
（`traceability.repo.md` 規則 5「軸を 1 本で終わらせない」）。

### 軸 3: 名前付き HTTP クライアントの登録（`Services:AuthorizationService`）

```console
$ grep -rn -A3 'AddHttpClient(' src/*/backend/Services/*/Program.cs \
    src/platform/backend/Bff/Platform.Bff/Program.cs | grep AuthorizationService
```

6 サービス: AiAnalysis / DataSource / Graph / Wiki / McpServer / **Bff**（対象外）。
軸 1・2 と合わせて **呼び出し元は 5 サービス・呼び出し箇所は 6 箇所**である。

### 軸 4: 利用者トークンの転送（本スライスの本丸）

```console
$ grep -rn --include=*.cs "Headers.Authorization" src/knowledge/backend/Services \
    src/platform/backend/Services | grep -v /obj/ | grep -v "/Tests/"
（12 行）
```

| 落とし込み | 件数 | 内訳 |
| --- | --- | --- |
| **認可サービスへ**利用者トークンを転送している | **2** | `AuthorizationServiceUserDirectory.cs:37`／`AuthorizationServiceRegistrarAttributes.cs:66` |
| 同型（転送）だが**本スライスの射程外**の east-west | 4 | `RagOrchestrator.cs:213`（→ Retrieval）／`HttpDocumentTagWriter.cs:34`（→ Document）／`GraphServiceNeighborExpander.cs:41`（→ Graph）／`ObsidianSyncEndpoints.cs:51`（受信側） |
| east-west でない（外部 SaaS / IdP / 外部 API） | 6 | SaaS/Wiki コネクタ・Wiki.js・Keycloak Admin・Copilot・Voyage |

**陽性対照**: テスト・BFF を含めた全体は **90 行**（`src/ai-stock-trading` を除く）。

### 軸 5: 現行の proto と realm の主体

```console
$ git ls-files "*.proto"
src/platform/backend/Shared/…/Protos/platform/authz/v1/authz_scope.proto
src/platform/backend/Shared/…/Protos/platform/llmgateway/v1/completion.proto
src/platform/backend/Shared/…/Protos/platform/llmgateway/v1/embedding.proto
（名簿の proto は 0 ＝ 陰性）

$ grep -n 'platform-service' deploy/keycloak/microservices-platform-realm.json
（ロール定義 1 ＋ client description 5 ＋ users[].realmRoles 5）
```

🔴 **`platform-service` を持つ主体は develop 時点で 5 つだけである** ——
`retrieval-service` / `ingestion-service`（#1290）と `aianalysis-service` / `graph-service` /
`conversion-service`（#1295）。**`bff` は `users[]` に存在せず、`platform-service` を持たない**
（`IADR-0379` 決定 4 の散文は「realm 側で付けてある」と書くが、**実測では付いていない**）。
本スライスはこの形を**変えない**（BFF の gRPC 配線には触らない。#1290 / #1295 が意図して残した）。

したがって本スライスで**新設する confidential client は 3 つ**である ——
`wiki-service` / `datasource-service` / `mcp-server`。
AiAnalysis と Graph は #1295 が作った `aianalysis-service` / `graph-service` を**再利用する**
（1 サービス 1 主体。呼び出し先ごとに主体を割らない）。

### 除外とその理由

| 除外したもの | 理由 |
| --- | --- |
| `Platform.Bff` の `/authz/users` プロキシ（`UserAdminBffEndpoints.cs:32`）と BFF の残り 14 本 | #1255 の別 PR（呼び出し先ごとに切る最小単位）。BFF の gRPC 配線は #1290 / #1295 が意図して残しており、ここで反転すると BFF のスコープ解決が**本 PR の証拠の外で** gRPC へ移る |
| `BffScopeResolver`（参照実装） | #1201 で gRPC 済み。本 PR は `AuthzScopeGrpcClient` に**足す**だけで既存経路を変えない |
| `RagOrchestrator.cs:213` → Retrieval／`HttpDocumentTagWriter`／`GraphServiceNeighborExpander` | 呼び出し先が**利用者の権限で動く**（ホップごと ABAC。`ADR-0034` 方式 A）。決定 3 の「読み口を狭める」では解けない。次の壁として名指しする |
| introspection 収集・`Document → Notification`・`Graph → Dashboard`・`McpServer HttpToolInvoker` | #1255 の別 PR |
| AST（`src/ai-stock-trading`）の各サービス | submodule。本リポジトリからは触らない（`ADR-0075` 決定 4） |
| REST の `/authz/scope` / `/authz/users` の撤去 | 並走中の正は REST（`IADR-0379` 決定 5）。撤去は REST 撤去の段の IADR |
| 利用者名の照合規則の統一（Ordinal vs OrdinalIgnoreCase） | 移行の不変条件は「挙動を変えない」。統一は別 issue |
| `IIdentityAdminClient` への by-username の口 | `UserDirectory` は `ListUsersAsync` の上で実装できる。最適化は別 issue |

## 対象範囲

### 対象

| # | 対象 | 内容 |
| --- | --- | --- |
| 1 | `Protos/platform/authz/v1/user_directory.proto` | `UserDirectory/{CheckUsernames, GetUserAttributes}`。**列挙と書き込みは出さない** |
| 2 | `AuthorizationService/Features/Users/Directory/GrpcService.cs` | `[Authorize(Policy = ServiceCaller)]`。`Program.cs` の `MapGrpcService<AuthzScopeGrpcService>()` の隣へ登録 |
| 3 | `AuthzScopeGrpcClient.ResolveScopeAsync` | 既存の `ResolveAsync → BffAccessScope?` は**変えない**。`AccessScopeResponse` を返す多重定義を**足す** |
| 4 | `Authz/UserDirectoryGrpcClient` ＋ 登録拡張 | `Services:AuthorizationServiceGrpc` が在るときだけ登録。チャネルは `AddAuthzScopeGrpcClient` と共有 |
| 5 | 行 7・8・9（AiAnalysis / Graph / Wiki） | `AuthzScopeGrpcClient?` の任意注入。**proto の追加は 0**（既存 `AuthzScope/Resolve` を使う） |
| 6 | 行 10（DataSource） | port を「列挙」から「照会」へ改める。REST 実装は列挙して交差（挙動不変）、gRPC 実装は `CheckUsernames` |
| 7 | 行 11（McpServer） | gRPC 兄弟クラス。`ReadAssignableConfidentiality` は**共通 static へ 1 文字も変えずに**括り出す |
| 8 | realm / helm / compose / ESO / Vault seed | 新設 3 client（`wiki-service` / `datasource-service` / `mcp-server`）＋ 5 呼び出し元の `ServiceToken` と `Services__AuthorizationServiceGrpc` |
| 9 | proto baseline | `node scripts/check-proto-contracts.js --update` |
| 10 | `IADR-0401` ＋ 索引行 ＋ `docs/api/east-west-grpc.md` の追記 | |

### 対象外（PR 本文にも書く）

BFF の 15 本の REST 呼び出し／introspection 収集／`Document → Notification`／`Graph → Dashboard`／
`McpServer HttpToolInvoker`／AST の各サービス／設計書 §5 が挙げたすべて。

## 🔴 本スライスの載る決定（弱めてはならない）

`docs/api/east-west-grpc.md` §4 は赤字で **利用者トークンはメタデータへ載せない**（confused deputy）と定め、
利用者の文脈は**本文**で運ぶと定める。しかし行 10・11 は現に利用者の `Authorization` を転送しており、
コード注記がその理由まで書いている。

**転送ではなく、呼び出し先の読み口を狭めることで移す。** 基点コミットで再検証した事実:

| 事実 | 実測（`32724227`） |
| --- | --- |
| `/authz/users` の門は `AdminOnly` のグループであり、**ハンドラは主体を一切読まない** | `UserAdminEndpoints.cs:35-37`（`RequireAuthorization(AdminOnly)`）／`ListUsers/Endpoint.cs:11-12`（`ListUsersAsync(ct)` のみ。`HttpContext` を取らない） |
| DataSource が要るのは「これらの利用者名は実在するか」だけ | `AuthorizationServiceUserDirectory.cs:61-62` → `OwnerMappingTable.ValidateTargetsExist` |
| McpServer が要るのは「認証済みの登録者**自身** 1 人の属性」だけ | `AuthorizationServiceRegistrarAttributes.cs:58, 105-108` |
| 人の側の `AdminOnly` は**呼び出し元の端点にも**ある | DataSource の Create / Update / Patch / Disable（4 端点）／McpServer の `/mcp-clients`（`McpClientEndpoints.cs:24`） |

したがって新 rpc は**意図的に狭い** —— 列挙と書き込みは s2s の面に**存在しない**。

🔴 **残余リスク（隠さずに IADR へ書く）**: `platform-service` を持つサービスは
「名指しした 1 人の**真の**属性」を読める。これは `AuthzScope/Resolve` が呼び出し元の**主張する**
`user_attributes` をそのまま評価に使うのと同じ信頼であり（偽の属性を主張できる方が強い）、
境界は `IADR-0299` / `IADR-0378` 内周と同型（ClusterIP ＋ NetworkPolicy ＋ STRICT mTLS）である。

🔴 **token exchange（RFC 8693）は採らない。** Keycloak は `24.0`（`deploy/docker-compose.yml`）で
token exchange は preview 機能であり、要件は「読む」だけで利用者の権限を使わずに満たせる。

🔴 **狭めても解けない呼び出し先が出たら、利用者トークンの転送へ倒さない。** その経路は REST に残し、
「何が要るか」を書いて止める —— `ADR-0075` 決定 5 が実装側 IADR による REST 継続の自認を禁じており、
計画側の裁定（token exchange の ADR か、対象経路を明記した REST 例外 ADR）が要る。

## 受け入れ基準

- [x] `user_directory.proto` が `check-proto-contracts.js` の R1〜R4 を通り、baseline へ**非破壊の追加**として載る
- [x] `UserDirectoryGrpcService` が `[Authorize(Policy = ServiceCaller)]` を宣言し、リフレクションで固定される
- [x] 🔴 **管理者の利用者トークンを転送しても `UserDirectory` は `PERMISSION_DENIED`**（決定 3 を機械で守る唯一の点）
- [x] 資格情報無しは `UNAUTHENTICATED`
- [x] `CheckUsernames` は要求と**同じ順・同じ数**を返す
- [x] REST と gRPC が同じ入力で同じ答えを返す（名簿・スコープの両方）
- [x] 行 7・8・9 で REST / gRPC の `Granted` / `AllowedFilters` / `Branches` が一致（`read` / `write` の両 action）
- [x] 🔴 Wiki は未認証のとき gRPC を**呼ばない**（偽クライアントの呼び出し回数 0 を表明）
- [x] 行 10 の両実装に陽性・陰性・縮退の対がある（**無効化された利用者も実在として数える**／序数一致）
- [x] 行 11 の既存 19 件が gRPC 実装でも緑（登録者は**自分自身**の名前でしか引かない／`tags` の集合値が往復する）
- [x] 🔴 `ReadAssignableConfidentiality` が**1 文字も動いていない**（`git diff` で確認）
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が両ユニットで通る
- [x] テストが 1 本も削除・スキップされていない（プロジェクト別の前後数を PR 本文へ）
- [x] 変異検査 2 種を実施し、赤になる試験を PR 本文へ載せた

## 試験計画

| ID | 対象 | 内容 |
| --- | --- | --- |
| T-P2-01 | 行 7 | REST / gRPC で `Granted` / `AllowedFilters` / `Branches` が一致（`RagOrchestratorScopeTests` を両経路で） |
| T-P2-02 | 行 8 | `read` / `write` の両 action で一致 |
| T-P2-03 | 行 9 | 認証済みで一致・**未認証は gRPC を呼ばない**（呼び出し回数 0） |
| T-P2-04 | 行 10 | REST / gRPC の両実装に陽性（実在）・陰性（不在）・縮退（不達 → `Available=false`）の対 |
| T-P2-05 | 行 10 | 無効化された利用者も実在として数える |
| T-P2-06 | 行 10 | 序数一致（大小文字違いは不在） |
| T-P2-07 | 行 11 | 既存の 19 件を gRPC 実装でも回す |
| T-P2-08 | 行 11 | 登録者は**自分自身**の名前でしか引かない |
| T-P2-09 | 行 11 | `tags=["sales","hr"]` が `"sales,hr"` として往復する（`IADR-0385`） |
| T-S-01 | 呼び出し先 | s2s で往復（陽性対照） |
| T-S-02 | 呼び出し先 | 資格情報無し → `UNAUTHENTICATED` |
| T-S-03 | 呼び出し先 | 🔴 **管理者の利用者トークン → `PERMISSION_DENIED`** |
| T-S-04 | 呼び出し先 | REST と gRPC が同じ入力で同じ答え |
| T-S-09 | 呼び出し先 | `CheckUsernames` は要求と同じ順・同じ数 |
| T-S-08 | 呼び出し先 | リフレクションで `ServiceCaller` ポリシー |

## 実測（push 直前に測り直した）

### プロジェクト別の試験数（前 → 後）

**前**は基点 `origin/develop` `32724227` ＋ `git submodule update --init src/ai-stock-trading` 済み。
🔴 submodule を入れないと `Platform.Bff` がコンパイルできず **510 件が黙って消える**ので、
入れた状態を基準にした（`Platform.Bff.Tests` が 510 件あることを確認済み）。

| プロジェクト | 前 | 後 | 差 |
| --- | --- | --- | --- |
| AuthorizationService.Tests | 174 | 185 | **+11** |
| McpServer.Tests | 134 | 146 | **+12** |
| DataSourceService.Tests | 245 | 255 | **+10** |
| GraphService.Tests | 456 | 463 | **+7** |
| AiAnalysisService.Tests | 123 | 129 | **+6** |
| WikiService.Tests | 97 | 102 | **+5** |
| Platform.Shared.Kernel.Tests | 42 | 42 | 0 |
| Platform.Shared.Infrastructure.Tests | 314 | 314 | 0 |
| NotificationService.Tests | 57 | 57 | 0 |
| Platform.Bff.Tests | 510（skip 1） | 510（skip 1） | 0 |
| LlmGateway.Tests | 275 | 275 | 0 |
| Knowledge.Contracts.Tests | 67 | 67 | 0 |
| IngestionService.Tests | 82 | 82 | 0 |
| FeedbackService.Tests | 38 | 38 | 0 |
| RetrievalService.Tests | 197 | 197 | 0 |
| DocumentService.Tests | 320 | 320 | 0 |
| ConversionService.Tests | 168（skip 6） | 168（skip 6） | 0 |
| DashboardService.Tests | 81 | 81 | 0 |
| Knowledge.IntegrationTests | 84（skip 44） | 84（skip 44） | 0 |
| **合計** | **3464** | **3515** | **+51** |

**削除・スキップ増は 0**（スキップは前後とも 51 件＝ 1 ＋ 6 ＋ 44 で同一）。

### 変異検査（実施。詳細は PR 本文）

| 変異 | 赤になった試験 |
| --- | --- |
| `UserDirectoryGrpcService` から `[Authorize(Policy = ServiceCaller)]` を落とす | AuthorizationService 3 件 |
| 呼び出し先 `CheckUsernames` の照合を `Ordinal` → `OrdinalIgnoreCase` | AuthorizationService 1 件 |
| REST 実装 `AuthorizationServiceUserDirectory` の交差を大小文字無視へ | DataSourceService 1 件 |
| `GrpcRegistrarAttributes` を `TryResolveScopeAsync` → `ResolveScopeAsync`（畳む方）へ | McpServer 3 件 |
| Wiki の gRPC 呼び出しを未認証短絡の**前**へ移す | WikiService 2 件 |
| （生存）`PlatformUserDirectorySnapshot.Of` の比較子を `OrdinalIgnoreCase` へ | **0 件**。等価変異である —— 交差は呼び出し元の要求集合（Ordinal）が決めるため、断面側の比較子は結果に効かない |
