---
title: IADR-0187 status は「裁定の進捗」を表す上流裁定に従い、伝達の記録は dispatched 鍵へ移す
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0115
  - IADR-0179
  - IADR-0185
author: claude
created: 2026-08-14
updated: 2026-08-14
plan_refs:
  - "../../planning/docs/ai-implementation-workflow-guide.md"
---

# IADR-0187: `status` 語彙を上流裁定へ差し替える（#721）

- 状態: Accepted
- 日付: 2026-08-14
- 決定者: claude（実装）

## 起点・関連

- **NFR**（文書統制。メタ作業なので無採番。[IADR-0179](./IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1）
- 実装 issue: **#721**（出所: PR #723 の作業中に `planning` を populate して検出）
- 作業仕様書: [20260814_issue-721](../specs/20260814_issue-721_kit-resync-status-vocabulary.md)
- **Supersedes: [IADR-0185](./IADR-0185_feedback-status-vocabulary.md) 決定 1・決定 2・決定 4**

## 文脈 —— **仰いだ裁定が、こちらの決定と逆で返ってきた**

**#712（[IADR-0185](./IADR-0185_feedback-status-vocabulary.md)）は `status` の語彙を定義し、同時に planning#323 として上流へ環流した。**
**その裁定が 2026-08-13 に下り、キットへ反映された** —— **本リポの決定とは逆の分割で。**

| | **IADR-0185 決定 1**（本リポ） | **キットの裁定**（planning#323） |
| --- | --- | --- |
| `status` が表すもの | **伝達したか** | **計画側の裁定がどこまで進んだか** |
| `open` | **未伝達** | **裁定がまだ下りていない**（**伝達済みでも `open` でよい**） |
| 伝達の記録 | `status: triaged` | **`dispatched:` / `planning_issue:` 鍵** |
| 値域 | open / **triaged** / accepted / rejected | open / **awaiting-decision** / accepted / rejected |

**キットは本リポの実測を名指しで引いて理由を述べている。**

> 実測では、実装リポジトリが `triaged`（旧称）を「**実装側が伝達した**」の意味で 7 件に付け、
> 計画側は同じ語を「**計画側が反映した**」の意味で使っていた。**1 つの語が両側で正反対の主体を指していた**

**IADR-0185 は「`open` と `triaged` が同義に崩れている」ことまでは正しく捉えたが、
どちらの軸へ寄せるかを 1 リポジトリの都合で決めていた。** **上流は 2 リポジトリを見て、
「1 つの鍵に 2 つの軸を載せない」——`status` は裁定、`dispatched:` は伝達——という分割を採った。**

## ★★ 決定 1: **上流の裁定に従う。IADR-0185 決定 1 を差し替える**

[IADR-0115](./IADR-0115_impl-handoff-kit-as-single-source.md) 決定 1 が**キットを足場の単一情報源**と定めている。
**本リポが裁定を仰ぎ、上流が答えを出した以上、上流に従う。**

> **★ 仰いだ側が結論を選べるなら、裁定の意味が無い。**
> **「自分の設計のほうが良い」と思っても、それは裁定の前に言うことであって、後に言うことではない。**

**本リポの設計が劣っていたから差し替えるのではない** —— **上流は 2 リポジトリの実測を持っており、
本リポは 1 つしか持っていなかった。** **`resolved` を 6 件使っていた別リポジトリの存在は、
本リポからは見えない情報である。**

## ★ 決定 2: **`triaged` 11 件は `open` ＋ `dispatched: true` へ移行する**

**キットの移行指示に従う。**

> **「伝達した」の意味で付けていたなら `open` ＋ `dispatched: true` が正しく、`awaiting-decision` ではない。**

**本リポの `triaged` は「実装側が伝達した」の意味である**（IADR-0185 決定 1）。**全 11 件が該当する。**

### 移行の前に「本当に伝達済みか」を全数で確かめた

**キットは「意味を確かめてから移行する」と要求している。** 確かめた結果、
**11 件すべてに `planning/draft/feedback/` の写しが在った**（記録ファイル経路）。
**`dispatched: true` はすべて真である。**

> **★ `20260719_headlamp-k8s-management-ui.md` は、検査器の証拠も planning issue 番号も持たず
> 「未伝達」に見えた。** **実際は写しが在り、計画側が `ADR-0040` / `ADR-0042` を起こしていた。**
> **確かめずに移行していたら、伝達済みの記録を未伝達として扱うところだった。**

## ★★ 決定 3: **`awaiting-decision` / `accepted` / `rejected` は実装側が書かない**

**キットの表は「誰が書き換えるか」を定めており、`open` 以外はすべて計画側である。**

**実装側が推測で埋めない。** **`triaged` で起きた事故——両側が同じ語を別の意味で使う——の再発になる。**

| 例 | 実測 | それでも `open` にする理由 |
| --- | --- | --- |
| headlamp | 計画側が `ADR-0040` / `ADR-0042` を起票（**どちらも `Proposed`**） | **「反映まで進んだが裁定待ち」は `awaiting-decision` に見えるが、それは計画側が書く欄**である |

**実装側が書くのは `dispatched:` / `planning_issue:` だけ**とする。**これが上流の分割の要点である。**

## 決定 4: **暫定デルタを撤去し、README をキット準拠へ戻す**

**[IADR-0185](./IADR-0185_feedback-status-vocabulary.md) 決定 4 が「キット側へ反映されたら撤去してキット準拠へ戻す」と定めた往復の完了である。**
**キットに `## \`status\` の語彙（4 値）` が入ったので、本リポの暫定デルタは役目を終えた。**

**残す固有デルタは CI ジョブ名（`doc-links` → `feedback-dispatched`）の 1 点だけ**（IADR-0115 決定 2 の 2）。

**`TEMPLATE.md` も分類 A へ戻す** —— **#712 の時点ではバイト一致だったが、pin が進んで差分が生まれた。**
**「前回一致していたから今回も」は成り立たない。**

## ★★ 決定 5: **`scripts.test.js` は全面追随せず、2 テストだけ暫定デルタで直す（#713 へ送る）**

**検査器を新版へ上げると `scripts/scripts.test.js` が落ちる** —— 同ファイルが**旧検査器の API と挙動**を
テストしているためである。**しかし全面追随はできなかった。実測した連鎖は次のとおり。**

| 段 | 起きたこと |
| --- | --- |
| 1 | キット版 `scripts.test.js` を入れる → **`isBotLogin is not a function`**（`check-commit-messages.js` が旧版） |
| 2 | `check-commit-messages.js` ほか 4 本も入れる → **companion が `isBotAuthorName is not a function`**（本リポ側の API 名） |
| 3 | あわせて **`PLAN_PROJECT` が `<project-name>` のまま**という警告 —— **キットの置換点**であり、バイト一致コピーでは壊れる |

**分類 A の検査器群は相互依存しており、1 ファイルだけ進めることができない。**

**キット全体との乖離を全数で測ると 108 ファイル中 35 件**（一致 65 / 本リポに無い 8 ＝ `*.example.yml`）。
**これは #713（キット追随の棚卸し）そのものである。**

**→ 本 ADR では `scripts.test.js` の該当 2 テストだけを直し、コメントで #713 を参照する。**

| テスト | 旧 | 新 |
| --- | --- | --- |
| 自己申告 | 本文の「未送付」で警告 | **`dispatched: false` で警告**（planning#319 知見 3。**本リポが環流した分**） |
| 証拠の走査 | `foreignIssueLinks`（自リポ以外か） | **`foreignPlanRefs`（計画リポ宛てか）＋ PR URL も証拠**（planning#319 知見 1。同上） |

> **★ `scripts.test.js` は既にキットと 671 行乖離しており、実態として分類 A を保てていない。**
> **「分類 A だから触らない」と言えるのは一致しているときだけ**である。**#713 が全面追随を行う。**

## 結果

- 良い影響
  - **`planning` を populate した環境でテストが通る**（#721 の起点が解消）
  - **2 リポジトリで語彙が揃う** —— 同じ語が両側で別の意味を指す状態が終わる
  - **キットとの分類 A が 2 ファイル回復**（`check-feedback-dispatched.js` / `TEMPLATE.md`）
  - **伝達と裁定が別の鍵になった** —— どちらの意味かを読み手が推測せずに済む
- 悪い影響・トレードオフ
  - **マージから 1 日以内の [IADR-0185](./IADR-0185_feedback-status-vocabulary.md) 決定 1 を差し替える。** 記録としては読みにくいが、
    **上流裁定の結果であることを明示すれば追える**（本 ADR がその役目を負う）
  - **`awaiting-decision` は当面 0 件**である —— 計画側が書く欄であり、実装側からは増やせない
  - **CI では引き続きこの乖離を検出できない**（`scripts-tests` が `planning` を populate しない）。
    **未 populate 時の notice**（#712）だけが手掛かりである
- フォローアップ
  - **`scripts-tests` に `planning` を populate させるか**は CI の変更であり別途（**本 ADR では触らない**）

## 関連

- Supersedes: **[IADR-0185](./IADR-0185_feedback-status-vocabulary.md) 決定 1・決定 2・決定 4**（決定 3・決定 5 は有効）
- Superseded by: なし
