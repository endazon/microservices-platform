#!/usr/bin/env node
'use strict';
/*
 * cloud-ops.test.js
 * NFR-21, NFR-03, NFR-05, ADR-0002, ADR-0006, IADR-0675 (#2402):
 * クラウドの最小構成の運用の部品（deploy/cloud/backup・deploy/cloud/observability）の**配備の形**を、kustomize の描画結果で固定する。
 * バックアップの本体の分岐は deploy/local/platform-backup/script/backup.test.sh、リストア試験は
 * scripts/backup-restore-drill.sh --self-test が持つ。ここはその 2 つが見られない「配備の形」と警報の設定を見る。
 *
 * 固定するもの:
 *   1. バックアップの CronJob は hostPath を持たず、保管先は S3（BACKUP_TARGETS=none・宛先は ConfigMap・資格情報は secretKeyRef）。
 *      イメージは自製（pull しない）で、age の固定はローカルのイメージと同じ。本体スクリプトはローカルと同じもの。
 *   2. 🔴 Alertmanager の描画に Discord の receiver があり、webhook の URL の値が描画に含まれない。URL は Secret から initContainer が描く。
 *      描く script は値をログへ出さず、空・不正な値では起動させない（exit 1）。
 *   3. 常に鳴る警報（Watchdog）が heartbeat の receiver（url_file）へ 5 分ごとに送られる。Discord の失敗は /fail へ送られる。
 *   4. 受け口を増やさない（LoadBalancer・NodePort・hostPort・Ingress が無い）。Grafana・Loki・Tempo が無い。
 *   5. Prometheus の保持とメモリが 8 GB のノード向けに絞られている。AST の PrometheusRule の spec を取り出して promtool で読める。
 *   6. runbook に、heartbeat が途絶えたときに外の監視がオーナーへ知らせる手順がある。
 *
 * 描画は `kubectl kustomize`（オフライン）。kubectl・promtool・amtool が無ければ失敗にする（fail-closed。CI の static-checks が導入する）。
 * 判定は純関数にし、変異を当てて落ちることも同じ試験の中で確かめる。
 * 外部依存ゼロ（Node 標準モジュール ＋ kubectl / promtool / amtool）。実行: node scripts/cloud-ops.test.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const read = (...p) => fs.readFileSync(path.join(REPO_ROOT, ...p), 'utf8');

function run(cmd, args, opts = {}) {
  const r = spawnSync(cmd, args, { cwd: REPO_ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, ...opts });
  if (r.error) throw new Error(`${cmd} を実行できない（PATH に無い可能性）: ${r.error.message}`);
  return r;
}

function render(dir) {
  const r = run('kubectl', ['kustomize', dir]);
  if (r.status !== 0) throw new Error(`kubectl kustomize ${dir} が失敗した:\n${r.stderr}`);
  return r.stdout;
}

/**
 * 描画結果（kustomize が出すブロック形式の YAML）を木に組む最小の解析器。外部依存ゼロ。
 * 扱うもの: ブロックの写像・列、プレーン／引用符のスカラー、ブロックスカラー（| / |-）、複数行の二重引用符。
 * フロー形式（`[a, b]` / `{ a: b }`）は解かずに文字列のまま返す（自前の設定の照合は文字列で足りる）。
 * 🔴 複数行を正規表現で探さない（後戻りの爆発を避ける。#2402 の CodeQL の指摘）。行ごとに字下げで読む。
 */
function parseYaml(text) {
  const lines = text.split('\n');
  const ind = (l) => l.length - l.trimStart().length;
  const blank = (l) => l.trim() === '' || l.trimStart().startsWith('#');
  const skip = (i) => {
    while (i < lines.length && blank(lines[i])) i += 1;
    return i;
  };
  const isItem = (l) => {
    const s = l.trimStart();
    return s === '-' || s.startsWith('- ');
  };
  /** `key: rest` / `key:` を分ける。キーでなければ null。 */
  const splitKey = (s) => {
    if (s.startsWith('"') || s.startsWith("'") || s.startsWith('[') || s.startsWith('{')) return null;
    const k = s.indexOf(': ');
    if (k > 0) return [s.slice(0, k), s.slice(k + 2)];
    if (s.endsWith(':') && !s.includes(' ')) return [s.slice(0, -1), ''];
    return null;
  };
  const scalar = (raw) => {
    const s = raw.trim();
    if (s.length >= 2 && s.startsWith('"') && s.endsWith('"')) {
      try {
        return JSON.parse(s);
      } catch {
        return s.slice(1, -1);
      }
    }
    if (s.length >= 2 && s.startsWith("'") && s.endsWith("'")) return s.slice(1, -1).split("''").join("'");
    return s;
  };
  function blockScalar(i, parentIndent) {
    const out = [];
    let base = -1;
    while (i < lines.length) {
      const l = lines[i];
      if (l.trim() === '') {
        out.push('');
        i += 1;
        continue;
      }
      if (ind(l) <= parentIndent) break;
      if (base < 0) base = ind(l);
      out.push(l.slice(base));
      i += 1;
    }
    while (out.length && out[out.length - 1] === '') out.pop();
    return [out.join('\n') + '\n', i];
  }
  function value(rest, i, indent) {
    if (rest === '') {
      const j = skip(i);
      if (j < lines.length && (ind(lines[j]) > indent || (ind(lines[j]) === indent && isItem(lines[j])))) {
        return node(j, ind(lines[j]));
      }
      return [null, i];
    }
    if (rest === '|' || rest === '|-' || rest === '|+' || rest === '>') return blockScalar(i, indent);
    if (rest.startsWith('"') && (rest.length < 2 || !rest.endsWith('"'))) {
      // 複数行の二重引用符（kustomize が長い文字列を折る形）。中身は使わないので、つないで返す。
      let s = rest;
      while (i < lines.length && lines[i].trim() !== '' && ind(lines[i]) > indent) {
        s += ' ' + lines[i].trim();
        i += 1;
      }
      return [s, i];
    }
    return [scalar(rest), i];
  }
  function map(i, indent) {
    const obj = {};
    for (i = skip(i); i < lines.length && ind(lines[i]) === indent && !isItem(lines[i]); i = skip(i)) {
      const kv = splitKey(lines[i].trim());
      if (!kv) throw new Error(`YAML の写像の行として読めない: ${lines[i].trim().slice(0, 60)}`);
      const [v, j] = value(kv[1], i + 1, indent);
      obj[scalar(kv[0])] = v;
      i = j;
    }
    return [obj, i];
  }
  function list(i, indent) {
    const arr = [];
    for (i = skip(i); i < lines.length && ind(lines[i]) === indent && isItem(lines[i]); i = skip(i)) {
      const s = lines[i].trimStart();
      const rest = s === '-' ? '' : s.slice(2);
      if (rest === '') {
        const [v, j] = value('', i + 1, indent);
        arr.push(v);
        i = j;
      } else if (splitKey(rest)) {
        lines[i] = ' '.repeat(indent + 2) + rest;
        const [v, j] = map(i, indent + 2);
        arr.push(v);
        i = j;
      } else {
        arr.push(scalar(rest));
        i += 1;
      }
    }
    return [arr, i];
  }
  function node(i, indent) {
    return isItem(lines[i]) ? list(i, indent) : map(i, indent);
  }
  const start = skip(0);
  if (start >= lines.length) return null;
  return node(start, ind(lines[start]))[0];
}

/** 描画結果を文書ごとに分けて木に組む。各文書は { kind, name, obj, text }。 */
function docs(yaml) {
  return yaml
    .split('\n---\n')
    .map((t) => t.trim())
    .filter((t) => t && t !== '---')
    .map((text) => {
      const obj = parseYaml(text) || {};
      return { kind: obj.kind, name: (obj.metadata || {}).name, obj, text };
    });
}
/** 種類・名前・名前空間で文書を引く（既定は platform-infra。別の名前空間に同じ名前の別名の Service がある。#2401 の aliases）。 */
const find = (ds, kind, name, ns = 'platform-infra') =>
  ds.find((d) => d.kind === kind && d.name === name && ((d.obj.metadata || {}).namespace || ns) === ns);

/** ConfigMap の data のキーの中身（文字列の比較だけ。正規表現を組み立てない）。 */
function cmData(doc, key) {
  const data = doc && doc.obj && doc.obj.data;
  return data && typeof data[key] === 'string' ? data[key] : undefined;
}

/** 木を深さ優先で歩き、各写像を fn(写像, 親のキー) へ渡す。 */
function walk(node, fn, parentKey = '') {
  if (Array.isArray(node)) for (const n of node) walk(n, fn, parentKey);
  else if (node && typeof node === 'object') {
    fn(node, parentKey);
    for (const [k, v] of Object.entries(node)) walk(v, fn, k);
  }
}
/** Pod の雛形のコンテナ（initContainers を含む）。 */
function containersOf(obj) {
  const out = [];
  walk(obj, (m) => {
    for (const k of ['containers', 'initContainers']) if (Array.isArray(m[k])) out.push(...m[k]);
  });
  return out;
}
const envOf = (container, name) => (container.env || []).find((e) => e.name === name);
const volumeOf = (obj, name) => {
  let found;
  walk(obj, (m) => {
    if (Array.isArray(m.volumes)) found = found || m.volumes.find((v) => v.name === name);
  });
  return found;
};

// ---------------------------------------------------------------- 判定（純関数）

/** 受け口を増やす資源（LoadBalancer・NodePort・hostPort・hostNetwork・Ingress）を挙げる。入力は docs() の結果。 */
function inboundExposures(ds) {
  const out = [];
  for (const d of ds) {
    if (['Ingress', 'Gateway', 'HTTPRoute'].includes(d.kind)) out.push(`${d.kind} ${d.name}`);
    if (d.kind === 'Service' && ['LoadBalancer', 'NodePort'].includes((d.obj.spec || {}).type)) out.push(`Service ${d.name} type ${d.obj.spec.type}`);
    walk(d.obj, (m) => {
      if (m.hostPort !== undefined) out.push(`hostPort in ${d.name}`);
      if (m.hostNetwork === 'true') out.push(`hostNetwork in ${d.name}`);
    });
  }
  return out;
}

/** 秘密の値に見えるもの（Discord の webhook・Healthchecks の ping の UUID・age の秘密鍵）を挙げる。 */
function secretLookingValues(text) {
  const out = [];
  const pats = [
    /https:\/\/(?:discord|discordapp)\.com\/api\/webhooks\/\d+\/[A-Za-z0-9_-]{10,}/,
    /hc-ping\.com\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/,
    /AGE-SECRET-KEY-1[0-9A-Z]{20,}/,
  ];
  for (const p of pats) {
    const m = p.exec(text);
    if (m) out.push(m[0].slice(0, 24) + '…');
  }
  return out;
}

/** env の名前が秘密を示すのに value を直書きしているものを挙げる。入力は docs() の結果。 */
const SECRET_ENV_WORDS = ['PASSWORD', 'SECRET', 'TOKEN', 'ACCESS_KEY', 'WEBHOOK'];
function inlineSecretEnv(ds) {
  const out = [];
  for (const d of ds) {
    for (const c of containersOf(d.obj)) {
      for (const e of c.env || []) {
        if (SECRET_ENV_WORDS.some((w) => String(e.name).includes(w)) && e.value !== undefined) out.push(e.name);
      }
    }
  }
  return out;
}

const durationSeconds = (s) => {
  const m = /^(\d+)([smhd])$/.exec(s || '');
  if (!m) return NaN;
  return Number(m[1]) * { s: 1, m: 60, h: 3600, d: 86400 }[m[2]];
};
const bytesOf = (s) => {
  const m = /^(\d+)(Mi|Gi|MB|GB)$/.exec(s || '');
  if (!m) return NaN;
  return Number(m[1]) * { Mi: 2 ** 20, Gi: 2 ** 30, MB: 1e6, GB: 1e9 }[m[2]];
};
/** `--name=value` の形の引数の値。 */
const argValue = (args, name) => {
  const a = (args || []).find((x) => String(x).startsWith(`--${name}=`));
  return a === undefined ? undefined : String(a).slice(name.length + 3);
};

// ---------------------------------------------------------------- 実行

let passed = 0;
let failed = 0;
function t(name, fn) {
  try {
    fn();
    passed += 1;
    console.log(`  ok    ${name}`);
  } catch (e) {
    failed += 1;
    console.log(`  NG    ${name}\n        ${String(e.message).split('\n').join('\n        ')}`);
  }
}

// ---- 判定の変異試験（見ているつもりで見ていない、を防ぐ）
t('T-2402-49 変異: 解析器はブロックの写像・列・ブロックスカラー・引用符を読む', () => {
  const o = parseYaml("a:\n  b: 'x'\n  c:\n  - name: n\n    value: \"v\"\n  - plain\n  d: |\n    l1\n\n    l2\ne: [f, g]\n");
  assert.deepStrictEqual(o, { a: { b: 'x', c: [{ name: 'n', value: 'v' }, 'plain'], d: 'l1\n\nl2\n' }, e: '[f, g]' });
});
t('T-2402-49 変異: LoadBalancer・hostPort・Ingress を拾う', () => {
  assert.strictEqual(inboundExposures(docs('kind: Service\nmetadata:\n  name: s\nspec:\n  type: LoadBalancer\n')).length, 1);
  assert.strictEqual(inboundExposures(docs('kind: Pod\nmetadata:\n  name: p\nspec:\n  containers:\n  - ports:\n    - containerPort: 1\n      hostPort: 80\n')).length, 1);
  assert.strictEqual(inboundExposures(docs('kind: Ingress\nmetadata:\n  name: i\n')).length, 1);
  assert.strictEqual(inboundExposures(docs('kind: Service\nmetadata:\n  name: s\nspec:\n  type: ClusterIP\n')).length, 0);
});
t('T-2402-49 変異: 秘密の値に見えるもの・env の直書きを拾う', () => {
  assert.strictEqual(secretLookingValues('url: https://discord.com/api/webhooks/123456/abcdefghijklmnop').length, 1);
  assert.strictEqual(secretLookingValues('https://hc-ping.com/0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b').length, 1);
  assert.strictEqual(secretLookingValues("webhook_url: '__DISCORD_WEBHOOK_URL__'").length, 0);
  const pod = (env) => docs(`kind: Pod\nmetadata:\n  name: p\nspec:\n  containers:\n  - name: c\n    env:\n${env}`);
  assert.deepStrictEqual(inlineSecretEnv(pod('    - name: RCLONE_CONFIG_X_SECRET_ACCESS_KEY\n      value: x\n')), ['RCLONE_CONFIG_X_SECRET_ACCESS_KEY']);
  assert.deepStrictEqual(inlineSecretEnv(pod('    - name: RCLONE_CONFIG_X_SECRET_ACCESS_KEY\n      valueFrom:\n        secretKeyRef:\n          name: s\n')), []);
});

let backup = '';
let obs = '';
t('T-2402-30 deploy/cloud/backup と deploy/cloud/observability を描画できる', () => {
  backup = render('deploy/cloud/backup');
  obs = render('deploy/cloud/observability');
});
const bds = docs(backup);
const ods = docs(obs);
const EMPTY = { obj: {}, text: '' };

// ---------------------------------------------------------------- バックアップ
const cj = find(bds, 'CronJob', 'platform-backup-postgres') || EMPTY;
const cjSpec = cj.obj.spec || {};
const jobSpec = ((cjSpec.jobTemplate || {}).spec) || {};
const backupC = containersOf(cj.obj).find((c) => c.name === 'backup') || {};
t('T-2402-30 バックアップ: CronJob platform-backup-postgres が出る', () => assert.ok(cj.text, 'CronJob が無い'));
t('T-2402-30 バックアップ: hostPath を持たない（保管先はクラスタ外のオブジェクトストレージ）', () => {
  const found = [];
  for (const d of bds) walk(d.obj, (m) => m.hostPath !== undefined && found.push(d.name));
  assert.deepStrictEqual(found, [], 'hostPath がある');
});
t('T-2402-31 バックアップ: BACKUP_TARGETS=none・宛先は ConfigMap・資格情報は secretKeyRef', () => {
  assert.strictEqual((envOf(backupC, 'BACKUP_TARGETS') || {}).value, 'none');
  const dest = envOf(backupC, 'BACKUP_S3_DEST') || {};
  assert.strictEqual((((dest.valueFrom || {}).configMapKeyRef) || {}).name, 'platform-backup-s3-target');
  for (const k of ['RCLONE_CONFIG_OBJSTORE_ACCESS_KEY_ID', 'RCLONE_CONFIG_OBJSTORE_SECRET_ACCESS_KEY', 'PGPASSWORD']) {
    const e = envOf(backupC, k) || {};
    assert.ok(e.valueFrom && e.valueFrom.secretKeyRef && e.value === undefined, `${k} が secretKeyRef でない`);
  }
  assert.deepStrictEqual(inlineSecretEnv(bds), []);
  assert.ok(!bds.some((d) => d.kind === 'Secret'), '描画に Secret がある（資格情報をリポジトリに置かない）');
});
const dockerCloud = read('deploy/cloud/backup/image/Dockerfile');
const dockerLocal = read('deploy/local/platform-backup/image/Dockerfile');
/** Dockerfile の `ARG <名>=<値>` の値（正規表現を組み立てない）。 */
const argOf = (src, n) => {
  const line = src.split('\n').find((l) => l.startsWith(`ARG ${n}=`));
  return line === undefined ? undefined : line.slice(`ARG ${n}=`.length).trim();
};
t('T-2402-32 バックアップ: イメージは自製・pull しない・タグはイメージの中身の版から作る', () => {
  const repo = 'microservices-platform/platform-backup-cloud:';
  assert.ok(String(backupC.image).startsWith(repo), 'イメージが microservices-platform/platform-backup-cloud でない');
  assert.strictEqual(backupC.imagePullPolicy, 'Never');
  const pg = /postgres:(\d+\.\d+)-alpine/.exec(dockerCloud)[1];
  assert.strictEqual(String(backupC.image).slice(repo.length), `pg${pg}-age${argOf(dockerCloud, 'AGE_VERSION')}-rclone${argOf(dockerCloud, 'APK_RCLONE_VERSION')}`);
});
t('T-2402-33 バックアップ: ベースと age の固定はローカルのイメージと同じ（片側だけ上げない）', () => {
  const from = (s) => s.split('\n').find((l) => l.startsWith('FROM '));
  assert.strictEqual(from(dockerCloud), from(dockerLocal));
  for (const n of ['BASE_REGISTRY', 'AGE_VERSION', 'AGE_APK_SHA256_X86_64', 'AGE_APK_SHA256_AARCH64', 'ALPINE_BRANCH']) {
    assert.strictEqual(argOf(dockerCloud, n), argOf(dockerLocal, n), n);
  }
  assert.match(argOf(dockerCloud, 'APK_RCLONE_SHA256_X86_64') || '', /^[0-9a-f]{64}$/);
  assert.match(argOf(dockerCloud, 'APK_RCLONE_SHA256_AARCH64') || '', /^[0-9a-f]{64}$/);
  assert.ok(!dockerCloud.includes('--allow-untrusted'), '署名の検証を外している');
});
// NFR-21: ARG・ENV は RUN の環境変数になり、rclone は RCLONE_<旗> を自分の旗として読む（旧の ARG RCLONE_VERSION が --version と読まれ、
// ビルドの中の `rclone version` が失敗した）。イメージの宣言に RCLONE_ で始まる ARG・ENV を置かない。
/** Dockerfile の ARG・ENV の名前のうち、rclone が旗・設定として読む接頭辞（RCLONE_）で始まるもの（命令の大小は区別しない）。 */
const rcloneEnvNames = (src) => src.split('\n')
  .map((l) => /^\s*(ARG|ENV)\s+([A-Za-z_][A-Za-z0-9_]*)/i.exec(l))
  .filter((m) => m && /^RCLONE_/i.test(m[2]))
  .map((m) => `${m[1]} ${m[2]}`);
t('NFR-21 バックアップ: イメージの宣言に RCLONE_ で始まる ARG・ENV が無い（rclone が自分の旗として読み、ビルドの rclone version が落ちる）', () => {
  assert.deepStrictEqual(rcloneEnvNames(dockerCloud), []);
  // 陽性対照: 版の ARG は在る（名前を変えただけで、固定は外していない）。
  assert.match(argOf(dockerCloud, 'APK_RCLONE_VERSION') || '', /^\d+\.\d+\.\d+-r\d+$/);
  // 陰性対照: 旧の名前（ARG RCLONE_VERSION）と ENV・小文字の命令を検出する。
  assert.deepStrictEqual(rcloneEnvNames('ARG RCLONE_VERSION=1.75.2-r0\nENV RCLONE_CONFIG_X=y\narg rclone_fast_list=1\nARG APK_RCLONE_VERSION=1'),
    ['ARG RCLONE_VERSION', 'ENV RCLONE_CONFIG_X', 'arg rclone_fast_list']);
});
t('T-2402-34 バックアップ: 本体スクリプトはローカルと同じもの（二重に持たない）', () => {
  const cm = find(bds, 'ConfigMap', 'platform-backup-script');
  assert.ok(cm, 'ConfigMap platform-backup-script が無い');
  // 同じ configMapGenerator（deploy/local/platform-backup/script）を取り込む。コピーを置かない。
  const kz = parseYaml(read('deploy/cloud/backup/kustomization.yaml'));
  assert.ok(kz.resources.includes('../../local/platform-backup/script'), 'ローカルの本体を取り込んでいない');
  assert.ok(!fs.existsSync(path.join(REPO_ROOT, 'deploy/cloud/backup/backup.sh')), 'backup.sh の写しがある');
  // 本体の S3 の経路の印が描画に入っている（古い本体を取り込んでいない）。
  assert.ok(cm.text.includes('publish_s3'), '描画の本体に S3 の経路が無い');
});
t('T-2402-35 バックアップ: 米国市場の時間帯を避け、並行させず、再試行しない', () => {
  assert.strictEqual(cjSpec.schedule, '0 12 * * *');
  assert.strictEqual(cjSpec.timeZone, 'Asia/Tokyo');
  assert.strictEqual(cjSpec.concurrencyPolicy, 'Forbid');
  assert.strictEqual(jobSpec.backoffLimit, '0');
  const sd = Number(cjSpec.startingDeadlineSeconds);
  // 12:00 JST の後追いが 22:30 JST（米国市場の始まり）へ食い込まない。
  assert.ok(sd > 0 && 12 * 3600 + sd + 3600 <= 22.5 * 3600, `startingDeadlineSeconds=${sd}`);
});
t('T-2402-36 バックアップ: 保持（lifecycle）と Object Lock の日数が決まっている', () => {
  const lc = JSON.parse(read('deploy/cloud/backup/bucket-lifecycle.json'));
  const lock = JSON.parse(read('deploy/cloud/backup/object-lock.json'));
  const days = (prefix) => lc.Rules.find((r) => r.Filter && r.Filter.Prefix && r.Filter.Prefix.endsWith(prefix)).Expiration.Days;
  assert.strictEqual(days('/daily/'), 30);
  assert.strictEqual(days('/monthly/'), 365);
  const ret = lock.ObjectLockEnabled === 'Enabled' && lock.Rule.DefaultRetention;
  assert.ok(ret && ret.Mode === 'COMPLIANCE', 'Object Lock が COMPLIANCE でない');
  // #2402 のレビュー 🟡4: 保持の期限（lifecycle）が来た日に Object Lock がまだ効いていると削除が拒まれる。
  // Object Lock は日次の保持より真に短くし、余りを 1 日以上持たせる（古い版は NoncurrentDays の後に消える）。
  assert.ok(ret.Days < days('/daily/'), 'Object Lock の保持が日次の保持より短くない（期限の日に消せない）');
  for (const r of lc.Rules) assert.ok(r.NoncurrentVersionExpiration, `${r.ID}: 古い版を消さないと容量が積み上がる`);
});
const np = find(bds, 'NetworkPolicy', 'platform-backup-egress') || EMPTY;
t('T-2402-38 バックアップ: 外への通信を絞る（DNS・Postgres・443 だけ。受けは無し）', () => {
  assert.ok(np.text, 'NetworkPolicy platform-backup-egress が無い');
  assert.deepStrictEqual(np.obj.spec.policyTypes, ['Egress']);
  const ports = new Set();
  for (const rule of np.obj.spec.egress) for (const p of rule.ports) ports.add(Number(p.port));
  assert.deepStrictEqual([...ports].sort((a, b) => a - b), [53, 443, 5432]);
});

// ---------------------------------------------------------------- 警報・heartbeat
const amCm = find(ods, 'ConfigMap', 'alertmanager-cloud-config') || EMPTY;
const amTmpl = cmData(amCm, 'alertmanager.yml.tmpl') || '';
const renderSh = cmData(amCm, 'render.sh') || '';
const am = parseYaml(amTmpl) || {};
const amRoute = am.route || {};
const amRecv = (name) => (am.receivers || []).find((r) => r.name === name) || {};
const amSub = (alertname) => (amRoute.routes || []).find((r) => r.matchers === `[alertname = "${alertname}"]`) || {};
const amDep = find(ods, 'Deployment', 'alertmanager') || EMPTY;
const amRenderC = containersOf(amDep.obj).find((c) => c.name === 'render-config') || {};
t('T-2402-40 Alertmanager: 描画に Discord の receiver があり、既定の route がそこへ向く', () => {
  const d = amRecv('discord').discord_configs || [];
  assert.strictEqual(d.length, 1, 'discord_configs が無い');
  assert.strictEqual(d[0].webhook_url, '__DISCORD_WEBHOOK_URL__');
  assert.strictEqual(amRoute.receiver, 'discord');
  assert.ok(!amTmpl.includes('default-null'), '既定の受信先が null のまま');
});
t('T-2402-41 Alertmanager: webhook の URL の値が描画に含まれない（Secret から読む）', () => {
  assert.deepStrictEqual(secretLookingValues(obs), []);
  assert.deepStrictEqual(secretLookingValues(backup), []);
  assert.ok(!ods.some((d) => d.kind === 'Secret'), '描画に Secret がある');
  const e = envOf(amRenderC, 'DISCORD_WEBHOOK_URL') || {};
  assert.strictEqual((((e.valueFrom || {}).secretKeyRef) || {}).name, 'alertmanager-discord');
  assert.strictEqual(e.value, undefined);
  assert.deepStrictEqual(inlineSecretEnv(ods), []);
});

// render.sh を砂場で走らせる（amtool はスタブ。本物の amtool の検査は T-2402-47）。
const sandbox = fs.mkdtempSync(path.join(os.tmpdir(), 'cloud-ops-'));
const srcDir = path.join(sandbox, 'src');
fs.mkdirSync(srcDir);
fs.writeFileSync(path.join(srcDir, 'alertmanager.yml.tmpl'), amTmpl);
fs.writeFileSync(path.join(srcDir, 'render.sh'), renderSh);
const hbDir = path.join(sandbox, 'hb');
fs.mkdirSync(hbDir);
const stubAmtool = path.join(sandbox, 'amtool');
fs.writeFileSync(stubAmtool, '#!/bin/sh\nexit 0\n', { mode: 0o755 });
// 🔴 試験用の値（実在の webhook ではない）。
const FAKE_URL = 'https://discord.com/api/webhooks/1234567890/fake-token_FOR-TEST-ONLY';
const FAKE_PING = 'https://hc-ping.com/00000000-0000-4000-8000-000000000000';
const FAKE_DPING = 'https://hc-ping.com/11111111-1111-4111-8111-111111111111';
const HB_OK = { 'ping-url': FAKE_PING, 'discord-ping-url': FAKE_DPING, 'discord-fail-url': `${FAKE_DPING}/fail` };
function renderWith(url, hbOverride = {}) {
  const outDir = fs.mkdtempSync(path.join(sandbox, 'out-'));
  for (const [k, v] of Object.entries({ ...HB_OK, ...hbOverride })) {
    const f = path.join(hbDir, k);
    if (v === null) fs.rmSync(f, { force: true });
    else fs.writeFileSync(f, v + '\n');
  }
  const r = run('sh', [path.join(srcDir, 'render.sh')], {
    env: { PATH: process.env.PATH, RENDER_SRC: srcDir, RENDER_OUT: outDir, AMTOOL: stubAmtool, HEARTBEAT_DIR: hbDir, DISCORD_WEBHOOK_URL: url },
  });
  const out = path.join(outDir, 'alertmanager.yml');
  return { status: r.status, logs: r.stdout + r.stderr, config: fs.existsSync(out) ? fs.readFileSync(out, 'utf8') : '' };
}
t('T-2402-42 render.sh: 正しい URL なら設定へ書き、ログへ値を出さない', () => {
  const r = renderWith(FAKE_URL);
  assert.strictEqual(r.status, 0, r.logs);
  assert.ok(r.config.includes(`webhook_url: '${FAKE_URL}'`), '設定に URL が入っていない');
  assert.ok(!r.logs.includes('fake-token'), 'ログに URL が出ている');
  assert.ok(!r.logs.includes('00000000-0000') && !r.logs.includes('11111111-1111'), 'ログに ping の URL が出ている');
});
for (const [label, url] of [
  ['空', ''],
  ['Discord 以外の宛先', 'https://example.com/api/webhooks/1/abc'],
  ['http', 'http://discord.com/api/webhooks/1/abc'],
  ['YAML への注入', "https://discord.com/api/webhooks/1/abc'\n      x: y"],
  ['sed への注入', 'https://discord.com/api/webhooks/1/abc&x'],
]) {
  t(`T-2402-43 render.sh: URL が${label}なら起動させない（exit 1）・値をログへ出さない`, () => {
    const r = renderWith(url);
    assert.notStrictEqual(r.status, 0);
    assert.strictEqual(r.config, '');
    if (url) assert.ok(!r.logs.includes(url.slice(8)), 'ログに値が出ている');
  });
}
for (const [label, hb] of [
  ['ping の URL が無い', { 'ping-url': null }],
  ['discord-fail の URL が「discord-ping/fail」でない', { 'discord-fail-url': FAKE_DPING }],
  ['ping の URL が https でない', { 'ping-url': 'http://hc-ping.com/x' }],
  ['discord-ping の URL が ping と同じ（check を分けていない）', { 'discord-ping-url': FAKE_PING, 'discord-fail-url': `${FAKE_PING}/fail` }],
]) {
  t(`T-2402-44 render.sh: heartbeat の ${label}なら起動させない（heartbeat の止まった構成で動かない）`, () => {
    const r = renderWith(FAKE_URL, hb);
    assert.notStrictEqual(r.status, 0, r.logs);
    assert.strictEqual(r.config, '');
  });
}
t('T-2402-45 heartbeat: Watchdog を check A（url_file）へ 5 分ごとに送る', () => {
  const w = amSub('Watchdog');
  assert.strictEqual(w.receiver, 'heartbeat');
  assert.strictEqual(w.repeat_interval, '5m');
  assert.strictEqual(((amRecv('heartbeat').webhook_configs || [])[0] || {}).url_file, '/etc/alertmanager/heartbeat/ping-url');
});
t('T-2402-45 heartbeat: Discord の到達は別の check B で見る（到達の ping と、失敗の /fail）', () => {
  const ok = amSub('DiscordDeliveryHealthy');
  assert.strictEqual(ok.receiver, 'discord-heartbeat');
  assert.strictEqual(ok.repeat_interval, '5m');
  assert.strictEqual(((amRecv('discord-heartbeat').webhook_configs || [])[0] || {}).url_file, '/etc/alertmanager/heartbeat/discord-ping-url');
  assert.strictEqual(amSub('AlertmanagerDiscordFailing').receiver, 'discord-heartbeat-fail');
  assert.strictEqual(((amRecv('discord-heartbeat-fail').webhook_configs || [])[0] || {}).url_file, '/etc/alertmanager/heartbeat/discord-fail-url');
  // 🔴 check A の ping（Watchdog）で B が戻らない: 失敗の送り先は A の URL ではない。
  assert.ok(!amTmpl.includes('heartbeat/fail-url'), 'check A の /fail を使っている');
});
t('T-2402-45 heartbeat: ping の Secret のボリュームは optional でない（無ければ Pod が起きず、ping が止まって見える）', () => {
  const v = volumeOf(amDep.obj, 'heartbeat') || {};
  assert.strictEqual((v.secret || {}).secretName, 'alertmanager-heartbeat');
  assert.notStrictEqual((v.secret || {}).optional, 'true', 'heartbeat の Secret が optional');
});
const promCm = find(ods, 'ConfigMap', 'prometheus-cloud-config') || EMPTY;
const rules = cmData(promCm, 'platform-rules.yml') || '';
const ruleOf = (name) => {
  for (const g of (parseYaml(rules) || {}).groups || []) for (const r of g.rules || []) if (r.alert === name) return r;
  return {};
};
t('T-2402-45 heartbeat: Watchdog・Discord の到達と失敗・AST の規則の不在の警報がある', () => {
  assert.strictEqual(ruleOf('Watchdog').expr, 'vector(1)');
  const failures = 'sum(increase(alertmanager_notifications_failed_total{integration="discord"}[15m]))';
  assert.strictEqual(ruleOf('DiscordDeliveryHealthy').expr, `${failures} == 0`);
  assert.strictEqual(ruleOf('AlertmanagerDiscordFailing').expr, `${failures} > 0`);
  assert.strictEqual(ruleOf('AstAlertRulesNotLoaded').expr, 'absent(prometheus_rule_group_rules{rule_group=~".*;ai-stock-trading\\\\..*"})');
});
t('T-2402-46 受け口を増やさない（LoadBalancer・NodePort・hostPort・Ingress が無い）', () => {
  assert.deepStrictEqual(inboundExposures(ods), []);
  assert.deepStrictEqual(inboundExposures(bds), []);
});
const obsImages = ods.flatMap((d) => containersOf(d.obj).map((c) => String(c.image)));
t('T-2402-46 Grafana・Loki・Tempo を持ち込まない', () => {
  const bad = obsImages.filter((i) => ['grafana', 'loki', 'tempo'].some((w) => i.toLowerCase().includes(w)));
  assert.deepStrictEqual(bad, [], 'Grafana・Loki・Tempo のイメージがある');
});
t('T-2402-46 イメージはローカルの経路 B と同じ digest（片側だけ上げない）', () => {
  const local = read('deploy/local/observability/alertmanager.yaml') + read('deploy/local/observability/prometheus.yaml');
  assert.ok(obsImages.length >= 4, `イメージが少ない: ${obsImages}`);
  for (const i of obsImages) assert.ok(i.includes('@sha256:') && local.includes(i), `経路 B に無いイメージ: ${i}`);
});
const promDep = find(ods, 'Deployment', 'prometheus') || EMPTY;
const promC = containersOf(promDep.obj).find((c) => c.name === 'prometheus') || {};
t('T-2402-47 Prometheus: 保持は 7 日・1GB 以内、メモリの上限は 768Mi 以内（8 GB のノード）', () => {
  const time = argValue(promC.args, 'storage.tsdb.retention.time');
  const size = argValue(promC.args, 'storage.tsdb.retention.size');
  assert.ok(durationSeconds(time) <= 7 * 86400, `retention.time=${time}`);
  assert.ok(bytesOf(size) <= 1e9, `retention.size=${size}`);
  const mem = ((promC.resources || {}).limits || {}).memory;
  assert.ok(bytesOf(mem) <= 768 * 2 ** 20, `memory limit=${mem}`);
  assert.ok((promC.args || []).includes('--web.enable-remote-write-receiver'));
});

// ---------------------------------------------------------------- クラウドの最小構成（#2401）との結線
// 🔴 #2401 の実物（deploy/cloud/infra）と、秘匿管理（deploy/cloud/vault。IADR-0689: CA は configure.sh・証明書は OpenBao の PKI）の名前と突き合わせる。
//   片側だけ変えると、ここで赤になる。
const configure = fs.existsSync(path.join(REPO_ROOT, 'deploy/cloud/vault/configure.sh')) ? read('deploy/cloud/vault/configure.sh') : '';
const tlsGen = fs.existsSync(path.join(REPO_ROOT, 'deploy/cloud/vault/eso/tls-platform-infra.yaml')) ? docs(read('deploy/cloud/vault/eso/tls-platform-infra.yaml')) : [];
let all = '';
t('T-2402-50 結線: deploy/cloud の kustomization が infra・backup・observability を束ねて描画できる', () => {
  const kz = parseYaml(read('deploy/cloud/kustomization.yaml'));
  for (const r of ['infra', 'backup', 'observability']) assert.ok((kz.resources || []).includes(r), `resources に ${r} が無い`);
  all = render('deploy/cloud');
});
const ads = docs(all);
let infraDs = [];
t('T-2402-50 結線: #2401 の deploy/cloud/infra を描画できる（突き合わせの相手）', () => {
  infraDs = docs(render('deploy/cloud/infra'));
});
const pgDep = find(infraDs, 'Deployment', 'postgres') || EMPTY;
const pgC = containersOf(pgDep.obj).find((c) => c.name === 'postgres') || {};
t('T-2402-37 TLS: バックアップは Postgres へ verify-full で繋ぎ、CA は #2401 の ConfigMap platform-infra-ca の ca.crt だけを読む', () => {
  assert.strictEqual((envOf(backupC, 'PGSSLMODE') || {}).value, 'verify-full');
  const root = (envOf(backupC, 'PGSSLROOTCERT') || {}).value;
  const mount = (backupC.volumeMounts || []).find((m) => m.name === 'platform-infra-ca') || {};
  assert.strictEqual(root, `${mount.mountPath}/ca.crt`, 'PGSSLROOTCERT がマウントした CA を指していない');
  assert.strictEqual(mount.readOnly, 'true');
  const v = volumeOf(cj.obj, 'platform-infra-ca') || {};
  assert.strictEqual((v.configMap || {}).name, 'platform-infra-ca');
  assert.deepStrictEqual(((v.configMap || {}).items || []).map((i) => i.key), ['ca.crt']);
  assert.notStrictEqual((v.configMap || {}).optional, 'true', 'CA が optional（無ければ検証なしへ倒れる構成を許す）');
  // 秘匿管理が作る CA の置き場所と名前を突き合わせる（IADR-0689 決定 4）。
  assert.ok(configure.includes('create configmap platform-infra-ca --from-file=ca.crt='), 'configure.sh が ConfigMap platform-infra-ca（ca.crt）を作っていない');
});
t('T-2402-37 TLS: 名前の照合に使うホスト（PGHOST）は Postgres の Service 名で、#2401 の証明書の SAN に入っている', () => {
  const host = (envOf(backupC, 'PGHOST') || {}).value;
  assert.ok(find(infraDs, 'Service', host), `Service ${host} が #2401 に無い`);
  const gen = find(tlsGen, 'VaultDynamicSecret', 'postgres-tls') || EMPTY;
  assert.strictEqual(((gen.obj.spec || {}).parameters || {}).common_name, host, 'Postgres の証明書（OpenBao の PKI）の名前が Service の短い名前でない');
  assert.ok((pgC.args || []).includes('ssl=on'), '#2401 の Postgres が TLS でない');
});
t('T-2402-37 資格情報: 超利用者の名前とパスワードは #2401 の Secret postgres の username / password から読む', () => {
  for (const [envName, key] of [['PGUSER', 'username'], ['PGPASSWORD', 'password']]) {
    const ref = (((envOf(backupC, envName) || {}).valueFrom || {}).secretKeyRef) || {};
    assert.deepStrictEqual([ref.name, ref.key], ['postgres', key], envName);
    const pgRef = (((envOf(pgC, envName === 'PGUSER' ? 'POSTGRES_USER' : 'POSTGRES_PASSWORD') || {}).valueFrom || {}).secretKeyRef) || {};
    assert.deepStrictEqual([pgRef.name, pgRef.key], ['postgres', key], `#2401 の Postgres の ${key} と違う`);
  }
  assert.strictEqual((envOf(backupC, 'PGUSER') || {}).value, undefined, 'PGUSER を直書きしている');
});
const ingressFrom = (ds, name) => {
  const p = find(ds, 'NetworkPolicy', name) || EMPTY;
  return { p, rules: ((p.obj.spec || {}).ingress) || [] };
};
t('T-2402-39 入りの許可: Postgres はバックアップの Pod から 5432 だけを受ける（#2401 の postgres-ingress-clients の外）', () => {
  const { p, rules } = ingressFrom(bds, 'postgres-ingress-backup');
  assert.ok(p.text, 'NetworkPolicy postgres-ingress-backup が無い');
  assert.deepStrictEqual(p.obj.spec.podSelector, { matchLabels: { app: 'postgres' } });
  assert.strictEqual(rules.length, 1);
  assert.deepStrictEqual(rules[0].from, [{ podSelector: { matchLabels: { app: 'platform-backup' } } }]);
  assert.deepStrictEqual(rules[0].ports.map((x) => x.port), ['5432']);
  assert.ok((find(infraDs, 'Deployment', 'postgres') || EMPTY).text.includes('app: postgres'), '#2401 の Postgres のラベルが違う');
});
t('T-2402-39 入りの許可: otel-collector は Prometheus から 8888 だけを受ける', () => {
  const { p, rules } = ingressFrom(ods, 'otel-collector-ingress-prometheus');
  assert.ok(p.text, 'NetworkPolicy otel-collector-ingress-prometheus が無い');
  assert.deepStrictEqual(p.obj.spec.podSelector, { matchLabels: { app: 'otel-collector' } });
  assert.strictEqual(rules.length, 1);
  assert.deepStrictEqual(rules[0].from, [{ podSelector: { matchLabels: { app: 'prometheus' } } }]);
  assert.deepStrictEqual(rules[0].ports.map((x) => x.port), ['8888']);
});
const otelCmAll = find(ads, 'ConfigMap', 'otel-collector-config') || EMPTY;
const otelCmBase = find(infraDs, 'ConfigMap', 'otel-collector-config') || EMPTY;
const otelAll = parseYaml(cmData(otelCmAll, 'config.yaml') || '') || {};
const otelBase = parseYaml(cmData(otelCmBase, 'config.yaml') || '') || {};
t('T-2402-51 otel: 結線の描画では、メトリクスだけを Prometheus へ remote write する', () => {
  assert.strictEqual(((otelAll.exporters || {}).prometheusremotewrite || {}).endpoint, 'http://prometheus:9090/api/v1/write');
  assert.strictEqual(((otelAll.service || {}).pipelines || {}).metrics.exporters, '[prometheusremotewrite]');
  for (const p of ['traces', 'logs']) {
    assert.strictEqual(otelAll.service.pipelines[p].exporters, otelBase.service.pipelines[p].exporters, `${p} を変えている`);
  }
});
t('T-2402-51 otel: 足すのは remote write だけで、ほかは #2401 の設定と同じ（片側だけの変更を止める）', () => {
  const strip = (c) => {
    const x = JSON.parse(JSON.stringify(c));
    if (x.exporters) delete x.exporters.prometheusremotewrite;
    if (x.service && x.service.pipelines && x.service.pipelines.metrics) delete x.service.pipelines.metrics.exporters;
    return x;
  };
  assert.ok(otelBase.receivers, '#2401 の otel の設定を読めない');
  assert.deepStrictEqual(strip(otelAll), strip(otelBase));
});
t('T-2402-51 otel: Service が 8888（自己テレメトリ）を出し、Prometheus の収集先と合う', () => {
  const svc = find(ads, 'Service', 'otel-collector') || EMPTY;
  assert.ok(((svc.obj.spec || {}).ports || []).some((x) => x.port === '8888' && x.targetPort === '8888'), 'Service に 8888 が無い');
  const base = find(infraDs, 'Service', 'otel-collector') || EMPTY;
  for (const p of (base.obj.spec || {}).ports || []) {
    assert.ok(svc.obj.spec.ports.some((x) => x.port === p.port), `#2401 の ${p.name} を落としている`);
  }
  const prom = parseYaml(cmData(find(ods, 'ConfigMap', 'prometheus-cloud-config') || EMPTY, 'prometheus.yml') || '') || {};
  const job = (prom.scrape_configs || []).find((j) => j.job_name === 'otel-collector') || {};
  assert.strictEqual((((job.static_configs || [])[0] || {}).targets), "['otel-collector:8888']");
});
t('T-2402-51 結線: 受け口を増やさず、Secret を描かない', () => {
  assert.deepStrictEqual(inboundExposures(ads), []);
  assert.ok(!ads.some((d) => d.kind === 'Secret'), '描画に Secret がある');
  assert.deepStrictEqual(secretLookingValues(all), []);
});

/** 道具が PATH に在ることを確かめる（無ければ試験を失敗にする。skip にしない。#2402 のレビュー 🟡2）。 */
function need(tool) {
  const r = spawnSync(tool, ['--version'], { encoding: 'utf8' });
  assert.ok(!r.error && r.status === 0, `${tool} が PATH に無い（CI の static-checks が導入する。手元では入れてから走らせる）`);
}
// AST の PrometheusRule（の形の写し）から spec を取り出し、promtool で読む。
const AST_FIXTURE = `# コメント
apiVersion: monitoring.coreos.com/v1
kind: PrometheusRule
metadata:
  name: ai-stock-trading-alerts
  labels:
    release: kube-prometheus-stack
spec:
  groups:
    - name: ai-stock-trading.trade-cycle
      rules:
        # 字下げされたコメント
        - alert: HoldingsUnknownOpenSkipped
          expr: |
            sum(increase(ast_trade_cycle_skipped_total{reason="HoldingsUnknownOpen"}[30m])) > 0
          for: 0m
          labels:
            severity: warning
          annotations:
            summary: "保有の照会が失敗"
`;
const extractSh = cmData(promCm, 'extract-ast-rules.sh') || '';
t('T-2402-48 AST の規則: PrometheusRule の spec を取り出し、promtool check rules を通す', () => {
  need('promtool');
  const src = fs.mkdtempSync(path.join(sandbox, 'ast-src-'));
  const out = fs.mkdtempSync(path.join(sandbox, 'ast-out-'));
  fs.writeFileSync(path.join(src, 'ai-stock-trading-alerts.yaml'), AST_FIXTURE);
  fs.writeFileSync(path.join(sandbox, 'extract.sh'), extractSh);
  const r = run('sh', [path.join(sandbox, 'extract.sh')], {
    env: { PATH: process.env.PATH, AST_RULES_SRC: src, AST_RULES_OUT: out, PROMTOOL: 'promtool' },
  });
  assert.strictEqual(r.status, 0, r.stdout + r.stderr);
  const got = fs.readFileSync(path.join(out, 'ai-stock-trading-alerts.yml'), 'utf8');
  assert.match(got, /^groups:\n  - name: ai-stock-trading\.trade-cycle$/m);
  assert.ok(!/PrometheusRule|metadata:/.test(got), 'spec の外を持ち込んでいる');
});
t('T-2402-48 AST の規則: 読めない規則なら Prometheus を起動させない（exit 1）', () => {
  need('promtool');
  const src = fs.mkdtempSync(path.join(sandbox, 'ast-src-'));
  const out = fs.mkdtempSync(path.join(sandbox, 'ast-out-'));
  fs.writeFileSync(path.join(src, 'broken.yaml'), AST_FIXTURE.replace('> 0', '> > 0'));
  fs.writeFileSync(path.join(sandbox, 'extract.sh'), extractSh);
  const r = run('sh', [path.join(sandbox, 'extract.sh')], {
    env: { PATH: process.env.PATH, AST_RULES_SRC: src, AST_RULES_OUT: out, PROMTOOL: 'promtool' },
  });
  assert.strictEqual(r.status, 1, r.stderr);
  assert.ok(r.stderr.includes('promtool が読めない'), '落ちた理由が promtool の検査ではない');
});
t('T-2402-48 AST の規則: ConfigMap が無い（空）なら警告して起動する（AstAlertRulesNotLoaded が鳴る）', () => {
  need('promtool');
  const src = fs.mkdtempSync(path.join(sandbox, 'ast-src-'));
  const out = fs.mkdtempSync(path.join(sandbox, 'ast-out-'));
  fs.writeFileSync(path.join(sandbox, 'extract.sh'), extractSh);
  const r = run('sh', [path.join(sandbox, 'extract.sh')], {
    env: { PATH: process.env.PATH, AST_RULES_SRC: src, AST_RULES_OUT: out, PROMTOOL: 'promtool' },
  });
  assert.strictEqual(r.status, 0, r.stdout + r.stderr);
  assert.match(r.stderr, /WARNING/);
});
t('T-2402-48 プラットフォームの規則と Prometheus の設定を promtool で読める', () => {
  need('promtool');
  assert.ok(/- alert: /.test(rules), 'プラットフォームの規則が空');
  const f = path.join(sandbox, 'platform-rules.yml');
  fs.writeFileSync(f, rules);
  const r = run('promtool', ['check', 'rules', f]);
  assert.strictEqual(r.status, 0, r.stdout + r.stderr);
});
// NFR-21, IADR-0692: クラウドの LLM 費用の月次予算の進みの警報。窓は保持（7 日）に合わせ、月次予算を 7/30 に按分して比べる。
const LLM_PACE_EXPR = 'sum by (llm_purpose, llm_currency) (increase(llm_cost_total[7d]))\n  > on (llm_purpose, llm_currency)\nmax by (llm_purpose, llm_currency) (llm_budget_monthly_limit) * 7 / 30\n';
t('IADR-0692 LLM の月次予算の進みの警報が、ゲートウェイのゲージを直近 7 日（保持と同じ）で見る', () => {
  const r = ruleOf('LlmMonthlyBudgetPaceExceeded');
  assert.strictEqual(r.expr, LLM_PACE_EXPR);
  assert.match(rules, /alert: LlmMonthlyBudgetPaceExceeded[\s\S]{0,400}labels: \{ severity: warning \}/, '重大度が warning でない');
  // 窓は保持を超えない（超えると黙って過小になる）。
  const dep = find(ods, 'Deployment', 'prometheus') || EMPTY;
  const args = ((((dep.obj || {}).spec || {}).template || {}).spec || {}).containers[0].args.join(' ');
  assert.match(args, /--storage\.tsdb\.retention\.time=7d/, '保持が 7 日でない（警報の窓と対で改める）');
});
t('IADR-0692 LLM の月次予算の進みの警報: promtool の単体試験（按分を超えたら鳴り、下なら鳴らず、上限の無い用途は鳴らない）', () => {
  need('promtool');
  // 描いた規則の式と for をそのまま使う（注釈は $value の小数で揺れるので、単体試験の照合から外す）。
  const r = ruleOf('LlmMonthlyBudgetPaceExceeded');
  assert.ok(r.expr && r.for, '警報の式か for が読めない');
  const f = path.join(sandbox, 'llm-pace-rules.yml');
  fs.writeFileSync(f, `groups:\n  - name: llm-cost-budget\n    rules:\n      - alert: LlmMonthlyBudgetPaceExceeded\n        expr: ${JSON.stringify(r.expr)}\n        for: ${r.for}\n        labels: { severity: warning }\n`);
  // 5 分ごとに 8 日分。trade-decision は月次 135 → 7 日の按分 31.5。5 分あたり 0.02 → 7 日で 40.32（超える）。
  // report-daily は月次 30 → 按分 7。5 分あたり 0.0025 → 7 日で 5.04（超えない）。other はゲージが無い（鳴らない）。
  const series = (purpose, step) => `      - series: 'llm_cost_total{llm_purpose="${purpose}", llm_currency="USD"}'\n        values: '0+${step}x2304'\n`;
  const gauge = (purpose, v) => `      - series: 'llm_budget_monthly_limit{llm_purpose="${purpose}", llm_currency="USD"}'\n        values: '${v}x2304'\n`;
  const test = `rule_files:\n  - ${f}\nevaluation_interval: 5m\ntests:\n  - interval: 5m\n    input_series:\n`
    + series('trade-decision', 0.02) + gauge('trade-decision', 135)
    + series('report-daily', 0.0025) + gauge('report-daily', 30)
    + series('other', 1)
    + `    alert_rule_test:\n      - eval_time: 8d\n        alertname: LlmMonthlyBudgetPaceExceeded\n        exp_alerts:\n          - exp_labels: { severity: warning, llm_purpose: trade-decision, llm_currency: USD }\n`;
  const tf = path.join(sandbox, 'llm-pace.test.yml');
  fs.writeFileSync(tf, test);
  const ok = run('promtool', ['test', 'rules', tf]);
  assert.strictEqual(ok.status, 0, ok.stdout + ok.stderr);
  // 陰性対照: 期待を外すと promtool が落ちる（試験が空回りしていない）。
  fs.writeFileSync(tf, test.replace(/exp_alerts:\n[\s\S]*$/, 'exp_alerts: []\n'));
  const ng = run('promtool', ['test', 'rules', tf]);
  assert.notStrictEqual(ng.status, 0, '鳴るはずの警報が鳴らなくても promtool が通る');
});
t('T-2402-47 描いた Alertmanager の設定を amtool check-config で読める', () => {
  need('amtool');
  const r0 = renderWith(FAKE_URL);
  const f = path.join(sandbox, 'am-rendered.yml');
  fs.writeFileSync(f, r0.config);
  const r = run('amtool', ['check-config', f]);
  assert.strictEqual(r.status, 0, r.stdout + r.stderr);
  // 束ねと抑止はローカルの経路 B と同じ（違うのは受信先だけ）。
  const local = read('deploy/local/observability/alertmanager.yaml');
  for (const line of ["group_by: ['alertname', 'job']", 'source_matchers: [severity = "critical"]', "equal: ['alertname', 'job']"]) {
    assert.ok(amTmpl.includes(line) && local.includes(line), line);
  }
});

// ---------------------------------------------------------------- runbook
const RUNBOOK = 'docs/operations/cloud-backup-alerts-heartbeat-runbook.md';
const rb = fs.existsSync(path.join(REPO_ROOT, RUNBOOK)) ? read(RUNBOOK) : '';
t('T-2402-60 runbook: heartbeat が途絶えたとき外の監視がオーナーへ知らせる手順がある', () => {
  assert.ok(rb, `${RUNBOOK} が無い`);
  for (const w of ['Healthchecks.io', 'Period', 'Grace', 'alertmanager-heartbeat', 'ping-url', 'discord-ping-url', 'discord-fail-url', 'check B', '途絶えたとき', 'scale deploy/alertmanager --replicas=0']) {
    assert.ok(rb.includes(w), `runbook に「${w}」が無い`);
  }
});
t('T-2402-61 runbook: Discord・オブジェクトストレージ（Object Lock・lifecycle）・リストア試験の手順がある', () => {
  for (const w of ['alertmanager-discord', 'webhook-url', 'platform-backup-s3', 'platform-backup-s3-target', 'object-lock.json', 'bucket-lifecycle.json', '--from-s3', 'ast-prometheus-rules']) {
    assert.ok(rb.includes(w), `runbook に「${w}」が無い`);
  }
});
t('T-2402-62 runbook と deploy/cloud に秘密の値が無い', () => {
  assert.deepStrictEqual(secretLookingValues(rb), []);
  for (const f of ['bucket-lifecycle.json', 'object-lock.json', 'image/Dockerfile']) {
    assert.deepStrictEqual(secretLookingValues(read('deploy/cloud/backup', f)), [], f);
  }
});

console.log(`\ncloud-ops.test.js: ${passed} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
