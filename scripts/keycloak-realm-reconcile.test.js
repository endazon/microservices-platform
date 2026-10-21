#!/usr/bin/env node
'use strict';
/*
 * keycloak-realm-reconcile.test.js
 * FR-05, NFR-09, ADR-0004 / ADR-0026, IADR-0369 (#1088 / #324):
 * deploy/local/keycloak-setup/reconcile-realm.js の計画器（純粋関数）の単体試験。
 *
 * 固定するもの:
 *   1. 一致していれば計画は 0 件（陰性対照。永続化後の「毎回何かを当ててしまう」を止める）。
 *   2. 宣言層の差分は種類ごとに 1 件の操作になる（realm 設定 / requiredAction / role / scope / client / mapper /
 *      scope 割当 / group / seed 利用者 / サービスアカウント）。
 *   3. 実行時層には触れない: 既存の人間の利用者。宣言に無い余剰の実体も消さない。
 *      ［2026-09-06 追記 / #1245 / IADR-0404］**`smtpServer` は宣言所有へ移った**（近接 MTA へ固定。状態 B を構造で消す）。
 *      **`resetPasswordAllowed` は条件つきの門所有**（宣言 true・稼働 false・`reset-gate.state=closed` の 1 組だけ除外）。
 *      🔴 逆向き（宣言 false・稼働 true）は drift のままであることを対で固定する。
 *   4. 前提が無い操作は deferred として数えられ、黙って消えない（check モードで drift になる）。
 *   5. fixture は **実物の realm JSON から切り出す**（値を書き写さない。宣言が変わればここも追随する）。
 *   6. ［2026-09-28 追記 / #1682 / ADR-0124 決定 1・IADR-0485］**client の `secret` は作成時にだけ運ぶ。**
 *      既存の client では比べず・`PUT` の本文に載せず・稼働の値を読みに行かない（対の手順で回した値を戻さない）。
 *   7. ［2026-10-09 追記 / #1830 / IADR-0517］**管理用の 3 クライアントを、許されていなければ宣言の（公知の）dev の値で作らない。**
 *      稼働の secret が dev の値のままかを `--check-dev-secrets` が名指す（読むだけ・値を出さない）。偽の Keycloak（HTTP）で main まで通す。
 *
 * 外部依存ゼロ（Node 標準 assert のみ）。実行: node scripts/keycloak-realm-reconcile.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const {
  plan, contains, merge, gateHoldsClosed, collectLive,
  devSecretCreations, devSecretFindings, collectGuardedSecrets, checkDevSecrets, DEV_SECRET_GUARDED_CLIENTS, DEV_SECRETS_ALLOWED_ENV,
  RUNTIME_OWNED_REALM_KEYS, CLIENT_CREATE_ONLY_KEYS, GATE_OWNED_REALM_KEYS, GATE_STATE_ATTRIBUTE, GATE_STATE_CLOSED,
  SEED_USER_SELF_SERVICE_ROLES,
  resolveE2eCredentials, passwordMeetsPolicy, describeSkipped, renderImport, describe,
} = require('../deploy/local/keycloak-setup/reconcile-realm.js');
const { generate: generateE2eAccounts, placeholderNames, propertyName } = require('./gen-e2e-accounts.js');

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
  // #1894: Keycloak が import 時に自分で作る realm ロール（宣言しない）。既定ロールの合成先に現れる。
  for (const name of ['offline_access', 'uma_authorization']) realmRoles.push({ id: uuid(), name });
  // 宣言が合成を持つ realm ロールは、import が合成先まで作る（既定ロールには Keycloak が uma_authorization も足す）。
  const roleComposites = {};
  for (const r of desired.roles.realm) {
    if (!(r.composite && r.composites && Array.isArray(r.composites.realm))) continue;
    roleComposites[r.name] = r.composites.realm.map((name) => realmRoles.find((x) => x.name === name));
  }
  const clientScopes = desired.clientScopes.map((s) => ({
    ...clone(s), id: uuid(), protocolMappers: (s.protocolMappers || []).map((m) => ({ ...clone(m), id: uuid() })),
  }));
  const clients = desired.clients.map((c) => {
    const l = { ...clone(c), id: uuid(), protocolMappers: (c.protocolMappers || []).map((m) => ({ ...clone(m), id: uuid() })) };
    delete l.secret;
    l.attributes = { ...(l.attributes || {}), 'client.secret.creation.time': '1700000000' }; // 余剰の属性
    return l;
  });
  const clientRoles = {};
  for (const [cid, roles] of Object.entries(desired.roles.client || {})) clientRoles[cid] = roles.map((r) => ({ ...clone(r), id: uuid() }));
  // realm-management はビルトイン。サービスアカウントの clientRoles が指すので居る。
  clients.push({ id: uuid(), clientId: 'realm-management' });
  // ［2026-09-07 / #1245 PR-C］`manage-realm` を足した —— 門（reset-gate）のサービスアカウントが
  // これを持つ。ビルトインの realm-management には元から在るロールであり、fixture が実物から
  // 遠いままだと「割当が deferred になる」形を試験できない。
  clientRoles['realm-management'] = ['view-users', 'manage-users', 'view-realm', 'manage-realm', 'query-users'].map((n) => ({ id: uuid(), name: n }));
  const groups = desired.groups.map((g) => ({
    ...clone(g), id: uuid(), subGroups: (g.subGroups || []).map((s) => ({ ...clone(s), id: uuid() })),
  }));
  const users = {};
  const userClientRoles = {};
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
      // #1894: import は宣言の clientRoles を当てる（collectLive は SEED_USER_SELF_SERVICE_ROLES の client だけを読む）。
      const cr = {};
      for (const cid of Object.keys(SEED_USER_SELF_SERVICE_ROLES)) if ((u.clientRoles || {})[cid]) cr[cid] = [...u.clientRoles[cid]];
      userClientRoles[u.username] = cr;
    }
  }
  return {
    realm, requiredActions: clone(desired.requiredActions), realmRoles, clientScopes, clients, clientRoles,
    roleComposites, groups, users, userClientRoles, serviceAccounts,
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

ok('realm 設定の差分（例: TOTP ポリシー・テーマ）は realm.update 1 件になる', () => {
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

// --- 2-b. smtpServer は宣言所有である（#1245 / ADR-0078 決定 2・IADR-0404）--------------
//
// 🔴 **状態 B（送出先未設定 ＋ 申請が開いている）を構造で作れなくするのが目的**である。
//    宣言が正になったので、稼働側で送出先が消えても／外を向いても、本 Job が宣言（近接 MTA）へ戻す。
//    実行時所有だった頃は「再起動で消えたまま」になり、**実在する利用者名だけ 500** が返る組へ落ちた。

ok('smtpServer は実行時所有では**ない**（空集合。宣言所有へ移した）', () => {
  assert.strictEqual(RUNTIME_OWNED_REALM_KEYS.size, 0, 'RUNTIME_OWNED_REALM_KEYS が空でない');
  assert.ok(!RUNTIME_OWNED_REALM_KEYS.has('smtpServer'));
});

ok('稼働側の smtpServer が消えていれば realm.update で宣言（近接 MTA）へ戻し、body に載せる', () => {
  const live = liveFrom(REALM);
  delete live.realm.smtpServer;
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(kinds(ops), ['realm.update']);
  assert.ok(/smtpServer/.test(ops[0].reason), '理由に smtpServer が無い');
  assert.deepStrictEqual(ops[0].body.smtpServer, REALM.smtpServer, 'body の smtpServer が宣言と一致しない');
});

ok('稼働側の smtpServer が外（実リレー）を向いていれば宣言へ戻す（状態 B / 誤送信の温床を潰す）', () => {
  const live = liveFrom(REALM);
  live.realm.smtpServer = { ...live.realm.smtpServer, host: 'smtp.gmail.com', port: '587' };
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(kinds(ops), ['realm.update']);
  assert.strictEqual(ops[0].body.smtpServer.host, REALM.smtpServer.host);
  assert.strictEqual(ops[0].body.smtpServer.port, REALM.smtpServer.port);
});

ok('宣言に無い smtpServer のキー（runbook が入れた user / password）は消さない（加算的な merge）', () => {
  const live = liveFrom(REALM);              // liveFrom は user / password を実行時注入として足す
  live.realm.smtpServer.host = 'smtp.gmail.com';
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(kinds(ops), ['realm.update']);
  assert.strictEqual(ops[0].body.smtpServer.user, 'relay-user');
  assert.strictEqual(ops[0].body.smtpServer.password, '**********');
});

// --- 2-c. resetPasswordAllowed は「条件つきで門が所有する」（#1245 / ADR-0078 決定 4）-----
//
// 🔴 **無条件の実行時所有にしてはならない。** それだと「宣言 false なのに稼働 true」
//    （閉じたはずの申請が開いている＝利用者名が漏れる向き）まで見なくなる。
//    除外するのは「宣言 true・稼働 false・attributes["reset-gate.state"]===closed」の 1 組だけである。

const closedLive = (attrs) => {
  const live = liveFrom(REALM);
  live.realm.resetPasswordAllowed = false;
  if (attrs !== null) live.realm.attributes = attrs;
  return live;
};

ok('宣言は resetPasswordAllowed=true である（この節の前提。陽性対照）', () => {
  assert.strictEqual(REALM.resetPasswordAllowed, true, 'realm 宣言が変わった。以下の試験の前提が崩れている');
  assert.ok(GATE_OWNED_REALM_KEYS.has('resetPasswordAllowed'));
});

ok('🔴 realm 宣言はトップレベルの attributes を持たない（門の状態を Job が open へ戻さない）', () => {
  // 宣言が attributes を持つと、それは**宣言所有**になり、門が書いた reset-gate.state=closed を
  // 本 Job が open へ戻す（除外は resetPasswordAllowed にしか効かない）。属性は実行時にだけ存在させる。
  assert.ok(!('attributes' in REALM), 'realm 宣言に attributes が入った。門の状態が Job に上書きされる');
});

ok('門が閉じている間（属性 reset-gate.state=closed）は drift 0 件 —— 開き直さない', () => {
  const ops = opsOf(REALM, closedLive({ [GATE_STATE_ATTRIBUTE]: GATE_STATE_CLOSED }));
  assert.deepStrictEqual(ops, [], `門が閉じた realm を開き直す計画が出た: ${JSON.stringify(kinds(ops))}`);
});

ok('門が閉じている間に**別のキー**が drift しても、PUT の body は閉じたまま（開き直さない）', () => {
  const live = closedLive({ [GATE_STATE_ATTRIBUTE]: GATE_STATE_CLOSED });
  live.realm.loginTheme = 'keycloak';
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(kinds(ops), ['realm.update']);
  assert.strictEqual(ops[0].body.loginTheme, REALM.loginTheme);
  assert.strictEqual(ops[0].body.resetPasswordAllowed, false, 'body が申請を開き直している');
  assert.ok(!/resetPasswordAllowed/.test(ops[0].reason), '理由に門所有のキーが混ざっている');
});

ok('属性が無い false（人が手で閉じた・realm が壊れた）は従来どおり drift 1 件で宣言へ戻す', () => {
  const ops = opsOf(REALM, closedLive(null));
  assert.deepStrictEqual(kinds(ops), ['realm.update']);
  assert.ok(/resetPasswordAllowed/.test(ops[0].reason));
  assert.strictEqual(ops[0].body.resetPasswordAllowed, true);
});

ok('属性が open（門は閉じたと言っていない）の false も drift 1 件で宣言へ戻す', () => {
  const ops = opsOf(REALM, closedLive({ [GATE_STATE_ATTRIBUTE]: 'open' }));
  assert.deepStrictEqual(kinds(ops), ['realm.update']);
  assert.strictEqual(ops[0].body.resetPasswordAllowed, true);
});

ok('🔴 逆向き（宣言 false・稼働 true）は属性が closed でも drift 1 件 —— 危険な向きを見逃さない', () => {
  const desired = { ...clone(REALM), resetPasswordAllowed: false };
  const live = liveFrom(REALM);
  live.realm.resetPasswordAllowed = true;
  live.realm.attributes = { [GATE_STATE_ATTRIBUTE]: GATE_STATE_CLOSED };
  const ops = opsOf(desired, live);
  assert.deepStrictEqual(kinds(ops), ['realm.update']);
  assert.ok(/resetPasswordAllowed/.test(ops[0].reason));
  assert.strictEqual(ops[0].body.resetPasswordAllowed, false, '閉じたはずの申請が開いたまま残っている');
});

ok('gateHoldsClosed は門所有でないキーには決して真を返さない（除外の射程を固定する）', () => {
  const liveRealm = { registrationAllowed: false, attributes: { [GATE_STATE_ATTRIBUTE]: GATE_STATE_CLOSED } };
  assert.strictEqual(gateHoldsClosed('registrationAllowed', true, liveRealm), false);
  assert.strictEqual(
    gateHoldsClosed('resetPasswordAllowed', true,
      { resetPasswordAllowed: false, attributes: { [GATE_STATE_ATTRIBUTE]: GATE_STATE_CLOSED } }),
    true,
  );
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
  // #1499 / IADR-0461 決定 5: realm が宣言していた唯一の client ロール（minio:consoleAdmin）は MinIO Console の
  // SSO と一緒に撤去した。**宣言に client ロールが 1 つも無い realm でも経路を測れる**よう、既存の client
  // （wiki-js）に合成のロールを 1 つ足した宣言で試す（実物の realm JSON は書き換えない）。
  const desired = clone(REALM);
  desired.roles.client = { 'wiki-js': [{ name: 'synthetic-client-role', description: 'test only' }] };
  const live = liveFrom(desired);
  live.realmRoles = live.realmRoles.filter((r) => r.name !== 'wiki-editor');
  const [cid] = Object.keys(desired.roles.client);
  live.clientRoles[cid] = [];
  const ops = opsOf(desired, live);
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

// ［2026-09-28 / #1682 / ADR-0124 決定 1・IADR-0485］従前は「secret が違えば client.update」を固定していた。
// 🔴 それは**認証基盤と Vault を対で回した値を、次の起動で認証基盤側だけ開発用の値へ戻す**動きだった（片側だけ書いた状態を作る）。
ok('既存の client の secret が宣言と違っても計画は 0 件（回した値を戻さない。ADR-0124 決定 1）', () => {
  const live = liveFrom(REALM);
  // GET の表現が secret を含む版の Keycloak を模す（含まない版は liveFrom の既定）。
  for (const c of live.clients) if (REALM.clients.some((d) => d.clientId === c.clientId && 'secret' in d)) c.secret = `rotated-${c.clientId}`;
  assert.ok(live.clients.filter((c) => c.secret).length >= 20, '陽性対照: secret を持つ client が宣言に揃っていない');
  assert.deepStrictEqual(opsOf(REALM, live), []);
});

ok('他の差分で client.update になっても、本文に secret を載せない（稼働の値も宣言の値も往復させない）', () => {
  const live = liveFrom(REALM);
  const bff = live.clients.find((c) => c.clientId === 'bff');
  bff.secret = 'rotated-elsewhere';
  bff.attributes['backchannel.logout.url'] = 'http://localhost:5000/bff/auth/backchannel-logout';
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(kinds(ops), ['client.update']);
  assert.ok(!('secret' in ops[0].body), 'PUT の本文に secret が載っている');
  assert.ok(!/secret/.test(ops[0].reason), '理由に secret が出ている（比べていないはず）');
});

ok('client が無ければ client.create の本文は宣言の secret を運ぶ（初期投入は「無いときだけ作る」）', () => {
  const live = liveFrom(REALM);
  live.clients = live.clients.filter((c) => c.clientId !== 'bff');
  const create = opsOf(REALM, live).find((o) => o.op === 'client.create' && o.target === 'bff');
  assert.ok(create, 'bff の client.create が無い');
  assert.strictEqual(create.body.secret, REALM.clients.find((c) => c.clientId === 'bff').secret);
});

ok('CLIENT_CREATE_ONLY_KEYS は secret を持つ（集合から外すと既存の client の secret を宣言所有に戻す）', () => {
  assert.ok(CLIENT_CREATE_ONLY_KEYS.has('secret'));
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

// --- 3-b. アカウントコンソール（SC-16）の構成と、seed 利用者の自己管理のロール（#1894 / IADR-0546）------------
//
// realm import で作られる利用者は既定ロールを受け取らない。宣言の seed 利用者は account の view-profile・manage-account を
// 直に持ち、**既存の realm でも**本 Job がこの 2 つだけを足す（境界の唯一の例外。加えるだけ）。

const HUMAN_SEEDS = REALM.users.filter((u) => !u.serviceAccountClientId).map((u) => u.username);

ok('#1894: 宣言の人の seed 利用者はすべて account の view-profile・manage-account を持つ（陽性対照）', () => {
  assert.ok(HUMAN_SEEDS.length >= 4, '人の seed 利用者が宣言から消えた');
  assert.deepStrictEqual(Object.keys(SEED_USER_SELF_SERVICE_ROLES), ['account']);
  assert.deepStrictEqual([...SEED_USER_SELF_SERVICE_ROLES.account].sort(), ['manage-account', 'view-profile']);
  for (const u of REALM.users.filter((x) => !x.serviceAccountClientId)) {
    assert.deepStrictEqual([...((u.clientRoles || {}).account || [])].sort(), ['manage-account', 'view-profile'], `${u.username} の account ロール`);
  }
});

ok('#1894: 既存の seed 利用者に自己管理のロールが無ければ user.clientRoles.add を 1 件ずつ（欠けた分だけ・加えるだけ）', () => {
  const live = liveFrom(REALM);
  live.userClientRoles['poc-user'] = { account: [] };                  // import で作られ既定ロールを受け取らなかった利用者
  live.userClientRoles.developer = { account: ['view-profile'] };      // 片方だけ持つ
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(kinds(ops), ['user.clientRoles.add', 'user.clientRoles.add']);
  const poc = ops.find((o) => o.target === 'poc-user:account');
  const accountUuid = live.clients.find((c) => c.clientId === 'account').id;
  assert.strictEqual(poc.path, `/admin/realms/platform/users/${live.users['poc-user'].id}/role-mappings/clients/${accountUuid}`);
  assert.deepStrictEqual(poc.body.map((r) => r.name).sort(), ['manage-account', 'view-profile']);
  assert.ok(poc.body.every((r) => r.id), 'ロール参照に id が無い');
  const dev = ops.find((o) => o.target === 'developer:account');
  assert.deepStrictEqual(dev.body.map((r) => r.name), ['manage-account']);
  assert.ok(!ops.some((o) => o.method === 'DELETE'), '外す操作が出た');
});

ok('#1894: 境界の例外は自己管理の 2 つだけ —— 宣言に他の client ロールや realm ロールがあっても既存の利用者へは足さない', () => {
  const desired = clone(REALM);
  const dev = desired.users.find((u) => u.username === 'developer');
  dev.clientRoles.account = [...dev.clientRoles.account, 'delete-account', 'manage-account-links'];
  dev.clientRoles['realm-management'] = ['manage-users'];
  const live = liveFrom(REALM);
  assert.deepStrictEqual(opsOf(desired, live), [], '自己管理の 2 つ以外を既存の人の利用者へ当てた');
  live.userClientRoles.developer = { account: [] };
  const ops = opsOf(desired, live);
  assert.deepStrictEqual(kinds(ops), ['user.clientRoles.add']);
  assert.deepStrictEqual(ops[0].body.map((r) => r.name).sort(), ['manage-account', 'view-profile']);
});

ok('#1894: 既定ロール経由で実効的に持っていれば（collectLive は合成を含む割当を読む）何もしない', () => {
  const live = liveFrom(REALM);
  for (const u of HUMAN_SEEDS) live.userClientRoles[u] = { account: ['manage-account', 'manage-account-links', 'view-profile'] };
  assert.deepStrictEqual(opsOf(REALM, live), []);
});

ok('#1894: account のロールがまだ無ければ deferred（黙って消えない）', () => {
  const live = liveFrom(REALM);
  live.userClientRoles['poc-user'] = { account: [] };
  live.clientRoles.account = live.clientRoles.account.filter((r) => r.name !== 'manage-account');
  const ops = opsOf(REALM, live);
  assert.ok(ops.some((o) => o.op === 'clientRole.create' && o.target === 'account:manage-account'));
  assert.ok(ops.some((o) => o.op === 'deferred' && o.target === 'user poc-user clientRoles account'));
  assert.ok(!ops.some((o) => o.op === 'user.clientRoles.add'));
});

ok('#1894: 旧い import の account-console（スコープ無し・写像は audience resolve だけ）には basic・profile と account のロールの写像を足す', () => {
  const live = liveFrom(REALM);
  const ac = live.clients.find((c) => c.clientId === 'account-console');
  ac.defaultClientScopes = [];
  ac.protocolMappers = ac.protocolMappers.filter((m) => m.name === 'audience resolve');
  delete ac.description;
  const ops = opsOf(REALM, live).filter((o) => /account-console/.test(o.target));
  assert.deepStrictEqual(kinds(ops).sort(), ['client.scope.add', 'client.scope.add', 'client.update', 'mapper.create']);
  assert.deepStrictEqual(ops.filter((o) => o.op === 'client.scope.add').map((o) => o.target).sort(), ['account-console:basic', 'account-console:profile']);
  const m = ops.find((o) => o.op === 'mapper.create');
  assert.strictEqual(m.body.config['usermodel.clientRoleMapping.clientId'], 'account', '写像が account のロールに絞られていない');
});

// ［2026-09-06 / #1245 / ADR-0078 決定 2・IADR-0404］**旧: 「smtpServer が宣言と違っても触らない」**
// （IADR-0261 決定 2 の実行時所有）を**反転させた**。送出先は近接 MTA（クラスタ内の Service 名）へ固定され、
// 秘匿値は relay 側の Secret へ移ったので、realm 側は宣言が正である。差分は当てて宣言へ戻す。
ok('smtpServer が宣言と違えば宣言（近接 MTA）へ戻す —— 実行時所有ではない（#1245 で反転）', () => {
  const live = liveFrom(REALM);
  live.realm.smtpServer = { host: 'smtp.example.test', port: '587', from: 'ops@example.test', user: 'u', password: '**********' };
  const ops = opsOf(REALM, live);
  assert.deepStrictEqual(kinds(ops), ['realm.update']);
  assert.strictEqual(ops[0].body.smtpServer.host, REALM.smtpServer.host);
  assert.strictEqual(ops[0].body.smtpServer.user, 'u', '宣言に無いキーまで消している');
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

// #1373: 稼働側のクライアントが在って service account が無効なとき、collectLive は SA 利用者を「未存在」として
// 返し、plan は client.update（SA 有効化）＋ deferred を出す。Keycloak はこの状態の service-account-user に
// 400 を返す（404 ではない）ため、以前はここで例外になり realm 全体の追随が止まっていた（2026-09-10 実測: `bff`）。
function fakeKc({ saEnabled, saUser, seen = [] }) {
  const R = `/admin/realms/${encodeURIComponent(REALM.realm)}`;
  const bff = { id: 'c-bff', clientId: 'bff', serviceAccountsEnabled: saEnabled };
  return {
    call: async () => { throw new Error('collectLive は call を使わない'); },
    get: async (p) => {
      seen.push(p);
      if (p === R) return { realm: REALM.realm };
      if (p === `${R}/clients?max=1000`) return [bff];
      if (p === `${R}/clients/c-bff/service-account-user`) {
        if (!saEnabled) throw new Error(`GET ${p} -> 400 {"error":"unknown_error"}`);
        return saUser;
      }
      if (p === `${R}/users/${saUser.id}/role-mappings/realm`) return [{ name: 'platform-service' }];
      if (p === `${R}/clients/c-bff/client-secret`) return { value: 'live-secret' };
      return [];
    },
  };
}
// --- 6. 試験アカウント e2e-*（#2024 / IADR-0570）------------------------------------------------
//
// 宣言は資格情報にプレースホルダ `${E2E_…}` だけを持つ。値は Secret e2e-accounts（OpenBao → ESO）を env で受けて差し込む。
// 差し込めない利用者は作らない（drift にも数えない）。作った後は他の人の seed 利用者と同じく実行時が所有する。

const E2E_USERS = ['e2e-user', 'e2e-operator', 'e2e-admin', 'e2e-noaccess', 'e2e-revocable', 'e2e-trading-owner'];
const E2E_EXPECTED = {
  'e2e-user': { roles: [], groups: ['/clearance/internal', '/department/engineering'] },
  'e2e-operator': { roles: ['platform-operator'], groups: ['/clearance/internal', '/department/engineering'] },
  'e2e-admin': { roles: ['platform-admin', 'platform-operator'], groups: ['/clearance/restricted', '/department/engineering'] },
  'e2e-noaccess': { roles: [], groups: ['/clearance/public'] },
  'e2e-revocable': { roles: [], groups: ['/clearance/internal'] },
  'e2e-trading-owner': { roles: ['trading-owner'], groups: ['/clearance/internal', '/department/engineering'] },
};
const envNameOf = (username, kind) => `E2E_${username.slice('e2e-'.length).toUpperCase().replace(/-/g, '_')}_${kind}`;
// 試験用の値（実値ではない）。生成器の出力を env の名前へ写して使う（生成器と差し込みの口が噛み合うことを同時に見る）。
const e2eEnv = () => {
  let n = 0;
  const kv = generateE2eAccounts(REALM, (max) => (n++ * 7 + 3) % max);
  return Object.fromEntries(placeholderNames(REALM).map((name) => [name, kv[propertyName(name)]]));
};
const withoutE2e = (realm) => { const r = clone(realm); r.users = r.users.filter((u) => !E2E_USERS.includes(u.username)); return r; };

ok('#2024: 宣言は 6 名を設計の表どおりのロール・グループ（＝属性）で持ち、資格情報はプレースホルダだけ・CONFIGURE_TOTP を持たない', () => {
  assert.ok(REALM.roles.realm.some((r) => r.name === 'trading-owner'), 'AST のロール trading-owner が realm platform に無い');
  for (const name of E2E_USERS) {
    const u = REALM.users.find((x) => x.username === name);
    assert.ok(u, `${name} が宣言に無い`);
    assert.deepStrictEqual([...(u.realmRoles || [])].sort(), [...E2E_EXPECTED[name].roles].sort(), `${name} の realm ロール`);
    assert.deepStrictEqual(u.groups, E2E_EXPECTED[name].groups, `${name} のグループ`);
    const attrGroups = [...(u.attributes.clearance || []).map((c) => `/clearance/${c}`), ...(u.attributes.department || []).map((d) => `/department/${d}`)];
    assert.deepStrictEqual(attrGroups, u.groups, `${name} の属性がグループと食い違う`);
    assert.ok(!(u.requiredActions || []).includes('CONFIGURE_TOTP'), `${name} に CONFIGURE_TOTP（2 つ目の OTP の登録画面が挟まる）`);
    assert.ok(u.email && u.firstName && u.lastName && u.emailVerified === true, `${name} のプロフィール（VERIFY_PROFILE が挟まる）`);
    const pw = u.credentials.find((c) => c.type === 'password');
    assert.strictEqual(pw.value, `\${${envNameOf(name, 'PASSWORD')}}`, `${name} のパスワードがプレースホルダでない`);
    const otp = u.credentials.find((c) => c.type === 'otp');
    assert.ok(!('value' in otp), `${name}: OTP に value があると Keycloak はパスワードとして作る`);
    assert.deepStrictEqual(JSON.parse(otp.secretData), { value: `\${${envNameOf(name, 'TOTP_SECRET')}}` });
    const data = JSON.parse(otp.credentialData);
    assert.deepStrictEqual(data, { subType: 'totp', digits: REALM.otpPolicyDigits, counter: 0, period: REALM.otpPolicyPeriod, algorithm: REALM.otpPolicyAlgorithm },
      `${name}: OTP の形が realm の otpPolicy と違う`);
  }
  const owner = REALM.users.find((x) => x.username === 'e2e-trading-owner');
  const user = REALM.users.find((x) => x.username === 'e2e-user');
  assert.ok(owner.realmRoles.includes('trading-owner') && !(user.realmRoles || []).includes('trading-owner'));
});

ok('#2024: dry-run — env が揃えば、e2e の居ない既存 realm へ user.create がちょうど 6 件（資格情報は差し込み済み・ロールは postSteps）', () => {
  const env = e2eEnv();
  const { desired, resolved, skipped } = resolveE2eCredentials(REALM, env);
  assert.deepStrictEqual(resolved, E2E_USERS);
  assert.deepStrictEqual(skipped, []);
  const live = liveFrom(withoutE2e(REALM));
  const ops = plan(desired, live);
  assert.deepStrictEqual(ops.map(describe), E2E_USERS.map((n) => `${'user.create'.padEnd(24)} ${n} — seed 利用者が無い`));
  for (const op of ops) {
    assert.ok(!JSON.stringify(op.body).includes('${'), `${op.target}: プレースホルダが本文に残った`);
    const pw = op.body.credentials.find((c) => c.type === 'password');
    assert.strictEqual(pw.value, env[envNameOf(op.target, 'PASSWORD')]);
    assert.ok(passwordMeetsPolicy(REALM.passwordPolicy, pw.value), `${op.target}: ポリシー違反のパスワード`);
    const otp = op.body.credentials.find((c) => c.type === 'otp');
    assert.deepStrictEqual(JSON.parse(otp.secretData), { value: env[envNameOf(op.target, 'TOTP_SECRET')] });
    const realmStep = op.postSteps.find((st) => st.pathSuffix === '/role-mappings/realm');
    assert.deepStrictEqual(realmStep ? realmStep.body.map((r) => r.name).sort() : [], [...E2E_EXPECTED[op.target].roles].sort(), `${op.target} の realm ロール`);
    assert.ok(op.postSteps.some((st) => /role-mappings\/clients\//.test(st.pathSuffix)), `${op.target}: 自己管理のロールが無い`);
    assert.ok(!describe(op).includes(pw.value), '出力の行に値が出る');
  }
  // 作った後（e2e が居る realm）は何もしない（資格情報は実行時が所有する。比べない・戻さない）
  const after = liveFrom(REALM);
  assert.deepStrictEqual(plan(desired, after), []);
});

ok('#2024: env が無ければ 6 名とも外し（名前と欠けたキーだけを告げる）、既存 realm の計画は 0 件（drift にしない）', () => {
  const { desired, resolved, skipped } = resolveE2eCredentials(REALM, {});
  assert.deepStrictEqual(resolved, []);
  assert.deepStrictEqual(skipped.map((x) => x.username), E2E_USERS);
  assert.deepStrictEqual(skipped[0].problems, ['E2E_USER_PASSWORD: 無い', 'E2E_USER_TOTP_SECRET: 無い']);
  assert.strictEqual(describeSkipped(skipped[0]), 'skipped   user e2e-user — e2e-accounts から差し込めない: E2E_USER_PASSWORD: 無い, E2E_USER_TOTP_SECRET: 無い');
  assert.deepStrictEqual(desired, withoutE2e(REALM));
  assert.deepStrictEqual(plan(desired, liveFrom(withoutE2e(REALM))), []);
  assert.ok(REALM.users.some((u) => u.username === 'e2e-user'), '入力の宣言を書き換えた');
});

ok('#2024: 差し込めない値（ポリシー違反・短い/英数でない TOTP・制御文字・${）の利用者は作らず、理由だけを告げる（値は出さない）', () => {
  const base = e2eEnv();
  const cases = {
    E2E_ADMIN_PASSWORD: ['short1A', 'realm の passwordPolicy を満たさない'],
    E2E_OPERATOR_PASSWORD: ['alllowercaseletters', 'realm の passwordPolicy を満たさない'],
    E2E_USER_TOTP_SECRET: ['ABCDEFGHIJKLMNOPQRS', '英数 20〜64 文字でない'],
    E2E_NOACCESS_TOTP_SECRET: ['ABCDEFGHIJKLMNOPQRSTU+', '英数 20〜64 文字でない'],
    E2E_REVOCABLE_PASSWORD: ['Probe-2024-\nnewline', '制御文字を含む'],
    E2E_TRADING_OWNER_PASSWORD: ['Probe-2024-${X}', '${ を含む（Keycloak の取り込みが置き換える）'],
  };
  const env = { ...base };
  for (const [k, [v]] of Object.entries(cases)) env[k] = v;
  const { resolved, skipped } = resolveE2eCredentials(REALM, env);
  assert.deepStrictEqual(resolved, []);
  for (const [k, [v, why]] of Object.entries(cases)) {
    const hit = skipped.find((x) => x.problems.includes(`${k}: ${why}`));
    assert.ok(hit, `${k} を ${why} で外していない: ${JSON.stringify(skipped)}`);
    assert.ok(!describeSkipped(hit).includes(v), '値を出力の行に出した');
  }
  // 陽性対照: 1 つだけ壊せば、その利用者だけが外れる
  const one = resolveE2eCredentials(REALM, { ...base, E2E_ADMIN_PASSWORD: 'short1A' });
  assert.deepStrictEqual(one.skipped.map((x) => x.username), ['e2e-admin']);
  assert.strictEqual(one.resolved.length, 5);
});

ok('#2024: 置き換えるのは E2E_ で始まる名前だけ —— 他の名前のプレースホルダは env に在っても写さず、その利用者を作らない', () => {
  const desired = clone(REALM);
  const u = desired.users.find((x) => x.username === 'e2e-user');
  u.credentials[0].value = '${KC_ADMIN_PASSWORD}';
  const env = { ...e2eEnv(), KC_ADMIN_PASSWORD: 'Admin-Probe-2024x' };
  const { desired: out, skipped } = resolveE2eCredentials(desired, env);
  assert.ok(!JSON.stringify(out).includes('Admin-Probe-2024x'), '管理者のパスワードを利用者へ写した');
  assert.deepStrictEqual(skipped.map((x) => x.username), ['e2e-user']);
  assert.ok(skipped[0].problems.some((p) => /E2E_ で始まらないプレースホルダ/.test(p)), skipped[0].problems.join(' / '));
});

ok('#2024: プレースホルダが資格情報の value / secretData の外にあれば例外（宣言の誤り）', () => {
  const inAttr = clone(REALM);
  inAttr.users.find((x) => x.username === 'e2e-user').attributes.note = ['${E2E_USER_PASSWORD}'];
  assert.throws(() => resolveE2eCredentials(inAttr, e2eEnv()), /資格情報の外/);
  const inCredData = clone(REALM);
  inCredData.users.find((x) => x.username === 'e2e-user').credentials[1].credentialData = '{"x":"${E2E_USER_TOTP_SECRET}"}';
  assert.throws(() => resolveE2eCredentials(inCredData, e2eEnv()), /credentialData/);
  const inClient = clone(REALM);
  inClient.clients[0].description = '${E2E_X}';
  assert.throws(() => resolveE2eCredentials(inClient, e2eEnv()), /資格情報の外/);
});

ok('#2024: passwordMeetsPolicy は length と regexPattern（全体一致）だけを見る（陽性・陰性）', () => {
  const policy = REALM.passwordPolicy;
  assert.ok(passwordMeetsPolicy(policy, 'Developer-2026'), '宣言の dev のパスワードが通らない（判定が厳しすぎる）');
  assert.ok(!passwordMeetsPolicy(policy, 'Dev-2026'), '12 文字未満を通した');
  assert.ok(!passwordMeetsPolicy(policy, 'developerdeveloper'), '1 種類の文字だけを通した');
  assert.ok(passwordMeetsPolicy('passwordHistory(5)', 'x'), '見ない項で落とした');
});

ok('#2024: --render-import は取り込み元からプレースホルダを消す（揃えば差し込み・無ければ外す）。告知に値を出さない', () => {
  const text = JSON.stringify(REALM, null, 2);
  const msgs = [];
  const none = renderImport(text, {}, (m) => msgs.push(m));
  assert.ok(!none.includes('${E2E_'), 'Secret が無いのにプレースホルダが残った');
  assert.deepStrictEqual(JSON.parse(none), withoutE2e(REALM));
  assert.strictEqual(msgs.filter((m) => /取り込まない/.test(m)).length, 6);
  const env = e2eEnv();
  const out = [];
  const full = renderImport(text, env, (m) => out.push(m));
  assert.ok(!full.includes('${E2E_'));
  assert.strictEqual(JSON.parse(full).users.filter((u) => E2E_USERS.includes(u.username)).length, 6);
  for (const v of Object.values(env)) assert.ok(!out.join('\n').includes(v), '告知に値が出た');
  // 試験アカウント以外は触らない（宣言の他の部分とバイト等価）
  assert.deepStrictEqual(withoutE2e(JSON.parse(full)), withoutE2e(REALM));
});

ok('#2024: 生成器のプロパティは宣言のプレースホルダの小文字（12 個）で、値は差し込みの検査を通る（パスワードは乱数・TOTP は英数 32 文字）', () => {
  const kv = generateE2eAccounts(REALM);
  assert.deepStrictEqual(Object.keys(kv), placeholderNames(REALM).map(propertyName));
  assert.strictEqual(Object.keys(kv).length, 12);
  for (const [k, v] of Object.entries(kv)) {
    if (k.endsWith('-totp-secret')) assert.match(v, /^[A-Za-z0-9]{32}$/, k);
    else assert.ok(passwordMeetsPolicy(REALM.passwordPolicy, v), k);
  }
  assert.notDeepStrictEqual(generateE2eAccounts(REALM), kv, '2 回の生成で同じ値（乱数でない）');
  const { resolved } = resolveE2eCredentials(REALM, Object.fromEntries(placeholderNames(REALM).map((n) => [n, kv[propertyName(n)]])));
  assert.deepStrictEqual(resolved, E2E_USERS);
});

ok('#2024: 後追い Job は Secret e2e-accounts を envFrom（optional）で読む（無くても Job は起動する）', () => {
  const job = fs.readFileSync(path.join(REPO_ROOT, 'deploy', 'local', 'keycloak-setup', 'realm-reconcile-job.yaml'), 'utf8');
  assert.match(job, /\n {10}envFrom:\n {12}- secretRef:\n {16}name: e2e-accounts\n {16}optional: true\n/);
});

(async () => {
  {
    const live = await collectLive(fakeKc({ saEnabled: false, saUser: { id: 'u-bff' } }), REALM);
    assert.ok(!('bff' in live.serviceAccounts), '稼働 SA 無効なら serviceAccounts に bff を持たない');
    const ops = plan(REALM, live);
    assert.ok(ops.some((o) => o.op === 'client.update' && o.target === 'bff'), 'bff の client.update（SA 有効化）が要る');
    assert.ok(ops.some((o) => o.op === 'deferred' && /service-account-bff/.test(o.target)), 'bff の SA ロールは deferred');
    passed++;
    process.stdout.write('  ok  #1373: 稼働に在って SA 無効のクライアントでは collectLive が落ちず、plan が client.update ＋ deferred を出す\n');
  }
  {
    // 陰性対照: SA 有効なら従来どおり SA 利用者とロールを読む。
    const live = await collectLive(fakeKc({ saEnabled: true, saUser: { id: 'u-bff', username: 'service-account-bff' } }), REALM);
    assert.deepStrictEqual(live.serviceAccounts.bff.realmRoles, ['platform-service']);
    passed++;
    process.stdout.write('  ok  #1373 陰性対照: SA 有効なら service-account-user とロールを読む\n');
  }
  {
    // #1682 / IADR-0485: collectLive は client の secret を読みに行かない（比べないので要らない。秘密が Job を通らない）。
    const seen = [];
    await collectLive(fakeKc({ saEnabled: true, saUser: { id: 'u-bff', username: 'service-account-bff' }, seen }), REALM);
    assert.ok(seen.some((p) => p.endsWith('/clients?max=1000')), '陽性対照: client の一覧は読んでいる');
    assert.ok(!seen.some((p) => /\/client-secret$/.test(p)), `client-secret を読んだ: ${seen.filter((p) => /client-secret/.test(p)).join(', ')}`);
    passed++;
    process.stdout.write('  ok  #1682: collectLive は client の secret（GET …/client-secret）を読みに行かない\n');
  }
  // ---------------------------------------------------------------- #1830 / IADR-0517: dev の値の管理用クライアント
  const done1830 = (name) => { passed++; process.stdout.write(`  ok  ${name}\n`); };
  {
    // 宣言との結び目: 対象の 3 クライアントは宣言に在り、secret は判定器（scripts/lib/dev-client-secret-guard.sh）の dev の値と同じ形。
    const lib = fs.readFileSync(path.join(REPO_ROOT, 'scripts', 'lib', 'dev-client-secret-guard.sh'), 'utf8');
    const shellSet = (lib.match(/^DEV_CLIENT_SECRET_GUARDED="([^"]+)"$/m) || [])[1];
    assert.ok(shellSet, '判定器の DEV_CLIENT_SECRET_GUARDED が読めない');
    assert.deepStrictEqual(shellSet.split(/\s+/).sort(), [...DEV_SECRET_GUARDED_CLIENTS].sort(), 'shell と JS の対象集合が食い違う');
    for (const id of DEV_SECRET_GUARDED_CLIENTS) {
      const c = REALM.clients.find((x) => x.clientId === id);
      assert.ok(c, `宣言に ${id} が無い`);
      assert.strictEqual(c.secret, `${id}-dev-secret-change-me`, `${id} の宣言の secret が判定器の dev の値と違う形`);
    }
    assert.strictEqual(DEV_SECRETS_ALLOWED_ENV, 'DEV_CLIENT_SECRETS_ALLOWED');
    const job = fs.readFileSync(path.join(REPO_ROOT, 'deploy', 'local', 'keycloak-setup', 'realm-reconcile-job.yaml'), 'utf8');
    assert.ok(/- name: DEV_CLIENT_SECRETS_ALLOWED\n\s+value: "deny"\n/.test(job), 'Job マニフェストの既定が deny でない（直接 apply で安全側に倒れない）');
    done1830('#1830: 対象の 3 クライアントは shell の判定器と同じ集合で、宣言の secret は dev の値の形・Job の既定は deny');
  }
  {
    const live = liveFrom(REALM);
    assert.deepStrictEqual(devSecretCreations(plan(REALM, live)), [], '陰性対照: 揃っていれば作る操作は無い');
    for (const id of DEV_SECRET_GUARDED_CLIENTS) {
      const l = clone(live);
      l.clients = l.clients.filter((c) => c.clientId !== id);
      assert.deepStrictEqual(devSecretCreations(plan(REALM, l)), [id], `${id} が無いときに名指さない`);
    }
    const noBff = clone(live);
    noBff.clients = noBff.clients.filter((c) => c.clientId !== 'bff');
    assert.deepStrictEqual(devSecretCreations(plan(REALM, noBff)), [], '対象外（bff）の作成まで名指した');
    assert.deepStrictEqual(devSecretCreations(plan(REALM, { realm: null })), [...DEV_SECRET_GUARDED_CLIENTS].sort(),
      'realm が無い（realm.create）ときに 3 つを名指さない');
    done1830('#1830: devSecretCreations は対象のクライアントを作る操作（client.create・realm.create）だけを名指す');
  }
  {
    const live = { 'identity-admin': 'identity-admin-dev-secret-change-me', 'reset-gate': ['rotated', '1830'].join('-'), 'mcp-client-admin': null };
    assert.deepStrictEqual(devSecretFindings(REALM, live), [
      { clientId: 'identity-admin', state: 'dev' },
      { clientId: 'mcp-client-admin', state: 'absent' },
      { clientId: 'reset-gate', state: 'rotated' },
    ]);
    assert.ok(devSecretFindings(REALM, live).every((f) => !('value' in f) && !('secret' in f)), '判定の結果に値を持たせた');
    done1830('#1830: devSecretFindings は dev / rotated / absent を分け、値を結果に含めない');
  }

  // 偽の Keycloak（HTTP）で main まで通す。受けた要求を控え、書き込み（GET 以外）の有無と出力に値が出ないことを見る。
  const http = require('http');
  const os = require('os');
  const { spawn } = require('child_process');
  const ROTATED = ['rotated', 'value', '1830'].join('-');
  const runMain = async ({ realmExists = true, secrets = {}, env = {}, args = [] }) => {
    const seen = [];
    const bodies = []; // #2024: 書き込みの本文（main まで通したときに差し込みが効いているかを見る）
    const R = `/admin/realms/${encodeURIComponent(REALM.realm)}`;
    const server = http.createServer((req, res) => {
      let body = '';
      req.on('data', (d) => { body += d; });
      req.on('end', () => {
        seen.push(`${req.method} ${req.url}`);
        if (req.method !== 'GET') bodies.push({ line: `${req.method} ${req.url}`, body });
        const json = (code, v) => { res.writeHead(code, { 'Content-Type': 'application/json' }); res.end(v === undefined ? '' : JSON.stringify(v)); };
        if (req.url === '/realms/master/protocol/openid-connect/token') return json(200, { access_token: 't' });
        if (req.method !== 'GET') return json(201);
        if (req.url === R) return realmExists ? json(200, { realm: REALM.realm }) : json(404, { error: 'not found' });
        const m = req.url.match(/\/clients\?clientId=([^&]+)$/);
        if (m) {
          const id = decodeURIComponent(m[1]);
          return json(200, id in secrets && secrets[id] !== null ? [{ id: `c-${id}`, clientId: id }] : []);
        }
        const s = req.url.match(/\/clients\/c-([^/]+)\/client-secret$/);
        if (s) return json(200, { type: 'secret', value: secrets[s[1]] });
        return json(200, []);
      });
    });
    await new Promise((r) => server.listen(0, '127.0.0.1', r));
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reconcile-1830-'));
    fs.copyFileSync(path.join(REPO_ROOT, 'deploy', 'keycloak', 'microservices-platform-realm.json'), path.join(dir, 'realm.json'));
    try {
      const childEnv = { ...process.env };
      for (const k of ['RECONCILE_MODE', DEV_SECRETS_ALLOWED_ENV, 'HTTP_PROXY', 'HTTPS_PROXY', 'http_proxy', 'https_proxy', 'NODE_OPTIONS']) delete childEnv[k];
      const child = spawn(process.execPath, [path.join(REPO_ROOT, 'deploy', 'local', 'keycloak-setup', 'reconcile-realm.js'), ...args], {
        env: { ...childEnv, NO_PROXY: '*', KC_URL: `http://127.0.0.1:${server.address().port}`, KC_ADMIN_USER: 'admin',
          KC_ADMIN_PASSWORD: 'admin', REALM_DIR: dir, ...env },
      });
      let out = '';
      child.stdout.on('data', (d) => { out += d; });
      child.stderr.on('data', (d) => { out += d; });
      const code = await new Promise((r) => child.on('close', r));
      return { code, out, seen, bodies, writes: seen.filter((l) => !l.startsWith('GET ') && !l.includes('/openid-connect/token')) };
    } finally {
      server.close();
      fs.rmSync(dir, { recursive: true, force: true });
    }
  };
  const devOf = (id) => `${id}-dev-secret-change-me`;
  {
    const r = await runMain({ args: ['--check-dev-secrets'],
      secrets: { 'identity-admin': devOf('identity-admin'), 'reset-gate': ROTATED, 'mcp-client-admin': devOf('mcp-client-admin') } });
    assert.strictEqual(r.code, 1, r.out);
    assert.ok(/dev-secret\s+identity-admin/.test(r.out) && /dev-secret\s+mcp-client-admin/.test(r.out), `名指していない:\n${r.out}`);
    assert.ok(/ok\s+reset-gate/.test(r.out) && !/dev-secret\s+reset-gate/.test(r.out), `回した reset-gate を dev と言った:\n${r.out}`);
    assert.ok(/dev-secrets=2$/m.test(r.out), r.out);
    for (const v of [devOf('identity-admin'), devOf('mcp-client-admin'), ROTATED]) assert.ok(!r.out.includes(v), `値が出力に出た: ${v}`);
    assert.deepStrictEqual(r.writes, [], `読むだけのはずが書いた: ${r.writes.join(', ')}`);
    assert.ok(r.seen.some((l) => /\/clients\/c-identity-admin\/client-secret$/.test(l)), '陽性対照: 稼働の secret を読んでいる');
    done1830('#1830: --check-dev-secrets は dev の値のクライアントを名指して非 0（値を出さない・書かない）');
  }
  {
    const r = await runMain({ env: { RECONCILE_MODE: 'check-dev-secrets' },
      secrets: { 'identity-admin': ROTATED, 'reset-gate': ROTATED, 'mcp-client-admin': ROTATED } });
    assert.strictEqual(r.code, 0, r.out);
    assert.ok(/checked=3 dev-secrets=0$/m.test(r.out), r.out);
    assert.ok(!r.out.includes(ROTATED), '値が出力に出た');
    const absent = await runMain({ args: ['--check-dev-secrets'], secrets: { 'identity-admin': ROTATED, 'reset-gate': ROTATED, 'mcp-client-admin': null } });
    assert.strictEqual(absent.code, 0, absent.out);
    assert.ok(/absent\s+mcp-client-admin/.test(absent.out), absent.out);
    done1830('#1830 陰性対照: すべて回してあれば（RECONCILE_MODE でも引数でも）0。稼働に無いクライアントは不在と言うだけ');
  }
  {
    const r = await runMain({ args: ['--check-dev-secrets'], realmExists: false });
    assert.strictEqual(r.code, 1, `realm が無いのに 0 で終えた（測れないを緑にした）:\n${r.out}`);
    done1830('#1830: --check-dev-secrets は realm が無いと非 0（測れないを緑にしない）');
  }
  {
    // 後追い（apply）: realm が無い ＝ realm.create が 3 クライアントを宣言の dev の値で作る。
    const denied = await runMain({ realmExists: false });
    assert.strictEqual(denied.code, 1, denied.out);
    assert.deepStrictEqual(denied.writes, [], `拒否のはずが書いた: ${denied.writes.join(', ')}`);
    assert.ok(/identity-admin, mcp-client-admin, reset-gate/.test(denied.out), `名指していない:\n${denied.out}`);
    const deniedExplicit = await runMain({ realmExists: false, env: { [DEV_SECRETS_ALLOWED_ENV]: 'deny' } });
    assert.deepStrictEqual(deniedExplicit.writes, [], 'deny で書いた');
    const notOne = await runMain({ realmExists: false, env: { [DEV_SECRETS_ALLOWED_ENV]: '1' } });
    assert.deepStrictEqual(notOne.writes, [], 'allow 以外（1）で書いた（値域は allow だけ）');
    const allowed = await runMain({ realmExists: false, env: { [DEV_SECRETS_ALLOWED_ENV]: 'allow' } });
    assert.ok(allowed.writes.includes('POST /admin/realms'), `陽性対照: allow で realm を作っていない: ${allowed.writes.join(', ')}`);
    assert.ok(/WARN/.test(allowed.out), '許可して作るときに警告しない');
    done1830('#1830: 後追いは DEV_CLIENT_SECRETS_ALLOWED=allow でなければ、管理用クライアントを dev の値で作る realm に何も書かず非 0');
  }
  {
    // #2024 / IADR-0570: main まで通す。realm が無い（realm.create）とき、本文に `${E2E_` を残さない。
    const none = await runMain({ realmExists: false, env: { [DEV_SECRETS_ALLOWED_ENV]: 'allow' } });
    const create = none.bodies.find((b) => b.line === 'POST /admin/realms');
    assert.ok(create, `realm を作っていない: ${none.writes.join(', ')}`);
    assert.ok(!create.body.includes('${E2E_'), 'realm.create の本文にプレースホルダが残った（公知の文字列がパスワードになる）');
    assert.ok(!JSON.parse(create.body).users.some((u) => E2E_USERS.includes(u.username)), 'e2e-accounts が無いのに試験アカウントを作った');
    assert.strictEqual((none.out.match(/skipped {3}user e2e-/g) || []).length, 6, none.out);
    const env = e2eEnv();
    const full = await runMain({ realmExists: false, env: { [DEV_SECRETS_ALLOWED_ENV]: 'allow', ...env } });
    const body = JSON.parse(full.bodies.find((b) => b.line === 'POST /admin/realms').body);
    assert.deepStrictEqual(body.users.filter((u) => E2E_USERS.includes(u.username)).map((u) => u.username), E2E_USERS);
    for (const v of Object.values(env)) assert.ok(!full.out.includes(v), '値が出力に出た');
    passed++;
    process.stdout.write('  ok  #2024: main は宣言ごとに差し込みを当ててから計画する（無ければ外す・揃えば差し込む・値を出さない）\n');
  }
  console.log(`\n${passed} tests passed.`);
})().catch((e) => { console.error(e); process.exit(1); });
