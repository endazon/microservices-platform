---
title: 作業仕様書 — #1248 群 1（GraphService の検証移送）に残っていた 3 つの欠陥を直す（区切り文字の共有・試験名の指し先 2 件）
type: spec
status: done
related_ids:
  - FR-17
  - SC-09
  - SC-18
  - UC-10
  - ADR-0030
  - ADR-0034
  - ADR-0041
  - IADR-0371
  - IADR-0393
  - IADR-0395
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md 決定 2・3
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (Accepted 2026-07-25) 決定
---

# 作業仕様書: #1248 群 1 の追随（3 つの欠陥）

起点: 実装 issue #1248（群 1 は PR #1280 で着地済み。**本 PR は #1248 を閉じない**）。
着地記録は `IADR-0395`、作業仕様書は
`.ai-context/specs/20260905_issue-1248_backend-stack-wave2-badrequest-validation.md`。

## 0. 前提の確認

- `git rev-parse --is-shallow-repository` = **`false`**（履歴の打ち切りではないので `git log` を出典に使える）。
- 基点 `origin/develop` = **`ac2269ec`**（`git fetch origin develop` 後に確認）。ブランチは
  `fix/1248-group1-followups`。
- 触らない領域（他 PR が動いている）: `deploy/` / `scripts/check-realm-constraints.js` /
  `.ai-context/adr/IADR-0404*`。本 PR の差分はこれらを 1 バイトも含まない。

## 1. 直す 3 つの欠陥（すべて実測。ファイル:行は基点 `ac2269ec` のもの）

### 欠陥 1: `IADR-0395` 決定 5 の散文が事実と違う（共有していたのは区切り文字ではない）

決定 5 は「🔴 **区切り文字の指定を検証器の `internal const` に置き、端点がそれを使う。**」と書き、
「片方だけ変えると『検証は通るが解析で落ちる』形になる（解析側は `Guid.Parse` であり、
読めない要素は例外＝500 になる）」を**閉じたもの**として扱っていた。

実測（基点 `ac2269ec`）:

| 場所 | 実際に共有されているもの | 重複しているもの |
| --- | --- | --- |
| `.../Neighbors/NeighborsQueryValidator.cs:37` | `internal const StringSplitOptions TypesSplitOptions` の宣言 | — |
| `.../Neighbors/NeighborsQueryValidator.cs:59` | `TypesSplitOptions` を使う | **区切り文字のリテラル** |
| `.../Neighbors/Endpoint.cs:72` | `NeighborsQueryValidator.TypesSplitOptions` を使う | **区切り文字のリテラル** |

**共有されていたのは `StringSplitOptions` だけで、区切り文字は両側の重複リテラルだった。**
決定 5 が「閉じた」と書いた壊れ方は開いたままである。

### 欠陥 2: 存在しない試験名を指している（`RenameEdgeTypeOrderTests`）

`.../Features/EdgeTypes/Rename/RenameEdgeTypeValidator.cs:14` が
「`RenameEdgeTypeOrderTests` がこの帰結を固定する」と書いている。

`git grep -n "RenameEdgeTypeOrderTests" -- src/` が返すのは
**`RenameEdgeTypeValidator.cs:14` のこのコメント 1 行だけ**で、その名前の型宣言は 0 件である
（§2 の走査でも MISSING として出る）。

実際にこの帰結を固定しているのは
`GraphValidationResponseContractTests.RenameEdgeType_UnknownIdWithEmptyName_Is404NotBadRequest`
（`.../Tests/Features/Graph/GraphValidationResponseContractTests.cs:148`）。実在と中身を読んで確認した
（不存在の型 ID へ空白だけの名前で `PUT` し、404 であることを見る）。

### 欠陥 3: 別の場所を指している試験名（`GraphEndpointsSecrecyTests`）

`.../Features/Graph/Neighbors/Endpoint.cs:15` が
「`GraphEndpointsSecrecyTests` がこの帰結（本文・ヘッダで区別できないこと）を固定する」と書いている。

実測: `.../Tests/Features/Graph/GetNode/GraphEndpointsSecrecyTests.cs` の `[Fact]` は **3 本**で、
すべて `/graph/{id}`（GetNode）についてのものである
（`Unauthorized_missing_and_nonexistent_are_indistinguishable` /
`Returns_404_not_403_when_scope_is_not_granted` / `Returns_401_when_unauthenticated`）。
**`neighbors` を触る試験は 1 本も無い。** 指し先は空で、壊しても緑のままだった（§4 の変異 E で実測）。

## 2. 母集合（記憶で挙げず、誤りの側の文字列で走査した）

### 2-1. 走査の設計

**問い**: 「欠陥 2・3 と同型 ——『存在しない／的外れな試験名を指すコメント』——は他にあるか。」

- **誤りの側から引く**（規則 9）: 「あるべき参照」ではなく、**コメント中に現れる `Tests` で終わる識別子**を
  すべて集め、**実在する型宣言（`class` / `record` / `struct` / `interface`）の名前集合**と突き合わせる。
- **あり得る形を列挙する**: 行コメント・XML ドキュメントコメント・ブロックコメントの 3 形を
  切り出してから走査する（文字列リテラル中の一致は数えない）。
- **陽性対照を対で置く**: 既知の誤り `RenameEdgeTypeOrderTests` が **MISSING として出ること**を
  走査器の生存確認とした（出た）。あわせて **148 個の相異なる名前のうち 139 個が OK 側に出ること**を
  「宣言集合の側が空でない」ことの対照とした。

走査器は使い捨てで、リポジトリには残していない（scratchpad）。手順:

1. `git ls-files -- "src/**/*.cs"` から `obj/` `bin/` を除く。
2. 各ファイルの型宣言の名前を集める。
3. 各ファイルのコメント部分だけを切り出し、`Tests` で終わる識別子を集める。
4. 3 から 2 を引く。

### 2-2. 実測（基点 `ac2269ec`）

| 指標 | 値 |
| --- | --- |
| 走査した `.cs`（`obj/` `bin/` 除外） | **1211** |
| 集めた型宣言の名前 | **1581** |
| コメント中の `*Tests` 参照（出現） | **248** |
| 同（相異なる名前） | **148** |
| うち**宣言が存在しない**名前 | **9**（出現 22） |

**除外したもの（誤検出 2 名 / 出現 14）:**

| 名前 | 出現 | 除外理由 |
| --- | --- | --- |
| `IntegrationTests` | 13 | 試験クラス名ではなく**プロジェクト／アセンブリ名**の断片（`Knowledge.IntegrationTests`）。実在するのはディレクトリと `.csproj` である |
| `EndpointTests` | 1 | `BffEndpointCompositionTests.cs:174` の**グロブ表記**（`Bff` ＋ ワイルドカード ＋ `EndpointTests`）の断片。単独の型名ではない |

**除外したもの（誤りではない 1 名 / 出現 2）:**

| 名前 | 出現 | 除外理由 |
| --- | --- | --- |
| `AiSuggestionWiringTests` | 2 | `GraphService/Domain/AiSuggestion.cs:169` と `GraphService/Tests/Domain/AiSuggestionStateMachineTests.cs:13` はいずれも **「そのテストは一度も存在しなかった」と明記した #438 の是正追記**である。**誤った指し先ではなく、誤りを記録した文である。** 消すと記録が失われる |

**残った同型の欠陥（6 名 / 出現 6）:**

| 名前 | 参照元 | 本 PR で直すか |
| --- | --- | --- |
| `RenameEdgeTypeOrderTests` | `GraphService/Features/EdgeTypes/Rename/RenameEdgeTypeValidator.cs:14` | ✅ 欠陥 2 |
| `GrpcSuggestionClientTests` | `GraphService/Infrastructure/ExternalServices/LlmGatewayGrpcSuggestionClient.cs:58` | ❌ 下記 |
| `SimilaritySourceLoggingTests` | `GraphService/Infrastructure/Persistence/TermOverlapSimilarityCandidateSource.cs:18` | ❌ 下記 |
| `CreateDocumentAttributeValidationTests` | `DocumentService/Features/Documents/Create/Endpoint.cs:53` | ❌ 下記 |
| `GrpcDiagramCoderTests` | `ConversionService/Infrastructure/ExternalServices/DiagramCodingInterpretation.cs:56` | ❌ 下記 |
| `PrivateNoteEndpointsMappingTests` | `DocumentService/Tests/Features/PrivateNotes/PrivateNoteMapperTests.cs:16` | ❌ 下記 |

🔴 **直さない 5 件の理由**: いずれも **#1248 群 1（`Results.BadRequest` 系の検証移送）が触った
ファイルの外**にあり、由来する issue が別である（`GrpcSuggestionClientTests` /
`GrpcDiagramCoderTests` は `IADR-0400` の gRPC 経路、`CreateDocumentAttributeValidationTests` と
`PrivateNoteEndpointsMappingTests` は DocumentService、`SimilaritySourceLoggingTests` は
`ADR-0051` のログ方針）。**1 issue = 1 PR**（`IADR-0116` 規約 1）から本 PR には入れない。
**DocumentService / ConversionService は #1278 の PR が動いている領域でもある。**

🔴 **「同型が 2 回起きたら検査器」の条件は満たしている**（同型が 6 件）。ただし**検査器の追加は
本 PR の射程外**である —— 判定には「実在するが的外れ」（欠陥 3）の側が必要で、それは名前の
突き合わせでは決まらない。**本節の走査結果を記録に残すに留める。**

### 2-3. 「実在するが的外れ」の側（欠陥 3 と同型）

機械では決まらないので、**#1248 群 1 が触った 10 ファイル**（`IADR-0395` 決定 7 の表）に限って
人手で読んだ。`Tests` で終わる識別子を指すコメントは **2 箇所だけ**で、それが欠陥 2 と欠陥 3 である。
**同じ走査の OK 側**（`GraphService` 分 47 出現）も読み、他に指し先の食い違うものは見つからなかった
—— ただし **これは全数の目視であって機械検査ではない**（§6 参照）。

## 3. 直し方

### 欠陥 1

`NeighborsQueryValidator` に区切り文字の `internal const char TypesSeparator` を足し、
**検証器の規則と端点の解析の両方がそれを使う。**
以後「片側だけ変える」編集は**書けない**（リテラルが 1 つしかない）。

### 欠陥 2

`RenameEdgeTypeValidator.cs` の指し先を
`GraphValidationResponseContractTests.RenameEdgeType_UnknownIdWithEmptyName_Is404NotBadRequest` へ直す。
対になる `RenameEdgeType_ExistingIdWithEmptyName_Returns400WithOriginalBody` も併記する
（後者が無いと「常に 404」の実装が前者を通る）。

### 欠陥 3

`Neighbors/Endpoint.cs` の指し先を、**実際に順序を固定している 6 本**へ直す。
うち 5 本は既存で、走査ではなく**変異を当てて数え直した**（§4 の変異 C・C2）:

1. `GraphValidationResponseContractTests.Neighbors_HopsOutOfRange_Returns400WithBothFields`
2. `GraphValidationResponseContractTests.Neighbors_InvalidTypes_Returns400WithBothFields`
3. `TwoTierTraversalTests.Hops_out_of_range_is_still_rejected_before_authorization`
4. `EdgeTypeFilterTests.Malformed_types_returns_400_even_for_a_nonexistent_document`
5. `GraphTraversalTests.Hops_validation_does_not_leak_document_visibility`（可視と**不存在**の対）

🔴 **順序は既存 5 本で固定されている（「無ければ足す」の条件には当たらない）。**
足したのは、それらが持たない腕を埋める 2 本である:

6. `GraphEndpointsSecrecyTests.Neighbors_validation_runs_before_authorization_for_visible_and_hidden_alike`
   —— **実在するが不可視**の文書との対（上の 5 本は「不存在」か「スコープが空」しか見ていない）。
7. `GraphEndpointsSecrecyTests.Neighbors_unauthorized_missing_and_nonexistent_are_indistinguishable`
   —— **neighbors の 404 が本文・ヘッダで区別できないこと。これは 1 本も見ていなかった**
   （欠陥 3 のコメントが主張していたのはまさにこの性質である）。

置き場は既存の `GraphEndpointsSecrecyTests`（`Tests/Features/Graph/GetNode/`）のままにした ——
クラス名が指す範囲を注記で持たせ、**ファイル移動で差分を膨らませない**。

## 4. 変異試験（すべて実走。基点 `ac2269ec`、`GraphService.Tests`）

### 変異 A: 端点側の区切り文字だけを変える（**修正前**）

`Endpoint.cs:72` の分割の第 1 引数だけをセミコロンへ差し替えた。

```
失敗 GraphValidationResponseContractTests.Neighbors_TypesWithOnlySeparators_IsNotBadRequest
  System.FormatException : Unrecognized Guid format.
   at System.Guid.Parse(String input)
   at GraphNeighborsEndpoint...MoveNext() in ...\Neighbors\Endpoint.cs:line 73
失敗 EdgeTypeFilterTests.Multiple_types_are_a_union
  System.FormatException : Unrecognized Guid format.
失敗!   -失敗: 2、合格: 469、合計: 471
```

🔴 **「共有前は緑のまま」ではなかった** —— 片側だけ変える変異は **2 本**が捕まえる。
ただし**落ち方は 500（`FormatException`）**であり、`IADR-0395` 決定 5 が言った危険そのものである。
**捕まえていたのは試験であって、決定 5 が主張した構造上の保護は存在しなかった。**

### 変異 B: 共有した定数 `TypesSeparator` を変える（**修正後**）

```
失敗 NeighborsQueryValidatorTests.ValidTypes_Pass(types: "6d9f2e6a-...-0a1b2c3d4e5f, 7e8f9a0b-1c2"...)
失敗 NeighborsQueryValidatorTests.TypesWithOnlySeparators_Passes
  Expected result.IsValid to be True, but found False.
失敗 GraphValidationResponseContractTests.Neighbors_TypesWithOnlySeparators_IsNotBadRequest
  Expected resp.StatusCode to be HttpStatusCode.NotFound {value: 404} because 要素が 1 つも無い types は
  「絞らない」であり、検証を通って認可へ進む, but found HttpStatusCode.BadRequest {value: 400}.
失敗 EdgeTypeFilterTests.Multiple_types_are_a_union
  Expected res.StatusCode to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.BadRequest {value: 400}.
失敗!   -失敗: 4、合格: 469、合計: 473
```

🔴 **対比が本題である。** 修正後は同じ「区切り文字を変える」編集が**両側へ同時に効く**ため、
**`FormatException` は 1 件も出ない**（400 であって 500 ではない）。**片側だけ変える編集はもう書けない。**

### 変異 C: neighbors の認可（スコープ解決と `Granted` の判定）を検証の前へ

```
失敗 TwoTierTraversalTests.Hops_out_of_range_is_still_rejected_before_authorization
失敗 GraphValidationResponseContractTests.Neighbors_InvalidTypes_Returns400WithBothFields
失敗 GraphValidationResponseContractTests.Neighbors_HopsOutOfRange_Returns400WithBothFields
失敗!   -失敗: 3、合格: 470、合計: 473
```

### 変異 C2: neighbors の検証を**起点ノードの可視判定（`AuthorizedNode.Authorize`）の後ろ**へ

```
失敗 EdgeTypeFilterTests.Malformed_type_id_is_rejected_with_400
失敗 EdgeTypeFilterTests.Malformed_types_returns_400_even_for_a_nonexistent_document
失敗 TwoTierTraversalTests.Hops_out_of_range_is_still_rejected_before_authorization
失敗 GraphValidationResponseContractTests.Neighbors_InvalidTypes_Returns400WithBothFields
失敗 GraphValidationResponseContractTests.Neighbors_HopsOutOfRange_Returns400WithBothFields
失敗 GraphEndpointsSecrecyTests.Neighbors_validation_runs_before_authorization_for_visible_and_hidden_alike
失敗 GraphTraversalTests.Hops_validation_does_not_leak_document_visibility
失敗!   -失敗: 7、合格: 466、合計: 473
```

**変異 C と C2 を分けたのは、C では新設の可視／不可視の対が緑のままだったから**である
（対で使うスコープは `Granted = true` なので、C では検証が先に走る）。**危険な側は C2 である。**

### 変異 D: 改名の検証を型の照会（`db.EdgeTypes.FirstOrDefaultAsync`）の**前**へ

```
失敗 GraphValidationResponseContractTests.RenameEdgeType_UnknownIdWithEmptyName_Is404NotBadRequest
  Expected resp.StatusCode to be HttpStatusCode.NotFound {value: 404} because 検証を先頭へ上げると
  404 が 400 に化ける（移送は振る舞いを変えない）, but found HttpStatusCode.BadRequest {value: 400}.
失敗!   -失敗: 1、合格: 472、合計: 473
```

**落ちるのは 1 本だけで、それが欠陥 2 で指し先に据えた試験である。**

### 変異 E: neighbors の「文書なし」の 404 だけ本文を変える

共有の `GraphEndpoints.NotFound()` を、本文つきの `Results.NotFound(...)` へ差し替えた。

```
失敗 GraphEndpointsSecrecyTests.Neighbors_unauthorized_missing_and_nonexistent_are_indistinguishable
  Expected bodies.Distinct() to contain 1 item(s) because 応答本文に差があると、そこから存在の有無が読める,
  but found 2.
失敗!   -失敗: 1、合格: 472、合計: 473
```

🔴 **落ちるのは新設の 1 本だけである** —— **本 PR の前なら、この変異は全緑を通っていた。**
欠陥 3 が「指している試験を壊しても緑のまま」だったことの、直接の裏づけである。

## 5. 試験件数

| 時点 | 合格 | 失敗 | 合計 |
| --- | --- | --- | --- |
| 基点 `ac2269ec`（修正前） | 471 | 0 | **471** |
| 本 PR（修正後） | 473 | 0 | **473** |

**1 本も減らしていない**（+2 は §3 で足した 2 本）。

## 6. 確かめていないこと

- **「実在するが的外れ」の全数**。§2-3 は #1248 群 1 の 10 ファイル ＋ GraphService の OK 側 47 出現の
  **目視**であり、`src/` 全体については機械でも人手でも見ていない。
- **フロントエンド（TypeScript）側**。試験名は `describe` / `it` の**文字列**であって識別子ではなく、
  同じ走査が効かない。**走査していない**（範囲を絞った理由）。
- **`docs/` と `.ai-context/` の散文が指す試験名**。走査は `src/` 配下の `.cs` のコメントだけである。
- **検査器を置けるか**。§2-2 のとおり同型は 6 件あり条件は満たすが、欠陥 3 の側（実在するが的外れ）を
  機械で判定する方法を検討していない。
- **CI 上での挙動**。実走はローカル（Windows / .NET 10）のみである。
- **DataSourceService（群 2）に同型の欠陥があるか**。走査の対象には入っている（MISSING に 1 件も
  出ていない）が、「実在するが的外れ」の側は読んでいない。
- **`GraphEndpointsSecrecyTests` の置き場**。`Tests/Features/Graph/GetNode/` のままで
  neighbors の試験を持たせた。**移設が望ましいかは判断していない**（差分を膨らませないことを優先した）。
