#!/usr/bin/env node
'use strict';
/*
 * NFR-18, IADR-0705: クラウドの Keycloak の master realm の一時管理者（KC_BOOTSTRAP_ADMIN_* で作られる管理者）を、要る間だけ置く。
 *
 * 一時管理者は realm の取り込み（--import-realm）に要らない。要るのは、稼働中の realm へ手を入れるとき（keycloak-clients の Job など）だけである。
 * keycloak-admin.sh が、開く（Deployment に KC_BOOTSTRAP_ADMIN_* を足して作り直す → この手順の harden）→ 作業 → 閉じる（この手順の retire →
 * Deployment から外して作り直す → check）を行う。
 *
 *   - MODE=harden: master realm の総当たりの検知を有効にする（platform realm と同じ値。在れば何もしない）。
 *   - MODE=retire: harden の後、一時管理者（KC_ADMIN_USER）を消し、その資格でトークンを取れないことを確かめる。
 *   - MODE=check: 一時管理者の資格でトークンを取れなければ 0（在らない）。取れれば 3（在る。閉じ忘れ）。
 *   - 値（パスワード・トークン）は出力しない。出すのは段・件数・設定の名前だけ。
 *
 * Job（deploy/cloud/keycloak-admin/job.yaml）の中で node:22-alpine が実行する。外部依存なし（Node 標準の fetch）。
 * env: KC_URL（https://keycloak:8443）・KC_ADMIN_USER・KC_ADMIN_PASSWORD・MODE。
 */

/** master realm の総当たりの検知（deploy/keycloak/microservices-platform-realm.json の platform realm と同じ値。試験が突き合わせる）。 */
const MASTER_BRUTE_FORCE = Object.freeze({
  bruteForceProtected: true,
  permanentLockout: false,
  failureFactor: 5,
  waitIncrementSeconds: 900,
  maxFailureWaitSeconds: 900,
});

async function tokenFor(baseUrl, user, password, fetchImpl = fetch) {
  const res = await fetchImpl(`${baseUrl}/realms/master/protocol/openid-connect/token`, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: 'password', client_id: 'admin-cli', username: user, password }).toString(),
  });
  if (res.status === 400 || res.status === 401) return null; // 資格が通らない（在らない・違う・締め出し）
  if (!res.ok) throw new Error(`トークンの取得が ${res.status}`);
  return (await res.json()).access_token;
}

function makeAdmin(baseUrl, token, fetchImpl = fetch) {
  return async function call(method, p, body) {
    const res = await fetchImpl(`${baseUrl}${p}`, {
      method,
      headers: { authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'content-type': 'application/json' }) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    if (!res.ok) throw new Error(`${method} ${p.replace(/\/[0-9a-f-]{36}/g, '/<id>')} が ${res.status}`);
    return text ? JSON.parse(text) : null;
  };
}

/** master realm の総当たりの検知を有効にする。変えた設定の名前の一覧を返す（在れば空）。 */
async function harden(call) {
  const realm = await call('GET', '/admin/realms/master');
  const diff = Object.entries(MASTER_BRUTE_FORCE).filter(([k, v]) => realm[k] !== v).map(([k]) => k);
  if (diff.length) await call('PUT', '/admin/realms/master', { ...MASTER_BRUTE_FORCE });
  return diff;
}

/** 一時管理者を消す。消した件数を返す。 */
async function retire(call, username) {
  const users = ((await call('GET', `/admin/realms/master/users?username=${encodeURIComponent(username)}&exact=true`)) || []).filter((u) => u.username === username.toLowerCase());
  for (const u of users) await call('DELETE', `/admin/realms/master/users/${u.id}`);
  return users.length;
}

async function main(env = process.env, { log = console.log, fetchImpl = fetch } = {}) {
  const mode = env.MODE;
  if (!['harden', 'retire', 'check'].includes(mode)) { log('ERROR: MODE は harden・retire・check のどれか'); return 2; }
  const base = String(env.KC_URL || '').replace(/\/$/, '');
  if (!/^https:\/\//.test(base)) { log('ERROR: KC_URL は https'); return 2; }
  if (!env.KC_ADMIN_USER || !env.KC_ADMIN_PASSWORD) { log('ERROR: KC_ADMIN_USER・KC_ADMIN_PASSWORD が無い（Secret keycloak-admin）'); return 2; }
  const token = await tokenFor(base, env.KC_ADMIN_USER, env.KC_ADMIN_PASSWORD, fetchImpl);
  if (mode === 'check') {
    if (token) { log('NG: master realm の一時管理者が在る（閉じ忘れ。keycloak-admin.sh --live close）'); return 3; }
    log('OK: master realm の一時管理者は在らない（その資格でトークンを取れない）');
    return 0;
  }
  if (!token) { log('ERROR: 一時管理者でトークンを取れない（開いていない。keycloak-admin.sh --live open）'); return 1; }
  const call = makeAdmin(base, token, fetchImpl);
  const changed = await harden(call);
  log(changed.length ? `master realm の総当たりの検知を有効にした（${changed.join('・')}）` : 'master realm の総当たりの検知は既に有効');
  if (mode === 'harden') return 0;
  const n = await retire(call, env.KC_ADMIN_USER);
  log(`一時管理者を ${n} 件消した`);
  if (await tokenFor(base, env.KC_ADMIN_USER, env.KC_ADMIN_PASSWORD, fetchImpl)) { log('ERROR: 消した後もトークンを取れる'); return 1; }
  log('OK: 一時管理者の資格でトークンを取れないことを確かめた');
  return 0;
}

module.exports = { MASTER_BRUTE_FORCE, harden, retire, main };
if (require.main === module) {
  main().then((code) => process.exit(code), (e) => { console.log(`ERROR: ${e.message}`); process.exit(1); });
}
