#!/usr/bin/env node
'use strict';
/*
 * measure-abac-combinations.js
 *
 * FR-17 / FR-18, issue #456: 実在する ABAC 属性の組み合わせ数を実測する。
 *
 * 背景:
 *   計画 06_technical/14_knowledge-graph-graphrag.md §6「コミュニティ要約の粒度と費用の決定手順」は
 *   手順 1 として「実在する ABAC 属性の組み合わせ数（機密区分 × 部門 × lifecycle × その他）を実測する」
 *   ことを定め、手順 3 で粒度を落とす順序を
 *     属性組み合わせ単位 → ロール単位 → 機密区分単位（public/internal/confidential/restricted の 4 通り）
 *   と定めている。ADR-0035（Proposed）は planning#187 の裁定（案 B）により実測を待たずに
 *   「機密区分単位から始める」と決定済みだが、同裁定は
 *     「旧データ破棄（#457）の前に、属性組み合わせ数を機械的に数えられるスクリプトを用意する」
 *   を宿題として残した。本スクリプトがその手段であり、上記 3 粒度をまとめて数える。
 *
 * 性質:
 *   - **読み取り専用**。SELECT と Keycloak Admin API の GET しか行わない。
 *   - 乱数・現在時刻に依存しない（同一入力なら同一出力＝再現可能）。
 *   - 外部依存ゼロ（Node 標準ライブラリのみ）。集計は純関数で、scripts.repo.test.js が単体試験する。
 *
 * 実行方法:
 *   1) 経路B（ローカル k8s）が稼働している場合＝既定。kubectl exec 経由で収集する:
 *        node scripts/measure-abac-combinations.js
 *   2) 別環境の Postgres / Keycloak へ直接向ける場合:
 *        ABAC_DOC_DSN=postgres://... ABAC_AUTHZ_DSN=postgres://... \
 *        ABAC_KC_URL=https://keycloak.example/ node scripts/measure-abac-combinations.js
 *      （DSN 指定時はホストに psql が要る。Keycloak は Admin REST を直接叩く）
 *   3) 収集済みの JSON から集計だけやり直す場合（実データが消えた後の再集計・レビュー時の追試）:
 *        node scripts/measure-abac-combinations.js --input measured.json
 *
 * 主な環境変数（既定は経路B の値）:
 *   ABAC_NS=platform-infra / ABAC_PG_LABEL=app=postgres / ABAC_KC_LABEL=app=keycloak
 *   ABAC_PG_USER=kp / ABAC_DOC_DB=document_svc / ABAC_AUTHZ_DB=authz_svc
 *   ABAC_REALM=microservices-platform / ABAC_KC_ADMIN_USER=admin / ABAC_KC_ADMIN_PASSWORD=admin
 *
 * 出力: 既定は人が読める要約。--json で機械可読、--dump <path> で収集した生データを保存する
 *       （保存した生データは --input で再集計できる＝測定機会を失った後も追試できる）。
 */

const { spawnSync } = require('child_process');
const fs = require('fs');

// ---------------------------------------------------------------------------
// 計画側の定義（06_technical/07_abac-attribute-model.md）
// ---------------------------------------------------------------------------

// 文書の基本属性。計画の表の順序を保つ（出力の並びを安定させるため）。
const PLANNED_DOCUMENT_ATTRIBUTES = [
  'confidentiality',
  'department',
  'owner',
  'shared_with',
  'project',
  'lifecycle',
  'data_class',
  'region',
];
// 同表で「必須」とされているもの。実データに無ければ計画と実装の乖離として報告する。
const PLANNED_REQUIRED_DOCUMENT_ATTRIBUTES = ['confidentiality', 'department', 'owner', 'lifecycle'];
// 機密区分の設計上の値集合（ADR-0035 が「4 通り」と数えている根拠）。
const PLANNED_CONFIDENTIALITY_VALUES = ['public', 'internal', 'confidential', 'restricted'];
// 利用者側は BffScopeResolver.ExtractUserAttributes が JWT から取り出す 2 キーに合わせる
// （実装の意味論に合わせる。計画の利用者属性表には projects もあるが、判定には渡っていない）。
const USER_ATTRIBUTE_KEYS = ['clearance', 'department'];

const ABSENT = '(なし)'; // 属性が付いていないことを組み合わせの一値として扱うための表示

// ---------------------------------------------------------------------------
// 集計（純関数。副作用・I/O を持たない）
// ---------------------------------------------------------------------------

// 文書側で ABAC 判定に使うキーを決める。
// 優先順: (1) 属性辞書（AttributeDefinitions の scope=document）→ (2) 計画の文書基本属性。
// どちらにも該当しない実在キー（publishedAt のような高基数メタデータ）は別枠へ分ける。
// これをしないと「実在する組み合わせ数」がタイムスタンプの異なり数になってしまい意味を失う。
function resolveDocumentAbacKeys(definitions, observedKeys) {
  const fromDictionary = (definitions || [])
    .filter((d) => d && d.scope === 'document' && typeof d.key === 'string')
    .map((d) => d.key);
  const source = fromDictionary.length > 0 ? 'dictionary' : 'plan';
  const candidates = source === 'dictionary' ? fromDictionary : PLANNED_DOCUMENT_ATTRIBUTES;
  const observed = new Set(observedKeys || []);
  return {
    source,
    // 候補のうち実データに現れたものだけを組み合わせの軸にする。
    abacKeys: candidates.filter((k) => observed.has(k)),
    // 候補に無いのに実在するキー（＝ABAC 対象外の実在キー）。隠さず列挙する。
    outOfScopeKeys: [...observed].filter((k) => !candidates.includes(k)).sort(),
    // 候補に挙がっているのに実データに無いキー。
    unusedCandidateKeys: candidates.filter((k) => !observed.has(k)),
  };
}

// 文書行（{attributes, tags, count}）に現れる属性キーの全集合。
function collectObservedKeys(rows) {
  const keys = new Set();
  for (const row of rows || []) {
    for (const k of Object.keys(row.attributes || {})) keys.add(k);
  }
  return [...keys].sort();
}

function comboOf(attributes, keys) {
  const combo = {};
  for (const k of keys) {
    const v = (attributes || {})[k];
    combo[k] = v === undefined || v === null || v === '' ? ABSENT : String(v);
  }
  return combo;
}

function comboLabel(combo) {
  return Object.entries(combo)
    .map(([k, v]) => `${k}=${v}`)
    .join(' / ');
}

// 重み（count）つきで組み合わせを数える。entries は件数降順→ラベル昇順（同数のときの順序を固定する）。
function countCombinations(rows, keys) {
  const acc = new Map();
  let total = 0;
  for (const row of rows || []) {
    const weight = Number(row.count ?? 1);
    total += weight;
    if (keys.length === 0) continue;
    const combo = comboOf(row.attributes, keys);
    const label = comboLabel(combo);
    const hit = acc.get(label);
    if (hit) hit.count += weight;
    else acc.set(label, { label, combo, count: weight });
  }
  const entries = [...acc.values()].sort((a, b) => b.count - a.count || a.label.localeCompare(b.label));
  return { keys, distinct: entries.length, total, entries };
}

// 機密区分単位（ADR-0035 の採用粒度）。
function countConfidentiality(rows) {
  const result = countCombinations(rows, ['confidentiality']);
  const observedValues = result.entries.map((e) => e.combo.confidentiality).filter((v) => v !== ABSENT);
  return {
    ...result,
    plannedValues: PLANNED_CONFIDENTIALITY_VALUES,
    observedValues,
    // 設計上の 4 通りのうち、実データに現れなかった値。
    unusedPlannedValues: PLANNED_CONFIDENTIALITY_VALUES.filter((v) => !observedValues.includes(v)),
  };
}

// 利用者属性（clearance × department）の実在組み合わせ。
function countUserAttributeCombinations(users) {
  const rows = (users || []).map((u) => ({
    attributes: Object.fromEntries(
      Object.entries(u.attributes || {}).map(([k, v]) => [k, Array.isArray(v) ? v[0] : v])
    ),
    count: 1,
  }));
  return countCombinations(rows, USER_ATTRIBUTE_KEYS);
}

// ロール単位。realm ロールの保有集合（順序非依存）の異なり数を数える。
function countRoleSets(users) {
  const acc = new Map();
  for (const u of users || []) {
    const roles = [...new Set(u.realmRoles || [])].sort();
    const label = roles.length ? roles.join(' + ') : ABSENT;
    const hit = acc.get(label);
    if (hit) hit.count += 1;
    else acc.set(label, { label, roles, count: 1 });
  }
  const entries = [...acc.values()].sort((a, b) => b.count - a.count || a.label.localeCompare(b.label));
  return { distinct: entries.length, total: (users || []).length, entries };
}

// 計画が必須とする文書属性のうち、実データに存在しないもの（＝計画と実装の乖離）。
function missingRequiredDocumentAttributes(observedKeys) {
  const observed = new Set(observedKeys || []);
  return PLANNED_REQUIRED_DOCUMENT_ATTRIBUTES.filter((k) => !observed.has(k));
}

// AuthorizationService の意味論（AbacEvaluator.ResolveScope / BffScopeResolver.Matches）を写した評価。
// 条件は「キーが無ければ不一致」「値集合は OR」「キー間は AND」、マッチ 0 件なら deny-by-default。
function resolveScope(userAttributes, policies, action) {
  const filters = new Map();
  let granted = false;
  for (const p of (policies || []).filter((x) => x.isActive && x.action === action)) {
    const userOk = Object.entries(p.userConditions || {}).every(([k, allowed]) => {
      const v = userAttributes[k];
      return v !== undefined && (allowed || []).some((a) => String(a).toLowerCase() === String(v).toLowerCase());
    });
    if (!userOk) continue;
    granted = true;
    for (const [k, values] of Object.entries(p.documentConditions || {})) {
      const merged = new Set([...(filters.get(k) || []), ...(values || [])]);
      filters.set(k, [...merged]);
    }
  }
  return { granted, filters };
}

function scopeMatches(documentAttributes, scope) {
  if (!scope.granted) return false;
  for (const [k, allowed] of scope.filters) {
    const v = (documentAttributes || {})[k];
    if (v === undefined) return false;
    if (!allowed.some((a) => String(a).toLowerCase() === String(v).toLowerCase())) return false;
  }
  return true;
}

// 利用者 × 文書 の直積のうち、現行ポリシーで実際に到達できる組を数える。
// ポリシーが 0 件なら deny-by-default で 0 になる（＝「組み合わせは在るが誰も読めない」を可視化する）。
function countReachablePairs(users, rows, policies, action = 'read') {
  const active = (policies || []).filter((p) => p.isActive && p.action === action);
  let reachablePairs = 0;
  let reachableDocuments = 0;
  const grantedUsers = [];
  for (const u of users || []) {
    const attrs = Object.fromEntries(
      Object.entries(u.attributes || {}).map(([k, v]) => [k, Array.isArray(v) ? v[0] : v])
    );
    const scope = resolveScope(attrs, policies, action);
    if (!scope.granted) continue;
    grantedUsers.push(u.username);
    for (const row of rows || []) {
      if (scopeMatches(row.attributes, scope)) {
        reachablePairs += 1;
        reachableDocuments += Number(row.count ?? 1);
      }
    }
  }
  return {
    action,
    policyCount: (policies || []).length,
    activePolicyCount: active.length,
    grantedUsers,
    reachablePairs,
    reachableDocuments,
  };
}

// 収集済みデータ（documents / users / definitions / policies）から測定結果をまとめる。
function summarize(data) {
  const rows = data.documents || [];
  const observedKeys = collectObservedKeys(rows);
  const keyResolution = resolveDocumentAbacKeys(data.definitions, observedKeys);
  return {
    scope: {
      realm: data.realm,
      documentCount: rows.reduce((n, r) => n + Number(r.count ?? 1), 0),
      distinctDocumentRows: rows.length,
      userCount: (data.users || []).length,
      attributeDefinitionCount: (data.definitions || []).length,
      policyCount: (data.policies || []).length,
    },
    keyResolution,
    observedKeys,
    missingRequiredDocumentAttributes: missingRequiredDocumentAttributes(observedKeys),
    // 粒度 1: 属性組み合わせ単位
    byAttributeCombination: countCombinations(rows, keyResolution.abacKeys),
    // 粒度 2: ロール単位
    byRole: countRoleSets(data.users),
    // 粒度 3: 機密区分単位（ADR-0035 の採用粒度）
    byConfidentiality: countConfidentiality(rows),
    userAttributeCombinations: countUserAttributeCombinations(data.users),
    reachability: countReachablePairs(data.users, rows, data.policies),
  };
}

// ---------------------------------------------------------------------------
// 出力
// ---------------------------------------------------------------------------

function renderText(r, topN = 10) {
  const L = [];
  const hr = '----------------------------------------------------------------------';
  L.push('ABAC 属性組み合わせ数の実測（issue #456 / FR-17・FR-18）');
  L.push(hr);
  L.push(`realm                 : ${r.scope.realm}`);
  L.push(`文書数                : ${r.scope.documentCount}（属性・タグの異なり行 ${r.scope.distinctDocumentRows}）`);
  L.push(`利用者数              : ${r.scope.userCount}`);
  L.push(`属性辞書の定義数      : ${r.scope.attributeDefinitionCount}`);
  L.push(`ABAC ポリシー数       : ${r.scope.policyCount}`);
  L.push('');
  L.push(`ABAC 対象キーの決定   : ${r.keyResolution.source === 'dictionary' ? '属性辞書（AttributeDefinitions）' : '計画の文書基本属性（属性辞書が空のため）'}`);
  L.push(`  対象キー            : ${r.keyResolution.abacKeys.join(', ') || '(なし)'}`);
  L.push(`  対象外の実在キー    : ${r.keyResolution.outOfScopeKeys.join(', ') || '(なし)'}`);
  L.push(`  未使用の候補キー    : ${r.keyResolution.unusedCandidateKeys.join(', ') || '(なし)'}`);
  if (r.missingRequiredDocumentAttributes.length) {
    L.push(`  ⚠ 計画が必須とするが実データに無い属性: ${r.missingRequiredDocumentAttributes.join(', ')}`);
  }
  L.push('');
  L.push(hr);
  L.push('粒度 1: 属性組み合わせ単位');
  L.push(`  実在する組み合わせ数: ${r.byAttributeCombination.distinct}`);
  for (const e of r.byAttributeCombination.entries.slice(0, topN)) {
    L.push(`    ${String(e.count).padStart(6)} 件  ${e.label}`);
  }
  if (r.byAttributeCombination.entries.length > topN) {
    L.push(`    … 他 ${r.byAttributeCombination.entries.length - topN} 組`);
  }
  L.push('');
  L.push('粒度 2: ロール単位（realm ロールの保有集合）');
  L.push(`  実在する組み合わせ数: ${r.byRole.distinct}`);
  for (const e of r.byRole.entries.slice(0, topN)) {
    L.push(`    ${String(e.count).padStart(6)} 人  ${e.label}`);
  }
  L.push('');
  L.push('粒度 3: 機密区分単位（ADR-0035 の採用粒度）');
  L.push(`  実在する値の数      : ${r.byConfidentiality.observedValues.length}（設計上は ${r.byConfidentiality.plannedValues.length} 通り）`);
  for (const e of r.byConfidentiality.entries) {
    L.push(`    ${String(e.count).padStart(6)} 件  ${e.label}`);
  }
  if (r.byConfidentiality.unusedPlannedValues.length) {
    L.push(`  実データに現れない値: ${r.byConfidentiality.unusedPlannedValues.join(', ')}`);
  }
  L.push('');
  L.push(hr);
  L.push('利用者属性（clearance × department）の実在組み合わせ');
  L.push(`  組み合わせ数        : ${r.userAttributeCombinations.distinct}`);
  for (const e of r.userAttributeCombinations.entries) {
    L.push(`    ${String(e.count).padStart(6)} 人  ${e.label}`);
  }
  L.push('');
  L.push('到達可能性（read。AbacEvaluator と同じ意味論で評価）');
  L.push(`  有効ポリシー数      : ${r.reachability.activePolicyCount} / ${r.reachability.policyCount}`);
  L.push(`  許可された利用者    : ${r.reachability.grantedUsers.join(', ') || '(なし。deny-by-default)'}`);
  L.push(`  到達可能な 利用者×文書組: ${r.reachability.reachablePairs}（文書実数 ${r.reachability.reachableDocuments}）`);
  L.push(hr);
  return L.join('\n');
}

// ---------------------------------------------------------------------------
// 収集（I/O）
// ---------------------------------------------------------------------------

const env = (k, d) => process.env[k] || d;

function run(cmd, args, what) {
  const res = spawnSync(cmd, args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  if (res.error) throw new Error(`${what}: ${cmd} を実行できません（${res.error.message}）`);
  if (res.status !== 0) {
    throw new Error(`${what}: ${cmd} が失敗しました（exit ${res.status}）\n${(res.stderr || '').trim()}`);
  }
  return res.stdout;
}

function podName(ns, label) {
  const out = run(
    'kubectl',
    ['-n', ns, 'get', 'pod', '-l', label, '-o', 'jsonpath={.items[0].metadata.name}'],
    `pod の解決（${label}）`
  ).trim();
  if (!out) throw new Error(`pod が見つかりません（-n ${ns} -l ${label}）`);
  return out;
}

// psql を「1 行 1 JSON」で実行する。DSN があれば直接、無ければ kubectl exec 経由。
function psqlJson(db, sql, dsn) {
  const ns = env('ABAC_NS', 'platform-infra');
  const user = env('ABAC_PG_USER', 'kp');
  const stdout = dsn
    ? run('psql', [dsn, '-t', '-A', '-c', sql], `psql（${db}）`)
    : run(
        'kubectl',
        ['-n', ns, 'exec', podName(ns, env('ABAC_PG_LABEL', 'app=postgres')), '--', 'psql', '-U', user, '-d', db, '-t', '-A', '-c', sql],
        `psql（${db}）`
      );
  return stdout
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean)
    .map((s) => JSON.parse(s));
}

// kcadm を kubectl exec 経由で叩く。--fields は使わない:
// kcadm の項目フィルタは attributes（入れ子オブジェクト）を空にして返すため、属性が測れなくなる。
function kcadm(args) {
  const ns = env('ABAC_NS', 'platform-infra');
  const pod = podName(ns, env('ABAC_KC_LABEL', 'app=keycloak'));
  return run('kubectl', ['-n', ns, 'exec', pod, '--', '/opt/keycloak/bin/kcadm.sh', ...args], `kcadm ${args[0]}`);
}

function kcadmLogin() {
  kcadm([
    'config',
    'credentials',
    '--server',
    env('ABAC_KC_INTERNAL_URL', 'http://localhost:8080'),
    '--realm',
    'master',
    '--user',
    env('ABAC_KC_ADMIN_USER', 'admin'),
    '--password',
    env('ABAC_KC_ADMIN_PASSWORD', 'admin'),
  ]);
}

// Keycloak Admin REST を直接叩く（ABAC_KC_URL 指定時）。
async function fetchUsersOverRest(baseUrl, realm) {
  const form = new URLSearchParams({
    grant_type: 'password',
    client_id: 'admin-cli',
    username: env('ABAC_KC_ADMIN_USER', 'admin'),
    password: env('ABAC_KC_ADMIN_PASSWORD', 'admin'),
  });
  const tokenRes = await fetch(`${baseUrl.replace(/\/$/, '')}/realms/master/protocol/openid-connect/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: form,
  });
  if (!tokenRes.ok) throw new Error(`Keycloak 管理トークンの取得に失敗しました（${tokenRes.status}）`);
  const token = (await tokenRes.json()).access_token;
  const get = async (path) => {
    const res = await fetch(`${baseUrl.replace(/\/$/, '')}/admin/realms/${realm}${path}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    if (!res.ok) throw new Error(`Keycloak GET ${path} に失敗しました（${res.status}）`);
    return res.json();
  };
  const users = await get('/users?max=1000');
  for (const u of users) {
    u.realmRoles = (await get(`/users/${u.id}/role-mappings/realm`)).map((r) => r.name);
  }
  return users;
}

function fetchUsersOverKcadm(realm) {
  kcadmLogin();
  const users = JSON.parse(kcadm(['get', 'users', '-r', realm, '--limit', '1000']));
  for (const u of users) {
    u.realmRoles = JSON.parse(kcadm(['get', `users/${u.id}/role-mappings/realm`, '-r', realm])).map((r) => r.name);
  }
  return users;
}

async function collect() {
  const realm = env('ABAC_REALM', 'microservices-platform');
  const docDsn = env('ABAC_DOC_DSN', '');
  const authzDsn = env('ABAC_AUTHZ_DSN', '');
  const kcUrl = env('ABAC_KC_URL', '');

  // 文書は属性・タグでまとめて取る（行数を抑えつつ、重みつきで正確に数えられる）。
  const documents = psqlJson(
    env('ABAC_DOC_DB', 'document_svc'),
    'SELECT json_build_object(\'attributes\', "Attributes", \'tags\', "Tags", \'count\', count(*)) FROM "Documents" GROUP BY "Attributes", "Tags"',
    docDsn
  );
  const definitions = psqlJson(
    env('ABAC_AUTHZ_DB', 'authz_svc'),
    'SELECT json_build_object(\'key\', "Key", \'scope\', "Scope", \'allowedValues\', "AllowedValues", \'required\', "Required") FROM "AttributeDefinitions"',
    authzDsn
  );
  const policies = psqlJson(
    env('ABAC_AUTHZ_DB', 'authz_svc'),
    'SELECT json_build_object(\'name\', "Name", \'action\', "Action", \'userConditions\', "UserConditions", \'documentConditions\', "DocumentConditions", \'isActive\', "IsActive") FROM "Policies"',
    authzDsn
  );
  const users = kcUrl ? await fetchUsersOverRest(kcUrl, realm) : fetchUsersOverKcadm(realm);

  return {
    realm,
    documents,
    definitions,
    policies,
    users: users.map((u) => ({ username: u.username, attributes: u.attributes || {}, realmRoles: u.realmRoles || [] })),
  };
}

async function main(argv) {
  const asJson = argv.includes('--json');
  const inputIdx = argv.indexOf('--input');
  const dumpIdx = argv.indexOf('--dump');

  const data = inputIdx >= 0 ? JSON.parse(fs.readFileSync(argv[inputIdx + 1], 'utf8')) : await collect();
  if (dumpIdx >= 0) fs.writeFileSync(argv[dumpIdx + 1], `${JSON.stringify(data, null, 2)}\n`);

  const result = summarize(data);
  process.stdout.write(asJson ? `${JSON.stringify(result, null, 2)}\n` : `${renderText(result)}\n`);
}

module.exports = {
  PLANNED_DOCUMENT_ATTRIBUTES,
  PLANNED_REQUIRED_DOCUMENT_ATTRIBUTES,
  PLANNED_CONFIDENTIALITY_VALUES,
  USER_ATTRIBUTE_KEYS,
  ABSENT,
  resolveDocumentAbacKeys,
  collectObservedKeys,
  countCombinations,
  countConfidentiality,
  countUserAttributeCombinations,
  countRoleSets,
  missingRequiredDocumentAttributes,
  resolveScope,
  scopeMatches,
  countReachablePairs,
  summarize,
  renderText,
};

if (require.main === module) {
  main(process.argv.slice(2)).catch((e) => {
    process.stderr.write(`[measure-abac-combinations] ${e.message}\n`);
    process.exit(1);
  });
}
