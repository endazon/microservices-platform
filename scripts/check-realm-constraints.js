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
 *
 * 検査2: 経路ごとに必須の redirect URI / web origin の欠落（Issue #385 再発防止）。
 * 背景: `wiki-js` client の登録 URL は経路ごとに別物（edge 集約 50000 / k8s port-forward 3300 /
 * compose(dev) host 公開 3001 / in-cluster 3000）。#385 では 3001 を「port-forward 用」と取り違えた結果、
 * 非 edge の port-forward 経路（3300）が realm 未登録のまま docs だけが案内し、OIDC が
 * invalid_redirect_uri で完了しなかった。長さと違い URL 欠落は静的に列挙できるため import 前に止める。
 * 対象 client が realm に存在しない場合は検査しない（realm 分割・将来の client 削除で誤検出しない）。
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

// 経路ごとに必須の redirect URI / web origin（Issue #385 再発防止）。落とすとその経路の OIDC が
// invalid_redirect_uri で完了しなくなるため、宣言的に列挙して CI で欠落を検出する。
// 経路の対応は IADR-0095 の追記（2026-07-26・#385）の表を単一情報源とする。
const REQUIRED_CLIENT_URLS = {
  'wiki-js': {
    redirectUris: [
      'http://wiki.localhost:50000/*', // edge 集約（IADR-0091・LOCALEDGE=1）
      'http://localhost:3300/*',       // k8s の port-forward（非 edge・svc/wiki-js 3300:3000）
      'http://localhost:3001/*',       // compose(dev) の host 公開（IADR-0032・ports 3001:3000）
      'http://wiki-js:3000/*',         // in-cluster
    ],
    webOrigins: [
      'http://wiki.localhost:50000',
      'http://localhost:3300',
      'http://localhost:3001',
    ],
  },
};

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

// realm から「必須 URL の欠落」を { path, url } で列挙する（純粋関数）。
// 対象 client が realm に存在しなければ、その client の必須 URL は検査しない。
function collectMissingUrls(realm, required = REQUIRED_CLIENT_URLS) {
  const out = [];
  const clients = (realm && realm.clients) || [];
  for (const clientId of Object.keys(required)) {
    const client = clients.find((c) => c && c.clientId === clientId);
    if (!client) continue;
    for (const field of Object.keys(required[clientId])) {
      const present = new Set(((client[field] || []).filter((u) => u != null)).map(String));
      for (const url of required[clientId][field]) {
        if (!present.has(url)) out.push({ path: `clients[${clientId}].${field}`, url });
      }
    }
  }
  return out;
}

// realm JSON テキストから必須 URL の欠落を返す（パース失敗は throw）。
function checkRealmUrlsText(text, required = REQUIRED_CLIENT_URLS) {
  return collectMissingUrls(JSON.parse(text), required);
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
    results.push({ file: rel, violations: checkRealmText(text), missing: checkRealmUrlsText(text) });
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

  // --- 必須 URL の欠落検査（Issue #385）---
  const req = { 'wiki-js': { redirectUris: ['http://localhost:3300/*', 'http://localhost:3001/*'] } };
  cases.push({
    name: '必須 URL が揃っていれば欠落なし',
    pass: collectMissingUrls(
      { clients: [{ clientId: 'wiki-js', redirectUris: ['http://localhost:3001/*', 'http://localhost:3300/*'] }] },
      req,
    ).length === 0,
  });
  cases.push({
    name: '必須 URL（3300）が欠けていれば検出する',
    pass: (() => {
      const m = collectMissingUrls({ clients: [{ clientId: 'wiki-js', redirectUris: ['http://localhost:3001/*'] }] }, req);
      return m.length === 1 && m[0].url === 'http://localhost:3300/*';
    })(),
  });
  cases.push({
    name: '対象 client が realm に無ければ検査しない（誤検出しない）',
    pass: collectMissingUrls({ clients: [{ clientId: 'other' }] }, req).length === 0,
  });
  cases.push({
    name: 'redirectUris 欠損（undefined）は全件欠落として検出する',
    pass: collectMissingUrls({ clients: [{ clientId: 'wiki-js' }] }, req).length === 2,
  });
  cases.push({
    name: '既定表（REQUIRED_CLIENT_URLS）で実 realm 形と突合できる',
    pass: checkRealmUrlsText(JSON.stringify({
      clients: [{
        clientId: 'wiki-js',
        redirectUris: REQUIRED_CLIENT_URLS['wiki-js'].redirectUris,
        webOrigins: REQUIRED_CLIENT_URLS['wiki-js'].webOrigins,
      }],
    })).length === 0,
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
  const totalMissing = results.reduce((n, r) => n + r.missing.length, 0);
  if (total === 0 && totalMissing === 0) {
    console.log(`[check-realm-constraints] OK: ${files.length} ファイルに ${MAX_LEN} 文字超のフィールド・必須 URL の欠落はありません。`);
    process.exit(0);
  }

  if (total > 0) {
    console.error(`[check-realm-constraints] ${MAX_LEN} 文字（varchar(${MAX_LEN})）超のフィールド ${total} 件を検出しました:`);
    for (const r of results) {
      for (const v of r.violations) {
        console.error(`\n  ${r.file}\n    ${v.path}: ${v.len} 文字（上限 ${v.maxLen}）`);
      }
    }
    console.error('\nrealm import は SQLSTATE 22001 で失敗します。該当フィールドを 255 文字以内へ短縮してください（Issue #18）。');
  }

  if (totalMissing > 0) {
    console.error(`[check-realm-constraints] 経路ごとに必須の URL の欠落 ${totalMissing} 件を検出しました:`);
    for (const r of results) {
      for (const m of r.missing) {
        console.error(`\n  ${r.file}\n    ${m.path}: ${m.url} が未登録`);
      }
    }
    console.error('\n当該経路の OIDC が invalid_redirect_uri で完了しなくなります。経路の対応は IADR-0095 の追記（#385）を参照してください。');
  }
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  charLen,
  collectFields,
  findViolations,
  checkRealmText,
  collectMissingUrls,
  checkRealmUrlsText,
  MAX_LEN,
  REQUIRED_CLIENT_URLS,
};
