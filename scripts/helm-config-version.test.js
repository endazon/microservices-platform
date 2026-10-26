#!/usr/bin/env node
'use strict';
/*
 * helm-config-version.test.js
 * FR-15, IADR-0029 フォローアップ 3, IADR-0646 (#2118):
 * **構成バージョンのコミット ID（config.gitCommit → BFF の Config__GitCommit）が、配備の 2 経路で空でない値になる**ことを固定する。
 *
 * 🔴 守るのは「構成ビューアの構成バージョンが空のまま」を残さないことである。chart の既定は空で、従前は Argo CD の経路にも
 *    起動器（k8s-local-up.sh）の経路にも値を与える箇所が無かった。空でも画面は落ちず、気付けない。
 *
 * 固定するもの:
 *   1. Argo CD の Application は helm.parameters の config.gitCommit に `$ARGOCD_APP_REVISION`（forceString: true）を渡す。
 *      `$$`（置換を止める書き方）にしない。誤った旧注記（「ビルド変数をパラメータへ自動展開しない」）が残っていない。
 *   2. helm の描画で、config.gitCommit に与えた値が BFF の Config__GitCommit にそのまま載る（Argo CD が置換した値・起動器の値の行き先）。
 *      既定（何も与えない）は空である（陰性対照: 上の 2 経路が無いと空のままになる）。
 *   3. 起動器の経路は k8s-local-up.test.js の「#2118」の節が固定する（HEAD の完全な sha を 1 つだけ渡す・読めなければ止まる）。
 *   4. 変異: `$$ARGOCD_APP_REVISION`・パラメータの欠落・forceString の欠落を判定器へ通すと赤になる。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** helm-mcp-client-provisioning.test.js と同じ理由。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/helm-config-version.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const { RENDER_IMAGE_TAG_ARGS } = require('./lib/helm-render-tag'); // NFR-20, IADR-0605: chart はタグが無いと描画を落とす

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const APPLICATION = 'deploy/argocd/application.yaml';
const SHA = '0123456789abcdef0123456789abcdef01234567';

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

function mustRender(args) {
  const r = spawnSync('helm', ['template', 'msp', CHART, ...RENDER_IMAGE_TAG_ARGS, ...args], {
    cwd: REPO_ROOT,
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
  });
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.stderr}`);
  return r.stdout;
}

/** Application の helm.parameters の違反を返す（空なら合格）。字面だけを見る純関数。 */
function checkApplication(text) {
  const errors = [];
  const m = /^(\s*)- name: config\.gitCommit\n\1 {2}value:\s*(\S+)\s*\n\1 {2}forceString:\s*true\s*$/m.exec(text);
  if (!m) errors.push('helm.parameters に config.gitCommit（value と forceString: true）が無い');
  else if (m[2] !== '$ARGOCD_APP_REVISION') errors.push(`config.gitCommit の値が $ARGOCD_APP_REVISION ではない: ${m[2]}`);
  if (/自動展開しないため/.test(text)) errors.push('誤った旧注記（ビルド変数を自動展開しない）が残っている');
  return errors;
}

/** 描画物から BFF の Deployment の Config__GitCommit の値を取る（無ければ null）。 */
function gitCommitOf(rendered) {
  const bff = rendered.split(/^---\s*$/m).find((d) => /^kind:\s*Deployment/m.test(d) && /^ {2}name:\s*bff-service\s*$/m.test(d));
  assert.ok(bff, '対照: BFF の Deployment が描かれていない');
  const m = /- name: Config__GitCommit\s*\n\s*value:\s*"([^"]*)"/.exec(bff);
  return m ? m[1] : null;
}

function main() {
  if (!hasHelm()) {
    process.stderr.write('helm-config-version.test: helm が見つからない（fail-closed）\n');
    process.exit(1);
  }
  const app = fs.readFileSync(path.join(REPO_ROOT, APPLICATION), 'utf8');

  ok('Argo CD の Application は config.gitCommit に $ARGOCD_APP_REVISION（forceString）を渡し、誤った旧注記が無い', () => {
    assert.deepStrictEqual(checkApplication(app), []);
  });

  ok('helm の描画で config.gitCommit の値が BFF の Config__GitCommit にそのまま載る', () => {
    assert.strictEqual(gitCommitOf(mustRender(['--set-string', `config.gitCommit=${SHA}`])), SHA);
  });

  ok('既定（何も与えない）は空である（陰性対照: 2 経路が値を与えないと空のまま）', () => {
    assert.strictEqual(gitCommitOf(mustRender([])), '');
  });

  ok('変異: $$（置換を止める書き方）は赤', () => {
    // 置換文字列の `$$` は JS では `$` 1 つになるので、関数で返す。
    const mutated = app.replace('value: $ARGOCD_APP_REVISION', () => 'value: $$ARGOCD_APP_REVISION');
    assert.notStrictEqual(mutated, app);
    assert.ok(checkApplication(mutated).some((e) => e.includes('ではない')));
  });

  ok('変異: パラメータの欠落は赤', () => {
    const mutated = app.replace(/^\s*- name: config\.gitCommit\n.*\n.*forceString: true\n/m, '');
    assert.notStrictEqual(mutated, app);
    assert.ok(checkApplication(mutated).some((e) => e.includes('無い')));
  });

  ok('変異: forceString の欠落は赤（数字だけの sha が数として読まれる）', () => {
    const mutated = app.replace(/(- name: config\.gitCommit\n\s*value: \$ARGOCD_APP_REVISION\n)\s*forceString: true\n/, '$1');
    assert.notStrictEqual(mutated, app);
    assert.ok(checkApplication(mutated).some((e) => e.includes('無い')));
  });

  process.stdout.write(`helm-config-version.test: ${passed} 件合格\n`);
}

main();
