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

## ★［2026-09-08 追記 / #1330 レビュー］走査の穴を 2 つ塞いだ

レビューが 🟢 として挙げた 2 点はどちらも**実データでは 0 件**だが、**片方は偽陰性**である。

| # | 穴 | 向き | 対応 |
| --- | --- | --- | --- |
| 1 | `commentOf` が最初のスラッシュ 2 つを探すので、`"http://…/FooTests"` の中をコメント扱いする | **偽陽性** | **リテラルを先に伏せてから**コメント開始を探す（`stripStringLiterals`） |
| 2 | 行コメントだけを見ており、**ブロックコメント内の主張を拾わない** | 🔴 **偽陰性** | ブロックコメントも走査する |

🔴 **2 のほうが重い。** 偽陽性は赤くなって気付くが、**偽陰性は「同じ主張をブロックコメントで書くだけで
検査を丸ごと逃れられる」**という逃げ道であり、**検査器の目的そのもの**（同じ事故を止めること）を壊す。
「実データが 0 件だから」で残さない。

**陽性対照（実データでの変異）**: 追跡下の `AiSuggestion.cs` へ
`/* BlockGhostProbeTests が固定する。 */` を 1 行足すと

```
[check-test-name-references] 1 名の指し先が実在しません:
  - BlockGhostProbeTests
      src/knowledge/backend/Services/GraphService/Domain/AiSuggestion.cs:204
```

で **exit 1**、戻すと緑。自己試験は **13 → 20 件**、`scripts.repo.test.js` は **753 → 755 本**。

**射程は広げていない** —— 見るのはあくまで「コメントが指す名前が実在するか」だけで、
帰結を固定しているかは人が読む。

### 🔴 追加の指摘（2 巡目）—— **同じ穴が宣言側にも残っていた**

1 巡目は**参照側**（コメントの中の名前）だけを正しく切り出し、**宣言側は生の行を見ていた**。
そのため次がいずれも「実在する」と数えられた:

```csharp
// public class GhostTests { }              ← コメントアウトされた宣言
/* public class GhostBlockTests { } */      ← ブロックコメント内の宣言
var s = "public class StringDeclTests { }"; ← リテラル内の宣言
```

⇒ **実在しない指し先が黙って通る。**「参照側の穴を塞いだ」と書いた当の PR に、
**同じ類型の穴が反対側に残っていた**。

**是正**: 行を**コード部分とコメント部分へ切り分ける** `splitLine` を置き、
**参照はコメントからだけ / 宣言はコードからだけ**集める。宣言側も**リテラルを伏せてから**見る。

**陽性対照（実データでの変異）**: `AiSuggestion.cs` へ

```csharp
// EscapeProbeTests が固定する。
// public class EscapeProbeTests { }
```

の 2 行を足すと **`EscapeProbeTests` が違反として出る**（1 巡目の実装では**通っていた**）。

自己試験 **20 → 26 件**、`scripts.repo.test.js` **755 → 756 本**。
`declared` の数は **393 のまま**であり、**生きている宣言を取りこぼしていない**ことも確かめた。

### 🔴 追加の指摘（3 巡目）—— **境界を決める当の関数が C# の文字列 3 種を区別していなかった**

`stripStringLiterals` は「バックスラッシュがエスケープ」という**通常文字列の規則を全種へ一律適用**していた。

| 種別 | 終端規則 | 旧実装の壊れ方 |
| --- | --- | --- |
| 通常 `"…"` | `\` がエスケープ・行内 | 正しい |
| verbatim `@"…"` | **`\` は素の文字**・`""` が引用符のエスケープ・行をまたぐ | 🔴 `@"C:\dir\"` の**閉じ引用符を読み飛ばし**、後続の `//` を丸ごと見落とす（偽陰性） |
| raw `"""…"""` | 開き引用符と**同数以上**で閉じる・行をまたぐ | 🔴 引用符の連なりを 1 つずつ数えるので**中身と外を取り違える**（両向き） |

**実測: `src` に verbatim 72 出現 / raw 330 出現。** 机上の話ではない。

**是正**: 行単位の伏せ字方式をやめ、**ファイル全体を 1 つの走査器 `scanSegments` で
「コード」「コメント」の区間へ切り分ける**。3 種の文字列・行コメント・ブロックコメントを
それぞれの終端規則で読み、**参照はコメント区間から / 宣言はコード区間から**取る。

🔴 **3 巡の穴はすべて「境界を決める規則が場所ごとに違う」ことに由来していた。**
規則を 1 か所に持てば、**片側だけ直せる形そのものが無くなる。**

実データの結果は**変わらない**（参照 153 名 / 宣言 393 名 / 違反 0 件）——
**変えたのは逃げ道の有無であって、今日の判定ではない。**
自己試験 26 件（内訳を入れ替え）、`scripts.repo.test.js` **756 → 757 本**。

### 🔴 追加の指摘（4 巡目）—— **綴りを 1 つずつ列挙したので漏れた**

3 巡目の是正は verbatim を `@"` **という綴りだけ**で見ていた。
C# 8 以降、補間つき verbatim は **`$@"…"` と `@$"…"` のどちらの順序でも書ける**ので、
`@$"` 順は `@` の次が `"` でないため verbatim と認識されず、
**通常文字列（バックスラッシュがエスケープ）として読まれていた** ——
つまり **3 巡目で塞いだのとまったく同じ偽陰性が、別の綴りで再現していた。**

実測（`src` の C#）: **`$@"` 13 出現 / `@$"` 0 出現**。
⇒ **今日壊れてはいない**が、`@$"` 順を 1 行書いた日に静かに発現する。

**是正**: 綴りの列挙をやめ、**接頭辞（`$` と `@` の任意の順序・個数）をまとめて読み飛ばしてから
種別を決める**。raw の補間 `$$"""…"""` も同じ形で入る。
`@` は**逐語識別子**（`@class`）の接頭辞でもあるので、引用符が続かなければコードとして読み進める。

🔴 **4 巡とも同じ形である** ——「列挙で塞ぐと、列挙から漏れた綴りで同じ穴が開く」。
だから 3 巡目で走査器を 1 つにし、4 巡目で**接頭辞の扱いも 1 か所へ**寄せた。

自己試験 **26 → 31 件**。実データの結果は**変わらない**（参照 153 名 / 宣言 393 名 / 違反 0 件）。

### 🔴 追加の指摘（5 巡目）—— **これまでで最も重い。ファイル全体を飲み込む**

4 巡目の実装は文字列の種別を「**先頭 3 連続引用符なら raw**」で先に判定していた。
C# の raw string literal は **`@` 接頭辞を取れない** —— `@` が付いていれば常に verbatim であり、
続く `""` は「raw の開始区切り」ではなく**エスケープされた 1 個の `"`** である。

⇒ `@"""a"` は raw と読まれ、**3 連以上の閉じ引用符が見つからないまま EOF まで走る**。

| これまで（1〜4 巡） | 5 巡目 |
| --- | --- |
| 1 行・1 リテラル分の見落とし | 🔴 **当該行以降のファイル全体**（参照も宣言も）を飲み込む |
| 偽陰性のみ | 偽陰性 ＋ **偽陽性**（同ファイル後方の宣言が消え、無関係な参照が違反として出る） |

**是正**: **verbatim の判定を raw より先に置く**（`hasAt` を最初に見る）。

**変異試験（実走）**: 判定順を逆へ戻すと自己試験 3 本が赤になり、出力は

```
NG  scanSegments: @""" を raw と誤読してファイルの残りを飲み込まない :: {"comments":[],"codes":"var s = "}
```

—— **`codes` が `var s = ` で止まっている**＝残り全体を飲み込んだことがそのまま見えている。
`$@"""` / `@$"""` の両順でも同じく赤になる。

実測: `src` に `@"""` は **0 件**（未発火）。実データの結果は 1〜5 巡を通して**一度も動いていない**
（参照 153 名 / 宣言 393 名 / 違反 0 件）。自己試験 **31 → 34 件**。

### 5 巡を通しての形

| 巡 | 穴 | 形 |
| --- | --- | --- |
| 1 | ブロックコメント内の参照／リテラル内の URL | 片側だけ見ていた |
| 2 | コメントアウトされた宣言 | 片側だけ直していた |
| 3 | C# の文字列 3 種の終端規則 | 境界の規則が場所ごとに違った |
| 4 | `@$"` 順 | 綴りを列挙したので、漏れた綴りで同じ穴が開いた |
| 5 | `@"""` | **種別の判定順序**（列挙ではなく順序の誤り） |

🔴 **1〜4 は「同じ規則を 2 か所に持つ」型で、3 巡目に走査器を 1 つへ寄せて構造的に消した。**
**5 は別の型である** —— 走査器は 1 つのままで、**その中の分岐の順序**が違った。
構造を 1 つにしても、**その 1 つが正しいかは別に確かめる必要がある**。
だから 5 巡目は「順序を入れ替える変異」を自己試験へ常設した。

### 🔴 レビューの器の側 —— `git grep` が許可リストに無く、必須 check が落ちた

5 巡目の `claude-review` は **`Bash(git grep)` の権限拒否 5 件**で exit 1 になった
（`check-permission-denials.js`。`claude-review` は**必須 check** である）。

**同型は 4 度目である**: planning#155（cat/head/tail）→ planning#160（cmp/diff）→
planning#163（grep/sort）→ 本件（`git grep`）。

🔴 **既存の機械（`check-ai-workflow-config.js` の `genericBashDrift`）では捕まらない形だった。**
あれは**実装用 ⇔ レビュー用の非対称**を見るが、`git grep` は**両方に無い共通の欠落**である。
「以後この種の非対称は検査器が ERROR で検出する」という記録は、
**片落ちには効くが共通の欠落には効かない**。

**是正**（`IADR-0115` の先例に倣う。読み取り専用サブコマンドを個別に列挙する方式）:
`Bash(git grep:*)` を **3 系統すべて**へ足した ——
`claude-coding.yml` / `claude-code-review.yml` / `.claude/settings.json`。
（1 系統だけだと `genericBashDrift` か「settings.json に無い」warn のどちらかで落ちる。
**3 系統を揃える**のが本リポジトリの形である。）

なお同じ実行で**リダイレクト 1 件**も拒否されているが、これは**許可リストでは直せない類（B）**であり
失敗判定の分母に入らない。プロンプトには既に
「**出力をファイルへリダイレクトしない**」（`claude-code-review.yml:360`）と書いてあるので、
**規則は在り、その回に守られなかった**だけである。**規則を増やさない。**

## やらないこと

- `AiSuggestionWiringTests` の 2 箇所を書き換えること（**記録を壊す**）
- 「指し先は実在するが帰結を固定していない」側を機械で判定しようとすること（射程外）
- 指し先を**消して**主張を残すこと（issue の手順 1。取り下げるなら理由を書く）
