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
const { desiredFrom, reconcile, syncSecrets, main, REPLACEABLE_CLIENT } = require('./add-clients.js');
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
    // IADR-0702: secret の読み書き（client-secret と、クライアントの表現の PUT）。
    m = p.match(/^\/admin\/realms\/platform\/clients\/([0-9a-f-]{36})(\/client-secret)?$/);
    if (m) {
      const c = st.clients.find((x) => x.id === m[1]);
      if (method === 'GET' && m[2]) return { type: 'secret', value: c.secret };
      if (method === 'GET') return { ...c };
      if (method === 'PUT' && !m[2]) { Object.assign(c, body); return null; }
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
  assert.deepStrictEqual(out.rolesLacking, []);
  assert.deepStrictEqual(kc.st.mappings.get(kc.st.sa.get(c.id).id).map((r) => r.name), ['offline_access', 'trading-service']);
  assert.ok(!kc.st.writes.includes('POST /admin/realms/platform/clients'));
});

test('在るクライアントに既定のスコープ（宛先）が欠けていれば、足さずに失敗にする（apply・check とも。流し直しで素通りしない）', async () => {
  for (const apply of [true, false]) {
    const kc = fakeKeycloak({ clientIds: CLOUD_CLIENTS });
    const c = kc.st.clients.find((x) => x.clientId === SERVICE_CLIENTS[2]);
    c.defaultClientScopes = c.defaultClientScopes.filter((x) => x !== 'ai-stock-trading-api-audience');
    const out = await reconcile(kc.call, 'platform', desiredFrom(cloud), { apply, log: quiet });
    assert.deepStrictEqual(out.problems, [`${SERVICE_CLIENTS[2]} に既定のスコープ ai-stock-trading-api-audience が付いていない（サービスごとのクライアント。管理の画面で消してから流し直す）`]);
    assert.deepStrictEqual(kc.st.writes, [], '在るクライアントに書いた（スコープを足した）');
  }
  // 陰性対照: スコープが揃っていれば問題は出ない（同じ在る 10 のクライアント）
  const ok = fakeKeycloak({ clientIds: CLOUD_CLIENTS });
  assert.deepStrictEqual((await reconcile(ok.call, 'platform', desiredFrom(cloud), { apply: true, log: quiet })).problems, []);
});

test('稼働中のクライアント（旧の 3 つ）のスコープの欠けは「消さない・差分を確かめる」と出し、サービスごとの 7 つとは区別する', async () => {
  // 名前の形の区分は realm-for-cloud.js の集合と同じ（7 つは消してよい・3 つは消さない）
  assert.deepStrictEqual(CLOUD_CLIENTS.filter((c) => REPLACEABLE_CLIENT.test(c)).sort(), [...SERVICE_CLIENTS].sort());
  assert.ok(DECLARED_CLOUD_CLIENTS.every((c) => !REPLACEABLE_CLIENT.test(c)), '旧の 3 つが「消してよい」に入った');
  const kc = fakeKeycloak({ clientIds: CLOUD_CLIENTS });
  const owner = kc.st.clients.find((x) => x.clientId === 'ai-stock-trading-owner');
  owner.defaultClientScopes = owner.defaultClientScopes.filter((x) => x !== 'ai-stock-trading-api-audience');
  const out = await reconcile(kc.call, 'platform', desiredFrom(cloud), { apply: true, log: quiet });
  assert.deepStrictEqual(out.problems, ['ai-stock-trading-owner に既定のスコープ ai-stock-trading-api-audience が付いていない（稼働中のクライアント。消さない（消すと稼働中の取引ユニットが止まる）。取り込み元との差分を確かめる）']);
  assert.ok(!out.problems[0].includes('消してから'), '稼働中のクライアントに「消してから流し直す」を出した');
  assert.deepStrictEqual(kc.st.writes, []);
});

test('作った直後に既定のスコープが付かなければ失敗にする', async () => {
  const kc = fakeKeycloak();
  const orig = kc.call;
  const call = async (method, p, body) => {
    if (method === 'POST' && p === '/admin/realms/platform/clients') body = { ...body, defaultClientScopes: ['roles'] }; // Keycloak が黙って捨てた形
    return orig(method, p, body);
  };
  const out = await reconcile(call, 'platform', desiredFrom(cloud), { apply: true, log: quiet });
  assert.strictEqual(out.problems.length, SERVICE_CLIENTS.length);
  assert.ok(out.problems.every((x) => /ai-stock-trading-api-audience が付いていない/.test(x)), out.problems.join('\n'));
  // 流し直しても素通りしない（在るクライアントとして同じ失敗）
  const again = await reconcile(call, 'platform', desiredFrom(cloud), { apply: true, log: quiet });
  assert.strictEqual(again.created.length, 0);
  assert.strictEqual(again.problems.length, SERVICE_CLIENTS.length);
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

test('IADR-0702 sync-secrets: 取り込み元と違う secret だけを書き換え（ほかの設定は変えない）、同じものは書かず、無いクライアントは作らずに名指す。値は出力しない', async () => {
  const kc = fakeKeycloak({ clientIds: CLOUD_CLIENTS });
  const rotated = SERVICE_CLIENTS[1];
  const stale = kc.st.clients.find((x) => x.clientId === rotated);
  stale.secret = 'stale-secret-value-should-not-print';
  stale.description = 'keep-me';
  const logs = [];
  const out = await syncSecrets(kc.call, 'platform', desiredFrom(cloud), { log: (l) => logs.push(l) });
  assert.deepStrictEqual(out.synced, [rotated]);
  assert.strictEqual(out.same.length, CLOUD_CLIENTS.length - 1);
  assert.deepStrictEqual(out.missing, []);
  assert.strictEqual(stale.secret, secretOf(rotated), '取り込み元の値に合わない');
  assert.strictEqual(stale.description, 'keep-me', 'secret 以外の設定を変えた');
  assert.deepStrictEqual(kc.st.writes, ['PUT /admin/realms/platform/clients/<id>'], `違うもの以外に書いた: ${kc.st.writes.join(', ')}`);
  const text = logs.join('\n');
  assert.ok(!text.includes('probe-') && !text.includes('stale-secret'), 'secret の値を出力した');
  const before = kc.st.writes.length;
  const again = await syncSecrets(kc.call, 'platform', desiredFrom(cloud), { log: quiet });
  assert.deepStrictEqual(again.synced, []);
  assert.strictEqual(kc.st.writes.length, before, '2 回目が書いた');
  // 無いクライアント（サービスごとの 7 つが未作成）は作らない
  const fresh = fakeKeycloak();
  const miss = await syncSecrets(fresh.call, 'platform', desiredFrom(cloud), { log: quiet });
  assert.deepStrictEqual(miss.missing.sort(), [...SERVICE_CLIENTS].sort());
  assert.ok(!fresh.st.writes.some((w) => /^POST/.test(w)), 'sync-secrets がクライアントを作った');
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
