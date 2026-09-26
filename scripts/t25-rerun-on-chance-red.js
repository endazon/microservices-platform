#!/usr/bin/env node
'use strict';
/*
 * t25-rerun-on-chance-red.js
 * SC-15 / NFR-13 / 計画 ADR-0118 決定 2・3 / IADR-0470（#1617）:
 * **integration-stack が T-25（所要時間の順位和検定）だけで赤になったら、同じコミットで 1 回だけ再実行し、
 * 結果を CI の失敗の自動起票の issue へ書く。**
 *
 * `.github/workflows/integration-stack-rerun.yml`（`workflow_run: completed`）から呼ばれる。判定は純関数 `decide` に閉じ、
 * 自己試験が場面ごとに固定する。
 *
 * ## 判定（`decide`）
 *
 * - **attempt 1 が赤**で、次のすべてを満たすときだけ **再実行する**（`gh run rerun <id> --failed`。同じ run の新しい attempt ＝同じコミット・同じワークフロー定義）:
 *   - 契機が `push` か `schedule`（手動実行は床なしの比較〔`istio=false`〕があり得るので対象外。手で確かめる）
 *   - integration-stack.yml の「T-25 だけの赤」の手順（`CANDIDATE_STEP`）が `success`
 *     ＝ パスワードリセットの門の失敗が T-25 の順位和検定の赤ただ 1 件で、後段の門がすべて緑（検査器と `if:` が判定する）
 *   - 🔴 **それに加えて、ここでも手順の結論を数え直す**: 失敗した手順はパスワードリセットの門ただ 1 つで、ほかの門はすべて `success`
 *     （`if:` の式が崩れても、他の門の赤を偶然の赤として再実行しない）
 * - **attempt 2（その再実行）が終わったら、結果を issue へ書く**（attempt 1 が上の条件を満たしていたときだけ）。
 *   合格なら「偶然の赤」、T-25 がまた赤なら「再実行も赤」（床の引き直しの契機・計画へ環流）。
 * - 🔴 **attempt 3 以降には何もしない。再実行の再実行はしない**（ADR-0118 決定 2「赤の実行ごとに 1 回まで」）。
 *
 * ## issue を閉じない
 *
 * マーカー（`ci-failure:integration-stack`）はワークフロー単位なので、開いている issue には**他の run の本物の失敗**も集まる。
 * 偶然の赤と確かめても、閉じる前に issue の他のコメント（他の run の失敗）を人が見る。**自動では閉じない。**
 *
 * 使い方:
 *   node scripts/t25-rerun-on-chance-red.js --run-id <id> --attempt <n> [--repo <owner/name>]          # 判定だけ（GitHub を読むだけ）
 *   node scripts/t25-rerun-on-chance-red.js --run-id <id> --attempt <n> --repo <owner/name> --apply     # 再実行とコメントを行う（ワークフローから）
 *   node scripts/t25-rerun-on-chance-red.js --self-test
 */
const fs = require('fs');
const { execFileSync } = require('child_process');
const { TIMING_ALPHA, TIMING_VERDICT } = require('./check-password-reset-mail.js');
const {
  parseT25FromJobLog, ghGet, ghGetJson, DEFAULT_REPO, WORKFLOW_FILE, STACK_JOB_NAME, RESET_GATE_PREFIX,
} = require('./t25-monthly-summary.js');

/** integration-stack.yml の「T-25 だけの赤」の手順名（API は長い手順名を切るので短い ASCII に保つ）。 */
const CANDIDATE_STEP = 'T-25 only red (chance-red candidate)';
/** CI の失敗の自動起票（ci-failure-issue.yml）が integration-stack の issue に埋めるマーカー。 */
const ISSUE_MARKER = '<!-- ci-failure:integration-stack -->';
/** 自動で再実行する契機。 */
const RERUN_EVENTS = ['push', 'schedule'];
/** 自動で扱う run の head ブランチ（integration-stack の push と schedule は develop だけ。#1617 監査 R1）。 */
const RERUN_BRANCH = 'develop';
const GATE_PREFIX = '🔴 Gate';
/** 後段の門の最小数（スタック・ABAC と検索・ログイン経路）。手順名の変更で 0 件走査になったら候補にしない。 */
const MIN_OTHER_GATES = 3;

// ---------------------------------------------------------------- 判定（純関数）

/**
 * attempt のジョブの一覧から「T-25 だけの赤」かを判定する。**純関数**。
 * @returns {{ok:boolean, reason:string}}
 */
function candidateOf(jobs) {
  const list = Array.isArray(jobs) ? jobs : [];
  const stack = list.find((j) => j.name === STACK_JOB_NAME);
  if (!stack) return { ok: false, reason: `ジョブ ${STACK_JOB_NAME} が無い` };
  if (stack.conclusion !== 'failure') return { ok: false, reason: `ジョブ ${STACK_JOB_NAME} の結論が ${stack.conclusion}（赤ではない）` };
  const otherRedJobs = list.filter((j) => j !== stack && j.conclusion === 'failure').map((j) => j.name);
  if (otherRedJobs.length) return { ok: false, reason: `ほかのジョブも赤: ${otherRedJobs.join(' / ')}` };
  const steps = stack.steps || [];
  const marker = steps.find((s) => s.name === CANDIDATE_STEP);
  if (!marker) return { ok: false, reason: `手順「${CANDIDATE_STEP}」が無い（この run のワークフロー定義は自動の再実行に対応していない）` };
  if (marker.conclusion !== 'success') {
    return { ok: false, reason: `手順「${CANDIDATE_STEP}」が ${marker.conclusion}（パスワードリセットの門の失敗が T-25 の順位和検定の赤だけではないか、ほかの門が緑でない）` };
  }
  const failed = steps.filter((s) => s.conclusion === 'failure').map((s) => s.name);
  if (failed.length !== 1 || !String(failed[0]).startsWith(RESET_GATE_PREFIX)) {
    return { ok: false, reason: `失敗した手順がパスワードリセットの門ただ 1 つではない: ${failed.join(' / ') || '（なし）'}` };
  }
  const others = steps.filter((s) => String(s.name).startsWith(GATE_PREFIX) && !String(s.name).startsWith(RESET_GATE_PREFIX));
  const notGreen = others.filter((s) => s.conclusion !== 'success').map((s) => `${s.name}=${s.conclusion}`);
  if (others.length < MIN_OTHER_GATES) return { ok: false, reason: `ほかの門が ${others.length} 件しか読めない（手順名が変わった？）` };
  if (notGreen.length) return { ok: false, reason: `ほかの門が緑でない: ${notGreen.join(' / ')}` };
  return { ok: true, reason: 'T-25 だけの赤（ほかの門はすべて緑）' };
}

/**
 * 再実行（attempt 2）の結果を分類する。**純関数**。
 * @param {{conclusion:string}} run  attempt 2 の run
 * @param {{verdict:(string|null)}} t25  attempt 2 のログから読んだ T-25
 */
function rerunOutcome(run, t25) {
  if (run.conclusion === 'success') return 'chance-red';
  if (run.conclusion !== 'failure') return 'rerun-cancelled';
  const verdict = t25 && t25.verdict;
  if (verdict === TIMING_VERDICT.FAIL) return 'rerun-red';
  if (verdict === TIMING_VERDICT.PASS) return 'rerun-t25-green-other-red';
  return 'rerun-unevaluable';
}

/**
 * 何をするかを決める。**純関数**。
 * @param {{run:object, jobs:object[], prevJobs?:object[]}} input  `run` はこの attempt の run（`run_attempt` を持つ）
 * @returns {{action:'rerun'|'report'|'none', reason:string}}
 */
function decide({ run, jobs, prevJobs }) {
  const none = (reason) => ({ action: 'none', reason });
  if (!run) return none('run が無い');
  if (!String(run.path || '').endsWith(`/${WORKFLOW_FILE}`)) return none(`対象外のワークフロー: ${run.path}`);
  if (run.status !== 'completed') return none(`run が終わっていない: ${run.status}`);
  if (!RERUN_EVENTS.includes(run.event)) return none(`契機 ${run.event} は自動で再実行しない（手動実行は床なしの比較があり得る。手で確かめる）`);
  // #1617 監査 R1（多層防御）: ワークフローの `branches: [develop]` に加えて、ここでも head のブランチとリポジトリを確かめる。
  // 別ブランチ・フォークの head の run を、書き込みのトークンで再実行したり issue へ書いたりしない。
  if (run.head_branch !== RERUN_BRANCH) return none(`head のブランチ ${run.head_branch} は自動で扱わない（${RERUN_BRANCH} だけ）`);
  const headRepo = run.head_repository && run.head_repository.full_name;
  const baseRepo = run.repository && run.repository.full_name;
  if (!headRepo || !baseRepo || headRepo !== baseRepo) return none(`head のリポジトリ ${headRepo} が ${baseRepo} と違う（フォークの run は自動で扱わない）`);
  if (run.run_attempt === 1) {
    if (run.conclusion !== 'failure') return none(`attempt 1 の結論が ${run.conclusion}（赤ではない）`);
    const c = candidateOf(jobs);
    return c.ok ? { action: 'rerun', reason: c.reason } : none(c.reason);
  }
  if (run.run_attempt === 2) {
    const c = candidateOf(prevJobs);
    if (!c.ok) return none(`attempt 1 は T-25 だけの赤ではなかった（この attempt は自動の再実行ではない）: ${c.reason}`);
    return { action: 'report', reason: '自動の再実行（attempt 2）が終わった' };
  }
  return none(`attempt ${run.run_attempt}: 再実行の再実行はしない（赤の実行ごとに 1 回まで）`);
}

// ---------------------------------------------------------------- 文面（純関数）

const fmtP = (p) => (p === null || p === undefined || !Number.isFinite(p) ? '—' : (p >= 1e-4 ? p.toFixed(4) : p.toExponential(2)));
const runUrl = (repo, id, attempt) => `https://github.com/${repo}/actions/runs/${id}${attempt ? `/attempts/${attempt}` : ''}`;
const markerOf = (kind, runId) => `<!-- t25-chance-red:${kind}:${runId} -->`;
const manualCommand = (repo, runId) => `gh run rerun ${runId} --failed --repo ${repo}`;

function rerunRequestedBody({ repo, run, t25, error }) {
  const head = error
    ? `**T-25（所要時間）だけが赤だったが、同じコミットの再実行を起こせなかった。** 手で 1 回だけ再実行すること:\n\n\`\`\`sh\n${manualCommand(repo, run.id)}\n\`\`\`\n\n（エラー: ${String(error).split('\n')[0]}）`
    : '**T-25（所要時間）だけが赤だったので、同じコミットで 1 回だけ再実行した**（計画 ADR-0118 決定 2）。結果はこの issue へ自動で書く。';
  return [
    markerOf('rerun', run.id),
    head,
    '',
    `- 赤の実行: ${runUrl(repo, run.id, 1)}（契機 \`${run.event}\` / コミット \`${run.head_sha}\`）`,
    `- T-25: p=${fmtP(t25 && t25.p)} / W=${(t25 && t25.w) ?? '—'}（有意水準 ${TIMING_ALPHA}・両側）`,
    '- ほかの門: すべて緑（スタック・ABAC と検索の投入・ABAC と検索の門・ログイン経路の門）。パスワードリセットの門の中の失敗も T-25 の順位和検定の赤だけ',
    `- 再実行: \`${manualCommand(repo, run.id)}\` 相当（同じ run の attempt 2）。**再実行の再実行はしない。**`,
    '',
    '再実行の結果がこの issue に書かれないまま run の attempt 2 が終わっていたら、attempt 2 の結果を見て手で記録すること。',
  ].join('\n');
}

const OUTCOME_TEXT = {
  'chance-red': [
    '**再実行は合格した。偶然の赤として記録する**（計画 ADR-0118 決定 2・3。7 日の窓で不合格に数えない）。',
    '🔴 **閉じる前に**: この issue に**他の run の失敗**（別のコメント）が積まれていないかを見る。マーカーはワークフロー単位なので、本物の失敗も同じ issue に集まる。他の失敗が無ければ閉じてよい。',
  ],
  'rerun-red': [
    '🔴 **再実行も T-25 が赤だった。偶然の赤ではない。**',
    '床の引き直しの契機として調べる（計画 ADR-0097 決定 3）。**計画へ環流する**（計画 ADR-0118 決定 4）。この issue は閉じない。',
  ],
  'rerun-t25-green-other-red': [
    '**再実行で T-25 は合格したが、ほかの門が赤だった。** T-25 の赤は偶然の赤と見てよいが、**この issue は閉じない** —— ほかの門の失敗を先に調べる。',
  ],
  'rerun-unevaluable': [
    '**再実行は赤だったが、T-25 が判定まで届かなかった（評価不能・前提の失敗・前段の赤）。** 偶然の赤と確かめられていない。原因を調べ、必要なら手で 1 回だけ再実行する。',
  ],
  'rerun-cancelled': [
    '**再実行が完了しなかった（取り消し等）。** 同じコミットの確かめはまだである。後続の push の実行に取り消された可能性がある（同時実行は 1 本）。手で 1 回だけ再実行すること。',
  ],
};

function reportBody({ repo, run, outcome, first, second }) {
  const lines = [
    markerOf('report', run.id),
    `### T-25 の偶然の赤の確かめ: ${outcome}`,
    '',
    ...OUTCOME_TEXT[outcome],
    '',
    `| attempt | run | 結論 | T-25 | p | W |`,
    '| --- | --- | --- | --- | --- | --- |',
    `| 1（赤） | ${runUrl(repo, run.id, 1)} | failure | ${(first && first.verdict) || '—'} | ${fmtP(first && first.p)} | ${(first && first.w) ?? '—'} |`,
    `| 2（再実行） | ${runUrl(repo, run.id, 2)} | ${run.conclusion} | ${(second && second.verdict) || '—'} | ${fmtP(second && second.p)} | ${(second && second.w) ?? '—'} |`,
    '',
    `コミット \`${run.head_sha}\`（契機 \`${run.event}\`）。月次の要約は \`node scripts/t25-monthly-summary.js --month <YYYY-MM>\` が数える。`,
  ];
  if (outcome === 'rerun-cancelled' || outcome === 'rerun-unevaluable') {
    lines.push('', `手で再実行するなら（1 回だけ）: \`${manualCommand(repo, run.id)}\``);
  }
  return lines.join('\n');
}

// ---------------------------------------------------------------- GitHub（読み取りは GET・書き込みは 2 種だけ）

function readAttempt({ repo, runId, attempt, execFn }) {
  const run = ghGetJson(execFn, `repos/${repo}/actions/runs/${runId}/attempts/${attempt}`);
  const jobs = ghGetJson(execFn, `repos/${repo}/actions/runs/${runId}/attempts/${attempt}/jobs`, { per_page: 100 }).jobs || [];
  return { run, jobs };
}

function t25Of({ repo, jobs, execFn }) {
  const job = (jobs || []).find((j) => j.name === STACK_JOB_NAME);
  if (!job) return null;
  try {
    return parseT25FromJobLog(ghGet(execFn, `repos/${repo}/actions/jobs/${job.id}/logs`));
  } catch (e) {
    return null;
  }
}

function findIssue({ repo, execFn }) {
  for (let page = 1; page <= 10; page += 1) {
    const batch = ghGetJson(execFn, `repos/${repo}/issues`, { state: 'open', labels: 'ci-failure', per_page: 100, page });
    const hit = batch.find((i) => !i.pull_request && String(i.body || '').includes(ISSUE_MARKER));
    if (hit) return hit.number;
    if (batch.length < 100) break;
  }
  return null;
}

function hasComment({ repo, issue, marker, execFn }) {
  for (let page = 1; page <= 20; page += 1) {
    const batch = ghGetJson(execFn, `repos/${repo}/issues/${issue}/comments`, { per_page: 100, page });
    if (batch.some((c) => String(c.body || '').includes(marker))) return true;
    if (batch.length < 100) break;
  }
  return false;
}

/** 書き込み 1: issue へのコメント。 */
function postComment({ repo, issue, body, execFn }) {
  execFn('gh', ['api', '-X', 'POST', `repos/${repo}/issues/${issue}/comments`, '-f', `body=${body}`],
    { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
}

/** 書き込み 2: 失敗したジョブの再実行（同じ run の新しい attempt）。 */
function rerunFailed({ repo, runId, execFn }) {
  execFn('gh', ['run', 'rerun', String(runId), '--failed', '--repo', repo], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
}

/**
 * 判定して、`apply` なら実行する。戻り値は終了コードと記録。
 * @returns {{code:number, decision:object, log:string[]}}
 */
function execute({ repo, runId, attempt, apply, execFn = execFileSync }) {
  const log = [];
  const cur = readAttempt({ repo, runId, attempt, execFn });
  const prev = attempt === 2 ? readAttempt({ repo, runId, attempt: 1, execFn }) : null;
  const decision = decide({ run: cur.run, jobs: cur.jobs, prevJobs: prev && prev.jobs });
  log.push(`判定: ${decision.action} —— ${decision.reason}`);
  if (decision.action === 'none') return { code: 0, decision, log };

  if (decision.action === 'rerun') {
    const t25 = t25Of({ repo, jobs: cur.jobs, execFn });
    const issue = findIssue({ repo, execFn });
    const marker = markerOf('rerun', runId);
    if (!apply) {
      log.push(`（--apply なし）再実行する: ${manualCommand(repo, runId)} / コメント先 issue ${issue ? `#${issue}` : '（見つからない）'}`);
      return { code: 0, decision, log };
    }
    // 二重の抑止: 最新の attempt が 1 のままか（誰かが先に再実行していないか）、同じ run の再実行の記録が無いか。
    const latest = ghGetJson(execFn, `repos/${repo}/actions/runs/${runId}`);
    if (latest.run_attempt !== 1) {
      log.push(`最新の attempt が ${latest.run_attempt}: すでに再実行されている。何もしない`);
      return { code: 0, decision, log };
    }
    if (issue && hasComment({ repo, issue, marker, execFn })) {
      log.push(`issue #${issue} に再実行の記録がある。何もしない`);
      return { code: 0, decision, log };
    }
    let error = null;
    try {
      rerunFailed({ repo, runId, execFn });
      log.push(`再実行した: ${manualCommand(repo, runId)}`);
    } catch (e) {
      error = String((e && (e.stderr || e.message)) || e);
      log.push(`🔴 再実行を起こせなかった: ${error.split('\n')[0]}`);
    }
    if (issue) {
      postComment({ repo, issue, body: rerunRequestedBody({ repo, run: cur.run, t25, error }), execFn });
      log.push(`issue #${issue} へ書いた`);
    } else {
      log.push('🔴 マーカーの issue が開いていない（起票に失敗した？）。コメントは書いていない');
    }
    return { code: error ? 1 : 0, decision, log };
  }

  // report（attempt 2）
  const first = t25Of({ repo, jobs: prev.jobs, execFn });
  const second = t25Of({ repo, jobs: cur.jobs, execFn });
  const outcome = rerunOutcome(cur.run, second);
  log.push(`再実行の結果: ${outcome}（T-25 attempt 1 p=${fmtP(first && first.p)} / attempt 2 p=${fmtP(second && second.p)}）`);
  const issue = findIssue({ repo, execFn });
  if (!apply) {
    log.push(`（--apply なし）issue ${issue ? `#${issue}` : '（見つからない）'} へ結果を書く`);
    return { code: 0, decision: { ...decision, outcome }, log };
  }
  if (!issue) {
    log.push('🔴 マーカーの issue が開いていない（先に閉じられた？）。結果は書いていない');
    return { code: 1, decision: { ...decision, outcome }, log };
  }
  const marker = markerOf('report', runId);
  if (hasComment({ repo, issue, marker, execFn })) {
    log.push(`issue #${issue} に結果の記録がある。何もしない`);
    return { code: 0, decision: { ...decision, outcome }, log };
  }
  postComment({ repo, issue, body: reportBody({ repo, run: cur.run, outcome, first, second }), execFn });
  log.push(`issue #${issue} へ書いた`);
  return { code: 0, decision: { ...decision, outcome }, log };
}

// ---------------------------------------------------------------- 自己試験

function selfTest() {
  const assert = require('assert');
  let n = 0;
  const ok = (name, fn) => { fn(); n += 1; console.log(`  ok  ${name}`); };

  const RESET = '🔴 Gate — パスワードリセットの送出とメール本文（#1144）';
  const stepsOf = (over = {}) => {
    const base = [
      ['Self-test the password-reset mail gate', 'success'],
      ['🔴 Gate — the stack is actually up', 'success'],
      [RESET, 'failure'],
      ['ABAC ポリシーの投入を確定させる（冪等・失敗を握り潰さない）', 'success'],
      ['検索検証用文書の投入を確定させる（冪等・失敗を握り潰さない）', 'success'],
      ['🔴 Gate — ABAC の正常系と検索の命中が観測できる（#972 /', 'success'],
      ['🔴 Gate — ログイン経路の存在秘匿（#1245 PR-0）', 'success'],
      [CANDIDATE_STEP, 'success'],
      ['Dump cluster state (診断用・失敗時のみ)', 'success'],
      ['Tear down', 'success'],
    ];
    return base.map(([name, c]) => ({ name, conclusion: Object.prototype.hasOwnProperty.call(over, name) ? over[name] : c }));
  };
  const jobsOf = (over = {}, { stack = 'failure', report = 'success', attempt = 1 } = {}) => [
    { id: 900 + attempt, name: STACK_JOB_NAME, conclusion: stack, steps: stepsOf(over) },
    { id: 800 + attempt, name: 'report-failure / report', conclusion: report, steps: [] },
  ];
  const runOf = (over = {}) => ({
    id: 42, path: '.github/workflows/integration-stack.yml', status: 'completed', event: 'push',
    run_attempt: 1, conclusion: 'failure', head_sha: 'abc123', head_branch: 'develop',
    head_repository: { full_name: 'o/r' }, repository: { full_name: 'o/r' }, ...over,
  });
  const greenJobs = (attempt = 2) => jobsOf({ [RESET]: 'success', [CANDIDATE_STEP]: 'skipped', 'Dump cluster state (診断用・失敗時のみ)': 'skipped' }, { stack: 'success', report: 'skipped', attempt });

  // ---- 判定 --------------------------------------------------------------------------------
  ok('🔴 attempt 1 で T-25 だけが赤（ほかの門はすべて緑）なら再実行する（push / schedule）', () => {
    assert.strictEqual(decide({ run: runOf(), jobs: jobsOf() }).action, 'rerun');
    assert.strictEqual(decide({ run: runOf({ event: 'schedule' }), jobs: jobsOf() }).action, 'rerun');
  });
  ok('🔴 ほかの門も赤なら再実行しない（手順「T-25 だけの赤」が走っていても、手順の結論を数え直して止める）', () => {
    for (const other of ['🔴 Gate — ABAC の正常系と検索の命中が観測できる（#972 /', '🔴 Gate — ログイン経路の存在秘匿（#1245 PR-0）', 'ABAC ポリシーの投入を確定させる（冪等・失敗を握り潰さない）']) {
      const d = decide({ run: runOf(), jobs: jobsOf({ [other]: 'failure' }) });
      assert.strictEqual(d.action, 'none', `${other} が赤なのに再実行する`);
    }
    // 後段の門が飛ばされた（skipped）＝緑と確かめられていない
    const skipped = decide({ run: runOf(), jobs: jobsOf({ '🔴 Gate — ABAC の正常系と検索の命中が観測できる（#972 /': 'skipped' }) });
    assert.strictEqual(skipped.action, 'none');
  });
  ok('🔴 パスワードリセットの門の中に T-25 以外の失敗がある（手順「T-25 だけの赤」が skipped）なら再実行しない', () => {
    assert.strictEqual(decide({ run: runOf(), jobs: jobsOf({ [CANDIDATE_STEP]: 'skipped' }) }).action, 'none');
  });
  ok('手順「T-25 だけの赤」が無い（古い定義の run）・スタックの門が赤・ほかのジョブが赤は再実行しない', () => {
    const noMarker = jobsOf();
    noMarker[0].steps = noMarker[0].steps.filter((s) => s.name !== CANDIDATE_STEP);
    assert.strictEqual(decide({ run: runOf(), jobs: noMarker }).action, 'none');
    assert.strictEqual(decide({ run: runOf(), jobs: jobsOf({ '🔴 Gate — the stack is actually up': 'failure', [RESET]: 'skipped' }) }).action, 'none');
    assert.strictEqual(decide({ run: runOf(), jobs: jobsOf({}, { report: 'failure' }) }).action, 'none');
  });
  ok('手動実行・緑の run・終わっていない run・別のワークフローは再実行しない', () => {
    assert.strictEqual(decide({ run: runOf({ event: 'workflow_dispatch' }), jobs: jobsOf() }).action, 'none');
    assert.strictEqual(decide({ run: runOf({ conclusion: 'success' }), jobs: jobsOf() }).action, 'none');
    assert.strictEqual(decide({ run: runOf({ status: 'in_progress' }), jobs: jobsOf() }).action, 'none');
    assert.strictEqual(decide({ run: runOf({ path: '.github/workflows/integration.yml' }), jobs: jobsOf() }).action, 'none');
  });
  ok('🔴 #1617 監査 R1: develop 以外のブランチ・フォーク（head のリポジトリが違う）の run は再実行も記録もしない', () => {
    for (const branch of ['feature/x', 'main', '', undefined]) {
      assert.strictEqual(decide({ run: runOf({ head_branch: branch }), jobs: jobsOf() }).action, 'none', `ブランチ ${branch}`);
      assert.strictEqual(decide({ run: runOf({ head_branch: branch, run_attempt: 2 }), jobs: jobsOf({}, { attempt: 2 }), prevJobs: jobsOf() }).action, 'none', `attempt 2 / ブランチ ${branch}`);
    }
    for (const head of [{ full_name: 'someone/fork' }, null, {}]) {
      assert.strictEqual(decide({ run: runOf({ head_repository: head }), jobs: jobsOf() }).action, 'none', `head ${JSON.stringify(head)}`);
    }
    assert.strictEqual(decide({ run: runOf({ repository: null }), jobs: jobsOf() }).action, 'none', '基のリポジトリが読めない');
  });
  ok('🔴 再実行の再実行はしない: attempt 2 は赤でも再実行せず結果を書くだけ・attempt 3 以降は何もしない', () => {
    const d2 = decide({ run: runOf({ run_attempt: 2 }), jobs: jobsOf({}, { attempt: 2 }), prevJobs: jobsOf() });
    assert.strictEqual(d2.action, 'report');
    for (const a of [3, 4, 9]) {
      const d = decide({ run: runOf({ run_attempt: a }), jobs: jobsOf({}, { attempt: a }), prevJobs: jobsOf() });
      assert.strictEqual(d.action, 'none', `attempt ${a}`);
    }
  });
  ok('attempt 2 でも attempt 1 が T-25 だけの赤でなければ（人の再実行）結果を書かない', () => {
    const d = decide({ run: runOf({ run_attempt: 2, conclusion: 'success' }), jobs: greenJobs(), prevJobs: jobsOf({ [CANDIDATE_STEP]: 'skipped' }) });
    assert.strictEqual(d.action, 'none');
  });
  ok('再実行の結果の分類: 合格＝偶然の赤／T-25 がまた赤／T-25 は合格でほかが赤／判定に届かない／取り消し', () => {
    assert.strictEqual(rerunOutcome({ conclusion: 'success' }, { verdict: '合格' }), 'chance-red');
    assert.strictEqual(rerunOutcome({ conclusion: 'failure' }, { verdict: '不合格' }), 'rerun-red');
    assert.strictEqual(rerunOutcome({ conclusion: 'failure' }, { verdict: '合格' }), 'rerun-t25-green-other-red');
    assert.strictEqual(rerunOutcome({ conclusion: 'failure' }, { verdict: '評価不能' }), 'rerun-unevaluable');
    assert.strictEqual(rerunOutcome({ conclusion: 'failure' }, null), 'rerun-unevaluable');
    assert.strictEqual(rerunOutcome({ conclusion: 'cancelled' }, null), 'rerun-cancelled');
  });
  ok('文面: 偶然の赤は「閉じる前に他の run の失敗を見る」を言い、再実行も赤は計画への環流を言う・両方の p と W を載せる', () => {
    const run = runOf({ run_attempt: 2, conclusion: 'success' });
    const body = reportBody({ repo: 'o/r', run, outcome: 'chance-red', first: { verdict: '不合格', p: 0.0094, w: 713 }, second: { verdict: '合格', p: 0.6165, w: 563 } });
    for (const needle of ['t25-chance-red:report:42', '偶然の赤', '閉じる前に', '他の run の失敗', '0.0094', '713', '0.6165', '563', '/attempts/1', '/attempts/2']) {
      assert.ok(body.includes(needle), `文面に「${needle}」が無い`);
    }
    assert.ok(reportBody({ repo: 'o/r', run: runOf({ run_attempt: 2 }), outcome: 'rerun-red', first: null, second: null }).includes('計画へ環流'));
    const req = rerunRequestedBody({ repo: 'o/r', run: runOf(), t25: { p: 0.0094, w: 713 } });
    assert.ok(req.includes('t25-chance-red:rerun:42') && req.includes('再実行の再実行はしない'));
    assert.ok(rerunRequestedBody({ repo: 'o/r', run: runOf(), t25: null, error: 'HTTP 403' }).includes('gh run rerun 42 --failed --repo o/r'));
  });

  // ---- 実行（gh を差し替える）----------------------------------------------------------------
  const ts = '2026-09-27T00:00:00.0000000Z ';
  const logOf = (verdict, p, w) => [
    `${ts}##[group]Run node scripts/check-password-reset-mail.js --live`,
    `${ts}  ISTIO: 1`,
    `${ts}[check-password-reset-mail] T-25 所要時間（判定: ${verdict}）: 反復 3 回 × 片側 12 標本`,
    `${ts}  順位和検定（両側・正確法）: 暖機を除く 2 反復をまとめて 実在 24 / 非実在 24 標本 / 実在側の順位和 W=${w}（期待値 588）・U=1 / p=${p}（有意水準 0.01）/ 中央値で遅い側 実在`,
  ].join('\n');
  /** 状態を持つ gh のスタブ。呼び出しを記録し、書き込みは rerun と POST comments だけを許す。 */
  const makeGh = ({ attempts, latestAttempt = 1, comments = [], issueOpen = true, rerunThrows = false }) => {
    const calls = [];
    const fn = (cmd, args) => {
      assert.strictEqual(cmd, 'gh');
      calls.push(args.join(' '));
      if (args[0] === 'run') {
        assert.deepStrictEqual(args, ['run', 'rerun', '42', '--failed', '--repo', 'o/r'], `再実行の引数が違う: ${args.join(' ')}`);
        if (rerunThrows) { const e = new Error('HTTP 403: Resource not accessible by integration'); throw e; }
        return '';
      }
      assert.strictEqual(args[0], 'api');
      if (args[2] === 'POST') {
        assert.ok(/^repos\/o\/r\/issues\/7\/comments$/.test(args[3]), `コメント以外へ書いた: ${args.join(' ')}`);
        comments.push({ body: args[5].replace(/^body=/, '') });
        return '{}';
      }
      assert.deepStrictEqual(args.slice(1, 3), ['-X', 'GET'], `GET 以外の読み取り: ${args.join(' ')}`);
      const p = args[3];
      let m;
      if ((m = /actions\/runs\/42\/attempts\/(\d)\/jobs$/.exec(p))) return JSON.stringify({ jobs: attempts[m[1]].jobs });
      if ((m = /actions\/runs\/42\/attempts\/(\d)$/.exec(p))) return JSON.stringify(attempts[m[1]].run);
      if (/actions\/runs\/42$/.test(p)) return JSON.stringify({ id: 42, run_attempt: latestAttempt });
      if ((m = /actions\/jobs\/(\d+)\/logs$/.exec(p))) {
        const a = Object.values(attempts).find((x) => x.jobs[0].id === Number(m[1]));
        return a.log;
      }
      if (/repos\/o\/r\/issues$/.test(p)) {
        return JSON.stringify(issueOpen ? [{ number: 3, body: 'other' }, { number: 7, body: `${ISSUE_MARKER}\n**integration-stack が失敗した。**` }] : []);
      }
      if (/issues\/7\/comments$/.test(p)) return JSON.stringify(comments);
      throw new Error(`想定外の呼び出し: ${args.join(' ')}`);
    };
    return { fn, calls, comments };
  };
  const a1 = { run: runOf(), jobs: jobsOf(), log: logOf('不合格', '0.0094', '713') };

  ok('🔴 実行: T-25 だけの赤で `gh run rerun 42 --failed` を 1 回だけ呼び、issue へ記録を 1 件書く', () => {
    const gh = makeGh({ attempts: { 1: a1 } });
    const r = execute({ repo: 'o/r', runId: 42, attempt: 1, apply: true, execFn: gh.fn });
    assert.strictEqual(r.code, 0, r.log.join('\n'));
    assert.strictEqual(gh.calls.filter((c) => c.startsWith('run rerun')).length, 1);
    assert.strictEqual(gh.comments.length, 1);
    assert.ok(gh.comments[0].body.includes('t25-chance-red:rerun:42') && gh.comments[0].body.includes('p=0.0094'));
  });
  ok('🔴 実行: 同じ run がすでに再実行されている（最新の attempt ≠ 1）・記録が既にある なら再実行しない', () => {
    const g1 = makeGh({ attempts: { 1: a1 }, latestAttempt: 2 });
    execute({ repo: 'o/r', runId: 42, attempt: 1, apply: true, execFn: g1.fn });
    assert.strictEqual(g1.calls.filter((c) => c.startsWith('run rerun')).length, 0);
    const g2 = makeGh({ attempts: { 1: a1 }, comments: [{ body: markerOf('rerun', 42) }] });
    execute({ repo: 'o/r', runId: 42, attempt: 1, apply: true, execFn: g2.fn });
    assert.strictEqual(g2.calls.filter((c) => c.startsWith('run rerun')).length, 0);
  });
  ok('実行: --apply なしは読むだけ（再実行もコメントもしない）', () => {
    const gh = makeGh({ attempts: { 1: a1 } });
    const r = execute({ repo: 'o/r', runId: 42, attempt: 1, apply: false, execFn: gh.fn });
    assert.strictEqual(r.decision.action, 'rerun');
    assert.ok(!gh.calls.some((c) => c.startsWith('run ') || c.includes('-X POST')), gh.calls.join('\n'));
  });
  ok('🔴 実行: 再実行を起こせなければ、手で打つコマンドを issue へ書いて終了コード 1（黙って緑にしない）', () => {
    const gh = makeGh({ attempts: { 1: a1 }, rerunThrows: true });
    const r = execute({ repo: 'o/r', runId: 42, attempt: 1, apply: true, execFn: gh.fn });
    assert.strictEqual(r.code, 1);
    assert.ok(gh.comments[0].body.includes('gh run rerun 42 --failed --repo o/r'));
  });
  ok('🔴 実行: ほかの門も赤の run は再実行もコメントもしない', () => {
    const gh = makeGh({ attempts: { 1: { ...a1, jobs: jobsOf({ '🔴 Gate — ログイン経路の存在秘匿（#1245 PR-0）': 'failure' }) } } });
    const r = execute({ repo: 'o/r', runId: 42, attempt: 1, apply: true, execFn: gh.fn });
    assert.strictEqual(r.decision.action, 'none');
    assert.ok(!gh.calls.some((c) => c.startsWith('run ') || c.includes('-X POST')));
  });
  ok('🔴 実行: attempt 2 の合格で「偶然の赤」を 1 回だけ書き、再実行は呼ばない（2 回目の起動では書かない）', () => {
    const a2 = { run: runOf({ run_attempt: 2, conclusion: 'success' }), jobs: greenJobs(), log: logOf('合格', '0.6165', '563') };
    const gh = makeGh({ attempts: { 1: a1, 2: a2 }, latestAttempt: 2 });
    const r = execute({ repo: 'o/r', runId: 42, attempt: 2, apply: true, execFn: gh.fn });
    assert.strictEqual(r.decision.outcome, 'chance-red');
    assert.strictEqual(gh.comments.length, 1);
    assert.ok(gh.comments[0].body.includes('0.0094') && gh.comments[0].body.includes('0.6165'));
    assert.ok(!gh.calls.some((c) => c.startsWith('run ')), '再実行の再実行を呼んだ');
    execute({ repo: 'o/r', runId: 42, attempt: 2, apply: true, execFn: gh.fn });
    assert.strictEqual(gh.comments.length, 1, '同じ結果を 2 回書いた');
  });
  ok('実行: attempt 2 の T-25 の赤は「再実行も赤」を書く・issue が閉じられていれば終了コード 1', () => {
    const a2 = { run: runOf({ run_attempt: 2 }), jobs: jobsOf({}, { attempt: 2 }), log: logOf('不合格', '1.00e-10', '900') };
    const gh = makeGh({ attempts: { 1: a1, 2: a2 }, latestAttempt: 2 });
    const r = execute({ repo: 'o/r', runId: 42, attempt: 2, apply: true, execFn: gh.fn });
    assert.strictEqual(r.decision.outcome, 'rerun-red');
    assert.ok(gh.comments[0].body.includes('再実行も T-25 が赤'));
    const closed = makeGh({ attempts: { 1: a1, 2: a2 }, latestAttempt: 2, issueOpen: false });
    assert.strictEqual(execute({ repo: 'o/r', runId: 42, attempt: 2, apply: true, execFn: closed.fn }).code, 1);
  });

  console.log(`[t25-rerun-on-chance-red] self-test OK: ${n} 件`);
}

// ---------------------------------------------------------------- main

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) { selfTest(); return; }
  const args = { repo: DEFAULT_REPO, runId: null, attempt: null, apply: false };
  for (let i = 0; i < argv.length; i += 1) {
    const a = argv[i];
    if (a === '--run-id') args.runId = Number(argv[++i]);
    else if (a === '--attempt') args.attempt = Number(argv[++i]);
    else if (a === '--repo') args.repo = argv[++i];
    else if (a === '--apply') args.apply = true;
    else { console.error(`[t25-rerun-on-chance-red] 未知の引数: ${a}`); process.exit(2); }
  }
  if (!Number.isInteger(args.runId) || !Number.isInteger(args.attempt) || args.attempt < 1) {
    console.error('[t25-rerun-on-chance-red] --run-id <id> --attempt <n> を与える（--self-test は GitHub へ出ない）');
    process.exit(2);
  }
  const r = execute(args);
  for (const l of r.log) console.log(`[t25-rerun-on-chance-red] ${l}`);
  if (process.env.GITHUB_STEP_SUMMARY) {
    fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, `### T-25 の偶然の赤の再実行\n\n${r.log.map((l) => `- ${l}`).join('\n')}\n`);
  }
  process.exit(r.code);
}

if (require.main === module) main();

module.exports = {
  candidateOf,
  decide,
  rerunOutcome,
  reportBody,
  rerunRequestedBody,
  execute,
  CANDIDATE_STEP,
  ISSUE_MARKER,
  RERUN_EVENTS,
  RERUN_BRANCH,
};
