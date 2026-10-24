#!/usr/bin/env node
'use strict';
/*
 * e2e-live-sweep.js — NFR / #2030 / IADR-0629: live E2E の**残骸掃き**（sweep）。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * live の書き込み層（L3）が作るものは名前に `e2e-<runId>-` を付け、test の afterEach（`e2e/live/support/cleanup.ts`）が消す。
 * 消し損ねた残りを、この sweep が**接頭辞 `e2e-` の一致だけ**で拾う（基準書 §7・live-poc.md §3）。
 *
 * 🔴 安全の門（すべて要求を 1 つも送る前に判定する）:
 *   1. `--live`（#1550）が無ければ exit 3 で何もしない。
 *   2. 向け先（`--edge-url`・`--auth-dir`・`--stack`）は既定値を持たない。1 つでも無ければ例外（exit 2）。
 *   3. `--stack` は使い捨てのスタック（DISPOSABLE_STACKS）の名前だけを受け、kubectl の current-context が `k3d-<stack>` で
 *      なければ止まる（exit 2）。常設の PoC（context `rancher-desktop`）・本番へは向けない。エッジの URL は PoC も
 *      使い捨てのスタックも `https://localhost` なので、URL では区別できない。
 *   4. **既定は dry-run**（GET だけ）。消すには `--delete` の明示が要る。dry-run の経路で GET 以外を送ろうとしたら例外。
 *   5. 一致は `name.startsWith('e2e-')` だけ（大文字小文字を区別。文字列でないものは一致しない）。サーバ側の絞り込みは候補を減らす
 *      だけで、判定は必ず手元で行う。**接頭辞が一致しないものは消さない**（scripts.repo.test.js の #2030 節が固定する）。
 *
 * セッションは live-setup が書いた storageState（`<auth-dir>/<role>.json`）の cookie を使う。値は出さない。teardown が失効させる
 * ので、live の実行の中（globalTeardown の失効の前。`E2E_SWEEP`）で流す。
 *
 * 終了コード: 0 = 済み（dry-run で残骸があれば警告だけ）／1 = 一覧・削除の失敗／2 = 門で止めた・引数の誤り／3 = `--live` が無い。
 *
 * 使い方:
 *   node scripts/e2e-live-sweep.js --live --edge-url https://localhost --auth-dir <dir> --stack e2e-live [--dry-run | --delete]
 *   node scripts/e2e-live-sweep.js --help
 */

const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const { requireLiveOptIn, withoutLiveFlag } = require('./lib/live-opt-in.js');

const TAG = '[e2e-live-sweep]';
const PREFIX = 'e2e-';
/** 向けてよい使い捨てのスタック（k3d のクラスタ名。e2e-live.yml の CLUSTER）。 */
const DISPOSABLE_STACKS = ['e2e-live'];
const CSRF_HEADER = 'X-MSP-CSRF';
const MAX_PAGES = 50;

const USAGE = `使い方: node scripts/e2e-live-sweep.js --live --edge-url <origin> --auth-dir <dir> --stack <${DISPOSABLE_STACKS.join('|')}> [--dry-run | --delete]
  既定は --dry-run（接頭辞 ${PREFIX} に一致するものを列挙するだけ）。消すには --delete を明示する。
  向け先は使い捨てのスタックだけ（kubectl の current-context が k3d-<stack> であること）。`;

/** 接頭辞 `e2e-` の一致（純関数）。これだけが消す対象の判定である。 */
function isSweepable(name) {
  return typeof name === 'string' && name.startsWith(PREFIX);
}

const enc = encodeURIComponent;

/**
 * 掃く種類（live-poc.md §3）。`role` はその一覧と削除が許される試験アカウント。`remove` が null の種類は列挙だけ。
 * 並びは消す順: タグは使用中だと 409 になるので、タグを付けうる文書・個人資料の後に置く。
 */
const KINDS = [
  {
    kind: 'documents',
    role: 'admin',
    async list(get) {
      const out = [];
      for (let page = 0, offset = 0; page < MAX_PAGES; page++) {
        // `q` は表題の部分一致（大文字小文字を区別しない）。候補を減らすだけで、判定は isSweepable で行う。
        const body = await get(`/bff/documents?q=${enc(PREFIX)}&limit=100&offset=${offset}`);
        if (!body || !Array.isArray(body.items) || typeof body.total !== 'number') throw new Error('一覧の形が違う（items・total）');
        out.push(...body.items.map((d) => ({ id: d.id, name: d.title })));
        offset += body.items.length;
        if (body.items.length === 0 || offset >= body.total) return out;
      }
      throw new Error(`一覧が ${MAX_PAGES} ページで終わらない`);
    },
    remove: (item) => [{ method: 'DELETE', path: `/bff/documents/${enc(item.id)}` }],
  },
  {
    kind: 'private-notes',
    role: 'user',
    async list(get) {
      const body = await get('/bff/private-notes');
      if (!body || !Array.isArray(body.notes)) throw new Error('一覧の形が違う（notes）');
      return body.notes.map((n) => ({ id: n.id, name: n.title, deleted: n.deleted === true }));
    },
    // 論理削除（ゴミ箱へ）→ 完全削除。ゴミ箱に在るものは完全削除だけ。
    remove: (item) => [
      ...(item.deleted ? [] : [{ method: 'DELETE', path: `/bff/private-notes/${enc(item.id)}` }]),
      { method: 'POST', path: '/bff/private-notes/purge', body: { ids: [item.id] } },
    ],
  },
  {
    kind: 'abac-policies',
    role: 'admin',
    async list(get) {
      const body = await get('/bff/admin/authz/policies');
      if (!Array.isArray(body)) throw new Error('一覧の形が違う（配列）');
      return body.map((p) => ({ id: p.id, name: p.name }));
    },
    remove: (item) => [{ method: 'DELETE', path: `/bff/admin/authz/policies/${enc(item.id)}` }],
  },
  {
    kind: 'datasources',
    role: 'admin',
    async list(get) {
      const body = await get('/bff/datasources');
      if (!Array.isArray(body)) throw new Error('一覧の形が違う（配列）');
      return body.map((s) => ({ id: s.id, name: s.name }));
    },
    remove: (item) => [{ method: 'DELETE', path: `/bff/datasources/${enc(item.id)}` }],
  },
  {
    kind: 'mcp-clients',
    role: 'admin',
    async list(get) {
      const body = await get('/bff/admin/mcp-clients');
      if (!Array.isArray(body)) throw new Error('一覧の形が違う（配列）');
      return body.map((c) => ({ id: c.clientId, name: c.displayName }));
    },
    // BFF に削除の口が無い（無効化だけ）。列挙して残ることを示す。
    remove: null,
  },
  {
    kind: 'tags',
    role: 'admin',
    async list(get) {
      const body = await get('/bff/tags');
      if (!body || !Array.isArray(body.tags)) throw new Error('一覧の形が違う（tags）');
      return body.tags.map((t) => ({ id: t.id, name: t.name }));
    },
    remove: (item) => [{ method: 'DELETE', path: `/bff/tags/${enc(item.id)}` }],
  },
];

/** 引数を読む。誤りは例外（呼び出し側が exit 2 にする）。`--live` は外してから渡す。 */
function parseArgs(argv) {
  const opts = { edgeUrl: null, authDir: null, stack: null, mode: null };
  const value = (i, name) => {
    const v = argv[i + 1];
    if (v === undefined || v === '' || v.startsWith('--')) throw new Error(`${name} に値が無い`);
    return v;
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--edge-url') opts.edgeUrl = value(i++, a);
    else if (a === '--auth-dir') opts.authDir = value(i++, a);
    else if (a === '--stack') opts.stack = value(i++, a);
    else if (a === '--dry-run' || a === '--delete') {
      const mode = a === '--delete' ? 'delete' : 'dry-run';
      if (opts.mode && opts.mode !== mode) throw new Error('--dry-run と --delete は同時に指定できない');
      opts.mode = mode;
    } else throw new Error(`知らない引数: ${a}`);
  }
  for (const [name, v] of [['--edge-url', opts.edgeUrl], ['--auth-dir', opts.authDir], ['--stack', opts.stack]]) {
    if (!v) throw new Error(`${name} が無い（向け先は明示する。既定値は持たない）`);
  }
  let url;
  try {
    url = new URL(opts.edgeUrl);
  } catch {
    throw new Error(`--edge-url が URL ではない: ${opts.edgeUrl}`);
  }
  if (url.pathname !== '/' || url.search || url.hash) throw new Error(`--edge-url はオリジンだけを書く: ${opts.edgeUrl}`);
  opts.edgeUrl = url.origin;
  opts.mode = opts.mode || 'dry-run';
  return opts;
}

/** 向け先が使い捨てのスタックか（要求の前に呼ぶ）。違えば例外。 */
function checkTarget(stack, currentContext) {
  if (!DISPOSABLE_STACKS.includes(stack)) {
    throw new Error(`--stack は使い捨てのスタックだけを受ける（${DISPOSABLE_STACKS.join(' / ')}）: ${stack}`);
  }
  let ctx;
  try {
    ctx = String(currentContext() || '').trim();
  } catch (e) {
    throw new Error(`kubectl の current-context を読めない（どのクラスタへ当たるか分からないまま流さない）: ${e.message}`);
  }
  if (ctx !== `k3d-${stack}`) {
    throw new Error(`kubectl の current-context が k3d-${stack} でない（${ctx || '空'}）。使い捨てのスタックの外へは向けない`);
  }
}

function kubeContext() {
  const r = spawnSync('kubectl', ['config', 'current-context'], { encoding: 'utf8' });
  if (r.error) throw r.error;
  if (r.status !== 0) throw new Error(String(r.stderr || '').trim() || `exit ${r.status}`);
  return r.stdout;
}

/** storageState の cookie から、到達先へ送る Cookie ヘッダを作る（値は返り値にしか載せない）。 */
function cookieHeader(state, edgeUrl, nowSeconds = Date.now() / 1000) {
  const host = new URL(edgeUrl).hostname;
  const cookies = (state && Array.isArray(state.cookies) ? state.cookies : []).filter((c) => {
    const domain = String(c.domain || '').replace(/^\./, '');
    const hostOk = domain === host || host.endsWith(`.${domain}`);
    const alive = typeof c.expires !== 'number' || c.expires < 0 || c.expires > nowSeconds;
    return hostOk && alive;
  });
  if (cookies.length === 0) throw new Error(`到達先 ${host} の cookie が無い`);
  return cookies.map((c) => `${c.name}=${c.value}`).join('; ');
}

/**
 * 掃く本体。deps: { fetch, readState(role) => object, log(line) }。返り値は終了コード（0 / 1）。
 * 🔴 dry-run の経路は GET だけ。GET 以外を送ろうとしたら例外にする（消す経路の取り違えを要求の手前で止める）。
 */
async function sweep(opts, deps) {
  const { fetch, readState, log } = deps;
  const dryRun = opts.mode !== 'delete';
  const sessions = new Map();
  const failures = [];
  let found = 0;
  let removed = 0;

  const send = async (role, method, p, body) => {
    if (dryRun && method !== 'GET') throw new Error(`dry-run なのに ${method} を送ろうとした（${p}）`);
    if (!p.startsWith('/bff/')) throw new Error(`/bff/ 以外へは送らない: ${p}`);
    if (!sessions.has(role)) sessions.set(role, cookieHeader(readState(role), opts.edgeUrl));
    const headers = { Accept: 'application/json', Cookie: sessions.get(role) };
    if (method !== 'GET') headers[CSRF_HEADER] = '1';
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    return fetch(new URL(p, opts.edgeUrl).toString(), {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      redirect: 'manual',
    });
  };

  for (const k of KINDS) {
    let items;
    try {
      items = await k.list(async (p) => {
        const res = await send(k.role, 'GET', p);
        if (res.status !== 200) throw new Error(`GET ${p.split('?')[0]} が ${res.status}`);
        return res.json();
      });
    } catch (e) {
      failures.push(`${k.kind}: 一覧を読めない（${e.message}）`);
      continue;
    }
    const matched = items.filter((it) => isSweepable(it.name));
    found += matched.length;
    log(`${TAG} ${k.kind}: 一致 ${matched.length} 件（一覧 ${items.length} 件・e2e-${k.role}）`);
    for (const it of matched) {
      if (dryRun) {
        log(`  - ${it.id} ${it.name}`);
        continue;
      }
      if (!k.remove) {
        log(`  - ${it.id} ${it.name}（消す口が無い。残る）`);
        continue;
      }
      if (!isSweepable(it.name)) throw new Error(`接頭辞が一致しないものを消そうとした: ${it.id}`); // 防御（ここへは来ない）
      let ok = true;
      for (const step of k.remove(it)) {
        try {
          const res = await send(k.role, step.method, step.path, step.body);
          // 404 は既に消えている（test の afterEach が消した・論理削除の後に完全削除へ進んだ）。
          if (res.status !== 404 && (res.status < 200 || res.status >= 300)) {
            failures.push(`${k.kind}: ${it.id} ${it.name} を消せない（${step.method} ${step.path.split('?')[0]} が ${res.status}）`);
            ok = false;
            break;
          }
        } catch (e) {
          failures.push(`${k.kind}: ${it.id} ${it.name} を消せない（${e.message}）`);
          ok = false;
          break;
        }
      }
      if (ok) {
        removed++;
        log(`  - ${it.id} ${it.name}（消した）`);
      }
    }
  }

  if (dryRun) {
    log(found ? `${TAG} ⚠ dry-run: 残骸 ${found} 件（消すには --delete）` : `${TAG} dry-run: 残骸は無い`);
  } else {
    log(`${TAG} delete: 一致 ${found} 件のうち ${removed} 件を消した`);
  }
  if (failures.length) {
    log(`${TAG} ✗ 失敗 ${failures.length} 件:\n  - ${failures.join('\n  - ')}`);
    return 1;
  }
  return 0;
}

/** 入口。deps は試験が差し替える（fetch・kubectl の context・state の読み込み・出力）。返り値は終了コード。 */
async function main(argv, deps = {}) {
  const log = deps.log || ((line) => process.stdout.write(`${line}\n`));
  const err = deps.err || ((line) => process.stderr.write(`${line}\n`));
  if (argv.includes('--help')) {
    log(USAGE);
    return 0;
  }
  requireLiveOptIn('e2e-live-sweep', argv, { offline: '--help', env: deps.env });
  let opts;
  try {
    opts = parseArgs(withoutLiveFlag(argv));
    checkTarget(opts.stack, deps.currentContext || kubeContext);
  } catch (e) {
    err(`${TAG} 止めた: ${e.message}\n${USAGE}`);
    return 2;
  }
  log(`${TAG} ${opts.mode}: ${opts.edgeUrl}（stack ${opts.stack}）`);
  const readState =
    deps.readState ||
    ((role) => {
      const file = path.join(opts.authDir, `${role}.json`);
      if (!fs.existsSync(file)) throw new Error(`e2e-${role} の state が無い（${file}）`);
      return JSON.parse(fs.readFileSync(file, 'utf8'));
    });
  return sweep(opts, { fetch: deps.fetch || globalThis.fetch, readState, log });
}

module.exports = { PREFIX, DISPOSABLE_STACKS, KINDS, isSweepable, parseArgs, checkTarget, cookieHeader, sweep, main };

if (require.main === module) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (e) => {
      process.stderr.write(`${TAG} ${e && e.stack ? e.stack : e}\n`);
      process.exitCode = 1;
    },
  );
}
