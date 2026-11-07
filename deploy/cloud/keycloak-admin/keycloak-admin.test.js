#!/usr/bin/env node
'use strict';
/*
 * NFR-18, IADR-0704: keycloak-admin.sh（master realm の一時管理者を開け閉めする入口）の振る舞いの試験。稼働のクラスタの代わりに、
 * kubectl のスタブ（Deployment の env・Job・作り直しを状態のファイルで持つ）と、Job の中で本物の master-admin.js が呼ぶ Keycloak の偽物
 * （fetch の差し替え）を当てる。scripts/deploy-cloud-egress-audit.test.js が子のプロセスで走らせる（node deploy/cloud/keycloak-admin/keycloak-admin.test.js でも）。
 *
 *   B1 open → close の一巡: open で env が足され一時管理者が作られ、close で両方が消えて check が 0。
 *   B2 close は流し直せる: retire の後に作り直し（rollout）が落ちた状態から、もう一度 close を流すと env が外れて閉じる。
 *      env を外す操作が落ちて env が残った状態からも同じ。閉じた状態で流しても何も変えずに閉じる。
 *   B3 本当の失敗は区別して止まる: Keycloak に繋がらないときは、一時管理者の有無を確かめられないとして 1 で止まり、env を外さない。
 *      check は 0（閉じている）・3（閉じ忘れ）・1（確かめられない）を分ける。
 *   陰性対照: close が一時管理者の有無を見ずに retire を流す形（前の版）へ戻すと、B2 の流し直しは止まる。
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const HERE = __dirname;
const ROOT = path.resolve(HERE, '..', '..', '..');
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'keycloak-admin-'));
const STATE = path.join(dir, 'state.json');
const PASSWORD = ['probe', 'pw', 'k'.repeat(24)].join('-');
const CLUSTER_UID = '11111111-2222-4333-8444-555555555555';
const MARK = path.join(dir, 'cloud-cluster-mark');
fs.writeFileSync(MARK, `${CLUSTER_UID}\n`, { mode: 0o600 });

// ---- Keycloak の偽物（Job の中の master-admin.js の fetch を差し替える。node --require で読む）
const FAKE_FETCH = path.join(dir, 'fake-fetch.js');
fs.writeFileSync(FAKE_FETCH, `'use strict';
const fs = require('fs');
const load = () => JSON.parse(fs.readFileSync(process.env.FAKE_STATE, 'utf8'));
const save = (s) => { const t = process.env.FAKE_STATE + '.' + process.pid; fs.writeFileSync(t, JSON.stringify(s)); fs.renameSync(t, process.env.FAKE_STATE); }; // 半端なファイルを読ませない（パイプの両端が同時に動く）
const res = (status, body) => ({ status, ok: status < 300, json: async () => body, text: async () => (body === undefined ? '' : JSON.stringify(body)) });
globalThis.fetch = async (url, init) => {
  const s = load();
  if (!s.kc.up) throw new TypeError('fetch failed');
  const u = new URL(url);
  if (u.pathname === '/realms/master/protocol/openid-connect/token') {
    const f = new URLSearchParams(init.body);
    return s.kc.admin && f.get('username') === s.user && f.get('password') === s.password ? res(200, { access_token: 'tok' }) : res(401, { error: 'invalid_grant' });
  }
  if (init.headers.authorization !== 'Bearer tok' || !s.kc.admin) return res(401, {});
  if (u.pathname === '/admin/realms/master' && init.method === 'GET') return res(200, s.kc.realm);
  if (u.pathname === '/admin/realms/master' && init.method === 'PUT') { Object.assign(s.kc.realm, JSON.parse(init.body)); save(s); return res(204); }
  if (u.pathname === '/admin/realms/master/users' && init.method === 'GET') return res(200, s.kc.admin ? [{ id: '00000000-0000-0000-0000-000000000001', username: s.user }] : []);
  if (/^\\/admin\\/realms\\/master\\/users\\/[0-9a-f-]{36}$/.test(u.pathname) && init.method === 'DELETE') { s.kc.admin = false; save(s); return res(204); }
  return res(404, {});
};
`);

// ---- kubectl のスタブ
const BIN = path.join(dir, 'bin');
fs.mkdirSync(BIN);
const FAKE_KUBECTL = path.join(dir, 'fake-kubectl.js');
fs.writeFileSync(FAKE_KUBECTL, `'use strict';
const fs = require('fs');
const { spawnSync } = require('child_process');
const load = () => JSON.parse(fs.readFileSync(process.env.FAKE_STATE, 'utf8'));
const save = (s) => { const t = process.env.FAKE_STATE + '.' + process.pid; fs.writeFileSync(t, JSON.stringify(s)); fs.renameSync(t, process.env.FAKE_STATE); }; // 半端なファイルを読ませない（パイプの両端が同時に動く）
let a = process.argv.slice(2);
// 宛先の確かめ（deploy/cloud/lib/target-cluster.sh）: context・API の宛先（トンネル）・クラスタの印に答える
if (a.join(' ') === 'config current-context') { process.stdout.write('ctx-cloud\\n'); process.exit(0); }
if (a[0] === 'config' && a[1] === 'view') { process.stdout.write('https://127.0.0.1:6443'); process.exit(0); }
if (a[0] === '--context' && a[2] === 'get' && a[3] === 'namespace' && a[4] === 'kube-system') { process.stdout.write(process.env.FAKE_CLUSTER_UID); process.exit(0); }
// 確かめた後の呼び出しは、確かめた context に固定される
if (a[0] === '--context') { if (a[1] !== 'ctx-cloud') { process.stderr.write('fake kubectl: 確かめていない context: ' + a[1] + '\\n'); process.exit(9); } a = a.slice(2); }
if (a[0] === '-n') a = a.slice(2);
const s = load();
const line = a.join(' ');
s.calls.push(line.replace(/jsonpath=.*/, 'jsonpath=…'));
save(s);
const stdin = () => { try { return fs.readFileSync(0, 'utf8'); } catch { return ''; } };
const out = (t) => process.stdout.write(t);
const end = (code) => process.exit(code);
if (a[0] === 'get' && a[1] === 'deploy') { out(s.env ? 'KC_BOOTSTRAP_ADMIN_USERNAME\\nKC_BOOTSTRAP_ADMIN_PASSWORD\\n' : 'KC_PROXY_HEADERS\\n'); end(0); }
if (a[0] === 'get' && a[1] === 'secret') end(0);
if (a[0] === 'create' && a[1] === 'configmap') { out('kind: ConfigMap\\n'); end(0); }
if (a[0] === 'apply') { stdin(); end(0); }
if (a[0] === 'delete') end(0);
if (a[0] === 'create' && a[1] === '-f') {
  const m = /- name: MODE\\n\\s+value: "(\\w+)"/.exec(stdin());
  const r = spawnSync(process.execPath, ['--require', process.env.FAKE_FETCH, process.env.MASTER_ADMIN_JS], {
    encoding: 'utf8',
    env: { PATH: process.env.PATH, FAKE_STATE: process.env.FAKE_STATE, MODE: m ? m[1] : '', KC_URL: 'https://keycloak:8443', KC_ADMIN_USER: s.user, KC_ADMIN_PASSWORD: s.password },
  });
  const t = load();
  t.job = { mode: m ? m[1] : '', rc: r.status, logs: r.stdout + r.stderr };
  t.jobs.push(t.job.mode);
  save(t);
  end(0);
}
if (a[0] === 'get' && a[1] === 'job') { out(s.job.rc === 0 ? '1/' : '/1'); end(0); }
if (a[0] === 'get' && a[1] === 'pods') { out(String(s.job.rc)); end(0); }
if (a[0] === 'logs') { out(s.job.logs); end(0); }
if (a[0] === 'set' && a[1] === 'env') {
  if (line.includes('--from=secret/keycloak-admin')) { s.env = true; save(s); end(0); }
  if (s.failUnset > 0) { s.failUnset -= 1; save(s); process.stderr.write('error: the server rejected the request\\n'); end(1); }
  s.env = false; save(s); end(0);
}
if (a[0] === 'rollout') {
  const next = s.rollouts.shift() || 'ok';
  if (next === 'ok' && s.env && !s.kc.admin) s.kc.admin = true; // 起動の時に、env が在り master に管理者が居なければ作る
  save(s);
  if (next !== 'ok') { process.stderr.write('error: timed out waiting for the condition\\n'); end(1); }
  end(0);
}
if (a[0] === 'wait') end(0);
process.stderr.write('fake kubectl: 知らない呼び方: ' + line + '\\n');
end(9);
`);
fs.writeFileSync(path.join(BIN, 'kubectl'), `#!/bin/sh\nexec "${process.execPath}" "${FAKE_KUBECTL}" "$@"\n`, { mode: 0o755 });

function setState(over) {
  const base = { user: 'temp-admin-x1', password: PASSWORD, env: false, kc: { up: true, admin: false, realm: { realm: 'master', bruteForceProtected: false } }, rollouts: [], failUnset: 0, calls: [], jobs: [], job: null };
  fs.writeFileSync(STATE, JSON.stringify({ ...base, ...over, kc: { ...base.kc, ...(over.kc || {}) } }));
}
const state = () => JSON.parse(fs.readFileSync(STATE, 'utf8'));

/** 入口を流す。script は差し替えた入口（陰性対照）。 */
function runStage(stage, script = path.join(HERE, 'keycloak-admin.sh')) {
  const r = spawnSync('bash', [script, '--live', stage], {
    encoding: 'utf8',
    env: { PATH: `${BIN}:${process.env.PATH}`, HOME: dir, CLOUD_CLUSTER_MARK_FILE: MARK, FAKE_CLUSTER_UID: CLUSTER_UID, FAKE_STATE: STATE, FAKE_FETCH, MASTER_ADMIN_JS: path.join(HERE, 'master-admin.js'), KEYCLOAK_ADMIN_TIMEOUT_S: '30' },
  });
  return { status: r.status, out: r.stdout + r.stderr };
}

const tests = [];
const ok = (name, fn) => tests.push([name, fn]);

ok('B1 open → close の一巡: env と一時管理者が足され、close で両方が消えて check が 0', () => {
  setState({});
  let r = runStage('open');
  assert.strictEqual(r.status, 0, r.out);
  assert.deepStrictEqual([state().env, state().kc.admin, state().kc.realm.bruteForceProtected], [true, true, true]);
  assert.strictEqual(runStage('check').status, 3, '開いている間の check が 3 でない');
  r = runStage('close');
  assert.strictEqual(r.status, 0, r.out);
  assert.match(r.out, /OK: 閉じた/);
  assert.deepStrictEqual([state().env, state().kc.admin], [false, false]);
  assert.strictEqual(runStage('check').status, 0);
});

ok('B2 close は流し直せる: retire の後の作り直しの失敗・env を外す操作の失敗の後でも、もう一度流すと閉じる', () => {
  // retire の後、env を外して作り直す段（2 回目の rollout）が落ちる
  setState({ env: true, kc: { admin: true }, rollouts: ['ok', 'fail'] });
  let r = runStage('close');
  assert.notStrictEqual(r.status, 0, '陽性対照: 作り直しの失敗で止まらない');
  assert.strictEqual(state().kc.admin, false, '陽性対照: retire まで進んでいない');
  r = runStage('close');
  assert.strictEqual(r.status, 0, `流し直しで閉じない:\n${r.out}`);
  assert.match(r.out, /一時管理者は既に無い/);
  assert.deepStrictEqual([state().env, state().kc.admin], [false, false]);
  assert.ok(!state().jobs.slice(-2).includes('retire'), '一時管理者が無いのに retire を流した');

  // retire の後、env を外す操作そのものが落ちて env が残る
  setState({ env: true, kc: { admin: true }, failUnset: 1 });
  r = runStage('close');
  assert.notStrictEqual(r.status, 0);
  assert.deepStrictEqual([state().env, state().kc.admin], [true, false], '陽性対照: env が残り一時管理者が消えた状態を作れていない');
  r = runStage('close');
  assert.strictEqual(r.status, 0, `env が残った状態から閉じない:\n${r.out}`);
  assert.deepStrictEqual([state().env, state().kc.admin], [false, false]);
  assert.strictEqual(runStage('check').status, 0);

  // 閉じた状態で流しても、何も変えずに閉じる
  const before = state().calls.length;
  r = runStage('close');
  assert.strictEqual(r.status, 0, r.out);
  assert.ok(!state().calls.slice(before).some((c) => c.startsWith('set env')), '閉じた状態で env を書き換えた');
});

ok('B3 本当の失敗は区別して止まる: Keycloak に繋がらなければ 1 で止まり env を外さない。check は 0・3・1 を分ける', () => {
  setState({ env: true, kc: { up: false } });
  const r = runStage('close');
  assert.strictEqual(r.status, 1, r.out);
  assert.match(r.out, /確かめられない/);
  assert.strictEqual(state().env, true, '確かめられないのに env を外した');
  assert.ok(!state().jobs.includes('retire'));
  assert.ok(!state().calls.some((c) => c.startsWith('set env')));
  setState({ kc: { up: false } });
  assert.strictEqual(runStage('check').status, 1, '繋がらないことを閉じ忘れ（3）や閉じている（0）と取り違えた');
  setState({ kc: { admin: true } });
  assert.strictEqual(runStage('check').status, 3);
  setState({});
  assert.strictEqual(runStage('check').status, 0);
});

ok('陰性対照: close が有無を見ずに retire を流す形（前の版）へ戻すと、B2 の流し直しは止まる', () => {
  // 入口を一時の場所へ写し（job.yaml・master-admin.js・live-opt-in.sh も同じ相対の位置へ）、一時管理者の有無で retire を飛ばす分岐を外す
  const t = path.join(dir, 'mut');
  fs.mkdirSync(path.join(t, 'deploy/cloud/keycloak-admin'), { recursive: true });
  fs.mkdirSync(path.join(t, 'scripts/lib'), { recursive: true });
  fs.mkdirSync(path.join(t, 'deploy/cloud/lib'), { recursive: true });
  fs.copyFileSync(path.join(ROOT, 'deploy/cloud/lib/target-cluster.sh'), path.join(t, 'deploy/cloud/lib/target-cluster.sh'));
  for (const f of ['job.yaml', 'master-admin.js']) fs.copyFileSync(path.join(HERE, f), path.join(t, 'deploy/cloud/keycloak-admin', f));
  fs.copyFileSync(path.join(ROOT, 'scripts/lib/live-opt-in.sh'), path.join(t, 'scripts/lib/live-opt-in.sh'));
  const sh = fs.readFileSync(path.join(HERE, 'keycloak-admin.sh'), 'utf8');
  const guard = 'if [ "$admin" = present ]; then\n      run_job retire';
  assert.strictEqual(sh.split(guard).length, 2, '陽性対照: 差し替える分岐が入口に無い');
  const mutated = path.join(t, 'deploy/cloud/keycloak-admin/keycloak-admin.sh');
  fs.writeFileSync(mutated, sh.replace(guard, 'if true; then\n      run_job retire'));
  setState({ env: true, kc: { admin: true }, rollouts: ['ok', 'fail'] });
  runStage('close', mutated);
  const r = runStage('close', mutated);
  assert.notStrictEqual(r.status, 0, '前の版の形でも流し直せてしまう（試験が振る舞いを見分けていない）');
  assert.match(r.out, /トークンを取れない/);
});

let failed = 0;
try {
  for (const [name, fn] of tests) {
    try {
      fn();
      console.log(`  ok  ${name}`);
    } catch (e) {
      failed += 1;
      console.log(`  NG  ${name}\n${e.stack || e.message}`);
    }
  }
} finally {
  fs.rmSync(dir, { recursive: true, force: true });
}
if (failed) { console.error(`✗ ${failed} 件が失敗した`); process.exit(1); }
console.log('✓ 一時管理者の入口（keycloak-admin.sh）の振る舞いの試験');
