#!/usr/bin/env node
'use strict';
/*
 * NFR-18, NFR-05, IADR-0700: 稼働中のクラウドの Keycloak の realm に、取り込み元（Secret keycloak-realm-import の platform-realm.json。
 * realm-for-cloud.js の雛形に ESO が OpenBao の値を差し込んだもの）に在って稼働の realm に無い**機密クライアント（サービスアカウントを持つもの）だけ**を作る。
 *
 * なぜ要るか: Keycloak の `--import-realm` は同名の realm が在ると黙って飛ばす。取り込み元にクライアントを足しても、稼働の realm は変わらない。
 *
 *   - 在るクライアントの secret・設定は読まず書かない。既定のスコープ（宛先）とサービスアカウントのロールだけを読んで確かめる。
 *     無いクライアントだけを取り込み元の表現（secret を含む）で作る。
 *   - 既定のスコープの欠けは失敗にする（足さない）。直し方は、サービスごとのクライアント（ai-stock-trading-svc-<サービス>。この Job が足すもの）なら
 *     消してから流し直す。それ以外（旧の共有クライアントなど稼働中のもの）は消さない（消すと稼働中の取引ユニットが止まる）。取り込み元との差分を確かめる。
 *   - 作ったクライアント（と在るクライアント）のサービスアカウントに、取り込み元の利用者の realm ロールのうち無いものだけを足す（外さない）。
 *   - クライアントの既定のスコープが稼働の realm に無ければ、何も書かずに止める（無いスコープは Keycloak が黙って捨て、トークンの宛先が欠ける）。
 *   - MODE=check は読むだけ（無いクライアント・足りないロールを名指しし、在れば 3 で終わる）。MODE=apply は書く。
 *   - NFR-18, IADR-0706: MODE=sync-secrets は、在る機密クライアントの secret を取り込み元の値（OpenBao で作り直した値）へ合わせる
 *     （作り直しの反映。`--import-realm` は在る realm を取り込み直さない）。違うものだけを書き、同じものは書かない。無いクライアントは作らない
 *     （apply の役目。名指して 1 で終わる）。secret の値は比べるだけで出力しない。secret 以外の設定は変えない（読んだ表現に secret だけを差す）。
 *   - 値（secret・トークン・パスワード）は出力しない。出すのはクライアントの名前・ロールの名前・件数だけ。
 *
 * Job（deploy/cloud/keycloak-clients/job.yaml）の中で node:22-alpine が実行する。手元の入口は keycloak-clients.sh。外部依存なし（Node 標準の fetch・fs）。
 * env: KC_URL（https://keycloak:8443）・REALM（platform）・REALM_FILE・KC_ADMIN_USER・KC_ADMIN_PASSWORD・MODE（apply / check）。
 */
const fs = require('fs');

/** この Job が足すサービスごとのクライアント（消して流し直してよい）。名前の形は realm-for-cloud.js の SERVICE_CLIENTS と同じ（試験が固定する）。 */
const REPLACEABLE_CLIENT = /^ai-stock-trading-svc-[a-z][a-z-]*$/;
const fixHint = (clientId) => (REPLACEABLE_CLIENT.test(clientId)
  ? 'サービスごとのクライアント。管理の画面で消してから流し直す'
  : '稼働中のクライアント。消さない（消すと稼働中の取引ユニットが止まる）。取り込み元との差分を確かめる');

function desiredFrom(realm) {
  const users = new Map((realm.users || []).filter((u) => u.serviceAccountClientId).map((u) => [u.serviceAccountClientId, u]));
  return (realm.clients || [])
    .filter((c) => c.serviceAccountsEnabled === true && c.publicClient !== true && typeof c.secret === 'string')
    .map((c) => ({ client: c, roles: [...new Set(((users.get(c.clientId) || {}).realmRoles) || [])] }));
}

function makeAdmin(baseUrl, token) {
  return async function call(method, p, body) {
    const res = await fetch(`${baseUrl}${p}`, {
      method,
      headers: { authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'content-type': 'application/json' }) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    if (!res.ok) throw new Error(`${method} ${p.replace(/\/[0-9a-f-]{36}/g, '/<id>')} が ${res.status}`);
    return text ? JSON.parse(text) : null;
  };
}

async function adminToken(baseUrl, user, password) {
  const res = await fetch(`${baseUrl}/realms/master/protocol/openid-connect/token`, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: 'password', client_id: 'admin-cli', username: user, password }),
  });
  if (!res.ok) throw new Error(`管理者のトークンを取れない（${res.status}）`);
  return (await res.json()).access_token;
}

/** 稼働の realm と突き合わせ、無いクライアントを作り、サービスアカウントへ無いロールを足す。戻り値は { missing, created, rolesLacking（check で足りない）, rolesAdded（apply で足した）, problems }。 */
async function reconcile(call, realmName, desired, { apply, log = console.log }) {
  const r = `/admin/realms/${encodeURIComponent(realmName)}`;
  const scopes = new Set(((await call('GET', `${r}/client-scopes`)) || []).map((s) => s.name));
  const roles = new Set(((await call('GET', `${r}/roles`)) || []).map((x) => x.name));
  const out = { missing: [], created: [], rolesLacking: [], rolesAdded: [], problems: [] };
  for (const { client, roles: want } of desired) {
    for (const s of client.defaultClientScopes || []) if (!scopes.has(s)) out.problems.push(`${client.clientId} の既定のスコープ ${s} が稼働の realm に無い`);
    for (const x of want) if (!roles.has(x)) out.problems.push(`${client.clientId} のサービスアカウントの realm ロール ${x} が稼働の realm に無い`);
  }
  if (out.problems.length > 0) return out; // 何も書かない
  for (const { client, roles: want } of desired) {
    const id = client.clientId;
    let live = ((await call('GET', `${r}/clients?clientId=${encodeURIComponent(id)}`)) || []).find((c) => c.clientId === id);
    if (!live) {
      out.missing.push(id);
      if (!apply) { log(`    無い: クライアント ${id}`); continue; }
      const body = { ...client };
      delete body.id;
      await call('POST', `${r}/clients`, body);
      live = ((await call('GET', `${r}/clients?clientId=${encodeURIComponent(id)}`)) || []).find((c) => c.clientId === id);
      if (!live) throw new Error(`クライアント ${id} を作った後に読めない`);
      out.created.push(id);
      log(`    作った: クライアント ${id}（サービスアカウントだけ。secret は取り込み元の値）`);
    } else {
      log(`    在る: クライアント ${id}（触らない）`);
    }
    // 作ったものも在るものも、既定のスコープ（取引ユニットの宛先 aud を載せるもの）が付いているかを読んで確かめる。足しはしない
    // （在るものは触らない）。欠けていれば失敗にする（流し直しで「在る」として素通りし、宛先の無いクライアントが残るのを防ぐ）。
    const got = new Set(((await call('GET', `${r}/clients/${live.id}/default-client-scopes`)) || []).map((x) => x.name));
    for (const sc of client.defaultClientScopes || []) {
      if (!got.has(sc)) out.problems.push(`${id} に既定のスコープ ${sc} が付いていない（${fixHint(id)}）`);
    }
    const sa = await call('GET', `${r}/clients/${live.id}/service-account-user`);
    const has = new Set(((await call('GET', `${r}/users/${sa.id}/role-mappings/realm`)) || []).map((x) => x.name));
    const lack = want.filter((x) => !has.has(x));
    if (lack.length === 0) continue;
    if (!apply) { out.rolesLacking.push(`${id}:${lack.join('+')}`); log(`    足りない: ${id} のサービスアカウントのロール ${lack.join(', ')}`); continue; }
    const reps = [];
    for (const x of lack) reps.push(await call('GET', `${r}/roles/${encodeURIComponent(x)}`));
    await call('POST', `${r}/users/${sa.id}/role-mappings/realm`, reps);
    out.rolesAdded.push(`${id}:${lack.join('+')}`);
    log(`    足した: ${id} のサービスアカウントのロール ${lack.join(', ')}`);
  }
  return out;
}

/** IADR-0706: 在る機密クライアントの secret を取り込み元の値へ合わせる。戻り値は { synced, same, missing }。 */
async function syncSecrets(call, realmName, desired, { log = console.log } = {}) {
  const r = `/admin/realms/${encodeURIComponent(realmName)}`;
  const out = { synced: [], same: [], missing: [] };
  for (const { client } of desired) {
    const id = client.clientId;
    const live = ((await call('GET', `${r}/clients?clientId=${encodeURIComponent(id)}`)) || []).find((c) => c.clientId === id);
    if (!live) { out.missing.push(id); log(`    無い: クライアント ${id}（作るのは apply）`); continue; }
    const cur = (await call('GET', `${r}/clients/${live.id}/client-secret`)) || {};
    if (cur.value === client.secret) { out.same.push(id); log(`    同じ: クライアント ${id} の secret（書かない）`); continue; }
    const rep = await call('GET', `${r}/clients/${live.id}`);
    await call('PUT', `${r}/clients/${live.id}`, { ...rep, secret: client.secret });
    const after = (await call('GET', `${r}/clients/${live.id}/client-secret`)) || {};
    if (after.value !== client.secret) throw new Error(`クライアント ${id} の secret を合わせた後に読むと違う`);
    out.synced.push(id);
    log(`    合わせた: クライアント ${id} の secret（取り込み元の値。値は表示しない）`);
  }
  return out;
}

async function main(env = process.env, log = console.log) {
  const mode = env.MODE || 'check';
  if (!['apply', 'check', 'sync-secrets'].includes(mode)) { log(`ERROR: MODE は apply・check・sync-secrets のどれか（${mode}）`); return 2; }
  for (const k of ['KC_URL', 'REALM', 'REALM_FILE', 'KC_ADMIN_USER', 'KC_ADMIN_PASSWORD']) if (!env[k]) { log(`ERROR: env ${k} が無い`); return 2; }
  if (!/^https:\/\//.test(env.KC_URL)) { log('ERROR: KC_URL は https（基盤の CA で検証する）'); return 2; }
  const realm = JSON.parse(fs.readFileSync(env.REALM_FILE, 'utf8'));
  if (realm.realm !== env.REALM) { log(`ERROR: 取り込み元の realm（${realm.realm}）が REALM（${env.REALM}）と違う`); return 2; }
  if (/\{\{|\$\{[A-Z]/.test(JSON.stringify(realm))) { log('ERROR: 取り込み元に置換の印が残っている（ESO の差し込みの前の雛形を読んだ）'); return 2; }
  const desired = desiredFrom(realm);
  log(`==> realm ${env.REALM}: 取り込み元の機密クライアント ${desired.length} 件（${desired.map((d) => d.client.clientId).join(', ')}）を稼働と突き合わせる（${mode}）`);
  const call = makeAdmin(env.KC_URL.replace(/\/$/, ''), await adminToken(env.KC_URL.replace(/\/$/, ''), env.KC_ADMIN_USER, env.KC_ADMIN_PASSWORD));
  if (mode === 'sync-secrets') {
    const s = await syncSecrets(call, env.REALM, desired, { log });
    log(`OK: secret を合わせた ${s.synced.length} 件・同じ ${s.same.length} 件・無いクライアント ${s.missing.length} 件`);
    return s.missing.length > 0 ? 1 : 0;
  }
  const out = await reconcile(call, env.REALM, desired, { apply: mode === 'apply', log });
  for (const p of out.problems) log(`ERROR: ${p}`);
  if (out.problems.length > 0) return 1;
  log(mode === 'apply'
    ? `OK: 無かったクライアント ${out.missing.length} 件・作った ${out.created.length} 件・ロールを足した ${out.rolesAdded.length} 件`
    : `OK: 無いクライアント ${out.missing.length} 件・ロールの足りないサービスアカウント ${out.rolesLacking.length} 件（読むだけ。書いていない）`);
  return mode === 'check' && (out.missing.length > 0 || out.rolesLacking.length > 0) ? 3 : 0;
}

module.exports = { desiredFrom, reconcile, syncSecrets, main, REPLACEABLE_CLIENT };
if (require.main === module) {
  main().then((code) => process.exit(code), (e) => { console.log(`ERROR: ${e.message}`); process.exit(1); });
}
