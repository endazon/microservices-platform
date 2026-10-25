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
const { requireLiveOptIn, withoutLiveFlag } = require('./lib/live-opt-in.js');
// IADR-0630: 使い捨てのスタックの判定は live-rw の門と同じ実装を使う（一覧を 2 か所に持たない）。
const { DISPOSABLE_STACKS, checkTarget, kubeContext } = require('./lib/disposable-stack.js');

const TAG = '[e2e-live-sweep]';
const PREFIX = 'e2e-';
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

/** 試験アカウントの役割（realm の `e2e-<role>`。`e2e/live/support/env.ts` の ROLES と同じ並び）。 */
const ALL_ROLES = ['user', 'operator', 'admin', 'noaccess', 'revocable', 'trading-owner'];

/**
 * 掃く種類（live-poc.md §3）。`remove` が null の種類は列挙だけ。並びは消す順: タグは使用中だと 409 になるので、
 * タグを付けうる文書・個人資料の後に置く。
 *
 * 🔴 **視点**（#2234 のレビュー）: `viewers` は、その種類を**作り得る役割すべて**（BFF の作成の口の x-roles。docs/api/openapi.yaml）
 * のうち一覧を読める役割である。一覧は役割ごとに取り、**ID の和集合**で判定する。`deleter` は消す役割（`'viewer'` なら見えた役割）。
 *   - 文書・ABAC ポリシー・データソース・タグ・MCP クライアント: 作成は platform-admin だけ → 視点は e2e-admin だけ。
 *     データソースと MCP クライアントは削除の口が無い（無効化だけ）。`settled` が真の行（無効化済み）は後始末済みとして数えない（IADR-0631）。
 *     文書の一覧は ABAC で絞られる。admin が自分で作った文書が admin に見えるのは seed の「所有者は自分の文書を読める」
 *     （action read・`owner: ${current_user}`）があるときだけなので、`precondition` で毎回確かめ、無ければ視点の欠けとして出す。
 *   - 個人資料: 作成は認証だけ（全役割）で、一覧は**本人のものだけ** → 視点は e2e-revocable を除く 5 役割。消すのは見えた本人。
 *     🔴 e2e-revocable を外す理由（IADR-0630 決定 5）: そのセッションは失効の試験が実行の中で無効にするので、teardown の時点で一覧を
 *     読めない（PR #2234 の e2e-live で実測: 401）。代わりに **e2e-revocable では作らない**（作る側の部品 `createCleanup` が実行時に止める）。
 */
/** 個人資料の視点（e2e-revocable を除く。上の注記）。 */
const NOTE_VIEWERS = ALL_ROLES.filter((r) => r !== 'revocable');
const OWNER_READ = (p) =>
  p && p.action === 'read' && p.isActive !== false && p.documentConditions &&
  JSON.stringify(p.documentConditions.owner) === JSON.stringify(['${current_user}']);

const KINDS = [
  {
    kind: 'documents',
    viewers: ['admin'],
    deleter: 'admin',
    async precondition(get) {
      const policies = await get('/bff/admin/authz/policies');
      if (!Array.isArray(policies) || !policies.some(OWNER_READ)) {
        return '「所有者は自分の文書を読める」の有効なポリシーが無い（admin が作った文書が admin の一覧に出ない）';
      }
      return null;
    },
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
    viewers: NOTE_VIEWERS,
    deleter: 'viewer',
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
    viewers: ['admin'],
    deleter: 'admin',
    async list(get) {
      const body = await get('/bff/admin/authz/policies');
      if (!Array.isArray(body)) throw new Error('一覧の形が違う（配列）');
      return body.map((p) => ({ id: p.id, name: p.name }));
    },
    remove: (item) => [{ method: 'DELETE', path: `/bff/admin/authz/policies/${enc(item.id)}` }],
  },
  {
    kind: 'datasources',
    viewers: ['admin'],
    deleter: 'admin',
    async list(get) {
      const body = await get('/bff/datasources');
      if (!Array.isArray(body)) throw new Error('一覧の形が違う（配列）');
      return body.map((s) => ({ id: s.id, name: s.name, status: s.status }));
    },
    // 🔴 DELETE は実体を消さず status を disabled にする（DataSource.Disable()）。行は残るので、無効化済みは後始末済み（IADR-0631 決定 1）。
    settled: (item) => item.status === 'disabled',
    remove: (item) => [{ method: 'DELETE', path: `/bff/datasources/${enc(item.id)}` }],
  },
  {
    kind: 'mcp-clients',
    viewers: ['admin'],
    deleter: 'admin',
    async list(get) {
      const body = await get('/bff/admin/mcp-clients');
      if (!Array.isArray(body)) throw new Error('一覧の形が違う（配列）');
      return body.map((c) => ({ id: c.clientId, name: c.displayName, enabled: c.enabled }));
    },
    // 🔴 BFF に削除の口が無い（無効化だけ。何度送っても 200）。無効化済みは後始末済み、無効化されていなければ無効化を送る（IADR-0631 決定 1）。
    settled: (item) => item.enabled === false,
    remove: (item) => [{ method: 'POST', path: `/bff/admin/mcp-clients/${enc(item.id)}/disable` }],
  },
  {
    kind: 'tags',
    viewers: ['admin'],
    deleter: 'admin',
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
  /** 後始末済み（削除の口が無く無効化済み）の件数。残骸とは別に出す —— 使い捨てのスタックの外で増えていたら気付けるように（IADR-0631 決定 1）。 */
  let settledTotal = 0;

  const send = async (role, method, p, body) => {
    if (dryRun && method !== 'GET') throw new Error(`dry-run なのに ${method} を送ろうとした（${p}）`);
    if (!p.startsWith('/bff/')) throw new Error(`/bff/ 以外へは送らない: ${p}`);
    if (!sessions.has(role)) {
      let state;
      try {
        state = readState(role);
      } catch (e) {
        throw Object.assign(new Error(e.message), { missingState: true });
      }
      sessions.set(role, cookieHeader(state, opts.edgeUrl));
    }
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

  /** 視点が欠けたもの（その役割の一覧を見ていない）。「残骸 0」を嘘にしないため、件数の行に必ず添える。 */
  const gaps = [];
  for (const k of KINDS) {
    const get = (role) => async (p) => {
      const res = await send(role, 'GET', p);
      if (res.status === 401) throw Object.assign(new Error('セッションが無効（401）'), { gap: true });
      if (res.status !== 200) throw new Error(`GET ${p.split('?')[0]} が ${res.status}`);
      return res.json();
    };
    // ID の和集合（どの役割で見えたか）。
    const seen = new Map();
    const looked = [];
    for (const role of k.viewers) {
      try {
        if (k.precondition) {
          const missing = await k.precondition(get(role));
          if (missing) {
            gaps.push(`${k.kind}@e2e-${role}: ${missing}`);
            continue;
          }
        }
        for (const it of await k.list(get(role))) {
          const entry = seen.get(it.id) || { item: it, seenBy: [] };
          entry.seenBy.push(role);
          seen.set(it.id, entry);
        }
        looked.push(role);
      } catch (e) {
        if (e.gap || e.missingState) gaps.push(`${k.kind}@e2e-${role}: ${e.message}`);
        else failures.push(`${k.kind}@e2e-${role}: 一覧を読めない（${e.message}）`);
      }
    }
    const all = [...seen.values()].filter((e) => isSweepable(e.item.name));
    // 削除の口が無い種類（データソース・MCP クライアント）の無効化済みは「後始末済み」として列挙し、残骸に数えない（IADR-0631 決定 1）。
    const settledOnes = k.settled ? all.filter((e) => k.settled(e.item)) : [];
    const matched = all.filter((e) => !settledOnes.includes(e));
    found += matched.length;
    settledTotal += settledOnes.length;
    for (const { item: it } of settledOnes) log(`  = ${it.id} ${it.name}（後始末済み: 無効化。削除の口が無いので行は残る）`);
    log(
      `${TAG} ${k.kind}: 一致 ${matched.length} 件${settledOnes.length ? `・後始末済み ${settledOnes.length} 件` : ''}（一覧 ${seen.size} 件・視点: ${looked.map((r) => `e2e-${r}`).join(', ') || 'なし'}` +
        `${looked.length < k.viewers.length ? `／見ていない視点: ${k.viewers.filter((r) => !looked.includes(r)).map((r) => `e2e-${r}`).join(', ')}` : ''}）`,
    );
    for (const { item: it, seenBy } of matched) {
      if (dryRun) {
        log(`  - ${it.id} ${it.name}（e2e-${seenBy.join(', e2e-')}）`);
        continue;
      }
      if (!k.remove) {
        log(`  - ${it.id} ${it.name}（消す口が無い。残る）`);
        continue;
      }
      if (!isSweepable(it.name)) throw new Error(`接頭辞が一致しないものを消そうとした: ${it.id}`); // 防御（ここへは来ない）
      // 🔴 消すのは**見えた役割**で。消す役割に見えていないものへ送ると、存在秘匿の 404 を「消えた」と取り違える。
      const role = k.deleter === 'viewer' ? seenBy[0] : k.deleter;
      if (!seenBy.includes(role)) {
        failures.push(`${k.kind}: ${it.id} ${it.name} は e2e-${role} の視点で見えない（消せない。見えた役割: e2e-${seenBy.join(', e2e-')}）`);
        continue;
      }
      let ok = true;
      for (const step of k.remove(it)) {
        try {
          const res = await send(role, step.method, step.path, step.body);
          // 404 は既に消えている（test の afterEach が消した・論理削除の後に完全削除へ進んだ）。見えた役割で送るので、存在秘匿の 404 ではない。
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
        log(`  - ${it.id} ${it.name}（e2e-${role} で消した）`);
      }
    }
  }

  const gapNote =
    (gaps.length ? `（ただし見ていない視点 ${gaps.length}: この視点の残骸は数えていない）` : '') +
    `・後始末済み ${settledTotal} 件（残骸に数えない）`;
  if (gaps.length) log(`${TAG} ⚠ 見ていない視点:\n  - ${gaps.join('\n  - ')}`);
  if (dryRun) {
    log(found ? `${TAG} ⚠ dry-run: 残骸 ${found} 件${gapNote}（消すには --delete）` : `${TAG} dry-run: 残骸 0 件${gapNote}`);
  } else {
    log(`${TAG} delete: 一致 ${found} 件のうち ${removed} 件を消した${gapNote}`);
    // 消す実行で視点が欠けたら、掃き切ったとは言えない（exit 1）。dry-run は警告だけ（赤にするかは波 4 の後に #2235 で決める）。
    for (const g of gaps) failures.push(`見ていない視点: ${g}`);
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

module.exports = { PREFIX, DISPOSABLE_STACKS, ALL_ROLES, NOTE_VIEWERS, KINDS, isSweepable, parseArgs, checkTarget, cookieHeader, sweep, main };

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
