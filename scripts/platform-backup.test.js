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
 *   7. pg_dump のメジャー版を本体の Postgres と揃える（同梱イメージの FROM で）。失敗を再試行で上書きしない（backoffLimit 0）。
 *   8. 🔴 秘密鍵（AGE-SECRET-KEY-）が deploy/ のどこにも無い。
 *   9. 🔴 ［#1564］age は digest 固定のベースへ版・sha256 で同梱する（deploy/local/platform-backup/image/Dockerfile）。
 *      タグは版から作り、Dockerfile・k8s-local-images.sh の LOCAL_ONLY_IMAGES・2 つの CronJob で揃える。IfNotPresent。
 *      実行時に apk を呼ばない（BACKUP_AGE_INSTALL も撤去）。CI（images.yml）がビルドする。
 *  11. 🔴 ［#1868 / IADR-0548］Vault の回は raft の整合したスナップショットを k8s auth で取る。専用の SA（RBAC なし・自動マウントなし）の
 *      projected token、role は default ポリシーなし・1 回きり、ポリシーは sys/storage/raft/snapshot の read だけ。root トークン・
 *      Secret vault-dev-token を渡さない。作業場所はメモリの emptyDir。HTTP は curl / wget の引数でトークンを渡さない。
 *  10. 🔴 ［#1709 / IADR-0489］ベースの取得元は匿名の取得にチャレンジを返さないミラー（mirror.gcr.io/library）。
 *      docker.io・ghcr.io・public.ecr.aws（と取得元を書かない FROM＝docker.io）は匿名でも 401 の Bearer チャレンジを返し、
 *      その応答でビルダーが資格情報ヘルパーを呼ぶ。稼働 PoC ではヘルパーが落ち、このイメージを作れなかった。
 *      FROM の `${BASE_REGISTRY}` は Dockerfile の ARG の既定で解いてから見る。
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
    emptyDir: v.emptyDir ? parseMap(v.emptyDir) : null,
    // IADR-0548: projected の serviceAccountToken（k8s auth の名乗り）。
    saToken: v.projected
      ? parseList(parseMap(v.projected).sources || [])
          .map((src) => (src.serviceAccountToken ? parseMap(src.serviceAccountToken) : null))
          .find(Boolean) || null
      : null,
  }));
  const securityContext = container.securityContext ? parseMap(container.securityContext) : {};
  const seccomp = securityContext.seccompProfile ? parseMap(securityContext.seccompProfile) : {};
  return {
    name: doc.name,
    namespace: doc.namespace,
    allowPrivilegeEscalation: securityContext.allowPrivilegeEscalation,
    seccompType: seccomp.type,
    schedule: spec.schedule,
    timeZone: spec.timeZone,
    concurrencyPolicy: spec.concurrencyPolicy,
    startingDeadlineSeconds: spec.startingDeadlineSeconds,
    backoffLimit: jobSpec.backoffLimit,
    restartPolicy: podSpec.restartPolicy,
    automountServiceAccountToken: podSpec.automountServiceAccountToken,
    serviceAccountName: podSpec.serviceAccountName,
    image: container.image,
    imagePullPolicy: container.imagePullPolicy,
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

// ---------------------------------------------------------------- イメージ（#1564）

const BACKUP_DOCKERFILE = 'deploy/local/platform-backup/image/Dockerfile';
// #1709 / IADR-0489: 匿名の manifest の取得に 200 を返す（Bearer チャレンジを返さない）ことを実測した取得元。
// 増やすときは、匿名の GET /v2/library/postgres/manifests/<digest> が 401 にならないことを確かめてから足す。
const NO_CHALLENGE_BASE_REGISTRIES = ['mirror.gcr.io/library'];

/** Dockerfile・k8s-local-images.sh・images.yml・backup.sh・2 つの CronJob から、イメージの約束に要る事実を抜き出す。 */
function imageFacts({ dockerfile, imagesSh, imagesYml, backupSh, cronJobs }) {
  const argIn = (text, name) => (new RegExp(`^ARG\\s+${name}=(\\S+)`, 'm').exec(text) || [])[1];
  const arg = (name) => argIn(dockerfile, name);
  // #1709: FROM の ${名前} / $名前 は ARG の既定で解く（解けない名前はそのまま残し、下の検査で落とす）。
  // FROM で使える ARG は最初の FROM より前のものだけ（後ろの ARG は Docker が FROM で展開しない。独立監査）。
  const preamble = dockerfile.split(/^FROM\s/m)[0];
  const rawFrom = (/^FROM\s+(\S+)/m.exec(dockerfile) || [])[1] || '';
  const from = rawFrom.replace(/\$\{(\w+)\}|\$(\w+)/g, (m, a, b) => argIn(preamble, a || b) ?? m);
  const block = (/\nLOCAL_ONLY_IMAGES=\(([\s\S]*?)\n\)/.exec(imagesSh) || [])[1] || '';
  const localOnly = [...block.matchAll(/"([^"|]+)\|([^"|]+)\|([^"|]+)"/g)].map((m) => ({ ref: m[1], context: m[2], dockerfile: m[3] }));
  // 🔴 注記（# 行）は除いて、実行される行だけで apk の呼び出しを探す（撤去の経緯を書いた注記に語が残る）。
  const codeLines = backupSh.split('\n').filter((l) => !/^\s*#/.test(l));
  return {
    from,
    ageVersion: arg('AGE_VERSION'),
    ageSha: { x86_64: arg('AGE_APK_SHA256_X86_64'), aarch64: arg('AGE_APK_SHA256_AARCH64') },
    localOnly,
    // 注記の中の言及ではなく、実際の `docker build -f <Dockerfile>` の行（YAML の run: 値）で見る。
    ciBuildsDockerfile: imagesYml
      .split('\n')
      .filter((l) => !/^\s*#/.test(l))
      .some((l) => new RegExp(`\\bdocker build\\b.*\\s-f\\s+${BACKUP_DOCKERFILE.replace(/[./]/g, '\\$&')}(\\s|$)`).test(l)),
    scriptCallsApk: codeLines.some((l) => /(^|[\s;&|(])apk\s/.test(l)),
    scriptReadsInstallEnv: codeLines.some((l) => l.includes('BACKUP_AGE_INSTALL')),
    cronJobs: cronJobs.map((cj) => ({
      name: cj.name,
      image: cj.image,
      imagePullPolicy: cj.imagePullPolicy,
      envNames: cj.env.map((e) => e.name),
    })),
  };
}

/** 同梱イメージの約束: digest 固定・PG のメジャー版・age の版と sha256・タグの 3 か所一致・IfNotPresent・実行時の apk なし。 */
function checkBackupImage(f, serverMajor) {
  const errors = [];
  const m = /^(?:(\S+)\/)?postgres:((\d+)\.(\d+))-alpine[\d.]*@sha256:[0-9a-f]{64}$/.exec(f.from);
  const from = m && [m[0], m[2], m[3]];
  if (!from) {
    errors.push(`Dockerfile の FROM（${f.from}）が postgres:<メジャー>.<マイナー>-alpine… を digest（@sha256:）で固定していない`);
  } else if (from[2] !== String(serverMajor)) {
    errors.push(`Dockerfile の PG のメジャー版 ${from[2]} が本体 ${serverMajor} と違う（pg_dump が本体を写せない）`);
  }
  // 10. #1709: 取得元は匿名の取得にチャレンジを返さないミラーだけ（取得元を書かない FROM は docker.io）。
  if (m && !NO_CHALLENGE_BASE_REGISTRIES.includes(m[1] || 'docker.io/library')) {
    errors.push(`Dockerfile のベースの取得元（${m[1] || 'docker.io/library（省略）'}）が ${NO_CHALLENGE_BASE_REGISTRIES.join(' / ')} でない（匿名でも 401 のチャレンジを返すレジストリは資格情報ヘルパーを呼び、稼働 PoC ではそこで落ちる）`);
  }
  if (!/^\d+\.\d+\.\d+-r\d+$/.test(f.ageVersion || '')) errors.push(`age の版（${f.ageVersion}）が <版>-r<N> で固定されていない`);
  for (const [arch, sum] of Object.entries(f.ageSha)) {
    if (!/^[0-9a-f]{64}$/.test(sum || '')) errors.push(`age のパッケージの sha256（${arch}）が無い`);
  }
  const entries = f.localOnly.filter((e) => e.ref.startsWith('platform-backup:'));
  if (entries.length !== 1) {
    errors.push(`k8s-local-images.sh の LOCAL_ONLY_IMAGES に platform-backup がちょうど 1 つ無い（${entries.length} 件。ビルドされない）`);
    return errors;
  }
  const e = entries[0];
  if (`${e.context.replace(/\/$/, '')}/${e.dockerfile}` !== BACKUP_DOCKERFILE) {
    errors.push(`LOCAL_ONLY_IMAGES の platform-backup が ${BACKUP_DOCKERFILE} を指していない（${e.context}/${e.dockerfile}）`);
  }
  if (from) {
    const want = `platform-backup:pg${from[1]}-age${f.ageVersion}`;
    if (e.ref !== want) errors.push(`タグ ${e.ref} が Dockerfile の版から作ったタグ ${want} と違う（版を上げてタグを据え置くと古いイメージが使われ続ける）`);
  }
  if (/:latest$/.test(e.ref)) errors.push('タグが :latest（IfNotPresent では上げても入れ替わらない）');
  for (const cj of f.cronJobs) {
    if (cj.image !== `k3d-local/${e.ref}`) errors.push(`${cj.name}: イメージ ${cj.image} が k3d-local/${e.ref} と違う`);
    if (cj.imagePullPolicy !== 'IfNotPresent') errors.push(`${cj.name}: imagePullPolicy が IfNotPresent でない（${cj.imagePullPolicy}。レジストリに無いので pull すると起動しない）`);
    if (cj.envNames.includes('BACKUP_AGE_INSTALL')) errors.push(`${cj.name}: env に BACKUP_AGE_INSTALL が残っている（実行時の apk は撤去した）`);
  }
  if (f.scriptCallsApk) errors.push('backup.sh が apk を呼んでいる（実行時にパッケージを入れない）');
  if (f.scriptReadsInstallEnv) errors.push('backup.sh が BACKUP_AGE_INSTALL を読んでいる（退避路としても残さない）');
  if (!f.ciBuildsDockerfile) errors.push(`images.yml が ${BACKUP_DOCKERFILE} をビルドしていない（CI でビルド可否を見ていない）`);
  return errors;
}

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
const IMAGE_FACTS = imageFacts({
  dockerfile: read(...BACKUP_DOCKERFILE.split('/')),
  imagesSh: read('scripts', 'k8s-local-images.sh'),
  imagesYml: read('.github', 'workflows', 'images.yml'),
  backupSh: BACKUP_SH,
  cronJobs: [PG, VA],
});

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
  // IADR-0548: トークンは k8s auth で Job の中で得る（最小のポリシー・1 回きり）。root トークンを env で渡さない。
  assert.ok(!envOf(VA, 'VAULT_TOKEN'), 'Vault の回が Vault のトークンを env で受け取っている（k8s auth で得る）');
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

ok('7. pg_dump のメジャー版は本体の Postgres と同じ（同梱イメージの FROM で）。失敗を再試行で上書きしない。SA トークンを持たない', () => {
  // ［2026-09-26 / #1564］CronJob は本体のイメージそのものではなく、age を同梱したローカルイメージで動く。
  // 揃えるべきは pg_dump のメジャー版であり、それは Dockerfile の FROM が決める。
  const server = find(INFRA_DOCS, 'Deployment', 'postgres');
  const serverImage = parseList(sub(server.top, 'spec', 'template', 'spec').containers)[0].image;
  const serverMajor = /^postgres:(\d+)/.exec(serverImage);
  assert.ok(serverMajor, `本体のイメージを読めない（試験の前提）: ${serverImage}`);
  assert.deepStrictEqual(checkBackupImage(IMAGE_FACTS, serverMajor[1]), [], checkBackupImage(IMAGE_FACTS, serverMajor[1]).join(' / '));
  for (const cj of [PG, VA]) {
    assert.strictEqual(cj.backoffLimit, '0', `${cj.name}: backoffLimit が 0 でない（失敗が再試行で上書きされる）`);
    assert.strictEqual(cj.restartPolicy, 'Never', `${cj.name}: restartPolicy が Never でない`);
    assert.strictEqual(cj.automountServiceAccountToken, 'false', `${cj.name}: SA トークンを自動マウントしている（要らない権限）`);
    assert.strictEqual(cj.allowPrivilegeEscalation, 'false', `${cj.name}: allowPrivilegeEscalation が false でない`);
    // capabilities の削減は稼働クラスタでの実測待ち（IADR-0471 のフォローアップ）。seccomp は今ここで固定する。
    assert.strictEqual(cj.seccompType, 'RuntimeDefault', `${cj.name}: seccompProfile が RuntimeDefault でない`);
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

// ---------------------------------------------------------------- 9. イメージ（#1564）

ok('🔴 9. / 10. age は digest 固定のベースへ版・sha256 で同梱し、タグは版から作って 3 か所で揃え、実行時に apk を呼ばない。ベースはチャレンジを返さない取得元から取る', () => {
  assert.deepStrictEqual(checkBackupImage(IMAGE_FACTS, '16'), [], checkBackupImage(IMAGE_FACTS, '16').join(' / '));
  const mut = (fn) => {
    const f = clone(IMAGE_FACTS);
    fn(f);
    return checkBackupImage(f, '16');
  };
  // 変異: 見ているつもりで見ていない、を防ぐ。どれか 1 つでも見逃せば落ちる。
  const cases = [
    ['digest を外す', (f) => { f.from = f.from.replace(/@sha256:[0-9a-f]+$/, ''); }],
    ['浮動タグ（16-alpine）へ戻す', (f) => { f.from = 'postgres:16-alpine'; }],
    ['PG のメジャー版をずらす', (f) => { f.from = f.from.replace(/postgres:16\./, 'postgres:17.'); }],
    ['age の版を外す', (f) => { f.ageVersion = undefined; }],
    ['age の sha256（x86_64）を外す', (f) => { f.ageSha.x86_64 = undefined; }],
    ['age の sha256（aarch64）を外す', (f) => { f.ageSha.aarch64 = undefined; }],
    ['LOCAL_ONLY_IMAGES から外す', (f) => { f.localOnly = []; }],
    ['タグを :latest にする', (f) => { f.localOnly[0].ref = 'platform-backup:latest'; }],
    ['age の版を上げてタグを据え置く', (f) => { f.ageVersion = '1.3.2-r99'; }],
    ['CronJob のイメージを 1 つだけずらす', (f) => { f.cronJobs[1].image = 'postgres:16-alpine'; }],
    ['IfNotPresent を外す', (f) => { f.cronJobs[0].imagePullPolicy = undefined; }],
    ['BACKUP_AGE_INSTALL を env へ戻す', (f) => { f.cronJobs[0].envNames.push('BACKUP_AGE_INSTALL'); }],
    ['backup.sh に apk add を戻す', (f) => { f.scriptCallsApk = true; }],
    ['CI のビルドから外す', (f) => { f.ciBuildsDockerfile = false; }],
    // 10. #1709: チャレンジを返す取得元へ戻す。
    ['取得元を docker.io/library へ戻す', (f) => { f.from = f.from.replace(/^\S+\/postgres:/, 'docker.io/library/postgres:'); }],
    ['取得元を書かない（docker.io）', (f) => { f.from = f.from.replace(/^\S+\/postgres:/, 'postgres:'); }],
    ['取得元を public.ecr.aws へ替える', (f) => { f.from = f.from.replace(/^\S+\/postgres:/, 'public.ecr.aws/docker/library/postgres:'); }],
    ['取得元を ghcr.io へ替える', (f) => { f.from = f.from.replace(/^\S+\/postgres:/, 'ghcr.io/library/postgres:'); }],
  ];
  for (const [name, fn] of cases) assert.ok(mut(fn).length > 0, `変異「${name}」を見逃した`);
  // 10. #1709: FROM の ARG は既定で解いて見る。解けない名前は通さない。
  const fromOf = (dockerfile) => imageFacts({ dockerfile, imagesSh: '', imagesYml: '', backupSh: '', cronJobs: [] }).from;
  const pin = 'postgres:16.15-alpine3.24@sha256:' + 'a'.repeat(64);
  assert.strictEqual(fromOf(`ARG BASE_REGISTRY=mirror.gcr.io/library\nFROM \${BASE_REGISTRY}/${pin}\n`), `mirror.gcr.io/library/${pin}`, '${…} を ARG の既定で解いていない');
  assert.strictEqual(fromOf(`ARG BASE_REGISTRY=docker.io/library\nFROM $BASE_REGISTRY/${pin}\n`), `docker.io/library/${pin}`, '$… を ARG の既定で解いていない');
  assert.strictEqual(IMAGE_FACTS.from.startsWith('mirror.gcr.io/library/postgres:'), true, `実物の FROM が mirror.gcr.io を指していない（${IMAGE_FACTS.from}）`);
  const unresolved = clone(IMAGE_FACTS);
  unresolved.from = fromOf(`FROM \${BASE_REGISTRY}/${pin}\n`);
  assert.ok(checkBackupImage(unresolved, '16').length > 0, '既定の無い ARG の FROM を通した');
  unresolved.from = fromOf(`FROM \${BASE_REGISTRY}/${pin}\nARG BASE_REGISTRY=mirror.gcr.io/library\n`);
  assert.ok(checkBackupImage(unresolved, '16').length > 0, 'FROM より後ろの ARG で FROM を解いた（Docker は展開しない）');
  // 抜き出しの側も確かめる: 注記の中の apk は数えず、実行行の apk は数える。
  const facts = (backupSh) => imageFacts({ dockerfile: '', imagesSh: '', imagesYml: '', backupSh, cronJobs: [] });
  assert.strictEqual(facts('# 従前は `apk add age` を撃っていた\nensure_age() { :; }\n').scriptCallsApk, false, '注記の apk を数えた');
  assert.strictEqual(facts('ensure_age() {\n\tapk add --no-cache age\n}\n').scriptCallsApk, true, '実行行の apk を見逃した');
  assert.strictEqual(facts('x="$(apk add age 2>&1)"\n').scriptCallsApk, true, 'コマンド置換の中の apk を見逃した');
  // CI のビルドは実際の build 行で数える。注記の中の言及・別の Dockerfile のビルドは数えない。
  const ci = (imagesYml) => imageFacts({ dockerfile: '', imagesSh: '', imagesYml, backupSh: '', cronJobs: [] }).ciBuildsDockerfile;
  assert.strictEqual(ci(`      - run: docker build --progress plain -f ${BACKUP_DOCKERFILE} -t x deploy/local/platform-backup/image\n`), true, '実際の build 行を見逃した');
  assert.strictEqual(ci(`  # build-local は ${BACKUP_DOCKERFILE} を docker build -f ${BACKUP_DOCKERFILE} でビルドする\n`), false, '注記の言及を数えた');
  assert.strictEqual(ci(`      - run: docker build -f ${BACKUP_DOCKERFILE}.old -t x .\n`), false, '別の Dockerfile のビルドを数えた');
  assert.strictEqual(ci(`      - run: echo ${BACKUP_DOCKERFILE}\n`), false, 'build でない行を数えた');
});

// ---------------------------------------------------------------- 11. 秘匿管理の整合したスナップショット（#1868 / IADR-0548）

const BACKUP_VAULT_DIR = path.join('deploy', 'local', 'platform-backup', 'vault');
const SNAPSHOT_POLICY = read(BACKUP_VAULT_DIR, 'policy-raft-snapshot.hcl');
const SNAPSHOT_BOOTSTRAP = read(BACKUP_VAULT_DIR, 'bootstrap.sh');
const UP_SH = read('scripts', 'k8s-local-up.sh');

/** HCL のポリシーの path ブロック（コメントを除く）を {path: [capabilities]} にする。 */
function policyPaths(hcl) {
  const body = hcl.replace(/#.*$/gm, '');
  const out = {};
  const re = /path\s+"([^"]+)"\s*\{([^}]*)\}/g;
  let m;
  while ((m = re.exec(body))) {
    const caps = /capabilities\s*=\s*\[([^\]]*)\]/.exec(m[2]);
    out[m[1]] = caps ? caps[1].split(',').map((c) => c.trim().replace(/"/g, '')).filter(Boolean).sort() : [];
  }
  return out;
}

/** bootstrap.sh の変数（NAME="value"）を読む。 */
const shVar = (text, name) => (new RegExp(`^${name}="([^"]*)"`, 'm').exec(text) || [])[1];

function checkVaultSnapshotAuth({ cj, docs, policy, bootstrap, script }) {
  const errors = [];
  const sa = 'platform-backup-vault';
  if (cj.serviceAccountName !== sa) errors.push(`serviceAccountName が ${sa} でない（${cj.serviceAccountName}）`);
  if (cj.automountServiceAccountToken !== 'false') errors.push('既定の SA トークンを自動マウントしている（projected token だけを渡す）');
  const saDoc = find(docs, 'ServiceAccount', sa);
  if (!saDoc) errors.push(`ServiceAccount ${sa} が描かれていない`);
  else if (saDoc.top.automountServiceAccountToken !== 'false') errors.push(`ServiceAccount ${sa} が automountServiceAccountToken: false でない`);
  if (docs.some((d) => /Binding$/.test(d.kind) && d.body.includes(`name: ${sa}`))) errors.push(`${sa} に k8s の権限（RoleBinding）を付けている`);
  // projected token は期限つきで、スクリプトが読むパスにマウントされる。
  const jwtFile = (envOf(cj, 'BACKUP_VAULT_SA_JWT_FILE') || {}).value || '';
  const tokVol = cj.volumes.find((v) => v.saToken);
  if (!tokVol) errors.push('projected の serviceAccountToken が無い');
  else {
    const m = mountOf(cj, tokVol.name) || {};
    if (`${m.mountPath}/${tokVol.saToken.path}` !== jwtFile) errors.push(`SA トークンのパス（${m.mountPath}/${tokVol.saToken.path}）が BACKUP_VAULT_SA_JWT_FILE（${jwtFile}）と違う`);
    if (!m.readOnly) errors.push('SA トークンが読み取り専用でマウントされていない');
    if (!(Number(tokVol.saToken.expirationSeconds) > 0 && Number(tokVol.saToken.expirationSeconds) <= 3600)) errors.push('SA トークンの期限が 1 時間以内でない');
    // NFR-18, IADR-0585（Refs GHSA-j4qv-2w99-xg5p）: 対象者（audience）は秘匿管理（vault）。role も同じ値を要求する（下の want）。
    if (tokVol.saToken.audience !== 'vault') errors.push(`SA トークンの audience が vault でない（${tokVol.saToken.audience}）`);
  }
  // root トークン（Secret vault-dev-token）を渡さない。
  if (cj.env.some((e) => e.secretKeyRef && e.secretKeyRef.name === 'vault-dev-token')) errors.push('Secret vault-dev-token（root トークン）を渡している');
  if (!/^http:\/\/vault\.platform-infra(\.svc)?:8200$/.test((envOf(cj, 'BACKUP_VAULT_ADDR') || {}).value || '')) errors.push('BACKUP_VAULT_ADDR がクラスタ内の秘匿管理を指していない');
  // スナップショットの作業場所はメモリの emptyDir。
  const scratch = cj.volumes.find((v) => (mountOf(cj, v.name) || {}).mountPath === (envOf(cj, 'BACKUP_VAULT_SCRATCH') || {}).value);
  if (!scratch || !scratch.emptyDir || scratch.emptyDir.medium !== 'Memory') errors.push('BACKUP_VAULT_SCRATCH がメモリの emptyDir でない（平文をディスクへ書く）');
  // ポリシー: sys/storage/raft/snapshot の read だけ。
  const paths = policyPaths(policy);
  if (JSON.stringify(paths) !== JSON.stringify({ 'sys/storage/raft/snapshot': ['read'] }))
    errors.push(`ポリシーが sys/storage/raft/snapshot の read だけでない: ${JSON.stringify(paths)}`);
  // role: この SA・この名前空間にだけ束縛し、default ポリシーを付けず、1 回きり。
  const role = shVar(bootstrap, 'BACKUP_VAULT_ROLE');
  const roleLine = (bootstrap.match(/vault write auth\/kubernetes\/role\/\$BACKUP_VAULT_ROLE[^"]*"/) || [''])[0].replace(/\\\n\s*/g, ' ');
  if (role !== (envOf(cj, 'BACKUP_VAULT_AUTH_ROLE') || {}).value) errors.push(`bootstrap の role（${role}）と CronJob の BACKUP_VAULT_AUTH_ROLE が違う`);
  if (shVar(bootstrap, 'BACKUP_VAULT_SA') !== sa) errors.push('bootstrap の role が別の SA に束縛される');
  const want = [
    'bound_service_account_names=$BACKUP_VAULT_SA',
    'bound_service_account_namespaces=$INFRA_NS',
    'audience=vault',
    'token_policies=$BACKUP_VAULT_POLICY',
    'token_no_default_policy=true',
    'token_num_uses=1',
  ];
  for (const w of want) if (!roleLine.split(/\s+/).includes(w)) errors.push(`role の設定に ${w} が無い`);
  if (/bound_service_account_(names|namespaces)=\*/.test(roleLine)) errors.push('role をワイルドカードで束縛している');
  if (!bootstrap.includes('vault policy write $BACKUP_VAULT_POLICY -" < "$ROOT/deploy/local/platform-backup/vault/policy-raft-snapshot.hcl"'))
    errors.push('bootstrap がこのポリシーのファイルを書いていない');
  // スクリプト: トークンを引数へ載せる道具（curl / wget）を使わず、root トークンの名前に触れない。
  const code = script.replace(/^\s*#.*$/gm, '');
  if (/\b(curl|wget)\b/.test(code)) errors.push('backup.sh が curl / wget を呼ぶ（ヘッダのトークンが argv に出る）');
  if (/VAULT_DEV_ROOT_TOKEN_ID|vault-dev-token|Initial Root Token/.test(code)) errors.push('backup.sh が root トークンに触れる');
  if (!code.includes('/v1/sys/storage/raft/snapshot')) errors.push('backup.sh がスナップショットの API を呼ばない');
  return errors;
}

ok('🔴 11. 秘匿管理の回は raft の整合したスナップショットを、最小のポリシーと専用の SA（k8s auth）で取る', () => {
  const facts = { cj: VA, docs: VAULT_DOCS, policy: SNAPSHOT_POLICY, bootstrap: SNAPSHOT_BOOTSTRAP, script: BACKUP_SH };
  assert.deepStrictEqual(checkVaultSnapshotAuth(facts), [], checkVaultSnapshotAuth(facts).join(' / '));
  // 起動スクリプトが永続化の overlay を当てた後に bootstrap を呼ぶ（呼ばなければ Job はログインできない）。
  assert.ok(/platform_backup_vault_bootstrap\(\)/.test(UP_SH), 'k8s-local-up.sh に bootstrap の呼び出しが無い');
  assert.ok((UP_SH.match(/^\s*platform_backup_vault_bootstrap\s*$/gm) || []).length >= 2, 'VAULT と ESO の両方の当て直しの後で bootstrap を呼んでいない');
  // 変異: どれでも落ちる。
  const bad = (patch, label) => {
    const f = clone({ ...facts, cj: VA });
    f.docs = facts.docs;
    patch(f);
    assert.ok(checkVaultSnapshotAuth(f).length > 0, `${label} を見逃した`);
  };
  bad((f) => (f.policy = f.policy.replace('["read"]', '["read", "update"]')), 'ポリシーへの update（restore）');
  bad((f) => (f.policy += '\npath "secret/data/*" {\n  capabilities = ["read"]\n}\n'), 'ポリシーへの KV の read');
  bad((f) => (f.bootstrap = f.bootstrap.replace('token_no_default_policy=true', 'token_no_default_policy=false')), 'default ポリシーの付与');
  bad((f) => (f.bootstrap = f.bootstrap.replace(' token_num_uses=1', '')), '回数の無いトークン');
  bad((f) => (f.bootstrap = f.bootstrap.replace('bound_service_account_names=$BACKUP_VAULT_SA', 'bound_service_account_names=*')), 'SA のワイルドカード');
  bad((f) => (f.cj.automountServiceAccountToken = 'true'), '既定の SA トークンの自動マウント');
  bad((f) => (f.cj.serviceAccountName = 'default'), 'default SA での実行');
  bad((f) => f.cj.env.push({ name: 'VAULT_TOKEN_REF', secretKeyRef: { name: 'vault-dev-token', key: 'token' } }), 'root トークンの Secret の受け渡し');
  bad((f) => (f.cj.volumes.find((v) => v.emptyDir && v.emptyDir.medium).emptyDir.medium = ''), 'ディスクの emptyDir');
  bad((f) => (f.cj.volumes.find((v) => v.saToken).saToken.expirationSeconds = '86400'), '期限の長い SA トークン');
  bad((f) => delete f.cj.volumes.find((v) => v.saToken).saToken.audience, '対象者（audience）の無い SA トークン');
  bad((f) => (f.bootstrap = f.bootstrap.replace('  audience=vault token_policies', '  token_policies')), 'audience を要求しない role');
  bad((f) => (f.script += '\ncurl -H "X-Vault-Token: $t" "$BACKUP_VAULT_ADDR"\n'), 'curl でのトークンの受け渡し');
  bad((f) => (f.script += '\nVAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"\n'), 'root トークンの使用');
});

console.log(`[platform-backup.test] OK: ${passed} 件`);
