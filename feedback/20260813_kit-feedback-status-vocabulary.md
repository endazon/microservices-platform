---
title: キットが feedback/ の status を既定値ごと配りながら、4 値の意味をどこにも定義していない
type: plan-feedback
status: open
category: 記述の不足
related_ids: [NFR]
source_repo: microservices-platform
source_ref: "claude/issue-response-handoff-2hl25v / docs/specs/20260813_issue-712_feedback-status-vocabulary.md（実装側 issue #712）"
author: Claude（実装）
created: 2026-08-13
dispatched: true
---

# フィードバック: `feedback/` の `status` に語彙の定義が無い

## 起票状況

**planning#323 として起票済み**（2026-08-13・`decision-needed`）。裁定待ち。

## 種別

**記述の不足**（キット `impl-handoff-kit/repo-template` の手順書に、配っている鍵の意味が無い）。
**計画書の誤りではない。**

## 対象

- `tools/impl-handoff-kit/repo-template/feedback/README.md`（**定義を置くべき側**）
- `tools/impl-handoff-kit/repo-template/feedback/TEMPLATE.md`（既定値 `status: open` を配っている側）

## 知見 1: **キットは `status: open` を配るが、`status` という語を手順書で 1 度も使っていない**

`TEMPLATE.md` の frontmatter は `status: open` を既定値として配る。しかし
**`README.md` は `status` という語を 1 度も使っていない**（実測: 出現 0 回）。

| 何が | どこに書いてあるか |
| --- | --- |
| 既定値 `open` | `TEMPLATE.md` の frontmatter |
| **値域**（`open` / `triaged` / `accepted` / `rejected`） | **キットのどこにも無い**（本リポは独自の回帰テストで閉じた） |
| **各値の意味・遷移・判断の主体** | **どこにも無い** |

**キットの `check-feedback-dispatched.js` は `status: open` を「未伝達の疑い」と読む**が、
**その読みは検査器のコード内にしか存在しない。**

## 知見 2: **定義が無いせいで `open` と `triaged` が実務で同義に崩れた**

`endazon/microservices-platform` の `feedback/` を**全数**で走査した（develop `a671a80`・2026-08-13）。
**「伝達の証拠」は検査器 `inspect()` の判定をそのまま用いた。**

| status | 件数 | 検査器が認める伝達の証拠 | `## 起票状況` 節 |
| --- | ---: | ---: | ---: |
| `accepted` | 26 | 10 | 4 |
| `triaged` | 7 | 6 | 6 |
| **`open`** | **3** | 2 | **3** |
| `rejected` | 2 | 0 | 0 |
| **合計** | **38** | 18 | 13 |

**`open` の 3 件はいずれも伝達済みであった**（3 件とも `## 起票状況` 節を持ち、計画リポの issue / PR を
参照している）。**つまり実務の `open` は「未伝達」ではなく「裁定待ち」を意味しており、`triaged` と
区別が付いていなかった。** **検査器の読みとは正反対である。**

> **★ この食い違いが、[[IADR-0184]]（実装側 #710）で「既知の偽陽性」として残した 1 件の正体である。**
> **検査器の欠陥だと考えていたが、実際には語彙が定義されていないことが原因だった。**

## 知見 3: **同義の 2 語は、片方が必ず意味を失う**

`open` を「裁定待ち」と読むと `triaged` と区別が付かず、`triaged` を「伝達済み」と読むと `open` の
既定値が「未伝達」を意味することになる。**どちらの読みも成り立つため、記録ごとに判断が分かれた** ——
実装側の #707 / #710 は「起票済みだが裁定前」を `triaged` と読んだが、**それはその場の判断であって
規約ではなかった。**

## 提案

**`repo-template/feedback/README.md` に `status` の語彙節を足す。** 実装側で採った定義は次のとおりで、
**検査器の読み（`status: open` ＝ 未伝達の疑い）と一致させてある。**

| 値 | 意味 | 誰が遷移させるか |
| --- | --- | --- |
| `open` | 記録は作ったが、**計画リポジトリへまだ伝達していない**（`TEMPLATE.md` の既定値） | —— |
| `triaged` | **伝達済み**（issue 起票、または `draft/feedback/` へのコピー）。**計画側の裁定を待っている** | **実装側**が、伝達した事実を書く |
| `accepted` | 計画側が**受け入れた**（計画書・ADR へ反映される／された） | **計画側の裁定**を実装側が転記する |
| `rejected` | **採らないと決まった** | 同上 |

**遷移は `open` → `triaged` → `accepted` / `rejected` の一方向とする。**

> **`triaged` という語は「計画側がトリアージに載せた」とも読めてしまう**（実際の意味は「実装側が
> 伝達した」である）。**改名する余地はあるが、値域を検査で閉じた後なので既存記録すべてに波及する。**
> **キット側で改名を採るなら、移行の指針も併せて示してほしい** —— 実装側は語を変えず意味を固定した。

## 影響

- **キットを使う実装リポはすべて同じ穴を踏む。** 既定値だけ配られ、進め方が書かれていない
- **`status` が意味を失うと、`check-feedback-dispatched.js` の警告も意味を失う** ——
  「未伝達」を指すはずの警告が、実際には「裁定待ちだが `open` のまま」を拾い続ける

## 参考（実装側の対応）

**本リポでは語彙節を暫定デルタとして `feedback/README.md` へ置いた**（[[IADR-0185]] 決定 4）。
**語彙の定義は [[IADR-0115]] 決定 2 の固有デルタ 4 種のどれにも当たらない**ため、
**同 決定 3 に従い本記録で環流する。キット側へ反映されたら暫定デルタを撤去してキット準拠へ戻す。**

**あわせて `open` の 3 件を `triaged` へ是正した**（[[IADR-0185]] 決定 2）。
**定義ができたことで初めて「`open` は誤りである」と言えるようになったためで、
[[IADR-0184]] 決定 2 の「偽陽性を消すために記録へ嘘を書かない」には反しない。**
**これにより既知の偽陽性 1 件が解消し、警告は 1 → 0 件になった。**

> **★ 知見 1（検査器が記録ファイル経路を証拠と認めない・planning#319）は未解決のままである。**
> **本件は `status` 側だけを正した** —— `20260809_document-write-machine-client.md` は
> **証拠を持たないまま `triaged`** であり、planning#319 が反映されるまでその状態が正しい。

> **［2026-08-14 追記 / #721］`status: triaged` → `open` ＋ `dispatched: true` へ移行した**（[IADR-0187](../docs/adr/IADR-0187_status-vocabulary-follows-upstream-adjudication.md) 決定 2）。
> **planning#323 の裁定により `status` は「計画側の裁定の進捗」を表すことになり、`triaged` は廃された。**
> **本記録が伝達済みであることは `planning/draft/feedback/` の写しで確認済み**であり、その事実は `dispatched: true` が担う。
