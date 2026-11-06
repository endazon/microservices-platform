#!/usr/bin/env node
/**
 * NFR-18, IADR-0674 (#2401): クラウドの最小構成の Postgres を、**空のボリュームから実物のイメージで初めて起動**して確かめる。
 *
 * deploy/cloud/infra/postgres.yaml の宣言（イメージの digest・引数・ConfigMap postgres-init / postgres-hba の中身・
 * 証明書を写す initContainer の command）をそのまま使う。
 *   1. 初期化（initdb と 01-create-databases.sh）が、pg_hba の `local ... scram-sha-256` の下で止まらずに終わる。
 *   2. 取引ユニットの 7 つの DB と keycloak の DB ができる。
 *   3. pg_hba が効いている: ソケットはパスワードなしでは入れない・TCP の平文は拒む・TCP の TLS は受ける・pg_isready は通る。
 *   4. 変異: initContainer が鍵の所有者と mode を整えない（root の所有・0644）と、Postgres は起動しない（判定が空振りしない）。
 *
 * 証明書の鍵の所有者と mode は本番と同じ経路で作る: Secret の中身（試験だけの使い捨ての鍵）を読み取り専用で渡し、
 * 宣言の initContainer の command をそのイメージ（root）で走らせて docker のボリュームへ写し、そのボリュームを Postgres に
 * 読み取り専用で渡す。手元のファイルの所有者（試験を走らせる利用者の uid）に依らない。
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

function loadDeclaration() {
  const docs = parseDocuments(fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/infra/postgres.yaml'), 'utf8')).filter((d) => d && d.kind);
  const dep = docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'postgres');
  const spec = dep.spec.template.spec;
  const c = spec.containers.find((x) => x.name === 'postgres');
  const init = (spec.initContainers || []).find((x) => x.name === 'tls-files');
  assert.ok(init, '証明書を写す initContainer（tls-files）が無い');
  return { docs, spec, c, init };
}

/**
 * 宣言（または変異を当てた宣言）で起動する。起動の結果（初期化が終わったか・ログ）と、終わったときの確かめの関数を返す。
 * 呼び出し側が cleanup を必ず呼ぶ。
 */
function start(decl, label) {
  const { docs, spec, c, init } = decl;
  const image = String(c.image);
  assert.match(image, /@sha256:[0-9a-f]{64}$/, 'イメージが digest で固定されていない');
  const args = (c.args || []).map(String);
  const hbaArg = args.find((a) => a.startsWith('hba_file='));
  assert.ok(hbaArg, 'hba_file の引数が無い');

  const tag = crypto.randomBytes(4).toString('hex');
  const name = `deploy-cloud-pg-${label}-${tag}`;
  const tlsVolume = `deploy-cloud-pg-tls-${label}-${tag}`;
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'deploy-cloud-pg-'));
  const cleanup = () => {
    docker(['rm', '-f', name]);
    docker(['volume', 'rm', '-f', tlsVolume]);
    fs.rmSync(dir, { recursive: true, force: true });
  };
  try {
    fs.chmodSync(dir, 0o755);
    const sub = (n) => {
      const p = path.join(dir, n);
      fs.mkdirSync(p, { mode: 0o755 });
      return p;
    };
    const initDir = sub('init');
    const hbaDir = sub('hba');
    const srcDir = sub('tls-src');
    for (const d of docs.filter((x) => x.kind === 'ConfigMap')) {
      const to = d.metadata.name === 'postgres-hba' ? hbaDir : d.metadata.name === 'postgres-init' ? initDir : null;
      if (!to) continue;
      for (const [k, v] of Object.entries(d.data || {})) fs.writeFileSync(path.join(to, k), String(v), { mode: 0o755 });
    }
    const hbaMount = path.posix.dirname(hbaArg.slice('hba_file='.length));
    assert.ok(fs.existsSync(path.join(hbaDir, path.posix.basename(hbaArg))), 'hba_file の中身が ConfigMap postgres-hba に無い');

    // Secret postgres-tls の中身の代わり（試験だけの使い捨て）。手元の所有者は問わない（initContainer が root で読んで写す）。
    const ssl = spawnSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=postgres',
      '-keyout', path.join(srcDir, 'tls.key'), '-out', path.join(srcDir, 'tls.crt')], { encoding: 'utf8' });
    assert.strictEqual(ssl.status, 0, `openssl が失敗した: ${ssl.stderr}`);
    for (const f of ['tls.key', 'tls.crt']) fs.chmodSync(path.join(srcDir, f), 0o644);

    const mountOf = (container, volName) => (container.volumeMounts || []).find((m) => m.name === volName).mountPath;
    const srcMount = mountOf(init, 'tls-src');
    const initTlsMount = mountOf(init, 'tls');
    const tlsMount = mountOf(c, 'tls');
    assert.ok((spec.volumes || []).find((v) => v.name === 'tls' && v.emptyDir), '写し先（tls）が emptyDir でない');

    // 手元に在れば取りに行かない（レジストリの回数の上限に掛からないよう、正常の回と変異の回で 1 回だけ取る）。digest で固定しているので同じ物である。
    if (docker(['image', 'inspect', image]).status !== 0) {
      const pull = docker(['pull', '-q', image], { timeout: 600000 });
      assert.strictEqual(pull.status, 0, `イメージを取れない: ${pull.stderr}`);
    }
    assert.strictEqual(docker(['volume', 'create', tlsVolume]).status, 0, 'ボリュームを作れない');
    // 本番と同じ: initContainer をそのイメージ（既定の利用者＝root）で、宣言の command のまま走らせる。
    const initRun = docker(['run', '--rm', '-v', `${srcDir}:${srcMount}:ro`, '-v', `${tlsVolume}:${initTlsMount}`,
      '--entrypoint', String(init.command[0]), String(init.image), ...init.command.slice(1).map(String)]);
    assert.strictEqual(initRun.status, 0, `initContainer が失敗した: ${initRun.stderr}`);

    const rnd = (n) => crypto.randomBytes(n).toString('hex');
    const admin = `pgadmin_${rnd(3)}`;
    const envFile = path.join(dir, 'env');
    fs.writeFileSync(envFile, [
      `POSTGRES_USER=${admin}`, `POSTGRES_PASSWORD=${rnd(24)}`, `POSTGRES_DB=${admin}`, 'PGDATA=/var/lib/postgresql/data/pgdata',
      `AST_DB_USER=ast_${rnd(3)}`, `AST_DB_PASSWORD=${rnd(24)}`, `KEYCLOAK_DB_USER=kc_${rnd(3)}`, `KEYCLOAK_DB_PASSWORD=${rnd(24)}`, '',
    ].join('\n'), { mode: 0o600 });

    const run = docker(['run', '-d', '--name', name, '--env-file', envFile,
      '-v', `${initDir}:/docker-entrypoint-initdb.d:ro`, '-v', `${hbaDir}:${hbaMount}:ro`, '-v', `${tlsVolume}:${tlsMount}:ro`,
      '--tmpfs', '/var/lib/postgresql/data', image, ...args]);
    assert.strictEqual(run.status, 0, `起動できない: ${run.stderr}`);

    // 初期化が終わり、本番の起動まで進む（initdb の後の一時サーバと本番で「ready」が 2 回出る）か、止まるまで待つ。
    const deadline = Date.now() + 120000;
    let logs = '';
    let state = '';
    for (;;) {
      const l = docker(['logs', name]);
      logs = `${l.stdout}${l.stderr}`;
      state = docker(['inspect', '-f', '{{.State.Status}}', name]).stdout.trim();
      const ready = /PostgreSQL init process complete/.test(logs) && (logs.match(/ready to accept connections/g) || []).length >= 2;
      if (ready) return { ready: true, logs, name, hbaArg, cleanup };
      if (state !== 'running' || Date.now() > deadline) return { ready: false, logs, state, name, hbaArg, cleanup };
      spawnSync('sleep', ['1']);
    }
  } catch (e) {
    cleanup();
    throw e;
  }
}

const tailOf = (logs) => logs.split('\n').filter((l) => !/PASSWORD/i.test(l)).slice(-20).join('\n');

function checkDeclared() {
  const r = start(loadDeclaration(), 'ok');
  try {
    if (!r.ready) throw new Error(`空のボリュームからの初回起動が終わらない（state=${r.state}）:\n${tailOf(r.logs)}`);
    console.log('  ok  空のボリュームから初回起動し、初期化（initdb・01-create-databases.sh）が終わる（鍵は initContainer が整える）');

    // 値はコンテナの中の env からだけ読む（docker exec の引数に値を載せない）。
    const sh = (script) => docker(['exec', r.name, 'sh', '-c', script]);
    const dbs = sh('PGPASSWORD="$POSTGRES_PASSWORD" psql -X -tA -U "$POSTGRES_USER" -d postgres -h /var/run/postgresql -c "select datname from pg_database"');
    assert.strictEqual(dbs.status, 0, dbs.stderr);
    const have = new Set(dbs.stdout.split('\n').map((s) => s.trim()).filter(Boolean));
    for (const db of WANT_DBS) assert.ok(have.has(db), `DB ${db} が無い`);
    console.log('  ok  取引ユニットの 7 つの DB と keycloak の DB ができる');

    const probe = (env, host) => sh(`${env} psql -X -tA -U "$POSTGRES_USER" -d postgres -h ${host} -w -c "select 1"`);
    assert.notStrictEqual(probe('PGPASSWORD=', '/var/run/postgresql').status, 0, 'ソケットにパスワードなしで入れた（local が scram-sha-256 でない）');
    assert.strictEqual(probe('PGPASSWORD="$POSTGRES_PASSWORD"', '/var/run/postgresql').status, 0, 'ソケットにパスワードで入れない');
    const plain = probe('PGPASSWORD="$POSTGRES_PASSWORD" PGSSLMODE=disable', '127.0.0.1');
    assert.notStrictEqual(plain.status, 0, 'TCP の平文を受けた（hostssl だけでない）');
    assert.match(plain.stderr, /no pg_hba\.conf entry/);
    assert.strictEqual(probe('PGPASSWORD="$POSTGRES_PASSWORD" PGSSLMODE=require', '127.0.0.1').status, 0, 'TCP の TLS を受けない');
    const hba = sh('PGPASSWORD="$POSTGRES_PASSWORD" psql -X -tA -U "$POSTGRES_USER" -d postgres -h /var/run/postgresql -c "show hba_file"');
    assert.strictEqual(hba.stdout.trim(), r.hbaArg.slice('hba_file='.length), '宣言の hba_file が効いていない');
    assert.strictEqual(sh('pg_isready -U "$POSTGRES_USER"').status, 0, 'pg_isready（readiness）が通らない');
    console.log('  ok  pg_hba が効く（ソケットはパスワード必須・TCP の平文は拒む・TLS は受ける・pg_isready は通る）');
  } finally {
    r.cleanup();
  }
}

function checkMutation() {
  // 変異: initContainer の command から所有者と mode を整える行を外す（root の所有・0644 の鍵になる）。
  const decl = loadDeclaration();
  const script = String(decl.init.command[decl.init.command.length - 1]);
  const mutated = script.split('\n').filter((l) => !/^\s*(chown|chmod)\b/.test(l)).join('\n');
  assert.notStrictEqual(mutated, script, '変異の置換元（chown・chmod の行）が無い（試験が古い）');
  decl.init = { ...decl.init, command: [...decl.init.command.slice(0, -1), mutated] };
  const r = start(decl, 'mut');
  try {
    assert.ok(!r.ready, '鍵の所有者と mode を整えないのに起動した（判定が空振りしている）');
    assert.match(r.logs, /private key file .* has group or world access|private key file .* must be owned by/, `鍵の検査で止まっていない:\n${tailOf(r.logs)}`);
    console.log('  ok  変異: 鍵の所有者と mode を整えないと起動しない（Postgres の鍵の検査で止まる）');
  } finally {
    r.cleanup();
  }
}

try {
  checkDeclared();
  checkMutation();
  console.log('✓ deploy/cloud の Postgres は空のボリュームから起動し、pg_hba と鍵の扱いが宣言どおりに効く');
} catch (e) {
  console.error(`✗ ${e.message}`);
  process.exit(1);
}
