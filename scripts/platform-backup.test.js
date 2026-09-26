#!/usr/bin/env node
'use strict';
/*
 * platform-backup.test.js
 * NFR-21, NFR-05, NFR-18, ADR-0002, ADR-0008, IADR-0471 (#1560):
 * platform-infra の日次バックアップ（deploy/local/platform-backup）の**マニフェストの約束**を、kustomize の描画結果で固定する。
 * 本体スクリプトの分岐は deploy/local/platform-backup/script/backup.test.sh、リストア試験は
 * scripts/backup-restore-drill.sh --self-test が持つ。ここはその 2 つが見られない「配備の形」を見る。
 *
 * 固定するもの:
 *   1. 🔴 既定の配備（永続化 overlay）に CronJob が出る。Postgres は infra-persistence、Vault は vault-persistence。
 *      永続化しない base（PERSIST=0）には出さない（PVC の無い配備で Vault の回を描くと Pod が Pending のまま残る）。
 *   2. 時刻が米国市場の時間帯（JST 22:30〜05:00）を避け、取りこぼしの後追いもそこへ食い込まない。Forbid。
 *   3. 🔴 資格情報は secretKeyRef（Postgres 本体と同じ Secret / キー）。PASSWORD / TOKEN / SECRET の名前に value を書かない。
 *   4. 🔴 保管先は hostPath 2 本（/mnt/c と /mnt/e）で、どちらもコンテナにマウントされ、スクリプトの保管先一覧と一致する。
 *   5. 🔴 読むだけのもの（Vault の PVC・スクリプト・受取人）は読み取り専用でマウントする。PVC は本体の PVC 名と一致する。
 *   6. 🔴 受取人（公開鍵）の ConfigMap は optional で、**どの kustomization も描かない**（起動器の再実行で占位へ戻さない）。
 *   7. pg_dump のイメージを本体の Postgres と揃える。失敗を再試行で上書きしない（backoffLimit 0）。
 *   8. 🔴 秘密鍵（AGE-SECRET-KEY-）が deploy/ のどこにも無い。
 * 判定は純関数にし、変異を当てて落ちることも同じ試験の中で確かめる（見ているつもりで見ていない、を防ぐ）。
 *
 * 描画は `kubectl kustomize`（オフライン。クラスタに接続しない）。kubectl が無ければ失敗にする（fail-closed。
 * CI の static-checks は check-deploy-manifests と同じく kubectl を持つ）。描画結果は kustomize がキーを並べ替えた
 * ブロック形式の YAML なので、字面の順序に頼らず、字下げで木に組んでから見る（下の parseMap / parseList）。
 * 外部依存ゼロ（Node 標準モジュール ＋ kubectl）。実行: node scripts/platform-backup.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const read = (...p) => fs.readFileSync(path.join(REPO_ROOT, ...p), 'utf8');

function render(dir) {
  const r = spawnSync('kubectl', ['kustomize', dir], { cwd: REPO_ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  if (r.error || r.status !== 0) {
    throw new Error(
      `kubectl kustomize ${dir} が失敗した（kubectl が PATH に無い可能性。オフライン描画に要る）:\n${r.stderr || r.error}`,
    );
  }
  return r.stdout;
}

// ---------------------------------------------------------------- 最小のブロック YAML 読み（描画結果専用）

const indentOf = (l) => l.search(/\S/);
const unquote = (v) => {
  const s = v.trim();
  if (/^".*"$/.test(s) || /^'.*'$/.test(s)) return s.slice(1, -1);
  return s;
};

/** 同じ字下げに並ぶキーの写像。値はスカラー（文字列）か、子の行の配列。 */
function parseMap(lines) {
  const rows = lines.filter((l) => l.trim() && !/^\s*#/.test(l));
  const out = {};
  if (rows.length === 0) return out;
  const col = Math.min(...rows.map(indentOf));
  for (let i = 0; i < rows.length; ) {
    const m = /^\s*([A-Za-z0-9_.\/-]+):(?:\s+(.*))?$/.exec(rows[i]);
    if (indentOf(rows[i]) !== col || !m) {
      i += 1;
      continue;
    }
    const child = [];
    let j = i + 1;
    while (j < rows.length && (indentOf(rows[j]) > col || (indentOf(rows[j]) === col && /^\s*- /.test(rows[j])))) {
      child.push(rows[j]);
      j += 1;
    }
    out[m[1]] = m[2] !== undefined && m[2] !== '' ? unquote(m[2]) : child;
    i = j;
  }
  return out;
}

/** ブロック形式の列。各要素を写像として返す（`- ` を字下げに置き換えてから読む）。 */
function parseList(lines) {
  const items = [];
  let dash = null;
  let cur = null;
  for (const l of lines || []) {
    if (!l.trim()) continue;
    const ind = indentOf(l);
    if (/^\s*- /.test(l) && (dash === null || ind === dash)) {
      dash = ind;
      cur = [`${' '.repeat(ind + 2)}${l.slice(ind + 2)}`];
      items.push(cur);
    } else if (cur) cur.push(l);
  }
  return items.map(parseMap);
}

const sub = (node, ...keys) => keys.reduce((n, k) => (n && Array.isArray(n[k]) ? parseMap(n[k]) : n && n[k]), node);

/** 描画結果を文書ごとに分ける。CronJob は判定に要る部分を組み立てておく。 */
function docsOf(yaml) {
  return yaml
    .split(/^---\s*$/m)
    .map((body) => {
      const top = parseMap(body.split('\n'));
      const meta = sub(top, 'metadata') || {};
      return { kind: top.kind || '', name: meta.name || '', namespace: meta.namespace || '', top, body };
    })
    .filter((d) => d.kind);
}
const find = (docs, kind, name) => docs.find((d) => d.kind === kind && d.name === name);

/** CronJob を判定しやすい形へ。変異はこの形に当てる。 */
function cronJobView(doc) {
  const spec = sub(doc.top, 'spec');
  const jobSpec = sub(spec, 'jobTemplate', 'spec');
  const podSpec = sub(jobSpec, 'template', 'spec');
  const container = parseList(podSpec.containers)[0];
  const env = parseList(container.env).map((e) => ({
    name: e.name,
    value: e.value,
    secretKeyRef: e.valueFrom ? sub(parseMap(e.valueFrom), 'secretKeyRef') || null : null,
  }));
  const mounts = parseList(container.volumeMounts).map((m) => ({ name: m.name, mountPath: m.mountPath, readOnly: m.readOnly === 'true' }));
  const volumes = parseList(podSpec.volumes).map((v) => ({
    name: v.name,
    hostPath: v.hostPath ? parseMap(v.hostPath) : null,
    configMap: v.configMap ? parseMap(v.configMap) : null,
    pvc: v.persistentVolumeClaim ? parseMap(v.persistentVolumeClaim) : null,
  }));
  return {
    name: doc.name,
    namespace: doc.namespace,
    schedule: spec.schedule,
    timeZone: spec.timeZone,
    concurrencyPolicy: spec.concurrencyPolicy,
    startingDeadlineSeconds: spec.startingDeadlineSeconds,
    backoffLimit: jobSpec.backoffLimit,
    restartPolicy: podSpec.restartPolicy,
    automountServiceAccountToken: podSpec.automountServiceAccountToken,
    image: container.image,
    env,
    mounts,
    volumes,
  };
}
const clone = (v) => JSON.parse(JSON.stringify(v));
const envOf = (cj, name) => cj.env.find((e) => e.name === name);
const mountOf = (cj, name) => cj.mounts.find((m) => m.name === name);

// ---------------------------------------------------------------- 判定（純関数）

const MARKET_OPEN_MIN = 22 * 60 + 30; // JST 22:30（米国市場の開場。夏時間の値。冬時間は 23:30 でさらに遅い）
const MARKET_CLOSE_MIN = 5 * 60; // JST 05:00（夏時間の閉場）

/** スケジュールが市場の時間帯を避け、後追いの締切も開場より前であること。 */
function checkSchedule(cj) {
  const errors = [];
  const sched = /^(\d+) (\d+) \* \* \*$/.exec(cj.schedule || '');
  if (!sched) return [`schedule（${cj.schedule}）が「分 時 * * *」の日次の形でない`];
  if (cj.timeZone !== 'Asia/Tokyo') errors.push('timeZone が Asia/Tokyo でない（時刻を JST で読めない）');
  if (cj.concurrencyPolicy !== 'Forbid') errors.push('concurrencyPolicy が Forbid でない');
  const start = Number(sched[2]) * 60 + Number(sched[1]);
  if (start < MARKET_CLOSE_MIN || start >= MARKET_OPEN_MIN) errors.push(`開始 ${sched[2]}:${sched[1]} JST が米国市場の時間帯にある`);
  if (!/^\d+$/.test(cj.startingDeadlineSeconds || '')) errors.push('startingDeadlineSeconds が無い（取りこぼしの後追いが時間帯を選ばない）');
  else if (start + Math.ceil(Number(cj.startingDeadlineSeconds) / 60) > MARKET_OPEN_MIN)
    errors.push(`後追いの締切（開始 + ${cj.startingDeadlineSeconds} 秒）が JST 22:30 を越える`);
  return errors;
}

/** env のうち、秘密を思わせる名前に value（平文）を書いたもの。 */
const secretLiterals = (cj) =>
  cj.env.filter((e) => /PASS|TOKEN|SECRET|PRIVATE|IDENTITY/i.test(e.name) && e.value !== undefined).map((e) => e.name);

// ---------------------------------------------------------------- 入力

const R = {
  infraPersist: render('deploy/local/infra-persistence'),
  infraBase: render('deploy/local/infra'),
  vaultPersist: render('deploy/local/vault-persistence'),
  vaultBase: render('deploy/local/vault'),
  backupPostgres: render('deploy/local/platform-backup/postgres'),
  backupVault: render('deploy/local/platform-backup/vault'),
  backupScript: render('deploy/local/platform-backup/script'),
};
const PG_DOC = find(docsOf(R.infraPersist), 'CronJob', 'platform-backup-postgres');
const VA_DOC = find(docsOf(R.vaultPersist), 'CronJob', 'platform-backup-vault');
const INFRA_DOCS = docsOf(R.infraPersist);
const VAULT_DOCS = docsOf(R.vaultPersist);
const BACKUP_SH = read('deploy', 'local', 'platform-backup', 'script', 'backup.sh');

let passed = 0;
const ok = (name, fn) => {
  fn();
  passed += 1;
  console.log(`  ok  ${name}`);
};

// ---------------------------------------------------------------- 1. 既定の配備に出る

ok('🔴 1. 永続化 overlay（既定）に 2 つの CronJob と本体スクリプトが出る', () => {
  assert.ok(PG_DOC, 'deploy/local/infra-persistence の描画に CronJob platform-backup-postgres が無い（既定の配備でバックアップが動かない）');
  assert.ok(VA_DOC, 'deploy/local/vault-persistence の描画に CronJob platform-backup-vault が無い');
  assert.ok(find(INFRA_DOCS, 'ConfigMap', 'platform-backup-script'), 'infra-persistence に本体スクリプトの ConfigMap が無い');
  assert.ok(find(VAULT_DOCS, 'ConfigMap', 'platform-backup-script'), 'vault-persistence に本体スクリプトの ConfigMap が無い');
  for (const d of [PG_DOC, VA_DOC]) assert.strictEqual(d.namespace, 'platform-infra', `${d.name} が platform-infra に無い`);
});

const PG = cronJobView(PG_DOC);
const VA = cronJobView(VA_DOC);

ok('1. 永続化しない base（PERSIST=0）には出さない（PVC の無い配備で Pending を残さない）', () => {
  assert.ok(!/platform-backup/.test(R.infraBase), 'deploy/local/infra（emptyDir の使い捨て）にバックアップが混ざった');
  assert.ok(!/platform-backup/.test(R.vaultBase), 'deploy/local/vault（-dev・PVC なし）にバックアップが混ざった');
});

ok('1. 本体スクリプトの ConfigMap はリポジトリの backup.sh を運ぶ', () => {
  // kustomize はタブを含む本文を二重引用の文字列で描き、空白で折り返す —— 空白を含まない印だけで照合する。
  for (const token of ['RUN_NAME_RE=', 'AGE_RECIPIENT_RE=', 'check_recipients()', 'remove_flat_dir()', 'prune_list()']) {
    assert.ok(BACKUP_SH.includes(token), `backup.sh から ${token} が消えた（試験の前提）`);
    assert.ok(R.backupScript.includes(token), `描画された ConfigMap に ${token} が無い`);
  }
  for (const cj of [PG, VA]) {
    const script = cj.volumes.find((v) => v.name === 'script');
    assert.strictEqual(script && script.configMap && script.configMap.name, 'platform-backup-script', `${cj.name}: 本体スクリプトの ConfigMap をマウントしていない`);
    assert.strictEqual(envOf(cj, 'BACKUP_KIND').value, cj.name.replace('platform-backup-', ''), `${cj.name}: BACKUP_KIND が名前と合わない`);
  }
});

// ---------------------------------------------------------------- 2. 時刻

ok('2. 時刻は米国市場の時間帯（JST 22:30〜05:00）を避け、後追いも食い込まない。Forbid', () => {
  for (const cj of [PG, VA]) assert.deepStrictEqual(checkSchedule(cj), [], `${cj.name}: ${checkSchedule(cj).join(' / ')}`);
  // 変異: 市場の時間帯（23:00）へずらす／後追いを伸ばす／Forbid を外す／timeZone を外す、のどれでも落ちる。
  assert.ok(checkSchedule({ ...PG, schedule: '0 23 * * *' }).length > 0, '23:00 開始を見逃した');
  assert.ok(checkSchedule({ ...PG, schedule: '30 4 * * *' }).length > 0, '04:30 開始を見逃した');
  assert.ok(checkSchedule({ ...PG, startingDeadlineSeconds: '86400' }).length > 0, '後追いの越境を見逃した');
  assert.ok(checkSchedule({ ...PG, concurrencyPolicy: 'Allow' }).length > 0, 'Allow を見逃した');
  assert.ok(checkSchedule({ ...PG, timeZone: undefined }).length > 0, 'timeZone の欠落を見逃した');
});

// ---------------------------------------------------------------- 3. 資格情報

ok('🔴 3. PGPASSWORD は Postgres 本体と同じ Secret / キーの secretKeyRef で渡す', () => {
  const pg = envOf(PG, 'PGPASSWORD');
  assert.ok(pg && pg.secretKeyRef, 'PGPASSWORD が secretKeyRef で渡されていない');
  assert.strictEqual(pg.value, undefined, 'PGPASSWORD に平文の value がある');
  const server = find(INFRA_DOCS, 'Deployment', 'postgres');
  const serverPod = sub(server.top, 'spec', 'template', 'spec');
  const serverEnv = parseList(parseList(serverPod.containers)[0].env).find((e) => e.name === 'POSTGRES_PASSWORD');
  const serverRef = sub(parseMap(serverEnv.valueFrom), 'secretKeyRef');
  // 読み取りが壊れて両方 undefined になると、下の一致は空振りで通る。値が在ることを先に確かめる。
  assert.ok(serverRef && serverRef.name && serverRef.key, '本体の POSTGRES_PASSWORD の secretKeyRef を読めない（試験の前提）');
  assert.deepStrictEqual(
    { name: pg.secretKeyRef.name, key: pg.secretKeyRef.key },
    { name: serverRef.name, key: serverRef.key },
    'バックアップと本体で参照する Secret / キーが違う',
  );
  // 変異: 平文の value に置き換えると秘密の名前に value が立つ。
  const mutated = clone(PG);
  envOf(mutated, 'PGPASSWORD').value = 'postgres';
  assert.deepStrictEqual(secretLiterals(mutated), ['PGPASSWORD'], '平文の value を見逃した');
});

ok('🔴 3. PASSWORD / TOKEN / SECRET を思わせる env に平文の value を書かない（2 つの CronJob とも）', () => {
  for (const cj of [PG, VA]) assert.deepStrictEqual(secretLiterals(cj), [], `${cj.name} に平文の秘密: ${secretLiterals(cj)}`);
  assert.ok(!envOf(VA, 'VAULT_TOKEN'), 'Vault の回が Vault のトークンを受け取っている（ファイルを写すだけで要らない）');
});

// ---------------------------------------------------------------- 4. 保管先

function checkTargets(cj) {
  const errors = [];
  const hp = cj.volumes.filter((v) => v.hostPath);
  if (hp.length !== 2) return [`hostPath が 2 本でない（${hp.length}）`];
  const drives = hp.map((v) => v.hostPath.path.split('/').slice(0, 3).join('/')).sort();
  if (drives.join(',') !== '/mnt/c,/mnt/e') errors.push(`C: と E: の 2 か所でない（${drives}）`);
  if (new Set(hp.map((v) => v.hostPath.path)).size !== 2) errors.push('2 本が同じパスを指している');
  const targets = ((envOf(cj, 'BACKUP_TARGETS') || {}).value || '').split(/\s+/).filter(Boolean).sort();
  const mounted = hp.map((v) => (mountOf(cj, v.name) || {}).mountPath).sort();
  if (!mounted.every(Boolean)) errors.push('hostPath がコンテナにマウントされていない');
  if (targets.join(',') !== mounted.join(',')) errors.push(`BACKUP_TARGETS（${targets}）とマウント先（${mounted}）が違う`);
  for (const v of hp) {
    // ドライブが外れたときに Pod ごと起動しなくなると、もう片方にも書けない。作らせて、目印で見分ける。
    if (v.hostPath.type !== 'DirectoryOrCreate') errors.push(`${v.name} の型が DirectoryOrCreate でない`);
    if ((mountOf(cj, v.name) || {}).readOnly) errors.push(`保管先 ${v.name} が読み取り専用になっている`);
  }
  return errors;
}

ok('🔴 4. 保管先は hostPath 2 本（/mnt/c と /mnt/e）で、どちらもマウントされ、スクリプトの保管先一覧と一致する', () => {
  for (const cj of [PG, VA]) assert.deepStrictEqual(checkTargets(cj), [], `${cj.name}: ${checkTargets(cj).join(' / ')}`);
  // 変異: 片方を外す／マウントを外す／BACKUP_TARGETS から片方を落とす、のどれでも落ちる。
  const oneDrive = clone(PG);
  oneDrive.volumes = oneDrive.volumes.filter((v) => v.name !== 'target-e');
  assert.ok(checkTargets(oneDrive).length > 0, '保管先 1 本を見逃した');
  const unmounted = clone(PG);
  unmounted.mounts = unmounted.mounts.filter((m) => m.name !== 'target-c');
  assert.ok(checkTargets(unmounted).length > 0, 'マウント漏れを見逃した');
  const narrowed = clone(PG);
  envOf(narrowed, 'BACKUP_TARGETS').value = '/backup/c';
  assert.ok(checkTargets(narrowed).length > 0, 'BACKUP_TARGETS の片落ちを見逃した');
  assert.ok(BACKUP_SH.includes('BACKUP_MARKER_NAME=".platform-backup-target"'), 'スクリプトが目印で保管先を確かめていない');
});

// ---------------------------------------------------------------- 5. 読み取り専用

function checkReadOnlySources(cj) {
  const errors = [];
  for (const name of ['script', 'recipients']) if (!(mountOf(cj, name) || {}).readOnly) errors.push(`${name} が読み取り専用でない`);
  if (cj.name === 'platform-backup-vault') {
    const vol = cj.volumes.find((v) => v.name === 'vault-data');
    if (!(mountOf(cj, 'vault-data') || {}).readOnly) errors.push('Vault のデータが読み取り専用でマウントされていない');
    if (!vol || !vol.pvc || vol.pvc.readOnly !== 'true') errors.push('PVC ボリュームそのものが readOnly: true でない');
  }
  return errors;
}

ok('🔴 5. 読むだけのもの（Vault の PVC・スクリプト・受取人）は読み取り専用でマウントする', () => {
  for (const cj of [PG, VA]) assert.deepStrictEqual(checkReadOnlySources(cj), [], `${cj.name}: ${checkReadOnlySources(cj).join(' / ')}`);
  const vol = VA.volumes.find((v) => v.name === 'vault-data');
  const pvc = find(VAULT_DOCS, 'PersistentVolumeClaim', vol.pvc.claimName);
  assert.ok(pvc, `Vault の回が写す PVC（${vol.pvc.claimName}）が vault-persistence に無い（別のボリュームを写す）`);
  const vaultDeploy = find(VAULT_DOCS, 'Deployment', 'vault');
  assert.ok(vaultDeploy.body.includes(`claimName: ${vol.pvc.claimName}`), 'Vault 本体と別の PVC を写している');
  assert.strictEqual(mountOf(VA, 'vault-data').mountPath, envOf(VA, 'VAULT_DATA_DIR').value, 'VAULT_DATA_DIR とマウント先が違う');
  // 変異: readOnly を外すと落ちる（マウント側・PVC 側のどちらでも）。
  const m1 = clone(VA);
  mountOf(m1, 'vault-data').readOnly = false;
  assert.ok(checkReadOnlySources(m1).length > 0, 'マウントの readOnly: false を見逃した');
  const m2 = clone(VA);
  m2.volumes.find((v) => v.name === 'vault-data').pvc.readOnly = 'false';
  assert.ok(checkReadOnlySources(m2).length > 0, 'PVC の readOnly: false を見逃した');
});

// ---------------------------------------------------------------- 6. 受取人

ok('🔴 6. 受取人の ConfigMap は optional で、どの kustomization も描かない（占位で上書きしない）', () => {
  for (const cj of [PG, VA]) {
    const v = cj.volumes.find((x) => x.name === 'recipients');
    assert.ok(v && v.configMap, `${cj.name}: 受取人のボリュームが無い`);
    assert.strictEqual(v.configMap.name, 'platform-backup-age-recipients', `${cj.name}: 受取人の ConfigMap 名が違う`);
    assert.strictEqual(v.configMap.optional, 'true', `${cj.name}: 受取人の ConfigMap が optional でない（無いと Pod が起動せず、失敗としても見えない）`);
    assert.ok(BACKUP_SH.includes(`${mountOf(cj, 'recipients').mountPath}/recipients.txt`), `${cj.name}: 受取人のマウント先とスクリプトの既定パスが違う`);
  }
  for (const [k, y] of Object.entries(R)) {
    assert.ok(!find(docsOf(y), 'ConfigMap', 'platform-backup-age-recipients'), `${k} が受取人の ConfigMap を描いている（起動器の再実行で上書きされる）`);
  }
});

// ---------------------------------------------------------------- 7. イメージ・再試行

ok('7. pg_dump のイメージは本体の Postgres と同じ。失敗を再試行で上書きしない。SA トークンを持たない', () => {
  const server = find(INFRA_DOCS, 'Deployment', 'postgres');
  const serverImage = parseList(sub(server.top, 'spec', 'template', 'spec').containers)[0].image;
  assert.ok(/^postgres:/.test(serverImage), `本体のイメージを読めない（試験の前提）: ${serverImage}`);
  for (const cj of [PG, VA]) {
    assert.strictEqual(cj.image, serverImage, `${cj.name}: イメージが本体（${serverImage}）と違う（pg_dump のメジャー版がずれる）`);
    assert.strictEqual(cj.backoffLimit, '0', `${cj.name}: backoffLimit が 0 でない（失敗が再試行で上書きされる）`);
    assert.strictEqual(cj.restartPolicy, 'Never', `${cj.name}: restartPolicy が Never でない`);
    assert.strictEqual(cj.automountServiceAccountToken, 'false', `${cj.name}: SA トークンを自動マウントしている（要らない権限）`);
  }
});

// ---------------------------------------------------------------- 8. 秘密鍵

ok('🔴 8. age の秘密鍵（AGE-SECRET-KEY-）が deploy/ のどこにも無い。例示ファイルは占位のまま', () => {
  const hits = [];
  const walk = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, e.name);
      if (e.isDirectory()) walk(full);
      else if (/AGE-SECRET-KEY-1/i.test(fs.readFileSync(full, 'latin1'))) hits.push(path.relative(REPO_ROOT, full));
    }
  };
  walk(path.join(REPO_ROOT, 'deploy'));
  assert.deepStrictEqual(hits, [], `秘密鍵らしき値: ${hits.join(', ')}`);
  const example = read('deploy', 'local', 'platform-backup', 'age-recipients.example.txt');
  assert.ok(!/^age1[0-9a-z]{58}\s*$/m.test(example), '例示ファイルに実在の形をした公開鍵が入っている（占位のままにする）');
});

console.log(`[platform-backup.test] OK: ${passed} 件`);
