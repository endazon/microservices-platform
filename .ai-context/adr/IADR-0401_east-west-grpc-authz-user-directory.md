---
title: IADR-0401 east-west gRPC の第 3 スライス（AuthorizationService）— 利用者トークンの転送は「転送」ではなく呼び出し先の読み口を狭めて置き換え、名簿の列挙をサービス間の面へ出さない
type: impl-adr
status: Proposed
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
  - ADR-0032
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
---

# IADR-0401: east-west gRPC の第 3 スライス — AuthorizationService の 5 呼び出し元（#1255）

- 状態: Proposed
- 日付: 2026-09-06
- 決定者: claude（実装）

## 起点・関連

- 計画: `ADR-0029` §決定（east-west 同期は gRPC。例外は**対象経路を明記した新 ADR**に限る）／
  `ADR-0075` 決定 3（一括移行の義務は緩めない）・**決定 5（実装側 IADR で REST 継続を自認しない）**・
  決定 6（基盤先行は MSP 自身の移行を含む）／`ADR-0004`（ABAC）／
  `ADR-0062` 決定 2・3（無人アカウントの属性は登録者の部分集合。**判定は後段が行う**）／
  `ADR-0074` 決定 1・4（写像先の実在検証。**課すのは「実在」であって「有効」ではない**）／
  `ADR-0064` 決定 4（SC-17 の後段は認可サービス）／`ADR-0034` 決定 8・`ADR-0036` D-07（閲覧と書き込みは別のアクション）／
  `ADR-0011`（Wiki エンジン）／`ADR-0032`（BFF セッション）
- 実装 ADR: `IADR-0379`（先行条件の 4 決定。**本 IADR はこれを変えない**）／
  `IADR-0397`・`IADR-0400`（第 1・第 2 スライス。**本 IADR はその鎖を延ばす**）／
  `IADR-0009`（存在秘匿）／`IADR-0253`（分岐つきスコープ）／`IADR-0272` 決定 4（action は既定値の無い必須引数）／
  `IADR-0299`（内周の残余リスクの受容）／`IADR-0316`（Secret 注入の宣言と配備の突合）／
  `IADR-0329` 決定 1（`view-users` を持つ主体は 1 つ）／`IADR-0335`（Wiki の未認証短絡）／
  `IADR-0378`（内周の境界）／`IADR-0384` 決定 1（`ReadAssignableConfidentiality` に読み方を閉じる）／
  `IADR-0385` 決定 2（集合値属性のカンマ連結）
- 実装ガイド（人が読む正）: `docs/api/east-west-grpc.md`（本 IADR で 4 つ目の面を追記した）
- 作業仕様書: `.ai-context/specs/20260906_issue-1255_east-west-grpc-authz.md`
- issue: #1255（本 PR は第 3 スライス。第 1 スライスは #1290、第 2 スライスは #1295）

## コンテキストと課題

第 1・第 2 スライスは LlmGateway を移した。どちらの経路も**利用者の文脈を持たない**（REST の本文にも無い）。
本スライスは AuthorizationService を呼ぶ **5 呼び出し元 / 6 呼び出し箇所**を移す ——
すなわち利用者の文脈を**本文で**運ぶ経路であり、`docs/api/east-west-grpc.md` §4 の中心である。

実測（`origin/develop` `32724227`。`git rev-parse --is-shallow-repository` = `false`）:

| 集合 | 件数 | 内訳 |
| --- | --- | --- |
| 呼び出し元 | **5 サービス / 6 箇所** | AiAnalysis・Graph・Wiki（`/authz/scope` ×3）／DataSource（`/authz/users`）／McpServer（`/authz/users` ＋ `/authz/scope`） |
| **利用者トークンを認可サービスへ転送している箇所** | **2** | `AuthorizationServiceUserDirectory.cs:37`／`AuthorizationServiceRegistrarAttributes.cs:66` |
| 除外 | 2 | `BffScopeResolver`（#1201 で gRPC 済み）／`UserAdminBffEndpoints`（BFF の側。別 PR） |
| `platform-service` を持つ主体 | **5** | #1290 の 2 つと #1295 の 3 つ。🔴 **`bff` は `users[]` に無く、持っていない** |

固有の論点は 1 つだけである ——
**2 呼び出し元が利用者の `Authorization` を後段へ転送しており、コード注記がその理由まで書いている。**
`docs/api/east-west-grpc.md` §4 の 🔴「利用者トークンはメタデータへ載せない（confused deputy）」と
正面から衝突する。

## 決定

### 決定 1: `/authz/scope` の 3 呼び出し元（AiAnalysis / Graph / Wiki）は既存の `AuthzScope/Resolve` を使う。proto の追加は 0

3 者とも本文は `AccessScopeRequest(userId, userAttrs[, action])` であり、`authz_scope.proto` の
`ResolveScopeRequest{user_id, user_attributes, action}` と 1 対 1 である。**移行は本文を変えない
トランスポートの差し替え**であり、`AuthzScopeGrpcClient` に契約 DTO を返す多重定義
（`ResolveScopeAsync`）を**足すだけ**でよい（既存の `ResolveAsync → BffAccessScope?` は 1 文字も変えない）。

権限は**現状より強くなる向き**である: REST の `/authz/scope` は認可を掛けていない（サービス間呼び出しの
ためと注記がある）のに対し、gRPC は `ServiceCaller` を要求する。

🔴 **Wiki の未認証短絡は輸送の手前にある**（`IADR-0335` / #1126）。gRPC でも**匿名では 1 度も呼ばない**。
短絡の後ろへ滑り込むと「未認証時の応答がポリシーの内容次第で変わる」という #1126 の欠陥が再発する ——
これは呼び出し回数 0 の表明でしか守れない。

🔴 **`action` は既定へ丸めない**（`IADR-0272` 決定 4）。Graph は呼び出し元が明示した値をそのまま送り、
AiAnalysis / Wiki は REST が既定引数に頼っている `read` を**明示して**送る（既定への依存を輸送ごとに隠さない）。

### 決定 2（本丸）: 利用者トークンを転送していた 2 呼び出し元は、**転送ではなく呼び出し先の読み口を狭めること**で移す

新 rpc は `platform.authz.v1.UserDirectory/{CheckUsernames, GetUserAttributes}`（`ServiceCaller`）である。
**列挙（`GET /authz/users` に相当）と書き込みは s2s の面へ出さない。**

根拠となる実測（基点コミットで再検証した）:

- `/authz/users` の門は `AdminOnly` のグループ（`UserAdminEndpoints.cs:35-37`）であり、
  **ハンドラは主体を一切読まない**（`ListUsers/Endpoint.cs` は `HttpContext` を取らず全員を列挙する）。
  すなわち転送されたトークンは「**列挙してよいか**」の門にだけ使われ、「**誰の**」には使われていない。
- DataSource が要るのは「これらの利用者名は実在するか」だけである（結果は名前の集合へ落ちる）。
- McpServer が要るのは「認証済みの登録者**自身** 1 人の属性」だけである。
- 人の側の門は**呼び出し元の端点にも**ある —— DataSource の Create / Update / Patch / Disable と
  McpServer の `/mcp-clients` はいずれも `AdminOnly` である。

したがって、コード注記が守ろうとした線（「SC-06 / SC-12 を触れない主体が名簿を引ける経路を作らない」）は
**列挙を出さないこと**で満たされる。サービス専用の資格情報を新設しても、その主体は名簿を引けない。

🔴 **残余リスク（受容する。隠さない）**: `platform-service` を持つサービスは
「名指しした 1 人の**真の**属性」を読める。これは `AuthzScope/Resolve` が呼び出し元の**主張する**
`user_attributes` をそのまま評価に使うのと同じ信頼であり（偽の属性を主張できる方が強い）、
境界も `IADR-0299` / `IADR-0378` 内周と同型（ClusterIP ＋ NetworkPolicy ＋ STRICT mTLS）である。

🔴 **token exchange（RFC 8693）は採らない。** Keycloak は `24.0`（compose）で token exchange は
preview 機能であり、要件は「読む」だけで**利用者の権限を使わずに**満たせる。
ガイド §4「将来」が置いた条件（呼び出し先が利用者自身の権限で動く必要が出たら）に当たらない。

🔴 **この決定が退けられるなら、この 2 経路は計画側の裁定無しには移せない。**
必要なのは「token exchange を採る計画 ADR」か「対象経路を明記した REST 例外 ADR」のどちらかであり、
**実装側 IADR では決められない**（`ADR-0075` 決定 5）。

### 決定 3: `IPlatformUserDirectory` の口を「列挙」から「照会」へ改める

`ListUsernamesAsync(ct)` → `LookupAsync(IReadOnlySet<string> usernames, ct)`。
返すのは**要求した名前のうち実在する部分集合**である。

- REST 実装は列挙して交差する（**挙動は変わらない**。`ValidateTargetsExist` は要求した名前しか判定に使わない）。
- gRPC 実装は `CheckUsernames` を呼ぶ。

**口を問いの形へ狭めたから、呼び出し先も狭いままでよい。** 逆順（呼び出し先を先に決める）にすると、
「呼び出し元が列挙を求めているから列挙を出す」という形になり、決定 2 が成立しない。

🔴 **照合規則は現行のまま写す。** `CheckUsernames` は**序数一致**（DataSource の現行）、
`GetUserAttributes` は**大小文字無視**（McpServer の現行）である。両者の不一致は既存のものであり、
揃えるのは移行の不変条件（挙動を変えない）を破るので別 issue とする。
🔴 **無効化された利用者も実在として数える**（`ADR-0074` 決定 4 が課すのは「実在」であって「有効」ではない）。

### 決定 4: `ReadAssignableConfidentiality` は **1 文字も変えずに**共通 static へ括り出す

`IADR-0384` 決定 1 は「読み方を 1 か所へ閉じる」と定めた。#1255 で輸送が 2 つ（REST と gRPC）に
なったため、両実装が呼べる位置（`RegistrarScopeReading`）へ**本文を一字も変えずに移した**。

🔴 **写しを 2 つ作らない。** 片方だけが fail-open へ戻る事故（#1242）が輸送ごとに再現し得る。
既存の 19 件（`AuthorizationServiceRegistrarAttributesTests`）は**両輸送を通す**ヘルパへ書き換えたので、
1 本追加するたびに両実装が覆われる。

### 決定 5: 「引けなかった」を deny へ畳むかは**呼び出し元が決める**。共有クライアントは事実だけを返す

`AuthzScopeGrpcClient` は 2 つの口を持つ:

| 口 | 輸送の失敗 | 使う呼び出し元 |
| --- | --- | --- |
| `ResolveScopeAsync` | `Granted=false` へ**畳む** | AiAnalysis / Graph / Wiki（REST 実装が非 2xx・不達を deny へ倒しているのと同じ枝） |
| `TryResolveScopeAsync` | `null`（引けなかった） | McpServer（「配れるものが無い」と「引けなかった」で**返す型が違う**） |

🔴 **McpServer で畳むと緩む。** 認可サービスの障害中に `Available=true`・`Clearance` 空・
**しかしタグは配れる**という、REST 実装には無い挙動になる（REST は `Unavailable` で何も配らない）。
畳む／畳まないは輸送ではなく**呼び出し元の縮退契約**が決める。

同じ形が名簿にもある —— 「居ない」は**応答**（`exists=false` / `found=false`）、
「引けなかった」は **gRPC status**（呼び出し側では `null`）。混ぜると認可サービスの障害が
「その利用者は存在しません」という嘘の理由になる（DataSource は 502 と 400 で分ける）。

### 決定 6: 切替は `Services:AuthorizationServiceGrpc` の有無。並走中の正は REST。**1 サービス 1 主体**

各呼び出し元の `Program.cs` が構成の有無で実装を選ぶ。**戻すのは構成を外すだけ**（コードは変えない）。

🔴 **realm の主体は呼び出し元サービスごとに 1 つである**（呼び出し先ごとに割らない）。
AiAnalysis と Graph は #1295 が作った `aianalysis-service` / `graph-service` を**再利用**し、
本 PR で新設するのは `wiki-service` / `datasource-service` / `mcp-server` の 3 つだけである。

チャネルも 1 本に保つ —— 認可サービス宛は `AddAuthzScopeGrpcClient` と `AddUserDirectoryGrpcClient` の
両方から登録され得る（McpServer は 2 つとも要る）ので `TryAddSingleton` で先着 1 つに固定する。
LlmGateway 側は**キー付き**で登録されているので衝突しない（`IADR-0397` が既に分けてある）。

## 理由

- 決定 2 が本 PR の全体である。「利用者トークンを載せない」を守りながら権限を広げない形は
  **読み口を狭めること**しかない —— 列挙をそのまま s2s へ開けると `platform-service` を持つ全サービスが
  名簿を引け、本文にロールを載せると呼び出し先が**検証できない主張**で門を開ける。
- 決定 3 を独立させたのは、**口の形が呼び出し先の広さを決める**からである。
  ポートが「列挙」を求め続ける限り、呼び出し先の rpc も列挙になる。
- 決定 5 が最も事故りやすい。**同じ #1255 の中で、3 呼び出し元は畳み、1 呼び出し元は畳まない。**
  分ける基準は「REST の現行がどうしているか」ただ 1 つである（`IADR-0400` 決定 5 と同じ向き）。

## 結果

- 良い影響:
  - proto 1 本（`user_directory.proto`）＋ 既存 `authz_scope.proto` の再利用で、
    5 呼び出し元 6 箇所が gRPC で呼べる。認可サービスの gRPC 面は**スコープと名簿の 2 面**になった。
  - 🔴 **利用者トークンを east-west へ転送する経路が 2 本減った**（残る 4 本は §移していないもの）。
  - `AuthorizationServiceUserDirectory` に**初めて**単体試験が付いた（従前 0 本。#1194 以来）。
  - 既存の 19 件（McpServer）が**両輸送を通る**ようになった（ヘルパ 1 か所の写しで覆う）。
- 悪い影響・トレードオフ:
  - Keycloak の confidential client が 3 つ増え、Secret 注入が 3 サービスに要る（helm / compose /
    ExternalSecret / Vault seed の 4 経路）。
  - 🔴 **残余リスク**: `platform-service` を持つサービスは名指しした 1 人の属性を読める（決定 2 で受容）。
  - 🔴 照合規則の不一致（DataSource は序数一致・McpServer は大小文字無視）を**そのまま**
    呼び出し先の 2 rpc へ写した。揃えるのは別 issue。
  - `UserDirectory` は当面 `IIdentityAdminClient.ListUsersAsync` の上で絞る（by-username の口が
    ポートに無い）。**挙動は呼び出し元側フィルタと同じ**であり、最適化は別 issue。
  - 🔴 稼働クラスタでの h2c 往復は依然として**未実測**（#1255 やること 7。`IADR-0397` / `IADR-0400` と同じ）。
- 移していないもの（作業仕様書 §対象範囲・対象外が正）:
  BFF の 15 本の REST 呼び出し（`UserAdminBffEndpoints` を含む）／introspection 収集／
  `Document → Notification`／`Graph → Dashboard`／`McpServer HttpToolInvoker`／AST の各サービス／
  REST の並走（`IADR-0379` 決定 5）／BFF 自身の gRPC 配線（#1290 / #1295 が意図して残した）。
  🔴 **AiAnalysis → Retrieval `/search`・Graph → Document・Retrieval → Graph の利用者トークン転送 3 箇所は
  決定 2 では解けない** —— 呼び出し先が**利用者の権限で動く**（ホップごと ABAC。`ADR-0034` 方式 A）ためであり、
  ガイド §4「将来」が token exchange の条件として置いた形そのものである。
- フォローアップ:
  1. 🔴 上の 3 箇所は knowledge 所有の proto の PR で、token exchange の初の実需として計画へ裁定を依頼する。
  2. 利用者名の照合規則の統一（別 issue。移行の不変条件と分ける）。
  3. `IIdentityAdminClient` に by-username の口を足す最適化（`view-users` で `GET /users?username=&exact=true`）。
  4. BFF の 15 本と、参照実装（BFF → 認可）の配備上の未配線。

## 関連

- Supersedes: なし（`IADR-0379` の 4 決定・`IADR-0397` / `IADR-0400` の決定はいずれも不変。本 IADR はその適用と拡張）
- Superseded by: なし
