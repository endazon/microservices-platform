#!/usr/bin/env node
'use strict';
/*
 * helm-private-notes-sync-authz.test.js
 * NFR-09, FR-20, ADR-0084, IADR-0348 追記 (#1606):
 * **edge.privateNotesSync を有効にしたとき、エッジの主体を DocumentService の /private-notes/sync/* に絞る
 * AuthorizationPolicy を、実際に `helm template` で描いて固定する。**
 *
 * 🔴 **守るのは「呼び出し元を 1 本も切らずに、エッジの穴だけを経路で絞る」ことである。**
 *    Istio の ALLOW はワークロードを選んだ瞬間に「当たらないものは全部拒否」へ変わる。DocumentService の
 *    呼び出し元（BFF / GraphService / McpServer / 別名前空間の AST / kubelet のプローブ）を 1 本でも落とすと、
 *    同期は動くのに文書閲覧・タグ付け・MCP・AST の KB 保存が黙って 403 になる。だから本試験は字面だけでなく、
 *    **描いた方針を Istio の評価規則で呼び出し元の一覧へ当て**、結果（通す／落とす）で固定する。
 *
 * 固定するもの:
 *   1. 既定（無効）・values-local: 方針が描かれない（稼働中の PoC へ影響しない）。
 *   2. 有効: DocumentService を選ぶ AuthorizationPolicy がちょうど 1 枚、action DENY、
 *      from = gateway の Namespace（NetworkPolicy の穴と同じ値）、to = notPaths ["/private-notes/sync/*"]
 *      （route の前置 + "*" と同じ字面）。ALLOW は 1 枚も無い。
 *   3. 呼び出し元の一覧への評価: gateway × sync だけが通り、gateway × それ以外は落ち、他の呼び出し元は全部通る。
 *   4. knob の追随: edge.gateway.namespace / edge.privateNotesSync.service を変えると方針も同じ値へ動く。
 *      edge.enabled=false なら route と同じく描かない。
 *   5. 変異: 一時複製したチャートで経路の制限を外す／from を別の Namespace へ変えると、本試験の判定が赤になる。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** helm-synthetic-monitor.test.js と同じ理由（黙って飛ばすと
 *    「検査していない」と「問題が無い」が同じ出力になる）。CI は static-checks-units（azure/setup-helm 済み）で走らせる。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/helm-private-notes-sync-authz.test.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const CI_VALUES = `${CHART}/ci/private-notes-sync-values.yaml`;
const VALUES_LOCAL = 'deploy/local/values-local.yaml';
const SYNC_PATHS = ['/private-notes/sync/*'];

const read = (rel) => fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');

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

function helmTemplate(args, chartDir = CHART) {
  const r = spawnSync('helm', ['template', 'msp', chartDir, ...args], {
    cwd: REPO_ROOT,
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
  });
  return { status: r.status, out: r.stdout || '', err: r.stderr || '' };
}

function mustRender(args, chartDir) {
  const r = helmTemplate(args, chartDir);
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.err}`);
  return r.out;
}

const unquote = (s) => String(s).trim().replace(/^["']|["']$/g, '');

/**
 * 描画物（ブロック形式の YAML）の最小パーサ。対応: マップ・リスト・スカラー・`{ k: v }` の flow マップ・注記行。
 * 本チャートの AuthorizationPolicy / NetworkPolicy / VirtualService を読むのに足りる範囲だけを持つ。
 */
function parseYaml(text) {
  const lines = text
    .split('\n')
    .filter((l) => l.trim() !== '' && !/^\s*#/.test(l) && l.trim() !== '---')
    .map((l) => ({ indent: l.length - l.trimStart().length, body: l.trim() }));
  let i = 0;

  function scalar(s) {
    const t = s.trim();
    const flow = /^\{(.*)\}$/.exec(t);
    if (flow) {
      const m = {};
      for (const part of flow[1].split(',').map((p) => p.trim()).filter(Boolean)) {
        const at = part.indexOf(':');
        m[part.slice(0, at).trim()] = unquote(part.slice(at + 1));
      }
      return m;
    }
    return unquote(t.replace(/\s+#.*$/, ''));
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
    // 値が次の行から始まる（より深い字下げ、または同じ字下げのリスト）。
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
        if (/^[^{"'][^:]*:(\s|$)/.test(itemBody)) {
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

/**
 * helm の出力を資源ごとに分け、kind・名前を持たせる。解析（obj）は**読む資源だけ**遅延で行う ——
 * ConfigMap の literal block 等は最小パーサの対象外であり、読まない資源まで解析して落ちないようにする。
 */
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

// ---------------------------------------------------------------- Istio の評価規則（本試験が使う部分集合）

/** Istio の path / 文字列の照合（完全一致・`x*` 前置・`*x` 後置・`*` 何でも）。 */
function istioMatch(pattern, value) {
  if (pattern === '*') return true;
  if (pattern.endsWith('*')) return value.startsWith(pattern.slice(0, -1));
  if (pattern.startsWith('*')) return value.endsWith(pattern.slice(1));
  return pattern === value;
}

const SOURCE_FIELDS = new Set(['namespaces', 'notNamespaces', 'principals', 'notPrincipals']);
const OPERATION_FIELDS = new Set(['paths', 'notPaths', 'methods', 'notMethods', 'ports', 'notPorts']);

/**
 * source の照合。平文（mTLS でない）の要求は principal も namespace も持たない（req.ns === null）。
 * Istio は namespaces / principals を「mTLS が要る」属性として扱うので、平文は正の照合に当たらず、否定の照合には当たる。
 */
function sourceMatches(src, req) {
  for (const k of Object.keys(src)) {
    assert.ok(SOURCE_FIELDS.has(k), `評価器が知らない source の項目 ${k}（試験の評価器を広げること）`);
  }
  const principal = req.ns === null ? null : `cluster.local/ns/${req.ns}/sa/${req.sa}`;
  if (src.namespaces && !(req.ns !== null && src.namespaces.some((p) => istioMatch(p, req.ns)))) return false;
  if (src.notNamespaces && req.ns !== null && src.notNamespaces.some((p) => istioMatch(p, req.ns))) return false;
  if (src.principals && !(principal !== null && src.principals.some((p) => istioMatch(p, principal)))) return false;
  if (src.notPrincipals && principal !== null && src.notPrincipals.some((p) => istioMatch(p, principal))) return false;
  return true;
}

function operationMatches(op, req) {
  for (const k of Object.keys(op)) {
    assert.ok(OPERATION_FIELDS.has(k), `評価器が知らない operation の項目 ${k}（試験の評価器を広げること）`);
  }
  if (op.paths && !op.paths.some((p) => istioMatch(p, req.path))) return false;
  if (op.notPaths && op.notPaths.some((p) => istioMatch(p, req.path))) return false;
  if (op.methods && !op.methods.some((m) => istioMatch(m, req.method))) return false;
  if (op.notMethods && op.notMethods.some((m) => istioMatch(m, req.method))) return false;
  if (op.ports && !op.ports.map(String).includes(String(req.port))) return false;
  if (op.notPorts && op.notPorts.map(String).includes(String(req.port))) return false;
  return true;
}

function ruleMatches(rule, req) {
  for (const k of Object.keys(rule)) {
    assert.ok(['from', 'to'].includes(k), `評価器が知らない rule の項目 ${k}（when 等。試験の評価器を広げること）`);
  }
  const fromOk = !rule.from || rule.from.some((f) => sourceMatches(f.source || {}, req));
  const toOk = !rule.to || rule.to.some((t) => operationMatches(t.operation || {}, req));
  return fromOk && toOk;
}

/**
 * DocumentService のワークロード（app ラベル）を選ぶ AuthorizationPolicy 群で要求を評価する。
 * Istio の順序: CUSTOM → DENY（当たれば拒否）→ ALLOW（1 枚でも在れば、当たらなければ拒否）。
 */
function evaluate(policies, req) {
  for (const p of policies) {
    assert.ok(['DENY', 'ALLOW'].includes(p.spec.action || 'ALLOW'), `評価器が知らない action ${p.spec.action}`);
  }
  const denies = policies.filter((p) => p.spec.action === 'DENY');
  if (denies.some((p) => (p.spec.rules || []).some((r) => ruleMatches(r, req)))) return 'deny';
  const allows = policies.filter((p) => (p.spec.action || 'ALLOW') === 'ALLOW');
  if (allows.length === 0) return 'allow';
  return allows.some((p) => (p.spec.rules || []).some((r) => ruleMatches(r, req))) ? 'allow' : 'deny';
}

/** 描画から、指定ワークロードを選ぶ AuthorizationPolicy の本体を集める。 */
function policiesFor(text, app) {
  return docs(text)
    .filter((d) => d.kind === 'AuthorizationPolicy')
    .map((d) => d.obj)
    .filter((o) => ((((o.spec || {}).selector || {}).matchLabels) || {}).app === app);
}

/**
 * DocumentService の呼び出し元の一覧（#1606 の作業仕様書 §2 で引いた母集合）。
 * ns: 呼び出し元の名前空間（mTLS の主体から採れる値）。null は平文（サイドカー無し）＝ principal を持たない。
 * gateway の名前空間は描画の値から与える（knob を変えた描画でも同じ一覧で評価するため）。
 */
function callers(gatewayNs, releaseNs) {
  const inNs = (sa) => ({ ns: releaseNs, sa });
  return [
    { who: 'BFF → REST 文書（閲覧・保存・個人資料・端末・競合の中継）', ...inNs('bff-service'), port: 8080, method: 'GET', path: '/documents/00000000-0000-0000-0000-000000000001', want: 'allow' },
    { who: 'BFF → REST 同期設定（sync- で始まるが sync/ ではない）', ...inNs('bff-service'), port: 8080, method: 'PUT', path: '/private-notes/sync-settings/', want: 'allow' },
    { who: 'BFF → REST 構成の自己申告', ...inNs('bff-service'), port: 8080, method: 'GET', path: '/internal/introspection', want: 'allow' },
    { who: 'BFF → gRPC 文書の読み取り', ...inNs('bff-service'), port: 8081, method: 'POST', path: '/knowledge.document.v1.DocumentRead/GetDocument', want: 'allow' },
    { who: 'BFF → gRPC 構成の自己申告', ...inNs('bff-service'), port: 8081, method: 'POST', path: '/platform.introspection.v1.ServiceIntrospection/Get', want: 'allow' },
    { who: 'GraphService → REST タグの書き戻し', ...inNs('graph-service'), port: 8080, method: 'POST', path: '/documents/00000000-0000-0000-0000-000000000001/tags', want: 'allow' },
    { who: 'GraphService → gRPC タグの書き戻し', ...inNs('graph-service'), port: 8081, method: 'POST', path: '/knowledge.document.v1.DocumentTagWrite/AddTag', want: 'allow' },
    { who: 'GraphService → gRPC タグ辞書', ...inNs('graph-service'), port: 8081, method: 'POST', path: '/knowledge.document.v1.TagDictionary/ListNames', want: 'allow' },
    { who: 'McpServer → REST ツール申告', ...inNs('mcp-server'), port: 8080, method: 'GET', path: '/internal/mcp-tools', want: 'allow' },
    { who: 'McpServer → gRPC ツール実行（#1516。REST の実行経路は廃した）', ...inNs('mcp-server'), port: 8081, method: 'POST', path: '/platform.mcp.v1.McpToolExecution/Execute', want: 'allow' },
    { who: 'McpServer → gRPC ツール申告', ...inNs('mcp-server'), port: 8081, method: 'POST', path: '/platform.mcp.v1.McpToolDeclarations/Declare', want: 'allow' },
    { who: 'AST（別名前空間・サイドカー無し＝平文）→ REST KB 保存', ns: null, port: 8080, method: 'POST', path: '/documents', want: 'allow' },
    { who: 'AST（別名前空間・メッシュ参入後＝mTLS）→ REST KB 保存', ns: 'ai-stock-trading', sa: 'default', port: 8080, method: 'POST', path: '/documents', want: 'allow' },
    { who: 'kubelet → readiness（平文。プローブの書き換えが無い場合）', ns: null, port: 8080, method: 'GET', path: '/health/ready', want: 'allow' },
    { who: 'kubelet → liveness（平文）', ns: null, port: 8080, method: 'GET', path: '/health/live', want: 'allow' },
    { who: 'エッジ → 同期 manifest', ns: gatewayNs, sa: 'istio-ingressgateway-service-account', port: 8080, method: 'GET', path: '/private-notes/sync/manifest', want: 'allow' },
    { who: 'エッジ → 同期 push', ns: gatewayNs, sa: 'istio-ingressgateway-service-account', port: 8080, method: 'POST', path: '/private-notes/sync/notes', want: 'allow' },
    { who: 'エッジ（helm の gateway chart の SA 名）→ 同期 pull', ns: gatewayNs, sa: 'istio-ingress', port: 8080, method: 'GET', path: '/private-notes/sync/notes/00000000-0000-0000-0000-000000000001', want: 'allow' },
    { who: '🔴 エッジ → /documents（JWT 経路。別の VirtualService が振り分けた想定）', ns: gatewayNs, sa: 'istio-ingressgateway-service-account', port: 8080, method: 'GET', path: '/documents', want: 'deny' },
    { who: '🔴 エッジ → /private-notes（一覧）', ns: gatewayNs, sa: 'istio-ingressgateway-service-account', port: 8080, method: 'GET', path: '/private-notes/', want: 'deny' },
    { who: '🔴 エッジ → /private-notes/devices/', ns: gatewayNs, sa: 'istio-ingressgateway-service-account', port: 8080, method: 'POST', path: '/private-notes/devices/', want: 'deny' },
    { who: '🔴 エッジ → /private-notes/sync-settings/（前置の境界）', ns: gatewayNs, sa: 'istio-ingressgateway-service-account', port: 8080, method: 'PUT', path: '/private-notes/sync-settings/', want: 'deny' },
    { who: '🔴 エッジ → /private-notes/sync（末尾スラッシュ無し。route も当たらない）', ns: gatewayNs, sa: 'istio-ingressgateway-service-account', port: 8080, method: 'GET', path: '/private-notes/sync', want: 'deny' },
    { who: '🔴 エッジ → gRPC 面（8081）', ns: gatewayNs, sa: 'istio-ingressgateway-service-account', port: 8081, method: 'POST', path: '/knowledge.document.v1.DocumentRead/GetDocument', want: 'deny' },
    { who: '🔴 gateway の名前空間の別の主体 → /documents（NetworkPolicy の穴は名前空間単位）', ns: gatewayNs, sa: 'some-other-sa', port: 8080, method: 'GET', path: '/documents', want: 'deny' },
  ];
}

/** 描画に対する判定（変異試験でも同じ関数を使う）。戻り値は問題の一覧。空なら緑。 */
function enabledProblems(text, { app, gatewayNs, releaseNs }) {
  const problems = [];
  const policies = policiesFor(text, app);
  if (policies.length !== 1) problems.push(`${app} を選ぶ AuthorizationPolicy が ${policies.length} 枚（期待 1）`);
  if (policies.some((p) => (p.spec.action || 'ALLOW') === 'ALLOW')) {
    problems.push(`🔴 ${app} を選ぶ ALLOW が在る（当たらない呼び出し元がすべて拒否に変わる）`);
  }
  for (const c of callers(gatewayNs, releaseNs)) {
    const got = evaluate(policies, c);
    if (got !== c.want) problems.push(`${c.who}（${c.method} ${c.path} :${c.port}）が ${got}（期待 ${c.want}）`);
  }
  return problems;
}

// ---------------------------------------------------------------- 静的（helm 不要）

ok('評価器の自己試験: Istio の path 照合（前置・完全一致・後置・境界）', () => {
  assert.ok(istioMatch('/private-notes/sync/*', '/private-notes/sync/manifest'));
  assert.ok(istioMatch('/private-notes/sync/*', '/private-notes/sync/'));
  assert.ok(!istioMatch('/private-notes/sync/*', '/private-notes/sync'));
  assert.ok(!istioMatch('/private-notes/sync/*', '/private-notes/sync-settings/'));
  assert.ok(istioMatch('*', '/anything'));
  assert.ok(istioMatch('*.proto', '/a.proto'));
  assert.ok(istioMatch('/exact', '/exact') && !istioMatch('/exact', '/exact/'));
});

ok('評価器の自己試験: ALLOW が 1 枚でも在れば当たらない要求は拒否（default-deny）・DENY は当たった要求だけ', () => {
  const allow = { spec: { action: 'ALLOW', rules: [{ from: [{ source: { namespaces: ['ns-a'] } }] }] } };
  const deny = { spec: { action: 'DENY', rules: [{ from: [{ source: { namespaces: ['ns-g'] } }], to: [{ operation: { notPaths: ['/ok/*'] } }] }] } };
  assert.strictEqual(evaluate([allow], { ns: 'ns-a', sa: 'x', path: '/', port: 8080, method: 'GET' }), 'allow');
  assert.strictEqual(evaluate([allow], { ns: 'ns-b', sa: 'x', path: '/', port: 8080, method: 'GET' }), 'deny');
  assert.strictEqual(evaluate([allow], { ns: null, path: '/', port: 8080, method: 'GET' }), 'deny', '平文は namespaces の ALLOW に当たらない');
  assert.strictEqual(evaluate([deny], { ns: 'ns-g', sa: 'x', path: '/ok/1', port: 8080, method: 'GET' }), 'allow');
  assert.strictEqual(evaluate([deny], { ns: 'ns-g', sa: 'x', path: '/no', port: 8080, method: 'GET' }), 'deny');
  assert.strictEqual(evaluate([deny], { ns: 'ns-b', sa: 'x', path: '/no', port: 8080, method: 'GET' }), 'allow');
  assert.strictEqual(evaluate([deny], { ns: null, path: '/no', port: 8080, method: 'GET' }), 'allow', '平文は namespaces の DENY に当たらない');
  assert.throws(() => evaluate([{ spec: { action: 'DENY', rules: [{ when: [] }] } }], { ns: null, path: '/', port: 1, method: 'GET' }));
});

ok('評価器の自己試験: 最小パーサがブロック形式・flow マップ・注記を読む', () => {
  const y = parseYaml(
    'kind: X\n# note\nspec:\n  selector:\n    matchLabels:\n      app: "a"\n  rules:\n    - from:\n        - source:\n            namespaces:\n              - "ns"\n      to:\n        - operation:\n            notPaths:\n              - /p/*\n  http:\n    - match:\n        - uri: { prefix: /q/ }\n',
  );
  assert.deepStrictEqual(y.spec.selector.matchLabels, { app: 'a' });
  assert.deepStrictEqual(y.spec.rules, [{ from: [{ source: { namespaces: ['ns'] } }], to: [{ operation: { notPaths: ['/p/*'] } }] }]);
  assert.deepStrictEqual(y.spec.http[0].match[0].uri, { prefix: '/q/' });
});

ok('前提: ci の values は「有効」だけを宣言している（有効の定義を 2 か所に書かない）', () => {
  const body = read(CI_VALUES).split('\n').filter((l) => l.trim() !== '' && !/^\s*#/.test(l));
  assert.deepStrictEqual(body, ['edge:', '  privateNotesSync:', '    enabled: true']);
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
const ON = mustRender(['-f', CI_VALUES]);
const OFF_LOCAL = mustRender(['-f', VALUES_LOCAL]);
const ON_LOCAL = mustRender(['-f', VALUES_LOCAL, '-f', CI_VALUES]);
const DEFAULTS = { app: 'document-service', gatewayNs: 'istio-system', releaseNs: 'microservices-platform' };

ok('既定（無効）: DocumentService を選ぶ AuthorizationPolicy が無く、同期の route も無い', () => {
  for (const [label, text] of [['既定', OFF], ['values-local', OFF_LOCAL]]) {
    assert.deepStrictEqual(policiesFor(text, 'document-service'), [], `${label} に方針が描かれた`);
    assert.ok(!text.includes('/private-notes/sync/'), `${label} に同期の経路が現れた`);
    assert.ok(!text.includes('-edge-sync-only'), `${label} に方針の名前が現れた`);
  }
});

ok('values-local（edge.enabled=false）: 同期を有効にしても方針は描かれない（route と同じ条件）', () => {
  assert.deepStrictEqual(policiesFor(ON_LOCAL, 'document-service'), []);
  assert.strictEqual(ON_LOCAL, OFF_LOCAL, 'edge 無効の構成で同期の knob が何かを描いた');
});

ok('既定（無効）: 評価すると陰性対照が赤になる（方針が無い＝エッジの非 sync も通る）', () => {
  const problems = enabledProblems(OFF, DEFAULTS);
  assert.ok(problems.some((p) => p.includes('/documents') && p.includes('期待 deny')), JSON.stringify(problems));
});

ok('有効: DocumentService を選ぶ DENY がちょうど 1 枚、from = gateway の Namespace、to = notPaths /private-notes/sync/*', () => {
  const [p, ...rest] = policiesFor(ON, 'document-service');
  assert.ok(p && rest.length === 0, '方針が 1 枚でない');
  assert.strictEqual(p.metadata.name, 'document-service-edge-sync-only');
  assert.strictEqual(p.metadata.namespace, 'microservices-platform');
  assert.strictEqual(p.apiVersion, 'security.istio.io/v1');
  assert.strictEqual(p.spec.action, 'DENY');
  assert.deepStrictEqual(p.spec.rules, [
    { from: [{ source: { namespaces: ['istio-system'] } }], to: [{ operation: { notPaths: SYNC_PATHS } }] },
  ]);
});

ok('有効: 方針の経路は route の前置 + "*"、選ぶ先は route の行き先と NetworkPolicy の podSelector、from は NetworkPolicy の穴', () => {
  const vs = docs(ON).find((d) => d.kind === 'VirtualService' && d.name === 'microservices-platform-edge').obj;
  const route = vs.spec.http.find((h) => h.route[0].destination.host === 'document-service');
  assert.ok(route, 'document-service への route が無い');
  assert.deepStrictEqual(route.match, [{ uri: { prefix: '/private-notes/sync/' } }]);
  assert.ok(!route.match[0].method, 'route がメソッドを絞っている（方針もそれに合わせて見直すこと）');
  const p = policiesFor(ON, 'document-service')[0];
  assert.deepStrictEqual(p.spec.rules[0].to[0].operation.notPaths, [`${route.match[0].uri.prefix}*`]);
  const np = docs(ON).find((d) => d.kind === 'NetworkPolicy' && d.name === 'allow-edge-ingress-to-document-service').obj;
  assert.strictEqual(np.spec.podSelector.matchLabels.app, p.spec.selector.matchLabels.app);
  assert.deepStrictEqual(
    [np.spec.ingress[0].from[0].namespaceSelector.matchLabels['kubernetes.io/metadata.name']],
    p.spec.rules[0].from[0].source.namespaces,
  );
});

ok('🔴 有効: 呼び出し元の一覧へ評価すると、エッジ × sync だけが通り、エッジ × それ以外は落ち、他は 1 本も切れない', () => {
  assert.deepStrictEqual(enabledProblems(ON, DEFAULTS), []);
});

ok('有効: 他のワークロードを選ぶ方針は増えない（BFF の方針は既定で描かれないまま）', () => {
  const all = docs(ON).filter((d) => d.kind === 'AuthorizationPolicy');
  assert.deepStrictEqual(all.map((d) => d.name), ['document-service-edge-sync-only']);
});

ok('knob の追随: edge.gateway.namespace / edge.privateNotesSync.service を変えると方針も同じ値へ動く', () => {
  const t = mustRender(['-f', CI_VALUES, '--set', 'edge.gateway.namespace=istio-ingress']);
  assert.deepStrictEqual(enabledProblems(t, { ...DEFAULTS, gatewayNs: 'istio-ingress' }), []);
  const u = mustRender(['-f', CI_VALUES, '--set', 'edge.privateNotesSync.service=docs-svc']);
  assert.deepStrictEqual(policiesFor(u, 'document-service'), []);
  assert.deepStrictEqual(enabledProblems(u, { ...DEFAULTS, app: 'docs-svc' }), []);
  assert.strictEqual(policiesFor(u, 'docs-svc')[0].metadata.name, 'docs-svc-edge-sync-only');
});

ok('edge.enabled=false: 同期の knob が立っていても方針は描かれない', () => {
  const t = mustRender(['-f', CI_VALUES, '--set', 'edge.enabled=false']);
  assert.deepStrictEqual(policiesFor(t, 'document-service'), []);
});

ok('mesh.mtlsMode=PERMISSIVE でも方針の形は同じ（平文は namespaces に当たらない＝巻き込まない）', () => {
  const t = mustRender(['-f', CI_VALUES, '--set', 'mesh.mtlsMode=PERMISSIVE']);
  assert.deepStrictEqual(enabledProblems(t, DEFAULTS), []);
});

function mutatedRender(transform) {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'helm-pns-authz-mut-'));
  try {
    const chartCopy = path.join(tmp, 'chart');
    fs.cpSync(path.join(REPO_ROOT, CHART), chartCopy, { recursive: true });
    const tpl = path.join(chartCopy, 'templates', 'edge.yaml');
    const original = fs.readFileSync(tpl, 'utf8');
    const mutated = transform(original);
    assert.notStrictEqual(mutated, original, '変異を当てられなかった（テンプレートの書き方が変わった）');
    fs.writeFileSync(tpl, mutated);
    return mustRender(['-f', path.join(REPO_ROOT, CI_VALUES)], chartCopy);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
}

ok('🔴 変異: 経路の制限（to: notPaths）を外すと、エッジ × sync が落ちて赤になる', () => {
  const text = mutatedRender((s) => s.replace(/\n {6}to:\n {8}- operation:\n {12}notPaths:\n {14}- \/private-notes\/sync\/\*/, ''));
  const problems = enabledProblems(text, DEFAULTS);
  assert.ok(problems.some((p) => p.includes('同期 manifest') && p.includes('期待 allow')), JSON.stringify(problems));
});

ok('🔴 変異: 経路を広げる（/private-notes/*）と、エッジ × 一覧・端末が通って赤になる', () => {
  const text = mutatedRender((s) => s.replace('- /private-notes/sync/*', '- /private-notes/*'));
  const problems = enabledProblems(text, DEFAULTS);
  assert.ok(problems.some((p) => p.includes('/private-notes/devices/') && p.includes('期待 deny')), JSON.stringify(problems));
});

ok('🔴 変異: from を名前空間の内側へ向けると、BFF が切れて赤になる', () => {
  const text = mutatedRender((s) => s.replace('{{ .Values.edge.gateway.namespace | quote }}', '{{ .Values.namespace.name | quote }}'));
  const problems = enabledProblems(text, DEFAULTS);
  assert.ok(problems.some((p) => p.startsWith('BFF') && p.includes('期待 allow')), JSON.stringify(problems));
});

ok('🔴 変異: DENY を ALLOW に変えると、名前空間の呼び出し元・AST・プローブが切れて赤になる', () => {
  const text = mutatedRender((s) => s.replace('action: DENY', 'action: ALLOW'));
  const problems = enabledProblems(text, DEFAULTS);
  assert.ok(problems.some((p) => p.includes('ALLOW が在る')), JSON.stringify(problems));
  assert.ok(problems.some((p) => p.startsWith('AST') && p.includes('期待 allow')), JSON.stringify(problems));
  assert.ok(problems.some((p) => p.startsWith('kubelet') && p.includes('期待 allow')), JSON.stringify(problems));
});

process.stdout.write(`\n✓ ${passed} tests passed\n`);
