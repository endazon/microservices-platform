---
title: 実走に伴う周辺操作の権限拒否を塞ぐ（which / dotnet --version / list_workflow_runs）とプロンプトによる rm・シェル構文の抑止
type: spec
status: in-progress
related_ids:
  - NFR
  - IADR-0115
author: claude
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - "../adr/IADR-0115_impl-handoff-kit-as-single-source.md"
related_specs:
  - "../adr/IADR-0115_impl-handoff-kit-as-single-source.md"
  - "./20260803_issue-460_ai-review-permission-denials.md"
---

# 仕様書: AI レビューの実走に伴う権限拒否を塞ぐ（issue #469）

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#469](https://github.com/endazon/microservices-platform/issues/469)
- 起点 ID: NFR（保守性・運用性。CI の信頼性）／[IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)
  （`impl-handoff-kit` を足場の単一情報源とする同期規約。両ワークフローは**分類 B**）
- 検出元: PR [#464](https://github.com/endazon/microservices-platform/pull/464)（#453 退行防止テスト基盤）の
  `claude-review` ジョブが**権限拒否 5 件**（許容値 4）で exit 1。レビュー本文は完走・投稿済み
- 先行: [#460](https://github.com/endazon/microservices-platform/issues/460)（PR #461 で解消。
  作業仕様書は [20260803_issue-460_ai-review-permission-denials.md](./20260803_issue-460_ai-review-permission-denials.md)）

## 目的・背景

#460 は「レビューが**読み取り作業**をしようとして塞がれた」形だった。本件は「レビューが
**検証を実走した結果**として塞がれた」形であり、同じ「成果物は正しいのに赤」でも原因の性質が違う。

`claude-code-review.yml` のプロンプトは「PR 本文が『テストが通る』と主張している場合は、可能な限り
**自分で実走して確認**せよ」と指示している。PR #464 のレビューは実際に Release 構成で
`dotnet build` / `dotnet test --collect:"XPlat Code Coverage"` を完走させ、PR の主張を実測で再現した。
その過程で必然的に発生するのが「SDK の在否確認」「複数プロジェクトのループ処理」「生成物の後片付け」
であり、**実走を求めながら実走に伴う周辺操作を許可していない**という非対称が露見した。

拒否 5 件の内訳（run `30811464950` の実測）:

| 拒否 | 件数 | 原因 |
| --- | --- | --- |
| `Bash(dotnet)` | 1 | `dotnet --version`。許可済みは `restore` / `build` / `test` / `format` の**サブコマンド固定形のみ**で、`dotnet --version` はどれにも前方一致しない |
| `Bash(which \| dotnet)` | 1 | `which` が未許可（SDK の在否確認に使おうとした） |
| `Bash(for \| find \| echo \| grep)` | 1 | シェルの `for` 構文。各コマンドが許可済みでも**構文自体が許可リストの型で表せない** |
| `Bash(rm \| …/TestResults \| …)` | 1 | `dotnet test` が生成した `TestResults` の後片付け |
| `mcp__github__list_workflow_runs` | 1 | 未許可。CI 結果を参照できず、レビューはローカル再現の遠回りをした |

#454 の再実装は子 issue 20 件＝ PR 20 本であり、#460 を塞いだ直後の別ルート再発を放置すると
「拒否の赤を無視する学習」（[`scripts/README.md`](../../scripts/README.md) の
`check-permission-denials.js` 節が整理した常態化。上流の起票は
[planning#162](https://github.com/endazon/project-planning/issues/162)）が定着する。

**この常態化の典拠は `scripts/README.md` の `check-permission-denials.js` 節（段階ポリシーの設計）と
上流の planning#162 である。IADR-0115 を典拠にしない**（同 IADR に該当記述は無い。IADR-0115 は
`impl-handoff-kit` の**同期規約**として、分類 B・対称性・暫定デルタの文脈でのみ言及する）。

## 対象範囲

- 対象:
  - `.github/workflows/claude-code-review.yml` / `.github/workflows/claude-coding.yml` の
    `--allowedTools` へ読み取り専用ツールを追加し、**両者を対称に保つ**
  - 同 2 ファイルのプロンプト（レビュー用は `prompt:`、実装用は `--append-system-prompt`）へ、
    許可リストでは表現できない事項（生成物の後片付け・シェル構文）の**作業手順の制約**を追記
  - `feedback/` への記録とキット（`impl-handoff-kit`）への環流
- 対象外:
  - `PERMISSION_DENIALS_TOLERANCE` の引き上げ（原因を隠すだけ。#469 の制約で明示的に除外）
  - `Bash(rm:*)` の許可（下記「設計 2」）
  - `Bash(dotnet:*)` の一括許可（下記「設計 1」）
  - `.claude/settings.json` の編集（**AI による編集は deny されている**。下記「未決事項」）

## 設計

### 1. 追加する許可（いずれも読み取り専用・両ワークフローへ同一に追加）

| 追加 | 理由 |
| --- | --- |
| `Bash(which:*)` | SDK の在否確認。読み取りのみで副作用が無い |
| `Bash(dotnet --version)` / `Bash(dotnet --info)` | **引数を固定した形**で足す。Bash の許可はコマンド文字列の前方一致であり、既存の 4 エントリはすべて `dotnet <サブコマンド>` 形なので `dotnet --version` はどれにも当たらない |
| `mcp__github__list_workflow_runs` / `mcp__github__actions_list` | CI 結果の参照は本来レビューの主業務。許可が無いとレビューは CI を見られずローカル再現へ遠回りする。名前が**サーバの版に依存**するため新旧を併記する（下記） |

**`Bash(dotnet:*)` の一括許可は採らない。** 前方一致であるため `dotnet nuget push` /
`dotnet tool install` 等の書き込み・ネットワーク操作まで通る。引数固定形（`Bash(dotnet --version)`）は
その文字列に完全一致した場合のみ許可されるため、拡張の範囲が最小で済む。

**MCP ツール名の新旧併記**は #460 / PR #461 で確立した作法に合わせた（同ファイルの
`issue_read` / `get_issue` の併記と同じ）。実測で確認した事実は次のとおり:

- `anthropics/claude-code-action@v1` の `src/mcp/install-mcp-server.ts` は
  `ghcr.io/github/github-mcp-server:sha-23fa0dd`（= **v0.17.1**）を pin している（2026-08-03 時点で不変）。
- v0.17.1 の `pkg/github/actions.go` には `list_workflow_runs` / `list_workflows` /
  `get_workflow_run` / `get_job_logs` 等が**個別ツール**として在る。
- 最新（`main`）では Actions 系が `actions_get` / `actions_list` / `actions_run_trigger` へ**統合**され、
  `list_workflow_runs` は `actions_list` の**メソッド名**になっている。
- したがって **CI が実際に実行する版で当たるのは `list_workflow_runs`** であり、
  `actions_list` は将来の版のための併記である。どちらも削らない。

### 2. `rm` は許可しない（プロンプトで使わせない）

`dotnet test` の生成物（`TestResults/` / Cobertura の XML）の後片付けは**レビューの責務ではない**。
ワークスペースはジョブ終了で破棄される使い捨てであり、消す理由が無い。`rm` を許可すると、
レビュー用ツール群から書き込み・削除手段を排した設計（「レビュー用に書き込み手段は無い」と
プロンプト自身が宣言している）が崩れる。よって**プロンプトへ「削除しなくてよい」と明示**して、
そもそも試みさせない方向で塞ぐ。

### 3. シェル構文（`for` / リダイレクト / コマンド置換）は作業手順で狭める

許可リストは「コマンド文字列の前方一致」でしか表現できず、`for` / `while` / `>` / `$(…)` /
`<(…)` は**原理的に許可できない**。プロンプトには既に「実行できない。未検証と明記せよ」という
一般則があったが、**実走を指示している以上「では何をすればよいか」まで示さないと再発する**。
そこで既存の該当箇所へ、代替となる具体的な作業手順を足した:

- ビルド・テストを複数プロジェクトへ回すときは `for` を組まず、
  **`src/<unit>/backend/backend.slnx` 単位の 1 コマンド**で実行する
  （CLAUDE.md「技術スタック別ルール」の `dotnet test <unit>/backend/backend.slnx` と同じ粒度）。
- フロントエンドも `src` のワークスペース単位で 1 コマンドにする。

### 4. 対称性の維持（IADR-0115）

追加はすべて読み取り専用であり、実装セッション側にも同じ作業（SDK の在否確認・自分が出した PR の
CI 結果の参照）があるため、**5 エントリすべてを両ファイルへ同一に入れた**。プロンプトの追記も
同内容を、実装用は `--append-system-prompt`（同ファイルは `prompt:` を持たない構造のため）へ入れた。

`check-ai-workflow-config.js` の `toolchainDrift` は `TOOLCHAINS`（スタック別の実行ツール）しか
比較しないため、`Bash(which:*)` や MCP ツールの片落ちは機械検出されない
（`Bash(dotnet --version)` / `Bash(dotnet --info)` は `dotnet` にマッチするので**検出される**）。
この検査範囲の狭さは planning#163 の提案 3 として環流済みであり、本件は 4 度目の再発である。

### 5. IADR を新規に起こさない理由

「引数固定形で足す／一括許可は採らない」「新旧の MCP 名を併記する」は、#460（PR #461）で
既に確定させた方針の適用であり、新しい意思決定ではない。本件は [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)
の運用（分類 B の**暫定デルタ**＋環流）に沿った是正であるため、記録は本仕様書と `feedback/` に残す。

## 変更内容

| ファイル | 変更 |
| --- | --- |
| `.github/workflows/claude-code-review.yml` | `--allowedTools` に 5 エントリ追加。追加理由の解説コメント。プロンプトの「シェルのループ・複合形」節へ slnx 単位の代替手順を追記。「検証の実行」節へ SDK 在否確認の手段・生成物を消さない旨・CI 結果参照の手段を追記 |
| `.github/workflows/claude-coding.yml` | 同じ 5 エントリを追加（対称）。追加理由の解説コメント。`--append-system-prompt` へ CI 結果参照・SDK 在否確認・`for` を組まない・生成物を消さない旨を追記 |
| `docs/specs/20260803_issue-469_ai-review-execution-permissions.md` | 本書 |
| `feedback/20260803_ai-review-execution-permissions.md` | キットへの環流記録 |

`--allowedTools` の追加差分（両ファイル共通）:

```
+Bash(which:*)
+mcp__github__list_workflow_runs
+mcp__github__actions_list
+Bash(dotnet --version)
+Bash(dotnet --info)
```

## 受け入れ基準

- [x] 上記の読み取り専用ツールが `claude-code-review.yml` / `claude-coding.yml` に揃い、両者が対称である
      （既存の意図的な差＝`Edit` / `Write` / 書き込み系 git / `find` / `mkdir` / `gh pr view` 等を除く）
- [x] `rm` / シェル構文はプロンプト側で使わせない形に整理されている（許可は追加していない）
- [x] `node scripts/check-ai-workflow-config.js` が成功する
- [x] `node scripts/check-ai-workflow-config.js --self-test`（23 件）/ `node scripts/scripts.test.js` /
      `node scripts/check-doc-links.js` / `node scripts/check-commit-messages.js` が成功する
- [x] 両 workflow が YAML としてパースできる
- [ ] 本件の変更が入った状態の `claude-review` が**権限拒否 0 件**で green になる
      （間欠発現のため「緑になった」だけでは不十分。実行サマリで 0 件を確認する）
      → **未達**。PR #475 マージ後の初回実走（PR #479 の `claude-review` / run `30829121373`）で
      **拒否 1 件**（環境変数の前置き形 `Bash(REQUIRE_REPO_TESTS=1 node | tail)`）を実測した。
      追加した 5 エントリ由来の拒否は 0 件であり、残ったのは「プロンプトで手順を狭める」型の
      最後の 1 片だった。下記「追補（2026-08-03）」でプロンプトへ代替形を明示した。
      → **なお未達**。その追補を載せた PR #480 の `claude-review` 実走
      （run `30830151995` / HEAD `734da26`）で**拒否 7 件**（許容値 4 超過）となり、
      ジョブは fail した。詳細は下記「追補 2（2026-08-03）」
      → **改善したが、なお未達**。追補 2 を載せた PR #480 の再実走（run `30832367628`）で
      **拒否 3 件**（7 → 3）となり、許容値 4 以下のため**ジョブは success** した。
      ただし基準は「拒否 0 件」であり満たしていない。残り 3 件は**すべて一覧に無いコマンドの
      試行**（`gh auth` 系 2 件・`python3 -c` 1 件）だった。詳細は下記「追補 3（2026-08-03）」
- [x] キットへの環流を `feedback/` に記録した（planning 側への起票は下記「未決事項」）

## テスト方針

- 機械検査: `scripts/check-ai-workflow-config.js`（記法・SDK 整合・ドリフト・3 系統乖離）と
  `--self-test`、`scripts/scripts.test.js` / `check-doc-links.js` / `check-commit-messages.js`。
- 対称性: 両ファイルの `--allowedTools` を行分解して差分を取り、宣言済みの意図的な差だけが
  残ることを確認する（下記「検証結果」）。
- 陰性確認: 追加した `dotnet` エントリが書き込み系を通さないことは、指定が**引数固定形**であり
  `Bash(dotnet:*)` を入れていないという事実で保証される。
- 実地検証: 本 PR の `claude-review` の実行サマリで拒否件数を確認する（緑ではなく**0 件**が基準）。

## 検証結果（2026-08-03・ローカル）

| 検査 | 結果 |
| --- | --- |
| `node scripts/check-ai-workflow-config.js` | ✓ 成功（2 件を検査。ERROR 0。warn は下記「未決事項」の 3 系統乖離のみ） |
| `node scripts/check-ai-workflow-config.js --self-test` | ✓ 23 件すべて合格 |
| `node scripts/scripts.test.js` | ✓ 全件合格 |
| `node scripts/check-doc-links.js` | ✓ 成功 |
| `node scripts/check-commit-messages.js` | ✓ 成功 |
| `python3 -c "import yaml; yaml.safe_load(...)"` × 2 ファイル | ✓ 両方パース可能 |
| `--allowedTools` の対称性 diff | ✓ 追加 5 件は両ファイルに存在。残る差は宣言済みの意図的な差のみ |

`--allowedTools` の残差（意図的・従来どおり）:

- 実装用にのみ: `Edit` / `Write` / `Bash(git add:*)` / `Bash(git commit:*)` / `Bash(git push:*)` /
  `Bash(git switch:*)` / `Bash(git checkout:*)` / `Bash(git branch:*)` / `Bash(find:*)` / `Bash(mkdir:*)`
- レビュー用にのみ: `Bash(gh issue view:*)` / `Bash(gh pr view:*)` / `Bash(gh run list:*)`

## 実走結果と追補（2026-08-03）: 環境変数の前置き形で拒否 1 件

PR #475（上記の変更）をマージした**直後の初回実走**（PR #479 の `claude-review` /
run `30829121373`）で `permission_denials_count: 1` を実測した。

| 拒否 | 件数 | 原因 |
| --- | --- | --- |
| `Bash(REQUIRE_REPO_TESTS=1 node \| tail)` | 1 | 環境変数の前置き形。先頭トークンが `REQUIRE_REPO_TESTS=1` になり、どの許可エントリにも前方一致しない |

- 追加した 5 エントリ（`which` / `dotnet --version` / `dotnet --info` / MCP 2 件）由来の拒否は
  **0 件**であり、「設計 1」の効果は実走で確認できた。
- 残った 1 件は「設計 3」と**同型**（許可リストで原理的に表現できず、プロンプトで手順を狭めるしかない型）である。
  `Bash(env:*)` を許すと任意コマンドが通るため、許可の追加では塞げない。
- 当のレビュー自身が回避形を**自力で発見して使っていた**
  （`node -e "process.env.REQUIRE_REPO_TESTS='1'; require('./scripts/scripts.test.js');"`）。
  代替手順は存在するのにプロンプトが書いていないため、AI は拒否を 1 件消費してから発見し直す。

### 追補の変更内容

| ファイル | 変更 |
| --- | --- |
| `.github/workflows/claude-code-review.yml` | `prompt:` の「原理的に実行できない」節の環境変数の項へ、代替形（`node -e` で `process.env` を設定してから `require` する `node` 1 コマンド形）と実測（PR #479）を追記。`--allowedTools` は不変 |
| `.github/workflows/claude-coding.yml` | `--append-system-prompt` へ同趣旨を追記（対称）。`--allowedTools` は不変 |
| `feedback/20260803_ai-review-execution-permissions.md` | 「追記」節を追加（planning#168 への追加事例。「許可リストで表現できないシェル構文の型のリスト」に環境変数の前置き形を加える提案） |

`--allowedTools` を変更していないため、3 系統（両ワークフロー ＋ `.claude/settings.json`）の
パリティは追補の前後で不変である。

### 追補の検証結果（ローカル）

| 検査 | 結果 |
| --- | --- |
| `node scripts/check-ai-workflow-config.js` | ✓ 成功（ERROR 0） |
| `node scripts/scripts.test.js` | ✓ 全件合格 |
| `node scripts/check-doc-links.js` | ✓ 成功 |
| `node scripts/check-commit-messages.js` | ✓ 成功 |
| 両 workflow の YAML パース | ✓ 両方パース可能 |

## 追補 2（2026-08-03）: 実走で拒否 7 件 — 許可の粒度と回避形の落とし穴

上記「追補」を載せた PR [#480](https://github.com/endazon/microservices-platform/pull/480) の
`claude-review` 実走（run `30830151995` / HEAD `734da26`）で **`permission_denials_count: 7`**
（許容値 4 超過）となり、ジョブは fail した。`scripts/check-permission-denials.js` の出力による内訳:

| 拒否 | 件数 | 型 |
| --- | --- | --- |
| `Bash(git show \| grep \| diff)` | 2 | 3 段パイプ連鎖 |
| `Bash(git show \| grep \| echo)` | 1 | 3 段パイプ連鎖 |
| `Bash(gh run)` | 1 | 前方一致の粒度（許可は `gh run list` のみ） |
| `Bash(PR_TITLE="fix \| IADR-0115)` | 1 | 単独の変数代入（`PR_TITLE="fix(IADR-0115): …"` を含むコマンド） |
| `mcp__github__get_workflow_run` | 1 | MCP はツール名単位の許可 |
| `mcp__github__list_workflow_jobs` | 1 | MCP はツール名単位の許可 |

チェッカーは同時に「リダイレクト（`>`）を含むコマンドがある」とも出力した。3 段パイプの各コマンド
（`git show` / `grep` / `diff` / `echo`）はいずれも `--allowedTools` に個別に存在するため、
**3 件の拒否の根本原因は実測出力からは特定できていない**（長い連鎖を判定が扱えないのか、鎖の
どこかにリダイレクトが混ざったのかを切り分けられない）。**原因は断定せず**、運用側で
「3 段以上の連鎖を組ませない」形に狭める。

受け入れ基準「拒否 0 件」は**未達のまま**であり、本仕様書の `status` は `in-progress` を維持する。

### 同レビューの 🟡 指摘と本追補の対応

- 🟡 指摘: 「追補」でプロンプトに書いた回避形 `node -e "process.env.X='1'; require('./…');"` は、
  `require.main === module` ガードを持つスクリプト（`scripts/check-ai-workflow-config.js`）では
  `main` が呼ばれず**無出力のまま exit 0** になる（＝検査していないのに成功に見える）。
  → 本追補で、**子プロセス（`spawnSync`）形を標準**とし `require` 形はガードの無いスクリプト
  （例: `scripts/scripts.test.js`）限定である旨と、**使う前に対象スクリプト末尾のガード有無を
  自分で確認する**手順を両プロンプトへ明記した。ローカル実測で両形の挙動を確認済み
  （下記「検証結果」）。なおガードの有無はスクリプトごとに異なり、`scripts/` 内でガードを
  持たないものは `scripts.test.js` だけではない（実測: `gen-openapi-skeleton.js` /
  `k8s-local-up.test.js` / `scripts.repo.test.js` / `validate-pipeline-config.js` も該当）。
  **「`scripts.test.js` のみ」と断定しない**こと。

### 追補 2 の変更内容

| ファイル | 変更 |
| --- | --- |
| `.github/workflows/claude-code-review.yml` | `prompt:` の「検証の誠実性」節へ、(a) `node -e` の 2 形（`require` 形の適用条件と子プロセス形の標準形・無出力なら実測と書かない判定則）、(b) 単独の変数代入・`gh run list` 以外の `gh run`・許可外の Actions 系 MCP ツール・3 段以上のパイプ連鎖の各制約、(c) 拒否件数は `Check permission denials` ステップが権威である旨を追記。`--allowedTools` は不変 |
| `.github/workflows/claude-coding.yml` | `--append-system-prompt` へ同趣旨を追記。ただし `gh` の記述は**同ファイルの `--allowedTools` に合わせる**（下記「クロス監査による是正」）。`--allowedTools` は不変 |
| `feedback/20260803_ai-review-execution-permissions.md` | 「追記 2」節を追加（型リストへ「単独の変数代入」「3 段以上のパイプ連鎖」を追加、環境変数の代替形を子プロセス形へ改める提案、許可の粒度の明示。環流先 planning#168 は既存のまま） |
| `docs/specs/20260803_issue-469_ai-review-execution-permissions.md` | 本節 |

`--allowedTools` / `.claude/settings.json` / `PERMISSION_DENIALS_TOLERANCE` はいずれも変更していない
（issue #469 の方針「許容値を上げず、プロンプト側で作業手順を狭める」に従う）。よって 3 系統の
パリティは追補 2 の前後で不変である。

### クロス監査による是正（2026-08-03）

追補 2 のクロス監査で 2 件の指摘を受け、是正した。

1. 🔴 **`claude-coding.yml` の `gh` の記述が同ファイルの `--allowedTools` と不一致だった。**
   レビュー用の文面（「`gh run` で許可されているのは `gh run list` のみ」）をそのまま写したが、
   実装用の `--allowedTools` に `Bash(gh run list:*)` は**無い**（実装用の `gh` は
   `Bash(gh issue create:*)` のみ。`Bash(gh run list:*)` を持つのはレビュー用だけである）。
   指示に従った実装エージェントが `gh run list` を実行すると**新たな拒否を 1 件生む**。
   → 実装用の記述を「`gh run` 系は一切許可されていない。CI 結果の参照は
   `mcp__github__list_workflow_runs` / `mcp__github__actions_list` のみで行う」へ修正した。
   `--allowedTools` へは追加していない（不変方針を維持）。
   **教訓**: 両ファイルのプロンプトは*趣旨*を対称にするが、**「何が許可されているか」の記述は
   機械的に対称化せず、各ファイルの実際の `--allowedTools` に一致させる**こと。
2. 🟡 **「`require.main` ガードが無いのは `scripts/scripts.test.js` のみ」という断定が不正確だった。**
   実測では `scripts/` にガード無しが少なくとも 5 本ある（`scripts.test.js` /
   `gen-openapi-skeleton.js` / `k8s-local-up.test.js` / `scripts.repo.test.js` /
   `validate-pipeline-config.js`）。この断定は 4 ファイルへ展開されており、planning#168 へ
   環流するとキットに誤情報が転写される。
   → 「のみ」の断定をやめ、「例: `scripts/scripts.test.js`。使う前に対象スクリプト末尾の
   `require.main` ガードの有無を自分で確認すること」の形へ弱めた（両ワークフロー・本仕様書・
   `feedback/` の 4 箇所）。`feedback/` には「キットは固定のファイル名リストを書かず、
   確認手順として書くこと」を環流内容として明記した。

### 追補 2 の検証結果（ローカル・worktree 内で実測）

| 検査 | 結果 |
| --- | --- |
| `node scripts/check-ai-workflow-config.js` | ✓ 成功（2 件を検査 / ERROR 0 / exit 0） |
| 子プロセス形（`spawnSync` ＋ `STRICT_AI_WORKFLOW_CONFIG=1`） | ✓ 「2 件を検査 / ✓ 問題なし」を出力し exit 0（検査が走る） |
| `require` 形（`node -e "process.env.STRICT_AI_WORKFLOW_CONFIG='1'; require('./scripts/check-ai-workflow-config.js');"`） | ⚠ **出力なしで exit 0**（`require.main` ガードにより `main` が呼ばれない＝🟡 指摘を再現） |
| `node scripts/scripts.test.js` | ✓ 全件合格 |
| `node scripts/check-doc-links.js` | ✓ 成功 |
| `node scripts/check-commit-messages.js` | ✓ 成功 |
| 両 workflow の YAML パース | ✓ 両方パース可能 |
| `--append-system-prompt` の引用符 | ✓ 値を囲む二重引用符は 1 対のみ（追記でトークン化を壊していない） |

## 追補 3（2026-08-03）: 拒否 3 件へ改善 — 負の列挙から正の列挙へ

追補 2 とクロス監査の是正を載せた PR #480 の**再実走**（run `30832367628`）で
**`permission_denials_count: 3`** となり、7 件から改善して**ジョブは success**（許容値 4 以下）した。
ただし受け入れ基準は「拒否 0 件」であり、**未達のまま**である（`status` は `in-progress` を維持）。

| 拒否 | 件数 | 型 |
| --- | --- | --- |
| `Bash(gh \| head \| gh auth)` | 1 | 一覧に無いコマンド（`gh auth`） |
| `Bash(gh auth)` | 1 | 一覧に無いコマンド（`gh auth`） |
| `Bash(python3 \| yaml.safe_load \| open \| '.github/workflows/claude-code-review.yml' \| …)` | 1 | 一覧に無いコマンド（`python3 -c` での YAML パース試行） |

**追補 2 で塞いだ型（3 段パイプ・単独の変数代入・許可外の Actions 系 MCP・`gh run view`）由来の
拒否は 0 件**であり、型ごとの制約という方針自体は効いた。残った 3 件は質が違い、構文でも粒度でもなく
**`--allowedTools` に存在しないコマンドを試した**ものである。

### 追補 3 の設計: 負の列挙から正の列挙へ

これまでの追記はすべて「拒否される形」の**負の列挙**だった。負の列挙は**書いた型しか塞げない**。
AI が知らないのは「何が拒否されるか」ではなく「**何なら使えるか**」であり、未知の作業では
手持ちの一般常識（`python3` / `gh auth` / `curl` / `jq`）へ手が伸びる。そこで両プロンプトの
先頭近くへ **「使える Bash コマンドの正の一覧」** を置いた。

- 一覧は**各ファイル自身の `--allowedTools` から書き起こした**（クロス監査の 🔴 と同じ誤りを
  繰り返さないため。レビュー用と実装用で中身は異なる）。
  - レビュー用にのみ: `gh issue view` / `gh pr view` / `gh run list`
  - 実装用にのみ: 書き込み系 git（`add` / `commit` / `push` / `switch` / `checkout` / `branch`）・
    `find` / `mkdir`・`Edit` / `Write`。実装用の `gh` は `gh issue create` **だけ**である。
- **要約表現は実際の許可より広く読めてはならない**という制約を守った。「`git` 系」ではなく
  「`git`（読み取りのみ）」と書き、レビュー用には**書き込み系 git が無い**ことを明記した。
- 一覧に無い代表例（`python3` / `gh auth` / `curl` / `wget` / `pip` / `jq` / `sed` / `awk` /
  `xargs` / `env` / `chmod` / `rm` / `mv` / `cp` / `touch` / `tee`）と**代替手順**を併記した。
  - YAML / JSON の構文確認は **Read で読む**。機械的な確認が要るなら許可済みの `node` で行う。
  - `gh auth status` は**不要**である（認証は環境が構成済み。GitHub の情報は GitHub MCP ツールで取る）。
- 出典として run `30832367628` の拒否 3 件の内訳をプロンプト本文に明記した。

### 追補 3 で判明した落とし穴: `claude_args` の中に `--allowedTools` と書けない

実装用の正の一覧を書く際、当初「これは本ファイルの `--allowedTools` の写しである」という
一文を `--append-system-prompt` の値に入れたところ、`check-ai-workflow-config.js` が
**ERROR 1 件で fail した**（実測）。同スクリプトは `claude_args` ブロック内から
`--allowedTools` トークンを探して以降を値とみなすため、**プロンプト本文に書いた
フラグ名を本物のフラグとして拾い**、「値が引用符で囲まれておらず空白を含む」と誤検出する。

- 対処: 実装用の文面を「この `claude_args` が指定している許可ツール一覧の写し」と言い換えた
  （フラグ名そのものを書かない）。
- レビュー用は `prompt:` が `claude_args` とは別のキーであり、同スクリプトの走査対象外なので
  `--allowedTools` と書いても検出されない（実際に成功する）。**この非対称は覚えておく必要がある。**
- 検査器が先に気付いた形であり、`check-ai-workflow-config.js` が機能した好例でもある。

### 追補 3 の変更内容

| ファイル | 変更 |
| --- | --- |
| `.github/workflows/claude-code-review.yml` | `prompt:` へ【使える Bash コマンドの一覧（正の一覧）・最重要】節を新設（`--allowedTools` の写し・`find` と書き込み系 git が無い旨・`gh` は 4 形のみ・一覧外の代表例と代替手順・run 30832367628 の実測）。`--allowedTools` は不変 |
| `.github/workflows/claude-coding.yml` | `--append-system-prompt` へ同趣旨を追記。ただし一覧は**同ファイルの `--allowedTools` から書き起こし**、実装用にのみ在るもの（書き込み系 git / `find` / `mkdir` / `Edit` / `Write`）と、実装用の `gh` が `gh issue create` だけである点を反映。`--allowedTools` は不変 |
| `feedback/20260803_ai-review-execution-permissions.md` | 「追記 3」節を追加（run 30832367628 の実測、「負の列挙では新型を塞げない」という知見、planning#168 へ追記する内容） |
| `docs/specs/20260803_issue-469_ai-review-execution-permissions.md` | 本節 |

`--allowedTools` / `.claude/settings.json` / `PERMISSION_DENIALS_TOLERANCE` はいずれも変更していない。
よって 3 系統のパリティは追補 3 の前後で不変である。

あわせて（クロス監査の 🟡 指摘）、両ファイルの `【暫定デルタ・issue #469 / 環流先 planning#168】`
注記ブロックへ、**追補 3 で足した「正の一覧」節も撤去対象のデルタである**旨を run `30832367628`
の参照込みで追記した。[IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md) の運用では、
planning#168 の反映後にこの注記を目印としてデルタを撤去しキットとバイト一致へ戻すため、
**プロンプト側の追補も注記に列挙されていなければならない**（従来は `--allowedTools` への
追加エントリだけが列挙されており、追補 2・3 のプロンプト追記が漏れていた）。
「`--allowedTools` は変更していない」の記述も、run `30830151995` / run `30832367628` の
両追補を指すよう更新した。実装用の注記には、本文へ `--allowedTools` という文字列を書けない
制約（上記の落とし穴）も併記した。

### 追補 3 の検証結果（ローカル・worktree 内で実測）

| 検査 | 結果 |
| --- | --- |
| `node scripts/check-ai-workflow-config.js` | ✓ 成功（2 件を検査 / ERROR 0 / exit 0） |
| `node scripts/check-ai-workflow-config.js --self-test` | ✓ 23 件すべて合格 |
| 正の一覧と `--allowedTools` の突き合わせ（`node` で機械的に照合） | ✓ 両ファイルとも、`--allowedTools` の Bash エントリで**プロンプトに現れないものは 0 件** |
| 両 workflow の構造検査（`check-ai-workflow-config.js` が 2 ファイルとも `claude_args` を抽出できること。`python3` は使わない） | ✓ 「2 件を検査」＝両ファイルとも構造を読み取れている |
| YAML パーサの在否（`node -e` で `require('js-yaml')`） | **MODULE_NOT_FOUND**。ワークスペースに YAML パーサは無い（プロンプトへ書いた「YAML パーサは用意されていない前提で考える」の裏付け） |
| `--append-system-prompt` の引用符 | ✓ 値を囲む二重引用符は 1 対のみ |
| `node scripts/check-doc-links.js` | ✓ 成功 |
| `node scripts/check-commit-messages.js` | ✓ 成功 |

## 計画書との差異

- 差異: あり（キット側の不足）。`--allowedTools` とプロンプトは [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)
  でキットを単一情報源とした**分類 B** のファイルである。「実走を求めるプロンプト」と
  「実走の周辺操作を許可しない `--allowedTools`」の非対称は**キット由来**であり、
  キットを使う他の実装リポジトリでも同じ拒否が出る。本 PR の変更は planning#140 / planning#163 と同じ
  **暫定デルタ**（コメントで環流先を参照し、キット反映後の同期で撤去してバイト一致へ戻す）として扱い、
  `feedback/20260803_ai-review-execution-permissions.md` に記録した。

## 未決事項

- ~~**`.claude/settings.json`（3 系統目）への同内容の追加はオーナーが適用する必要がある。**~~
  → **適用済み**（2026-08-03 時点の `develop` で 5 行が存在し、
  `STRICT_AI_WORKFLOW_CONFIG=1 node scripts/check-ai-workflow-config.js` が成功することを実測）。
  以下は経緯として残す。
  同ファイルは AI による編集が deny されている（`permissions.deny` の
  `Edit(./.claude/settings.json)` / `Write(...)` と `hooks/guard-bash.js`。AI が自分の許可リストを
  広げられないようにする設計）。#460（PR #461）でも同じ手順でオーナーが適用した。
  適用しないと `ci.yml` の `ai-workflow-config` ジョブ（`STRICT_AI_WORKFLOW_CONFIG=1`）が
  3 系統乖離の警告で失敗する（ローカルで `STRICT_AI_WORKFLOW_CONFIG=1` 実行時に exit 1 を実測）。
  `permissions.allow` へ次の 5 行を追加する:

  ```json
  "Bash(which:*)",
  "Bash(dotnet --version)",
  "Bash(dotnet --info)",
  "mcp__github__list_workflow_runs",
  "mcp__github__actions_list",
  ```

- planning リポジトリ（`impl-handoff-kit`）への起票は
  `feedback/20260803_ai-review-execution-permissions.md` の内容で
  [planning#168](https://github.com/endazon/project-planning/issues/168)
  として**起票済み**である（IADR-0115 の「記録 1 件 ↔ 環流 1 件」規約に従い、planning#163 への
  追記ではなく新規 issue とした）。反映されたら本リポジトリの暫定デルタを撤去し、
  キットとバイト一致へ戻す。
