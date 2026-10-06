#!/usr/bin/env node
'use strict';
/*
 * republish-document-updated.js
 *
 * FR-02, FR-06, UC-04, ADR-0013, ADR-0027 / IADR-0503 (#1762):
 *   稼働クラスタの全文書（または条件で絞った文書）へ `DocumentUpdated` を再発行し、射影（Qdrant の索引・Wiki.js・グラフ）を
 *   作り直す。運用仕様書「埋め込みプロバイダの設定・ゼロ保持・再索引」の手順 2 の手段である。
 *
 * 方式（IADR-0503）:
 *   - 発行は DocumentService の管理者だけの口 `POST /documents/republish-updated` が行う（通常の発行の門とアダプタを通る）。
 *     本スクリプトは**量の制御・進捗・中断と再開・DLQ の監視**だけを持つ。イベントを自分で組み立てない。
 *   - 認証は Keycloak の client_credentials（client `abac-seeder`。platform-admin を持つ）。資格情報の解決は
 *     `seed-abac-policies.js` の関数を再利用する（realm ファイルから引く作法を 2 か所へ写さない）。
 *
 * 🔴 **稼働クラスタへ当たる。** `--live`（または LIVE=1）が無ければ何もせずに終わる（#1550）。
 * 🔴 **再発行の前に埋め込み先を確かめる。** public / internal の文書は埋め込み（既定は Voyage）へ進む。
 *    埋め込みが使えない構成で流すと、全件が再試行のあと DLQ（`wolverine-dead-letter-queue`）へ行くだけになる。
 *    これを止めるため、最初のページは小さく（カナリア）、取り込みのキューが空になるまで待って DLQ の増加を見る。
 *
 * 実行方法:
 *   node scripts/republish-document-updated.js --help
 *   node scripts/republish-document-updated.js --live --dry-run           # 件数と内訳だけ（発行しない）
 *   node scripts/republish-document-updated.js --live                     # 全件（カナリア → ページごと）
 *   node scripts/republish-document-updated.js --live --resume            # 状態ファイルから続ける
 *   node scripts/republish-document-updated.js --live --attr confidentiality=internal --page-size 20 --sleep-ms 5000
 *
 * 主な環境変数:
 *   REPUBLISH_DOCUMENT_URL / REPUBLISH_KC_URL（与えれば port-forward を張らない）
 *   REPUBLISH_NS（既定 microservices-platform）/ REPUBLISH_INFRA_NS（既定 platform-infra）
 *   REPUBLISH_REALM（既定 platform）/ REPUBLISH_CLIENT_ID（既定 abac-seeder）/ REPUBLISH_CLIENT_SECRET
 *   REPUBLISH_INGEST_QUEUE（既定 ingestion-service.DocumentUpdated）/ REPUBLISH_DLQ（既定 wolverine-dead-letter-queue）
 *
 * 終了コード: 0=完了（または --max-pages で区切って止めた） / 1=失敗・安全のための停止（状態は残る） /
 *            2=前提未整備（k8s へ到達できない・キューを読めない等） / 3=明示の指定が無い（#1550）
 */

const fs = require('fs');
const { spawn, spawnSync } = require('child_process');
const { requireLiveOptIn } = require('./lib/live-opt-in.js');

const env = (k, d) => process.env[k] || d;
const ROUTE = '/documents/republish-updated';
const DEFAULTS = Object.freeze({
  pageSize: 50,
  sleepMs: 2000,
  maxPages: 0, // 0 = 上限なし
  canary: 10,
  drainTimeoutMs: 300000,
  maxQueueDepth: 200,
  maxDlqGrowth: 20,
  maxConsecutiveFailures: 3,
  stateFile: 'republish-document-updated.state.json',
});
const STATE_VERSION = 1;

const log = (s) => process.stdout.write(`${s}\n`);
const warn = (s) => process.stderr.write(`${s}\n`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const USAGE = `使い方: node scripts/republish-document-updated.js --live [オプション]
  --dry-run                     発行せず、件数・機密区分の内訳・本文なし・門で止まる件数を出す
  --resume                      状態ファイルから続ける（絞り込みは状態ファイルのものを使う）
  --state <file>                状態ファイル（既定 ./${DEFAULTS.stateFile}）
  --page-size <n>               1 ページの件数（既定 ${DEFAULTS.pageSize}・1〜500）
  --sleep-ms <n>                ページの間に待つ時間（既定 ${DEFAULTS.sleepMs}）
  --max-pages <n>               この実行で出すページ数の上限（既定 0＝上限なし。続きは --resume）
  --canary <n>                  最初のページの件数（既定 ${DEFAULTS.canary}・0 で無効）。キューが空になるまで待ち DLQ が増えたら止まる
  --drain-timeout-ms <n>        カナリアの後にキューが空になるのを待つ上限（既定 ${DEFAULTS.drainTimeoutMs}）
  --max-queue-depth <n>         取り込みのキューの深さがこれ以下になるまで次のページを出さない（既定 ${DEFAULTS.maxQueueDepth}）
  --max-dlq-growth <n>          開始時からの DLQ の増加がこれを超えたら止まる（既定 ${DEFAULTS.maxDlqGrowth}）
  --no-queue-watch              RabbitMQ のキューを見ない（深さの待ちも DLQ の監視もしない）
  --max-consecutive-failures <n> 連続でこの回数失敗したら止まる（既定 ${DEFAULTS.maxConsecutiveFailures}）
  --ids <id,id,...>             文書 ID で絞る（500 件まで）
  --attr <key=value>            属性の完全一致で絞る（繰り返し可・AND）
  --created-before <ISO8601>    この時刻より前に作られた文書だけ（既定は新規の走査の開始時刻）`;

// --- 純粋関数（実機なしで試験できるように切り出す） ---------------------------------

function positiveInt(name, raw, { allowZero = false } = {}) {
  const n = Number(raw);
  if (!Number.isInteger(n) || n < 0 || (!allowZero && n === 0))
    throw new Error(`${name} は${allowZero ? '0 以上' : '1 以上'}の整数で指定してください（${raw}）。`);
  return n;
}

/**
 * 引数を解析する。知らない引数・値の欠けた引数は落とす（黙って既定へ倒さない）。
 * `--live` は判定器（lib/live-opt-in.js）が見るので、ここでは読み飛ばす。
 * @param {string[]} argv
 */
function parseArgs(argv) {
  const o = {
    help: false, dryRun: false, resume: false, queueWatch: true,
    pageSize: DEFAULTS.pageSize, sleepMs: DEFAULTS.sleepMs, maxPages: DEFAULTS.maxPages,
    canary: DEFAULTS.canary, drainTimeoutMs: DEFAULTS.drainTimeoutMs,
    maxQueueDepth: DEFAULTS.maxQueueDepth, maxDlqGrowth: DEFAULTS.maxDlqGrowth,
    maxConsecutiveFailures: DEFAULTS.maxConsecutiveFailures,
    stateFile: DEFAULTS.stateFile, ids: null, attributes: null, createdBefore: null,
  };
  const value = (i, name) => {
    if (i + 1 >= argv.length || String(argv[i + 1]).startsWith('--'))
      throw new Error(`${name} に値がありません。`);
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
      case '--page-size': o.pageSize = Math.min(500, positiveInt(a, value(i, a))); i++; break;
      case '--sleep-ms': o.sleepMs = positiveInt(a, value(i, a), { allowZero: true }); i++; break;
      case '--max-pages': o.maxPages = positiveInt(a, value(i, a), { allowZero: true }); i++; break;
      case '--canary': o.canary = Math.min(500, positiveInt(a, value(i, a), { allowZero: true })); i++; break;
      case '--drain-timeout-ms': o.drainTimeoutMs = positiveInt(a, value(i, a)); i++; break;
      case '--max-queue-depth': o.maxQueueDepth = positiveInt(a, value(i, a), { allowZero: true }); i++; break;
      case '--max-dlq-growth': o.maxDlqGrowth = positiveInt(a, value(i, a), { allowZero: true }); i++; break;
      case '--max-consecutive-failures': o.maxConsecutiveFailures = positiveInt(a, value(i, a)); i++; break;
      case '--ids': {
        const ids = value(i, a).split(',').map((s) => s.trim()).filter(Boolean);
        if (ids.length === 0) throw new Error('--ids が空です。');
        o.ids = [...new Set(ids)].sort();
        i++;
        break;
      }
      case '--attr': {
        const kv = value(i, a);
        const eq = kv.indexOf('=');
        if (eq <= 0 || eq === kv.length - 1) throw new Error(`--attr は key=value の形で指定してください（${kv}）。`);
        const k = kv.slice(0, eq);
        o.attributes = o.attributes || {};
        if (k in o.attributes) throw new Error(`--attr ${k} が重複しています。`);
        o.attributes[k] = kv.slice(eq + 1);
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
  if (o.resume && o.dryRun) throw new Error('--resume と --dry-run は同時に指定できません。');
  if (o.resume && (o.ids || o.attributes || o.createdBefore))
    throw new Error('--resume では絞り込みを指定しません（状態ファイルの絞り込みを使う）。');
  return o;
}

/** 絞り込みの正規形（状態ファイルとの突き合わせに使う。キーの順に依らない）。 */
function filtersOf(o) {
  const attributes = o.attributes
    ? Object.fromEntries(Object.keys(o.attributes).sort().map((k) => [k, o.attributes[k]]))
    : null;
  return { ids: o.ids ? [...o.ids].sort() : null, attributes, createdBefore: o.createdBefore || null };
}

/** 新規の走査の状態。`createdBefore` を開始時刻に固定する（途中で作られた文書は作成の経路で発行済み）。 */
function newState(o, now = new Date()) {
  const f = filtersOf(o);
  return {
    version: STATE_VERSION,
    startedAt: now.toISOString(),
    filters: { ...f, createdBefore: f.createdBefore || now.toISOString() },
    cursor: null,
    done: false,
    canaryPassed: false,
    dlqBaseline: null,
    totals: { pages: 0, published: 0, skippedByGate: 0, withoutBody: 0 },
    matched: null,
  };
}

/**
 * 読んだ状態ファイルが続けてよい形かを確かめる。違反を文字列で返す（null なら続けてよい）。
 * 🔴 状態の版・形が合わないものから続けない（カーソルの意味が違えば読み飛ばしか重複になる）。
 */
function stateProblem(state) {
  if (!state || typeof state !== 'object') return '状態ファイルが JSON のオブジェクトではありません。';
  if (state.version !== STATE_VERSION) return `状態ファイルの版が違います（${state.version}。期待 ${STATE_VERSION}）。`;
  if (!state.filters || typeof state.filters.createdBefore !== 'string')
    return '状態ファイルに絞り込み（createdBefore）がありません。';
  if (state.cursor !== null && typeof state.cursor !== 'string') return '状態ファイルの cursor が不正です。';
  if (!state.totals || typeof state.totals.published !== 'number') return '状態ファイルの累計が不正です。';
  return null;
}

/**
 * 新規の走査の絞り込みが、既にある状態ファイルの絞り込みと同じかを判定する（同じなら --resume を促し、違えば別の走査）。
 * 🔴 **違う絞り込みのまま同じ状態ファイルで続けない**（カーソルは絞り込みの集合の中の位置である）。
 */
function sameFilters(state, o) {
  const f = filtersOf(o);
  const s = state.filters || {};
  return JSON.stringify(f.ids) === JSON.stringify(s.ids ?? null)
    && JSON.stringify(f.attributes) === JSON.stringify(s.attributes ?? null)
    && (f.createdBefore === null || f.createdBefore === s.createdBefore);
}

function loadState(file, fsImpl = fs) {
  if (!fsImpl.existsSync(file)) return null;
  return JSON.parse(fsImpl.readFileSync(file, 'utf8'));
}

/** 書きかけで落ちても前の状態が残るよう、一時ファイルへ書いてから置き換える。 */
function saveState(file, state, fsImpl = fs) {
  const tmp = `${file}.tmp`;
  fsImpl.writeFileSync(tmp, `${JSON.stringify(state, null, 2)}\n`);
  fsImpl.renameSync(tmp, file);
}

/** 口へ送る要求の本文。 */
function buildRequestBody(filters, { dryRun, limit, cursor }) {
  const body = { dryRun: Boolean(dryRun) };
  if (!dryRun) body.limit = limit;
  if (cursor) body.cursor = cursor;
  if (filters.createdBefore) body.createdBefore = filters.createdBefore;
  if (filters.ids) body.ids = filters.ids;
  if (filters.attributes) body.attributes = filters.attributes;
  return body;
}

/** 1 ページの応答を状態へ積む（新しい状態を返す。元は変えない）。 */
function applyPage(state, res) {
  const t = state.totals;
  return {
    ...state,
    cursor: res.nextCursor ?? null,
    done: res.nextCursor == null,
    matched: res.matched,
    totals: {
      pages: t.pages + 1,
      published: t.published + (res.published || 0),
      skippedByGate: t.skippedByGate + (res.skippedByGate || 0),
      withoutBody: t.withoutBody + (res.withoutBody || 0),
    },
  };
}

/** 連続失敗の上限に達したか。**上限の回数に達した時点で止まる**（上限 3 なら 3 回目の失敗で止まる）。 */
function shouldStopOnFailures(consecutiveFailures, max) {
  return consecutiveFailures >= max;
}

/** 失敗の後に待つ時間（倍々。上限 60 秒）。 */
function backoffMs(baseMs, consecutiveFailures) {
  return Math.min(60000, Math.max(1000, baseMs) * 2 ** Math.max(0, consecutiveFailures - 1));
}

/**
 * `rabbitmqctl list_queues -q name messages` の出力をキュー名 → 件数へ。
 * `messages` は ready ＋ unacked（再試行で待っている配信も数える）。数値でない行は捨てる。
 */
function parseQueueDepths(text) {
  const out = {};
  for (const line of String(text || '').split('\n')) {
    const m = line.trim().match(/^(\S+)\s+(\d+)$/);
    if (m) out[m[1]] = Number(m[2]);
  }
  return out;
}

/** 開始時からの DLQ の増加（キューが無い＝ 0 件として扱う。ブローカの作り直しで減った場合は 0）。 */
function dlqGrowth(baseline, current) {
  return Math.max(0, (current || 0) - (baseline || 0));
}

/** dry-run の要約（埋め込みへ進む件数＝ public ＋ internal。費用の見積もりの母数）。 */
function summarizeDryRun(res) {
  const by = res.byConfidentiality || {};
  const embedding = (by.public || 0) + (by.internal || 0);
  const lexicalOnly = (by.confidential || 0) + (by.restricted || 0);
  return [
    `対象: ${res.remaining} 件（絞り込みの全件 ${res.matched} 件）`,
    `  機密区分: public ${by.public || 0} / internal ${by.internal || 0} / confidential ${by.confidential || 0} / restricted（欠落・未知を含む） ${by.restricted || 0}`,
    `  埋め込みへ進む（public + internal）: ${embedding} 件 —— 1 チャンクにつき埋め込み 1 回（本文なしは題名などで 1 回）。費用はこの件数と本文の量から見積もる`,
    `  語彙索引だけ（埋め込まない）: ${lexicalOnly} 件`,
    `  本文の所在が無い（取り込みは何もしない。Wiki・グラフの同期だけ動く）: ${res.withoutBody} 件`,
    `  発行の門で止まる（露出の 3 トグルが OFF の個人資料）: ${res.skippedByGate} 件`,
  ];
}

function formatProgress(state, res, elapsedMs) {
  const done = res.matched - res.remaining + res.selected;
  const pct = res.matched > 0 ? ((100 * done) / res.matched).toFixed(1) : '100.0';
  return `[page ${state.totals.pages}] ${done}/${res.matched}（${pct}%） 発行 ${res.published} / 門で止めた ${res.skippedByGate}`
    + ` / 本文なし ${res.withoutBody} ・累計 発行 ${state.totals.published} ・${Math.round(elapsedMs / 1000)}s`;
}

const DLQ_GUIDANCE = (dlq) => [
  `DLQ（${dlq}）が増えています。取り込みが埋め込みか本文の取得に失敗しています。続ける前に原因を直してください:`,
  '  - ゲートウェイに埋め込み先があるか（Voyage の鍵 Embedding__Voyage__ApiKey、または検証スタックの LOCALEMBED=1）',
  '      kubectl -n microservices-platform logs deploy/llmgateway-service --since=15m | grep -E "Embedding call failed|API キーが未設定"',
  '  - 取り込みのログ: kubectl -n microservices-platform logs deploy/ingestion-service --since=15m | grep -E "transient|Ingestion|Exception"',
  '  - DLQ のメッセージは古い状態の写しなので再投入しない。原因を直したら --resume（または状態ファイルを消して最初から）で再発行する。',
  '    DLQ は全サービスで共有している。中身を確かめずに purge しない（管理 UI: kubectl -n platform-infra port-forward svc/rabbitmq 15672）。',
];

// --- 実行（I/O） --------------------------------------------------------------

function makeIo() {
  const forwards = [];
  return {
    portForward(ns, svc, localPort, remotePort) {
      const child = spawn('kubectl', ['-n', ns, 'port-forward', `svc/${svc}`, `${localPort}:${remotePort}`], {
        stdio: ['ignore', 'ignore', 'ignore'],
      });
      forwards.push(child);
    },
    cleanup() {
      for (const c of forwards) {
        try { c.kill(); } catch { /* 片付けの失敗で終了コードを変えない */ }
      }
      forwards.length = 0;
    },
  };
}

async function waitReachable(url, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      await fetch(url, { signal: AbortSignal.timeout(2000) });
      return true;
    } catch {
      await sleep(500);
    }
  }
  return false;
}

function readQueueDepths(infraNs) {
  const r = spawnSync('kubectl', ['-n', infraNs, 'exec', 'deploy/rabbitmq', '--', 'rabbitmqctl', 'list_queues', '-q', 'name', 'messages'],
    { encoding: 'utf8', timeout: 30000 });
  if (r.status !== 0) throw new Error(`RabbitMQ のキューを読めませんでした（exit ${r.status}）: ${String(r.stderr || '').slice(0, 300)}`);
  return parseQueueDepths(r.stdout);
}

async function main(argv) {
  let o;
  try {
    o = parseArgs(argv);
  } catch (e) {
    warn(`[republish-document-updated] ${e.message}`);
    return 1;
  }
  if (o.help) {
    log(USAGE);
    return 0;
  }
  // NFR, #1550: ここから先は稼働クラスタへ当たる。明示の指定が無ければ何もしない。
  requireLiveOptIn('republish-document-updated', argv, { offline: '--help' });

  const NS = env('REPUBLISH_NS', 'microservices-platform');
  const INFRA_NS = env('REPUBLISH_INFRA_NS', 'platform-infra');
  const REALM = env('REPUBLISH_REALM', 'platform');
  const INGEST_QUEUE = env('REPUBLISH_INGEST_QUEUE', 'ingestion-service.DocumentUpdated');
  const DLQ = env('REPUBLISH_DLQ', 'wolverine-dead-letter-queue');
  const abacSeed = require('./seed-abac-policies.js');
  const CLIENT_ID = env('REPUBLISH_CLIENT_ID', abacSeed.CLIENT_ID);
  const CLIENT_SECRET = process.env.REPUBLISH_CLIENT_SECRET || abacSeed.clientSecretFromRealm(CLIENT_ID) || '';

  const io = makeIo();
  process.on('exit', () => io.cleanup());
  let state = null;
  process.on('SIGINT', () => {
    io.cleanup();
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
    // 投入器（18090〜18093）とポートを重ねない。
    io.portForward(NS, 'document-service', 18094, 8080);
    documentUrl = 'http://localhost:18094';
  }
  if (!kcUrl) {
    io.portForward(INFRA_NS, 'keycloak', 18095, 8080);
    kcUrl = 'http://localhost:18095';
  }
  if (!(await waitReachable(`${documentUrl}/health/live`)) ||
      !(await waitReachable(`${kcUrl}/realms/${REALM}/.well-known/openid-configuration`))) {
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
      signal: AbortSignal.timeout(120000),
    });
    const text = await res.text();
    if (!res.ok) throw new Error(`POST ${ROUTE} が失敗しました（${res.status}）: ${text.slice(0, 300)}`);
    return JSON.parse(text);
  }

  if (o.dryRun) {
    const f = filtersOf(o);
    const res = await call(buildRequestBody(f, { dryRun: true }));
    for (const line of summarizeDryRun(res)) log(line);
    log('--dry-run のため発行しません。');
    return 0;
  }

  // 状態: 新規か再開か。
  const existing = loadState(o.stateFile);
  if (o.resume) {
    const problem = existing ? stateProblem(existing) : `状態ファイルがありません: ${o.stateFile}`;
    if (problem) {
      warn(`[republish-document-updated] ${problem}`);
      return 1;
    }
    state = existing;
    if (state.done) {
      log(`状態ファイルは完了しています（累計 発行 ${state.totals.published} 件）。やり直すなら状態ファイルを消して新規に走らせてください。`);
      return 0;
    }
    log(`再開します（開始 ${state.startedAt}・累計 発行 ${state.totals.published} 件・createdBefore ${state.filters.createdBefore}）。`);
  } else {
    if (existing) {
      warn(`[republish-document-updated] 状態ファイルが既にあります（${o.stateFile}）。`
        + (sameFilters(existing, o) ? '続けるなら --resume、' : '絞り込みが違います。別の --state を指定するか、')
        + 'やり直すなら状態ファイルを消してください。');
      return 1;
    }
    state = newState(o);
    saveState(o.stateFile, state);
    log(`新規の走査を始めます（createdBefore ${state.filters.createdBefore}・状態 ${o.stateFile}）。`);
  }

  // DLQ の基準値（開始時）。キューを読めなければ黙って監視なしに倒さず、前提未整備で止める。
  const depths = () => readQueueDepths(INFRA_NS);
  if (o.queueWatch) {
    try {
      const d = depths();
      if (state.dlqBaseline == null) {
        state = { ...state, dlqBaseline: d[DLQ] || 0 };
        saveState(o.stateFile, state);
      }
      log(`キュー: ${INGEST_QUEUE}=${d[INGEST_QUEUE] ?? '(未宣言)'} / ${DLQ}=${d[DLQ] ?? 0}（基準 ${state.dlqBaseline}）`);
    } catch (e) {
      warn(`[republish-document-updated] ${e.message}`);
      warn('キューを見ずに流すなら --no-queue-watch を明示してください（量の制御と DLQ の監視が無くなる）。');
      return 2;
    }
  }

  async function waitQueueAtMost(limit, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
      const d = depths();
      const depth = d[INGEST_QUEUE] || 0;
      if (depth <= limit) return d;
      if (Date.now() > deadline) throw new Error(`取り込みのキューが ${timeoutMs}ms のうちに ${limit} 件以下になりませんでした（${depth} 件）。`);
      await sleep(5000);
    }
  }

  const started = Date.now();
  let failures = 0;
  let pagesThisRun = 0;
  while (!state.done) {
    if (o.maxPages > 0 && pagesThisRun >= o.maxPages) {
      log(`--max-pages ${o.maxPages} に達したので止めます。続けるには --resume。`);
      return 0;
    }
    const isCanary = o.canary > 0 && !state.canaryPassed;
    let res;
    try {
      if (o.queueWatch && !isCanary) await waitQueueAtMost(o.maxQueueDepth, 30 * 60 * 1000);
      res = await call(buildRequestBody(state.filters, {
        dryRun: false, limit: isCanary ? o.canary : o.pageSize, cursor: state.cursor,
      }));
    } catch (e) {
      failures++;
      warn(`[republish-document-updated] 失敗 ${failures}/${o.maxConsecutiveFailures}: ${e.message}`);
      if (shouldStopOnFailures(failures, o.maxConsecutiveFailures)) {
        warn(`連続 ${failures} 回失敗したので止めます。状態は ${o.stateFile}（カーソルはまだ進めていない）。原因を直して --resume。`);
        return 1;
      }
      await sleep(backoffMs(o.sleepMs, failures));
      continue;
    }
    failures = 0;
    pagesThisRun++;
    state = applyPage(state, res);
    saveState(o.stateFile, state);
    log(formatProgress(state, res, Date.now() - started));

    if (o.queueWatch) {
      let d;
      try {
        d = isCanary ? await waitQueueAtMost(0, o.drainTimeoutMs) : depths();
      } catch (e) {
        warn(`[republish-document-updated] ${e.message}`);
        warn(`止めます。状態は ${o.stateFile}。キューが落ち着いたら --resume。`);
        return 1;
      }
      const growth = dlqGrowth(state.dlqBaseline, d[DLQ]);
      const allowed = isCanary ? 0 : o.maxDlqGrowth;
      if (growth > allowed) {
        warn(`[republish-document-updated] ${isCanary ? 'カナリアの後に' : ''}DLQ が開始時から ${growth} 件増えました（許容 ${allowed}）。止めます。`);
        for (const line of DLQ_GUIDANCE(DLQ)) warn(line);
        return 1;
      }
      if (isCanary) {
        state = { ...state, canaryPassed: true };
        saveState(o.stateFile, state);
        log(`カナリア ${res.selected} 件: 取り込みのキューが空になり、DLQ は増えていません。続けます。`);
      }
    } else if (isCanary) {
      state = { ...state, canaryPassed: true };
      saveState(o.stateFile, state);
    }
    if (!state.done && o.sleepMs > 0) await sleep(o.sleepMs);
  }

  log(`完了: 発行 ${state.totals.published} 件 / 門で止めた ${state.totals.skippedByGate} 件 / 本文なし ${state.totals.withoutBody} 件（${state.totals.pages} ページ）。`);
  log('確かめ方: 取り込みのキューが空になってから、Qdrant の各コレクションの points_count が増えていること（読み取りだけ）:');
  log(`  kubectl -n ${INFRA_NS} run qdrant-check --rm -i --restart=Never --image=curlimages/curl -- sh -c \\`);
  log('    \'for c in knowledge_chunks_voyage_3_5 knowledge_chunks_lexical knowledge_chunks_ruri_v3; do curl -s http://qdrant:6333/collections/$c; echo; done\'');
  return 0;
}

module.exports = {
  DEFAULTS,
  STATE_VERSION,
  parseArgs,
  filtersOf,
  newState,
  stateProblem,
  sameFilters,
  loadState,
  saveState,
  buildRequestBody,
  applyPage,
  shouldStopOnFailures,
  backoffMs,
  parseQueueDepths,
  dlqGrowth,
  summarizeDryRun,
  formatProgress,
};

if (require.main === module) {
  main(process.argv.slice(2))
    .then((code) => process.exit(code))
    .catch((e) => {
      warn(`[republish-document-updated] ${e.message}`);
      process.exit(1);
    });
}
