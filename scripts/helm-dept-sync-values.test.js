#!/usr/bin/env node
'use strict';
/*
 * helm-dept-sync-values.test.js
 * SC-17, FR-05, ADR-0116 決定 1（2026-10-09 補完）, IADR-0473（2026-10-09 追記 / #1850）:
 * **起動器（k8s-local-up.sh）が部門属性の同期を宣言し直すときに [6/7] へ渡す values ファイルを、実際に `helm template` で描いて固定する。**
 * `k8s-local-up.test.js` は helm をスタブにするので、生成したファイルが実物のチャートで意図どおりの env になることは見ていない。
 *
 * 固定するもの:
 *   1. values-local.yaml ＋ 生成した values ファイル（`scripts/lib/dept-sync-mode.sh` の `dept_sync_values_file`）で描くと、
 *      authorization-service の Deployment の env に `DepartmentAttributeSync__Mode`（宣言した値）と、現行から保った他の要素が
 *      字面どおりに現れる（`value: "true"` は文字列のまま・secretKeyRef は optional まで）。チャート既定の extraEnv（IdentityAdmin__*）も残る。
 *   2. 他のワークロードには `DepartmentAttributeSync__Mode` が現れない。
 *   3. values-local.yaml だけ（宣言しない既定）では、どこにも `DepartmentAttributeSync__Mode` が現れない（CI の描画は従来どおり）。
 *   4. 対照（採らなかった形が実際に壊れること）: 添字 0 の `--set` は要素 0 を上書きし（他の要素が消え、古い Mode が同名で残る）、
 *      他の要素を `--set` で写し直すと `value: "false"` が bool に化け、チャートの `{{- if .value }}` で env の値が消える。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** helm-synthetic-monitor.test.js と同じ理由。CI は static-checks-units（helm 済み）で走らせる。
 * 外部依存ゼロ（Node 標準モジュール ＋ helm ＋ bash）。実行: node scripts/helm-dept-sync-values.test.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const VALUES_LOCAL = 'deploy/local/values-local.yaml';
const LIB = 'scripts/lib/dept-sync-mode.sh';
const MODE_ENV = 'DepartmentAttributeSync__Mode';
const OWNER = 'authorization-service';

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

const helmProbe = spawnSync(process.platform === 'win32' ? 'where' : 'which', ['helm'], { encoding: 'utf8' });
if (helmProbe.status !== 0) {
  process.stderr.write('helm が見つからない。本試験は helm template で描いて確かめるので、helm 無しでは合否を出せない（fail-closed）。\n');
  process.exit(1);
}

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'helm-dept-sync-'));
process.on('exit', () => fs.rmSync(tmp, { recursive: true, force: true }));

function render(args) {
  const r = spawnSync('helm', ['template', 'msp', CHART, '-f', VALUES_LOCAL, ...args], {
    cwd: REPO_ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024,
  });
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.stderr}`);
  return r.stdout;
}

/** 描画物を文書へ割り、kind と metadata.name を添える。 */
function documents(rendered) {
  return rendered.split(/^---\s*$/m).map((text) => {
    const kind = (text.match(/^kind:\s*(\S+)/m) || [])[1] || '';
    const meta = text.match(/^metadata:\n((?:[ ].*\n)+)/m);
    const name = meta ? ((meta[1].match(/^ {2}name:\s*(\S+)/m) || [])[1] || '') : '';
    return { kind, name, text };
  });
}

/** env の 1 項目（`- name: X` から同じ字下げの次の `- ` か、より浅い行の手前まで）を、字下げを除いて返す。 */
function envEntry(text, envName) {
  const lines = text.split('\n');
  const out = [];
  for (let i = 0; i < lines.length; i++) {
    const m = lines[i].match(/^(\s*)- name:\s*["']?([^"'\s]+)["']?\s*$/);
    if (!m || m[2] !== envName) continue;
    const indent = m[1].length;
    const body = [lines[i].slice(indent)];
    for (let j = i + 1; j < lines.length; j++) {
      const l = lines[j];
      const ind = l.match(/^(\s*)/)[1].length;
      if (l.trim() === '' || ind < indent || (ind === indent && /^\s*- /.test(l))) break;
      body.push(l.slice(indent));
    }
    out.push(body.join('\n'));
  }
  return out;
}

const deployment = (rendered, name) => {
  const d = documents(rendered).find((x) => x.kind === 'Deployment' && x.name === name);
  assert.ok(d, `Deployment ${name} が描画物に無い`);
  return d.text;
};
const holders = (rendered, envName) => documents(rendered)
  .filter((d) => envEntry(d.text, envName).length > 0).map((d) => `${d.kind}/${d.name}`);

// 現行のリリース（helm get values -o yaml の形）: 他の要素 2 つ（"true" の文字列・secretKeyRef optional）と Mode=Report
const CURRENT = [
  'services:',
  '  authorization:',
  '    extraEnvAppend:',
  '    - name: DepartmentAttributeSync__Interval',
  '      value: "00:10:00"',
  '    - name: DepartmentAttributeSync__Mode',
  '      value: Report',
  '    - name: Probe__Flag',
  '      value: "true"',
  '    - name: Probe__Secret',
  '      secretKeyRef:',
  '        key: k',
  '        name: probe-secret',
  '        optional: true',
  '',
].join('\n');

/** 起動器と同じ関数で values ファイルを作る（現行の他の要素 ＋ 宣言する値）。 */
function generated(mode, current) {
  const r = spawnSync('bash', ['-c', `. "${LIB}"; dept_sync_values_file "$1" "$(dept_sync_values_other_entries "$2")"`, '_', mode, current],
    { cwd: REPO_ROOT, encoding: 'utf8' });
  assert.strictEqual(r.status, 0, `dept_sync_values_file が失敗した: ${r.stderr}`);
  const file = path.join(tmp, `dept-sync-${mode}.yaml`);
  fs.writeFileSync(file, r.stdout);
  return file;
}

ok('#1850: 生成した values ファイルで、authorization-service に Mode と現行の他の要素が字面どおりに描かれる（チャート既定の extraEnv も残る）', () => {
  const out = render(['-f', generated('Fix', CURRENT)]);
  const dep = deployment(out, OWNER);
  assert.deepStrictEqual(envEntry(dep, MODE_ENV), [`- name: ${MODE_ENV}\n  value: "Fix"`]);
  assert.deepStrictEqual(envEntry(dep, 'DepartmentAttributeSync__Interval'), ['- name: DepartmentAttributeSync__Interval\n  value: "00:10:00"']);
  assert.deepStrictEqual(envEntry(dep, 'Probe__Flag'), ['- name: Probe__Flag\n  value: "true"'], '"true" が文字列のまま描かれていない');
  assert.deepStrictEqual(envEntry(dep, 'Probe__Secret'),
    ['- name: Probe__Secret\n  valueFrom:\n    secretKeyRef:\n      name: probe-secret\n      key: k\n      optional: true']);
  assert.strictEqual(envEntry(dep, 'IdentityAdmin__Provider').length, 1, 'チャート既定の extraEnv（IdentityAdmin__*）が消えた');
  assert.deepStrictEqual(holders(out, MODE_ENV), [`Deployment/${OWNER}`], '他のワークロードへ Mode が混入した');
});

ok('#1850: 宣言しない既定（values-local.yaml だけ）では、どこにも Mode が現れない', () => {
  assert.deepStrictEqual(holders(render([]), MODE_ENV), []);
});

ok('#1850 対照: 添字 0 の --set は要素 0 を上書きする（現行の他の要素が消え、古い Mode が同名で残る。採らなかった形が実際に壊れる）', () => {
  // 現行の値を同じコマンドの -f で与えた形（--reuse-values なら添字 0 の --set でリストごと置き換わり、他の要素がすべて消える。運用仕様書の注記）。
  const current = path.join(tmp, 'current.yaml');
  fs.writeFileSync(current, CURRENT);
  const dep = deployment(render(['-f', current,
    '--set', `services.authorization.extraEnvAppend[0].name=${MODE_ENV}`, '--set', 'services.authorization.extraEnvAppend[0].value=Fix']), OWNER);
  assert.strictEqual(envEntry(dep, 'DepartmentAttributeSync__Interval').length, 0, '前提が崩れた: 添字 0 の --set で要素 0 が残った');
  assert.deepStrictEqual(envEntry(dep, MODE_ENV), [`- name: ${MODE_ENV}\n  value: "Fix"`, `- name: ${MODE_ENV}\n  value: "Report"`],
    '前提が崩れた: 添字 0 の --set で同名の Mode が 2 つにならなかった');
});

ok('#1850 対照: 他の要素を --set で写し直すと "false" が bool に化け、env の値が消える（字面のまま values ファイルへ写す理由）', () => {
  const dep = deployment(render([
    '--set', 'services.authorization.extraEnvAppend[0].name=Probe__Flag', '--set', 'services.authorization.extraEnvAppend[0].value=false',
  ]), OWNER);
  assert.deepStrictEqual(envEntry(dep, 'Probe__Flag'), ['- name: Probe__Flag'], '前提が崩れた: --set の false が文字列のまま描かれた');
});

process.stdout.write(`\n✓ ${passed} tests passed\n`);
