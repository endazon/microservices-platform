---
title: east-west gRPC の展開（第 4 スライス）— BFF 自身の east-west 呼び出しのうち利用者の資格情報を運ばない 4 箇所を文書読み取りの gRPC 面へ移し、BFF の service account 未配線を閉じる
type: spec
status: done
related_ids:
  - FR-05
  - FR-06
  - FR-09
  - FR-19
  - NFR-09
  - NFR-16
  - UC-03
  - SC-03
  - SC-05
  - ADR-0002
  - ADR-0004
  - ADR-0029
  - ADR-0032
  - ADR-0036
  - ADR-0054
  - ADR-0056
  - ADR-0065
  - ADR-0070
  - ADR-0075
  - IADR-0009
  - IADR-0012
  - IADR-0041
  - IADR-0044
  - IADR-0045
  - IADR-0253
  - IADR-0290
  - IADR-0316
  - IADR-0343
  - IADR-0379
  - IADR-0388
  - IADR-0397
  - IADR-0400
  - IADR-0401
  - IADR-0402
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md §決定・2026-08-04 追記
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 3・5・6
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md §決定
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md D-07・D-08
  - planning:projects/microservices-platform/07_adr/ADR-0056_existence-hiding-boundary-404-403.md §決定
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md 決定 2
  - planning:projects/microservices-platform/07_adr/ADR-0070_pdf-body-extraction-and-ingest-format-set.md 決定 3
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md §決定
---

# 仕様書: BFF 自身の east-west 呼び出しの gRPC 化（#1255 第 4 スライス・文書読み取り）

前 3 スライス（[[IADR-0397]] / [[IADR-0400]] / [[IADR-0401]]）は**サービス → サービス**を移した。
本スライスは残っていた**BFF → サービス**の側を、呼び出し先ごとに切った最小単位の 1 つ目として移す。

## 起点となる計画書（トレーサビリティ）

- `ADR-0029` §決定: east-west の同期呼び出しは gRPC。例外は対象経路を明記した新しい計画 ADR に限る。
- `ADR-0075` 決定 3・5・6: 一括移行の義務は緩めない／実装 ADR で REST 継続を自認しない／基盤先行は MSP 自身の移行を含む。
- `ADR-0004` §決定・`ADR-0036` D-07/D-08・`ADR-0056`: 文書読み取りの ABAC と存在秘匿。**本スライスで判定の位置を動かさない。**
- `ADR-0065` 決定 2: 操作の実体は `Features/<操作>/`。操作をまたいで共有されるものだけを合成点に置く。
- `ADR-0070` 決定 3（`HasBody`）: 本文なしで完了した文書の区別。**proto3 の既定と逆向きの真偽値**である（後述）。
- `ADR-0032` §決定: BFF は confidential client として Keycloak と通信する。s2s の資格情報はこの client をそのまま使える。

## 母集合の再導出（自分で引いた。issue・設計書の数字は転記していない）

基点 `origin/develop` `a50403ce`。

```console
$ git rev-parse --short HEAD; git rev-parse --is-shallow-repository
a50403ce
false
```

**`--is-shallow-repository` が `false`** なので、以下の走査と `git log` 由来の記述は履歴の打ち切り位置を指していない。
`src/ai-stock-trading` は submodule であり、**走査から除外する**（proto の所有者は呼び出し先＝AST 側で、
本リポジトリからは著述できない）。**ただし `git submodule update --init` は先に行った** ——
未初期化のままだと `Platform.Bff` が compile に失敗し、`Platform.Bff.Tests` の 510 件が母集合から静かに消える。

### 軸 1: BFF の名前付き HTTP クライアント（呼び出し先の列挙）

```console
$ grep -c "AddHttpClient" src/platform/backend/Bff/Platform.Bff/Program.cs
16
$ grep -cE 'AddHttpClient\("' src/platform/backend/Bff/Platform.Bff/Program.cs
15
```

16 − 15 = 1 は `builder.Services.AddHttpClient();`（無名。introspection と OIDC 背面が使う既定のファクトリ）。
**名前付きは 15 本**で、内訳は AiAnalysisService / FeedbackService / DashboardService / AuthorizationService /
RetrievalService / GraphService / McpServer / NotificationService / WikiService / DocumentService /
ConversionService / DataSourceService（＝MSP 所有 **12**）と ConfigurationService / RiskManagementService /
MarketMonitorService（＝AST 所有 **3**）である。

### 軸 2: 呼び出し箇所（軸 1 は「登録」しか数えないので、実際に呼ぶ場所を別に引く）

```console
$ grep -rn "CreateClient(" src/knowledge/backend/Bff/Knowledge.Bff.Endpoints \
    src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints --include=*.cs | wc -l
28
```

### 軸 3: 利用者の資格情報を後段へ運ぶ箇所（本スライスの判定軸）

```console
$ grep -rn 'TryAddWithoutValidation("Authorization"' src/knowledge/backend/Bff/Knowledge.Bff.Endpoints \
    src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints --include=*.cs | wc -l
23
```

🔴 **陽性対照（軸を 1 本で終わらせない。`traceability.repo.md` 規則 5）**:
`Usage/UsageEventDispatcher.cs:72` は同じ `TryAddWithoutValidation("Authorization", …)` だが、
**運ぶ値は `http.Request.Headers.Authorization` ではなく列に載せた `signal.Authorization`** である。
「ファイル内に `http.Request.Headers.Authorization` があるか」という軸で引くと**この 1 件を落とし**、
利用者主体で絞られる経路（[[IADR-0343]] 決定 2。受け口が `HttpContext.User` から主体を解決する）を
「資格情報を運ばない」と誤判定する。したがって判定は**送る側の行**で引く。

### 軸 4: 資格情報を運ばない箇所（28 − 23 = 5）

```console
$ (軸 2 と軸 3 の差をファイル単位で突き合わせた結果)
src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs:71   GET /documents/{id}/versions
src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs:100  GET /documents/{id}/versions/{version}
src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs:277  GET /documents/{id}          （FetchAuthorizedAsync）
src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs:301  GET /documents               （FetchListAsync）
src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/SearchBffEndpoints.cs:143    POST /search/attribute-values（RetrievalService）
```

**陰性対照**: 同じ `DocumentBffEndpoints.cs` の `Forwarding()`（`:233-236`）と
同じ `SearchBffEndpoints.cs` の `:57`（`/search`）・`:188`（`/tags`）は資格情報を運ぶ。
**同一ファイルの中で運ぶ箇所と運ばない箇所が同居している**ので、母集合はファイル単位では引けない。

### 軸 5: 呼び出し先の門（「運ばない」が偶然ではないことの確認）

```console
$ sed -n '31,34p' src/knowledge/backend/Services/DocumentService/Features/Documents/DocumentEndpoints.cs
        // 読み取り（一覧・個別・版）は一般利用者の文書閲覧（SC-03）のためロールで塞がない。
        // 読み取りの機密制御は取得段の ABAC（IADR-0012）が担う。
        var g = app.MapGroup("/documents").WithTags("Documents");
```

読み取り 4 口が属する group `g` には **`RequireAuthorization()` が無い**（書き込みの `write` 群・
本文投入の `bodyIntake` 群・タグ反映の `tagReflection` 群には在る）。すなわち
**この 4 口は現状 s2s 相当で開いており、gRPC 面（`ServiceCaller` 必須）へ移すのは権限が狭まる向き**である
（`AuthzScope/Resolve` を移したとき（[[IADR-0401]] 決定 1）と同じ向き）。
ABAC の実施点は BFF 側の `BffScopeResolver` ＋ `IsManageable` のままで、**1 文字も動かさない**（[[IADR-0041]] / [[IADR-0045]]）。

### 軸 6: 現行の proto と realm の主体

```console
$ git ls-files "*.proto"
src/platform/backend/Shared/Platform.Shared.Contracts/Protos/platform/authz/v1/authz_scope.proto
src/platform/backend/Shared/Platform.Shared.Contracts/Protos/platform/authz/v1/user_directory.proto
src/platform/backend/Shared/Platform.Shared.Contracts/Protos/platform/llmgateway/v1/completion.proto
src/platform/backend/Shared/Platform.Shared.Contracts/Protos/platform/llmgateway/v1/embedding.proto

$ grep -n '"username": "service-account-' deploy/keycloak/microservices-platform-realm.json
（abac-seeder / identity-admin / ai-stock-trading-kb-writer / aianalysis-service / graph-service /
  conversion-service / retrieval-service / ingestion-service / wiki-service / datasource-service /
  mcp-server の 11 件。**`service-account-bff` は無い**）
```

🔴 **knowledge ユニットの proto は 0 件**（陰性）。**陽性対照は platform 側の 4 件**であり、走査は生きている。
🔴 **`service-account-bff` は realm の `users[]` に無い。** `bff` client は `serviceAccountsEnabled: true` だが、
service account に `platform-service` を割り当てる `users[]` 項目が無いため、
**[[IADR-0379]] 決定 4 の散文「realm に service account と `platform-service` を付けた」（同 IADR 127 行目）は事実に反する。**
参照実装（BFF → 認可）が配備上 1 度も走っていないのはこれが理由の 1 つである
（`docs/api/east-west-grpc.md` §未決事項が同じことを記録している）。**BFF を呼び出し元にする本スライスで閉じる。**

### 除外とその理由（PR 本文にも書く）

| 除外するもの | 理由 |
| --- | --- |
| 資格情報を運ぶ **23 箇所** | 呼び出し先が**利用者の権限で判定する**（ホップごと ABAC・主体絞り・後段の `AdminOnly` 二重ゲート）。[[IADR-0379]] 決定 4 が禁じる「利用者トークンをメタデータへ載せる」以外に運ぶ手が無い。[[IADR-0401]] 決定 2 の「読み口を狭める」は**利用者の権限を使わずに済む場合の解**であり、ここには当てはまらない ——**token exchange の候補**である |
| AST 所有の **3 呼び出し先**（ConfigurationService / RiskManagementService / MarketMonitorService） | proto の所有者は**呼び出し先**（`ADR-0029`。[[IADR-0379]] 決定 1）。`src/ai-stock-trading` は submodule であり本リポジトリから著述できない。**AST 所有の proto を MSP の共有契約へ置いてはならない** —— 置くと所有者と置き場が食い違い、`check-proto-contracts.js` R1 の「所有サービスのユニットの共有契約プロジェクト」が意味を失う |
| introspection の収集（`HttpEffectiveConfigCollector`） | east-west ∧ 同期 ∧ 応答を待つ、なので**候補ではある**。しかし呼び出し先集合は**構成（`Introspection:Services:*`）で開いており AST 所有のサービスを含む**（helm の `bff.extraEnv` に 13 件）。**一部だけ移すと収集器が 2 つの輸送を話し、`UnreachableServices`（＝「適用漏れと到達不能の区別」。FR-15）が 2 つの意味を持つ。** 全申告元を一度に移すスライスに属し、**そのスライスは本リポジトリだけでは完結しない**。移さない |
| BFF 自身のスコープ解決（`BffScopeResolver`） | 参照実装で既に gRPC 面を持つ（`Services:AuthorizationServiceGrpc` が在るときだけ）。前 3 スライスが意図的に触っていない。**本スライスも触らない**（宛先の配線も足さない。資格情報の欠落だけを閉じる） |
| `SearchBffEndpoints.cs:143`（→ RetrievalService `/search/attribute-values`） | 資格情報を運ばない 5 件目だが**呼び出し先が違う**（RetrievalService。proto も gRPC 面も別）。しかも**同じ named client の兄弟（`/search`）は運ぶ**（二段検索のホップごと ABAC。`ADR-0034` 方式 A）ので、Retrieval の面は「利用者の権限で動く呼び出し先」の未決（token exchange）と**同じ PR で決めるべき**である。呼び出し先ごとに切る最小単位として**次のスライスへ送る** |

## 対象範囲

### 対象

1. proto `knowledge/document/v1/document_read.proto`（新設。`Knowledge.Contracts`）—— service `DocumentRead`、rpc 4 本。
2. DocumentService に gRPC 面を足す。**REST と gRPC が同じ本体を通る**よう `DocumentReadUseCase` を括り出す（評価器を 2 つにしない）。
3. `Knowledge.Bff.Endpoints` に呼び出し側 `DocumentReadGrpcClient` ＋ `AddDocumentReadGrpcClient`。
   切替は `Services:DocumentServiceGrpc`。**未設定なら何も登録せず REST のまま**（並走中の正は REST）。
4. `DocumentBffEndpoints` の 4 箇所を、登録が在れば gRPC・無ければ REST へ振る。**縮退の枝は 1 つも増やさない。**
5. 🔴 **BFF の service account の未配線を閉じる**（realm `users[]` ＋ helm `serviceToken` ＋ compose の `ServiceToken__*`）。
6. 配備: DocumentService に h2c ポート（helm `grpcPort` / compose `Grpc__Port`）、BFF に `Services__DocumentServiceGrpc`。

### 対象外

上の「除外とその理由」の表のとおり。**REST は残す**（並走。撤去は REST 撤去の段の IADR）。

## 🔴 本スライスの載る決定（弱めてはならない）

1. **ABAC の実施点を動かさない。** `BffScopeResolver.ResolveAsync` → `IsManageable`（`Matches` ∧ `!IsPrivateNote`）は
   BFF に残る。gRPC 面は**判定を持たない**（[[IADR-0041]] が定めた唯一の実施点を 2 つにしない）。
   個人資料の構造的除外（`doc_scope=private-note`。`ADR-0036` D-08 / `ADR-0054`）も BFF 側のまま。
2. **書き込みプリフライト（`ForwardIfInScope` → `FetchAuthorizedAsync`）は残す**（[[IADR-0045]]）。
   本スライスはその中の**取得**だけを gRPC へ差し替える。往復は減らさない。
3. **`ServiceCaller` を要求する。** 利用者のトークンは（管理者であっても）通らない。機械で守るのは
   「管理者の利用者トークンでも `PERMISSION_DENIED`」の 1 本である。
4. 🔴 **proto3 に null は無い。** `HasBody` は DTO の既定が **`true`**、proto3 の既定は **`false`** で**向きが逆**である
   （[[IADR-0400]] が `sent` で踏んだのと同型・同じ静かな壊れ方 —— 全文書が「本文なし」に見え、SC-03 が
   本文の位置に「本文なし（原本を参照）」を出す）。サーバは**常に明示的に**書く。
   `MarkdownUri` / `ChangeNote` は `null` と `""` が**画面で区別される**（前者は「(未設定)」の縮退文言、
   後者は空の変更メモ）ので `optional`（proto3 の field presence）で運ぶ。
5. **「無い」と「引けなかった」を分ける。** 無いのは応答（`found=false`）、引けなかったのは gRPC status。
   呼び出し側は**どちらも** `null`（→ 404 秘匿 / 空一覧）へ倒す —— これは**現行の REST 実装と同じ枝**である
   （`FetchAuthorizedAsync` は非 2xx も不達も `null`、`FetchListAsync` は不達を `[]`）。**新しい枝を作らない。**

## 受け入れ基準

- [x] `Services:DocumentServiceGrpc` が未設定のとき、BFF は 1 バイトも挙動が変わらない（REST が正）。
- [x] 設定されたとき、4 箇所が gRPC 経路になり、REST と**同じ応答**を返す（同値試験）。
- [x] gRPC 面は `ServiceCaller` を要求し、資格情報なしは `UNAUTHENTICATED`、**管理者の利用者トークンは `PERMISSION_DENIED`**。
- [x] `HasBody=false` の文書が gRPC 経路でも `false` のまま届く（proto3 既定の再適用）。
- [x] `MarkdownUri=null` と `MarkdownUri=""` が gRPC 経路で区別される。
- [x] REST の 4 口は `DocumentReadUseCase` を通り、**判定器は 1 つ**（gRPC 面は同じ関数を呼ぶ）。
- [x] `check-proto-contracts.js` が非破壊の file 追加 1 件として通り、baseline が更新されている。
- [x] realm に `service-account-bff`（`platform-service`）が在り、helm・compose に BFF の `ServiceToken__*` が在る。
- [x] 試験は 1 本も削除・skip していない（前後の実測を PR 本文に載せる）。

## 試験計画

呼び出し先（DocumentService。`GrpcResolveScopeTests` / `GrpcUserDirectoryTests` と同型・実 Kestrel）:

- **T-01** 陽性: s2s トークンで 4 rpc が往復する。
- **T-02** 資格情報なし → `UNAUTHENTICATED`。
- **T-03** 🔴 管理者（`platform-admin`）の利用者トークン → `PERMISSION_DENIED`。
- **T-04** REST と gRPC が同じ入力で同じ答え（一覧・個別・版一覧・版取得の 4 口）。
- **T-05** `HasBody=false` が `false` のまま届く（proto3 既定の再適用）。
- **T-06** `MarkdownUri` / `ChangeNote` の `null` と `""` が区別される。
- **T-07** 不在は `found=false` の**応答**であり `NOT_FOUND` ではない。
- **T-08** リフレクションで gRPC サービス型に `[Authorize(Policy = ServiceCaller)]` が在る。
- **T-09** h2c を有効にしても HTTP/1.1 側（REST・`/health/*`）が残る。

呼び出し元（BFF）:

- **T-10** 既定（`Services:DocumentServiceGrpc` 未設定）で gRPC クライアントが DI に**登録されない**。
- **T-11** 設定すると登録され、写像が REST と同値。
- **T-12** 縮退: 引けなかった（`RpcException` / s2s トークン取得失敗）→ 一覧は `[]`、個別は `null`（→ 404）。

## 実測（push 直前に測り直した）

### プロジェクト別の試験数（前 → 後）

| プロジェクト | 前 | 後 | 差 |
| --- | --- | --- | --- |
| DocumentService.Tests | 369 | 380 | +11 |
| Platform.Bff.Tests | 510（うち skip 1） | 521（うち skip 1） | +11 |
| Knowledge.Contracts.Tests | 67 | 73 | +6 |
| その他（両 slnx の残り 16 プロジェクト） | 変化なし | 変化なし | 0 |

合計 **+28**。**削除・skip 化した試験は 0 本。**
（既存の skip は `Platform.Bff.Tests` の 1 件（往復レイテンシのベンチマーク）と knowledge 側の 50 件
（integration 44 / conversion 6）で、いずれも本 PR の前から skip であり触っていない。）

🔴 **`git submodule update --init src/ai-stock-trading` を先に行った。** 未初期化のままだと
`Platform.Bff` が compile に失敗し、`Platform.Bff.Tests` の 510 件が baseline から静かに消える。

### 変異検査（実施。4 変異とも意図した試験だけが赤になった）

| 変異 | 赤になった試験 |
| --- | --- |
| `ToProto` の `HasBody` 明示代入を落とす（proto3 の既定 false に任せる） | 写像 2 本（`HasBody_true_survives` / `Document_round_trips_every_field`）＋ 実 Kestrel 2 本（`HasBody_survives_the_wire_in_both_directions` / `Rest_and_grpc_report_the_same_documents`） |
| `optional` の presence を無視し `?? ""` で常に代入する | 写像 2 本（`MarkdownUri_null_and_empty_are_distinguishable` / `ChangeNote_null_and_empty_are_distinguishable`）＋ 実 Kestrel 2 本（`MarkdownUri_null_is_distinguishable_from_empty` / `Rest_and_grpc_report_the_same_documents`） |
| `[Authorize(Policy = ServiceCaller)]` を `[Authorize]` へ緩める | `DocumentRead_with_forwarded_admin_user_token_is_permission_denied` / `Grpc_service_declares_service_caller_policy` |
| 版履歴の `RpcException` を捕捉して `[]` へ畳む | `Versions_over_grpc_do_not_hide_a_failure_as_an_empty_history` |
