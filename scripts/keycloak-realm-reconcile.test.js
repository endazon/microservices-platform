#!/usr/bin/env node
'use strict';
/*
 * keycloak-realm-reconcile.test.js
 * FR-05, NFR-09, ADR-0004 / ADR-0026, IADR-0368 (#1088 / #324):
 * deploy/local/keycloak-setup/reconcile-realm.js の計画器（純粋関数）の単体試験。
 *
 * 固定するもの:
 *   1. 一致していれば計画は 0 件（陰性対照。永続化後の「毎回何かを当ててしまう」を止める）。
 *   2. 宣言層の差分は種類ごとに 1 件の操作になる（realm 設定 / requiredAction / role / scope / client / mapper /
 *      scope 割当 / group / seed 利用者 / サービスアカウント）。
 *   3. 実行時層には触れない: 既存の人間の利用者・smtpServer。宣言に無い余剰の実体も消さない。
 *   4. 前提が無い操作は deferred として数えられ、黙って消えない（check モードで drift になる）。
 *   5. fixture は **実物の realm JSON から切り出す**（値を書き写さない。宣言が変わればここも追随する）。
 *
 * 外部依存ゼロ（Node 標準 assert のみ）。実行: node scripts/keycloak-realm-reconcile.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { plan, contains, merge, RUNTIME_OWNED_REALM_KEYS } = require('../deploy/local/keycloak-setup/reconcile-realm.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const REALM = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'deploy', 'keycloak', 'microservices-platform-realm.json'), 'utf8'));

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}
const clone = (v) => JSON.parse(JSON.stringify(v));
let seq = 0;
const uuid = () => `id-${++seq}`;

/**
 * 宣言から「完全に一致している稼働側」を合成する（import 直後の Keycloak の形を模す）。
 * Keycloak が往復で揺らすもの（数値の文字列化・余剰の属性・追加のスコープ）も混ぜ、比較が寛容側で
 * 正しいことを同時に確かめる。
 */
function liveFrom(desired) {
  const realm = clone(desired);
  for (const k of ['users', 'clients', 'clientScopes', 'roles', 'groups', 'components', 'requiredActions']) delete realm[k];
  realm.id = uuid();
  realm.accessTokenLifespan = String(realm.accessTokenLifespan); // 揺れ: 数値 ↔ 文字列
  realm.supportedLocales = [...realm.supportedLocales].reverse(); // 揺れ: 集合の順序
  realm.smtpServer = { ...realm.smtpServer, user: 'relay-user', password: '**********' }; // 実行時注入（IADR-0261）
  const realmRoles = desired.roles.realm.map((r) => ({ ...clone(r), id: uuid(), composite: false }));
  const clientScopes = desired.clientScopes.map((s) => ({
    ...clone(s), id: uuid(), protocolMappers: (s.protocolMappers || []).map((m) => ({ ...clone(m), id: uuid() })),
  }));
  const clients = desired.clients.map((c) => {
    const l = { ...clone(c), id: uuid(), protocolMappers: (c.protocolMappers || []).map((m) => ({ ...clone(m), id: uuid() })) };
    delete l.secret;
    l.attributes = { ...(l.attributes || {}), 'client.secret.creation.time': '1700000000' }; // 余剰の属性
    return l;
  });
  const clientSecrets = Object.fromEntries(desired.clients.filter((c) => c.secret).map((c) => [c.clientId, c.secret]));
  const clientRoles = {};
  for (const [cid, roles] of Object.entries(desired.roles.client || {})) clientRoles[cid] = roles.map((r) => ({ ...clone(r), id: uuid() }));
  // realm-management はビルトイン。サービスアカウントの clientRoles が指すので居る。
  clients.push({ id: uuid(), clientId: 'realm-management' });
  clientRoles['realm-management'] = ['view-users', 'manage-users', 'view-realm', 'query-users'].map((n) => ({ id: uuid(), name: n }));
  const groups = desired.groups.map((g) => ({
    ...clone(g), id: uuid(), subGroups: (g.subGroups || []).map((s) => ({ ...clone(s), id: uuid() })),
  }));
  const users = {};
  const serviceAccounts = {};
  for (const u of desired.users) {
    if (u.serviceAccountClientId) {
      serviceAccounts[u.serviceAccountClientId] = {
        user: { id: uuid(), username: u.username, attributes: clone(u.attributes || {}) },
        realmRoles: [...(u.realmRoles || [])],
        clientRoles: clone(u.clientRoles || {}),
      };
    } else {
      users[u.username] = { id: uuid(), username: u.username, attributes: clone(u.attributes || {}), requiredActions: [] };
    }
  }
  return {
    realm, requiredActions: clone(desired.requiredActions), realmRoles, clientScopes, clients, clientSecrets, clientRoles,
    roleComposites: {}, groups, users, serviceAccounts,
  };
}

const opsOf = (desired, live) => plan(desired, live);
const kinds = (ops) => ops.map((o) => o.op);

// --- 1. 陰性対照 ---------------------------------------------------------------------

ok('一致していれば計画は 0 件（数値の文字列化・集合の順序・余剰属性・smtp の実行時注入は差分ではない）', () => {
  assert.deepStrictEqual(opsOf(REALM, liveFrom(REALM)), []);
});

ok('fixture は実物の realm JSON から切り出している（宣言が空なら試験も空になる＝陽性対照）', () => {
  assert.ok(REALM.clients.length >= 5 && REALM.users.length >= 4 && REALM.clientScopes.length >= 1, 'realm JSON の形が変わった');
  assert.ok(REALM.users.some((u) => u.serviceAccountClientId), 'サービスアカウント利用者が宣言に無い');
});

// --- 2. 宣言層の差分は種類ごとに 1 件 --------------------------------------------------

ok('realm 設定の差分（例: TOTP ポリシー・テーマ）は realm.update 1 件になり、smtpServer は body から落ちる', () => {
  const live = liveFrom(REALM);
  live.realm.otpPolicyDigits = 8;
  live.realm.loginTheme = 'keycloak';
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(kinds(ops), ['realm.update']);
  assert.strictEqual(ops[0].method, 'PUT');
  assert.strictEqual(ops[0].path, '/admin/realms/platform');
  assert.strictEqual(ops[0].body.otpPolicyDigits, REALM.otpPolicyDigits);
  assert.strictEqual(ops[0].body.loginTheme, REALM.loginTheme);
  for (const k of RUNTIME_OWNED_REALM_KEYS) assert.ok(!(k in ops[0].body), `${k} が body に載っている（実行時所有）`);
  assert.ok(!('clients' in ops[0].body) && !('users' in ops[0].body), 'コレクションが realm PUT に載っている');
  assert.ok(/otpPolicyDigits/.test(ops[0].reason) && /loginTheme/.test(ops[0].reason), '理由に差分キーが無い');
});

ok('requiredActions の defaultAction が違えば requiredAction.update（#1088 の症状そのもの）', () => {
  const live = liveFrom(REALM);
  const totp = live.requiredActions.find((r) => r.alias === 'CONFIGURE_TOTP');
  totp.defaultAction = false;
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(kinds(ops), ['requiredAction.update']);
  assert.strictEqual(ops[0].path, '/admin/realms/platform/authentication/required-actions/CONFIGURE_TOTP');
  assert.strictEqual(ops[0].body.defaultAction, true);
});

ok('realm ロールが無ければ role.create、client ロールが無ければ clientRole.create', () => {
  const live = liveFrom(REALM);
  live.realmRoles = live.realmRoles.filter((r) => r.name !== 'wiki-editor');
  const [cid] = Object.keys(REALM.roles.client);
  live.clientRoles[cid] = [];
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(kinds(ops).sort(), ['clientRole.create', 'role.create']);
  assert.strictEqual(ops.find((o) => o.op === 'role.create').body.name, 'wiki-editor');
  assert.ok(ops.find((o) => o.op === 'clientRole.create').path.includes('/clients/'));
});

ok('client scope が無ければ clientScope.create（mappers 込み）、mapper だけ違えば mapper.update', () => {
  const live = liveFrom(REALM);
  const abac = REALM.clientScopes.find((s) => s.name === 'abac-attributes');
  live.clientScopes = live.clientScopes.filter((s) => s.name !== 'abac-attributes');
  // scope が消えたなら、それを割り当てていた client の割当も消えている（Keycloak は scope 削除で割当を外す）。
  for (const c of live.clients) if (Array.isArray(c.defaultClientScopes)) c.defaultClientScopes = c.defaultClientScopes.filter((n) => n !== 'abac-attributes');
  let ops = opsOf(REALM, live);
  const create = ops.find((o) => o.op === 'clientScope.create');
  assert.ok(create, 'clientScope.create が無い');
  assert.strictEqual(create.body.protocolMappers.length, abac.protocolMappers.length);
  // scope が消えると、それを default に持つ client の割当は deferred（scope がまだ無い）
  assert.ok(ops.some((o) => o.op === 'deferred' && /abac-attributes/.test(o.target)), '割当が deferred になっていない');

  const live2 = liveFrom(REALM);
  const m = live2.clientScopes.find((s) => s.name === 'abac-attributes').protocolMappers.find((x) => x.name === 'clearance');
  m.config['claim.name'] = 'wrong';
  ops = opsOf(REALM, live2);
  assert.deepStrictEqual(kinds(ops), ['mapper.update']);
  assert.strictEqual(ops[0].body.config['claim.name'], 'clearance');
  assert.strictEqual(ops[0].body.id, m.id, 'GET の全体へ合成していない（id が落ちた）');
});

ok('client の属性（backchannel.logout.url）が違えば client.update 1 件で、余剰の属性は保たれる（#1115 の後追いを一般化）', () => {
  const live = liveFrom(REALM);
  const bff = live.clients.find((c) => c.clientId === 'bff');
  bff.attributes['backchannel.logout.url'] = 'http://localhost:5000/bff/auth/backchannel-logout';
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(kinds(ops), ['client.update']);
  assert.strictEqual(ops[0].path, `/admin/realms/platform/clients/${bff.id}`);
  const want = REALM.clients.find((c) => c.clientId === 'bff').attributes['backchannel.logout.url'];
  assert.strictEqual(ops[0].body.attributes['backchannel.logout.url'], want);
  assert.strictEqual(ops[0].body.attributes['client.secret.creation.time'], '1700000000', '余剰の属性が消えた');
  assert.ok(!('protocolMappers' in ops[0].body) && !('defaultClientScopes' in ops[0].body));
  assert.ok(/attributes/.test(ops[0].reason));
});

ok('client の secret が違えば client.update になるが、理由に値は出ない', () => {
  const live = liveFrom(REALM);
  live.clientSecrets.bff = 'rotated-elsewhere';
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(kinds(ops), ['client.update']);
  assert.ok(!ops[0].reason.includes('rotated-elsewhere') && !ops[0].reason.includes(REALM.clients.find((c) => c.clientId === 'bff').secret));
  assert.ok(/secret/.test(ops[0].reason));
});

ok('client が無ければ client.create、そのサービスアカウントは deferred（次の周で当てる）', () => {
  const live = liveFrom(REALM);
  live.clients = live.clients.filter((c) => c.clientId !== 'identity-admin');
  delete live.serviceAccounts['identity-admin'];
  const ops = opsOf(REALM, live);
  assert.ok(ops.some((o) => o.op === 'client.create' && o.target === 'identity-admin'));
  assert.ok(ops.some((o) => o.op === 'deferred' && /service-account-identity-admin/.test(o.target)));
  assert.ok(!('id' in ops.find((o) => o.op === 'client.create').body));
});

ok('scope 割当は宣言が全集合: 欠落は add、余剰は remove', () => {
  const live = liveFrom(REALM);
  const bff = live.clients.find((c) => c.clientId === 'bff');
  bff.defaultClientScopes = bff.defaultClientScopes.filter((n) => n !== 'abac-attributes').concat(['email', 'realm-management-roles']);
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(kinds(ops).sort(), ['client.scope.add', 'client.scope.remove']);
  assert.strictEqual(ops.find((o) => o.op === 'client.scope.add').target, 'bff:abac-attributes');
  assert.strictEqual(ops.find((o) => o.op === 'client.scope.remove').target, 'bff:realm-management-roles');
});

ok('グループが無ければ group.create（サブグループは deferred）、サブグループだけ無ければ group.child.create', () => {
  const live = liveFrom(REALM);
  live.groups = live.groups.filter((g) => g.name !== 'clearance');
  let ops = opsOf(REALM, live);
  assert.ok(ops.some((o) => o.op === 'group.create' && o.target === 'clearance'));
  assert.ok(ops.some((o) => o.op === 'deferred' && /subGroups of clearance/.test(o.target)));

  const live2 = liveFrom(REALM);
  const dept = live2.groups.find((g) => g.name === 'department');
  dept.subGroups = dept.subGroups.filter((s) => s.name !== 'hr');
  ops = opsOf(REALM, live2);
  assert.deepStrictEqual(kinds(ops), ['group.child.create']);
  assert.strictEqual(ops[0].path, `/admin/realms/platform/groups/${dept.id}/children`);
});

ok('seed 利用者が無ければ user.create（資格情報・requiredActions・グループを body に、ロール割当を postSteps に運ぶ）', () => {
  const live = liveFrom(REALM);
  live.users.developer = null;
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(kinds(ops), ['user.create']);
  const dev = REALM.users.find((u) => u.username === 'developer');
  const op = ops[0];
  assert.deepStrictEqual(op.body.credentials, dev.credentials);
  assert.deepStrictEqual(op.body.requiredActions, dev.requiredActions);
  assert.deepStrictEqual(op.body.groups, dev.groups);
  assert.ok(!('realmRoles' in op.body), 'POST /users は realmRoles を処理しない。postSteps へ');
  const realmStep = op.postSteps.find((s) => s.pathSuffix === '/role-mappings/realm');
  assert.deepStrictEqual(realmStep.body.map((r) => r.name).sort(), [...dev.realmRoles].sort());
  assert.ok(realmStep.body.every((r) => r.id), 'ロール参照に id が無い');
});

ok('seed 利用者が無くてもロールがまだ無ければ deferred（前提が無い操作は黙って消えない）', () => {
  const live = liveFrom(REALM);
  live.users.developer = null;
  live.realmRoles = live.realmRoles.filter((r) => r.name !== 'trading-owner');
  const ops = opsOf(REALM, live);
  assert.ok(ops.some((o) => o.op === 'role.create' && o.target === 'trading-owner'));
  assert.ok(ops.some((o) => o.op === 'deferred' && o.target === 'user developer'));
  assert.ok(!ops.some((o) => o.op === 'user.create'));
});

ok('サービスアカウントの realm ロール・client ロール・属性の欠落はそれぞれ 1 件になる', () => {
  const live = liveFrom(REALM);
  live.serviceAccounts['abac-seeder'].realmRoles = [];
  live.serviceAccounts['abac-seeder'].user.attributes = {};
  live.serviceAccounts['identity-admin'].clientRoles['realm-management'] = ['view-users'];
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(kinds(ops).sort(), ['user.attributes.update', 'user.clientRoles.add', 'user.realmRoles.add']);
  const cr = ops.find((o) => o.op === 'user.clientRoles.add');
  assert.deepStrictEqual(cr.body.map((r) => r.name).sort(), ['manage-users', 'view-realm']);
  assert.ok(/manage-users/.test(cr.reason) && !/view-users/.test(cr.reason));
});

// --- 3. 実行時層には触れない ------------------------------------------------------------

ok('既存の人間の利用者は宣言と違っても触らない（資格情報・属性・requiredActions・ロールは実行時が所有する）', () => {
  const live = liveFrom(REALM);
  live.users.developer.attributes = { clearance: ['public'] }; // SC-17 で変えた
  live.users.developer.requiredActions = []; // TOTP を登録し終えた
  live.users['poc-user'].enabled = false;
  assert.deepStrictEqual(opsOf(REALM, live), []);
});

ok('smtpServer が宣言と違っても触らない（IADR-0261 決定 2: runbook が kcadm で入れる実行時状態）', () => {
  const live = liveFrom(REALM);
  live.realm.smtpServer = { host: 'smtp.example.test', port: '587', from: 'ops@example.test', user: 'u', password: '**********' };
  assert.deepStrictEqual(opsOf(REALM, live), []);
});

ok('宣言に無い余剰の実体（client / role / group / 利用者 / mapper）は消さない（加算的）', () => {
  const live = liveFrom(REALM);
  live.clients.push({ id: uuid(), clientId: 'msp-probe-client', attributes: {} });
  live.realmRoles.push({ id: uuid(), name: 'ad-hoc-role' });
  live.groups.push({ id: uuid(), name: 'ad-hoc-group', subGroups: [] });
  live.users['someone-added-at-runtime'] = { id: uuid(), username: 'someone-added-at-runtime' };
  live.clients.find((c) => c.clientId === 'bff').protocolMappers.push({ id: uuid(), name: 'extra-mapper' });
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(ops, []);
  assert.ok(!ops.some((o) => o.method === 'DELETE'));
});

// --- 4. realm 自体が無いとき ----------------------------------------------------------

ok('realm が稼働側に無ければ realm.create 1 件（宣言を丸ごと。import と同じ形）で、他は計画しない', () => {
  const ops = opsOf(REALM, { realm: null });
  assert.deepStrictEqual(kinds(ops), ['realm.create']);
  assert.strictEqual(ops[0].path, '/admin/realms');
  assert.strictEqual(ops[0].body, REALM);
});

// --- 5. 比較の補助関数 ----------------------------------------------------------------

ok('contains: スカラーは文字列で、スカラー配列は集合で、オブジェクトは宣言のキーだけを見る', () => {
  assert.ok(contains(1025, '1025'));
  assert.ok(contains(true, 'true'));
  assert.ok(contains(['a', 'b'], ['b', 'a']));
  assert.ok(!contains(['a', 'b'], ['a']));
  assert.ok(contains({ x: 1 }, { x: '1', y: 2 }));
  assert.ok(!contains({ x: 1 }, { y: 2 }));
  assert.ok(!contains({ x: { y: 1 } }, { x: 'str' }));
});

ok('merge: オブジェクトは再帰、配列とスカラーは置換、宣言に無いキーは live のまま', () => {
  const out = merge({ a: { p: 1, q: 2 }, b: [1, 2], c: 'x' }, { a: { p: 9 }, b: [3] });
  assert.deepStrictEqual(out, { a: { p: 9, q: 2 }, b: [3], c: 'x' });
});

console.log(`\n${passed} tests passed.`);
