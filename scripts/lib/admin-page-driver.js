'use strict';
/*
 * lib/admin-page-driver.js
 *
 * FR-06 / IADR-0549・IADR-0556 (#1931 項目 3・#1973): DocumentService の**管理者だけの一回限りの口**（1 ページを確かめて書き換え、
 * 続きのカーソルを返す口）を、量を制御しながら最後まで流す駆動器の本体。
 *
 * 一括アーカイブの駆動器（archive-bodyless-documents.js。#1931）の main をそのまま切り出し、口ごとに違う所（経路・引数・
 * 状態ファイルの絞り込み・集計・文言）だけを profile で受けるようにした（#1973 で 2 つ目の口が増えたため。作法を写さない）。
 * 持つもの: `--live` の確認（#1550）・dry-run（全ページを辿って集計する。状態ファイルを作らない）・カナリア・`--max-pages`・
 * 状態ファイルと `--resume`・確かめた位置へ戻す・`--operator` / `--reason`・連続失敗で止まる・見張るキューと DLQ の監視。
 * I/O（port-forward・トークン・キューの深さ・状態ファイル）は再発行の駆動器（republish-document-updated.js）の関数を使う。
 *
 * 🔴 **稼働クラスタへ当たる**のは、これを呼ぶ入口（live-scripts.json の live）である。ここは require しても何もしない。
 *
 * profile（必須の項目）:
 *   name                    ログの接頭辞（入口のファイル名から .js を除いたもの）
 *   requireLive(argv)       --live の確認（入口が lib/live-opt-in.js の requireLiveOptIn を呼ぶ。#1550。--help の後・稼働クラスタの前に呼ばれる）
 *   route                   口の経路（例 /documents/archive-bodyless）
 *   stateFile               状態ファイルの既定の名前
 *   usage                   --help の文
 *   verb                    書き換えの動詞（例「アーカイブ」）。文言に使う
 *   parseArgs(argv)         引数の解析（共通の引数は parseCommonArg に渡す）
 *   preconditionProblem(o)  実行の前提の違反（null なら続けてよい）
 *   newState(o) / stateProblem(state)
 *   filtersOf(o)            新規の走査の絞り込み（状態ファイルの filters）
 *   describeFilters(f)      絞り込みの 1 行の説明
 *   buildRequestBody({ dryRun, filters, limit, cursor, requestedBy, reason })
 *   emptyTotals() / addTotals(t, res) / changedOf(res)（このページで書き換えた件数）/ changedTotal(t)（累計で書き換えた件数）
 *   dryRunPageLine(totals, res) / summarizeDryRun(first, totals, filters)
 *   formatProgress(state, res, elapsedMs) / doneLines(totals)
 *   canaryLine(n)           カナリアが通ったときの 1 行
 *   dlqHint                 DLQ が増えたときに足す説明（行の配列）
 */

const { spawnSync } = require('child_process');

const env = (k, d) => process.env[k] || d;
const MAX_PAGE_SIZE = 200; // 口の上限
const COMMON_DEFAULTS = Object.freeze({
  pageSize: 100,
  maxPageSize: MAX_PAGE_SIZE,
  sleepMs: 1000,
  maxPages: 0,
  canary: 20,
  drainTimeoutMs: 300000,
  maxQueueDepth: 200,
  maxDlqGrowth: 0,
  maxConsecutiveFailures: 3,
  watchQueues: 'ingestion-service.DocumentUpdated,wiki-service.DocumentUpdated,graph-service.DocumentUpdated',
});
const MAX_OPERATOR_LENGTH = 100;
const MAX_REASON_LENGTH = 500;

const log = (s) => process.stdout.write(`${s}\n`);
const warn = (s) => process.stderr.write(`${s}\n`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const COMMON_USAGE = `  --dry-run                     書き換えず、全ページを確かめて件数と内訳を出す
  --resume                      状態ファイルから続ける
  --state <file>                状態ファイル
  --page-size <n>               1 ページの件数（既定 ${COMMON_DEFAULTS.pageSize}・1〜${MAX_PAGE_SIZE}）
  --canary <n>                  最初のページの件数（既定 ${COMMON_DEFAULTS.canary}・0 で無効）。キューが空になるまで待ち DLQ が増えたら止まる
  --max-pages <n>               この実行で出すページ数の上限（既定 0＝上限なし。続きは --resume）
  --sleep-ms <n>                ページの間に待つ時間（既定 ${COMMON_DEFAULTS.sleepMs}）
  --drain-timeout-ms <n>        キューが空になるのを待つ上限（既定 ${COMMON_DEFAULTS.drainTimeoutMs}）
  --max-queue-depth <n>         見張るキューの深さの合計がこれ以下になるまで次のページを出さない（既定 ${COMMON_DEFAULTS.maxQueueDepth}）
  --max-dlq-growth <n>          開始時からの DLQ の増加がこれを超えたら止まる（既定 ${COMMON_DEFAULTS.maxDlqGrowth}。カナリアは常に 0）
  --no-queue-watch              RabbitMQ のキューを見ない（深さの待ちも DLQ の監視もしない）
  --max-consecutive-failures <n> 連続でこの回数失敗したら止まる（既定 ${COMMON_DEFAULTS.maxConsecutiveFailures}）
  --operator <name>             操作者（口のログへ認証済みの主体と並べて残す。既定は環境変数 USER）
  --reason <text>               理由（書き換える実行では必須。口のログへ残す）`;

function positiveInt(name, raw, { allowZero = false } = {}) {
  const n = Number(raw);
  if (!Number.isInteger(n) || n < 0 || (!allowZero && n === 0))
    throw new Error(`${name} は${allowZero ? '0 以上' : '1 以上'}の整数で指定してください（${raw}）。`);
  return n;
}

/** 共通の引数の既定値（状態ファイルの既定の名前だけは入口が渡す）。 */
function commonOptions(stateFile) {
  return {
    help: false, dryRun: false, resume: false, queueWatch: true,
    pageSize: COMMON_DEFAULTS.pageSize, sleepMs: COMMON_DEFAULTS.sleepMs, maxPages: COMMON_DEFAULTS.maxPages, canary: COMMON_DEFAULTS.canary,
    drainTimeoutMs: COMMON_DEFAULTS.drainTimeoutMs, maxQueueDepth: COMMON_DEFAULTS.maxQueueDepth, maxDlqGrowth: COMMON_DEFAULTS.maxDlqGrowth,
    maxConsecutiveFailures: COMMON_DEFAULTS.maxConsecutiveFailures, stateFile,
    operator: null, reason: null,
  };
}

/** 値を取る引数の値（欠けていれば落とす。黙って既定へ倒さない）。 */
function argValue(argv, i, name) {
  if (i + 1 >= argv.length || String(argv[i + 1]).startsWith('--')) throw new Error(`${name} に値がありません。`);
  return argv[i + 1];
}

/**
 * 共通の引数を 1 つ解析して o に書く。読んだ引数の数（値を取る引数は 2）を返し、知らない引数なら 0 を返す。
 */
function parseCommonArg(o, argv, i) {
  const a = argv[i];
  const take = (fn) => { fn(argValue(argv, i, a)); return 2; };
  switch (a) {
    case '--live': return 1;
    case '--help': case '-h': o.help = true; return 1;
    case '--dry-run': o.dryRun = true; return 1;
    case '--resume': o.resume = true; return 1;
    case '--no-queue-watch': o.queueWatch = false; return 1;
    case '--state': return take((v) => { o.stateFile = v; });
    case '--page-size': return take((v) => { o.pageSize = Math.min(MAX_PAGE_SIZE, positiveInt(a, v)); });
    case '--canary': return take((v) => { o.canary = Math.min(MAX_PAGE_SIZE, positiveInt(a, v, { allowZero: true })); });
    case '--max-pages': return take((v) => { o.maxPages = positiveInt(a, v, { allowZero: true }); });
    case '--sleep-ms': return take((v) => { o.sleepMs = positiveInt(a, v, { allowZero: true }); });
    case '--drain-timeout-ms': return take((v) => { o.drainTimeoutMs = positiveInt(a, v); });
    case '--max-queue-depth': return take((v) => { o.maxQueueDepth = positiveInt(a, v, { allowZero: true }); });
    case '--max-dlq-growth': return take((v) => { o.maxDlqGrowth = positiveInt(a, v, { allowZero: true }); });
    case '--max-consecutive-failures': return take((v) => { o.maxConsecutiveFailures = positiveInt(a, v); });
    case '--operator': return take((raw) => {
      const v = raw.trim();
      if (!v || v.length > MAX_OPERATOR_LENGTH) throw new Error(`--operator は 1〜${MAX_OPERATOR_LENGTH} 文字で指定してください。`);
      o.operator = v;
    });
    case '--reason': return take((raw) => {
      const v = raw.trim();
      if (!v || v.length > MAX_REASON_LENGTH) throw new Error(`--reason は 1〜${MAX_REASON_LENGTH} 文字で指定してください。`);
      o.reason = v;
    });
    default: return 0;
  }
}

/** 共通の組み合わせの検査（解析の後で呼ぶ）。 */
function checkCommonCombination(o) {
  if (o.help) return;
  if (o.resume && o.dryRun) throw new Error('--resume と --dry-run は同時に指定できません。');
}

/** 状態ファイルの共通の形。違反を文字列で返す（null なら続けてよい）。 */
function commonStateProblem(state, kind, version) {
  if (!state || typeof state !== 'object') return '状態ファイルが JSON のオブジェクトではありません。';
  if (state.kind !== kind) return `状態ファイルの種類が違います（${state.kind}。期待 ${kind}）。別の駆動器の状態ファイルを渡していませんか。`;
  if (state.version !== version) return `状態ファイルの版が違います（${state.version}。期待 ${version}）。`;
  if (!state.filters || typeof state.filters !== 'object') return '状態ファイルに絞り込みがありません。';
  if (state.cursor !== null && typeof state.cursor !== 'string') return '状態ファイルの cursor が不正です。';
  if (state.confirmedCursor !== null && typeof state.confirmedCursor !== 'string') return '状態ファイルの confirmedCursor が不正です。';
  if (typeof state.confirmedDone !== 'boolean') return '状態ファイルの confirmedDone が不正です。';
  if (!state.totals || typeof state.totals !== 'object') return '状態ファイルの累計が不正です。';
  return null;
}

/** 新規の走査の状態（共通の形）。 */
function commonNewState({ kind, version, filters, totals }, now = new Date()) {
  return {
    version,
    kind,
    startedAt: now.toISOString(),
    filters,
    cursor: null,
    confirmedCursor: null,
    done: false,
    confirmedDone: false,
    canaryPassed: false,
    dlqBaseline: null,
    matched: null,
    totals,
  };
}

/** 1 ページの応答を状態へ積む（新しい状態を返す）。confirmedCursor は動かさない。 */
function applyPage(state, res, addTotals) {
  return {
    ...state,
    cursor: res.nextCursor ?? null,
    done: res.nextCursor == null,
    matched: state.matched ?? res.matched ?? null,
    totals: addTotals(state.totals, res),
  };
}

/** 見張るキューの深さの合計。 */
function watchedDepth(depths, queues) {
  return queues.reduce((n, q) => n + (depths[q] || 0), 0);
}

// --- 実行（I/O） --------------------------------------------------------------

async function run(profile, argv) {
  const p = profile;
  const rp = require('../republish-document-updated.js');
  let o;
  try {
    o = p.parseArgs(argv);
  } catch (e) {
    warn(`[${p.name}] ${e.message}`);
    return 1;
  }
  if (o.help) {
    log(p.usage);
    return 0;
  }
  // NFR, #1550: ここから先は稼働クラスタへ当たる。明示の指定が無ければ何もしない。
  // 判定器は入口が呼ぶ（profile.requireLive。入口ごとに呼んでいることを #1550 の静的検査が見るため）。
  p.requireLive(argv);
  const precondition = p.preconditionProblem(o);
  if (precondition) {
    warn(`[${p.name}] ${precondition}`);
    return 1;
  }

  const NS = env('REPUBLISH_NS', 'microservices-platform');
  const INFRA_NS = env('REPUBLISH_INFRA_NS', 'platform-infra');
  const REALM = env('REPUBLISH_REALM', 'platform');
  const DLQ = env('REPUBLISH_DLQ', 'wolverine-dead-letter-queue');
  const WATCH = env('ARCHIVE_WATCH_QUEUES', COMMON_DEFAULTS.watchQueues).split(',').map((s) => s.trim()).filter(Boolean);
  const POLL_MS = positiveInt('REPUBLISH_POLL_MS', env('REPUBLISH_POLL_MS', '5000'));
  const abacSeed = require('../seed-abac-policies.js');
  const CLIENT_ID = env('REPUBLISH_CLIENT_ID', abacSeed.CLIENT_ID);
  const CLIENT_SECRET = process.env.REPUBLISH_CLIENT_SECRET || abacSeed.clientSecretFromRealm(CLIENT_ID) || '';

  const io = rp.makeIo();
  process.on('exit', () => io.cleanup());
  let state = null;
  let onInterrupt = () => {};
  process.on('SIGINT', () => {
    io.cleanup();
    try { onInterrupt(); } catch (e) { warn(`[${p.name}] 状態を戻せませんでした: ${e.message}`); }
    if (state) warn(`\n中断しました。状態は ${o.stateFile} に残っています。続けるには --resume。`);
    process.exit(130);
  });

  let documentUrl = env('REPUBLISH_DOCUMENT_URL', '');
  let kcUrl = env('REPUBLISH_KC_URL', '');
  const needK8s = !documentUrl || !kcUrl || (o.queueWatch && !o.dryRun);
  if (needK8s && spawnSync('kubectl', ['cluster-info'], { stdio: 'ignore' }).status !== 0) {
    warn('k8s に到達できません（kubectl cluster-info が失敗）。REPUBLISH_DOCUMENT_URL / REPUBLISH_KC_URL と --no-queue-watch で直接指定もできます。');
    return 2;
  }
  if (!documentUrl) {
    io.portForward(NS, 'document-service', 18096, 8080);
    documentUrl = 'http://localhost:18096';
  }
  if (!kcUrl) {
    io.portForward(INFRA_NS, 'keycloak', 18097, 8080);
    kcUrl = 'http://localhost:18097';
  }
  if (!(await rp.waitReachable(`${documentUrl}/health/live`)) ||
      !(await rp.waitReachable(`${kcUrl}/realms/${REALM}/.well-known/openid-configuration`))) {
    warn('document-service / keycloak へ到達できませんでした。');
    return 2;
  }
  log(`接続先: document=${documentUrl} / keycloak=${kcUrl}`);

  let token = null;
  let tokenAt = 0;
  async function freshToken() {
    if (token && Date.now() - tokenAt < 60000) return token;
    const res = await fetch(`${kcUrl}/realms/${REALM}/protocol/openid-connect/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: abacSeed.buildTokenForm({ clientId: CLIENT_ID, clientSecret: CLIENT_SECRET }),
    });
    if (!res.ok) throw new Error(`Keycloak のトークン取得に失敗しました（${res.status}。client ${CLIENT_ID}）。`);
    token = (await res.json()).access_token;
    tokenAt = Date.now();
    return token;
  }
  async function call(body) {
    const res = await fetch(`${documentUrl}${p.route}`, {
      method: 'POST',
      headers: { Authorization: `Bearer ${await freshToken()}`, 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(300000),
    });
    const text = await res.text();
    if (!res.ok) throw new Error(`POST ${p.route} が失敗しました（${res.status}）: ${text.slice(0, 300)}`);
    return JSON.parse(text);
  }

  const requestedBy = o.operator || process.env.USER || process.env.USERNAME || null;
  const tags = { requestedBy, reason: o.reason };
  log(`操作者: ${requestedBy ?? '(未指定)'}${o.reason ? ` / 理由: ${o.reason}` : ''}（口のログへ認証済みの主体と並べて残る）`);

  if (o.dryRun) {
    // dry-run は全ページを辿って確かめる（状態ファイルは使わない。途中で止まったらやり直せばよい —— 何も変えていない）。
    const filters = p.filtersOf(o);
    let cursor = null;
    let first = null;
    let totals = p.emptyTotals();
    do {
      const res = await call(p.buildRequestBody({ dryRun: true, filters, limit: o.pageSize, cursor, ...tags }));
      first = first || res;
      totals = p.addTotals(totals, res);
      log(p.dryRunPageLine(totals, res));
      cursor = res.nextCursor;
    } while (cursor);
    for (const line of p.summarizeDryRun(first, totals, filters)) log(line);
    log(`--dry-run のため${p.verb}しません。`);
    return 0;
  }

  const existing = rp.loadState(o.stateFile);
  if (o.resume) {
    const problem = existing ? p.stateProblem(existing) : `状態ファイルがありません: ${o.stateFile}`;
    if (problem) {
      warn(`[${p.name}] ${problem}`);
      return 1;
    }
    state = existing;
    if (state.done && state.confirmedDone) {
      log(`状態ファイルは完了しています（累計 ${p.verb} ${p.changedTotal(state.totals)} 件）。やり直すなら状態ファイルを消して新規に走らせてください（冪等）。`);
      return 0;
    }
    log(`再開します（開始 ${state.startedAt}・累計 ${p.verb} ${p.changedTotal(state.totals)} 件・${p.describeFilters(state.filters)}）。`);
  } else {
    if (existing) {
      warn(`[${p.name}] 状態ファイルが既にあります（${o.stateFile}）。続けるなら --resume、やり直すなら状態ファイルを消してください。`);
      return 1;
    }
    state = p.newState(o);
    rp.saveState(o.stateFile, state);
    log(`新規の走査を始めます（${p.describeFilters(state.filters)}・状態 ${o.stateFile}）。`);
  }

  function stopAtConfirmed() {
    if (!rp.hasUnconfirmed(state)) return;
    state = rp.rollbackToConfirmed(state);
    rp.saveState(o.stateFile, state);
    warn(`状態の cursor を確かめた位置へ戻しました（${state.confirmedCursor ?? '先頭'}）。--resume は確かめていないページをもう一度確かめる（${p.verb}済みは何もしないので冪等）。`);
  }
  onInterrupt = stopAtConfirmed;

  const depths = () => rp.readQueueDepths(INFRA_NS);
  if (o.queueWatch) {
    try {
      const d = depths();
      if (state.dlqBaseline == null || o.resume) {
        const r = rp.rebaselineDlq(state, d[DLQ]);
        state = r.state;
        rp.saveState(o.stateFile, state);
        if (o.resume) log(`DLQ の基準を取り直しました: 前の基準 ${r.previous ?? '(なし)'} → 今の深さ ${r.current}。`);
      }
      log(`キュー: ${WATCH.map((q) => `${q}=${d[q] ?? '(未宣言)'}`).join(' / ')} / ${DLQ}=${d[DLQ] ?? 0}（基準 ${state.dlqBaseline}）`);
    } catch (e) {
      warn(`[${p.name}] ${e.message}`);
      warn('キューを見ずに流すなら --no-queue-watch を明示してください（量の制御と DLQ の監視が無くなる）。');
      return 2;
    }
  }

  async function waitQueueAtMost(limit, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
      const d = depths();
      const depth = watchedDepth(d, WATCH);
      if (depth <= limit) return d;
      if (Date.now() > deadline) throw new Error(`見張るキューが ${timeoutMs}ms のうちに ${limit} 件以下になりませんでした（${depth} 件）。`);
      await sleep(POLL_MS);
    }
  }

  function checkDlq(d, isCanary) {
    const v = rp.dlqVerdict({ isCanary, baseline: state.dlqBaseline, current: d[DLQ], maxGrowth: o.maxDlqGrowth });
    if (v.stop) {
      warn(`[${p.name}] ${isCanary ? 'カナリアの後に' : ''}DLQ が基準から ${v.growth} 件増えました（許容 ${v.allowed}）。止めます。`);
      for (const line of p.dlqHint) warn(`  ${line}`);
      warn(`  kubectl -n ${NS} logs deploy/<ingestion|wiki|graph>-service --since=15m | grep -E "Exception|NoSuchKey|dead"`);
      stopAtConfirmed();
      return false;
    }
    state = rp.confirmChecked(state, { queueWatch: true, ingestDepth: watchedDepth(d, WATCH) });
    rp.saveState(o.stateFile, state);
    return true;
  }

  async function settle() {
    if (!o.queueWatch || !rp.hasUnconfirmed(state)) return true;
    log(`見張るキューが空になるまで待って DLQ を確かめます（上限 ${o.drainTimeoutMs}ms）。`);
    let d;
    try {
      d = await waitQueueAtMost(0, o.drainTimeoutMs);
    } catch (e) {
      warn(`[${p.name}] ${e.message}`);
      stopAtConfirmed();
      return false;
    }
    return checkDlq(d, false);
  }

  const started = Date.now();
  let failures = 0;
  let pagesThisRun = 0;
  while (!state.done) {
    if (o.maxPages > 0 && pagesThisRun >= o.maxPages) {
      if (!(await settle())) return 1;
      log(`--max-pages ${o.maxPages} に達したので止めます。続けるには --resume。`);
      return 0;
    }
    const isCanary = o.canary > 0 && !state.canaryPassed;
    let res;
    try {
      const before = rp.queueWaitPlan({ ...o, queueWatch: o.queueWatch, isCanary }).before;
      if (before) await waitQueueAtMost(before.limit, before.timeoutMs);
      res = await call(p.buildRequestBody({
        dryRun: false, filters: state.filters, limit: isCanary ? o.canary : o.pageSize, cursor: state.cursor, ...tags,
      }));
    } catch (e) {
      failures++;
      warn(`[${p.name}] 失敗 ${failures}/${o.maxConsecutiveFailures}: ${e.message}`);
      if (rp.shouldStopOnFailures(failures, o.maxConsecutiveFailures)) {
        warn(`連続 ${failures} 回失敗したので止めます。状態は ${o.stateFile}。原因を直して --resume（口はページの途中まで保存しない）。`);
        stopAtConfirmed();
        return 1;
      }
      await sleep(rp.backoffMs(o.sleepMs, failures));
      continue;
    }
    failures = 0;
    pagesThisRun++;
    const changed = p.changedOf(res);
    const nothingInFlight = !rp.hasUnconfirmed(state) && !(changed > 0);
    state = applyPage(state, res, p.addTotals);
    rp.saveState(o.stateFile, state);
    log(p.formatProgress(state, res, Date.now() - started));

    if (nothingInFlight) {
      state = rp.confirmChecked(state, { queueWatch: false });
      rp.saveState(o.stateFile, state);
    } else if (o.queueWatch) {
      const after = rp.queueWaitPlan({ ...o, queueWatch: true, isCanary, published: changed }).after;
      let d;
      try {
        d = after ? await waitQueueAtMost(after.limit, after.timeoutMs) : depths();
      } catch (e) {
        warn(`[${p.name}] ${e.message}`);
        stopAtConfirmed();
        return 1;
      }
      if (!checkDlq(d, isCanary)) return 1;
      if (isCanary && changed > 0) {
        state = { ...state, canaryPassed: true };
        rp.saveState(o.stateFile, state);
        log(p.canaryLine(changed));
      }
    } else {
      state = rp.confirmChecked(state, { queueWatch: false });
      if (isCanary) state = { ...state, canaryPassed: true };
      rp.saveState(o.stateFile, state);
    }
    if (!state.done && o.sleepMs > 0) await sleep(o.sleepMs);
  }

  if (!(await settle())) return 1;
  for (const line of p.doneLines(state.totals)) log(line);
  return 0;
}

module.exports = {
  COMMON_DEFAULTS,
  COMMON_USAGE,
  MAX_OPERATOR_LENGTH,
  MAX_REASON_LENGTH,
  positiveInt,
  commonOptions,
  argValue,
  parseCommonArg,
  checkCommonCombination,
  commonStateProblem,
  commonNewState,
  applyPage,
  watchedDepth,
  run,
};
