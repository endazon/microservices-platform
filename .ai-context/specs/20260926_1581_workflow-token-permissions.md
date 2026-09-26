---
title: 作業仕様書 — CI のワークフローに permissions を置き、既定の書き込み権限のトークンで動くジョブを必要最小限へ絞る（#1581）
type: spec
status: in-progress
related_ids: [IADR-0232, IADR-0065, IADR-0182, IADR-0194]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:docs/ai-implementation-workflow-guide.md
related_specs: [20260926_issue-1551_submodule-backend-pr-ci.md]
issue: "#1581"
---

# 作業仕様書 — CI のワークフローに permissions を置き、書き込みは要るジョブにだけ与える

## 起点

- issue: #1581（#1580〔#1551〕の監査で見つかった既存の構成の問題）。
- 起点 ID: 無採番の `NFR`（CI の統制というメタ作業で、計画の非機能要件表に当たる番号が無い。`.claude/rules/traceability.md` の 2 の場合）。
- 実装判断の記録先: **新しい IADR は起こさない。** CI の構成と「必須 check 名を変えない」（決定 2）・後段の失敗の自動起票を持つ
  IADR-0232 へ `［2026-09-26 追記 / #1581］` を足す（#1551 の追記と同じ場所）。

## 現状（着手時に確かめた。2026-09-26・`origin/develop` = 3a9f9c83）

| # | 事実 | 確かめた場所 |
| --- | --- | --- |
| 1 | リポジトリの既定は `default_workflow_permissions: write`（issue 本文。本作業では設定を読むだけで変えない） | issue #1581 |
| 2 | ワークフロー単位の `permissions:` が**無い**: `ci.yml` / `frontend.yml` / `frontend-tests.yml` / `claude-code-review.yml` / `claude-coding.yml` / `copilot-setup-steps.yml`（後ろ 3 つはジョブ単位だけ持つ） | 20 ファイルを行で読み、`yaml@2.9.0` でも構文解析して確かめた（スクラッチ。コミットしない） |
| 3 | ワークフロー単位に書き込み・追加の読み取りを置く: `changelog.yml`（contents / pull-requests write）・`openapi.yml`（同）・`obsidian-plugin-release.yml`（contents write）・`codeql.yml`（security-events write ＋ actions read）・`backlog-audit.yml`（issues write ＋ pull-requests read）・`ci-failure-issue.yml`（issues write）・`ci-latency-watch.yml`（pull-requests / checks read） | 同上 |
| 4 | ワークフロー単位が既に `contents: read` だけ: `images.yml` / `image-mapping.yml` / `integration.yml` / `integration-stack.yml` / `pr-size.yml` / `pr-title.yml` / `security.yml` | 同上 |
| 5 | `persist-credentials: false` を持つ checkout は #1580 の 2 つ（`submodule-changes` / `submodule-backend-build`）だけ | 同上 |
| 6 | 本リポジトリも submodule（`endazon/ai-stock-trading`）も **public**。`git submodule update --init` と `git fetch` は認証なしで通る（#1580 の 2 ジョブが `persist-credentials: false` のまま submodule を取って緑である） | `gh repo view --json visibility` → 両方 `PUBLIC` |
| 7 | GitHub へパッケージ・イメージを上げるジョブは無い（`images.yml` はビルドの検証だけ。「レジストリへ push はしない」と明記） | `images.yml` の build ジョブ |
| 8 | 必須 check 名は `build-and-test` / `lint` / `commit-messages` / `pr-title` / `image-build` / `scripts-tests` / `static-checks-units` / `claude-review`（ジョブ名） | `docs/ai-workflow.md` の必須チェック表 |

## ジョブ → 必要な権限（各ジョブが実際に何をするかから決めた）

凡例: 「書き込み」は GITHUB_TOKEN のスコープ。**ワークフロー単位は全ファイルで `contents: read` だけ**にし、下表の書き込みはジョブ単位で置く。
checkout 列は `persist-credentials: false` を付けたか（✓）／付けない理由。

| ワークフロー | ジョブ | 何をするか（GitHub へ書くもの） | 必要な権限 | checkout |
| --- | --- | --- | --- | --- |
| `ci.yml` | `commit-messages` / `scripts-tests` / `static-checks` / `static-checks-units` / `discover-units` / `backend-build` / `submodule-changes` / `submodule-backend-build` / `build-and-test` / `backend-format` / `lint` / `template-backend-build` | 検査・ビルド・テスト。成果物は `actions/upload-artifact`、キャッシュは `actions/cache`（どちらも GITHUB_TOKEN の権限を使わない）。計画リポの読み取りは別の秘密 `PLANNING_REPO_TOKEN` | `contents: read` | ✓（`lint` は checkout なし） |
| `frontend.yml` | `build-test` / `e2e` | 型検査・lint・ビルド・E2E | `contents: read` | ✓ |
| `frontend-tests.yml` | `test` | 単体テスト・カバレッジ（artifact） | `contents: read` | ✓ |
| `images.yml` | `changes` / `build` / `build-local` / `image-build` | イメージのビルド検証（push しない） | `contents: read` | ✓（`image-build` は checkout なし） |
| `image-mapping.yml` | `image-mapping` | 対応表の検査 | `contents: read` | ✓ |
| `pr-size.yml` | `pr-size` | 差分の大きさを step summary へ（PR へ書かない） | `contents: read` | ✓（`git fetch` は public） |
| `pr-title.yml` | `pr-title` | PR タイトルの検査 | `contents: read` | ✓ |
| `security.yml` | `secret-scan` / `dependency-review` / `vulnerable-scan` | gitleaks・依存の差分審査・脆弱性走査（PR へコメントしない設定のまま） | `contents: read` | ✓ |
| `security.yml` ほか 5 本 | `report-failure`（`security` / `codeql` / `integration` / `integration-stack` / `backlog-audit` / `ci-latency-watch`） | 再利用ワークフロー `ci-failure-issue.yml` を呼んで起票（非 PR の失敗時だけ） | `contents: read` ＋ **`issues: write`**（従前どおり） | ―（checkout なし） |
| `ci-failure-issue.yml` | `report` | issue の起票・コメント・ラベルの作成（`actions/github-script`） | `contents: read` ＋ **`issues: write`**（ワークフロー単位からジョブへ移した。呼び出し側の上限と同じ） | ―（checkout なし） |
| `codeql.yml` | `analyze` | SARIF のアップロード | `contents: read` ＋ **`security-events: write`** ＋ `actions: read`（ワークフロー単位から移した） | ✓ |
| `integration.yml` / `integration-stack.yml` | `integration` / `stack` | 統合テスト（push・日次） | `contents: read` | ✓ |
| `backlog-audit.yml` | `audit` | 棚卸し issue の本文の置き換え・要約コメント・ラベルの作成、PR の列挙 | `contents: read` ＋ **`issues: write`** ＋ `pull-requests: read`（移した） | ✓ |
| `ci-latency-watch.yml` | `watch` | PR 一覧と check-runs を読む（書かない） | `contents: read` ＋ `pull-requests: read` ＋ `checks: read`（移した） | ✓ |
| `claude-code-review.yml` | `claude-review` | スティッキーコメント・レビュー・issue の起票（API）。git は読むだけ | `contents: read` ＋ **`pull-requests: write`** ＋ **`issues: write`** ＋ **`id-token: write`** ＋ `actions: read`（従前どおり。ワークフロー単位を足しただけ） | ✓（push しない。`git fetch` は public。残すと許可済みの `cat` でトークンを読める） |
| `claude-coding.yml` | `claude` | 実装ブランチの **git push**・PR / issue へのコメント | **`contents: write`** ＋ **`pull-requests: write`** ＋ **`issues: write`** ＋ **`id-token: write`** ＋ `actions: read`（従前どおり） | 付けない（git push に要る） |
| `changelog.yml` | `changelog` | 更新ブランチの push と更新 PR（`peter-evans/create-pull-request`）、タグ時の Release | **`contents: write`** ＋ **`pull-requests: write`**（ワークフロー単位から移した） | 付けない（同アクションが push する。挙動を変えない） |
| `openapi.yml` | `openapi` | 更新ブランチの push と更新 PR | **`contents: write`** ＋ **`pull-requests: write`**（移した） | 付けない（同上） |
| `obsidian-plugin-release.yml` | `release` | Release と資産（API） | **`contents: write`**（移した） | ✓（push しない） |
| `copilot-setup-steps.yml` | `copilot-setup-steps` | Copilot coding agent の環境準備 | `contents: read`（従前どおり。ワークフロー単位を足しただけ） | **付けない**（作業ツリーがエージェントのセッションへ引き渡され、PR の CI では確かめられない。トークンは read だけ） |

**どのジョブの権限も広げていない。** 変化は (1) 書いていなかったワークフロー単位を `contents: read` にした（既定 write からの縮小）、
(2) ワークフロー単位の書き込み・追加の読み取りを、それを使うジョブへ移した（同じワークフローの他のジョブからは消える）、(3) checkout の資格情報を残さない、の 3 つだけである。

## 設計判断

- **ワークフロー単位は `contents: read` だけ**（依頼どおり）。`ci-latency-watch.yml` の読み取り 2 つ・`backlog-audit.yml` の `pull-requests: read` もジョブへ移し、全ファイルで同じ形にした
  （検査を「ワークフロー単位は `{ contents: read }` に等しい」の 1 条件で書ける）。
- **ジョブ単位の書き込みは、上の表と `scripts/scripts.repo.test.js` の表（`WRITE_JOBS`）で名指しする。** 表に無いジョブへ書き込みを足すと落ち、表にあるのに書き込みが消えても落ちる。
- **`persist-credentials: false`** は `contents: write` を持たないジョブの checkout すべてに付ける（足したのは 28 か所。#1580 の 2 つを含めて 30）。例外は `copilot-setup-steps` の 1 つだけで、検査の側に理由とともに名指しする。
  push するジョブ（`claude` / `changelog` / `openapi`）は付けない。
- **必須 check 名は変えない**: ジョブ ID・`name:` を 1 つも変えていない（差分に `name:` の行が無いことを確かめた）。`on:` も変えていない。
- **リポジトリ設定は変えない**。既定を `read` へ下げる提案は IADR-0232 の追記と PR 本文に書く（下げても本変更の YAML はそのまま効く。下げると、今後 `permissions:` を書き忘れたワークフローも読むだけで走る）。
- **採らなかったこと**: `ci-failure-issue.yml` の失敗ジョブ名の取得（`listJobsForWorkflowRun`）は `actions: read` が無いと失敗し、既存のコードは警告に倒して起票を続ける。
  `actions: read` を足すには呼び出し側 6 か所の上限も同時に上げる必要があり、本作業（縮小）の射程を越えるので変えない（起票の品質の改善として別件）。

## PR の CI で確かめられること・確かめられないこと

| 対象 | この PR で走るか | 確かめ方 |
| --- | --- | --- |
| `ci.yml` 全ジョブ・`images.yml`・`pr-title.yml`・`pr-size.yml`・`security.yml`・`image-mapping.yml` | 走る（`pull_request`） | 緑であること（読むだけの権限・資格情報なしの checkout で submodule 取得と `git fetch` が通る） |
| `frontend.yml` / `frontend-tests.yml` / `copilot-setup-steps.yml` / `codeql.yml` | 走る（本 PR が各ファイル自身を変えるので `paths:` に当たる） | 緑であること。`codeql` は **ジョブ単位へ移した `security-events: write` で SARIF を上げられる**ことの確認になる |
| `claude-code-review.yml` | 走る（PR 版のワークフローが使われる） | **スティッキーコメントが PR に投稿される**こと、`persist-credentials: false` でも `git fetch` / 差分の取得が通ること |
| 再利用ワークフロー `ci-failure-issue.yml` の権限の整合 | 一部走る | 呼び出す `report-failure` は PR では `if:` で skipped になるが、`codeql.yml` / `security.yml` の run が**起動時にエラーにならず** `report-failure` が skipped と出れば、呼び出し側の上限（`contents: read, issues: write`）と `report` ジョブの要求の整合は通っている。**起票そのもの**は非 PR の失敗時にしか走らない |
| `changelog.yml` / `openapi.yml` | **走らない**（develop / main / タグへの push、手動） | 権限は従前のワークフロー単位の値（`contents: write` ＋ `pull-requests: write`）をそのままジョブへ移しただけで、ジョブは 1 つしかない＝実効のトークンは同一。checkout・トークンの渡し方も変えていない。**マージ後の最初の develop への push で `changelog` の run を確かめる** |
| `obsidian-plugin-release.yml` | 走らない（タグ、手動） | 同上（`contents: write` を移しただけ）。checkout の資格情報は Release の API 呼び出しに使われない |
| `backlog-audit.yml` / `ci-latency-watch.yml` / `integration*.yml` / 各 `report-failure` | 走らない（週次・日次・push・非 PR の失敗時） | 同上（スコープを移しただけ）。`workflow_dispatch` で PR ブランチから回す案は、棚卸し issue の書き換えなど**リポジトリへの副作用**を伴うので採らない |
| `claude-coding.yml` | 走らない（`@claude` のコメント） | ジョブ単位の権限・checkout とも変えていない（ワークフロー単位を足しただけ） |

`ci.yml` を変えるので `ci-latency-watch` の epoch（CI 構成が最後に変わった時刻。IADR-0241）が本 PR のマージ時刻へ進む。これは ci.yml を変える変更すべてに共通で、#1580 と同じ。

## 母集合（誤りになる記述の走査）

`git grep`（パス除外: `src/ai-stock-trading` / `CHANGELOG.md` / `.ai-context/specs/`。拡張子で絞らない）。

| 軸 | 検索 | 拾ったもの |
| --- | --- | --- |
| 1 ワークフロー | `.github/workflows/*.yml` の全 20 ファイル | 全数を上の表へ（行で読み、YAML パーサでも確かめた） |
| 2 権限の言明 | `default_workflow_permissions\|permissions:\|persist-credentials\|GITHUB_TOKEN.*(権限\|write)\|書き込み権限` in docs / `AI_SETUP.md` / `README.md` / IADR | ワークフローの権限を述べる live な文書は無かった（当たりは IADR-0065 の「`GITHUB_TOKEN` の既定権限で public を read できる」〔正しい〕と、無関係なアプリの書き込み権限） |
| 3 既存の検査 | `contents: (write\|read)\|issues: write\|pull-requests: write\|security-events\|id-token\|persist-credentials` in `scripts/` | `scripts.repo.test.js` の #1551 のジョブ単位の検査（残す）と #735 の `pr-size.yml` の未使用権限の検査（残す） |
| 4 checkout の注記 | `persist-credentials` / 「permissions: contents: write が push を許可する」 | `claude-code-review.yml` の checkout のキット由来の注記（レビューは push しないのに push を前提にしていた）→ 書き直した |
| 5 ワークフロー単位の位置に依存する検査 | `indexOf('\npermissions:')` | `scripts.repo.test.js` の #1213（`obsidian-plugin-release.yml` の `on:` を `permissions:` の手前までで切る）→ ワークフロー単位の `permissions:` を残すので影響なし |

**直すもの**: 20 ファイル（ワークフロー単位の追加・移動・checkout）、`claude-code-review.yml` の checkout の注記、
`scripts/scripts.repo.test.js`（#1581 の 2 件）、`docs/ai-workflow.md`（権限の節と表）、IADR-0232 の追記。

**除外したものと理由**

- `images.yml` / `image-mapping.yml` / `integration*.yml` / `pr-size.yml` / `pr-title.yml` / `security.yml` のワークフロー単位: 既に `contents: read` だけなので触らない（checkout だけ直す）。
- `scripts.repo.test.js` の #1551（ジョブ単位の `contents: read` と checkout）・#735（`pr-size.yml` に `pull-requests` が無い）: 本変更と両立し、より狭い不変条件として残す。
- IADR-0065: 「public ユニットは `GITHUB_TOKEN` の既定権限で read できる」は `contents: read` でも正しい。
- `ci-failure-issue.yml` 冒頭の「呼び出し側の書き方」: 呼び出し側の `permissions` は変えないので正しいまま。

## 受け入れ基準

1. 20 ワークフローすべてがワークフロー単位で `permissions: { contents: read }` だけを持つ。
2. 書き込みのスコープを持つジョブは上の表のものだけで、スコープも表と一致する。`write-all` / `read-all` は使わない。
3. `contents: write` を持たないジョブの checkout は `persist-credentials: false`（例外は `copilot-setup-steps` の 1 つ）。
4. ジョブ ID・`name:`・`on:` は変わらず、必須 check 名 8 件は変わらない。
5. `scripts/scripts.repo.test.js` が 1〜3 を全ワークフローについて検査し、変異（ワークフロー単位の削除・`write-all`・ワークフロー単位への書き込みの追加・ジョブの `write-all`・表に無い書き込み・資格情報の残置）で落ちる。
6. 本 PR の CI で、走るワークフローがすべて緑、`claude-review` がスティッキーコメントを投稿する。走らないワークフローの扱いは上の表のとおり記録する。
7. リポジトリ設定は変えない。

## 検証（証跡は PR 本文）

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- `node scripts/check-workflow-job-refs.js` / `check-ai-workflow-config.js`（`STRICT_AI_WORKFLOW_CONFIG=1`）/ `check-action-versions.js --dir .github/workflows --compare-with-ref origin/develop`
- 20 ファイルの YAML 構文（スクラッチの `yaml@2.9.0` で解析。コミットしない）
- PR の `gh pr checks` と `claude-review` のコメント

## ［2026-09-26 追記 / #1588］`claude-review` の `persist-credentials: false` の理由は誤りだった・読み取りのスコープも固定した

- 🔴 **上の表の `claude-code-review.yml` の行の「残すと許可済みの `cat` でトークンを読める」は誤りである。** 固定している
  `anthropics/claude-code-action@cfc3eb22…` は起動時に `replaceCheckoutCredentials`（`src/github/operations/git-config.ts`）で
  checkout の extraheader を消したうえで、`origin` を `https://x-access-token:<github_token>@github.com/…` へ書き換える
  （`allowed_non_write_users` を使わない構成の分岐。tag / agent の両モードとも `use_commit_signing` の有無によらず呼ぶ）。
  `github_token` は `secrets.GITHUB_TOKEN` なので、**同じトークンが `.git/config` に残る**。同じ値は step の env（`GH_TOKEN`）にも在る。
  したがって `persist-credentials: false` は `claude-review` では**トークンを隠していない**（害も無い）。
  ワークフローの注記を正し、設定は #1581 の一律の規則（`contents: write` を持たないジョブの checkout は資格情報を残さない）に揃えるために残した。
- 許可する道具を絞ってトークンを隠す案（`Bash(cat:*)` を外して Read にパスの制限を掛ける等）は**採らなかった**:
  `.git/config` を読める道具は `cat` だけではなく（Read・`grep`・`rg`・`head`・`tail`・`awk`）、`Bash(node:*)` は任意のコードを走らせて
  env の `GH_TOKEN` も読める。どれもレビュー（差分の読解・検査器の実走）に要る。削っても隠せず、レビューだけが壊れる。
  トークンを縛っているのはジョブの `permissions`（最小限）とジョブ終了での失効である。**残る案は #1588 の作業仕様書に follow-up として記録した。**
- 本仕様書の「採らなかったこと」の `ci-failure-issue.yml` の `actions: read` は #1588 で足した（呼び出し側 6 本と `report` ジョブを同時に）。
  `scripts/scripts.repo.test.js` の表は `WRITE_JOBS`（書き込みだけ）から `JOB_SCOPES`（`contents: read` 以外のすべてのスコープ）へ広げ、
  要る読み取りを外しても落ちるようにした。作業仕様書: `.ai-context/specs/20260926_issue-1588_grafana-rule-verify-and-workflow-read-scopes.md`
