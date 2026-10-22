#!/usr/bin/env node
'use strict';
/*
 * helm-bff-externalsecret-admission.test.js
 * SC-22, NFR-18, IADR-0456 決定 4, IADR-0588（Refs GHSA-j4qv-2w99-xg5p）:
 * **BFF の SA が ExternalSecret に対して持つ更新を force-sync の注釈だけに限る ValidatingAdmissionPolicy を、
 * 実際に `helm template` で描いて固定する。**
 *
 * 固定するもの:
 *   1. 描画の条件: BFF の Role と同じ条件（services.bff.enabled かつ externalSecretSync.enabled）で描き、
 *      既定（本番像・false）では描かない。values-local では描く。利用者名は values の名前空間と SA 名から作る。
 *   2. 形: failurePolicy Fail・binding の validationActions は [Deny] だけ・binding は policy を名指す・
 *      external-secrets.io の externalsecrets（とサブリソース）の CREATE / UPDATE / DELETE に当たる。
 *   3. 判定（CEL を本試験の小さな評価器で評価する）:
 *      陽性 = BFF の SA の UPDATE で、注釈 force-sync を数字にする変更だけ（サーバが変える resourceVersion 等は無関係）。
 *      陰性 = spec・status・labels・finalizers・ownerReferences・他の注釈の追加 / 変更 / 削除・force-sync の削除や数字以外・
 *             CREATE・DELETE・サブリソース。
 *      主体 = BFF 以外の利用者には当たらない（同じ変更でも許す）。
 *      評価できない式は拒否（failurePolicy Fail）として扱う。
 *   4. 供給側と一致する: 注釈のキー ＝ BFF の ExternalSecretSyncRequester.ForceSyncAnnotation。BFF の要求は
 *      merge-patch で、本体は metadata.annotations の 1 キーだけ。RBAC（チャート・deploy/local）の動詞は get / patch だけ。
 *   5. 変異: 描画物から検査を 1 つ外す・キーを変える・主体の条件を広げる・鍵の有無の備えを外すと、本試験の判定が赤になる。
 *      （failurePolicy: Fail は形の試験が字面で固定する。正しい式は誤りを出さないので、行列では Ignore と区別できない。）
 *
 * 評価器は本ポリシーが使う CEL の部分集合だけを持つ（未知の構文・関数は例外で落とす＝黙って緑にしない）。
 * 実 API サーバ（v1.35）での同じ行列の実測は作業仕様書と IADR-0588 にある。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** CI は static-checks-units で走らせる。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/helm-bff-externalsecret-admission.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const TEMPLATE = 'templates/bff-externalsecret-sync-admission.yaml';
const RBAC_TEMPLATE = 'templates/bff-externalsecret-sync-rbac.yaml';
const VALUES_LOCAL = 'deploy/local/values-local.yaml';
const LOCAL_RBAC = 'deploy/local/vault/eso/rbac-bff-externalsecret-sync.yaml';
const SYNC_CS = 'src/platform/backend/Bff/Platform.Bff/Foundation/Secrets/ExternalSecretSync.cs';
const POLICY_NAME = 'bff-externalsecret-force-sync-only';
const BFF_USER = 'system:serviceaccount:microservices-platform:bff';

const read = (rel) => fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

// ---------------------------------------------------------------------------------------------
// CEL の部分集合の評価器（本ポリシーが使う構文だけ）。
// 評価時の誤り（鍵が無い・型が違う）は CelError の値として返し、CEL と同じく && / || / all で吸収する。
// 構文・関数が未知なら Error を投げる（試験の誤りとして落とす）。
// ---------------------------------------------------------------------------------------------
class CelError {
  constructor(msg) {
    this.msg = msg;
  }
}
const isErr = (v) => v instanceof CelError;
const isMap = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);

function tokenize(src) {
  const toks = [];
  let i = 0;
  while (i < src.length) {
    const c = src[i];
    if (/\s/.test(c)) { i++; continue; }
    if (c === "'" || c === '"') {
      let j = i + 1;
      let s = '';
      while (j < src.length && src[j] !== c) {
        if (src[j] === '\\') { s += src[j + 1]; j += 2; continue; }
        s += src[j++];
      }
      if (j >= src.length) throw new Error(`閉じない文字列: ${src}`);
      toks.push({ t: 'str', v: s });
      i = j + 1;
      continue;
    }
    if (/[0-9]/.test(c)) {
      const m = src.slice(i).match(/^[0-9]+/);
      toks.push({ t: 'int', v: Number(m[0]) });
      i += m[0].length;
      continue;
    }
    if (/[A-Za-z_]/.test(c)) {
      const m = src.slice(i).match(/^[A-Za-z_][A-Za-z0-9_]*/);
      toks.push({ t: 'id', v: m[0] });
      i += m[0].length;
      continue;
    }
    const two = src.slice(i, i + 2);
    if (['||', '&&', '==', '!='].includes(two)) { toks.push({ t: 'op', v: two }); i += 2; continue; }
    if ('!?:.,()[]{}'.includes(c)) { toks.push({ t: 'op', v: c }); i++; continue; }
    throw new Error(`未対応の字句 '${c}': ${src}`);
  }
  return toks;
}

function parse(src) {
  const toks = tokenize(src);
  let p = 0;
  const peek = (v) => toks[p] && toks[p].t === 'op' && toks[p].v === v;
  const peekId = (v) => toks[p] && toks[p].t === 'id' && toks[p].v === v;
  const eat = (v) => {
    if (!peek(v)) throw new Error(`'${v}' が要る（位置 ${p}）: ${src}`);
    p++;
  };
  function expr() {
    const c = or();
    if (peek('?')) { p++; const a = expr(); eat(':'); const b = expr(); return { k: 'cond', c, a, b }; }
    return c;
  }
  function or() {
    let l = and();
    while (peek('||')) { p++; l = { k: 'or', l, r: and() }; }
    return l;
  }
  function and() {
    let l = rel();
    while (peek('&&')) { p++; l = { k: 'and', l, r: rel() }; }
    return l;
  }
  function rel() {
    let l = unary();
    for (;;) {
      if (peek('==') || peek('!=')) { const op = toks[p++].v; l = { k: op, l, r: unary() }; continue; }
      if (peekId('in')) { p++; l = { k: 'in', l, r: unary() }; continue; }
      return l;
    }
  }
  function unary() {
    if (peek('!')) { p++; return { k: 'not', e: unary() }; }
    return member();
  }
  function args() {
    eat('(');
    const out = [];
    if (!peek(')')) { out.push(expr()); while (peek(',')) { p++; out.push(expr()); } }
    eat(')');
    return out;
  }
  function member() {
    let e = primary();
    for (;;) {
      if (peek('.')) {
        p++;
        const tok = toks[p++];
        if (!tok || tok.t !== 'id') throw new Error(`. の後に名前が要る: ${src}`);
        if (peek('(')) { e = { k: 'call', target: e, fn: tok.v, args: args() }; continue; }
        e = { k: 'sel', e, f: tok.v };
        continue;
      }
      if (peek('[')) { p++; const i = expr(); eat(']'); e = { k: 'idx', e, i }; continue; }
      return e;
    }
  }
  function primary() {
    const tok = toks[p++];
    if (!tok) throw new Error(`式が途中で終わる: ${src}`);
    if (tok.t === 'str' || tok.t === 'int') return { k: 'lit', v: tok.v };
    if (tok.t === 'id') {
      if (tok.v === 'true' || tok.v === 'false') return { k: 'lit', v: tok.v === 'true' };
      if (tok.v === 'null') return { k: 'lit', v: null };
      if (peek('(')) return { k: 'call', target: null, fn: tok.v, args: args() };
      return { k: 'id', v: tok.v };
    }
    if (tok.v === '(') { const e = expr(); eat(')'); return e; }
    if (tok.v === '[') { eat(']'); return { k: 'lit', v: [] }; }
    if (tok.v === '{') { eat('}'); return { k: 'lit', v: {} }; }
    throw new Error(`未対応の構文 '${tok.v}': ${src}`);
  }
  const e = expr();
  if (p !== toks.length) throw new Error(`余りの字句（位置 ${p}）: ${src}`);
  return e;
}

function deepEq(a, b) {
  return JSON.stringify(canon(a)) === JSON.stringify(canon(b));
}
function canon(v) {
  if (Array.isArray(v)) return v.map(canon);
  if (isMap(v)) return Object.fromEntries(Object.keys(v).sort().map((k) => [k, canon(v[k])]));
  return v;
}

function evaluate(node, env) {
  const ev = (n, e = env) => evaluate(n, e);
  switch (node.k) {
    case 'lit':
      return node.v;
    case 'id':
      if (!(node.v in env.vars)) throw new Error(`未知の名前: ${node.v}`);
      return env.vars[node.v];
    case 'sel': {
      if (node.e.k === 'id' && node.e.v === 'variables') return env.variable(node.f);
      const o = ev(node.e);
      if (isErr(o)) return o;
      if (!isMap(o)) return new CelError(`map でないものの .${node.f}`);
      return Object.prototype.hasOwnProperty.call(o, node.f) ? o[node.f] : new CelError(`no such key: ${node.f}`);
    }
    case 'idx': {
      const o = ev(node.e);
      const i = ev(node.i);
      if (isErr(o)) return o;
      if (isErr(i)) return i;
      if (isMap(o)) return Object.prototype.hasOwnProperty.call(o, i) ? o[i] : new CelError(`no such key: ${i}`);
      if (Array.isArray(o) && Number.isInteger(i)) return i < o.length ? o[i] : new CelError('index out of range');
      return new CelError('添字の型が違う');
    }
    case 'not': {
      const v = ev(node.e);
      if (isErr(v)) return v;
      return typeof v === 'boolean' ? !v : new CelError('! の型が違う');
    }
    case 'and':
    case 'or': {
      const l = ev(node.l);
      const r = ev(node.r);
      const short = node.k === 'or';
      for (const v of [l, r]) if (v === short) return short;
      for (const v of [l, r]) if (isErr(v)) return v;
      for (const v of [l, r]) if (typeof v !== 'boolean') return new CelError(`${node.k} の型が違う`);
      return !short;
    }
    case '==':
    case '!=': {
      const l = ev(node.l);
      const r = ev(node.r);
      if (isErr(l)) return l;
      if (isErr(r)) return r;
      return node.k === '==' ? deepEq(l, r) : !deepEq(l, r);
    }
    case 'in': {
      const l = ev(node.l);
      const r = ev(node.r);
      if (isErr(l)) return l;
      if (isErr(r)) return r;
      if (isMap(r)) return Object.prototype.hasOwnProperty.call(r, l);
      if (Array.isArray(r)) return r.some((x) => deepEq(x, l));
      return new CelError('in の右辺の型が違う');
    }
    case 'cond': {
      const c = ev(node.c);
      if (isErr(c)) return c;
      if (typeof c !== 'boolean') return new CelError('?: の条件の型が違う');
      return c ? ev(node.a) : ev(node.b);
    }
    case 'call': {
      if (node.target === null && node.fn === 'has') {
        const a = node.args[0];
        if (node.args.length !== 1 || a.k !== 'sel') throw new Error('has() はフィールド選択だけを取る');
        const o = ev(a.e);
        if (isErr(o)) return o;
        if (!isMap(o)) return new CelError('has() の対象が map でない');
        return Object.prototype.hasOwnProperty.call(o, a.f);
      }
      if (node.target !== null && node.fn === 'all') {
        const [v, body] = node.args;
        if (node.args.length !== 2 || v.k !== 'id') throw new Error('all(x, e) の形でない');
        const t = ev(node.target);
        if (isErr(t)) return t;
        const items = isMap(t) ? Object.keys(t) : Array.isArray(t) ? t : null;
        if (items === null) return new CelError('all() の対象の型が違う');
        let err = null;
        for (const it of items) {
          const r = evaluate(body, { ...env, vars: { ...env.vars, [v.v]: it } });
          if (r === false) return false;
          if (isErr(r)) err = r;
          else if (r !== true) err = new CelError('all() の本体が bool でない');
        }
        return err || true;
      }
      if (node.target !== null && node.fn === 'matches') {
        const s = ev(node.target);
        const re = ev(node.args[0]);
        if (isErr(s)) return s;
        if (typeof s !== 'string' || typeof re !== 'string') return new CelError('matches() の型が違う');
        return new RegExp(re).test(s);
      }
      throw new Error(`未対応の関数: ${node.fn}`);
    }
    default:
      throw new Error(`未対応の節: ${node.k}`);
  }
}

// ---------------------------------------------------------------------------------------------
// 描画物の読み取り（本テンプレートの形だけ。数が合わなければ落とす）。
// ---------------------------------------------------------------------------------------------
function hasHelm() {
  const probe = spawnSync(process.platform === 'win32' ? 'where' : 'which', ['helm'], { encoding: 'utf8' });
  return probe.status === 0;
}
function render(args) {
  const r = spawnSync('helm', ['template', 'msp', CHART, ...args], { cwd: REPO_ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.stderr}`);
  return r.stdout;
}
function docs(rendered) {
  return rendered.split(/^---\s*$/m).filter((t) => /^kind:/m.test(t));
}
function docOfKind(rendered, kind) {
  return docs(rendered).filter((t) => new RegExp(`^kind:\\s*${kind}\\s*$`, 'm').test(t));
}

/** `- name:` / `- expression:` で始まる項目の列を、{ name, expression, message } で返す（`key:` の直下だけ）。 */
function listItems(doc, key) {
  const lines = doc.split('\n');
  const at = lines.findIndex((l) => new RegExp(`^ {2}${key}:\\s*$`).test(l));
  if (at < 0) return null;
  const items = [];
  let cur = null;
  let field = null;
  for (let i = at + 1; i < lines.length; i++) {
    const l = lines[i];
    if (l.trim() === '') continue;
    const ind = l.match(/^(\s*)/)[1].length;
    if (ind <= 2) break;
    const m = l.match(/^ {4}(- )?( *)([A-Za-z]+):\s*(.*)$/);
    if (m && (m[1] || ind === 6)) {
      if (m[1]) { cur = {}; items.push(cur); }
      field = m[3];
      const v = m[4];
      if (v === '>-') cur[field] = '';
      else if (v.startsWith('"')) cur[field] = JSON.parse(v);
      else cur[field] = v;
      continue;
    }
    if (cur && field && ind >= 8) cur[field] = (cur[field] ? `${cur[field]} ` : '') + l.trim();
  }
  return items;
}

function policyFrom(doc) {
  const conds = listItems(doc, 'matchConditions');
  const vars = listItems(doc, 'variables');
  const vals = listItems(doc, 'validations');
  for (const [k, v] of [['matchConditions', conds], ['variables', vars], ['validations', vals]]) {
    assert.ok(v && v.length > 0, `${k} が読めない`);
    for (const it of v) assert.ok(it.expression, `${k} の項目に expression が無い: ${JSON.stringify(it)}`);
  }
  return {
    failurePolicy: (doc.match(/^ {2}failurePolicy:\s*(\S+)/m) || [])[1],
    conds: conds.map((c) => ({ ...c, ast: parse(c.expression) })),
    vars: vars.map((c) => ({ ...c, ast: parse(c.expression) })),
    vals: vals.map((c) => ({ ...c, ast: parse(c.expression) })),
  };
}

/**
 * API サーバの判定を模す: matchConditions が 1 つでも false なら当たらない（許可）。
 * 当たれば validations を順に評価し、false か評価できない（failurePolicy Fail）なら拒否。
 */
function admit(policy, { operation, username, subResource, object, oldObject }) {
  const request = { operation, userInfo: { username } };
  if (subResource) request.subResource = subResource; // 主資源への要求では鍵そのものが無い（実測）
  const cache = new Map();
  const env = { vars: { request, object, oldObject } };
  env.variable = (name) => {
    if (!cache.has(name)) {
      const def = policy.vars.find((v) => v.name === name);
      if (!def) throw new Error(`未定義の variables.${name}`);
      cache.set(name, evaluate(def.ast, env));
    }
    return cache.get(name);
  };
  const failClosed = policy.failurePolicy === 'Fail';
  for (const c of policy.conds) {
    const r = evaluate(c.ast, env);
    if (isErr(r)) return failClosed ? { allowed: false, reason: `matchCondition の誤り: ${r.msg}` } : { allowed: true, applied: false };
    if (r === false) return { allowed: true, applied: false };
  }
  for (const v of policy.vals) {
    const r = evaluate(v.ast, env);
    if (r === true) continue;
    if (isErr(r) && !failClosed) continue;
    return { allowed: false, applied: true, reason: isErr(r) ? `評価の誤り: ${r.msg}` : v.message };
  }
  return { allowed: true, applied: true };
}

// ExternalSecret の現状（サーバが付ける鍵を含む）。
function baseObject() {
  return {
    apiVersion: 'external-secrets.io/v1',
    kind: 'ExternalSecret',
    metadata: {
      name: 'llm-provider-credentials',
      namespace: 'microservices-platform',
      uid: 'u-1',
      resourceVersion: '100',
      generation: 1,
      labels: { app: 'x' },
      annotations: { keep: '1', 'force-sync': '1761130000' },
      managedFields: [{ manager: 'kubectl', operation: 'Apply' }],
    },
    spec: {
      refreshInterval: '1h',
      secretStoreRef: { kind: 'ClusterSecretStore', name: 'vault' },
      target: { name: 'llm-provider-credentials' },
      data: [{ secretKey: 'k', remoteRef: { key: 'a', property: 'b' } }],
    },
    status: { refreshTime: '2026-10-22T00:00:00Z', conditions: [{ type: 'Ready', status: 'True' }] },
  };
}
const clone = (o) => JSON.parse(JSON.stringify(o));
/** 古い物から新しい物を作る（サーバが変える resourceVersion と managedFields は常に変える）。 */
function updated(mutate, old = baseObject()) {
  const o = clone(old);
  o.metadata.resourceVersion = '101';
  o.metadata.managedFields = [...o.metadata.managedFields, { manager: 'bff', operation: 'Update' }];
  mutate(o);
  return o;
}

/** [名前, 期待（true＝許可）, 要求] の行列。BFF の要求は既定で UPDATE・主資源。 */
function cases() {
  const bff = (o) => ({ operation: 'UPDATE', username: BFF_USER, ...o });
  const old = baseObject();
  const noAnn = baseObject();
  delete noAnn.metadata.annotations;
  return [
    ['陽性: force-sync の値だけを変える', true, bff({ oldObject: old, object: updated((o) => { o.metadata.annotations['force-sync'] = '1761139999'; }) })],
    ['陽性: 注釈の無い物に force-sync を足す', true, bff({ oldObject: noAnn, object: updated((o) => { o.metadata.annotations = { 'force-sync': '1' }; }, noAnn) })],
    ['陽性: 値が同じ（変化なし）でも通る', true, bff({ oldObject: old, object: updated(() => {}) })],
    ['陰性: spec を変える', false, bff({ oldObject: old, object: updated((o) => { o.spec.refreshInterval = '5m'; }) })],
    ['陰性: spec と force-sync を同時に変える', false, bff({ oldObject: old, object: updated((o) => { o.spec.target.name = 'x'; o.metadata.annotations['force-sync'] = '2'; }) })],
    ['陰性: spec の項目を足す', false, bff({ oldObject: old, object: updated((o) => { o.spec.dataFrom = [{ extract: { key: 'z' } }]; }) })],
    ['陰性: status を変える', false, bff({ oldObject: old, object: updated((o) => { o.status.refreshTime = 'x'; }) })],
    ['陰性: labels を変える', false, bff({ oldObject: old, object: updated((o) => { o.metadata.labels.app = 'y'; }) })],
    ['陰性: labels を足す', false, bff({ oldObject: old, object: updated((o) => { o.metadata.labels.z = 'y'; }) })],
    ['陰性: labels を消す', false, bff({ oldObject: old, object: updated((o) => { delete o.metadata.labels; }) })],
    ['陰性: 他の注釈を足す', false, bff({ oldObject: old, object: updated((o) => { o.metadata.annotations.evil = '1'; }) })],
    ['陰性: 他の注釈を変える', false, bff({ oldObject: old, object: updated((o) => { o.metadata.annotations.keep = '2'; }) })],
    ['陰性: 他の注釈を消す', false, bff({ oldObject: old, object: updated((o) => { delete o.metadata.annotations.keep; }) })],
    ['陰性: force-sync を消す', false, bff({ oldObject: old, object: updated((o) => { delete o.metadata.annotations['force-sync']; }) })],
    ['陰性: force-sync を数字以外にする', false, bff({ oldObject: old, object: updated((o) => { o.metadata.annotations['force-sync'] = 'abc'; }) })],
    ['陰性: force-sync を長すぎる数字にする', false, bff({ oldObject: old, object: updated((o) => { o.metadata.annotations['force-sync'] = '1'.repeat(20); }) })],
    ['陰性: finalizers を足す', false, bff({ oldObject: old, object: updated((o) => { o.metadata.finalizers = ['x/y']; }) })],
    ['陰性: ownerReferences を足す', false, bff({ oldObject: old, object: updated((o) => { o.metadata.ownerReferences = [{ kind: 'ConfigMap', name: 'a' }]; }) })],
    ['陰性: generateName を足す', false, bff({ oldObject: old, object: updated((o) => { o.metadata.generateName = 'g-'; }) })],
    ['陰性: CREATE', false, bff({ operation: 'CREATE', oldObject: null, object: baseObject() })],
    ['陰性: DELETE', false, bff({ operation: 'DELETE', oldObject: old, object: null })],
    ['陰性: サブリソース（status）の UPDATE', false, bff({ subResource: 'status', oldObject: old, object: updated(() => {}) })],
    ['主体: BFF 以外は spec を変えても当たらない', true, { operation: 'UPDATE', username: 'system:serviceaccount:external-secrets:external-secrets', oldObject: old, object: updated((o) => { o.spec.refreshInterval = '5m'; }) }],
    ['主体: 名前空間の違う同名の SA には当たらない', true, { operation: 'UPDATE', username: 'system:serviceaccount:other:bff', oldObject: old, object: updated((o) => { o.metadata.labels.z = '1'; }) }],
    ['主体: 人の利用者の DELETE には当たらない', true, { operation: 'DELETE', username: 'admin', oldObject: old, object: null }],
  ];
}

function runMatrix(policy) {
  return cases().map(([name, expect, req]) => {
    const r = admit(policy, req);
    return { name, expect, got: r.allowed, reason: r.reason };
  });
}

function main() {
  if (!hasHelm()) {
    console.error('helm-bff-externalsecret-admission: helm が無い（fail-closed）。https://helm.sh/docs/intro/install/');
    process.exit(1);
  }
  const prod = render([]);
  const local = render(['-f', VALUES_LOCAL]);
  const custom = render(['--set', 'services.bff.externalSecretSync.enabled=true', '--set', 'services.bff.serviceAccount.name=bff-x', '--set', 'namespace.name=msp-x']);
  const [policyDoc] = docOfKind(local, 'ValidatingAdmissionPolicy');
  const [bindingDoc] = docOfKind(local, 'ValidatingAdmissionPolicyBinding');

  ok('描画: 既定（本番像）では描かず、values-local では policy と binding を 1 つずつ描く（Role と同じ条件）', () => {
    assert.strictEqual(docOfKind(prod, 'ValidatingAdmissionPolicy').length, 0);
    assert.strictEqual(docOfKind(prod, 'ValidatingAdmissionPolicyBinding').length, 0);
    assert.strictEqual(docOfKind(prod, 'Role').length, 0, '前提: 既定では Role も描かない');
    assert.strictEqual(docOfKind(local, 'ValidatingAdmissionPolicy').length, 1);
    assert.strictEqual(docOfKind(local, 'ValidatingAdmissionPolicyBinding').length, 1);
    assert.ok(docOfKind(local, 'Role').some((d) => /name: bff-externalsecret-sync/.test(d)), '前提: values-local では Role も描く');
    const cond = (rel) => (read(path.join(CHART, rel)).match(/\{\{- if (and \$bff\.enabled \$sync\.enabled) \}\}/) || [])[1];
    assert.ok(cond(TEMPLATE), 'policy の描画条件が読めない');
    assert.strictEqual(cond(TEMPLATE), cond(RBAC_TEMPLATE), 'policy と Role の描画条件が違う');
  });

  ok('描画: 利用者名は values の名前空間と SA 名から作る', () => {
    const p = policyFrom(docOfKind(custom, 'ValidatingAdmissionPolicy')[0]);
    assert.match(p.conds[0].expression, /'system:serviceaccount:msp-x:bff-x'/);
    assert.match(policyFrom(policyDoc).conds[0].expression, new RegExp(`'${BFF_USER}'`));
  });

  ok('形: failurePolicy Fail・binding は [Deny] だけで policy を名指す・externalsecrets の CREATE / UPDATE / DELETE に当たる', () => {
    assert.match(policyDoc, /^apiVersion: admissionregistration\.k8s\.io\/v1$/m);
    assert.match(policyDoc, new RegExp(`^ {2}name: ${POLICY_NAME}$`, 'm'));
    assert.match(policyDoc, /^ {2}failurePolicy: Fail$/m);
    assert.match(policyDoc, /apiGroups: \["external-secrets\.io"\]/);
    assert.match(policyDoc, /operations: \["CREATE", "UPDATE", "DELETE"\]/);
    assert.match(policyDoc, /resources: \["externalsecrets", "externalsecrets\/\*"\]/);
    assert.doesNotMatch(policyDoc, /namespaceSelector|objectSelector|paramKind/, '当たる範囲を狭める指定を置かない');
    assert.match(bindingDoc, /^apiVersion: admissionregistration\.k8s\.io\/v1$/m);
    assert.match(bindingDoc, new RegExp(`^ {2}policyName: ${POLICY_NAME}$`, 'm'));
    assert.match(bindingDoc, /^ {2}validationActions: \[Deny\]$/m);
    assert.doesNotMatch(bindingDoc, /matchResources|paramRef/, 'binding で当たる範囲を狭めない');
  });

  const policy = policyFrom(policyDoc);

  ok('判定: 陽性・陰性・主体の行列が期待どおり（CEL を評価）', () => {
    const wrong = runMatrix(policy).filter((r) => r.got !== r.expect);
    assert.deepStrictEqual(wrong, [], wrong.map((r) => `${r.name}: 期待 ${r.expect} 実際 ${r.got}（${r.reason}）`).join('\n'));
  });

  ok('判定: 陰性はどれも検査の文言で拒否される（評価の誤りで落ちているのではない）', () => {
    for (const r of runMatrix(policy).filter((x) => !x.expect)) {
      assert.ok(r.reason && !/誤り/.test(r.reason), `${r.name}: ${r.reason}`);
    }
  });

  ok('供給側: 注釈のキーは BFF の ForceSyncAnnotation と一致し、BFF の要求は merge-patch で metadata.annotations の 1 キーだけ', () => {
    const cs = read(SYNC_CS);
    const key = (cs.match(/public const string ForceSyncAnnotation = "([^"]+)";/) || [])[1];
    assert.ok(key, 'ForceSyncAnnotation が読めない');
    assert.match(cs, /public const string MergePatchContentType = "application\/merge-patch\+json";/);
    const body = (cs.match(/var body = new Dictionary<string, object>\s*\{([\s\S]*?)\n\s{12}\};/) || [])[1];
    assert.ok(body, '要求の本体が読めない');
    assert.deepStrictEqual([...body.matchAll(/\["?([A-Za-z]+)"?\] =/g)].map((m) => m[1]), ['metadata', 'annotations', 'ForceSyncAnnotation']);
    assert.match(body, /ToUnixTimeSeconds\(\)\.ToString\(CultureInfo\.InvariantCulture\)/, '値が unix 秒の数字でない');
    const literals = new Set([...policyDoc.matchAll(/'([a-z][a-z0-9./-]*-[a-z0-9-]+)'/g)].map((m) => m[1]).filter((s) => !s.includes(':')));
    assert.deepStrictEqual([...literals], [key], 'policy の注釈のキーが BFF と違う');
  });

  ok('供給側: RBAC（チャート・deploy/local）の動詞は get / patch だけ（作成・削除は policy より先に RBAC で持たせない）', () => {
    const verbs = (text) => [...text.matchAll(/verbs: \[([^\]]*)\]/g)].map((m) => m[1].replace(/\s/g, ''));
    const chart = docOfKind(local, 'Role').filter((d) => /name: bff-externalsecret-sync/.test(d)).join('\n');
    for (const [label, text] of [['chart', chart], ['local', read(LOCAL_RBAC)]]) {
      const v = verbs(text);
      assert.ok(v.length > 0, `${label}: verbs が読めない`);
      for (const x of v) assert.strictEqual(x, '"get","patch"', `${label}: ${x}`);
    }
  });

  ok('評価器: 未知の構文・関数は例外で落ちる（黙って緑にしない）', () => {
    assert.throws(() => parse("a.exists(x, x == 'y')") && evaluate(parse("a.exists(x, x == 'y')"), { vars: { a: [] } }), /未対応の関数/);
    assert.throws(() => parse('a + b'), /未対応の字句/);
    assert.ok(isErr(evaluate(parse('request.subResource'), { vars: { request: {} } })), '無い鍵の選択が誤りにならない');
  });

  ok('変異: 検査を外す・キーを変える・主体を広げる・鍵の有無の備えを外すと判定が赤になる', () => {
    const mutations = [
      ['spec の検査を外す', (p) => ({ ...p, vals: p.vals.filter((v) => !/object\.spec/.test(v.expression)) })],
      ['labels の検査を外す', (p) => ({ ...p, vals: p.vals.filter((v) => !/labels/.test(v.expression)) })],
      ['他の注釈の検査を外す', (p) => ({ ...p, vals: p.vals.filter((v) => !/\.all\(/.test(v.expression)) })],
      ['UPDATE の検査を外す', (p) => ({ ...p, vals: p.vals.filter((v) => v.expression !== 'variables.isUpdate') })],
      ['値の形の検査を外す', (p) => ({ ...p, vals: p.vals.filter((v) => !/matches/.test(v.expression)) })],
      ['キーを別名にする', (p) => {
        const re = (s) => s.replace(/'force-sync'/g, "'force-sync-x'");
        return { ...p, vals: p.vals.map((v) => ({ ...v, expression: re(v.expression), ast: parse(re(v.expression)) })) };
      }],
      // 主資源への要求では request に subResource の鍵が無い。has() の備えを外すと評価の誤り（＝Fail で拒否）になり、陽性が落ちる。
      ['subResource の鍵の有無の備えを外す', (p) => {
        const re = (s) => s.replace("(!has(request.subResource) || request.subResource == '')", "request.subResource == ''");
        return { ...p, vars: p.vars.map((v) => ({ ...v, expression: re(v.expression), ast: parse(re(v.expression)) })) };
      }],
      ['主体の条件を全員にする', (p) => ({ ...p, conds: [{ name: 'all', expression: 'true', ast: parse('true') }] })],
    ];
    for (const [name, mutate] of mutations) {
      const wrong = runMatrix(mutate(policy)).filter((r) => r.got !== r.expect);
      assert.ok(wrong.length > 0, `変異「${name}」でも行列が緑のまま`);
    }
  });

  console.log(`helm-bff-externalsecret-admission: ${passed} tests passed`);
}

main();
