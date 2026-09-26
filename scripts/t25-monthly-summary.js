#!/usr/bin/env node
'use strict';
/*
 * t25-monthly-summary.js
 * SC-15 / NFR-13 / 計画 ADR-0118 決定 4・ADR-0113 フォローアップ 2 / IADR-0470（#1617）:
 * **integration-stack の T-25（リセット申請の所要時間の順位和検定）の p の分布を、月ごとに要約する。**
 *
 * ## 何を数えるか（ADR-0118 決定 4 の 6 項目）
 *
 * - 検査まで届いた件数 n（p が出た attempt。`合格` / `不合格` / `評価不能` のどれでも p は出る）
 * - 不合格の件数（うち偶然の赤＝同じコミットの次の観測が `合格`／再実行も赤／再実行なし）
 * - 評価不能の件数
 * - p の中央値
 * - 一様分布からの KS 距離 D と、有意水準 5% の目安 1.36/√n
 * - 計画へ環流するか（D が目安を超えた／再実行も赤が 1 件以上）
 *
 * ## 🔴 GitHub だけを読む。書かない・稼働クラスタへ触れない
 *
 * 入力は GitHub の Actions の API（`gh api` の GET）だけである —— run の一覧、attempt ごとのジョブ、ジョブのログ。
 * 自己試験は `gh` の呼び出しを差し替えて **GET 以外を呼ばないこと**を固定する。
 *
 * ## 観測の単位は attempt である
 *
 * 同じ run の再実行（attempt 2）も、帰無仮説のもとでは独立な 1 標本である。したがって n に数える。
 * 「偶然の赤」は、赤の観測の**同じコミットの次の観測**（同じ run の次の attempt か、同じコミットの後の run）が `合格` のもの。
 * 次の観測も赤なら「再実行も赤」（床の引き直しの契機・計画へ環流。ADR-0118 決定 2・4）。次の観測が無ければ「再実行なし」。
 * 次の観測の側は「赤の再実行」として数え、それ自身の次の観測は見ない（再実行の再実行はしない）。
 *
 * ## 数えないもの（内訳には出す）
 *
 * - 判定式が順位和検定になる前の run（`RANK_SUM_SINCE` より前。#1541 のマージ `77898402` の run 36217595485）
 * - 検査まで届かなかった attempt（取り消し・前段の門の赤・門の前提の失敗）
 * - 床の無い比較実行（手動実行で `istio=false`。手順の env の `ISTIO` が空。床は Istio のエッジにしか無い）
 * - 判定の前提で落ちた `不合格`（反復・標本数・格子。p が出ない）
 *
 * 使い方:
 *   node scripts/t25-monthly-summary.js --month 2026-09            # 要約を出す（GitHub を読むだけ）
 *   node scripts/t25-monthly-summary.js --month 2026-09 --json     # 同じ内容を JSON で
 *   node scripts/t25-monthly-summary.js --month 2026-09 --repo <owner/name>
 *   node scripts/t25-monthly-summary.js --self-test                # 読み取りの器と集計の自己試験（GitHub へ出ない）
 */
const { execFileSync } = require('child_process');
const { TIMING_ALPHA, TIMING_VERDICT } = require('./check-password-reset-mail.js');

const DEFAULT_REPO = 'endazon/microservices-platform';
const WORKFLOW_FILE = 'integration-stack.yml';
/** integration-stack.yml のジョブの表示名（`name:`）。 */
const STACK_JOB_NAME = 'integration-stack';
/** パスワードリセットの門の手順名の先頭（API は長い手順名を途中で切るので前方一致で引く）。 */
const RESET_GATE_PREFIX = '🔴 Gate — パスワードリセット';
/** 判定式が順位和検定になった最初の run（36217595485・#1541 のマージ `77898402`）の作成時刻。これより前は数えない。 */
const RANK_SUM_SINCE = '2026-09-26T04:21:42Z';
/** 一様分布との KS 検定の有意水準 5% の目安の係数（D > 1.36/√n。ADR-0118 決定 4）。 */
const KS_COEFFICIENT = 1.36;

// ---------------------------------------------------------------- ログの読み取り（純関数）

const stripTimestamp = (line) => line.replace(/^\d{4}-\d{2}-\d{2}T[\d:.]+Z ?/, '');

/**
 * ジョブのログから、パスワードリセットの門（`--live`）の手順の区間だけを読み、T-25 の結果を取り出す。**純関数**。
 *
 * 🔴 **区間の外を読まない。** 同じログの自己試験の手順（`--self-test`）も「順位和検定」を含む行を出す。
 *
 * @returns {{stepRan:boolean, reached:boolean, verdict:(string|null), istio:(string|null),
 *            p:(number|null), w:(number|null), expectedW:(number|null), slower:(string|null)}}
 */
function parseT25FromJobLog(text) {
  const lines = String(text || '').split(/\r?\n/).map(stripTimestamp);
  const empty = { stepRan: false, reached: false, verdict: null, istio: null, p: null, w: null, expectedW: null, slower: null };
  // 🔴 `--live` を要件にしない —— #1550 より前の run は引数なしで呼んでいた（`--self-test` の手順だけを外す）。
  const start = lines.findIndex((l) => /^##\[group\]Run node scripts\/check-password-reset-mail\.js(?![^\n]*--self-test)/.test(l));
  if (start < 0) return empty;
  let end = lines.length;
  for (let i = start + 1; i < lines.length; i += 1) {
    if (/^##\[group\]Run /.test(lines[i])) { end = i; break; }
  }
  const section = lines.slice(start, end);
  const out = { ...empty, stepRan: true };
  for (const l of section) {
    const m = /^\s+ISTIO:\s*(.*?)\s*$/.exec(l);
    if (m) { out.istio = m[1]; break; }
  }
  for (const l of section) {
    const m = /T-25 所要時間（判定: (合格|不合格|評価不能)）/.exec(l);
    if (m) { out.verdict = m[1]; break; }
  }
  for (const l of section) {
    const m = /順位和検定（両側・正確法[^）]*）:.*?W=([\d.]+)（期待値 ([\d.]+)）・U=[\d.]+ \/ p=([0-9.eE+-]+)（有意水準 [\d.]+）(?:\s*\/\s*中央値で遅い側 (\S+))?/.exec(l);
    if (m) {
      out.w = Number(m[1]);
      out.expectedW = Number(m[2]);
      out.p = Number(m[3]);
      out.slower = m[4] || null;
      break;
    }
  }
  out.reached = out.verdict !== null;
  return out;
}

// ---------------------------------------------------------------- 集計（純関数）

/**
 * 1 つの観測（attempt）を数える側へ振り分ける。**純関数**。
 * @returns {'beforeRankSum'|'inProgress'|'logUnavailable'|'notReached'|'noFloor'|'judgementPrecondition'|'evaluated'}
 */
function categorize(o, { since = RANK_SUM_SINCE } = {}) {
  if (Date.parse(o.createdAt) < Date.parse(since)) return 'beforeRankSum';
  if (o.inProgress) return 'inProgress';
  if (o.logUnavailable) return 'logUnavailable';
  if (!o.reached) return 'notReached';
  if (o.istio === '') return 'noFloor';
  if (o.p === null || !Number.isFinite(o.p)) return 'judgementPrecondition';
  return 'evaluated';
}

const median = (xs) => {
  const s = xs.slice().sort((a, b) => a - b);
  if (s.length === 0) return null;
  const mid = Math.floor(s.length / 2);
  return s.length % 2 ? s[mid] : (s[mid - 1] + s[mid]) / 2;
};

/** 一様分布 U(0,1) との KS 距離 D（両側の上限）。**純関数**。0 件は null。 */
function ksDistanceUniform(ps) {
  const s = ps.slice().sort((a, b) => a - b);
  const n = s.length;
  if (n === 0) return null;
  let d = 0;
  s.forEach((p, i) => { d = Math.max(d, (i + 1) / n - p, p - i / n); });
  return d;
}

/**
 * 赤の観測ごとに、同じコミットの次の観測で「偶然の赤／再実行も赤／再実行なし」を決める。**純関数**。
 * @param {object[]} obs  `category` を付けた観測
 * @returns {{red:object, rerun:(object|null), outcome:'chanceRed'|'rerunRed'|'rerunUnevaluable'|'unconfirmed'}[]}
 */
function rerunOutcomes(obs) {
  const isRed = (o) => o && o.category === 'evaluated' && o.verdict === TIMING_VERDICT.FAIL;
  const bySha = new Map();
  for (const o of obs) {
    if (o.category !== 'evaluated' && o.category !== 'judgementPrecondition') continue;
    if (!bySha.has(o.sha)) bySha.set(o.sha, []);
    bySha.get(o.sha).push(o);
  }
  const at = (o) => Date.parse(o.startedAt || o.createdAt);
  const out = [];
  for (const group of bySha.values()) {
    group.sort((a, b) => at(a) - at(b) || a.runId - b.runId || a.attempt - b.attempt);
    group.forEach((o, i) => {
      if (!isRed(o)) return;
      if (i > 0 && isRed(group[i - 1])) return; // 赤の再実行（前の赤の側で数えた）
      const next = group[i + 1] || null;
      let outcome = 'unconfirmed';
      if (next && next.verdict === TIMING_VERDICT.PASS) outcome = 'chanceRed';
      else if (isRed(next)) outcome = 'rerunRed';
      else if (next) outcome = 'rerunUnevaluable';
      out.push({ red: o, rerun: next, outcome });
    });
  }
  return out.sort((a, b) => at(a.red) - at(b.red));
}

/** 観測の列から月次の要約を作る。**純関数**。 */
function summarize(observations, { since = RANK_SUM_SINCE } = {}) {
  const obs = observations.map((o) => ({ ...o, category: categorize(o, { since }) }));
  const counts = {};
  for (const o of obs) counts[o.category] = (counts[o.category] || 0) + 1;
  const evaluated = obs.filter((o) => o.category === 'evaluated');
  const ps = evaluated.map((o) => o.p);
  const n = ps.length;
  const reds = rerunOutcomes(obs);
  const count = (k) => reds.filter((r) => r.outcome === k).length;
  const d = ksDistanceUniform(ps);
  const threshold = n > 0 ? KS_COEFFICIENT / Math.sqrt(n) : null;
  const ksExceeds = d !== null && d > threshold;
  const rerunRed = count('rerunRed');
  return {
    runs: new Set(observations.map((o) => o.runId)).size,
    attempts: observations.length,
    counts,
    n,
    failures: evaluated.filter((o) => o.verdict === TIMING_VERDICT.FAIL).length,
    chanceRed: count('chanceRed'),
    rerunRed,
    rerunUnevaluable: count('rerunUnevaluable'),
    unconfirmed: count('unconfirmed'),
    inconclusive: evaluated.filter((o) => o.verdict === TIMING_VERDICT.INCONCLUSIVE).length,
    medianP: median(ps),
    belowHalf: ps.filter((p) => p < 0.5).length,
    ks: d,
    ksThreshold: threshold,
    ksExceeds,
    feedbackToPlanning: ksExceeds || rerunRed > 0,
    reds: reds.map((r) => ({
      runId: r.red.runId, attempt: r.red.attempt, event: r.red.event, sha: r.red.sha, p: r.red.p, w: r.red.w, slower: r.red.slower,
      outcome: r.outcome,
      rerun: r.rerun ? { runId: r.rerun.runId, attempt: r.rerun.attempt, verdict: r.rerun.verdict, p: r.rerun.p, w: r.rerun.w } : null,
    })),
  };
}

const OUTCOME_LABEL = {
  chanceRed: '偶然の赤（同じコミットの次の観測が合格）',
  rerunRed: '🔴 再実行も赤（床の引き直しの契機・計画へ環流）',
  rerunUnevaluable: '再実行が判定に届かない（評価不能・前提の失敗）',
  unconfirmed: '再実行なし（同じコミットの次の観測が無い）',
};
const CATEGORY_LABEL = {
  beforeRankSum: '判定式が順位和検定になる前',
  inProgress: '実行中',
  logUnavailable: 'ログを読めない',
  notReached: '検査まで届かず（取り消し・前段の赤・前提の失敗）',
  noFloor: '床の無い比較実行（ISTIO が空）',
  judgementPrecondition: '判定の前提で不合格（p が出ない）',
  evaluated: '検査まで届いた（p あり）',
};

const fmtP = (p) => (p === null || p === undefined ? '—' : (p >= 1e-4 ? p.toFixed(4) : p.toExponential(2)));
const fmt3 = (x) => (x === null || x === undefined ? '—' : x.toFixed(3));

/** 人向けの要約（IADR の追記へそのまま貼れる形）。**純関数**。 */
function formatSummary(s, { month, repo, since, asOf }) {
  const lines = [
    `T-25 の月次の要約 ${month}（${repo} / ${WORKFLOW_FILE}・集計 ${asOf}）`,
    `- 対象: ${month} に作られた run の全 attempt（判定式の変更 ${since} より前は数えない）。run ${s.runs} 件 / attempt ${s.attempts} 件`,
    `- 内訳: ${Object.keys(CATEGORY_LABEL).filter((k) => s.counts[k]).map((k) => `${CATEGORY_LABEL[k]} ${s.counts[k]}`).join(' / ') || '（なし）'}`,
    `- 検査まで届いた件数 n = ${s.n}`,
    `- 不合格 ${s.failures}（偶然の赤 ${s.chanceRed} / 再実行も赤 ${s.rerunRed} / 再実行が判定に届かない ${s.rerunUnevaluable} / 再実行なし ${s.unconfirmed}）`,
    `- 評価不能 ${s.inconclusive}`,
    `- p の中央値 ${fmtP(s.medianP)}（p < 0.5 は ${s.belowHalf}/${s.n}）`,
    `- 一様分布からの KS 距離 D = ${fmt3(s.ks)}（有意水準 5% の目安 ${KS_COEFFICIENT}/√n = ${fmt3(s.ksThreshold)}）→ ${s.ks === null ? '判定できない（n = 0）' : (s.ksExceeds ? '🔴 超えた' : '超えていない')}`,
    `- 計画への環流: ${s.feedbackToPlanning ? '🔴 要る' : '要らない'}（KS が目安を超えた: ${s.ksExceeds ? 'はい' : 'いいえ'} / 再実行も赤: ${s.rerunRed} 件）`,
  ];
  if (s.reds.length) {
    lines.push('- 不合格の一覧:');
    for (const r of s.reds) {
      const rerun = r.rerun ? ` → 次の観測 run ${r.rerun.runId} attempt ${r.rerun.attempt}: ${r.rerun.verdict} p=${fmtP(r.rerun.p)} W=${r.rerun.w ?? '—'}` : '';
      lines.push(`  - run ${r.runId} attempt ${r.attempt}（${r.event} \`${String(r.sha).slice(0, 8)}\`）p=${fmtP(r.p)} W=${r.w}（遅い側 ${r.slower || '—'}）: ${OUTCOME_LABEL[r.outcome]}${rerun}`);
    }
  }
  return lines.join('\n');
}

// ---------------------------------------------------------------- GitHub の読み取り（GET だけ）

/** `gh api` を GET で呼ぶ（-X GET を必ず付ける。書き込みの口をこの関数から作らない）。 */
function ghGet(execFn, pathAndQuery, fields = {}) {
  const args = ['api', '-X', 'GET', pathAndQuery];
  for (const [k, v] of Object.entries(fields)) args.push('-f', `${k}=${v}`);
  return execFn('gh', args, { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'] });
}
const ghGetJson = (execFn, p, f) => JSON.parse(ghGet(execFn, p, f));

/** 月の範囲（UTC の日付。GitHub の `created` の書式）。 */
function monthRange(month) {
  const m = /^(\d{4})-(\d{2})$/.exec(String(month || ''));
  if (!m) throw new Error(`--month は YYYY-MM で与える: ${month}`);
  const y = Number(m[1]);
  const mo = Number(m[2]);
  if (mo < 1 || mo > 12) throw new Error(`--month の月が範囲外: ${month}`);
  const last = new Date(Date.UTC(y, mo, 0)).getUTCDate();
  return { from: `${m[1]}-${m[2]}-01`, to: `${m[1]}-${m[2]}-${String(last).padStart(2, '0')}` };
}

function listRuns({ repo, month, execFn }) {
  const { from, to } = monthRange(month);
  const runs = [];
  for (let page = 1; page <= 50; page += 1) {
    const r = ghGetJson(execFn, `repos/${repo}/actions/workflows/${WORKFLOW_FILE}/runs`,
      { created: `${from}..${to}`, per_page: 100, page });
    const batch = r.workflow_runs || [];
    runs.push(...batch);
    if (batch.length < 100) break;
  }
  return runs;
}

/** run の 1 attempt を観測にする（ジョブを引き、門まで届いていればログを読む）。 */
function observeAttempt({ repo, run, attempt, execFn, since }) {
  const base = { runId: run.id, attempt, event: run.event, sha: run.head_sha, createdAt: run.created_at };
  if (Date.parse(run.created_at) < Date.parse(since)) return { ...base, reached: false };
  const jobs = ghGetJson(execFn, `repos/${repo}/actions/runs/${run.id}/attempts/${attempt}/jobs`, { per_page: 100 }).jobs || [];
  const job = jobs.find((j) => j.name === STACK_JOB_NAME);
  if (!job) return { ...base, reached: false };
  if (job.status !== 'completed') return { ...base, inProgress: true, reached: false };
  const step = (job.steps || []).find((s) => String(s.name).startsWith(RESET_GATE_PREFIX));
  const withJob = { ...base, startedAt: job.started_at, jobConclusion: job.conclusion };
  if (!step || (step.conclusion !== 'success' && step.conclusion !== 'failure')) return { ...withJob, reached: false };
  let log;
  try {
    log = ghGet(execFn, `repos/${repo}/actions/jobs/${job.id}/logs`);
  } catch (e) {
    return { ...withJob, logUnavailable: true, reached: false, error: String(e.message || e).split('\n')[0] };
  }
  const t = parseT25FromJobLog(log);
  return { ...withJob, reached: t.reached, verdict: t.verdict, istio: t.istio, p: t.p, w: t.w, slower: t.slower };
}

function collect({ repo, month, execFn = execFileSync, since = RANK_SUM_SINCE }) {
  const observations = [];
  for (const run of listRuns({ repo, month, execFn })) {
    for (let attempt = 1; attempt <= (run.run_attempt || 1); attempt += 1) {
      observations.push(observeAttempt({ repo, run, attempt, execFn, since }));
    }
  }
  return observations;
}

// ---------------------------------------------------------------- 自己試験

function selfTest() {
  const assert = require('assert');
  let n = 0;
  const ok = (name, fn) => { fn(); n += 1; console.log(`  ok  ${name}`); };

  const ts = '2026-09-26T13:20:01.8898944Z ';
  // 実ログ（run 36244009369）の形を縮めたもの。自己試験の手順にも「順位和検定」の行がある。
  const logOf = ({ verdict = '合格', p = '0.6165', w = '563', istio = '1', rankSum = true, gate = true } = {}) => [
    `${ts}##[group]Run node scripts/check-password-reset-mail.js --self-test`,
    `${ts}  ISTIO: 1`,
    `${ts}  ok  順位和検定: 既知の値（x=[1,2,3] y=[4,5,6] は両側 p = 2/20）・全同値は p = 1`,
    `${ts}  順位和検定（両側・正確法）: 暖機を除く 2 反復をまとめて 実在 24 / 非実在 24 標本 / 実在側の順位和 W=999（期待値 588）・U=1 / p=0.0001（有意水準 0.01）/ 中央値で遅い側 実在`,
    ...(gate ? [
      `${ts}##[group]Run node scripts/check-password-reset-mail.js --live`,
      `${ts}node scripts/check-password-reset-mail.js --live`,
      `${ts}shell: /usr/bin/bash -e {0}`,
      `${ts}env:`,
      `${ts}  CLUSTER: integration-stack`,
      `${ts}  ISTIO: ${istio}`,
      `${ts}##[endgroup]`,
      ...(verdict ? [`${ts}[check-password-reset-mail] T-25 所要時間（判定: ${verdict}）: 反復 3 回 × 片側 12 標本（…）`] : []),
      `${ts}  反復 2: n=12/12 実在 中央=152.578 ms / 非実在 中央=152.303 ms / 比=1.00 倍`,
      ...(rankSum ? [`${ts}  順位和検定（両側・正確法）: 暖機を除く 2 反復をまとめて 実在 24 / 非実在 24 標本 / 実在側の順位和 W=${w}（期待値 588）・U=413 / p=${p}（有意水準 0.01）/ 中央値で遅い側 実在`] : []),
      `${ts}##[error]Process completed with exit code 1.`,
    ] : []),
    `${ts}##[group]Run node scripts/seed-abac-policies.js --live`,
    `${ts}  順位和検定（両側・正確法）: W=1（期待値 588）・U=1 / p=0.0002（有意水準 0.01）`,
  ].join('\n');

  // ---- ログの読み取り --------------------------------------------------------------------
  ok('ログ: 門の区間から判定・p・W・ISTIO を読む（自己試験と後続の手順の行は読まない）', () => {
    const t = parseT25FromJobLog(logOf({ verdict: '不合格', p: '0.0094', w: '713' }));
    assert.deepStrictEqual(
      { stepRan: t.stepRan, reached: t.reached, verdict: t.verdict, p: t.p, w: t.w, expectedW: t.expectedW, istio: t.istio, slower: t.slower },
      { stepRan: true, reached: true, verdict: '不合格', p: 0.0094, w: 713, expectedW: 588, istio: '1', slower: '実在' },
    );
  });
  ok('ログ: 指数表記の p・中間順位の W（x.5）・同順位ありの見出しを読む', () => {
    const t = parseT25FromJobLog(logOf({ verdict: '不合格', p: '3.21e-10', w: '800.5' })
      .replace('順位和検定（両側・正確法）: 暖機を除く 2 反復をまとめて 実在 24 / 非実在 24 標本 / 実在側の順位和 W=800.5', '順位和検定（両側・正確法・同順位あり＝中間順位）: 暖機を除く 2 反復をまとめて 実在 24 / 非実在 24 標本 / 実在側の順位和 W=800.5'));
    assert.strictEqual(t.p, 3.21e-10);
    assert.strictEqual(t.w, 800.5);
  });
  ok('🔴 ログ: `--live` の無い呼び出し（#1550 より前の run）の区間も読む', () => {
    const old = logOf({ verdict: '合格', p: '0.5062', w: '590' })
      .replace(/(##\[group\]Run node scripts\/check-password-reset-mail\.js) --live/, '$1');
    assert.ok(!old.includes('Run node scripts/check-password-reset-mail.js --live'), '前提: 変異が当たっていない');
    const t = parseT25FromJobLog(old);
    assert.strictEqual(t.reached, true);
    assert.strictEqual(t.p, 0.5062);
  });
  ok('ログ: 門の手順が無い（前段で止まった）・判定の行が無い（前提の失敗）は届いていない', () => {
    assert.strictEqual(parseT25FromJobLog(logOf({ gate: false })).stepRan, false);
    const noVerdict = parseT25FromJobLog(logOf({ verdict: null, rankSum: false }));
    assert.strictEqual(noVerdict.stepRan, true);
    assert.strictEqual(noVerdict.reached, false);
  });
  ok('ログ: ISTIO が空（床の無い比較実行）を空文字として読む・判定の前提の不合格は p が null', () => {
    assert.strictEqual(parseT25FromJobLog(logOf({ istio: '' })).istio, '');
    const pre = parseT25FromJobLog(logOf({ verdict: '不合格', rankSum: false }));
    assert.strictEqual(pre.reached, true);
    assert.strictEqual(pre.p, null);
  });

  // ---- 集計 ------------------------------------------------------------------------------
  const at = (h) => `2026-09-27T${String(h).padStart(2, '0')}:00:00Z`;
  const ob = (runId, attempt, sha, h, verdict, p, extra = {}) => ({
    runId, attempt, event: 'push', sha, createdAt: at(h), startedAt: at(h), reached: true, istio: '1', verdict, p, w: 600, ...extra,
  });

  ok('KS 距離: 既知の値（[0.1,0.2,0.3,0.4] は D = 0.6・完全な格子 (i-0.5)/n は D = 0.5/n）と 0 件は null', () => {
    assert.ok(Math.abs(ksDistanceUniform([0.1, 0.2, 0.3, 0.4]) - 0.6) < 1e-12);
    assert.ok(Math.abs(ksDistanceUniform([0.125, 0.375, 0.625, 0.875]) - 0.125) < 1e-12);
    assert.strictEqual(ksDistanceUniform([]), null);
  });
  ok('🔴 偶然の赤: 赤の後の同じコミットの観測（同じ run の attempt 2）が合格なら偶然の赤', () => {
    const s = summarize([ob(1, 1, 'a', 1, '不合格', 0.009), ob(1, 2, 'a', 2, '合格', 0.6)]);
    assert.strictEqual(s.failures, 1);
    assert.strictEqual(s.chanceRed, 1);
    assert.strictEqual(s.rerunRed, 0);
    assert.strictEqual(s.n, 2, '再実行も 1 標本として数える');
    assert.strictEqual(s.feedbackToPlanning, false);
  });
  ok('🔴 再実行も赤: 次の観測も赤なら偶然の赤ではない・計画へ環流する（赤の再実行を別の赤として数え直さない）', () => {
    const s = summarize([ob(1, 1, 'a', 1, '不合格', 0.009), ob(1, 2, 'a', 2, '不合格', 1e-10), ob(2, 1, 'a', 3, '合格', 0.5)]);
    assert.strictEqual(s.failures, 2);
    assert.strictEqual(s.rerunRed, 1);
    assert.strictEqual(s.chanceRed, 0, '再実行の赤の次の合格を偶然の赤に数えた（再実行の再実行）');
    assert.strictEqual(s.reds.length, 1);
    assert.strictEqual(s.feedbackToPlanning, true);
  });
  ok('再実行なし: 次の観測が別のコミットだけなら再実行なし（偶然の赤と数えない）', () => {
    const s = summarize([ob(1, 1, 'a', 1, '不合格', 0.009), ob(2, 1, 'b', 2, '合格', 0.6)]);
    assert.strictEqual(s.unconfirmed, 1);
    assert.strictEqual(s.chanceRed, 0);
  });
  ok('振り分け: 判定式の変更前・届かない・床なし・前提の不合格・評価不能を n と不合格から外す／評価不能は n に入る', () => {
    const s = summarize([
      ob(1, 1, 'a', 1, '合格', 0.4, { createdAt: '2026-09-20T00:00:00Z' }),
      ob(2, 1, 'b', 2, null, null, { reached: false }),
      ob(3, 1, 'c', 3, '不合格', 1e-12, { istio: '' }),
      ob(4, 1, 'd', 4, '不合格', null),
      ob(5, 1, 'e', 5, '評価不能', 0.7),
      ob(6, 1, 'f', 6, '合格', 0.3),
    ]);
    assert.deepStrictEqual(s.counts, { beforeRankSum: 1, notReached: 1, noFloor: 1, judgementPrecondition: 1, evaluated: 2 });
    assert.strictEqual(s.n, 2);
    assert.strictEqual(s.failures, 0);
    assert.strictEqual(s.inconclusive, 1);
    assert.strictEqual(s.medianP, 0.5);
  });
  ok('🔴 KS の目安: D > 1.36/√n で環流（p が 0 付近へ偏った 10 件）・一様な 10 件は環流しない', () => {
    const skewed = summarize(Array.from({ length: 10 }, (_, i) => ob(i + 1, 1, `s${i}`, i, '合格', 0.02 + i * 0.01)));
    assert.ok(skewed.ks > skewed.ksThreshold, `D=${skewed.ks} 目安=${skewed.ksThreshold}`);
    assert.strictEqual(skewed.feedbackToPlanning, true);
    const uniform = summarize(Array.from({ length: 10 }, (_, i) => ob(i + 1, 1, `u${i}`, i, '合格', (i + 0.5) / 10)));
    assert.ok(Math.abs(uniform.ksThreshold - 1.36 / Math.sqrt(10)) < 1e-12);
    assert.strictEqual(uniform.feedbackToPlanning, false);
  });
  ok('要約の文面: 6 項目と環流の要否・不合格の一覧を出す', () => {
    const s = summarize([ob(1, 1, 'abcdef0123', 1, '不合格', 0.0094, { w: 713, slower: '実在' }), ob(1, 2, 'abcdef0123', 2, '合格', 0.6165, { w: 563 })]);
    const text = formatSummary(s, { month: '2026-09', repo: 'o/r', since: RANK_SUM_SINCE, asOf: 'X' });
    for (const needle of ['n = 2', '不合格 1（偶然の赤 1', '評価不能 0', 'p の中央値', 'KS 距離 D =', '1.36/√n', '計画への環流: 要らない', 'run 1 attempt 1', 'p=0.0094 W=713']) {
      assert.ok(text.includes(needle), `文面に「${needle}」が無い:\n${text}`);
    }
  });

  // ---- 読み取りの器（gh を差し替える）------------------------------------------------------
  ok('🔴 GitHub は GET でしか呼ばない（run の一覧・attempt のジョブ・ログ）・届いた attempt だけログを読む', () => {
    const calls = [];
    const stub = (cmd, args) => {
      calls.push(args);
      assert.strictEqual(cmd, 'gh');
      assert.strictEqual(args[0], 'api');
      assert.deepStrictEqual(args.slice(1, 3), ['-X', 'GET'], `GET 以外を呼んだ: ${args.join(' ')}`);
      const p = args[3];
      if (/\/workflows\/integration-stack\.yml\/runs$/.test(p)) {
        return JSON.stringify({ workflow_runs: [
          { id: 11, run_attempt: 2, event: 'push', head_sha: 'a', created_at: '2026-09-27T01:00:00Z' },
          { id: 10, run_attempt: 1, event: 'push', head_sha: 'z', created_at: '2026-09-02T01:00:00Z' },
        ] });
      }
      const m = /runs\/(\d+)\/attempts\/(\d+)\/jobs$/.exec(p);
      if (m) {
        const gate = m[1] === '11' ? (m[2] === '1' ? 'failure' : 'success') : 'skipped';
        return JSON.stringify({ jobs: [
          { id: Number(`${m[1]}${m[2]}`), name: 'integration-stack', status: 'completed', started_at: `2026-09-27T0${m[2]}:00:00Z`,
            steps: [{ name: '🔴 Gate — パスワードリセットの送出とメール本文（#1144）', conclusion: gate }] },
          { id: 1, name: 'report-failure / report', status: 'completed', steps: [] },
        ] });
      }
      if (/jobs\/111\/logs$/.test(p)) return logOf({ verdict: '不合格', p: '0.0094', w: '713' });
      if (/jobs\/112\/logs$/.test(p)) return logOf({ verdict: '合格', p: '0.6165', w: '563' });
      throw new Error(`想定外の呼び出し: ${p}`);
    };
    const obs = collect({ repo: 'o/r', month: '2026-09', execFn: stub });
    assert.strictEqual(obs.length, 3);
    const s = summarize(obs);
    assert.strictEqual(s.n, 2);
    assert.strictEqual(s.chanceRed, 1);
    assert.strictEqual(s.counts.beforeRankSum, 1);
    assert.ok(!calls.some((a) => /runs\/10\//.test(a[3])), '判定式の変更前の run のジョブまで読んだ');
    const listCall = calls.find((a) => /\/runs$/.test(a[3]));
    assert.ok(listCall.includes('created=2026-09-01..2026-09-30'), `月の範囲が違う: ${listCall.join(' ')}`);
  });
  ok('ログを読めない（保持期間切れ）は数えず、内訳に出す', () => {
    const stub = (cmd, args) => {
      const p = args[3];
      if (/\/runs$/.test(p)) return JSON.stringify({ workflow_runs: [{ id: 5, run_attempt: 1, event: 'schedule', head_sha: 'a', created_at: '2026-09-27T01:00:00Z' }] });
      if (/jobs$/.test(p)) return JSON.stringify({ jobs: [{ id: 51, name: 'integration-stack', status: 'completed', steps: [{ name: '🔴 Gate — パスワードリセット…', conclusion: 'failure' }] }] });
      throw new Error('HTTP 410: gone');
    };
    const s = summarize(collect({ repo: 'o/r', month: '2026-09', execFn: stub }));
    assert.deepStrictEqual(s.counts, { logUnavailable: 1 });
    assert.strictEqual(s.n, 0);
    assert.strictEqual(s.ks, null);
  });
  ok('月の範囲: 閏年の 2 月・12 月・書式違いは落ちる', () => {
    assert.deepStrictEqual(monthRange('2028-02'), { from: '2028-02-01', to: '2028-02-29' });
    assert.deepStrictEqual(monthRange('2026-12'), { from: '2026-12-01', to: '2026-12-31' });
    assert.throws(() => monthRange('2026-9'));
    assert.throws(() => monthRange('2026-13'));
  });
  ok('判定式の変更の時刻と有意水準は検査器の値と同じ', () => {
    assert.strictEqual(RANK_SUM_SINCE, '2026-09-26T04:21:42Z');
    assert.strictEqual(TIMING_ALPHA, 0.01);
  });

  console.log(`[t25-monthly-summary] self-test OK: ${n} 件`);
}

// ---------------------------------------------------------------- main

function parseArgs(argv) {
  const out = { month: null, repo: DEFAULT_REPO, json: false, selfTest: false };
  for (let i = 0; i < argv.length; i += 1) {
    const a = argv[i];
    if (a === '--self-test') out.selfTest = true;
    else if (a === '--json') out.json = true;
    else if (a === '--month') out.month = argv[++i];
    else if (a === '--repo') out.repo = argv[++i];
    else throw new Error(`未知の引数: ${a}`);
  }
  return out;
}

function main() {
  let args;
  try {
    args = parseArgs(process.argv.slice(2));
  } catch (e) {
    console.error(`[t25-monthly-summary] ${e.message}`);
    process.exit(2);
  }
  if (args.selfTest) { selfTest(); return; }
  if (!args.month) {
    console.error('[t25-monthly-summary] --month YYYY-MM を与える（--self-test は GitHub へ出ない）');
    process.exit(2);
  }
  const asOf = new Date().toISOString().replace(/\.\d{3}Z$/, 'Z');
  const observations = collect({ repo: args.repo, month: args.month });
  const s = summarize(observations);
  if (args.json) {
    // 観測の一覧も出す（run・attempt・p・W。集計を後から検算できるように）。
    const rows = observations.map((o) => ({
      runId: o.runId, attempt: o.attempt, event: o.event, sha: o.sha, createdAt: o.createdAt,
      category: categorize(o), verdict: o.verdict ?? null, p: o.p ?? null, w: o.w ?? null,
    }));
    console.log(JSON.stringify({ month: args.month, repo: args.repo, since: RANK_SUM_SINCE, asOf, ...s, observations: rows }, null, 2));
  } else {
    console.log(formatSummary(s, { month: args.month, repo: args.repo, since: RANK_SUM_SINCE, asOf }));
  }
}

if (require.main === module) main();

module.exports = {
  parseT25FromJobLog,
  categorize,
  ksDistanceUniform,
  rerunOutcomes,
  summarize,
  formatSummary,
  monthRange,
  collect,
  ghGet,
  ghGetJson,
  DEFAULT_REPO,
  WORKFLOW_FILE,
  STACK_JOB_NAME,
  RESET_GATE_PREFIX,
  RANK_SUM_SINCE,
  KS_COEFFICIENT,
};
