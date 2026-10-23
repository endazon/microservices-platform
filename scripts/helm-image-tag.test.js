#!/usr/bin/env node
'use strict';
/*
 * helm-image-tag.test.js
 * NFR-20, ADR-0007, IADR-0605, #2132（親 #2047 指摘 M-27）:
 * **自製イメージ（services の全 Deployment と frontend）が不変のタグを参照し、タグが無ければ描画が落ちることを、
 * 実際に `helm template` で描いて固定する。**
 *
 * 従前 chart は全サービスを `tag: latest`（19 箇所）＋ `pullPolicy: IfNotPresent` で描き、template も `default "latest"` で倒していた。
 * 同じタグの中身が替わってもノードのキャッシュが古い中身を配り続け、版の固定もロールバック（前の版の指定）も成り立たなかった。
 *
 * 固定するもの:
 *   1. 既定（本番像）と values-local の描画で、自製イメージ（`<registry>/microservices-platform/...`）を持つコンテナは
 *      **すべて** `global.image.tag` の値を参照する（1 件も latest を参照しない）。件数は values の宣言から数え、0 件を緑にしない。
 *   2. サービスごとの上書き（`services.<名>.tag` / `frontend.tag`）はそのサービスだけを替える。
 *   3. タグが無い（既定・values-local のまま）なら描画が落ち、何を渡せばよいかを告げる。`latest`・`--set` の数・タグの形でない値も落ちる。
 *   4. 値の側: values.yaml に `tag: latest` が無く、`global.image.tag` の既定は空。template の自製イメージの行はすべて共通の部品を通る。
 *   5. 配備の経路: ArgoCD の Application はタグの口（`global.image.tag`・forceString）を持つ。起動器は同じタグでビルドして helm へ渡す。
 *   6. ローカルのタグの作り方（scripts/lib/local-image-tag.sh）: 変更の無い作業ツリーは HEAD の sha、変更があれば差分の hash つき、
 *      IMAGE_TAG の上書きは検査つき（latest を拒む）。
 *   7. 変異: (a) template に `default "latest"` を戻す、(b) `required` を外す、(c) `latest` の拒否を外す —— いずれも判定が赤になる。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** CI は static-checks-units で走らせる。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm ＋ git ＋ bash）。実行: node scripts/helm-image-tag.test.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const VALUES = `${CHART}/values.yaml`;
const VALUES_LOCAL = 'deploy/local/values-local.yaml';
const APPLICATION = 'deploy/argocd/application.yaml';
const TAG_LIB = 'scripts/lib/local-image-tag.sh';
const TAG = '0a1b2c3d4e5f';

const read = (rel) => fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

function hasHelm() {
  return spawnSync(process.platform === 'win32' ? 'where' : 'which', ['helm'], { encoding: 'utf8' }).status === 0;
}

function render(chart, args) {
  return spawnSync('helm', ['template', 'msp', chart, ...args], { cwd: REPO_ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
}
function mustRender(chart, args) {
  const r = render(chart, args);
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.stderr}`);
  return r.stdout;
}

/** 描画物の Deployment ごとの { name, images }。 */
function deployments(rendered) {
  return rendered.split(/^---\s*$/m)
    .filter((t) => /^kind:\s*Deployment\s*$/m.test(t))
    .map((text) => ({
      name: ((text.match(/^metadata:\n(?: {2}.*\n)*? {2}name:\s*(\S+)/m) || [])[1] || ''),
      images: [...text.matchAll(/^\s*(?:- )?image:\s*"?([^"\s]+)"?\s*$/gm)].map((m) => m[1]),
    }));
}

/** 自製イメージ（`<registry>/microservices-platform/<名>:<タグ>`）の参照だけを取り出す。 */
function selfBuilt(rendered) {
  const out = [];
  for (const d of deployments(rendered)) {
    for (const ref of d.images) if (/^[^/]+\/microservices-platform\//.test(ref)) out.push({ deployment: d.name, ref });
  }
  return out;
}

/** values の宣言から、描かれるはずの自製イメージの数（services の enabled と frontend.enabled）。 */
function expectedSelfBuiltCount(valuesFiles) {
  const r = mustRender(CHART, ['--set-string', `global.image.tag=${TAG}`, ...valuesFiles.flatMap((f) => ['-f', f]), '--show-only', 'templates/deployment.yaml', '--show-only', 'templates/frontend.yaml']);
  return deployments(r).length;
}

/** 判定 1: 自製イメージの参照がすべて :<tag> で終わり、件数が宣言と一致する。違反の文言の配列。 */
function tagViolations(rendered, tag, expectedCount) {
  const refs = selfBuilt(rendered);
  const v = [];
  if (refs.length === 0) v.push('自製イメージの参照が 1 件も無い（走査が壊れている。0 件を緑にしない）');
  if (expectedCount !== undefined && refs.length !== expectedCount) v.push(`自製イメージの参照が ${refs.length} 件（宣言から数えた ${expectedCount} 件と違う）`);
  for (const { deployment, ref } of refs) {
    if (!ref.endsWith(`:${tag}`)) v.push(`${deployment}: ${ref} が不変のタグ ${tag} を参照していない`);
    if (/:latest$/.test(ref)) v.push(`${deployment}: ${ref} が latest を参照している`);
  }
  return v;
}

/** 判定 3: タグが無い・不正なら描画が落ち、落ちた理由がタグである。違反の文言の配列。 */
function failClosedViolations(chart) {
  const v = [];
  const cases = [
    // 初回の切り替え前のリリースへ --reuse-values を流した人が、エラー文から起動器の再実行へ辿れること（レビュー 🟡）
    ['既定（タグなし）', [], /global\.image\.tag[\s\S]*k8s-local-up\.sh/],
    ['values-local（タグなし）', ['-f', VALUES_LOCAL], /global\.image\.tag[\s\S]*k8s-local-up\.sh/],
    ['latest を明示', ['--set-string', 'global.image.tag=latest'], /latest は使えない/],
    ['サービスの上書きに latest', ['--set-string', `global.image.tag=${TAG}`, '--set-string', 'services.bff.tag=latest'], /latest は使えない/],
    ['--set で数字だけの sha（数として読まれる）', ['--set', 'global.image.tag=123456789012'], /--set-string/],
    ['タグの形でない', ['--set-string', 'global.image.tag=a/b'], /Docker のタグの形でない/],
  ];
  for (const [what, args, reason] of cases) {
    const r = render(chart, args);
    if (r.status === 0) v.push(`${what}: 描画が通った（落ちるべき）`);
    else if (!reason.test(r.stderr)) v.push(`${what}: 落ちたが理由がタグでない: ${r.stderr.trim().split('\n')[0]}`);
  }
  return v;
}

/** 変異用: chart を一時ディレクトリへ写し、_image.tpl に変異を当てる。 */
function mutatedChart(mutate) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'msp-image-tag-'));
  const dst = path.join(dir, 'chart');
  fs.cpSync(path.join(REPO_ROOT, CHART), dst, { recursive: true });
  const tpl = path.join(dst, 'templates', '_image.tpl');
  const before = fs.readFileSync(tpl, 'utf8');
  const after = mutate(before);
  assert.notStrictEqual(after, before, '変異が当たっていない（置換の対象が見つからない）');
  fs.writeFileSync(tpl, after);
  return { chart: dst, cleanup: () => fs.rmSync(dir, { recursive: true, force: true }) };
}

function bash(script, { cwd = REPO_ROOT, env = {} } = {}) {
  const e = { ...process.env, ...env };
  if (!('IMAGE_TAG' in env)) delete e.IMAGE_TAG;
  return spawnSync('bash', ['-c', script], { cwd, env: e, encoding: 'utf8' });
}
function git(cwd, ...args) {
  const r = spawnSync('git', args, { cwd, encoding: 'utf8', env: { ...process.env, GIT_AUTHOR_NAME: 't', GIT_AUTHOR_EMAIL: 't@example.invalid', GIT_COMMITTER_NAME: 't', GIT_COMMITTER_EMAIL: 't@example.invalid' } });
  assert.strictEqual(r.status, 0, `git ${args.join(' ')}: ${r.stderr}`);
  return r.stdout.trim();
}

if (!hasHelm()) {
  process.stderr.write('helm が見つからない。本試験は helm template で描いて確かめるので、helm 無しでは合否を出せない（fail-closed）。\n');
  process.exit(1);
}

// ---- 1. 不変のタグを参照する
for (const [label, files] of [['既定（本番像）', []], ['values-local', [VALUES_LOCAL]]]) {
  ok(`${label}: 自製イメージはすべて global.image.tag を参照する（件数は宣言と一致・latest は 0 件）`, () => {
    const rendered = mustRender(CHART, [...files.flatMap((f) => ['-f', f]), '--set-string', `global.image.tag=${TAG}`]);
    const expected = expectedSelfBuiltCount(files);
    assert.ok(expected >= 16, `宣言から数えた自製イメージが ${expected} 件しかない（services と frontend を数えられていない）`);
    assert.deepStrictEqual(tagViolations(rendered, TAG, expected), []);
  });
}

ok('pullPolicy は IfNotPresent のまま（不変のタグなら新しいタグは取りに行く。k3d-local の擬似レジストリは Always にできない）', () => {
  const rendered = mustRender(CHART, ['--set-string', `global.image.tag=${TAG}`]);
  const policies = [...rendered.matchAll(/^\s*imagePullPolicy:\s*(\S+)\s*$/gm)].map((m) => m[1]);
  assert.ok(policies.length > 0);
  assert.deepStrictEqual([...new Set(policies)], ['IfNotPresent']);
});

// ---- 2. サービスごとの上書き
ok('サービスごとの上書き（services.bff.tag / frontend.tag）はそのサービスだけを替える', () => {
  const rendered = mustRender(CHART, ['--set-string', `global.image.tag=${TAG}`, '--set-string', 'services.bff.tag=bff111', '--set-string', 'frontend.tag=fe222']);
  const refs = selfBuilt(rendered);
  const byDep = Object.fromEntries(refs.map((r) => [r.deployment, r.ref]));
  assert.ok(byDep['bff-service'].endsWith(':bff111'), byDep['bff-service']);
  assert.ok(byDep['frontend-service'].endsWith(':fe222'), byDep['frontend-service']);
  const others = refs.filter((r) => !['bff-service', 'frontend-service'].includes(r.deployment));
  assert.ok(others.length > 0 && others.every((r) => r.ref.endsWith(`:${TAG}`)), '上書きが他のサービスへ漏れた');
});

// ---- 3. 描画で落ちる
ok('タグが無い・latest・--set の数・タグの形でない値は描画が落ち、理由がタグである（latest へ黙って倒さない）', () => {
  assert.deepStrictEqual(failClosedViolations(CHART), []);
});

// ---- 4. 値と template の側
ok('values.yaml に tag: latest が無く、global.image.tag の既定は空', () => {
  const v = read(VALUES);
  assert.ok(!/^\s*tag:\s*"?latest"?\s*$/m.test(v), 'values.yaml に tag: latest が残っている');
  assert.match(v, /^global:\n {2}image:\n(?: {4}.*\n| *#.*\n)*? {4}tag:\s*""\s*$/m, 'global.image.tag: "" が無い');
});
ok('template の自製イメージの行はすべて共通の部品（msp.selfBuiltImageTag）を通り、default "latest" を持たない', () => {
  const dir = path.join(REPO_ROOT, CHART, 'templates');
  const lines = [];
  for (const f of fs.readdirSync(dir)) {
    for (const l of fs.readFileSync(path.join(dir, f), 'utf8').split('\n')) {
      if (/image:\s*".*global\.image\.registry/.test(l)) lines.push({ f, l });
      assert.ok(!/default\s+"latest"/.test(l), `${f}: default "latest" が残っている: ${l.trim()}`);
    }
  }
  assert.strictEqual(lines.length, 2, `自製イメージの行が ${lines.length} 件（deployment.yaml と frontend.yaml の 2 件のはず）`);
  for (const { f, l } of lines) assert.ok(/include "msp\.selfBuiltImageTag"/.test(l), `${f}: 共通の部品を通っていない: ${l.trim()}`);
});

// ---- 5. 配備の経路
ok('ArgoCD の Application はタグの口（global.image.tag・forceString: true）を持ち、latest を与えない', () => {
  const a = read(APPLICATION);
  const m = a.match(/^(\s*)- name: global\.image\.tag\n\1 {2}value:\s*"([^"]*)"\n\1 {2}forceString:\s*true\s*$/m);
  assert.ok(m, 'helm.parameters に global.image.tag（forceString: true）が無い');
  assert.notStrictEqual(m[2], 'latest');
});
ok('起動器は [6/7] の helm へ --set-string global.image.tag=${IMAGE_TAG} を渡し、ビルドと同じ値を export する', () => {
  const up = read('scripts/k8s-local-up.sh');
  assert.match(up, /^helm upgrade --install msp deploy\/helm\/microservices-platform \\\n {2}-n "\$MSP_NS" -f deploy\/local\/values-local\.yaml --set-string "global\.image\.tag=\$\{IMAGE_TAG\}"/m);
  assert.match(up, /^IMAGE_TAG="\$\(resolve_local_image_tag "\$ROOT"\)" \|\| exit 1\nexport IMAGE_TAG$/m);
  const images = read('scripts/k8s-local-images.sh');
  assert.match(images, /^TAG="\$\(resolve_local_image_tag "\$ROOT"\)" \|\| exit 1$/m);
  assert.ok(!/^TAG="latest"/m.test(images), 'k8s-local-images.sh が latest でビルドしている');
});

// ---- 6. ローカルのタグの作り方
ok('local-image-tag.sh: 変更の無い作業ツリーは HEAD の sha（12 桁）、変更があれば <sha>-dirty-<8 桁>（同じ差分なら同じ値）', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'msp-local-tag-'));
  try {
    git(dir, 'init', '-q');
    fs.writeFileSync(path.join(dir, 'a.txt'), 'a\n');
    git(dir, 'add', 'a.txt');
    git(dir, 'commit', '-q', '-m', 'init');
    const sha = git(dir, 'rev-parse', '--short=12', 'HEAD');
    const lib = path.join(REPO_ROOT, TAG_LIB);
    const tagOf = () => {
      const r = bash(`bash "${lib}" "${dir}"`);
      assert.strictEqual(r.status, 0, r.stderr);
      return r.stdout.trim();
    };
    assert.strictEqual(tagOf(), sha);
    fs.writeFileSync(path.join(dir, 'a.txt'), 'b\n');
    const dirty1 = tagOf();
    assert.match(dirty1, new RegExp(`^${sha}-dirty-[0-9a-f]{8}$`));
    assert.strictEqual(tagOf(), dirty1, '同じ差分で値が変わった（再実行のたびにタグが増える）');
    fs.writeFileSync(path.join(dir, 'a.txt'), 'c\n');
    assert.notStrictEqual(tagOf(), dirty1, '中身の違う差分が同じタグになった');
    fs.writeFileSync(path.join(dir, 'a.txt'), 'a\n');
    assert.strictEqual(tagOf(), sha, '差分を戻しても sha に戻らない');
    fs.writeFileSync(path.join(dir, 'new.txt'), 'n\n');
    assert.match(tagOf(), /-dirty-/, '追跡していない新しいファイルを変更として数えていない');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
ok('local-image-tag.sh: IMAGE_TAG の上書きは検査つき（latest・空白入り・/ 入りを拒み、正しい値はそのまま返す）', () => {
  const lib = path.join(REPO_ROOT, TAG_LIB);
  const r = bash(`bash "${lib}"`, { env: { IMAGE_TAG: 'abc123' } });
  assert.strictEqual(r.status, 0, r.stderr);
  assert.strictEqual(r.stdout.trim(), 'abc123');
  for (const bad of ['latest', 'a b', 'a/b', '-x']) {
    const b = bash(`bash "${lib}"`, { env: { IMAGE_TAG: bad } });
    assert.notStrictEqual(b.status, 0, `IMAGE_TAG=${bad} を受けた`);
  }
});
ok('local-image-tag.sh: git の HEAD を読めない場所では止まり、IMAGE_TAG を与えるよう告げる', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'msp-local-tag-nogit-'));
  try {
    const r = bash(`bash "${path.join(REPO_ROOT, TAG_LIB)}" "${dir}"`, { env: { GIT_CEILING_DIRECTORIES: path.dirname(dir) } });
    assert.notStrictEqual(r.status, 0);
    assert.match(r.stderr, /IMAGE_TAG=/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ---- 7. 変異
const MUTATIONS = [
  ['(a) default "latest" を戻す', (s) => s.replace('{{- $tag := .tag | default .root.Values.global.image.tag -}}', '{{- $tag := .tag | default .root.Values.global.image.tag | default "latest" -}}')],
  ['(b) required を外す', (s) => s.replace(/\{\{- \$tag = required \(printf .*?\) \$tag -\}\}/, '')],
  ['(c) latest の拒否を外す', (s) => s.replace(/\{\{- if eq \$tag "latest" -\}\}\n.*\n\{\{- end -\}\}\n/, '')],
];
for (const [what, mutate] of MUTATIONS) {
  ok(`変異 ${what}: 判定が赤になる（判定が空振りしない）`, () => {
    const m = mutatedChart(mutate);
    try {
      assert.ok(failClosedViolations(m.chart).length > 0, `${what} を当てても描画の判定が緑のまま`);
    } finally {
      m.cleanup();
    }
  });
}
ok('変異 (d) 描画物の 1 件を latest へ替える: 参照の判定が赤になる', () => {
  const rendered = mustRender(CHART, ['--set-string', `global.image.tag=${TAG}`]);
  const mutated = rendered.replace(`microservices-platform/bff:${TAG}`, 'microservices-platform/bff:latest');
  assert.notStrictEqual(mutated, rendered);
  assert.ok(tagViolations(mutated, TAG).length >= 1);
});

process.stdout.write(`\nhelm-image-tag: ${passed} 件 ok\n`);
