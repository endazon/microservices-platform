---
title: 作業仕様書 — #1230 残射程の `Results.BadRequest` 候補 4 箇所を数え直し、移送済みであることと等価性の穴を確かめる（#1230）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0030
  - ADR-0041
  - ADR-0068
  - IADR-0229
  - IADR-0282
  - IADR-0371
  - IADR-0393
  - IADR-0395
  - IADR-0398
  - IADR-0408
  - IADR-0409
author: claude
created: 2026-09-07
updated: 2026-09-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (Accepted 2026-07-25) 決定・選定基準 3・4
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md (Accepted 2026-08-22) 決定 2・3
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md (fixed 2026-08-30) 基本方針・実装状況・Application 層
---

# 作業仕様書: #1230 残射程の候補 4 箇所の裁定（#1230）

起点: 実装 issue #1230（親 #1064 / 環流 planning#490）。
先行は `IADR-0371`（参照実装 / #1064）・`IADR-0393`（波 1 / #1257）・`IADR-0395`（波 2 群 1・2 / #1248）・
`IADR-0398`（群 3 / #1278）・`IADR-0406`（群 4 / #1279）。

## 0. 前提の確認

- 基点 `origin/develop` = **`07fc5ec5`**。
- `git rev-parse --is-shallow-repository` = **`false`** —— 履歴の打ち切りではないので
  `git log` を出典に使える（planning#410 の作法）。
- `git submodule status src/ai-stock-trading` = **populate 済み**（`75075404`。先頭に `-` が無い）。
  未 populate だと約 510 試験が黙って消えるため、試験件数を語る前に確かめた。

## 1. 母集合を自分で引き直す

### 1.1 誤りの側から引く

依頼された「まだ裁定されていない手書き検証 4 箇所」を検証するため、
**`Results.BadRequest` の全出現**を引いた（`':!*Tests*'` で試験を除外）。

```console
$ git grep -n "Results.BadRequest" origin/develop -- src/knowledge/backend src/platform/backend ':!*Tests*'
→ 18 行
```

**依頼者の実測（18）と一致した。**

### 1.2 あり得る他の形を列挙して潰す（規則 9: 誤りの側の文字列で走査する）

「400 を返す手書き検証」は `Results.BadRequest` 以外の綴りでも書ける。次を全部当てた。

| 走査 | 実測（`07fc5ec5`） | 扱い |
| --- | --- | --- |
| `TypedResults.BadRequest` | **0 件** | 該当なし |
| `Status400BadRequest` / `statusCode: 400`（`Produces*` 除く） | **1 件**（`BffSessionExtensions.cs:224`） | セッション更新失敗の写像。要求 DTO の入力検証ではない・射程外 |
| `ValidationProblem` | **36 ファイル**（呼び出しは群 3） | `IADR-0398`（#1278）の射程。本 PR の対象外 |

**陽性対照**（走査器が生きていることの確認。「n 件しか無い」を「無い」と読む前に）——
**同じ pathspec・同じ基点で**引いた実測:

| 陽性対照の走査 | 実測 |
| --- | --- |
| `Results.Ok` | **133 行** |
| `Results.NotFound` | **77 行** |

走査器は生きている。したがって `TypedResults.BadRequest` の 0 件は沈黙ではなく実際の不在である
（`TypedResults` そのものを引いても **0 件**であり、このリポジトリは `Results.` で統一されている）。

### 1.3 18 箇所の全数と除外理由

| # | 箇所 | 扱い | 出典 |
| --- | --- | --- | --- |
| 1-8 | GraphService 8（`AiSuggestions/Approve` ×2・`List`・`EdgeTypes/Create`・`Rename`・`Graph/CreateEdge` ×2・`Neighbors`） | 裁定済み。触らない | `IADR-0395` 決定 7 |
| 9-12 | DataSourceService 4（`Create`・`Patch`・`Update` ×2） | 裁定済み。触らない | `IADR-0395` 決定 6 / `IADR-0398` 決定 6 |
| 13 | `Platform.Bff/.../AuthBffEndpoints.cs:59` | 射程外（`sid` 一致検査） | `IADR-0393` 決定 4-5 / `IADR-0395` 決定 7 |
| 14 | `FeedbackService/.../Submit/Endpoint.cs:29` | 参照実装。**移送済みの HTTP 写像** | `IADR-0371` |
| 15 | `AiAnalysisService/.../Analyze/Endpoint.cs:26` | 🔴 **移送済み**（波 1） | `IADR-0393` 結果 |
| 16 | `ConversionService/.../CorrectFigure/Endpoint.cs:29` | 🔴 **移送済み**（波 1） | 同上 |
| 17 | `DashboardService/.../RecordEvent/Endpoint.cs:68` | 🔴 **移送済み**（波 1） | 同上 |
| 18 | `DashboardService/.../KnowledgeHealth/Report/Endpoint.cs:47` | 🔴 **移送済み**（波 1） | 同上 |

🔴 **候補 4 箇所（15〜18）はいずれも既に FluentValidation へ移送済みである。**
実測（`07fc5ec5`。ファイルの存在と参照を確認）:

| 候補 | 検証器 | 端点が受ける口 |
| --- | --- | --- |
| `Analyze` | `AnalyzeRequestValidator`（2 規則） | `IValidator<AnalysisTaskRequest>` を引数で受ける |
| `CorrectFigure` | `FigureCorrectionValidator`（1 規則） | `IValidator<FigureCorrectionRequest>` |
| `RecordEvent` | `RecordUsageEventValidator`（1 規則） | `IValidator<UsageEventRequest>` |
| `KnowledgeHealth/Report` | `ReportKnowledgeHealthValidator`（2 規則） | `ReportKnowledgeHealthUseCase` が `IValidator<KnowledgeHealthReportRequest>` を受ける |

**残っている `Results.BadRequest` の行は、`Error.Validation` を HTTP へ写す 1 行**であり、
手書きのガード節ではない。`IADR-0393` 結果の「手書きのガード節は 23 → 17 箇所（6 箇所を移送）」の
6 箇所がまさにこれである。

### 1.4 走査そのものの飽和（規則 10: 是正で新たに誤りになる記述を引き直す）

🔴 **`Results.BadRequest` の grep は、移送の済 / 未を区別しない。**
移送後も HTTP 写像として同じ綴りが残るからである。#1230 の受け入れ基準
「手書きのガード節が残っていない」を**この走査で判定してはならない**。
判別子は「`Results.BadRequest` の引数が検証器由来の `Error.Message` / `Errors[0].ErrorMessage` か」である。

## 2. 判断（候補 4 箇所それぞれ）

**一律にしない**という依頼どおり、4 箇所を個別に見た。結論は 4 箇所とも同じだが、
**理由は「既に移送済みだから」であり「移さないと決めたから」ではない。**
`IADR-0395` の物差し（純粋な入力検証か / 位置が仕様か / 応答の形）を当てるまでもなく、
**移送は完了しており、当てる対象が無い。**

| 候補 | 移した / 残した | 理由 |
| --- | --- | --- |
| `Analyze:26` | **移送済み**（本 PR で動かすものは無い） | 波 1（#1257）が `AnalyzeRequestValidator` へ移した。残る行は `Error` → 400 の写像 |
| `CorrectFigure:29` | **移送済み** | 同上（`FigureCorrectionValidator`）。判定の実体は `FigureMarkdown.IsEmbeddable`（Domain）に 1 つだけ置かれている |
| `RecordEvent:68` | **移送済み** | 同上（`RecordUsageEventValidator`）。判定は契約側 `UsageEventType.IsValid` |
| `KnowledgeHealth/Report:47` | **移送済み。ただし等価性の固定に穴があった** | 下記 3 節 |

## 3. 実際に見つかった穴（本 PR が直すもの）

移送済みであることを確かめる過程で、**`IADR-0393` 決定 3 が「固定した」と書いた等価性が、
`KnowledgeHealth/Report` では実際には固定されていない**ことが判った。

### 3.1 穴の形

`IADR-0371` 決定 2 の契約は 2 段ある。

1. **検証器の宣言順**（どの違反が `Errors[0]` に来るか）
2. **呼び出し側が `Errors[0]` を採ること**（宣言順が応答に効くのはこれがあるから）

`ReportKnowledgeHealthValidatorTests.MultipleViolations_ReportsIndicatorFirst` が固定するのは **1 だけ**である
（検証器を直接呼ぶ単体試験なので、`ReportKnowledgeHealthUseCase.Validate` の添字を通らない）。
**2 を通す試験は「違反が 2 件同時に起きる要求」でしか区別できず、それが無かった。**

対照的に `Analyze` は `AnalyzeResponseContractTests.MultipleViolations_ReturnsFirstRuleBody` が
**端点越しに**同じことを見ている（`IADR-0371` が参照実装で対にした 2 本の形）。

🔴 **`CorrectFigure` と `RecordEvent` には穴が無い** —— 検証器が `RuleFor` 1 本 ＋ `Must` 1 本であり、
**違反は構造上最大 1 件**である。`Errors[0]` と `Errors[^1]` は同じ要素を指すので、
そもそも区別できる変異が存在しない（等価変異）。**試験の不足ではない。**

### 3.2 影響範囲は REST だけではない

`ReportKnowledgeHealthUseCase` は **REST の端点と gRPC の rpc が通る同じ 1 本**である
（`IADR-0408`）。したがってこの添字が黙って変わると **2 つの輸送が同時に**壊れる。

## 4. 変更内容

**製品コードは 1 行も変えない。** 足すのは試験 2 本と記録である。

1. `DashboardService/Tests/.../KnowledgeHealth/KnowledgeHealthEndpointTests.cs`
   … REST 越しに「指標が未知 ＋ しきい値 0」を送り、本文が `IndicatorInvalidMessage` であることを固定。
2. `DashboardService/Tests/.../KnowledgeHealth/GrpcKnowledgeHealthReportTests.cs`
   … gRPC でも同じ要求で `Status.Detail` が `IndicatorInvalidMessage` であることを固定
   （**同じ本体を通ることの証拠**。輸送ごとに写像が割れていないことを見る）。
3. `IADR-0409` … 走査の飽和・4 箇所の裁定・穴の記録。
4. `IADR-0393` へ日付つき追記 … 決定 3 の「固定した」が 1 箇所で成立していなかったことを記録。

## 5. 受け入れ基準（Given-When-Then）

- [x] Given 候補 4 箇所 / When 実装を読む / Then すべて `AbstractValidator` へ移送済みであり、
      残る `Results.BadRequest` は `Error` → HTTP の写像 1 行である
- [x] Given `ReportKnowledgeHealthUseCase.Validate` の `Errors[0]` / When `Errors[^1]` へ変異させる /
      Then **変異前は全 90 試験が緑（穴）**、**本 PR 後は赤になる**
- [x] Given `Analyze` の同じ変異 / When 実行する / Then 赤になる（陽性対照。穴が無いことの確認）
- [x] Given 各サービスの試験 / When 実行する / Then **件数が減っていない**
- [x] Given `dotnet build` / `dotnet test` を両ユニットで / When 実行する / Then 成功する
- [x] Given 文書検査器一式 / When 実行する / Then 緑である

## 6. 除外したもの（全数と理由）

| 除外 | 理由 |
| --- | --- |
| GraphService 8 ・ DataSourceService 4 | 裁定済み（`IADR-0395` / `IADR-0398`）。依頼で明示的に触らないと指定 |
| `Platform.Bff` 1 | 射程外（`sid` 一致検査。`IADR-0393` 決定 4-5） |
| `FeedbackService` 1 | 参照実装（`IADR-0371`）。移送済みで、端点越しの多重違反試験も既にある |
| `ValidationProblem` 系 | `IADR-0398`（#1278）の射程。本 PR の候補ではない |
| `BffSessionExtensions.cs:224` | セッション更新失敗の写像。入力検証ではない |
| `IADR-0398` が「適用は後続 PR」と書いた 3 件（`ObsidianSync/Push` の `RuleSet` / McpServer の `kind`・`egressTier` / NotificationService） | **群 3 の残射程**であり `Results.BadRequest` 系ではない。本 PR の候補 4 箇所の外。🔴 未着手として `IADR-0409` に引き継ぐ |

## 7. 人の裁定が要る点

- **#1230 の受け入れ基準の判定方法**。「手書きのガード節が残っていない」を
  `Results.BadRequest` の件数で測ると**永遠に 0 にならない**（移送後も写像として残る）。
  基準の文言を変えるか、判別子を明記するかは起票側の判断である（`IADR-0409` に記録した）。
