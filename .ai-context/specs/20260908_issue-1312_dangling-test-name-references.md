---
title: コード注記が指す試験名が実在しない箇所を直し、機械で止める（#1312）
type: spec
status: done
related_ids: [NFR, ADR-0004, ADR-0051, IADR-0130, IADR-0141, IADR-0377, IADR-0379, IADR-0400, IADR-0406]
author: Claude（実装）
created: 2026-09-08
updated: 2026-09-08
---

# 仕様書: 指し先の実在しない試験名を直し、機械で止める（#1312）

## 起点となる計画書（トレーサビリティ）

- 非機能要件: NFR（当たる番号が無い。[[IADR-0188]] 決定 1 の運用）
- 実装 ADR: [[IADR-0130]]（0 件走査を緑にしない）／[[IADR-0141]]（同型の事故が 2 回で機械を置く）
- issue: #1312

## 🔴 母集合を引き直したら **5 件ではなく 7 件**だった

issue は 5 件（6 件目 `RenameEdgeTypeOrderTests` は PR #1311 で解消済み）と書いていた。
**同じ走査を develop `e568c0eb` で回すと 7 件出る。**

走査: `src/**/*.cs`（submodule 除外）のコメント行から `*Tests` を集め、
`class|record|struct|interface` の宣言と突き合わせる。
**参照 154 名（延べ 269 出現） / 宣言 393 名。**

**陽性対照**: 走査器が 7 件を返し、そのうち 5 件が issue の列挙と一致する
（＝走査器は死んでいない）。既知の誤り `RenameEdgeTypeOrderTests` は**出ない**（#1311 で解消済み）。

## 除外（理由つき。規則 6）

| 名前 | 参照元 | 除外の理由 |
| --- | --- | --- |
| `IntegrationTests` | 複数 | **アセンブリ名**であって型ではない |
| `EndpointTests` | 複数 | **グロブ表記**（`*EndpointTests` の意）であって特定の型ではない |
| `AiSuggestionWiringTests` | `AiSuggestion.cs:169` / `AiSuggestionStateMachineTests.cs:13` | 🔴 **走査器の誤検出である。** 2 箇所とも `［2026-08-28 追記 / #438］` の日付つき追記で **「そのテストは一度も存在しない」と書いている当の記述**であり、**指し先の主張ではなく、その否定**である。**直すと記録を壊す。** 機械では区別できないので、検査器では allowlist へ入れる |

## 直す対象（6 件）

| # | 参照（実在しない） | 実在する指し先 | 種別 |
| --- | --- | --- | --- |
| 1 | `GrpcSuggestionClientTests` | `LlmGatewayGrpcSuggestionClientTests` | 接頭辞の欠落 |
| 2 | `GrpcDiagramCoderTests` | `LlmGatewayGrpcDiagramCoderTests` | 接頭辞の欠落 |
| 3 | `SimilaritySourceLoggingTests` | `TermOverlapSimilarityCandidateSourceTests` | 別名 |
| 4 | `CreateDocumentAttributeValidationTests` | `ValidationProblemContractTests` | 別名 |
| 5 | `KnowledgeHealthReportGrpcTests` | `GrpcKnowledgeHealthReportTests` | **語順の入れ替え** |
| 6 | `PrivateNoteEndpointsMappingTests` | 🔴 **無い** | **試験を足す** |

🔴 **指し先を直すだけで済ませない。** issue の手順 2 のとおり、
**「その試験が本当にその帰結を固定しているか」を変異試験で実測してから**直す。

## 6 番だけは試験を足す

`PrivateNoteEndpoints.cs:113` の縮退

```csharp
internal static PrivateNoteDto ToDto(PrivateNote n, Document? doc)
    => PrivateNoteMapper.ToDto(n, doc?.Title ?? string.Empty, doc?.Version ?? 0);
```

を通す試験は **0 件**である（`doc == null` の経路を踏む試験が無い）。
注記は「端に残した縮退は `PrivateNoteEndpointsMappingTests` が見る」と書いているが、
**見ている機械は居ない。**

`PrivateNoteMapper.cs:14` が「移送前がやっていた `doc?.Title ?? string.Empty` /
`doc?.Version ?? 0` は**導出の指示**であり」（生成マッパへ持ち込むと
`?? throw new ArgumentNullException` に化ける）と書いているとおり、
**この縮退は端の判断として意図的に残されている**。そこを固定する。

## 検査器を置くか —— 置く（[[IADR-0141]] の条件を満たす）

issue が「条件は満たしている」と書いたとおり、**同型の事故は 7 回目**である
（`RenameEdgeTypeOrderTests` を含めると 8）。
**「実在しない名前を指す」側は機械で判定できる**（コメント中の `*Tests` を型宣言と突き合わせる）。

🔴 **判定できないのは「指し先は実在するが、その帰結を固定していない」側**である。
そちらは機械を置かない —— **主張の中身は人が読むしかない**。**検査器の射程をそう明記する。**

## テスト（受け入れ基準）

- [x] 6 件それぞれについて、**指し先の試験がその帰結を固定していること**を変異試験で実測した
- [x] 6 番は試験を足し、変異で赤になることを実測した
- [x] 検査器 `check-test-name-references.js` が現状の追跡下ファイルで **0 件**を返す
- [x] 同検査器が、**実在しない名前を注記へ入れた変異**を検出する（陽性対照）
- [x] 同検査器の allowlist に `AiSuggestionWiringTests` が**理由つき**で入っている
- [x] `scripts.test.js` の自己診断件数が減らない

## 変異試験（実走した。実出力を記録する）

**5 件の指し先が「本当にその帰結を固定しているか」を、直す前に 1 件ずつ変異で確かめた。**
指し先を直すだけでは、**間違った試験を指し直すだけ**になり得るためである。

| # | 変異（実装を壊す） | 赤くなった試験 |
| --- | --- | --- |
| 1 | `LlmGatewayGrpcSuggestionClient` の `!body.Sent` を落とす | `LlmGatewayGrpcSuggestionClientTests.ProposeAsync_縮退した応答では提案を作らない(sent: False, stopReason: "end_turn")` |
| 2 | `DiagramCodingInterpretation` の `!result.Sent` を `false` へ | `LlmGatewayGrpcDiagramCoderTests.Grpc_の帰結は経路ごとに異なる(path: "egress-denied", coded: False, ...)`（＋ REST 側 `LlmGatewayDiagramCoderTests.Retains_when_egress_denied`） |
| 3 | 類似度のログへ候補件数を足す | `TermOverlapSimilarityCandidateSourceTests.Logs_only_the_origin_id_and_never_candidate_counts_or_ids` |
| 4 | 413 の門を属性検査の**後ろ**へ動かす | `ValidationProblemContractTests.Create_OversizedBodyWithMissingConfidentiality_Returns413` |
| 5 | `[Authorize(Policy = ServiceCaller)]` を `[Authorize]` へ | `GrpcKnowledgeHealthReportTests.Report_with_forwarded_admin_user_token_is_permission_denied`（＋ `Grpc_service_declares_service_caller_policy`） |
| 6 | 縮退 `doc?.Title ?? ""` / `doc?.Version ?? 0` を外す | 🆕 `PrivateNoteMapperTests.ToDto_WhenTheDocumentIsNotYetReplicated_FallsBackToEmptyTitleAndVersionZero` |

**5 件とも、注記が主張していた帰結を指し先の試験が実際に固定していた。**
つまり**指し先の名前だけが間違っていた**（接頭辞の欠落 2 / 別名 2 / 語順の入れ替え 1）。
6 番だけは試験そのものが無かったので足した。

🔴 変異を戻したあと**試験が緑に戻ったこと**まで確認した（`grep MUT-A` の残留 0 件も併せて確認）。

## 検査器の実測

```console
$ node scripts/check-test-name-references.js --self-test
[check-test-name-references] 自己試験 13 件 OK。

$ node scripts/check-test-name-references.js
[check-test-name-references] src の C# 1237 件を走査
  （注記が指す試験名 153 名 / 宣言 393 名 / 除外 IntegrationTests, EndpointTests,
    AiSuggestionWiringTests, PrivateNoteEndpointsMappingTests）。
[check-test-name-references] OK: 注記が指す試験名はすべて実在します。
```

**陽性対照（実データでの変異）**: 追跡下の `AiSuggestion.cs` へ
`// TotallyFakeProbeTests が固定する。` を 1 行足すと

```
[check-test-name-references] 1 名の指し先が実在しません:
  - TotallyFakeProbeTests
      src/knowledge/backend/Services/GraphService/Domain/AiSuggestion.cs:204
```

で **exit 1**。戻すと緑に戻る。同じ変異を `scripts.repo.test.js` にも常設した。

## 🔴 実装中に判明したこと（予定に無かった事実）

### 是正の記録そのものが母集合を動かした（規則 8）

指し先を直したあと検査器を回すと、**まだ 1 件残っていた** ——
`PrivateNoteEndpointsMappingTests`。出所は**私が書いた是正の記録**である
（「以前は `PrivateNoteEndpointsMappingTests` を指していたが、その名前の試験は存在しなかった」）。

`AiSuggestionWiringTests` と**まったく同じ類型**である。
⇒ 走査結果は **「7 名 → 是正 6 名 → 記録が 1 名を戻す」**。
allowlist へ理由つきで足し、**この類型を足すときの条件**（指し先を作らないことが確定していること）を
検査器のコメントに書いた。

**名前を消して主張だけ残す形は採らない** —— 「何を直したか」が追えなくなる。

### この環境の Bash ヒアドキュメントでは検査器を書けなかった

`<<'EOF'` で囲っても行末の継続文字とエスケープが潰れ、
`unexpected EOF while looking for matching '` で書き込みごと失敗した（#1316 で記録した罠の 3 回目）。
**検査器の本体は Write ツールで書いた。**

## やらないこと

- `AiSuggestionWiringTests` の 2 箇所を書き換えること（**記録を壊す**）
- 「指し先は実在するが帰結を固定していない」側を機械で判定しようとすること（射程外）
- 指し先を**消して**主張を残すこと（issue の手順 1。取り下げるなら理由を書く）
