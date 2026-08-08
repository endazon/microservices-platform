---
title: レビュー用 allowedTools に grep / sort が無く、git -C が planning 決め打ちで submodule の履歴を検証できない
type: plan-feedback
status: accepted
category: その他
related_ids: [NFR, IADR-0115]
source_repo: endazon/microservices-platform
source_ref: issue #460 / PR #459（検出元）/ docs/specs/20260803_issue-460_ai-review-permission-denials.md
author: Claude
created: 2026-08-03
updated: 2026-08-08
---

# フィードバック: `grep` / `sort` の欠落と `git -C` の planning 決め打ち

## 種別

その他（`impl-handoff-kit` の `repo-template` の不足）。計画書（要求・UC・画面）の記述に対する
誤り指摘ではなく、**キットが配布する成果物**（`claude-code-review.example.yml` /
`claude-coding.example.yml` / `.claude/settings.json`）に対するフィードバックである。

## 起点となる計画書

- 機能要求（FR）/ ユースケース（UC）/ 画面（SC）: なし（開発基盤・NFR）
- 関連 ADR: 本リポジトリの `docs/adr/IADR-0115_impl-handoff-kit-as-single-source.md`
  （キットを単一情報源とする同期規約。`--allowedTools` はキットが正であり、実装リポで独自に
  足したものは**暫定デルタ**として扱い、キット反映後の同期で撤去してバイト一致へ戻す）
- 計画書リンク:
  - `tools/impl-handoff-kit/repo-template/.github/workflows/claude-code-review.example.yml`
  - `tools/impl-handoff-kit/repo-template/.github/workflows/claude-coding.example.yml`
  - `tools/impl-handoff-kit/repo-template/.claude/settings.json`

## 現状（As-Is）

PR #459（トラッキング issue #454 の着手準備）の `claude-review` ジョブが**権限拒否 6 件**で
exit 1 した。レビュー本文は完走・投稿されており、**成果物は正しいのに赤**という状態である。

```
Bash(grep | sort)（3 件） / Bash(git -C src/ai-stock-trading log)（1 件）
/ Bash(git show | grep)（1 件） / mcp__github__list_sub_issues（1 件）
```

段階ポリシー（`check-permission-denials.js` の許容値 4 件）を超えたため失敗した。内訳と原因は
次のとおりで、**5 件はキット由来の 2 系統**に集約される。

| 拒否 | 件数 | 原因 |
| --- | --- | --- |
| `Bash(grep \| sort)` | 3 | `grep` / `sort` がいずれも未許可。許可されているのは `rg` のみ |
| `Bash(git show \| grep)` | 1 | 前段 `git show` は許可済みだが、**後段 `grep` で拒否**（パイプは各コマンドが個別判定） |
| `Bash(git -C src/ai-stock-trading log)` | 1 | `Bash(git log:*)` と `Bash(git -C planning log:*)` は在るが、**`git -C <planning 以外>` の形が無い** |
| `mcp__github__list_sub_issues` | 1 | 未許可。ただし許可済みの `mcp__github__issue_read` が `method: get_sub_issues` を持つ |

キットのレビュー用 `--allowedTools`（planning `aeb97c4` 実測）には `cat` / `head` / `tail` /
`cmp` / `diff` / `echo` / `rg` が既に在り、**`grep` と `sort` だけが無い**。planning#145 / planning#146 /
#155 / #157 / #158 / #160 / #161 / #162 が繰り返し塞いできたのと同型の非対称である。

`git -C` は `planning` の 4 サブコマンド（`log` / `show` / `diff` / `ls-tree`）だけが列挙されており、
コメントに「【置換点】submodule のパスが `planning` でない場合は 4 か所とも書き換える」とある。
しかし本リポジトリのように **submodule が複数（入れ子を含む）ある構成**では「書き換え」ではなく
**パスごとの追加**が要る（`planning` / `src/ai-stock-trading` / `src/ai-stock-trading/planning` の 3 つ）。
Bash の許可はコマンド文字列の**前方一致**であるため、`Bash(git -C src/ai-stock-trading log:*)` は
`git -C src/ai-stock-trading/planning log` には当たらない。

### 発現が間欠的である点（実測）

同 PR の 3 ラウンドのうち **1・2 回目が赤・3 回目は緑**だった。レビューがその回に `grep` 等を
何回使おうとしたかで拒否件数が変わり、**許容値 4 件を超えた回だけ赤くなる**。設定不足そのものは
3 回とも同じだけ存在する。「毎回赤」より質が悪い——**「再実行したら緑になった」で片付ける運用**を
誘発し、`check-permission-denials.js` を入れた目的（レビューが実質未実施であることの可視化）を
逆から壊す。planning#162 が警告した「成果物は正しいのに赤の常態化」の一形態である。

## 問題点 / あるべき姿（To-Be）

1. **`grep` / `sort` は読み取り専用の基本コマンドであり、`rg` があるから不要とは言えない。**
   AI は `git show <ref>:<path> | grep -n …` のように**前段が git のときは自然に `grep` を選ぶ**
   （`rg` は標準入力を読めるが、パイプの後段で `grep` を選ぶ確率は消せない）。件数の集約に
   `sort` を使うのも同様である。許可されていないと、鎖全体が実行されず前段の結果も得られない。
2. **`git -C` の粒度がキットのテンプレートで表現されていない。** 「置換点」というコメントは
   submodule が 1 つの構成しか想定しておらず、複数・入れ子の構成では静かに不足する。
   本件は `git -C <dir>` 形の拒否として **2 度目**である（planning#160 は報告ラベルの
   `Bash(git -C)` 切り詰めを是正したが、**許可の側は planning 決め打ちのままだった**）。
3. **読み取り専用ツールの非対称が機械検出されない。** `check-ai-workflow-config.js` の
   `toolchainDrift` は `TOOLCHAINS`（スタック別の実行ツール）しか比較しないため、
   `grep` / `sort` / `git -C …` の片落ちは検出されない。planning#160 の反映時にも
   「手で揃えること」というコメントが足されただけで、機械化はされていない。同じ型の欠落が
   **3 度目**（#155 の `cat`/`head`/`tail`、#160 の `cmp`/`diff`、本件の `grep`/`sort`）である以上、
   人手の規律ではなく検査で守るべきである。
4. あるべき姿: キット配布時点で読み取り専用の基本コマンドが揃っており、submodule が複数ある
   構成でも `git -C` の列挙方法が明示され、両ワークフローの非対称が CI で止まること。

## 実装で判明した経緯

- 検出: PR #459 の `claude-review` ジョブ（拒否 6 件で exit 1）。3 ラウンド中 2 回が赤。
- 起票: 本リポジトリ issue #460。作業: `docs/specs/20260803_issue-460_ai-review-permission-denials.md`。
- 本リポジトリでは **暫定デルタ**として両ワークフローと `.claude/settings.json` の 3 系統へ
  `Bash(grep:*)` / `Bash(sort:*)` と `git -C src/ai-stock-trading[/planning] {log,show,diff,ls-tree}`
  を追加した（#454 の再実装が子 issue 20 件＝ PR 20 本であり、キット反映を待つと相当数の PR で
  同じ間欠的な赤が出るため。前回 planning#160 の記録では「上流の修正を待って同期する」としたが、
  本件は待てない規模である）。キット反映後の同期で撤去し、バイト一致へ戻す。
- なお `mcp__github__list_sub_issues` は**許可を追加していない**。許可済みの
  `mcp__github__issue_read`（`method: get_sub_issues`）で同じ情報が得られるため、
  プロンプト（レビュー用は `prompt:`、実装用は `--append-system-prompt`）で誘導する方式を採った。

## 提案（計画への反映案）

反映先候補: **`impl-handoff-kit` の修正**（要求更新・新 ADR ではない）

1. **`Bash(grep:*)` / `Bash(sort:*)` を両テンプレートの `--allowedTools` と
   `repo-template/.claude/settings.json` へ追加する**（3 系統）。いずれも読み取り専用である。
2. **`git -C <submodule>` の列挙をテンプレートで一般化する。**
   - コメントを「置換点（1 つの submodule を前提）」から「**`.gitmodules` にあるパスごとに
     4 サブコマンドを列挙する。入れ子の submodule も別パスとして列挙が要る**」へ改める
     （ワークフローは `git submodule update --init --recursive` で入れ子まで populate するため、
     読める範囲と許可の範囲が食い違っている）。
   - `Bash(git -C:*)` の一括許可を採らない理由（前方一致で `push` / `commit` / `reset` まで通る）は
     既存コメントのまま維持する。
3. **`toolchainDrift` を読み取り専用の汎用ツールまで広げる**（`check-ai-workflow-config.js`）。
   - 実装用にしか無いのが正しいツール（`Edit` / `Write` / 書き込み系 git / `mkdir` / `find`）と、
     レビュー用にしか無いのが正しいツール（`gh pr view` 等）を**明示の除外リスト**として持ち、
     それ以外の `Bash(...)` 指定の差分を ERROR にする、という形が素直である。
   - 現状は「手で揃えること」という注意書きだけであり、同じ欠落が 3 度再発している。
     受け入れ時は陽性対照（片方から `Bash(grep:*)` を抜いて ERROR を確認し、戻して合格を確認）を取る。
4. **`mcp__github__list_sub_issues` の扱いを明文化する。** 許可を増やさず、
   `mcp__github__issue_read` の `method: get_sub_issues` を使うようテンプレートのプロンプトに
   1 行入れる（トラッキング issue を持つリポジトリでは確実に踏む経路である）。

## 影響範囲

- 影響先: キットを利用する全実装リポジトリの `claude-coding` / `claude-code-review` ジョブ。
  提案 1・2 は許可の追加（読み取り専用のみ）であり、既存の判定を緩める方向だが書き込み系は含まない。
  提案 3 は新しい ERROR 条件であり、**既存リポジトリで非対称があれば CI が赤くなる**——
  受け入れ時は段階導入（まず warn、次のラウンドで ERROR）も選択肢である。
- 本リポジトリ側: 暫定デルタを保持し、キット反映後の同期で撤去する（IADR-0115 の運用）。
- 関連: planning#145 / planning#146（読み取り系 git の欠落）・#147（拒否報告をコマンド名まで出す）・
  #155 / #157 / #158（整形パイプ・検証の誠実性）・#160（`git -C` の報告ラベル・`cmp` / `diff` 追加）・
  #161 / #162（段階ポリシーと「成果物は正しいのに赤」の常態化）。本件はその系列の続きである。

## 追加の判明事項（2026-08-03・PR #461 のレビュー指摘から実測）

**キットが 3 系統に埋め込んでいる GitHub MCP のツール名が、CI が実際に実行するサーバの版と
食い違っている。** 当初は `mcp__github__list_sub_issues` の拒否を「許可済みの
`mcp__github__issue_read`（`method: get_sub_issues`）へ誘導すれば足りる」と扱ったが、
本 PR のレビューが 🔴 で反証し、次を実測で確認した。

- claude-code-action v1 は `ghcr.io/github/github-mcp-server:sha-23fa0dd` = **v0.17.1** を pin する
  （`src/mcp/install-mcp-server.ts`）。
- v0.17.1 に在るのは `get_issue` / `list_sub_issues` / `get_pull_request` /
  `create_pending_pull_request_review` / `submit_pending_pull_request_review` /
  `add_comment_to_pending_review` / `add_issue_comment` / `get_file_contents` / `push_files`。
  統合名 **`issue_read` / `pull_request_read` / `pull_request_review_write` は存在しない**
  （統合名は v1.x 系で入る。最新 v1.8.0 には `issue_read` がある）。
- キットの `.claude/settings.json` の `//` 注記と両ワークフローのプロンプトは
  「`issue_read` / `pull_request_read` / `pull_request_review_write` が現行。
  `get_issue` / `get_pull_request` / `create_*_review` は廃止名」と書いているが、
  **CI が実行する版では逆**である。

この不一致は**拒否として現れない**ため気付けない（存在しないツールは AI へ提示されず、AI は
`gh` CLI 等へ迂回する）。すなわち「許可したつもりのエントリが 3 件とも当たっていない」状態が
静かに続く。プロンプトが誤った名前を「現行」と教えている分、AI は正しい名前を試さない。

**追加提案 5**: キットの 3 系統で GitHub MCP のツール名を**新旧併記**にする
（`issue_read` と `get_issue` の両方を許可する）。アクションがサーバを更新しても壊れず、
現在の版でも当たる。あわせて `.claude/settings.json` の `//` 注記とプロンプトの
「廃止名」という説明を「**サーバの版に依存する。アクションが pin する版を確認すること**」へ
改める。参考手順: `anthropics/claude-code-action` の `src/mcp/install-mcp-server.ts` で pin されている
イメージ tag を読み、`github/github-mcp-server` の該当 tag の `pkg/github/*.go` の
`mcp.NewTool("…")` を確認する。

**追加提案 6**: レビュー用の `Bash(git show:*)` が実装用に無い（`.claude/settings.json` には在る）。
読み取り専用であり、意図的な差の一覧（`Edit` / `Write` / 書き込み系 git 等）にも該当しない。
提案 3（`toolchainDrift` を読み取り専用ツールへ広げる）が入れば機械的に検出される類である。

## 計画側への起票（2026-08-03）

計画リポジトリへ [planning#163](https://github.com/endazon/project-planning/issues/163) として起票済み
（`plan-feedback` ラベル。上記の提案 1〜4 をそのまま記載）。反映されたら本リポジトリの暫定デルタ
（両ワークフローと `.claude/settings.json` の 3 系統）を撤去し、キットとバイト一致へ戻す。

追記コメント 2 件:

1. [追加提案 5・6](https://github.com/endazon/project-planning/issues/163#issuecomment-5159486402)
   — MCP ツール名の版依存（新旧併記）と `Bash(git show:*)` の非対称。
2. [提案 5 の補足](https://github.com/endazon/project-planning/issues/163#issuecomment-5159537243)
   — **誤った前提の発生源はキットの `.claude/settings.json` の `//` 注記**であること。
   許可リストを直しても、直し方を教える注記が「`get_issue` 系は廃止名」のままなら、
   後日「廃止名だから」と削られて**当たらないエントリの状態へ静かに戻る**（削っても拒否は
   出ない）。同ファイルは AI による編集が deny されており人手でしか直せないため、
   キットが正しい文言を配布する価値が他のファイルより高い、という点も添えた。
