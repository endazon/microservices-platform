---
title: 実走に伴う周辺操作の権限拒否を塞ぐ（which / dotnet --version / list_workflow_runs）とプロンプトによる rm・シェル構文の抑止
type: spec
status: done
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
「拒否の赤を無視する学習」（IADR-0115 が planning#162 を引いて警告した常態化）が定着する。

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
- [ ] 本 PR 自身の `claude-review` が**権限拒否 0 件**で green になる
      （間欠発現のため「緑になった」だけでは不十分。実行サマリで 0 件を確認する）
      → PR 作成後に確認する
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

## 計画書との差異

- 差異: あり（キット側の不足）。`--allowedTools` とプロンプトは [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)
  でキットを単一情報源とした**分類 B** のファイルである。「実走を求めるプロンプト」と
  「実走の周辺操作を許可しない `--allowedTools`」の非対称は**キット由来**であり、
  キットを使う他の実装リポジトリでも同じ拒否が出る。本 PR の変更は planning#140 / #163 と同じ
  **暫定デルタ**（コメントで環流先を参照し、キット反映後の同期で撤去してバイト一致へ戻す）として扱い、
  `feedback/20260803_ai-review-execution-permissions.md` に記録した。

## 未決事項

- **`.claude/settings.json`（3 系統目）への同内容の追加はオーナーが適用する必要がある。**
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

- planning リポジトリ（`impl-handoff-kit`）への起票は `feedback/20260803_ai-review-execution-permissions.md`
  の内容で行う（PR 作成と同じタイミング。planning#163 の続きとして追記する案もある）。
