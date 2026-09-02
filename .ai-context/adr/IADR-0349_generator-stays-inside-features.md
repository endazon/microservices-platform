---
title: IADR-0349 操作の処理は「外部境界か純粋な判断か」に分解できない限り Features/ の中に残し、段だけを数えて決める
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - FR-18
  - UC-10
  - ADR-0051
  - ADR-0063
  - ADR-0065
  - ADR-0068
  - IADR-0261
  - IADR-0266
  - IADR-0282
  - IADR-0319
  - IADR-0334
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md (Accepted 2026-08-30) 決定 1・2・5
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30) 決定 1・2・7
  - planning:projects/microservices-platform/07_adr/ADR-0051_ai-suggestion-abac-boundary.md (Accepted) 決定 1・3・4
---

# IADR-0349: `AiSuggestionGenerator` は `Features/AiSuggestions/Generate/` へ下ろす（#1093）

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: claude（実装）

## コンテキストと課題

`ADR-0068` 決定 2 は「そのファイルを使う操作が 1 つなら 3 段目へ下ろす」とだけ定めている。
`GraphService/Features/AiSuggestions/AiSuggestionGenerator.cs` は**この判定では 1 操作**である
（使うのは `AiSuggestions/Generate` だけ。`Approve` / `Reject` / `List` は使わない）。

**にもかかわらず #1062 の移送 PR 群はこれを射程外に置いた。** 理由は
「争点が段ではなく、**`Features/` の中に居てよいのか**である」というものだった。#1093 の本文は
その争点をこう置いている。

> `AiSuggestionGenerator` は **LLM 境界（外部サービス呼び出し）とスコープ内候補の絞り込み**を持つ。
> `ADR-0065` のプロジェクト構成では、この種のものは `Features/` ではなく
> **`Domain/`（判断）と `Infrastructure/ExternalServices/`（外部境界）**に置くのが素直である。

🔴 **`ADR-0068` 決定 2 は「段」を決める規則であって、「層」を決める規則ではない。**
決定 2 を機械適用すると `Generate/` へ降りるが、それは**この争点に答えていない** ——
`Features/` の外へ出すなら、そもそも段の話が始まらないからである。

## 決定

**決定 1: `Features/AiSuggestions/AiSuggestionGenerator.cs` を
`Features/AiSuggestions/Generate/AiSuggestionGenerator.cs` へ下ろす。中身は割らない。**

namespace は `GraphService.Features.AiSuggestions.Generate`（`IADR-0261` の `<Svc>Service.*` 規約を維持）。
`ADR-0068` 決定 5「純粋な移送に留める」の範囲に収める。

**決定 2: `Features/` の外へ出すかどうかは、「外部境界を持つか」「純粋か」を
`Domain/` と `Infrastructure/` の実測で判定する。中身から受ける印象で決めない。**

`IADR-0319` 決定 1 が**段**について定めた手続き（数える／印象で決めない）を、**層**へも広げる。
本件では 2 つの走査が答えを出した。

| 前提（#1093 本文） | 走査 | 結果 |
| --- | --- | --- |
| 「LLM 境界（外部サービス呼び出し）を持つ」 | `grep -rln "HttpClient\|HttpRequestMessage" Domain/ Features/ Infrastructure/` | 🔴 **成り立たない。** 出るのは `Infrastructure/ExternalServices/` の 4 件だけで、`Features/` は 0 件。**外部境界は既に `LlmGatewaySuggestionClient` に切り出されている**（`AiSuggestionGenerator` が触るのは `Domain/Ports/ISuggestionLlmClient` の 1 メソッド） |
| 「純粋な判断は `Domain/` へ出せる」 | `grep -rn "^using GraphService\.\(Infrastructure\|Features\|Common\)" Domain/` | 🔴 **成り立たない。** 0 件（陽性対照: 逆向きの `Infrastructure/ → Domain` は出る）。`AiSuggestionGenerator` は `GraphDbContext` を受け取って読み書きするため、`Domain/` へ置くと `check-unit-dependencies.js` 規則 3③（`IADR-0282` 決定 2）が止める |

**残っているのは `ADR-0051` 決定 3 が定めた 6 段の順序そのもの**（起点 → 類似度 → 候補列挙 →
封 → LLM → 取り込み）である。**純粋な判断はすでに `Domain/`（`SuggestionPrompt.Seal` /
`AuthorizedNode.Authorize` / `AiSuggestion.CreateLink`・`CreateTag`）に、外部境界はすでに
`Infrastructure/ExternalServices/` に居る。** 割る先はどちらも埋まっており、**割れるものが残っていない。**

**決定 3: `ADR-0068` 決定 2 の適用範囲は `Features/` の中に閉じる。**

決定 2 は「1 操作なら 3 段目、2 操作以上なら 2 段目」という**2 値の判定**であり、
**`Features/` の外という第 3 の行き先を持たない。** したがって順序はこうである。

1. **まず層を決める**（`ADR-0065` 決定 1 の標準樹形。`Domain` / `Features` / `Infrastructure` / `Common`）。
   判定は決定 2 の実測 —— 外部境界か、依存の向きが許すか。
2. **`Features/` に居ると決まったものだけに、`ADR-0068` 決定 2 を当てて段を決める。**

**「1 操作しか使っていない」は層を動かす理由にならない。** 逆に「2 操作以上が使う」ことも
`Features/` の外へ出す理由にはならない —— それは 2 段目に残る理由である（決定 1・登録表）。

**決定 4: テストは `IADR-0334` 決定 3 に従って一緒に動かす。**
`Tests/Features/AiSuggestions/AiSuggestionGenerationTests.cs` は `AiSuggestionGenerator` を
直接 `new` する（主題である）ため、`Tests/Features/AiSuggestions/Generate/` へ写す。

## 理由

**決定 1〜3 は 1 つの見方から出ている** —— **段の規則と層の規則は別の問いであり、
片方でもう片方に答えられない。**

`ADR-0068` 決定 2 は「所属（どの操作のものか）」を問う。`ADR-0065` 決定 1 の層は
「向き（何に依存してよいか）」を問う。**#1093 が難しく見えたのは、段の規則を層の問いへ
当てようとしたからである。** 順序を決めてしまえば、どちらの問いも機械的に答えが出る。

🔴 **#1093 本文の 2 つの前提は、どちらも `AiSuggestionGenerator.cs` の**中身を読んだ印象**である。**
このファイルは冒頭コメントに「`[5] LLM ISuggestionLlmClient.ProposeAsync`」と書き、
`ADR-0034` 決定 5 の越境禁止を長く説明している —— **読めば「LLM 境界を持つ」と見える。**
`IADR-0319` が `McpToolContracts.cs` で観測したのと**同じ型の推定**である（「語彙」に見えるものは
「操作をまたぐ」と推定された）。**同じ誤りが 2 回起きた**ので、決定 2 で手続きを層へ広げた。

> **検査器は置かない。** 2 回目ではあるが、**同型ではない** —— 1 回目は段の誤判定、
> 2 回目は層の誤判定である。どちらも静的に判定するにはシンボル解決が要る点も変わらない
> （`IADR-0319` の記録どおり）。**手続きを 1 本にすることを先に行う。**

**決定 1 で「割らない」ことは #1014 にとっても都合がよい。** `ADR-0063`（AI のタグ提案が
タグ辞書の値域に収まらない。裁定待ち）の案 A「生成の段で辞書外を落とす」は
`AiSuggestionGenerator.PersistAsync` を触る。**`Generate/` に居れば #1014 の作業は 1 フォルダに
閉じる**が、`Domain/` ＋ `Infrastructure/` へ割っていれば 3 フォルダへ散る。
**配置の判断が、次に触る作業の宣言ファイル領域を広げない側へ倒れている。**

## 結果

- **良い影響**
  - `Features/AiSuggestions/` 直下に残るのが `AiSuggestionEndpoints.cs`（登録表）**1 件だけ**になり、
    `ADR-0068` 決定 1 の形（登録表は 2 段目・操作の処理は 3 段目）が集約単位で揃う。
  - **層と段の判定順序が明文化された。** 以後「`Features/` の外へ出すか」は決定 2 の 2 走査で答が出る。
  - **#1014 の宣言ファイル領域が広がらない**（`Features/AiSuggestions/Generate/` の 1 フォルダ）。
- **悪い影響 / トレードオフ**
  - 🔴 **決定 3 の順序は、層が動けば段をやり直すことを含意する。** `AiSuggestionGenerator` が
    将来ポートの実装になれば `Infrastructure/` へ移り、そのとき段の判定は無効になる。
    `IADR-0319` が受け入れた「判定は時点に依存する」と同じ依存であり、同じ理由で受け入れる。
  - **`ADR-0051` 決定 3 の 6 段が `Features/` の中に残る。** 「AI 提案の順序はドメインの規則では
    ないか」という読みは残り得る。**本 IADR はそれを否定しない** —— 否定するのは
    「`Domain/` へ**置ける**」であり、置けない理由は依存の向き（機械検査）である。
- **フォローアップ**
  - `Features/GraphDocuments/LinkEdgeSynchronizer.cs`（同じ 1 操作の決定 2 違反）は #1094 で直す。
    **本 IADR の決定 3 は同件にも当たる**（`GraphDbContext` を触るため `Domain/` へは置けない）。

## 関連

- 計画 ADR: `ADR-0068` 決定 1・2・5、`ADR-0065` 決定 1・2・7、`ADR-0051` 決定 1・3・4、`ADR-0063`（#1014。裁定待ち）
- 実装 IADR: `IADR-0319`（段は数えて決める。本 IADR はその手続きを層へ広げる）、
  `IADR-0282`（標準樹形と層の依存方向）、`IADR-0266`（AI 提案の生成。本 IADR は位置だけを変え、
  その判断には触れない）、`IADR-0261`（namespace 規約）、`IADR-0334`（テストの鏡写し）
- 作業仕様書: `.ai-context/specs/20260903_issue-1093_ai-suggestion-generator-placement.md`
- issue: #1093
