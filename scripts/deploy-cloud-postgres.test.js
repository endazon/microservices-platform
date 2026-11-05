#!/usr/bin/env node
/**
 * NFR-18, IADR-0674 (#2401): クラウドの最小構成の Postgres を、**空のボリュームから実物のイメージで初めて起動**して確かめる。
 *
 * deploy/cloud/infra/postgres.yaml の宣言（イメージの digest・引数・ConfigMap postgres-init / postgres-hba の中身）をそのまま使う。
 *   1. 初期化（initdb と 01-create-databases.sh）が、pg_hba の `local ... scram-sha-256` の下で止まらずに終わる。
 *   2. 取引ユニットの 7 つの DB と keycloak の DB ができる。
 *   3. pg_hba が効いている: ソケットはパスワードなしでは入れない・TCP の平文は拒む・TCP の TLS は受ける・pg_isready は通る。
 *
 * docker が要る（CI の static-checks-units は持つ）。無ければ失敗にする（fail-closed）。稼働のクラスタには触れない。
 * 試験の値（利用者名・パスワード）はこの場で乱数で作り、0600 のファイル（--env-file）からだけ渡す（引数・出力に出さない）。
 * 実行: node scripts/deploy-cloud-postgres.test.js
 */
const assert = require('assert');
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const { parseDocuments } = require('./lib/yaml-subset');

const REPO_ROOT = path.resolve(__dirname, '..');
const WANT_DBS = ['audit_svc', 'configuration_svc', 'cost_control_svc', 'market_monitor_svc', 'order_execution_svc', 'report_svc', 'risk_management_svc', 'keycloak'];

function docker(args, opts = {}) {
  const r = spawnSync('docker', args, { encoding: 'utf8', timeout: opts.timeout || 120000, maxBuffer: 16 * 1024 * 1024 });
  if (r.error) throw new Error(`docker が使えない（PATH・デーモンを確かめる）: ${r.error.message}`);
  return r;
}

function main() {
  const docs = parseDocuments(fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/infra/postgres.yaml'), 'utf8')).filter((d) => d && d.kind);
  const dep = docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'postgres');
  const c = dep.spec.template.spec.containers.find((x) => x.name === 'postgres');
  const image = String(c.image);
  assert.match(image, /@sha256:[0-9a-f]{64}$/, 'イメージが digest で固定されていない');
  const args = (c.args || []).map(String);
  const hbaArg = args.find((a) => a.startsWith('hba_file='));
  assert.ok(hbaArg, 'hba_file の引数が無い');

  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'deploy-cloud-pg-'));
  const name = `deploy-cloud-pg-${crypto.randomBytes(4).toString('hex')}`;
  try {
    fs.chmodSync(dir, 0o755);
    const sub = (n) => {
      const p = path.join(dir, n);
      fs.mkdirSync(p, { mode: 0o755 });
      return p;
    };
    const initDir = sub('init');
    const hbaDir = sub('hba');
    const tlsDir = sub('tls');
    for (const d of docs.filter((x) => x.kind === 'ConfigMap')) {
      const to = d.metadata.name === 'postgres-hba' ? hbaDir : d.metadata.name === 'postgres-init' ? initDir : null;
      if (!to) continue;
      for (const [k, v] of Object.entries(d.data || {})) fs.writeFileSync(path.join(to, k), String(v), { mode: 0o755 });
    }
    // 宣言の hba_file のパスを、手元のマウント先に合わせる（ファイル名は宣言のまま）。
    const hbaMount = path.posix.dirname(hbaArg.slice('hba_file='.length));
    assert.ok(fs.existsSync(path.join(hbaDir, path.posix.basename(hbaArg))), 'hba_file の中身が ConfigMap postgres-hba に無い');
    // サーバ証明書（試験だけの使い捨て）。所有者はイメージの postgres（70）で、鍵は 0600（Pod では initContainer が同じことをする）。
    const ssl = spawnSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=postgres',
      '-keyout', path.join(tlsDir, 'tls.key'), '-out', path.join(tlsDir, 'tls.crt')], { encoding: 'utf8' });
    assert.strictEqual(ssl.status, 0, `openssl が失敗した: ${ssl.stderr}`);
    fs.chmodSync(path.join(tlsDir, 'tls.key'), 0o600);
    fs.chmodSync(path.join(tlsDir, 'tls.crt'), 0o644);
    try {
      fs.chownSync(path.join(tlsDir, 'tls.key'), 70, 70);
      fs.chownSync(path.join(tlsDir, 'tls.crt'), 70, 70);
    } catch {
      // root でない実行者は所有者を替えられない。鍵を読めるよう 0644 にする（試験だけの使い捨て）。
      fs.chmodSync(path.join(tlsDir, 'tls.key'), 0o644);
    }
    const rnd = (n) => crypto.randomBytes(n).toString('hex');
    const admin = `pgadmin_${rnd(3)}`;
    const envFile = path.join(dir, 'env');
    fs.writeFileSync(envFile, [
      `POSTGRES_USER=${admin}`, `POSTGRES_PASSWORD=${rnd(24)}`, `POSTGRES_DB=${admin}`, 'PGDATA=/var/lib/postgresql/data/pgdata',
      `AST_DB_USER=ast_${rnd(3)}`, `AST_DB_PASSWORD=${rnd(24)}`, `KEYCLOAK_DB_USER=kc_${rnd(3)}`, `KEYCLOAK_DB_PASSWORD=${rnd(24)}`, '',
    ].join('\n'), { mode: 0o600 });

    const tlsMount = (c.volumeMounts || []).find((m) => m.name === 'tls').mountPath;
    const pull = docker(['pull', '-q', image], { timeout: 600000 });
    assert.strictEqual(pull.status, 0, `イメージを取れない: ${pull.stderr}`);
    const run = docker(['run', '-d', '--name', name, '--env-file', envFile,
      '-v', `${initDir}:/docker-entrypoint-initdb.d:ro`, '-v', `${hbaDir}:${hbaMount}:ro`, '-v', `${tlsDir}:${tlsMount}:ro`,
      '--tmpfs', '/var/lib/postgresql/data', image, ...args]);
    assert.strictEqual(run.status, 0, `起動できない: ${run.stderr}`);

    // 1. 初期化が終わり、本番の起動まで進む（initdb の後の一時サーバと本番で「ready」が 2 回出る）。
    const deadline = Date.now() + 120000;
    let logs = '';
    for (;;) {
      logs = docker(['logs', name]).stderr + docker(['logs', name]).stdout;
      const state = docker(['inspect', '-f', '{{.State.Status}}', name]).stdout.trim();
      if (/PostgreSQL init process complete/.test(logs) && (logs.match(/ready to accept connections/g) || []).length >= 2) break;
      if (state !== 'running' || Date.now() > deadline) {
        const tail = logs.split('\n').filter((l) => !/PASSWORD/i.test(l)).slice(-20).join('\n');
        throw new Error(`空のボリュームからの初回起動が終わらない（state=${state}）:\n${tail}`);
      }
      spawnSync('sleep', ['1']);
    }
    console.log('  ok  空のボリュームから初回起動し、初期化（initdb・01-create-databases.sh）が終わる');

    // 値はコンテナの中の env からだけ読む（docker exec の引数に値を載せない）。
    const sh = (script) => docker(['exec', name, 'sh', '-c', script]);
    const dbs = sh('PGPASSWORD="$POSTGRES_PASSWORD" psql -X -tA -U "$POSTGRES_USER" -d postgres -h /var/run/postgresql -c "select datname from pg_database"');
    assert.strictEqual(dbs.status, 0, dbs.stderr);
    const have = new Set(dbs.stdout.split('\n').map((s) => s.trim()).filter(Boolean));
    for (const db of WANT_DBS) assert.ok(have.has(db), `DB ${db} が無い`);
    console.log('  ok  取引ユニットの 7 つの DB と keycloak の DB ができる');

    const probe = (env, host) => sh(`${env} psql -X -tA -U "$POSTGRES_USER" -d postgres -h ${host} -w -c "select 1"`);
    const noPw = probe('PGPASSWORD=', '/var/run/postgresql');
    assert.notStrictEqual(noPw.status, 0, 'ソケットにパスワードなしで入れた（local が scram-sha-256 でない）');
    assert.strictEqual(probe('PGPASSWORD="$POSTGRES_PASSWORD"', '/var/run/postgresql').status, 0, 'ソケットにパスワードで入れない');
    const plain = probe('PGPASSWORD="$POSTGRES_PASSWORD" PGSSLMODE=disable', '127.0.0.1');
    assert.notStrictEqual(plain.status, 0, 'TCP の平文を受けた（hostssl だけでない）');
    assert.match(plain.stderr, /no pg_hba\.conf entry/);
    assert.strictEqual(probe('PGPASSWORD="$POSTGRES_PASSWORD" PGSSLMODE=require', '127.0.0.1').status, 0, 'TCP の TLS を受けない');
    const hba = sh('PGPASSWORD="$POSTGRES_PASSWORD" psql -X -tA -U "$POSTGRES_USER" -d postgres -h /var/run/postgresql -c "show hba_file"');
    assert.strictEqual(hba.stdout.trim(), hbaArg.slice('hba_file='.length), '宣言の hba_file が効いていない');
    assert.strictEqual(sh('pg_isready -U "$POSTGRES_USER"').status, 0, 'pg_isready（readiness）が通らない');
    console.log('  ok  pg_hba が効く（ソケットはパスワード必須・TCP の平文は拒む・TLS は受ける・pg_isready は通る）');
  } finally {
    docker(['rm', '-f', name]);
    fs.rmSync(dir, { recursive: true, force: true });
  }
  console.log('✓ deploy/cloud の Postgres は空のボリュームから起動し、pg_hba が宣言どおりに効く');
}

try {
  main();
} catch (e) {
  console.error(`✗ ${e.message}`);
  process.exit(1);
}
