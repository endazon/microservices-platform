#!/usr/bin/env node
'use strict';
/*
 * helm-broker-per-service-users.test.js
 * NFR-18, IADR-0577（Refs GHSA-rh44-px44-p48q）, IADR-0573 の段 3:
 * **基盤のサービスがブローカへサービスごとのユーザーで名乗ることを、実際に `helm template` で描いて固定する。**
 *
 * 固定するもの:
 *   1. 既定（本番像）・values-local の描画で、`RabbitMq__ConnectionString` を持つ Deployment の集合は、
 *      deploy/local/rabbitmq-setup/users.txt の microservices-platform の行（app ラベル）とちょうど一致する。
 *   2. 各 Deployment の接続文字列のユーザーは `msp-<サービス名>`（= users.txt の 1 列目）で、パスワードは
 *      env RABBITMQ_PASSWORD の `$(…)` 補間（リテラルを持たない）。RABBITMQ_PASSWORD は Secret msp-rabbitmq-users の
 *      同名のキーを**非 optional** な secretKeyRef で読む。
 *   3. 描画物のどこにも、全サービス共通の 1 組（Secret rabbitmq-app・`guest` のユーザー）が現れない。
 *   4. 廃止した旧キー（global.messaging.user / existingSecret / passwordKey）を与えると描画で止まる。
 *      新キーが欠けても止まる。
 *   5. 供給側: ExternalSecret msp-rabbitmq-users は KV msp/rabbitmq-users を丸ごと読み（キー = ユーザー名）、
 *      ブローカ側の写し（rabbitmq-users）と同じ KV である。
 *   6. 変異: 接頭辞を変える・Secret 名を変えると本試験の判定が赤になる（判定が空振りしない）。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** helm-llm-provider-keys.test.js と同じ理由。CI は static-checks-units で走らせる。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/helm-broker-per-service-users.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const VALUES_LOCAL = 'deploy/local/values-local.yaml';
const USERS = 'deploy/local/rabbitmq-setup/users.txt';
const ES_MSP = 'deploy/local/vault/eso/externalsecret-msp-rabbitmq-users.yaml';
const ES_BROKER = 'deploy/local/vault/eso/externalsecret-rabbitmq-users.yaml';
const SECRET = 'msp-rabbitmq-users';

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

/** 描画物の Deployment を { app, text } で返す。 */
function deployments(rendered) {
  return rendered.split(/^---\s*$/m)
    .filter((t) => /^kind:\s*Deployment\s*$/m.test(t))
    .map((text) => ({ app: ((text.match(/^ {2}labels:\n {4}app:\s*(\S+)/m) || [])[1] || ''), text }));
}

/** env の 1 項目（`- name: X` から、同じ字下げの次の `- ` かより浅い行の手前まで）。 */
function envEntry(text, envName) {
  const lines = text.split('\n');
  const at = lines.findIndex((l) => new RegExp(`^(\\s*)- name: ${envName}\\s*$`).test(l));
  if (at < 0) return null;
  const indent = lines[at].match(/^(\s*)/)[1].length;
  const out = [lines[at]];
  for (let i = at + 1; i < lines.length; i++) {
    const ind = lines[i].match(/^(\s*)/)[1].length;
    if (lines[i].trim() === '' || ind < indent || (ind === indent && lines[i].trim().startsWith('- '))) break;
    out.push(lines[i]);
  }
  return out.join('\n');
}

/** 描画物から「サービスごとのユーザー」の判定に要る事実を抜く（判定は呼び出し側）。 */
function facts(rendered) {
  return deployments(rendered).filter((d) => envEntry(d.text, 'RabbitMq__ConnectionString')).map((d) => {
    const conn = envEntry(d.text, 'RabbitMq__ConnectionString');
    const pw = envEntry(d.text, 'RABBITMQ_PASSWORD') || '';
    return {
      app: d.app,
      // NFR-16, IADR-0591: 既定は amqps（TLS）。切り戻しの amqp も同じ規則で読む。
      user: (conn.match(/amqps?:\/\/([^:"]+):/) || [])[1] || '',
      interpolated: /amqps?:\/\/[^:"]+:\$\(RABBITMQ_PASSWORD\)@/.test(conn),
      secret: (pw.match(/secretKeyRef:\n\s*name:\s*(\S+)/) || [])[1] || '',
      key: (pw.match(/\n\s*key:\s*(\S+)/) || [])[1] || '',
      optional: /optional:/.test(pw),
      literal: /\n\s*value:/.test(pw),
    };
  });
}

const declared = read(USERS).split('\n').filter((l) => l.trim() && !l.trim().startsWith('#'))
  .map((l) => l.trim().split(/\s+/)).filter((r) => r[1] === 'microservices-platform')
  .map((r) => ({ user: r[0], app: r[2] }));

/** 判定。違反の一覧を返す（空 = 合格）。 */
function violations(rendered) {
  const out = [];
  const got = facts(rendered);
  const want = declared.map((d) => d.app).sort();
  const have = got.map((f) => f.app).sort();
  if (JSON.stringify(have) !== JSON.stringify(want)) out.push(`ブローカへ繋ぐ Deployment が users.txt と違う: ${have.join(',')} ≠ ${want.join(',')}`);
  for (const f of got) {
    const d = declared.find((x) => x.app === f.app);
    if (!d) continue;
    if (f.user !== d.user) out.push(`${f.app}: ユーザーが ${f.user}（期待 ${d.user}）`);
    if (!f.interpolated) out.push(`${f.app}: パスワードを $(RABBITMQ_PASSWORD) で補間していない`);
    if (f.secret !== SECRET) out.push(`${f.app}: Secret が ${f.secret}（期待 ${SECRET}）`);
    if (f.key !== d.user) out.push(`${f.app}: キーが ${f.key}（期待 ${d.user}）`);
    if (f.optional) out.push(`${f.app}: secretKeyRef が optional（欠けたまま起動する）`);
    if (f.literal) out.push(`${f.app}: RABBITMQ_PASSWORD がリテラルの値を持つ`);
  }
  if (/\brabbitmq-app\b/.test(rendered)) out.push('全サービス共通の Secret rabbitmq-app が描画物に残る');
  if (/amqps?:\/\/guest\b/.test(rendered)) out.push('既定のユーザー guest で名乗る接続文字列が残る');
  return out;
}

if (!hasHelm()) {
  console.error('helm が見つからない。この試験は helm template で描いて検査する（黙って飛ばさない）。');
  process.exit(1);
}

ok('前提: users.txt の microservices-platform の行は 7 件で、ユーザー名は msp-<名前>・app は <名前>-service', () => {
  assert.strictEqual(declared.length, 7, `件数が違う（読めていない）: ${declared.length}`);
  for (const d of declared) assert.strictEqual(d.app, `${d.user.replace(/^msp-/, '')}-service`, `${d.user} と ${d.app} の対応が崩れた`);
});

for (const [label, args] of [['既定（本番像）', []], ['values-local', ['-f', VALUES_LOCAL]]]) {
  ok(`${label}: ブローカへ繋ぐ全サービスがサービスごとのユーザー・同名のキー（非 optional）で名乗り、共通の 1 組が無い`, () => {
    const rendered = mustRender(args);
    assert.deepStrictEqual(violations(rendered), []);
  });
}

ok('廃止した旧キーを与えると描画で止まる。新キーが欠けても止まる', () => {
  for (const set of ['global.messaging.user=guest', 'global.messaging.existingSecret=rabbitmq-app', 'global.messaging.passwordKey=password']) {
    const r = render(['--set', set]);
    assert.notStrictEqual(r.status, 0, `${set} で描画が通った`);
    assert.match(r.stderr, /廃止しました/, `${set}: 理由を告げていない: ${r.stderr}`);
  }
  for (const set of ['global.messaging.usernamePrefix=', 'global.messaging.credentialsSecretName=', 'global.messaging.host=']) {
    const r = render(['--set', set]);
    assert.notStrictEqual(r.status, 0, `${set} で描画が通った`);
    assert.match(r.stderr, /が要ります/, `${set}: 理由を告げていない: ${r.stderr}`);
  }
});

ok('供給: ExternalSecret msp-rabbitmq-users は KV msp/rabbitmq-users を丸ごと読み、ブローカ側の写しと同じ KV である（Owner）', () => {
  const es = read(ES_MSP);
  assert.match(es, /^ {2}name: msp-rabbitmq-users$/m);
  assert.match(es, /^ {2}namespace: microservices-platform$/m);
  assert.match(es, /target:\n\s*name: msp-rabbitmq-users\n\s*creationPolicy: Owner/);
  assert.deepStrictEqual([...es.matchAll(/key: (\S+)/g)].map((m) => m[1]), ['msp/rabbitmq-users'], 'msp/rabbitmq-users だけを読むこと');
  assert.ok(/key: msp\/rabbitmq-users/.test(read(ES_BROKER)), 'ブローカ側の写しが同じ KV を読まない（値が食い違う）');
});

ok('変異: 接頭辞・Secret 名を変えると判定が赤になる（判定が空振りしない）', () => {
  const prefix = violations(mustRender(['--set', 'global.messaging.usernamePrefix=svc-']));
  assert.ok(prefix.some((v) => /ユーザーが svc-/.test(v)) && prefix.some((v) => /キーが svc-/.test(v)), `接頭辞の変異を捕まえない: ${prefix}`);
  const secret = violations(mustRender(['--set', 'global.messaging.credentialsSecretName=rabbitmq-app']));
  assert.ok(secret.some((v) => /Secret が rabbitmq-app/.test(v)) && secret.some((v) => /rabbitmq-app が描画物に残る/.test(v)), `Secret の変異を捕まえない: ${secret}`);
  const off = violations(mustRender(['--set', 'services.graph.messaging=false']));
  assert.ok(off.some((v) => /users\.txt と違う/.test(v)), `サービスの欠落を捕まえない: ${off}`);
});

process.stdout.write(`helm-broker-per-service-users: ${passed} tests passed\n`);
