#!/usr/bin/env node
'use strict';
// NFR-18, ADR-0124 決定 1, IADR-0517 / IADR-0572 (GHSA-rh44-px44-p48q):
// **dev の値の守りの対象を realm の宣言から導出する**口（ホスト側）。scripts/lib/dev-client-secret-guard.sh が呼ぶ。
//
//   node scripts/lib/dev-client-secret-targets.js <realm ファイル>
//
// 標準出力: 対象 1 つにつき 1 行 `<clientId>\t<env の名前>\t<宣言の secret（公知の dev の値）>`（clientId の昇順）。
// 対象の述語は realm の後追い Job と同じ isDevSecretGuardedClient（deploy/local/keycloak-setup/reconcile-realm.js）を使う ——
// Job へはそのファイル 1 本だけがマウントされるので、述語の正はそちらに置き、ここは読むだけ（複写しない）。
// 🔴 出力するのは宣言の値（リポジトリに公開されている dev の値）だけ。env の値は読まない・出さない。
// 終了コード: 0＝1 件以上を書いた / 1＝読めない・壊れている・0 件（0 件を「守るものが無い」と読まない）。
const fs = require('fs');
const path = require('path');
const { devSecretGuardedClients } = require(path.join(__dirname, '..', '..', 'deploy', 'local', 'keycloak-setup', 'reconcile-realm.js'));

// env の名前の例外（既定の規則 `<CLIENT_ID の大文字・- を _>_CLIENT_SECRET` と違う名前を、起動器・種・投入器が既に使っているもの）。
// 🔴 ここに無いクライアントは既定の規則で決まる（新しいクライアントは足さなくても名前が決まる）。
const ENV_NAME_EXCEPTIONS = Object.freeze({
  bff: 'BFF_OIDC_CLIENT_SECRET', // k8s-local-up.sh・bootstrap.sh・docker-compose.yml の既存の名前
  'abac-seeder': 'ABAC_SEED_CLIENT_SECRET', // scripts/seed-abac-policies.js の既存の名前
});

/** clientId → 上書きに使う環境変数の名前。**純関数**。 */
function envNameOf(clientId) {
  if (Object.prototype.hasOwnProperty.call(ENV_NAME_EXCEPTIONS, clientId)) return ENV_NAME_EXCEPTIONS[clientId];
  return `${String(clientId).toUpperCase().replace(/[^A-Z0-9]/g, '_')}_CLIENT_SECRET`;
}

/** realm の宣言 → [{ clientId, env, devValue }]（clientId の昇順）。**純関数**。 */
function targetsOf(realm) {
  const byId = new Map(((realm && realm.clients) || []).map((c) => [c && c.clientId, c]));
  return devSecretGuardedClients(realm).map((clientId) => ({ clientId, env: envNameOf(clientId), devValue: String(byId.get(clientId).secret) }));
}

function main(argv) {
  const file = argv[0];
  if (!file) throw new Error('使い方: node scripts/lib/dev-client-secret-targets.js <realm ファイル>');
  const realm = JSON.parse(fs.readFileSync(file, 'utf8'));
  const rows = targetsOf(realm);
  if (rows.length === 0) throw new Error(`${file} にサービスアカウントを持つ機密クライアントが 1 つも無い（0 件を緑にしない）`);
  for (const r of rows) {
    // 行の形を壊す字（タブ・改行）や clientId の想定外の字があれば止める（shell 側の読み取りを壊さない）。
    if (!/^[a-z0-9][a-z0-9-]*$/.test(r.clientId)) throw new Error(`clientId の形が想定外: ${JSON.stringify(r.clientId)}`);
    if (/[\t\r\n]/.test(r.devValue)) throw new Error(`${r.clientId} の宣言の secret にタブ・改行がある（値は表示しない）`);
  }
  process.stdout.write(rows.map((r) => `${r.clientId}\t${r.env}\t${r.devValue}\n`).join(''));
}

if (require.main === module) {
  try {
    main(process.argv.slice(2));
  } catch (e) {
    process.stderr.write(`ERROR: dev-client-secret-targets: ${e.message}\n`);
    process.exitCode = 1;
  }
}

module.exports = { envNameOf, targetsOf, ENV_NAME_EXCEPTIONS };
