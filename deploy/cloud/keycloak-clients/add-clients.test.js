#!/usr/bin/env node
'use strict';
/*
 * NFR-18, IADR-0700: add-clients.js（稼働中の realm へ無い機密クライアントだけを作る）の試験。稼働の Keycloak の代わりに、管理の API を
 * 記録する偽物（メモリの中）を当てる。scripts/deploy-cloud.test.js が子のプロセスで走らせる（node deploy/cloud/keycloak-clients/add-clients.test.js でも）。
 *
 *   - apply: 無いクライアント（サービスごとの 7 つ）だけを作り、サービスアカウントに trading-service を足す。在るクライアントには書かない。
 *   - 2 回目の apply・check は何も書かない。check は無いものを名指して 3（apply の前）・0（後）。
 *   - 既定のスコープ・ロールが稼働に無ければ、何も書かずに止める（問題を返す）。
 *   - 取り込み元の機密クライアントは CLOUD_CLIENTS と同じ集合（雛形に値を差し込んだ realm から引く）。値は出力に出ない。
 */
const assert = require('assert');
const path = require('path');
const fs = require('fs');
const { desiredFrom, reconcile, main } = require('./add-clients.js');
const { buildCloudRealm, CLOUD_CLIENTS, SERVICE_CLIENTS, DECLARED_CLOUD_CLIENTS } = require('../realm-for-cloud.js');

const ROOT = path.resolve(__dirname, '..', '..', '..');
const declared = JSON.parse(fs.readFileSync(path.join(ROOT, 'deploy/keycloak/microservices-platform-realm.json'), 'utf8'));
const secretOf = (c) => ['probe', c, 'q'.repeat(40)].join('-');
const cloud = buildCloudRealm(declared, Object.fromEntries(CLOUD_CLIENTS.map((c) => [c, secretOf(c)])));

/** 稼働の Keycloak の代わり。clients・users・role-mappings をメモリに持ち、書いた呼び出しを記録する。 */
function fakeKeycloak({ clientIds = DECLARED_CLOUD_CLIENTS, scopes, roles } = {}) {
  let n = 0;
  const id = () => `00000000-0000-0000-0000-${String(++n).padStart(12, '0')}`;
  const st = {
    scopes: scopes || (cloud.clientScopes || []).map((s) => s.name),
    roles: roles || ((cloud.roles || {}).realm || []).map((r) => r.name),
    clients: [],
    sa: new Map(),
    mappings: new Map(),
    writes: [],
  };
  const addClient = (rep) => {
    const c = { ...rep, id: id() };
    st.clients.push(c);
    const u = { id: id(), username: `service-account-${rep.clientId}` };
    st.sa.set(c.id, u);
    st.mappings.set(u.id, []);
    return c;
  };
  for (const cid of clientIds) {
    const c = addClient(cloud.clients.find((x) => x.clientId === cid));
    const want = (cloud.users.find((u) => u.serviceAccountClientId === cid) || {}).realmRoles || [];
    st.mappings.set(st.sa.get(c.id).id, want.map((name) => ({ name })));
  }
  async function call(method, p, body) {
    const r = '/admin/realms/platform';
    if (method !== 'GET') st.writes.push(`${method} ${p.replace(/[0-9a-f-]{36}/g, '<id>')}`);
    if (method === 'GET' && p === `${r}/client-scopes`) return st.scopes.map((name) => ({ name }));
    if (method === 'GET' && p === `${r}/roles`) return st.roles.map((name) => ({ name }));
    let m = p.match(/^\/admin\/realms\/platform\/clients\?clientId=(.+)$/);
    if (method === 'GET' && m) return st.clients.filter((c) => c.clientId === decodeURIComponent(m[1])).map(({ id: cid, clientId }) => ({ id: cid, clientId }));
    if (method === 'POST' && p === `${r}/clients`) {
      if (st.clients.some((c) => c.clientId === body.clientId)) throw new Error('409');
      addClient(body);
      return null;
    }
    m = p.match(/^\/admin\/realms\/platform\/clients\/([0-9a-f-]{36})\/(service-account-user|default-client-scopes)$/);
    if (method === 'GET' && m) {
      const c = st.clients.find((x) => x.id === m[1]);
      return m[2] === 'service-account-user' ? st.sa.get(c.id) : (c.defaultClientScopes || []).filter((s) => st.scopes.includes(s)).map((name) => ({ name }));
    }
    m = p.match(/^\/admin\/realms\/platform\/users\/([0-9a-f-]{36})\/role-mappings\/realm$/);
    if (m && method === 'GET') return st.mappings.get(m[1]);
    if (m && method === 'POST') { st.mappings.set(m[1], [...st.mappings.get(m[1]), ...body]); return null; }
    m = p.match(/^\/admin\/realms\/platform\/roles\/(.+)$/);
    if (method === 'GET' && m) return { name: decodeURIComponent(m[1]) };
    throw new Error(`偽物が知らない呼び出し: ${method} ${p}`);
  }
  return { st, call };
}

const tests = [];
const test = (name, fn) => tests.push([name, fn]);
const quiet = () => {};

test('取り込み元の機密クライアントは CLOUD_CLIENTS（宣言の 3 つとサービスごとの 7 つ）と同じ集合', async () => {
  assert.deepStrictEqual(desiredFrom(cloud).map((d) => d.client.clientId).sort(), [...CLOUD_CLIENTS].sort());
  for (const d of desiredFrom(cloud).filter((x) => SERVICE_CLIENTS.includes(x.client.clientId))) assert.deepStrictEqual(d.roles, ['trading-service']);
});

test('apply: 無い 7 つだけを作り、サービスアカウントに trading-service を足す。在る 3 つには書かない。2 回目は何も書かない', async () => {
  const kc = fakeKeycloak();
  const logs = [];
  const out = await reconcile(kc.call, 'platform', desiredFrom(cloud), { apply: true, log: (l) => logs.push(l) });
  assert.deepStrictEqual(out.problems, []);
  assert.deepStrictEqual(out.created.sort(), [...SERVICE_CLIENTS].sort());
  const posts = kc.st.writes.filter((w) => w === 'POST /admin/realms/platform/clients');
  assert.strictEqual(posts.length, SERVICE_CLIENTS.length);
  assert.ok(!kc.st.writes.some((w) => /^(PUT|DELETE)/.test(w)), `在るものを書き換えた: ${kc.st.writes.join(', ')}`);
  for (const cid of SERVICE_CLIENTS) {
    const c = kc.st.clients.find((x) => x.clientId === cid);
    assert.strictEqual(c.secret, secretOf(cid), `${cid} の secret が取り込み元の値でない`);
    assert.deepStrictEqual(kc.st.mappings.get(kc.st.sa.get(c.id).id).map((r) => r.name), ['trading-service']);
  }
  for (const cid of DECLARED_CLOUD_CLIENTS) assert.strictEqual(kc.st.clients.filter((x) => x.clientId === cid).length, 1);
  assert.ok(!logs.join('\n').includes('probe-'), 'secret の値を出力した');
  const before = kc.st.writes.length;
  const again = await reconcile(kc.call, 'platform', desiredFrom(cloud), { apply: true, log: quiet });
  assert.deepStrictEqual([again.created, again.rolesAdded, again.problems], [[], [], []]);
  assert.strictEqual(kc.st.writes.length, before, '2 回目の apply が書いた');
});

test('check: 読むだけ。無いものを名指す（apply の前は 7・後は 0）', async () => {
  const kc = fakeKeycloak();
  const out = await reconcile(kc.call, 'platform', desiredFrom(cloud), { apply: false, log: quiet });
  assert.deepStrictEqual(out.missing.sort(), [...SERVICE_CLIENTS].sort());
  assert.deepStrictEqual(kc.st.writes, []);
  const done = fakeKeycloak({ clientIds: CLOUD_CLIENTS });
  assert.deepStrictEqual((await reconcile(done.call, 'platform', desiredFrom(cloud), { apply: false, log: quiet })).missing, []);
});

test('在るクライアントのサービスアカウントに足りないロールだけを足す（外さない）', async () => {
  const kc = fakeKeycloak({ clientIds: CLOUD_CLIENTS });
  const c = kc.st.clients.find((x) => x.clientId === SERVICE_CLIENTS[0]);
  kc.st.mappings.set(kc.st.sa.get(c.id).id, [{ name: 'offline_access' }]);
  const out = await reconcile(kc.call, 'platform', desiredFrom(cloud), { apply: true, log: quiet });
  assert.deepStrictEqual(out.rolesAdded, [`${SERVICE_CLIENTS[0]}:trading-service`]);
  assert.deepStrictEqual(kc.st.mappings.get(kc.st.sa.get(c.id).id).map((r) => r.name), ['offline_access', 'trading-service']);
  assert.ok(!kc.st.writes.includes('POST /admin/realms/platform/clients'));
});

test('陰性対照: 宛先のスコープ・ロールが稼働に無ければ、何も書かずに止める', async () => {
  const noScope = fakeKeycloak({ scopes: (cloud.clientScopes || []).map((s) => s.name).filter((s) => s !== 'ai-stock-trading-api-audience') });
  const a = await reconcile(noScope.call, 'platform', desiredFrom(cloud), { apply: true, log: quiet });
  assert.ok(a.problems.some((p) => /ai-stock-trading-api-audience が稼働の realm に無い/.test(p)), a.problems.join('\n'));
  assert.deepStrictEqual(noScope.st.writes, []);
  const noRole = fakeKeycloak({ roles: ((cloud.roles || {}).realm || []).map((r) => r.name).filter((r) => r !== 'trading-service') });
  const b = await reconcile(noRole.call, 'platform', desiredFrom(cloud), { apply: true, log: quiet });
  assert.ok(b.problems.some((p) => /trading-service が稼働の realm に無い/.test(p)));
  assert.deepStrictEqual(noRole.st.writes, []);
});

test('main: 段・env・https・雛形（印の残り）の誤りは管理の API を呼ぶ前に止まる', async () => {
  const dir = fs.mkdtempSync(path.join(require('os').tmpdir(), 'kc-add-'));
  try {
    const file = path.join(dir, 'platform-realm.json');
    fs.writeFileSync(file, JSON.stringify(cloud));
    const tpl = path.join(dir, 'tpl.json');
    fs.writeFileSync(tpl, JSON.stringify(buildCloudRealm(declared, {}, { template: true })));
    const base = { MODE: 'check', KC_URL: 'https://keycloak.invalid:8443', REALM: 'platform', REALM_FILE: file, KC_ADMIN_USER: 'u', KC_ADMIN_PASSWORD: 'p' };
    assert.strictEqual(await main({ ...base, MODE: 'delete' }, quiet), 2);
    assert.strictEqual(await main({ ...base, KC_URL: 'http://keycloak:8080' }, quiet), 2);
    assert.strictEqual(await main({ ...base, REALM: 'other' }, quiet), 2);
    assert.strictEqual(await main({ ...base, REALM_FILE: tpl }, quiet), 2);
    assert.strictEqual(await main({ ...base, KC_ADMIN_PASSWORD: '' }, quiet), 2);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

(async () => {
  let failed = 0;
  for (const [name, fn] of tests) {
    try {
      await fn();
      console.log(`  ok  ${name}`);
    } catch (e) {
      failed++;
      console.log(`  NG  ${name}\n      ${String(e.message).split('\n').join('\n      ')}`);
    }
  }
  console.log(`${tests.length - failed} passed, ${failed} failed`);
  process.exit(failed ? 1 : 0);
})();
