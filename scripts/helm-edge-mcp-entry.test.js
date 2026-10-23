#!/usr/bin/env node
'use strict';
/*
 * helm-edge-mcp-entry.test.js
 * FR-16, UC-08, NFR-09, ADR-0024 / ADR-0021, IADR-0594 (#2045):
 * **外部 AI エージェントの入口（エッジ host の /mcp）を、実際に `helm template` で描いて固定する。**
 *
 * 🔴 **守るのは「/mcp の面だけを外へ出し、同じポートの管理 REST・OpenAPI・自己申告・ヘルスは出さない」ことと、
 *    「名前空間の中の呼び出し元（BFF の管理 API の中継）とプローブを 1 本も切らない」ことである。**
 *    MCP サーバーの 1 つのポートには /mcp と /mcp-clients* が同居し、Istio の `prefix` は文字列の前方一致なので
 *    `prefix: /mcp` は /mcp-clients まで飲む。だから本試験は字面だけでなく、
 *      (a) 経路（VirtualService）の照合を Istio の規則（exact／文字列の prefix）で要求の一覧へ当て、
 *      (b) L7 の門（AuthorizationPolicy）を Istio の評価規則で呼び出し元の一覧へ当て、
 *    結果（届く／届かない・通す／落とす）で固定する。
 *
 * 固定するもの:
 *   1. 既定（無効）・values-local: 経路・門・NetworkPolicy の穴のどれも描かれない（稼働中の配備へ影響しない）。values.yaml の既定は false。
 *   2. 有効: 経路は exact /mcp ＋ prefix /mcp/ → edge.mcp.service:port、rewrite 無し、catch-all より前。
 *   3. 有効: MCP サーバーを選ぶ AuthorizationPolicy がちょうど 1 枚、DENY、from = gateway の Namespace（NetworkPolicy の穴と同じ値）、
 *      to = notPaths ["/mcp", "/mcp/*"]（経路と同じ字面）。ALLOW は 1 枚も無い。NetworkPolicy の穴は当該ポートだけ。
 *   4. 呼び出し元の一覧への評価: エッジ × /mcp の面だけが通り、エッジ × それ以外（/mcp-clients・/openapi・/internal・/health・/mcpx）は落ち、
 *      名前空間の中（BFF の管理 API の中継）・プローブは全部通る。
 *   5. knob の追随と edge.enabled=false。
 *   6. 変異: 一時複製したチャートで経路の制限を外す／from を名前空間の内側へ向ける／DENY を ALLOW へ／経路を `prefix: /mcp` へ戻すと赤。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** helm-private-notes-sync-authz.test.js と同じ理由。CI は static-checks-units で走らせる。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/helm-edge-mcp-entry.test.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const { parseDocuments } = require('./lib/yaml-subset.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const CI_VALUES = `${CHART}/ci/edge-mcp-values.yaml`;
const VALUES_LOCAL = 'deploy/local/values-local.yaml';
const MCP_PATHS = ['/mcp', '/mcp/*'];

const read = (rel) => fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

// ---------------------------------------------------------------- helpers

function hasHelm() {
  return spawnSync(process.platform === 'win32' ? 'where' : 'which', ['helm'], { encoding: 'utf8' }).status === 0;
}

function mustRender(args, chartDir = CHART) {
  const r = spawnSync('helm', ['template', 'msp', chartDir, ...args], { cwd: REPO_ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.stderr}`);
  return r.stdout;
}

/** 描画物のうち、読む kind だけを解析する（ConfigMap 等の読まない資源で落ちないように、文書を分けてから選ぶ）。 */
function resources(text, kinds = ['VirtualService', 'AuthorizationPolicy', 'NetworkPolicy']) {
  return text
    .split(/^---\s*$/m)
    .filter((c) => kinds.some((k) => new RegExp(`^kind:\\s*${k}\\s*$`, 'm').test(c)))
    .flatMap((c) => parseDocuments(c))
    .filter((d) => d && kinds.includes(d.kind));
}

const byKind = (text, kind) => resources(text).filter((d) => d.kind === kind);

/** Istio の path / 文字列の照合（AuthorizationPolicy の paths: 完全一致・`x*` 前置・`*x` 後置・`*` 何でも）。 */
function istioMatch(pattern, value) {
  if (pattern === '*') return true;
  if (pattern.endsWith('*')) return value.startsWith(pattern.slice(0, -1));
  if (pattern.startsWith('*')) return value.endsWith(pattern.slice(1));
  return pattern === value;
}

/** VirtualService の uri の照合（exact は完全一致、prefix は**文字列の**前方一致。Istio / Envoy の規則）。 */
function uriMatches(uri, p) {
  if (uri.exact !== undefined) return p === uri.exact;
  if (uri.prefix !== undefined) return p.startsWith(uri.prefix);
  throw new Error(`評価器が知らない uri の照合 ${JSON.stringify(uri)}`);
}

/** エッジの VirtualService で、要求のパスがどの行き先へ振られるか（先勝ち）。directResponse は 'direct'。 */
function routeOf(vs, p) {
  for (const h of vs.spec.http) {
    if ((h.match || []).some((m) => uriMatches(m.uri, p))) {
      return h.directResponse ? 'direct' : h.route[0].destination.host;
    }
  }
  return null;
}

const SOURCE_FIELDS = new Set(['namespaces', 'notNamespaces', 'principals', 'notPrincipals']);
const OPERATION_FIELDS = new Set(['paths', 'notPaths', 'methods', 'notMethods', 'ports', 'notPorts']);

function sourceMatches(src, req) {
  for (const k of Object.keys(src)) assert.ok(SOURCE_FIELDS.has(k), `評価器が知らない source の項目 ${k}`);
  const principal = req.ns === null ? null : `cluster.local/ns/${req.ns}/sa/${req.sa}`;
  if (src.namespaces && !(req.ns !== null && src.namespaces.some((x) => istioMatch(x, req.ns)))) return false;
  if (src.notNamespaces && req.ns !== null && src.notNamespaces.some((x) => istioMatch(x, req.ns))) return false;
  if (src.principals && !(principal !== null && src.principals.some((x) => istioMatch(x, principal)))) return false;
  if (src.notPrincipals && principal !== null && src.notPrincipals.some((x) => istioMatch(x, principal))) return false;
  return true;
}

function operationMatches(op, req) {
  for (const k of Object.keys(op)) assert.ok(OPERATION_FIELDS.has(k), `評価器が知らない operation の項目 ${k}`);
  if (op.paths && !op.paths.some((x) => istioMatch(x, req.path))) return false;
  if (op.notPaths && op.notPaths.some((x) => istioMatch(x, req.path))) return false;
  if (op.methods && !op.methods.some((x) => istioMatch(x, req.method))) return false;
  if (op.notMethods && op.notMethods.some((x) => istioMatch(x, req.method))) return false;
  if (op.ports && !op.ports.map(String).includes(String(req.port))) return false;
  if (op.notPorts && op.notPorts.map(String).includes(String(req.port))) return false;
  return true;
}

function ruleMatches(rule, req) {
  for (const k of Object.keys(rule)) assert.ok(['from', 'to'].includes(k), `評価器が知らない rule の項目 ${k}`);
  const fromOk = !rule.from || rule.from.some((f) => sourceMatches(f.source || {}, req));
  const toOk = !rule.to || rule.to.some((t) => operationMatches(t.operation || {}, req));
  return fromOk && toOk;
}

/** Istio の順序: DENY（当たれば拒否）→ ALLOW（1 枚でも在れば、当たらなければ拒否）。 */
function evaluate(policies, req) {
  for (const p of policies) assert.ok(['DENY', 'ALLOW'].includes(p.spec.action || 'ALLOW'), `評価器が知らない action ${p.spec.action}`);
  if (policies.filter((p) => p.spec.action === 'DENY').some((p) => (p.spec.rules || []).some((r) => ruleMatches(r, req)))) return 'deny';
  const allows = policies.filter((p) => (p.spec.action || 'ALLOW') === 'ALLOW');
  if (allows.length === 0) return 'allow';
  return allows.some((p) => (p.spec.rules || []).some((r) => ruleMatches(r, req))) ? 'allow' : 'deny';
}

const policiesFor = (text, app) =>
  byKind(text, 'AuthorizationPolicy').filter((o) => ((((o.spec || {}).selector || {}).matchLabels) || {}).app === app);

/**
 * MCP サーバーの呼び出し元の一覧（作業仕様書 20261023_2045 §母集合）。ns が null は平文（サイドカー無し）。
 * 名前空間の中の呼び出し元は BFF（管理 REST の中継。Services__McpServer）だけで、east-west の REST は撤去済み（IADR-0533）。
 */
function callers(gatewayNs, releaseNs) {
  const gw = (sa = 'istio-ingressgateway-service-account') => ({ ns: gatewayNs, sa });
  return [
    { who: 'BFF → 管理 REST 一覧（/bff/admin/mcp-clients の中継）', ns: releaseNs, sa: 'bff-service', port: 8080, method: 'GET', path: '/mcp-clients', want: 'allow' },
    { who: 'BFF → 管理 REST 登録', ns: releaseNs, sa: 'bff-service', port: 8080, method: 'POST', path: '/mcp-clients', want: 'allow' },
    { who: 'BFF → 管理 REST 実効ツール一覧', ns: releaseNs, sa: 'bff-service', port: 8080, method: 'GET', path: '/mcp-clients/effective-tools', want: 'allow' },
    { who: 'kubelet → readiness（平文）', ns: null, port: 8080, method: 'GET', path: '/health/ready', want: 'allow' },
    { who: 'kubelet → liveness（平文）', ns: null, port: 8080, method: 'GET', path: '/health/live', want: 'allow' },
    { who: 'エッジ → /mcp（Streamable HTTP の POST）', ...gw(), port: 8080, method: 'POST', path: '/mcp', want: 'allow' },
    { who: 'エッジ → /mcp（SSE の GET）', ...gw(), port: 8080, method: 'GET', path: '/mcp', want: 'allow' },
    { who: 'エッジ → /mcp（セッションの DELETE）', ...gw(), port: 8080, method: 'DELETE', path: '/mcp', want: 'allow' },
    { who: 'エッジ（helm の gateway chart の SA 名）→ /mcp/ 配下', ...gw('istio-ingress'), port: 8080, method: 'POST', path: '/mcp/message', want: 'allow' },
    { who: '🔴 エッジ → /mcp-clients（管理 REST。別の VirtualService が振り分けた想定）', ...gw(), port: 8080, method: 'GET', path: '/mcp-clients', want: 'deny' },
    { who: '🔴 エッジ → /mcp-clients/x/reissue-secret', ...gw(), port: 8080, method: 'POST', path: '/mcp-clients/x/reissue-secret', want: 'deny' },
    { who: '🔴 エッジ → /mcpx（前置の境界）', ...gw(), port: 8080, method: 'POST', path: '/mcpx', want: 'deny' },
    { who: '🔴 エッジ → /openapi/v1.json', ...gw(), port: 8080, method: 'GET', path: '/openapi/v1.json', want: 'deny' },
    { who: '🔴 エッジ → /internal/introspection', ...gw(), port: 8080, method: 'GET', path: '/internal/introspection', want: 'deny' },
    { who: '🔴 エッジ → /health/ready', ...gw(), port: 8080, method: 'GET', path: '/health/ready', want: 'deny' },
    { who: '🔴 gateway の名前空間の別の主体 → /mcp-clients（NetworkPolicy の穴は名前空間単位）', ...gw('some-other-sa'), port: 8080, method: 'GET', path: '/mcp-clients', want: 'deny' },
  ];
}

/** 描画に対する判定（変異試験でも同じ関数を使う）。戻り値は問題の一覧。空なら緑。 */
function enabledProblems(text, { app, port, gatewayNs, releaseNs }) {
  const problems = [];
  const policies = policiesFor(text, app);
  if (policies.length !== 1) problems.push(`${app} を選ぶ AuthorizationPolicy が ${policies.length} 枚（期待 1）`);
  if (policies.some((p) => (p.spec.action || 'ALLOW') === 'ALLOW')) problems.push(`🔴 ${app} を選ぶ ALLOW が在る（当たらない呼び出し元がすべて拒否に変わる）`);
  for (const c of callers(gatewayNs, releaseNs)) {
    const got = evaluate(policies, c);
    if (got !== c.want) problems.push(`${c.who}（${c.method} ${c.path} :${c.port}）が ${got}（期待 ${c.want}）`);
  }
  const vs = byKind(text, 'VirtualService').find((d) => d.metadata.name === 'microservices-platform-edge');
  if (!vs) {
    problems.push('エッジの VirtualService が無い');
    return problems;
  }
  // 経路: エッジ host の要求のパス → 行き先。/mcp の面だけが MCP サーバーへ届き、管理 REST は届かない。
  for (const [p, want] of [['/mcp', app], ['/mcp/', app], ['/mcp/message', app], ['/mcp-clients', 'frontend-service'],
    ['/mcp-clients/x/reissue-secret', 'frontend-service'], ['/mcpx', 'frontend-service'], ['/openapi/v1.json', 'frontend-service'], ['/bff/auth/me', 'bff-service']]) {
    const got = routeOf(vs, p);
    if (got !== want) problems.push(`経路: ${p} が ${got}（期待 ${want}）`);
  }
  const route = vs.spec.http.find((h) => h.route && h.route[0].destination.host === app);
  if (route) {
    if (route.rewrite) problems.push('経路が rewrite を張っている（公開パスと契約パスがずれる）');
    if (String(route.route[0].destination.port.number) !== String(port)) problems.push(`経路のポートが ${route.route[0].destination.port.number}（期待 ${port}）`);
  }
  const np = byKind(text, 'NetworkPolicy').find((d) => d.metadata.name === 'allow-edge-ingress-to-mcp');
  if (!np) {
    problems.push('NetworkPolicy allow-edge-ingress-to-mcp が無い（default-deny の下でエッジから届かない）');
  } else {
    if (np.spec.podSelector.matchLabels.app !== app) problems.push(`NetworkPolicy の podSelector が ${np.spec.podSelector.matchLabels.app}`);
    const from = np.spec.ingress[0].from[0].namespaceSelector.matchLabels['kubernetes.io/metadata.name'];
    if (from !== gatewayNs) problems.push(`NetworkPolicy の from が ${from}（期待 ${gatewayNs}）`);
    const ports = np.spec.ingress[0].ports.map((x) => String(x.port));
    if (JSON.stringify(ports) !== JSON.stringify([String(port)])) problems.push(`NetworkPolicy のポートが ${ports}（期待 ${port} だけ）`);
  }
  return problems;
}

// ---------------------------------------------------------------- 静的（helm 不要）

ok('評価器の自己試験: VirtualService の prefix は文字列の前方一致（/mcp は /mcp-clients を飲む）・exact は完全一致', () => {
  assert.ok(uriMatches({ prefix: '/mcp' }, '/mcp-clients'), 'Istio の prefix は文字列の前方一致である');
  assert.ok(!uriMatches({ prefix: '/mcp/' }, '/mcp-clients'));
  assert.ok(!uriMatches({ prefix: '/mcp/' }, '/mcp'));
  assert.ok(uriMatches({ exact: '/mcp' }, '/mcp') && !uriMatches({ exact: '/mcp' }, '/mcp/'));
  assert.throws(() => uriMatches({ regex: '.*' }, '/'));
});

ok('評価器の自己試験: AuthorizationPolicy の DENY は当たった要求だけ・ALLOW が 1 枚でも在れば当たらない要求は拒否', () => {
  const deny = { spec: { action: 'DENY', rules: [{ from: [{ source: { namespaces: ['g'] } }], to: [{ operation: { notPaths: MCP_PATHS } }] }] } };
  const req = (ns, p) => ({ ns, sa: 'x', path: p, port: 8080, method: 'GET' });
  assert.strictEqual(evaluate([deny], req('g', '/mcp')), 'allow');
  assert.strictEqual(evaluate([deny], req('g', '/mcp/x')), 'allow');
  assert.strictEqual(evaluate([deny], req('g', '/mcp-clients')), 'deny');
  assert.strictEqual(evaluate([deny], req('g', '/mcpx')), 'deny');
  assert.strictEqual(evaluate([deny], req('in', '/mcp-clients')), 'allow');
  assert.strictEqual(evaluate([deny], req(null, '/health/ready')), 'allow', '平文は namespaces の DENY に当たらない');
  const allow = { spec: { action: 'ALLOW', rules: [{ from: [{ source: { namespaces: ['g'] } }] }] } };
  assert.strictEqual(evaluate([allow], req('in', '/mcp-clients')), 'deny');
  assert.throws(() => evaluate([{ spec: { action: 'DENY', rules: [{ when: [] }] } }], req('g', '/')));
});

ok('前提: ci の values は「有効」だけを宣言している（有効の定義を 2 か所に書かない）', () => {
  const body = read(CI_VALUES).split('\n').filter((l) => l.trim() !== '' && !/^\s*#/.test(l));
  assert.deepStrictEqual(body, ['edge:', '  mcp:', '    enabled: true']);
});

ok('🔴 本番像の既定は opt-in（values.yaml の edge.mcp.enabled: false）', () => {
  const values = read(`${CHART}/values.yaml`);
  const at = values.search(/^ {2}mcp:\n {4}enabled:/m);
  assert.ok(at !== -1, 'values.yaml に edge.mcp（字下げ 2 の mcp: の直下に enabled）が無い');
  assert.match(values.slice(at, at + 80), /^ {2}mcp:\n {4}enabled: false\n/, 'edge.mcp.enabled の既定が false でない');
});

// ---------------------------------------------------------------- 描画（helm が要る）

if (!hasHelm()) {
  process.stderr.write('✗ helm が PATH に無い。本試験は実際に `helm template` を叩くことが目的であり、飛ばさない（fail-closed）。\n');
  process.exit(1);
}

const OFF = mustRender([]);
const ON = mustRender(['-f', CI_VALUES]);
const OFF_LOCAL = mustRender(['-f', VALUES_LOCAL]);
const ON_LOCAL = mustRender(['-f', VALUES_LOCAL, '-f', CI_VALUES]);
const DEFAULTS = { app: 'mcp-service', port: 8080, gatewayNs: 'istio-system', releaseNs: 'microservices-platform' };

ok('既定（無効）・values-local: 経路・門・NetworkPolicy の穴のどれも描かれない', () => {
  for (const [label, text] of [['既定', OFF], ['values-local', OFF_LOCAL]]) {
    assert.deepStrictEqual(policiesFor(text, 'mcp-service'), [], `${label} に方針が描かれた`);
    assert.ok(!/exact: \/mcp\b|prefix: \/mcp\//.test(text), `${label} に /mcp の経路が現れた`);
    assert.ok(!text.includes('allow-edge-ingress-to-mcp'), `${label} に NetworkPolicy の穴が現れた`);
  }
});

ok('values-local（edge.enabled=false）: edge.mcp を有効にしても何も描かれない（他の edge.* と同じ条件）', () => {
  assert.strictEqual(ON_LOCAL, OFF_LOCAL, 'edge 無効の構成で edge.mcp の knob が何かを描いた');
});

ok('既定（無効）: 評価すると陰性対照が赤になる（経路も門も無い＝/mcp は届かない）', () => {
  const problems = enabledProblems(OFF, DEFAULTS);
  assert.ok(problems.some((x) => x.includes('経路: /mcp が frontend-service')), JSON.stringify(problems));
});

ok('有効: MCP サーバーを選ぶ DENY がちょうど 1 枚、from = gateway の Namespace、to = notPaths /mcp・/mcp/*', () => {
  const [p, ...rest] = policiesFor(ON, 'mcp-service');
  assert.ok(p && rest.length === 0, '方針が 1 枚でない');
  assert.strictEqual(p.metadata.name, 'mcp-service-edge-mcp-only');
  assert.strictEqual(p.metadata.namespace, 'microservices-platform');
  assert.strictEqual(p.apiVersion, 'security.istio.io/v1');
  assert.strictEqual(p.spec.action, 'DENY');
  assert.deepStrictEqual(p.spec.rules, [{ from: [{ source: { namespaces: ['istio-system'] } }], to: [{ operation: { notPaths: MCP_PATHS } }] }]);
});

ok('有効: 経路の照合は exact /mcp ＋ prefix /mcp/ で、門の notPaths はその字面（exact ＋ prefix の "*"）と一致する', () => {
  const vs = byKind(ON, 'VirtualService').find((d) => d.metadata.name === 'microservices-platform-edge');
  const route = vs.spec.http.find((h) => h.route && h.route[0].destination.host === 'mcp-service');
  assert.deepStrictEqual(route.match, [{ uri: { exact: '/mcp' } }, { uri: { prefix: '/mcp/' } }]);
  const want = route.match.map((m) => (m.uri.exact !== undefined ? m.uri.exact : `${m.uri.prefix}*`));
  assert.deepStrictEqual(policiesFor(ON, 'mcp-service')[0].spec.rules[0].to[0].operation.notPaths, want);
  const at = vs.spec.http.indexOf(route);
  const catchAll = vs.spec.http.findIndex((h) => (h.match || []).some((m) => m.uri.prefix === '/'));
  assert.ok(catchAll > at, '/mcp の経路が catch-all より後ろにある（SPA に吸われる）');
});

ok('🔴 有効: 呼び出し元と経路の一覧へ評価すると、エッジ × /mcp の面だけが通り、他は 1 本も切れない', () => {
  assert.deepStrictEqual(enabledProblems(ON, DEFAULTS), []);
});

ok('有効: 他のワークロードを選ぶ方針は増えない', () => {
  assert.deepStrictEqual(byKind(ON, 'AuthorizationPolicy').map((d) => d.metadata.name), ['mcp-service-edge-mcp-only']);
});

ok('knob の追随: edge.gateway.namespace / edge.mcp.service / edge.mcp.port を変えると経路・門・穴が同じ値へ動く', () => {
  const t = mustRender(['-f', CI_VALUES, '--set', 'edge.gateway.namespace=istio-ingress']);
  assert.deepStrictEqual(enabledProblems(t, { ...DEFAULTS, gatewayNs: 'istio-ingress' }), []);
  const u = mustRender(['-f', CI_VALUES, '--set', 'edge.mcp.service=mcp-x', '--set', 'edge.mcp.port=9090']);
  assert.deepStrictEqual(policiesFor(u, 'mcp-service'), []);
  assert.deepStrictEqual(enabledProblems(u, { ...DEFAULTS, app: 'mcp-x', port: 9090 }), []);
  assert.strictEqual(policiesFor(u, 'mcp-x')[0].metadata.name, 'mcp-x-edge-mcp-only');
});

ok('edge.enabled=false: edge.mcp が立っていても経路・門・穴は描かれない', () => {
  const t = mustRender(['-f', CI_VALUES, '--set', 'edge.enabled=false']);
  assert.deepStrictEqual(policiesFor(t, 'mcp-service'), []);
  assert.ok(!t.includes('allow-edge-ingress-to-mcp'));
});

ok('同期の経路と併用しても、2 つの門は互いのワークロードを選ばない', () => {
  const t = mustRender(['-f', CI_VALUES, '-f', `${CHART}/ci/private-notes-sync-values.yaml`]);
  assert.deepStrictEqual(enabledProblems(t, DEFAULTS), []);
  assert.deepStrictEqual(byKind(t, 'AuthorizationPolicy').map((d) => d.metadata.name).sort(),
    ['document-service-edge-sync-only', 'mcp-service-edge-mcp-only']);
});

// ---------------------------------------------------------------- 変異（一時複製したチャートで）

function mutatedRender(file, transform) {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'helm-edge-mcp-mut-'));
  try {
    const chartCopy = path.join(tmp, 'chart');
    fs.cpSync(path.join(REPO_ROOT, CHART), chartCopy, { recursive: true });
    const tpl = path.join(chartCopy, 'templates', file);
    const original = fs.readFileSync(tpl, 'utf8');
    const mutated = transform(original);
    assert.notStrictEqual(mutated, original, '変異を当てられなかった（テンプレートの書き方が変わった）');
    fs.writeFileSync(tpl, mutated);
    return mustRender(['-f', path.join(REPO_ROOT, CI_VALUES)], chartCopy);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
}

ok('🔴 変異: 門の経路の制限（notPaths）を外すと、エッジ × /mcp が落ちて赤になる', () => {
  const text = mutatedRender('edge.yaml', (s) => s.replace(/\n {6}to:\n {8}- operation:\n {12}notPaths:\n {14}- \/mcp\n {14}- \/mcp\/\*/, ''));
  const problems = enabledProblems(text, DEFAULTS);
  assert.ok(problems.some((x) => x.includes('/mcp（Streamable HTTP の POST）') && x.includes('期待 allow')), JSON.stringify(problems));
});

ok('🔴 変異: 門を /mcp* へ広げると、エッジ × /mcp-clients・/mcpx が通って赤になる', () => {
  const text = mutatedRender('edge.yaml', (s) => s.replace('              - /mcp\n              - /mcp/*', '              - /mcp*'));
  const problems = enabledProblems(text, DEFAULTS);
  assert.ok(problems.some((x) => x.includes('/mcp-clients（管理 REST') && x.includes('期待 deny')), JSON.stringify(problems));
});

ok('🔴 変異: 経路を `prefix: /mcp` へ戻すと、/mcp-clients がエッジから MCP サーバーへ届いて赤になる', () => {
  const text = mutatedRender('edge.yaml', (s) => s.replace('        - uri: { exact: /mcp }\n        - uri: { prefix: /mcp/ }', '        - uri: { prefix: /mcp }'));
  const problems = enabledProblems(text, DEFAULTS);
  assert.ok(problems.some((x) => x.startsWith('経路: /mcp-clients が mcp-service')), JSON.stringify(problems));
});

ok('🔴 変異: from を名前空間の内側へ向けると、BFF の管理 API の中継が切れて赤になる', () => {
  const text = mutatedRender('edge.yaml', (s) => {
    const at = s.indexOf('-edge-mcp-only');
    return s.slice(0, at) + s.slice(at).replace('{{ .Values.edge.gateway.namespace | quote }}', '{{ .Values.namespace.name | quote }}');
  });
  const problems = enabledProblems(text, DEFAULTS);
  assert.ok(problems.some((x) => x.startsWith('BFF') && x.includes('期待 allow')), JSON.stringify(problems));
});

ok('🔴 変異: DENY を ALLOW に変えると、BFF・プローブが切れて赤になる', () => {
  const text = mutatedRender('edge.yaml', (s) => {
    const at = s.indexOf('-edge-mcp-only');
    return s.slice(0, at) + s.slice(at).replace('action: DENY', 'action: ALLOW');
  });
  const problems = enabledProblems(text, DEFAULTS);
  assert.ok(problems.some((x) => x.includes('ALLOW が在る')), JSON.stringify(problems));
  assert.ok(problems.some((x) => x.startsWith('kubelet') && x.includes('期待 allow')), JSON.stringify(problems));
});

ok('🔴 変異: NetworkPolicy の穴を消すと赤になる', () => {
  const text = mutatedRender('networkpolicy.yaml', (s) => s.replace('name: allow-edge-ingress-to-mcp', 'name: allow-edge-ingress-to-mcp-gone'));
  const problems = enabledProblems(text, DEFAULTS);
  assert.ok(problems.some((x) => x.includes('allow-edge-ingress-to-mcp が無い')), JSON.stringify(problems));
});

process.stdout.write(`\n✓ ${passed} tests passed\n`);
