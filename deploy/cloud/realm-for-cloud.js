#!/usr/bin/env node
'use strict';
/**
 * NFR-18, IADR-0674 (#2401): クラウドの最小構成（deploy/cloud）の Keycloak へ取り込む realm を、宣言
 * （deploy/keycloak/microservices-platform-realm.json）から組む。**宣言の開発用の値を 1 つも持ち込まない。**
 *
 *   - クライアントは、取引ユニット（AST）が使う機密クライアント（CLOUD_CLIENTS）と Keycloak の組み込み（account 系）だけを残す。
 *   - 残すクライアントの secret は、呼び出し側が乱数で作って渡す値へ差し替える（宣言の値は使わない）。
 *   - 利用者は、残すクライアントのサービスアカウントだけを残す（人の利用者・試験の利用者は持ち込まない。画面を公開しないので要らない）。
 *   - SMTP の送出先は外す（クラウドの構成は送出の中継を持たない）。
 *   - 結果に `${大文字の名前}`（取り込み時の環境変数の置換）・宣言の secret の値が残っていれば止める（黙って持ち込まない）。
 *
 * 使い方（bootstrap-secrets.sh が呼ぶ）:
 *   node deploy/cloud/realm-for-cloud.js <realm.json> <secrets.json> > <出力>
 *     secrets.json: { "<clientId>": "<secret>", ... }（0600 の一時ファイル。値は標準出力の realm にだけ載る）
 *   標準エラーへは残したクライアントの名前と件数だけを出す（値は出さない）。
 */

const fs = require('fs');

/** 取引ユニットが MSP の realm に要る機密クライアント（サービスアカウント）。AST の 5 つ。 */
const CLOUD_CLIENTS = Object.freeze([
  'ai-stock-trading-svc',
  'ai-stock-trading-kb-writer',
  'ai-stock-trading-kb-reader',
  'ai-stock-trading-llm-caller',
  'ai-stock-trading-owner',
]);

/** Keycloak が realm ごとに持つ組み込みのクライアント（公開クライアント・secret を持たない）。 */
const BUILTIN_CLIENTS = Object.freeze(['account', 'account-console']);

function buildCloudRealm(realm, secrets) {
  const out = JSON.parse(JSON.stringify(realm));
  const declared = new Map(realm.clients.filter((c) => typeof c.secret === 'string').map((c) => [c.clientId, c.secret]));
  for (const id of CLOUD_CLIENTS) {
    if (!realm.clients.some((c) => c.clientId === id)) throw new Error(`宣言に ${id} が無い`);
    const s = secrets[id];
    if (typeof s !== 'string' || s.length < 32) throw new Error(`${id} の secret が渡されていないか短い（32 字以上）`);
    if (s === declared.get(id)) throw new Error(`${id} の secret が宣言の開発用の値と同じ`);
    if (/["\\\s]|\$\{/.test(s)) throw new Error(`${id} の secret に使えない文字がある`);
  }
  const keep = new Set([...CLOUD_CLIENTS, ...BUILTIN_CLIENTS]);
  out.clients = out.clients
    .filter((c) => keep.has(c.clientId))
    .map((c) => (CLOUD_CLIENTS.includes(c.clientId) ? { ...c, secret: secrets[c.clientId] } : c));
  const saOf = new Set(CLOUD_CLIENTS.map((id) => `service-account-${id}`));
  out.users = (out.users || []).filter((u) => saOf.has(u.username) && CLOUD_CLIENTS.includes(u.serviceAccountClientId));
  delete out.smtpServer;
  // 外したクライアントへの対応付けを残さない（account-console の組み込みの対応付けだけを残す）。
  if (out.clientScopeMappings) {
    for (const k of Object.keys(out.clientScopeMappings)) {
      if (!keep.has(k)) delete out.clientScopeMappings[k];
      else out.clientScopeMappings[k] = out.clientScopeMappings[k].filter((m) => keep.has(m.client));
    }
  }
  const text = JSON.stringify(out, null, 2);
  // 環境変数の置換（${E2E_…} 等の大文字の名前）だけを見る。${role_…} は Keycloak の文言の鍵で、置換ではない。
  if (/\$\{[A-Z][A-Z0-9_]*(:[^}]*)?\}/.test(text)) throw new Error('取り込みの置換（${…}）が残っている');
  for (const [id, v] of declared) {
    if (text.includes(JSON.stringify(v))) throw new Error(`宣言の開発用の secret（${id}）が残っている`);
  }
  return out;
}

function main(argv) {
  const [realmPath, secretsPath] = argv;
  if (!realmPath || !secretsPath) {
    process.stderr.write('使い方: node deploy/cloud/realm-for-cloud.js <realm.json> <secrets.json>\n');
    return 2;
  }
  const realm = JSON.parse(fs.readFileSync(realmPath, 'utf8'));
  const secrets = JSON.parse(fs.readFileSync(secretsPath, 'utf8'));
  let out;
  try {
    out = buildCloudRealm(realm, secrets);
  } catch (e) {
    process.stderr.write(`ERROR: realm-for-cloud.js: ${e.message}（値は表示しない）\n`);
    return 1;
  }
  process.stdout.write(`${JSON.stringify(out, null, 2)}\n`);
  process.stderr.write(`    クラウドの realm: クライアント ${out.clients.length} 件（${CLOUD_CLIENTS.join(', ')} ほか組み込み）・利用者 ${out.users.length} 件（サービスアカウントだけ）\n`);
  return 0;
}

module.exports = { buildCloudRealm, CLOUD_CLIENTS, BUILTIN_CLIENTS };
if (require.main === module) process.exit(main(process.argv.slice(2)));
