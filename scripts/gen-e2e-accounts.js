#!/usr/bin/env node
'use strict';
/*
 * NFR-10, NFR-18, ADR-0026 / ADR-0124, IADR-0570 (#2024):
 * 試験アカウント e2e-* の資格情報を乱数で作り、OpenBao の KV `secret/msp/e2e-accounts` へ入れる JSON を標準出力へ書く。
 *
 *   node scripts/gen-e2e-accounts.js | kubectl -n platform-infra exec -i deploy/vault -- sh -c \
 *     'export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"; vault kv put secret/msp/e2e-accounts -'
 *
 * - プロパティの集合は realm JSON（deploy/keycloak/microservices-platform-realm.json）のプレースホルダ `${E2E_<ROLE>_PASSWORD}` /
 *   `${E2E_<ROLE>_TOTP_SECRET}` から導く（名前の小文字・`_`→`-`。ExternalSecret の remoteRef.property と同じ規則）。
 *   宣言に利用者を足せば、ここを直さずに増える。
 * - パスワードは realm の passwordPolicy を満たす（判定は reconcile-realm.js の passwordMeetsPolicy。単一の口）。
 *   TOTP の秘密は Keycloak の `secretData.value` に入る**生の文字列**（英数 32 文字）。認証アプリ・E2E が使う base32 は
 *   その UTF-8 バイト列の base32（scripts/lib/totp.js の base32Encode）。
 * - 🔴 **端末へは書かない**（標準出力が TTY なら止める）。パイプで `vault kv put … -` へ流す。値はどのプロセスの引数にも載らない。
 */
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const { passwordMeetsPolicy, E2E_TOTP_SECRET_RE } = require('../deploy/local/keycloak-setup/reconcile-realm.js');

const REALM_FILE = path.join(__dirname, '..', 'deploy', 'keycloak', 'microservices-platform-realm.json');
const ALNUM = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
const SYMBOLS = '-_.';

/** realm の users[].credentials[] に現れる `${E2E_…}` の名前（出現順・重複なし）。 */
function placeholderNames(realm) {
  const names = [];
  for (const u of realm.users || []) {
    for (const m of JSON.stringify(u.credentials || []).matchAll(/\$\{(E2E_[A-Z0-9_]+)\}/g)) {
      if (!names.includes(m[1])) names.push(m[1]);
    }
  }
  return names;
}

/** Secret のキー（プレースホルダの名前）→ KV のプロパティ名。 */
const propertyName = (envName) => envName.toLowerCase().replace(/_/g, '-');

function randomString(alphabet, length, randomInt) {
  let s = '';
  for (let i = 0; i < length; i += 1) s += alphabet[randomInt(alphabet.length)];
  return s;
}

/**
 * KV の JSON（プロパティ → 値）を作る。randomInt は試験で差し替える（既定は crypto.randomInt）。
 * @returns {Record<string, string>}
 */
function generate(realm, randomInt = (n) => crypto.randomInt(n)) {
  const names = placeholderNames(realm);
  if (names.length === 0) throw new Error('realm の宣言に ${E2E_…} のプレースホルダが無い');
  const out = {};
  for (const name of names) {
    let value;
    if (name.endsWith('_TOTP_SECRET')) {
      value = randomString(ALNUM, 32, randomInt);
      if (!E2E_TOTP_SECRET_RE.test(value)) throw new Error(`${name}: 生成した値が形に合わない`);
    } else if (name.endsWith('_PASSWORD')) {
      for (let tries = 0; ; tries += 1) {
        value = randomString(ALNUM, 21, randomInt) + randomString(SYMBOLS, 1, randomInt) + randomString(ALNUM, 2, randomInt);
        if (passwordMeetsPolicy(realm.passwordPolicy, value)) break;
        if (tries >= 100) throw new Error(`${name}: passwordPolicy を満たす値を作れない（ポリシーが変わった？）`);
      }
    } else {
      throw new Error(`${name}: 名前が _PASSWORD / _TOTP_SECRET で終わらない`);
    }
    out[propertyName(name)] = value;
  }
  return out;
}

if (require.main === module) {
  if (process.stdout.isTTY) {
    console.error('ERROR: 標準出力が端末である。値を画面に出さないため止める。`| kubectl … vault kv put secret/msp/e2e-accounts -` へ流す（deploy/local/README.md）');
    process.exit(2);
  }
  const realm = JSON.parse(fs.readFileSync(process.argv[2] || REALM_FILE, 'utf8'));
  const kv = generate(realm);
  process.stdout.write(`${JSON.stringify(kv)}\n`);
  console.error(`    e2e-accounts: ${Object.keys(kv).length} 個のプロパティを生成した（値は表示しない）: ${Object.keys(kv).join(', ')}`);
}

module.exports = { placeholderNames, propertyName, generate };
