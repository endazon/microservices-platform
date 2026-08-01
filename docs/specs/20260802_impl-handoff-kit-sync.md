---
title: planning submodule 最新化と impl-handoff-kit の同期（権限拒否の可視化）
type: spec
status: done
related_ids: [NFR, IADR-0115]
author: Claude
created: 2026-08-02
updated: 2026-08-02
plan_refs: []
---

# 仕様書: planning submodule 最新化と impl-handoff-kit の同期（権限拒否の可視化）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性・運用性。開発基盤の整備）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR: [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)
  （impl-handoff-kit を正とする同期規約。本作業はその規約の適用であり、新規の実装判断は生じない）
- 計画書リンク: `planning/tools/impl-handoff-kit/`（`HOWTO.md` / `repo-template/`）
- 上流の起点: planning#145 / planning#146 / planning#148 / planning#149
  （AI ワークフローが「緑のまま実質未実施」「成果物は正しいのに赤」になる 3 系統の欠陥と、その検出器）

## 目的・背景

前回の全面同期（[20260801_impl-handoff-kit-sync.md](20260801_impl-handoff-kit-sync.md) / PR #433）以降、
計画リポジトリに 3 コミット（`9cd3499` → `0847687` → `26402cb` → `3b0deb2`）が積まれ、キットに
**AI ワークフローの失敗を可視化・予防する 2 つの検査器**と、それに伴うワークフローの是正が入った。

取り込む是正は次の 4 点である。いずれもジョブの成否が実態と食い違う欠陥である。

1. **緑のまま実質未実施（planning#145）**: `claude-code-action` は、AI がツールを 1 つも実行できなくても
   `"subtype": "success", "is_error": false` で終了する。実測ではレビューが 21 ターン中 17 件の権限拒否で
   潰れ、本文を 1 文字も書けないまま **CI は緑**・PR には「並列精査中」という進行中コメントだけが残った。
   CI には承認する人間が居ないため、権限拒否は「待たされた」ではなく**「その作業は永久に実行されない」**を
   意味する。既存の `check-ai-workflow-config.js` は *設定の書き方の誤り* しか見つけられず、
   「設定は正しいが AI が要求したツールが揃っていなかった」型は実行するまで判らない。
2. **成果物は正しいのに赤（planning#146）**: アクションの組み込みプロンプト自身が `git diff origin/main...HEAD` /
   `git log origin/main..HEAD` / `git status` を差分取得の手段として指示するため、読み取り系 git を許可しない限り
   **差分の内容と無関係に毎回拒否が出る**。1 の検査器を入れると、この欠落がそのまま全 PR の CI 赤に変わる
   （planning 側で実際に発生）。本リポジトリのレビュー用は `Bash(git status:*)` を欠いていた。
3. **サブエージェント禁止の置き場所（planning#149）**: 禁止指示はレビュー用が `prompt:` 入力に持つ一方、
   実装用は `@claude` メンション本文で駆動し `prompt:` を持たないため `--append-system-prompt` に置くしかない。
   欠けると、実装を完遂してコミット・PR まで出せていても `Task` の拒否 1 件でジョブが赤くなる。
4. **同期のたびに Actions が巻き戻る（planning#148）**: Dependabot は github-actions エコシステムでは
   **リポジトリ直下の `.github/workflows/` しか走査しない**ため、キットのテンプレート配下は自動追随しない
   （`dependabot.yml` に `directory:` を足しても no-op で、失敗せず単に走らないため対処済みに見える）。
   前回同期のフィードバック 2 番目に挙げた問題であり、キット側が検査器で塞いだ。

## 対象範囲

- 対象: `planning` submodule の pin 更新（`9cd3499` → `3b0deb2`）と、`repo-template` 配下の差分の反映。
- 対象外: `src/` 配下のアプリケーション実装、`deploy/`、`src/ai-stock-trading` submodule の pin、
  `CHANGELOG.md`（`changelog.yml` の生成物）。

## 設計

IADR-0115 の 3 分類（A: キット完全一致 / B: キット＋固有デルタ / C: 本リポの中身）で機械的に扱う。
`repo-template` の全 102 ファイルを本リポジトリと突合した結果、**キット側が進んでいるのは次の 9 ファイル
のみ**であった。他の差分（`ci.yml` / `codeql.yml` / `frontend*.yml` / `security.yml` / `openapi.yml` /
`doc-links-planning.yml` / `CLAUDE.md` / `AI_SETUP.md` / `.claude/rules/traceability.md` /
`docs/README.md` / `docs/ai-workflow.md` / `scripts/README.md` / `scripts/changelog-overrides.json` /
`scripts/check-commit-messages.js` / `.gitignore` / `.gitmodules` / `docs/adr/README.md` /
`docs/operations|security|tech`）は、いずれも IADR-0115 が許容する固有デルタ（分類 B/C）であり変更しない。

### A: キットで新規追加・上書き

| ファイル | 内容 |
| --- | --- |
| `scripts/check-permission-denials.js` | 新規。実行ログ（`outputs.execution_file`）を読み、権限拒否されたツールを **`Bash(git diff)` のようにコマンド名まで**報告し **exit 1**（許可リストの粒度がコマンド単位のため、ツール名だけでは何を足せばよいか決められない。引数は出さない）。ログを読めない構成では `warn` を出して exit 0（fail-open）。`--self-test` を持つ |
| `scripts/check-action-versions.js` | 新規。ワークフローの `uses: <action>@vN` を集め、`action-versions.json` の下限または `--compare-with` 先より古ければ **exit 1**。表に無いアクション・未使用エントリは `warn`。`--check-latest` は GitHub API 参照で warn のみ（fail-open）。`--self-test` を持つ |
| `scripts/action-versions.json` | 新規。上記の下限表（単一情報源）。`github/codeql-action` はタグ形式上メジャーを引けないため `$exempt` |
| `scripts/check-ai-workflow-config.js` | 実装用の `--append-system-prompt`（サブエージェント禁止）欠落の検査を追加 |
| `scripts/scripts.test.js` | 上記 2 検査器のテストブロックを追加（+11 ケース。125 → 136） |

### B: キット＋固有デルタ（キットの追加分のみ取り込む）

| ファイル | 取り込む差分 |
| --- | --- |
| `.github/workflows/claude-coding.yml` | `permissions:` に `actions: read`／`Run Claude Code` に `id: claude`／`claude_args` に `--append-system-prompt`（サブエージェント禁止）／末尾に `Check permission denials`（`if: always()`）ステップ |
| `.github/workflows/claude-code-review.yml` | 同上（`id: claude`・`actions: read`・拒否検査ステップ）に加え、`--allowedTools` へ **`Bash(git status:*)`** を追加 |
| `.github/workflows/ci.yml` | コメント例の `actions/setup-python@v5` → `@v7`（キット本文。実体は無効化されたコメントで挙動に影響しない） |
| `scripts/README.md` | `check-action-versions.js` の一覧行・実行例、`check-permission-denials.js` の説明更新（本リポ固有の行はすべて保持） |

`actions: read` はツール許可の前提でもある。`claude-code-action` は `mcp__github_ci__*` サーバーを注入
する前にトークンが `actions: read` を持つか実検証し、無ければ `Skipping CI server installation` と警告して
導入を取り止めるため、**許可済みのはずのツールが存在しない**状態になる。`--allowedTools` の
`Bash(gh run list:*)` も同権限を要求する。ツール許可に `additional_permissions` は使わない
（あれはアプリトークンのスコープ用）。

### C: 変更しない

上記以外の全ファイル。判断が要ったものを挙げる。

- **`ci.yml` に `check-permission-denials` / `check-action-versions` のジョブを足さない**。前者は
  実行ログを持つ AI ワークフローにしか検査対象が無い（両ワークフローに同梱済み）。後者はキット自身が
  `repo-template/.github/workflows/ci.example.yml` に載せておらず、配布元の CI で
  テンプレートを検査する設計である（planning#148）。**本リポジトリ直下の `.github/workflows/` は
  Dependabot の管理下**にあり、退行の発生源はキット側にしか無い。なお `scripts-tests` ジョブが
  `scripts.test.js` 経由で両検査器の `--self-test` を実行するため、検査器自体の回帰は CI で止まる。
- `.github/workflows/frontend-tests.yml` の `actions/upload-artifact` は既に `@v7` で、キットが今回
  引き上げた水準（v4 → v7）を満たす。本リポジトリの全 Actions が `action-versions.json` の下限以上で
  あることを `check-action-versions.js` の実行で確認した。

## 受け入れ基準

1. `git submodule status planning` が `3b0deb2` を指す。
2. `repo-template` と本リポジトリの突合で、**キット側が進んでいるファイルが 0 件**になる
   （残差分はすべて分類 B/C の固有デルタであること）。
3. `node scripts/check-permission-denials.js --self-test` が成功する。
4. `node scripts/scripts.test.js` が全件成功する（新規 11 ケースを含む 136 件）。
5. `node scripts/check-ai-workflow-config.js` が成功する（`claude_args` 記法・ツール許可のドリフト・
   実装用の `--append-system-prompt` 欠落が無い）。
6. `node scripts/check-action-versions.js`（および `--self-test`）が成功する。
8. `node scripts/check-doc-links.js` が破損リンク 0 で成功する。
9. 両 AI ワークフローが `actions: read` を持ち、`Check permission denials` ステップを `if: always()` で
   実行する（`actionlint` 相当の構文検査として `check-ai-workflow-config.js` の通過をもって代える）。

## 影響範囲・リスク

- **AI ワークフローが赤くなり得る**: これまで緑で潰れていた実行が、拒否を検出した時点で fail する。
  これは意図した挙動（未実施のレビューを緑と誤認しない）である。緊急避難が要る場合は
  `ALLOW_PERMISSION_DENIALS=1` で警告のみに落とせる。
  なお、拒否を生む既知の 2 原因（読み取り系 git の欠落・サブエージェント禁止指示の欠落）は
  本作業で同時に塞いでいるため、検査器の導入だけで CI が赤くなる状態にはしていない。
- `.github/workflows/` は GitHub App 権限では編集不可のため、ローカル（`workflow` スコープ）から
  コミット/プッシュする。
