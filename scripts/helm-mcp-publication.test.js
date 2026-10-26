#!/usr/bin/env node
'use strict';
/*
 * helm-mcp-publication.test.js
 * FR-16, ADR-0024, IADR-0645 (#2117):
 * **MCP の宣言的公開構成（許可リスト）が Helm values（services.mcp.mcpPublication）から ConfigMap で mcp-service に届くことを、
 * 実際に `helm template` で描いて固定する。**
 *
 * 🔴 守るのは「values を変えたのに、mcp-service がイメージの既定を読み続ける」形を残さないことである。
 *    ConfigMap だけが描かれてマウントか環境変数のどちらかが欠けると、McpServer は空の Mcp:PublicationConfigPath を見て
 *    イメージの既定へ戻る —— 例外もヘルスの赤も出ない（構成の差し替えが黙って効かない）。
 *
 * 固定するもの:
 *   1. 既定（本番像）・values-local の描画は ConfigMap mcp-publication-config・マウント・Mcp__PublicationConfigPath を持たない（陰性対照）。
 *   2. ci/mcp-publication-values.yaml の描画で、ConfigMap の値は values の文字列とバイト等価であり、mcp-service だけが
 *      それをマウントし、Mcp__PublicationConfigPath はマウント先のキーのファイルを指し、checksum/mcp-publication は値の sha256 である。
 *   3. JSON として読めない値・mcp 以外のサービスへの設定・extraEnv との併記は描画で止まる。
 *   4. 変異: マウント先と環境変数のパスの食い違い・checksum の欠落・別サービスへのマウントを判定器へ通すと赤になる。
 *
 * 中身の検証（初期公開範囲・公開名の一意性・属性割当の禁止）は本試験の対象外である。McpServer の検証器そのものが
 * CI（McpServer.Tests の PublicationConfigDeploymentFilesTests）と起動時の 2 か所で行う（規則を JS へ写さない）。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** helm-mcp-client-provisioning.test.js と同じ理由。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/helm-mcp-publication.test.js
 */
const assert = require('assert');
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const { RENDER_IMAGE_TAG_ARGS } = require('./lib/helm-render-tag'); // NFR-20, IADR-0605: chart はタグが無いと描画を落とす

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const VALUES_LOCAL = 'deploy/local/values-local.yaml';
const CI_VALUES = `${CHART}/ci/mcp-publication-values.yaml`;

const CONFIGMAP = 'mcp-publication-config';
const KEY = 'mcp-publication.json';
const ENV = 'Mcp__PublicationConfigPath';
const CHECKSUM = 'checksum/mcp-publication';
const OWNER = 'mcp-service';

/** 正規表現へ文字列を埋め込むときの完全なエスケープ（バックスラッシュを含むすべてのメタ文字）。
 *  本ファイルが `new RegExp(...)` へ埋め込む値はすべてこれを通す（CodeQL js/incomplete-sanitization）。 */
const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

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
  return spawnSync('helm', ['template', 'msp', CHART, ...RENDER_IMAGE_TAG_ARGS, ...args], {
    cwd: REPO_ROOT,
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
  });
}

/** values を一時ファイルへ書き、その `-f` 引数を返す（`--set-string` は値の中のカンマで割れるので JSON を渡せない）。 */
function valuesFile(yaml) {
  const file = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'mcp-pub-')), 'values.yaml');
  fs.writeFileSync(file, yaml);
  return ['-f', file];
}

/** services.<svc>.mcpPublication に JSON の文字列をブロックで置く values。 */
function publicationValues(svc, json) {
  return `services:\n  ${svc}:\n    mcpPublication: |\n${json.trimEnd().split('\n').map((l) => `      ${l}`).join('\n')}\n`;
}

function mustRender(args) {
  const r = render(args);
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.stderr}`);
  return r.stdout;
}

/** 描画物を文書へ割り、kind と metadata.name（最初に現れる 2 字下げの name）を添える。 */
function documents(rendered) {
  return rendered.split(/^---\s*$/m).map((text) => {
    const kind = (text.match(/^kind:\s*(\S+)/m) || [])[1] || '';
    const meta = text.match(/^metadata:\n((?:[ #].*\n)+)/m);
    const name = meta ? ((meta[1].match(/^ {2}name:\s*(\S+)/m) || [])[1] || '') : '';
    return { kind, name, text };
  });
}

/** ConfigMap の data の 1 キー（`key: "..."` の JSON 文字列。helm の quote は Go の %q で、JSON の文字列として読める）。 */
function configMapValue(doc, key) {
  const m = new RegExp(`^ {2}${escapeRe(key)}:\\s*(".*")\\s*$`, 'm').exec(doc.text);
  return m ? JSON.parse(m[1]) : null;
}

/** env の項目 `- name: X` の次行 `value: ...` を返す（無ければ null）。 */
function envValue(text, name) {
  const m = new RegExp(`^\\s*- name:\\s*${escapeRe(name)}\\s*\\n\\s*value:\\s*["']?([^"'\\n]*)["']?\\s*$`, 'm').exec(text);
  return m ? m[1] : null;
}

/** values ファイルの services.mcp.mcpPublication（`mcpPublication: |` のブロック）を取り出す。YAML のブロックの字下げを外す。 */
function publicationOf(valuesText) {
  const lines = valuesText.split('\n');
  const start = lines.findIndex((l) => /^\s*mcpPublication:\s*\|\s*$/.test(l));
  if (start < 0) return null;
  const body = [];
  let indent = null;
  for (let i = start + 1; i < lines.length; i++) {
    const l = lines[i];
    if (l.trim() === '') { body.push(''); continue; }
    const ind = l.match(/^(\s*)/)[1].length;
    if (indent === null) indent = ind;
    if (ind < indent) break;
    body.push(l.slice(indent));
  }
  while (body.length && body[body.length - 1] === '') body.pop();
  return `${body.join('\n')}\n`; // `|`（clip）は末尾の改行を 1 つだけ残す
}

/** 判定器本体（設定ありの描画）。違反の一覧を返す（空なら合格）。描画物の字面だけを見る純関数。 */
function checkConfigured(rendered, expected) {
  const errors = [];
  const docs = documents(rendered);
  const cms = docs.filter((d) => d.kind === 'ConfigMap' && d.name === CONFIGMAP);
  if (cms.length !== 1) errors.push(`ConfigMap ${CONFIGMAP} が ${cms.length} 個（ちょうど 1 個であるべき）`);
  for (const cm of cms) {
    const v = configMapValue(cm, KEY);
    if (v !== expected) errors.push(`ConfigMap ${CONFIGMAP} の ${KEY} が values の文字列と一致しない`);
  }
  const mounting = docs.filter((d) => d.kind === 'Deployment' && new RegExp(`name:\\s*${escapeRe(CONFIGMAP)}\\b`).test(d.text));
  const names = mounting.map((d) => d.name);
  if (names.length !== 1 || names[0] !== OWNER) errors.push(`${CONFIGMAP} をマウントする Deployment が ${JSON.stringify(names)}（${OWNER} だけであるべき）`);
  const owner = docs.find((d) => d.kind === 'Deployment' && d.name === OWNER);
  if (!owner) {
    errors.push(`Deployment ${OWNER} が描かれていない`);
    return errors;
  }
  const mount = new RegExp(`- name:\\s*${escapeRe(CONFIGMAP)}\\s*\\n\\s*mountPath:\\s*(\\S+)\\s*\\n\\s*readOnly:\\s*true`).exec(owner.text);
  if (!mount) errors.push(`${OWNER} に ${CONFIGMAP} の読み取り専用マウントが無い`);
  const env = envValue(owner.text, ENV);
  if (env === null) errors.push(`${OWNER} に ${ENV} が無い（イメージの既定を読み続ける）`);
  if (mount && env !== null && env !== `${mount[1]}/${KEY}`) errors.push(`${ENV}=${env} がマウント先 ${mount[1]}/${KEY} を指さない`);
  const sum = new RegExp(`^\\s*${escapeRe(CHECKSUM)}:\\s*"([0-9a-f]{64})"`, 'm').exec(owner.text);
  const want = crypto.createHash('sha256').update(expected).digest('hex');
  if (!sum) errors.push(`${OWNER} の Pod テンプレートに ${CHECKSUM} が無い（構成の変更でロールアウトしない）`);
  else if (sum[1] !== want) errors.push(`${CHECKSUM} が値の sha256 と違う: ${sum[1]} / ${want}`);
  return errors;
}

/** 判定器（設定なしの描画）。差し替えの部品が 1 つも現れないこと。 */
function checkUnconfigured(renderedWithComments) {
  const errors = [];
  // コメント行（テンプレートの冒頭の注記は条件の外に在り、未設定でも `# Source:` と一緒に出る）は描画物ではない。
  const rendered = renderedWithComments.split('\n').filter((l) => !/^\s*#/.test(l)).join('\n');
  if (rendered.includes(CONFIGMAP)) errors.push(`${CONFIGMAP} が描かれた（未設定ならイメージの既定を読む）`);
  if (rendered.includes(ENV)) errors.push(`${ENV} が描かれた（未設定ならイメージの既定を読む）`);
  if (rendered.includes(CHECKSUM)) errors.push(`${CHECKSUM} が描かれた`);
  return errors;
}

function main() {
  if (!hasHelm()) {
    process.stderr.write('helm-mcp-publication.test: helm が見つからない（fail-closed）\n');
    process.exit(1);
  }

  const ciValuesText = fs.readFileSync(path.join(REPO_ROOT, CI_VALUES), 'utf8');
  const expected = publicationOf(ciValuesText);

  ok('CI 用の values から公開構成の文字列を取り出せ、JSON として読める（対照: 以下が何かを検査している）', () => {
    assert.ok(expected, `${CI_VALUES} に mcpPublication: | のブロックが無い`);
    const parsed = JSON.parse(expected);
    assert.ok(Array.isArray(parsed.tools) && parsed.tools.length > 0);
  });

  const defaultRender = mustRender([]);
  const localRender = mustRender(['-f', VALUES_LOCAL]);
  const configured = mustRender(['-f', CI_VALUES]);

  ok('既定（本番像）の描画は差し替えの部品を持たない（イメージの既定を読む）', () => {
    assert.deepStrictEqual(checkUnconfigured(defaultRender), []);
    assert.ok(defaultRender.includes(`name: ${OWNER}`), `対照: 既定の描画に ${OWNER} が在る`);
  });

  ok('values-local の描画も差し替えの部品を持たない（PoC は変わらない）', () => {
    assert.deepStrictEqual(checkUnconfigured(localRender), []);
  });

  ok('CI 用の values では ConfigMap・マウント・環境変数・checksum が揃い、ConfigMap の値は values の文字列そのもの', () => {
    assert.deepStrictEqual(checkConfigured(configured, expected), []);
  });

  ok('値が変わると checksum が変わる（構成の変更でロールアウトする）', () => {
    const other = `${expected.trimEnd().replace('"ci-2026-10-26"', '"ci-2026-10-27"')}\n`;
    const r = mustRender(valuesFile(publicationValues('mcp', other)));
    assert.deepStrictEqual(checkConfigured(r, other), []);
    const sumOf = (txt) => /checksum\/mcp-publication:\s*"([0-9a-f]{64})"/.exec(txt)[1];
    assert.notStrictEqual(sumOf(r), sumOf(configured));
  });

  ok('JSON として読めない値は描画で止まる', () => {
    const r = render(valuesFile(publicationValues('mcp', '{ "version": "x", tools: [')));
    assert.notStrictEqual(r.status, 0, '壊れた JSON が描画を通った');
    assert.match(r.stderr, /JSON として読めません/);
  });

  ok('mcp 以外のサービスへの設定は描画で止まる', () => {
    const r = render(valuesFile(publicationValues('retrieval', '{ "version": "x", "tools": [] }')));
    assert.notStrictEqual(r.status, 0, 'mcp 以外への設定が描画を通った');
    assert.match(r.stderr, /services\.retrieval\.mcpPublication は書けません/);
  });

  ok('extraEnvAppend の Mcp__PublicationConfigPath との併記は描画で止まる', () => {
    const r = render([
      '-f', CI_VALUES,
      '--set', `services.mcp.extraEnvAppend[0].name=${ENV}`,
      '--set', 'services.mcp.extraEnvAppend[0].value=/elsewhere.json',
    ]);
    assert.notStrictEqual(r.status, 0, '併記が描画を通った');
    assert.match(r.stderr, /併記できません/);
  });

  ok('変異: マウント先と環境変数のパスの食い違いは赤', () => {
    const mutated = configured.replace(`value: /etc/microservices-platform/mcp/${KEY}`, 'value: /etc/microservices-platform/other/mcp-publication.json');
    assert.notStrictEqual(mutated, configured);
    assert.ok(checkConfigured(mutated, expected).some((e) => e.includes('を指さない')));
  });

  ok('変異: 環境変数の欠落は赤（イメージの既定を読み続ける）', () => {
    const mutated = configured.replace(new RegExp(`- name: ${escapeRe(ENV)}\\s*\\n\\s*value:[^\\n]*\\n`), '');
    assert.notStrictEqual(mutated, configured);
    assert.ok(checkConfigured(mutated, expected).some((e) => e.includes(`${ENV} が無い`)));
  });

  ok('変異: checksum の欠落は赤', () => {
    const mutated = configured.replace(/^\s*checksum\/mcp-publication:[^\n]*\n/m, '');
    assert.notStrictEqual(mutated, configured);
    assert.ok(checkConfigured(mutated, expected).some((e) => e.includes(`${CHECKSUM} が無い`)));
  });

  ok('変異: ConfigMap の値の食い違いは赤', () => {
    assert.ok(checkConfigured(configured, `${expected}x`).some((e) => e.includes('一致しない')));
  });

  ok('変異: 未設定の描画に ConfigMap が紛れ込むと赤', () => {
    assert.ok(checkUnconfigured(`${defaultRender}\n---\nkind: ConfigMap\nmetadata:\n  name: ${CONFIGMAP}\n`).length > 0);
  });

  process.stdout.write(`helm-mcp-publication.test: ${passed} 件合格\n`);
}

main();
