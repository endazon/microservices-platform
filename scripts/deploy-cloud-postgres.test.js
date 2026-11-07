#!/usr/bin/env node
/**
 * NFR-18, IADR-0674 (#2401): クラウドの最小構成の Postgres を、**空のボリュームから実物のイメージで初めて起動**して確かめる。
 *
 * deploy/cloud/infra/postgres.yaml の宣言（イメージの digest・引数・ConfigMap postgres-init / postgres-hba の中身・
 * 証明書を写す initContainer の command）をそのまま使う。
 *   1. 初期化（initdb と 01-create-databases.sh）が、pg_hba の `local ... scram-sha-256` の下で止まらずに終わる。
 *   2. 取引ユニットの 7 つの DB と keycloak の DB ができる。
 *   3. pg_hba が効いている: ソケットはパスワードなしでは入れない・TCP の平文は拒む・TCP の TLS は受ける・pg_isready は通る。
 *   4. 変異: initContainer が鍵の mode を整えない（写した 0440 のまま）と、Postgres は起動しない（判定が空振りしない）。
 *   5. IADR-0695: ConfigMap postgres-ast-db-users の手順（Job が流すもの）を同じ Postgres に TLS で流す。apply が 7 人を作り、各 DB と中の物の
 *      OWNER を移し、各利用者は自分の DB にだけ入れる。移行の間も共有の利用者は読み書きできる。drop-shared は共有の利用者の接続が残れば止め、
 *      無ければ消す。どちらも冪等。値はログ・出力に出ない。変異（CONNECT を外さない・共有の利用者を役割の一員にしない）を検出する。
 *
 * 証明書の鍵の所有者と mode は本番と同じ経路で作る: Secret の中身（試験だけの使い捨ての鍵）を kubelet と同じ形（root の所有・
 * 宣言の defaultMode）のボリュームに置いて読み取り専用で渡し、宣言の initContainer の command を宣言の securityContext（利用者・
 * capability・権限の昇格）で走らせて emptyDir の代わりのボリューム（0777）へ写し、そのボリュームを Postgres に読み取り専用で渡す。
 * 手元のファイルの所有者（試験を走らせる利用者の uid）に依らない。
 * IADR-0697: initContainer・Postgres とも、宣言の securityContext を docker の旗（--user・--cap-drop・--cap-add・no-new-privileges・
 * --read-only）へ写して走らせる。capability を全部落とした root でない利用者で、初回の起動と鍵の扱いが成り立つことを実物で確かめる。
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
const AST_DB_SERVICES = ['audit', 'configuration', 'cost-control', 'market-monitor', 'order-execution', 'report', 'risk-management'];
const dbOf = (svc) => `${svc.replace(/-/g, '_')}_svc`;
const envOf = (kind, svc) => `AST_DB_${kind}_${svc.replace(/-/g, '_').toUpperCase()}`;
const WANT_DBS = ['audit_svc', 'configuration_svc', 'cost_control_svc', 'market_monitor_svc', 'order_execution_svc', 'report_svc', 'risk_management_svc', 'keycloak'];

/**
 * IADR-0697: 宣言の securityContext（Pod と コンテナ。コンテナの側が勝つ）を docker run の旗へ写す。
 * 宣言に無いもの（root で走る・capability を落とさない）は旗を足さない＝その宣言のまま走る（試験が宣言を緩めない）。
 * seccomp は docker の既定のプロファイル（RuntimeDefault と同じ扱い）で走る。
 */
function dockerSecurityArgs(spec, container) {
  const pod = spec.securityContext || {};
  const sc = container.securityContext || {};
  const pick = (k) => (sc[k] !== undefined ? sc[k] : pod[k]);
  const out = [];
  const user = pick('runAsUser');
  const group = pick('runAsGroup');
  if (String(pick('runAsNonRoot')) === 'true') assert.ok(user !== undefined && String(user) !== '0', `${container.name}: runAsNonRoot なのに利用者が数で書かれていない（${user}）`);
  if (user !== undefined) out.push('--user', group !== undefined ? `${user}:${group}` : String(user));
  const caps = sc.capabilities || {};
  for (const x of caps.drop || []) out.push('--cap-drop', String(x));
  for (const x of caps.add || []) out.push('--cap-add', String(x));
  if (String(sc.allowPrivilegeEscalation) === 'false') out.push('--security-opt', 'no-new-privileges');
  if (String(sc.readOnlyRootFilesystem) === 'true') out.push('--read-only');
  return out;
}

/** 宣言の defaultMode（'0440' の形の文字列か数）を chmod の 8 進の字面にする。 */
const modeOf = (v) => (typeof v === 'number' ? v.toString(8) : String(v).replace(/^0o/, ''));

function docker(args, opts = {}) {
  const r = spawnSync('docker', args, { encoding: 'utf8', timeout: opts.timeout || 120000, maxBuffer: 16 * 1024 * 1024, input: opts.input });
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
  const users = parseDocuments(fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/infra/postgres-ast-db-users.yaml'), 'utf8'))
    .find((d) => d && d.kind === 'ConfigMap' && d.metadata.name === 'postgres-ast-db-users');
  assert.ok(users && users.data && users.data['ast-db-users.sh'], 'ConfigMap postgres-ast-db-users の ast-db-users.sh が無い');
  return { docs, spec, c, init, usersScript: String(users.data['ast-db-users.sh']) };
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
  const secretVolume = `deploy-cloud-pg-secret-${label}-${tag}`;
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'deploy-cloud-pg-'));
  const cleanup = () => {
    docker(['rm', '-f', name]);
    docker(['volume', 'rm', '-f', tlsVolume]);
    docker(['volume', 'rm', '-f', secretVolume]);
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
    const usersDir = sub('ast-db-users');
    fs.writeFileSync(path.join(usersDir, 'ast-db-users.sh'), decl.usersScript, { mode: 0o755 });
    const hbaDir = sub('hba');
    const srcDir = sub('tls-src');
    for (const d of docs.filter((x) => x.kind === 'ConfigMap')) {
      const to = d.metadata.name === 'postgres-hba' ? hbaDir : d.metadata.name === 'postgres-init' ? initDir : null;
      if (!to) continue;
      for (const [k, v] of Object.entries(d.data || {})) fs.writeFileSync(path.join(to, k), String(v), { mode: 0o755 });
    }
    const hbaMount = path.posix.dirname(hbaArg.slice('hba_file='.length));
    assert.ok(fs.existsSync(path.join(hbaDir, path.posix.basename(hbaArg))), 'hba_file の中身が ConfigMap postgres-hba に無い');

    // Secret postgres-tls の中身の代わり（試験だけの使い捨て）。手元の所有者は問わない（下で kubelet と同じ形のボリュームへ写す）。
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
    assert.strictEqual(docker(['volume', 'create', secretVolume]).status, 0, 'ボリュームを作れない');
    // kubelet の代わり（試験の足場。宣言ではない）: Secret のボリューム（root の所有・宣言の defaultMode）と emptyDir（0777）を root で整える。
    const secretVol = (spec.volumes || []).find((v) => v.name === 'tls-src' && v.secret);
    assert.ok(secretVol && secretVol.secret.defaultMode !== undefined, '写し元（tls-src）が defaultMode を持つ Secret でない');
    const prep = docker(['run', '--rm', '--user', '0:0', '-v', `${srcDir}:/in:ro`, '-v', `${secretVolume}:/s`, '-v', `${tlsVolume}:/e`, '--entrypoint', 'sh', image, '-c',
      `cp /in/tls.crt /in/tls.key /s/ && chown 0:0 /s/tls.crt /s/tls.key && chmod ${modeOf(secretVol.secret.defaultMode)} /s/tls.crt /s/tls.key && chmod 0777 /e`]);
    assert.strictEqual(prep.status, 0, `ボリュームを整えられない: ${prep.stderr}`);
    // 本番と同じ: initContainer をそのイメージで、宣言の command と securityContext のまま走らせる。
    const initRun = docker(['run', '--rm', ...dockerSecurityArgs(spec, init), '-v', `${secretVolume}:${srcMount}:ro`, '-v', `${tlsVolume}:${initTlsMount}`,
      '--entrypoint', String(init.command[0]), String(init.image), ...init.command.slice(1).map(String)]);
    assert.strictEqual(initRun.status, 0, `initContainer が失敗した: ${initRun.stderr}`);

    const rnd = (n) => crypto.randomBytes(n).toString('hex');
    const admin = `pgadmin_${rnd(3)}`;
    const envFile = path.join(dir, 'env');
    fs.writeFileSync(envFile, [
      `POSTGRES_USER=${admin}`, `POSTGRES_PASSWORD=${rnd(24)}`, `POSTGRES_DB=${admin}`, 'PGDATA=/var/lib/postgresql/data/pgdata',
      `AST_DB_USER=ast_${rnd(3)}`, `AST_DB_PASSWORD=${rnd(24)}`, `KEYCLOAK_DB_USER=kc_${rnd(3)}`, `KEYCLOAK_DB_PASSWORD=${rnd(24)}`,
      // IADR-0695: Job の env と同じ名前（seed-secrets.sh の形: ast_<サービス>_<乱数>・24 バイト）。初期化は読まない。
      ...AST_DB_SERVICES.flatMap((svc) => [`${envOf('USER', svc)}=ast_${svc.replace(/-/g, '_')}_${rnd(3)}`, `${envOf('PASSWORD', svc)}=${rnd(24)}`]), '',
    ].join('\n'), { mode: 0o600 });

    const run = docker(['run', '-d', '--name', name, ...dockerSecurityArgs(spec, c), '--env-file', envFile,
      '-v', `${initDir}:/docker-entrypoint-initdb.d:ro`, '-v', `${usersDir}:/opt/ast-db-users:ro`, '-v', `${hbaDir}:${hbaMount}:ro`, '-v', `${tlsVolume}:${tlsMount}:ro`,
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
      if (ready) return { ready: true, logs, name, hbaArg, cleanup, envFile };
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
    console.log('  ok  空のボリュームから初回起動し、初期化（initdb・01-create-databases.sh）が終わる（鍵は initContainer が整える。どちらも宣言の securityContext＝root でない・capability なし）');
    const who = docker(['exec', r.name, 'sh', '-c', 'id -u; grep -E "^Cap(Eff|Prm|Bnd):" /proc/1/status']);
    assert.strictEqual(who.status, 0, who.stderr);
    assert.match(who.stdout, /^70\n/, `Postgres が uid 70 で走っていない:\n${who.stdout}`);
    for (const m of who.stdout.matchAll(/^Cap(Eff|Prm|Bnd):\s*([0-9a-f]+)$/gm)) assert.strictEqual(BigInt(`0x${m[2]}`), 0n, `Postgres の PID 1 が capability を持つ（${m[1]}=${m[2]}）`);
    console.log('  ok  IADR-0697: Postgres の PID 1 は uid 70 で、capability を 1 つも持たない（NET_RAW を含む）');

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
    checkDbUsers(r, sh);
  } finally {
    r.cleanup();
  }
}

/**
 * IADR-0695: ConfigMap postgres-ast-db-users の手順を、Job と同じ形（超利用者・TCP の TLS・値は env から）で流す。
 * 試験の証明書は名前の照合ができない（CN=postgres を 127.0.0.1 で引く）ので sslmode は require（宣言の Job は verify-full。deploy-cloud.test.js が固定）。
 */
function checkDbUsers(r, sh) {
  const values = fs.readFileSync(r.envFile, 'utf8').split('\n').filter((l) => /^(AST_DB_|KEYCLOAK_DB_|POSTGRES_PASSWORD)/.test(l)).map((l) => l.slice(l.indexOf('=') + 1)).filter((v) => v.length >= 6);
  assert.ok(values.length >= 18, `陽性対照: 確かめる値が少ない（${values.length}）`);
  const noValues = (text, what) => { for (const v of values) assert.ok(!text.includes(v), `${what} に値（利用者名かパスワード）が出た`); };
  const JOB_ENV = 'PGHOST=127.0.0.1 PGSSLMODE=require PGDATABASE=postgres PGUSER="$POSTGRES_USER" PGPASSWORD="$POSTGRES_PASSWORD" AST_DB_SHARED_USER="$AST_DB_USER"';
  const job = (stage, script = '/opt/ast-db-users/ast-db-users.sh') => {
    const x = sh(`${JOB_ENV} bash ${script} ${stage}`);
    noValues(`${x.stdout}${x.stderr}`, `${stage} の出力`);
    return x;
  };
  // psql で <利用者>（env の名前）として <DB> に入って SQL を流す。値は env から、SQL は標準入力から（どちらもシェルの文字列・引数に載せない）。
  const psqlIn = (conn, sql) => docker(['exec', '-i', r.name, 'sh', '-c', `${conn} psql -X -q -tA -v ON_ERROR_STOP=1 -f -`], { input: sql });
  const as = (userEnv, passEnv, db, sql) => psqlIn(`PGPASSWORD="$${passEnv}" PGHOST=127.0.0.1 PGSSLMODE=require PGDATABASE=${db} PGUSER="$${userEnv}"`, sql);
  // 超利用者（ソケット）。共有の利用者の名前は env から psql の変数（:'shared'）で読む。
  const asAdmin = (sql) => psqlIn('PGPASSWORD="$POSTGRES_PASSWORD" PGHOST=/var/run/postgresql PGDATABASE=postgres PGUSER="$POSTGRES_USER"', `\\getenv shared AST_DB_USER\n${sql}`);
  const asShared = (db, sql) => as('AST_DB_USER', 'AST_DB_PASSWORD', db, sql);
  const asSvc = (svc, db, sql) => as(envOf('USER', svc), envOf('PASSWORD', svc), db, sql);

  // 稼働中の構成の代わり: 共有の利用者がマイグレーションで作った物（表・連番・スキーマ）を各 DB に置く。
  for (const svc of AST_DB_SERVICES) {
    const x = asShared(dbOf(svc), 'create table "__EFMigrationsHistory"(id text primary key); create table t(id serial primary key, v text); insert into t(v) values (1); create schema wolverine; create table wolverine.envelopes(id int)');
    assert.strictEqual(x.status, 0, `共有の利用者で ${dbOf(svc)} に物を作れない: ${x.stderr}`);
  }

  // 変異 B（先に流す）: 共有の利用者を役割の一員にしないと、移行の間に旧の Pod（共有の利用者）が読めなくなる。
  const script = '/opt/ast-db-users/ast-db-users.sh';
  const src = fs.readFileSync(path.join(path.dirname(r.envFile), 'ast-db-users', 'ast-db-users.sh'), 'utf8');
  const write = (name, text) => {
    const f = path.join(path.dirname(r.envFile), 'ast-db-users', name);
    fs.writeFileSync(f, text, { mode: 0o755 });
    return `/opt/ast-db-users/${name}`;
  };
  const noGrant = src.replace(/^.*GRANT :"u_%s" TO :"shared".*$/m, '');
  assert.notStrictEqual(noGrant, src, '変異の置換元（GRANT … TO shared）が無い（試験が古い）');
  assert.strictEqual(job('apply', write('mut-nogrant.sh', noGrant)).status, 0, '変異 B の apply が通らない（下の確かめの前提）');
  assert.notStrictEqual(asShared('audit_svc', 'select count(*) from t').status, 0, '変異 B: 共有の利用者を役割の一員にしないのに、移行の間も読めた（判定が空振りしている）');
  console.log('  ok  変異: 共有の利用者を役割の一員にしないと、移行の間に旧の利用者が読めなくなる（検出する）');

  // apply（本物）
  const a1 = job('apply');
  assert.strictEqual(a1.status, 0, `apply が失敗した:\n${a1.stdout}${a1.stderr}`);
  assert.match(a1.stdout, /確かめた: 7 つのサービスの利用者は自分の DB と中の物だけの OWNER/);
  for (const svc of AST_DB_SERVICES) {
    const own = asSvc(svc, dbOf(svc), "insert into t(v) values (2); insert into wolverine.envelopes values (1); select count(*) from t");
    assert.strictEqual(own.status, 0, `${svc} の利用者が自分の DB の物を読み書きできない: ${own.stderr}`);
    assert.strictEqual(own.stdout.trim().split('\n').pop(), '2');
    const ddl = asSvc(svc, dbOf(svc), 'create table t2(x int); drop table t2; alter table t add column c int; alter table t drop column c');
    assert.strictEqual(ddl.status, 0, `${svc} の利用者が自分の DB でマイグレーション（DDL）を流せない: ${ddl.stderr}`);
    for (const other of [...AST_DB_SERVICES.filter((x) => x !== svc).map(dbOf), 'keycloak', 'postgres', 'template1']) {
      const x = asSvc(svc, other, 'select 1');
      assert.notStrictEqual(x.status, 0, `${svc} の利用者が ${other} に入れた`);
      assert.match(x.stderr, /CONNECT privilege/, `${svc} → ${other} が CONNECT の権限で拒まれていない: ${x.stderr}`);
    }
    const shared = asShared(dbOf(svc), 'insert into t(v) values (3); select count(*) from t');
    assert.strictEqual(shared.status, 0, `移行の間に共有の利用者が ${dbOf(svc)} を読み書きできない: ${shared.stderr}`);
  }
  const kc = as('KEYCLOAK_DB_USER', 'KEYCLOAK_DB_PASSWORD', 'keycloak', 'select 1;');
  assert.strictEqual(kc.status, 0, `Keycloak の利用者が自分の DB に入れなくなった: ${kc.stderr}`);
  const a2 = job('apply');
  assert.strictEqual(a2.status, 0, `2 回目の apply が失敗した（冪等でない）:\n${a2.stdout}${a2.stderr}`);
  console.log('  ok  apply: 7 人を作り、各 DB と中の物の OWNER を移し、各利用者は自分の DB にだけ入れて DDL も流せる。移行の間も共有の利用者は読み書きでき、Keycloak は変わらない。2 回目も通る');

  // 変異 A: CONNECT を PUBLIC から外さないと、確かめ（CONNECT の表）が赤になる。PUBLIC に戻した DB の上で流す。
  const noRevoke = src.replace(/^.*REVOKE CONNECT, TEMPORARY ON DATABASE.*$/m, '');
  assert.notStrictEqual(noRevoke, src, '変異の置換元（REVOKE CONNECT）が無い（試験が古い）');
  assert.strictEqual(asAdmin('GRANT CONNECT ON DATABASE configuration_svc TO PUBLIC;').status, 0);
  const ma = job('apply', write('mut-norevoke.sh', noRevoke));
  assert.notStrictEqual(ma.status, 0, '変異 A: CONNECT を外さないのに apply が通った（確かめが空振りしている）');
  assert.match(ma.stderr, /NG: .* が configuration_svc に CONNECT できる/);
  assert.strictEqual(job('apply').status, 0, '変異 A の後の apply（本物）が通らない');
  console.log('  ok  変異: PUBLIC の CONNECT を外さないと、確かめが他の DB への CONNECT を検出して失敗する');

  // drop-shared: 共有の利用者の接続が残っていれば止める（消さない）。
  const hold = docker(['exec', '-d', r.name, 'sh', '-c', 'PGPASSWORD="$AST_DB_PASSWORD" PGHOST=127.0.0.1 PGSSLMODE=require PGDATABASE=audit_svc PGUSER="$AST_DB_USER" psql -X -c "select pg_sleep(20)"']);
  assert.strictEqual(hold.status, 0);
  const deadline = Date.now() + 10000;
  for (;;) {
    const n = asAdmin("select count(*) from pg_stat_activity where usename = :'shared';");
    if (n.stdout.trim() === '1') break;
    assert.ok(Date.now() < deadline, '共有の利用者の接続を張れない（試験の前提）');
    spawnSync('sleep', ['0.5']);
  }
  const busy = job('drop-shared');
  assert.strictEqual(busy.status, 4, `共有の利用者の接続が残るのに drop-shared が 4 で止まらない（${busy.status}）:\n${busy.stderr}`);
  assert.match(busy.stderr, /共有の利用者の接続が 1 本ある/);
  asAdmin("select pg_terminate_backend(pid) from pg_stat_activity where usename = :'shared';");
  assert.strictEqual(asShared('audit_svc', 'select 1').status, 0, '止まった drop-shared の後に、共有の利用者が入れない（消さずに止まっていない）');

  // drop-shared: 移行の間に共有の利用者が作った物も各サービスの利用者へ移して、共有の利用者を消す。
  assert.strictEqual(asShared('report_svc', 'create table stray(x int)').status, 0);
  const d1 = job('drop-shared');
  assert.strictEqual(d1.status, 0, `drop-shared が失敗した:\n${d1.stdout}${d1.stderr}`);
  assert.match(d1.stdout, /消した: 共有の利用者/);
  const gone = asShared('audit_svc', 'select 1');
  assert.notStrictEqual(gone.status, 0, 'drop-shared の後も共有の利用者で入れる');
  assert.match(gone.stderr, /does not exist|authentication failed/);
  assert.strictEqual(asSvc('report', 'report_svc', 'insert into stray values (1); select count(*) from t').status, 0, 'drop-shared の後に、移行の間に作られた物をサービスの利用者が使えない');
  for (const svc of AST_DB_SERVICES) assert.strictEqual(asSvc(svc, dbOf(svc), 'select count(*) from t').status, 0, `drop-shared の後に ${svc} の利用者が自分の DB を読めない`);
  const d2 = job('drop-shared');
  assert.strictEqual(d2.status, 0, `2 回目の drop-shared が失敗した（冪等でない）:\n${d2.stderr}`);
  assert.match(d2.stdout, /共有の利用者は既に無い/);
  assert.strictEqual(job('apply').status, 0, 'drop-shared の後の apply が通らない');

  // 値はサーバのログにも出ない（パスワードを含む文を記録しない）。
  const logs = docker(['logs', r.name]);
  const logText = `${logs.stdout}${logs.stderr}`;
  noValues(logText.replace(/(user|role) "[^"]*"/gi, ''), 'Postgres のログ');
  console.log('  ok  drop-shared: 共有の利用者の接続が残れば 4 で止まり（消さない）、無ければ残りの物を移して消す。2 回目も通る。値はログに出ない');
}

function checkMutation() {
  // 変異: initContainer の command から mode を整える行を外す（写した 0440 のまま＝group が読める鍵になる）。
  const decl = loadDeclaration();
  const script = String(decl.init.command[decl.init.command.length - 1]);
  const mutated = script.split('\n').filter((l) => !/^\s*(chown|chmod)\b/.test(l)).join('\n');
  assert.notStrictEqual(mutated, script, '変異の置換元（chown・chmod の行）が無い（試験が古い）');
  decl.init = { ...decl.init, command: [...decl.init.command.slice(0, -1), mutated] };
  const r = start(decl, 'mut');
  try {
    assert.ok(!r.ready, '鍵の mode を整えないのに起動した（判定が空振りしている）');
    assert.match(r.logs, /private key file .* has group or world access|private key file .* must be owned by/, `鍵の検査で止まっていない:\n${tailOf(r.logs)}`);
    console.log('  ok  変異: 鍵の mode を整えないと起動しない（Postgres の鍵の検査で止まる）');
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
