#!/usr/bin/env node
'use strict';
/*
 * helm-pod-security.test.js
 * NFR, IADR-0583（Refs GHSA-j4qv-2w99-xg5p）:
 * **チャートの Pod とコンテナのセキュリティ設定を、実際に `helm template` で描いて固定する。**
 *
 * 固定するもの:
 *   1. 既定（本番像）・values-local・任意部品を全て有効にした描画で、Pod を持つ全ての資源（Deployment / Job）が
 *      seccomp RuntimeDefault を持ち、全てのコンテナが allowPrivilegeEscalation: false を持つ。
 *   2. チャートが作るイメージと利用者 ID が分かっている汎用イメージ（外部 3 つ以外の全て）は restricted の形:
 *      runAsNonRoot: true・runAsUser / runAsGroup が 1 以上・全コンテナが capabilities.drop: ["ALL"]。
 *   3. 専用の ServiceAccount を持たない Pod は automountServiceAccountToken: false。持つ Pod（BFF・datasource）は付けない。
 *   4. Namespace のラベル: warn / audit は restricted。enforce はメッシュの注入が無いときだけ baseline。
 *   5. 描画で止まる: uid / gid が 0、水準が値域の外。ドリフト検出の Job は curl イメージの uid:gid（100:101）。
 *   6. 供給側と一致する: frontend の Dockerfile の USER ＝ frontend.runAsUser、合成監視の overlay ＝ チャート、
 *      k8s-local-up.sh が Namespace へ貼る水準 ＝ チャートの既定。
 *   7. 変異: 描画物から設定を 1 つ抜くと本試験の判定が赤になる（判定が空振りしない）。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** CI は static-checks-units で走らせる。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/helm-pod-security.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const VALUES_LOCAL = 'deploy/local/values-local.yaml';
const FRONTEND_DOCKERFILE = 'src/platform/frontend/Dockerfile';
const OVERLAY_SYNTHETIC = 'deploy/local/synthetic-monitor/synthetic-monitor.yaml';
const LOCAL_UP = 'scripts/k8s-local-up.sh';
// 外部のイメージ（利用者 ID とデータの所有者を変えない。IADR-0583 決定 2）。
const THIRD_PARTY = new Set(['seaweedfs', 'wiki-js', 'embedding-service']);
// 専用の ServiceAccount で k8s API（Vault の k8s auth・ExternalSecret の同期）を使う Pod。
const WITH_SA = new Set(['bff-service', 'datasource-service']);

const read = (rel) => fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

function hasHelm() {
  const probe = spawnSync(process.platform === 'win32' ? 'where' : 'which', ['helm'], { encoding: 'utf8' });
  return probe.status === 0;
}
function render(args) {
  return spawnSync('helm', ['template', 'msp', CHART, ...args], { cwd: REPO_ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
}
function mustRender(args) {
  const r = render(args);
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.stderr}`);
  return r.stdout;
}

/** Pod テンプレートを持つ資源を { kind, app, text } で返す。 */
function podOwners(rendered) {
  return rendered.split(/^---\s*$/m)
    .filter((t) => /^kind:\s*(Deployment|Job|StatefulSet|DaemonSet|CronJob)\s*$/m.test(t))
    .map((text) => ({
      kind: text.match(/^kind:\s*(\S+)/m)[1],
      app: ((text.match(/^ {2}labels:\n {4}app:\s*(\S+)/m) || [])[1] || ''),
      text,
    }));
}

/** Pod spec（`    spec:` の下）を、字下げ 6 の鍵ごとの塊で返す。 */
function podSpecKeys(text) {
  const lines = text.split('\n');
  const at = lines.findIndex((l) => /^ {4}spec:\s*$/.test(l));
  assert.ok(at >= 0, 'Pod spec が見つからない');
  const blocks = new Map();
  let cur = null;
  for (let i = at + 1; i < lines.length; i++) {
    const l = lines[i];
    if (l.trim() === '' || /^\s*#/.test(l)) continue;
    const ind = l.match(/^(\s*)/)[1].length;
    if (ind < 6) break;
    const m = l.match(/^ {6}([A-Za-z]+):\s*(.*)$/);
    if (m) {
      cur = m[1];
      blocks.set(cur, [l]);
    } else if (cur) {
      blocks.get(cur).push(l);
    }
  }
  return blocks;
}

/** containers / initContainers の各コンテナの塊（`        - name:` から次の同じ字下げの `- ` まで）。 */
function containerBlocks(specKeys) {
  const out = [];
  for (const key of ['containers', 'initContainers']) {
    const lines = specKeys.get(key);
    if (!lines) continue;
    let cur = null;
    for (const l of lines.slice(1)) {
      if (/^ {8}- /.test(l)) {
        cur = [l];
        out.push(cur);
      } else if (cur) {
        cur.push(l);
      }
    }
  }
  return out.map((ls) => ls.join('\n'));
}

/** 1 つの資源の違反を文字列の配列で返す（空 = 適合）。 */
function violations(owner) {
  const v = [];
  const spec = podSpecKeys(owner.text);
  const podSc = (spec.get('securityContext') || []).join('\n');
  if (!/seccompProfile:\n\s+type: RuntimeDefault/.test(podSc)) v.push('Pod の seccompProfile が RuntimeDefault でない');
  const containers = containerBlocks(spec);
  if (containers.length === 0) v.push('コンテナが見つからない');
  for (const c of containers) {
    const name = (c.match(/- name:\s*(\S+)/) || [])[1];
    if (!/allowPrivilegeEscalation: false/.test(c)) v.push(`${name}: allowPrivilegeEscalation: false が無い`);
    if (!THIRD_PARTY.has(owner.app) && !/capabilities:\n\s+drop: \["ALL"\]/.test(c)) v.push(`${name}: capabilities.drop ALL が無い`);
  }
  if (!THIRD_PARTY.has(owner.app)) {
    if (!/runAsNonRoot: true/.test(podSc)) v.push('runAsNonRoot: true が無い');
    for (const k of ['runAsUser', 'runAsGroup']) {
      const m = podSc.match(new RegExp(`${k}: (\\d+)`));
      if (!m || Number(m[1]) < 1) v.push(`${k} が 1 以上でない`);
    }
  }
  const hasSa = spec.has('serviceAccountName');
  const automountOff = /^ {6}automountServiceAccountToken: false$/m.test((spec.get('automountServiceAccountToken') || []).join('\n'));
  if (!hasSa && !automountOff) v.push('専用の SA が無いのに automountServiceAccountToken: false が無い');
  if (hasSa && automountOff) v.push('専用の SA を持つのにトークンを外している（k8s API が使えなくなる）');
  return v;
}

function namespaceLabels(rendered) {
  const doc = rendered.split(/^---\s*$/m).find((t) => /^kind:\s*Namespace\s*$/m.test(t));
  if (!doc) return null;
  const labels = {};
  for (const m of doc.matchAll(/^ {4}(pod-security\.kubernetes\.io\/[a-z-]+): "?([a-z]+)"?\s*$/gm)) labels[m[1]] = m[2];
  return labels;
}

function main() {
  if (!hasHelm()) {
    console.error('helm-pod-security: helm が見つからない（fail-closed。CI は azure/setup-helm で入れる）');
    process.exit(1);
  }

  const CONFIGS = {
    default: [],
    local: ['-f', VALUES_LOCAL],
    // 任意部品（合成監視・TEI）も描く。メッシュ無し（注入なし）の Namespace のラベルもここで見る。
    all: ['--set', 'syntheticMonitor.enabled=true', '--set', 'embedding.enabled=true', '--set', 'mesh.enabled=false'],
  };
  const rendered = Object.fromEntries(Object.entries(CONFIGS).map(([k, a]) => [k, mustRender(a)]));
  const valuesText = read(`${CHART}/values.yaml`);

  for (const [cfg, text] of Object.entries(rendered)) {
    ok(`${cfg}: Pod を持つ全ての資源が seccomp・権限昇格なし・（外部 3 つ以外は）非 root とケーパビリティなし・トークンの扱い`, () => {
      const owners = podOwners(text);
      assert.ok(owners.length >= 10, `Pod を持つ資源が少なすぎる（${owners.length}）。描画か抽出が壊れている`);
      const bad = owners.map((o) => [o, violations(o)]).filter(([, v]) => v.length > 0);
      assert.deepStrictEqual(bad.map(([o, v]) => `${o.kind}/${o.app}: ${v.join(' / ')}`), []);
    });
  }

  ok('all: 想定した資源が実際に描かれている（陰性対照が空振りしない）', () => {
    const apps = new Set(podOwners(rendered.all).map((o) => o.app));
    for (const a of ['bff-service', 'datasource-service', 'frontend-service', 'seaweedfs', 'wiki-js', 'embedding-service', 'synthetic-monitor', 'config-drift-postsync']) {
      assert.ok(apps.has(a), `${a} が描かれていない`);
    }
    for (const a of WITH_SA) {
      const o = podOwners(rendered.all).find((x) => x.app === a);
      assert.ok(podSpecKeys(o.text).has('serviceAccountName'), `${a} が専用の SA を持っていない`);
    }
  });

  ok('.NET の全 Deployment は aspnet の APP_UID（podSecurity.dotnetRunAsUser）で動く', () => {
    const uid = Number(valuesText.match(/^ {2}dotnetRunAsUser: (\d+)/m)[1]);
    assert.strictEqual(uid, 1654, 'aspnet イメージの APP_UID は 1654');
    const dotnet = podOwners(rendered.default).filter((o) => /-service$/.test(o.app) && !['frontend-service', 'embedding-service'].includes(o.app));
    assert.ok(dotnet.length >= 10, `.NET の Deployment が少なすぎる（${dotnet.length}）`);
    for (const o of dotnet) assert.match(o.text, new RegExp(`runAsUser: ${uid}\\n`), `${o.app} の runAsUser が ${uid} でない`);
  });

  ok('ドリフト検出の Job は curl イメージの curl_user:curl_group（100:101。gid は uid と違う番号）で動く', () => {
    const job = podOwners(rendered.default).find((o) => o.app === 'config-drift-postsync');
    assert.ok(job, 'config-drift-postsync が描かれていない');
    const v = (k) => valuesText.match(new RegExp(`^drift:\\n(?:.*\\n)*? {4}${k}: (\\d+)`, 'm'))[1];
    assert.deepStrictEqual([v('runAsUser'), v('runAsGroup')], ['100', '101'], 'values の drift.postSyncHook の uid / gid がイメージと食い違う');
    assert.match(job.text, /runAsUser: 100\n\s+runAsGroup: 101\n/, 'Job の runAsGroup が curl_group（101）でない');
    // gid を与えない部品の呼び出しは従前どおり uid と同じ番号になる（.NET の 1654:1654）。
    const bff = podOwners(rendered.default).find((o) => o.app === 'bff-service');
    assert.match(bff.text, /runAsUser: 1654\n\s+runAsGroup: 1654\n/);
  });

  ok('Namespace: 注入あり（既定）は enforce を描かず warn / audit = restricted、注入なしは enforce = baseline', () => {
    assert.deepStrictEqual(namespaceLabels(rendered.default), {
      'pod-security.kubernetes.io/warn': 'restricted',
      'pod-security.kubernetes.io/audit': 'restricted',
    });
    assert.deepStrictEqual(namespaceLabels(rendered.all), {
      'pod-security.kubernetes.io/enforce': 'baseline',
      'pod-security.kubernetes.io/warn': 'restricted',
      'pod-security.kubernetes.io/audit': 'restricted',
    });
    // 経路B はチャートが Namespace を描かない（namespace.create=false）。k8s-local-up.sh が貼る（下の試験）。
    assert.strictEqual(namespaceLabels(rendered.local), null);
  });

  ok('描画で止まる: uid / gid が 0・文字列・水準が値域の外', () => {
    for (const args of [
      ['--set', 'podSecurity.dotnetRunAsUser=0'],
      ['--set', 'frontend.runAsUser=0'],
      ['--set', 'drift.postSyncHook.runAsGroup=0'],
      ['--set-string', 'podSecurity.dotnetRunAsUser=root'],
      ['--set', 'mesh.enabled=false', '--set', 'podSecurity.admission.enforce=strict'],
      ['--set', 'podSecurity.admission.warn=none'],
    ]) {
      const r = render(args);
      assert.notStrictEqual(r.status, 0, `${args.join(' ')} で描画が通った`);
      assert.match(r.stderr, /IADR-0583/, `${args.join(' ')} の止まる理由が違う: ${r.stderr.slice(-300)}`);
    }
  });

  ok('供給側: frontend の Dockerfile の USER は frontend.runAsUser と同じ uid で、caddy のファイルケーパビリティを外している', () => {
    const df = read(FRONTEND_DOCKERFILE);
    const uid = valuesText.match(/^frontend:\n(?:.*\n)*? {2}runAsUser: (\d+)/m)[1];
    const users = [...df.matchAll(/^USER (\S+)\s*$/gm)].map((m) => m[1]);
    assert.deepStrictEqual(users, [`${uid}:${uid}`], 'Dockerfile の USER が chart と食い違う');
    assert.match(df, /cp \/usr\/bin\/caddy \/tmp\/caddy \\\n\s+&& mv \/tmp\/caddy \/usr\/bin\/caddy/, 'ファイルケーパビリティを外していない');
    assert.match(df, new RegExp(`chown ${uid}:${uid} /usr/share/caddy/config.js`), 'entrypoint が描く config.js を書けない');
    // USER は最後の段（runtime）の中、ENTRYPOINT より前にある。
    const runtimeAt = df.lastIndexOf('\nFROM ');
    assert.ok(df.indexOf('\nUSER ') > runtimeAt && df.indexOf('\nUSER ') < df.indexOf('\nENTRYPOINT '), 'USER の位置が違う');
  });

  ok('供給側: 合成監視の overlay はチャートと同じ Pod / コンテナの設定を持つ', () => {
    const overlay = read(OVERLAY_SYNTHETIC);
    const chart = podOwners(rendered.all).find((o) => o.app === 'synthetic-monitor');
    const pick = (t) => ({
      runAsUser: (t.match(/runAsUser: (\d+)/) || [])[1],
      runAsGroup: (t.match(/runAsGroup: (\d+)/) || [])[1],
      runAsNonRoot: /runAsNonRoot: true/.test(t),
      seccomp: /seccompProfile:\n\s+type: RuntimeDefault/.test(t),
      noEscalation: /allowPrivilegeEscalation: false/.test(t),
      dropAll: /capabilities:\n\s+drop: \["ALL"\]/.test(t),
      automountOff: /automountServiceAccountToken: false/.test(t),
    });
    assert.deepStrictEqual(pick(overlay), pick(chart.text));
    assert.strictEqual(pick(overlay).runAsUser, '1000');
  });

  ok('供給側: k8s-local-up.sh が Namespace へ貼る水準はチャートの既定と同じ（ISTIO=1 では enforce を外す）', () => {
    const sh = read(LOCAL_UP);
    const level = (k) => valuesText.match(new RegExp(`^ {4}${k}: (\\S+)`, 'm'))[1];
    const istio = sh.match(/kubectl label namespace "\$MSP_NS" (pod-security[^\n]*enforce- --overwrite)/);
    const plain = sh.match(/kubectl label namespace "\$MSP_NS" (pod-security\.kubernetes\.io\/enforce=[^\n]*--overwrite)/);
    assert.ok(istio && plain, 'k8s-local-up.sh に Pod Security Admission のラベル付けが無い');
    assert.match(istio[1], new RegExp(`warn=${level('warn')} .*audit=${level('audit')}`));
    assert.match(plain[1], new RegExp(`enforce=${level('enforce')} .*warn=${level('warn')} .*audit=${level('audit')}`));
  });

  ok('変異: 描画物から設定を 1 つ抜くと判定が赤になる', () => {
    const owners = podOwners(rendered.all);
    const pickApp = (a) => owners.find((o) => o.app === a);
    const mutate = (o, from, to) => ({ ...o, text: o.text.replace(from, to) });
    const cases = [
      [pickApp('bff-service'), /\n {12}allowPrivilegeEscalation: false/, ''],
      [pickApp('frontend-service'), /\n {12}capabilities:\n {14}drop: \["ALL"\]/, ''],
      [pickApp('synthetic-monitor'), /runAsNonRoot: true/, 'runAsNonRoot: false'],
      [pickApp('config-drift-postsync'), /runAsUser: \d+/, 'runAsUser: 0'],
      [pickApp('wiki-js'), /type: RuntimeDefault/, 'type: Unconfined'],
      [pickApp('seaweedfs'), /\n {6}automountServiceAccountToken: false/, ''],
      [pickApp('bff-service'), /\n {6}serviceAccountName: \S+/, ''],
    ];
    for (const [o, from, to] of cases) {
      assert.deepStrictEqual(violations(o), [], `${o.app}: 変異の前から赤い`);
      const m = mutate(o, from, to);
      assert.notStrictEqual(m.text, o.text, `${o.app}: 変異が当たらない（${from}）`);
      assert.ok(violations(m).length > 0, `${o.app}: ${from} を変えても判定が緑のまま`);
    }
  });

  console.log(`helm-pod-security: ${passed} tests passed`);
}

main();
