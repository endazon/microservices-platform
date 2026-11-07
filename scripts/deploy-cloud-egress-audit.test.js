#!/usr/bin/env node
'use strict';
/**
 * NFR-18, NFR-16, NFR-21, NFR-05, IADR-0704: クラウドの最小構成（deploy/cloud）の、外向きの通信・Keycloak の一時管理者・ESO の名前空間・
 * API サーバーの監査・サーバ証明書の期限の警報を、宣言と描画で固定する（クラスタに触れない）。
 *
 *   K（一時管理者）
 *     K1 Keycloak の Deployment は KC_BOOTSTRAP_ADMIN_* を持たない（一時管理者を常に置かない）。
 *     K2 開け閉めの Job と入口の形（Secret への参照だけ・段は open / close / check・--live が無ければ動かない・値を扱わない）と、手順の試験。
 *        入口の振る舞い（close は流し直せる・Keycloak に繋がらなければ止まる）は keycloak-admin.test.js が kubectl と Job のスタブで確かめる。
 *   E（外向きの通信）
 *     E1 宛先を ipBlock で名指して出る許可は外向きの中継の Pod だけ（基盤・chart・vault・ESO の名前空間の描画）。
 *        🔴 この検査は ipBlock だけを見る。宛先（to）を持たずポートだけで許す出は宛先を問わず出られ、ここでは止まらない。その出は
 *        OpenBao と ESO の API サーバー向け（443・6443）の 2 つだけであることを固定する（増えたら赤。IADR-0704 残余 5）。
 *     E2 外の 443 を使う 3 つ（LLM ゲートウェイ・バックアップ・Alertmanager）は中継の 3128 へ出て、中継を使う設定を持つ。
 *     E3 中継は最終の段で enforce。移す間の重ね（cloud-transition/egress-proxy-report）は report で、3 つの直の 443 を戻す。
 *     E4 許可表は設定に在る宛先（LLM の提供元の BaseUrl・Alertmanager の Discord の形）を全部持ち、中継の入りは 3 つと Prometheus だけ。
 *     E5 中継の手順の試験。
 *   N（ESO の名前空間・監査）
 *     N1 external-secrets の名前空間は PSA restricted、入り・出とも既定で拒否し、出は名前解決・OpenBao の 8443・API サーバーのポートだけ、入りは webhook だけ。
 *     N2 手順書は名前空間を helm に作らせず、宣言を chart より先に当てる。chart 2.8.0 の Pod は restricted を満たす（固定の tgz を描く）。
 *     N3 k3s の API サーバーの監査は、方針（本文を残さない・Secret 等を必ず残す）と置き場（暗号化した領域）を持ち、手順書が導入の前に置く。
 *   C（証明書の期限）
 *     C1 測りの宛先は PKI の role（lib.sh の OB_PKI_ROLES）と同じ 4 つ、名前は ESO の common_name と同じ。受け口の入りは測りの Pod から TLS の口だけ。
 *     C2 Prometheus が測りを収集し、期限・検証・不在・CA・OpenBao の受け口の警報を持つ。閾値は作り直しの周期（4380h）と期限（8760h）の間。
 *        OpenBao の受け口の期限の導出（CA − 365 日）は tls-cert.sh の日数（CA 730・受け口 365）と同じ。promtool の単体試験で鳴る・鳴らないを見る。
 *     C3 測りの手順の試験。
 *
 * kubectl・helm・promtool が無ければ失敗にする（fail-closed。CI の deploy-cloud.test.js と同じジョブ）。
 * 実行: node scripts/deploy-cloud-egress-audit.test.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const { parseDocuments } = require('./lib/yaml-subset');
const dc = require('./deploy-cloud.test.js');

const ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
function run(cmd, args, opts = {}) {
  const r = spawnSync(cmd, args, { cwd: ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, ...opts });
  if (r.error || r.status !== 0) throw new Error(`${cmd} ${args.join(' ')} が失敗した（${cmd} が PATH に無い可能性）:\n${r.stderr || r.error}`);
  return r.stdout;
}
const docsOf = (text) => parseDocuments(text).filter((d) => d && typeof d === 'object' && d.kind);
const kustomize = (dir) => docsOf(run('kubectl', ['kustomize', dir]));
const renderChart = () => docsOf(run('helm', ['template', 'msp', CHART, '-n', 'microservices-platform', '-f', 'deploy/cloud/values-cloud.yaml', '--set-string', 'global.image.tag=0123456789ab']));
const find = (docs, kind, name, ns) => docs.find((d) => d.kind === kind && d.metadata.name === name && (!ns || d.metadata.namespace === ns));
const envOf = (c) => Object.fromEntries((c.env || []).map((e) => [e.name, e]));
const appOf = (sel) => (sel && sel.matchLabels && sel.matchLabels.app) || null;
const nsOf = (sel) => (sel && sel.matchLabels && sel.matchLabels['kubernetes.io/metadata.name']) || null;
const portsOf = (rule) => (rule.ports || []).map((p) => `${p.protocol || 'TCP'}/${p.port}`).sort();

const EGRESS_PROXY_URLS = ['http://egress-proxy:3128', 'http://egress-proxy.platform-infra.svc.cluster.local:3128'];
const EXTERNAL_CLIENTS = [
  ['microservices-platform', 'llmgateway-service', 'llmgateway-egress'],
  ['platform-infra', 'platform-backup', 'platform-backup-egress'],
  ['platform-infra', 'alertmanager', 'alertmanager-cloud'],
];

// ---------------------------------------------------------------- K
function keycloakBootstrapProblems(docs) {
  const kc = find(docs, 'Deployment', 'keycloak', 'platform-infra');
  if (!kc) return ['Keycloak の Deployment が無い'];
  const names = kc.spec.template.spec.containers.flatMap((c) => (c.env || []).map((e) => e.name));
  return names.filter((n) => /^KC_BOOTSTRAP_ADMIN_/.test(n)).map((n) => `Keycloak が ${n} を持つ（一時管理者を常に置く）`);
}

// ---------------------------------------------------------------- E
/** 宛先を問わない出（to を持たずポートだけで許す規則）。ipBlock の検査（directEgressProblems）が見ない範囲。 */
function portsOnlyEgress(docs) {
  const out = [];
  for (const d of docs.filter((x) => x.kind === 'NetworkPolicy')) {
    for (const r of d.spec.egress || []) if (!(r.to || []).length) out.push(`${d.metadata.namespace}/${d.metadata.name} ${portsOf(r).join(',') || '(全ポート)'}`);
  }
  return out.sort();
}

/** クラスタの外の範囲（ipBlock）へ出る許可を持つ NetworkPolicy のうち、外向きの中継の Pod 以外を選ぶもの。 */
function directEgressProblems(docs, where) {
  const out = [];
  for (const d of docs.filter((x) => x.kind === 'NetworkPolicy')) {
    const toOutside = (d.spec.egress || []).some((r) => (r.to || []).some((t) => t.ipBlock));
    if (!toOutside) continue;
    if (!(d.metadata.namespace === 'platform-infra' && appOf(d.spec.podSelector) === 'egress-proxy')) out.push(`${where}: ${d.metadata.namespace}/${d.metadata.name} がクラスタの外の範囲へ直に出る`);
  }
  return out;
}

function externalClientProblems(cloudDocs, chartDocs) {
  const out = [];
  for (const [ns, app, policy] of EXTERNAL_CLIENTS) {
    const np = find(cloudDocs, 'NetworkPolicy', policy, ns);
    if (!np) { out.push(`${policy} が無い`); continue; }
    const toProxy = (np.spec.egress || []).filter((r) => (r.to || []).some((t) => appOf(t.podSelector) === 'egress-proxy' && (nsOf(t.namespaceSelector) || np.metadata.namespace) === 'platform-infra'));
    if (!(toProxy.length === 1 && JSON.stringify(portsOf(toProxy[0])) === '["TCP/3128"]')) out.push(`${policy}: 外向きの中継の 3128 への出がちょうど 1 つでない`);
    if ((np.spec.egress || []).some((r) => portsOf(r).includes('TCP/443'))) out.push(`${policy}: 443 へ直に出る規則が残っている（${app}）`);
  }
  // 中継を使う設定
  const gw = chartDocs.find((d) => d.kind === 'Deployment' && /llmgateway/.test(d.metadata.name));
  const gwEnv = gw ? envOf(gw.spec.template.spec.containers[0]) : {};
  if (!(gwEnv.HTTPS_PROXY && EGRESS_PROXY_URLS.includes(String(gwEnv.HTTPS_PROXY.value)))) out.push('LLM ゲートウェイに外向きの中継の HTTPS_PROXY が無い');
  const noProxy = String((gwEnv.NO_PROXY || {}).value || '').split(',');
  for (const h of ['keycloak', 'otel-collector']) if (!noProxy.includes(h)) out.push(`LLM ゲートウェイの NO_PROXY に ${h} が無い（クラスタの中を中継へ回す）`);
  const cj = find(cloudDocs, 'CronJob', 'platform-backup-postgres', 'platform-infra') || cloudDocs.find((d) => d.kind === 'CronJob');
  const bEnv = cj ? envOf(dc.podSpec(cj).containers[0]) : {};
  if (!(bEnv.HTTPS_PROXY && EGRESS_PROXY_URLS.includes(String(bEnv.HTTPS_PROXY.value)))) out.push('バックアップに外向きの中継の HTTPS_PROXY が無い');
  const am = find(cloudDocs, 'ConfigMap', 'alertmanager-cloud-config', 'platform-infra');
  const tmpl = am ? docsOf(`kind: X\n${am.data['alertmanager.yml.tmpl']}`)[0] : {};
  const proxyUrl = tmpl && tmpl.global && tmpl.global.http_config && tmpl.global.http_config.proxy_url;
  if (!EGRESS_PROXY_URLS.includes(String(proxyUrl))) out.push('Alertmanager の global.http_config.proxy_url が外向きの中継でない');
  return out;
}

function proxyProblems(docs, { mode }) {
  const out = [];
  const dep = find(docs, 'Deployment', 'egress-proxy', 'platform-infra');
  if (!dep) return ['外向きの中継の Deployment が無い'];
  const env = envOf(dep.spec.template.spec.containers[0]);
  if (String((env.EGRESS_PROXY_MODE || {}).value) !== mode) out.push(`外向きの中継の段が ${mode} でない（${(env.EGRESS_PROXY_MODE || {}).value}）`);
  const np = find(docs, 'NetworkPolicy', 'egress-proxy', 'platform-infra');
  if (!np) return [...out, '外向きの中継の NetworkPolicy が無い'];
  const ing = np.spec.ingress || [];
  const proxyIn = ing.filter((r) => JSON.stringify(portsOf(r)) === '["TCP/3128"]');
  const senders = proxyIn.flatMap((r) => r.from || []).map((f) => `${nsOf(f.namespaceSelector) || 'platform-infra'}/${appOf(f.podSelector)}`).sort();
  const want = EXTERNAL_CLIENTS.map(([ns, app]) => `${ns}/${app}`).sort();
  if (JSON.stringify(senders) !== JSON.stringify(want)) out.push(`中継の 3128 の送り元が 3 つの呼び手だけでない（${senders.join(', ')}）`);
  const other = ing.filter((r) => !proxyIn.includes(r));
  if (!(other.length === 1 && JSON.stringify(portsOf(other[0])) === '["TCP/9464"]' && (other[0].from || []).length === 1 && appOf(other[0].from[0].podSelector) === 'prometheus')) out.push('中継の入りに 3128（3 つの呼び手）と 9464（Prometheus）の外がある');
  const outside = (np.spec.egress || []).filter((r) => (r.to || []).some((t) => t.ipBlock));
  if (!(outside.length === 1 && JSON.stringify(portsOf(outside[0])) === '["TCP/443"]')) out.push('中継の外への出が 443 だけでない');
  for (const r of outside) for (const t of r.to) {
    const ex = (t.ipBlock.except || []).map(String);
    for (const c of ['10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '169.254.0.0/16', '100.64.0.0/10']) if (!ex.includes(c)) out.push(`中継の外への出が ${c} を除かない`);
  }
  return out;
}

function allowlistProblems(docs) {
  const out = [];
  const cm = find(docs, 'ConfigMap', 'egress-proxy-hosts', 'platform-infra');
  const { parseHosts } = require('../deploy/cloud/egress/proxy.js');
  const hosts = new Set(parseHosts(cm ? cm.data.hosts : ''));
  // 設定に在る宛先: LLM ゲートウェイの appsettings の BaseUrl（Voyage）と、Anthropic の SDK の既定（配線の試験が固定する）
  const app = JSON.parse(read('src/platform/backend/Services/LlmGateway/appsettings.json'));
  const urls = [];
  const walk = (o) => { for (const [k, v] of Object.entries(o || {})) { if (k === 'BaseUrl' && typeof v === 'string' && v) urls.push(v); else if (v && typeof v === 'object') walk(v); } };
  walk(app);
  // Anthropic の宛先は SDK の既定（appsettings に書かない）。配線の試験が固定する値を読む。
  for (const m of read('src/platform/backend/Services/LlmGateway/Tests/Infrastructure/ExternalServices/AnthropicClientWiringTests.cs').matchAll(/BaseUrl\.Should\(\)\.Be\("(https:\/\/[^"]+)"\)/g)) urls.push(m[1]);
  const provider = [...new Set(urls.map((u) => new URL(u).hostname))].sort();
  if (provider.length < 2) out.push(`陽性対照: appsettings から提供元の BaseUrl を拾えていない（${provider.join(', ')}）`);
  for (const h of provider) if (!hosts.has(h)) out.push(`許可表に LLM の提供元 ${h}（appsettings の BaseUrl）が無い`);
  // Alertmanager の Discord の受信先の形（render.sh の正規表現の名前）
  const am = find(docs, 'ConfigMap', 'alertmanager-cloud-config', 'platform-infra');
  const m = /\^https:\/\/\(([^)]+)\)\/api\/webhooks/.exec(String(am ? am.data['render.sh'] : ''));
  const discord = m ? m[1].split('|').map((x) => x.replace(/\\\./g, '.')) : [];
  if (!discord.length) out.push('陽性対照: render.sh の Discord の形を読めない');
  for (const h of discord) if (!hosts.has(h)) out.push(`許可表に Discord の受信先 ${h} が無い`);
  // バックアップの保管先は ConfigMap platform-backup-s3-target の endpoint から中継が読む（オーナーが作る）
  const dep = find(docs, 'Deployment', 'egress-proxy', 'platform-infra');
  const vols = dep ? dep.spec.template.spec.volumes : [];
  const backup = vols.find((v) => v.configMap && v.configMap.name === 'platform-backup-s3-target');
  if (!(backup && String(backup.configMap.optional) === 'true' && JSON.stringify(backup.configMap.items) === JSON.stringify([{ key: 'endpoint', path: 'endpoint' }]))) out.push('中継がバックアップの保管先の endpoint だけを読まない');
  const extra = vols.find((v) => v.configMap && v.configMap.name === 'egress-proxy-extra-hosts');
  if (!(extra && String(extra.configMap.optional) === 'true')) out.push('オーナーが埋める表（egress-proxy-extra-hosts）を optional で読まない');
  if (vols.some((v) => v.secret)) out.push('中継が Secret を読む');
  return out;
}

// ---------------------------------------------------------------- N
function esoNamespaceProblems(docs) {
  const out = [];
  const ns = find(docs, 'Namespace', 'external-secrets');
  if (!ns) return ['Namespace external-secrets が無い'];
  for (const m of ['enforce', 'warn', 'audit']) if ((ns.metadata.labels || {})[`pod-security.kubernetes.io/${m}`] !== 'restricted') out.push(`external-secrets の PSA の ${m} が restricted でない`);
  const np = docs.filter((d) => d.kind === 'NetworkPolicy' && d.metadata.namespace === 'external-secrets');
  const deny = np.find((d) => JSON.stringify(d.spec.podSelector) === '{}' && !d.spec.ingress && !d.spec.egress);
  if (!deny || !['Ingress', 'Egress'].every((t) => (deny.spec.policyTypes || []).includes(t))) out.push('external-secrets に入り・出の既定拒否が無い');
  for (const d of np) {
    for (const r of d.spec.egress || []) {
      const to = r.to || [];
      const p = JSON.stringify(portsOf(r));
      const dns = to.length === 1 && nsOf(to[0].namespaceSelector) === 'kube-system' && to[0].podSelector && to[0].podSelector.matchLabels['k8s-app'] === 'kube-dns' && p === '["TCP/53","UDP/53"]';
      const bao = to.length === 1 && nsOf(to[0].namespaceSelector) === 'openbao' && appOf(to[0].podSelector) === 'openbao' && p === '["TCP/8443"]';
      const api = to.length === 0 && p === '["TCP/443","TCP/6443"]';
      if (!(dns || bao || api)) out.push(`external-secrets の出に名前解決・OpenBao の 8443・API サーバーのポートの外がある（${d.metadata.name}: ${JSON.stringify(r)}）`);
    }
    for (const r of d.spec.ingress || []) {
      const webhook = d.spec.podSelector && d.spec.podSelector.matchLabels && d.spec.podSelector.matchLabels['app.kubernetes.io/name'] === 'external-secrets-webhook';
      if (!(webhook && JSON.stringify(portsOf(r)) === '["TCP/10250"]')) out.push(`external-secrets の入りに webhook の 10250 の外がある（${d.metadata.name}）`);
    }
  }
  return out;
}

/** Pod Security の restricted の要件（ESO の chart の Pod。ServiceAccount のトークンは controller が API を使うので載せてよい）。 */
function restrictedPodProblems(docs, where) {
  const out = [];
  for (const d of docs.filter((x) => dc.WORKLOAD_KINDS.includes(x.kind))) {
    const ps = dc.podSpec(d);
    const psc = ps.securityContext || {};
    for (const k of ['hostPID', 'hostIPC', 'hostNetwork']) if (String(ps[k]) === 'true') out.push(`${where}: ${d.metadata.name}: ${k}`);
    if ((ps.volumes || []).some((v) => v.hostPath)) out.push(`${where}: ${d.metadata.name}: hostPath`);
    for (const c of [...(ps.initContainers || []), ...(ps.containers || [])]) {
      const sc = c.securityContext || {};
      const pick = (k) => (sc[k] !== undefined ? sc[k] : psc[k]);
      const id = `${where}: ${d.metadata.name} の ${c.name}`;
      if (String(pick('runAsNonRoot')) !== 'true') out.push(`${id}: runAsNonRoot が true でない`);
      if (String(sc.allowPrivilegeEscalation) !== 'false') out.push(`${id}: allowPrivilegeEscalation が false でない`);
      if (String(sc.privileged) === 'true') out.push(`${id}: privileged`);
      if (!((sc.capabilities || {}).drop || []).map(String).includes('ALL')) out.push(`${id}: capabilities.drop に ALL が無い`);
      if (((sc.capabilities || {}).add || []).length) out.push(`${id}: capability を足している`);
      const sp = (sc.seccompProfile && sc.seccompProfile.type) || (psc.seccompProfile && psc.seccompProfile.type);
      if (!['RuntimeDefault', 'Localhost'].includes(sp)) out.push(`${id}: seccompProfile が RuntimeDefault でない`);
    }
  }
  return out;
}

function auditProblems(readme, policyText, dropinText) {
  const out = [];
  const policy = docsOf(policyText)[0] || {};
  if (policy.kind !== 'Policy' || policy.apiVersion !== 'audit.k8s.io/v1') out.push('監査の方針が audit.k8s.io/v1 の Policy でない');
  const rules = policy.rules || [];
  if (rules.some((r) => ['Request', 'RequestResponse'].includes(String(r.level)))) out.push('監査の方針が要求・応答の本文を残す（Request / RequestResponse）');
  const firstNone = rules.findIndex((r) => r.level === 'None');
  const must = rules.findIndex((r) => r.level === 'Metadata' && (r.resources || []).some((g) => (g.group || '') === '' && (g.resources || []).includes('secrets')));
  if (must < 0 || (firstNone >= 0 && must > firstNone)) out.push('Secret の操作を残す規則が、残さない規則（None）より先に無い');
  for (const res of ['serviceaccounts/token', 'pods/exec']) if (!rules[must] || !rules[must].resources.some((g) => (g.resources || []).includes(res))) out.push(`必ず残す規則に ${res} が無い`);
  const last = rules[rules.length - 1] || {};
  if (!(last.level === 'Metadata' && !last.resources && !last.users && !last.userGroups && !last.verbs && !last.nonResourceURLs)) out.push('最後の規則が「ほかは Metadata」でない（記録の漏れ）');
  if (rules.some((r) => r.level === 'None' && !(r.nonResourceURLs || r.resources || (r.userGroups && r.verbs)))) out.push('条件の無い None がある（全部を捨てる）');
  const args = ((/kube-apiserver-arg\+?:\n((?:\s+- .*\n?)+)/.exec(dropinText) || [])[1] || '').split('\n').map((l) => l.replace(/^\s*-\s*/, '').trim()).filter(Boolean);
  const arg = (k) => (args.find((a) => a.startsWith(`${k}=`)) || '').slice(k.length + 1);
  if (arg('audit-policy-file') !== '/var/lib/rancher/k3s/server/audit-policy.yaml') out.push('API サーバーが監査の方針を /var/lib/rancher の下から読まない');
  if (!arg('audit-log-path').startsWith('/var/lib/rancher/')) out.push('監査の記録が暗号化した領域（/var/lib/rancher）の外に置かれる');
  for (const k of ['audit-log-maxage', 'audit-log-maxbackup', 'audit-log-maxsize']) if (!/^[1-9][0-9]*$/.test(arg(k))) out.push(`監査の記録の保持 ${k} が無い`);
  // 手順書（README §1-4）: 導入（install.sh は k3s を起こす）の前に、暗号化した領域の下へ方針を置き、設定の追加を置く
  const sec = (/### 1-4\. k3s\n([\s\S]*?)\n### /.exec(readme) || [])[1] || '';
  const iPolicy = sec.indexOf('/var/lib/rancher/k3s/server/audit-policy.yaml');
  const iDropin = sec.indexOf('/etc/rancher/k3s/config.yaml.d/20-apiserver-audit.yaml');
  const iRun = sec.indexOf('sh ./k3s-install-');
  if (iPolicy < 0 || iDropin < 0 || iRun < 0 || !(iPolicy < iRun && iDropin < iRun)) out.push('README §1-4 が、導入の前に監査の方針と設定の追加を置かない');
  if (!/scp[^\n]*deploy\/cloud\/k3s\/audit-policy\.yaml[^\n]*deploy\/cloud\/k3s\/config\.yaml\.d\/20-apiserver-audit\.yaml/.test(sec)) out.push('README §1-4 が監査の方針と設定の追加をノードへ運ばない');
  return out;
}

// ---------------------------------------------------------------- C
function probeTargetProblems(cloudDocs, esoTexts, libSh) {
  const out = [];
  const cm = find(cloudDocs, 'ConfigMap', 'tls-cert-probe-config', 'platform-infra');
  const cfg = JSON.parse(cm ? cm.data['targets.json'] : '{"targets":[],"cas":[]}');
  const roles = ((/^OB_PKI_ROLES="([^"]+)"/m.exec(libSh) || [])[1] || '').split(/\s+/).filter(Boolean).map((e) => e.split(':')[0]);
  if (roles.length !== 4) out.push(`陽性対照: lib.sh の OB_PKI_ROLES を読めない（${roles.join(', ')}）`);
  const names = cfg.targets.map((t) => t.name).sort();
  if (JSON.stringify(names) !== JSON.stringify([...roles].sort())) out.push(`測りの宛先が PKI の role と違う（${names.join(', ')} / ${roles.join(', ')}）`);
  const cn = Object.fromEntries([...esoTexts.join('\n').matchAll(/path: pki_cloud\/issue\/([a-z-]+)\n\s+parameters:\n\s+common_name: ([a-z-]+)\n\s+alt_names: "([^"]+)"/g)].map((m) => [m[1], [m[2], ...m[3].split(',')]]));
  const ports = { postgres: 5432, rabbitmq: 5671, keycloak: 8443, 'llmgateway-service': 8080 };
  for (const t of cfg.targets) {
    if (!(cn[t.name] || []).includes(t.servername)) out.push(`${t.name} の servername ${t.servername} が ESO の証明書の名前に無い`);
    if (t.port !== ports[t.name]) out.push(`${t.name} のポートが TLS の口（${ports[t.name]}）でない`);
    if (t.ca !== '/etc/tls-cert-probe/platform-infra-ca/ca.crt') out.push(`${t.name} を基盤の CA で確かめない`);
    if ((t.name === 'postgres') !== (t.protocol === 'postgres')) out.push(`${t.name} の SSLRequest の扱いが違う`);
  }
  if (JSON.stringify(cfg.cas.map((c) => c.name).sort()) !== '["openbao-ca","platform-infra-ca"]') out.push('CA の測りが platform-infra-ca と openbao-ca でない');
  const dep = find(cloudDocs, 'Deployment', 'tls-cert-probe', 'platform-infra');
  const vols = dep ? dep.spec.template.spec.volumes : [];
  if (vols.some((v) => v.secret || v.projected)) out.push('測りが Secret・投影を読む（鍵に触れない）');
  // 受け口の側の入り（別宣言）: 測りの Pod から TLS の口だけ
  const want = {
    'postgres-ingress-tls-cert-probe': [[null, 'tls-cert-probe'], 'TCP/5432'],
    'rabbitmq-ingress-tls-cert-probe': [[null, 'tls-cert-probe'], 'TCP/5671'],
    'keycloak-ingress-tls-cert-probe': [[null, 'tls-cert-probe'], 'TCP/8443'],
    'llmgateway-ingress-tls-cert-probe': [['platform-infra', 'tls-cert-probe'], 'TCP/8080'],
  };
  for (const [name, [from, port]] of Object.entries(want)) {
    const d = cloudDocs.find((x) => x.kind === 'NetworkPolicy' && x.metadata.name === name);
    if (!d || dc.tlsProbeIngressShape(d) !== JSON.stringify([[from], [port]])) out.push(`${name} が「測りの Pod から ${port} だけ」でない`);
  }
  const np = find(cloudDocs, 'NetworkPolicy', 'tls-cert-probe', 'platform-infra');
  const eg = np ? np.spec.egress || [] : [];
  if (!(eg.length === 1 && JSON.stringify(portsOf(eg[0])) === '["TCP/8080"]' && appOf(eg[0].to[0].podSelector) === 'llmgateway-service')) out.push('測りの出が LLM ゲートウェイの 8080 だけでない（同じ名前空間の外）');
  return out;
}

const PLATFORM_RULES = (docs) => {
  const cm = find(docs, 'ConfigMap', 'prometheus-cloud-config', 'platform-infra');
  return { prom: docsOf(`kind: X\n${cm.data['prometheus.yml']}`)[0], rules: docsOf(`kind: X\n${cm.data['platform-rules.yml']}`)[0], rulesText: cm.data['platform-rules.yml'] };
};
function certAlertProblems(cloudDocs, esoText, tlsCertSh) {
  const out = [];
  const { prom, rules } = PLATFORM_RULES(cloudDocs);
  const jobs = Object.fromEntries((prom.scrape_configs || []).map((j) => [j.job_name, j.static_configs[0].targets]));
  if (JSON.stringify(jobs['tls-cert-probe']) !== '["tls-cert-probe:9465"]') out.push('Prometheus が測り（tls-cert-probe:9465）を収集しない');
  if (JSON.stringify(jobs['egress-proxy']) !== '["egress-proxy:9464"]') out.push('Prometheus が外向きの中継（egress-proxy:9464）を収集しない');
  const group = (rules.groups || []).find((g) => g.name === 'cloud-tls-expiry');
  const alerts = Object.fromEntries(((group && group.rules) || []).map((r) => [r.alert, r]));
  for (const a of ['TlsServerCertExpiringSoon', 'TlsServerCertExpiryCritical', 'TlsServerCertNotVerified', 'TlsServerCertProbeMissing', 'PlatformInfraCaExpiringSoon', 'OpenBaoListenerCertExpiringSoon', 'TlsCaCertMissing']) {
    if (!alerts[a]) out.push(`警報 ${a} が無い`);
  }
  const days = (expr) => Number((/< (\d+) \* 86400/.exec(String(expr)) || [])[1]);
  const refreshH = Number((/refreshInterval: (\d+)h/.exec(esoText) || [])[1]);
  const ttlH = Number((/ttl: (\d+)h/.exec(esoText) || [])[1]);
  const warn = days(alerts.TlsServerCertExpiringSoon && alerts.TlsServerCertExpiringSoon.expr);
  // 作り直しの直前の残り（ttl − refresh）より小さく（平時に鳴らない）、危急（30 日）より大きい
  if (!(refreshH > 0 && ttlH > refreshH && warn * 24 < ttlH - refreshH && warn > 30)) out.push(`期限の警報の閾値（${warn} 日）が作り直しの周期（${refreshH}h）・期限（${ttlH}h）と合わない`);
  if ((alerts.TlsServerCertProbeMissing && !/< 4$/.test(String(alerts.TlsServerCertProbeMissing.expr).trim()))) out.push('測れている受け口の数の閾値が 4 でない');
  // OpenBao の受け口: CA（730 日）と受け口（365 日）を同じ回に作る（tls-cert.sh）。警報は CA − 365 日で導く。
  const caDays = Number((/openssl req -x509 -new -sha256 -days (\d+)/.exec(tlsCertSh) || [])[1]);
  const leafDays = Number((/openssl x509 -req -sha256 -days (\d+)/.exec(tlsCertSh) || [])[1]);
  const ob = String((alerts.OpenBaoListenerCertExpiringSoon || {}).expr || '');
  if (!(caDays === 730 && leafDays === 365)) out.push(`tls-cert.sh の日数が想定（CA 730・受け口 365）と違う（${caDays}・${leafDays}）。警報の導出を見直す`);
  if (!ob.includes(`- ${leafDays} * 86400`) || !/max\(tls_ca_cert_not_after_seconds\{ca="openbao-ca"\}\)/.test(ob)) out.push('OpenBao の受け口の期限を、最新の CA の notAfter から受け口の日数を引いて導かない');
  const eg = (rules.groups || []).find((g) => g.name === 'cloud-egress');
  for (const a of ['EgressProxyDenied', 'EgressProxyReportMode', 'EgressProxyDown']) if (!((eg && eg.rules) || []).some((r) => r.alert === a)) out.push(`警報 ${a} が無い`);
  return out;
}

/** promtool の単体試験: 期限の警報が残り 150 日・30 日の境で鳴る・鳴らない、OpenBao の受け口の導出、測りの不在。 */
function promtoolCertAlertTest(cloudDocs) {
  const { rulesText } = PLATFORM_RULES(cloudDocs);
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'cert-alerts-'));
  try {
    const rf = path.join(dir, 'rules.yml');
    // 注釈は $value の文字で揺れるので、単体試験の照合から外す（cloud-ops.test.js と同じ扱い）。
    const lines = rulesText.split('\n');
    const kept = [];
    for (let i = 0; i < lines.length; i += 1) {
      const m = /^(\s*)annotations:\s*$/.exec(lines[i]);
      if (!m) { kept.push(lines[i]); continue; }
      while (i + 1 < lines.length && (lines[i + 1].trim() === '' || /^\s*/.exec(lines[i + 1])[0].length > m[1].length)) i += 1;
    }
    fs.writeFileSync(rf, kept.join('\n'));
    const day = 86400;
    // 評価の時刻（time()）は試験の始まりからの秒。始まりを 0 として、2h の評価の時点で残りを決める。
    const at = 2 * 3600;
    const series = (target, remainDays) => `      - series: 'tls_probe_cert_not_after_seconds{target="${target}"}'\n        values: '${at + remainDays * day}x150'\n`;
    const verified = (target, v) => `      - series: 'tls_probe_verified{target="${target}"}'\n        values: '${v}x150'\n      - series: 'tls_probe_up{target="${target}"}'\n        values: '1x150'\n`;
    const test = (expAlerts) => `rule_files:\n  - ${rf}\nevaluation_interval: 1m\ntests:\n  - interval: 1m\n    input_series:\n`
      + series('postgres', 140) + series('rabbitmq', 200) + series('keycloak', 20) + series('llmgateway-service', 364)
      + verified('postgres', 1) + verified('rabbitmq', 1) + verified('keycloak', 1)
      + `      - series: 'tls_ca_cert_not_after_seconds{ca="openbao-ca",serial="a"}'\n        values: '${at + (365 + 100) * day}x150'\n`
      + `      - series: 'tls_ca_cert_not_after_seconds{ca="openbao-ca",serial="b"}'\n        values: '${at + (365 + 10) * day}x150'\n`
      + `      - series: 'tls_ca_cert_not_after_seconds{ca="platform-infra-ca",serial="c"}'\n        values: '${at + 3000 * day}x150'\n`
      + `    alert_rule_test:\n${expAlerts}`;
    const exp = [
      '      - eval_time: 2h\n        alertname: TlsServerCertExpiringSoon\n        exp_alerts:\n'
        + '          - exp_labels: { severity: warning, target: postgres }\n'
        + '          - exp_labels: { severity: warning, target: keycloak }\n',
      '      - eval_time: 2h\n        alertname: TlsServerCertExpiryCritical\n        exp_alerts:\n          - exp_labels: { severity: critical, target: keycloak }\n',
      // 束の最新の CA（残り 365 + 100 日）から導くので、受け口は残り 100 日 → 鳴る（古い CA の 10 日で鳴らすのではない）
      '      - eval_time: 2h\n        alertname: OpenBaoListenerCertExpiringSoon\n        exp_alerts:\n          - exp_labels: { severity: warning }\n',
      '      - eval_time: 2h\n        alertname: PlatformInfraCaExpiringSoon\n        exp_alerts: []\n',
      // llmgateway-service は tls_probe_up が無い → 測れているのは 3 つ
      '      - eval_time: 2h\n        alertname: TlsServerCertProbeMissing\n        exp_alerts:\n          - exp_labels: { severity: warning }\n',
      '      - eval_time: 2h\n        alertname: TlsCaCertMissing\n        exp_alerts: []\n',
    ].join('');
    const tf = path.join(dir, 'test.yml');
    fs.writeFileSync(tf, test(exp));
    const okRun = spawnSync('promtool', ['test', 'rules', tf], { encoding: 'utf8' });
    if (okRun.error) throw new Error('promtool が PATH に無い');
    assert.strictEqual(okRun.status, 0, okRun.stdout + okRun.stderr);
    // 陰性対照: rabbitmq（残り 200 日）が鳴ると期待すると落ちる（閾値が空回りしていない）
    fs.writeFileSync(tf, test(exp.replace('          - exp_labels: { severity: warning, target: keycloak }\n', '          - exp_labels: { severity: warning, target: keycloak }\n          - exp_labels: { severity: warning, target: rabbitmq }\n')));
    const ng = spawnSync('promtool', ['test', 'rules', tf], { encoding: 'utf8' });
    assert.notStrictEqual(ng.status, 0, '残り 200 日でも鳴ると期待して promtool が通る');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

module.exports = { keycloakBootstrapProblems, directEgressProblems, portsOnlyEgress, externalClientProblems, proxyProblems, allowlistProblems, esoNamespaceProblems, restrictedPodProblems, auditProblems, probeTargetProblems, certAlertProblems };

if (require.main === module) {
  let failed = 0;
  const ok = (name, fn) => {
    try {
      fn();
      console.log(`  ok  ${name}`);
    } catch (e) {
      failed += 1;
      console.log(`  NG  ${name}\n${e.message}`);
    }
  };
  const child = (rel) => {
    const t = spawnSync(process.execPath, [path.join(ROOT, rel)], { encoding: 'utf8' });
    assert.strictEqual(t.status, 0, `${rel} が落ちた:\n${t.stdout}${t.stderr}`);
  };
  const clone = (x) => JSON.parse(JSON.stringify(x));
  const cloud = kustomize('deploy/cloud');
  const infra = kustomize('deploy/cloud/infra');
  const chart = renderChart();
  const report = kustomize('deploy/cloud-transition/egress-proxy-report');
  const esoNs = kustomize('deploy/cloud/external-secrets');
  const readme = read('deploy/cloud/README.md');
  const esoTexts = ['deploy/cloud/vault/eso/tls-platform-infra.yaml', 'deploy/cloud/vault/eso/tls-microservices-platform.yaml'].map(read);

  ok('K1: Keycloak の Deployment は KC_BOOTSTRAP_ADMIN_* を持たない（基盤と全体の描画）', () => {
    assert.deepStrictEqual(keycloakBootstrapProblems(infra), []);
    assert.deepStrictEqual(keycloakBootstrapProblems(cloud), []);
    const bad = clone(infra);
    find(bad, 'Deployment', 'keycloak').spec.template.spec.containers[0].env.push({ name: 'KC_BOOTSTRAP_ADMIN_PASSWORD', valueFrom: { secretKeyRef: { name: 'keycloak-admin', key: 'password' } } });
    assert.ok(keycloakBootstrapProblems(bad).length === 1, '陰性対照: 一時管理者の env を検出しない');
  });
  ok('K2: 開け閉めの Job と入口の形（Secret への参照だけ・3 つの段・--live 必須・値を扱わない）と、手順と入口の振る舞いの試験', () => {
    const job = docsOf(read('deploy/cloud/keycloak-admin/job.yaml'))[0];
    const pod = dc.podSpec(job);
    const c = pod.containers[0];
    const env = envOf(c);
    assert.strictEqual(job.metadata.namespace, 'platform-infra');
    assert.strictEqual(job.spec.template.metadata.labels.app, 'keycloak-master-admin');
    assert.strictEqual(Number(job.spec.backoffLimit), 0);
    assert.deepStrictEqual([env.MODE.value, env.KC_URL.value, env.NODE_EXTRA_CA_CERTS.value], ['check', 'https://keycloak:8443', '/etc/platform-infra-ca/ca.crt']);
    for (const k of ['KC_ADMIN_USER', 'KC_ADMIN_PASSWORD']) assert.strictEqual(env[k].valueFrom.secretKeyRef.name, 'keycloak-admin', `${k} が Secret から読まない`);
    const addClients = docsOf(read('deploy/cloud/keycloak-clients/job.yaml'))[0];
    assert.strictEqual(c.image, dc.podSpec(addClients).containers[0].image, 'keycloak-clients の Job と同じ digest でない');
    assert.deepStrictEqual(dc.podHardeningProblems([job], 'keycloak-admin'), []);
    const sh = read('deploy/cloud/keycloak-admin/keycloak-admin.sh');
    assert.match(sh, /open \| close \| check\) ;;/);
    assert.match(sh, /live_opt_in_require "keycloak-admin\.sh"/);
    assert.match(sh, /set env deploy\/keycloak --from=secret\/keycloak-admin --keys=username,password --prefix=KC_BOOTSTRAP_ADMIN_/);
    assert.match(sh, /set env deploy\/keycloak KC_BOOTSTRAP_ADMIN_USERNAME- KC_BOOTSTRAP_ADMIN_PASSWORD-/);
    assert.ok(!/--from-literal|get secret[^\n]*-o|base64 -d|\$KC_ADMIN_PASSWORD/.test(sh), '入口が値を扱う');
    const r = spawnSync('bash', [path.join(ROOT, 'deploy/cloud/keycloak-admin/keycloak-admin.sh'), 'open'], { encoding: 'utf8', env: { PATH: process.env.PATH } });
    assert.strictEqual(r.status, 3, '--live なしで動いた');
    child('deploy/cloud/keycloak-admin/master-admin.test.js');
    // 入口の振る舞い（open → close・close の流し直し・本当の失敗との区別）は kubectl と Job のスタブで確かめる
    child('deploy/cloud/keycloak-admin/keycloak-admin.test.js');
  });

  ok('E1: ipBlock で宛先を名指す出は外向きの中継だけ（基盤・chart・vault・ESO）。ポートだけの出は OpenBao と ESO の API サーバー向けの 2 つだけ', () => {
    const vault = kustomize('deploy/cloud/vault');
    assert.deepStrictEqual(directEgressProblems(cloud, 'deploy/cloud'), []);
    assert.deepStrictEqual(directEgressProblems(chart, 'chart'), []);
    assert.deepStrictEqual(directEgressProblems(vault, 'vault'), []);
    assert.deepStrictEqual(directEgressProblems(esoNs, 'external-secrets'), []);
    // ipBlock の検査が見ない範囲（宛先を問わない出）を名指して固定する（IADR-0704 残余 5）
    const portsOnly = [...new Set([cloud, chart, vault, esoNs].flatMap(portsOnlyEgress))].sort();
    assert.deepStrictEqual(portsOnly, ['external-secrets/eso-egress-dns-openbao-apiserver TCP/443,TCP/6443', 'openbao/openbao-egress-dns-and-apiserver TCP/443,TCP/6443'].sort(), `宛先を問わない出が想定の 2 つと違う:\n${portsOnly.join('\n')}`);
    const extra = clone(cloud);
    find(extra, 'NetworkPolicy', 'platform-backup-egress').spec.egress.push({ ports: [{ protocol: 'TCP', port: 443 }] });
    assert.ok(portsOnlyEgress(extra).some((x) => /platform-backup-egress/.test(x)), '陰性対照: ポートだけの出の追加を拾わない');
    assert.ok(cloud.some((d) => d.kind === 'NetworkPolicy' && d.metadata.name === 'egress-proxy'), '陽性対照: 中継の NetworkPolicy が描画に無い');
    const bad = clone(cloud);
    find(bad, 'NetworkPolicy', 'platform-backup-egress').spec.egress.push({ to: [{ ipBlock: { cidr: '0.0.0.0/0' } }], ports: [{ protocol: 'TCP', port: 443 }] });
    assert.ok(directEgressProblems(bad, 'x').some((p) => /platform-backup-egress/.test(p)), '陰性対照: バックアップの直の 443 を検出しない');
  });
  ok('E2: 外の 443 を使う 3 つは中継の 3128 へ出て、中継を使う設定を持つ', () => {
    assert.deepStrictEqual(externalClientProblems(cloud, chart), []);
    const bad = clone(cloud);
    const am = find(bad, 'ConfigMap', 'alertmanager-cloud-config');
    am.data['alertmanager.yml.tmpl'] = am.data['alertmanager.yml.tmpl'].replace(/global:\n\s+http_config:\n\s+proxy_url: \S+\n/, '');
    const cj = bad.find((d) => d.kind === 'CronJob');
    dc.podSpec(cj).containers[0].env = dc.podSpec(cj).containers[0].env.filter((e) => e.name !== 'HTTPS_PROXY');
    const p = externalClientProblems(bad, chart);
    for (const want of [/Alertmanager の global\.http_config\.proxy_url/, /バックアップに外向きの中継の HTTPS_PROXY が無い/]) assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
  });
  ok('E3: 中継は最終の段で enforce。移す間の重ねは report で、3 つの直の 443 を戻し、最終の段の判定では落ちる', () => {
    assert.deepStrictEqual(proxyProblems(cloud, { mode: 'enforce' }), []);
    assert.deepStrictEqual(proxyProblems(report, { mode: 'report' }), []);
    const direct = directEgressProblems(report, 'report');
    assert.deepStrictEqual(direct.map((p) => p.replace(/^report: /, '').split(' ')[0]).sort(), ['microservices-platform/llmgateway-egress-direct-https-transition', 'platform-infra/alertmanager-egress-direct-https-transition', 'platform-infra/platform-backup-egress-direct-https-transition']);
    assert.ok(proxyProblems(report, { mode: 'enforce' }).some((p) => /段が enforce でない/.test(p)), '重ねの report を検出しない');
    // 重ねは中継の段と直の 443 の外を変えない（ほかの資源は最終の段と同じ）
    const key = (d) => `${d.kind}/${d.metadata.namespace}/${d.metadata.name}`;
    const base = new Map(cloud.map((d) => [key(d), JSON.stringify(d)]));
    const changed = report.filter((d) => base.get(key(d)) !== JSON.stringify(d)).map(key).sort();
    assert.deepStrictEqual(changed, ['Deployment/platform-infra/egress-proxy', 'NetworkPolicy/microservices-platform/llmgateway-egress-direct-https-transition', 'NetworkPolicy/platform-infra/alertmanager-egress-direct-https-transition', 'NetworkPolicy/platform-infra/platform-backup-egress-direct-https-transition']);
  });
  ok('E4: 許可表は設定に在る宛先を全部持ち、中継の入りは 3 つの呼び手と Prometheus だけ', () => {
    assert.deepStrictEqual(allowlistProblems(cloud), []);
    const bad = clone(cloud);
    const cm = find(bad, 'ConfigMap', 'egress-proxy-hosts');
    cm.data.hosts = cm.data.hosts.replace('api.voyageai.com\n', '');
    find(bad, 'NetworkPolicy', 'egress-proxy').spec.ingress[0].from.push({ podSelector: { matchLabels: { app: 'keycloak' } } });
    assert.ok(allowlistProblems(bad).some((p) => /api\.voyageai\.com/.test(p)), '陰性対照: 提供元の欠けを検出しない');
    assert.ok(proxyProblems(bad, { mode: 'enforce' }).some((p) => /送り元が 3 つの呼び手だけでない/.test(p)), '陰性対照: 送り元の追加を検出しない');
  });
  ok('E5: 中継の手順の試験', () => child('deploy/cloud/egress/proxy.test.js'));

  ok('N1: external-secrets の名前空間は PSA restricted・既定で拒否・出は名前解決と OpenBao の 8443 と API サーバーのポートだけ・入りは webhook だけ', () => {
    assert.deepStrictEqual(esoNamespaceProblems(esoNs), []);
    const bad = clone(esoNs);
    find(bad, 'Namespace', 'external-secrets').metadata.labels['pod-security.kubernetes.io/enforce'] = 'baseline';
    find(bad, 'NetworkPolicy', 'eso-egress-dns-openbao-apiserver').spec.egress.push({ to: [{ ipBlock: { cidr: '0.0.0.0/0' } }], ports: [{ protocol: 'TCP', port: 443 }] });
    const p = esoNamespaceProblems(bad);
    for (const want of [/enforce が restricted でない/, /出に名前解決・OpenBao/]) assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
  });
  ok('N2: 手順書は名前空間を helm に作らせず宣言を chart より先に当てる。chart 2.8.0 の Pod は restricted を満たす', () => {
    const sec3 = (/## 3\. 配備の順序\n([\s\S]*?)\n## 4\./.exec(readme) || [])[1] || '';
    const helm = sec3.indexOf('helm upgrade --install external-secrets');
    assert.ok(helm > 0, '陽性対照: README §3 に ESO の導入が無い');
    assert.ok(!/external-secrets[^\n]*--create-namespace|--create-namespace[^\n]*external-secrets/.test(sec3.replace(/\\\n\s*/g, ' ')), 'ESO の名前空間を helm に作らせている');
    const ns = sec3.indexOf('kubectl apply -k deploy/cloud/external-secrets');
    assert.ok(ns > 0 && ns < helm, '名前空間の宣言を chart より先に当てない');
    assert.ok(!/--create-namespace/.test(read('deploy/cloud/vault/external-secrets-values.yaml').split('\n').filter((l) => /^#\s+-n external-secrets/.test(l)).join('\n')), 'values の注記の手順が名前空間を helm に作らせる');
    const tgz = run('bash', ['-c', '. scripts/lib/pinned-artifacts.sh && PINNED_ARTIFACTS_DIR="$1" && pinned_work_init && pinned_chart external-secrets 2.8.0 "$PINNED_WORK"', 'x', path.join(os.tmpdir(), 'deploy-cloud-egress-audit-charts')]).trim();
    const eso = docsOf(run('helm', ['template', 'external-secrets', tgz, '-n', 'external-secrets', '-f', 'deploy/cloud/vault/external-secrets-values.yaml']));
    const workloads = eso.filter((d) => dc.WORKLOAD_KINDS.includes(d.kind));
    assert.strictEqual(workloads.length, 3, `陽性対照: chart の Pod の宣言が 3 つでない（${workloads.map((d) => d.metadata.name).join(', ')}）`);
    assert.deepStrictEqual(restrictedPodProblems(eso, 'external-secrets'), []);
    // webhook の Pod の名前（入りの許可の podSelector）と受け口のポートが chart と同じ
    const wh = workloads.find((d) => d.metadata.name === 'external-secrets-webhook');
    assert.strictEqual(wh.spec.template.metadata.labels['app.kubernetes.io/name'], 'external-secrets-webhook');
    assert.ok(wh.spec.template.spec.containers[0].ports.some((p) => Number(p.containerPort) === 10250), 'webhook の受け口が 10250 でない');
    const ctl = workloads.find((d) => d.metadata.name === 'external-secrets');
    assert.strictEqual(ctl.spec.template.metadata.labels['app.kubernetes.io/name'], 'external-secrets', 'controller の名前が OpenBao の入りの許可と違う');
    const bad = clone(eso);
    dc.podSpec(bad.find((d) => d.kind === 'Deployment' && d.metadata.name === 'external-secrets')).containers[0].securityContext.allowPrivilegeEscalation = true;
    assert.ok(restrictedPodProblems(bad, 'x').length > 0, '陰性対照: 権限の昇格を検出しない');
  });
  ok('N3: k3s の API サーバーの監査は、本文を残さない方針と暗号化した領域の置き場を持ち、手順書が導入の前に置く', () => {
    const policy = read('deploy/cloud/k3s/audit-policy.yaml');
    const dropin = read('deploy/cloud/k3s/config.yaml.d/20-apiserver-audit.yaml');
    assert.deepStrictEqual(auditProblems(readme, policy, dropin), []);
    const p = auditProblems(readme.replace(/\/etc\/rancher\/k3s\/config\.yaml\.d\/20-apiserver-audit\.yaml/g, '/tmp/x.yaml'), policy.replace(/- level: Metadata\n {4}resources:\n {6}- group: ""\n {8}resources: \[secrets/, '- level: RequestResponse\n    resources:\n      - group: ""\n        resources: [secrets'), dropin.replace('/var/lib/rancher/k3s/server/logs/audit.log', '/var/log/audit.log'));
    for (const want of [/本文を残す/, /Secret の操作を残す規則/, /暗号化した領域/, /導入の前に/]) assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
    // k3s の起動の旗（INSTALL_K3S_EXEC）は変えない（監査は設定の追加で足す。deploy-cloud.test.js の K3S_FLAGS が固定する）
    assert.deepStrictEqual(dc.k3sInstallProblems(readme, read('scripts/lib/pinned-artifacts.sh')), []);
  });

  ok('C1: 測りの宛先は PKI の role と同じ 4 つで名前は ESO の証明書と同じ。受け口の入りは測りの Pod から TLS の口だけ', () => {
    assert.deepStrictEqual(probeTargetProblems(cloud, esoTexts, read('deploy/cloud/vault/lib.sh')), []);
    const bad = clone(cloud);
    const cm = find(bad, 'ConfigMap', 'tls-cert-probe-config');
    const cfg = JSON.parse(cm.data['targets.json']);
    cfg.targets = cfg.targets.filter((t) => t.name !== 'rabbitmq');
    cfg.targets[0].servername = 'postgres.example';
    cm.data['targets.json'] = JSON.stringify(cfg);
    find(bad, 'NetworkPolicy', 'postgres-ingress-tls-cert-probe').spec.ingress[0].from = [{ podSelector: {} }];
    const p = probeTargetProblems(bad, esoTexts, read('deploy/cloud/vault/lib.sh'));
    for (const want of [/PKI の role と違う/, /servername postgres\.example/, /postgres-ingress-tls-cert-probe/]) assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
  });
  ok('C2: Prometheus が測りを収集し、期限・検証・不在・CA・OpenBao の受け口の警報を持つ（閾値・導出は作りと同じ。promtool の単体試験）', () => {
    const tlsCertSh = read('deploy/cloud/vault/tls-cert.sh');
    assert.deepStrictEqual(certAlertProblems(cloud, esoTexts[0], tlsCertSh), []);
    const p = certAlertProblems(cloud, esoTexts[0], tlsCertSh.replace('-days 365', '-days 400'));
    assert.ok(p.some((x) => /tls-cert\.sh の日数/.test(x)), '陰性対照: 受け口の日数の変更を検出しない');
    assert.ok(certAlertProblems(cloud, esoTexts[0].replace(/refreshInterval: 4380h/g, 'refreshInterval: 8000h'), tlsCertSh).some((x) => /閾値/.test(x)), '陰性対照: 作り直しの周期との不整合を検出しない');
    promtoolCertAlertTest(cloud);
  });
  ok('C3: 測りの手順の試験', () => child('deploy/cloud/tls-cert-probe/probe.test.js'));

  if (failed) {
    console.error(`✗ ${failed} 件が失敗した`);
    process.exit(1);
  }
  console.log('✓ deploy/cloud の外向きの通信・一時管理者・ESO の名前空間・監査・証明書の期限は受け入れ基準を満たす');
}
