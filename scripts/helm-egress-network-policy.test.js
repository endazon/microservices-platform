#!/usr/bin/env node
'use strict';
/*
 * helm-egress-network-policy.test.js
 * NFR-18, IADR-0589（Refs GHSA-j4qv-2w99-xg5p）:
 * **外向き通信（egress）の既定拒否と許可の一覧を、実際に `helm template` で描いて固定する。**
 *
 * 🔴 NetworkPolicy は許可の和であり、Pod は「Egress を持つポリシーに 1 枚でも選ばれたとき」だけ隔離される。字面の 1 か所の崩れ
 *    （宛先の AND が OR に割れる・ports が抜ける・DNS の podSelector が抜ける）で名前空間全体や全ポートへ開く。だから本試験は字面だけでなく、
 *    **描いた NetworkPolicy 群を k8s の評価規則で通信の一覧へ当て**、結果（通す／落とす）で固定する。
 *
 * 固定するもの:
 *   1. 既定（本番像・networkPolicy.enabled）: 名前空間の全 Pod に既定拒否・DNS は kube-system の kube-dns の 53 番だけ・同じ名前空間・
 *      istiod（mesh.enabled）。allow-intra-namespace は Ingress だけ。SeaweedFS の DNS も kube-dns だけ。別の名前空間・クラスタの外は落ちる。
 *   2. values-local（経路B・networkPolicy.enabled=false）: 段 1 の workloads にだけ既定拒否が当たる。**描画される全ての Pod は
 *      workloads か段 2（STAGE2）のどちらかに分類されている**（新しい Pod が黙って制限の外に残らない）。workloads は描画される Pod か
 *      確かめ用の egress-probe だけ。
 *   3. values-local の宛先が宣言とそろう: postgres の送り元 = DB を持つ段 1 の Deployment（ConnectionStrings__DefaultConnection）、
 *      rabbitmq = ブローカへ繋ぐもの（RabbitMq__ConnectionString）、keycloak / otel-collector = 段 1 の .NET の全サービス（Otlp__Endpoint）、
 *      qdrant = appsettings に Qdrant を持つサービス。宛先の名前空間・ラベル・ポートは deploy/local/infra の実体と一致する。
 *   4. 通信の一覧への評価（values-local・メッシュの有無）: 許す宛先だけが通り、他（Valkey・API サーバ・クラスタの外・別の宛先のポート）は落ちる。
 *      段 2 の Pod（bff 等）は隔離されない（本変更で振る舞いを変えない）。
 *   5. 描画で止まる条件と、両方 false なら描かないこと。
 *   6. 変異: DNS の podSelector を外す・宛先の AND を OR に割る・ports を外す・workloads に無い送り元の検査を外すと赤になる。
 *
 * 稼働のクラスタでの強制（k3s の内蔵のコントローラ）は integration-stack.yml の「外向き通信の既定拒否」の門が測る。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** CI は static-checks-units で走らせる。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/helm-egress-network-policy.test.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const VALUES_LOCAL = 'deploy/local/values-local.yaml';
const INFRA_DIR = 'deploy/local/infra';
const RELEASE_NS = 'microservices-platform';
const INFRA_NS = 'platform-infra';
const PROBE = 'egress-probe';

/**
 * 段 2（作業仕様書 20261022_j4qv_egress-default-deny §段の分け方）。values-local で既定拒否をまだ当てない Pod。
 * 🔴 段 2 で workloads へ移したら、ここから外す（両方に在ると下の分類の試験が落ちる）。
 */
const STAGE2 = ['bff-service', 'llmgateway-service', 'datasource-service', 'wiki-js', 'config-drift-postsync'];

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

/** 描画物（ブロック形式の YAML）の最小パーサ。helm-ast-kb-ingress.test.js と同じ範囲（NetworkPolicy を読むのに足りる分）。 */
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

const chunks = (text) => text.split(/^---\n/m);

/** 描画から NetworkPolicy だけを解析して返す。 */
function networkPolicies(text) {
  return chunks(text)
    .filter((c) => /^kind:\s*NetworkPolicy\s*$/m.test(c))
    .map((c) => parseYaml(c));
}

const byName = (text, name) => networkPolicies(text).filter((p) => p.metadata.name === name);
const egressPolicies = (text) => networkPolicies(text).filter((p) => (p.spec.policyTypes || []).includes('Egress'));

/**
 * 描画される Pod（Deployment / Job の Pod テンプレートの app ラベル）と、その Pod の env 名の集合。
 * env の名前だけを字面で拾う（Deployment 全体は最小パーサの対象外）。
 */
function workloads(text) {
  const out = new Map();
  for (const c of chunks(text)) {
    if (!/^kind:\s*(Deployment|Job|CronJob|StatefulSet|DaemonSet)\s*$/m.test(c)) continue;
    const tpl = c.slice(c.indexOf('  template:'));
    const app = (/\n\s+labels:\n(?:\s+[\w./-]+: .*\n)*?\s+app: ([\w.-]+)/.exec(tpl) || [])[1];
    assert.ok(app, `Pod テンプレートに app ラベルが無い: ${c.slice(0, 200)}`);
    const env = new Set([...c.matchAll(/^\s+- name: ([A-Za-z_][\w]*)\s*$/gm)].map((m) => m[1]));
    out.set(app, env);
  }
  return out;
}

// ---------------------------------------------------------------- k8s の NetworkPolicy（egress）の評価規則（本試験が使う部分集合）

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

function ipToInt(ip) {
  return ip.split('.').reduce((a, o) => (a << 8) + Number(o), 0) >>> 0;
}
function cidrContains(cidr, ip) {
  const [base, bits] = cidr.split('/');
  const mask = Number(bits) === 0 ? 0 : (~0 << (32 - Number(bits))) >>> 0;
  return (ipToInt(base) & mask) === (ipToInt(ip) & mask);
}

/**
 * to の 1 要素（NetworkPolicyPeer）の照合。宛先は Pod（dstNs・dstLabels）かクラスタの外（ip）。
 * podSelector だけ＝ポリシーと同じ名前空間の Pod。namespaceSelector と podSelector の両方＝AND。ipBlock はクラスタの外の IP だけに当てる。
 */
function peerMatches(peer, policyNs, req) {
  for (const k of Object.keys(peer)) {
    assert.ok(['podSelector', 'namespaceSelector', 'ipBlock'].includes(k), `評価器が知らない peer の項目 ${k}`);
  }
  if (peer.ipBlock) {
    if (!req.ip) return false;
    return cidrContains(peer.ipBlock.cidr, req.ip) && !(peer.ipBlock.except || []).some((e) => cidrContains(e, req.ip));
  }
  if (!req.dstNs) return false; // クラスタの外の IP は Pod の selector に当たらない。
  const nsOk = peer.namespaceSelector
    ? selectorMatches(peer.namespaceSelector, { 'kubernetes.io/metadata.name': req.dstNs })
    : req.dstNs === policyNs;
  const podOk = peer.podSelector ? selectorMatches(peer.podSelector, req.dstLabels || {}) : true;
  return nsOk && podOk;
}

function portMatches(ports, req) {
  if (!ports || ports.length === 0) return true;
  return ports.some((p) => (p.protocol || 'TCP') === (req.proto || 'TCP') && String(p.port) === String(req.port));
}

/** RELEASE_NS の送り元 Pod（app ラベル）からの egress の可否。Egress を持つポリシーが 1 枚でも選べば隔離され、どれかの規則に当たれば通る。 */
function evaluate(policies, req) {
  const srcLabels = req.srcApp ? { app: req.srcApp } : {};
  const selecting = policies.filter(
    (p) =>
      (p.metadata.namespace || RELEASE_NS) === RELEASE_NS &&
      (p.spec.policyTypes || ['Ingress']).includes('Egress') &&
      selectorMatches(p.spec.podSelector, srcLabels),
  );
  if (selecting.length === 0) return 'allow';
  const allowed = selecting.some((p) =>
    (p.spec.egress || []).some(
      (rule) => (!rule.to || rule.to.some((peer) => peerMatches(peer, RELEASE_NS, req))) && portMatches(rule.ports, req),
    ),
  );
  return allowed ? 'allow' : 'deny';
}

// 宛先の型（通信の一覧で使う）。
const KUBE_DNS = (proto = 'UDP') => ({ dstNs: 'kube-system', dstLabels: { 'k8s-app': 'kube-dns' }, port: 53, proto });
const OTHER_53 = { dstNs: 'kube-system', dstLabels: { app: 'not-dns' }, port: 53, proto: 'UDP' };
const INFRA = (app, port) => ({ dstNs: INFRA_NS, dstLabels: { app }, port });
const SAME_NS = (app, port) => ({ dstNs: RELEASE_NS, dstLabels: { app }, port });
const ISTIOD = { dstNs: 'istio-system', dstLabels: { app: 'istiod' }, port: 15012 };
const AST = { dstNs: 'ai-stock-trading', dstLabels: { app: 'configuration-service' }, port: 8080 };
const EXTERNAL = { dstNs: null, ip: '203.0.113.10', port: 443 };
const APISERVER = { dstNs: null, ip: '10.43.0.1', port: 443 };

function problemsFor(text, cases) {
  const policies = networkPolicies(text);
  const problems = [];
  for (const c of cases) {
    const got = evaluate(policies, c);
    if (got !== c.want) problems.push(`${c.who} が ${got}（期待 ${c.want}）`);
  }
  return problems;
}

/** 本番像（名前空間の全 Pod に当たる）の通信の一覧。mesh は istiod の可否。 */
function prodCases(apps, { mesh = true } = {}) {
  const cases = [];
  for (const a of apps) {
    cases.push({ who: `${a} → kube-dns UDP 53`, srcApp: a, ...KUBE_DNS('UDP'), want: 'allow' });
    cases.push({ who: `${a} → kube-dns TCP 53`, srcApp: a, ...KUBE_DNS('TCP'), want: 'allow' });
    cases.push({ who: `🔴 ${a} → kube-system の別の Pod の 53`, srcApp: a, ...OTHER_53, want: 'deny' });
    cases.push({ who: `${a} → 同じ名前空間`, srcApp: a, ...SAME_NS('document-service', 8080), want: 'allow' });
    cases.push({ who: `${a} → istiod`, srcApp: a, ...ISTIOD, want: mesh ? 'allow' : 'deny' });
    cases.push({ who: `🔴 ${a} → 別の名前空間（基盤）`, srcApp: a, ...INFRA('postgres', 5432), want: 'deny' });
    cases.push({ who: `🔴 ${a} → クラスタの外`, srcApp: a, ...EXTERNAL, want: 'deny' });
  }
  cases.push({ who: '🔴 app ラベルの無い Pod → クラスタの外', srcApp: null, ...EXTERNAL, want: 'deny' });
  return cases;
}

const DB_ENV = 'ConnectionStrings__DefaultConnection';
const MQ_ENV = 'RabbitMq__ConnectionString';
const OTLP_ENV = 'Otlp__Endpoint';

/** appsettings に Qdrant の接続を持つサービス（app ラベルへ写す: RetrievalService → retrieval-service）。 */
function qdrantUsers() {
  const roots = ['src/knowledge/backend/Services', 'src/platform/backend/Services'];
  const users = [];
  for (const r of roots) {
    for (const dir of fs.readdirSync(path.join(REPO_ROOT, r))) {
      const f = path.join(REPO_ROOT, r, dir, 'appsettings.json');
      if (!fs.existsSync(f)) continue;
      if (/"Qdrant"\s*:\s*\{[^}]*"Host"/.test(fs.readFileSync(f, 'utf8'))) {
        users.push(`${dir.replace(/Service$/, '').toLowerCase()}-service`);
      }
    }
  }
  return users.sort();
}

/** values-local の通信の一覧（段 1 の各 Pod の宛先・段 2 の Pod は隔離されない・確かめ用の Pod）。 */
function localCases(text, { mesh = false } = {}) {
  const w = workloads(text);
  const stage1 = [...w.keys()].filter((a) => !STAGE2.includes(a));
  const allowTo = (name) => (byName(text, `allow-egress-to-${name}`)[0] || { spec: { podSelector: {} } }).spec.podSelector;
  const cases = [];
  for (const a of [...stage1, PROBE]) {
    const env = w.get(a) || new Set();
    cases.push({ who: `${a} → kube-dns`, srcApp: a, ...KUBE_DNS('UDP'), want: 'allow' });
    cases.push({ who: `🔴 ${a} → kube-system の別の Pod の 53`, srcApp: a, ...OTHER_53, want: 'deny' });
    cases.push({ who: `${a} → 同じ名前空間`, srcApp: a, ...SAME_NS('bff-service', 8080), want: 'allow' });
    cases.push({ who: `${a} → istiod`, srcApp: a, ...ISTIOD, want: mesh ? 'allow' : 'deny' });
    cases.push({ who: `${a} → postgres 5432`, srcApp: a, ...INFRA('postgres', 5432), want: env.has(DB_ENV) ? 'allow' : 'deny' });
    cases.push({ who: `${a} → rabbitmq 5672`, srcApp: a, ...INFRA('rabbitmq', 5672), want: env.has(MQ_ENV) ? 'allow' : 'deny' });
    cases.push({ who: `${a} → keycloak 8080`, srcApp: a, ...INFRA('keycloak', 8080), want: env.has(OTLP_ENV) ? 'allow' : 'deny' });
    cases.push({ who: `${a} → otel-collector 4317`, srcApp: a, ...INFRA('otel-collector', 4317), want: env.has(OTLP_ENV) ? 'allow' : 'deny' });
    const q = qdrantUsers().includes(a) ? 'allow' : 'deny';
    cases.push({ who: `${a} → qdrant 6334`, srcApp: a, ...INFRA('qdrant', 6334), want: q });
    cases.push({ who: `${a} → qdrant 6333`, srcApp: a, ...INFRA('qdrant', 6333), want: q });
    cases.push({ who: `🔴 ${a} → postgres の別のポート`, srcApp: a, ...INFRA('postgres', 6379), want: 'deny' });
    cases.push({ who: `🔴 ${a} → valkey`, srcApp: a, ...INFRA('valkey', 6379), want: 'deny' });
    cases.push({ who: `🔴 ${a} → 基盤の別の Pod（postgres と同じポート）`, srcApp: a, ...INFRA('mailpit', 5432), want: 'deny' });
    cases.push({ who: `🔴 ${a} → 別の名前空間の同名ラベル（postgres）`, srcApp: a, dstNs: 'ai-stock-trading', dstLabels: { app: 'postgres' }, port: 5432, want: 'deny' });
    cases.push({ who: `🔴 ${a} → AST の名前空間`, srcApp: a, ...AST, want: 'deny' });
    cases.push({ who: `🔴 ${a} → API サーバ`, srcApp: a, ...APISERVER, want: 'deny' });
    cases.push({ who: `🔴 ${a} → クラスタの外`, srcApp: a, ...EXTERNAL, want: 'deny' });
  }
  for (const a of STAGE2) {
    cases.push({ who: `段 2 の ${a} → クラスタの外（本変更では隔離しない）`, srcApp: a, ...EXTERNAL, want: 'allow' });
    cases.push({ who: `段 2 の ${a} → AST の名前空間（本変更では隔離しない）`, srcApp: a, ...AST, want: 'allow' });
  }
  cases.push({ who: '確かめの対照（egress-probe-control）→ 基盤', srcApp: 'egress-probe-control', ...INFRA('keycloak', 8080), want: 'allow' });
  // 宛先のポリシーの送り元が空（{}）に崩れていないこと（崩れると段 2 の Pod まで隔離し、上の段 2 の行が落ちる）。
  for (const n of ['postgres', 'rabbitmq', 'keycloak', 'otel-collector', 'qdrant']) {
    assert.ok(Object.keys(allowTo(n)).length > 0, `allow-egress-to-${n} の送り元が空`);
  }
  return cases;
}

// ---------------------------------------------------------------- 静的（helm 不要）

ok('評価器の自己試験: 隔離・許可の和・AND・ipBlock・ポートとプロトコル', () => {
  const deny = { metadata: { name: 'd', namespace: RELEASE_NS }, spec: { podSelector: {}, policyTypes: ['Egress'] } };
  const dns = {
    metadata: { name: 'dns', namespace: RELEASE_NS },
    spec: {
      podSelector: {},
      policyTypes: ['Egress'],
      egress: [
        {
          to: [{ namespaceSelector: { matchLabels: { 'kubernetes.io/metadata.name': 'kube-system' } }, podSelector: { matchLabels: { 'k8s-app': 'kube-dns' } } }],
          ports: [{ protocol: 'UDP', port: '53' }],
        },
      ],
    },
  };
  assert.strictEqual(evaluate([], { srcApp: 'a', ...EXTERNAL }), 'allow', '選ばれなければ隔離されない');
  assert.strictEqual(evaluate([deny], { srcApp: 'a', ...KUBE_DNS() }), 'deny');
  assert.strictEqual(evaluate([deny, dns], { srcApp: 'a', ...KUBE_DNS('UDP') }), 'allow');
  assert.strictEqual(evaluate([deny, dns], { srcApp: 'a', ...KUBE_DNS('TCP') }), 'deny', 'プロトコルが違えば落ちる');
  assert.strictEqual(evaluate([deny, dns], { srcApp: 'a', ...OTHER_53 }), 'deny', 'AND の podSelector');
  const ext = { metadata: { name: 'e' }, spec: { podSelector: {}, policyTypes: ['Egress'], egress: [{ to: [{ ipBlock: { cidr: '0.0.0.0/0', except: ['10.0.0.0/8'] } }] }] } };
  assert.strictEqual(evaluate([ext], { srcApp: 'a', ...EXTERNAL }), 'allow');
  assert.strictEqual(evaluate([ext], { srcApp: 'a', ...APISERVER }), 'deny', 'except');
  assert.strictEqual(evaluate([ext], { srcApp: 'a', ...INFRA('postgres', 5432) }), 'deny', 'ipBlock は Pod に当てない');
  const same = { metadata: { name: 's' }, spec: { podSelector: {}, policyTypes: ['Egress'], egress: [{ to: [{ podSelector: {} }] }] } };
  assert.strictEqual(evaluate([same], { srcApp: 'a', ...SAME_NS('x', 1) }), 'allow');
  assert.strictEqual(evaluate([same], { srcApp: 'a', ...INFRA('x', 1) }), 'deny', 'namespaceSelector の無い podSelector は同じ名前空間だけ');
  assert.throws(() => peerMatches({ namespaceSelectorX: {} }, RELEASE_NS, {}));
});

ok('確かめ用の Pod のラベルは values-local と統合スタックの門で同じ（egress-probe・対照は workloads に無い）', () => {
  const wf = read('.github/workflows/integration-stack.yml');
  assert.ok(wf.includes('probe egress-probe-control "$n_control"') && wf.includes('probe egress-probe "$n_subject"'), '統合スタックの門が 2 つの Pod を立てていない');
  const vl = read(VALUES_LOCAL);
  assert.ok(/\n {6}- egress-probe\n/.test(vl), 'values-local の workloads に egress-probe が無い');
  assert.ok(!/- egress-probe-control/.test(vl), '対照のラベルが workloads に在る（対照にならない）');
});

// ---------------------------------------------------------------- helm

if (!hasHelm()) {
  process.stderr.write('helm が見つからない。本試験は helm template で描いて固定するため、helm 無しでは検証できない（fail-closed）。\n');
  process.exit(1);
}

const DEFAULT = mustRender([]);
const LOCAL = mustRender(['-f', VALUES_LOCAL]);
const LOCAL_MESH = mustRender(['-f', VALUES_LOCAL, '--set', 'mesh.enabled=true']);

ok('既定（本番像）: 名前空間の全 Pod に既定拒否・DNS は kube-dns だけ・同じ名前空間・istiod。宛先の許可は描かない', () => {
  const deny = byName(DEFAULT, 'default-deny-egress');
  assert.strictEqual(deny.length, 1);
  assert.deepStrictEqual(deny[0].spec, { podSelector: {}, policyTypes: ['Egress'] });
  const dns = byName(DEFAULT, 'allow-egress-dns')[0];
  assert.deepStrictEqual(dns.spec, {
    podSelector: {},
    policyTypes: ['Egress'],
    egress: [
      {
        to: [{ namespaceSelector: { matchLabels: { 'kubernetes.io/metadata.name': 'kube-system' } }, podSelector: { matchLabels: { 'k8s-app': 'kube-dns' } } }],
        ports: [{ protocol: 'UDP', port: '53' }, { protocol: 'TCP', port: '53' }],
      },
    ],
  });
  assert.deepStrictEqual(byName(DEFAULT, 'allow-egress-intra-namespace')[0].spec.egress, [{ to: [{ podSelector: {} }] }]);
  assert.strictEqual(byName(DEFAULT, 'allow-egress-istiod').length, 1, '既定は mesh.enabled なので istiod を許す');
  assert.deepStrictEqual(networkPolicies(DEFAULT).filter((p) => /^allow-egress-to-/.test(p.metadata.name)), [], '本番像は宛先を宣言しない');
});

ok('既定: allow-intra-namespace は Ingress だけ。SeaweedFS の egress の DNS も kube-dns だけ（全名前空間の 53 番を残さない）', () => {
  const intra = byName(DEFAULT, 'allow-intra-namespace')[0];
  assert.deepStrictEqual(intra.spec.policyTypes, ['Ingress']);
  assert.strictEqual(intra.spec.egress, undefined);
  const sw = byName(DEFAULT, 'allow-seaweedfs-s3-only')[0];
  assert.deepStrictEqual(sw.spec.egress, [{ to: [{ podSelector: {} }] }, byName(DEFAULT, 'allow-egress-dns')[0].spec.egress[0]]);
  for (const p of egressPolicies(DEFAULT)) {
    for (const rule of p.spec.egress || []) {
      for (const peer of rule.to || []) {
        assert.ok(!(peer.namespaceSelector && Object.keys(peer.namespaceSelector).length === 0), `${p.metadata.name} に空の namespaceSelector（全名前空間）が残る`);
      }
    }
  }
});

ok('既定: 描画される全ての Pod へ評価すると、DNS・同じ名前空間・istiod だけが通り、基盤・クラスタの外は落ちる（メッシュ無しは istiod も落ちる）', () => {
  const apps = [...workloads(DEFAULT).keys()];
  assert.ok(apps.length >= 15, `Pod が少なすぎる（${apps.length}）`);
  assert.deepStrictEqual(problemsFor(DEFAULT, prodCases(apps)), []);
  const noMesh = mustRender(['--set', 'mesh.enabled=false']);
  assert.strictEqual(byName(noMesh, 'allow-egress-istiod').length, 0);
  assert.deepStrictEqual(problemsFor(noMesh, prodCases(apps, { mesh: false })), []);
});

ok('values-local: 描画される全ての Pod は段 1（workloads）か段 2 のどちらか一方に分類され、workloads は描画される Pod か egress-probe だけ', () => {
  const apps = [...workloads(LOCAL).keys()];
  const sel = byName(LOCAL, 'default-deny-egress')[0].spec.podSelector;
  assert.deepStrictEqual(Object.keys(sel), ['matchExpressions']);
  assert.strictEqual(sel.matchExpressions.length, 1);
  assert.deepStrictEqual([sel.matchExpressions[0].key, sel.matchExpressions[0].operator], ['app', 'In']);
  const scope = sel.matchExpressions[0].values;
  for (const a of apps) {
    assert.ok(scope.includes(a) !== STAGE2.includes(a), `${a} が段 1 と段 2 の${scope.includes(a) ? '両方' : 'どちらにも無い'}（新しい Pod は既定拒否の対象に分類すること）`);
  }
  for (const s of scope) assert.ok(apps.includes(s) || s === PROBE, `workloads の ${s} は描画される Pod ではない（打ち間違いは制限の外に残る）`);
  for (const s of STAGE2) assert.ok(apps.includes(s), `段 2 の ${s} が描画されない（一覧を直すこと）`);
  for (const n of ['allow-egress-dns', 'allow-egress-intra-namespace']) {
    assert.deepStrictEqual(byName(LOCAL, n)[0].spec.podSelector, sel, `${n} の対象が既定拒否と違う`);
  }
  assert.strictEqual(byName(LOCAL, 'allow-intra-namespace').length, 0, 'values-local は ingress の NetworkPolicy を描かない（従前どおり）');
  assert.strictEqual(byName(LOCAL, 'allow-egress-istiod').length, 0, 'values-local は mesh.enabled=false');
});

ok('values-local: 宛先の送り元は宣言（DB・ブローカ・OTLP・Qdrant）とちょうど一致し、宛先は deploy/local/infra の実体と一致する', () => {
  const w = workloads(LOCAL);
  const stage1 = [...w.keys()].filter((a) => !STAGE2.includes(a));
  const withEnv = (name) => stage1.filter((a) => w.get(a).has(name)).sort();
  const from = (n) => [...byName(LOCAL, `allow-egress-to-${n}`)[0].spec.podSelector.matchExpressions[0].values].sort();
  assert.deepStrictEqual(from('postgres'), withEnv(DB_ENV));
  assert.deepStrictEqual(from('rabbitmq'), withEnv(MQ_ENV));
  assert.deepStrictEqual(from('keycloak'), withEnv(OTLP_ENV));
  assert.deepStrictEqual(from('otel-collector'), withEnv(OTLP_ENV));
  assert.deepStrictEqual(qdrantUsers(), ['ingestion-service', 'retrieval-service'], 'Qdrant を使うサービスが変わった（宛先を見直すこと）');
  assert.deepStrictEqual(from('qdrant'), qdrantUsers());
  // 宛先の実体（名前空間・ラベル・ポート）。
  const infra = fs.readdirSync(path.join(REPO_ROOT, INFRA_DIR)).filter((f) => f.endsWith('.yaml')).map((f) => read(`${INFRA_DIR}/${f}`)).join('\n---\n');
  const names = networkPolicies(LOCAL).filter((p) => /^allow-egress-to-/.test(p.metadata.name)).map((p) => p.metadata.name.replace('allow-egress-to-', ''));
  assert.deepStrictEqual(names.sort(), ['keycloak', 'otel-collector', 'postgres', 'qdrant', 'rabbitmq']);
  for (const n of names) {
    const p = byName(LOCAL, `allow-egress-to-${n}`)[0];
    const peer = p.spec.egress[0].to[0];
    assert.deepStrictEqual(peer.namespaceSelector, { matchLabels: { 'kubernetes.io/metadata.name': INFRA_NS } });
    const app = peer.podSelector.matchLabels.app;
    const dep = chunks(infra).find((c) => /^kind: Deployment\s*$/m.test(c) && new RegExp(`^  name: ${app}\\s*$`, 'm').test(c));
    assert.ok(dep, `deploy/local/infra に Deployment ${app} が無い`);
    assert.ok(new RegExp(`^  namespace: ${INFRA_NS}\\s*$`, 'm').test(dep), `${app} の名前空間が ${INFRA_NS} でない`);
    const podTpl = dep.slice(dep.indexOf('template:'));
    // ブロック形（`app: x`）と flow 形（`labels: { app: x }`）の両方を読む。
    assert.ok(new RegExp(`(\\n\\s+app: ${app}\\s*\\n|labels: \\{[^}]*\\bapp: ${app}\\b)`).test(podTpl), `${app} の Pod のラベルが app: ${app} でない`);
    for (const port of p.spec.egress[0].ports) {
      assert.strictEqual(port.protocol, 'TCP');
      assert.ok(new RegExp(`containerPort: ${port.port}\\b`).test(dep), `${app} は ${port.port} を開いていない`);
    }
  }
});

ok('values-local: 通信の一覧への評価 —— 宣言どおりの宛先だけが通り、段 2 の Pod は隔離されない（メッシュの有無とも）', () => {
  assert.deepStrictEqual(problemsFor(LOCAL, localCases(LOCAL)), []);
  assert.strictEqual(byName(LOCAL_MESH, 'allow-egress-istiod').length, 1);
  assert.deepStrictEqual(problemsFor(LOCAL_MESH, localCases(LOCAL_MESH, { mesh: true })), []);
});

ok('本番像の既存の 1 本（Vault・API サーバ）は従前どおり networkPolicy.enabled のときだけ（values-local は描かない）', () => {
  assert.strictEqual(byName(LOCAL, 'allow-bff-egress-to-vault').length, 0);
  const withVault = mustRender(['--set', 'services.bff.vault.address=http://vault.platform-infra.svc.cluster.local:8200']);
  assert.strictEqual(byName(withVault, 'allow-bff-egress-to-vault').length, 1);
});

ok('両方 false なら egress のポリシーを 1 枚も描かない（明示の退路）', () => {
  const off = mustRender(['-f', VALUES_LOCAL, '--set', 'networkPolicy.egress.enabled=false']);
  assert.deepStrictEqual(egressPolicies(off).map((p) => p.metadata.name), []);
});

ok('描画で止まる条件（egress を外す・本番像で絞る・ingress 無効で workloads が空・欠けた宛先・workloads に無い送り元・重複・空の DNS・istiod）', () => {
  const dest = (i, k, v) => ['--set', `networkPolicy.egress.destinations[${i}].${k}=${v}`];
  const goodDest = (i = 0) => [...dest(i, 'name', 'x'), ...dest(i, 'namespace', 'ns'), ...dest(i, 'podLabels.app', 'x'), ...dest(i, 'ports[0]', '1'), ...dest(i, 'from[0]', 'document-service')];
  const cases = [
    [['--set', 'networkPolicy.egress.enabled=false'], /egress\.enabled=false は受けない/],
    [['--set', 'networkPolicy.egress.workloads[0]=document-service'], /workloads を絞れない/],
    [['--set', 'networkPolicy.enabled=false'], /networkPolicy\.egress\.workloads に当てる Pod を与える/],
    [['-f', VALUES_LOCAL, '--set', 'networkPolicy.egress.workloads=null'], /networkPolicy\.egress\.workloads に当てる Pod を与える/],
    [['-f', VALUES_LOCAL, '--set', 'networkPolicy.egress.workloads[0]='], /workloads に空の要素/],
    [['--set', 'networkPolicy.egress.dns.podLabels=null'], /dns\.podLabels が空/],
    [['--set', 'networkPolicy.egress.dns.namespace='], /dns\.namespace が空/],
    [['--set', 'networkPolicy.egress.istiod.port=null'], /istiod の namespace・podLabels・port は空にできない/],
    [[...goodDest(), ...dest(0, 'name', 'Bad_Name')], /小文字英数とハイフンだけ/],
    [[...goodDest(0), ...goodDest(1)], /"x" が重複/],
    [[...goodDest(), '--set', 'networkPolicy.egress.destinations[0].namespace='], /\[x\]\.namespace が空/],
    [[...goodDest(), '--set', 'networkPolicy.egress.destinations[0].podLabels=null'], /\[x\]\.podLabels が空/],
    [[...goodDest(), '--set', 'networkPolicy.egress.destinations[0].ports=null'], /\[x\]\.ports が空/],
    [[...goodDest(), '--set', 'networkPolicy.egress.destinations[0].from=null'], /\[x\]\.from が空/],
    [['-f', VALUES_LOCAL, '--set', 'networkPolicy.egress.destinations[0].from[0]=bff-service'], /"bff-service" は workloads に無い/],
  ];
  for (const [args, re] of cases) {
    const r = helmTemplate(args);
    assert.notStrictEqual(r.status, 0, `${args.join(' ')} で描画が通った`);
    assert.match(r.err, re, `${args.join(' ')} の失敗理由が違う: ${r.err}`);
  }
  assert.strictEqual(helmTemplate(goodDest()).status, 0, '本番像（workloads 空）では宛先の送り元を workloads で縛らない');
});

// ---------------------------------------------------------------- 変異

function mutatedRender(file, transform, args = ['-f', path.join(REPO_ROOT, VALUES_LOCAL)]) {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'helm-egress-np-mut-'));
  try {
    const chartCopy = path.join(tmp, 'chart');
    fs.cpSync(path.join(REPO_ROOT, CHART), chartCopy, { recursive: true });
    const tpl = path.join(chartCopy, 'templates', file);
    const original = fs.readFileSync(tpl, 'utf8');
    const mutated = transform(original);
    assert.notStrictEqual(mutated, original, '変異を当てられなかった（テンプレートの書き方が変わった）');
    fs.writeFileSync(tpl, mutated);
    return helmTemplate(args, chartCopy);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
}

ok('🔴 変異: DNS の規則から podSelector を外すと、kube-system の別の Pod の 53 番が通って赤になる', () => {
  const r = mutatedRender('_networkpolicy.tpl', (s) => s.replace(/\n {6}podSelector:\n {8}matchLabels:\n {10}\{\{- toYaml \$dns\.podLabels \| nindent 10 \}\}/, ''));
  assert.strictEqual(r.status, 0, r.err);
  const problems = problemsFor(r.out, localCases(r.out));
  assert.ok(problems.some((p) => p.includes('kube-system の別の Pod の 53') && p.includes('期待 deny')), JSON.stringify(problems.slice(0, 5)));
});

ok('🔴 変異: 宛先の AND を OR に割る（podSelector を別の要素へ）と、基盤の名前空間の別の Pod が通って赤になる', () => {
  const r = mutatedRender('networkpolicy-egress.yaml', (s) =>
    s.replace(/(kubernetes\.io\/metadata\.name: \{\{ \$d\.namespace \}\}\n)( {10})podSelector:/, '$1        - podSelector:'),
  );
  assert.strictEqual(r.status, 0, r.err);
  const problems = problemsFor(r.out, localCases(r.out));
  assert.ok(problems.some((p) => p.includes('基盤の別の Pod（postgres と同じポート）') && p.includes('期待 deny')), JSON.stringify(problems.slice(0, 5)));
});

ok('🔴 変異: 宛先の ports を外すと、基盤の別のポートが通って赤になる', () => {
  const r = mutatedRender('networkpolicy-egress.yaml', (s) =>
    s.replace(/\n {6}ports:\n {8}\{\{- range \$d\.ports \}\}\n {8}- protocol: TCP\n {10}port: \{\{ \. \}\}\n {8}\{\{- end \}\}/, ''),
  );
  assert.strictEqual(r.status, 0, r.err);
  const problems = problemsFor(r.out, localCases(r.out));
  assert.ok(problems.some((p) => p.includes('postgres の別のポート') && p.includes('期待 deny')), JSON.stringify(problems.slice(0, 5)));
});

ok('🔴 変異: workloads に無い送り元の検査を外すと、段 2 の Pod が許可だけで隔離される描画が通って赤になる', () => {
  const r = mutatedRender(
    'networkpolicy-egress.yaml',
    (s) => s.replace(/\{\{- if and \$workloads \(not \(has \. \$workloads\)\) \}\}.*\{\{ end \}\}\n/, ''),
    ['-f', path.join(REPO_ROOT, VALUES_LOCAL), '--set', 'networkPolicy.egress.destinations[0].from[0]=bff-service'],
  );
  assert.strictEqual(r.status, 0, `変異後のチャートで描画が止まった（変異が効いていない）: ${r.err}`);
  const problems = problemsFor(r.out, localCases(r.out));
  assert.ok(problems.some((p) => p.startsWith('段 2 の bff-service → クラスタの外') && p.includes('期待 allow')), JSON.stringify(problems.slice(0, 5)));
});

process.stdout.write(`\n✓ ${passed} tests passed\n`);
