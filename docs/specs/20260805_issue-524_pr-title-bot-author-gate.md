---
title: PR タイトル検査が GitHub App 作成 PR で skipped になる穴を塞ぐ — 除外を user.type から作成者名へ
type: spec
status: done
related_ids: [NFR, IADR-0115]
author: Claude
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - "../../feedback/20260805_kit-pr-title-bot-author-gate.md"
  - ../adr/IADR-0115_impl-handoff-kit-as-single-source.md
---

# 仕様書: PR タイトル検査の bot 除外を作成者名へ（issue #524）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性・トレーサビリティの機械強制）
- 関連 IADR: [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)（キットを単一情報源とする。`pr-title.yml` はキット由来）
- 実装 issue: [#524](https://github.com/endazon/microservices-platform/issues/524)（検出元: PR #523 の CI 実測）
- 規約: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) §PR タイトル（スカッシュ後件名）の検査

## 目的・背景

`pr-title.yml` は自身のヘッダで「**PR タイトル検査が最後の砦**」と位置づけている。スカッシュ後に
develop へ載る件名は `check-commit-messages.js` の `base..HEAD` 検査に**含まれず**、force push 禁止のため
**事後修正もできない**からである。

ところがジョブ条件が `if: github.event.pull_request.user.type != 'Bot'` であり、
**GitHub App（`claude[bot]`）が作成した PR は `user.type == 'Bot'` なので除外**されていた（PR #523 で実測。
`pr-title` の結論が `skipped`）。このリポジトリは AI に実装を委ねる運用を前提とするため、
**その経路の PR でだけ最後の砦が外れる**。

除外の意図（dependabot 等の自動 PR を対象外にする）は妥当だが、
**「自動生成された PR」と「AI が人の代わりに書いた PR」を `user.type` だけで区別できていない**のが原因である。

## 対象範囲

- 対象:
  - `.github/workflows/pr-title.yml`: ジョブ条件の削除と `PR_AUTHOR` の受け渡し
  - `scripts/check-commit-messages.js`: 作成者名による除外（`isBotAuthorName`）と `--author` / `PR_AUTHOR`
  - `scripts/scripts.repo.test.js`: 回帰テスト 4 件
  - キットへの環流記録（`feedback/`）と計画リポジトリへの起票
- 対象外:
  - `BOT_AUTHORS` の中身の見直し（現行の一覧をそのまま使う）
  - `check-commit-messages.js` の件名規約そのもの
  - キット本体（`impl-handoff-kit`）の修正（別リポジトリ。環流までが本作業）

## 設計

### 決定: 除外は**ワークフローではなくスクリプト**で、**名前**で行う

issue の提案は `if:` を名指しリストへ変える案だったが、**採らない**。
`if:` に名前一覧を書くと、`check-commit-messages.js` の `BOT_AUTHORS` と**同じ規約を二重実装**することになり、
`pr-title.yml` 自身の設計方針（「規約の単一情報源を二重実装しない」）に反する。

代わりに:

1. `pr-title.yml` の `if:` を**削除**する（＝ジョブは常に実行される。`skipped` にならない）
2. 作成者ログインを `PR_AUTHOR` で渡す
3. `check-commit-messages.js` が `BOT_AUTHORS`（既存の単一情報源）で判定し、
   一致すれば `skip(bot)` として **exit 0** で終える

これにより「除外したい bot の一覧」はリポジトリ内で 1 か所（`BOT_AUTHORS`）に保たれる。

### 照合は**完全一致**（部分一致にしない）

`isBotAuthorName` の突合は**ログイン名の完全一致**（大小文字・前後空白は無視）である。

既存の `isBot` は部分一致だが、あちらの突合先は `"名前 <メール>"` という**連結文字列**であり、
形が違う。ログイン名へ部分一致を流用すると、`BOT_AUTHORS` の語を含む**人間のログイン**
（`the-renovate-guy` / `dependabot-team` 等）まで除外され、**最後の砦を無検査で素通り**させる
（PR #527 のレビュー指摘。当初は部分一致で実装しており、指摘を受けて是正した）。

**除外は狭く取る。** 広すぎる除外は、本作業が塞いだ穴（`user.type` による広すぎる除外）と同型である。

### 判定の意味論

| 作成者 | 結果 |
| --- | --- |
| `dependabot[bot]` / `renovate[bot]` / `github-actions[bot]` | `skip(bot)` → exit 0（従来どおり除外） |
| **`claude[bot]`** | **検査する**（規約違反なら exit 1） |
| 人間ユーザー | 検査する |
| 未指定（ローカル実行・イベント外） | 件名だけで判定（従来どおり） |

## 受け入れ基準

- [x] GitHub App が作成した PR で `pr-title` が **skipped にならず実行される**（本 PR 自身では実測できないため下記 §未決事項）
- [x] dependabot 等の自動 PR は引き続き除外される（単体テストで固定）
- [x] 規約の判定ロジックを二重実装していない（`BOT_AUTHORS` が単一情報源）
- [x] 同型の `user.type` 判定が他のワークフローに残っていない（全量 grep ＋ 回帰テスト）
- [x] `node scripts/check-ai-workflow-config.js` が成功する
- [x] キットへの環流を `feedback/` に記録し、計画リポジトリ側へ起票した
      （記録 = `feedback/20260805_kit-pr-title-bot-author-gate.md`／起票 =
      [planning#202](https://github.com/endazon/project-planning/issues/202)。
      **起票は PR マージ後の 2026-08-06 に実施した**——PR 提出時点では記録のみで、
      本項目は先走って `[x]` にしていた）

## テスト方針

`scripts/scripts.repo.test.js` に 4 件（CI の `scripts-tests` ジョブが実行する）。

1. `pr-title.yml` に `user.type` 判定が**無い**こと（コメント行は除外して判定する——
   経緯の説明で言及すること自体は禁じない。禁じたいのは効いている条件である）
2. `PR_AUTHOR` が渡されていること
3. 他のワークフローに同型の判定が無いこと（全量走査）
4. `isBotAuthorName` / `checkSingleTitle` の分岐（`dependabot[bot]`=skip / **`claude[bot]`=検査** /
   作成者未指定=従来どおり）

## 計画書との差異

- 差異: なし

## 未決事項

- **本 PR 自身は人間ユーザー（`endazon`）が作成するため、「App 作成 PR で skipped にならない」ことを
  この PR の CI では実測できない。** 次に `claude[bot]` が作成する PR で `pr-title` の結論が
  `skipped` ではなくなることを確認する（issue #524 の受け入れ基準は本 PR マージ後に閉じる）。
  ワークフローの `if:` を削除した以上、GitHub がジョブを skip する理由は無くなっている。

## 実測

```console
$ node scripts/check-commit-messages.js --title "壊れた件名" --author "claude[bot]"
✗ PR タイトルが規約違反 …                       # exit=1（従来は検査そのものが走らなかった）

$ node scripts/check-commit-messages.js --title "壊れた件名" --author "dependabot[bot]"
  skip(bot)    作成者 dependabot[bot] は規約対象外（BOT_AUTHORS）   # exit=0

$ node scripts/scripts.test.js            # 262 tests passed（#524 の 4 件を含む）
$ node scripts/check-ai-workflow-config.js # ✓ 問題なし
$ grep -rn "user.type" .github/workflows/  # 残るのは経緯を説明するコメント 1 行のみ
```
