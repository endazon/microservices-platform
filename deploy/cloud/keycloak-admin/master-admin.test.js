#!/usr/bin/env node
'use strict';
/*
 * NFR-18, IADR-0704: master-admin.js（master realm の一時管理者を要る間だけ置く）の試験。稼働の Keycloak の代わりに、トークンの口と管理の API を
 * 記録する偽物（fetch の差し替え）を当てる。scripts/deploy-cloud-egress-audit.test.js が子のプロセスで走らせる。
 *
 *   A1 harden: master realm の総当たりの検知を platform realm と同じ値で有効にし、2 回目は書かない。
 *   A2 retire: 検知を有効にした後に一時管理者を消し、その資格でトークンを取れなくなる。ほかの利用者は消さない。
 *   A3 check: 在れば 3、在らなければ 0。開いていない（トークンを取れない）retire は 1。段・宛先・資格の欠けは 2。
 *   A4 値（パスワード・トークン）を出力しない。
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { MASTER_BRUTE_FORCE, main } = require('./master-admin.js');

const ROOT = path.resolve(__dirname, '..', '..', '..');
const PASSWORD = ['probe', 'pw', 'q'.repeat(32)].join('-');

function fakeKeycloak({ admin = 'temp-admin-x1', others = ['someone'] } = {}) {
  const st = { realm: { realm: 'master', bruteForceProtected: false }, users: [admin, ...others].filter(Boolean).map((u, i) => ({ id: `00000000-0000-0000-0000-00000000000${i}`, username: u })), writes: [] };
  const json = (status, body) => ({ status, ok: status < 300, json: async () => body, text: async () => (body === undefined ? '' : JSON.stringify(body)) });
  async function fetchImpl(url, init) {
    const u = new URL(url);
    if (u.pathname === '/realms/master/protocol/openid-connect/token') {
      const f = new URLSearchParams(init.body);
      const ok = f.get('password') === PASSWORD && st.users.some((x) => x.username === f.get('username'));
      return ok ? json(200, { access_token: 'tok-value-never-printed' }) : json(401, { error: 'invalid_grant' });
    }
    if (init.headers.authorization !== 'Bearer tok-value-never-printed') return json(401, {});
    if (init.method !== 'GET') st.writes.push(`${init.method} ${u.pathname.replace(/[0-9a-f-]{36}/g, '<id>')}`);
    if (u.pathname === '/admin/realms/master' && init.method === 'GET') return json(200, st.realm);
    if (u.pathname === '/admin/realms/master' && init.method === 'PUT') { Object.assign(st.realm, JSON.parse(init.body)); return json(204); }
    if (u.pathname === '/admin/realms/master/users' && init.method === 'GET') return json(200, st.users.filter((x) => x.username === u.searchParams.get('username')));
    const m = /^\/admin\/realms\/master\/users\/([0-9a-f-]{36})$/.exec(u.pathname);
    if (m && init.method === 'DELETE') { st.users = st.users.filter((x) => x.id !== m[1]); return json(204); }
    return json(404, {});
  }
  return { st, fetchImpl };
}

const env = (mode) => ({ MODE: mode, KC_URL: 'https://keycloak:8443', KC_ADMIN_USER: 'temp-admin-x1', KC_ADMIN_PASSWORD: PASSWORD });
const tests = [];
const ok = (name, fn) => tests.push([name, fn]);

ok('A1 harden: 総当たりの検知を platform realm と同じ値で有効にし、2 回目は書かない', async () => {
  const kc = fakeKeycloak();
  const logs = [];
  assert.strictEqual(await main(env('harden'), { fetchImpl: kc.fetchImpl, log: (l) => logs.push(l) }), 0);
  for (const [k, v] of Object.entries(MASTER_BRUTE_FORCE)) assert.strictEqual(kc.st.realm[k], v, k);
  assert.deepStrictEqual(kc.st.writes, ['PUT /admin/realms/master']);
  assert.strictEqual(await main(env('harden'), { fetchImpl: kc.fetchImpl, log: () => {} }), 0);
  assert.deepStrictEqual(kc.st.writes, ['PUT /admin/realms/master'], '2 回目に書いた');
  // platform realm の宣言と同じ値（片側だけ変えない）
  const declared = JSON.parse(fs.readFileSync(path.join(ROOT, 'deploy/keycloak/microservices-platform-realm.json'), 'utf8'));
  for (const [k, v] of Object.entries(MASTER_BRUTE_FORCE)) assert.strictEqual(declared[k], v, `platform realm の ${k} と違う`);
});

ok('A2 retire: 検知を有効にした後に一時管理者を消し、トークンを取れなくなる。ほかの利用者は消さない', async () => {
  const kc = fakeKeycloak();
  assert.strictEqual(await main(env('retire'), { fetchImpl: kc.fetchImpl, log: () => {} }), 0);
  assert.deepStrictEqual(kc.st.writes, ['PUT /admin/realms/master', 'DELETE /admin/realms/master/users/<id>']);
  assert.deepStrictEqual(kc.st.users.map((u) => u.username), ['someone']);
  assert.strictEqual(kc.st.realm.bruteForceProtected, true);
});

ok('A3 check は在れば 3・無ければ 0。開いていない retire は 1。段・宛先・資格の欠けは 2', async () => {
  const quiet = { log: () => {} };
  assert.strictEqual(await main(env('check'), { ...quiet, fetchImpl: fakeKeycloak().fetchImpl }), 3);
  const gone = fakeKeycloak({ admin: null });
  assert.strictEqual(await main(env('check'), { ...quiet, fetchImpl: gone.fetchImpl }), 0);
  assert.strictEqual(await main(env('retire'), { ...quiet, fetchImpl: gone.fetchImpl }), 1);
  assert.deepStrictEqual(gone.st.writes, [], '開いていないのに書いた');
  assert.strictEqual(await main({ ...env('check'), MODE: 'delete' }, { ...quiet, fetchImpl: gone.fetchImpl }), 2);
  assert.strictEqual(await main({ ...env('check'), KC_URL: 'http://keycloak:8080' }, { ...quiet, fetchImpl: gone.fetchImpl }), 2);
  assert.strictEqual(await main({ ...env('check'), KC_ADMIN_PASSWORD: '' }, { ...quiet, fetchImpl: gone.fetchImpl }), 2);
});

ok('A4 値（パスワード・トークン）を出力しない', async () => {
  const logs = [];
  const kc = fakeKeycloak();
  for (const m of ['harden', 'retire', 'check']) await main(env(m), { fetchImpl: kc.fetchImpl, log: (l) => logs.push(l) });
  const all = logs.join('\n');
  assert.ok(!all.includes(PASSWORD) && !all.includes('tok-value'), '値を出力した');
  assert.ok(logs.length >= 3, '陽性対照: 出力を拾えていない');
});

(async () => {
  let failed = 0;
  for (const [name, fn] of tests) {
    try {
      await fn();
      console.log(`  ok  ${name}`);
    } catch (e) {
      failed += 1;
      console.log(`  NG  ${name}\n${e.stack || e.message}`);
    }
  }
  if (failed) { console.error(`✗ ${failed} 件が失敗した`); process.exit(1); }
  console.log('✓ master realm の一時管理者の手順の試験');
})();
