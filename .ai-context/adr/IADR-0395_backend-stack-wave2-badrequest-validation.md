---
title: IADR-0395 波 2 のうち `Results.BadRequest` 系の手書き検証だけを先に移し、検証器の置き場・クエリ引数の器・2 欄の応答・状態依存の検証の残し方を決める（何を入れ、何を入れなかったか）
type: impl-adr
status: Accepted
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
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (Accepted 2026-07-25) 決定・選定基準 3・4
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md (Accepted 2026-08-22) 決定 2・3
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md (fixed 2026-08-30) 基本方針・実装状況・Application 層
---

# IADR-0395: 波 2 の第 1 弾（`Results.BadRequest` 系）で先に決めたこと（#1248）

- 状態: Accepted
- 日付: 2026-09-05
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: `ADR-0030`（ライブラリ標準）／`ADR-0041`（Result 型）／`ADR-0068` 決定 2
  （スライス 3 段目の切り出し基準）／`ADR-0034` 決定 2・3（存在秘匿・hops 上限）／
  `NFR`（無採番。ライブラリ標準の浸透は保守性の非機能要件であり、計画側の要求表に当たる番号が無い）
- 関連する実装 ADR: `IADR-0371`（参照実装の置き方）／`IADR-0393`（波 1 の適用記録。本 ADR は
  その決定 4 が波 2 へ送った 4 群のうち 2 群に答える）／`IADR-0117`（`Platform.Shared.Kernel` の
  配置とユニット外参照 3 プロジェクト）／`IADR-0229`（Kernel が公開する `Result` / `Error` の操作面）／
  `IADR-0242` / `IADR-0272`（判定順が仕様になっている箇所）／`IADR-0295`（`ConnectionUri` の
  資格情報検査）／`IADR-0323`（提案一覧の文書絞りは 404 に倒さない）／`IADR-0282`（単一プロジェクト＋VSA）
- 関連する実装仕様書: `.ai-context/specs/20260905_issue-1248_backend-stack-wave2-badrequest-validation.md`
- issue: #1248（親 #1230 / #1064 / 環流 planning#490）

## コンテキストと課題

`IADR-0393` 決定 4 は、波 2 へ送るものを 4 群に分けた —— 群 1（GraphService の 12 箇所）・
群 2（DataSourceService の 4 箇所）・群 3（`ValidationProblem` 系 34 箇所）・群 4（追加引数のある写像 7 本）。

**本 ADR が答えるのは「なぜこの形か」ではない**（それは `IADR-0371` が持つ）。
答えるのは **「先に決めることは何で、どう決めたか。何を入れ、何を入れなかったか」** である。

着手時に基点 `facebfe9` で母集合を引き直した。

| 走査 | 実測（`facebfe9`） | 備考 |
| --- | --- | --- |
| `Platform.Shared.Kernel` への `ProjectReference` | **4/14 サービス** | 波 1 の 4 件（AiAnalysis / Conversion / Dashboard / Feedback） |
| FluentValidation / Riok.Mapperly の `PackageReference` | **各 4 件** | 同上（Mapperly は Feedback / Authorization / McpServer / Notification） |
| `Results.BadRequest` のガード節（手書き） | **17 箇所** | `IADR-0393` の「23 → 17」と一致 |
| 🔴 `Results.ValidationProblem` 系の呼び出し | **37 箇所** | **#1248 の「34」より 3 多い**（下記） |

🔴 **#1064 が掲げた「Kernel 4/14」は当時 0/14 だった**（VSA 移行で `ProjectReference` が落ち、
コメントだけ残っていた）。**いま再び 4/14 なのは偶然の一致であり、中身が違う** ——
当時の 4 件は「参照が無いのにコメントだけある」4 サービス、いまの 4 件は波 1 で
**実際に `Result` を使っている** 4 サービスである。**数が同じでも同じ状態ではない。**

🔴 **#1248 の「群 3 は 34 箇所」は狭い。** 内訳（DocumentService 20 ＋ AuthorizationService /
McpServer 14）に **DataSourceService の 2（`OwnerMappingValidation`）と NotificationService の 1
（`outcome.Errors` の透過）が入っていない。実測は 37 である。**

**陽性対照**（「n 件しか無い」を「無い」と読む前に走査器が生きていることを確かめた）: `PackageReference`
の走査は `WolverineFx` を 2 件、`ProjectReference` の走査は `*.Contracts.csproj` を 23 件でヒットする。

## 検討した選択肢

### 本 PR の射程

1. **4 群すべてを 1 PR で。** #1248 の受け入れ基準を 1 回で満たすが、検証 57 箇所前後 ＋ 写像 7 本 ＋
   応答形式 2 種へ同時に触る。レビュー単位が壊れる。
2. **サービス単位で割る**（GraphService の PR、DocumentService の PR …）。DocumentService は
   群 3 と群 4 の両方を持つので、1 本の PR に応答形式の新規約と写像の新規約が同居する。
3. **応答の形で割る**（`Results.BadRequest` 系 ＝ 群 1 ＋ 群 2 を先に、`ValidationProblem` 系と写像は後）。

### `Neighbors` / `AiSuggestions.List` の検証と認可の順序

- A: 検証器を端点フィルタ（`IEndpointFilter`）へ載せ、認可より前に走らせる。
- B: `IValidator<T>` をハンドラの引数で受け、**従前のガード節が居た位置で実行する**。

### クエリ引数を `AbstractValidator` に載せる器

- A: 要求モデル（`record`）を起こす。
- B: `AbstractValidator<string?>` のような素の型に対する検証器を並べる。
- C: 端点に残す（移送しない）。

## 決定

### 決定 1: 射程は「応答の形」で割り、本 PR は `Results.BadRequest` 系に限る（選択肢 3）

群 1（GraphService）と群 2（DataSourceService）は**同じ 1 欄の本文**（`{ "error": "..." }`）を持ち、
**同じ判定**（移送前後で状態コードも本文も同じ）で等価性を確かめられる。着地後の帰結も 1 行で言える。

**サービス単位（選択肢 2）を採らなかったのは、DocumentService が群 3 と群 4 の両方を持つからである。**
1 本の PR に「全違反を返す応答をどう固定するか」と「Mapperly の複数源引数をどう扱うか」という
**別々の新規約が同居する** —— レビュアーはどちらかを流し読みすることになる。

#1248 自身が「群ごとに PR を刻んでよい」と明示しており、この割り方は起票の想定内である。

### 決定 2: `IValidator<T>` は引数で受け、**実行は従前のガード節が居た位置に置く**（選択肢 B）

🔴 **端点フィルタ（選択肢 A）を採らない。** 理由は 2 つある。

1. **順序が読めなくなる。** `Neighbors` と `AiSuggestions.List` は**検証が認可より前**にあることが
   仕様であり（後ろへ動かすと文書の存在が漏れる）、`IADR-0242` / `IADR-0272` が積み上げた判定順の
   設計にそのまま乗っている。フィルタへ出すと、**順序は登録の並びが決めることになり、ハンドラを
   読んでも判らない。** 既存の 🔴 注記（CodeQL `cs/user-controlled-bypass` の指摘ごと理由を書いたもの）
   が指すべき行が消える。
2. **`RenameEdgeType` は逆向きである。** 移送前の検証は `db.EdgeTypes.FirstOrDefaultAsync` の**後ろ**に
   あり、**不存在の型 ID への空名改名は 404 である。** フィルタは端点の手前で走るので、
   この 1 本だけ**必ず 400 に化ける。** 「全部フィルタへ」は成り立たない。

→ **`IValidator<T>` はハンドラの引数で受け（解決は DI）、実行行を従前のガード節の位置へ置く。**
🔴 **引数の並びは順序の証拠にならない**ので、その旨を各端点のコメントに明記した。

### 決定 3: クエリ引数には**その 1 操作専用の要求モデルを起こす**（選択肢 A）

`hops` / `types` / `state` / `kind` は端点の引数であって DTO ではない。3 段目
（`Features/<集約>/<操作>/`）に `internal sealed record` を置く（`ADR-0068` 決定 2 の適用）。

- `NeighborsQuery(int? Hops, string? Types)`
- `ListAiSuggestionsQuery(string? State, string? Kind)`

**素の型に対する検証器（選択肢 B）を採らない** —— `IValidator<string?>` は DI の鍵として
衝突するうえ、**規則の宣言順（＝応答の契約）を 1 つの型の中に並べられない。**

🔴 **要求モデルは端点の引数一覧の複製ではない。** 検証しない引数（`by` / `documentId`）は載せない。
載せると「検証されているように見えるが規則が無い」欄ができる。`documentId` は
**不存在・権限外でも 404 に倒さないのが仕様**（`IADR-0323` 決定 2）であり、検証対象ではない。

**端点の署名は変えない。** `[AsParameters]` へ束ねると OpenAPI の生成が変わり得るので、
振る舞いを変えない制約から出る。端点が受け取った引数を要求モデルへ詰め替えてから検証する。

### 決定 4: `Neighbors` の 2 欄の本文は `ErrorCode` ／ `ErrorMessage` へ割り当てる

移送した 10 箇所のうち **8 箇所の本文は `{ "error": "<文字列>" }` の 1 欄**で、波 1 の形
（`Errors[0].ErrorMessage` を `error` に載せる）がそのまま使える。

**`Neighbors` の 2 箇所だけが `{ "error": "<機械語>", "message": "<説明文>" }` の 2 欄**である。
検証器が `WithErrorCode` と `WithMessage` の両方を宣言し、端点は
`Error.Validation(Errors[0].ErrorCode, Errors[0].ErrorMessage)` として
**`Error.Code` を `error` へ、`Error.Message` を `message` へ**写す。

🔴 **1 欄の 8 箇所へこの規約を広げない。** 広げると `error` の値の出どころが `ErrorCode` 由来になり、
波 1 の 6 サービス（`Error.Message` 由来）と読み方が割れる。**2 欄の本文を持つ端点だけの規約**である。

### 決定 5: `types` は**検証と解析を分ける**

移送前は `Guid.TryParse` の結果をそのまま `edgeTypes` として使う融合ループだった。
**検証器は形式だけを見て、解析（`HashSet<Guid>` の構築）は端点に残す。**

- **検証器が `IReadOnlySet<Guid>` を持ち出すと、それは検証器ではなく解析器になる。**
  `AbstractValidator` の戻りは `ValidationResult` であり副産物を返す口が無い。
  `ValidationContext.RootContextData` へ詰めると**規則の副作用**になり、「規則の宣言順が応答の契約」
  という `IADR-0371` 決定 2 の読み方と噛み合わない。
- 二度読みの費用は無視できる（`types` はクエリ文字列 1 本、要素数は辺の型辞書の規模で頭打ち）。
- 🔴 **区切り文字の指定を検証器の `internal const` に置き、端点がそれを使う。**
  片方だけ変えると「検証は通るが解析で落ちる」形になる（解析側は `Guid.Parse` であり、
  読めない要素は例外＝500 になる）。
- 🔴 **`parsed.Count > 0` のときだけ絞る**という移送前の縮退を保つ（`types=",,,"` は 400 ではない）。

### 決定 6: 状態に依存する検証は**端点に残す**（DataSourceService は 1 箇所だけ移す）

| 箇所 | 扱い | 理由 |
| --- | --- | --- |
| `Update` の `config` / `defaultAttributes` の省略拒否 | ✅ 移送 | 要求 DTO だけで判定できる純粋な入力検証 |
| `ConnectionUriPolicy.Validate`（Create / Patch / Update の 3 箇所） | ❌ 端点に残す | 下記 |
| `OwnerMappingValidation.ValidateAsync`（3 箇所） | ❌ 群 3 へ | RFC7807（全違反）＋ 外部の利用者名簿 |

`ConnectionUriPolicy` を移さない理由は 3 つある。

1. **位置を動かせない。** `Patch` / `Update` では `db.DataSources.FindAsync` の**後ろ**にあり
   （不存在は 404 が先）、ハンドラ先頭で回すと **404 が 400 に化ける。**
   **位置を動かせない時点で「端点入口の入力検証」ではない。**
2. **既存値（`ds.ConnectionUri`）が要る。** 検証器へ持ち込むには `RootContextData` か
   非同期規則 ＋ `DbContext` の注入が要り、**`Features/` の検証器へ Infrastructure 依存が入る。**
3. **`ConnectionUriPolicy` は `Domain/` のドメイン方針**であり、`SecretMask` の 1 本の判定規則
   （`IADR-0295` 決定 1）を共有している。器を替える理由が無い。

### 決定 7: 移送したもの・しなかったものの一覧

| サービス | 端点 | 移送 | 検証器 |
| --- | --- | --- | --- |
| GraphService | `POST /graph/edges` | ✅ 2 規則 | `CreateGraphEdgeValidator` |
| GraphService | `GET /graph/{id}/neighbors` | ✅ 2 規則 | `NeighborsQueryValidator` ＋ `NeighborsQuery` |
| GraphService | `POST /graph/edge-types` | ✅ 2 規則 | `CreateEdgeTypeValidator` |
| GraphService | `PUT /graph/edge-types/{id}` | ✅ 1 規則 | `RenameEdgeTypeValidator` |
| GraphService | `GET /graph/suggestions` | ✅ 2 規則 | `ListAiSuggestionsQueryValidator` ＋ `ListAiSuggestionsQuery` |
| DataSourceService | `PUT /datasources/{id}` | ✅ 1 規則 | `UpdateDataSourceValidator` |

**移送しなかった `Results.BadRequest`（射程内 7 箇所）:**

- **後段の照会結果である**（4 箇所）: `CreateEdge` の `unknown_edge_type`（DB を引いた結果。
  かつ認可の後ろ）／`AiSuggestions/Approve` の `unknown_tag` / `unknown_edge_type`
  （タグ辞書・辺の型辞書の照会結果）。**入力検証ではない。**
- **状態に依存する**（3 箇所）: `ConnectionUriPolicy.Validate`（決定 6）。

**射程外**: `Platform.Bff` の `/bff/auth/logout` 1 箇所（セッション `sid` の一致検査。
要求 DTO の検証ではなく、本文も返さない）。

### 決定 8: 等価性は**状態コード ＋ 本文 ＋ 判定の位置**で固定する

`IADR-0371` 決定 2・`IADR-0393` 決定 3 をそのまま適用したうえで、**位置**を足す。

- **規則の宣言順を移送前のガード節の順に揃える**（端点は `Errors[0]` を本文へ載せる）。
- **メッセージは `internal` 定数として検証器が持ち、試験は定数とリテラルの両方へ当てる。**
- **述語も元のまま写す。** `NotEmpty()` へ置き換えなかったのは、移送前が
  `EdgeType.Normalize`（`Trim()`）を掛けた後の空判定であり、**`NotEmpty()` の空判定と一致するかが
  ライブラリの版に依存する**ためである。確かめられない置き換えを移送に混ぜない。
- 🔴 **述語の粒度も写す。** `CreateEdge` の両端必須と `Update` の省略拒否は移送前が **1 本の `||`**
  であり、2 本の `RuleFor` へ割ると**違反の件数が変わる**（`Errors[0]` は同じでも、件数を見る試験が
  将来書かれたときに移送前と食い違う）。1 本の述語のまま写した。
- 🔴 **判定の位置を試験で固定する**（本 ADR で足した分）:
  - `Neighbors` / `AiSuggestions.List` … 何も見えないスコープでも 400 が返る（**検証が認可より前**）。
  - `RenameEdgeType` … 不存在の ID ＋ 空名は **404**（400 ではない）。
  - `Update` … 不存在の ID ＋ 省略は **400**（404 ではない）。
- **登録は 1 検証器 1 行の明示登録**（`AddValidatorsFromAssembly` を使わない）。

### 決定 9: 残る 2 群は追随 issue へ切り出し、#1248 を親として追跡する

**#1248 は閉じない。** 本 PR は `Refs` に留め、群 3（`ValidationProblem` 系 37 箇所）と
群 4（追加引数のある写像 7 本）は追随 issue が持つ。

## 理由

- 決定 1〜3 はいずれも計画へ逐語で当たった結果である。`ADR-0030` §決定 は用途ごとのライブラリを
  指定するだけで、**適用サービス数も検証器の置き場も要求モデルの起こし方も定めていない。**
  `ADR-0041` 決定 2 は参照の向きの制約である。**計画の裁定を要する事項は 1 つも無く、
  planning への `decision-needed` 起票はしていない**（起票前に同件を検索したうえでの判断である）。
- 決定 2 の「フィルタへ出さない」は、**順序が仕様になっている箇所へ器の変更を持ち込まない**という
  `IADR-0393` 決定 4 の警戒をそのまま実装したものである。`RenameEdgeType` の 1 本が
  「全部フィルタへ」を機械的に否定したことが、判断の決め手になった。
- 決定 5 の「検証と解析を分ける」は、分けた結果として**解析側が `Guid.Parse`（例外を投げる）になる**
  という危うさを持つ。これは**区切り指定を 1 箇所に置き、変異試験で赤になることを実測する**ことで
  引き受けた —— 実測では `types` の規則を外すと検証器の単体試験 3 本・端点の契約試験 1 本・
  既存の `EdgeTypeFilterTests` 2 本の計 6 本が落ちる。
- 決定 6 は `IADR-0371` 決定 1 の適用である —— **義務は「その関心を実装する箇所では標準ライブラリを
  使うこと」**であり、入力検証ではないものを検証器へ押し込むことではない。

## 結果

- 良い影響: `Platform.Shared.Kernel` の実参照が **4/14 → 6/14**、FluentValidation が **4 → 6** になった。
  **射程内の純粋な入力検証の手書きガード節は 0 になった**（残る `Results.BadRequest` 7 箇所は
  「入力検証ではない」ものだけである）。GraphService のテストは 386 → 446、
  DataSourceService は 234 → 245 で、**1 本も減っていない。**
- 悪い影響 / トレードオフ:
  - **#1248 の受け入れ基準は本 PR では満たしきらない**（4 群のうち 2 群）。残りは追随 issue が持つ。
  - 🔴 **`Neighbors` の解析側が `Guid.Parse` になった** —— 検証を外すと 400 ではなく 500 になる。
    これは「検証器と端点が同じ区切り指定を使う」ことに依存しており、**型では守れていない。**
    変異試験で赤になることを実測して引き受けた（上記）。
  - **2 欄の応答規約（決定 4）が 1 種類だけ増えた。** 端点が 1 本しかないので今は読み分けられるが、
    2 本目が出たときは規約の側を見直す（増やし続ける形ではない）。
  - `Errors[0]` を採る形は、**将来「全違反を返す」応答へ変えるときに書き換えが要る**
    （`IADR-0371` から引き継ぐトレードオフ）。
- フォローアップ: 群 3（`ValidationProblem` 系 **37** 箇所。#1248 の数え 34 に DataSourceService 2 と
  NotificationService 1 を足した実測）と群 4（写像 7 本）を追随 issue へ切り出した。
