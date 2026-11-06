#!/usr/bin/env node
'use strict';
/*
 * otel-collector-network-policy.test.js
 * NFR-18, IADR-0682: otel-collector の到達の制限（deploy/local/otel-collector-netpol。opt-in の段 1）を、k8s の評価規則で通信の一覧へ当てて固定する。
 *
 * 固定するもの:
 *   1. 送り先（egress）は collector の設定（既定構成 deploy/local/infra/otel-collector.yaml と転送構成
 *      deploy/local/observability/otel-collector-forward.yaml）の exporter と scrape の宛先から導き、その全部が通る。
 *      宛先の Pod（app ラベル）と Pod のポートは、リポジトリの Service の宣言（selector・targetPort）から引く。名前解決（kube-dns）も通る。
 *   2. 送り手（ingress）は宣言から導き、その全部が通る: helm の global.otel.endpoint（基盤の名前空間）と、取引ユニットの名前空間
 *      （submodule があれば同ユニットの values の otlpEndpoint）・Prometheus の scrape（8888）・Vault の audit の宛先（9514）。
 *   3. それ以外は通らない（4318・宣言の外の Pod・宣言の外の宛先・クラスタの外）。
 *   4. 変異: 送り先・送り手の規則を 1 つ外すと、必ず赤になる（試験が空振りしない）。
 *   5. 段 1（opt-in）: 既定の経路B（deploy/local/infra）には入れず、overlay の kustomization だけが読む。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。実行: node scripts/otel-collector-network-policy.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const NS = 'platform-infra';
const POLICY = 'deploy/local/otel-collector-netpol/otel-collector-networkpolicy.yaml';
const CONFIGS = ['deploy/local/infra/otel-collector.yaml', 'deploy/local/observability/otel-collector-forward.yaml'];
const read = (rel) => fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

// ------------------------------------------------------------------ 最小の YAML（ブロック形式・流れ形式の map / list の 1 行）
const unquote = (s) => String(s).trim().replace(/^["']|["']$/g, '');
function parseYaml(text) {
  const lines = text.split('\n').filter((l) => l.trim() !== '' && !/^\s*#/.test(l) && l.trim() !== '---')
    .map((l) => ({ indent: l.length - l.trimStart().length, body: l.trim() }));
  let i = 0;
  function scalar(s) {
    const t = s.trim().replace(/\s+#.*$/, '');
    const m = /^\{(.*)\}$/.exec(t);
    if (m) {
      const o = {};
      for (const part of m[1].split(',').map((p) => p.trim()).filter(Boolean)) { const at = part.indexOf(':'); o[part.slice(0, at).trim()] = unquote(part.slice(at + 1)); }
      return o;
    }
    const l = /^\[(.*)\]$/.exec(t);
    if (l) return l[1].split(',').map((p) => unquote(p)).filter((p) => p !== '');
    return unquote(t);
  }
  function kv(body, indent, target) {
    const at = body.search(/:(\s|$)/);
    assert.ok(at > 0, `YAML を読めない行: ${body}`);
    const key = unquote(body.slice(0, at));
    const rest = body.slice(at + 1).trim();
    if (rest !== '') { target[key] = scalar(rest); return; }
    target[key] = i < lines.length && (lines[i].indent > indent || (lines[i].indent === indent && lines[i].body.startsWith('- '))) ? block(lines[i].indent) : null;
  }
  function block(indent) {
    if (lines[i].body.startsWith('- ')) {
      const list = [];
      while (i < lines.length && lines[i].indent === indent && lines[i].body.startsWith('- ')) {
        const item = lines[i].body.slice(2);
        i++;
        if (/^[^{"'[][^:]*:(\s|$)/.test(item)) {
          const m = {};
          kv(item, indent + 2, m);
          while (i < lines.length && lines[i].indent === indent + 2 && !lines[i].body.startsWith('- ')) { const b = lines[i].body; i++; kv(b, indent + 2, m); }
          list.push(m);
        } else list.push(scalar(item));
      }
      return list;
    }
    const map = {};
    while (i < lines.length && lines[i].indent === indent && !lines[i].body.startsWith('- ')) { const b = lines[i].body; i++; kv(b, indent, map); }
    return map;
  }
  return block(lines[0].indent);
}
const policiesOf = (text) => text.split(/^---\n/m).filter((c) => /^kind:\s*NetworkPolicy\s*$/m.test(c)).map(parseYaml);

// ------------------------------------------------------------------ k8s の NetworkPolicy の評価（本試験が使う部分集合）
function selectorMatches(sel, labels) {
  const s = sel || {};
  for (const k of Object.keys(s)) assert.ok(k === 'matchLabels', `評価器が知らない selector の項目 ${k}`);
  return Object.entries(s.matchLabels || {}).every(([k, v]) => labels[k] === v);
}
function peerMatches(peer, policyNs, ns, labels, ip) {
  for (const k of Object.keys(peer)) assert.ok(['podSelector', 'namespaceSelector'].includes(k), `評価器が知らない peer の項目 ${k}`);
  if (ip) return false; // クラスタの外は Pod / 名前空間の selector に当たらない（ipBlock は使っていない）。
  const nsOk = peer.namespaceSelector ? selectorMatches(peer.namespaceSelector, { 'kubernetes.io/metadata.name': ns }) : ns === policyNs;
  const podOk = peer.podSelector ? selectorMatches(peer.podSelector, labels) : true;
  return nsOk && podOk;
}
const portOk = (ports, port, proto) => !ports || ports.length === 0 || ports.some((p) => (p.protocol || 'TCP') === proto && String(p.port) === String(port));

/** 1 本の通信の可否。src / dst = { ns, labels } か { ip }。送り手側の egress と受け手側の ingress の両方で決まる。 */
function evaluate(policies, { src, dst, port, proto = 'TCP' }) {
  const side = (type, self, peer, list) => {
    if (!self.ns) return true; // クラスタの外は NetworkPolicy の対象外。
    const selecting = policies.filter((p) => (p.metadata.namespace || NS) === self.ns && (p.spec.policyTypes || ['Ingress']).includes(type)
      && selectorMatches(p.spec.podSelector, self.labels));
    if (selecting.length === 0) return true;
    return selecting.some((p) => (p.spec[list] || []).some((rule) => {
      const peers = rule[type === 'Egress' ? 'to' : 'from'];
      return (!peers || peers.some((x) => peerMatches(x, p.metadata.namespace || NS, peer.ns, peer.labels || {}, peer.ip))) && portOk(rule.ports, port, proto);
    }));
  };
  return side('Egress', src, dst, 'egress') && side('Ingress', dst, src, 'ingress') ? 'allow' : 'deny';
}

// ------------------------------------------------------------------ 宣言から導く送り先・送り手
const COLLECTOR = { ns: NS, labels: { app: 'otel-collector' } };

/** リポジトリの Service（platform-infra）の名前 → { app, ports: { port → targetPort } }。 */
function services() {
  const out = new Map();
  const files = ['deploy/local/infra', 'deploy/local/observability', 'deploy/mail-relay', 'deploy/mail-relay/reset-floor']
    .flatMap((d) => fs.readdirSync(path.join(REPO_ROOT, d)).filter((f) => f.endsWith('.yaml')).map((f) => `${d}/${f}`));
  for (const f of files) {
    // Service は本ファイルの最小の YAML パーサで読む（正規表現で複数行を拾わない）。
    const text = read(f);
    for (const c of text.split(/^---\n/m)) {
      if (!/^kind:\s*Service\s*$/m.test(c)) continue;
      const doc = parseYaml(c);
      const name = doc.metadata && doc.metadata.name;
      const ns = doc.metadata && doc.metadata.namespace;
      const app = doc.spec && doc.spec.selector && doc.spec.selector.app;
      if (!name || ns !== NS || !app) continue;
      // targetPort が名前なら、同じファイルの Pod の containerPort（同じ名前）へ引く。
      const named = (t) => (/^\d+$/.test(String(t)) ? String(t) : (new RegExp(`containerPort: (\\d+), name: ${t}\\b`).exec(text) || [])[1]);
      const ports = {};
      for (const p of doc.spec.ports || []) ports[String(p.port)] = named(p.targetPort === undefined ? p.port : p.targetPort);
      out.set(name, { app, ports, file: f });
    }
  }
  return out;
}

/** collector の設定の宛先（exporter の endpoint・scrape の targets）。host:port の一覧。 */
function collectorDestinations() {
  const out = new Set();
  for (const f of CONFIGS) {
    const cfg = read(f);
    const exporters = cfg.slice(cfg.indexOf('    exporters:'), cfg.indexOf('    service:'));
    for (const m of exporters.matchAll(/endpoint: (?:https?:\/\/)?([\w.-]+):(\d+)/g)) out.add(`${m[1]}:${m[2]}`);
    for (const m of cfg.matchAll(/targets: \[([^\]]*)\]/g)) for (const t of m[1].split(',')) out.add(unquote(t));
  }
  return [...out].sort();
}

function egressCases(svc) {
  return collectorDestinations().map((hp) => {
    const [host, port] = hp.split(':');
    const s = svc.get(host);
    assert.ok(s, `collector の宛先 ${hp} の Service（${NS}）がリポジトリに無い`);
    assert.ok(s.ports[port], `Service ${host} に port ${port} が無い（${s.file}）`);
    return { who: `collector → ${hp}`, src: COLLECTOR, dst: { ns: NS, labels: { app: s.app } }, port: Number(s.ports[port]), want: 'allow' };
  });
}

function portOf(url) {
  const m = /:(\d+)(?:\/|$)/.exec(url);
  assert.ok(m, `ポートを読めない: ${url}`);
  return Number(m[1]);
}

function ingressCases() {
  const values = read('deploy/helm/microservices-platform/values.yaml');
  const mspEndpoint = (/^\s+endpoint: (http:\/\/otel-collector:\d+)\s*$/m.exec(values.slice(values.indexOf('  otel:'))) || [])[1];
  assert.ok(mspEndpoint, 'helm の global.otel.endpoint を読めない');
  let astPort = portOf(mspEndpoint);
  const astValues = path.join(REPO_ROOT, 'src/ai-stock-trading/deploy/helm/ai-stock-trading/values.yaml');
  if (fs.existsSync(astValues)) {
    const m = /otlpEndpoint: (\S+)/.exec(fs.readFileSync(astValues, 'utf8'));
    assert.ok(m, '取引ユニットの otlpEndpoint を読めない');
    astPort = portOf(m[1]);
  } else {
    process.stdout.write('::notice::取引ユニットの写しが無いので、取引ユニットの OTLP のポートは基盤と同じとみなす\n');
  }
  const vault = /VAULT_AUDIT_SOCKET_ADDRESS:-otel-collector\.platform-infra\.svc:(\d+)/.exec(read('deploy/local/vault-persistence/vault-entrypoint.sh'));
  assert.ok(vault, 'Vault の audit の宛先を読めない');
  const prom = read('deploy/local/observability/prometheus.yaml');
  const scrape = /job_name: otel-collector[\s\S]*?targets: \[["']?otel-collector:(\d+)/.exec(prom);
  assert.ok(scrape, 'Prometheus の otel-collector の scrape を読めない');
  return [
    { who: '基盤のサービス → OTLP', src: { ns: 'microservices-platform', labels: { app: 'document-service' } }, dst: COLLECTOR, port: portOf(mspEndpoint), want: 'allow' },
    { who: '取引ユニットのサービス → OTLP', src: { ns: 'ai-stock-trading', labels: { app: 'order-execution-service' } }, dst: COLLECTOR, port: astPort, want: 'allow' },
    { who: 'Prometheus → 自己テレメトリ', src: { ns: NS, labels: { app: 'prometheus' } }, dst: COLLECTOR, port: Number(scrape[1]), want: 'allow' },
    { who: 'Vault → audit', src: { ns: NS, labels: { app: 'vault' } }, dst: COLLECTOR, port: Number(vault[1]), want: 'allow' },
  ];
}

const DENY = [
  { who: '基盤のサービス → OTLP HTTP 4318', src: { ns: 'microservices-platform', labels: { app: 'document-service' } }, dst: COLLECTOR, port: 4318, want: 'deny' },
  { who: '基盤のサービス → 自己テレメトリ 8888', src: { ns: 'microservices-platform', labels: { app: 'document-service' } }, dst: COLLECTOR, port: 8888, want: 'deny' },
  { who: '基盤のサービス → audit 9514', src: { ns: 'microservices-platform', labels: { app: 'document-service' } }, dst: COLLECTOR, port: 9514, want: 'deny' },
  { who: 'platform-infra の他の Pod（keycloak） → OTLP', src: { ns: NS, labels: { app: 'keycloak' } }, dst: COLLECTOR, port: 4317, want: 'deny' },
  { who: 'Grafana → audit 9514', src: { ns: NS, labels: { app: 'grafana' } }, dst: COLLECTOR, port: 9514, want: 'deny' },
  { who: '別の名前空間 → OTLP', src: { ns: 'default', labels: { app: 'x' } }, dst: COLLECTOR, port: 4317, want: 'deny' },
  { who: 'collector → postgres 5432', src: COLLECTOR, dst: { ns: NS, labels: { app: 'postgres' } }, port: 5432, want: 'deny' },
  { who: 'collector → rabbitmq 5672', src: COLLECTOR, dst: { ns: NS, labels: { app: 'rabbitmq' } }, port: 5672, want: 'deny' },
  { who: 'collector → 基盤のサービス', src: COLLECTOR, dst: { ns: 'microservices-platform', labels: { app: 'bff' } }, port: 8080, want: 'deny' },
  { who: 'collector → クラスタの外', src: COLLECTOR, dst: { ip: '93.184.216.34' }, port: 443, want: 'deny' },
  { who: 'collector → kube-system の DNS 以外の 53', src: COLLECTOR, dst: { ns: 'kube-system', labels: { app: 'not-dns' } }, port: 53, proto: 'UDP', want: 'deny' },
];
const DNS = [
  { who: 'collector → kube-dns UDP', src: COLLECTOR, dst: { ns: 'kube-system', labels: { 'k8s-app': 'kube-dns' } }, port: 53, proto: 'UDP', want: 'allow' },
  { who: 'collector → kube-dns TCP', src: COLLECTOR, dst: { ns: 'kube-system', labels: { 'k8s-app': 'kube-dns' } }, port: 53, proto: 'TCP', want: 'allow' },
];

const POLICIES = policiesOf(read(POLICY));
const SVC = services();
const problems = (policies, cases) => cases.filter((c) => evaluate(policies, c) !== c.want).map((c) => `${c.who}: ${c.want} であるべき`);

ok('送り先は collector の両方の設定から導いた宛先の全部（と名前解決）で、すべて通る', () => {
  const cases = egressCases(SVC);
  assert.deepStrictEqual(cases.map((c) => c.who.replace('collector → ', '')),
    ['loki:3100', 'mail-relay:9154', 'prometheus:9090', 'reset-floor:8080', 'tempo:4317'], '宛先の導出が変わった（試験の前提を見直す）');
  assert.deepStrictEqual(problems(POLICIES, [...cases, ...DNS]), []);
});

ok('送り手は宣言から導いた全部（基盤・取引ユニットの OTLP・Prometheus・Vault）で、すべて通る', () => {
  assert.deepStrictEqual(problems(POLICIES, ingressCases()), []);
});

ok('それ以外（4318・宣言の外の Pod と名前空間・宣言の外の宛先・クラスタの外）は通らない', () => {
  assert.deepStrictEqual(problems(POLICIES, DENY), []);
});

ok('変異: 送り先・送り手の規則を 1 つ外すと、必ず赤になる', () => {
  const all = [...egressCases(SVC), ...DNS, ...ingressCases()];
  let n = 0;
  for (const [pi, list] of [[0, 'ingress'], [1, 'egress']]) {
    const p = POLICIES.find((x) => (x.spec.policyTypes || []).includes(pi === 0 ? 'Ingress' : 'Egress'));
    for (let r = 0; r < p.spec[list].length; r++) {
      const mutated = POLICIES.map((x) => (x === p ? { ...x, spec: { ...x.spec, [list]: x.spec[list].filter((_, j) => j !== r) } } : x));
      assert.ok(problems(mutated, all).length > 0, `${list} の規則 ${r} を外しても赤にならない（試験が空振りしている）`);
      n++;
    }
  }
  assert.strictEqual(n, 3 + 6);
});

ok('段 1（opt-in）: 既定の経路B（deploy/local/infra）には入れず、overlay の kustomization だけが読む', () => {
  assert.ok(!/otel-collector-netpol|otel-collector-networkpolicy/.test(read('deploy/local/infra/kustomization.yaml')), '既定の経路B に入っている（段 1 は opt-in）');
  assert.ok(/^\s+- otel-collector-networkpolicy\.yaml\s*$/m.test(read('deploy/local/otel-collector-netpol/kustomization.yaml')), 'overlay が manifest を読まない');
  for (const p of POLICIES) {
    assert.strictEqual(p.metadata.namespace, NS);
    assert.deepStrictEqual(p.spec.podSelector, { matchLabels: { app: 'otel-collector' } });
  }
  assert.deepStrictEqual(POLICIES.map((p) => p.spec.policyTypes).flat().sort(), ['Egress', 'Ingress']);
});

process.stdout.write(`otel-collector-network-policy.test.js: ${passed} passed\n`);
