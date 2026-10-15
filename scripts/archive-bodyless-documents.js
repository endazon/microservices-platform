#!/usr/bin/env node
'use strict';
/*
 * archive-bodyless-documents.js
 *
 * FR-06, UC-03, SC-05 / IADR-0549 (#1931 項目 3。オーナー裁定 B・2026-10-15):
 *   9/26 の SeaweedFS への切り替えより前に作られ、本文のオブジェクトが無い（NoSuchKey）文書を、既存の状態「アーカイブ」へ移す
 *   **一回限りの管理操作**の駆動器。台帳と表題は残す。下流（取り込み・Wiki 同期）へは単票のアーカイブと同じイベントで伝わる。
 *
 * 方式（IADR-0549）:
 *   - 確かめとアーカイブは DocumentService の管理者だけの口 `POST /documents/archive-bodyless` が行う
 *     （本文の有無はオブジェクトストレージへの HEAD 相当で確かめる。本文を読まない。「無い」は NoSuchKey だけ）。
 *     本スクリプトは**量の制御・進捗・中断と再開・キューと DLQ の監視**だけを持つ（再発行の駆動器 republish-document-updated.js と同じ作法）。
 *   - 認証は Keycloak の client_credentials（client `abac-seeder`。platform-admin を持つ）。I/O は再発行の駆動器の関数を再利用する。
 *   - 🔴 `--created-before` は**必須**（既定を持たない）。切り替え後の文書を巻き込む実行を組み立てられないようにする。
 *   - 🔴 冪等。アーカイブ済みは口の母集合から外れるので、2 回流しても 2 回目は何もしない（止まった後の --resume も安全）。
 *
 * 🔴 **稼働クラスタへ当たる。** `--live`（または LIVE=1）が無ければ何もせずに終わる（#1550）。
 *
 * 実行方法（PoC の手順は IADR-0549 §PoC の手順）:
 *   node scripts/archive-bodyless-documents.js --help
 *   node scripts/archive-bodyless-documents.js --live --dry-run --operator alice --created-before 2026-09-27T00:00:00Z
 *   node scripts/archive-bodyless-documents.js --live --operator alice --reason "#1931 項目 3（カナリア）" --created-before 2026-09-27T00:00:00Z --canary 20 --max-pages 1
 *   node scripts/archive-bodyless-documents.js --live --resume --operator alice --reason "#1931 項目 3"
 *
 * 主な環境変数（再発行の駆動器と同じ名前を読む）:
 *   REPUBLISH_DOCUMENT_URL / REPUBLISH_KC_URL（与えれば port-forward を張らない）
 *   REPUBLISH_NS / REPUBLISH_INFRA_NS / REPUBLISH_REALM / REPUBLISH_CLIENT_ID / REPUBLISH_CLIENT_SECRET / REPUBLISH_DLQ / REPUBLISH_POLL_MS
 *   ARCHIVE_WATCH_QUEUES（既定 ingestion-service.DocumentUpdated,wiki-service.DocumentUpdated,graph-service.DocumentUpdated）
 *
 * 終了コード: 0=完了（または --max-pages で区切って止めた） / 1=失敗・安全のための停止（状態は残る） /
 *            2=前提未整備（k8s へ到達できない・キューを読めない等） / 3=明示の指定が無い（#1550）
 */

const { spawnSync } = require('child_process');
const { requireLiveOptIn } = require('./lib/live-opt-in.js');
const rp = require('./republish-document-updated.js');

const env = (k, d) => process.env[k] || d;
const ROUTE = '/documents/archive-bodyless';
const DEFAULTS = Object.freeze({
  pageSize: 100,
  maxPageSize: 200, // 口の上限
  sleepMs: 1000,
  maxPages: 0,
  canary: 20,
  drainTimeoutMs: 300000,
  maxQueueDepth: 200,
  maxDlqGrowth: 0,
  maxConsecutiveFailures: 3,
  stateFile: 'archive-bodyless-documents.state.json',
  watchQueues: 'ingestion-service.DocumentUpdated,wiki-service.DocumentUpdated,graph-service.DocumentUpdated',
});
const STATE_VERSION = 1;
const STATE_KIND = 'archive-bodyless';
const MAX_OPERATOR_LENGTH = 100;
const MAX_REASON_LENGTH = 500;

const log = (s) => process.stdout.write(`${s}\n`);
const warn = (s) => process.stderr.write(`${s}\n`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const USAGE = `使い方: node scripts/archive-bodyless-documents.js --live [オプション]
  --created-before <ISO8601>    この時刻より前に作られた文書だけ（必須。--resume では状態ファイルのものを使う）
  --dry-run                     アーカイブせず、全ページの本文の有無を確かめて件数と内訳を出す
  --resume                      状態ファイルから続ける
  --state <file>                状態ファイル（既定 ./${DEFAULTS.stateFile}）
  --page-size <n>               1 ページの件数（既定 ${DEFAULTS.pageSize}・1〜${DEFAULTS.maxPageSize}）
  --canary <n>                  最初のページの件数（既定 ${DEFAULTS.canary}・0 で無効）。キューが空になるまで待ち DLQ が増えたら止まる
  --max-pages <n>               この実行で出すページ数の上限（既定 0＝上限なし。続きは --resume）
  --sleep-ms <n>                ページの間に待つ時間（既定 ${DEFAULTS.sleepMs}）
  --drain-timeout-ms <n>        キューが空になるのを待つ上限（既定 ${DEFAULTS.drainTimeoutMs}）
  --max-queue-depth <n>         見張るキューの深さの合計がこれ以下になるまで次のページを出さない（既定 ${DEFAULTS.maxQueueDepth}）
  --max-dlq-growth <n>          開始時からの DLQ の増加がこれを超えたら止まる（既定 ${DEFAULTS.maxDlqGrowth}。カナリアは常に 0）
  --no-queue-watch              RabbitMQ のキューを見ない（深さの待ちも DLQ の監視もしない）
  --max-consecutive-failures <n> 連続でこの回数失敗したら止まる（既定 ${DEFAULTS.maxConsecutiveFailures}）
  --operator <name>             操作者（口のログへ認証済みの主体と並べて残す。既定は環境変数 USER）
  --reason <text>               理由（アーカイブする実行では必須。口のログへ残す）`;

// --- 純粋関数（実機なしで試験できるように切り出す） ---------------------------------

function positiveInt(name, raw, { allowZero = false } = {}) {
  const n = Number(raw);
  if (!Number.isInteger(n) || n < 0 || (!allowZero && n === 0))
    throw new Error(`${name} は${allowZero ? '0 以上' : '1 以上'}の整数で指定してください（${raw}）。`);
  return n;
}

/** 引数を解析する。知らない引数・値の欠けた引数は落とす（黙って既定へ倒さない）。 */
function parseArgs(argv) {
  const o = {
    help: false, dryRun: false, resume: false, queueWatch: true,
    pageSize: DEFAULTS.pageSize, sleepMs: DEFAULTS.sleepMs, maxPages: DEFAULTS.maxPages, canary: DEFAULTS.canary,
    drainTimeoutMs: DEFAULTS.drainTimeoutMs, maxQueueDepth: DEFAULTS.maxQueueDepth, maxDlqGrowth: DEFAULTS.maxDlqGrowth,
    maxConsecutiveFailures: DEFAULTS.maxConsecutiveFailures, stateFile: DEFAULTS.stateFile,
    createdBefore: null, operator: null, reason: null,
  };
  const value = (i, name) => {
    if (i + 1 >= argv.length || String(argv[i + 1]).startsWith('--')) throw new Error(`${name} に値がありません。`);
    return argv[i + 1];
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    switch (a) {
      case '--live': break;
      case '--help': case '-h': o.help = true; break;
      case '--dry-run': o.dryRun = true; break;
      case '--resume': o.resume = true; break;
      case '--no-queue-watch': o.queueWatch = false; break;
      case '--state': o.stateFile = value(i, a); i++; break;
      case '--page-size': o.pageSize = Math.min(DEFAULTS.maxPageSize, positiveInt(a, value(i, a))); i++; break;
      case '--canary': o.canary = Math.min(DEFAULTS.maxPageSize, positiveInt(a, value(i, a), { allowZero: true })); i++; break;
      case '--max-pages': o.maxPages = positiveInt(a, value(i, a), { allowZero: true }); i++; break;
      case '--sleep-ms': o.sleepMs = positiveInt(a, value(i, a), { allowZero: true }); i++; break;
      case '--drain-timeout-ms': o.drainTimeoutMs = positiveInt(a, value(i, a)); i++; break;
      case '--max-queue-depth': o.maxQueueDepth = positiveInt(a, value(i, a), { allowZero: true }); i++; break;
      case '--max-dlq-growth': o.maxDlqGrowth = positiveInt(a, value(i, a), { allowZero: true }); i++; break;
      case '--max-consecutive-failures': o.maxConsecutiveFailures = positiveInt(a, value(i, a)); i++; break;
      case '--operator': {
        const v = value(i, a).trim();
        if (!v || v.length > MAX_OPERATOR_LENGTH) throw new Error(`--operator は 1〜${MAX_OPERATOR_LENGTH} 文字で指定してください。`);
        o.operator = v;
        i++;
        break;
      }
      case '--reason': {
        const v = value(i, a).trim();
        if (!v || v.length > MAX_REASON_LENGTH) throw new Error(`--reason は 1〜${MAX_REASON_LENGTH} 文字で指定してください。`);
        o.reason = v;
        i++;
        break;
      }
      case '--created-before': {
        const raw = value(i, a);
        const t = Date.parse(raw);
        if (Number.isNaN(t)) throw new Error(`--created-before は ISO 8601 の時刻で指定してください（${raw}）。`);
        o.createdBefore = new Date(t).toISOString();
        i++;
        break;
      }
      default:
        throw new Error(`知らない引数です: ${a}（--help を参照）`);
    }
  }
  if (o.help) return o;
  if (o.resume && o.dryRun) throw new Error('--resume と --dry-run は同時に指定できません。');
  if (o.resume && o.createdBefore) throw new Error('--resume では --created-before を指定しません（状態ファイルのものを使う）。');
  return o;
}

/**
 * 実行の前提（必須の指定）。`--live` の判定（exit 3）より後で見る —— 指定の無い素の実行は、何が欠けていても何もせずに終わる（#1550）。
 * 🔴 `--created-before` は必須（既定を持たない）。アーカイブする実行（再開を含む）は理由も必須。違反を文字列で返す（null なら続けてよい）。
 */
function preconditionProblem(o) {
  if (o.help) return null;
  if (!o.resume && !o.createdBefore)
    return '--created-before <ISO8601> は必須です（この時刻より前に作られた文書だけが対象。既定はありません）。';
  if (o.dryRun || o.reason) return null;
  return 'アーカイブする実行では --reason <理由> が必須です（口のログへ残す。dry-run では任意）。';
}

/** 口へ送る要求の本文。 */
function buildRequestBody({ dryRun, createdBefore, limit, cursor, requestedBy, reason }) {
  const body = { dryRun: Boolean(dryRun), createdBefore, limit };
  if (cursor) body.cursor = cursor;
  if (requestedBy) body.requestedBy = requestedBy;
  if (reason) body.reason = reason;
  return body;
}

const emptyTotals = () => ({ pages: 0, archived: 0, bodyMissing: 0, bodyPresent: 0, skippedPrivateNote: 0 });

/** ページの応答を累計へ積む（新しい累計を返す）。 */
function addTotals(t, res) {
  return {
    pages: t.pages + 1,
    archived: t.archived + (res.archived || 0),
    bodyMissing: t.bodyMissing + (res.bodyMissing || 0),
    bodyPresent: t.bodyPresent + (res.bodyPresent || 0),
    skippedPrivateNote: t.skippedPrivateNote + (res.skippedPrivateNote || 0),
  };
}

/** dry-run の要約（全ページを辿った累計と、最初のページの母集合の件数から作る）。 */
function summarizeDryRun(first, totals, createdBefore) {
  return [
    `createdBefore ${createdBefore} より前に作られた文書のうち:`,
    `  確かめた（未アーカイブ・本文の参照が storage://）: ${first.matched} 件`,
    `    本文のオブジェクトが無い（NoSuchKey）＝アーカイブの対象: ${totals.bodyMissing} 件`,
    `    本文が在る（触らない）: ${totals.bodyPresent} 件`,
    `    個人資料（対象外。触らない）: ${totals.skippedPrivateNote} 件`,
    `  既にアーカイブ済み（対象外）: ${first.alreadyArchived} 件`,
    `  本文の参照が無い・storage:// でない（対象外）: ${first.notStored} 件`,
  ];
}

/** 新規の走査の状態。 */
function newState(o, now = new Date()) {
  return {
    version: STATE_VERSION,
    kind: STATE_KIND,
    startedAt: now.toISOString(),
    filters: { createdBefore: o.createdBefore },
    cursor: null,
    confirmedCursor: null,
    done: false,
    confirmedDone: false,
    canaryPassed: false,
    dlqBaseline: null,
    matched: null,
    totals: emptyTotals(),
  };
}

/** 読んだ状態ファイルが続けてよい形か。違反を文字列で返す（null なら続けてよい）。 */
function stateProblem(state) {
  if (!state || typeof state !== 'object') return '状態ファイルが JSON のオブジェクトではありません。';
  if (state.kind !== STATE_KIND) return `状態ファイルの種類が違います（${state.kind}。期待 ${STATE_KIND}）。再発行の状態ファイルを渡していませんか。`;
  if (state.version !== STATE_VERSION) return `状態ファイルの版が違います（${state.version}。期待 ${STATE_VERSION}）。`;
  if (!state.filters || typeof state.filters.createdBefore !== 'string' || Number.isNaN(Date.parse(state.filters.createdBefore)))
    return '状態ファイルに絞り込み（createdBefore）がありません。';
  if (state.cursor !== null && typeof state.cursor !== 'string') return '状態ファイルの cursor が不正です。';
  if (state.confirmedCursor !== null && typeof state.confirmedCursor !== 'string') return '状態ファイルの confirmedCursor が不正です。';
  if (typeof state.confirmedDone !== 'boolean') return '状態ファイルの confirmedDone が不正です。';
  if (!state.totals || typeof state.totals.archived !== 'number') return '状態ファイルの累計が不正です。';
  return null;
}

/** 1 ページの応答を状態へ積む（新しい状態を返す）。confirmedCursor は動かさない。 */
function applyPage(state, res) {
  return {
    ...state,
    cursor: res.nextCursor ?? null,
    done: res.nextCursor == null,
    matched: state.matched ?? res.matched,
    totals: addTotals(state.totals, res),
  };
}

/** 見張るキューの深さの合計。 */
function watchedDepth(depths, queues) {
  return queues.reduce((n, q) => n + (depths[q] || 0), 0);
}

function formatProgress(state, res, elapsedMs) {
  const t = state.totals;
  return `[page ${t.pages}] 確かめた ${res.selected}（本文なし ${res.bodyMissing} / 本文あり ${res.bodyPresent} / 個人資料 ${res.skippedPrivateNote}）`
    + ` アーカイブ ${res.archived} ・残り ${Math.max(0, res.remaining - res.selected)} ・累計 アーカイブ ${t.archived} ・${Math.round(elapsedMs / 1000)}s`;
}

// --- 実行（I/O） --------------------------------------------------------------

async function main(argv) {
  let o;
  try {
    o = parseArgs(argv);
  } catch (e) {
    warn(`[archive-bodyless-documents] ${e.message}`);
    return 1;
  }
  if (o.help) {
    log(USAGE);
    return 0;
  }
  // NFR, #1550: ここから先は稼働クラスタへ当たる。明示の指定が無ければ何もしない。
  requireLiveOptIn('archive-bodyless-documents', argv, { offline: '--help' });
  const precondition = preconditionProblem(o);
  if (precondition) {
    warn(`[archive-bodyless-documents] ${precondition}`);
    return 1;
  }

  const NS = env('REPUBLISH_NS', 'microservices-platform');
  const INFRA_NS = env('REPUBLISH_INFRA_NS', 'platform-infra');
  const REALM = env('REPUBLISH_REALM', 'platform');
  const DLQ = env('REPUBLISH_DLQ', 'wolverine-dead-letter-queue');
  const WATCH = env('ARCHIVE_WATCH_QUEUES', DEFAULTS.watchQueues).split(',').map((s) => s.trim()).filter(Boolean);
  const POLL_MS = positiveInt('REPUBLISH_POLL_MS', env('REPUBLISH_POLL_MS', '5000'));
  const abacSeed = require('./seed-abac-policies.js');
  const CLIENT_ID = env('REPUBLISH_CLIENT_ID', abacSeed.CLIENT_ID);
  const CLIENT_SECRET = process.env.REPUBLISH_CLIENT_SECRET || abacSeed.clientSecretFromRealm(CLIENT_ID) || '';

  const io = rp.makeIo();
  process.on('exit', () => io.cleanup());
  let state = null;
  let onInterrupt = () => {};
  process.on('SIGINT', () => {
    io.cleanup();
    try { onInterrupt(); } catch (e) { warn(`[archive-bodyless-documents] 状態を戻せませんでした: ${e.message}`); }
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
    const res = await fetch(`${documentUrl}${ROUTE}`, {
      method: 'POST',
      headers: { Authorization: `Bearer ${await freshToken()}`, 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(300000),
    });
    const text = await res.text();
    if (!res.ok) throw new Error(`POST ${ROUTE} が失敗しました（${res.status}）: ${text.slice(0, 300)}`);
    return JSON.parse(text);
  }

  const requestedBy = o.operator || process.env.USER || process.env.USERNAME || null;
  const tags = { requestedBy, reason: o.reason };
  log(`操作者: ${requestedBy ?? '(未指定)'}${o.reason ? ` / 理由: ${o.reason}` : ''}（口のログへ認証済みの主体と並べて残る）`);

  if (o.dryRun) {
    // dry-run は全ページを辿って確かめる（状態ファイルは使わない。途中で止まったらやり直せばよい —— 何も変えていない）。
    let cursor = null;
    let first = null;
    let totals = emptyTotals();
    do {
      const res = await call(buildRequestBody({ dryRun: true, createdBefore: o.createdBefore, limit: o.pageSize, cursor, ...tags }));
      first = first || res;
      totals = addTotals(totals, res);
      log(`  [dry-run page ${totals.pages}] 確かめた ${res.selected}（本文なし ${res.bodyMissing} / 本文あり ${res.bodyPresent} / 個人資料 ${res.skippedPrivateNote}）`);
      cursor = res.nextCursor;
    } while (cursor);
    for (const line of summarizeDryRun(first, totals, o.createdBefore)) log(line);
    log('--dry-run のためアーカイブしません。');
    return 0;
  }

  const existing = rp.loadState(o.stateFile);
  if (o.resume) {
    const problem = existing ? stateProblem(existing) : `状態ファイルがありません: ${o.stateFile}`;
    if (problem) {
      warn(`[archive-bodyless-documents] ${problem}`);
      return 1;
    }
    state = existing;
    if (state.done && state.confirmedDone) {
      log(`状態ファイルは完了しています（累計 アーカイブ ${state.totals.archived} 件）。やり直すなら状態ファイルを消して新規に走らせてください（冪等）。`);
      return 0;
    }
    log(`再開します（開始 ${state.startedAt}・累計 アーカイブ ${state.totals.archived} 件・createdBefore ${state.filters.createdBefore}）。`);
  } else {
    if (existing) {
      warn(`[archive-bodyless-documents] 状態ファイルが既にあります（${o.stateFile}）。続けるなら --resume、やり直すなら状態ファイルを消してください。`);
      return 1;
    }
    state = newState(o);
    rp.saveState(o.stateFile, state);
    log(`新規の走査を始めます（createdBefore ${state.filters.createdBefore}・状態 ${o.stateFile}）。`);
  }

  function stopAtConfirmed() {
    if (!rp.hasUnconfirmed(state)) return;
    state = rp.rollbackToConfirmed(state);
    rp.saveState(o.stateFile, state);
    warn(`状態の cursor を確かめた位置へ戻しました（${state.confirmedCursor ?? '先頭'}）。--resume は確かめていないページをもう一度確かめる（アーカイブ済みは選ばれないので冪等）。`);
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
      warn(`[archive-bodyless-documents] ${e.message}`);
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
      warn(`[archive-bodyless-documents] ${isCanary ? 'カナリアの後に' : ''}DLQ が基準から ${v.growth} 件増えました（許容 ${v.allowed}）。止めます。`);
      warn('  アーカイブのイベントを受けた下流（取り込み・Wiki 同期・グラフ）のどれかが失敗しています。ログで原因を確かめてから --resume。');
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
      warn(`[archive-bodyless-documents] ${e.message}`);
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
      res = await call(buildRequestBody({
        dryRun: false, createdBefore: state.filters.createdBefore, limit: isCanary ? o.canary : o.pageSize, cursor: state.cursor, ...tags,
      }));
    } catch (e) {
      failures++;
      warn(`[archive-bodyless-documents] 失敗 ${failures}/${o.maxConsecutiveFailures}: ${e.message}`);
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
    const nothingInFlight = !rp.hasUnconfirmed(state) && !(res.archived > 0);
    state = applyPage(state, res);
    rp.saveState(o.stateFile, state);
    log(formatProgress(state, res, Date.now() - started));

    if (nothingInFlight) {
      state = rp.confirmChecked(state, { queueWatch: false });
      rp.saveState(o.stateFile, state);
    } else if (o.queueWatch) {
      const after = rp.queueWaitPlan({ ...o, queueWatch: true, isCanary, published: res.archived }).after;
      let d;
      try {
        d = after ? await waitQueueAtMost(after.limit, after.timeoutMs) : depths();
      } catch (e) {
        warn(`[archive-bodyless-documents] ${e.message}`);
        stopAtConfirmed();
        return 1;
      }
      if (!checkDlq(d, isCanary)) return 1;
      if (isCanary && res.archived > 0) {
        state = { ...state, canaryPassed: true };
        rp.saveState(o.stateFile, state);
        log(`カナリア ${res.archived} 件: 見張るキューが空になり、DLQ は増えていません。SC-05 で「アーカイブ済み」に入ったことを確かめてから続けてください。`);
      }
    } else {
      state = rp.confirmChecked(state, { queueWatch: false });
      if (isCanary) state = { ...state, canaryPassed: true };
      rp.saveState(o.stateFile, state);
    }
    if (!state.done && o.sleepMs > 0) await sleep(o.sleepMs);
  }

  if (!(await settle())) return 1;
  const t = state.totals;
  log(`完了: アーカイブ ${t.archived} 件 / 本文あり（触らない） ${t.bodyPresent} 件 / 個人資料（触らない） ${t.skippedPrivateNote} 件（${t.pages} ページ）。`);
  log('確かめ方: もう一度 --dry-run を流し、「アーカイブの対象」が 0 件、「既にアーカイブ済み」が増えていること。');
  return 0;
}

module.exports = {
  main,
  DEFAULTS,
  STATE_VERSION,
  STATE_KIND,
  parseArgs,
  preconditionProblem,
  buildRequestBody,
  addTotals,
  summarizeDryRun,
  newState,
  stateProblem,
  applyPage,
  watchedDepth,
  formatProgress,
};

if (require.main === module) {
  main(process.argv.slice(2))
    .then((code) => process.exit(code))
    .catch((e) => {
      warn(`[archive-bodyless-documents] ${e.message}`);
      process.exit(1);
    });
}
