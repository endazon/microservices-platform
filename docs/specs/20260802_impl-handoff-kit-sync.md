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
- 上流の起点: planning#145 / #146 / #148 / #149 / #152 / #153 / #155 / #157 / #158 / #160 / #161
  （AI ワークフローが「緑のまま実質未実施」「成果物は正しいのに赤」になる欠陥と、
  キット同期そのものが Actions のバージョンを巻き戻す欠陥、レビューが未実施の検証を
  「実測」と偽る欠陥、およびそれらの検出器）

## 目的・背景

前回の全面同期（[20260801_impl-handoff-kit-sync.md](20260801_impl-handoff-kit-sync.md) / PR #433）以降、
計画リポジトリに 9 コミット（`9cd3499` → … → `3bdc8f8`）が積まれ、キットに
**AI ワークフローの失敗を可視化・予防する 2 つの検査器**と、それに伴うワークフローの是正が入った。

取り込む是正は次の 8 点である。いずれもジョブの成否・報告が実態と食い違う欠陥である。

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
5. **同期そのものが Actions を巻き戻す（planning#152 / planning#153）**: キットの下限表は
   「これ以上古くしない」線であって常に最新とは限らない。**本リポジトリが Dependabot で下限より先へ
   進んだあとにキットのファイルをコピーすると、本リポジトリにとっては退行なのに下限検査では合格する**。
   実測でキットが `upload-artifact@v4` のとき本リポジトリは既に `@v7` であり、素直に上書きしていれば
   3 メジャー分の退行を持ち込んでいた（本作業で回避できたのは手作業の走査によるものだった）。
   キットは検査を **実装リポの `ci.yml` 側へ**置き、統合ブランチ時点との比較で捉える方式に改めた。
6. **未実施の検証を「実測」と偽る（planning#155 / planning#157）**: 実運用で、レビューが
   **自分が実行できなかった検証を ✅ で報告した**事例がある（権限拒否 12 件が起きていたが本文に
   言及が 1 行も無かった）。読み手にはこれを見分ける手段が無い。原因は 2 系統ある。
   - **報告の置き場所**: 拒否の内訳がジョブログにしか無く、AI 本文の「✅ 実測」と突き合わせられない。
   - **残っていた拒否そのもの**: パイプは**各コマンドが個別に判定される**ため
     `node scripts/x.js | tail -5` は先頭が許可済みでも `tail` で拒否される（報告に出るのは先頭
     コマンド名なので原因が判りにくい）。レビュー用にだけ `cat` / `head` / `tail` が無く、
     `git -C planning ls-tree` は許可済みなのに直下の `git ls-tree` / `git submodule status` が
     無かった（キット同期 PR のレビューでは pin の確認に毎回要る）。
7. **拒否報告が原因を隠す（planning#158 / planning#160）**: 6 の対処を入れた PR #437 のレビューで
   拒否が 7 件出た。うち `Bash(git diff)` / `Bash(git show)` は**許可済みなのに「拒否された」と
   報告されており**、読み手を誤った対処へ導く形になっていた。原因は `labelOf()` が
   複合コマンドを**先頭セグメントだけで**ラベル付けし（拒否の実体が後段の `diff` でも
   `Bash(git show)` と出る）、かつ先頭 2 トークン固定の切り詰めが `git -C <dir> <sub>` 形で破綻して
   `Bash(git -C)` になり**対処に必要なサブコマンドが消える**ことであった。
   本リポジトリから planning#160 として起票し、planning#159 で提案どおり是正された
   （[feedback/20260802_review-allowlist-diff-and-denial-labeling.md](../../feedback/20260802_review-allowlist-diff-and-denial-labeling.md)）。
8. **ラベルが読めても拒否は残る（planning#161）**: 7 の是正を入れた PR #437 のレビューで拒否が
   4 件出た。**新しいラベルが原因をそのまま見せた**ため、内訳は次と判った。
   - `Bash(A=$(git | B=$(git show | if [ | echo BYTE_IDENTICAL | …)` — 変数代入と `if` を含む
     長い連鎖のワンライナー。`echo` も未許可だった。
   - `Bash(git -C planning show | diff | 1 | true)` — `2>&1` の fd 複製が `&` で分割され、
     `1` という実在しないセグメントがラベルを汚していた。
   キット側は `echo` を許可し、fd 複製と引用符付き引数がラベルを汚す不具合を直したうえで、
   プロンプトに「長い連鎖のワンライナーを作らない（鎖のどこかに未許可コマンドが混ざると
   **鎖全体が実行されず前段の結果も得られない**）」「`$?` はパイプの後では直前 1 コマンドの
   結果しか表さないため判定に使えない」を追記した。

## 対象範囲

- 対象: `planning` submodule の pin 更新（`9cd3499` → `3bdc8f8`）と、`repo-template` 配下の差分の反映。
- 対象外: `src/` 配下のアプリケーション実装、`deploy/`、`src/ai-stock-trading` submodule の pin、
  `CHANGELOG.md`（`changelog.yml` の生成物）。

## 設計

IADR-0115 の 3 分類（A: キット完全一致 / B: キット＋固有デルタ / C: 本リポの中身）で機械的に扱う。
`repo-template` の全 104 ファイルを本リポジトリと突合した結果、**キット側が進んでいるのは次の 11 ファイル
のみ**であった。他の差分（`ci.yml` / `codeql.yml` / `frontend*.yml` / `security.yml` / `openapi.yml` /
`doc-links-planning.yml` / `CLAUDE.md` / `AI_SETUP.md` / `.claude/rules/traceability.md` /
`docs/README.md` / `docs/ai-workflow.md` / `scripts/README.md` / `scripts/changelog-overrides.json` /
`scripts/check-commit-messages.js` / `.gitignore` / `.gitmodules` / `docs/adr/README.md` /
`docs/operations|security|tech`）は、いずれも IADR-0115 が許容する固有デルタ（分類 B/C）であり変更しない。

### A: キットで新規追加・上書き

| ファイル | 内容 |
| --- | --- |
| `scripts/check-permission-denials.js` | 新規。実行ログ（`outputs.execution_file`）を読み、権限拒否されたツールを **コマンド名まで**報告し **exit 1**。複合コマンドは全セグメントを `Bash(git show | diff)` の形で出し、`git -C <dir> <sub>` は 4 トークンまで採る（planning#160）（許可リストの粒度がコマンド単位のため、ツール名だけでは何を足せばよいか決められない。引数は出さない）。内訳は `$GITHUB_STEP_SUMMARY`（PR の Checks 画面から 1 クリック）にも書く。ログを読めない構成では `warn` を出して exit 0（fail-open）。`--self-test` を持つ |
| `.claude/settings.json` | ローカル実行の許可に `git ls-tree` / `git submodule status` / `git fetch` / `head` / `tail` / `echo` / `cmp` / `diff` を追加（いずれも読み取り専用。`git submodule` を丸ごと許可すると前方一致で `update` / `add` まで通るため、`git submodule status` に限定する） |
| `scripts/check-action-versions.js` | 新規。ワークフローの `uses: <action>@vN` を集め、`action-versions.json` の下限または `--compare-with` 先より古ければ **exit 1**。表に無いアクション・未使用エントリは `warn`。`--check-latest` は GitHub API 参照で warn のみ（fail-open）。`--self-test` を持つ |
| `scripts/action-versions.json` | 新規。上記の下限表（単一情報源）。`github/codeql-action` はタグ形式上メジャーを引けないため `$exempt` |
| `scripts/check-ai-workflow-config.js` | 実装用の `--append-system-prompt`（サブエージェント禁止）欠落の検査を追加 |
| `scripts/scripts.test.js` | 上記 2 検査器のテストブロックを追加（+26 ケース。125 → 151） |

### B: キット＋固有デルタ（キットの追加分のみ取り込む）

| ファイル | 取り込む差分 |
| --- | --- |
| `.github/workflows/claude-coding.yml` | `permissions:` に `actions: read`／`Run Claude Code` に `id: claude`／`claude_args` に `--append-system-prompt`（サブエージェント禁止）／末尾に `Check permission denials`（`if: always()`）ステップ／読み取り専用ツールをレビュー用と対称に揃える（`head` / `tail` / `echo` / `cmp` / `diff` / `git ls-tree` / `git submodule status` / `git fetch`。ドリフト検査はスタック別実行ツールしか見ないため、この種の非対称は機械的に検出されない） |
| `.github/workflows/claude-code-review.yml` | 同上（`id: claude`・`actions: read`・拒否検査ステップ）に加え、`--allowedTools` へ **`Bash(git status:*)` / `Bash(git ls-tree:*)` / `Bash(git submodule status:*)` / `Bash(git fetch:*)` / `Bash(cat:*)` / `Bash(head:*)` / `Bash(tail:*)` / `Bash(cmp:*)` / `Bash(diff:*)` / `Bash(echo:*)`** を追加。プロンプトに **「検証の誠実性」節**（実行した項目だけを ✅ と書く・`VAR=1 cmd` 形と書き込みを伴う検証は原理的に不可なので未検証と明記する）と、出力形式へ **「🔍 実行できなかったこと」節**（該当なしでも省略しない）を追加 |
| `.github/workflows/ci.yml` | `ai-workflow-config` ジョブに **`Check action versions` ステップ**（`--compare-with-ref`）と checkout の `fetch-depth: 0` を追加。コメント例の `actions/setup-python@v5` → `@v7`（キット本文。実体は無効化されたコメントで挙動に影響しない） |
| `scripts/README.md` | `check-action-versions.js` の一覧行・実行例、`check-permission-denials.js` の説明更新（本リポ固有の行はすべて保持） |

`actions: read` はツール許可の前提でもある。`claude-code-action` は `mcp__github_ci__*` サーバーを注入
する前にトークンが `actions: read` を持つか実検証し、無ければ `Skipping CI server installation` と警告して
導入を取り止めるため、**許可済みのはずのツールが存在しない**状態になる。`--allowedTools` の
`Bash(gh run list:*)` も同権限を要求する。ツール許可に `additional_permissions` は使わない
（あれはアプリトークンのスコープ用）。

### C: 変更しない

上記以外の全ファイル。判断が要ったものを挙げる。

- **`ci.yml` に `check-permission-denials` のジョブは足さない**。実行ログを持つ AI ワークフローにしか
  検査対象が無いためである（両ワークフローに同梱済み）。
- **`scripts/action-versions.repo.json`（固有の下限表）は作成しない**。本リポジトリが使う 10 アクションは
  すべてキットの下限表に載っており（`github/codeql-action` は `$exempt`）、`check-action-versions.js` の
  実行で警告ゼロを確認した。**空の companion を置くと「書き忘れ」として `warning:` が出る**ため、
  固有アクションを導入するまで作らない（キットの状態表に従う）。置き場所と書式は `scripts/README.md`
  「リポジトリ固有の Actions を足す場所」に記載した。
- `.github/workflows/frontend-tests.yml` の `actions/upload-artifact` は既に `@v7` で、キットが今回
  引き上げた水準（v4 → v7）を満たす。**キットのファイルをコピーする際の原則は「バージョンは高い方を
  残す」**であり、本作業でもキット側が低いものは採らなかった。この判断を人の注意力に委ねないための
  機械検査が上記の `Check action versions` ステップである。
- `--compare-with-ref` の値は **`origin/develop`**（キット既定の `origin/main` からの置換点）。
  本リポジトリの統合ブランチが `develop` であるため。

## 受け入れ基準

1. `git submodule status planning` が `3bdc8f8` を指す。
2. `repo-template` と本リポジトリの突合で、**キット側が進んでいるファイルが 0 件**になる
   （残差分はすべて分類 B/C の固有デルタであること）。
3. `node scripts/check-permission-denials.js --self-test` が成功する。
4. `node scripts/scripts.test.js` が全件成功する（新規 26 ケースを含む 151 件）。
5. `node scripts/check-ai-workflow-config.js` が成功する（`claude_args` 記法・ツール許可のドリフト・
   実装用の `--append-system-prompt` 欠落が無い）。
6. `node scripts/check-action-versions.js --dir .github/workflows --compare-with-ref origin/develop`
   （および `--self-test`）が **警告ゼロ**で成功する。
7. `node scripts/check-doc-links.js` が破損リンク 0 で成功する。
8. 両 AI ワークフローが `actions: read` を持ち、`Check permission denials` ステップを `if: always()` で
   実行する（`actionlint` 相当の構文検査として `check-ai-workflow-config.js` の通過をもって代える）。

## 影響範囲・リスク

- **AI ワークフローが赤くなり得る**: これまで緑で潰れていた実行が、拒否を検出した時点で fail する。
  これは意図した挙動（未実施のレビューを緑と誤認しない）である。緊急避難が要る場合は
  `ALLOW_PERMISSION_DENIALS=1` で警告のみに落とせる。
  なお、拒否を生む既知の 2 原因（読み取り系 git の欠落・サブエージェント禁止指示の欠落）は
  本作業で同時に塞いでいるため、検査器の導入だけで CI が赤くなる状態にはしていない。
- `.github/workflows/` は GitHub App 権限では編集不可のため、ローカル（`workflow` スコープ）から
  コミット/プッシュする。
