#!/usr/bin/env node
'use strict';
/*
 * helm-worker-probes.test.js
 * NFR-21, NFR-06, IADR-0602, #2140（親 #2047 指摘 L-11）:
 * **キュー駆動のワーカー（conversion / ingestion）にも liveness / readiness が描かれることを、実際に `helm template` で描いて固定する。**
 *
 * 従前 chart は `worker: true` で両者の HTTP probe を抑止していた。conversion は health を Map しているのに probe が無く、
 * ingestion は health を持たなかった（どちらも落ちても k8s が気づかない＝NFR-21 の「検出 5 分以内」を満たす手段が無い）。
 *
 * 固定するもの:
 *   1. 既定（本番像）・values-local の描画で、conversion-service と ingestion-service の Deployment が
 *      liveness `/health/live`・readiness `/health/ready` を、コンテナの HTTP ポート（最初の containerPort＝`$svc.port`）へ向けて持つ。
 *   2. HTTP のポートを持つ Deployment は**すべて** probe を持つ（ワーカーだけの例外を作らない）。
 *   3. 値 `worker` はもう chart のどこにも無い（意味の無い旗を残さない）。
 *   4. 実装側: 両サービスの Program.cs が `MapPlatformHealthChecks()` を呼ぶ（probe が叩く経路の在処）。
 *   5. 変異: (a) 描画物から ingestion の probe を外す、(b) 抑止の条件（`not $svc.worker`）を template へ戻して `worker=true` を与える、
 *      (c) Program.cs から Map を外す —— いずれも判定が赤になる（判定が空振りしない）。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** CI は static-checks-units で走らせる。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/helm-worker-probes.test.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const { RENDER_IMAGE_TAG_ARGS } = require('./lib/helm-render-tag'); // NFR-20, IADR-0605: chart はタグが無いと描画を落とす

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const VALUES_LOCAL = 'deploy/local/values-local.yaml';
const WORKERS = ['conversion-service', 'ingestion-service'];
const PROGRAMS = {
  'conversion-service': 'src/knowledge/backend/Services/ConversionService/Program.cs',
  'ingestion-service': 'src/knowledge/backend/Services/IngestionService/Program.cs',
};

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

function mustRender(chart, args) {
  const r = spawnSync('helm', ['template', 'msp', chart, ...RENDER_IMAGE_TAG_ARGS, ...args], { cwd: REPO_ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.stderr}`);
  return r.stdout;
}

/** 描画物の Deployment を { name, text } で返す。 */
function deployments(rendered) {
  return rendered.split(/^---\s*$/m)
    .filter((t) => /^kind:\s*Deployment\s*$/m.test(t))
    .map((text) => ({ name: ((text.match(/^metadata:\n {2}name:\s*(\S+)/m) || [])[1] || ''), text }));
}

/** `<key>:` の httpGet の { path, port }（無ければ null）。 */
function probe(text, key) {
  const m = text.match(new RegExp(`^(\\s*)${key}:\\n\\1  httpGet:\\n\\1    path:\\s*(\\S+)\\n\\1    port:\\s*(\\S+)`, 'm'));
  return m ? { path: m[2], port: m[3] } : null;
}

/** HTTP のポート（template が `$svc.port` を最初の containerPort に描く。無ければ null）。 */
function httpPort(text) {
  const m = text.match(/^\s*- containerPort:\s*(\d+)\s*$/m);
  return m ? m[1] : null;
}

/** 判定。違反の文言の配列を返す（空なら合格）。 */
function violations(rendered, programs) {
  const out = [];
  const deps = deployments(rendered);
  for (const w of WORKERS) {
    const d = deps.find((x) => x.name === w);
    if (!d) { out.push(`${w} の Deployment が描かれない`); continue; }
    const port = httpPort(d.text);
    const live = probe(d.text, 'livenessProbe');
    const ready = probe(d.text, 'readinessProbe');
    if (!port) out.push(`${w} に HTTP の containerPort が無い`);
    if (!live || live.path !== '/health/live' || live.port !== port) out.push(`${w} の liveness が /health/live:${port} でない: ${JSON.stringify(live)}`);
    if (!ready || ready.path !== '/health/ready' || ready.port !== port) out.push(`${w} の readiness が /health/ready:${port} でない: ${JSON.stringify(ready)}`);
  }
  for (const d of deps) {
    const port = httpPort(d.text);
    if (port && (!probe(d.text, 'livenessProbe') || !probe(d.text, 'readinessProbe'))) {
      out.push(`${d.name} は HTTP のポート ${port} を持つのに probe が無い`);
    }
  }
  for (const [w, src] of Object.entries(programs)) {
    if (!/^\s*app\.MapPlatformHealthChecks\(\);/m.test(src)) out.push(`${w} の Program.cs が MapPlatformHealthChecks() を呼ばない`);
  }
  return out;
}

const realPrograms = Object.fromEntries(Object.entries(PROGRAMS).map(([w, p]) => [w, read(p)]));

if (!hasHelm()) {
  console.error('helm が PATH に無い（fail-closed）。CI は static-checks-units で azure/setup-helm を使う。');
  process.exit(1);
}

const renders = {
  既定: mustRender(CHART, []),
  'values-local': mustRender(CHART, ['-f', VALUES_LOCAL]),
};

for (const [label, rendered] of Object.entries(renders)) {
  ok(`${label}: conversion / ingestion が liveness /health/live・readiness /health/ready を HTTP ポートへ持ち、HTTP ポートを持つ全 Deployment に probe がある`, () => {
    assert.deepStrictEqual(violations(rendered, realPrograms), []);
  });
}

ok('値 worker は chart に残っていない（意味の無い旗を残さない）', () => {
  const values = read(path.join(CHART, 'values.yaml'));
  const tpl = read(path.join(CHART, 'templates/deployment.yaml'));
  assert.ok(!/^\s*worker:/m.test(values), 'values.yaml に worker: が残る');
  assert.ok(!/\$svc\.worker/.test(tpl), 'deployment.yaml が $svc.worker を読む');
});

ok('変異 (a): 描画物から ingestion の probe を外すと赤', () => {
  const r = renders['既定'];
  const parts = r.split(/^---\s*$/m).map((t) => (/^ {2}name:\s*ingestion-service$/m.test(t) && /^kind:\s*Deployment/m.test(t)
    ? t.replace(/^(\s*)livenessProbe:\n(?:\1 .*\n)+/m, '').replace(/^(\s*)readinessProbe:\n(?:\1 .*\n)+/m, '')
    : t));
  const v = violations(parts.join('---'), realPrograms);
  assert.ok(v.some((x) => /ingestion-service の liveness/.test(x)) && v.some((x) => /ingestion-service の readiness/.test(x)), `捕まえない: ${v}`);
});

ok('変異 (b): 抑止の条件を template へ戻して worker=true を与えると赤', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'helm-worker-probes-'));
  try {
    const chart = path.join(tmp, 'chart');
    fs.cpSync(path.join(REPO_ROOT, CHART), chart, { recursive: true });
    const tplPath = path.join(chart, 'templates/deployment.yaml');
    const tpl = fs.readFileSync(tplPath, 'utf8');
    const mutated = tpl.replace(/\{\{- if \$svc\.port \}\}\n(\s*livenessProbe:)/, '{{- if and $svc.port (not $svc.worker) }}\n$1');
    assert.notStrictEqual(mutated, tpl, '変異の適用点（probe を描く if）が見つからない');
    fs.writeFileSync(tplPath, mutated);
    const r = mustRender(chart, ['--set', 'services.conversion.worker=true', '--set', 'services.ingestion.worker=true']);
    const v = violations(r, realPrograms);
    for (const w of WORKERS) {
      assert.ok(v.some((x) => x.startsWith(`${w} の liveness`)), `${w} の抑止を捕まえない: ${v}`);
    }
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

ok('変異 (c): Program.cs から MapPlatformHealthChecks() を外すと赤', () => {
  const programs = { ...realPrograms, 'ingestion-service': realPrograms['ingestion-service'].replace(/^\s*app\.MapPlatformHealthChecks\(\);\n/m, '') };
  assert.notStrictEqual(programs['ingestion-service'], realPrograms['ingestion-service'], '変異の適用点が見つからない');
  const v = violations(renders['既定'], programs);
  assert.ok(v.some((x) => /ingestion-service の Program\.cs/.test(x)), `捕まえない: ${v}`);
});

process.stdout.write(`helm-worker-probes: ${passed} passed\n`);
