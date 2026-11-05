#!/usr/bin/env node
'use strict';
/*
 * runbook-secret-handling.test.js
 * NFR-18, IADR-0674: 手順書（docs/ と deploy/ の .md）のコマンドが、秘密を手元に残さない形であることを固定する。
 *
 * 見るもの（コードの行。行頭の空白・`$ ` を除いて読む）:
 *   1. 秘密らしい名前（PASS / SECRET / TOKEN / PHRASE / KEY を含む）へ、字面の値か手元のコマンド置換を `export` しない
 *      （シェルの履歴・環境に残る）。`read -rs` で読み、`export` は ( ) の中で 1 回に限る。
 *      コンテナの中の値の受け渡し（`export …="$VAULT_DEV_ROOT_TOKEN_ID"` のように変数だけを渡す形）は対象外。
 *   2. DB のダンプ・realm の書き出し・tar の退避を、平文のままファイルへ書かない（`| age -R …` へパイプで渡す）。
 *   3. Bearer トークンを curl の引数に載せない（`-H @<(printf …)` で渡す）。
 *   変異: 各規則は、直す前の字面（本変更で直した行）を必ず検出する（規則が空振りしない）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。実行: node scripts/runbook-secret-handling.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

function walk(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (['node_modules', '.git', 'bin', 'obj'].includes(e.name)) continue;
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, out);
    else if (e.name.endsWith('.md')) out.push(p);
  }
  return out;
}

const SECRETISH = /^(?:[A-Z0-9_]*_)?(?:[A-Z0-9]*(?:PASS|PASSWORD|SECRET|TOKEN|PHRASE|KEY))[A-Z0-9_]*$/;
// パス・ファイル名を持つ変数（`*_FILE` / `*_PATH` / `*_DIR`）は値でない。
const PATHISH = /_(FILE|PATH|DIR)$/;

/** 1 行の違反（無ければ空の配列）。 */
function violations(rawLine) {
  const line = rawLine.replace(/^\s*(?:\$\s+)?/, '');
  const out = [];
  for (const m of line.matchAll(/\bexport\s+((?:[A-Za-z_][A-Za-z0-9_]*=(?:"[^"]*"|'[^']*'|\S+)\s*)+)/g)) {
    for (const a of m[1].matchAll(/([A-Za-z_][A-Za-z0-9_]*)=("[^"]*"|'[^']*'|\S+)/g)) {
      const [name, valueRaw] = [a[1], a[2]];
      if (!SECRETISH.test(name) || PATHISH.test(name)) continue;
      const v = valueRaw.replace(/^\\?["']|\\?["']$/g, '');
      // 変数だけを渡す形（"$X" / "\$X"）は対象外。字面の値と、手元のコマンド置換は違反。
      if (/^\\?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?$/.test(v)) continue;
      out.push(`export ${name}=…（字面の値かコマンド置換を export している）`);
    }
  }
  if (/\b(pg_dumpall|pg_dump|kcadm\.sh get realms|tar\b[^|]*-c[a-z]*f -)/.test(line) && /\s>\s*["']?[^\s|&]+/.test(line) && !/\|\s*age\b/.test(line)) {
    out.push('ダンプ・書き出しを平文のままファイルへ書いている（| age -R … へ渡す）');
  }
  if (/curl\b.*-H\s+["']Authorization: Bearer \$/.test(line) || /^\s*-H\s+["']Authorization: Bearer \$/.test(line)) out.push('Bearer トークンを curl の引数に載せている（-H @<(printf …) で渡す）');
  if (/(?:^|\s)(?:-d|--data(?:-urlencode|-raw)?)\s+["']?(?:password|client_secret)=/.test(line)) out.push('パスワード・client secret を curl の引数に載せている（@<(…) か標準入力で渡す）');
  return out;
}

/** .md のコードブロックとインラインコードの中の行を、場所つきで返す。 */
function codeLines(file) {
  const lines = fs.readFileSync(file, 'utf8').split('\n');
  const out = [];
  let inFence = false;
  lines.forEach((l, i) => {
    if (/^\s*(```|~~~)/.test(l)) { inFence = !inFence; return; }
    if (inFence) out.push({ at: i + 1, text: l });
    else for (const m of l.matchAll(/`([^`]+)`/g)) out.push({ at: i + 1, text: m[1] });
  });
  return out;
}

ok('手順書のコマンドは秘密を手元に残さない（export・平文のダンプ・Bearer の引数）', () => {
  const files = [...walk(path.join(REPO_ROOT, 'docs')), ...walk(path.join(REPO_ROOT, 'deploy'))];
  assert.ok(files.length > 50, `走査した手順書が少なすぎる（${files.length}）`);
  const found = [];
  for (const f of files) {
    for (const { at, text } of codeLines(f)) {
      for (const v of violations(text)) found.push(`${path.relative(REPO_ROOT, f)}:${at}: ${v}`);
    }
  }
  assert.deepStrictEqual(found, []);
});

ok('変異: 本変更で直した前の字面を、各規則が検出する（空振りしない）', () => {
  const before = [
    'export DISCORD_BOT_KILLSWITCH_PHRASE="CONFIRM-KILL"',
    'export KB_AUTH_CLIENTSECRET="ai-stock-trading-kb-writer-dev-secret-change-me"',
    'export TOKEN="<platform-admin を持つ主体のアクセストークン>"',
    'export LOKI_TOKEN="$(kubectl -n platform-infra get secret observability-gate -o jsonpath=\'{.data.reader}\' | base64 -d)"',
    'export VAULT_TOKEN=$(kubectl -n platform-infra get secret vault-dev-token -o jsonpath=\'{.data.token}\' | base64 -d)',
    'kubectl -n platform-infra exec "$PG" -- pg_dumpall -U postgres --clean > cutover-pg-before.sql',
    'kubectl -n platform-infra exec "$KC" -- /opt/keycloak/bin/kcadm.sh get realms/platform > cutover-realm-before.json',
    'kubectl -n platform-infra exec deploy/postgres -- pg_dump -U <管理者> -d datasource_svc -Fc > datasource_svc-<日付>.dump',
    'kubectl -n "$NS" exec kc-h2-backup -- tar -C /d -cf - h2 > "keycloak-h2-$(date +%Y%m%d%H%M).tar"',
    'curl -sS -H "Authorization: Bearer $TOKEN" http://127.0.0.1:18092/documents/ast-stale-copies > stale-copies.json',
    'curl -sf -H "Authorization: Bearer $TOKEN" "$KC/admin/realms/platform/clients?clientId=$CLIENT"',
    '  -H "Authorization: Bearer $TOKEN" "$KC/admin/realms/platform/clients/$CID")"',
    '  -d password="$(kubectl -n platform-infra get secret keycloak-admin -o jsonpath=\'{.data.password}\' | base64 -d)" | jq -r .access_token)',
  ];
  for (const l of before) assert.ok(violations(l).length > 0, `検出しない: ${l}`);
  const allowed = [
    'export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"',
    'export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN=\\"\\$VAULT_DEV_ROOT_TOKEN_ID\\"',
    'read -rs -p "x: " KB_AUTH_CLIENTSECRET; echo; export KB_AUTH_CLIENTSECRET',
    'kubectl -n platform-infra exec "$PG" -- pg_dumpall -U postgres --clean | age -R "$R" -o cutover-pg-before.sql.age',
    'curl -sS -H @<(printf \'Authorization: Bearer %s\\n\' "$TOKEN") http://127.0.0.1:18092/x > stale-copies.json',
    'curl -sf -H @"$(auth_hdr)" "$KC/admin/realms/platform/clients?clientId=$CLIENT"',
    '  --data-urlencode password@<(kubectl -n platform-infra get secret keycloak-admin -o jsonpath=\'{.data.password}\' | base64 -d) | jq -r .access_token)',
    'export MSP_SYNC_TOKEN_FILE="$HOME/.msp/token"',
  ];
  for (const l of allowed) assert.deepStrictEqual(violations(l), [], `誤って検出する: ${l}`);
});

process.stdout.write(`runbook-secret-handling.test.js: ${passed} passed\n`);
