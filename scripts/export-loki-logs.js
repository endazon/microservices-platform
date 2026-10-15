#!/usr/bin/env node
'use strict';
/*
 * export-loki-logs.js — NFR-18, ADR-0133, IADR-0543 (#1935): 経路 B の Loki から、時間範囲を指定してログを
 * ファイル（JSON Lines）へ書き出す。**読み取り専用**（GET の query_range / query / label の値だけを呼ぶ）。
 * 外部依存ゼロ（Node 18 以降の標準 fetch のみ）。
 *
 * 経路（runbook: docs/operations/loki-log-read-runbook.md）:
 *   kubectl -n platform-infra port-forward svc/loki 3100:3100      # 前段（gate）の口へ。読み取りの身元が要る
 *   LOKI_TOKEN=$(kubectl -n platform-infra get secret observability-gate -o jsonpath='{.data.reader}' | base64 -d)
 *   LOKI_TOKEN="$LOKI_TOKEN" node scripts/export-loki-logs.js --live --from ... --to ... --out ...
 *   🔴 トークンは環境変数 LOKI_TOKEN だけで受け取る（引数で受けるとプロセスの一覧と履歴に残る）。表示もしない。
 *   🔴 前段は GET の読み取り API しか通さない。本スクリプトが呼ぶ道（query_range / query / label/<名>/values）は
 *      その列挙の内側にある（observability-gate.yaml の @reader）。運用の口・削除 API は呼ばない。
 *
 * ページ送り: Loki は 1 回の応答を `limit`（上限 max_entries_limit_per_query＝既定 5000）件で切る。
 *   forward（古い順）に取り、次のページは「前のページの最後の時刻」から**含めて**取り直す。同じ時刻の行は
 *   前のページで書いたもの（ラベル＋時刻＋本文の組）を落とす。ページ全体が同じ時刻で先へ進めないときは、その時刻
 *   （1 ns の幅）だけを上限の件数で取り直して次の時刻へ進む。それでも上限で切れるなら黙って打ち切らず失敗にする
 *   （取りこぼしを成功に見せない）。
 *
 * 稼働クラスタへ当たる（localhost の Loki）ため、--live か LIVE=1 が無ければ何もせずに exit 3（#1550。
 * scripts/live-scripts.json）。--help だけは指定なしで動く。
 *
 * 終了コード: 0 = 成功 / 1 = 失敗（HTTP の誤り・取りこぼしの恐れ・出力先が既に在る等） / 2 = 引数の誤り / 3 = 明示の指定なし。
 */

const fs = require('fs');
const crypto = require('crypto');
const { requireLiveOptIn, withoutLiveFlag } = require('./lib/live-opt-in.js');
const { fetchWithin, msFromEnv } = require('./lib/bounded-wait.js');

const NAME = 'export-loki-logs';
const DEFAULT_URL = 'http://localhost:3100';
const DEFAULT_QUERY = '{job=~".+"}';
// Loki の limits_config.max_entries_limit_per_query の既定（3.0 の実測。-print-config-stderr）。これを超える limit は 400 になる。
const MAX_LIMIT = 5000;
// 1 要求の上限（#1869: 上限の無い fetch は port-forward が切れたまま黙って待ち続ける）。5000 行の 1 ページは数秒で返る（実測）。
const REQUEST_TIMEOUT_MS = msFromEnv('LOKI_EXPORT_REQUEST_MS', 120_000);

const USAGE = `使い方:
  LOKI_TOKEN=<読み取りのトークン> node scripts/${NAME}.js --live --from <時刻> --to <時刻> --out <ファイル> [--query <LogQL>] [--url <URL>] [--limit <件数>]
  LOKI_TOKEN=<読み取りのトークン> node scripts/${NAME}.js --live --summary --from <時刻> --to <時刻> [--query <LogQL>] [--url <URL>]
  node scripts/${NAME}.js --help

  --from / --to  RFC 3339 の時刻。時差（Z か +09:00 等）を必ず付ける（例 2026-10-14T00:00:00+09:00）。--from を含み --to を含まない
  --query        LogQL のログのクエリ（既定 ${DEFAULT_QUERY}）。例 {job="vault-audit"} / {job=~"ai-stock-trading\\\\..+"}
  --url          Loki の口（既定 ${DEFAULT_URL}＝svc/loki の port-forward）。前段の内側（Pod の 3101）なら LOKI_TOKEN は要らない
  --out          書き出し先（JSON Lines。1 行 = {"ts","ns","labels","line"}）。既に在るファイルは上書きしない
  --limit        1 回の応答の件数（既定・上限 ${MAX_LIMIT}）
  --summary      書き出さずに、範囲内の job ごとの件数と最古・最新の時刻だけを出す（消えていないかの確認）
`;

class UsageError extends Error {}

/** RFC 3339（時差つき）の時刻を Unix ナノ秒（BigInt）へ。時差の無い書き方は拒む（PC の時差で 9 時間ずれるのを防ぐ）。 */
function parseInstant(text, flag) {
  const m = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2})?)(?:\.(\d{1,9}))?(Z|[+-]\d{2}:\d{2})$/.exec(String(text || ''));
  if (!m) throw new UsageError(`${flag} は RFC 3339 の時差つきの時刻で与える（例 2026-10-14T00:00:00+09:00）: ${text}`);
  const ms = Date.parse(`${m[1]}${m[3]}`);
  if (Number.isNaN(ms)) throw new UsageError(`${flag} を時刻として読めない: ${text}`);
  const frac = BigInt((m[2] || '').padEnd(9, '0') || '0');
  return BigInt(ms) * 1000000n + frac;
}

/** ナノ秒（BigInt か 10 進の文字列）を RFC 3339（UTC・ナノ秒まで）へ。 */
function formatNs(ns) {
  const v = BigInt(ns);
  const ms = v / 1000000n;
  const iso = new Date(Number(ms)).toISOString(); // 2026-10-14T01:02:03.123Z
  return `${iso.slice(0, 19)}.${(v % 1000000000n).toString().padStart(9, '0')}Z`;
}

function parseArgs(argv) {
  const o = { url: DEFAULT_URL, query: DEFAULT_QUERY, limit: MAX_LIMIT, summary: false, help: false };
  const takes = new Set(['--url', '--query', '--from', '--to', '--out', '--limit']);
  for (let i = 0; i < argv.length; i += 1) {
    const a = argv[i];
    if (a === '--help' || a === '-h') { o.help = true; continue; }
    if (a === '--summary') { o.summary = true; continue; }
    if (!takes.has(a)) throw new UsageError(`知らない引数: ${a}`);
    const v = argv[i + 1];
    if (v === undefined) throw new UsageError(`${a} に値が無い`);
    i += 1;
    o[a.slice(2)] = v;
  }
  if (o.help) return o;
  if (o.from === undefined || o.to === undefined) throw new UsageError('--from と --to は必須');
  o.startNs = parseInstant(o.from, '--from');
  o.endNs = parseInstant(o.to, '--to');
  if (o.endNs <= o.startNs) throw new UsageError('--to は --from より後にする');
  if (!o.summary && !o.out) throw new UsageError('--out は必須（--summary のときは要らない）');
  const n = Number(o.limit);
  if (!Number.isInteger(n) || n < 1 || n > MAX_LIMIT) throw new UsageError(`--limit は 1〜${MAX_LIMIT} の整数`);
  o.limit = n;
  if (!/^https?:\/\/[^/]+$/.test(String(o.url).replace(/\/+$/, ''))) throw new UsageError(`--url は http(s)://host:port の形で与える: ${o.url}`);
  o.url = String(o.url).replace(/\/+$/, '');
  return o;
}

function apiUrl(base, path, params) {
  const u = new URL(`${base}${path}`);
  for (const [k, v] of Object.entries(params)) u.searchParams.set(k, String(v));
  return u.toString();
}

/** 前段の判定を運用者が読める言葉にする（401 = トークン無し・違う、403 = 道が読み取りの列挙の外）。 */
function explainStatus(status) {
  if (status === 401) return '前段がトークンを受け付けなかった（LOKI_TOKEN が無いか違う。Secret observability-gate の reader を使う）';
  if (status === 403) return '前段がこの道を通さなかった（読み取りの身元で GET の読み取り API だけが通る。writer のトークンを使っていないか）';
  if (status === 400) return 'Loki が要求を拒んだ（LogQL の誤り・範囲が max_query_length〔30d1h〕を超える等。本文を見る）';
  return 'Loki か前段が誤りを返した';
}

async function getJson(fetchFn, url, token) {
  const headers = token ? { Authorization: `Bearer ${token}` } : {};
  let res;
  try {
    res = await fetchFn(url, { method: 'GET', headers });
  } catch (e) {
    throw new Error(`Loki へ要求できなかった（port-forward は動いているか）: ${e.message}`);
  }
  const text = await res.text();
  if (!res.ok) {
    throw new Error(`HTTP ${res.status}: ${explainStatus(res.status)}\n  本文: ${text.slice(0, 300)}`);
  }
  try {
    return JSON.parse(text);
  } catch {
    throw new Error(`応答が JSON でない: ${text.slice(0, 200)}`);
  }
}

/** query_range の streams を 1 行ずつへ平らにし、時刻（→ ラベル → 本文）の順に並べる。 */
function flattenStreams(result) {
  const out = [];
  for (const s of result || []) {
    const labels = s.stream || {};
    const lkey = JSON.stringify(Object.keys(labels).sort().map((k) => [k, labels[k]]));
    for (const [ns, line] of s.values || []) out.push({ ns: BigInt(ns), labels, lkey, line });
  }
  out.sort((a, b) => (a.ns < b.ns ? -1 : a.ns > b.ns ? 1 : a.lkey < b.lkey ? -1 : a.lkey > b.lkey ? 1 : a.line < b.line ? -1 : a.line > b.line ? 1 : 0));
  return out;
}

const entryKey = (e) => `${e.ns}\u0000${e.lkey}\u0000${e.line}`;

/**
 * 範囲を古い順にページ送りして、各行を onEntry へ渡す。戻り値は件数と最古・最新。
 * @param {(url: string, init: object) => Promise<Response>} fetchFn
 */
async function exportRange(fetchFn, opts, onEntry) {
  const tieLimit = opts.tieLimit || MAX_LIMIT;
  let start = opts.startNs;
  let seenAtStart = new Set(); // 前のページの最後の時刻（＝今の start）で書いた行
  let count = 0;
  let first = null;
  let last = null;
  let pages = 0;
  const emit = (e) => {
    onEntry(e);
    count += 1;
    if (first === null) first = e.ns;
    last = e.ns;
  };
  const page = async (from, to, limit) => {
    pages += 1;
    const url = apiUrl(opts.url, '/loki/api/v1/query_range', {
      query: opts.query, start: from.toString(), end: to.toString(), limit, direction: 'forward',
    });
    const body = await getJson(fetchFn, url, opts.token);
    if (!body || body.status !== 'success' || !body.data || body.data.resultType !== 'streams') {
      throw new Error(`query_range の応答がログの streams でない（resultType=${body && body.data && body.data.resultType}。メトリクスのクエリを与えていないか）`);
    }
    return flattenStreams(body.data.result);
  };
  for (;;) {
    const entries = await page(start, opts.endNs, opts.limit);
    let fresh = 0;
    for (const e of entries) {
      if (e.ns === start && seenAtStart.has(entryKey(e))) continue;
      emit(e);
      fresh += 1;
    }
    if (entries.length < opts.limit) break; // 範囲の終わりまで読んだ
    const maxNs = entries[entries.length - 1].ns;
    if (maxNs === start && fresh === 0) {
      // ページ全体が同じ時刻で、すべて書き済み。その時刻（1 ns の幅）だけを上限の件数で取り直し、残りを書いて次の時刻へ進む。
      // それでも上限で切れるなら、その時刻の行を全部は読めない —— 黙って打ち切らず失敗にする。
      const tie = await page(start, start + 1n, tieLimit);
      if (tie.length >= tieLimit) {
        throw new Error(`同じ時刻（${formatNs(start)}）に ${tieLimit} 件以上の行があり先へ進めない。範囲を分けても読めない量であり、Loki の max_entries_limit_per_query を上げて取り直す（ここまでの ${count} 件は書いた）`);
      }
      for (const e of tie) if (!seenAtStart.has(entryKey(e))) emit(e);
      start += 1n;
      seenAtStart = new Set();
      if (start >= opts.endNs) break;
      continue;
    }
    const next = new Set(maxNs === start ? seenAtStart : []);
    for (const e of entries) if (e.ns === maxNs) next.add(entryKey(e));
    seenAtStart = next;
    start = maxNs;
  }
  return { count, first, last, pages };
}

/**
 * 範囲内の job ごとの件数（count_over_time）と最古・最新の 1 行の時刻を返す。書き出さない。
 * 🔴 件数は**目安**である。Loki はメトリクスのクエリを時間で分けて評価し、分けた境目にちょうど載る行を数え落とす
 *    （実測: 正時ちょうどの 3 行が数えられず 8000 → 7997。書き出しは 8000 行）。正は書き出しの行数。
 */
async function summarize(fetchFn, opts) {
  const seconds = (opts.endNs - opts.startNs + 999999999n) / 1000000000n;
  const metric = `sum by (job) (count_over_time(${opts.query} [${seconds}s]))`;
  const inst = await getJson(fetchFn, apiUrl(opts.url, '/loki/api/v1/query', { query: metric, time: opts.endNs.toString() }), opts.token);
  const perJob = ((inst.data && inst.data.result) || [])
    .map((r) => ({ job: (r.metric && r.metric.job) || '(job なし)', count: Number(r.value && r.value[1]) }))
    .sort((a, b) => b.count - a.count || (a.job < b.job ? -1 : 1));
  const edge = async (direction) => {
    const b = await getJson(fetchFn, apiUrl(opts.url, '/loki/api/v1/query_range', {
      query: opts.query, start: opts.startNs.toString(), end: opts.endNs.toString(), limit: 1, direction,
    }), opts.token);
    const e = flattenStreams(b.data && b.data.result);
    return e.length ? (direction === 'forward' ? e[0].ns : e[e.length - 1].ns) : null;
  };
  return { perJob, oldest: await edge('forward'), newest: await edge('backward') };
}

async function main(argv, env = process.env, fetchFn = (url, init) => fetchWithin(url, init, REQUEST_TIMEOUT_MS)) {
  let o;
  try {
    o = parseArgs(argv);
  } catch (e) {
    if (e instanceof UsageError) { process.stderr.write(`[${NAME}] ${e.message}\n\n${USAGE}`); return 2; }
    throw e;
  }
  if (o.help) { process.stdout.write(USAGE); return 0; }
  o.token = env.LOKI_TOKEN || '';
  if (!o.token) process.stderr.write(`[${NAME}] LOKI_TOKEN が無い。前段（svc/loki 3100）へは 401 になる。前段の内側（Pod の 3101）へ繋いでいる場合だけ正しい\n`);
  const range = `${formatNs(o.startNs)} 〜 ${formatNs(o.endNs)}（--to は含まない）`;
  try {
    if (o.summary) {
      const s = await summarize(fetchFn, o);
      process.stdout.write(`範囲: ${range}\nクエリ: ${o.query}\n`);
      process.stdout.write(`最古の行: ${s.oldest === null ? '無し' : formatNs(s.oldest)}\n最新の行: ${s.newest === null ? '無し' : formatNs(s.newest)}\n`);
      process.stdout.write(s.perJob.length ? `job ごとの件数（目安。正は書き出しの行数）:\n${s.perJob.map((r) => `  ${r.count}\t${r.job}`).join('\n')}\n` : 'job ごとの件数: 0 件（この範囲にログが無い）\n');
      return 0;
    }
    if (fs.existsSync(o.out)) { process.stderr.write(`[${NAME}] 出力先が既に在る（上書きしない）: ${o.out}\n`); return 1; }
    const fd = fs.openSync(o.out, 'wx', 0o600);
    const hash = crypto.createHash('sha256');
    let r;
    try {
      r = await exportRange(fetchFn, o, (e) => {
        const line = `${JSON.stringify({ ts: formatNs(e.ns), ns: e.ns.toString(), labels: e.labels, line: e.line })}\n`;
        fs.writeSync(fd, line);
        hash.update(line);
      });
    } finally {
      fs.closeSync(fd);
    }
    process.stdout.write(`範囲: ${range}\nクエリ: ${o.query}\n書き出し: ${o.out}（${r.count} 行・${r.pages} ページ）\n`);
    process.stdout.write(`最古の行: ${r.first === null ? '無し' : formatNs(r.first)}\n最新の行: ${r.last === null ? '無し' : formatNs(r.last)}\nsha256: ${hash.digest('hex')}\n`);
    return 0;
  } catch (e) {
    process.stderr.write(`[${NAME}] ${e.message}\n`);
    return 1;
  }
}

if (require.main === module) {
  const argv = process.argv.slice(2);
  if (argv.includes('--help') || argv.includes('-h')) {
    process.stdout.write(USAGE);
    process.exit(0);
  }
  requireLiveOptIn(NAME, argv, { offline: '--help' });
  main(withoutLiveFlag(argv)).then((code) => process.exit(code), (e) => {
    process.stderr.write(`[${NAME}] ${e.stack || e}\n`);
    process.exit(1);
  });
}

module.exports = { parseInstant, formatNs, parseArgs, flattenStreams, exportRange, summarize, main, MAX_LIMIT, UsageError };
