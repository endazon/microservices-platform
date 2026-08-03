---
title: 実走を求めるプロンプトと allowedTools の非対称 — which / dotnet --version / list_workflow_runs の欠落と、rm・シェル構文の扱い
type: plan-feedback
status: open
category: その他
related_ids: [NFR, IADR-0115]
source_repo: endazon/microservices-platform
source_ref: issue #469 / PR #464（検出元）/ docs/specs/20260803_issue-469_ai-review-execution-permissions.md
author: Claude
created: 2026-08-03
---

# フィードバック: 実走を求めながら、実走に伴う周辺操作を許可していない

## 種別

その他（`impl-handoff-kit` の `repo-template` の不足）。計画書（要求・UC・画面）の記述に対する
誤り指摘ではなく、**キットが配布する成果物**（`claude-code-review.example.yml` /
`claude-coding.example.yml` / `.claude/settings.json`）に対するフィードバックである。

## 起点となる計画書

- 機能要求（FR）/ ユースケース（UC）/ 画面（SC）: なし（開発基盤・NFR）
- 関連 ADR: 本リポジトリの `docs/adr/IADR-0115_impl-handoff-kit-as-single-source.md`
  （キットを単一情報源とする同期規約。`--allowedTools` とプロンプトはキットが正であり、
  実装リポで独自に足したものは**暫定デルタ**として扱い、キット反映後の同期で撤去する）
- 計画書リンク:
  - `tools/impl-handoff-kit/repo-template/.github/workflows/claude-code-review.example.yml`
  - `tools/impl-handoff-kit/repo-template/.github/workflows/claude-coding.example.yml`
  - `tools/impl-handoff-kit/repo-template/.claude/settings.json`

## 現状（As-Is）

[planning#163](https://github.com/endazon/project-planning/issues/163)（本リポジトリ issue #460 /
PR #461）で `grep` / `sort` と `git -C <submodule>` の欠落を塞いだ**直後**、PR #464 の
`claude-review` が**権限拒否 5 件**（許容値 4）で再び exit 1 した。今回も**レビュー本文は
完走・投稿されており、成果物は正しいのに赤**である。

拒否 5 件（run `30811464950` の実測）:

| 拒否 | 件数 | 原因 |
| --- | --- | --- |
| `Bash(dotnet)` | 1 | `dotnet --version`。許可済みは `restore` / `build` / `test` / `format` の**サブコマンド固定形のみ**で、`dotnet --version` は前方一致しない |
| `Bash(which \| dotnet)` | 1 | `which` が未許可（SDK の在否確認） |
| `Bash(for \| find \| echo \| grep)` | 1 | シェルの `for` 構文。各コマンドが許可済みでも**構文自体が許可リストの型で表せない** |
| `Bash(rm \| …/TestResults \| …)` | 1 | `dotnet test` が生成した `TestResults` の後片付け |
| `mcp__github__list_workflow_runs` | 1 | 未許可。CI 結果を参照できず、レビューはローカル再現の遠回りをした |

### 前回（#460）と性質が違う

- **#460**: レビューが**読み取り作業**（`grep | sort`・submodule の履歴確認）をしようとして塞がれた。
- **#469**: レビューが**検証を実走した結果**として塞がれた。

キットのレビュー用プロンプトは「PR 本文が『テストが通る』と主張している場合は、可能な限り
**自分で実走して確認**せよ」と指示しており、直前のステップで `setup-dotnet` により SDK まで
用意している。実際 PR #464 のレビューは Release 構成で `dotnet build` /
`dotnet test --collect:"XPlat Code Coverage"` を完走させ、PR の主張（レポート 14 件・line 34.46%）を
実測で再現した——**その PR で最も価値のある検証**だった。

その実走に伴って必然的に発生するのが次の 3 つである。いずれも許可されていない。

1. **SDK の在否確認**（`which dotnet` / `dotnet --version` / `dotnet --info`）
2. **複数プロジェクトを回すループ**（`for`）
3. **生成物の後片付け**（`rm -rf */TestResults`）

さらに、**CI の結果を読む手段（`mcp__github__list_workflow_runs`）が無い**ため、レビューは
「PR の CI が何を報告しているか」を見られず、ローカル再現という遠回りを選ばざるを得なかった。
CI 結果の参照は本来レビューの主業務である。

## 問題点 / あるべき姿（To-Be）

1. **「実走せよ」と指示するなら、実走に伴う周辺操作まで含めて許可を設計すべきである。**
   キットは実行系（`dotnet build/test/format`）を許可し SDK も用意しているのに、
   実行の前後で必ず出る操作（在否確認・生成物の扱い）を想定していない。
   個別のツール名を後追いで足す運用では追いつかない（#460 の 1 週間以内に別ルートで再発した）。
2. **`dotnet` の許可がサブコマンド固定形しか無い。** 前方一致であるため `dotnet --version` は
   どのエントリにも当たらない。一方で `Bash(dotnet:*)` の一括許可は `dotnet nuget push` /
   `dotnet tool install` まで通すため採れない。**引数固定形**（`Bash(dotnet --version)` /
   `Bash(dotnet --info)`）で足すのが妥当であり、同じ形は他スタックにもある
   （`node --version` / `npm --version` / `python3 --version` / `go version`）。
3. **`rm` は許可すべきではないが、「消さなくてよい」と教えるべきである。** ワークスペースは
   ジョブ終了で破棄される使い捨てであり、後片付けはレビューの責務ではない。キットのプロンプトは
   「レビュー用に書き込み手段は無い」と宣言しているのに、**実走が生成物を作ることには触れていない**。
   AI は「散らかしたまま終わってよいのか」を判断できず、自然に `rm` を試みて拒否 1 件を消費する。
4. **シェル構文は許可リストで原理的に表現できないので、代替手順まで書くべきである。**
   キットのプロンプトには既に「`for` / `while` は実行できない。未検証と明記せよ」という一般則が
   あるが、**実走を指示している以上「では何をすればよいか」まで示さないと再発する**
   （「1 ファイルずつ個別のコマンドで」はファイル走査の話であり、ビルド／テストの話ではない）。
   ソリューション単位・ワークスペース単位の 1 コマンドへ寄せる、といった具体が要る。
   **許可リストで表現できないシェル構文は 1 つの型ではなくリストである。**キットは型ごとに
   代替手順を書く必要がある（下記「追記」の実測で `VAR=1 cmd` が 4 つ目、単独の変数代入と
   3 段以上のパイプ連鎖が 5・6 つ目の型として増えた）:
   - シェルのループ・複合形（`for` / `while` / `if … then … fi`）→ 単位ごとの 1 コマンドへ寄せる
   - リダイレクト（`>` / `>> ` / `> /dev/null`）→ 捨てたい出力は最初から生成しない
   - コマンド置換・プロセス置換（`$(…)` / `<(…)`）→ パイプ + 標準入力（`cmd | diff - <path>`）
   - **環境変数の前置き（`VAR=1 cmd`）→ その処理系の 1 コマンドに閉じる**
     （Node なら `node -e "process.env.VAR='1'; require('./…');"`。
     ただし後述のとおり `require` 形は `require.main` ガードを持たないスクリプトにしか通用しない）
   - **単独の変数代入（`PR_TITLE="…"` だけのコマンド）→ 値を変数へ置いて使い回さず、
     必要な文字列は実行する 1 コマンドの中へ直接埋め込む**（環境変数の前置きと同型。
     先頭トークンが `PR_TITLE=…` になり、どの許可エントリにも前方一致しない）
   - **3 段以上のパイプ連鎖（`a | b | c`）→ 2 段に収め、絞り込みはコマンドを分けて 2 回実行する**
     （各コマンドが個別に許可済みでも拒否された実測がある。下記「追記 2」。根本原因は未特定）
5. **CI 結果参照の MCP ツールが許可されていない。** `Bash(gh run list:*)` はレビュー用にのみ
   在るが、実行の詳細（ジョブ・結論・ログ）を読むには MCP 側が要る。なお Actions 系の
   ツール名は**サーバの版に依存する**（planning#163 の追加提案 5 と同型の問題）:
   - claude-code-action v1 が pin する **v0.17.1** の `pkg/github/actions.go` には
     `list_workflow_runs` / `list_workflows` / `get_workflow_run` / `get_job_logs` が個別ツールとして在る。
   - 最新（`main`）では `actions_get` / `actions_list` / `actions_run_trigger` へ**統合**され、
     `list_workflow_runs` は `actions_list` の**メソッド名**になっている。
   - したがって**新旧を併記**しないと、どちらかの版で当たらないエントリになる。
6. あるべき姿: キット配布時点で「実走に伴う周辺操作」が許可に含まれ、許可リストで表現できない
   事項（生成物の扱い・シェル構文）はプロンプトが**代替手順まで**指示していること。

## 実装で判明した経緯

- 検出: PR #464（#453 退行防止テスト基盤）の `claude-review` が拒否 5 件で exit 1。
- 系譜: #460 → PR #461（`grep` / `sort` / `git -C <submodule>` を追加）→ **1 週間以内に #469 で別ルート再発**。
  planning#145 / planning#146 / planning#155 / planning#157 / planning#158 / planning#160 /
  planning#161 / planning#162 / planning#163 と続く同系列の 10 度目である。
- 起票: 本リポジトリ issue #469。作業: `docs/specs/20260803_issue-469_ai-review-execution-permissions.md`。
- 本リポジトリでは**暫定デルタ**として両ワークフローへ次の 5 エントリを追加した
  （`.claude/settings.json` は AI 編集が deny のためオーナーが適用する）。

  ```
  Bash(which:*)
  Bash(dotnet --version)
  Bash(dotnet --info)
  mcp__github__list_workflow_runs
  mcp__github__actions_list
  ```

- `rm` は**追加していない**。プロンプトへ「`dotnet test` の生成物は削除しなくてよい」と明記して
  使わせない方向で塞いだ。`for` も同様に、slnx 単位の 1 コマンドへ寄せる手順をプロンプトへ足した。
- `PERMISSION_DENIALS_TOLERANCE` の引き上げは採っていない（原因を隠すだけである）。

## 提案（計画への反映案）

反映先候補: **`impl-handoff-kit` の修正**（要求更新・新 ADR ではない）

1. **実走に伴う読み取り専用操作を両テンプレート＋`repo-template/.claude/settings.json` の
   3 系統へ追加する。** `Bash(which:*)` と、スタック別のバージョン確認を**引数固定形**で
   （C#/.NET なら `Bash(dotnet --version)` / `Bash(dotnet --info)`。他スタックの対照表にも
   `node --version` / `npm --version` / `python3 --version` / `go version` を載せる）。
   `Bash(<tool>:*)` の一括許可を採らない理由（前方一致で `nuget push` / `tool install` まで通る）を
   既存の `git -C` の注意書きと同じ形でコメントに残す。
2. **CI 結果参照の MCP ツールを許可へ入れる**（`mcp__github__list_workflow_runs` と
   統合名 `mcp__github__actions_list` の**併記**）。理由と版依存の注記は planning#163 の
   追加提案 5 と同じ枠組みで書ける。
3. **プロンプトへ「実走の作法」節を足す**（レビュー用は `prompt:`、実装用は `--append-system-prompt`）。
   - 生成物（テスト結果・カバレッジ・ビルド出力）は**削除しなくてよい**。ワークスペースは
     使い捨てであり、後片付けは責務ではない（`rm` は許可されておらず、試みは拒否として記録される）。
   - 複数プロジェクトを回すときは `for` を組まず、**ソリューション／ワークスペース単位の 1 コマンド**で
     実行する。パイプは許可済みコマンド同士に限る。
   - SDK の在否は `--version` / `--info` / `which` で確認する。
4. **`toolchainDrift` の検査範囲を広げる**（planning#163 の提案 3 の再掲・優先度引き上げ）。
   本件の `Bash(which:*)` と MCP ツールは `TOOLCHAINS` に載らないため、片落ちしても機械検出されない
   （`Bash(dotnet --version)` は `dotnet` にマッチするので検出される）。読み取り専用ツールの
   非対称が検出されないまま**4 度目**の再発である。
5. **「実走を求めるなら周辺操作も設計する」という原則をキットの HOWTO へ明文化する。**
   個別ツール名の後追いでは追いつかないことが #460 → #469 の 1 週間で実証された。
   プロンプトが AI へ要求する行動（実走・CI 参照・submodule 履歴の検証）と `--allowedTools` を
   **対にしてレビューする**チェック項目を、キット同期の手順に加えるのが本質的である。

## 影響範囲

- 影響先: キットを利用する全実装リポジトリの `claude-coding` / `claude-code-review` ジョブ。
  提案 1・2 は読み取り専用の許可追加であり、書き込み系は含まない。提案 3 はプロンプトの追記のみ。
  提案 4 は新しい ERROR 条件であり、既存リポジトリに非対称があれば CI が赤くなる（段階導入が妥当）。
- 本リポジトリ側: 暫定デルタを保持し、キット反映後の同期で撤去してバイト一致へ戻す（IADR-0115 の運用）。
- 関連: planning#145 / planning#146（読み取り系 git の欠落）・
  planning#147（拒否報告をコマンド名まで出す）・
  planning#155 / planning#157 / planning#158（整形パイプ・検証の誠実性）・
  planning#160（`git -C` の報告ラベル・`cmp` / `diff` 追加）・
  planning#161 / planning#162（段階ポリシーと「成果物は正しいのに赤」の常態化）・
  planning#163（`grep` / `sort` / `git -C <submodule>` / MCP ツール名の版依存）。
  本件はその系列の続きである。

## 計画側への起票

**起票済み**: [planning#168](https://github.com/endazon/project-planning/issues/168)
（`impl-handoff-kit`: AI レビューに実走を求めるなら実走の周辺権限も揃える（MSP からの環流））。

planning#163 が同じキットの同じファイル群を対象としているが、IADR-0115 の
「記録 1 件 ↔ 環流 1 件」規約（`/sync-impl` が記録と環流を 1 対 1 で突き合わせて到達を判定するため、
キット側の不足は**記録を分けて起こす**）に従い、planning#163 への追記ではなく
**新規 issue planning#168 として起票済み**である。
反映されたら本リポジトリの暫定デルタを撤去し、キットとバイト一致へ戻す。

## 追記（2026-08-03）: 初回実走で「環境変数の前置き」が 4 つ目の型として現れた

PR #475（本フィードバックに基づく暫定デルタの適用）をマージした**直後の初回実走**
（PR #479 の `claude-review` / run `30829121373`）で、**権限拒否 1 件**を実測した。

| 拒否 | 件数 | 原因 |
| --- | --- | --- |
| `Bash(REQUIRE_REPO_TESTS=1 node \| tail)` | 1 | 環境変数の前置き形。先頭トークンが `REQUIRE_REPO_TESTS=1` になり、どの許可エントリにも前方一致しない |

- 追加した 5 エントリ（`which` / `dotnet --version` / `dotnet --info` / MCP 2 件）由来の拒否は
  **0 件**であり、提案 1・2 の効果は確認できた。残ったのは提案 3・4（プロンプトで手順を狭める型）である。
- 本件は `for` ループと**同型**である。`Bash(env:*)` を許すと任意コマンドが通るため
  許可リストでは塞げず、**(b) プロンプト側で手順を狭める**しか手が無い。
- 重要なのは、**当のレビュー自身が回避形を自力で発見して使っていた**ことである
  （`node -e "process.env.REQUIRE_REPO_TESTS='1'; require('./scripts/scripts.test.js');"`）。
  すなわち代替手順は存在し、書けば済む話であるのに、キットのプロンプトが書いていないために
  AI は毎回 1 件を拒否として消費してから発見し直す。
- 対応: 両ワークフローのプロンプト（レビュー用 `prompt:` / 実装用 `--append-system-prompt`）へ
  「環境変数の前置き形は必ず拒否される。Node へ環境変数を渡すときは `node -e` で `process.env` を
  設定してから `require` する形（`node` の 1 コマンドに閉じる）を使う」を追記した（本リポジトリの暫定デルタ）。

**キットへの追加提案（planning#168 へ追記する内容）**: 提案 3「実走の作法」に、
上記「許可リストで表現できないシェル構文の型のリスト」と**型ごとの代替手順**を入れること。
一般則（「シェル構文は使えない」）だけでは、AI は具体の型に出会うたびに拒否を 1 件出してから
回避形を探す。型を列挙して代替を先に示すのが、拒否 0 件を安定させる唯一の方法である。

## 追記 2（2026-08-03）: 拒否 7 件 — 型が 2 つ増え、「許可の粒度」と「回避形の落とし穴」が露見した

PR #480（上記「追記」の対応）の `claude-review` 実走（run `30830151995` / HEAD `734da26`）で
**権限拒否 7 件**（許容値 4 超過）を実測し、ジョブは fail した。
`scripts/check-permission-denials.js` の出力による内訳は次のとおりである。

| 拒否 | 件数 | 型 |
| --- | --- | --- |
| `Bash(git show \| grep \| diff)` | 2 | 3 段パイプ連鎖 |
| `Bash(git show \| grep \| echo)` | 1 | 3 段パイプ連鎖 |
| `Bash(gh run)` | 1 | 前方一致の粒度（`gh run list` のみ許可） |
| `Bash(PR_TITLE="fix \| IADR-0115)` | 1 | 単独の変数代入 |
| `mcp__github__get_workflow_run` | 1 | MCP はツール名単位の許可 |
| `mcp__github__list_workflow_jobs` | 1 | MCP はツール名単位の許可 |

チェッカーは同時に「リダイレクト（`>`）を含むコマンドがある」とも出力した。3 段パイプの各コマンド
（`git show` / `grep` / `diff` / `echo`）はいずれも `--allowedTools` に個別に存在するため、
**3 件の拒否の根本原因は実測出力からは特定できていない**（判定が長い連鎖を扱えないのか、鎖の
どこかにリダイレクト等が混ざったのかを切り分けられない）。ここでは原因を断定せず、
**「3 段以上の連鎖を組ませない」という運用側の回避**として扱う。

### 新たに判明した 2 点

1. **許可の粒度が型として現れた。**「不足しているツールを足す」では届かない、
   *同じツールの別サブコマンド／別ツール名* が拒否の源になっている。
   - Bash は**前方一致**であるため、`Bash(gh run list:*)` があっても `gh run view` は当たらない。
   - MCP は**ツール名単位**であるため、`list_workflow_runs` / `actions_list` を許可しても
     `get_workflow_run` / `list_workflow_jobs` / `get_job_logs` は当たらない。
   - 結果として、レビューは **CI の実行一覧と結論までしか読めず、ジョブ単位のログを取る手段が無い**。
     プロンプトはこの限界を明示し、それ以上の裏付けが要る主張は「未検証」と書かせる必要がある。
     あわせて、`permission_denials_count`（レビュー自身の拒否件数）は
     ジョブ末尾の `Check permission denials` ステップが権威であり、
     **レビュー側で実行ログを取り直して再検証する必要は無い**ことも書くべきである
     （再検証の試みが `gh run view` / `get_job_logs` を呼び、拒否を増やす動機になっている）。
2. **キットが書いた「回避形」自体に落とし穴があった。**「追記」で提示した
   `node -e "process.env.X='1'; require('./scripts/…');"` は、**`require.main === module` ガードを
   持たないスクリプトにしか通用しない**。本リポジトリの `scripts/check-ai-workflow-config.js` は
   末尾に `if (require.main === module) { main(process.argv); }` があるため、`require` 形では
   `main` が呼ばれず**無出力のまま exit 0** になる。
   これは単に効かないより悪く、**「検査していないのに成功に見える」**ため、
   キットが最重要と位置づける「検証の誠実性」を直接損なう。
   実測（本作業の worktree）:

   ```
   # require 形: 出力なし / exit 0（何も検査していない）
   node -e "process.env.STRICT_AI_WORKFLOW_CONFIG='1'; require('./scripts/check-ai-workflow-config.js');"

   # 子プロセス形: 「AI ワークフロー設定チェック: 2 件を検査 / ✓ 問題なし」/ exit 0
   node -e "const r=require('child_process').spawnSync('node',['scripts/check-ai-workflow-config.js'],{env:Object.assign({},process.env,{STRICT_AI_WORKFLOW_CONFIG:'1'}),stdio:'inherit'});process.exit(r.status===null?1:r.status);"
   ```

   したがってキットは、環境変数の代替形として**子プロセス形（`spawnSync` ＋ 終了コードの伝播）を
   標準形**とし、`require` 形はガードの無いスクリプト限定である旨を併記すべきである。
   さらに「**出力が 1 行も出なかったときは実測したと書かない**」という判定則を足すと、
   同種の取り違えが誠実性の欠落へ変わるのを防げる。
   なお**ガードの有無はスクリプトごとに異なる**ため、キットは「このファイルだけが例外」という
   固定のリストを書いてはならない（実装リポジトリごとに顔ぶれが変わる）。本リポジトリの
   `scripts/` でも、ガードを持たないものは `scripts.test.js` の他に `gen-openapi-skeleton.js` /
   `k8s-local-up.test.js` / `scripts.repo.test.js` / `validate-pipeline-config.js` があった（実測）。
   書くべきは**「使う前に対象スクリプト末尾のガード有無を自分で確認する」という手順**である。

### 本リポジトリの対応（暫定デルタ）

- 両ワークフローのプロンプト（レビュー用 `prompt:` / 実装用 `--append-system-prompt`）へ、
  上記 1・2 と「単独の変数代入」「3 段以上のパイプ連鎖」の制約を**対称に**追記した。
- **`--allowedTools` / `.claude/settings.json` / `PERMISSION_DENIALS_TOLERANCE` は変更していない**
  （issue #469 の方針「許容値を上げず、プロンプト側で作業手順を狭める」に従う）。

### planning#168 へ追記する内容

- シェル構文の型リストに**単独の変数代入**と**3 段以上のパイプ連鎖**を加える。
- 環境変数の代替形を**子プロセス形（`spawnSync`）を標準**へ改め、`require` 形の適用条件
  （`require.main` ガードが無いこと）と、取り違えたときの症状（無出力 exit 0）を明記する。
  適用条件は**固定のファイル名リストではなく確認手順**として書く（上記 2 の末尾）。
- **許可の粒度**（Bash = 前方一致 / MCP = ツール名単位）を、キットのプロンプトが
  「何が読めて何が読めないか」として先に宣言する。特に CI 結果は一覧と結論までで、
  ジョブログは取得できない旨と、拒否件数は CI のチェックステップが権威である旨を書く。
- **プロンプトが宣言する「許可されているもの」は、同じファイルの `--allowedTools` と
  一致していなければならない。** レビュー用と実装用でプロンプトを機械的に対称化すると、
  片方にしか無いツールを「許可されている」と書いてしまい、**指示に従った AI が新たな拒否を
  出す**（本リポジトリの実測: 実装用には `Bash(gh run list:*)` が無いのに、レビュー用の
  文面をそのまま写して「`gh run list` のみ許可」と書いてしまい、クロス監査で是正した）。
  キットは**プロンプトの記述と `--allowedTools` の突き合わせ**を同期手順のチェック項目に加え、
  可能なら `check-ai-workflow-config` 相当の機械検査へ入れるべきである
  （提案 4「`toolchainDrift` の検査範囲を広げる」と同じ枠組みで扱える）。
