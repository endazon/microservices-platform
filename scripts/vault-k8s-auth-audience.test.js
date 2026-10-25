#!/usr/bin/env node
'use strict';
/*
 * vault-k8s-auth-audience.test.js
 * NFR-18, IADR-0585（Refs GHSA-j4qv-2w99-xg5p）:
 * **秘匿管理（OpenBao / Vault）の kubernetes 認証で、受ける側（role）と名乗る側（トークン）の対象者（audience）が揃っていることを固定する。**
 *
 * 固定するもの:
 *   1. 受ける側: deploy/ の全ての bootstrap が書く `auth/kubernetes/role/*` は、どれも `audience=vault` を持つ
 *      （母集合は deploy/ 配下の追跡下の .sh を走査して引く。想定の 4 つ〔eso・bff-secret-writer・datasource-connector-reader・
 *      バックアップ〕が実際に見つかることも確かめる＝走査の空振りを防ぐ）。
 *   2. 名乗る側（ESO）: kubernetes 認証の SecretStore / ClusterSecretStore は、どれも serviceAccountRef.audiences ＝ [vault]。
 *   3. 名乗る側（アプリ）: helm の描画（既定＋Vault の所在を与えたもの・values-local）で、Vault__Address が空でない Deployment は
 *      全て、audience: vault・期限 1 時間以内の projected token を読み取り専用でマウントし、Vault__ServiceAccountTokenPath がその
 *      ファイルを指す。所在が空の Deployment にはそのトークンを置かない。コードの既定（BFF・datasource）も同じパス。
 *   4. 名乗る側（バックアップの CronJob）: projected token の audience が vault（platform-backup.test.js も同じ点を見る）。
 *   5. 起動器（scripts/k8s-local-up.sh）は chart（名乗る側）を当ててから、role に audience を付ける bootstrap を呼ぶ
 *      （role に audience が無い間も新しいトークンでログインできるので、この順なら途中で失敗しない。IADR-0585 決定 4）。
 *   5b. 配備の 2 段の口（scripts/lib/vault-role-audience.sh）: role の書き込みは全てこの口を通り、既定は字面どおり audience=vault、
 *      VAULT_ROLE_AUDIENCE_DEFER=1 のときだけ audience=vault を除く（role の audience を書き換えない＝1 段目の確かめ用）。
 *   6. 変異: 上のどれか 1 つを崩すと判定が赤になる（判定が空振りしない）。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** CI は static-checks-units で走らせる。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/vault-k8s-auth-audience.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const { RENDER_IMAGE_TAG_ARGS } = require('./lib/helm-render-tag'); // NFR-20, IADR-0605: chart はタグが無いと描画を落とす

const REPO_ROOT = path.resolve(__dirname, '..');
const AUDIENCE = 'vault';
const CHART = 'deploy/helm/microservices-platform';
const VALUES_LOCAL = 'deploy/local/values-local.yaml';
const BACKUP_CRONJOB = 'deploy/local/platform-backup/vault/cronjob.yaml';
const CODE_DEFAULTS = [
  'src/platform/backend/Bff/Platform.Bff/Foundation/Secrets/VaultKvClient.cs',
  'src/knowledge/backend/Services/DataSourceService/Infrastructure/Secrets/VaultConnectorSecretOptions.cs',
];
// 走査が空振りしていないことの確かめ（増えるのは構わない。減ったら母集合の取り方を見直す）。
const EXPECTED_ROLES = ['eso', 'bff-secret-writer', 'datasource-connector-reader', '$BACKUP_VAULT_ROLE'];

const read = (rel) => fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

function trackedFiles(pathspec) {
  const r = spawnSync('git', ['ls-files', '-z', '--', pathspec], { cwd: REPO_ROOT, encoding: 'utf8' });
  assert.strictEqual(r.status, 0, `git ls-files が失敗した: ${r.stderr}`);
  return r.stdout.split('\0').filter(Boolean);
}

/** シェルの継続行（`\` ＋改行）をつないだうえで、コメントでない行のうち role を書く行を返す。 */
function roleWrites(files) {
  const out = [];
  for (const { file, text } of files) {
    const joined = text.replace(/\\\n\s*/g, ' ');
    for (const line of joined.split('\n')) {
      if (/^\s*#/.test(line)) continue;
      const m = line.match(/vault write auth\/kubernetes\/role\/(\S+)/);
      if (m) out.push({ file, role: m[1].replace(/['"]$/, ''), line });
    }
  }
  return out;
}

function checkRoles(files) {
  const errors = [];
  const writes = roleWrites(files);
  for (const w of writes) {
    const args = w.line.split(/\s+/);
    if (!args.includes(`audience=${AUDIENCE}`)) errors.push(`${w.file}: role ${w.role} が audience=${AUDIENCE} を要求しない`);
  }
  const roles = new Set(writes.map((w) => w.role));
  for (const r of EXPECTED_ROLES) if (!roles.has(r)) errors.push(`role ${r} の書き込みが見つからない（走査の空振り）`);
  return errors;
}

/** kubernetes 認証の store（ESO）。`kubernetes:` の下の serviceAccountRef.audiences を字下げで読む。 */
function checkStores(files) {
  const errors = [];
  let stores = 0;
  for (const { file, text } of files) {
    for (const doc of text.split(/^---\s*$/m)) {
      if (!/^kind:\s*(Cluster)?SecretStore\s*$/m.test(doc)) continue;
      const lines = doc.split('\n');
      const k = lines.findIndex((l) => /^\s+kubernetes:\s*$/.test(l));
      if (k < 0) continue;
      stores++;
      const indent = lines[k].match(/^\s*/)[0].length;
      const block = [];
      for (let i = k + 1; i < lines.length; i++) {
        if (lines[i].trim() === '' || /^\s*#/.test(lines[i])) continue;
        if (lines[i].match(/^\s*/)[0].length <= indent) break;
        block.push(lines[i]);
      }
      const ref = block.findIndex((l) => /^\s+serviceAccountRef:\s*$/.test(l));
      if (ref < 0) { errors.push(`${file}: kubernetes 認証の store に serviceAccountRef が無い`); continue; }
      const refIndent = block[ref].match(/^\s*/)[0].length;
      const aud = [];
      let inAud = false;
      for (let i = ref + 1; i < block.length; i++) {
        const ind = block[i].match(/^\s*/)[0].length;
        if (ind <= refIndent) break;
        if (/^\s+audiences:\s*$/.test(block[i])) { inAud = true; continue; }
        const item = block[i].match(/^\s+-\s*(\S+)\s*$/);
        if (inAud && item) aud.push(item[1].replace(/^["']|["']$/g, ''));
        else inAud = false;
      }
      if (JSON.stringify(aud) !== JSON.stringify([AUDIENCE])) errors.push(`${file}: store の serviceAccountRef.audiences が [${AUDIENCE}] でない（${JSON.stringify(aud)}）`);
    }
  }
  if (stores === 0) errors.push('kubernetes 認証の store が見つからない（走査の空振り）');
  return errors;
}

function hasHelm() {
  return spawnSync(process.platform === 'win32' ? 'where' : 'which', ['helm'], { encoding: 'utf8' }).status === 0;
}
function mustRender(args) {
  const r = spawnSync('helm', ['template', 'msp', CHART, ...RENDER_IMAGE_TAG_ARGS, ...args], { cwd: REPO_ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.stderr}`);
  return r.stdout;
}

/** 描画物の Deployment を { name, vaultAddress, tokenPath, mounts, tokens } にする（描画は helm が整形しているので字下げで読める）。 */
function deployments(rendered) {
  return rendered.split(/^---\s*$/m)
    .filter((t) => /^kind:\s*Deployment\s*$/m.test(t))
    .map((text) => {
      const name = (text.match(/^metadata:\n {2}name:\s*(\S+)/m) || [])[1] || '';
      const envValue = (key) => {
        const m = text.match(new RegExp(`- name: ${key}\\n\\s+value: "?([^"\\n]*)"?`));
        return m ? m[1] : null;
      };
      const mounts = [...text.matchAll(/- name: (\S+)\n\s+mountPath: (\S+)(?:\n\s+readOnly: (true|false))?/g)]
        .map((m) => ({ name: m[1], mountPath: m[2], readOnly: m[3] === 'true' }));
      const tokens = [...text.matchAll(/- name: (\S+)\n\s+projected:\n\s+sources:\n\s+- serviceAccountToken:\n((?:\s{18,}\S.*\n?)+)/g)]
        .map((m) => {
          const field = (k) => ((m[2].match(new RegExp(`${k}:\\s*(\\S+)`)) || [])[1] || null);
          return { volume: m[1], path: field('path'), audience: field('audience'), expirationSeconds: field('expirationSeconds') };
        });
      return { name, vaultAddress: envValue('Vault__Address'), tokenPath: envValue('Vault__ServiceAccountTokenPath'), mounts, tokens };
    });
}

function checkDeployments(deps) {
  const errors = [];
  let withVault = 0;
  for (const d of deps) {
    const vaultTokens = d.tokens.filter((t) => t.audience === AUDIENCE);
    if (!d.vaultAddress) {
      if (vaultTokens.length || d.tokenPath) errors.push(`${d.name}: Vault の所在が空なのに Vault 用のトークンを置いている`);
      continue;
    }
    withVault++;
    // 意図した固定: Vault で名乗る Deployment の projected token は Vault 用の 1 つだけ。別の projected token を足すときはこの判定も見直す。
    if (d.tokens.length !== 1) { errors.push(`${d.name}: projected の serviceAccountToken がちょうど 1 つでない（${d.tokens.length}）`); continue; }
    const t = d.tokens[0];
    if (t.audience !== AUDIENCE) errors.push(`${d.name}: projected token の audience が ${AUDIENCE} でない（${t.audience}）`);
    const exp = Number(t.expirationSeconds);
    if (!(exp >= 600 && exp <= 3600)) errors.push(`${d.name}: projected token の期限が 600〜3600 秒でない（${t.expirationSeconds}）`);
    const m = d.mounts.find((x) => x.name === t.volume);
    if (!m) { errors.push(`${d.name}: projected token をマウントしていない`); continue; }
    if (!m.readOnly) errors.push(`${d.name}: projected token が読み取り専用でない`);
    if (`${m.mountPath}/${t.path}` !== d.tokenPath) errors.push(`${d.name}: Vault__ServiceAccountTokenPath（${d.tokenPath}）がトークン（${m.mountPath}/${t.path}）を指さない`);
    if (/kubernetes\.io\/serviceaccount/.test(d.tokenPath || '')) errors.push(`${d.name}: k8s API 用の既定のトークンで名乗る`);
  }
  return { errors, withVault };
}

const shFiles = trackedFiles('deploy').filter((f) => f.endsWith('.sh')).map((file) => ({ file, text: read(file) }));
const yamlFiles = trackedFiles('deploy').filter((f) => /\.ya?ml$/.test(f) && !f.startsWith(`${CHART}/`)).map((file) => ({ file, text: read(file) }));

ok('1. 受ける側: deploy/ の bootstrap が書く k8s auth の role は全て audience=vault を要求する', () => {
  assert.deepStrictEqual(checkRoles(shFiles), []);
  // 変異: 1 つの role から audience を外すと落ちる。
  const eso = shFiles.find((f) => f.file === 'deploy/local/vault/eso/bootstrap.sh');
  assert.ok(eso, 'deploy/local/vault/eso/bootstrap.sh が無い');
  for (const role of ['eso', 'bff-secret-writer', 'datasource-connector-reader']) {
    const mutated = shFiles.map((f) => (f === eso ? { ...f, text: f.text.replace(new RegExp(`(role/${role} [^\\n]*?) audience=${AUDIENCE}`), '$1') } : f));
    assert.ok(checkRoles(mutated).some((e) => e.includes(`role ${role} `)), `role ${role} の audience の欠落を見逃した`);
  }
  const backup = shFiles.find((f) => f.file === 'deploy/local/platform-backup/vault/bootstrap.sh');
  const mutatedBackup = shFiles.map((f) => (f === backup ? { ...f, text: f.text.replace(`  audience=${AUDIENCE} token_policies`, '  token_policies') } : f));
  assert.ok(checkRoles(mutatedBackup).length > 0, 'バックアップの role の audience の欠落を見逃した');
  // 変異: 新しい role を audience なしで足すと落ちる。
  const added = [...shFiles, { file: 'x.sh', text: "vexec 'vault write auth/kubernetes/role/new-reader bound_service_account_names=x policies=p ttl=1h'\n" }];
  assert.ok(checkRoles(added).some((e) => e.includes('new-reader')), 'audience の無い新しい role を見逃した');
});

ok('2. 名乗る側（ESO）: kubernetes 認証の store は serviceAccountRef.audiences ＝ [vault]', () => {
  assert.deepStrictEqual(checkStores(yamlFiles), []);
  const store = yamlFiles.find((f) => f.file === 'deploy/local/vault/eso/clustersecretstore-k8s.yaml');
  assert.ok(store, 'clustersecretstore-k8s.yaml が無い');
  const without = yamlFiles.map((f) => (f === store ? { ...f, text: f.text.replace(/\n\s+audiences:\n\s+- vault/, '') } : f));
  assert.ok(checkStores(without).length > 0, 'audiences の欠落を見逃した');
  const other = yamlFiles.map((f) => (f === store ? { ...f, text: f.text.replace(/(audiences:\n\s+- )vault/, '$1api') } : f));
  assert.ok(checkStores(other).length > 0, '別の audience を見逃した');
});

if (!hasHelm()) {
  console.error('vault-k8s-auth-audience: helm が見つからない（fail-closed。CI は azure/setup-helm で入れる）');
  process.exit(1);
}

const RENDERS = {
  default: mustRender([]),
  // 本番像に Vault の所在を与えたもの（BFF と datasource の両方が Vault で名乗る構成）。
  vault: mustRender([
    '--set', 'services.bff.vault.address=http://vault.example:8200',
    '--set', 'services.datasource.vault.address=http://vault.example:8200',
  ]),
  local: mustRender(['-f', VALUES_LOCAL]),
};

ok('3. 名乗る側（アプリ）: Vault の所在を持つ Deployment は audience: vault の projected token で名乗り、持たないものには置かない', () => {
  const counts = {};
  for (const [label, rendered] of Object.entries(RENDERS)) {
    const { errors, withVault } = checkDeployments(deployments(rendered));
    assert.deepStrictEqual(errors, [], `${label}: ${errors.join(' / ')}`);
    counts[label] = withVault;
  }
  // 母集合の確かめ: 既定（所在が空）は 0、所在を与えると BFF と datasource の 2 つ、values-local は BFF の 1 つ。
  assert.deepStrictEqual(counts, { default: 0, vault: 2, local: 1 });
  // 変異: 描画物から audience を外す・別の値にする・env のパスをずらす・マウントを書き込み可にすると落ちる。
  const v = RENDERS.vault;
  const mutants = {
    'audience の欠落': v.replace(/\n\s+audience: vault$/m, ''),
    '別の audience': v.replace(/^(\s+)audience: vault$/m, '$1audience: api'),
    'パスのずれ': v.replace('value: /var/run/secrets/vault/token', 'value: /var/run/secrets/kubernetes.io/serviceaccount/token'),
    '書き込み可のマウント': v.replace(/(mountPath: \/var\/run\/secrets\/vault\n\s+readOnly: )true/, '$1false'),
    '長い期限': v.replace('expirationSeconds: 600', 'expirationSeconds: 86400'),
  };
  for (const [label, text] of Object.entries(mutants)) {
    assert.notStrictEqual(text, v, `変異（${label}）が描画物に当たっていない`);
    assert.ok(checkDeployments(deployments(text)).errors.length > 0, `${label} を見逃した`);
  }
});

ok('3b. コードの既定のトークンの置き場（BFF・datasource）はチャートが置くパスと同じ', () => {
  const chartPath = deployments(RENDERS.vault).find((d) => d.vaultAddress).tokenPath;
  for (const rel of CODE_DEFAULTS) {
    const m = read(rel).match(/DefaultServiceAccountTokenPath\s*=\s*"([^"]+)"/);
    assert.ok(m, `${rel} に DefaultServiceAccountTokenPath が無い`);
    assert.strictEqual(m[1], chartPath, `${rel} の既定（${m[1]}）がチャートのパス（${chartPath}）と違う`);
    assert.ok(/ServiceAccountTokenPath \{ get; set; \} = DefaultServiceAccountTokenPath;/.test(read(rel)), `${rel} の既定が定数を使っていない`);
  }
});

ok('4. 名乗る側（バックアップの CronJob）: projected token の audience が vault', () => {
  const cj = read(BACKUP_CRONJOB);
  const m = cj.match(/- serviceAccountToken:\n((?:\s{22,}\S.*\n?)+)/);
  assert.ok(m, 'CronJob に projected の serviceAccountToken が無い');
  assert.ok(new RegExp(`audience:\\s*${AUDIENCE}\\b`).test(m[1]), 'CronJob の projected token の audience が vault でない');
});

/** 起動器の順序: 名乗る側（chart の [6/7]）を、role に audience を付ける bootstrap より先に当てる。 */
function checkOrder(up) {
  const errors = [];
  const lines = up.split('\n');
  const at = (re) => lines.findIndex((l) => !/^\s*#/.test(l) && re.test(l));
  const helm = at(/^helm upgrade --install msp deploy\/helm\/microservices-platform/);
  const eso = at(/^\s*bash deploy\/local\/vault\/eso\/bootstrap\.sh\s*$/);
  const backup = at(/^\s*platform_backup_vault_bootstrap\s*$/);
  const store = at(/^\s*kubectl apply -f deploy\/local\/vault\/eso\/clustersecretstore-k8s\.yaml\s*$/);
  if (helm < 0 || eso < 0 || backup < 0 || store < 0) errors.push(`起動器の段が見つからない（helm=${helm} eso=${eso} backup=${backup} store=${store}）`);
  else {
    if (!(helm < eso)) errors.push('chart（[6/7]）より先に ESO の bootstrap（role の audience）を当てている');
    if (!(helm < backup)) errors.push('chart（[6/7]）より先にバックアップの bootstrap を当てている');
  }
  return errors;
}

ok('5. 起動器の順序: chart（名乗る側）を当ててから role に audience を付ける', () => {
  const up = read('scripts/k8s-local-up.sh');
  assert.deepStrictEqual(checkOrder(up), []);
  // 変異: ESO の bootstrap を chart より前へ動かすと落ちる。
  const moved = up.replace(/^(\s*)bash deploy\/local\/vault\/eso\/bootstrap\.sh\s*$/m, '$1:')
    .replace(/^helm upgrade --install msp /m, 'bash deploy/local/vault/eso/bootstrap.sh\nhelm upgrade --install msp ');
  assert.ok(checkOrder(moved).length > 0, '順序の逆転を見逃した');
});

/** 2 段の口（VAULT_ROLE_AUDIENCE_DEFER）: lib を source して関数を通した結果を返す。 */
const ROLE_LIB = 'scripts/lib/vault-role-audience.sh';
function viaLib(libText, cmd, env) {
  const r = spawnSync('bash', ['-c', `${libText}\nvault_role_write_cmd "$1"`, 'x', cmd], {
    encoding: 'utf8', env: { PATH: process.env.PATH, ...env },
  });
  assert.strictEqual(r.status, 0, `lib の実行が失敗した: ${r.stderr}`);
  return r.stdout;
}
function checkDeferLib(libText) {
  const errors = [];
  const cmd = 'vault write auth/kubernetes/role/eso bound_service_account_names=a policies=p audience=vault ttl=1h';
  const want = 'vault write auth/kubernetes/role/eso bound_service_account_names=a policies=p ttl=1h';
  // 既定（未設定・1 以外の値）は字面のまま＝audience=vault を書く（打ち間違いで黙って緩めない）。
  for (const v of [undefined, '', '0', 'true', 'yes', ' 1']) {
    const out = viaLib(libText, cmd, v === undefined ? {} : { VAULT_ROLE_AUDIENCE_DEFER: v });
    if (out !== cmd) errors.push(`DEFER=${JSON.stringify(v)} で字面が変わった（${out}）`);
  }
  // 1 のときだけ audience=vault の 1 語を除く（他の引数・語の一部は触らない）。
  const out = viaLib(libText, cmd, { VAULT_ROLE_AUDIENCE_DEFER: '1' });
  if (out !== want) errors.push(`DEFER=1 で audience=vault を除いていない（${out}）`);
  const tricky = 'vault write auth/kubernetes/role/x policies=audience=vault-ish ttl=1h';
  if (viaLib(libText, tricky, { VAULT_ROLE_AUDIENCE_DEFER: '1' }) !== tricky) errors.push('DEFER=1 で audience=vault 以外の語を書き換えた');
  return errors;
}
/** deploy/ の role の書き込みは全て口（vault_role_write_cmd）を通り、そのファイルは lib を source する。 */
function checkRolesUseLib(files) {
  const errors = [];
  for (const w of roleWrites(files)) {
    if (!/vault_role_write_cmd ['"]vault write auth\/kubernetes\/role\//.test(w.line)) errors.push(`${w.file}: role ${w.role} の書き込みが vault_role_write_cmd を通らない`);
    const text = files.find((f) => f.file === w.file).text;
    if (!/^\. "\$ROOT\/scripts\/lib\/vault-role-audience\.sh"$/m.test(text)) errors.push(`${w.file}: ${ROLE_LIB} を source していない`);
  }
  return errors;
}

ok('5b. 配備の 2 段の口: 既定は audience=vault のまま、VAULT_ROLE_AUDIENCE_DEFER=1 のときだけ role の audience を書き換えない', () => {
  const lib = read(ROLE_LIB);
  assert.deepStrictEqual(checkDeferLib(lib), []);
  assert.deepStrictEqual(checkRolesUseLib(shFiles), []);
  // 変異: 既定でも除く・1 以外の値でも除く・除かない、のどれも落ちる。
  const mutants = {
    '既定でも除く': lib.replace(/if \[ "\$\{VAULT_ROLE_AUDIENCE_DEFER:-\}" = "1" \]; then\n {4}local s/, 'if true; then\n    local s'),
    '1 以外でも除く': lib.replace(/if \[ "\$\{VAULT_ROLE_AUDIENCE_DEFER:-\}" = "1" \]; then\n {4}local s/, 'if [ -n "${VAULT_ROLE_AUDIENCE_DEFER:-}" ]; then\n    local s'),
    '除かない': lib.replace('s="${s/ audience=vault / }"', ':'),
  };
  for (const [label, text] of Object.entries(mutants)) {
    assert.notStrictEqual(text, lib, `変異（${label}）が lib に当たっていない`);
    assert.ok(checkDeferLib(text).length > 0, `${label} を見逃した`);
  }
  // 変異: 口を通らない role の書き込みを足すと落ちる。
  const eso = shFiles.find((f) => f.file === 'deploy/local/vault/eso/bootstrap.sh');
  const bypass = shFiles.map((f) => (f === eso ? { ...f, text: f.text.replace(/vexec "\$\(vault_role_write_cmd ('vault write auth\/kubernetes\/role\/eso [^\n]*')\)"/, 'vexec $1') } : f));
  assert.notStrictEqual(bypass.find((f) => f.file === eso.file).text, eso.text, '変異（口の迂回）が当たっていない');
  assert.ok(checkRolesUseLib(bypass).some((e) => e.includes('role eso ')), '口を通らない role の書き込みを見逃した');
});

console.log(`vault-k8s-auth-audience: ${passed} tests passed`);
