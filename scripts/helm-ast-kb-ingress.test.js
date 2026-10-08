#!/usr/bin/env node
'use strict';
/*
 * helm-ast-kb-ingress.test.js
 * NFR-09, ADR-0125 決定 4, IADR-0513 (#1756・#1811):
 * **取引ユニット（AST）の名前空間 → KB の読み手（検索）・書き手（文書の保存）・LLM ゲートウェイ（報告書・取引判断の生成）への
 * ingress の許可を、実際に `helm template` で描いて固定する。**（ファイル名は #1756 の KB のまま。）
 *
 * 🔴 **守るのは「既定では 1 本も開けない」と「開けるときは最小の穴だけ」である。** NetworkPolicy は許可の和なので、
 *    字面の 1 か所の崩れ（from の AND が OR に割れる・ポートが抜ける）で名前空間全体や gRPC まで開く。だから本試験は字面だけでなく、
 *    **描いた NetworkPolicy 群を k8s の評価規則で呼び出し元の一覧へ当て**、結果（通す／落とす）で固定する。
 *
 * 固定するもの:
 *   1. 既定（本番像）・values-local: AST からの許可は 1 枚も描かれない。評価すると読み手・書き手・LLM ゲートウェイの呼び出し元は落ちる（陰性対照）。
 *   2. 有効（ci の values）: 3 枚がちょうどこの形 —— 行き先は retrieval-service / document-service / llmgateway-service、送り元は
 *      「AST の Namespace（kubernetes.io/metadata.name）かつ clients の Pod」（同じ from の要素＝AND）、ポートは REST の 8080 だけ。
 *   3. 呼び出し元の一覧への評価: 読み手 → 検索 8080・書き手 → 文書 8080・報告書 / 取引判断 → LLM ゲートウェイ 8080 だけが通り、
 *      gRPC・他の Pod・他の名前空間・他のサービスは落ち、
 *      名前空間の中の通信は変わらない。
 *   4. 片方だけの有効化・networkPolicy.enabled=false・knob の追随（namespace・clients・services.<target>.port）・描画で止まる条件
 *      （target は用途ごとに 1 つ〔読み手 retrieval・書き手 document・llmGateway llmgateway〕だけを受ける）。
 *   5. 変異: 一時複製したチャートで from の AND を OR に割る／ポートを外す／namespaceSelector を空にする／用途の一覧から llmGateway を外すと、
 *      本試験の判定が赤になる。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** helm-private-notes-sync-authz.test.js と同じ理由。CI は static-checks-units（azure/setup-helm 済み）で走らせる。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/helm-ast-kb-ingress.test.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const CI_VALUES = `${CHART}/ci/ast-kb-ingress-values.yaml`;
const VALUES_LOCAL = 'deploy/local/values-local.yaml';
const RELEASE_NS = 'microservices-platform';
const AST_NS = 'ai-stock-trading';

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
 * 描画物（ブロック形式の YAML）の最小パーサ。対応: マップ・リスト・スカラー・`{ k: v }` の flow マップ・`[a, b]` の flow リスト・注記行。
 * 本チャートの NetworkPolicy を読むのに足りる範囲だけを持つ（helm-private-notes-sync-authz.test.js と同型に、flow リストを足した）。
 */
function parseYaml(text) {
  const lines = text
    .split('\n')
    .filter((l) => l.trim() !== '' && !/^\s*#/.test(l) && l.trim() !== '---')
    .map((l) => ({ indent: l.length - l.trimStart().length, body: l.trim() }));
  let i = 0;

  function scalar(s) {
    const t = s.trim().replace(/\s+#.*$/, '');
    const flowMap = /^\{(.*)\}$/.exec(t);
    if (flowMap) {
      const m = {};
      for (const part of flowMap[1].split(',').map((p) => p.trim()).filter(Boolean)) {
        const at = part.indexOf(':');
        m[part.slice(0, at).trim()] = unquote(part.slice(at + 1));
      }
      return m;
    }
    const flowList = /^\[(.*)\]$/.exec(t);
    if (flowList) return flowList[1].split(',').map((p) => unquote(p)).filter((p) => p !== '');
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

/** 描画から NetworkPolicy だけを解析して返す（ConfigMap の literal block 等は最小パーサの対象外なので読まない）。 */
function networkPolicies(text) {
  return text
    .split(/^---\n/m)
    .filter((c) => /^kind:\s*NetworkPolicy\s*$/m.test(c))
    .map((c) => parseYaml(c));
}

const byName = (text, name) => networkPolicies(text).filter((p) => p.metadata.name === name);
const astAllows = (text) => networkPolicies(text).filter((p) => /^allow-ast-/.test(p.metadata.name));

// ---------------------------------------------------------------- k8s の NetworkPolicy の評価規則（本試験が使う部分集合）

/** LabelSelector の照合。`{}`（空）は全部に当たる。matchExpressions は In / NotIn / Exists / DoesNotExist。 */
function selectorMatches(sel, labels) {
  const s = sel || {};
  for (const k of Object.keys(s)) {
    assert.ok(['matchLabels', 'matchExpressions'].includes(k), `評価器が知らない selector の項目 ${k}`);
  }
  for (const [k, v] of Object.entries(s.matchLabels || {})) {
    if (labels[k] !== v) return false;
  }
  for (const e of s.matchExpressions || []) {
    const has = Object.prototype.hasOwnProperty.call(labels, e.key);
    switch (e.operator) {
      case 'In':
        if (!(has && e.values.includes(labels[e.key]))) return false;
        break;
      case 'NotIn':
        if (has && e.values.includes(labels[e.key])) return false;
        break;
      case 'Exists':
        if (!has) return false;
        break;
      case 'DoesNotExist':
        if (has) return false;
        break;
      default:
        assert.fail(`評価器が知らない operator ${e.operator}`);
    }
  }
  return true;
}

/** from の 1 要素（NetworkPolicyPeer）の照合。podSelector だけ＝ポリシーと同じ名前空間。両方＝AND。 */
function peerMatches(peer, policyNs, req) {
  for (const k of Object.keys(peer)) {
    assert.ok(['podSelector', 'namespaceSelector'].includes(k), `評価器が知らない peer の項目 ${k}（ipBlock 等。試験の評価器を広げること）`);
  }
  const nsLabels = { 'kubernetes.io/metadata.name': req.srcNs };
  const podLabels = req.srcApp ? { app: req.srcApp } : {};
  const nsOk = peer.namespaceSelector ? selectorMatches(peer.namespaceSelector, nsLabels) : req.srcNs === policyNs;
  const podOk = peer.podSelector ? selectorMatches(peer.podSelector, podLabels) : true;
  return nsOk && podOk;
}

function portMatches(ports, req) {
  if (!ports || ports.length === 0) return true;
  return ports.some((p) => (p.protocol || 'TCP') === 'TCP' && String(p.port) === String(req.port));
}

/**
 * 宛先 Pod（RELEASE_NS・app ラベル）への ingress の可否。Ingress を持つポリシーが 1 枚でも選べば隔離され、
 * それらのどれかの規則（from と ports）に当たれば通る（許可の和）。
 */
function evaluate(policies, req) {
  const selecting = policies.filter(
    (p) =>
      (p.metadata.namespace || RELEASE_NS) === RELEASE_NS &&
      (p.spec.policyTypes || ['Ingress']).includes('Ingress') &&
      selectorMatches(p.spec.podSelector, { app: req.dstApp }),
  );
  if (selecting.length === 0) return 'allow';
  const allowed = selecting.some((p) =>
    (p.spec.ingress || []).some(
      (rule) =>
        (!rule.from || rule.from.some((peer) => peerMatches(peer, RELEASE_NS, req))) && portMatches(rule.ports, req),
    ),
  );
  return allowed ? 'allow' : 'deny';
}

/** 呼び出し元の一覧（作業仕様書 §現状で引いた AST の呼び出しと、開けてはならない向き）。want は「両方を有効にしたとき」の期待。 */
function callers(astNs) {
  const ast = (srcApp) => ({ srcNs: astNs, srcApp });
  return [
    { who: 'AST 取引判断 → 検索 REST（KB の読み手）', ...ast('trade-decision-service'), dstApp: 'retrieval-service', port: 8080, want: 'allow', use: 'kbReader' },
    { who: 'AST 情報収集 → 文書 REST（KB の書き手）', ...ast('information-collection-service'), dstApp: 'document-service', port: 8080, want: 'allow', use: 'kbWriter' },
    { who: 'AST 報告書 → 文書 REST（KB の書き手）', ...ast('report-service'), dstApp: 'document-service', port: 8080, want: 'allow', use: 'kbWriter' },
    { who: '🔴 AST 取引判断 → 検索 gRPC 8081', ...ast('trade-decision-service'), dstApp: 'retrieval-service', port: 8081, want: 'deny' },
    { who: '🔴 AST 取引判断 → 文書 REST（読み手は書かない）', ...ast('trade-decision-service'), dstApp: 'document-service', port: 8080, want: 'deny' },
    { who: '🔴 AST 情報収集 → 文書 gRPC 8081', ...ast('information-collection-service'), dstApp: 'document-service', port: 8081, want: 'deny' },
    { who: '🔴 AST 情報収集 → 検索 REST（検索を配線していない）', ...ast('information-collection-service'), dstApp: 'retrieval-service', port: 8080, want: 'deny' },
    { who: '🔴 AST 報告書 → 検索 REST', ...ast('report-service'), dstApp: 'retrieval-service', port: 8080, want: 'deny' },
    { who: '🔴 AST 発注 → 文書 REST（clients に無い Pod）', ...ast('order-execution-service'), dstApp: 'document-service', port: 8080, want: 'deny' },
    { who: '🔴 AST 発注 → 検索 REST（clients に無い Pod）', ...ast('order-execution-service'), dstApp: 'retrieval-service', port: 8080, want: 'deny' },
    { who: '🔴 AST の app ラベルの無い Pod → 文書 REST', srcNs: astNs, srcApp: null, dstApp: 'document-service', port: 8080, want: 'deny' },
    { who: '🔴 AST 取引判断 → 認可サービス', ...ast('trade-decision-service'), dstApp: 'authorization-service', port: 8080, want: 'deny' },
    { who: '🔴 AST 取引判断 → BFF', ...ast('trade-decision-service'), dstApp: 'bff-service', port: 8080, want: 'deny' },
    // #1811: LLM ゲートウェイ。呼び出し元は AST の chart の LlmGateway__BaseUrl を持つ 2 つ（報告書・取引判断。作業仕様書 §現状）。
    { who: 'AST 報告書 → LLM ゲートウェイ REST', ...ast('report-service'), dstApp: 'llmgateway-service', port: 8080, want: 'allow', use: 'llmGateway' },
    { who: 'AST 取引判断 → LLM ゲートウェイ REST', ...ast('trade-decision-service'), dstApp: 'llmgateway-service', port: 8080, want: 'allow', use: 'llmGateway' },
    { who: '🔴 AST 報告書 → LLM ゲートウェイ gRPC 8081（AST は REST だけ）', ...ast('report-service'), dstApp: 'llmgateway-service', port: 8081, want: 'deny' },
    { who: '🔴 AST 取引判断 → LLM ゲートウェイ gRPC 8081', ...ast('trade-decision-service'), dstApp: 'llmgateway-service', port: 8081, want: 'deny' },
    { who: '🔴 AST 情報収集 → LLM ゲートウェイ REST（配線していない）', ...ast('information-collection-service'), dstApp: 'llmgateway-service', port: 8080, want: 'deny' },
    { who: '🔴 AST 通知 → LLM ゲートウェイ REST（clients に無い Pod）', ...ast('notification-service'), dstApp: 'llmgateway-service', port: 8080, want: 'deny' },
    { who: '🔴 AST 発注 → LLM ゲートウェイ REST（clients に無い Pod）', ...ast('order-execution-service'), dstApp: 'llmgateway-service', port: 8080, want: 'deny' },
    { who: '🔴 AST の app ラベルの無い Pod → LLM ゲートウェイ REST', srcNs: astNs, srcApp: null, dstApp: 'llmgateway-service', port: 8080, want: 'deny' },
    { who: '🔴 AST 報告書 → 認可サービス（LLM の主体の platform-service が通る東西端点）', ...ast('report-service'), dstApp: 'authorization-service', port: 8080, want: 'deny' },
    { who: '🔴 別の名前空間の同名ラベルの Pod → LLM ゲートウェイ REST', srcNs: 'some-other-ns', srcApp: 'report-service', dstApp: 'llmgateway-service', port: 8080, want: 'deny' },
    { who: '名前空間の中: RAG（検索）→ LLM ゲートウェイ REST（不変）', srcNs: RELEASE_NS, srcApp: 'retrieval-service', dstApp: 'llmgateway-service', port: 8080, want: 'allow' },
    { who: '🔴 別の名前空間の同名ラベルの Pod → 検索 REST', srcNs: 'some-other-ns', srcApp: 'trade-decision-service', dstApp: 'retrieval-service', port: 8080, want: 'deny' },
    { who: '🔴 別の名前空間の同名ラベルの Pod → 文書 REST', srcNs: 'some-other-ns', srcApp: 'report-service', dstApp: 'document-service', port: 8080, want: 'deny' },
    { who: '🔴 istio-system → 検索 REST', srcNs: 'istio-system', srcApp: 'istio-ingressgateway', dstApp: 'retrieval-service', port: 8080, want: 'deny' },
    { who: '名前空間の中: BFF → 検索 REST（不変）', srcNs: RELEASE_NS, srcApp: 'bff-service', dstApp: 'retrieval-service', port: 8080, want: 'allow' },
    { who: '名前空間の中: グラフ → 文書 gRPC（不変）', srcNs: RELEASE_NS, srcApp: 'graph-service', dstApp: 'document-service', port: 8081, want: 'allow' },
    { who: '名前空間の中: MCP → 文書 REST（不変）', srcNs: RELEASE_NS, srcApp: 'mcp-server', dstApp: 'document-service', port: 8080, want: 'allow' },
  ];
}

/** 描画に対する判定。enabled は有効にした用途の集合（無効の用途の allow は deny を期待する）。戻り値は問題の一覧。空なら緑。 */
function problemsFor(text, { astNs = AST_NS, enabled = ['kbReader', 'kbWriter', 'llmGateway'] } = {}) {
  const policies = networkPolicies(text);
  const problems = [];
  for (const c of callers(astNs)) {
    const want = c.use && !enabled.includes(c.use) ? 'deny' : c.want;
    const got = evaluate(policies, c);
    if (got !== want) problems.push(`${c.who}（${c.srcNs}/${c.srcApp} → ${c.dstApp}:${c.port}）が ${got}（期待 ${want}）`);
  }
  return problems;
}

/** 有効時の 1 枚の期待形（knob を変えた描画でも同じ関数で作る）。 */
function expectedPolicy({ use, target, clients, astNs = AST_NS, port = 8080 }) {
  return {
    apiVersion: 'networking.k8s.io/v1',
    kind: 'NetworkPolicy',
    metadata: { name: `allow-ast-${use}-ingress`, namespace: RELEASE_NS, labels: { 'app.kubernetes.io/part-of': 'microservices-platform' } },
    spec: {
      podSelector: { matchLabels: { app: `${target}-service` } },
      policyTypes: ['Ingress'],
      ingress: [
        {
          from: [
            {
              namespaceSelector: { matchLabels: { 'kubernetes.io/metadata.name': astNs } },
              podSelector: { matchExpressions: [{ key: 'app', operator: 'In', values: clients }] },
            },
          ],
          ports: [{ protocol: 'TCP', port: String(port) }],
        },
      ],
    },
  };
}

const READER = { use: 'kb-reader', target: 'retrieval', clients: ['trade-decision-service'] };
const WRITER = { use: 'kb-writer', target: 'document', clients: ['information-collection-service', 'report-service'] };
const LLM = { use: 'llm-gateway', target: 'llmgateway', clients: ['report-service', 'trade-decision-service'] };
const ALL_NAMES = ['allow-ast-kb-reader-ingress', 'allow-ast-kb-writer-ingress', 'allow-ast-llm-gateway-ingress'];

// ---------------------------------------------------------------- 静的（helm 不要）

ok('評価器の自己試験: selector（空・matchLabels・In・NotIn・ラベル無し）', () => {
  assert.ok(selectorMatches({}, { app: 'x' }));
  assert.ok(selectorMatches({}, {}));
  assert.ok(selectorMatches({ matchLabels: { app: 'x' } }, { app: 'x' }));
  assert.ok(!selectorMatches({ matchLabels: { app: 'x' } }, { app: 'y' }));
  assert.ok(selectorMatches({ matchExpressions: [{ key: 'app', operator: 'In', values: ['a', 'b'] }] }, { app: 'b' }));
  assert.ok(!selectorMatches({ matchExpressions: [{ key: 'app', operator: 'In', values: ['a'] }] }, {}), 'In はラベル無しに当たらない');
  assert.ok(selectorMatches({ matchExpressions: [{ key: 'app', operator: 'NotIn', values: ['a'] }] }, {}), 'NotIn はラベル無しに当たる');
  assert.throws(() => selectorMatches({ matchFields: [] }, {}));
});

ok('評価器の自己試験: peer の AND と OR・ポリシーと同じ名前空間・ポート・許可の和', () => {
  const and = { namespaceSelector: { matchLabels: { 'kubernetes.io/metadata.name': 'a' } }, podSelector: { matchLabels: { app: 'p' } } };
  assert.ok(peerMatches(and, 'home', { srcNs: 'a', srcApp: 'p' }));
  assert.ok(!peerMatches(and, 'home', { srcNs: 'a', srcApp: 'q' }));
  assert.ok(!peerMatches(and, 'home', { srcNs: 'b', srcApp: 'p' }));
  assert.ok(peerMatches({ podSelector: { matchLabels: { app: 'p' } } }, 'home', { srcNs: 'home', srcApp: 'p' }));
  assert.ok(!peerMatches({ podSelector: { matchLabels: { app: 'p' } } }, 'home', { srcNs: 'a', srcApp: 'p' }), 'podSelector だけはポリシーの名前空間');
  assert.ok(peerMatches({ namespaceSelector: {} }, 'home', { srcNs: 'z', srcApp: null }), '空の namespaceSelector は全名前空間');
  assert.throws(() => peerMatches({ ipBlock: { cidr: '0.0.0.0/0' } }, 'home', { srcNs: 'a', srcApp: 'p' }));
  assert.ok(portMatches(undefined, { port: 1 }));
  assert.ok(!portMatches([{ protocol: 'TCP', port: '8080' }], { port: 8081 }));
  const deny = { metadata: { name: 'd' }, spec: { podSelector: {}, policyTypes: ['Ingress'] } };
  const allow = { metadata: { name: 'a' }, spec: { podSelector: { matchLabels: { app: 't' } }, policyTypes: ['Ingress'], ingress: [{ from: [and], ports: [{ port: '80' }] }] } };
  assert.strictEqual(evaluate([], { srcNs: 'x', srcApp: 'y', dstApp: 't', port: 80 }), 'allow', 'どのポリシーにも選ばれない Pod は隔離されない');
  assert.strictEqual(evaluate([deny], { srcNs: 'a', srcApp: 'p', dstApp: 't', port: 80 }), 'deny');
  assert.strictEqual(evaluate([deny, allow], { srcNs: 'a', srcApp: 'p', dstApp: 't', port: 80 }), 'allow');
  assert.strictEqual(evaluate([deny, allow], { srcNs: 'a', srcApp: 'p', dstApp: 'u', port: 80 }), 'deny');
});

ok('評価器の自己試験: 最小パーサが flow リスト・matchExpressions のブロック形式を読む', () => {
  const y = parseYaml(
    'kind: NetworkPolicy\nspec:\n  podSelector:\n    matchExpressions:\n      - key: app\n        operator: NotIn\n        values: [seaweedfs]\n  ingress:\n    - from:\n        - namespaceSelector:\n            matchLabels:\n              k: v\n          podSelector:\n            matchExpressions:\n              - key: app\n                operator: In\n                values:\n                  - a\n                  - b\n      ports:\n        - protocol: TCP\n          port: 8080\n',
  );
  assert.deepStrictEqual(y.spec.podSelector.matchExpressions, [{ key: 'app', operator: 'NotIn', values: ['seaweedfs'] }]);
  assert.deepStrictEqual(y.spec.ingress[0].from[0].podSelector.matchExpressions[0].values, ['a', 'b']);
  assert.deepStrictEqual(y.spec.ingress[0].from[0].namespaceSelector, { matchLabels: { k: 'v' } });
  assert.deepStrictEqual(y.spec.ingress[0].ports, [{ protocol: 'TCP', port: '8080' }]);
});

ok('前提: ci の values は「3 用途を有効」だけを宣言している（有効の定義を 2 か所に書かない）', () => {
  const body = read(CI_VALUES).split('\n').filter((l) => l.trim() !== '' && !/^\s*#/.test(l));
  assert.deepStrictEqual(body, [
    'networkPolicy:', '  fromAst:',
    '    kbReader:', '      enabled: true',
    '    kbWriter:', '      enabled: true',
    '    llmGateway:', '      enabled: true',
  ]);
});

ok('前提: values.yaml の既定は 3 用途とも閉じている（enabled: false）', () => {
  const v = read(`${CHART}/values.yaml`);
  const block = /^networkPolicy:\n((?:[ ]{2}.*\n|\s*\n)*)/m.exec(v);
  assert.ok(block, 'networkPolicy のブロックが無い');
  assert.match(block[1], /\n {4}kbReader:\n {6}enabled: false\n/);
  assert.match(block[1], /\n {4}kbWriter:\n {6}enabled: false\n/);
  assert.match(block[1], /\n {4}llmGateway:\n {6}enabled: false\n {6}target: llmgateway\n/);
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

ok('🔴 陰性対照（既定＝本番像）: AST からの許可は 1 枚も描かれず、どの NetworkPolicy も AST の名前空間を名指ししない', () => {
  assert.deepStrictEqual(astAllows(OFF), []);
  assert.ok(!OFF.includes(`kubernetes.io/metadata.name: ${AST_NS}`), '既定の描画に AST の名前空間を選ぶ selector が現れた');
  assert.ok(networkPolicies(OFF).some((p) => p.metadata.name === 'default-deny-ingress'), '既定拒否が描かれていない（前提が崩れた）');
});

ok('🔴 陰性対照（既定）: 評価すると読み手・書き手の 3 本と LLM ゲートウェイの 2 本が落ち、ほかは期待どおり', () => {
  const problems = problemsFor(OFF);
  assert.strictEqual(problems.length, 5, JSON.stringify(problems));
  for (const who of [
    'AST 取引判断 → 検索 REST',
    'AST 情報収集 → 文書 REST',
    'AST 報告書 → 文書 REST',
    'AST 報告書 → LLM ゲートウェイ REST',
    'AST 取引判断 → LLM ゲートウェイ REST',
  ]) {
    assert.ok(problems.some((p) => p.startsWith(who) && p.includes('期待 allow')), `${who} が落ちていない: ${JSON.stringify(problems)}`);
  }
  assert.deepStrictEqual(problemsFor(OFF, { enabled: [] }), [], '既定は「何も開けない」と同じ判定');
});

ok('values-local（networkPolicy.enabled=false）: 既定でも有効にしても何も描かない（描画がバイト等価）', () => {
  assert.deepStrictEqual(astAllows(OFF_LOCAL), []);
  assert.strictEqual(ON_LOCAL, OFF_LOCAL, 'NetworkPolicy を無効にした構成で fromAst の knob が何かを描いた');
});

ok('有効: 3 枚がちょうどこの形（行き先・from の AND・ポート 8080 だけ）', () => {
  assert.deepStrictEqual(astAllows(ON).map((p) => p.metadata.name), ALL_NAMES);
  assert.deepStrictEqual(byName(ON, 'allow-ast-kb-reader-ingress')[0], expectedPolicy(READER));
  assert.deepStrictEqual(byName(ON, 'allow-ast-kb-writer-ingress')[0], expectedPolicy(WRITER));
  assert.deepStrictEqual(byName(ON, 'allow-ast-llm-gateway-ingress')[0], expectedPolicy(LLM));
});

ok('有効: ポートは Service の REST の口と同じ値で、gRPC の口ではない', () => {
  const svc = (name) => {
    const c = ON.split(/^---\n/m).find((d) => /^kind:\s*Service\s*$/m.test(d) && new RegExp(`^  name: ${name}$`, 'm').test(d));
    assert.ok(c, `Service ${name} が無い`);
    return parseYaml(c).spec.ports;
  };
  for (const [policy, service] of [
    ['allow-ast-kb-reader-ingress', 'retrieval-service'],
    ['allow-ast-kb-writer-ingress', 'document-service'],
    ['allow-ast-llm-gateway-ingress', 'llmgateway-service'],
  ]) {
    const ports = svc(service);
    const http = ports.find((p) => p.name === 'http') || ports[0];
    const grpc = ports.find((p) => p.name === 'grpc');
    assert.ok(grpc, `${service} に gRPC の口が無い（前提が崩れた）`);
    const opened = byName(ON, policy)[0].spec.ingress[0].ports.map((p) => p.port);
    assert.deepStrictEqual(opened, [http.port]);
    assert.ok(!opened.includes(grpc.port));
  }
});

ok('🔴 有効: 呼び出し元の一覧へ評価すると、読み手 → 検索 REST・書き手 → 文書 REST・報告書 / 取引判断 → LLM REST だけが新たに通り、他は 1 本も開かない', () => {
  assert.deepStrictEqual(problemsFor(ON), []);
});

ok('有効: 既定に対して増えるのは 3 枚の NetworkPolicy だけ（他の資源・既存の NetworkPolicy は不変）', () => {
  // 資源ごとに末尾の空白を落として比べる（helm 4 は末尾の `---` の前に空行を残し、全文の比較だと版で結果が変わる）。
  const docs = (t) => t.split(/^---\n/m).map((c) => c.trimEnd());
  assert.deepStrictEqual(docs(ON).filter((c) => !/^  name: allow-ast-(kb-reader|kb-writer|llm-gateway)-ingress$/m.test(c)), docs(OFF));
});

ok('1 用途だけの有効化: 読み手だけ・書き手だけ・LLM ゲートウェイだけはその 1 枚だけを描き、ほかは閉じたまま', () => {
  const r = mustRender(['--set', 'networkPolicy.fromAst.kbReader.enabled=true']);
  assert.deepStrictEqual(astAllows(r).map((p) => p.metadata.name), ['allow-ast-kb-reader-ingress']);
  assert.deepStrictEqual(problemsFor(r, { enabled: ['kbReader'] }), []);
  const w = mustRender(['--set', 'networkPolicy.fromAst.kbWriter.enabled=true']);
  assert.deepStrictEqual(astAllows(w).map((p) => p.metadata.name), ['allow-ast-kb-writer-ingress']);
  assert.deepStrictEqual(problemsFor(w, { enabled: ['kbWriter'] }), []);
  const l = mustRender(['--set', 'networkPolicy.fromAst.llmGateway.enabled=true']);
  assert.deepStrictEqual(astAllows(l).map((p) => p.metadata.name), ['allow-ast-llm-gateway-ingress']);
  assert.deepStrictEqual(problemsFor(l, { enabled: ['llmGateway'] }), []);
});

ok('networkPolicy.enabled=false: 有効の values でも NetworkPolicy は 1 枚も描かれない', () => {
  const t = mustRender(['-f', CI_VALUES, '--set', 'networkPolicy.enabled=false']);
  assert.deepStrictEqual(networkPolicies(t), []);
});

ok('knob の追随: namespace・clients・services.<target>.port を変えると穴も同じ値へ動く', () => {
  const t = mustRender(['-f', CI_VALUES, '--set', 'networkPolicy.fromAst.namespace=ast-prod']);
  assert.deepStrictEqual(byName(t, 'allow-ast-kb-reader-ingress')[0], expectedPolicy({ ...READER, astNs: 'ast-prod' }));
  assert.deepStrictEqual(problemsFor(t, { astNs: 'ast-prod' }), []);
  assert.ok(problemsFor(t).some((p) => p.startsWith('AST 取引判断 → 検索 REST')), '元の名前空間が通ったまま');
  const u = mustRender(['-f', CI_VALUES, '--set', 'networkPolicy.fromAst.kbWriter.clients={report-service}']);
  assert.deepStrictEqual(byName(u, 'allow-ast-kb-writer-ingress')[0], expectedPolicy({ ...WRITER, clients: ['report-service'] }));
  const v = mustRender(['-f', CI_VALUES, '--set', 'services.retrieval.port=9090']);
  assert.deepStrictEqual(byName(v, 'allow-ast-kb-reader-ingress')[0], expectedPolicy({ ...READER, port: 9090 }));
  const x = mustRender(['-f', CI_VALUES, '--set', 'networkPolicy.fromAst.llmGateway.clients={trade-decision-service}']);
  assert.deepStrictEqual(byName(x, 'allow-ast-llm-gateway-ingress')[0], expectedPolicy({ ...LLM, clients: ['trade-decision-service'] }));
  assert.ok(problemsFor(x).some((p) => p.startsWith('AST 報告書 → LLM ゲートウェイ REST')), 'clients から外した報告書が通ったまま');
  const y = mustRender(['-f', CI_VALUES, '--set', 'services.llmgateway.port=9091']);
  assert.deepStrictEqual(byName(y, 'allow-ast-llm-gateway-ingress')[0], expectedPolicy({ ...LLM, port: 9091 }));
});

ok('🔴 描画で止まる: clients が空・namespace が空・target が無効・target が用途の行き先と違う', () => {
  const cases = [
    [['--set', 'networkPolicy.fromAst.kbReader.clients=null'], /kbReader\.clients が空/],
    [['--set', 'networkPolicy.fromAst.kbWriter.clients={}'], /kbWriter\.clients に空の要素がある/],
    [['--set', 'networkPolicy.fromAst.namespace='], /fromAst\.namespace が空/],
    [['--set', 'services.retrieval.enabled=false'], /kbReader\.target="retrieval" のサービスが無効/],
    [['--set', 'networkPolicy.fromAst.kbWriter.target=nope'], /kbWriter\.target は "document" だけを受ける（"nope"）/],
    [['--set', 'networkPolicy.fromAst.kbReader.target=bff'], /kbReader\.target は "retrieval" だけを受ける（"bff"）/],
    [['--set', 'networkPolicy.fromAst.kbWriter.target=retrieval'], /kbWriter\.target は "document" だけを受ける（"retrieval"）/],
    [['--set', 'networkPolicy.fromAst.llmGateway.clients=null'], /llmGateway\.clients が空/],
    [['--set', 'networkPolicy.fromAst.llmGateway.clients={}'], /llmGateway\.clients に空の要素がある/],
    [['--set', 'services.llmgateway.enabled=false'], /llmGateway\.target="llmgateway" のサービスが無効/],
    [['--set', 'networkPolicy.fromAst.llmGateway.target=document'], /llmGateway\.target は "llmgateway" だけを受ける（"document"）/],
    [['--set', 'networkPolicy.fromAst.llmGateway.target=authorization'], /llmGateway\.target は "llmgateway" だけを受ける（"authorization"）/],
    [['--set', 'networkPolicy.fromAst.llmGateway.target='], /llmGateway\.target は "llmgateway" だけを受ける（""）/],
    [['--set', 'networkPolicy.fromAst.kbReader.target=llmgateway'], /kbReader\.target は "retrieval" だけを受ける（"llmgateway"）/],
  ];
  for (const [args, re] of cases) {
    const r = helmTemplate(['-f', CI_VALUES, ...args]);
    assert.notStrictEqual(r.status, 0, `${args.join(' ')} で描画が通った`);
    assert.match(r.err, re, `${args.join(' ')} の失敗理由が違う: ${r.err}`);
  }
  assert.strictEqual(helmTemplate(['--set', 'networkPolicy.fromAst.kbReader.clients=null']).status, 0, '無効のままなら値が欠けても止めない');
  assert.strictEqual(helmTemplate(['--set', 'networkPolicy.fromAst.llmGateway.target=document']).status, 0, '無効のままなら target が違っても止めない');
});

function mutatedRender(transform) {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'helm-ast-kb-ingress-mut-'));
  try {
    const chartCopy = path.join(tmp, 'chart');
    fs.cpSync(path.join(REPO_ROOT, CHART), chartCopy, { recursive: true });
    const tpl = path.join(chartCopy, 'templates', 'networkpolicy.yaml');
    const original = fs.readFileSync(tpl, 'utf8');
    const mutated = transform(original);
    assert.notStrictEqual(mutated, original, '変異を当てられなかった（テンプレートの書き方が変わった）');
    fs.writeFileSync(tpl, mutated);
    return mustRender(['-f', path.join(REPO_ROOT, CI_VALUES)], chartCopy);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
}

ok('🔴 変異: from の AND を OR に割る（podSelector を別の要素へ）と、AST の他の Pod が通って赤になる', () => {
  const text = mutatedRender((s) =>
    s.replace(/(kubernetes\.io\/metadata\.name: \{\{ \$fromAst\.namespace \}\}\n)( {10})podSelector:/, '$1        - podSelector:'),
  );
  const problems = problemsFor(text);
  assert.ok(problems.some((p) => p.startsWith('🔴 AST 発注 → 文書 REST') && p.includes('期待 deny')), JSON.stringify(problems));
  assert.ok(problems.some((p) => p.startsWith('🔴 AST 発注 → LLM ゲートウェイ REST') && p.includes('期待 deny')), JSON.stringify(problems));
});

ok('🔴 変異: ports を外すと、gRPC の口が通って赤になる', () => {
  const text = mutatedRender((s) => s.replace(/\n {6}ports:\n {8}- protocol: TCP\n {10}port: \{\{ \$svc\.port \}\}/, ''));
  const problems = problemsFor(text);
  assert.ok(problems.some((p) => p.includes('検索 gRPC 8081') && p.includes('期待 deny')), JSON.stringify(problems));
  assert.ok(problems.some((p) => p.includes('LLM ゲートウェイ gRPC 8081') && p.includes('期待 deny')), JSON.stringify(problems));
});

ok('🔴 変異: namespaceSelector を空にすると、別の名前空間の同名ラベルの Pod が通って赤になる', () => {
  const text = mutatedRender((s) =>
    s.replace(/namespaceSelector:\n {12}matchLabels:\n {14}kubernetes\.io\/metadata\.name: \{\{ \$fromAst\.namespace \}\}/, 'namespaceSelector: {}'),
  );
  const problems = problemsFor(text);
  assert.ok(problems.some((p) => p.startsWith('🔴 別の名前空間の同名ラベルの Pod') && p.includes('期待 deny')), JSON.stringify(problems));
});

ok('🔴 変異: 用途の一覧から llmGateway を外す（有効でも黙って描かない）と、LLM ゲートウェイの呼び出し元が落ちて赤になる', () => {
  const text = mutatedRender((s) => s.replace('list "kbReader" "kbWriter" "llmGateway"', 'list "kbReader" "kbWriter"'));
  const problems = problemsFor(text);
  assert.deepStrictEqual(
    problems.map((p) => p.split('（')[0]).sort(),
    ['AST 取引判断 → LLM ゲートウェイ REST', 'AST 報告書 → LLM ゲートウェイ REST'],
    JSON.stringify(problems),
  );
});

ok('🔴 変異: llmGateway の行き先の固定を外す（target を検査しない）と、別の行き先へ向けた値が描画を通って赤になる', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'helm-ast-kb-ingress-mut-'));
  try {
    const chartCopy = path.join(tmp, 'chart');
    fs.cpSync(path.join(REPO_ROOT, CHART), chartCopy, { recursive: true });
    const tpl = path.join(chartCopy, 'templates', 'networkpolicy.yaml');
    const original = fs.readFileSync(tpl, 'utf8');
    const mutated = original.replace('"llmGateway" "llmgateway"', '"llmGateway" "authorization"');
    assert.notStrictEqual(mutated, original, '変異を当てられなかった（テンプレートの書き方が変わった）');
    fs.writeFileSync(tpl, mutated);
    // 変異後のチャートは target=authorization を受けてしまう。本試験の期待（"llmgateway" だけを受ける）がこの描画を拒めば赤。
    const r = helmTemplate(['-f', path.join(REPO_ROOT, CI_VALUES), '--set', 'networkPolicy.fromAst.llmGateway.target=authorization'], chartCopy);
    assert.strictEqual(r.status, 0, `変異後のチャートで描画が止まった（変異が効いていない）: ${r.err}`);
    const problems = problemsFor(r.out);
    assert.ok(problems.some((p) => p.startsWith('🔴 AST 報告書 → 認可サービス') && p.includes('期待 deny')), JSON.stringify(problems));
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

process.stdout.write(`\n✓ ${passed} tests passed\n`);
