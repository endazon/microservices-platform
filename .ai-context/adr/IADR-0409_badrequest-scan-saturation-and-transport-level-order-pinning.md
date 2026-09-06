---
title: IADR-0409 `Results.BadRequest` の走査は移送の済／未を区別しないので受け入れ基準の判別子にしない。宣言順の契約は検証器の単体試験では固定できず、輸送を通す対が要る
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0030
  - ADR-0041
  - ADR-0068
  - IADR-0141
  - IADR-0229
  - IADR-0282
  - IADR-0371
  - IADR-0393
  - IADR-0395
  - IADR-0398
  - IADR-0408
author: claude
created: 2026-09-07
updated: 2026-09-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (Accepted 2026-07-25) 決定・選定基準 3・4
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md (Accepted 2026-08-22) 決定 2・3
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md (fixed 2026-08-30) 基本方針・実装状況・Application 層
---

# IADR-0409: 走査の飽和と、宣言順の契約を輸送越しに固定すること（#1230）

- 状態: Accepted
- 日付: 2026-09-07
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: `ADR-0030`（ライブラリ標準）／`ADR-0041`（Result 型）／`ADR-0068` 決定 2 ／
  `NFR`（無採番。ライブラリ標準の浸透は保守性の非機能要件であり、計画側の要求表に当たる番号が無い）
- 関連する実装 ADR: `IADR-0371`（参照実装。決定 2「宣言順が応答の契約」）／`IADR-0393`（波 1。
  本 ADR はその決定 3 の主張が 1 箇所で成立していなかったことを記録する）／`IADR-0395`（波 2 群 1・2）／
  `IADR-0398`（群 3）／`IADR-0408`（REST と gRPC が同じ本体を通る形）／
  `IADR-0141`（是正・追随の母集合の取り方）
- 関連する実装仕様書: `.ai-context/specs/20260907_issue-1230_remaining-validators.md`
- issue: #1230（親 #1064 / 環流 planning#490）

## コンテキストと課題

#1230 の残射程として「まだ裁定されていない手書き検証 4 箇所」を移すか、移さない理由を記録するのが
本作業の依頼であった。候補は次の 4 箇所である。

- `AiAnalysisService/Features/Analysis/Analyze/Endpoint.cs:26`
- `ConversionService/Features/ConversionJobs/CorrectFigure/Endpoint.cs:29`
- `DashboardService/Features/Dashboard/RecordEvent/Endpoint.cs:68`
- `DashboardService/Features/KnowledgeHealth/Report/Endpoint.cs:47`

母集合を基点 `07fc5ec5` で引き直した実測は次のとおりである。

| 走査（`':!*Tests*'` で試験を除外） | 実測 |
| --- | --- |
| `Results.BadRequest` | **18 行** |
| `TypedResults.BadRequest` ／ `TypedResults`（任意） | **0 件** |
| `Status400BadRequest` / `statusCode: 400`（`Produces*` 除く） | **1 件**（`BffSessionExtensions.cs:224`。セッション更新失敗の写像） |
| 陽性対照 `Results.Ok` | **133 行** |
| 陽性対照 `Results.NotFound` | **77 行** |

陽性対照が二重に立つので、`TypedResults` の 0 件は走査器の沈黙ではなく実際の不在である。

🔴 **その 18 行を 1 行ずつ開いた結果、候補 4 箇所はいずれも既に FluentValidation へ移送済みだった。**
`AnalyzeRequestValidator` / `FigureCorrectionValidator` / `RecordUsageEventValidator` /
`ReportKnowledgeHealthValidator` が現に存在し、端点（または `ReportKnowledgeHealthUseCase`）は
`IValidator<T>` を DI で受けている。**残っている `Results.BadRequest` の行は、
`Error.Validation` を HTTP へ写す 1 行**であって、手書きのガード節ではない。
移したのは波 1（#1257 / `IADR-0393`）である。

決めるべきことが 2 つ現れた。

1. **なぜ「残っている」と読めたのか。** 走査そのものの性質の問題である。
2. **移送済みなら本当に何もすることが無いのか。** 等価性の固定を確かめたところ、1 箇所で穴が開いていた。

## 検討した選択肢

### 何をもって「手書きのガード節が残っていない」と判定するか

- A: `Results.BadRequest` の件数で測る（#1230 の受け入れ基準が事実上そう読める）。
- B: 引数の出どころで判別する（検証器由来の `Error.Message` / `Errors[0].ErrorMessage` かどうか）。
- C: 機械検査器を新設する。

### 見つかった穴の直し方

- D: 何もしない（`IADR-0393` が「固定した」と書いているので記録上は閉じている）。
- E: 輸送を通す多重違反の試験を足す。
- F: 検証器の単体試験の側を厚くする。

## 決定

### 決定 1: 候補 4 箇所は 4 つとも「移送済み」。移さない裁定ではない

**一律の扱いにしないという依頼どおり 4 箇所を個別に見たが、結論は 4 箇所とも同じである。**
ただし理由は「移さないと決めた」ではなく「**当てる対象がもう無い**」である。
`IADR-0395` 決定 6・7 の物差し（純粋な入力検証か／位置が仕様か／応答の形）は、
移送先が既に存在する箇所には適用しようがない。

| 候補 | 判定 | 検証器 |
| --- | --- | --- |
| `Analyze:26` | 移送済み | `AnalyzeRequestValidator`（2 規則） |
| `CorrectFigure:29` | 移送済み | `FigureCorrectionValidator`（1 規則。判定の実体は Domain の `FigureMarkdown.IsEmbeddable`） |
| `RecordEvent:68` | 移送済み | `RecordUsageEventValidator`（1 規則。判定は契約側 `UsageEventType.IsValid`） |
| `KnowledgeHealth/Report:47` | 移送済み。ただし等価性の固定に穴（決定 3） | `ReportKnowledgeHealthValidator`（2 規則） |

**本 ADR で製品コードは 1 行も変えていない。**

### 決定 2: 🔴 `Results.BadRequest` の走査は移送の済／未を区別しない。受け入れ基準の判別子に使わない（選択肢 B）

**移送が成功するほど、この走査は当たり続ける。** 移送後の端点も同じ綴りで 400 を返すからである
（`IADR-0371` 決定 4 が「応答本文を変えない」と決めた帰結として、そうでなければならない）。

したがって #1230 の受け入れ基準「手書きのガード節が残っていない」を
**`Results.BadRequest` の件数で測ると永遠に 0 にならない。**
`IADR-0393` 結果が「手書きのガード節は 23 → 17 箇所」と書いた 17 も、
**17 箇所すべてが手書きという意味ではない**（うち 6 箇所は自分が移送した後の写像である）。

**判別子は引数の出どころである。**

- 移送済み: `Results.BadRequest(new { error = gate.Error.Message })`
  ／ `... outcome.Error.Message` ／ `... Errors[0].ErrorMessage`
- 未移送: 述語がその場に書かれ、リテラルまたは端点内ヘルパの戻りを本文に載せている

🔴 **機械検査器は新設しない**（選択肢 C）。`IADR-0141` の「同型の事故が 2 回起きたら」を満たしていない
—— 本件が 1 回目である。**1 回目は記録に留める。**

**この規約は「走査の飽和」という一般形を持つ。** 是正が対象の綴りを変えない種類の作業では、
是正前の語で引く走査は是正後も当たり続ける。`traceability.repo.md` の規則 10
（是正のたびに、この変更で新たに誤りになる自分の記述を引き直す）と同じ向きだが、
規則 10 が言うのは**記述**の話であり、本件は**走査器**の話である。

### 決定 3: 🔴 宣言順の契約は検証器の単体試験では固定できない。輸送を通す対が要る（選択肢 E）

`IADR-0371` 決定 2 の契約は 2 段ある。

1. **検証器の宣言順** —— どの違反が `Errors[0]` に来るか
2. **呼び出し側が `Errors[0]` を採ること** —— 宣言順が応答に効くのはこれがあるから

**検証器を直接呼ぶ単体試験は 1 しか通らない。** `ReportKnowledgeHealthUseCase.Validate` の添字は
そこを通らないので、`MultipleViolations_ReportsIndicatorFirst` があっても 2 は固定されない。
🔴 **違反が 1 件のときは `Errors[0]` と `Errors[^1]` が同じ要素を指すため、
単一違反の試験をいくら足しても添字の変更は区別できない。**

実測（変異試験。**変異が実際にファイルへ入っていることを毎回 `grep` で確かめた**）:

| 変異 | 対象 | 結果 |
| --- | --- | --- |
| M1 `Errors[0]` → `Errors[^1]` | `ReportKnowledgeHealthUseCase.cs:90` | 🔴 **本 PR 前: 全 90 試験が緑（穴）** ／ 本 PR 後: **2 本が赤** |
| M2 同じ変異 | `AiAnalysisService .../Analyze/Endpoint.cs:58` | **1 本が赤**（`AnalyzeResponseContractTests.MultipleViolations_ReturnsFirstRuleBody`）。**陽性対照** |
| M3 2 規則の宣言順を入れ替え | `ReportKnowledgeHealthValidator.cs` | **3 本が赤**（単体 1 ＋ 本 PR の 2） |
| M4 明示登録の 1 行を落とす | `DashboardService/Program.cs:79` | **30 本が赤** |

M2 が陽性対照である —— **同じ形の穴が `Analyze` には無い**。`IADR-0371` が参照実装で
`MultipleViolations_ReportsAnswerIdFirst`（単体）と `MultipleViolations_ReturnsFirstRuleBody`（端点越し）を
**対で**置いた理由がこれであり、波 1 は `Analyze` にはその対を写したが
`KnowledgeHealth/Report` には写していなかった。

M4 は `IADR-0371` 決定 2 の「アセンブリ走査を使わないのは、検証器を消したときに黙って
無検証にならないようにするため」という主張の実測である —— **30 本が赤になり、確かに黙らない。**

🔴 **`CorrectFigure` と `RecordEvent` に同じ対は要らない。** 検証器が `RuleFor` 1 本 ＋ `Must` 1 本であり、
**違反は構造上最大 1 件**である。M1 と同じ変異を当てても意味が変わらない（等価変異）。
**試験の不足ではないので足さない。**

### 決定 4: 輸送が 2 つある本体では、輸送ごとに対を置く

`ReportKnowledgeHealthUseCase` は **REST の端点と gRPC の rpc が通る同じ 1 本**である（`IADR-0408`）。
本 PR は REST と gRPC の両方に多重違反の試験を置いた。

**片方だけで足りるか**という問いには「M1 を殺すだけなら足りる」が答えだが、採らない ——
`IADR-0408` の主張は「**本体が 1 本だから 2 つの輸送で同じ**」であり、
その主張を測るのは**両方を見る試験**だけである。輸送側の写像（gRPC の `Status.Detail` への変換）は
本体とは別の行にあり、そこが割れる退行は REST の試験では捕まらない。

### 決定 5: `IADR-0393` 決定 3 へ日付つき追記を置く

決定 3 の「波 1 で移した 6 箇所すべてについて宣言順を固定した」は、
`KnowledgeHealth/Report` について**成立していなかった**。
新しい IADR で言い直すのではなく、**主張した場所に追記を置く**
（`traceability.repo.md`「Superseded / Deprecated な ADR を引用するときの書式」の
`［YYYY-MM-DD 追記 / #NNN］`。live な権威文書が対象）。

## 理由

- 決定 2 は、`IADR-0395` が自分の母集合を「#1248 の 34 は狭い、実測は 37」と直したのと同じ作業を
  **走査器そのものに対して**行った結果である。件数を直しても、**件数の意味が違っていれば直らない。**
- 決定 3 の穴は「宣言だけの監査は不合格」（運用ガイド）の実例である ——
  `IADR-0393` 決定 3 は固定したと**書いて**おり、`ReportKnowledgeHealthValidatorTests` という
  **それらしい試験も実在した**。差は「その試験が実際にどの行を通るか」であり、
  **変異を当てるまで区別できなかった。**
- 決定 3 は `IADR-0395` の 2026-09-06 追記（コード注記が指す試験名が 2 箇所とも誤っていた／
  neighbors の 404 をどの試験も見ていなかった）と**同型の事故の 2 回目**である。
  🔴 ただし `IADR-0141` の「2 回で検査器」を発動させない —— 前者は**注記の指し先**の誤り、
  本件は**試験の到達範囲**の不足であり、機械で見る対象が違う。同型と数えるのは
  「実測せずに固定したと書いた」という**書き手の癖**の水準であって、検査器を書ける水準ではない。

## 結果

- 良い影響: `KnowledgeHealth/Report` の宣言順の契約が **2 つの輸送で**固定された。
  DashboardService の試験は **90 → 92** で 1 本も減っていない。
  #1230 の受け入れ基準を測る判別子が言葉になった（決定 2）。
- 悪い影響 / トレードオフ:
  - **決定 2 に機械検査が無い。** 走査の飽和は人が気付くしかない（1 回目なので記録に留めた）。
  - **#1230 の受け入れ基準の文言はそのままである。** 判別子を本 ADR に書いたが、
    起票の本文を書き換えていない —— 起票側の判断が要る（下記）。
  - 決定 4 の対を輸送ごとに置く形は、輸送が 3 つになれば 3 本になる。
    いまは 2 つなので受容する。
- フォローアップ / 🔴 **人の裁定が要る点**:
  1. **#1230 の受け入れ基準「手書きのガード節が残っていない」の判定方法。**
     `Results.BadRequest` の件数では永遠に満たせない（決定 2）。文言を直すか、
     判別子を明記するかは起票側の判断である。
  2. **#1230 に残る本当の残射程は群 3 の 3 件である** —— `IADR-0398` が
     「適用は後続 PR」と書いた `ObsidianSync/Push` の `RuleSet`（決定 3 末尾）・
     McpServer の `kind` / `egressTier`（決定 5）・NotificationService（決定 7）。
     **`Results.BadRequest` 系ではないので本 PR の候補ではなく、未着手のまま引き継ぐ。**
     追随 issue を切るかどうかは起票側の判断である。
