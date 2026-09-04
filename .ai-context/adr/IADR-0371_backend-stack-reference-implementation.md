---
title: IADR-0371 計画スタック 3 種（FluentValidation / Riok.Mapperly / Platform.Shared.Kernel）は 1 サービスへ同時に入れて参照実装とし、残りは別 issue で展開する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0030
  - ADR-0041
  - ADR-0065
  - ADR-0068
  - IADR-0117
  - IADR-0195
  - IADR-0196
  - IADR-0229
  - IADR-0282
author: claude
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (Accepted 2026-07-25) 決定・選定基準 3・4
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md (Accepted 2026-08-22) 決定 2・3
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md (fixed 2026-08-30) 基本方針・実装状況・Application 層
---

# IADR-0371: 計画スタック 3 種の参照実装の置き方（#1064）

- 状態: Accepted
- 日付: 2026-09-04
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: `ADR-0030`（ライブラリ標準）／`ADR-0041`（Result 型）／`ADR-0065` §結果 の
  フォローアップ 6 ／ `ADR-0068` 決定 2（スライス 3 段目の切り出し基準）／`NFR`（無採番。
  ライブラリ標準の浸透は保守性の非機能要件であり、計画側の要求表に当たる番号が無い）
- 関連する実装 ADR: `IADR-0117`（`Platform.Shared.Kernel` の配置とユニット外参照 3 プロジェクト）／
  `IADR-0229`（Kernel が公開する `Result` / `Error` の操作面）／`IADR-0196`（Kernel だけに
  `CSharpFunctionalExtensions` を許す許可リスト）／`IADR-0195`（source generator の出力を
  カバレッジ集計から落とす）／`IADR-0282`（単一プロジェクト＋VSA）
- 関連する実装仕様書: `.ai-context/specs/20260904_issue-1064_backend-stack-reference-impl.md`
- issue: #1064（環流 planning#490 の追跡先）

## コンテキストと課題

計画 `12_backend-application-stack` §実装状況（2026-08-30 新設）は、Application 層のライブラリ標準が
**ほぼ浸透していない**と実測を掲げた —— FluentValidation 0 件・Riok.Mapperly 0 件・
`Platform.Shared.Kernel` を参照するサービス 4/14。

**着手時に引き直した実測は、`Kernel` についてはさらに悪い。**

| 要素 | 計画の実測（`0784dd2` / 2026-08-30） | 本作業の実測（`888e307d` / 2026-09-04） |
| --- | --- | --- |
| FluentValidation の `PackageReference` | 0 件 | **0 件**（陽性対照: `WolverineFx` は 2 件で拾える） |
| Riok.Mapperly の `PackageReference` | 0 件 | **0 件** |
| `Platform.Shared.Kernel` への `ProjectReference` | 4/14 サービス | 🔴 **0/14 サービス**（Kernel 自身のテストのみ） |

**4 → 0 は退行ではなく、移送の副作用である。** 単一プロジェクト＋VSA への移送（#1061〜#1063 /
`IADR-0282`）で層プロジェクト（`.Domain` / `.Application` 等）を撤去した際、そこに載っていた
`ProjectReference` が一緒に落ちた。**当該 4 サービスの `.csproj` には「Result / Error・DDD 基底型は
Platform.Shared.Kernel を使う」というコメントだけが残っている** —— 宣言が参照より長生きした形である。

決めるべきは 2 点である。

1. **どこまでを 1 つの PR の射程にするか**（14 サービス × 3 ライブラリは巨大すぎる）。
2. **3 ライブラリを「ライブラリごとに割る」のか「サービスごとに割る」のか。**

## 検討した選択肢

### 射程の割り方

1. **全 14 サービスへ 3 ライブラリを一度に入れる。** 計画の受け入れ基準を 1 回で満たすが、
   `Results.BadRequest` 26 箇所・写像 19 本・`.csproj` 14 個へ同時に触る。レビュー単位が壊れる。
2. **1 ライブラリ 1 PR（横に割る）。** 「FluentValidation を 14 サービスへ」「Mapperly を 14 サービスへ」。
   PR は 3 本に減るが、**1 本あたりが依然 14 サービス横断**であり、しかも 3 つの噛み合い方
   （検証の失敗を `Error` にし、成功時に写像する）が最後の PR まで誰にも見えない。
3. **1 サービスへ 3 ライブラリを同時に入れる（縦に割る）。残りは別 issue。**

### `Kernel` の `Result` を何に使うか

- A: 使わずに `ProjectReference` だけ足す（受け入れ基準の字面は満たす）。
- B: 失敗経路を `Result` / `Error` で表し、HTTP への写像を API 層に 1 箇所だけ置く。
- C: `Error` → ProblemDetails の共通変換ヘルパを `Platform.Shared.Infrastructure` へ新設する。

## 決定

### 決定 1: 射程は「1 サービス × 3 ライブラリ」で縦に割る（選択肢 3）

**`FeedbackService` を 3 ライブラリすべての参照実装とし、残り 13 サービスへの展開は別 issue へ切り出す。**

**「1 ライブラリ 1 PR」を採らなかった根拠は、計画に網羅の義務が書かれていないことである。**
逐語で当たった結果は次のとおり。

- `ADR-0030` §決定 は「マッピング = Riok.Mapperly、検証 = FluentValidation」と書く。
  **用途ごとに何を使うかの指定**であって、適用サービス数の指定ではない。
- `12_backend-application-stack` §Application 層 の表も「採否」の欄である。
- `ADR-0041` 決定 2 は「`Domain` / `Application` / `Api` / `Infrastructure` は `SharedKernel` が
  公開する型のみを参照し、外部ライブラリの型・名前空間を直接参照してはならない」——
  **`Result` を使うなら Kernel 由来であれ**という制約であり、全サービスに `Result` を使えとは書いていない。
- 同書 §実装状況 の「配備までの暫定手段」は、未参照の 10 サービスについて
  「**同型を使わず、例外と戻り値で表している**」「型の分裂は起きていない」と評価している。

→ **義務は「その関心を実装する箇所では標準ライブラリを使うこと」である。** 関心の無いサービスへ
空の参照を足すことを計画は求めていない。したがって「1 ライブラリ 1 PR」の前提（全サービス必須）が
成立せず、**割るなら関心の側＝サービス単位**になる。

`FeedbackService` を選んだのは、**3 つの関心を 1 サービスの中に全部持っている**ためである ——
ガード節 3 本（検証）・1:1 の `ToDto` 1 本（写像）・400 と 401 という 2 つの失敗経路（`Result`）。
展開時に写せる型が 1 本で済む。

issue #1064 自身が「サービス単位・関心単位で PR を刻んでよい。刻む場合は本 issue を親として
追跡する」と明示しており、この割り方は起票の想定内である。

### 決定 2: 検証は FluentValidation へ移し、**規則の宣言順を応答の契約として固定する**

手書きのガード節 3 本を `SubmitFeedbackValidator : AbstractValidator<FeedbackRequest>` へ移す。

🔴 **移送で振る舞いを変えない条件は「状態コードが同じ」では足りない。**
移送前のガード節は `if` の並び順どおり**最初の違反で返って**いた。FluentValidation は既定で
**全規則を走らせる**ため、そのままでは「どの違反が本文に出るか」が変わり得る。

したがって次の 2 つを規約として置く。

- **規則の宣言順を移送前のガード節の順に揃える**（`AnswerId` → `Rating` → `Comment`）。
- **端点は `Errors[0].ErrorMessage` を本文に載せる。**

**この 2 つは「宣言順が応答の契約の一部である」ことを意味する。** 順序を入れ替える変更が
入ったときに黙って本文が変わらないよう、**順序そのものを試験で固定した**
（`MultipleViolations_ReportsAnswerIdFirst` と、端点越しの `MultipleViolations_ReturnsFirstRuleBody`）。

メッセージ文字列は `internal` 定数として検証器が持ち、試験はその定数と**リテラルの両方**に当てる。
定数だけを見る試験は、定数ごと書き換わったときに緑のまま通ってしまう。

**登録はアセンブリ走査（`AddValidatorsFromAssembly`）を使わず、1 行 1 検証器の明示登録とする。**
走査は登録を暗黙にし、**検証器を消しても起動時には何も起きず、端点が黙って無検証になる**。

### 決定 3: 写像は Riok.Mapperly へ移し、**置き場は `ADR-0068` 決定 2 の基準で決める**

`FeedbackEndpoints.ToDto`（手書き）を `[Mapper]` の `FeedbackMapper.ToDto` へ置き換える。

**2 段目（`Features/Feedback/`）に置く。** 投稿と一覧の **2 操作が使う**ためであり、
`ADR-0068` 決定 2（1 操作にしか使われないものだけ 3 段目へ）の適用結果である。
**手書きだった頃と置き場は変わらない。** 変わったのは実体だけである。

**カバレッジの床は動かない。** 生成コードは
`obj/Debug/net10.0/Riok.Mapperly/Riok.Mapperly.MapperGenerator/FeedbackMapper.g.cs` に出るため、
`IADR-0195` 決定 1 の「`obj/` 配下の source generator 出力を集計から落とす」に既に入っている
（実測: 集計は source generator 259 クラスを除外している）。

### 決定 4: `Kernel` の `Result` は**失敗経路を束ねる**ために使う（選択肢 B）

`ProjectReference` を足すだけ（選択肢 A）は採らない —— **受け入れ基準の字面だけを満たし、
`ADR-0041` が求めた「エラー表現の一貫」を何も実現しない。**

共通変換ヘルパの新設（選択肢 C）も採らない。**本作業は振る舞いを変えない制約を持ち**、
既存端点の 400 の本文は `{ "error": "..." }` である。ProblemDetails への共通化は応答本文の変更を
伴うため、別の起票で扱う。

採るのは B である。投稿端点の 2 つの失敗経路を 1 つの値に束ね、**HTTP への写像を 1 箇所だけにする**。

```text
Validate(validator, req)      : Result          … 入力不正なら Error.Validation
  .Bind(() => Identify(http)) : Result<string>  … 未認証なら Error.Unauthorized
→ IsFailure なら ErrorKind で 400 / 401 を分け、成功なら Value が userId
```

- **判定の順序は移送前と同じ**（検証 → 利用者特定）であり、状態コードも本文も変わらない。
- ヘルパは端点ファイル内の `private static` 2 本に留める。**新しい層・新しいプロジェクトを作らない**
  （計画外の抽象化を足さない、というリポジトリの禁止事項）。

### 決定 5: 残射程は別 issue へ切り出し、本 issue を親として追跡する

`#1064` は閉じない。本 PR は `Refs` に留め、残り 13 サービスへの展開を子 issue が持つ。

## 理由

- 決定 1 は「計画が何を義務づけているか」を逐語で確かめた結果である。**issue 本文の数え（4/14）を
  転記せず引き直したことで、`Kernel` については前提そのものが変わっていた**（0/14）ことも判った。
  母集合を転記していたら、この PR は「4 サービスに残っている参照を 14 へ増やす」という
  存在しない作業から始まっていた。
- 決定 2 の「宣言順が契約」は、移送の等価性を状態コードだけで見ると取り逃す種類の退行である。
  **同型の事故（`IADR-0141`）はまだ 1 回目**なので検査器は置かず、試験で固定するに留める。
- 決定 4 の選択肢 A を退けたのは、`12_backend-application-stack` §実装状況 が
  「**暫定手段は『動いている統制』ではない**」と書いた指摘と同じ理由による ——
  参照だけがあって使われていない状態は、**適合しているように見える**という点で `.gitkeep` と同型である
  （計画は 2026-08-30 にその見え方を理由として `.gitkeep` 規範ごと撤回している）。

## 結果

- 良い影響: 3 ライブラリの噛み合い方（検証の失敗を `Error` にし、成功時に写像する）が
  **1 スライスの中に揃って見える**。残り 13 サービスの展開は、この 1 本を写す作業になる。
  `Platform.Shared.Kernel` の実参照が 0 から 1 になり、`IADR-0229` が公開した面が初めて使われた。
- 悪い影響 / トレードオフ:
  - **計画の受け入れ基準は本 PR では満たしきらない**（1/14 サービス）。残りは子 issue が持つ。
  - `Errors[0]` を採る形は、**将来「全違反を返す」応答へ変えるときに書き換えが要る**。
    それは応答本文の変更であり、本作業の制約（振る舞いを変えない）の外である。
  - 検証器の登録が明示（1 行 1 検証器）であるため、検証器が増えると `Program.cs` の行も増える。
    走査による自動登録に比べて手数は多いが、決定 2 の理由（消したときに止まること）を優先した。
- フォローアップ:
  - 残り 13 サービスへの展開（子 issue）。
  - `LlmGateway` の `IValidateOptions` 手書き 2 本の扱い（**設定値の検証**であり端点の入力検証とは
    器が違う。`ValidateOptions` との合流点を先に決める必要がある）。
  - `Error` → ProblemDetails の共通変換（応答本文の変更を伴うため、計画側の確認が要る）。
