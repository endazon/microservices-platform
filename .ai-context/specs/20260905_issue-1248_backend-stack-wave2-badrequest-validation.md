---
title: 作業仕様書 — 計画スタック 3 種の横展開 波 2 の第 1 弾（`Results.BadRequest` 系の手書き検証を GraphService / DataSourceService から移す）（#1248）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0030
  - ADR-0034
  - ADR-0041
  - ADR-0065
  - ADR-0068
  - IADR-0117
  - IADR-0195
  - IADR-0229
  - IADR-0242
  - IADR-0272
  - IADR-0282
  - IADR-0295
  - IADR-0323
  - IADR-0371
  - IADR-0393
  - IADR-0395
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (Accepted 2026-07-25) 決定・選定基準 3・4
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md (Accepted 2026-08-22) 決定 2・3
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md (fixed 2026-08-30) 基本方針・実装状況・Application 層
---

# 作業仕様書: 計画スタック 3 種の横展開 波 2 の第 1 弾（#1248）

起点: 実装 issue #1248（親 #1230 / #1064 / 環流 planning#490）。
参照実装は `IADR-0371`（`FeedbackService`）、直前の波 1 は PR #1257（`facebfe9`）と `IADR-0393`。

## 0. 前提の確認

- 基点 `origin/develop` = **`facebfe9`**。`git rev-parse --is-shallow-repository` = **`false`**
  （履歴の打ち切りではないので `git log` を出典に使える）。
- 着手前に `git fetch origin && git merge origin/develop` を実行し、**Already up to date**。
- `src/ai-stock-trading` は `git submodule update --init --depth 1` で populate 済み
  （`Platform.Bff` のビルドに要る）。**内容は変更しない。**

## 1. 母集合（issue と #1230 の数えを転記せず、基点 `facebfe9` で自分で走査した）

走査対象は `src/platform` と `src/knowledge`（`src/ai-stock-trading` は submodule ＝ 別プロジェクトのため除外）。
サービスは **14 件**（`src/*/backend/Services/*/` の一覧で確定）。

### 1-1. 3 ライブラリの現況（波 1 の着地で数が動いている）

| 要素 | 走査 | 実測（`facebfe9`） |
| --- | --- | --- |
| FluentValidation の `PackageReference` | `Include="FluentValidation"` を `*.csproj` へ | **4 件**（AiAnalysis / Conversion / Dashboard / Feedback） |
| Riok.Mapperly の `PackageReference` | `Include="Riok.Mapperly"` を `*.csproj` へ | **4 件**（Feedback / Authorization / McpServer / Notification） |
| `Platform.Shared.Kernel` への `ProjectReference` | `Platform.Shared.Kernel.csproj` を `*.csproj` へ | **4/14 サービス**（AiAnalysis / Conversion / Dashboard / Feedback。ほかのヒット 1 件は Kernel 自身のテスト） |

🔴 **#1064 が掲げた「4/14」は当時の実測では 0/14 だった**（VSA 移行で `ProjectReference` が落ち、
コメントだけ残っていた）。**いま再び 4/14 なのは偶然の一致であり、中身は違う** —— 当時の 4 件は
「参照が無いのにコメントだけある」4 サービス、いまの 4 件は波 1 で**実際に `Result` を使っている** 4 サービスである。

**陽性対照**（「n 件しか無い」を「無い」と読む前に、走査器が生きていることを確かめた）:

- `PackageReference` の走査 → `Include="WolverineFx"` が **2 件**でヒットする。
- `ProjectReference` の走査 → `*.Contracts.csproj` が **23 件**でヒットする。

### 1-2. 手書きの入力検証（`Results.BadRequest` 系）

`Results.BadRequest` を返す行（テストと submodule を除く）は **22 行**。うち **5 行は波 1 が移送済み**の
`gate.Error.Message`（`Result` からの写像）であり、手書きのガード節ではない。**残る手書きは 17 箇所**
（`IADR-0393` の「23 → 17」と一致する）。

| サービス | 箇所 | 本 PR の扱い |
| --- | --- | --- |
| GraphService | **12** | **9 箇所を移送**（残り 3 は入力検証ではない。§4-1） |
| DataSourceService | **4** | **1 箇所を移送**（残り 3 は入力検証ではない。§2-4） |
| `Platform.Bff` | 1 | **射程外**（#1248 が明示。`/bff/auth/logout` はセッション `sid` の一致検査であり要求 DTO の検証ではない。本文も返さない） |

### 1-3. 手書きの入力検証（`Results.ValidationProblem` 系）＝ #1248 の群 3

`ValidationProblem` のヒットは 46 行。コメント・ヘルパ定義を除いた**実際に 400 を返す呼び出しは 37 箇所**である。

| サービス | 箇所 | 経路 |
| --- | --- | --- |
| DocumentService | 20 | `Results.ValidationProblem` を直接 |
| AuthorizationService | 8 | `AuthzEndpoints.ValidationProblem` 6 ＋ `UserAdminEndpoints.ValidationProblem` 2 |
| McpServer | 6 | `McpClientEndpoints.Problem` |
| DataSourceService | 2 | `OwnerMappingValidation` 内の私有ヘルパ |
| NotificationService | 1 | `outcome.Errors` の透過（検証は `NotificationIngress.AcceptAsync` の中） |

🔴 **#1248 の「34 箇所」は DocumentService 20 ＋ AuthorizationService / McpServer 14 の合計であり、
DataSourceService の 2 と NotificationService の 1 が入っていない。** 実測は **37** である。
本 PR の射程外なので数え直しだけを記録し、追随 issue の本文へ引き渡す。

### 1-4. 追加引数のある写像 ＝ #1248 の群 4

7 本（DocumentService 4 / GraphService 1 / RetrievalService 1 / AuthorizationService 1）。
**本 PR では扱わない**（§3 の分割）。

## 2. 先に決めること（#1248 が「判断が要る」と名指ししたもの）

### 2-0. 裁定の要否 —— **5 点すべて実装裁量。planning への `decision-needed` 起票は要らない**

根拠は計画へ逐語で当たった結果である。

- 計画 `ADR-0030` §決定 は「マッピング = Riok.Mapperly、検証 = FluentValidation」と用途ごとの
  ライブラリを指定する。**適用サービス数も、検証器の置き場も、要求モデルの起こし方も定めていない。**
- `ADR-0041` 決定 2 は「`Domain` / `Application` / `Api` / `Infrastructure` は `SharedKernel` が
  公開する型のみを参照する」——**参照の向きの制約**であり、どの失敗を `Result` で表すかは定めない。
- `IADR-0371` 決定 1 が既に「**義務は『その関心を実装する箇所では標準ライブラリを使うこと』**」と
  読み方を確定させており、本 PR の判断はすべてその適用である。

**起票前に同件を検索した**（planning の open issue を `FluentValidation` / `Mapperly` / `Result` /
`検証` で検索）。**新たに裁定を要する事項は見つかっていない。**

### 2-1. 検証を認可より前に置く形を、順序が読める書き方で示す（#1248 群 1 の決めごと 1）

`Neighbors` と `AiSuggestions.List` は**検証が認可より前**にあることが仕様である
（後ろへ動かすと文書の存在が漏れる。`GraphEndpointsSecrecyTests` / `GraphTraversalTests` が帰結を固定する）。

**採る形**: `IValidator<T>` は**ハンドラの引数**として受け取り（解決は DI が行う）、
**実行は従前のガード節が居た位置＝ハンドラ本体の先頭**に置く。認可（`accessResolver.ResolveAsync`）は
その後ろに残る。**行の順序が判定の順序である**という読み方は移送前と変わらない。

`IValidator<T>` の引数追加は解決であって実行ではないので、**引数の並びは順序の証拠にならない**。
そのため既存の 🔴 注記（CodeQL `cs/user-controlled-bypass` の指摘ごと理由を書いたもの）は
**1 文字も削らずに検証の実行行へ付け直す**。

### 2-2. クエリ引数の検証に要求モデルを起こす（#1248 群 1 の決めごと 2 の前半）

`hops` / `types` / `state` / `kind` は端点の引数であって DTO ではない。`AbstractValidator<T>` は
型に対して規則を宣言するので、**その 1 操作でしか使わない `internal sealed record` の要求モデルを
スライスの 3 段目（`Features/<集約>/<操作>/`）に起こす**（`ADR-0068` 決定 2 —— 1 操作にしか
使われないものは 3 段目）。

- `Features/Graph/Neighbors/NeighborsQuery.cs` … `record NeighborsQuery(int? Hops, string? Types)`
- `Features/AiSuggestions/List/ListAiSuggestionsQuery.cs` … `record ListAiSuggestionsQuery(string? State, string? Kind)`

**検証しない引数（`by` / `documentId`）は載せない。** 要求モデルは「検証の対象」を表す器であって、
端点の引数一覧の複製ではない。載せると「検証されているように見えるが規則が無い」欄ができる。

**ルーティングは変えない**（引数は端点の署名に残る）。`[AsParameters]` へ束ねると OpenAPI の生成が
変わり得るので、振る舞いを変えない制約から出る。

### 2-3. `Neighbors` の `types` は検証と解析を分ける（#1248 群 1 の決めごと 2 の後半）

移送前は `Guid.TryParse` の結果をそのまま `edgeTypes` として使う**融合ループ**だった。

**分ける。** 検証器は「各要素が GUID として読めるか」だけを見て、解析（`HashSet<Guid>` の構築）は
端点に残す。理由は 2 つ:

1. **検証器が `IReadOnlySet<Guid>` を持ち出すと、それは検証器ではなく解析器になる。**
   `AbstractValidator` の戻りは `ValidationResult` であり、副産物を返す口が無い。
   `ValidationContext.RootContextData` へ詰めると**規則の副作用**になり、規則の宣言順が
   応答の契約であるという `IADR-0371` 決定 2 の読み方と噛み合わない。
2. **二度読みの費用は無視できる。** `types` はクエリ文字列 1 本であり、要素数は辺の型辞書の規模
   （数十）で頭打ちである。

🔴 **`parsed.Count > 0` のときだけ `edgeTypes` に入れる**という移送前の縮退を保つ
（`types=",,,"` は「絞らない」であって 400 ではない）。

### 2-4. 状態に依存する検証は端点に残す（#1248 群 2 の決めごと）

DataSourceService の 4 箇所のうち、**移すのは `Update` の 1 箇所だけ**である。

- ✅ **`Update` の `config` / `defaultAttributes` の省略拒否** … 純粋な入力検証。要求 DTO だけで判定できる。
- ❌ **`ConnectionUriPolicy.Validate(incoming, existing)`**（Create / Patch / Update の 3 箇所）…
  **端点に残す。**
  - `Patch` / `Update` は `db.DataSources.FindAsync` の**後ろ**にある（不存在は 404 が先）。
    検証器をハンドラ先頭で回すと **404 が 400 に化ける。** 移送は振る舞いを変えない作業なので、
    位置を動かせない時点で「端点入口の入力検証」ではない。
  - 既存値（`ds.ConnectionUri`）が要るので、検証器へ持ち込むには `ValidationContext.RootContextData`
    か非同期規則 ＋ `DbContext` の注入が要る。**`Features/` の検証器へ Infrastructure 依存が入る。**
  - そもそも `ConnectionUriPolicy` は `Domain/` に居る**ドメイン方針**であり、
    `SecretMask` の 1 本の判定規則（`IADR-0295` 決定 1）を共有している。器を替える理由が無い。
- ❌ **`OwnerMappingValidation.ValidateAsync`**（Create / Patch / Update の 3 箇所。`Results.BadRequest`
  の数えには入らない）… **群 3（RFC7807）である。** 応答は `Results.ValidationProblem`（全違反を返す）
  であり、外部の利用者名簿（`IPlatformUserDirectory`）を引く。**本 PR の射程外。**

### 2-5. 2 欄の 400 本文をどう運ぶか（`Neighbors` だけの問題）

移送対象 10 箇所のうち **8 箇所の本文は `{ "error": "<文字列>" }` の 1 欄**で、波 1 の形
（`Errors[0].ErrorMessage` を `error` に載せる）がそのまま使える。

**`Neighbors` の 2 箇所だけが `{ "error": "<機械語>", "message": "<説明文>" }` の 2 欄**である。

**採る形**: 検証器が `WithErrorCode("<機械語>")` と `WithMessage("<説明文>")` の両方を宣言し、
端点は `Error.Validation(Errors[0].ErrorCode, Errors[0].ErrorMessage)` として
**`Error.Code` を `error` へ、`Error.Message` を `message` へ**写す。

🔴 **1 欄の 8 箇所へこの形を広げない。** 広げると `error` の値が `ErrorCode` 由来になり、
波 1 の 6 サービス（`Error.Message` 由来）と読み方が割れる。**2 欄の本文を持つ端点だけの規約**である。

## 3. 射程の分割（#1248 の 4 群のうち 2 群だけを本 PR で扱う）

#1248 は「**群ごとに PR を刻んでよい**」と明示している。本 PR は **`Results.BadRequest` 系の
手書き検証（群 1 ＋ 群 2）** に限る。

**この割り方を採った理由**: 群 1 と群 2 は**同じ応答の形**（`{ error }` の 1 欄）を持ち、
**同じ判定**（移送前後で状態コードも本文も同じ）で等価性を確かめられる。着地後の帰結も 1 行で言える
——「**射程内の純粋な入力検証のガード節が 0 になる**」。
群 3 は応答の形が違い（全違反を返す RFC7807）、群 4 は検証ではなく写像である。混ぜると
「何が終わったか」が PR 単位で言えなくなる。

**残りは追随 issue へ切り出す**（`Refs #1248`。起票前に重複を検索する）。

## 4. 変更するもの

### 4-1. GraphService（`src/knowledge/backend/Services/GraphService/`）

| 端点 | 移す箇所 | 新設する検証器 | 要求モデル |
| --- | --- | --- | --- |
| `POST /graph/edges` | `document_id_required` / `self_edge_not_allowed` | `Features/Graph/CreateEdge/CreateGraphEdgeValidator.cs` | 既存 DTO `CreateGraphEdgeRequest` |
| `GET /graph/{id}/neighbors` | `hops_out_of_range` / `edge_type_filter_invalid` | `Features/Graph/Neighbors/NeighborsQueryValidator.cs` | **新設** `NeighborsQuery` |
| `POST /graph/edge-types` | `name_required` / `invalid_layer` | `Features/EdgeTypes/Create/CreateEdgeTypeValidator.cs` | 既存 DTO `CreateEdgeTypeRequest` |
| `PUT /graph/edge-types/{id}` | `name_required` | `Features/EdgeTypes/Rename/RenameEdgeTypeValidator.cs` | 既存 DTO `RenameEdgeTypeRequest` |
| `GET /graph/ai-suggestions` | `invalid_state` / `invalid_kind` | `Features/AiSuggestions/List/ListAiSuggestionsQueryValidator.cs` | **新設** `ListAiSuggestionsQuery` |

**移さない 3 箇所（理由つき）:**

- `Features/Graph/CreateEdge/Endpoint.cs` の `unknown_edge_type` … **DB を引いた結果**であり、
  かつ認可の後ろにある（型の実在は文書の可視性と混ぜない、という既存の設計）。入力検証ではない。
- `Features/AiSuggestions/Approve/Endpoint.cs` の `unknown_tag` / `unknown_edge_type` …
  **後段（タグ辞書・辺の型辞書）の照会結果**である。入力検証ではない。

🔴 **`RenameEdgeType` の検証は 404 の後ろに残す**（不存在の型 ID への空名改名は 404 のまま）。
ハンドラ先頭へ上げると 404 が 400 に化ける。

### 4-2. DataSourceService（`src/knowledge/backend/Services/DataSourceService/`）

| 端点 | 移す箇所 | 新設する検証器 |
| --- | --- | --- |
| `PUT /datasources/{id}` | `config` / `defaultAttributes` の省略拒否 | `Features/DataSources/Update/UpdateDataSourceValidator.cs` |

移さない 3 箇所は §2-4 のとおり。

### 4-3. `.csproj` と `Program.cs`

- 両サービスへ `PackageReference Include="FluentValidation"`（**版は書かない**。CPM。
  `src/Directory.Packages.props` に `12.1.1` が既にある）と
  `ProjectReference` `Platform.Shared.Kernel.csproj`（ユニット外参照として許可された 3 プロジェクトの 1 つ。
  `IADR-0117` / `check-unit-dependencies.js` 規則 1）。
- `InternalsVisibleTo` は両サービスとも既にある（`GraphService.Tests` / `DataSourceService.Tests`）。
- 検証器の登録は **1 検証器 1 行の明示登録**（`AddValidatorsFromAssembly` を使わない。`IADR-0371` 決定 2）。

### 4-4. 実装 ADR

`IADR-0395`（仮番）＋ `.ai-context/adr/README.md` の索引行（**3 列・要約 200 字以内**）。

## 5. テスト

- 各検証器の単体テスト（規則ごとの成否 ＋ **メッセージは定数とリテラルの両方**へ当てる）。
- **宣言順の固定**: 複数違反を同時に起こしたとき `Errors[0]` が移送前の最初のガード節に対応することを見る。
- **端点越しの契約テスト**: 移送前後で**状態コードも本文も同じ**であることを見る
  （`Neighbors` は `error` と `message` の 2 欄）。
- **順序の固定**: `Neighbors` / `AiSuggestions.List` の検証が**認可より前**にあること
  （既存の `GraphTraversalTests` / `GraphEndpointsSecrecyTests` が緑のまま ＋ 追加）。
  `RenameEdgeType` は **404 が 400 より先**であることを端点越しに固定する。
- **変異試験 1 本以上**: 検証器から規則を 1 本外すと赤になることを実測し、出力を PR 本文に載せる。
- **既存テストの件数を減らさない**（純粋な移送であること）。

## 6. 受け入れ基準（Given-When-Then）

- [ ] Given 群 1・群 2 / When 着手する / Then §2 の判断が `IADR-0395` に残っている
- [ ] Given 移送した 10 箇所 / When 移送前後の応答を比べる / Then **状態コードも本文も同じ**である
- [ ] Given `Neighbors` / `AiSuggestions.List` / When 実装を読む / Then **検証が認可より前**にある
- [ ] Given `RenameEdgeType` / When 不存在の ID へ空名で PUT する / Then **404**（400 ではない）
- [ ] Given GraphService / DataSourceService の `.csproj` / When 読む / Then `Platform.Shared.Kernel` を参照している
- [ ] Given 射程内 / When 純粋な入力検証の手書きガード節を走査する / Then **0 箇所**
      （残る `Results.BadRequest` は §4-1・§2-4 の「入力検証ではない」7 箇所と射程外の BFF 1 箇所だけ）
- [ ] Given 各サービスのテスト / When 実行する / Then **件数が減っていない**
- [ ] Given 変異試験 / When 検証を外す / Then **赤になることを実測**している
- [ ] Given `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` を両ユニットで / When 実行する / Then 成功する
- [ ] Given `node scripts/check-backend-libraries.js` / `check-cpm-versions.js` / `check-unit-dependencies.js` /
      `check-coverage-floor.js` / `check-doc-*.js` / When 実行する / Then 緑である
