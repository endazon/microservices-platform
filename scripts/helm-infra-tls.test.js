#!/usr/bin/env node
'use strict';
/*
 * helm-infra-tls.test.js
 * NFR-16, NFR-18, IADR-0591（Refs GHSA-j4qv-2w99-xg5p）:
 * **基盤（DB・ブローカ）への接続が検証つきの TLS であることを、実際に `helm template` で描いて固定する。**
 *
 * 固定するもの:
 *   1. 既定（本番像）・values-local の描画で、DB へ繋ぐ全 Deployment の接続文字列は
 *      `SSL Mode=VerifyFull;Root Certificate=/etc/platform-infra-ca/<caKey>` で終わる（検証を緩める形を持たない）。
 *   2. ブローカへ繋ぐ全 Deployment は `amqps://…@<host>:<tls.port>` で、SSL_CERT_DIR は既定のストアを先に残して CA の置き場を足す。
 *   3. どちらかを持つ Deployment は ConfigMap global.infraCa.configMap を /etc/platform-infra-ca へ読み取り専用で（非 optional）
 *      マウントし、キーは caKey の 1 つだけを写す。どちらも持たない Deployment はマウントしない。
 *   4. infraCa の configMap / caKey が空、または tls.port が空なら描画で止まる。
 *   5. 切り戻し（tls.enabled=false）は平文の従来の形に戻り、マウントも SSL_CERT_DIR も描かない。
 *   6. 変異: 接続文字列の検証の部分を外す・SSL_CERT_DIR の既定のストアを落とす・マウントを外すと判定が赤になる。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** CI は static-checks-units で走らせる。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/helm-infra-tls.test.js
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
const CA_DIR = '/etc/platform-infra-ca';

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

function render(args, chart = CHART) {
  return spawnSync('helm', ['template', 'msp', chart, ...RENDER_IMAGE_TAG_ARGS, ...args], { cwd: REPO_ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
}
function mustRender(args, chart = CHART) {
  const r = render(args, chart);
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.stderr}`);
  return r.stdout;
}

/** 描画物の Deployment を { app, text } で返す。 */
function deployments(rendered) {
  return rendered.split(/^---\s*$/m)
    .filter((t) => /^kind:\s*Deployment\s*$/m.test(t))
    .map((text) => ({ app: ((text.match(/^ {2}labels:\n {4}app:\s*(\S+)/m) || [])[1] || ''), text }));
}

/** env の値（`- name: X` の次の `value:`）。無ければ null。 */
function envValue(text, name) {
  const m = new RegExp(`^\\s*- name: ${name}\\s*\\n\\s*value: (.*)$`, 'm').exec(text);
  return m ? m[1].trim().replace(/^"|"$/g, '') : null;
}

/** 判定。違反の一覧を返す（空 = 合格）。 */
function violations(rendered, { caConfigMap = 'platform-infra-ca', caKey = 'ca.crt', mqPort = '5671' } = {}) {
  const out = [];
  let db = 0;
  let mq = 0;
  for (const d of deployments(rendered)) {
    const conn = envValue(d.text, 'ConnectionStrings__DefaultConnection');
    const amqp = envValue(d.text, 'RabbitMq__ConnectionString');
    const certDir = envValue(d.text, 'SSL_CERT_DIR');
    const mount = new RegExp(`- name: platform-infra-ca\\n\\s*mountPath: ${CA_DIR}\\n\\s*readOnly: true`).test(d.text);
    const vol = new RegExp(`- name: platform-infra-ca\\n\\s*configMap:\\n\\s*name: ${caConfigMap}\\n\\s*items:\\n\\s*- key: ${caKey.replace('.', '\\.')}\\n\\s*path: ${caKey.replace('.', '\\.')}\\n`).test(d.text);
    if (conn !== null) {
      db++;
      if (!conn.endsWith(`;SSL Mode=VerifyFull;Root Certificate=${CA_DIR}/${caKey}`)) out.push(`${d.app}: DB の接続が VerifyFull ＋ 基盤の CA で終わらない: ${conn}`);
      if (/Trust\s*Server\s*Certificate|SSL Mode=(Require|Prefer|Allow|Disable)/i.test(conn)) out.push(`${d.app}: DB の接続に検証を緩める形がある`);
    }
    if (amqp !== null) {
      mq++;
      if (!new RegExp(`^amqps://[^@]+@[^:]+:${mqPort}$`).test(amqp)) out.push(`${d.app}: ブローカの接続が amqps:${mqPort} でない: ${amqp}`);
      if (certDir !== `/etc/ssl/certs:${CA_DIR}`) out.push(`${d.app}: SSL_CERT_DIR が既定のストア ＋ 基盤の CA でない: ${certDir}`);
    } else if (certDir !== null) {
      out.push(`${d.app}: ブローカへ繋がないのに SSL_CERT_DIR を描く`);
    }
    const needs = conn !== null || amqp !== null;
    if (needs && !(mount && vol)) out.push(`${d.app}: 基盤の CA をマウントしない（mount=${mount} vol=${vol}）`);
    if (!needs && (mount || vol)) out.push(`${d.app}: 基盤へ繋がないのに CA をマウントする`);
    if (/name: platform-infra-ca[\s\S]{0,200}optional: true/.test(d.text)) out.push(`${d.app}: CA の ConfigMap が optional（無いまま起動する）`);
  }
  if (db < 10) out.push(`DB へ繋ぐ Deployment を読めない（${db} 件）`);
  if (mq < 7) out.push(`ブローカへ繋ぐ Deployment を読めない（${mq} 件）`);
  return out;
}

if (!hasHelm()) {
  console.error('helm が見つからない。この試験は helm template で描いて検査する（黙って飛ばさない）。');
  process.exit(1);
}

for (const [label, args] of [['既定（本番像）', []], ['values-local', ['-f', VALUES_LOCAL]]]) {
  ok(`${label}: DB は VerifyFull ＋ 基盤の CA、ブローカは amqps:5671 ＋ 信頼ストアへの追加、CA は非 optional で 1 キーだけマウントする`, () => {
    assert.deepStrictEqual(violations(mustRender(args)), []);
  });
}

ok('CA の ConfigMap 名・キー・TLS の口を変えると描画がそれに従う（値の受け口が効いている）', () => {
  const rendered = mustRender(['--set', 'global.infraCa.configMap=infra-ca-x', '--set', 'global.infraCa.caKey=root.pem', '--set', 'global.messaging.tls.port=15671']);
  assert.deepStrictEqual(violations(rendered, { caConfigMap: 'infra-ca-x', caKey: 'root.pem', mqPort: '15671' }), []);
});

ok('描画で止まる: infraCa の configMap / caKey が空、ブローカの tls.port が空', () => {
  for (const [set, re] of [
    ['global.infraCa.configMap=', /global\.infraCa\.configMap \/ caKey が要ります/],
    ['global.infraCa.caKey=', /global\.infraCa\.configMap \/ caKey が要ります/],
    ['global.messaging.tls.port=', /global\.messaging\.tls\.port が要ります/],
  ]) {
    const r = render(['--set', set]);
    assert.notStrictEqual(r.status, 0, `${set} で描画が通った`);
    assert.match(r.stderr, re, `${set}: 理由を告げていない: ${r.stderr}`);
  }
});

ok('切り戻し（tls.enabled=false）は平文の従来の形に戻り、CA のマウントも SSL_CERT_DIR も描かない', () => {
  const rendered = mustRender(['--set', 'global.db.tls.enabled=false', '--set', 'global.messaging.tls.enabled=false']);
  for (const d of deployments(rendered)) {
    const conn = envValue(d.text, 'ConnectionStrings__DefaultConnection');
    const amqp = envValue(d.text, 'RabbitMq__ConnectionString');
    if (conn !== null) assert.ok(!/SSL Mode|Root Certificate/.test(conn), `${d.app}: 切り戻しても TLS の指定が残る`);
    if (amqp !== null) assert.match(amqp, /^amqp:\/\/[^@]+@rabbitmq:5672$/, `${d.app}: 切り戻しで平文の口に戻らない`);
    assert.strictEqual(envValue(d.text, 'SSL_CERT_DIR'), null, `${d.app}: 切り戻しても SSL_CERT_DIR を描く`);
    assert.ok(!/name: platform-infra-ca/.test(d.text), `${d.app}: 切り戻しても CA をマウントする`);
  }
});

ok('変異: 検証の部分を外す・既定のストアを落とす・マウントを外すと判定が赤になる（判定が空振りしない）', () => {
  const tpl = path.join(REPO_ROOT, CHART, 'templates', 'deployment.yaml');
  const src = fs.readFileSync(tpl, 'utf8');
  const mutate = (from, to) => {
    assert.ok(src.includes(from), `変異の置換元が無い（試験が古い）: ${from}`);
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'helm-infra-tls-'));
    try {
      fs.cpSync(path.join(REPO_ROOT, CHART), dir, { recursive: true });
      fs.writeFileSync(path.join(dir, 'templates', 'deployment.yaml'), src.replace(from, to));
      return violations(mustRender([], dir));
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  };
  assert.ok(mutate(';SSL Mode=VerifyFull;', ';SSL Mode=Require;').some((v) => /VerifyFull/.test(v)), '検証を緩めた変異を捕まえない');
  assert.ok(mutate('"/etc/ssl/certs:/etc/platform-infra-ca"', '"/etc/platform-infra-ca"').some((v) => /SSL_CERT_DIR/.test(v)), '既定のストアを落とした変異を捕まえない');
  assert.ok(mutate('{{- if or $pipelineMount $vaultToken $infraCaMount $mcpPublication }}\n          volumeMounts:', '{{- if or $pipelineMount $vaultToken $mcpPublication }}\n          volumeMounts:')
    .some((v) => /マウントしない/.test(v)), 'マウントを外した変異を捕まえない');
});

process.stdout.write(`helm-infra-tls: ${passed} tests passed\n`);
