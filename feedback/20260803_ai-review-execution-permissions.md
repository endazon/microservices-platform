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
