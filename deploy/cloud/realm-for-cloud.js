#!/usr/bin/env node
'use strict';
/**
 * NFR-18, IADR-0674 (#2401): クラウドの最小構成（deploy/cloud）の Keycloak へ取り込む realm を、宣言
 * （deploy/keycloak/microservices-platform-realm.json）から組む。**宣言の開発用の値を 1 つも持ち込まない。**
 *
 *   - クライアントは、取引ユニット（AST）が使う機密クライアント（DECLARED_CLOUD_CLIENTS）と Keycloak の組み込み（account 系）だけを残す。
 *   - ［2026-11-07 / IADR-0699］取引ユニットのサービスごとの機密クライアント（SERVICE_CLIENTS）を、宣言の ai-stock-trading-svc と同じ作りで足す
 *     （サービスアカウントだけ・標準フロー・直接のパスワード・リダイレクトなし。トークンの宛先は取引ユニットの ai-stock-trading-api だけ）。
 *   - 残すクライアントの secret は、呼び出し側が乱数で作って渡す値へ差し替える（宣言の値は使わない）。
 *   - 利用者は、残すクライアントのサービスアカウントだけを残す（人の利用者・試験の利用者は持ち込まない。画面を公開しないので要らない）。
 *   - SMTP の送出先は外す（クラウドの構成は送出の中継を持たない）。
 *   - 結果に `${大文字の名前}`（取り込み時の環境変数の置換）・宣言の secret の値が残っていれば止める（黙って持ち込まない）。
 *
 * 使い方:
 *   node deploy/cloud/realm-for-cloud.js --template <realm.json> > platform-realm.json
 *     ［IADR-0689］ESO の雛形を出す（secret の代わりに印を置く。値を持たない）。README §3 が ConfigMap keycloak-realm-template に置く。
 *   node deploy/cloud/realm-for-cloud.js <realm.json> <secrets.json> > <出力>
 *     secrets.json: { "<clientId>": "<secret>", ... }（試験と確かめに使う。クラウドの値は OpenBao の中だけにあり、手元へ出さない）
 *   標準エラーへは残したクライアントの名前と件数だけを出す（値は出さない）。
 */

const fs = require('fs');

/**
 * 取引ユニットがクラウドで MSP の realm に要る機密クライアント（サービスアカウント）。
 * ［2026-11-06 / IADR-0689］3 つに減らした（kb-writer・kb-reader は、ニュースの RAG を持ち込まないクラウドでは取引ユニットが使わない。
 * 取引ユニットの側が values-cloud の描画の secretKeyRef を全件引いて確かめた）。
 */
const DECLARED_CLOUD_CLIENTS = Object.freeze([
  'ai-stock-trading-svc',
  'ai-stock-trading-llm-caller',
  'ai-stock-trading-owner',
]);

/**
 * ［2026-11-07 / IADR-0699］取引ユニットのサービス間の認証を、サービスごとの機密クライアントに分ける（取引ユニットの AST:IADR-0636）。
 * 名前は `ai-stock-trading-svc-<サービス>`。集合は取引ユニットの values-cloud で ServiceAuth を持つ 7 つ（取引ユニットの作業仕様書
 * 20261107_s2s-per-service-clients が呼び手の実装から引いた）。宣言（ローカル）には置かず、クラウドの realm にだけ足す。
 * 🔴 旧の共有クライアント（ai-stock-trading-svc）は、取引ユニットの移行の段が終わるまで残す（外すのは別の変更）。
 */
const SERVICE_CLIENT_TEMPLATE = 'ai-stock-trading-svc';
const SERVICE_CLIENT_SERVICES = Object.freeze([
  'backtest',
  'cost-control',
  'information-collection',
  'market-monitor',
  'order-execution',
  'report',
  'trade-decision',
]);
const SERVICE_CLIENTS = Object.freeze(SERVICE_CLIENT_SERVICES.map((s) => `${SERVICE_CLIENT_TEMPLATE}-${s}`));
/** サービスごとのクライアントが既定のスコープに持つ、取引ユニットの宛先（aud ai-stock-trading-api）のスコープ。 */
const AST_AUDIENCE_SCOPE = 'ai-stock-trading-api-audience';

/** クラウドの realm の機密クライアントの全部（secret を OpenBao の cloud/shared/ast-client-secrets に持つ。seed-secrets.sh が読む）。 */
const CLOUD_CLIENTS = Object.freeze([...DECLARED_CLOUD_CLIENTS, ...SERVICE_CLIENTS]);

/** 宣言の ai-stock-trading-svc とそのサービスアカウントから、サービスごとのクライアントを組む（secret は呼び出し側が差し込む）。 */
function serviceClientsFrom(realm) {
  const base = realm.clients.find((c) => c.clientId === SERVICE_CLIENT_TEMPLATE);
  const baseUser = (realm.users || []).find((u) => u.serviceAccountClientId === SERVICE_CLIENT_TEMPLATE);
  if (!base || !baseUser) throw new Error(`宣言に ${SERVICE_CLIENT_TEMPLATE}（とそのサービスアカウント）が無い`);
  for (const scope of ['roles', AST_AUDIENCE_SCOPE]) {
    if (!(realm.clientScopes || []).some((x) => x.name === scope)) throw new Error(`宣言にクライアントスコープ ${scope} が無い`);
  }
  const flags = { publicClient: false, standardFlowEnabled: false, directAccessGrantsEnabled: false, serviceAccountsEnabled: true, implicitFlowEnabled: false };
  for (const [k, v] of Object.entries(flags)) if (k in base && base[k] !== v) throw new Error(`宣言の ${SERVICE_CLIENT_TEMPLATE} の ${k} が ${v} でない`);
  return SERVICE_CLIENT_SERVICES.map((svc) => {
    const clientId = `${SERVICE_CLIENT_TEMPLATE}-${svc}`;
    const client = {
      ...JSON.parse(JSON.stringify(base)),
      clientId,
      name: `AST service client: ${svc} (intra-unit s2s, client_credentials)`,
      description: `IADR-0699 / AST:IADR-0636: 取引ユニットの ${svc} のサービス間の照会の機密クライアント（client_credentials・service-account に trading-service）。宛先 ai-stock-trading-api を載せる。クラウドの realm にだけ置く。`,
      ...flags,
      redirectUris: [],
      webOrigins: [],
      // 宛先は取引ユニットの API だけ（基盤の共通の宛先 platform-api は載せない。呼ぶのは取引ユニットの口だけ）。
      defaultClientScopes: ['roles', AST_AUDIENCE_SCOPE],
    };
    delete client.id;
    const user = { ...JSON.parse(JSON.stringify(baseUser)), username: `service-account-${clientId}`, serviceAccountClientId: clientId };
    delete user.id;
    return { client, user };
  });
}

/**
 * ［IADR-0689］ESO の template の雛形（ConfigMap keycloak-realm-template）に置く印。secret の代わりに置き、ESO が
 * OpenBao の cloud/shared/ast-client-secrets の値で置き換える（engineVersion v2 の Go の template。キーは clientId）。
 * 雛形は secret を持たないので、ConfigMap に置いてよい。
 */
// 引用符は JSON で \" に化けるので、Go の template の生の文字列（逆引用符）でキーを書く。
const templateMarker = (clientId) => `{{ index . \`${clientId}\` }}`;

/** Keycloak が realm ごとに持つ組み込みのクライアント（公開クライアント・secret を持たない）。 */
const BUILTIN_CLIENTS = Object.freeze(['account', 'account-console']);

function buildCloudRealm(realm, secrets, { template = false } = {}) {
  if (template) secrets = Object.fromEntries(CLOUD_CLIENTS.map((id) => [id, templateMarker(id)]));
  const out = JSON.parse(JSON.stringify(realm));
  const declared = new Map(realm.clients.filter((c) => typeof c.secret === 'string').map((c) => [c.clientId, c.secret]));
  for (const id of SERVICE_CLIENTS) if (realm.clients.some((c) => c.clientId === id)) throw new Error(`宣言に ${id} が在る（クラウドの realm にだけ足す）`);
  for (const id of DECLARED_CLOUD_CLIENTS) if (!realm.clients.some((c) => c.clientId === id)) throw new Error(`宣言に ${id} が無い`);
  const added = serviceClientsFrom(realm);
  for (const id of CLOUD_CLIENTS) {
    const s = secrets[id];
    if (typeof s !== 'string' || s.length < 32) throw new Error(`${id} の secret が渡されていないか短い（32 字以上）`);
    if (s === declared.get(id)) throw new Error(`${id} の secret が宣言の開発用の値と同じ`);
    if (!template && /["\\\s]|\$\{|\{\{/.test(s)) throw new Error(`${id} の secret に使えない文字がある`);
  }
  const keep = new Set([...CLOUD_CLIENTS, ...BUILTIN_CLIENTS]);
  out.clients = [...out.clients, ...added.map((a) => a.client)]
    .filter((c) => keep.has(c.clientId))
    .map((c) => (CLOUD_CLIENTS.includes(c.clientId) ? { ...c, secret: secrets[c.clientId] } : c));
  const saOf = new Set(CLOUD_CLIENTS.map((id) => `service-account-${id}`));
  out.users = [...(out.users || []), ...added.map((a) => a.user)].filter((u) => saOf.has(u.username) && CLOUD_CLIENTS.includes(u.serviceAccountClientId));
  delete out.smtpServer;
  // 外したクライアントへの対応付けを残さない（account-console の組み込みの対応付けだけを残す）。
  if (out.clientScopeMappings) {
    for (const k of Object.keys(out.clientScopeMappings)) {
      if (!keep.has(k)) delete out.clientScopeMappings[k];
      else out.clientScopeMappings[k] = out.clientScopeMappings[k].filter((m) => keep.has(m.client));
    }
  }
  const text = JSON.stringify(out, null, 2);
  // 雛形の印（{{ … }}）は、残したクライアントの secret の位置にだけ在る（ほかの字面が Go の template に読まれない）。
  const markers = (text.match(/\{\{/g) || []).length;
  if (markers !== (template ? CLOUD_CLIENTS.length : 0)) throw new Error(`template の印の数が違う（${markers}）`);
  // 環境変数の置換（${E2E_…} 等の大文字の名前）だけを見る。${role_…} は Keycloak の文言の鍵で、置換ではない。
  if (/\$\{[A-Z][A-Z0-9_]*(:[^}]*)?\}/.test(text)) throw new Error('取り込みの置換（${…}）が残っている');
  for (const [id, v] of declared) {
    if (text.includes(JSON.stringify(v))) throw new Error(`宣言の開発用の secret（${id}）が残っている`);
  }
  return out;
}

function main(argv) {
  const template = argv[0] === '--template';
  const [realmPath, secretsPath] = template ? argv.slice(1) : argv;
  if (!realmPath || (!template && !secretsPath)) {
    process.stderr.write('使い方: node deploy/cloud/realm-for-cloud.js --template <realm.json> | <realm.json> <secrets.json>\n');
    return 2;
  }
  const realm = JSON.parse(fs.readFileSync(realmPath, 'utf8'));
  const secrets = template ? {} : JSON.parse(fs.readFileSync(secretsPath, 'utf8'));
  let out;
  try {
    out = buildCloudRealm(realm, secrets, { template });
  } catch (e) {
    process.stderr.write(`ERROR: realm-for-cloud.js: ${e.message}（値は表示しない）\n`);
    return 1;
  }
  process.stdout.write(`${JSON.stringify(out, null, 2)}\n`);
  process.stderr.write(`    クラウドの realm: クライアント ${out.clients.length} 件（${CLOUD_CLIENTS.join(', ')} ほか組み込み）・利用者 ${out.users.length} 件（サービスアカウントだけ）\n`);
  return 0;
}

module.exports = {
  buildCloudRealm, templateMarker, CLOUD_CLIENTS, DECLARED_CLOUD_CLIENTS, SERVICE_CLIENTS, SERVICE_CLIENT_SERVICES, AST_AUDIENCE_SCOPE, BUILTIN_CLIENTS,
};
if (require.main === module) process.exit(main(process.argv.slice(2)));
