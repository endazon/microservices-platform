---
title: 未認証の要求が anonymous として ABAC を通る欠陥を AiAnalysis / Graph で塞ぐ（#1318 欠陥 A）
type: spec
status: done
related_ids: [FR-04, FR-05, FR-07, FR-17, UC-01, UC-02, UC-10, ADR-0004, ADR-0032, ADR-0034, ADR-0036, ADR-0065, ADR-0068, IADR-0009, IADR-0037, IADR-0044, IADR-0111, IADR-0272, IADR-0335, IADR-0379, IADR-0401]
author: Claude（実装）
created: 2026-09-07
updated: 2026-09-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0004_authorization-abac.md
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-authentication-bff-session.md
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-exploration.md
---

# 仕様書: 未認証の要求が `anonymous` として ABAC を通る欠陥を塞ぐ（#1318 欠陥 A）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（認可・ABAC）／FR-04・FR-07（RAG 回答・分析）／FR-17（グラフ探索）
- ユースケース（UC）: UC-01（横断検索・質問）／UC-02（分析）／UC-10（関連文書の探索）
- 計画 ADR: ADR-0004（ABAC）／ADR-0032（BFF セッション・Token Handler）／ADR-0034（グラフ探索）／ADR-0036（read/write 規則）
- 実装 ADR: [[IADR-0335]]（Wiki で**同型の欠陥**を是正済み。本仕様書はその形を写す）／[[IADR-0009]]（存在秘匿）／[[IADR-0044]]（多層防御）／[[IADR-0111]]（モデル未使用の表現）
- issue: #1318（欠陥 A のみ。**欠陥 B は射程外** —— 後述）

## 射程

🔴 **本仕様書は #1318 の欠陥 A だけを扱う。欠陥 B（RetrievalService の自称 Scope）には触れない。**
B は「Retrieval が自分でスコープを解決するのか、経路上のどこかで解決されていればよいと定めるのか」という
**裁定を要する**問いであり、実装側の判断で閉じてよい穴ではない。母集合の走査でも Retrieval は
「B として除外」と明示する（後述の除外表）。

## 欠陥 A（実測）

未認証の要求が `anonymous` という**利用者名**として ABAC 判定へ入り、認可側
（`AuthorizationService/Domain/AbacEvaluator.cs`）が条件 null（＝条件なし）を全利用者にマッチさせるため、
**「利用者条件を持たない active な allow ポリシー」が 1 件でもあれば匿名にも許可が下りる。**
すなわち fail-closed に*見えている*だけで、**未認証時の応答がポリシーの内容次第で変わる** ——
契約として固定されていない。これは [[IADR-0335]] が Wiki で塞いだものと**同型**である。

## 母集合（自分で引き直した走査。規則 1〜10）

基点 `origin/develop` = `ea587aaf`。**`git rev-parse --is-shallow-repository` = `false` を先に確認した**
（履歴の打ち切り位置を出典に採らないため。planning#410）。

### 走査 1: 文字列 `"anonymous"`（非テスト・非 bin/obj）

```console
$ git grep -n '"anonymous"' -- 'src/**/*.cs' ':!*Tests*' ':!*/bin/*' ':!*/obj/*'
AiAnalysisService/Features/Analysis/Analyze/Endpoint.cs:30
AiAnalysisService/Features/Analysis/Ask/Endpoint.cs:15
AiAnalysisService/Features/Analysis/AskStream/Endpoint.cs:35
FeedbackService/Features/Feedback/Submit/Endpoint.cs:93,98   ← コメントのみ
GraphService/Infrastructure/ExternalServices/GraphAccessResolver.cs:39
WikiService/Infrastructure/ExternalServices/WikiAccessResolver.cs:24  ← 是正済み（陽性対照）
Platform.Shared.Infrastructure/Foundation/Authz/BffScopeResolver.cs:35
```

**issue 本文の 4 行は `?? "anonymous"` という綴りで、かつ `src/knowledge/backend/Services` に限って
引いた結果であり、母集合ではない。** 綴りと範囲を緩めた本走査で **`BffScopeResolver.cs:35` が
1 件増えた**（platform 側が入っていなかった）。

### 走査 2: 綴りに依らない入口 —— `Identity.Name` を読む全箇所

```console
$ git grep -n 'Identity?\.Name\|Identity\.Name\|FindFirstValue(ClaimTypes.Name)\|FindFirst(ClaimTypes.Name)' \
    -- 'src/**/*.cs' ':!*Tests*' ':!*/bin/*' ':!*/obj/*'
（24 件。うち ABAC スコープ解決へ流れるのは走査 1 と同じ 5 箇所）
```

`anonymous` 以外のフォールバック綴りも出た —— `?? "unknown"`（`DashboardService/.../View/Endpoint.cs:70`、
`ConfigBffEndpoints.cs:105`）。**いずれも監査記録の主体名であって ABAC 判定の入力ではない**ので
本欠陥には当たらない（除外表）。

### 走査 3: ABAC の解決点そのもの（入口ではなく出口から引く。規則 9）

```console
$ git grep -n 'new AccessScopeRequest(' -- 'src/**/*.cs' ':!*Tests*' ':!*/bin/*' ':!*/obj/*'
AiAnalysisService/Infrastructure/ExternalServices/RagOrchestrator.cs:327
GraphService/Infrastructure/ExternalServices/GraphAccessResolver.cs:49
WikiService/Infrastructure/ExternalServices/WikiAccessResolver.cs:60      ← 是正済み（陽性対照）
AuthorizationService/Features/Authz/ResolveScope/GrpcService.cs:34        ← 認可側（受け口）
McpServer/Infrastructure/ExternalServices/AuthorizationServiceRegistrarAttributes.cs:133
Platform.Shared.Infrastructure/Foundation/Authz/BffScopeResolver.cs:45
```

### 走査 4: 陽性対照 —— `IsAuthenticated` を見ている箇所

```console
$ git grep -n 'IsAuthenticated' -- 'src/**/*.cs' ':!*Tests*' ':!*/bin/*' ':!*/obj/*'
WikiService/.../WikiAccessResolver.cs:45          ← 是正済み。**引っかからない側に出る**
Platform.Bff/.../AuthBffEndpoints.cs:72
Platform.Bff/.../SessionTokenRefresher.cs:38
NotificationService/Domain/NotificationSubject.cs:21
Platform.Shared.../KeycloakRolesClaimsTransformation.cs:20
Platform.Shared.../SyntheticTraffic.cs:50
```

🔴 **陽性対照が対で立っている** —— 是正済みの `WikiAccessResolver` は走査 1・3 では
「`anonymous` を持つ」側に出るが、走査 4 で `IsAuthenticated` 判定を持つ側にも出る。
未是正の 4 箇所は走査 4 に**一度も出ない**。これが「同型かどうか」を分ける印である。

## 直す対象と、直さない対象（除外理由つき）

### 直す

| # | 箇所 | 匿名で到達できるか（実測） | 直す理由 |
|---|---|---|---|
| A-1 | `AiAnalysisService` の 3 端点 | 🔴 **できる** | `/analysis` 群に `RequireAuthorization()` が**無い**（`AnalysisEndpoints.cs:17`）。欠陥は live |
| A-2 | `GraphService/GraphAccessResolver` | **できない**（後述） | 多層防御（[[IADR-0044]]）。端点側の宣言に依存した fail-closed を、解決器側の契約として固定する |

### 除外（走査に出たが直さないもの）

| 箇所 | 除外理由 |
|---|---|
| `RetrievalService`（自称 Scope） | 🔴 **#1318 欠陥 B。裁定待ち**。本 PR の射程外 |
| `WikiService/WikiAccessResolver` | **是正済み**（[[IADR-0335]] 決定 4 / #1126）。本走査の陽性対照 |
| `Platform.Shared.../BffScopeResolver.cs:35` | **BFF はエッジであり、6 つの呼び出し元がすべて `RequireAuthorization()` の群に居る**（`/bff/documents` `/bff/search` `/bff/attribute-values` `/bff/private-notes`）。匿名はミドルウェアが **401** で弾き、ハンドラへ到達しない。かつ **401 を返すのがエッジの正しい契約**（ADR-0032）であって、後段と同じ「秘匿へ倒す」形にすると**エッジの契約が変わる**ので触らない |
| `McpServer/AuthorizationServiceRegistrarAttributes.cs:64` | `anonymous` へ**倒していない** —— 主体が空なら `RegistrarAssignableAttributes.Unavailable` を返し、`/authz/scope` を呼ばない。既に fail-closed |
| `AuthorizationService/.../ResolveScope/GrpcService.cs:34` | **認可側の受け口**であって呼び出し側ではない。ここで身元を作らない |
| `DashboardService/.../View/Endpoint.cs:70`、`ConfigBffEndpoints.cs:105`（`?? "unknown"`） | **監査記録の主体名**であり ABAC 判定の入力ではない。判定を緩めない |
| `FeedbackService/.../Submit/Endpoint.cs:93,98` | **コメント中の言及のみ**（#586 で `anonymous` フォールバックは撤去済み）。コードは `Identity?.Name` を null のまま扱う |

## 🔴 既存の契約を自分で確かめた結果（「Wiki がそうだから」で写さない）

### AiAnalysisService —— Wiki と**同じ**（拒否は 200 ＋ 空。401 にしない）

`/analysis` 群は `RequireAuthorization()` を持たず、`Program.cs` にフォールバックポリシーも無い。
匿名は 3 端点すべてに到達する。**では今、匿名には何が返っているか** —— 実物の
`RagOrchestrator` は `Granted=false` のとき次へ倒す:

- `AskAsync` / `AnalyzeAsync`: `EmptyAnswer()` = **200** ＋ `("閲覧権限のある文書が見つかりませんでした。", [], "", 0, 0)`
- `AskStreamAsync`: **200** ＋ SSE `citations([])` → `token("閲覧権限のある文書が見つかりませんでした。")` → `done("",0,0)`

したがって **「拒否＝200 ＋ 空」は AiAnalysis に既に在る契約である。** 本 PR は
**その同じ応答を、認可サービスを呼ばずに返す**ようにするだけであり、**状態コードも本文も変えない。**
401 にしないという [[IADR-0335]] の判断は、AiAnalysis でも**同じ理由で**成り立つ（エッジは BFF。
`/bff/analysis` 群は `RequireAuthorization()` を持ち、匿名はそこで 401 になる。後段の
AiAnalysisService は mesh 内である）。

### GraphService —— Wiki と**違う**。これは違うと書く

🔴 **`GraphAccessResolver` の匿名経路は、現在の HTTP 表面からは到達できない。**
`IGraphAccessResolver` の消費者 7 端点はすべて認証を要求している（実測）:

```console
$ git grep -n 'RequireAuthorization\|MapGroup' -- 'src/knowledge/backend/Services/GraphService/**/*.cs' ':!*Tests*'
Features/AiSuggestions/AiSuggestionEndpoints.cs:38  MapGroup("/graph/suggestions") … .RequireAuthorization()
Features/Graph/GraphEndpoints.cs:20                 MapGroup("/graph")   ← 群には無い
Features/Graph/GetNode/Endpoint.cs:52               .RequireAuthorization()
Features/Graph/Neighbors/Endpoint.cs:114            .RequireAuthorization()
Features/Graph/CreateEdge/Endpoint.cs:116           .RequireAuthorization()
```

**Wiki は `/wiki` 群にも各端点にも `RequireAuthorization` を持たなかった**（[[IADR-0335]] の実測欄が
「0 件」と記録している）。そこが Graph との違いである。よって Graph の匿名契約は
**401**（ミドルウェアが弾く）であり、Wiki の 200 ＋ 空 / 404 とは**別物である。本 PR はそれを変えない。**

では**なぜ直すか** —— 現在の fail-closed が**端点ごとの `RequireAuthorization()` 宣言に依存している**
からである。1 個書き忘れた端点が足された瞬間、その端点は `anonymous` で ABAC を通る。
[[IADR-0044]] の多層防御をここでも 1 枚から 2 枚にする。**到達できないことは「直さなくてよい」ではないが、
「今そこから漏れている」でもない。本 PR は後者を主張しない。**

## 決定（実装方針）

### D-1: 短絡は「認可サービスを呼ぶ前」に置き、状態コードを変えない

Wiki と同じ形。**401 にしない。**

### D-2: AiAnalysis は**端点**に置く（Wiki は解決器に置いた。理由が違うので置き場所も違う）

Wiki の短絡は `WikiAccessResolver`（Infrastructure）に在る —— 認可解決をスタブへ差し替えている
既存テストの意味を変えないためである。**AiAnalysis には `HttpContext` を受け取る解決器が無い**
（`RagOrchestrator.ResolveScopeAsync` は `userId` 文字列を受け取るだけで、認証済みか否かを知り得ない）。
身元を作っているのは端点であり、**`?? "anonymous"` を書いている行そのものが欠陥の在り処**である。
よって短絡は端点に置く。3 端点が共有するので `AnalysisEndpoints`（2 段目。ADR-0068 決定 2 が
「操作をまたいで共有されるもの」を置く場所と定める）へ 1 つだけ置く。

### D-3: 拒否時の応答は**新造しない**。既にある縮退の値を 1 箇所へ寄せて再利用する

拒否の文言 `"閲覧権限のある文書が見つかりませんでした。"` は現在 `RagOrchestrator` の中に在り、
非ストリーミング（`EmptyAnswer()`）とストリーミング（SSE 3 イベント）の 2 形を持つ。
端点側でこれを**書き写すと同じ文字列が 2 箇所に増える**（規則 10 が禁じる形）。
`Domain/NoAccessAnswer.cs` へ 1 つだけ置き、`RagOrchestrator` と端点の両方がそこから採る。
**値も意味も変えない**（`ModelOrNone` / `NoModel` の IADR-0111 の役割には触らない）。

### D-4: Graph は Wiki と**同一の形**を解決器へ写す

```csharp
if (ctx.User.Identity?.IsAuthenticated != true)
    return new AccessScopeResponse(AnonymousUserId, [], false);
```

🔴 **短絡は輸送（REST / gRPC）の分岐より手前に置く** —— gRPC 経路でも匿名では 1 度も呼ばれない。

### D-5: 新しい IADR は起こさない。[[IADR-0335]] へ日付つき追記を足す

本 PR の判断は [[IADR-0335]] 決定 4 の**適用**であって、新しい決定ではない ——
①短絡を認可サービスの手前に置く、②401 にしない、③既存の状態コードを変えない、の 3 点は
同決定がそのまま定めており、本 PR が足すのは**適用先が Wiki の外にもあったという事実**と、
**AiAnalysis は同じ形・Graph は違う契約（401）だという実測**だけである。
規約上「決定を変える追記は日付つき追記ブロック」であり、本追記は決定を変えない補足なので
`［2026-09-07 追記 / #1318］` の形で足し、`updated:` を前進させる。

## テスト（受け入れ基準）

🔴 **既存の器では踏めない** —— `AiAnalysisService/Tests/TestWebApplicationFactory.cs:29-30` が
`IRagOrchestrator` を丸ごとスタブへ差し替えるので、**既存の端点テストは ABAC を 1 度も踏んでいない**。
Wiki の器（`AnonymousContractTestFactory`。認可を**全許可に固定**し resolver は差し替えない＝
**最も甘い構え**）を写す。

| ID | 期待 |
|---|---|
| T-1 | 未認証 `POST /analysis/ask` → 200 ＋ 空回答、**認可サービス呼び出し 0 回** |
| T-2 | 未認証 `POST /analysis/analyze` → 200 ＋ 空回答、**呼び出し 0 回** |
| T-3 | 未認証 `POST /analysis/ask/stream` → 200 ＋ SSE（citations 空・中立文言・done）、**呼び出し 0 回** |
| T-4 | 🔴 **陽性対照**: 認証済みなら 3 端点とも認可サービスが**呼ばれる**（＝「常に拒否する実装」を落とす） |
| T-5 | Graph 解決器: 未認証 ctx → `Granted=false`。**REST 経路で HTTP 0 回**（`NeverHttpClientFactory`） |
| T-6 | Graph 解決器: 未認証 ctx → `Granted=false`。**gRPC 経路で `CallCount` 0**（read / write 両 action） |
| T-7 | 🔴 **陽性対照**: 認証済み ctx なら REST / gRPC とも呼ばれ `Granted=true` が返る |
| T-8 | Graph 端点の**既存契約**: 匿名 `GET /graph/nodes/{id}` は **401 のまま**（本 PR が変えていない） |

## 🔴 実装中に判明したこと（予定に無かった事実）

### 既存の端点テスト 8 本は、**未認証で走っていた**

短絡を入れた直後、`AiAnalysisService.Tests` の**既存 8 本が落ちた**（実出力）:

```console
失敗 AnalysisEndpointTests.PostAnalyze_ReturnsAnswerWithCitations
失敗 AnalysisEndpointTests.PostAnalysisAsk_ReturnsAnswerWithCitations
失敗 AskStreamEndpointTests.PostAskStream_EmitsCitationsThenTokensThenDone
失敗 AskAttributeFilterTests.Ask_PassesAttributeFiltersDownstream
失敗 AskAttributeFilterTests.AskStream_PassesAttributeFiltersDownstream
失敗 AskStreamFirstTokenMetricsTests.PostAskStream_RecordsExactlyOneFirstTokenMeasurementInSeconds
失敗 AskStreamFirstTokenMetricsTests.PostAskStream_FirstTokenDuration_DoesNotGrowWithResponseLength
失敗 AskStreamFirstTokenMetricsTests.PostAskStream_WhenNoTokenEmitted_RecordsNothing
```

原因は実装の誤りではない —— **`TestWebApplicationFactory` が認証を一切構成しておらず、
ここを通る全テストが匿名で走っていた**。端点が `?? "anonymous"` で身元を作っていたので
素通りしていたのであり、「認証済みの利用者が使う経路」を測っているつもりで**匿名の経路を
測っていた**。**テストの前提が誤っていたことが、短絡によって初めて可視になった。**

是正は器の側で行う（`AlwaysAuthenticatedTestHandler` を既定で有効にする）。**測りたかったのは
認証済みの振る舞い**だからである。未認証の契約は専用の器が測る。

### 認可側の「条件なし＝全員にマッチ」は、**どこからも試験で固定されていない**（M-4 の結果）

M-4（`AbacEvaluator.MatchesUserConditions` で `conditions is null` を「誰にもマッチしない」へ変える）
は**赤にならなかった** —— `src/platform/backend` の全試験 **1628 本が緑のまま通った**（後述）。
本欠陥が成立する前提そのものが無試験である。**本 PR では直さない**（AuthorizationService の
振る舞いを変える変更であり、欠陥 A の射程ではない）が、**「無かった」で済ませずここに記録する。**

## 変異試験（実走した。実出力を記録する）

基準: 変異前は AiAnalysis 136 / Graph 495 / Wiki 102 / platform 1628 がすべて緑。

| M | 変異 | 結果 | 赤になった試験（実名） |
|---|---|---|---|
| M-1 | `AnalysisEndpoints.IsAnonymous` を `=> false` にする（AiAnalysis の短絡を外す） | 🔴 **赤 3 本** | `Ask_ReturnsEmptyAnswerForAnonymous_WithoutAskingAuthorization` / `Analyze_ReturnsEmptyAnswerForAnonymous_WithoutAskingAuthorization` / `AskStream_ReturnsNeutralSseForAnonymous_WithoutAskingAuthorization`（`失敗: 3、合格: 4、合計: 7`） |
| M-2 | `!= true` を `== false` へ（AiAnalysis・Graph の両方） | 🔴 **赤 1 本**（Graph のみ） | `GraphAnonymousAccessContractTests.Null_identity_is_denied_without_calling_authorization`（`失敗: 1、合格: 7、合計: 8`）。**AiAnalysis 側は 136 本すべて緑のまま** |
| M-3 | 短絡を輸送分岐の後ろへ移す（`authzScopeGrpc is null &&` を足して gRPC だけ外す） | 🔴 **赤 3 本** | `Anonymous_is_denied_without_any_grpc_call(action: "read")` / `(action: "write")` / `Null_identity_is_denied_without_calling_authorization`（`失敗: 3、合格: 5、合計: 8`） |
| M-4 | 認可側を「条件なしは誰にもマッチしない」へ（`AbacEvaluator`。**直さない。赤を見るだけ**） | ⚪ **緑のまま** | **1 本も落ちない。** `AuthorizationService.Tests` 201 本を含む platform 全 7 アセンブリ（42 / 325 / 175 / 201 / 90 / 275 / 521）が緑。上の §実装中に判明したこと に記録した |
| M-5 | 匿名契約テストの器で `IRagOrchestrator` をスタブへ戻す | 🔴 **赤 4 本** | `AllThreeRoutes_AskAuthorizationForAuthenticatedUser` の 3 ケース（ask / analyze / ask/stream）＋ `Ask_ForAuthenticatedUser_DoesNotReturnTheAnonymousDegradation`（`失敗: 4、合格: 3、合計: 7`）—— **陽性対照が仕事をしている**（器を甘くすると ABAC を踏まなくなり、対照が落ちる） |
| M-6 | `AskStream` **だけ**短絡を外す（M-1 の部分版。M-4 が緑だったため追加した） | 🔴 **赤 1 本** | `AskStream_ReturnsNeutralSseForAnonymous_WithoutAskingAuthorization`（`失敗: 1、合格: 6、合計: 7`）—— 3 端点が**個別に**測られていることの確認 |

🔴 **M-2 の非対称は設計どおりである。** `!= true` と `== false` が分かれるのは
`ClaimsPrincipal.Identity` が **null** のときだけであり、ASP.NET のホストを通る要求は
（未認証でも）非 null の `ClaimsIdentity` を持つ。したがって**端点経由の試験では原理的に
区別できない**。区別できる入力（`new ClaimsPrincipal()`）を作れるのは解決器を直接構築する
Graph 側の単体試験だけであり、そこに `Null_identity_is_denied_without_calling_authorization`
を置いた。**「落ちなかった」ではなく「落とせる場所に 1 本置いた」。**

## 試験件数の前後

| 対象 | 前 | 後 | 差 |
|---|---|---|---|
| `AiAnalysisService.Tests` | 129 | 136 | **+7** |
| `GraphService.Tests` | 487 | 495 | **+8** |
| `WikiService.Tests` | 102 | 102 | ±0（陽性対照。触っていない） |

## 残す残余（塞いでいないもの。記録する）

- **認証済みだが `Identity.Name` が null の主体**は、従来どおり `?? "anonymous"` で ABAC へ入る
  （AiAnalysis の 3 端点・Graph・Wiki すべて同じ）。**[[IADR-0335]] の Wiki 実装と同じ綴りであり、
  本 PR は先例に揃えた。** 本欠陥（＝**未認証**が通る）とは別の口であり、
  「名前クレームを持たない認証済み主体が在り得るか」は認証基盤側（`AuthExtensions` の
  `NameClaimType` = `preferred_username`）の問いである。**在り得るかを確かめていない。**
- **認可側の「条件なし＝全員にマッチ」が無試験である**（M-4）。上に記録した。

## やらないこと

- 欠陥 B（RetrievalService）
- `BffScopeResolver` の変更（エッジの契約を変えるため）
- 状態コードの変更（401 化）
- `deploy/mail-relay/**`・`scripts/check-password-reset-mail.js`・`scripts/check-realm-constraints.js`・
  `scripts/check-stack-ready.js`・`.github/workflows/**`（別 PR / 別 issue が動いている）
