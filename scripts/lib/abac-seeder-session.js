'use strict';
// NFR-18, ADR-0124 決定 1, IADR-0576 (GHSA-rh44-px44-p48q):
// **投入器の主体 `abac-seeder` は既定で無効**（realm の宣言が `enabled: false`）。投入器・運用スクリプトは、実行の間だけ
// 有効にし、終わったら無効へ戻す。資格情報は実行時に、明示（env）か稼働の Secret（OpenBao → ESO、ESO が無ければ起動器が作る
// `platform-infra/abac-seeder-oidc`）から読む。**realm の宣言の値（公知の dev の値）へは落ちない。**
//
// 使い方（呼び出し側。どの入口も同じ形）:
//   const seeder = require('./lib/abac-seeder-session.js');
//   const secret = seeder.resolveSeederSecret({ envName: 'REPUBLISH_CLIENT_SECRET' });   // 明示 ＞ Secret。無ければ例外
//   await seeder.ensureEnabled({ kcUrl, realm, clientId });                               // 無効なら有効にし、控える
//   ... client_credentials で名乗る ...
//   await seeder.restore();                                                               // 控えたものだけ無効へ戻す（入口の終わり・中断）
//
// - 有効・無効の切り替えは master realm の管理者（明示 KC_ADMIN_USER / KC_ADMIN_PASSWORD ＞ Secret platform-infra/keycloak-admin）が
//   Admin REST で行う。**この実行が有効にしたものだけを戻す**（元から有効なら触らない）。
// - 戻し損ねても（中断・強制終了）、realm の後追い（reconcile-realm.sh。宣言の enabled: false との差分を当てる）が次の起動で閉じる。
//   手で閉じるときは `node scripts/lib/abac-seeder-session.js --live --disable`。
// - 🔴 client の更新は GET した表現を丸ごと PUT し、`enabled` だけを変える（項目を欠いた PUT は SA を消し得る。PR #1832 監査）。
// 🔴 値（client secret・管理者のパスワード・トークン）はどこにも出力しない。
const { spawnSync } = require('child_process');

const INFRA_NS_DEFAULT = 'platform-infra';
const SEEDER_SECRET = Object.freeze({ name: 'abac-seeder-oidc', key: 'client-secret' });
const ADMIN_SECRET = Object.freeze({ name: 'keycloak-admin', userKey: 'username', passwordKey: 'password' });
const REQUEST_TIMEOUT_MS = 30000;

/** kubectl で Secret の 1 キーを読む（base64 を解いて返す）。無ければ空。値は出力しない。 */
function readSecretKey(ns, name, key, run = spawnSync) {
  const r = run('kubectl', ['-n', ns, 'get', 'secret', name, '-o', `jsonpath={.data.${key}}`], { encoding: 'utf8' });
  if (!r || r.status !== 0 || !r.stdout) return '';
  return Buffer.from(String(r.stdout).trim(), 'base64').toString('utf8');
}

/**
 * 投入器の client secret を決める。**明示（env）＞ 稼働の Secret**。どちらも無ければ例外（realm の宣言の値へ落ちない）。
 * @param {{envName?: string, env?: object, ns?: string, read?: Function}} o
 */
function resolveSeederSecret({ envName = 'ABAC_SEED_CLIENT_SECRET', env = process.env, ns = env.REPUBLISH_INFRA_NS || INFRA_NS_DEFAULT,
  read = readSecretKey } = {}) {
  if (env[envName]) return env[envName];
  if (envName !== 'ABAC_SEED_CLIENT_SECRET' && env.ABAC_SEED_CLIENT_SECRET) return env.ABAC_SEED_CLIENT_SECRET;
  const fromSecret = read(ns, SEEDER_SECRET.name, SEEDER_SECRET.key);
  if (fromSecret) return fromSecret;
  throw new Error(`投入器の client secret を決められない: ${envName} が無く、Secret ${ns}/${SEEDER_SECRET.name}（${SEEDER_SECRET.key}）も読めない。`
    + ' realm の宣言の値へは落ちない（IADR-0576）。稼働の値を env で与えるか、Secret を作る（起動器・ESO）。');
}

/** master realm の管理者の資格情報。明示 ＞ Secret platform-infra/keycloak-admin。無ければ例外。 */
function resolveAdminCredentials({ env = process.env, ns = env.REPUBLISH_INFRA_NS || INFRA_NS_DEFAULT, read = readSecretKey } = {}) {
  const user = env.KC_ADMIN_USER || read(ns, ADMIN_SECRET.name, ADMIN_SECRET.userKey);
  const password = env.KC_ADMIN_PASSWORD || read(ns, ADMIN_SECRET.name, ADMIN_SECRET.passwordKey);
  if (!user || !password) {
    throw new Error(`投入器の有効・無効を切り替える管理者の資格情報が無い（KC_ADMIN_USER / KC_ADMIN_PASSWORD か Secret ${ns}/${ADMIN_SECRET.name}）。`);
  }
  return { user, password };
}

async function fetchWithin(fetchImpl, url, init) {
  return fetchImpl(url, { ...init, signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS) });
}

async function adminToken(fetchImpl, kcUrl, { user, password }) {
  const res = await fetchWithin(fetchImpl, `${kcUrl}/realms/master/protocol/openid-connect/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: 'password', client_id: 'admin-cli', username: user, password }),
  });
  if (!res.ok) throw new Error(`master realm の管理者のトークンを取得できない（HTTP ${res.status}）`);
  return (await res.json()).access_token;
}

async function findClient(fetchImpl, kcUrl, realm, clientId, token) {
  const R = `${kcUrl}/admin/realms/${encodeURIComponent(realm)}`;
  const res = await fetchWithin(fetchImpl, `${R}/clients?clientId=${encodeURIComponent(clientId)}`, { headers: { Authorization: `Bearer ${token}` } });
  if (!res.ok) throw new Error(`client ${clientId} を読めない（HTTP ${res.status}）`);
  const found = ((await res.json()) || []).find((c) => c.clientId === clientId);
  if (!found) throw new Error(`client ${clientId} が realm ${realm} に無い`);
  return { R, client: found };
}

/** client の enabled を変える（GET した表現を丸ごと PUT し、enabled だけを変える）。前の値を返す。 */
async function setEnabled(fetchImpl, kcUrl, realm, clientId, token, enabled) {
  const { R, client } = await findClient(fetchImpl, kcUrl, realm, clientId, token);
  const before = client.enabled !== false;
  if (before === enabled) return before;
  const res = await fetchWithin(fetchImpl, `${R}/clients/${client.id}`, {
    method: 'PUT',
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ ...client, enabled }),
  });
  if (!res.ok) throw new Error(`client ${clientId} の enabled を ${enabled} にできない（HTTP ${res.status}）`);
  return before;
}

// この実行が有効にしたもの（戻す対象）。{ kcUrl, realm, clientId, creds, fetchImpl }
const opened = [];

/** 無効なら有効にし、この実行が有効にしたことを控える。元から有効なら何もしない。 */
async function ensureEnabled({ kcUrl, realm, clientId, fetchImpl = fetch, env = process.env, read = readSecretKey, log = () => {} }) {
  const creds = resolveAdminCredentials({ env, read });
  const token = await adminToken(fetchImpl, kcUrl, creds);
  const before = await setEnabled(fetchImpl, kcUrl, realm, clientId, token, true);
  if (!before) {
    opened.push({ kcUrl, realm, clientId, creds, fetchImpl });
    log(`投入器の主体 ${clientId} を実行の間だけ有効にした（終わったら無効へ戻す。IADR-0576）`);
  }
  return { wasEnabled: before };
}

/** この実行が有効にしたものを無効へ戻す。失敗しても例外を投げず、戻せなかったものを返す（呼び出し側が警告する）。 */
async function restore({ log = () => {}, warn = () => {} } = {}) {
  const failed = [];
  while (opened.length > 0) {
    const o = opened.pop();
    try {
      const token = await adminToken(o.fetchImpl, o.kcUrl, o.creds);
      await setEnabled(o.fetchImpl, o.kcUrl, o.realm, o.clientId, token, false);
      log(`投入器の主体 ${o.clientId} を無効へ戻した`);
    } catch (e) {
      failed.push(o.clientId);
      warn(`投入器の主体 ${o.clientId} を無効へ戻せなかった（${e.message}）。node scripts/lib/abac-seeder-session.js --live --disable で閉じる`
        + '（realm の後追いも次の起動で閉じる）');
    }
  }
  return failed;
}

/** 中断（SIGINT・全体の上限）のときの終わり方: 上限つきで戻してから exit する。 */
function restoreThenExit(code, { warn = () => {}, before = () => {}, timeoutMs = 15000 } = {}) {
  const t = setTimeout(() => { before(); process.exit(code); }, timeoutMs);
  restore({ warn }).finally(() => { clearTimeout(t); before(); process.exit(code); });
}

module.exports = { resolveSeederSecret, resolveAdminCredentials, ensureEnabled, restore, restoreThenExit, readSecretKey, setEnabled,
  SEEDER_SECRET, ADMIN_SECRET, _opened: opened };

/** 自己試験（稼働クラスタへ当たらない。偽の fetch で一時の有効化と戻しを確かめる。scripts.repo.test.js が走らせる）。 */
async function selfTest() {
  const assert = require('assert');

      const sess = module.exports;
      const mk = (enabled) => {
        const st = { client: { id: 'c1', clientId: 'abac-seeder', enabled, serviceAccountsEnabled: true, publicClient: false, attributes: { a: '1' } }, puts: [] };
        const fetchImpl = async (url, init = {}) => {
          const json = (o, status = 200) => ({ ok: status < 300, status, json: async () => o, text: async () => JSON.stringify(o) });
          if (url.endsWith('/realms/master/protocol/openid-connect/token')) return json({ access_token: 'adm' });
          if (init.method === 'PUT') { const b = JSON.parse(init.body); st.puts.push(b); st.client = b; return json(null, 204); }
          if (/\/clients\?clientId=abac-seeder$/.test(url)) return json([st.client]);
          return json({}, 404);
        };
        return { st, fetchImpl };
      };
      const env = { KC_ADMIN_USER: 'a', KC_ADMIN_PASSWORD: 'p' };
      const off = mk(false);
      assert.deepStrictEqual(await sess.ensureEnabled({ kcUrl: 'http://kc', realm: 'platform', clientId: 'abac-seeder', fetchImpl: off.fetchImpl, env }), { wasEnabled: false });
      assert.strictEqual(off.st.puts.length, 1);
      assert.deepStrictEqual(off.st.puts[0], { id: 'c1', clientId: 'abac-seeder', enabled: true, serviceAccountsEnabled: true, publicClient: false, attributes: { a: '1' } },
        '表現を丸ごと PUT していない（SA などの項目を欠くと消え得る）');
      assert.deepStrictEqual(await sess.restore(), []);
      assert.deepStrictEqual(off.st.puts.map((p) => p.enabled), [true, false], '戻していない');
      const on = mk(true);
      await sess.ensureEnabled({ kcUrl: 'http://kc', realm: 'platform', clientId: 'abac-seeder', fetchImpl: on.fetchImpl, env });
      await sess.restore();
      assert.deepStrictEqual(on.st.puts, [], '元から有効なのに書いた（戻しもしない）');
      // 戻しの失敗は例外にせず名指す
      const broken = mk(false);
      await sess.ensureEnabled({ kcUrl: 'http://kc', realm: 'platform', clientId: 'abac-seeder', fetchImpl: broken.fetchImpl, env });
      sess._opened[0].fetchImpl = async () => { throw new Error('down'); };
      const warns = [];
      assert.deepStrictEqual(await sess.restore({ warn: (m) => warns.push(m) }), ['abac-seeder']);
      assert.ok(warns.some((w) => /--disable/.test(w)), '閉じ方を告げていない');
    }

// 手で閉じる・状態を見る入口（稼働クラスタへ当たる）。--self-test だけは指定なしで動く。
if (require.main === module && process.argv.includes('--self-test')) {
  selfTest().then(() => { process.stdout.write('[abac-seeder-session] self-test OK\n'); }, (e) => { process.stderr.write(`${e.stack || e}\n`); process.exit(1); });
} else if (require.main === module) {
  const { requireLiveOptIn } = require('./live-opt-in.js');
  const argv = process.argv.slice(2);
  const usage = '使い方: node scripts/lib/abac-seeder-session.js --live --disable | --status\n'
    + '  KC_URL（既定 http://localhost:8080。port-forward svc/keycloak 8080:8080）・REALM（既定 platform）・CLIENT_ID（既定 abac-seeder）\n';
  if (argv.includes('--help')) {
    process.stdout.write(usage);
    process.exit(0);
  }
  requireLiveOptIn('lib/abac-seeder-session', argv, { offline: '--help / --self-test' });
  const mode = argv.includes('--disable') ? 'disable' : argv.includes('--status') ? 'status' : null;
  if (!mode) {
    process.stderr.write(usage);
    process.exit(1);
  }
  (async () => {
    const kcUrl = (process.env.KC_URL || 'http://localhost:8080').replace(/\/+$/, '');
    const realm = process.env.REALM || 'platform';
    const clientId = process.env.CLIENT_ID || 'abac-seeder';
    const token = await adminToken(fetch, kcUrl, resolveAdminCredentials());
    if (mode === 'disable') await setEnabled(fetch, kcUrl, realm, clientId, token, false);
    const { client } = await findClient(fetch, kcUrl, realm, clientId, token);
    process.stdout.write(`${clientId} enabled=${client.enabled !== false}\n`);
  })().catch((e) => { process.stderr.write(`ERROR: ${e.message}\n`); process.exit(1); });
}
