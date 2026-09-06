#!/usr/bin/env node
'use strict';
/*
 * reset-gate.test.js
 * SC-15, FR-05, NFR-09, ADR-0026 / ADR-0078 決定 4, IADR-0404 (#1245 PR-C):
 * **門（deploy/mail-relay/reset-gate.js）と、後追い Job（deploy/local/keycloak-setup/reconcile-realm.js）が
 * 競合しないこと**を、両モジュールを同時に読み込んで固定する。
 *
 * 🔴 **これが本 PR の核心である。** 門が閉じたことと Job が開き直すことが競合すると、
 *    **両方が「直した」と記録して静かに壊れる**（門は「閉じた」と記録し、Job は「drift を直した」と記録する）。
 *    IADR-0404 の理由節がこの形を名指ししている。単体の自己試験は片側しか見ないので、
 *    **接点（realm 属性の綴りと、閉じた realm の表現そのもの）を突き合わせる試験をここに置く。**
 *
 * 固定するもの:
 *   1. 属性の綴りが両モジュールで**同一**である（1 文字ズレると Job が開き直す）。
 *   2. 門が組み立てた「閉じた realm」を Job に見せると **drift 0 件**（開き直さない）。
 *   3. 門が組み立てた「開け直した realm」を Job に見せても **drift 0 件**（往復して安定する）。
 *   4. 🔴 門の標識を伴わない `false`（人が手で閉じた）は Job が**宣言へ戻す**＝門が
 *      それを reopen しないことと**対**になっている（二重に閉じ込めない）。
 *   5. realm 宣言（実データ）が門の前提を満たす: `reset-gate` クライアントと**その SA 利用者**が居て、
 *      `manage-realm` を持つのは 1 つだけである（#1301 の「client はあるが users[] に無い」を止める）。
 *
 * 外部依存ゼロ（Node 標準 assert のみ）。実行: node scripts/reset-gate.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');

const gate = require('../deploy/mail-relay/reset-gate.js');
const {
  plan, gateHoldsClosed, GATE_OWNED_REALM_KEYS,
  GATE_STATE_ATTRIBUTE: JOB_GATE_STATE_ATTRIBUTE, GATE_STATE_CLOSED: JOB_GATE_STATE_CLOSED,
} = require('../deploy/local/keycloak-setup/reconcile-realm.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const REALM = JSON.parse(fs.readFileSync(
  path.join(REPO_ROOT, 'deploy', 'keycloak', 'microservices-platform-realm.json'), 'utf8'));
const GATE_MANIFEST = path.join(REPO_ROOT, 'deploy', 'mail-relay', 'reset-gate.yaml');

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}
const clone = (v) => JSON.parse(JSON.stringify(v));

// --- 1. 接点（属性の綴り）--------------------------------------------------------------

ok('🔴 門が書く属性と Job が読む属性の綴りが同一である（違うと Job が開き直す）', () => {
  assert.strictEqual(gate.GATE_STATE_ATTRIBUTE, JOB_GATE_STATE_ATTRIBUTE,
    '属性名がズレている。門は閉じたつもりで、Job は「人が閉じた」と見なして開き直す');
  assert.strictEqual(gate.GATE_STATE_CLOSED, JOB_GATE_STATE_CLOSED, '閉じている印の値がズレている');
  assert.ok(GATE_OWNED_REALM_KEYS.has('resetPasswordAllowed'),
    'Job 側が resetPasswordAllowed を門所有として扱っていない');
});

// --- 2〜4. 往復（門が作った realm を Job に見せる）--------------------------------------
//
// Job の `plan()` が要る live の形は keycloak-realm-reconcile.test.js の liveFrom と同じである。
// ここでは **realm 部分だけを門の出力で置き換え**、他は宣言から素直に合成する（差分を realm 設定に絞る）。

let seq = 0;
const uuid = () => `id-${++seq}`;

function liveFrom(desired, realmOverride) {
  const realm = realmOverride || clone(desired);
  for (const k of ['users', 'clients', 'clientScopes', 'roles', 'groups', 'components', 'requiredActions']) {
    delete realm[k];
  }
  if (!realm.id) realm.id = uuid();
  const realmRoles = desired.roles.realm.map((r) => ({ ...clone(r), id: uuid(), composite: false }));
  const clientScopes = desired.clientScopes.map((s) => ({
    ...clone(s), id: uuid(), protocolMappers: (s.protocolMappers || []).map((m) => ({ ...clone(m), id: uuid() })),
  }));
  const clients = desired.clients.map((c) => {
    const l = { ...clone(c), id: uuid(), protocolMappers: (c.protocolMappers || []).map((m) => ({ ...clone(m), id: uuid() })) };
    delete l.secret;
    return l;
  });
  const clientSecrets = Object.fromEntries(desired.clients.filter((c) => c.secret).map((c) => [c.clientId, c.secret]));
  const clientRoles = {};
  for (const [cid, roles] of Object.entries(desired.roles.client || {})) {
    clientRoles[cid] = roles.map((r) => ({ ...clone(r), id: uuid() }));
  }
  clients.push({ id: uuid(), clientId: 'realm-management' });
  clientRoles['realm-management'] = ['view-users', 'manage-users', 'view-realm', 'manage-realm', 'query-users']
    .map((n) => ({ id: uuid(), name: n }));
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
    realm, requiredActions: clone(desired.requiredActions), realmRoles, clientScopes, clients, clientSecrets,
    clientRoles, roleComposites: {}, groups, users, serviceAccounts,
  };
}

/** 稼働 realm（Keycloak の GET 応答に相当）を宣言から合成する。 */
const liveRealmRep = () => {
  const r = clone(REALM);
  r.id = uuid();
  return r;
};

ok('陽性対照: 宣言と一致した稼働は drift 0 件（この節の前提）', () => {
  assert.deepStrictEqual(plan(REALM, liveFrom(REALM, liveRealmRep())), []);
});

ok('🔴 門が閉じた realm を Job に見せても drift 0 件 —— **開き直さない**', () => {
  const closed = gate.buildRealmUpdate(liveRealmRep(), {
    allowed: false, reason: 'probe failed: ECONNREFUSED', at: '2026-09-07T00:00:00.000Z',
  });
  assert.strictEqual(closed.resetPasswordAllowed, false, '門が閉じていない（前提が崩れた）');
  assert.ok(gateHoldsClosed('resetPasswordAllowed', REALM.resetPasswordAllowed, closed),
    'Job 側の判定関数が「門が閉じた」と認めていない');
  const ops = plan(REALM, liveFrom(REALM, closed));
  assert.deepStrictEqual(ops, [], `門が閉じた realm を Job が触っている: ${JSON.stringify(ops.map((o) => o.op))}`);
});

ok('門が開け直した realm を Job に見せても drift 0 件（往復して安定する）', () => {
  const closed = gate.buildRealmUpdate(liveRealmRep(), { allowed: false, reason: 'r', at: 'T1' });
  const reopened = gate.buildRealmUpdate(closed, { allowed: true, reason: 'recovered', at: 'T2' });
  assert.strictEqual(reopened.resetPasswordAllowed, true);
  assert.strictEqual(reopened.attributes[gate.GATE_STATE_ATTRIBUTE], gate.GATE_STATE_OPEN);
  assert.deepStrictEqual(plan(REALM, liveFrom(REALM, reopened)), []);
});

ok('🔴 人が手で閉じた（門の標識が無い）realm は Job が宣言へ戻す —— 門も開けないので二重に閉じ込めない', () => {
  const byHand = clone(liveRealmRep());
  byHand.resetPasswordAllowed = false; // 属性を付けない＝門ではない
  const ops = plan(REALM, liveFrom(REALM, byHand));
  assert.strictEqual(ops.length, 1, 'Job が人手の閉鎖を drift として拾っていない');
  assert.strictEqual(ops[0].body.resetPasswordAllowed, true, 'Job が宣言へ戻していない');
  // 対（門の側）: 標識が無いので門は reopen しない。**Job だけが戻す**＝役割が重ならない。
  assert.strictEqual(gate.decide({
    probeOk: true, probeReason: '', declaredAllowed: true, liveAllowed: false,
    gateState: '', consecutiveSuccesses: 99, reopenAfter: 3,
  }).action, 'none');
});

ok('🔴 逆向き（宣言 false・稼働 true）は門の標識があっても Job が drift として拾う（危険な向き）', () => {
  const desired = { ...clone(REALM), resetPasswordAllowed: false };
  const live = liveRealmRep();
  live.attributes = { [gate.GATE_STATE_ATTRIBUTE]: gate.GATE_STATE_CLOSED };
  const ops = plan(desired, liveFrom(desired, live));
  assert.ok(ops.some((o) => o.op === 'realm.update' && o.body.resetPasswordAllowed === false),
    '閉じたはずの申請が開いている状態を見逃している');
});

// --- 5. realm 宣言の前提（実データ・ラチェット）------------------------------------------

ok('🔴 門のクライアントと**その SA 利用者**が両方居る（#1301: client はあるが users[] に無い形を止める）', () => {
  const client = REALM.clients.find((c) => c.clientId === 'reset-gate');
  assert.ok(client, 'reset-gate クライアントが realm 宣言に無い');
  assert.strictEqual(client.serviceAccountsEnabled, true);
  assert.strictEqual(client.publicClient, false, '公開クライアントでは client_credentials を打てない');
  assert.strictEqual(client.standardFlowEnabled, false, 'MFA を迂回できる経路が開いている');
  assert.strictEqual(client.directAccessGrantsEnabled, false, 'MFA を迂回できる経路が開いている');
  assert.ok((client.defaultClientScopes || []).includes('realm-management-roles'),
    'realm-management ロールをトークンへ載せるスコープが無い（Admin API が 403 になる。IADR-0329 決定 2）');

  const sa = REALM.users.filter((u) => u.serviceAccountClientId === 'reset-gate');
  assert.strictEqual(sa.length, 1, 'SA 利用者が users[] にちょうど 1 つ居ない（ロールが誰にも付かない）');
  assert.deepStrictEqual((sa[0].clientRoles || {})['realm-management'].slice().sort(),
    ['manage-realm', 'view-realm'], '門の権限が view-realm ＋ manage-realm ちょうどでない');
});

ok('🔴 manage-realm を持つサービスアカウントは 1 つだけである（IADR-0329 からの後退を最小に保つ）', () => {
  const holders = REALM.users
    .filter((u) => u.serviceAccountClientId)
    .filter((u) => ((u.clientRoles || {})['realm-management'] || []).includes('manage-realm'))
    .map((u) => u.serviceAccountClientId);
  assert.deepStrictEqual(holders, ['reset-gate'], `manage-realm の保持者が増えている: ${holders.join(', ')}`);
});

ok('🔴 realm 宣言はトップレベルの attributes を持たない（門の状態を Job が宣言所有にしない）', () => {
  assert.ok(!('attributes' in REALM),
    'realm 宣言に attributes が入った。門が書く reset-gate.state を Job が open へ戻す');
});

// --- 6. マニフェストと実装の対応（値をここへ書き写さない）--------------------------------

ok('門のマニフェストが**数値の既定をすべて与えている**（実装は既定を持たない）', () => {
  const yaml = fs.readFileSync(GATE_MANIFEST, 'utf8');
  for (const name of ['PROBE_INTERVAL_SECONDS', 'REOPEN_AFTER_SUCCESSES', 'PROBE_TIMEOUT_MS',
    'RELAY_HOST', 'RELAY_PORT', 'PROBE_MAIL_FROM', 'PROBE_RCPT_TO', 'KC_URL', 'KC_REALM',
    'GATE_CLIENT_ID', 'GATE_CLIENT_SECRET', 'REALM_DIR']) {
    assert.ok(new RegExp(`name:\\s*${name}\\b`).test(yaml), `${name} をマニフェストが与えていない`);
  }
  const source = fs.readFileSync(path.join(REPO_ROOT, 'deploy', 'mail-relay', 'reset-gate.js'), 'utf8');
  assert.ok(!/process\.env\.[A-Z_]+\s*\|\|\s*['"0-9]/.test(source),
    '実装が環境変数へ既定値を置いている（ADR-0078 決定 1: 数字は実測で決める）');
});

ok('門の差出人が realm の smtpServer.from と一致する（差出人の門で拒まれない）', () => {
  const yaml = fs.readFileSync(GATE_MANIFEST, 'utf8');
  const m = /name:\s*PROBE_MAIL_FROM\s*\n\s*value:\s*(\S+)/.exec(yaml);
  assert.ok(m, 'PROBE_MAIL_FROM をマニフェストから読めない');
  assert.strictEqual(m[1].replace(/^["']|["']$/g, ''), REALM.smtpServer.from,
    'プローブの差出人が realm の from と違う（近接 MTA の check_sender_access に拒まれ、常時 closed になる）');
});

ok('🔴 プローブは DATA を送らない（上流＝捕捉箱へプローブのメールを流し込まない）', () => {
  const source = fs.readFileSync(path.join(REPO_ROOT, 'deploy', 'mail-relay', 'reset-gate.js'), 'utf8');
  assert.ok(!/'DATA'|"DATA"|send:\s*`DATA/.test(source),
    'DATA を送っている。近接 MTA は宛先によらず上流へ中継するので、'
    + 'check-password-reset-mail.js の「ちょうど 1 通」（T-17）が壊れる');
  assert.ok(/RSET/.test(source) && /QUIT/.test(source), '取引を明示的に捨てて閉じていない');
});

console.log(`\n${passed} tests passed.`);
