#!/usr/bin/env node
'use strict';
/*
 * check-age-apkindex.js — NFR / #2005 / IADR-0568
 *
 * platform-backup のイメージ（deploy/local/platform-backup/image/Dockerfile）が固定する age の版（ARG AGE_VERSION）と、
 * 取得元の Alpine の APKINDEX（<ALPINE_MIRROR>/<ALPINE_BRANCH>/community/<arch>/APKINDEX.tar.gz の `P:age` の `V:`）を
 * 突き合わせる。外部依存ゼロ（Node 22 の fetch と zlib だけ）。
 *
 * 背景（同型の事故が 2 回。運用規約の「検査器の追加は同型事故 2 回から」に当たる）:
 *   Alpine の安定版ブランチは各パッケージの最新のリリースしか置かない。上流が age の `-rN` を作り直すと古い apk が消え、
 *   Dockerfile の取得が 404 で落ちて Images の `build-local (platform-backup)` が全 PR で赤くなる。
 *   - 1 回目 2026-10-08（#1825）: `1.3.1-r6` が消えた。
 *   - 2 回目 2026-10-20（#2003）: `1.3.2-r0` が消えた。2026-10-14 に r1 が作り直されてから 6 日間、誰も気づかなかった。
 *   本スクリプトを日次（.github/workflows/age-apkindex-watch.yml）で走らせ、食い違いを専用 issue に起票する。
 *
 * 🔴 **版を自動で上げない。** 上げるのは運用 Runbook（platform-infra-backup-runbook.md）§6 の手順で、人が sha256 を確かめてから。
 *
 * 状態と終了コード:
 *   - ok        : 全アーキテクチャの APKINDEX の版が AGE_VERSION と一致した。exit 0・起票しない。
 *   - drift     : どれかのアーキテクチャで版が違う（または age が APKINDEX に無い）。exit 0・`--upsert-issue` なら起票／上書き。
 *   - unverified: APKINDEX に届かない・読めない（ネットワーク断・5xx・壊れた圧縮）。exit 0・**起票しない**（誤った起票をしない）。
 *                 🔴 **404 / 410 はここへ入れない**（下の error）。取得元が恒久的に無い（ブランチの EOL でミラーから消えた等）のに
 *                 unverified にすると、毎日緑のまま検知が止まる（#2010 の AI レビュー 🟡）。
 *                 warning を出し、`scanned`（突き合わせられたアーキテクチャの数）で「一致」と「動いていない」を区別する。
 *                 一部のアーキテクチャだけ読めて、そこで食い違ったら drift（読めた分の食い違いは本物である）。
 *   - error     : Dockerfile から ARG を読めない（本リポジトリの欠陥）、または APKINDEX が 404 / 410（取得元が恒久的に無い）。
 *                 exit 1（ワークフローの ci-failure-issue が起票する）。
 *   起票が要るのに書けない（token 不在・API 失敗）ときも exit 1（検知を黙って捨てない）。自動ではクローズしない。
 *
 * 使い方:
 *   node scripts/check-age-apkindex.js --out <path.json> [--upsert-issue] [--dockerfile <path>]
 *   node scripts/check-age-apkindex.js --self-test
 */
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');
const { emit } = require('./lib/ci-annotate.js');

const DOCKERFILE_REL = 'deploy/local/platform-backup/image/Dockerfile';
const RUNBOOK_REL = 'docs/operations/platform-infra-backup-runbook.md';
/** Dockerfile が sha256 を固定しているアーキテクチャ（`AGE_APK_SHA256_<ARCH>`）。 */
const ARCHES = ['x86_64', 'aarch64'];
const PACKAGE = 'age';
const DRIFT_LABEL = 'age-apkindex-drift';
const DRIFT_MARKER = '<!-- age-apkindex-drift -->';
const DRIFT_TITLE = 'chore(NFR): platform-backup の age の版が Alpine の APKINDEX とずれている（自動起票）';
const FETCH_TIMEOUT_MS = 30000;

function parseArgs(argv) {
  const a = { selfTest: false, out: null, upsertIssue: false, dockerfile: null };
  for (let i = 0; i < argv.length; i++) {
    const t = argv[i];
    if (t === '--self-test') a.selfTest = true;
    else if (t === '--upsert-issue') a.upsertIssue = true;
    else if (t === '--out') a.out = argv[++i];
    else if (t === '--dockerfile') a.dockerfile = argv[++i];
  }
  return a;
}

/** Dockerfile の `ARG NAME=value` を読む。必要な 3 つが揃わなければ null（呼び出し側が error にする）。 */
function readPinned(text) {
  const arg = (name) => {
    const m = new RegExp(`^ARG ${name}=(\\S+)\\s*$`, 'm').exec(text);
    return m ? m[1] : null;
  };
  const pinned = { version: arg('AGE_VERSION'), branch: arg('ALPINE_BRANCH'), mirror: arg('ALPINE_MIRROR') };
  return pinned.version && pinned.branch && pinned.mirror ? pinned : null;
}

/** ustar を読み、名前が `name` の項目の中身を返す（無ければ null）。APKINDEX.tar.gz は gzip の多重メンバー。 */
function tarEntry(buf, name) {
  let off = 0;
  while (off + 512 <= buf.length) {
    const header = buf.subarray(off, off + 512);
    const entryName = header.subarray(0, 100).toString('utf8').replace(/\0[\s\S]*$/, '');
    if (!entryName) return null; // 終端の 0 ブロック
    const size = parseInt(header.subarray(124, 136).toString('utf8').replace(/\0[\s\S]*$/, '').trim() || '0', 8);
    if (!Number.isFinite(size) || size < 0) throw new Error(`tar の項目 ${entryName} の大きさを読めない`);
    if (entryName === name) return buf.subarray(off + 512, off + 512 + size);
    off += 512 + Math.ceil(size / 512) * 512;
  }
  return null;
}

/** APKINDEX の本文から `P:<pkg>` の記録の `V:` を返す（無ければ null）。記録は空行で区切られる。 */
function versionOf(apkindex, pkg = PACKAGE) {
  for (const rec of String(apkindex).split(/\n\n+/)) {
    if (new RegExp(`^P:${pkg.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`, 'm').test(rec)) {
      const v = /^V:(\S+)$/m.exec(rec);
      return v ? v[1] : null;
    }
  }
  return null;
}

/** 取得元が恒久的に無いことを表す HTTP 状態（一過性の 5xx・ネットワーク断とは分ける）。 */
const PERMANENT_STATUSES = new Set([404, 410]);

/** APKINDEX.tar.gz（バイト列）から版を読む。圧縮・tar が壊れていれば例外。 */
function versionFromIndexArchive(gz, pkg = PACKAGE) {
  const tar = zlib.gunzipSync(gz);
  const entry = tarEntry(tar, 'APKINDEX');
  if (!entry) throw new Error('APKINDEX.tar.gz に APKINDEX が無い');
  return versionOf(entry.toString('utf8'), pkg);
}

function indexUrl(pinned, arch) {
  return `${pinned.mirror.replace(/\/+$/, '')}/${pinned.branch}/community/${arch}/APKINDEX.tar.gz`;
}

async function fetchIndex(url) {
  const res = await fetch(url, { signal: AbortSignal.timeout(FETCH_TIMEOUT_MS) });
  if (!res.ok) {
    const e = new Error(`HTTP ${res.status} for ${url}`);
    e.status = res.status;
    throw e;
  }
  return Buffer.from(await res.arrayBuffer());
}

/**
 * 全体の結合点。例外は投げない。`fetchFn(url) → Promise<Buffer>` は差し替え可能（自己試験はネットワークを叩かない）。
 * @returns {Promise<object>} status: ok | drift | unverified | error
 */
async function resolve({ dockerfileText, fetchFn = fetchIndex } = {}) {
  const checkedAt = new Date().toISOString();
  const pinned = readPinned(dockerfileText || '');
  if (!pinned) {
    return {
      status: 'error', checkedAt, scanned: 0, arches: [],
      reason: `${DOCKERFILE_REL} から ARG AGE_VERSION / ALPINE_BRANCH / ALPINE_MIRROR を読めない（本リポジトリの欠陥）`,
    };
  }
  const arches = [];
  for (const arch of ARCHES) {
    const url = indexUrl(pinned, arch);
    try {
      const latest = versionFromIndexArchive(await fetchFn(url));
      arches.push({ arch, url, latest, status: latest === pinned.version ? 'ok' : 'drift' });
    } catch (e) {
      const permanent = Boolean(e && PERMANENT_STATUSES.has(e.status));
      arches.push({ arch, url, latest: null, status: permanent ? 'missing' : 'unverified', reason: String(e && e.message ? e.message : e) });
    }
  }
  const scanned = arches.filter((a) => a.status === 'ok' || a.status === 'drift').length;
  const drifted = arches.filter((a) => a.status === 'drift');
  const missing = arches.filter((a) => a.status === 'missing');
  const base = { checkedAt, pinned, scanned, expected: ARCHES.length, arches };
  // 🔴 取得元が恒久的に無い（404 / 410）は unverified に混ぜない。黙って緑を続けると検知が止まる。
  if (missing.length) {
    const why = missing.map((a) => `${a.arch}: ${a.reason}`).join(' / ');
    return {
      status: 'error', ...base,
      reason: `APKINDEX が恒久的に無い（${why}）。ALPINE_BRANCH（${pinned.branch}）が EOL でミラーから消えた、または ALPINE_MIRROR の誤りを疑う。Runbook §6 でベースと一緒に上げる`,
    };
  }
  if (drifted.length) {
    const what = drifted.map((a) => `${a.arch}: ${a.latest === null ? 'APKINDEX に age が無い' : a.latest}`).join(' / ');
    return { status: 'drift', ...base, reason: `固定 ${pinned.version} に対し APKINDEX の版が違う（${what}）` };
  }
  if (scanned < ARCHES.length) {
    const why = arches.filter((a) => a.status === 'unverified').map((a) => `${a.arch}: ${a.reason}`).join(' / ');
    return { status: 'unverified', ...base, reason: `APKINDEX に届かない・読めない（${why}）` };
  }
  return { status: 'ok', ...base, reason: `全 ${scanned} アーキテクチャで ${pinned.version} が APKINDEX の版と一致` };
}

function exitCodeFor(result) {
  return result && result.status === 'error' ? 1 : 0;
}

/** 専用 issue の本文（純関数）。 */
function renderDriftIssue(result, { date, runId } = {}) {
  const rows = result.arches.map((a) => `| ${a.arch} | \`${result.pinned.version}\` | ${a.latest === null ? (a.status === 'unverified' ? '（届かない）' : '（age が無い）') : `\`${a.latest}\``} | ${a.status} |`);
  return [
    DRIFT_MARKER,
    `<!-- age-apkindex-drift:run:${runId || 'local'} -->`,
    `## platform-backup の age の版が Alpine の APKINDEX とずれている（${date || result.checkedAt} の日次検査で検知）`,
    '',
    `\`${DOCKERFILE_REL}\` の \`ARG AGE_VERSION\` が、取得元（\`${result.pinned.mirror}/${result.pinned.branch}/community/<arch>/APKINDEX.tar.gz\`）の版と違う。`,
    'Alpine の安定版ブランチは最新のリリースしか置かないので、固定した apk はもう取れない（Images の `build-local (platform-backup)` が 404 で落ちる）。',
    '',
    `**${result.reason}**`,
    '',
    '| アーキテクチャ | 固定 | APKINDEX | 判定 |',
    '| --- | --- | --- | --- |',
    ...rows,
    '',
    '### 上げる手順（自動では上げない）',
    '',
    `[運用 Runbook](../blob/develop/${RUNBOOK_REL}) §6 の手順で、人が両アーキテクチャの sha256 を確かめてから上げる。`,
    '`AGE_VERSION`・`AGE_APK_SHA256_X86_64`・`AGE_APK_SHA256_AARCH64`、`scripts/k8s-local-images.sh` のタグ、2 つの CronJob の `image` を同じ値へ上げ、',
    '`node scripts/platform-backup.test.js` を通す。PR 本文に `Closes #<この issue の番号>` を書く。',
    '',
    '---',
    '本 issue は `.github/workflows/age-apkindex-watch.yml`（`scripts/check-age-apkindex.js --upsert-issue`）が、ずれが続く間は毎日本文を上書きする。**自動ではクローズしない**（IADR-0568）。',
  ].join('\n');
}

async function ghRequest(method, url, token, body) {
  const res = await fetch(url, {
    method,
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText} for ${method} ${url}`);
  return res.json();
}

/**
 * drift のときだけ専用 issue を upsert する。ok / unverified / error では何もしない（自動クローズもしない）。
 * `api(method, url, body)` は差し替え可能（自己試験は実ネットワークを叩かない）。
 */
async function upsertDriftIssue({ result, repo, token, date, runId, api } = {}) {
  if (!result || result.status !== 'drift') return { action: 'none' };
  if (!repo || !token) throw new Error('版のずれを検知したが、起票に要る GITHUB_REPOSITORY / GITHUB_TOKEN が無い');
  const call = api || ((method, url, body) => ghRequest(method, url, token, body));
  const base = `https://api.github.com/repos/${repo}`;
  try {
    await call('POST', `${base}/labels`, { name: DRIFT_LABEL, color: 'd93f0b', description: 'platform-backup の age の版と Alpine の APKINDEX のずれ（age-apkindex-watch.yml が自動起票）' });
  } catch (e) {
    if (!/^422/.test(String(e.message))) throw e; // 既にあれば 422
  }
  const body = renderDriftIssue(result, { date, runId });
  const open = await call('GET', `${base}/issues?state=open&labels=${encodeURIComponent(DRIFT_LABEL)}&per_page=100`);
  const found = (open || []).find((i) => !i.pull_request && String(i.body || '').includes(DRIFT_MARKER));
  if (found) {
    const r = await call('PATCH', `${base}/issues/${found.number}`, { body });
    return { action: 'updated', number: r && r.number ? r.number : found.number };
  }
  const r = await call('POST', `${base}/issues`, { title: DRIFT_TITLE, labels: [DRIFT_LABEL], body });
  if (!r || !Number.isInteger(r.number)) throw new Error('issue を作成したが番号を読み戻せない');
  return { action: 'created', number: r.number };
}

// ---- 自己試験 -------------------------------------------------------------------------------------------

/** 自己試験用の APKINDEX.tar.gz を作る（ustar ＋ gzip。実物と同じく署名の項目を先に置く）。 */
function makeIndexArchive(apkindexText) {
  const entry = (name, content) => {
    const data = Buffer.from(content, 'utf8');
    const h = Buffer.alloc(512);
    h.write(name, 0, 'utf8');
    h.write('0000644\0', 100);
    h.write('0000000\0', 108);
    h.write('0000000\0', 116);
    h.write(`${data.length.toString(8).padStart(11, '0')}\0`, 124);
    h.write('00000000000\0', 136);
    h.write('        ', 148);
    h.write('0', 156);
    h.write('ustar\0', 257);
    h.write('00', 263);
    let sum = 0;
    for (const b of h) sum += b;
    h.write(`${sum.toString(8).padStart(6, '0')}\0 `, 148);
    return Buffer.concat([h, data, Buffer.alloc((512 - (data.length % 512)) % 512)]);
  };
  // 実物は署名の項目と本体が別の gzip メンバーである（多重メンバーを読めることも確かめる）。
  return Buffer.concat([
    zlib.gzipSync(entry('.SIGN.RSA.alpine-devel@lists.alpinelinux.org-6165ee59.rsa.pub', 'sig')),
    zlib.gzipSync(Buffer.concat([entry('DESCRIPTION', 'v3.24.0-0-g0\n'), entry('APKINDEX', apkindexText), Buffer.alloc(1024)])),
  ]);
}

const FIXTURE_DOCKERFILE = [
  'FROM x',
  'ARG AGE_VERSION=1.3.2-r1',
  'ARG AGE_APK_SHA256_X86_64=aa',
  'ARG ALPINE_BRANCH=v3.24',
  'ARG ALPINE_MIRROR=https://dl-cdn.alpinelinux.org/alpine',
].join('\n');
const indexWith = (v) => makeIndexArchive(`C:Q1x\nP:agetty\nV:9.9-r0\n\nC:Q1y\nP:age\nV:${v}\nA:x86_64\n\nC:Q1z\nP:zstd\nV:1.5-r0\n`);

async function selfTest() {
  const sameFetch = (v) => async () => indexWith(v);
  const cases = [
    {
      name: 'readPinned: Dockerfile の ARG から版・ブランチ・ミラーを読む',
      run: async () => readPinned(FIXTURE_DOCKERFILE),
      expect: (p) => p.version === '1.3.2-r1' && p.branch === 'v3.24' && p.mirror === 'https://dl-cdn.alpinelinux.org/alpine',
    },
    {
      name: 'versionFromIndexArchive: 多重メンバーの gzip と tar を読み、P:age の V: を返す（P:agetty と取り違えない）',
      run: async () => versionFromIndexArchive(indexWith('1.3.2-r1')),
      expect: (v) => v === '1.3.2-r1',
    },
    {
      name: '一致: 両アーキテクチャで版が同じなら ok・scanned=2',
      run: async () => resolve({ dockerfileText: FIXTURE_DOCKERFILE, fetchFn: sameFetch('1.3.2-r1') }),
      expect: (r) => r.status === 'ok' && r.scanned === 2 && exitCodeFor(r) === 0,
    },
    {
      // 🔴 陽性対照。#2003 の形（固定 r0・APKINDEX は r1）で drift になること。
      name: '陽性対照（#2003 の形）: 固定 1.3.2-r0・APKINDEX 1.3.2-r1 で drift、理由に新しい版',
      run: async () => resolve({ dockerfileText: FIXTURE_DOCKERFILE.replace('1.3.2-r1', '1.3.2-r0'), fetchFn: sameFetch('1.3.2-r1') }),
      expect: (r) => r.status === 'drift' && /1\.3\.2-r1/.test(r.reason) && exitCodeFor(r) === 0,
    },
    {
      name: 'APKINDEX に age が無ければ drift（固定の版はもう取れない）',
      run: async () => resolve({ dockerfileText: FIXTURE_DOCKERFILE, fetchFn: async () => makeIndexArchive('C:Q1\nP:zstd\nV:1-r0\n') }),
      expect: (r) => r.status === 'drift' && /age が無い/.test(r.reason),
    },
    {
      // 🔴 誤った起票をしない。
      name: 'ネットワークに届かなければ unverified・scanned=0（起票しない・exit 0）',
      run: async () => resolve({ dockerfileText: FIXTURE_DOCKERFILE, fetchFn: async () => { throw new Error('fetch failed'); } }),
      expect: (r) => r.status === 'unverified' && r.scanned === 0 && /fetch failed/.test(r.reason) && exitCodeFor(r) === 0,
    },
    {
      // 🔴 #2010 の AI レビュー 🟡: 404 を unverified にすると、ブランチの EOL で毎日緑のまま検知が止まる。
      name: '🔴 APKINDEX が 404（取得元が恒久的に無い）なら error・exit 1（unverified にしない）',
      run: async () => resolve({
        dockerfileText: FIXTURE_DOCKERFILE,
        fetchFn: async (url) => { const e = new Error(`HTTP 404 for ${url}`); e.status = 404; throw e; },
      }),
      expect: (r) => r.status === 'error' && exitCodeFor(r) === 1 && /恒久的に無い/.test(r.reason) && /EOL/.test(r.reason),
    },
    {
      name: '片方だけ 404 でも error（もう片方の一致で緑にしない）',
      run: async () => resolve({
        dockerfileText: FIXTURE_DOCKERFILE,
        fetchFn: async (url) => { if (/aarch64/.test(url)) { const e = new Error('HTTP 410'); e.status = 410; throw e; } return indexWith('1.3.2-r1'); },
      }),
      expect: (r) => r.status === 'error' && r.scanned === 1,
    },
    {
      name: '5xx は一過性なので unverified のまま（exit 0・起票しない）',
      run: async () => resolve({
        dockerfileText: FIXTURE_DOCKERFILE,
        fetchFn: async () => { const e = new Error('HTTP 503'); e.status = 503; throw e; },
      }),
      expect: (r) => r.status === 'unverified' && exitCodeFor(r) === 0,
    },
    {
      name: '壊れた圧縮（プロキシの HTML 等）は unverified（drift にしない）',
      run: async () => resolve({ dockerfileText: FIXTURE_DOCKERFILE, fetchFn: async () => Buffer.from('<html>blocked</html>') }),
      expect: (r) => r.status === 'unverified' && r.scanned === 0,
    },
    {
      name: '片方だけ届き一致なら unverified（「一致」と言わない）',
      run: async () => resolve({
        dockerfileText: FIXTURE_DOCKERFILE,
        fetchFn: async (url) => { if (/aarch64/.test(url)) throw new Error('HTTP 503'); return indexWith('1.3.2-r1'); },
      }),
      expect: (r) => r.status === 'unverified' && r.scanned === 1 && /aarch64: HTTP 503/.test(r.reason),
    },
    {
      name: '片方だけ届き食い違えば drift（読めた分の食い違いは本物）',
      run: async () => resolve({
        dockerfileText: FIXTURE_DOCKERFILE,
        fetchFn: async (url) => { if (/aarch64/.test(url)) throw new Error('HTTP 503'); return indexWith('1.3.2-r2'); },
      }),
      expect: (r) => r.status === 'drift' && r.scanned === 1,
    },
    {
      name: 'Dockerfile の ARG を読めなければ error・exit 1',
      run: async () => resolve({ dockerfileText: 'FROM x\n', fetchFn: sameFetch('1.3.2-r1') }),
      expect: (r) => r.status === 'error' && exitCodeFor(r) === 1,
    },
    {
      name: '取りに行く URL は Dockerfile の取得元と同じ（ミラー/ブランチ/community/アーキテクチャ）',
      run: async () => {
        const urls = [];
        await resolve({ dockerfileText: FIXTURE_DOCKERFILE, fetchFn: async (u) => { urls.push(u); return indexWith('1.3.2-r1'); } });
        return urls;
      },
      expect: (u) => u.join() === ARCHES.map((a) => `https://dl-cdn.alpinelinux.org/alpine/v3.24/community/${a}/APKINDEX.tar.gz`).join(),
    },
    {
      name: '実物の Dockerfile から読める（配線。値は固定しない）',
      run: async () => readPinned(fs.readFileSync(path.join(__dirname, '..', DOCKERFILE_REL), 'utf8')),
      expect: (p) => p && /^\d+\.\d+\.\d+-r\d+$/.test(p.version) && /^v\d+\.\d+$/.test(p.branch),
    },
  ];
  const DRIFT = await resolve({ dockerfileText: FIXTURE_DOCKERFILE.replace('1.3.2-r1', '1.3.2-r0'), fetchFn: sameFetch('1.3.2-r1') });
  const recorder = (open) => {
    const calls = [];
    const api = async (method, url, body) => {
      calls.push(`${method} ${url.replace(/^https:\/\/api\.github\.com\/repos\/o\/r/, '')}`);
      if (method === 'POST' && url.endsWith('/labels')) throw new Error('422 Unprocessable Entity');
      if (method === 'GET') return open;
      if (method === 'POST') return { number: 77, body: body.body };
      return { number: Number(url.split('/').pop()) };
    };
    return { calls, api };
  };
  cases.push(
    {
      name: 'upsertDriftIssue: drift で既存が無ければ専用 issue を作る（ラベル付き・本文に両方の版）',
      run: async () => {
        const { calls, api } = recorder([{ number: 5, body: '別の issue' }]);
        const r = await upsertDriftIssue({ result: DRIFT, repo: 'o/r', token: 't', api });
        return { r, calls, body: renderDriftIssue(DRIFT) };
      },
      expect: ({ r, calls, body }) => r.action === 'created' && r.number === 77
        && calls.join('|') === `POST /labels|GET /issues?state=open&labels=${DRIFT_LABEL}&per_page=100|POST /issues`
        && body.includes(DRIFT_MARKER) && body.includes('`1.3.2-r0`') && body.includes('`1.3.2-r1`'),
    },
    {
      name: 'upsertDriftIssue: 既存（マーカー付き）があれば本文を上書きし、新しく作らない',
      run: async () => {
        const { calls, api } = recorder([{ number: 12, body: `${DRIFT_MARKER}\n古い本文` }]);
        const r = await upsertDriftIssue({ result: DRIFT, repo: 'o/r', token: 't', api });
        return { r, calls };
      },
      expect: ({ r, calls }) => r.action === 'updated' && r.number === 12
        && calls[calls.length - 1] === 'PATCH /issues/12' && !calls.includes('POST /issues'),
    },
    {
      // 🔴 一致していれば何もしない／届かなければ起票しない（受け入れ基準）。
      name: 'upsertDriftIssue: ok / unverified / error では API を一度も呼ばない（自動クローズもしない）',
      run: async () => {
        const out = [];
        for (const status of ['ok', 'unverified', 'error']) {
          const { calls, api } = recorder([]);
          const r = await upsertDriftIssue({ result: { ...DRIFT, status }, repo: 'o/r', token: 't', api });
          out.push(`${r.action}:${calls.length}`);
        }
        return out.join(',');
      },
      expect: (s) => s === 'none:0,none:0,none:0',
    },
    {
      name: '🔴 upsertDriftIssue: ずれを検知したのに token が無ければ例外（黙って skip しない）',
      run: async () => {
        try {
          await upsertDriftIssue({ result: DRIFT, repo: 'o/r', token: '' });
          return 'なぜか成功した';
        } catch (e) { return e.message; }
      },
      expect: (m) => /GITHUB_TOKEN/.test(m),
    },
  );
  let failed = 0;
  for (const c of cases) {
    let got;
    try {
      got = await c.run();
    } catch (e) {
      failed++;
      process.stderr.write(`  NG  ${c.name}\n      例外: ${e.message}\n`);
      continue;
    }
    if (c.expect(got)) process.stdout.write(`  ok  ${c.name}\n`);
    else {
      failed++;
      process.stderr.write(`  NG  ${c.name}\n      got=${JSON.stringify(got)}\n`);
    }
  }
  if (failed) {
    process.stderr.write(`\n✗ 検証器の自己試験が ${failed} 件失敗した\n`);
    return 1;
  }
  process.stdout.write(`✓ 検証器の自己試験 ${cases.length} 件すべて合格\n`);
  return 0;
}

async function main(argv) {
  const args = parseArgs(argv.slice(2));
  if (args.selfTest) process.exit(await selfTest());
  if (!args.out) {
    process.stderr.write('usage: check-age-apkindex.js --out <path.json> [--upsert-issue] [--dockerfile <path>] | --self-test\n');
    process.exit(2);
  }
  const dockerfilePath = args.dockerfile || path.join(__dirname, '..', DOCKERFILE_REL);
  let dockerfileText = '';
  try {
    dockerfileText = fs.readFileSync(dockerfilePath, 'utf8');
  } catch { /* resolve が error にする */ }
  const result = await resolve({ dockerfileText });
  let code = exitCodeFor(result);
  if (args.upsertIssue) {
    try {
      result.driftIssue = await upsertDriftIssue({
        result,
        repo: process.env.GITHUB_REPOSITORY,
        token: process.env.GITHUB_TOKEN,
        date: new Date().toISOString().slice(0, 10),
        runId: process.env.GITHUB_RUN_ID,
      });
    } catch (e) {
      result.driftIssue = { action: 'failed', reason: String(e.message || e) };
      code = 1;
    }
  }
  fs.mkdirSync(path.dirname(path.resolve(args.out)), { recursive: true });
  fs.writeFileSync(args.out, `${JSON.stringify(result, null, 2)}\n`, 'utf8');
  // 🔴 走査件数を必ず添える。0 件は「一致」ではなく「検査が動いていない」である。
  const line = `age の APKINDEX 突合: ${result.status}（突合 ${result.scanned}/${ARCHES.length} アーキテクチャ）— ${result.reason}`;
  if (result.status === 'error') emit('error', line, { stream: process.stderr, prefix: '  error  ' });
  else if (result.status === 'drift') emit('warning', line, { stream: process.stderr, prefix: '  warning  ' });
  else if (result.status === 'unverified') emit('warning', `${line}（「一致」ではなく「検査が動いていない」。起票しない）`, { stream: process.stderr, prefix: '  warning  ' });
  else process.stdout.write(`✓ ${line}\n`);
  const di = result.driftIssue;
  if (di && di.action === 'failed') {
    emit('error', `版のずれを専用 issue へ書けなかった: ${di.reason}`, { stream: process.stderr, prefix: '  error  ' });
  } else if (di) {
    process.stdout.write(di.action === 'none'
      ? `専用 issue: 起票不要（status: ${result.status}）\n`
      : `専用 issue: #${di.number} を${di.action === 'created' ? '作成した' : '更新した'}\n`);
  }
  process.stdout.write(`結果を書いた: ${args.out}\n`);
  process.exit(code);
}

if (require.main === module) {
  main(process.argv).catch((e) => {
    process.stderr.write(`${e.stack || e}\n`);
    process.exit(1);
  });
}

module.exports = {
  readPinned, tarEntry, versionOf, versionFromIndexArchive, indexUrl, resolve, exitCodeFor,
  renderDriftIssue, upsertDriftIssue, makeIndexArchive, selfTest,
  ARCHES, DOCKERFILE_REL, DRIFT_LABEL, DRIFT_MARKER, DRIFT_TITLE,
};
