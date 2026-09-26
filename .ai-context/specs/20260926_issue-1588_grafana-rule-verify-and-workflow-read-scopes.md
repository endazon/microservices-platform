---
title: "Grafana ルールの検査の見逃しを埋め、ワークフローの読み取りスコープを試験で固定し、claude-review の資格情報の記述を正す（#1588）"
type: spec
status: done
related_ids: [NFR-21, ADR-0006, IADR-0165, IADR-0232]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-21（障害検出 5 分以内）
  - planning:docs/ai-implementation-workflow-guide.md
related_specs: [20260926_1577_grafana-filter-evaluator-never-fires.md, 20260926_1581_workflow-token-permissions.md]
issue: "#1588"
---

# 作業仕様書 — Grafana ルールの検査の見逃しを埋め、読み取りスコープを固定し、claude-review の資格情報の記述を正す（#1588）

## 起点

- issue: #1588（#1585〔#1577〕と #1586〔#1581〕の監査で出た、ブロックしない指摘）。
- 起点 ID: **NFR-21**（Grafana 暫定アラートの検査 6。永久に発火しないルールは障害検出を黙って失わせる）。
  ワークフローの権限と claude-review の注記は CI の統制というメタ作業で、計画の非機能要件表に当たる番号が無い（`.claude/rules/traceability.md` の 2 の場合。無採番の `NFR`）。
- 計画 ADR: ADR-0006（アラートは Alertmanager。Grafana は暫定。改めない）。実装 IADR: IADR-0165（Grafana 暫定アラート）／IADR-0232（CI の権限・後段の起票）。
- **新しい IADR は起こさない。** 検査器の読み方の拡張と、IADR-0232 の #1581 追記の続き（日付つき追記）であり、新たな設計判断ではない。

## 1. `check-grafana-alerting.js` の検査 6（NFR-21）

### 着手時の現況（2026-09-26・`origin/develop` = 10805447）

- 実データ 20 件の式と評価器を `grafanaRuleConditions` で読んで並べた（スクラッチ）。形は 7 種: 生の選択子（`up{…}`・`postfix_queue_size{…}`）／`absent(…)`・`absent_over_time(…)`／
  `== bool 0 and on (…) (…)`／集約の除算（`sum by (job)(rate(…)) / sum by (job)(rate(…))`）／`histogram_quantile(0.95, sum by (le)(rate(…)))`／
  `increase(…) > 0`／`(sum(increase(…)) or vector(0)) + (…) > 0`／`sum by (…)(increase(…)) > on (…) max by (…)(…)`。
- 全ルールの `data` は `A`（prometheus・`expr`）と `C`（`__expr__`・`type: threshold`・`expression: A`・評価器 1 件）の 2 段、`condition: C`。math / reduce の段は無い。
- 従前の読み方は、読めない形を ALL（値を縛らない）へ倒していた。issue の列挙どおり `(up == 0) * 1`・`up == 0 + 0`・`max(up == 0)`・`clamp_max(up, 0)`・`vector(0)`・
  `up == 0 or vector(0)`・16 進はすべて偽陰性。`bool` を {0,1} に固定する試験も無かった（`bool` の行を消すと ALL に倒れて試験が通る）。
- `condition` と refId を突き合わせず、ルール本文の `expr:` と `evaluator:` を正規表現で 1 件ずつ拾っていた。

### 設計

- **積極的に読める形だけを値の集合（区間の和）へ写し、それ以外は「検証できない」と報告する。** 読める形:
  `or`（和集合）／`and`・`unless`（左辺）／式全体の括弧／最上位の比較 1 つ（`bool` → {0,1}、片辺が定数 → 他辺の値 ∩ 絞り込み、ベクタどうし・`on` / `ignoring` / `group_*` → 左辺。右辺も読めること）／
  生の選択子（範囲・`offset`・`@`）→ 任意／`absent*` → {1}／`vector(定数)` → {定数}／`clamp_max`・`clamp_min`・`clamp` → 厳密に切り詰め／
  `PASS_THROUGH_FUNCTIONS`（`sum`・`rate`・`histogram_quantile` など、引数が任意なら出力も任意に読んでよいもの）と `+ - * /`（定数は 0 でない有限値）→ 引数・辺がすべて任意で比較を含まないときだけ任意。
  定数は 10 進・指数・16 進・`Inf`・`NaN`・定数どうしの算術（`0 + 0`）。
- **報告する形**: 算術・集約・関数が比較を包む（`(up == 0) * 1`・`max(up == 0)`・`sum(x == bool 0)`）／値が縛られた辺に算術・集約を掛ける／`% ^ atan2`／単項の符号を掛けたベクタ／
  一覧に無い関数（`count`・`abs` のように値の範囲を縛り得るもの）／連鎖した比較／その他の読めない形。
- **`UNVERIFIABLE_ALLOWLIST`（title → 理由）**: レビューを経て載せる許可リスト。載せたルールの「検証できない」は黙る。**載せたのに検証できる／ルールが無い**項目は違反（腐らせない）。
- **`condition` → threshold → `expression` → クエリを refId で辿る。** condition の refId が無い・threshold の expression の refId が無い・refId の重複・評価器が 1 件でない → 違反。
  condition が threshold でない、間に math / reduce などの `__expr__` の段がある → 「検証できない」（許可リストの対象）。
- 近似として受容する偽陰性: `PASS_THROUGH_FUNCTIONS` の中にも値の範囲を持つもの（`rate` / `increase` は 0 以上）があるが、任意として読む（上位集合なので偽陽性は出ない。`lt 0` で比べる評価器は実在しない）。

### 結果: 実データの 20 件はすべて検証でき、許可リストは空

| ルール | 読んだ値の集合 | 評価器 |
| --- | --- | --- |
| OtelCollectorDown / ResetFloorNoReadyEndpoint | 任意（生の `up`） | `lt 1` |
| ServiceRequestMetricsAbsent | {0, 1}（`== bool 0` の左辺 ＋ `and on`） | `gt 0` |
| HighHttp5xxRate | 任意（集約の除算） | `gt 0.05` |
| SearchLatencyP95High / RagFirstTokenP95High / RagLatencyP95High | 任意（`histogram_quantile`） | `gt 1.5` / `gt 5` / `gt 5` |
| `…SeriesAbsent` 6 件・`…ProducerAbsent` 2 件 | {1}（`absent` / `absent_over_time`） | `gt 0` |
| UnitDocumentsMissingProjectAttribute | (0, ∞)（`increase(…) > 0`） | `gt 0` |
| DepartmentSyncNotCorrecting | (0, ∞)（`(… or vector(0)) + (… or vector(0)) > 0`。和の各辺は ALL ∪ {0} ＝ 任意） | `gt 0` |
| MailRelayDeferredBacklog / MailRelayDeferredMessageNearExpiry | 任意（生の選択子） | `gt 0` / `gt 1200` |
| LlmMonthlyBudgetExceeded | 任意（`> on (…)` のベクタどうし） | `gt 0` |

## 2. ワークフローの読み取りスコープを試験で固定する（無採番 NFR）

- `scripts/scripts.repo.test.js` の #1581 の表 `WRITE_JOBS`（書き込みだけ）を **`JOB_SCOPES`（`contents: read` 以外のすべてのスコープ）** へ広げた。
  ジョブの permissions から `contents: read` を除いたものが表と完全一致すること（ジョブ単位で `contents` を書かない＝ none も差として出る）。
- 母集合（`.github/workflows/*.yml` 20 本のジョブ単位の `permissions:` を行で全数読んだ）: `contents: read` 以外を持つジョブは 15。うち読み取りを持つのは
  `backlog-audit:audit`（pull-requests）・`ci-latency-watch:watch`（checks・pull-requests）・`claude-code-review:claude-review` / `claude-coding:claude` / `codeql:analyze`（actions）と、
  下の 3 で足した `report-failure` 6 本・`ci-failure-issue:report`（actions）。`ci.yml` の 2 ジョブは `contents: read` だけなので表に載らない（ワークフロー単位と同じ）。
- 変異試験: `ci-latency-watch` から `checks: read` を外す／`codeql:analyze` から `actions: read` を外す／表に無いジョブへ `pull-requests: read` を足す／
  ジョブ単位の permissions から `contents` を落とす —— いずれも落ちる。

## 3. `ci-failure-issue.yml` の呼び出し側に `actions: read`（issue の 4。任意 → 実施）

- `report` ジョブは失敗ジョブ名を `listJobsForWorkflowRun` で引くが、`actions: read` が無く 403 を警告へ倒していた（起票本文から失敗ジョブ名が落ちる）。
- `report` ジョブと呼び出し側 6 本（`security` / `codeql` / `integration` / `integration-stack` / `backlog-audit` / `ci-latency-watch` の `report-failure`）へ **`actions: read` を同時に**足した。
  読み取りだけで、書き込みは広げていない。要求が呼び出し側の上限を超えると呼び出し側の run が起動時に失敗するため、**呼び出し側が与える範囲と `report` の要求の一致**も試験で突き合わせる（変異: 1 本から外すと落ちる）。
- 表（`docs/ai-workflow.md`・`JOB_SCOPES`）と IADR-0232 の追記を同時に直した。

## 4. claude-review の `persist-credentials: false` の記述（無採番 NFR）

- 固定している `anthropics/claude-code-action@cfc3eb22…` の `src/github/operations/git-config.ts` と `src/modes/{tag,agent}/index.ts` を `gh api` で読んで確かめた:
  両モードとも（`use_commit_signing` の有無によらず）`replaceCheckoutCredentials` を呼び、checkout の extraheader を消したうえで、`allowed_non_write_users` を使わない構成では
  `origin` を `https://x-access-token:<github_token>@github.com/…` へ書き換える。本ワークフローは `track_progress: true` の `pull_request` なので tag モードで、`github_token` は `secrets.GITHUB_TOKEN`。
  **同じトークンが `.git/config` に残り**、step の env（`GH_TOKEN`）にも在る。→ `claude-code-review.yml` の注記を正し、#1581 の作業仕様書へ日付つき追記、IADR-0232 へ追記した。
- **許可する道具は変えない（明らかに安全とは言えないため）。** `Bash(cat:*)` を外して Read にパスの制限を掛けても、`.git/config` は `grep` / `rg` / `head` / `tail` / `awk` でも読め、
  `Bash(node:*)` は任意のコードで env の `GH_TOKEN` も読める。どれもレビュー（差分の読解・検査器の実走）に要る。削ってもトークンは隠せず、レビューだけが壊れる。
- **follow-up（本 PR では実施しない。記録に留める）**:
  1. `allowed_non_write_users` を設定した構成では、アクションが credential helper を使い `.git/config` をトークンなしに保ち、子プロセスの env を消す（`CLAUDE_CODE_SUBPROCESS_ENV_SCRUB`）。
     ただし同入力は**書き込み権限の無い利用者にも起動を許す**ので本リポジトリの目的に反する。env の消去だけを `CLAUDE_CODE_SUBPROCESS_ENV_SCRUB=1` で入れる案は、
     `gh issue create` などが `GH_TOKEN` を要することとの両立を PR 上で実測してから判断する（`.git/config` のトークンは残る）。
  2. `Read` / `Grep` に `.git/**` の拒否（`--disallowedTools`）を足し、`Bash(node:*)` を検査器の名指し（`Bash(node scripts/…)`）へ狭める案。レビューの実走への影響を PR 上で測ってから判断する。
  いまトークンを縛っているのはジョブの permissions（`contents: read` ＋ PR / issue への書き込み・`actions: read`）とジョブ終了での失効である。

## 母集合（誤りになる記述の走査。パス除外: `src/ai-stock-trading` / `CHANGELOG.md`。拡張子で絞らない）

| 軸 | 検索 | 拾ったもの → 扱い |
| --- | --- | --- |
| 1 検査器の API の利用者 | `check-grafana-alerting` / `grafanaRuleConditions` / `expressionValueSet` / `filterEvaluatorIssues` | `scripts/scripts.repo.test.js` だけ → 名指しの変異ケースを追加・実データの変異試験を 2 件追加。`grafanaRuleConditions` の戻り値は互換のまま（`exprs` / `evaluators` を残し `condition` / `nodes` を足した） |
| 2 検査 6 の射程の言明 | `check-grafana-alerting` in docs | `docs/operations/operations.md`（範囲の列挙）→ 「読めない式は報告・refId で辿る」を追記。860 行目（組み合わせを止める）は正しいまま。`docs/observability/rag-first-token-latency.md`（1 対 1 の突合）は正しいまま。`deploy/grafana/provisioning/alerting/slo-alerts.yaml` 冒頭の列挙は正しいまま（k8s inline と同内容の検査があるので、コメントだけの改稿はしない） |
| 3 権限の表 | `WRITE_JOBS` / `permissions` の表 | `scripts/scripts.repo.test.js`・`docs/ai-workflow.md`・#1581 の作業仕様書（追記）・IADR-0232（追記） |
| 4 呼び出し側の上限の言明 | `issues: write` ＋ `ci-failure-issue` / `listJobsForWorkflowRun` / `actions: read` | `ci-failure-issue.yml` 冒頭の「呼び出し側の書き方」と上限の注記 → 直した。IADR-0232 の #1581 追記の上限 → 新しい追記で置き換えを明記（本文は書き換えない）。#1581 の作業仕様書の「採らなかったこと」→ 追記 |
| 5 persist-credentials の理由 | `persist-credentials` / 「トークンを読め」/「`cat`」 | `claude-code-review.yml:85-89`（直した）・#1581 の作業仕様書の表の行（追記）・`docs/ai-workflow.md` 4.（注記を追加）。`20260926_issue-1551` の仕様書（submodule を匿名で取れる、は正しい）と IADR-0232（誤った言明は無い）は変えない |

## 受け入れ基準

- [x] `x == bool 0` と `gt 1` を検出する試験（`bool` の読み取りを消した変異版で自己試験が落ちることも確かめた）
- [x] issue の列挙した偽陰性（`(up == 0) * 1`・`up == 0 + 0`・`max(up == 0)`・`clamp_max(up, 0)`・`vector(0)`・`up == 0 or vector(0)`・16 進）がすべて検出または「検証できない」で報告される
- [x] 実データの 20 件がすべて通り、許可リストは空（`filterEvaluatorIssues` の `checked` がルール数と一致）
- [x] `condition` と refId の鎖の欠落・重複・中間の math の段を検出／報告する
- [x] 読み取りスコープを外すと落ちる（`JOB_SCOPES`）。呼び出し側と `report` の範囲の一致を突き合わせる
- [x] claude-review の注記と #1581 の作業仕様書（日付つき追記）を正し、道具の許可は変えずに follow-up を記録した
- [x] ジョブ ID・`name:`・`on:` は変えていない（必須 check 名は不変）。リポジトリ設定・ワークフローの手動実行はしていない

## 検証

- `node scripts/check-grafana-alerting.js --self-test` → 38 件通過
- `node scripts/check-grafana-alerting.js` → OK（組み合わせ 20 件・許可リスト 0 件）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → 全件 pass
- `node scripts/check-workflow-job-refs.js` / `STRICT_AI_WORKFLOW_CONFIG=1 node scripts/check-ai-workflow-config.js` / `node scripts/check-action-versions.js --dir .github/workflows --compare-with-ref origin/develop`
- PR の `gh pr checks`（`codeql` / `security` の run が起動時エラーにならず `report-failure` が skipped と出れば、呼び出し側の上限と `report` の要求の整合は通っている）
