---
title: AI ワークフローの権限拒否を塞ぐ（grep / sort / git -C <submodule>）とキットへの環流
type: spec
status: in-progress
related_ids:
  - NFR
  - IADR-0115
author: claude
created: 2026-08-03
updated: 2026-08-03
related_specs:
  - "../adr/IADR-0115_impl-handoff-kit-as-single-source.md"
  - "./20260802_impl-handoff-kit-sync.md"
---

# 仕様書: AI ワークフローの権限拒否を塞ぐ（issue #460）

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#460](https://github.com/endazon/microservices-platform/issues/460)
- 起点 ID: NFR（保守性・運用性。CI の信頼性）／[IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)
  （`impl-handoff-kit` を足場の単一情報源とする同期規約。両ワークフローは**分類 B**）
- 検出元: PR [#459](https://github.com/endazon/microservices-platform/pull/459) の
  `claude-review` ジョブが**権限拒否 6 件**で exit 1（レビュー本文は正常に投稿されていた）
- 上流の先行事例: planning#145 / #146 / #155 / #157 / #158 / #160 / #161 / #162

## 目的・背景

`claude-code-review.yml` の `--allowedTools` には `cat` / `head` / `tail` / `cmp` / `diff` /
`echo` / `rg` が揃っているのに、**`grep` と `sort` だけが無い**。また `git -C <dir>` 形の許可は
`planning` の 4 サブコマンドしか無く、本リポジトリのもう 1 つの submodule
（`src/ai-stock-trading`、およびその配下の `planning`）の履歴は検証できない。

結果、レビューが**成果物としては正しいのにジョブだけ赤くなる**。しかも発現は間欠的で
（PR #459 の 3 ラウンド中 2 回が赤・1 回が緑）、その回にレビューが `grep` 等を何回使おうとしたかで
拒否件数が変わり、段階ポリシーの許容値 4 件（`check-permission-denials.js`）を超えた回だけ落ちる。
「再実行したら緑になった」で片付ける運用を誘発するため、**拒否の赤を無視する学習**を生み、
検査そのものの目的（レビューが実質未実施であることの可視化）を壊す。

#454 の再実装は子 issue 20 件＝ PR 20 本であり、フェーズ 0（#453 / #455）の着手前に解消する。

## 対象範囲

- 対象:
  - `.github/workflows/claude-code-review.yml` / `.github/workflows/claude-coding.yml` の
    `--allowedTools` に読み取り専用コマンドを追加し、**両者を対称に保つ**
  - `.claude/settings.json`（ローカル）の `permissions.allow` を同じ内容へ揃える（3 系統同期）
  - `mcp__github__list_sub_issues` の扱い（許可追加ではなくプロンプト誘導）
  - `feedback/` への記録と計画リポジトリ（`impl-handoff-kit`）への環流
- 対象外:
  - `PERMISSION_DENIALS_TOLERANCE` の引き上げ（原因を隠すだけ。#460 の制約で明示的に除外）
  - `check-ai-workflow-config.js` のドリフト検査を読み取り専用ツールまで広げること
    （キット由来の分類 A ファイル。**環流で解決する**。後述「計画書との差異」）
  - 許可リストで原理的に解決できない構文（リダイレクト・`$(…)`・`<(…)`・`for` / `while`）。
    既にプロンプト側で「実行できない。未検証と明記せよ」と指示済みであり、本 PR では触らない

## 設計

### 1. 追加する許可（いずれも読み取り専用）

| 追加 | 対象 | 理由 |
| --- | --- | --- |
| `Bash(grep:*)` / `Bash(sort:*)` | 両ワークフロー | 拒否 4 件（`grep \| sort` 3 件・`git show \| grep` 1 件）の直接原因。パイプは各コマンドが個別に判定されるため、前段が許可済みでも後段の `grep` で落ちる |
| `Bash(git -C src/ai-stock-trading {log,show,diff,ls-tree}:*)` | 両ワークフロー | 拒否 1 件（`git -C src/ai-stock-trading log`）。`Bash(git log:*)` は**コマンド文字列の前方一致**なので `git -C <dir> log` には当たらない |
| `Bash(git -C src/ai-stock-trading/planning {log,show,diff,ls-tree}:*)` | 両ワークフロー | 同じ理由。submodule は入れ子で 3 つ（`planning` / `src/ai-stock-trading` / その配下の `planning`）あり、ワークフローは `git submodule update --init --recursive` で 3 つとも populate する。`src/ai-stock-trading` 用のエントリでは前方一致しない（`git -C src/ai-stock-trading/planning …` は別文字列） |

**粒度は「パス × 読み取り専用サブコマンド」の列挙とする。** `Bash(git -C:*)` のような一括許可は
前方一致で `git -C <dir> push` / `commit` / `reset` まで通し、「書き込み系 git をレビューへ入れない」
という既存設計（キットのコメントが明記）を崩すため採らない。サブコマンドは `planning` に既にある
4 種（`log` / `show` / `diff` / `ls-tree`）へ揃える。

### 2. `mcp__github__list_sub_issues`（拒否 1 件）

**許可は追加しない。** 許可済みの `mcp__github__issue_read` が `method: get_sub_issues` を持ち、
同じ情報を取得できるためである。ツールを増やすより、既に許可済みの経路へ誘導するほうが
攻撃面を広げない。両ワークフローの指示文（レビュー用は `prompt:`、実装用は
`--append-system-prompt`）に「子 issue の一覧は `mcp__github__issue_read` の
`method: get_sub_issues` を使う（`list_sub_issues` は許可されていない）」を明記する。
#454 は子 issue 20 件のトラッキング issue であり、以後の PR で確実に踏む経路である。

### 3. 対称性の維持

追加はすべて読み取り専用であり、実装用（`claude-coding.yml`）にも同じ作業（submodule pin の確認・
キット同期時の突き合わせ）があるため、**両ファイルへ同じ内容を入れる**。
`check-ai-workflow-config.js` の `toolchainDrift` は**スタック別の実行ツールしか見ない**ため、
この種の非対称は機械検出されない（planning#160 で注記済み）。

### 4. 3 系統の同期（`.claude/settings.json`）

`ci.yml` の `ai-workflow-config` ジョブは `STRICT_AI_WORKFLOW_CONFIG=1` で走る。この状態では
`parityWarnings`（settings.json の `allow` に無いツールをワークフローが許可している）が
**警告ではなく失敗**になる。したがって settings.json への同内容の追加は任意ではなく**必須**である。

### 5. IADR を新規に起こさない理由

粒度の決定（`Bash(git -C:*)` を採らずパス × サブコマンドで列挙する）は、キットのコメントが
既に確定させている方針の適用であり、新しい意思決定ではない。本件は [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)
の運用（分類 B の固有デルタ＋環流）に沿った是正であるため、記録は本仕様書と `feedback/` に残す。

## 受け入れ基準

- [x] `claude-code-review.yml` / `claude-coding.yml` の `--allowedTools` に上記の読み取り専用
      コマンドが揃い、両者が対称である（設計上の差＝`Edit` / `Write` / 書き込み系 git を除く）
- [x] `.claude/settings.json` の `permissions.allow` が同じ内容を含む（3 系統同期）
- [x] `node scripts/check-ai-workflow-config.js` が成功する（`STRICT_AI_WORKFLOW_CONFIG=1` でも成功する）
- [x] `node scripts/check-ai-workflow-config.js --self-test`（23 件）/ `node scripts/scripts.test.js`
      （154 件）が成功する
- [ ] 本 PR 自身の `claude-review` ジョブが**権限拒否 0 件**で green になる
      （緑になっただけでは不十分。実行サマリで拒否 0 件を確認する）
- [x] キットへの環流を `feedback/` に記録し、planning 側へ起票した
      （[planning#163](https://github.com/endazon/project-planning/issues/163)）

## テスト方針

- 機械検査: `scripts/check-ai-workflow-config.js`（記法・SDK 整合・ドリフト・3 系統乖離）を
  厳格モードで実行する。`--self-test` と `scripts/scripts.test.js` / `scripts.repo.test.js` も走らせる。
- 実地検証: 本 PR の `claude-review` ジョブの実行サマリで拒否件数を確認する。間欠発現のため、
  **緑であることではなく「拒否 0 件」であること**を判定基準とする。
- 陰性確認: 追加したエントリが書き込み系を通さないこと（`git -C <dir> push` 等が前方一致しない
  こと）は、エントリがサブコマンド固定である事実で保証される（`Bash(git -C:*)` を入れない）。

## 計画書との差異

- 差異: あり（キット側の不足）。`--allowedTools` は [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)
  でキットを単一情報源とした分類 B のファイルであり、`grep` / `sort` の欠落と `git -C <dir>` の
  planning 決め打ちは**キット由来**で、他の実装リポジトリでも同じ拒否が出る。本 PR の変更は
  planning#140 と同じ**暫定デルタ**（コメントで環流先を参照し、キット反映後の同期で撤去して
  バイト一致へ戻す）として扱い、`feedback/20260803_ai-workflow-grep-sort-and-submodule-git-c.md`
  に記録して計画側へ起票した（[planning#163](https://github.com/endazon/project-planning/issues/163)）。
  あわせて、**読み取り専用ツールの非対称を `toolchainDrift` が検出しない**点（本件が 3 度目の再発）も
  キット側の改善として環流した。

## 未決事項

- なし。ただし `.claude/settings.json` は AI による編集が deny されている
  （`permissions.deny` の `Edit(./.claude/settings.json)` / `Write(...)` と `hooks/guard-bash.js`。
  AI が自分の許可リストを広げられないようにする設計）。**当該ファイルの 10 行はオーナーが適用した**
  （2026-08-03）。適用しないと `ci.yml` の `ai-workflow-config` ジョブ（`STRICT_AI_WORKFLOW_CONFIG=1`）が
  3 系統乖離の警告で失敗する（適用前に実測して確認済み）。
