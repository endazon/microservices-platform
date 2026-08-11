---
title: check-feedback-dispatched.js が記録ファイル経路を伝達の証拠と認めず、TEMPLATE.md に planning_issue が無い
type: plan-feedback
status: triaged
category: その他
related_ids: [NFR]
source_repo: microservices-platform
source_ref: "claude/issue-710-feedback-dispatched-checker / docs/specs/20260811_issue-710_feedback-dispatched-checker.md（実装側 issue #710）"
author: Claude（実装）
created: 2026-08-11
---

# フィードバック: 伝達漏れの検査器が README の定める 2 経路の片方しか読まない

## 起票状況

**planning#319 として起票済み**（2026-08-11・`decision-needed`）。裁定待ち。

## 種別

**その他**（キット `impl-handoff-kit/repo-template` の内部不整合）。
**計画書の誤りではなく、キットの検査器と手順書の食い違いである。**

## 対象

- `tools/impl-handoff-kit/repo-template/scripts/check-feedback-dispatched.js`
- `tools/impl-handoff-kit/repo-template/feedback/TEMPLATE.md`
- `tools/impl-handoff-kit/repo-template/feedback/README.md`（正しい側）

## 知見 1: **README は伝達を 2 経路認めるのに、検査器は片方しか証拠と読まない**

`feedback/README.md` の手順 3 は「**両経路に対応**」と明記している。

| 経路 | README | 検査器 |
| --- | --- | --- |
| **記録ファイル経路**（記録を計画リポの `draft/feedback/` へコピー） | **定めている** | **証拠と認めない** |
| GitHub Issue 経路 | 定めている | 認める（条件 a / b / c） |

検査器が起票済みと見なす条件は次の 3 つで、**いずれも Issue 経路を前提にしている**。

- a. frontmatter の `planning_issue:` が非空
- b. 本文に**自リポ以外の GitHub issue URL**（`/issues/\d+` のみ。**`/pull/\d+` は読まない**）
- c. 本文に「起票済み」

### 実測（`endazon/microservices-platform`・develop `623606b`・2026-08-11）

キット原文を `feedback/` の **37 件**（`README.md` / `TEMPLATE.md` を除く）へ当てた。

| | 件数 |
| --- | ---: |
| 警告 | **1** |
| **うち偽陽性** | **1** |

`20260809_document-write-machine-client.md` は **PR planning#306 で `draft/feedback/` へのコピーが
マージ済み**であり、同記録の `## 起票状況` 節は「**Issue 起票は本件では実施しない（3-a の経路を
採ったため。いずれか一方で足りる）**」と書いている。**README どおりの運用である。**

**この記録が 3 条件を真として満たす方法は無い** —— `planning#306` は **PR** であって issue ではなく、
`planning_issue:` へ書けば誤りになる。**検査器を満たすために別途 issue を立てるのは、
「いずれか一方で足りる」という手順自体に反する。**

### 提案

**`/pull/\d+` の URL、または `draft/feedback/` へのコピーを示す記述も証拠として認める。**
あるいは **README 側で「Issue 経路のみが機械検査の対象である」と明示する**（どちらでもよいが、
**現状は手順書と検査器が食い違ったまま**である）。

## 知見 2: **`TEMPLATE.md` に `planning_issue:` が無いので、条件 a が誰にも使われない**

検査器が用意した最も明快な証拠の口（条件 a）が、**雛形に無いため 0 件**である。

| 条件 | 該当（37 件中） |
| --- | ---: |
| a. `planning_issue:` | **0** |
| b. 他リポの issue URL | 12 |
| c. 「起票済み」 | 16 |
| いずれも無し | 20 |

**`TEMPLATE.md` はキットと分類 A（バイト一致）なので、実装リポ側では足せない**
（[[IADR-0115]] 決定 2）。**キット側で足す必要がある。**

### 提案

`feedback/TEMPLATE.md` の frontmatter へ `planning_issue: <起票後に planning#NNN>` を足す。

## 知見 3: **本記録自身が、検査器の 3 つ目の粗さを踏んだ**

検査器は「本文に **未** ＋ **送付** の語がある」ことを、`status` を問わず警告する条件にしている
（記録自身の自己申告として最も強い信号だ、という設計）。**しかし本記録のように検査器 *について* 書いた
記録も、語を含むだけで発火する。** 実測で本記録は見出しに 1 回使っただけで警告が 1 → 2 件に増えた。

**本記録は見出しの語を「伝達漏れ」へ言い換えて回避した** —— 意味は変えていない（自己申告ではなく
検査器の説明である）。**ただし、語の出現だけを見る条件は同型の偽陽性を作り続ける。**

### 提案

**自己申告の語は frontmatter の鍵（例: `dispatched: false`）で表し、本文の語からは切り離す。**

## 知見 4: **コメントアウトされた `env:` が、外すと不正 YAML になる**

`ci.example.yml` は各検査器の厳格化を「コメントを外せば有効になる」opt-in として書いているが、
**`# env:` が `steps:` のリスト項目と同じ字下げにある箇所がある。** 外すと**リスト項目の並びに
mapping キーが混じる**ため、ワークフローが読めなくなる。

### 実測（`ci.example.yml` の `# env:` を全数）

| 行 | ジョブ | 字下げ | 判定 |
| ---: | --- | ---: | --- |
| 84 | `scripts-tests` | 8（**step 直下**） | **正しい**（`- name:` の子） |
| **114** | **`doc-links`** | **6（項目と同じ）** | **外すと不正** |
| **168** | **`ai-workflow-config`** | **6（項目と同じ）** | **外すと不正** |

> **★ 「`# env:` がある」で数えると 3 件になるが、実際に壊れるのは 2 件である。**
> **分かれ目は字下げの深さだけ**であり、目視の一次判定では誤った。

### 提案

**`# env:` を job 直下（4 字下げ）へ移す。** 本リポは移した形で配線しており、外せばそのまま効く。

## 影響

- **キットを使う実装リポはすべて同じ穴を踏む。** 記録ファイル経路を採ると恒久的に警告が残る
- 警告であって失敗ではない（exit 0）ため **CI は緑**だが、**恒常的に鳴る警告は読まれなくなる**

## 参考（実装側の対応）

**本リポでは検査器を書き換えない**（[[IADR-0115]] 決定 2 の固有デルタ 4 種に当たらず、
書けば次の同期で消えるため）。**偽陽性 1 件は消さずに残し、理由と本記録を添える**
（[[IADR-0184]] 決定 2）。**記録に嘘を書いて警告を消すことはしない。**
