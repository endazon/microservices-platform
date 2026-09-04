#!/usr/bin/env node
'use strict';
/*
 * FR-05, NFR-09, ADR-0004 / ADR-0026, IADR-0369 (#1088 / #324):
 * realm JSON（宣言）と稼働 realm（Keycloak Admin REST API）の**差分を計画し、当てる**。
 *
 *   Job（deploy/local/keycloak-setup/realm-reconcile-job.yaml）の中で node:22-alpine が実行する。
 *   ホスト側の入口は reconcile-realm.sh。**pod 内で kcadm.sh を exec しない**（別 JVM が Keycloak 本体を
 *   OOMKilled にする。2026-09-02 実測）。
 *
 * ## なぜ要るか —— `--import-realm` は同名 realm が在ると黙って飛ばす
 *
 * Keycloak は `start-dev --import-realm` で立つ。**永続化（PVC）した瞬間から、realm JSON を直しても
 * 稼働 realm は変わらない**（`IGNORE_EXISTING`。エラーも警告も出ない）。かといって `OVERWRITE_EXISTING` や
 * `kc.sh import --override` は realm を丸ごと作り直すので、永続化で守りたい runtime state（TOTP 資格情報・
 * 追加利用者・セッション）を毎回消す。**両立する形は「静的 import（空 PVC のときだけ）＋ 起動後に差分を当てる」**である。
 *
 * ## 境界 —— 宣言が所有する層と、実行時が所有する層（IADR-0369 決定 2）
 *
 * 宣言（realm JSON）が正: realm の非コレクション設定 / requiredActions / realm ロール・client ロール / グループ /
 *   client scopes ＋ protocol mappers / clients（属性・redirect・secret・scope 割当 ＋ mappers）/ **seed 利用者の存在** /
 *   サービスアカウント利用者のロールと属性。
 * 実行時が正（**触らない**）: 既存の人間の利用者の資格情報・属性・ロール・グループ・requiredActions・セッション /
 *   `smtpServer`（IADR-0261 決定 2: runbook が入れる秘匿値）。
 *
 * 集合欄（redirectUris / webOrigins / scope 割当 / enabledEventTypes …）は**宣言が全集合**（置換）。
 * 実体（client / role / group / mapper / user）は**加算的**（宣言に無い余剰は消さない）。
 *
 * ## 収束
 *
 * apply は「計画 → 適用 → 再計画」を最大 MAX_PASSES 周し、最後の計画が 0 件でなければ非 0 で終える
 * （当てたつもりで当たっていない状態を緑にしない）。check は計画だけを行い、1 件でも残れば非 0。
 * 標準出力の最終行は `realms=<n> drift=<m> applied=<k>` で、check-stack-ready.js の G9 がこれを読む。
 *
 * 環境変数（Job マニフェストが与える）:
 *   KC_URL              Keycloak（既定 http://keycloak:8080）
 *   KC_ADMIN_USER       master realm の管理者（Secret keycloak-admin/username）
 *   KC_ADMIN_PASSWORD   同パスワード（Secret keycloak-admin/password）。**値は出力しない**
 *   REALM_DIR           realm JSON の置き場（既定 /import ＝ ConfigMap keycloak-realms のマウント先）
 *   RECONCILE_MODE      apply（既定）| check
 */

const fs = require('fs');
const path = require('path');

const MAX_PASSES = 3;

// realm 表現のうち、トップレベルの PUT で扱わないキー。
//   コレクション（別の端点で個別に当てる）／同一性（id・realm 名）／export 由来のメタ／
//   実行時所有（smtpServer。IADR-0261 決定 2）。
const REALM_COLLECTION_KEYS = new Set([
  'users', 'clients', 'clientScopes', 'roles', 'groups', 'components', 'requiredActions',
  'authenticationFlows', 'authenticatorConfig', 'identityProviders', 'identityProviderMappers',
  'scopeMappings', 'clientScopeMappings', 'defaultDefaultClientScopes', 'defaultOptionalClientScopes',
  'protocolMappers', 'applications', 'oauthClients', 'federatedUsers', 'userFederationProviders',
  'userFederationMappers', 'clientPolicies', 'clientProfiles', 'organizations', 'defaultRole',
  'defaultRoles', 'defaultGroups', 'localizationTexts',
]);
const REALM_IDENTITY_KEYS = new Set(['id', 'realm', 'keycloakVersion']);
// 実行時が所有する realm のキー。**ここへ足すときは IADR-0369 の境界表も直す。**
const RUNTIME_OWNED_REALM_KEYS = new Set(['smtpServer']);

// client 表現のうち、トップレベルの PUT で扱わないキー（別端点で当てる／同一性／読み取り専用）。
const CLIENT_SKIP_KEYS = new Set([
  'id', 'protocolMappers', 'defaultClientScopes', 'optionalClientScopes', 'authorizationSettings',
  'access', 'registrationAccessToken', 'origin',
]);
const MAPPER_SKIP_KEYS = new Set(['id']);
// 利用者の作成時に POST /users が処理しないもの（作成後にロール割当の端点で当てる）。
const USER_CREATE_SKIP_KEYS = new Set(['realmRoles', 'clientRoles', 'serviceAccountClientId', 'id']);

// ---------------------------------------------------------------- 比較（純粋関数）

const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const asStr = (v) => (v === null || v === undefined ? '' : String(v));

/** 配列の要素がすべてスカラーか。 */
const scalarArray = (a) => Array.isArray(a) && a.every((x) => !isObj(x) && !Array.isArray(x));

/**
 * 宣言（desired）が live に**含まれている**か。宣言に無いキーは見ない（余剰は消さないため）。
 * スカラーは文字列として比べる（Keycloak は "1025" と 1025、true と "true" を往復で揺らす）。
 * スカラー配列は集合として比べる（順序は意味を持たない）。
 */
function contains(desired, live) {
  if (Array.isArray(desired)) {
    if (!Array.isArray(live)) return false;
    if (scalarArray(desired) && scalarArray(live)) {
      const a = [...desired].map(asStr).sort();
      const b = [...live].map(asStr).sort();
      return a.length === b.length && a.every((x, i) => x === b[i]);
    }
    return desired.length === live.length && desired.every((d, i) => contains(d, live[i]));
  }
  if (isObj(desired)) {
    if (!isObj(live)) return false;
    return Object.keys(desired).every((k) => contains(desired[k], live[k]));
  }
  return asStr(desired) === asStr(live);
}

/** live に desired を上書き合成する（オブジェクトは再帰、配列とスカラーは置換）。 */
function merge(live, desired) {
  if (isObj(live) && isObj(desired)) {
    const out = { ...live };
    for (const k of Object.keys(desired)) out[k] = merge(live[k], desired[k]);
    return out;
  }
  return desired === undefined ? live : desired;
}

const pick = (obj, keep) => Object.fromEntries(Object.entries(obj).filter(([k]) => keep(k)));
const byKey = (list, key) => new Map((list || []).map((x) => [x[key], x]));

// ---------------------------------------------------------------- 計画（純粋関数）

/**
 * 1 つの realm について、宣言 `desired` と稼働 `live` から適用すべき操作の列を計画する。
 *
 * live の形（collectLive が組む。試験は同じ形の fixture を渡す）:
 *   {
 *     realm: <RealmRepresentation>|null,
 *     requiredActions: [...], realmRoles: [...], clientScopes: [...], clients: [...],
 *     clientSecrets: { <clientId>: <secret> },       // confidential client のみ
 *     clientRoles:   { <clientId>: [...] },          // 宣言が触れる client のみ
 *     roleComposites:{ <roleName>: [...] },          // 宣言が composite を持つロールのみ
 *     groups: [ { ..., subGroups: [...] } ],
 *     users: { <username>: <UserRepresentation>|null },
 *     serviceAccounts: { <clientId>: { user, realmRoles: [names], clientRoles: { <clientId>: [names] } } },
 *   }
 *
 * 返す操作: { op, target, method, path, body, reason }。`op === 'deferred'` は前提（依存先の実体）が
 * まだ無く、次の周で計画し直すもの。check モードでは drift として数える。
 */
function plan(desired, live) {
  const ops = [];
  const realmName = desired.realm;
  const R = `/admin/realms/${encodeURIComponent(realmName)}`;
  const add = (o) => ops.push(o);
  const defer = (target, reason) => add({ op: 'deferred', target, reason });

  if (!live.realm) {
    // realm 自体が無い＝import が一度も走っていない。宣言を丸ごと投入する（import と同じ形）。
    add({ op: 'realm.create', target: realmName, method: 'POST', path: '/admin/realms', body: desired,
      reason: 'realm が稼働側に無い' });
    return ops;
  }

  // 1. realm の非コレクション設定（GET の全体へ宣言を合成して PUT。実行時所有キーは落とす）
  {
    const wanted = pick(desired, (k) =>
      !REALM_COLLECTION_KEYS.has(k) && !REALM_IDENTITY_KEYS.has(k) && !RUNTIME_OWNED_REALM_KEYS.has(k));
    const drifted = Object.keys(wanted).filter((k) => !contains(wanted[k], live.realm[k]));
    if (drifted.length > 0) {
      const body = pick(merge(live.realm, wanted), (k) => !RUNTIME_OWNED_REALM_KEYS.has(k) && !REALM_COLLECTION_KEYS.has(k));
      add({ op: 'realm.update', target: realmName, method: 'PUT', path: R, body,
        reason: `realm 設定の差分: ${drifted.join(', ')}` });
    }
  }

  // 2. requiredActions（alias で突合）
  {
    const liveRa = byKey(live.requiredActions, 'alias');
    for (const ra of desired.requiredActions || []) {
      const cur = liveRa.get(ra.alias);
      if (!cur) {
        add({ op: 'requiredAction.register', target: ra.alias, method: 'POST',
          path: `${R}/authentication/register-required-action`,
          body: { providerId: ra.providerId || ra.alias, name: ra.name || ra.alias },
          reason: 'requiredAction が未登録' });
        continue;
      }
      const wanted = pick(ra, (k) => ['name', 'enabled', 'defaultAction', 'priority', 'config'].includes(k));
      if (!contains(wanted, cur)) {
        add({ op: 'requiredAction.update', target: ra.alias, method: 'PUT',
          path: `${R}/authentication/required-actions/${encodeURIComponent(ra.alias)}`,
          body: merge(cur, wanted), reason: 'requiredAction の差分' });
      }
    }
  }

  // 3. realm ロール（name で突合）
  const liveRoles = byKey(live.realmRoles, 'name');
  for (const role of (desired.roles && desired.roles.realm) || []) {
    const cur = liveRoles.get(role.name);
    const wanted = pick(role, (k) => ['name', 'description', 'attributes'].includes(k));
    if (!cur) {
      add({ op: 'role.create', target: role.name, method: 'POST', path: `${R}/roles`, body: wanted,
        reason: 'realm ロールが無い' });
      continue;
    }
    if (!contains(wanted, cur)) {
      add({ op: 'role.update', target: role.name, method: 'PUT',
        path: `${R}/roles/${encodeURIComponent(role.name)}`, body: merge(cur, wanted), reason: 'realm ロールの差分' });
    }
    if (role.composite && role.composites && Array.isArray(role.composites.realm)) {
      const have = new Set(((live.roleComposites || {})[role.name] || []).map((r) => r.name));
      const missing = role.composites.realm.filter((n) => !have.has(n));
      const reps = missing.map((n) => liveRoles.get(n)).filter(Boolean).map((r) => ({ id: r.id, name: r.name }));
      if (reps.length !== missing.length) defer(`role ${role.name} composites`, '合成先のロールがまだ無い');
      else if (reps.length > 0) {
        add({ op: 'role.composites.add', target: role.name, method: 'POST',
          path: `${R}/roles/${encodeURIComponent(role.name)}/composites`, body: reps, reason: '合成ロールの欠落' });
      }
    }
  }

  // 4. client scopes（name で突合）＋ protocol mappers
  const liveScopes = byKey(live.clientScopes, 'name');
  for (const scope of desired.clientScopes || []) {
    const cur = liveScopes.get(scope.name);
    if (!cur) {
      add({ op: 'clientScope.create', target: scope.name, method: 'POST', path: `${R}/client-scopes`,
        body: pick(scope, (k) => k !== 'id'), reason: 'client scope が無い' });
      continue;
    }
    const wanted = pick(scope, (k) => !['id', 'protocolMappers'].includes(k));
    if (!contains(wanted, cur)) {
      add({ op: 'clientScope.update', target: scope.name, method: 'PUT',
        path: `${R}/client-scopes/${cur.id}`, body: pick(merge(cur, wanted), (k) => k !== 'protocolMappers'),
        reason: 'client scope の差分' });
    }
    planMappers(add, `${R}/client-scopes/${cur.id}`, `scope ${scope.name}`, scope.protocolMappers, cur.protocolMappers);
  }

  // 5. clients（clientId で突合）
  const liveClients = byKey(live.clients, 'clientId');
  for (const client of desired.clients || []) {
    const cur = liveClients.get(client.clientId);
    if (!cur) {
      add({ op: 'client.create', target: client.clientId, method: 'POST', path: `${R}/clients`,
        body: pick(client, (k) => k !== 'id'), reason: 'client が無い' });
      continue;
    }
    const wanted = pick(client, (k) => !CLIENT_SKIP_KEYS.has(k));
    const liveView = { ...cur };
    if (Object.prototype.hasOwnProperty.call(client, 'secret')) liveView.secret = (live.clientSecrets || {})[client.clientId];
    const drifted = Object.keys(wanted).filter((k) => !contains(wanted[k], liveView[k]));
    if (drifted.length > 0) {
      const body = pick(merge(cur, wanted), (k) => !['protocolMappers', 'defaultClientScopes', 'optionalClientScopes', 'access'].includes(k));
      add({ op: 'client.update', target: client.clientId, method: 'PUT', path: `${R}/clients/${cur.id}`, body,
        reason: `client の差分: ${drifted.map((k) => (k === 'secret' ? 'secret(値は出さない)' : k)).join(', ')}` });
    }
    // scope 割当（宣言が全集合）
    for (const [field, seg] of [['defaultClientScopes', 'default-client-scopes'], ['optionalClientScopes', 'optional-client-scopes']]) {
      if (!Array.isArray(client[field])) continue;
      const want = new Set(client[field]);
      const have = new Set(cur[field] || []);
      for (const name of want) {
        if (have.has(name)) continue;
        const sc = liveScopes.get(name);
        if (!sc) { defer(`client ${client.clientId} ${field} ${name}`, 'scope がまだ無い'); continue; }
        add({ op: 'client.scope.add', target: `${client.clientId}:${name}`, method: 'PUT',
          path: `${R}/clients/${cur.id}/${seg}/${sc.id}`, reason: `${field} の欠落` });
      }
      for (const name of have) {
        if (want.has(name)) continue;
        const sc = liveScopes.get(name);
        if (!sc) continue;
        add({ op: 'client.scope.remove', target: `${client.clientId}:${name}`, method: 'DELETE',
          path: `${R}/clients/${cur.id}/${seg}/${sc.id}`, reason: `${field} の余剰（宣言が全集合）` });
      }
    }
    planMappers(add, `${R}/clients/${cur.id}`, `client ${client.clientId}`, client.protocolMappers, cur.protocolMappers);
  }

  // 6. client ロール（宣言 roles.client[clientId][]）
  for (const [clientId, roles] of Object.entries((desired.roles && desired.roles.client) || {})) {
    const cur = liveClients.get(clientId);
    if (!cur) { defer(`client roles of ${clientId}`, 'client がまだ無い'); continue; }
    const have = byKey((live.clientRoles || {})[clientId], 'name');
    for (const role of roles) {
      const wanted = pick(role, (k) => ['name', 'description', 'attributes'].includes(k));
      const c = have.get(role.name);
      if (!c) {
        add({ op: 'clientRole.create', target: `${clientId}:${role.name}`, method: 'POST',
          path: `${R}/clients/${cur.id}/roles`, body: wanted, reason: 'client ロールが無い' });
      } else if (!contains(wanted, c)) {
        add({ op: 'clientRole.update', target: `${clientId}:${role.name}`, method: 'PUT',
          path: `${R}/clients/${cur.id}/roles/${encodeURIComponent(role.name)}`, body: merge(c, wanted),
          reason: 'client ロールの差分' });
      }
    }
  }

  // 7. グループ（name で突合。1 段の subGroups まで）
  const liveGroups = byKey(live.groups, 'name');
  for (const group of desired.groups || []) {
    const cur = liveGroups.get(group.name);
    const wanted = pick(group, (k) => ['name', 'attributes'].includes(k));
    if (!cur) {
      add({ op: 'group.create', target: group.name, method: 'POST', path: `${R}/groups`, body: wanted, reason: 'グループが無い' });
      if ((group.subGroups || []).length > 0) defer(`subGroups of ${group.name}`, '親グループがまだ無い');
      continue;
    }
    if (!contains(wanted, cur)) {
      add({ op: 'group.update', target: group.name, method: 'PUT', path: `${R}/groups/${cur.id}`,
        body: pick(merge(cur, wanted), (k) => k !== 'subGroups'), reason: 'グループの差分' });
    }
    const liveSub = byKey(cur.subGroups, 'name');
    for (const sub of group.subGroups || []) {
      const c = liveSub.get(sub.name);
      const w = pick(sub, (k) => ['name', 'attributes'].includes(k));
      if (!c) {
        add({ op: 'group.child.create', target: `${group.name}/${sub.name}`, method: 'POST',
          path: `${R}/groups/${cur.id}/children`, body: w, reason: 'サブグループが無い' });
      } else if (!contains(w, c)) {
        add({ op: 'group.update', target: `${group.name}/${sub.name}`, method: 'PUT', path: `${R}/groups/${c.id}`,
          body: pick(merge(c, w), (k) => k !== 'subGroups'), reason: 'サブグループの差分' });
      }
    }
  }

  // 8. 利用者
  const roleRef = (name) => { const r = liveRoles.get(name); return r ? { id: r.id, name: r.name } : null; };
  const clientRoleRefs = (clientId, names) => {
    const cur = liveClients.get(clientId);
    if (!cur) return null;
    const have = byKey((live.clientRoles || {})[clientId], 'name');
    const refs = names.map((n) => have.get(n)).filter(Boolean).map((r) => ({ id: r.id, name: r.name }));
    return refs.length === names.length ? { clientUuid: cur.id, refs } : null;
  };
  for (const user of desired.users || []) {
    if (user.serviceAccountClientId) {
      // サービスアカウントは人ではない。宣言側（ロール・属性）を当てる。
      const sa = (live.serviceAccounts || {})[user.serviceAccountClientId];
      if (!sa || !sa.user) { defer(`service account ${user.username}`, 'client（とそのサービスアカウント）がまだ無い'); continue; }
      const U = `${R}/users/${sa.user.id}`;
      const missingRealm = (user.realmRoles || []).filter((n) => !(sa.realmRoles || []).includes(n));
      if (missingRealm.length > 0) {
        const refs = missingRealm.map(roleRef);
        if (refs.some((r) => !r)) defer(`service account ${user.username} realmRoles`, 'ロールがまだ無い');
        else add({ op: 'user.realmRoles.add', target: user.username, method: 'POST', path: `${U}/role-mappings/realm`, body: refs,
          reason: `realm ロールの欠落: ${missingRealm.join(', ')}` });
      }
      for (const [clientId, names] of Object.entries(user.clientRoles || {})) {
        const missing = names.filter((n) => !((sa.clientRoles || {})[clientId] || []).includes(n));
        if (missing.length === 0) continue;
        const cr = clientRoleRefs(clientId, missing);
        if (!cr) { defer(`service account ${user.username} clientRoles ${clientId}`, 'client かロールがまだ無い'); continue; }
        add({ op: 'user.clientRoles.add', target: `${user.username}:${clientId}`, method: 'POST',
          path: `${U}/role-mappings/clients/${cr.clientUuid}`, body: cr.refs, reason: `client ロールの欠落: ${missing.join(', ')}` });
      }
      if (user.attributes && !contains(user.attributes, sa.user.attributes || {})) {
        add({ op: 'user.attributes.update', target: user.username, method: 'PUT', path: U,
          body: merge(sa.user, { attributes: user.attributes }), reason: 'サービスアカウントの属性の差分' });
      }
      continue;
    }
    const cur = (live.users || {})[user.username];
    if (cur) continue; // 既存の人間の利用者は実行時が所有する（境界）。触らない。
    // 作成。POST /users は realmRoles / clientRoles を処理しないので、作成直後の割当を postSteps で運ぶ。
    const postSteps = [];
    let blocked = null;
    if ((user.realmRoles || []).length > 0) {
      const refs = user.realmRoles.map(roleRef);
      if (refs.some((r) => !r)) blocked = 'realm ロールがまだ無い';
      else postSteps.push({ method: 'POST', pathSuffix: '/role-mappings/realm', body: refs });
    }
    for (const [clientId, names] of Object.entries(user.clientRoles || {})) {
      const cr = clientRoleRefs(clientId, names);
      if (!cr) { blocked = `client ロール（${clientId}）がまだ無い`; break; }
      postSteps.push({ method: 'POST', pathSuffix: `/role-mappings/clients/${cr.clientUuid}`, body: cr.refs });
    }
    if (blocked) { defer(`user ${user.username}`, blocked); continue; }
    add({ op: 'user.create', target: user.username, method: 'POST', path: `${R}/users`,
      body: pick(user, (k) => !USER_CREATE_SKIP_KEYS.has(k)), postSteps, reason: 'seed 利用者が無い' });
  }

  return ops;
}

/** protocol mappers を name で突合する（client scope と client で同型）。 */
function planMappers(add, base, label, desiredMappers, liveMappers) {
  const have = byKey(liveMappers, 'name');
  for (const m of desiredMappers || []) {
    const cur = have.get(m.name);
    const wanted = pick(m, (k) => !MAPPER_SKIP_KEYS.has(k));
    if (!cur) {
      add({ op: 'mapper.create', target: `${label} mapper ${m.name}`, method: 'POST',
        path: `${base}/protocol-mappers/models`, body: wanted, reason: 'protocol mapper が無い' });
    } else if (!contains(wanted, cur)) {
      add({ op: 'mapper.update', target: `${label} mapper ${m.name}`, method: 'PUT',
        path: `${base}/protocol-mappers/models/${cur.id}`, body: merge(cur, wanted), reason: 'protocol mapper の差分' });
    }
  }
}

/** 標準出力へ出す 1 行（秘匿値を含めない: body は出さない）。 */
function describe(op) {
  return op.op === 'deferred' ? `deferred  ${op.target} — ${op.reason}` : `${op.op.padEnd(24)} ${op.target} — ${op.reason}`;
}

// ---------------------------------------------------------------- 収集・適用（外部依存）

function makeClient(baseUrl, token) {
  const call = async (method, p, body) => {
    const res = await fetch(baseUrl + p, {
      method,
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', Accept: 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
    return { status: res.status, ok: res.ok, json, text };
  };
  const get = async (p) => {
    const r = await call('GET', p);
    if (r.status === 404) return null;
    if (!r.ok) throw new Error(`GET ${p} -> ${r.status} ${r.text.slice(0, 200)}`);
    return r.json;
  };
  return { call, get };
}

async function adminToken(baseUrl, user, password) {
  const body = new URLSearchParams({ grant_type: 'password', client_id: 'admin-cli', username: user, password });
  const res = await fetch(`${baseUrl}/realms/master/protocol/openid-connect/token`, {
    method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body,
  });
  if (!res.ok) throw new Error(`管理者トークンを取得できない: HTTP ${res.status}（資格情報の値は出さない）`);
  return (await res.json()).access_token;
}

/** 稼働側を、plan が読む形へ集める。宣言が触れるものだけを取りに行く。 */
async function collectLive(kc, desired) {
  const name = desired.realm;
  const R = `/admin/realms/${encodeURIComponent(name)}`;
  const realm = await kc.get(R);
  if (!realm) return { realm: null };
  const [requiredActions, realmRoles, clientScopes, clients] = await Promise.all([
    kc.get(`${R}/authentication/required-actions`),
    kc.get(`${R}/roles?max=1000&briefRepresentation=false`),
    kc.get(`${R}/client-scopes`),
    kc.get(`${R}/clients?max=1000`),
  ]);
  const clientByClientId = byKey(clients, 'clientId');

  const clientSecrets = {};
  for (const c of desired.clients || []) {
    const cur = clientByClientId.get(c.clientId);
    if (!cur || !Object.prototype.hasOwnProperty.call(c, 'secret')) continue;
    const s = await kc.get(`${R}/clients/${cur.id}/client-secret`);
    clientSecrets[c.clientId] = s ? s.value : undefined;
  }

  // 宣言が触れる client のロール（roles.client の宣言 ＋ 利用者の clientRoles が指す client）
  const roleClientIds = new Set(Object.keys((desired.roles && desired.roles.client) || {}));
  for (const u of desired.users || []) for (const cid of Object.keys(u.clientRoles || {})) roleClientIds.add(cid);
  const clientRoles = {};
  for (const cid of roleClientIds) {
    const cur = clientByClientId.get(cid);
    if (cur) clientRoles[cid] = (await kc.get(`${R}/clients/${cur.id}/roles?max=1000`)) || [];
  }

  const roleComposites = {};
  for (const role of (desired.roles && desired.roles.realm) || []) {
    if (role.composite && role.composites) {
      roleComposites[role.name] = (await kc.get(`${R}/roles/${encodeURIComponent(role.name)}/composites`)) || [];
    }
  }

  const groups = (await kc.get(`${R}/groups?max=1000&briefRepresentation=false`)) || [];
  for (const g of groups) {
    g.subGroups = (await kc.get(`${R}/groups/${g.id}/children?max=1000&briefRepresentation=false`)) || g.subGroups || [];
  }

  const users = {};
  const serviceAccounts = {};
  for (const u of desired.users || []) {
    if (u.serviceAccountClientId) {
      const cur = clientByClientId.get(u.serviceAccountClientId);
      if (!cur) continue;
      const user = await kc.get(`${R}/clients/${cur.id}/service-account-user`);
      if (!user) continue;
      const realmMappings = (await kc.get(`${R}/users/${user.id}/role-mappings/realm`)) || [];
      const cr = {};
      for (const cid of Object.keys(u.clientRoles || {})) {
        const cc = clientByClientId.get(cid);
        if (cc) cr[cid] = ((await kc.get(`${R}/users/${user.id}/role-mappings/clients/${cc.id}`)) || []).map((r) => r.name);
      }
      serviceAccounts[u.serviceAccountClientId] = { user, realmRoles: realmMappings.map((r) => r.name), clientRoles: cr };
    } else {
      const found = (await kc.get(`${R}/users?username=${encodeURIComponent(u.username)}&exact=true`)) || [];
      users[u.username] = found[0] || null;
    }
  }

  return { realm, requiredActions, realmRoles, clientScopes, clients, clientSecrets, clientRoles, roleComposites, groups, users, serviceAccounts };
}

async function applyOps(kc, realmName, ops) {
  const R = `/admin/realms/${encodeURIComponent(realmName)}`;
  let applied = 0;
  const errors = [];
  for (const op of ops) {
    if (op.op === 'deferred') continue;
    const r = await kc.call(op.method, op.path, op.body);
    if (!r.ok && r.status !== 409) {
      errors.push(`${op.op} ${op.target}: HTTP ${r.status} ${r.text.slice(0, 300)}`);
      continue;
    }
    applied += 1;
    if (op.op === 'user.create' && (op.postSteps || []).length > 0) {
      const found = (await kc.get(`${R}/users?username=${encodeURIComponent(op.target)}&exact=true`)) || [];
      const id = found[0] && found[0].id;
      if (!id) { errors.push(`user.create ${op.target}: 作成後に利用者を引けない`); continue; }
      for (const step of op.postSteps) {
        const s = await kc.call(step.method, `${R}/users/${id}${step.pathSuffix}`, step.body);
        if (!s.ok) errors.push(`user.create ${op.target} ${step.pathSuffix}: HTTP ${s.status} ${s.text.slice(0, 200)}`);
      }
    }
    console.log(`    applied   ${describe(op)}`);
  }
  return { applied, errors };
}

function readRealmFiles(dir) {
  const files = fs.readdirSync(dir).filter((f) => f.endsWith('.json')).sort();
  return files.map((f) => ({ file: f, desired: JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')) }));
}

async function main() {
  const baseUrl = (process.env.KC_URL || 'http://keycloak:8080').replace(/\/+$/, '');
  const mode = process.env.RECONCILE_MODE || 'apply';
  const dir = process.env.REALM_DIR || '/import';
  if (!['apply', 'check'].includes(mode)) throw new Error(`RECONCILE_MODE は apply | check（受け取った値: ${mode}）`);
  const user = process.env.KC_ADMIN_USER;
  const password = process.env.KC_ADMIN_PASSWORD;
  if (!user || !password) throw new Error('KC_ADMIN_USER / KC_ADMIN_PASSWORD が無い（Secret keycloak-admin の username / password）');

  const realms = readRealmFiles(dir);
  if (realms.length === 0) {
    console.log(`realms=0 drift=0 applied=0`);
    throw new Error(`${dir} に realm JSON が 1 つも無い（走査が壊れている。0 件を緑にしない）`);
  }
  const kc = makeClient(baseUrl, await adminToken(baseUrl, user, password));

  let drift = 0;
  let applied = 0;
  for (const { file, desired } of realms) {
    console.log(`==> realm '${desired.realm}' (${file}) mode=${mode}`);
    let ops = plan(desired, await collectLive(kc, desired));
    if (mode === 'apply') {
      for (let pass = 1; pass <= MAX_PASSES && ops.length > 0; pass += 1) {
        console.log(`    pass ${pass}: ${ops.length} 件`);
        for (const op of ops) if (op.op === 'deferred') console.log(`    ${describe(op)}`);
        const r = await applyOps(kc, desired.realm, ops);
        applied += r.applied;
        for (const e of r.errors) console.error(`    ERROR ${e}`);
        ops = plan(desired, await collectLive(kc, desired));
      }
    }
    for (const op of ops) console.log(`    drift     ${describe(op)}`);
    if (ops.length === 0) console.log('    一致（差分なし）');
    drift += ops.length;
  }
  console.log(`realms=${realms.length} drift=${drift} applied=${applied}`);
  if (drift > 0) process.exitCode = 1;
}

if (require.main === module) {
  main().catch((e) => {
    console.error(`ERROR: ${e.message}`);
    process.exitCode = 1;
  });
}

module.exports = {
  plan, planMappers, contains, merge, describe,
  REALM_COLLECTION_KEYS, RUNTIME_OWNED_REALM_KEYS, CLIENT_SKIP_KEYS, MAX_PASSES,
};
