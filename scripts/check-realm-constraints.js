#!/usr/bin/env node
'use strict';
/*
 * check-realm-constraints.js
 * Keycloak realm export（deploy/keycloak/*-realm.json）の文字列フィールド長が、import 先 RDB の
 * カラム上限（varchar(255)）を超えていないかを機械検査する（Issue #18 再発防止）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。check-unit-dependencies.js / check-image-mapping.js と同型。
 *
 * 背景: #307 で追加した client `ai-stock-trading-kb-writer` の description が 364 文字あり、Keycloak の
 * CLIENT.DESCRIPTION（varchar(255)）上限を超過。realm import が SQL エラー(SQLSTATE 22001)で失敗し
 * Keycloak pod がクラッシュした。export は JSON なので長さは静的検査でき、import 前に止められる。
 *
 * 検査対象（いずれも Keycloak の JPA エンティティで varchar(255) のカラムに対応する自由記述/名称）:
 *   - clients[].clientId / name / description
 *   - clients[].protocolMappers[].name
 *   - clientScopes[].name / description
 *   - clientScopes[].protocolMappers[].name
 *   - roles.realm[].name / description
 *   - roles.client[*][].name / description
 *   - groups（再帰）.name
 *   - realm / displayName / displayNameHtml
 * 長さは「文字数（コードポイント）」で数える（Postgres の varchar(N) は文字数上限。マルチバイトでも
 * 1 文字 = 1）。網羅的なスキーマ検証ではなく、オーバーフローしやすい自由記述/名称に絞った軽い lint
 * （PR #317 レビュー指摘）。対象外の varchar 系フィールド（attributes 値・authenticationFlows.alias 等）で
 * 同種の import 失敗が起きた場合は、この collectFields に対象を足して範囲を広げる。
 *
 * 使い方:
 *   node scripts/check-realm-constraints.js            # deploy/keycloak/*-realm.json を検査。違反で exit 1。
 *   node scripts/check-realm-constraints.js <path...>  # 明示したファイルのみ検査。
 *   node scripts/check-realm-constraints.js --self-test # 検査ロジック自体の自己試験。
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const REALM_DIR = 'deploy/keycloak';
// Keycloak の該当カラムはいずれも varchar(255)。閾値は 1 箇所に集約する。
const MAX_LEN = 255;

// --- 純粋ロジック（scripts.test.js から単体テストする） -------------------------

// 文字列の「文字数」（コードポイント数）を返す。null/undefined は 0。
function charLen(s) {
  return s == null ? 0 : [...String(s)].length;
}

// realm オブジェクトから、長さ検査対象の { path, value } を列挙する（純粋関数）。
// path は違反表示用の人間可読なパス。value は検査対象の文字列。
function collectFields(realm) {
  const out = [];
  const push = (p, v) => { if (v != null) out.push({ path: p, value: String(v) }); };

  for (const f of ['realm', 'displayName', 'displayNameHtml']) push(`realm.${f}`, realm && realm[f]);

  for (const c of (realm && realm.clients) || []) {
    const id = (c && c.clientId) || '(no clientId)';
    for (const f of ['clientId', 'name', 'description']) push(`clients[${id}].${f}`, c && c[f]);
    for (const pm of (c && c.protocolMappers) || []) {
      push(`clients[${id}].protocolMappers[${(pm && pm.name) || '?'}].name`, pm && pm.name);
    }
  }

  for (const cs of (realm && realm.clientScopes) || []) {
    const nm = (cs && cs.name) || '(no name)';
    for (const f of ['name', 'description']) push(`clientScopes[${nm}].${f}`, cs && cs[f]);
    for (const pm of (cs && cs.protocolMappers) || []) {
      push(`clientScopes[${nm}].protocolMappers[${(pm && pm.name) || '?'}].name`, pm && pm.name);
    }
  }

  const roles = (realm && realm.roles) || {};
  for (const r of roles.realm || []) {
    const n = (r && r.name) || '(no name)';
    for (const f of ['name', 'description']) push(`roles.realm[${n}].${f}`, r && r[f]);
  }
  const clientRoles = roles.client || {};
  for (const cid of Object.keys(clientRoles)) {
    for (const r of clientRoles[cid] || []) {
      const n = (r && r.name) || '(no name)';
      for (const f of ['name', 'description']) push(`roles.client[${cid}][${n}].${f}`, r && r[f]);
    }
  }

  const walkGroups = (gs, prefix) => {
    for (const g of gs || []) {
      const nm = (g && g.name) || '';
      push(`groups[${prefix}${nm}].name`, g && g.name);
      walkGroups(g && g.subGroups, `${prefix}${nm}/`);
    }
  };
  walkGroups(realm && realm.groups, '');

  return out;
}

// 収集済みフィールドのうち maxLen を超えるものを違反として返す（純粋関数）。
function findViolations(fields, maxLen = MAX_LEN) {
  const out = [];
  for (const f of fields) {
    const len = charLen(f.value);
    if (len > maxLen) out.push({ path: f.path, len, maxLen });
  }
  return out;
}

// realm JSON テキストを検査し、違反配列を返す（パース失敗は throw）。
function checkRealmText(text, maxLen = MAX_LEN) {
  const realm = JSON.parse(text);
  return findViolations(collectFields(realm), maxLen);
}

// --- I/O（副作用は main / checkFiles に閉じる） --------------------------------

// 既定の検査対象（REALM_DIR 配下の *-realm.json）をリポジトリ相対で列挙する。
function defaultRealmFiles() {
  const dir = path.join(REPO_ROOT, REALM_DIR);
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir)
    .filter((n) => n.endsWith('-realm.json'))
    .map((n) => `${REALM_DIR}/${n}`);
}

function checkFiles(relPaths) {
  const results = [];
  for (const rel of relPaths) {
    const abs = path.isAbsolute(rel) ? rel : path.join(REPO_ROOT, rel);
    const text = fs.readFileSync(abs, 'utf8');
    results.push({ file: rel, violations: checkRealmText(text) });
  }
  return results;
}

// --- 自己試験 -----------------------------------------------------------------

function selfTest() {
  const cases = [];
  const long = 'a'.repeat(256);
  const ok255 = 'b'.repeat(255);
  const jaLong = 'あ'.repeat(300); // マルチバイトでも 1 文字 = 1 で数える

  cases.push({
    name: '255 文字ちょうどは合格',
    pass: findViolations(collectFields({ clients: [{ clientId: 'x', description: ok255 }] })).length === 0,
  });
  cases.push({
    name: '256 文字の description は違反',
    pass: findViolations(collectFields({ clients: [{ clientId: 'x', description: long }] })).length === 1,
  });
  cases.push({
    name: 'マルチバイト（あ×300）も文字数で 255 超を検出',
    pass: charLen(jaLong) === 300
      && findViolations(collectFields({ clients: [{ clientId: 'x', description: jaLong }] })).length === 1,
  });
  cases.push({
    name: 'realm role / client role / group / realm も走査する',
    pass: findViolations(collectFields({
      realm: 'r', displayName: long,
      roles: { realm: [{ name: 'a', description: long }], client: { c: [{ name: 'b', description: long }] } },
      groups: [{ name: 'g', subGroups: [{ name: long }] }],
    })).length === 4,
  });
  cases.push({
    name: 'clientScopes / protocolMappers（client・scope 双方）も走査する',
    pass: findViolations(collectFields({
      clients: [{ clientId: 'x', protocolMappers: [{ name: long }] }],
      clientScopes: [{ name: 'ok', description: long, protocolMappers: [{ name: long }] }],
    })).length === 3,
  });
  cases.push({
    name: '欠損フィールドは無視（例外を投げない）',
    pass: findViolations(collectFields({ clients: [{ clientId: 'x' }], roles: {}, groups: null })).length === 0,
  });
  cases.push({
    name: 'JSON パース→検査（checkRealmText）が通る',
    pass: checkRealmText(JSON.stringify({ clients: [{ clientId: 'x', description: long }] })).length === 1,
  });

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) failed++;
  }
  if (failed) {
    console.error(`[check-realm-constraints] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-realm-constraints] 自己試験 ${cases.length} 件 OK。`);
}

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) { selfTest(); return; }

  const targets = argv.filter((a) => !a.startsWith('--'));
  const files = targets.length ? targets : defaultRealmFiles();
  if (files.length === 0) {
    console.log('[check-realm-constraints] 検査対象の realm JSON が見つかりません（skip）。');
    process.exit(0);
  }

  const results = checkFiles(files);
  const total = results.reduce((n, r) => n + r.violations.length, 0);
  if (total === 0) {
    console.log(`[check-realm-constraints] OK: ${files.length} ファイルに ${MAX_LEN} 文字超のフィールドはありません。`);
    process.exit(0);
  }
  console.error(`[check-realm-constraints] ${MAX_LEN} 文字（varchar(${MAX_LEN})）超のフィールド ${total} 件を検出しました:`);
  for (const r of results) {
    for (const v of r.violations) {
      console.error(`\n  ${r.file}\n    ${v.path}: ${v.len} 文字（上限 ${v.maxLen}）`);
    }
  }
  console.error('\nrealm import は SQLSTATE 22001 で失敗します。該当フィールドを 255 文字以内へ短縮してください（Issue #18）。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  charLen,
  collectFields,
  findViolations,
  checkRealmText,
  MAX_LEN,
};
