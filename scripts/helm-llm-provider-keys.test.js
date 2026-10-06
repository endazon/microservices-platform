#!/usr/bin/env node
'use strict';
/*
 * helm-llm-provider-keys.test.js
 * FR-02, ADR-0016, IADR-0504 (#1764):
 * **埋め込みの鍵（`Embedding__Voyage__ApiKey`）が LLM ゲートウェイにだけ、Secret の参照（secretKeyRef）として届くことを、
 * 実際に `helm template` で描いて固定する。** 併せて、供給側（ESO の ExternalSecret・Vault の種・SC-22 の項目表・
 * ESO 無しの手動 Secret）が同じキー名 `voyage-api-key` で揃っていることを字面で突き合わせる。
 *
 * 🔴 **守るのは「鍵の値がチャートにも描画物にも現れない」ことと「供給の連鎖が 1 か所も切れていない」ことである。**
 *    経路B は鍵の配線が無いまま動き、Qdrant の 3 コレクションが 0 件のまま気付かれなかった（#1762）。
 *    単体試験は構成を自分で与えて走るので、配線の欠落では絶対に落ちない。
 *
 * 固定するもの:
 *   1. 既定（本番像）・values-local の描画で、env `Embedding__Voyage__ApiKey` は llmgateway-service の Deployment に
 *      ちょうど 1 回だけ現れ、valueFrom.secretKeyRef（Secret llm-provider-credentials・キー voyage-api-key・optional: true）であり、
 *      `value:`（リテラル）を持たない。他のワークロードには現れない。
 *   2. 同じ Deployment に `Llm__ApiKey`（anthropic-api-key）が残っている（values-local の extraEnv はリストの置換なので、
 *      片方だけ書くともう片方が消える）。
 *   3. `optional:` は描画物の中でこの 1 行にしか現れない（テンプレートの変更が他の secretKeyRef の字面を変えていない）。
 *   4. ExternalSecret（deploy/local/vault/eso/externalsecret-llm.yaml）のキーは anthropic・openai・voyage の 3 つちょうどで、
 *      voyage-api-key は msp/llm-provider-credentials の同名プロパティを読む。SC-22 の項目表の properties と集合が一致する。
 *   5. Vault の種（bootstrap.sh）が voyage-api-key を「無いときだけ空で足す」（ESO はプロパティが無いと同期全体を失敗させる）、
 *      ESO 無しの手動 Secret（k8s-local-up.sh）も voyage-api-key を持つ。
 *   6. 変異: リテラルの値・別のワークロードへの混入・Llm__ApiKey の欠落を、実際に `helm template` で描かせると本試験の判定が赤になる。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** helm-synthetic-monitor.test.js と同じ理由（黙って飛ばすと
 *    「検査していない」と「問題が無い」が同じ出力になる）。CI は static-checks-units（azure/setup-helm 済み）で走らせる。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/helm-llm-provider-keys.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const VALUES_LOCAL = 'deploy/local/values-local.yaml';
const EXTERNAL_SECRET = 'deploy/local/vault/eso/externalsecret-llm.yaml';
const CATALOG = 'deploy/bootstrap/sc22-secret-items.json';
const BOOTSTRAP = 'deploy/local/vault/eso/bootstrap.sh';
const LOCAL_UP = 'scripts/k8s-local-up.sh';

const ENV_NAME = 'Embedding__Voyage__ApiKey';
const SECRET_NAME = 'llm-provider-credentials';
const SECRET_KEY = 'voyage-api-key';
const OWNER = 'llmgateway-service';

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

function mustRender(args) {
  const r = spawnSync('helm', ['template', 'msp', CHART, ...args], {
    cwd: REPO_ROOT,
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
  });
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.stderr}`);
  return r.stdout;
}

/** 描画物を文書へ割り、kind と metadata.name（最初に現れる 2 字下げの name）を添える。 */
function documents(rendered) {
  return rendered.split(/^---\s*$/m).map((text) => {
    const kind = (text.match(/^kind:\s*(\S+)/m) || [])[1] || '';
    const meta = text.match(/^metadata:\n((?:[ ].*\n)+)/m);
    const name = meta ? ((meta[1].match(/^ {2}name:\s*(\S+)/m) || [])[1] || '') : '';
    return { kind, name, text };
  });
}

/** env の 1 項目（`- name: X` から同じ字下げの次の `- ` か、より浅い行の手前まで）を切り出す。 */
function envEntries(text, envName) {
  const lines = text.split('\n');
  const out = [];
  for (let i = 0; i < lines.length; i++) {
    const m = lines[i].match(/^(\s*)- name:\s*["']?([^"'\s]+)["']?\s*$/);
    if (!m || m[2] !== envName) continue;
    const indent = m[1].length;
    const body = [lines[i]];
    for (let j = i + 1; j < lines.length; j++) {
      const l = lines[j];
      if (l.trim() === '' || /^\s*#/.test(l)) continue;
      const ind = l.match(/^(\s*)/)[1].length;
      if (ind < indent || (ind === indent && /^\s*- /.test(l))) break;
      body.push(l);
    }
    out.push(body.join('\n'));
  }
  return out;
}

/** 判定器本体。違反の一覧を返す（空なら合格）。描画物の字面だけを見る純関数。 */
function checkRender(rendered) {
  const errors = [];
  const hits = [];
  for (const d of documents(rendered)) {
    for (const e of envEntries(d.text, ENV_NAME)) hits.push({ ...d, entry: e });
  }
  if (hits.length !== 1) errors.push(`${ENV_NAME} の出現が ${hits.length} 回（期待は ${OWNER} の 1 回）: ${hits.map((h) => `${h.kind}/${h.name}`).join(', ')}`);
  for (const h of hits) {
    if (!(h.kind === 'Deployment' && h.name === OWNER)) errors.push(`${ENV_NAME} が ${h.kind}/${h.name} に現れた（${OWNER} だけに渡す）`);
    if (/^\s*value:/m.test(h.entry)) errors.push(`${ENV_NAME} がリテラルの value を持つ（Secret の参照で渡す）:\n${h.entry}`);
    if (!/^\s*valueFrom:\s*$/m.test(h.entry) || !/^\s*secretKeyRef:\s*$/m.test(h.entry)) errors.push(`${ENV_NAME} が secretKeyRef ではない:\n${h.entry}`);
    if (!new RegExp(`^\\s*name:\\s*${SECRET_NAME}\\s*$`, 'm').test(h.entry)) errors.push(`${ENV_NAME} の Secret が ${SECRET_NAME} ではない`);
    if (!new RegExp(`^\\s*key:\\s*${SECRET_KEY}\\s*$`, 'm').test(h.entry)) errors.push(`${ENV_NAME} のキーが ${SECRET_KEY} ではない`);
    if (!/^\s*optional:\s*true\s*$/m.test(h.entry)) errors.push(`${ENV_NAME} が optional: true ではない（キーが無いと Pod が起動しない。IADR-0504 決定 2）`);
  }
  const owner = documents(rendered).find((d) => d.kind === 'Deployment' && d.name === OWNER);
  if (!owner) errors.push(`Deployment/${OWNER} が描画されていない`);
  else {
    const llm = envEntries(owner.text, 'Llm__ApiKey');
    if (llm.length !== 1 || !/^\s*key:\s*anthropic-api-key\s*$/m.test(llm[0])) errors.push(`${OWNER} の Llm__ApiKey（anthropic-api-key）が無い（extraEnv の置換で落ちた）`);
  }
  const optionals = (rendered.match(/^\s*optional:/gm) || []).length;
  if (optionals !== 1) errors.push(`optional: が描画物に ${optionals} 回現れた（期待は ${ENV_NAME} の 1 回）`);
  return errors;
}

/** ExternalSecret の data[] を { secretKey, key, property } の列で返す（この 1 ファイルの字面に足りる最小の読み）。 */
function externalSecretData(text) {
  const body = text.split(/^\s*data:\s*$/m)[1] || '';
  return body.split(/^\s*- secretKey:/m).slice(1).map((chunk) => ({
    secretKey: chunk.split('\n')[0].trim(),
    key: ((chunk.match(/^\s*key:\s*(\S+)/m) || [])[1] || ''),
    property: ((chunk.match(/^\s*property:\s*(\S+)/m) || [])[1] || ''),
  }));
}

// ---------------------------------------------------------------- tests

if (!hasHelm()) {
  process.stderr.write('helm が見つからない。本試験は helm template の描画を検査するため、helm が無ければ失敗にする（fail-closed）。\n');
  process.exit(1);
}

const DEFAULT = mustRender([]);
const LOCAL = mustRender(['-f', VALUES_LOCAL]);

ok('既定（本番像）: Embedding__Voyage__ApiKey は llmgateway-service にだけ secretKeyRef（optional）で 1 回', () => {
  assert.deepStrictEqual(checkRender(DEFAULT), []);
});

ok('values-local（経路B）: 同じ形で描かれ、Llm__ApiKey も残っている', () => {
  assert.deepStrictEqual(checkRender(LOCAL), []);
});

ok('ExternalSecret のキーは anthropic・openai・voyage の 3 つちょうどで、voyage-api-key は同じ KV の同名プロパティを読む', () => {
  const data = externalSecretData(read(EXTERNAL_SECRET));
  assert.deepStrictEqual(data.map((d) => d.secretKey).sort(), ['anthropic-api-key', 'openai-api-key', SECRET_KEY]);
  const v = data.find((d) => d.secretKey === SECRET_KEY);
  assert.deepStrictEqual({ key: v.key, property: v.property }, { key: 'msp/llm-provider-credentials', property: SECRET_KEY });
  assert.ok(/^\s*name:\s*llm-provider-credentials\s*$/m.test(read(EXTERNAL_SECRET).split(/^\s*target:\s*$/m)[1] || ''),
    'ExternalSecret の同期先が llm-provider-credentials ではない');
});

ok('SC-22 の項目表の properties は ExternalSecret のキーと同じ集合（画面で書けるのに同期されない・同期されるのに書けない、を作らない）', () => {
  const item = JSON.parse(read(CATALOG)).items.find((i) => i.item === SECRET_NAME);
  assert.ok(item, `項目 ${SECRET_NAME} が無い`);
  const props = item.properties.map((p) => (typeof p === 'string' ? p : p.name)).sort();
  assert.deepStrictEqual(props, externalSecretData(read(EXTERNAL_SECRET)).map((d) => d.secretKey).sort());
});

ok('Vault の種は voyage-api-key を「無いときだけ空で足す」・作るときも持つ。ESO 無しの手動 Secret も持つ', () => {
  const b = read(BOOTSTRAP);
  assert.ok(/^\s*vkv_patch_if_missing msp\/llm-provider-credentials voyage-api-key ''\s*$/m.test(b),
    'bootstrap.sh が在る KV へ voyage-api-key を空で足していない（ESO の同期全体が失敗する）');
  assert.ok(/^\s*vkv_patch_nonempty msp\/llm-provider-credentials voyage-api-key "\$\{VOYAGE_API_KEY:-\}"\s*$/m.test(b),
    'bootstrap.sh が VOYAGE_API_KEY を部分更新で受けていない');
  assert.ok(/vault kv put -cas=0 secret\/msp\/llm-provider-credentials [^\n]*voyage-api-key='\$\{VOYAGE_API_KEY:-\}'/.test(b),
    'bootstrap.sh が KV を作るときに voyage-api-key を持たせていない');
  assert.ok(/apply_secret "\$MSP_NS" llm-provider-credentials[\s\S]{0,300}"voyage-api-key=\$\{VOYAGE_API_KEY:-\}"/.test(read(LOCAL_UP)),
    'k8s-local-up.sh の手動 Secret（ESO 無し）に voyage-api-key が無い');
});

// ---------------------------------------------------------------- mutation（判定器が赤になること）

const literal = JSON.stringify([
  { name: 'Llm__ApiKey', secretKeyRef: { name: SECRET_NAME, key: 'anthropic-api-key' } },
  { name: ENV_NAME, value: 'literal-not-a-key' },
]);
ok('変異: リテラルの値を描かせると赤', () => {
  const errs = checkRender(mustRender(['-f', VALUES_LOCAL, '--set-json', `services.llmgateway.extraEnv=${literal}`]));
  assert.ok(errs.some((e) => /リテラルの value/.test(e)), errs.join('\n'));
});

ok('変異: 別のワークロード（wiki）にも混ぜると赤', () => {
  const extra = JSON.stringify([{ name: ENV_NAME, secretKeyRef: { name: SECRET_NAME, key: SECRET_KEY } }]);
  const errs = checkRender(mustRender(['-f', VALUES_LOCAL, '--set-json', `services.wiki.extraEnvAppend=${extra}`]));
  assert.ok(errs.some((e) => /出現が 2 回/.test(e)) && errs.some((e) => /wiki-service/.test(e)), errs.join('\n'));
});

ok('変異: values-local が Voyage だけを書いて Llm__ApiKey を落とすと赤', () => {
  const only = JSON.stringify([{ name: ENV_NAME, secretKeyRef: { name: SECRET_NAME, key: SECRET_KEY, optional: true } }]);
  const errs = checkRender(mustRender(['-f', VALUES_LOCAL, '--set-json', `services.llmgateway.extraEnv=${only}`]));
  assert.ok(errs.some((e) => /Llm__ApiKey/.test(e)), errs.join('\n'));
});

ok('変異: optional を外すと赤（キーの無い Secret で Pod が起動しなくなる）', () => {
  const required = JSON.stringify([
    { name: 'Llm__ApiKey', secretKeyRef: { name: SECRET_NAME, key: 'anthropic-api-key' } },
    { name: ENV_NAME, secretKeyRef: { name: SECRET_NAME, key: SECRET_KEY } },
  ]);
  const errs = checkRender(mustRender(['-f', VALUES_LOCAL, '--set-json', `services.llmgateway.extraEnv=${required}`]));
  assert.ok(errs.some((e) => /optional: true ではない/.test(e)), errs.join('\n'));
});

process.stdout.write(`✓ ${passed} tests passed\n`);
