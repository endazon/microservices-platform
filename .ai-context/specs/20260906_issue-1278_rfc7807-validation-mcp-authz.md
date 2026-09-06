---
title: RFC7807（Results.ValidationProblem）系の手書き検証を FluentValidation へ移す —— PR-C（McpServer ＋ AuthorizationService。鍵は sink が持つ変種）
type: spec
status: done
related_ids:
  - FR-05
  - FR-16
  - FR-21
  - UC-05
  - UC-09
  - SC-12
  - ADR-0004
  - ADR-0024
  - ADR-0030
  - ADR-0036
  - ADR-0041
  - ADR-0062
  - ADR-0065
  - ADR-0068
  - IADR-0229
  - IADR-0371
  - IADR-0393
  - IADR-0395
  - IADR-0398
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md (Accepted) ABAC の判定・スコープ解決
  - planning:projects/microservices-platform/07_adr/ADR-0024_mcp-server-integration.md (Accepted) MCP クライアント登録管理
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (Accepted 2026-07-25) 決定（検証 = FluentValidation）
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md (Accepted) D-07（write の認可）
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md (Accepted 2026-08-22) 決定 2・3
  - planning:projects/microservices-platform/07_adr/ADR-0062_unattended-account-attribute-subset.md (Accepted 2026-09-05) 決定 2・3・結果
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md 決定 2
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md 決定 1
---

# 仕様書: #1278 PR-C —— McpServer ＋ AuthorizationService の RFC7807 系ガード節を FluentValidation へ移す

> 本仕様書は #1278（親 #1248 / #1230 / #1064。環流 planning#490）の 4 分割のうち **PR-C** を対象とする。
> 契約は PR-A が `IADR-0398` として確定させ、PR-B が適用済みである。**本 PR もその適用であり、新しい判断は無い。**
> `IADR-0398` 決定 10 の表が本 PR の射程（2 検証器 / 4 ガード）を定めている。
> PR-A / PR-B と違うのは **鍵の出どころ**である —— `IADR-0398` 決定 1 (b) の「サービスの sink が鍵を持つ」側である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-16（MCP サーバー連携 —— クライアント登録管理）/ FR-05（属性ベースアクセス制御）/ FR-21（所有・共有による裁量アクセス。write の認可スコープ）
- ユースケース（UC）: UC-09（MCP クライアントの登録・管理）/ UC-05（アクセス制御の設定・評価）
- 画面（SC）: SC-12（MCP クライアント登録管理。**管理者限定**）
- 関連 ADR: ADR-0004（Keycloak ＋ ABAC）/ ADR-0024（MCP サーバーの追加）/ ADR-0030（Application 層のライブラリ選定 —— 検証は FluentValidation）/ ADR-0036 D-07（所有・共有と write）/ ADR-0041（Result 型の外部ライブラリ。参照の向き）/ ADR-0062（無人アカウントの属性部分集合。決定 2・3 と §結果「拒否理由を丸めない」）/ ADR-0065 決定 2（単一プロジェクト VSA）/ ADR-0068 決定 1（3 段のスライス分割規則）
- 実装 ADR: **IADR-0398（PR-A が確定させた契約。本 PR はその適用）**/ IADR-0371 決定 2（`Errors[0]`・宣言順が契約・明示登録）/ IADR-0393（波 1）/ IADR-0395 決定 2・3・6・8（実行位置・DI 鍵・移送の物差し・述語の粒度）/ IADR-0229 決定 1（`Error` は `Message` を 1 つだけ持つ）
- 計画書リンク: `../project-planning/projects/microservices-platform/07_adr/`

## 目的・背景

`IADR-0398` 決定 10 の PR-C。McpServer と AuthorizationService に残る RFC7807
（`Results.ValidationProblem`）系の手書き入力検証ガード節のうち、**端点入口の入力検証に当たる 4 本**を
`AbstractValidator` へ移す。**応答本文は 1 バイトも変えない。**

### 🔴 着手前に自分で引き直した母集合（PR-A / PR-B / 設計書の数えを転記しない）

基点は `origin/develop` @ **`a50403ce`**。`git rev-parse --is-shallow-repository` = **`false`**
—— 履歴が打ち切られていないので `git log` を出典に使える。

🔴 **`src/ai-stock-trading`（submodule）を走査から外す。** 初期化した状態で走査すると
`Results.BadRequest` の非テスト行が 18 → **46** に化ける（submodule 側の実コードを数えてしまう）。
**母集合は本リポジトリの追跡ファイルだけである。** 以下の数値はすべて submodule 除外である。

**軸 1**（`grep -rn "ValidationProblem" src --include=*.cs | grep -v Tests`、submodule 除外）: **48 行**。
本 PR の 2 サービスに属するのは **15 行**で、内訳は**コメント 2**（`McpClientEndpoints.cs:53` /
`UserAdminEndpoints.cs:28`）・**sink の定義 4 行**（`AuthzEndpoints.cs:83-84` の 2 行 ＋
`UserAdminEndpoints.cs:56-57` の 2 行）・**sink の実体 1 行**（`McpClientEndpoints.cs:85`）・
**呼び出し 8 行**（Authz の 8 サイト）である。

**軸 2**（McpServer の私有 sink。軸 1 では `Results.` が付かないので落ちる。
`grep -rn "Problem(" src/platform/backend/Services/McpServer --include=*.cs | grep -v Tests`）: **9 行**。
うち**定義 3 行**（`:81` / `:84` / `:85`）を除いた**呼び出しが 6 行**（`:69` / `:78` /
`RegisterClient/Endpoint.cs:18` / `:20` / `:23` / `:35`）。

**軸 3**（400 以外の器）: 本 PR の 2 サービスに `Results.BadRequest` は 0 件。
`Authz/DeleteAttribute/Endpoint.cs:26` の `Results.Problem`（参照中の属性の削除拒否）は 409 系であり母集合外。

**陽性対照**（「無い」を「無い」と読む前に、走査器が生きていることを確かめた）:

| 走査（submodule 除外・非テスト） | ヒット | 意味 |
| --- | ---: | --- |
| `Results.BadRequest` | **18** | 波 2 第 1 弾が「入力検証ではない」として残した箇所が拾える ＝ 走査器は生きている（submodule を入れると 46 に化ける） |
| FluentValidation の `PackageReference` を持つ csproj | **7** | `IADR-0398` §結果「6 → 7」（PR-A で DocumentService が加わった）と一致。本 PR 適用後は **9** になる |
| `AddScoped<IValidator<` の登録行 | **24** | PR-B の結果表「24」と一致。本 PR 適用後は **26** になる |
| `AddValidatorsFromAssembly` の**呼び出し**（`AddValidatorsFromAssembly(` で数える） | **0** | 語で数えると 7 ヒットするが**すべて説明文のコメント行**である。🔴 **語ではなく構文で数える** |
| `return ValidationProblems.FirstViolation(` の呼び出し | **15** | PR-B の結果表「15」と一致 ＝ DocumentService の sink が実在する。**本 PR はこれを使わない**（2 サービスは自前の sink を持つ。決定 1 (b)） |
| `[Fact]` / `[Theory]` の属性行 —— McpServer.Tests | **110** | 試験の母数（`dotnet test` の 146 件は `[Theory]` の展開を含む） |
| 同 —— AuthorizationService.Tests | **173** | 同上（`dotnet test` は 185 件） |

### 🔴 各サイトの「1 件か全件か」を基点で読み直した（依頼文の枠を転記しない）

#1278 は群 3 を一律に「**全違反を返す**」と書き、本 PR の依頼文も
「McpServer の sink はコメントが『理由をすべて返す（先頭 1 件へ丸めない）』と書いているので**形 β**」と枠を与える。
**sink の器が β であることと、そこへ流れ込む各サイトの振る舞いが β であることは別である。**
制御フローを読むと、**移す 4 サイトはすべて形 α（先頭 1 件）である。**

- **`RegisterClient/Endpoint.cs:17-23`（M1–M3）**: 3 本のガードは**違反を見つけたその場で `return` する**。
  `McpClientEndpoints.Problem(string message)`（`:81`）は `Problem([message])` へ委譲するので、
  **sink が受け取る配列は常に要素 1 である**。実測: `clientId` 空 ＋ `kind: "robot"` の要求の応答は
  `{"errors":{"request":["clientId は必須です。"]}}` の **1 鍵 1 件**であり、`kind` の行へ到達しない。
- **`ResolveScope/Endpoint.cs:23-25`（A4）**: `AuthzEndpoints.ValidationProblem([...])` の実引数は
  **1 要素のコレクション式リテラル**である。全件を積むドメイン関数を通っていない。
- **形 β が実在するのは、本 PR が移さない側である。**
  `McpClientEndpoints.cs:69`（M4）は `ToolPublicationConfigValidator.ValidateServiceAccountAttributes` が
  返す `IReadOnlyList<string>`（**個人資料 ＋ 制限プロジェクトで最大 2 件**）を、`:78`（M5）は
  `ServiceAccountAttributeSubset.Validate` が返す配列（**`clearance` ＋ タグで最大 2 件**）を
  そのまま載せる。Authz の 6 サイト（A1 / A3 / A5–A8）も `AbacValidation` /
  `UserAssignmentValidation` の配列をそのまま載せる。
- したがって **sink の「全件を載せる」能力は残す側が使っており、本 PR で丸めてはならない。**
  🔴 移送で `Problem(IReadOnlyList<string>)` を先頭 1 件へ縮めると **M4 / M5 の応答が黙って壊れる**
  —— これは ADR-0062 §結果「拒否理由を丸めない。どの値が外れたかを本文へ載せる」への違反であり、
  本 PR が避けるために存在する欠陥そのものである。**形 β の件数と順序を試験で固定する。**

## 対象範囲

### ガード単位の判定表（基点 `a50403ce` の `path:line`）

物差しは `IADR-0395` 決定 6・7（i 述語が要求 DTO だけで閉じるか／ii 従前のガード節が居た位置で
実行できるか／iii ドメインの方針として意図的に 1 箇所へ束ねられていないか）を再利用する。

| # | サイト | 鍵 | 形 | 位置（直前の判定） | (i) | (ii) | (iii) | 判定 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| M1 | `McpServer/Features/McpClients/RegisterClient/Endpoint.cs:17-18` | `request`（sink） | **α** | 入口（403 の後ろ） | ✅ `req.ClientId` だけ | ✅ | – | **移す** |
| M2 | 同 `:19-21` | `request`（sink） | **α** | 同上 | ✅ `TryParseKind`（`:47-56`） | ✅ | – | **移す**（検証と解析を分ける。決定 5） |
| M3 | 同 `:22-23` | `request`（sink） | **α** | 同上 | ✅ `TryParseTier`（`:58-70`） | ✅ | – | **移す**（同上） |
| M4 | `McpClientEndpoints.cs:68-69`（`RejectUnassignableAsync`） | `request`（sink） | **β**（最大 2 件） | `kind` 分岐（`:64`）の中 | ❌ 呼び出し元 2 つ（登録 / 差し替え）で**要求型が違う** | – | ❌ 登録と差し替えが**同じ 1 関数**（`:48-51`。ADR-0062 決定 3） | **残す** |
| M5 | 同 `:76-78` | `request`（sink） | **β**（最大 2 件） | 同上（`Governs` の後ろ） | ❌ **外部解決**（`registrar.ResolveAsync`） | – | ❌ 同上 | **残す** |
| M6 | `RegisterClient/Endpoint.cs:34-35` | `request`（sink） | α | `RejectUnassignableAsync` の後ろ | ❌ **DB の重複照会**（`AnyAsync`） | – | – | **残す** |
| A1 | `Authz/CreateAttribute/Endpoint.cs:15-19` | `errors`（sink） | **β** | 入口（`db.AttributeDefinitions` 読み出しの後ろ） | ❌ `existing`（DB）で一意性を見る。**形式と一意性が 1 関数・1 配列** | – | ❌ `AbacValidation` はドメイン関数 | **残す** |
| A2 | 同 `:28-33` | `errors`（sink） | α | `SaveChangesAsync` の**例外捕捉** | ❌ 競合捕捉（`DbUpdateException`） | – | – | **残す** |
| A3 | `Authz/CreatePolicy/Endpoint.cs:16-18` | `errors`（sink） | **β** | 入口（`ValidatePolicyAsync` が DB を読む） | ❌ `definitions`（DB） | – | ❌ 🔴 **#535: 3 経路が 1 関数を呼ぶ**（`AuthzEndpoints.cs:74-80`） | **残す** |
| A4 | `Authz/ResolveScope/Endpoint.cs:23-25` | `errors`（sink） | **α** | 入口（`db.Policies` 照会の前） | ✅ `PolicyAction.IsValid` | ✅ | – | **移す** |
| A5 | `Authz/UpdateAttribute/Endpoint.cs:19-24` | `errors`（sink） | **β** | `FindAsync`（404）の後ろ | ❌ `attr.Key` / `attr.Scope`（**既存値**）＋ `existing`（DB） | – | ❌ | **残す** |
| A6 | `Authz/UpdatePolicy/Endpoint.cs:19-21` | `errors`（sink） | **β** | `FindAsync`（404）の後ろ | ❌ DB | – | ❌ #535 | **残す** |
| A7 | `Users/ReplaceAttributes/Endpoint.cs:19-21` | `errors`（sink） | **β** | 入口（`definitions` 読み出しの後ろ） | ❌ `definitions`（DB） | – | ❌ `UserAssignmentValidation` はドメイン関数 | **残す** |
| A8 | `Users/ReplaceRoles/Endpoint.cs:16-18` | `errors`（sink） | **β** | 入口（`ListAssignableRolesAsync` の後ろ） | ❌ **IdP から来る値域** | – | ❌ | **残す** |

集計: ガード **14**（McpServer 6 ＋ AuthorizationService 8）。**移す 4**（M1–M3 / A4）・**残す 10**。
`IADR-0398` 決定 10 の表（PR-C = 検証器 2 / ガード 4）と一致する（**転記ではなく再計算**）。

### 新設する検証器（PR-A / PR-B の置き場・命名の作法をそのまま使う —— 3 段目 ＝ その操作専用）

| 検証器 | 置き場 | 要求型 | 規則（宣言順が契約） |
| --- | --- | --- | --- |
| `RegisterMcpClientValidator` | `McpServer/Features/McpClients/RegisterClient/` | `RegisterMcpClientRequest` | M1 → M2 → M3 |
| `ResolveScopeValidator` | `AuthorizationService/Features/Authz/ResolveScope/` | `AccessScopeRequest` | A4 |

### 対象外（本 PR で**触らない**。理由つき。規則 6 の再掲）

| 箇所 | 理由 |
| --- | --- |
| M4 / M5（`RejectUnassignableAsync`） | **残す**（`IADR-0398` 決定 8）。要求型が 2 つ（`RegisterMcpClientRequest` / `ReplaceMcpClientAttributesRequest`）で 1 関数を共有することが ADR-0062 決定 3 の統制そのものであり、M5 は外部解決（`IRegistrarAttributeResolver`）を伴う。**形 β の唯一の実働サイトであり、本 PR は件数と順序を試験で固定するだけである** |
| M6（重複登録） | **残す**。`AnyAsync` の**照会結果**であって入力検証ではない。位置も `RejectUnassignableAsync` の後ろで動かせない |
| A1 / A3 / A5 / A6 / A7 / A8 | **残す**（`IADR-0398` 決定 8）。値域が DB / IdP から来るドメイン関数であり、`AbacValidation.ValidateAttributeDefinition` は**形式と一意性を 1 つの配列に順に積む** —— 形式だけを移すと、両方に違反する要求で**件数が変わる**。A3 / A6 は #535 の「3 経路が 1 関数を呼ぶ」要件に当たる |
| A2（競合捕捉） | **残す**。`DbUpdateException` の捕捉であり検証ではない |
| 🔴 **sink ヘルパ 4 本の統合** | `AuthzEndpoints.ValidationProblem` と `UserAdminEndpoints.ValidationProblem` は**同じ 4 行**だが、**統合しない**（`IADR-0398` 決定 8 が受容として記録済み）。集約をまたぐ共有物の置き場を新たに決める整理は本 issue の「移送」ではない。`McpClientEndpoints.Problem` 2 本も残す |
| `ResolveScope/GrpcService.cs` の同じ述語 | **母集合外**。`RpcException` を返す**別の器**であり、RFC7807 ではない。#1278 は `Results.ValidationProblem` 系だけを群 3 としている |
| `Authz/DeleteAttribute/Endpoint.cs:26` の `Results.Problem` | **母集合外**（409 系。参照中の属性の削除拒否） |
| DocumentService / DataSourceService / NotificationService | PR-A / PR-B が着地済み、または PR-D。`OwnerMappingValidation` は**恒久的に残す**（`IADR-0398` 決定 6） |
| `Features/ValidationProblems.cs`（DocumentService の sink） | **本 PR は使わない。** 2 サービスは自前の sink（`request` / `errors`）を持ち、鍵の正は sink 側にある（決定 1 (b)）。サービスを跨いで sink を共有すると `check-unit-dependencies.js` の境界を割る |

## 設計

`IADR-0398` の決定をそのまま適用する。**新しい判断は無い**（したがって新 IADR も起こさない）。

1. **決定 1 (b)（鍵は sink が持つ）**: 器（`Results.ValidationProblem(IDictionary<string,string[]>)`）は変えない。
   移送後の端点は既存の sink へ `result.Errors[0].ErrorMessage` を渡す。
   - McpServer: `return McpClientEndpoints.Problem(gate.Errors[0].ErrorMessage);`
   - AuthorizationService: `return AuthzEndpoints.ValidationProblem([gate.Errors[0].ErrorMessage]);`
   🔴 **検証器は鍵を持たない**（`OverridePropertyName` を書かない）。書くと鍵の正が 2 つになる。
   PR-A / PR-B が `OverridePropertyName` を**必ず**書いたのと**逆**であり、
   **サイトごとに鍵が違う DocumentService と、サービス 1 個で鍵が 1 つの本 PR の違い**である。
   推論名（`ClientId` / `Kind` / `EgressTier` / `Action`）は `PropertyName` に入るが、
   **応答本文には出ない**（sink が鍵を上書きする）。この「出ない」ことを契約試験が固定する。
2. **決定 5（検証と解析を分け、対応表を 1 つに保つ）**: `TryParseKind` / `TryParseTier` を
   `private static` → `internal static` にし、検証器は `Must(v => RegisterMcpClientEndpoint.TryParseKind(v, out _))`
   で**形式だけ**を見る。端点は検証通過後に**同じ関数**で解析する。
   🔴 **述語を写す**: `TryParseKind` は `value?.Trim().ToLowerInvariant()` してから比較するので
   `" Service-Account "` は**有効**であり、`TryParseTier` は `IsNullOrWhiteSpace` を**真**（既定
   `standard-external`）として通す。検証器が自前の集合比較を持つとここで割れる。**だから関数を共有する。**
3. **決定 2（Kernel を参照しない）**: `Platform.Shared.Kernel` を足さない。`Error` は `Message` を
   1 つしか持たず（`IADR-0229` 決定 1）、鍵つき・複数件の応答を運べない。
4. **決定 9（等価性の軸）**: 状態コード ＋ **鍵の列** ＋ **各鍵のメッセージ列** ＋ 判定の位置 ＋ 述語の粒度。
   - **宣言順を移送前のガード節の順に揃える**（`clientId` → `kind` → `egressTier`）。
   - **メッセージは検証器が持つ**。補間を含む 2 本は `const` にできないので
     `internal static string KindInvalidMessage(string?)` / `EgressTierInvalidMessage(string?)` の関数、
     `string.Join` を含む 1 本は `internal static readonly`、残る 1 本は `internal const` にする。
     試験は**定数（関数）とリテラルの両方**へ当てる。
   - 🔴 **位置を試験で固定する**。`/mcp-clients` は**管理者限定**（SC-12）なので
     「非管理者 ＋ 不正な kind → **403**（400 ではない）」を置く。
     「不正な kind ＋ 禁止属性 → kind のメッセージ」「妥当な kind ＋ 禁止属性 → 属性のメッセージ」の**対**で、
     検証器が `RejectUnassignableAsync` より**前**に居ることを固定する。
     「不正な kind ＋ 既存の clientId → kind のメッセージ」で `AnyAsync`（M6）より前も固定する。
5. **`RuleSet` は使わない。** 🔴 **本 PR の 2 端点はいずれも検証の位置が 1 つしかない**
   （M1–M3 は 3 本とも入口に連続し、A4 は入口の 1 本だけである）。`IADR-0398` 決定 3 は
   「位置が 2 つある端点」に限った決定であり、位置が 1 つの端点へ `RuleSet` を導入すると
   `Validate(req)` が名前つき集合を走らせないハザードを**理由なく**持ち込む。
   **したがって「規則が `RuleSet` から逃げる」変異は本 PR に適用対象が無い**（§変異試験に明記する）。
6. **1 本の `||` ガードは本 PR に存在しない。** M1–M3・A4 の述語はいずれも単項である
   （`IsNullOrWhiteSpace` / `TryParseKind` / `TryParseTier` / `PolicyAction.IsValid`）。
   **したがって「1 本の `||` を割る」変異も適用対象が無い**（同上）。
7. **登録**: `Program.cs` に **1 検証器 1 行の明示登録**（`AddScoped<IValidator<T>, TValidator>()`）を
   各サービス 1 行ずつ足す（全リポジトリ 24 → 26）。`AddValidatorsFromAssembly` は使わない
   （`IADR-0371` 決定 2 —— 消したときに止まること）。
8. **`.csproj`**: 両サービスへ `<PackageReference Include="FluentValidation" />` を足す（版は書かない。CPM）。
   **`Domain/` には入れない**（`check-backend-libraries.js` の Domain 外部依存ゼロ規則）。
   `PolicyAction` / `McpClientKind` は `Domain/` に残り、検証器は `Features/` から**呼ぶだけ**である。
9. **コミットはサービスごとに 1 つ**（1 コミット = 1 論理変更。`IADR-0398` 決定 10 の PR-C 行）。

### 母集合の取り方（`traceability.repo.md` 規則 9・10）

- **規則 9**（追随する文書を記憶で挙げない）: 「本 PR で新たに誤りになる自分の記述」を、
  **誤りの側の文字列で追跡下の全ファイルを走査してから**挙げた。走査語は
  `AddScoped<IValidator<`（24 → 26）・`PackageReference Include="FluentValidation"`（7 → 9）・
  **修飾つきの** `McpClientEndpoints.Problem(`（非テスト。4 → 2）・
  `AuthzEndpoints.ValidationProblem(`（非テスト。6 → 6。A4 が sink を使い続けるので**変わらない**）である。
  🔴 **`McpClientEndpoints.Problem(` は修飾つきの呼び出ししか拾わない。** 同クラス内の 2 呼び出し
  （`:69` / `:78`）は無修飾の `Problem(` であり、軸 2 の走査でしか出ない —— **同じ関数を、
  居場所によって別の語で数えることになる**。件数を書くときは走査語をそのまま添える。
- **規則 10**（導出値は走査ではなく計算し直す）: `IADR-0398` §結果の
  「FluentValidation が 6 → 7 サービス（PR-A 時点。PR-C・PR-D の着地で 10 になる）」は
  **PR-C 着地で 9 になる**。これは同 ADR が予告した増え方どおりであり、**ADR 側の追記は要らない**
  （追記が要るのは「新しい判断が出たとき」だけである。`IADR-0398` 決定 10 末尾）。
  なお同 ADR の「4 サービス」という数え（決定 2 の見出し）は
  DocumentService / McpServer / AuthorizationService / NotificationService を指す。
  csproj の 7 → 9 と混同しないこと（**csproj は Tests を含まない実プロジェクトの数であり、
  4 サービスのうち既に 1 つが済んでいる**）。
- **除外理由**: `IADR-0398` §母集合の表の「本 PR 適用後は 19 行」は **PR-A 時点の値として明記**されており、
  PR-B の結果表が 24 を記録している。**どちらも古くならない書き方**なので追随の必要は無い。

## 受け入れ基準

- [x] `RegisterClient` の 3 ガードと `ResolveScope` の 1 ガードが 2 つの `AbstractValidator` へ移り、
      両端点に手書きの入力検証ガード節が残らない（残るのは M6 の重複照会 1 本のみ）
- [x] 応答本文が移送前後で**バイト同一**である（形 α・形 β の代表 1 サイトずつで生の JSON を前後比較した）
- [x] 🔴 **検証器は鍵を持たない**（`OverridePropertyName` が 0 件）。鍵は sink（`request` / `errors`）が持つ
- [x] 🔴 **形 β（M4 / M5）の違反の順序と件数が変わっていない**（2 件が宣言順で並ぶことを試験が固定する）
- [x] `TryParseKind` / `TryParseTier` は `internal` の**同じ 1 関数**を検証器と端点が共有する
      （`" Service-Account "` が通り、`egressTier` 未指定が通ることを試験が固定する）
- [x] M4 / M5 / M6 / A1–A3 / A5–A8 の 10 ガードは**移していない**（`git diff` で該当ファイルに変更が無い）
- [x] sink ヘルパ 4 本は**統合していない**（`IADR-0398` 決定 8 の受容）
- [x] `Program.cs` の登録は明示登録であり（全リポジトリ 26 行）、`AddValidatorsFromAssembly` を使っていない
- [x] `Platform.Shared.Kernel` の参照を足していない（決定 2）
- [x] 端点契約試験を**先に**書き、端点 2 ファイル ＋ `McpClientEndpoints.cs` を `origin/develop` へ戻した
      状態でも**緑**になることを実測した
- [x] 変異で**実際に赤になった**試験名と本数を PR に書いた。**適用対象が無い変異はその旨を書いた**
- [x] `dotnet test` の件数が前後で減っていない（**測り直して書く**。試験の削除・skip 化は 0 件）
- [x] `dotnet build` × 2 / `dotnet test` × 2 / `dotnet format --verify-no-changes` / 検査器 6 本 ＋
      `REQUIRE_REPO_TESTS=1 scripts.test.js` が緑

## テスト方針

`IADR-0398` 決定 9 の軸（S 状態コード / K 鍵 / M メッセージ / P 位置 / G 粒度 / O 宣言順 / C 件数）へ写像する。

| 試験（置き場） | 軸 | 何を見るか | 赤にする変異 |
| --- | --- | --- | --- |
| `RegisterMcpClientValidatorTests.ValidRequest_Passes`（陽性対照） | – | `IsValid` かつ `Errors` 空 | 「常に落ちる検証器」 |
| `…ValidatorTests.<Rule>_FailsWithOriginalMessage` | M | `Errors[0].ErrorMessage` が**定数（関数）**と**リテラル**の両方に一致 | 定数だけ／リテラルだけを書き換える |
| `…ValidatorTests.AllThreeInvalid_ReportsClientIdFirst` | O, C | `Errors.Count == 3` かつ `Errors[0]` が `clientId` | 宣言順の入れ替え／規則を 1 本消す |
| `…ValidatorTests.Validator_DoesNotOwnTheKey` | K | 🔴 検証器の `PropertyName` は推論名であり、**応答の鍵ではない**ことを明示的に固定する | `OverridePropertyName` を足す（鍵の正が 2 つになる） |
| `…ValidatorTests.KindWithCaseAndSpaces_Passes` / `MissingEgressTier_Passes` | G | `" Service-Account "` と `egressTier` 未指定が通る（`TryParse*` の述語を写している） | 検証器が自前の集合比較を持つ |
| `ResolveScopeValidatorTests.*` | M, O | `PolicyAction.All` から組んだ文字列とリテラルの両方 | 同上 |
| 端点契約試験 `McpValidationProblemContractTests`（`errors` を `JsonElement` で**列挙順**に読む） | S, K, M | 鍵が `request` ただ 1 つ・メッセージ列が 1 件 | 端点が `ToDictionary()` を呼ぶ／sink の鍵を変える |
| 同 `…_FormBeta_ForbiddenAndRestricted_ReturnsBothInOrder` | **C, O** | 🔴 個人資料 ＋ 制限プロジェクトの同時割当で `request` が **2 件**、順序は `doc_scope` → `projects` | **sink を `Errors[0]` へ丸める**（本 PR が避けるために存在する欠陥） |
| 同 `…_FormBeta_ClearanceAndTags_ReturnsBothInOrder` | **C, O** | 🔴 `clearance` ＋ タグの同時逸脱で `request` が **2 件**、順序は `clearance` → `tags` | 同上 |
| 同 `ValidationProblem_KeepsRfc7807Envelope` | S, K, M | 生の JSON を**バイト比較**（`type` / `title` / `status` / `errors` ごと文字列一致） | 器を変える |
| `…_NonAdminWithInvalidKind_Returns403` | P | 🔴 **認可が検証より前**（無資格の呼び出しに入力の形を教えない） | 検証を認可より前へ動かす |
| `…_InvalidKindWithForbiddenAttribute_ReturnsKindMessage` と対の `…_ValidKindWithForbiddenAttribute_ReturnsAttributeMessage` | P | 検証器が `RejectUnassignableAsync` より**前** | 検証を `RejectUnassignableAsync` の後ろへ動かす |
| `…_InvalidKindWithDuplicateClientId_ReturnsKindMessage` と対の `…_ValidInputWithDuplicateClientId_ReturnsDuplicateMessage` | P | 検証器が `AnyAsync`（M6）より**前** | 同上 |
| `AuthzValidationProblemContractTests.ResolveScope_UnknownAction_ReturnsErrorsBucket` | S, K, M | `errors.errors[0]` がリテラルと `PolicyAction.All` 由来の文字列の両方に一致 | sink の鍵を変える／メッセージを書き換える |
| **既存**: `McpClientEndpointTests`（10）/ `ServiceAccountAttributeSubsetEndpointTests` / `AccessScopeContractTests` | S | 状態コード | **登録行を消す**（`IValidator<T>` が解決できず 500） |

🔴 **等価性の直接の証拠**: 契約試験を**先に**書き、**端点 3 ファイル**
（`RegisterClient/Endpoint.cs` / `McpClientEndpoints.cs` / `ResolveScope/Endpoint.cs`）だけを
`origin/develop` の内容へ戻して同じ試験を走らせる。移送前のコードでも緑であることが
「応答を変えていない」の実測である（PR-A / PR-B と同じ作法）。

## 結果（着地時に測り直した実測値）

🔴 **導出値は push の直前にすべて測り直した**（作業途中の値を受け入れ基準へ残さない。PR-A で
AI レビューが検出した失敗と同型）。基点は `origin/develop` @ `a50403ce`。

| 走査（submodule 除外・走査語をそのまま添える） | 前 | 後 |
| --- | ---: | ---: |
| `AddScoped<IValidator<`（全リポジトリ） | 24 | **26** |
| `PackageReference Include="FluentValidation"` を持つ csproj | 7 | **9** |
| `AddValidatorsFromAssembly(` の**呼び出し**（コメント行を除く） | 0 | **0** |
| `McpClientEndpoints\.Problem(`（**修飾つき**・非テスト） | 4 | **2** |
| `AuthzEndpoints\.ValidationProblem(`（非テスト） | 6 | **6** |
| `\.OverridePropertyName(`（本 PR の 2 サービス。**構文で数える**） | 0 | **0** |
| `Results\.BadRequest`（非テスト。陽性対照） | 18 | **18** |
| `Platform.Shared.Kernel` の参照（本 PR の 2 csproj） | 0 | **0** |

★ 🔴 **`OverridePropertyName` を語で数えると 6 ヒットする**（本 PR が書いた**説明文のコメント**である）。
**構文（`\.OverridePropertyName(`）で数えると 0** である —— `AddValidatorsFromAssembly` と同じ罠であり、
**語で数えると「鍵を持たないこと」を自分の説明文が否定して見える**。

試験数（`dotnet test`。**削除・skip 化は 0 件**）:

| プロジェクト | 前 | 後 |
| --- | ---: | ---: |
| `McpServer.Tests` | 146 | **175**（+29） |
| `AuthorizationService.Tests` | 185 | **201**（+16） |
| platform ユニット合計 | 1540 | **1585**（+45） |
| knowledge ユニット合計 | 2035 | **2035**（無変更） |
| 両ユニット合計 | 3575 | **3620** |

**等価性の直接の証拠**: 端点契約試験（`McpValidationProblemContractTests` 14 本 ＋
`AuthzValidationProblemContractTests` 9 本 ＝ **23 本**）を**移送より前に書き**、
**リポジトリの `src/` が `origin/develop` そのものの状態**（検証器も登録も csproj も未追加）で
**23/23 緑**であることを実測した。移送後も 23/23 緑である。
生の JSON をバイト比較する 2 本（`ValidationProblem_KeepsRfc7807Envelope` ×2）を含む。

**変異試験**（1 つずつ適用して戻した。母数は `McpServer.Tests` 175 / `AuthorizationService.Tests` 201）:

| 変異 | 赤 | 代表的な試験名 |
| --- | ---: | --- |
| M1 規則を 1 本消す（`RegisterMcpClientValidator` の `kind`） | **7** | `RegisterMcpClientValidatorTests.InvalidKind_*` / `AllThreeInvalid_ReportsClientIdFirst` / `Validator_DoesNotOwnTheResponseKey` / 契約試験 3 本 / **既存** `McpClientEndpointTests.不正な種別は拒否される` |
| M2 登録行を 1 行消す（McpServer） | **33** | `IValidator<T>` が解決できず 500。登録を下ごしらえに使う既存試験が広く落ちる |
| M3 sink の鍵を変える（`request` → `Request`） | **11** | 契約試験（鍵の列）と既存の本文部分一致試験 |
| M4 🔴 **形 β を `Errors[0]` へ丸める**（`Problem(IReadOnlyList<string>)` → `[messages[0]]`） | **2** | `RegisterClient_ForbiddenScopeAndRestrictedProject_ReturnsBothInDeclarationOrder` / `RegisterClient_ClearanceAndTagsOutsideRegistrar_ReturnsBothInDeclarationOrder` |
| M5 検証器へ `OverridePropertyName("request")` を足す（鍵の正を 2 つにする） | **1** | `RegisterMcpClientValidatorTests.Validator_DoesNotOwnTheResponseKey` |
| M6 述語を広げる（`egressTier` に `NotEmpty()` を足す） | **32** | `MissingOrLooselyWrittenEgressTier_Passes`（3）ほか、既定ティアで登録する既存試験が全滅 |
| A 登録行を 1 行消す（AuthorizationService） | **71** | `IValidator<T>` が解決できず 500 |
| B sink の鍵を変える（`errors` → `error`） | **4** | 契約試験 3 本 ＋ **既存** `PolicyDryRunValidationTests.Validate_AgreesWithSave_OnTheSameInput` |
| C 形 α を `ToDictionary()` で写す（鍵が `Action` に化ける） | **3** | `AuthzValidationProblemContractTests` の 3 本 |
| D 述語を広げる（`PolicyAction.IsValid` → `Trim` ＋ 大小無視） | **4** | `ResolveScopeValidatorTests.ActionOutsideTheDomain_Fails`（2）＋ 契約試験（2） |
| 「規則を `RuleSet` の外へ出す」 | — | 🔴 **適用対象が無い。** 本 PR の 2 端点は検証の位置が 1 つしかなく、`RuleSet` を導入していない（設計 5） |
| 「1 本の `\|\|` を割る」 | — | 🔴 **適用対象が無い。** M1–M3・A4 の述語はいずれも単項である（設計 6） |

🔴 **M4 の意味**: 赤になった 2 本は**どちらも本 PR が新設した試験**である。
**移送前の 146 本（McpServer）はこの欠陥を 1 本も捕まえない** —— 既存試験は
`body.Should().Contain("finance")` のような部分一致だけで、**2 件が 1 件に丸まっても緑のまま**である。
「拒否理由を丸めない」（ADR-0062 §結果）は移送前も機械的には守られていなかった。本 PR で初めて固定した。

## 計画書との差異

- 差異: なし。**応答本文を 1 バイトも変えない移送**であり、計画の裁定を要する事項は無い
  （ADR-0030 は用途ごとのライブラリ指定、ADR-0024 / ADR-0062 / ADR-0004 はいずれも
  MCP クライアント登録と ABAC の意味論についての決定であり、状態コードも本文も変えない本作業と整合する）。
  planning への `decision-needed` 起票はしていない。

## 未決事項

- **IADR は起こさない予定**である（`IADR-0398` 決定 10「PR-B〜D に新しい判断は無い」）。
  作業中に新しい判断が出た場合は `IADR-0398` へ日付つき追記ブロック（`［YYYY-MM-DD 追記 / #NNN］`）で足す
  —— **表の途中へ挟まない**（GFM の表が分断される。PR-A で実測された失敗）。
  新 IADR が要る場合の空き番号は基点 `a50403ce` で **`IADR-0402`** である（0401 は #1299 が取得済み）。
- **#1278 を閉じるのは PR-D** である。本 PR の PR 本文は `Refs #1278` に留める。
- PR-D（NotificationService）とはファイル領域が交差しない（`Services/NotificationService/**`）ので並列可。
