#!/usr/bin/env node
'use strict';
/*
 * helm-mcp-client-provisioning.test.js
 * FR-16, SC-12, 計画 ADR-0123 決定 2・4, IADR-0516 決定 2 (#1817):
 * **SC-12 の IdP への書き込み口（`McpClientProvisioning__*`）が mcp-service にだけ、Provider=keycloak と
 * 非 optional な Secret の参照（secretKeyRef）つきで届くことを、実際に `helm template` で描いて固定する。**
 *
 * 🔴 **守るのは「Provider を宣言していない配備が残らない」ことである。** 段 1（#1786）のマージ後、配備は書き込み口を
 *    宣言しておらず、無人の登録・差し替えが 503 になった。未宣言は起動を止めない（IADR-0516 決定 2）ので、
 *    **Pod は Ready のまま 503 を返し続ける**（静かな縮退）。単体試験は構成を自分で与えて走るので、配線の欠落では落ちない。
 *
 * 固定するもの:
 *   1. 既定（本番像）・values-local の描画で、`McpClientProvisioning__*` を 1 つでも持つ Deployment は、すべて
 *      `McpClientProvisioning__Provider` を `value: "keycloak"` でちょうど 1 回持つ（**否定形: Provider 未宣言の配備が無い**）。
 *   2. それを持つ Deployment は mcp-service ただ 1 つである（管理用の資格情報を他のワークロードへ配らない）。
 *   3. ClientSecret は valueFrom.secretKeyRef（Secret mcp-client-admin-oidc・キー client-secret）で、`optional:` を持たない
 *      （非 optional ＝ Secret が無ければ Pod が起動しない）。リテラルの value を持たない。
 *   4. BaseUrl / Realm / ClientId がリテラルで在る（既定値はアプリに無い。IADR-0286）。
 *   5. 変異: Provider の欠落・Provider の値違い・ClientSecret の optional 化・リテラル化・別ワークロードへの混入を
 *      判定器へ通すと赤になる。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** helm-llm-provider-keys.test.js と同じ理由。CI は static-checks-units で走らせる。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/helm-mcp-client-provisioning.test.js
 */
const assert = require('assert');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const VALUES_LOCAL = 'deploy/local/values-local.yaml';

const PREFIX = 'McpClientProvisioning__';
const PROVIDER = 'McpClientProvisioning__Provider';
const CLIENT_SECRET = 'McpClientProvisioning__Keycloak__ClientSecret';
const OWNER = 'mcp-service';
const SECRET_NAME = 'mcp-client-admin-oidc';
const SECRET_KEY = 'client-secret';
const LITERALS = {
  McpClientProvisioning__Keycloak__BaseUrl: 'http://keycloak:8080',
  McpClientProvisioning__Keycloak__Realm: 'platform',
  McpClientProvisioning__Keycloak__ClientId: 'mcp-client-admin',
};

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

/** env の項目（`- name: X` から同じ字下げの次の `- ` か、より浅い行の手前まで）を { name, body } の列で返す。 */
function envEntries(text) {
  const lines = text.split('\n');
  const out = [];
  for (let i = 0; i < lines.length; i++) {
    const m = lines[i].match(/^(\s*)- name:\s*["']?([^"'\s]+)["']?\s*$/);
    if (!m) continue;
    const indent = m[1].length;
    const body = [lines[i]];
    for (let j = i + 1; j < lines.length; j++) {
      const l = lines[j];
      if (l.trim() === '' || /^\s*#/.test(l)) continue;
      const ind = l.match(/^(\s*)/)[1].length;
      if (ind < indent || (ind === indent && /^\s*- /.test(l))) break;
      body.push(l);
    }
    out.push({ name: m[2], body: body.join('\n') });
  }
  return out;
}

const literalOf = (entry) => {
  const m = /^\s*value:\s*["']?([^"'\n]*)["']?\s*$/m.exec(entry.body);
  return m ? m[1] : null;
};

/** 判定器本体。違反の一覧を返す（空なら合格）。描画物の字面だけを見る純関数。 */
function checkRender(rendered) {
  const errors = [];
  const holders = documents(rendered)
    .map((d) => ({ ...d, env: envEntries(d.text).filter((e) => e.name.startsWith(PREFIX)) }))
    .filter((d) => d.env.length > 0);
  if (holders.length === 0) errors.push(`${PREFIX}* を持つワークロードが 1 つも無い（mcp-service の書き込み口が描画されていない）`);
  for (const d of holders) {
    const where = `${d.kind}/${d.name}`;
    if (!(d.kind === 'Deployment' && d.name === OWNER)) errors.push(`${PREFIX}* が ${where} に現れた（${OWNER} だけに渡す）`);
    // 🔴 否定形の本体: 書き込み口の項目を持つのに Provider が無い配備（＝503 のまま Ready になる）を残さない。
    const providers = d.env.filter((e) => e.name === PROVIDER);
    if (providers.length !== 1) errors.push(`${where} の ${PROVIDER} が ${providers.length} 回（ちょうど 1 回であるべき。未宣言は無人の登録・差し替えが 503）`);
    for (const p of providers) {
      if (literalOf(p) !== 'keycloak') errors.push(`${where} の ${PROVIDER} が keycloak ではない: ${JSON.stringify(literalOf(p))}`);
    }
    const secrets = d.env.filter((e) => e.name === CLIENT_SECRET);
    if (secrets.length !== 1) errors.push(`${where} の ${CLIENT_SECRET} が ${secrets.length} 回（ちょうど 1 回であるべき）`);
    for (const s of secrets) {
      if (/^\s*value:/m.test(s.body)) errors.push(`${CLIENT_SECRET} がリテラルの value を持つ（Secret の参照で渡す）:\n${s.body}`);
      if (!/^\s*valueFrom:\s*$/m.test(s.body) || !/^\s*secretKeyRef:\s*$/m.test(s.body)) errors.push(`${CLIENT_SECRET} が secretKeyRef ではない:\n${s.body}`);
      if (!new RegExp(`^\\s*name:\\s*${SECRET_NAME}\\s*$`, 'm').test(s.body)) errors.push(`${CLIENT_SECRET} の Secret が ${SECRET_NAME} ではない`);
      if (!new RegExp(`^\\s*key:\\s*${SECRET_KEY}\\s*$`, 'm').test(s.body)) errors.push(`${CLIENT_SECRET} のキーが ${SECRET_KEY} ではない`);
      if (/^\s*optional:/m.test(s.body)) errors.push(`${CLIENT_SECRET} が optional を持つ（Secret が無くても起動して 502 を返し続ける。非 optional にする）`);
    }
    for (const [name, want] of Object.entries(LITERALS)) {
      const hits = d.env.filter((e) => e.name === name);
      if (hits.length !== 1 || literalOf(hits[0]) !== want) {
        errors.push(`${where} の ${name} が ${JSON.stringify(want)} の 1 回ではない: ${JSON.stringify(hits.map(literalOf))}`);
      }
    }
  }
  return errors;
}

// ---------------------------------------------------------------- tests

if (!hasHelm()) {
  process.stderr.write('helm が見つからない。本試験は helm template の描画を検査するため、helm が無ければ失敗にする（fail-closed）。\n');
  process.exit(1);
}

const DEFAULT = mustRender([]);
const LOCAL = mustRender(['-f', VALUES_LOCAL]);

ok('既定（本番像）: 書き込み口は mcp-service にだけ、Provider=keycloak と非 optional の secretKeyRef つきで在る', () => {
  assert.deepStrictEqual(checkRender(DEFAULT), []);
});

ok('values-local: 同じ（ローカルの上書きが書き込み口を落とさない）', () => {
  assert.deepStrictEqual(checkRender(LOCAL), []);
});

ok('否定形の前提: mcp-service の Deployment が描画され、その中に書き込み口の項目が 5 つ在る（0 件を緑にしない）', () => {
  const mcp = documents(DEFAULT).find((d) => d.kind === 'Deployment' && d.name === OWNER);
  assert.ok(mcp, `Deployment/${OWNER} が描画されていない`);
  assert.strictEqual(envEntries(mcp.text).filter((e) => e.name.startsWith(PREFIX)).length, 5);
});

// ---- 変異: 判定器が実際に赤を出すこと（判定器が常に緑なら上の 3 本は何も言っていない） ----

/** mcp-service の env の 1 項目を描画物の上で書き換える。 */
function mutateEntry(rendered, envName, fn) {
  const docs = rendered.split(/^---\s*$/m);
  const at = docs.findIndex((t) => /^kind:\s*Deployment/m.test(t) && new RegExp(`^ {2}name:\\s*${OWNER}\\s*$`, 'm').test(t));
  assert.ok(at >= 0);
  const entry = envEntries(docs[at]).find((e) => e.name === envName);
  assert.ok(entry, `${envName} が描画に無い（変異の前提）`);
  docs[at] = docs[at].replace(entry.body, fn(entry.body));
  return docs.join('---');
}

ok('変異: Provider の欠落は赤（未宣言の配備＝503 を残さない）', () => {
  const errs = checkRender(mutateEntry(DEFAULT, PROVIDER, () => ''));
  assert.ok(errs.some((e) => e.includes(PROVIDER) && e.includes('0 回')), errs.join('\n'));
});

ok('変異: Provider の値違い（in-memory）は赤', () => {
  const errs = checkRender(mutateEntry(DEFAULT, PROVIDER, (b) => b.replace(/value:.*$/m, 'value: "in-memory"')));
  assert.ok(errs.some((e) => e.includes('keycloak ではない')), errs.join('\n'));
});

ok('変異: ClientSecret の optional 化は赤', () => {
  const errs = checkRender(mutateEntry(DEFAULT, CLIENT_SECRET, (b) => `${b}\n                  optional: true`));
  assert.ok(errs.some((e) => e.includes('optional')), errs.join('\n'));
});

ok('変異: ClientSecret のリテラル化は赤', () => {
  const errs = checkRender(mutateEntry(DEFAULT, CLIENT_SECRET, (b) => b.split('\n')[0] + '\n              value: "placeholder"'));
  assert.ok(errs.some((e) => e.includes('リテラル')), errs.join('\n'));
});

ok('変異: 別のワークロードへの混入（Provider なし）は赤', () => {
  const docs = DEFAULT.split(/^---\s*$/m);
  const other = docs.findIndex((t) => /^kind:\s*Deployment/m.test(t) && /^ {2}name:\s*authorization-service\s*$/m.test(t));
  assert.ok(other >= 0, '前提: authorization-service の Deployment が描画に在る');
  // 環境変数の項目（コンテナ名の `- name:` ではない）。間に注記の無い 1 項目を選ぶ。
  const firstEnv = envEntries(docs[other]).find((e) => e.name === 'Otlp__Endpoint');
  assert.ok(firstEnv && docs[other].includes(firstEnv.body), '前提: authorization-service の Otlp__Endpoint を描画の上で引ける');
  const indent = firstEnv.body.match(/^(\s*)/)[1];
  docs[other] = docs[other].replace(firstEnv.body,
    `${firstEnv.body}\n${indent}- name: McpClientProvisioning__Keycloak__ClientId\n${indent}  value: "mcp-client-admin"`);
  const errs = checkRender(docs.join('---'));
  assert.ok(errs.some((e) => e.includes("authorization-service") && e.includes(`${OWNER} だけに渡す`)), errs.join('\n'));
  assert.ok(errs.some((e) => e.includes('authorization-service') && e.includes(PROVIDER)), errs.join('\n'));
});

process.stdout.write(`\n${passed} tests passed.\n`);
