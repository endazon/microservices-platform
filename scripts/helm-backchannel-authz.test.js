#!/usr/bin/env node
'use strict';
/*
 * helm-backchannel-authz.test.js
 * NFR, ADR-0005, IADR-0273, Issue #1885:
 * **メッシュ外の認可サーバからのバックチャネルログアウトだけを平文で通す DENY（`<workload>-plaintext-only-backchannel`）に
 * `to.operation.ports` を明示したことを、実際に `helm template` で描いて固定する。**
 *
 * 🔴 **守るのは「Istio の警告を消しても、拒否の範囲が広がりも狭まりもしない」ことである。**
 *    ports を足すと DENY の効く先は「そのポート」に絞られる。それが拒否の範囲を変えないのは、BFF の受信ポートが
 *    そのポート 1 つだけだからである。だから本試験は字面だけでなく、
 *      (a) 前提: BFF の Deployment の containerPort の集合 ＝ { mesh.backchannelLogout.port }（描画から取る）、
 *      (b) 等価: ports を外した旧形と新形を、受信ポート × 主体の有無 × HTTP / TCP × 経路 の全組で Istio の評価規則に当て、
 *          結果が 1 件も違わないこと、
 *      (c) 警告: Istio の検証（ValidateAuthorizationPolicy）が警告を出す条件に新形が当たらず、旧形は当たること、
 *    で固定する。BFF に受信ポートが増えれば (a) が赤になり、ports の値を見直す契機になる。
 *
 * 固定するもの:
 *   1. 既定（mesh.backchannelLogout.fromOutsideMesh=false）・values-local: 方針が描かれない。
 *   2. 有効: 方針の形（DENY・notPrincipals ["*"]・ports ＝ portLevelMtls のポート・notPaths ＝ バックチャネルの URI）。
 *   3. 上の (a)〜(c)。
 *   4. knob の追随: mesh.backchannelLogout.port を変えると ports も portLevelMtls も同じ値へ動く（前提 (a) は赤になる）。
 *   5. 変異: 一時複製したチャートで ports を外すと警告の判定が赤になる／BFF に gRPC の受け口を足すと前提が赤になる。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** helm-private-notes-sync-authz.test.js と同じ理由。
 *    CI は static-checks-units（azure/setup-helm 済み）で走らせる。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/helm-backchannel-authz.test.js
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
const ON_ARGS = ['--set', 'mesh.enabled=true', '--set', 'mesh.backchannelLogout.fromOutsideMesh=true'];
const WORKLOAD = 'bff-service';
const POLICY = `${WORKLOAD}-plaintext-only-backchannel`;
const BC_PATH = '/bff/auth/backchannel-logout';

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

// ---------------------------------------------------------------- helpers

function hasHelm() {
  const probe = spawnSync(process.platform === 'win32' ? 'where' : 'which', ['helm'], { encoding: 'utf8' });
  return probe.status === 0;
}

function mustRender(args, chartDir = CHART) {
  const r = spawnSync('helm', ['template', 'msp', chartDir, ...RENDER_IMAGE_TAG_ARGS, ...args], {
    cwd: REPO_ROOT,
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
  });
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.stderr}`);
  return r.stdout;
}

const unquote = (s) => String(s).trim().replace(/^["']|["']$/g, '');

/**
 * 描画物（ブロック形式の YAML）の最小パーサ。対応: マップ・リスト・スカラー・`["a"]` の flow リスト・注記行。
 * 読むのは AuthorizationPolicy / PeerAuthentication と Deployment の ports だけ。
 */
function parseYaml(text) {
  const lines = text
    .split('\n')
    .filter((l) => l.trim() !== '' && !/^\s*#/.test(l) && l.trim() !== '---')
    .map((l) => ({ indent: l.length - l.trimStart().length, body: l.trim() }));
  let i = 0;

  function scalar(s) {
    const t = s.trim().replace(/\s+#.*$/, '');
    const flow = /^\[(.*)\]$/.exec(t);
    if (flow) return flow[1].split(',').map((p) => unquote(p)).filter((p) => p !== '');
    return unquote(t);
  }

  function keyValue(body, indent, target) {
    const at = body.search(/:(\s|$)/);
    assert.ok(at > 0, `YAML を読めない行: ${body}`);
    const key = unquote(body.slice(0, at));
    const rest = body.slice(at + 1).trim();
    if (rest !== '') {
      target[key] = scalar(rest);
      return;
    }
    if (i < lines.length && (lines[i].indent > indent || (lines[i].indent === indent && lines[i].body.startsWith('- ')))) {
      target[key] = block(lines[i].indent);
    } else {
      target[key] = null;
    }
  }

  function block(indent) {
    if (lines[i].body.startsWith('- ')) {
      const list = [];
      while (i < lines.length && lines[i].indent === indent && lines[i].body.startsWith('- ')) {
        const itemBody = lines[i].body.slice(2);
        i++;
        if (/^[^{"'[][^:]*:(\s|$)/.test(itemBody)) {
          const m = {};
          keyValue(itemBody, indent + 2, m);
          while (i < lines.length && lines[i].indent === indent + 2 && !lines[i].body.startsWith('- ')) {
            const b = lines[i].body;
            i++;
            keyValue(b, indent + 2, m);
          }
          list.push(m);
        } else {
          list.push(scalar(itemBody));
        }
      }
      return list;
    }
    const map = {};
    while (i < lines.length && lines[i].indent === indent && !lines[i].body.startsWith('- ')) {
      const b = lines[i].body;
      i++;
      keyValue(b, indent, map);
    }
    return map;
  }

  return block(lines[0].indent);
}

/** helm の出力を資源ごとに分ける（解析は読む資源だけ遅延で行う）。 */
function docs(text) {
  return text
    .split(/^---\n/m)
    .filter((c) => /^kind:/m.test(c))
    .map((c) => ({
      text: c,
      kind: (/^kind:\s*(\S+)/m.exec(c) || [])[1],
      name: (/^metadata:\n(?:[ ]{2}.*\n)*?[ ]{2}name:\s*(\S+)/m.exec(c) || [])[1],
      get obj() {
        return parseYaml(c);
      },
    }));
}

const find = (text, kind, name) => {
  const d = docs(text).find((x) => x.kind === kind && x.name === name);
  return d ? d.obj : null;
};

/** ワークロードの Deployment が宣言する containerPort の集合（全コンテナ。描画から取る）。 */
function listeningPorts(text, workload) {
  const d = docs(text).find((x) => x.kind === 'Deployment' && x.name === workload);
  assert.ok(d, `Deployment/${workload} が描かれていない`);
  return [...d.text.matchAll(/containerPort:\s*(\d+)/g)].map((m) => Number(m[1])).sort((a, b) => a - b);
}

// ---------------------------------------------------------------- Istio の評価規則（本試験が使う部分集合）

const HTTP_ONLY_OPERATION_FIELDS = new Set(['paths', 'notPaths', 'methods', 'notMethods', 'hosts', 'notHosts']);
const OPERATION_FIELDS = new Set([...HTTP_ONLY_OPERATION_FIELDS, 'ports', 'notPorts']);
const SOURCE_FIELDS = new Set(['principals', 'notPrincipals', 'namespaces', 'notNamespaces']);

function istioMatch(pattern, value) {
  if (pattern === '*') return true;
  if (pattern.endsWith('*')) return value.startsWith(pattern.slice(0, -1));
  if (pattern.startsWith('*')) return value.endsWith(pattern.slice(1));
  return pattern === value;
}

/** principal の有無だけで足りる source の照合（平文は principal を持たない）。 */
function sourceMatches(src, req) {
  for (const k of Object.keys(src)) assert.ok(SOURCE_FIELDS.has(k), `評価器が知らない source の項目 ${k}`);
  const p = req.principal;
  if (src.principals && !(p !== null && src.principals.some((x) => istioMatch(x, p)))) return false;
  if (src.notPrincipals && p !== null && src.notPrincipals.some((x) => istioMatch(x, p))) return false;
  if (src.namespaces || src.notNamespaces) assert.fail('本試験の評価器は namespaces を扱わない（評価器を広げること）');
  return true;
}

/**
 * operation の照合。**TCP（HTTP と判定されなかった流入）では、DENY の HTTP 専用属性を落として広げる**
 * （istiod の pilot/pkg/security/authz/model の挙動。fail-closed）。ports / notPorts は TCP でも残る。
 */
function operationMatches(op, req) {
  for (const k of Object.keys(op)) assert.ok(OPERATION_FIELDS.has(k), `評価器が知らない operation の項目 ${k}`);
  const http = req.proto === 'http';
  if (op.ports && !op.ports.map(String).includes(String(req.port))) return false;
  if (op.notPorts && op.notPorts.map(String).includes(String(req.port))) return false;
  if (!http) return true;
  if (op.paths && !op.paths.some((x) => istioMatch(x, req.path))) return false;
  if (op.notPaths && op.notPaths.some((x) => istioMatch(x, req.path))) return false;
  if (op.methods || op.notMethods || op.hosts || op.notHosts) assert.fail('本試験の評価器は methods / hosts を扱わない');
  return true;
}

function ruleMatches(rule, req) {
  for (const k of Object.keys(rule)) assert.ok(['from', 'to'].includes(k), `評価器が知らない rule の項目 ${k}`);
  const fromOk = !rule.from || rule.from.some((f) => sourceMatches(f.source || {}, req));
  const toOk = !rule.to || rule.to.some((t) => operationMatches(t.operation || {}, req));
  return fromOk && toOk;
}

/** DENY 1 枚の判定（本方針だけを評価する。ALLOW はこのワークロードに無い＝下で別に固定）。 */
function denies(policy, req) {
  assert.strictEqual(policy.spec.action, 'DENY');
  return (policy.spec.rules || []).some((r) => ruleMatches(r, req));
}

/**
 * Istio の検証（pkg/config/validation の ValidateAuthorizationPolicy）が出す警告
 * "configured AuthorizationPolicy will deny all traffic to TCP ports under its scope due to the use of only HTTP attributes
 * in a DENY rule" の条件を写したもの。ルールのどれか 1 つでも当たれば警告が出る。
 */
function tcpWarning(policy) {
  if (policy.spec.action !== 'DENY') return false;
  return (policy.spec.rules || []).some((r) => {
    const fromRuleExist = Array.isArray(r.from) && r.from.length > 0;
    const toRuleExist = Array.isArray(r.to) && r.to.length > 0;
    const tcpRulesInFrom = fromRuleExist && r.from.some((f) => Object.keys(f.source || {}).some((k) => SOURCE_FIELDS.has(k)));
    const tcpRulesInTo = toRuleExist && r.to.some((t) => { const o = t.operation || {}; return !!(o.ports || o.notPorts); });
    return (fromRuleExist && !toRuleExist && !tcpRulesInFrom) || (toRuleExist && !tcpRulesInTo);
  });
}

/** ports を外した旧形（#1885 より前の描画と同じ形）。 */
function withoutPorts(policy) {
  const p = JSON.parse(JSON.stringify(policy));
  for (const r of p.spec.rules || []) for (const t of r.to || []) delete (t.operation || {}).ports;
  return p;
}

/** 流入の全組: 受信ポート × 主体の有無 × HTTP / TCP × 経路。 */
function probes(ports) {
  const out = [];
  for (const port of ports) {
    for (const principal of [null, 'cluster.local/ns/microservices-platform/sa/graph-service']) {
      out.push({ port, principal, proto: 'tcp', path: null });
      for (const p of [BC_PATH, '/bff/auth/me', '/health/ready', `${BC_PATH}/x`, '/']) {
        out.push({ port, principal, proto: 'http', path: p });
      }
    }
  }
  return out;
}

const label = (q) => `${q.proto} :${q.port} ${q.principal ? 'mTLS' : '平文'} ${q.path || ''}`.trim();

/** 描画に対する判定（変異試験でも同じ関数を使う）。戻り値は問題の一覧。空なら緑。 */
function problems(text, { port = 8080, path: bcPath = BC_PATH } = {}) {
  const out = [];
  const ap = find(text, 'AuthorizationPolicy', POLICY);
  if (!ap) return [`AuthorizationPolicy/${POLICY} が描かれていない`];
  const listening = listeningPorts(text, WORKLOAD);
  if (JSON.stringify(listening) !== JSON.stringify([port])) {
    out.push(`🔴 前提: ${WORKLOAD} の受信ポート ${JSON.stringify(listening)} が ports の値 [${port}] と一致しない（拒否の範囲が変わる）`);
  }
  const ops = (ap.spec.rules || []).flatMap((r) => (r.to || []).map((t) => t.operation || {}));
  if (!ops.every((o) => JSON.stringify(o.ports) === JSON.stringify([String(port)]))) {
    out.push(`to.operation.ports が [${port}] でない: ${JSON.stringify(ops.map((o) => o.ports))}`);
  }
  if (!ops.every((o) => JSON.stringify(o.notPaths) === JSON.stringify([bcPath]))) {
    out.push(`to.operation.notPaths が [${bcPath}] でない`);
  }
  if (tcpWarning(ap)) out.push('Istio の検証が「DENY に HTTP 属性だけ」と警告する形である');
  const before = withoutPorts(ap);
  for (const q of probes(listening)) {
    if (denies(ap, q) !== denies(before, q)) out.push(`🔴 拒否の範囲が変わった: ${label(q)}（旧 ${denies(before, q)} / 新 ${denies(ap, q)}）`);
  }
  return out;
}

// ---------------------------------------------------------------- 静的（helm 不要）

ok('評価器の自己試験: TCP では DENY の notPaths が落ちて広がり、ports は残る', () => {
  const p = { spec: { action: 'DENY', rules: [{ from: [{ source: { notPrincipals: ['*'] } }], to: [{ operation: { notPaths: ['/ok'] } }] }] } };
  const plain = (proto, port, pth) => ({ proto, port, principal: null, path: pth });
  assert.ok(denies(p, plain('tcp', 9999, null)), '旧形は平文の TCP を全ポートで拒否する');
  assert.ok(!denies(p, plain('http', 8080, '/ok')));
  assert.ok(denies(p, plain('http', 8080, '/no')));
  assert.ok(!denies(p, { proto: 'http', port: 8080, principal: 'cluster.local/ns/a/sa/b', path: '/no' }), 'mTLS は当たらない');
  const q = JSON.parse(JSON.stringify(p));
  q.spec.rules[0].to[0].operation.ports = ['8080'];
  assert.ok(denies(q, plain('tcp', 8080, null)));
  assert.ok(!denies(q, plain('tcp', 9999, null)), 'ports を付けると他のポートには効かない（だから前提 (a) が要る）');
});

ok('評価器の自己試験: Istio の警告条件（to に ports / notPorts が無い DENY だけが当たる）', () => {
  const base = (op) => ({ spec: { action: 'DENY', rules: [{ from: [{ source: { notPrincipals: ['*'] } }], to: [{ operation: op }] }] } });
  assert.ok(tcpWarning(base({ notPaths: ['/x'] })));
  assert.ok(!tcpWarning(base({ ports: ['8080'], notPaths: ['/x'] })));
  assert.ok(!tcpWarning(base({ notPorts: ['1'], notPaths: ['/x'] })));
  assert.ok(!tcpWarning({ spec: { action: 'ALLOW', rules: [{ to: [{ operation: { paths: ['/x'] } }] }] } }));
  assert.ok(!tcpWarning({ spec: { action: 'DENY', rules: [{ from: [{ source: { notPrincipals: ['*'] } }] }] } }), 'from だけで TCP 属性があれば当たらない');
});

ok('評価器の自己試験: 最小パーサが flow リスト・注記・入れ子のリストを読む', () => {
  const y = parseYaml('spec:\n  action: DENY\n  rules:\n    - from:\n        - source:\n            # c\n            notPrincipals: ["*"]\n      to:\n        - operation:\n            ports:\n              - "8080"\n');
  assert.deepStrictEqual(y.spec.rules, [{ from: [{ source: { notPrincipals: ['*'] } }], to: [{ operation: { ports: ['8080'] } }] }]);
});

// ---------------------------------------------------------------- 描画（helm が要る）

if (!hasHelm()) {
  process.stderr.write(
    '✗ helm が PATH に無い。本試験は実際に `helm template` を叩くことが目的であり、飛ばさない（fail-closed）。\n' +
      '  helm を導入してから再実行すること（CI は static-checks-units の azure/setup-helm で入る）。\n',
  );
  process.exit(1);
}

const OFF = mustRender([]);
const OFF_LOCAL = mustRender(['-f', VALUES_LOCAL]);
const ON = mustRender(ON_ARGS);
// ローカルの起動器（k8s-local-up.sh の ISTIO=1）と同じ組。名前空間の既定が PERMISSIVE の段でも同じ形であること。
const ON_LOCAL = mustRender(['-f', VALUES_LOCAL, ...ON_ARGS, '--set', 'mesh.mtlsMode=PERMISSIVE', '--set', 'namespace.istioInjection=true']);

ok('既定・values-local（fromOutsideMesh=false）: 方針が描かれない', () => {
  for (const [l, t] of [['既定', OFF], ['values-local', OFF_LOCAL]]) {
    assert.strictEqual(find(t, 'AuthorizationPolicy', POLICY), null, `${l} に方針が描かれた`);
  }
});

ok('有効: DENY・notPrincipals ["*"]・ports ＝ portLevelMtls のポート・notPaths ＝ バックチャネルの URI', () => {
  for (const t of [ON, ON_LOCAL]) {
    const ap = find(t, 'AuthorizationPolicy', POLICY);
    assert.ok(ap);
    assert.strictEqual(ap.spec.action, 'DENY');
    assert.deepStrictEqual(ap.spec.selector.matchLabels, { app: WORKLOAD });
    assert.deepStrictEqual(ap.spec.rules, [
      { from: [{ source: { notPrincipals: ['*'] } }], to: [{ operation: { ports: ['8080'], notPaths: [BC_PATH] } }] },
    ]);
    const pa = find(t, 'PeerAuthentication', `${WORKLOAD}-backchannel-logout`);
    assert.deepStrictEqual(Object.keys(pa.spec.portLevelMtls), ap.spec.rules[0].to[0].operation.ports, '開けた口と DENY の範囲が揃っていない');
  }
});

ok('有効: このワークロードを選ぶ ALLOW は無い（DENY だけで判定が決まる＝等価の評価が成り立つ前提）', () => {
  const all = docs(ON).filter((d) => d.kind === 'AuthorizationPolicy').map((d) => d.obj);
  const forBff = all.filter((o) => ((((o.spec || {}).selector || {}).matchLabels) || {}).app === WORKLOAD);
  assert.deepStrictEqual(forBff.map((o) => o.spec.action), ['DENY']);
});

ok('🔴 有効: 前提（受信ポート＝{8080}）・警告が出ない・受信ポートの全組で旧形と拒否の範囲が 1 件も違わない', () => {
  assert.deepStrictEqual(problems(ON), []);
  assert.deepStrictEqual(problems(ON_LOCAL), []);
  assert.deepStrictEqual(listeningPorts(ON, WORKLOAD), [8080]);
  assert.ok(tcpWarning(withoutPorts(find(ON, 'AuthorizationPolicy', POLICY))), '陰性対照: 旧形は警告の条件に当たる');
});

ok('knob の追随: mesh.backchannelLogout.port を変えると ports と portLevelMtls が同じ値へ動き、前提の不一致は赤になる', () => {
  const t = mustRender([...ON_ARGS, '--set', 'mesh.backchannelLogout.port=9090']);
  const ap = find(t, 'AuthorizationPolicy', POLICY);
  assert.deepStrictEqual(ap.spec.rules[0].to[0].operation.ports, ['9090']);
  assert.deepStrictEqual(Object.keys(find(t, 'PeerAuthentication', `${WORKLOAD}-backchannel-logout`).spec.portLevelMtls), ['9090']);
  const p = problems(t, { port: 9090 });
  assert.ok(p.some((x) => x.includes('前提')), JSON.stringify(p));
});

// ---------------------------------------------------------------- 変異

ok('変異: BFF に gRPC の受け口（grpcPort）を足すと前提が赤になる（ports の値を見直す契機）', () => {
  const t = mustRender([...ON_ARGS, '--set', 'services.bff.grpcPort=8081']);
  const p = problems(t);
  assert.ok(p.some((x) => x.includes('前提')), JSON.stringify(p));
});

ok('変異: 一時複製したチャートで ports を外すと、警告と ports の判定が赤になる', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'helm-bc-authz-'));
  try {
    const dst = path.join(tmp, 'chart');
    fs.cpSync(path.join(REPO_ROOT, CHART), dst, { recursive: true });
    const f = path.join(dst, 'templates', 'istio-mtls.yaml');
    const src = fs.readFileSync(f, 'utf8');
    const mutated = src.replace(/\n[ ]+ports:\n[ ]+- \{\{ \.Values\.mesh\.backchannelLogout\.port \| quote \}\}/, '');
    assert.notStrictEqual(mutated, src, '変異を当てる字面が見つからない（試験を追随させること）');
    fs.writeFileSync(f, mutated);
    const p = problems(mustRender(ON_ARGS, dst));
    assert.ok(p.some((x) => x.includes('警告')), JSON.stringify(p));
    assert.ok(p.some((x) => x.includes('ports が')), JSON.stringify(p));
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

process.stdout.write(`\n${passed} passed\n`);
