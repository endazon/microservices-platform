#!/usr/bin/env node
'use strict';
/*
 * NFR-18, NFR-05, IADR-0700: 稼働中のクラウドの Keycloak の realm に、取り込み元（Secret keycloak-realm-import の platform-realm.json。
 * realm-for-cloud.js の雛形に ESO が OpenBao の値を差し込んだもの）に在って稼働の realm に無い**機密クライアント（サービスアカウントを持つもの）だけ**を作る。
 *
 * なぜ要るか: Keycloak の `--import-realm` は同名の realm が在ると黙って飛ばす。取り込み元にクライアントを足しても、稼働の realm は変わらない。
 *
 *   - 在るクライアントは触らない（secret も設定も読まず・書かない）。無いクライアントだけを取り込み元の表現（secret を含む）で作る。
 *   - 作ったクライアント（と在るクライアント）のサービスアカウントに、取り込み元の利用者の realm ロールのうち無いものだけを足す（外さない）。
 *   - クライアントの既定のスコープが稼働の realm に無ければ、何も書かずに止める（無いスコープは Keycloak が黙って捨て、トークンの宛先が欠ける）。
 *   - MODE=check は読むだけ（無いクライアント・足りないロールを名指しし、在れば 3 で終わる）。MODE=apply は書く。
 *   - 値（secret・トークン・パスワード）は出力しない。出すのはクライアントの名前・ロールの名前・件数だけ。
 *
 * Job（deploy/cloud/keycloak-clients/job.yaml）の中で node:22-alpine が実行する。手元の入口は keycloak-clients.sh。外部依存なし（Node 標準の fetch・fs）。
 * env: KC_URL（https://keycloak:8443）・REALM（platform）・REALM_FILE・KC_ADMIN_USER・KC_ADMIN_PASSWORD・MODE（apply / check）。
 */
const fs = require('fs');

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

/** 稼働の realm と突き合わせ、無いクライアントを作り、サービスアカウントへ無いロールを足す。戻り値は { missing, created, rolesAdded, problems }。 */
async function reconcile(call, realmName, desired, { apply, log = console.log }) {
  const r = `/admin/realms/${encodeURIComponent(realmName)}`;
  const scopes = new Set(((await call('GET', `${r}/client-scopes`)) || []).map((s) => s.name));
  const roles = new Set(((await call('GET', `${r}/roles`)) || []).map((x) => x.name));
  const out = { missing: [], created: [], rolesAdded: [], problems: [] };
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
      const got = new Set(((await call('GET', `${r}/clients/${live.id}/default-client-scopes`)) || []).map((s) => s.name));
      for (const s of client.defaultClientScopes || []) if (!got.has(s)) out.problems.push(`${id} に既定のスコープ ${s} が付かない`);
    } else {
      log(`    在る: クライアント ${id}（触らない）`);
    }
    const sa = await call('GET', `${r}/clients/${live.id}/service-account-user`);
    const has = new Set(((await call('GET', `${r}/users/${sa.id}/role-mappings/realm`)) || []).map((x) => x.name));
    const lack = want.filter((x) => !has.has(x));
    if (lack.length === 0) continue;
    if (!apply) { out.rolesAdded.push(`${id}:${lack.join('+')}`); log(`    足りない: ${id} のサービスアカウントのロール ${lack.join(', ')}`); continue; }
    const reps = [];
    for (const x of lack) reps.push(await call('GET', `${r}/roles/${encodeURIComponent(x)}`));
    await call('POST', `${r}/users/${sa.id}/role-mappings/realm`, reps);
    out.rolesAdded.push(`${id}:${lack.join('+')}`);
    log(`    足した: ${id} のサービスアカウントのロール ${lack.join(', ')}`);
  }
  return out;
}

async function main(env = process.env, log = console.log) {
  const mode = env.MODE || 'check';
  if (!['apply', 'check'].includes(mode)) { log(`ERROR: MODE は apply か check（${mode}）`); return 2; }
  for (const k of ['KC_URL', 'REALM', 'REALM_FILE', 'KC_ADMIN_USER', 'KC_ADMIN_PASSWORD']) if (!env[k]) { log(`ERROR: env ${k} が無い`); return 2; }
  if (!/^https:\/\//.test(env.KC_URL)) { log('ERROR: KC_URL は https（基盤の CA で検証する）'); return 2; }
  const realm = JSON.parse(fs.readFileSync(env.REALM_FILE, 'utf8'));
  if (realm.realm !== env.REALM) { log(`ERROR: 取り込み元の realm（${realm.realm}）が REALM（${env.REALM}）と違う`); return 2; }
  if (/\{\{|\$\{[A-Z]/.test(JSON.stringify(realm))) { log('ERROR: 取り込み元に置換の印が残っている（ESO の差し込みの前の雛形を読んだ）'); return 2; }
  const desired = desiredFrom(realm);
  log(`==> realm ${env.REALM}: 取り込み元の機密クライアント ${desired.length} 件（${desired.map((d) => d.client.clientId).join(', ')}）を稼働と突き合わせる（${mode}）`);
  const call = makeAdmin(env.KC_URL.replace(/\/$/, ''), await adminToken(env.KC_URL.replace(/\/$/, ''), env.KC_ADMIN_USER, env.KC_ADMIN_PASSWORD));
  const out = await reconcile(call, env.REALM, desired, { apply: mode === 'apply', log });
  for (const p of out.problems) log(`ERROR: ${p}`);
  if (out.problems.length > 0) return 1;
  log(mode === 'apply'
    ? `OK: 無かったクライアント ${out.missing.length} 件・作った ${out.created.length} 件・ロールを足した ${out.rolesAdded.length} 件`
    : `OK: 無いクライアント ${out.missing.length} 件・ロールの足りないサービスアカウント ${out.rolesAdded.length} 件（読むだけ。書いていない）`);
  return mode === 'check' && (out.missing.length > 0 || out.rolesAdded.length > 0) ? 3 : 0;
}

module.exports = { desiredFrom, reconcile, main };
if (require.main === module) {
  main().then((code) => process.exit(code), (e) => { console.log(`ERROR: ${e.message}`); process.exit(1); });
}
