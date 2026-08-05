---
title: キット由来の pr-title.yml が GitHub App 作成 PR で skipped になる — 除外条件 user.type != 'Bot' の見直し
type: plan-feedback
status: open
category: 新たな制約(ADR要)
related_ids: [NFR, IADR-0115]
source_repo: microservices-platform
source_ref: docs/specs/20260805_issue-524_pr-title-bot-author-gate.md（ブランチ fix/NFR-pr-title-bot-author-gate・issue #524）
author: Claude
created: 2026-08-05
---

# フィードバック: `pr-title.yml` の bot 除外条件がキット全体に同じ穴を作っている

> **計画リポジトリへ起票済み: [planning#202](https://github.com/endazon/project-planning/issues/202)**（2026-08-06）。
> 実装側の是正は microservices-platform#527（マージ済み）で完了している。

## 種別

新たな制約（キット由来の配布物の欠陥。**同期先の全実装リポジトリで同じ穴が空いている**）。

## 起点となる計画書

- 機能要求（FR）: なし（NFR: トレーサビリティの機械強制）
- 関連 ADR: なし（キットの配布物に関する事項）
- 実装側の対応: [作業仕様書](../docs/specs/20260805_issue-524_pr-title-bot-author-gate.md)／
  [IADR-0115](../docs/adr/IADR-0115_impl-handoff-kit-as-single-source.md)（キットを単一情報源とする）
- 実装 issue: microservices-platform#524（検出元: 同 PR #523 の CI 実測）

## 現状（As-Is）

`impl-handoff-kit` が配布する `.github/workflows/pr-title.yml` は、ジョブ条件に

```yaml
# bot 作成 PR は規約対象外（check-commit-messages.js の BOT_AUTHORS と同方針）。
if: github.event.pull_request.user.type != 'Bot'
```

を持つ。一方で同ファイルのヘッダは、この検査を「**最後の砦**」と位置づけている——
スカッシュ後に統合ブランチへ載る件名は `check-commit-messages.js` の `base..HEAD` 検査に含まれず、
force push 禁止のため事後修正もできないためである。

## 問題点 / あるべき姿（To-Be）

**GitHub App（`claude[bot]`）が作成した PR は `user.type == 'Bot'` なので、この条件で除外される。**
実測（microservices-platform）:

| PR | 作成者 | `user.type` | `pr-title` の結論 |
| --- | --- | --- | --- |
| #518 | `endazon` | `User` | success |
| #523 | `claude[bot]` | **`Bot`** | **skipped** |

キットは「**AI に実装を委ねる運用**」を前提に設計されており（`docs/ai-workflow.md`・`claude-coding.yml` は
GitHub App 経由で PR を作る）、**キットが想定する主要な経路でだけ最後の砦が外れる**。
除外の意図（dependabot 等の自動 PR を対象外にする）は妥当だが、
**「自動生成された PR」と「AI が人の代わりに書いた PR」を `user.type` だけでは区別できない**。

**同期先のすべての実装リポジトリで同じ穴が空いている**と考えられる（本ファイルはキット由来であり、
実装リポ側で書き換えた形跡は無い）。

## 実装で判明した経緯

microservices-platform#523（`claude[bot]` 作成）の CI 実測で `pr-title` が `skipped` になっていることに
気づいた。当該 PR のタイトル自体は規約に適合していたため**偶然通っただけ**で、検査は働いていなかった。

## 提案（キットへの反映案）

- 反映先候補: **キットの `.github/workflows/pr-title.yml` と `scripts/check-commit-messages.js`**
- 提案内容: **ジョブ条件を削除し、除外の判定をスクリプト側（`BOT_AUTHORS`）へ寄せる。**

  ```yaml
  # if: を削除し、作成者を渡す
  env:
    PR_TITLE: ${{ github.event.pull_request.title }}
    PR_AUTHOR: ${{ github.event.pull_request.user.login }}
  ```

  スクリプト側は `PR_AUTHOR` を受け取り、既存の `BOT_AUTHORS` に**名前が一致するときだけ** `skip` する。

  **`if:` に名前の一覧を書く案は採らない。** `check-commit-messages.js` の `BOT_AUTHORS` と
  同じ規約を二重実装することになり、`pr-title.yml` 自身の設計方針（「規約の単一情報源を二重実装しない」）に反する。

  実装は microservices-platform 側で先行して行った（issue #524）。差分はキットへそのまま持ち込める形にしてある:
  - `scripts/check-commit-messages.js`: `isBotAuthorName(login)` を追加し、`checkSingleTitle(title, author)` へ引数追加。
    `--author` / `PR_AUTHOR` に対応。
  - `.github/workflows/pr-title.yml`: `if:` を削除し `PR_AUTHOR` を渡す。
  - 回帰テスト 4 件（`user.type` がワークフローに残っていないこと／`claude[bot]` が検査対象に残ること 等）。

## 影響範囲

- **キットの同期先すべて**（同じ `pr-title.yml` を持つ実装リポジトリ）。
- 挙動の変化は「**これまで skipped だった PR で検査が走る**」ことのみ。dependabot 等は名前で引き続き除外される。
  既存の規約適合 PR には影響しない。
- キット側で採用されたら、実装リポ側は次回の同期で**暫定デルタを撤去**できる（IADR-0115 の分類に従う）。
