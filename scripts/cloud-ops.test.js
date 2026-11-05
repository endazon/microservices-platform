#!/usr/bin/env node
'use strict';
/*
 * cloud-ops.test.js
 * NFR-21, NFR-03, NFR-05, ADR-0002, ADR-0006, IADR-0672 (#2402):
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

/** 描画結果を文書ごとに分ける。各文書は { kind, name, text }。 */
function docs(yaml) {
  return yaml
    .split(/^---$/m)
    .map((t) => t.trim())
    .filter(Boolean)
    .map((text) => ({
      kind: (/^kind: (\S+)/m.exec(text) || [])[1],
      name: (/^metadata:\n(?:  .*\n)*?  name: (\S+)/m.exec(text + '\n') || [])[1],
      text,
    }));
}
const find = (ds, kind, name) => ds.find((d) => d.kind === kind && d.name === name);

/** ConfigMap の data のキーの中身（ブロックスカラー `key: |` の字下げを外したもの）。 */
function cmData(doc, key) {
  const lines = doc.text.split('\n');
  const i = lines.findIndex((l) => new RegExp(`^  ${key.replace(/\./g, '\\.')}: \\|`).test(l));
  if (i < 0) return undefined;
  const out = [];
  for (let j = i + 1; j < lines.length; j += 1) {
    if (lines[j].trim() !== '' && !/^ {4}/.test(lines[j])) break;
    out.push(lines[j].slice(4));
  }
  return out.join('\n').replace(/\n+$/, '\n');
}

// ---------------------------------------------------------------- 判定（純関数）

/** 受け口を増やす資源（LoadBalancer・NodePort・hostPort・hostNetwork・Ingress）を挙げる。 */
function inboundExposures(yaml) {
  const out = [];
  if (/^\s*type: (LoadBalancer|NodePort)\s*$/m.test(yaml)) out.push('Service type LoadBalancer/NodePort');
  if (/^\s*-?\s*hostPort:/m.test(yaml)) out.push('hostPort');
  if (/^\s*hostNetwork: true/m.test(yaml)) out.push('hostNetwork');
  if (/^kind: (Ingress|Gateway|HTTPRoute)$/m.test(yaml)) out.push('Ingress/Gateway');
  return out;
}

/** 秘密の値に見えるもの（Discord の webhook・Healthchecks の ping の UUID・S3 の鍵）を挙げる。 */
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

/** env の名前が秘密を示すのに value を直書きしているものを挙げる。 */
function inlineSecretEnv(yaml) {
  const out = [];
  const re = /- name: (\S*(?:PASSWORD|SECRET|TOKEN|ACCESS_KEY|WEBHOOK)\S*)\n(\s+)value:/g;
  let m;
  while ((m = re.exec(yaml))) out.push(m[1]);
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
t('T-2402-49 変異: LoadBalancer・hostPort・Ingress を拾う', () => {
  assert.strictEqual(inboundExposures('spec:\n  type: LoadBalancer\n').length, 1);
  assert.strictEqual(inboundExposures('ports:\n  - containerPort: 1\n    hostPort: 80\n').length, 1);
  assert.strictEqual(inboundExposures('kind: Ingress\n').length, 1);
  assert.strictEqual(inboundExposures('spec:\n  type: ClusterIP\n').length, 0);
});
t('T-2402-49 変異: 秘密の値に見えるものを拾う', () => {
  assert.strictEqual(secretLookingValues('url: https://discord.com/api/webhooks/123456/abcdefghijklmnop').length, 1);
  assert.strictEqual(secretLookingValues('https://hc-ping.com/0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b').length, 1);
  assert.strictEqual(secretLookingValues("webhook_url: '__DISCORD_WEBHOOK_URL__'").length, 0);
  assert.deepStrictEqual(inlineSecretEnv('- name: RCLONE_CONFIG_X_SECRET_ACCESS_KEY\n  value: x\n'), ['RCLONE_CONFIG_X_SECRET_ACCESS_KEY']);
  assert.deepStrictEqual(inlineSecretEnv('- name: RCLONE_CONFIG_X_SECRET_ACCESS_KEY\n  valueFrom:\n'), []);
});

let backup = '';
let obs = '';
t('T-2402-30 deploy/cloud/backup と deploy/cloud/observability を描画できる', () => {
  backup = render('deploy/cloud/backup');
  obs = render('deploy/cloud/observability');
});
const bds = docs(backup);
const ods = docs(obs);

// ---------------------------------------------------------------- バックアップ
const cj = find(bds, 'CronJob', 'platform-backup-postgres') || { text: '' };
t('T-2402-30 バックアップ: CronJob platform-backup-postgres が出る', () => assert.ok(cj.text, 'CronJob が無い'));
t('T-2402-30 バックアップ: hostPath を持たない（保管先はクラスタ外のオブジェクトストレージ）', () =>
  assert.ok(!/hostPath:/.test(backup), 'hostPath がある'));
t('T-2402-31 バックアップ: BACKUP_TARGETS=none・宛先は ConfigMap・資格情報は secretKeyRef', () => {
  assert.match(cj.text, /- name: BACKUP_TARGETS\n\s+value: none/);
  assert.match(cj.text, /- name: BACKUP_S3_DEST\n\s+valueFrom:\n\s+configMapKeyRef:\n(?:\s+.*\n)*?\s+name: platform-backup-s3-target/);
  for (const k of ['RCLONE_CONFIG_OBJSTORE_ACCESS_KEY_ID', 'RCLONE_CONFIG_OBJSTORE_SECRET_ACCESS_KEY', 'PGPASSWORD']) {
    assert.match(cj.text, new RegExp(`- name: ${k}\\n\\s+valueFrom:\\n\\s+secretKeyRef:`), `${k} が secretKeyRef でない`);
  }
  assert.deepStrictEqual(inlineSecretEnv(backup), []);
  assert.ok(!/^kind: Secret$/m.test(backup), '描画に Secret がある（資格情報をリポジトリに置かない）');
});
const dockerCloud = read('deploy/cloud/backup/image/Dockerfile');
const dockerLocal = read('deploy/local/platform-backup/image/Dockerfile');
const argOf = (src, n) => (new RegExp(`^ARG ${n}=(\\S+)$`, 'm').exec(src) || [])[1];
t('T-2402-32 バックアップ: イメージは自製・pull しない・タグはイメージの中身の版から作る', () => {
  const m = /image: (microservices-platform\/platform-backup-cloud:(\S+))/.exec(cj.text);
  assert.ok(m, 'イメージが microservices-platform/platform-backup-cloud でない');
  assert.match(cj.text, /imagePullPolicy: Never/);
  const pg = /postgres:(\d+\.\d+)-alpine/.exec(dockerCloud)[1];
  assert.strictEqual(m[2], `pg${pg}-age${argOf(dockerCloud, 'AGE_VERSION')}-rclone${argOf(dockerCloud, 'RCLONE_VERSION')}`);
});
t('T-2402-33 バックアップ: ベースと age の固定はローカルのイメージと同じ（片側だけ上げない）', () => {
  const from = (s) => /^FROM (.+)$/m.exec(s)[1];
  assert.strictEqual(from(dockerCloud), from(dockerLocal));
  for (const n of ['BASE_REGISTRY', 'AGE_VERSION', 'AGE_APK_SHA256_X86_64', 'AGE_APK_SHA256_AARCH64', 'ALPINE_BRANCH']) {
    assert.strictEqual(argOf(dockerCloud, n), argOf(dockerLocal, n), n);
  }
  assert.match(argOf(dockerCloud, 'RCLONE_APK_SHA256_X86_64') || '', /^[0-9a-f]{64}$/);
  assert.match(argOf(dockerCloud, 'RCLONE_APK_SHA256_AARCH64') || '', /^[0-9a-f]{64}$/);
  assert.ok(!/--allow-untrusted/.test(dockerCloud), '署名の検証を外している');
});
t('T-2402-34 バックアップ: 本体スクリプトはローカルと同じもの（二重に持たない）', () => {
  const cm = find(bds, 'ConfigMap', 'platform-backup-script');
  assert.ok(cm, 'ConfigMap platform-backup-script が無い');
  // 同じ configMapGenerator（deploy/local/platform-backup/script）を取り込む。コピーを置かない。
  assert.match(read('deploy/cloud/backup/kustomization.yaml'), /^  - \.\.\/\.\.\/local\/platform-backup\/script$/m);
  assert.ok(!fs.existsSync(path.join(REPO_ROOT, 'deploy/cloud/backup/backup.sh')), 'backup.sh の写しがある');
  // 本体の S3 の経路の印が描画に入っている（古い本体を取り込んでいない）。
  assert.ok(cm.text.includes('publish_s3'), '描画の本体に S3 の経路が無い');
});
t('T-2402-35 バックアップ: 米国市場の時間帯を避け、並行させず、再試行しない', () => {
  assert.match(cj.text, /schedule: 0 12 \* \* \*/);
  assert.match(cj.text, /timeZone: Asia\/Tokyo/);
  assert.match(cj.text, /concurrencyPolicy: Forbid/);
  assert.match(cj.text, /backoffLimit: 0/);
  const sd = Number((/startingDeadlineSeconds: (\d+)/.exec(cj.text) || [])[1]);
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
  assert.ok(ret.Days <= days('/daily/'), 'Object Lock の保持が日次の保持より長い（期限で消せない）');
  for (const r of lc.Rules) assert.ok(r.NoncurrentVersionExpiration, `${r.ID}: 古い版を消さないと容量が積み上がる`);
});
const np = find(bds, 'NetworkPolicy', 'platform-backup-egress') || { text: '' };
t('T-2402-38 バックアップ: 外への通信を絞る（DNS・Postgres・443 だけ。受けは無し）', () => {
  assert.ok(np.text, 'NetworkPolicy platform-backup-egress が無い');
  assert.match(np.text, /policyTypes:\n\s+- Egress/);
  const ports = [...np.text.matchAll(/port: (\d+)/g)].map((m) => Number(m[1])).sort((a, b) => a - b);
  assert.deepStrictEqual([...new Set(ports)], [53, 443, 5432]);
});

// ---------------------------------------------------------------- 警報・heartbeat
const amCm = find(ods, 'ConfigMap', 'alertmanager-cloud-config') || { text: '' };
const amTmpl = cmData(amCm, 'alertmanager.yml.tmpl') || '';
const renderSh = cmData(amCm, 'render.sh') || '';
const amDep = find(ods, 'Deployment', 'alertmanager') || { text: '' };
t('T-2402-40 Alertmanager: 描画に Discord の receiver があり、既定の route がそこへ向く', () => {
  assert.match(amTmpl, /^  - name: discord\n    discord_configs:\n      - webhook_url: '__DISCORD_WEBHOOK_URL__'/m);
  assert.match(amTmpl, /^route:\n(?:  .*\n)*?  receiver: discord$/m);
  assert.ok(!/default-null/.test(amTmpl), '既定の受信先が null のまま');
});
t('T-2402-41 Alertmanager: webhook の URL の値が描画に含まれない（Secret から読む）', () => {
  assert.deepStrictEqual(secretLookingValues(obs), []);
  assert.deepStrictEqual(secretLookingValues(backup), []);
  assert.ok(!/^kind: Secret$/m.test(obs), '描画に Secret がある');
  assert.match(amDep.text, /- name: DISCORD_WEBHOOK_URL\n\s+valueFrom:\n\s+secretKeyRef:\n(?:\s+.*\n)*?\s+name: alertmanager-discord/);
  assert.deepStrictEqual(inlineSecretEnv(obs), []);
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
function renderWith(url, ping = FAKE_PING, fail = `${FAKE_PING}/fail`) {
  const outDir = fs.mkdtempSync(path.join(sandbox, 'out-'));
  for (const [k, v] of [['ping-url', ping], ['fail-url', fail]]) {
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
  assert.ok(!r.logs.includes('00000000-0000'), 'ログに ping の URL が出ている');
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
for (const [label, ping, fail] of [
  ['ping の URL が無い', null, `${FAKE_PING}/fail`],
  ['fail の URL が /fail で終わらない', FAKE_PING, FAKE_PING],
  ['ping の URL が https でない', 'http://hc-ping.com/x', 'http://hc-ping.com/x/fail'],
]) {
  t(`T-2402-44 render.sh: heartbeat の ${label}なら起動させない（heartbeat の止まった構成で動かない）`, () => {
    const r = renderWith(FAKE_URL, ping, fail);
    assert.notStrictEqual(r.status, 0, r.logs);
  });
}
t('T-2402-45 heartbeat: Watchdog を heartbeat の receiver（url_file）へ 5 分ごとに送る', () => {
  assert.match(amTmpl, /- matchers: \[alertname = "Watchdog"\]\n\s+receiver: heartbeat\n(?:\s+.*\n)*?\s+repeat_interval: 5m/);
  assert.match(amTmpl, /- name: heartbeat\n\s+webhook_configs:\n\s+- url_file: \/etc\/alertmanager\/heartbeat\/ping-url/);
  assert.match(amTmpl, /- matchers: \[alertname = "AlertmanagerDiscordFailing"\]\n\s+receiver: heartbeat-fail/);
  assert.match(amTmpl, /- name: heartbeat-fail\n\s+webhook_configs:\n\s+- url_file: \/etc\/alertmanager\/heartbeat\/fail-url/);
  // ping の Secret のボリュームは optional でない（無ければ Pod が起きず、ping が止まって見える）。
  assert.match(amDep.text, /secret:\n\s+secretName: alertmanager-heartbeat\n/);
  assert.ok(!/secretName: alertmanager-heartbeat\n\s+optional: true/.test(amDep.text), 'heartbeat の Secret が optional');
});
const promCm = find(ods, 'ConfigMap', 'prometheus-cloud-config') || { text: '' };
const rules = cmData(promCm, 'platform-rules.yml') || '';
t('T-2402-45 heartbeat: 常に鳴る警報 Watchdog と、Discord の失敗・AST の規則の不在の警報がある', () => {
  assert.match(rules, /- alert: Watchdog\n\s+expr: vector\(1\)/);
  assert.match(rules, /- alert: AlertmanagerDiscordFailing\n\s+expr: .*alertmanager_notifications_failed_total\{integration="discord"\}/);
  assert.match(rules, /- alert: AstAlertRulesNotLoaded\n\s+expr: absent\(prometheus_rule_group_rules\{rule_group=~".*;ai-stock-trading\\\\\..*"\}\)/);
});
t('T-2402-46 受け口を増やさない（LoadBalancer・NodePort・hostPort・Ingress が無い）', () => {
  assert.deepStrictEqual(inboundExposures(obs), []);
  assert.deepStrictEqual(inboundExposures(backup), []);
});
t('T-2402-46 Grafana・Loki・Tempo を持ち込まない', () => {
  assert.ok(!/image: \S*(grafana|loki|tempo)/i.test(obs), 'Grafana・Loki・Tempo のイメージがある');
});
t('T-2402-46 イメージはローカルの経路 B と同じ digest（片側だけ上げない）', () => {
  const local = read('deploy/local/observability/alertmanager.yaml') + read('deploy/local/observability/prometheus.yaml');
  const imgs = [...obs.matchAll(/image: (\S+)/g)].map((m) => m[1]);
  assert.ok(imgs.length >= 4, `イメージが少ない: ${imgs}`);
  for (const i of imgs) assert.ok(local.includes(i), `経路 B に無いイメージ: ${i}`);
});
const promDep = find(ods, 'Deployment', 'prometheus') || { text: '' };
t('T-2402-47 Prometheus: 保持は 7 日・1GB 以内、メモリの上限は 768Mi 以内（8 GB のノード）', () => {
  const time = (/--storage\.tsdb\.retention\.time=(\S+?)"?$/m.exec(promDep.text) || [])[1];
  const size = (/--storage\.tsdb\.retention\.size=(\S+?)"?$/m.exec(promDep.text) || [])[1];
  assert.ok(durationSeconds(time) <= 7 * 86400, `retention.time=${time}`);
  assert.ok(bytesOf(size) <= 1e9, `retention.size=${size}`);
  const lim = /\n {8}name: prometheus\n(?: {8,}.*\n)*? {8,}limits:\n +cpu: .*\n +memory: (\S+)/.exec(promDep.text);
  assert.ok(lim && bytesOf(lim[1]) <= 768 * 2 ** 20, `memory limit=${lim && lim[1]}`);
  assert.match(promDep.text, /--web\.enable-remote-write-receiver/);
});

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
  const src = fs.mkdtempSync(path.join(sandbox, 'ast-src-'));
  const out = fs.mkdtempSync(path.join(sandbox, 'ast-out-'));
  fs.writeFileSync(path.join(src, 'broken.yaml'), AST_FIXTURE.replace('> 0', '> > 0'));
  fs.writeFileSync(path.join(sandbox, 'extract.sh'), extractSh);
  const r = run('sh', [path.join(sandbox, 'extract.sh')], {
    env: { PATH: process.env.PATH, AST_RULES_SRC: src, AST_RULES_OUT: out, PROMTOOL: 'promtool' },
  });
  assert.notStrictEqual(r.status, 0);
});
t('T-2402-48 AST の規則: ConfigMap が無い（空）なら警告して起動する（AstAlertRulesNotLoaded が鳴る）', () => {
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
  assert.ok(/- alert: /.test(rules), 'プラットフォームの規則が空');
  const f = path.join(sandbox, 'platform-rules.yml');
  fs.writeFileSync(f, rules);
  const r = run('promtool', ['check', 'rules', f]);
  assert.strictEqual(r.status, 0, r.stdout + r.stderr);
});
t('T-2402-47 描いた Alertmanager の設定を amtool check-config で読める', () => {
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
  for (const w of ['Healthchecks.io', 'Period', 'Grace', 'alertmanager-heartbeat', 'ping-url', 'fail-url', '途絶えたとき', 'scale deploy/alertmanager --replicas=0']) {
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
