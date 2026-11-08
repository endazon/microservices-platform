#!/usr/bin/env node
'use strict';
/**
 * NFR-18, NFR-16, NFR-21, NFR-05, IADR-0705: クラウドの最小構成（deploy/cloud）の、外向きの通信・Keycloak の一時管理者・ESO の名前空間・
 * API サーバーの監査・サーバ証明書の期限の警報を、宣言と描画で固定する（クラスタに触れない）。
 *
 *   K（一時管理者）
 *     K1 Keycloak の Deployment は KC_BOOTSTRAP_ADMIN_* を持たない（一時管理者を常に置かない）。
 *     K2 開け閉めの Job と入口の形（Secret への参照だけ・段は open / close / check・--live が無ければ動かない・値を扱わない）と、手順の試験。
 *        入口の振る舞い（close は流し直せる・Keycloak に繋がらなければ止まる）は keycloak-admin.test.js が kubectl と Job のスタブで確かめる。
 *   E（外向きの通信）
 *     E1 宛先を ipBlock で名指して出る許可は外向きの中継の Pod だけ（基盤・chart・vault・ESO の名前空間の描画）。
 *        🔴 この検査は ipBlock だけを見る。宛先（to）を持たずポートだけで許す出は宛先を問わず出られ、ここでは止まらない。その出は
 *        OpenBao と ESO の API サーバー向け（443・6443）の 2 つだけであることを固定する（増えたら赤。IADR-0705 残余 5）。
 *     E2 外の 443 を使う 3 つ（LLM ゲートウェイ・バックアップ・Alertmanager）は中継の自分の受け口（と移す間の 3128）へ出て、自分の受け口を使う設定を持つ。
 *     E3 中継は最終の段で enforce（送り元ごとの段は report。IADR-0709 決定 3）。移す間の重ね（cloud-transition/egress-proxy-report）は report で、3 つの直の 443 を戻す。
 *        送り元と受け口（env EGRESS_PROXY_SOURCES）・コンテナと Service のポート・NetworkPolicy の入り（受け口ごとに名前空間 かつ Pod が 1 つ）が同じ組。
 *     E4 許可表は送り元ごと（IADR-0709）: 設定に在る宛先（LLM の提供元・Discord の形）をそれを使う送り元の行にだけ持ち、クラウドで使わない宛先
 *        （埋め込みの提供元）を持たず、表の送り元はどれも受け口を持つ。取引ユニットの外へ出る 6 つと OpenD の送り元を持つ。
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

// IADR-0709 決定 1: 送り元・受け口・Pod（名前空間/app）。中継の env・ポート・NetworkPolicy の入りと同じ組（proxyProblems が突き合わせる）。
const PROXY_SOURCES = [
  ['llmgateway', 3131, 'microservices-platform', 'llmgateway-service'],
  ['backup', 3132, 'platform-infra', 'platform-backup'],
  ['alertmanager', 3133, 'platform-infra', 'alertmanager'],
  ['ast-information-collection', 3141, 'ai-stock-trading', 'information-collection-service'],
  ['ast-market-monitor', 3142, 'ai-stock-trading', 'market-monitor-service'],
  ['ast-notification', 3143, 'ai-stock-trading', 'notification-service'],
  ['ast-report', 3144, 'ai-stock-trading', 'report-service'],
  ['ast-risk-management', 3145, 'ai-stock-trading', 'risk-management-service'],
  ['ast-trade-decision', 3146, 'ai-stock-trading', 'trade-decision-service'],
  ['ast-opend', 3147, 'ai-stock-trading', 'opend'],
];
const portOfSource = (src) => PROXY_SOURCES.find((x) => x[0] === src)[1];
const proxyUrls = (port) => [`http://egress-proxy:${port}`, `http://egress-proxy.platform-infra.svc.cluster.local:${port}`];
// [名前空間, app, NetworkPolicy, 送り元]
const EXTERNAL_CLIENTS = [
  ['microservices-platform', 'llmgateway-service', 'llmgateway-egress', 'llmgateway'],
  ['platform-infra', 'platform-backup', 'platform-backup-egress', 'backup'],
  ['platform-infra', 'alertmanager', 'alertmanager-cloud', 'alertmanager'],
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

// IADR-0709 決定 4: API サーバーの番地（Pod が出す宛先 Service kubernetes の ClusterIP:443 と、ノードで書き換わった先 127.0.0.1:6443）。
//   k3s の --advertise-address 127.0.0.1 と、--service-cidr を渡さない（既定の 10.43.0.0/16 の先頭）ことから決まる（apiServerPinProblems が README と突き合わせる）。
const API_SERVER_PINS = [['10.43.0.1/32', 'TCP/443'], ['127.0.0.1/32', 'TCP/6443']];
/** API サーバーの番地を名指す規則（ipBlock 1 つ・except なし・ポート 1 つで、API_SERVER_PINS の組）。 */
const isApiServerPin = (r) => (r.to || []).length === 1 && r.to[0].ipBlock && !(r.to[0].ipBlock.except || []).length && Object.keys(r.to[0]).length === 1
  && API_SERVER_PINS.some(([cidr, port]) => r.to[0].ipBlock.cidr === cidr && JSON.stringify(portsOf(r)) === JSON.stringify([port]));
// API サーバーの番地を名指してよい Pod（名前空間/選ぶ Pod）。増やすときは理由を IADR に書く。
const API_SERVER_CALLERS = ['external-secrets/*', 'openbao/openbao', 'platform-infra/otel-collector'];

/** クラスタの外の範囲（ipBlock）へ出る許可を持つ NetworkPolicy のうち、外向きの中継の Pod 以外を選ぶもの（API サーバーの番地の名指しは除く）。 */
function directEgressProblems(docs, where) {
  const out = [];
  for (const d of docs.filter((x) => x.kind === 'NetworkPolicy')) {
    const selected = `${d.metadata.namespace}/${appOf(d.spec.podSelector) || (JSON.stringify(d.spec.podSelector) === '{}' ? '*' : '?')}`;
    if ((d.spec.egress || []).some(isApiServerPin) && !API_SERVER_CALLERS.includes(selected)) out.push(`${where}: ${d.metadata.namespace}/${d.metadata.name} が API サーバーの番地へ出る（許した呼び手でない: ${selected}）`);
    const toOutside = (d.spec.egress || []).some((r) => !isApiServerPin(r) && (r.to || []).some((t) => t.ipBlock));
    if (!toOutside) continue;
    if (!(d.metadata.namespace === 'platform-infra' && appOf(d.spec.podSelector) === 'egress-proxy')) out.push(`${where}: ${d.metadata.namespace}/${d.metadata.name} がクラスタの外の範囲へ直に出る`);
  }
  return out;
}

function externalClientProblems(cloudDocs, chartDocs) {
  const out = [];
  for (const [ns, app, policy, source] of EXTERNAL_CLIENTS) {
    const np = find(cloudDocs, 'NetworkPolicy', policy, ns);
    if (!np) { out.push(`${policy} が無い`); continue; }
    const toProxy = (np.spec.egress || []).filter((r) => (r.to || []).some((t) => appOf(t.podSelector) === 'egress-proxy' && (nsOf(t.namespaceSelector) || np.metadata.namespace) === 'platform-infra'));
    const want = JSON.stringify(['TCP/3128', `TCP/${portOfSource(source)}`].sort());
    if (!(toProxy.length === 1 && JSON.stringify(portsOf(toProxy[0])) === want)) out.push(`${policy}: 外向きの中継への出がちょうど 1 つ（3128 と送り元 ${source} の受け口）でない`);
    if ((np.spec.egress || []).some((r) => portsOf(r).includes('TCP/443'))) out.push(`${policy}: 443 へ直に出る規則が残っている（${app}）`);
  }
  // 中継を使う設定
  const gw = chartDocs.find((d) => d.kind === 'Deployment' && /llmgateway/.test(d.metadata.name));
  const gwEnv = gw ? envOf(gw.spec.template.spec.containers[0]) : {};
  if (!(gwEnv.HTTPS_PROXY && proxyUrls(portOfSource('llmgateway')).includes(String(gwEnv.HTTPS_PROXY.value)))) out.push('LLM ゲートウェイに外向きの中継の自分の受け口の HTTPS_PROXY が無い');
  const noProxy = String((gwEnv.NO_PROXY || {}).value || '').split(',');
  for (const h of ['keycloak', 'otel-collector']) if (!noProxy.includes(h)) out.push(`LLM ゲートウェイの NO_PROXY に ${h} が無い（クラスタの中を中継へ回す）`);
  const cj = find(cloudDocs, 'CronJob', 'platform-backup-postgres', 'platform-infra') || cloudDocs.find((d) => d.kind === 'CronJob');
  const bEnv = cj ? envOf(dc.podSpec(cj).containers[0]) : {};
  if (!(bEnv.HTTPS_PROXY && proxyUrls(portOfSource('backup')).includes(String(bEnv.HTTPS_PROXY.value)))) out.push('バックアップに外向きの中継の自分の受け口の HTTPS_PROXY が無い');
  const am = find(cloudDocs, 'ConfigMap', 'alertmanager-cloud-config', 'platform-infra');
  const tmpl = am ? docsOf(`kind: X\n${am.data['alertmanager.yml.tmpl']}`)[0] : {};
  const proxyUrl = tmpl && tmpl.global && tmpl.global.http_config && tmpl.global.http_config.proxy_url;
  if (!proxyUrls(portOfSource('alertmanager')).includes(String(proxyUrl))) out.push('Alertmanager の global.http_config.proxy_url が外向きの中継の自分の受け口でない');
  return out;
}

function proxyProblems(docs, { mode, sourceMode = 'report' }) {
  const out = [];
  const dep = find(docs, 'Deployment', 'egress-proxy', 'platform-infra');
  if (!dep) return ['外向きの中継の Deployment が無い'];
  const c = dep.spec.template.spec.containers[0];
  const env = envOf(c);
  if (String((env.EGRESS_PROXY_MODE || {}).value) !== mode) out.push(`外向きの中継の段が ${mode} でない（${(env.EGRESS_PROXY_MODE || {}).value}）`);
  if (String((env.EGRESS_PROXY_SOURCE_MODE || {}).value) !== sourceMode) out.push(`外向きの中継の送り元ごとの段が ${sourceMode} でない（${(env.EGRESS_PROXY_SOURCE_MODE || {}).value}）`);
  // 送り元と受け口（env）・コンテナのポート・Service のポートが同じ組
  const { parseListeners } = require('../deploy/cloud/egress/proxy.js');
  let listeners = [];
  try { listeners = parseListeners(String((env.EGRESS_PROXY_SOURCES || {}).value || '')); } catch (e) { out.push(`EGRESS_PROXY_SOURCES が読めない（${e.message}）`); }
  const want = PROXY_SOURCES.map(([src, port]) => `${src}:${port}`);
  if (JSON.stringify(listeners.map((l) => `${l.source}:${l.port}`)) !== JSON.stringify(want)) out.push(`送り元と受け口が表と違う（${listeners.map((l) => `${l.source}:${l.port}`).join(' ')}）`);
  const cports = (c.ports || []).map((x) => Number(x.containerPort)).sort((a, b) => a - b);
  const svc = find(docs, 'Service', 'egress-proxy', 'platform-infra');
  const sports = ((svc && svc.spec.ports) || []).map((x) => `${x.port}>${x.targetPort}`).sort();
  const allPorts = [3128, ...PROXY_SOURCES.map((x) => x[1]), 9464].sort((a, b) => a - b);
  if (JSON.stringify(cports) !== JSON.stringify(allPorts)) out.push(`中継のコンテナのポートが受け口と違う（${cports.join(',')}）`);
  if (JSON.stringify(sports) !== JSON.stringify(allPorts.map((p) => `${p}>${p}`).sort())) out.push(`中継の Service のポートが受け口と違う（${sports.join(',')}）`);
  const np = find(docs, 'NetworkPolicy', 'egress-proxy', 'platform-infra');
  if (!np) return [...out, '外向きの中継の NetworkPolicy が無い'];
  const ing = np.spec.ingress || [];
  const fromOf = (f) => `${nsOf(f.namespaceSelector) || '(名前空間を名指さない)'}/${appOf(f.podSelector)}`;
  const legacyIn = ing.filter((r) => JSON.stringify(portsOf(r)) === '["TCP/3128"]');
  const legacySenders = legacyIn.flatMap((r) => r.from || []).map(fromOf).sort();
  const wantLegacy = EXTERNAL_CLIENTS.map(([ns, app]) => `${ns}/${app}`).sort();
  if (JSON.stringify(legacySenders) !== JSON.stringify(wantLegacy)) out.push(`中継の 3128 の送り元が 3 つの呼び手だけでない（${legacySenders.join(', ')}）`);
  const seen = new Set(legacyIn);
  for (const [src, port, ns, app] of PROXY_SOURCES) {
    const rules = ing.filter((r) => JSON.stringify(portsOf(r)) === `["TCP/${port}"]`);
    rules.forEach((r) => seen.add(r));
    const from = rules.flatMap((r) => r.from || []);
    // 名前空間 かつ Pod（同じ要素の両方）。名前空間だけ・Pod だけの要素は、名乗りを 1 つに絞れない
    const okFrom = from.length === 1 && nsOf(from[0].namespaceSelector) === ns && appOf(from[0].podSelector) === app && !from[0].ipBlock;
    if (!(rules.length === 1 && okFrom)) out.push(`中継の受け口 ${port}（送り元 ${src}）へ届くのが ${ns}/${app} だけでない（${from.map(fromOf).join(', ') || 'なし'}）`);
  }
  const other = ing.filter((r) => !seen.has(r));
  if (!(other.length === 1 && JSON.stringify(portsOf(other[0])) === '["TCP/9464"]' && (other[0].from || []).length === 1 && appOf(other[0].from[0].podSelector) === 'prometheus')) out.push('中継の入りに、受け口（送り元ごと・3128）と 9464（Prometheus）の外がある');
  const outside = (np.spec.egress || []).filter((r) => (r.to || []).some((t) => t.ipBlock));
  if (!(outside.length === 1 && JSON.stringify(portsOf(outside[0])) === '["TCP/443"]')) out.push('中継の外への出が 443 だけでない');
  for (const r of outside) for (const t of r.to) {
    const ex = (t.ipBlock.except || []).map(String);
    for (const cidr of ['10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '169.254.0.0/16', '100.64.0.0/10']) if (!ex.includes(cidr)) out.push(`中継の外への出が ${cidr} を除かない`);
  }
  return out;
}

function allowlistProblems(docs) {
  const out = [];
  const cm = find(docs, 'ConfigMap', 'egress-proxy-hosts', 'platform-infra');
  const { parseSourceHosts } = require('../deploy/cloud/egress/proxy.js');
  const table = parseSourceHosts(cm ? cm.data.hosts : '');
  const rows = (src) => [...(table.get(src) || [])].map((k) => k.replace(/:443$/, ''));
  const everywhere = new Set([...table.values()].flatMap((x) => [...x]).map((k) => k.replace(/:443$/, '')));
  // 表の送り元はどれも受け口を持つ（受け口の無い送り元の行は、どの要求にも効かない書き損じ）。送り元を書かない行は宣言の表に置かない。
  for (const src of table.keys()) if (!PROXY_SOURCES.some((x) => x[0] === src)) out.push(`許可表の送り元 ${src} に受け口が無い（または送り元を書かない行）`);
  // 設定に在る宛先: LLM ゲートウェイの appsettings の BaseUrl と、Anthropic の SDK の既定（配線の試験が固定する）
  const app = JSON.parse(read('src/platform/backend/Services/LlmGateway/appsettings.json'));
  const urls = [];
  const walk = (o) => { for (const [k, v] of Object.entries(o || {})) { if (k === 'BaseUrl' && typeof v === 'string' && v) urls.push(v); else if (v && typeof v === 'object') walk(v); } };
  walk(app);
  for (const m of read('src/platform/backend/Services/LlmGateway/Tests/Infrastructure/ExternalServices/AnthropicClientWiringTests.cs').matchAll(/BaseUrl\.Should\(\)\.Be\("(https:\/\/[^"]+)"\)/g)) urls.push(m[1]);
  const provider = [...new Set(urls.map((u) => new URL(u).hostname))].sort();
  if (provider.length < 2) out.push(`陽性対照: appsettings から提供元の BaseUrl を拾えていない（${provider.join(', ')}）`);
  // クラウドで使わない宛先: 埋め込みの提供元（鍵を置かない。vault/eso/microservices-platform.yaml）。IADR-0709 決定 2
  const eso = read('deploy/cloud/vault/eso/microservices-platform.yaml');
  const noEmbedding = /埋め込みの鍵（voyage-api-key）は置かない/.test(eso) && !/remoteRef:[\s\S]{0,80}voyage-api-key/.test(eso);
  if (!noEmbedding) out.push('陽性対照: クラウドが埋め込みの鍵を置かないことを読めない（置くなら埋め込みの提供元を llmgateway の行へ戻す）');
  const unusedInCloud = provider.filter((h) => /voyage/.test(h));
  if (!unusedInCloud.length) out.push('陽性対照: appsettings から埋め込みの提供元を拾えていない');
  for (const h of provider) {
    if (unusedInCloud.includes(h)) { if (everywhere.has(h)) out.push(`許可表にクラウドで使わない ${h} がある`); continue; }
    if (!rows('llmgateway').includes(h)) out.push(`送り元 llmgateway の表に LLM の提供元 ${h}（appsettings の BaseUrl）が無い`);
    for (const src of table.keys()) if (src !== 'llmgateway' && rows(src).includes(h)) out.push(`送り元 ${src} の表に LLM の提供元 ${h} がある（使うのは LLM ゲートウェイだけ）`);
  }
  // Alertmanager の Discord の受信先の形（render.sh の正規表現の名前）
  const am = find(docs, 'ConfigMap', 'alertmanager-cloud-config', 'platform-infra');
  const m = /\^https:\/\/\(([^)]+)\)\/api\/webhooks/.exec(String(am ? am.data['render.sh'] : ''));
  const discord = m ? m[1].split('|').map((x) => x.replace(/\\\./g, '.')) : [];
  if (!discord.length) out.push('陽性対照: render.sh の Discord の形を読めない');
  for (const h of discord) if (!rows('alertmanager').includes(h)) out.push(`送り元 alertmanager の表に Discord の受信先 ${h} が無い`);
  for (const src of ['llmgateway', 'backup']) for (const h of discord) if (rows(src).includes(h)) out.push(`送り元 ${src} の表に Discord の ${h} がある`);
  // バックアップの保管先は ConfigMap platform-backup-s3-target から中継が読む（オーナーが作る）。宣言の表には書かない
  if ((table.get('backup') || new Set()).size) out.push('宣言の表に送り元 backup の行がある（保管先は ConfigMap platform-backup-s3-target から読む）');
  // 取引ユニットの外へ出るサービス（AST の values-cloud の externalHttps: true）と OpenD の送り元を持つ（AST の側が中継へ通す先）
  for (const src of ['ast-information-collection', 'ast-market-monitor', 'ast-notification', 'ast-report', 'ast-risk-management', 'ast-trade-decision']) if (!rows(src).length) out.push(`取引ユニットの送り元 ${src} の行が無い`);
  if (!rows('ast-notification').includes('discord.com')) out.push('送り元 ast-notification の表に Discord が無い');
  // 1 段下を許す行（"*.<名前>"）は、名前が応答で決まる Discord の Bot の再接続先だけ（IADR-0709 決定 1）
  const wild = [...table].flatMap(([src, keys]) => [...keys].filter((k) => k.startsWith('*.')).map((k) => `${src} ${k}`));
  if (JSON.stringify(wild) !== JSON.stringify(['ast-notification *.discord.gg:443'])) out.push(`1 段下を許す行が Discord の Bot の再接続先だけでない（${wild.join(', ')}）`);
  const dep = find(docs, 'Deployment', 'egress-proxy', 'platform-infra');
  const vols = dep ? dep.spec.template.spec.volumes : [];
  const backup = vols.find((v) => v.configMap && v.configMap.name === 'platform-backup-s3-target');
  const wantItems = [{ key: 'endpoint', path: 'endpoint' }, { key: 'dest', path: 'dest' }, { key: 'force_path_style', path: 'force_path_style' }];
  if (!(backup && String(backup.configMap.optional) === 'true' && JSON.stringify(backup.configMap.items) === JSON.stringify(wantItems))) out.push('中継がバックアップの保管先の endpoint・dest・force_path_style だけを読まない');
  const extra = vols.find((v) => v.configMap && v.configMap.name === 'egress-proxy-extra-hosts');
  if (!(extra && String(extra.configMap.optional) === 'true')) out.push('オーナーが埋める表（egress-proxy-extra-hosts）を optional で読まない');
  if (vols.some((v) => v.secret)) out.push('中継が Secret を読む');
  return out;
}

/**
 * IADR-0709 決定 4: 名指した API サーバーの番地が、k3s の起動の旗（README §1-4）と確かめの手順（apiserver-egress-check.sh）の値と同じ。
 *   --advertise-address 127.0.0.1（EndpointSlice の番地）・--service-cidr を渡さない（ClusterIP は既定の 10.43.0.1）。
 */
function apiServerPinProblems(readme, checkSh) {
  const out = [];
  const exec = (/INSTALL_K3S_EXEC="([\s\S]*?)"/.exec(readme) || [])[1] || '';
  if (!exec) return ['陽性対照: README の INSTALL_K3S_EXEC を読めない'];
  if (!/--advertise-address 127\.0\.0\.1(\s|$)/.test(exec)) out.push('k3s の --advertise-address が 127.0.0.1 でない（EndpointSlice の番地が名指しと違う）');
  if (/--service-cidr|--cluster-cidr|--https-listen-port/.test(exec)) out.push('k3s に --service-cidr・--https-listen-port 等を渡している（名指した番地・ポートと違い得る）');
  const pin = (name) => (new RegExp(`^${name}="([^"]+)"$`, 'm').exec(checkSh) || [])[1];
  if (pin('PINNED_SERVICE') !== '10.43.0.1:443') out.push(`確かめの手順の PINNED_SERVICE が宣言と違う（${pin('PINNED_SERVICE')}）`);
  if (pin('PINNED_ENDPOINT') !== '127.0.0.1:6443') out.push(`確かめの手順の PINNED_ENDPOINT が宣言と違う（${pin('PINNED_ENDPOINT')}）`);
  const want = JSON.stringify([['10.43.0.1/32', 'TCP/443'], ['127.0.0.1/32', 'TCP/6443']]);
  if (JSON.stringify(API_SERVER_PINS) !== want) out.push('試験の API_SERVER_PINS が手順と違う');
  return out;
}

/** 確かめの手順を、宛先の確かめと読みに答えるスタブの kubectl で流す（クラスタに触れない）。 */
function runApiServerCheck({ svc, eps, port }) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'apiserver-check-'));
  try {
    const uid = '11111111-2222-4333-8444-555555555555';
    fs.mkdirSync(path.join(dir, 'bin'));
    fs.writeFileSync(path.join(dir, 'mark'), `${uid}\n`);
    fs.writeFileSync(path.join(dir, 'bin', 'kubectl'), `#!/usr/bin/env bash
case "$*" in
  "config current-context") echo ctx-a ;;
  "config view --minify --context ctx-a -o jsonpath={.clusters[0].cluster.server}") echo https://127.0.0.1:6443 ;;
  "--context ctx-a get namespace kube-system -o jsonpath={.metadata.uid}") echo ${uid} ;;
  *"get service kubernetes"*) printf '%s' "$STUB_SVC" ;;
  *"get endpointslices"*".addresses"*) printf '%s\\n' $STUB_EPS ;;
  *"get endpointslices"*".port"*) printf '%s\\n' $STUB_PORT ;;
  *) echo "unexpected: $*" >&2; exit 9 ;;
esac
`, { mode: 0o755 });
    return spawnSync('bash', [path.join(ROOT, 'deploy/cloud/apiserver-egress-check.sh'), '--live'], {
      encoding: 'utf8',
      env: { PATH: `${path.join(dir, 'bin')}:${process.env.PATH}`, HOME: dir, CLOUD_CLUSTER_MARK_FILE: path.join(dir, 'mark'), STUB_SVC: svc, STUB_EPS: eps, STUB_PORT: port },
    });
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

/**
 * IADR-0709 決定 5: collector が送り手の名乗りの外で送り元の Pod から印を付け、LLM ゲートウェイの警報の元の系列をその印で絞る。
 *   - metrics の pipeline: memory_limiter → 送り手の付けた同じ名の属性を消す → 名前空間ごとの k8sattributes（接続の送り元から）→ 各点へ写す → batch。
 *   - 権限は名前空間ごとの Role（Pod の get・list・watch だけ）。ClusterRole は使わない。
 *   - LLM ゲートウェイの費用・上限・再起動・置き場の警報は、印（microservices-platform / llmgateway-service）で絞る。送り手の数は印で数える。
 */
const LLM_SENDER_SELECTOR = 'otlp_sender_namespace="microservices-platform", otlp_sender_app="llmgateway-service"';
function senderIdentityProblems(docs, astDocs) {
  const out = [];
  const cm = find(docs, 'ConfigMap', 'otel-collector-config', 'platform-infra');
  const cfg = cm ? docsOf(`kind: X\n${cm.data['config.yaml']}`)[0] : {};
  const procs = (cfg.processors || {});
  const pl = (((cfg.service || {}).pipelines || {}).metrics || {}).processors || '';
  const pipeline = Array.isArray(pl) ? pl.join(', ') : String(pl).replace(/^\[|\]$/g, '');
  if (pipeline !== 'memory_limiter, transform/otlp-sender-strip, k8sattributes/ast, k8sattributes/msp, transform/otlp-sender-identity, batch') out.push(`collector: metrics の pipeline の順が違う（${pipeline}）`);
  const text = String(cm ? cm.data['config.yaml'] : '');
  if (!/- context: resource\n\s+statements:\n\s+- delete_matching_keys\(attributes, "\^k8s\."\)/.test(text)) out.push('collector: 送り手が付けた k8s.* の属性を消さない');
  if (!/- context: datapoint\n\s+statements:\n\s+- delete_matching_keys\(attributes, "\^otlp\.sender\."\)/.test(text)) out.push('collector: 送り手が付けた otlp_sender の属性を消さない');
  for (const [name, ns] of [['k8sattributes/ast', 'ai-stock-trading'], ['k8sattributes/msp', 'microservices-platform']]) {
    const k = procs[name] || {};
    const assoc = JSON.stringify(k.pod_association || []);
    if (!(k.filter && k.filter.namespace === ns)) out.push(`collector: ${name} の名前空間が ${ns} でない`);
    if (String(k.passthrough) !== 'false') out.push(`collector: ${name} が passthrough でないと書かない`);
    if (assoc !== JSON.stringify([{ sources: [{ from: 'connection' }] }])) out.push(`collector: ${name} が送り元を接続だけから引かない（${assoc}）`);
  }
  for (const want of ['set(attributes["otlp_sender_namespace"], resource.attributes["k8s.namespace.name"])', 'set(attributes["otlp_sender_app"], resource.attributes["k8s.pod.label.app"])']) if (!text.includes(want)) out.push(`collector: 印を各点へ写さない（${want}）`);
  // 権限
  const rbac = [...docs, ...astDocs].filter((d) => /^(Cluster)?Role(Binding)?$/.test(d.kind) && JSON.stringify(d).includes('otel-collector'));
  if (rbac.some((d) => /^Cluster/.test(d.kind))) out.push('collector に ClusterRole・ClusterRoleBinding がある（名前空間ごとの Role にする）');
  const roles = rbac.filter((d) => d.kind === 'Role');
  const nss = roles.map((d) => d.metadata.namespace).sort();
  if (JSON.stringify(nss) !== JSON.stringify(['ai-stock-trading', 'microservices-platform'])) out.push(`collector の Role の名前空間が 2 つでない（${nss.join(', ')}）`);
  for (const r of roles) if (JSON.stringify(r.rules) !== JSON.stringify([{ apiGroups: [''], resources: ['pods'], verbs: ['get', 'list', 'watch'] }])) out.push(`collector の Role ${r.metadata.namespace} が Pod の読みの外を許す`);
  for (const b of rbac.filter((d) => d.kind === 'RoleBinding')) if (JSON.stringify(b.subjects) !== JSON.stringify([{ kind: 'ServiceAccount', name: 'otel-collector', namespace: 'platform-infra' }])) out.push(`collector の RoleBinding ${b.metadata.namespace} の相手が違う`);
  if (astDocs.some((d) => d.metadata.namespace !== 'ai-stock-trading')) out.push('otlp-sender-identity-ast が ai-stock-trading の外を描く');
  if (docs.some((d) => d.metadata && d.metadata.namespace === 'ai-stock-trading')) out.push('deploy/cloud が ai-stock-trading の資源を描く（取引ユニットの名前空間が在る前に当てると落ちる）');
  // 警報
  const prom = find(docs, 'ConfigMap', 'prometheus-cloud-config', 'platform-infra');
  const rules = String(prom ? prom.data['platform-rules.yml'] : '');
  const block = (a) => (new RegExp(`- alert: ${a}\\n\\s+expr: ([\\s\\S]*?)\\n\\s+(?:for|labels):`).exec(rules) || [])[1] || '';
  for (const [a, metrics] of [['LlmMonthlyBudgetPaceExceeded', ['llm_cost_total', 'llm_budget_monthly_limit']], ['LlmGatewayRestartLoop', ['llm_gateway_start_time_seconds']], ['LlmCostStateUnavailable', ['llm_call_limit_exceeded_total']]]) {
    const e = block(a);
    if (!e) { out.push(`警報 ${a} の式が読めない`); continue; }
    for (const m of metrics) {
      const uses = [...e.matchAll(new RegExp(`${m}(\\{[^}]*\\})?`, 'g'))];
      if (!uses.length) out.push(`警報 ${a} が ${m} を見ない（陽性対照）`);
      for (const u of uses) if (!(u[1] || '').includes(LLM_SENDER_SELECTOR)) out.push(`警報 ${a} の ${m} が送り元の印で絞らない`);
    }
  }
  if (!/count by \(k8s_namespace_name, k8s_pod_label_app\) \(last_over_time\(target_info\{k8s_pod_label_app!=""\}\[10m\]\)\)/.test(block('OtlpSendersMissing'))) out.push('警報 OtlpSendersMissing が送り元の印で数えない');
  if (block('OtlpSenderUnverified') !== 'count(last_over_time(target_info{k8s_pod_label_app=""}[10m])) > 0') out.push('警報 OtlpSenderUnverified が無い・形が違う');
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
      // 宛先を問わない API サーバーのポートは段 1 の別の宣言だけ（IADR-0709 決定 4）。番地の名指しはどの宣言でもよい
      const api = (to.length === 0 && p === '["TCP/443","TCP/6443"]' && d.metadata.name === 'eso-egress-apiserver-ports-only-stage1') || isApiServerPin(r);
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

/** 単体試験に渡す規則のファイルを書く（注釈は $value の文字で揺れるので照合から外す。cloud-ops.test.js と同じ扱い）。 */
function writeRulesWithoutAnnotations(rulesText, file) {
  const lines = rulesText.split('\n');
  const kept = [];
  for (let i = 0; i < lines.length; i += 1) {
    const m = /^(\s*)annotations:\s*$/.exec(lines[i]);
    if (!m) { kept.push(lines[i]); continue; }
    while (i + 1 < lines.length && (lines[i + 1].trim() === '' || /^\s*/.exec(lines[i + 1])[0].length > m[1].length)) i += 1;
  }
  fs.writeFileSync(file, kept.join('\n'));
}

const REPORT_MODE_EXPR = '(max(egress_proxy_enforcing) == 0 or on() max(egress_proxy_source_enforcing) == 0) and on() ((time() - max(egress_proxy_report_since_timestamp_seconds)) > 14 * 86400 or on() absent(egress_proxy_report_since_timestamp_seconds))';

/**
 * promtool の単体試験: EgressProxyReportMode は report の段に入った日時から 14 日で鳴る。Prometheus の再起動（系列の途切れ）の後でも鳴る。
 * 日時が無ければ鳴る。enforce なら鳴らない。戻り値は promtool の結果（陰性対照で、変えた規則を渡す）。
 */
function promtoolReportModeRun(rulesText) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'egress-report-alerts-'));
  try {
    const rf = path.join(dir, 'rules.yml');
    writeRulesWithoutAnnotations(rulesText, rf);
    const day = 86400;
    const at = 2 * 3600; // 評価の時刻（試験の始まりを 0 とする time()）
    const group = (series, evalTime, exp) => `  - interval: 1m\n    input_series:\n${series.map(([n, v]) => `      - series: '${n}'\n        values: '${v}'\n`).join('')}`
      + `    alert_rule_test:\n      - eval_time: ${evalTime}\n        alertname: EgressProxyReportMode\n        exp_alerts:${exp ? '\n          - exp_labels: { severity: warning }\n' : ' []\n'}`;
    const tests = [
      // 14 日を越えた
      group([['egress_proxy_enforcing', '0x150'], ['egress_proxy_report_since_timestamp_seconds', `${at - 15 * day}x150`]], '2h', true),
      // 14 日を越えていない
      group([['egress_proxy_enforcing', '0x150'], ['egress_proxy_report_since_timestamp_seconds', `${at - 13 * day}x150`]], '2h', false),
      // 日時が無い（系列が無い）
      group([['egress_proxy_enforcing', '0x150']], '2h', true),
      // enforce の段なら、古い日時が残っていても鳴らない
      group([['egress_proxy_enforcing', '1x150'], ['egress_proxy_report_since_timestamp_seconds', `${at - 30 * day}x150`]], '2h', false),
      group([['egress_proxy_enforcing', '1x150'], ['egress_proxy_source_enforcing', '1x150'], ['egress_proxy_report_since_timestamp_seconds', `${at - 30 * day}x150`]], '2h', false),
      // IADR-0709: 全体の段は enforce でも、送り元ごとの段が report のまま 14 日を越えたら鳴る（日時が無くても鳴る）
      group([['egress_proxy_enforcing', '1x150'], ['egress_proxy_source_enforcing', '0x150'], ['egress_proxy_report_since_timestamp_seconds', `${at - 15 * day}x150`]], '2h', true),
      group([['egress_proxy_enforcing', '1x150'], ['egress_proxy_source_enforcing', '0x150']], '2h', true),
      group([['egress_proxy_enforcing', '1x150'], ['egress_proxy_source_enforcing', '0x150'], ['egress_proxy_report_since_timestamp_seconds', `${at - 13 * day}x150`]], '2h', false),
      // Prometheus の再起動（80 分より前の系列が無い）の後でも、日時から数えるので 1 時間で鳴る
      group([['egress_proxy_enforcing', '_x80 0x70'], ['egress_proxy_report_since_timestamp_seconds', `_x80 ${150 * 60 - 20 * day}x70`]], '150m', true),
    ];
    const tf = path.join(dir, 'test.yml');
    fs.writeFileSync(tf, `rule_files:\n  - ${rf}\nevaluation_interval: 1m\ntests:\n${tests.join('')}`);
    const r = spawnSync('promtool', ['test', 'rules', tf], { encoding: 'utf8' });
    if (r.error) throw new Error('promtool が PATH に無い');
    return r;
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

/** promtool の単体試験: 期限の警報が残り 150 日・30 日の境で鳴る・鳴らない、OpenBao の受け口の導出、測りの不在。 */
function promtoolCertAlertTest(cloudDocs) {
  const { rulesText } = PLATFORM_RULES(cloudDocs);
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'cert-alerts-'));
  try {
    const rf = path.join(dir, 'rules.yml');
    writeRulesWithoutAnnotations(rulesText, rf);
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
    // ipBlock の検査が見ない範囲（宛先を問わない出）を名指して固定する（IADR-0705 残余 5）
    const portsOnly = [...new Set([cloud, chart, vault, esoNs].flatMap(portsOnlyEgress))].sort();
    // IADR-0709 決定 4: 宛先を問わない出は、段 1 の別の宣言（名前に stage1）の 2 つだけ。名指した番地は ESO・OpenBao・collector が持つ
    assert.deepStrictEqual(portsOnly, ['external-secrets/eso-egress-apiserver-ports-only-stage1 TCP/443,TCP/6443', 'openbao/openbao-egress-apiserver-ports-only-stage1 TCP/443,TCP/6443'].sort(), `宛先を問わない出が想定の 2 つと違う:\n${portsOnly.join('\n')}`);
    const pinned = (docs, ns, name) => { const d = find(docs, 'NetworkPolicy', name, ns); return d ? API_SERVER_PINS.filter(([cidr, port]) => (d.spec.egress || []).some((r) => isApiServerPin(r) && r.to[0].ipBlock.cidr === cidr && portsOf(r)[0] === port)).length : 0; };
    assert.strictEqual(pinned(esoNs, 'external-secrets', 'eso-egress-dns-openbao-apiserver'), 2, 'ESO の出が API サーバーの番地を 2 つとも名指さない');
    assert.strictEqual(pinned(vault, 'openbao', 'openbao-egress-dns-and-apiserver'), 2, 'OpenBao の出が API サーバーの番地を 2 つとも名指さない');
    assert.strictEqual(pinned(cloud, 'platform-infra', 'otel-collector-egress-apiserver'), 2, 'collector の出が API サーバーの番地を 2 つとも名指さない');
    // 陰性対照: 番地の名指しを他の Pod へ足す・名指しに広い範囲を混ぜると赤
    const wider = clone(cloud);
    find(wider, 'NetworkPolicy', 'platform-backup-egress').spec.egress.push({ to: [{ ipBlock: { cidr: '127.0.0.1/32' } }], ports: [{ protocol: 'TCP', port: 6443 }] });
    find(wider, 'NetworkPolicy', 'otel-collector-egress-apiserver').spec.egress[0].to[0].ipBlock.cidr = '10.43.0.0/16';
    const wp = directEgressProblems(wider, 'x');
    for (const want of [/platform-backup-egress が API サーバーの番地へ出る/, /otel-collector-egress-apiserver がクラスタの外の範囲へ直に出る/]) assert.ok(wp.some((x) => want.test(x)), `陰性対照: ${want} を検出しない:\n${wp.join('\n')}`);
    const extra = clone(cloud);
    find(extra, 'NetworkPolicy', 'platform-backup-egress').spec.egress.push({ ports: [{ protocol: 'TCP', port: 443 }] });
    assert.ok(portsOnlyEgress(extra).some((x) => /platform-backup-egress/.test(x)), '陰性対照: ポートだけの出の追加を拾わない');
    assert.ok(cloud.some((d) => d.kind === 'NetworkPolicy' && d.metadata.name === 'egress-proxy'), '陽性対照: 中継の NetworkPolicy が描画に無い');
    const bad = clone(cloud);
    find(bad, 'NetworkPolicy', 'platform-backup-egress').spec.egress.push({ to: [{ ipBlock: { cidr: '0.0.0.0/0' } }], ports: [{ protocol: 'TCP', port: 443 }] });
    assert.ok(directEgressProblems(bad, 'x').some((p) => /platform-backup-egress/.test(p)), '陰性対照: バックアップの直の 443 を検出しない');
  });
  ok('E2: 外の 443 を使う 3 つは中継の自分の受け口（と移す間の 3128）へ出て、自分の受け口を使う設定を持つ', () => {
    assert.deepStrictEqual(externalClientProblems(cloud, chart), []);
    const bad = clone(cloud);
    const am = find(bad, 'ConfigMap', 'alertmanager-cloud-config');
    am.data['alertmanager.yml.tmpl'] = am.data['alertmanager.yml.tmpl'].replace(/global:\n\s+http_config:\n\s+proxy_url: \S+\n/, '');
    const cj = bad.find((d) => d.kind === 'CronJob');
    dc.podSpec(cj).containers[0].env = dc.podSpec(cj).containers[0].env.filter((e) => e.name !== 'HTTPS_PROXY');
    const p = externalClientProblems(bad, chart);
    for (const want of [/Alertmanager の global\.http_config\.proxy_url/, /バックアップに外向きの中継の自分の受け口の HTTPS_PROXY が無い/]) assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
    // 陰性対照: 従来の口（3128）へ戻すと、自分の受け口でないので赤
    const legacy = clone(cloud);
    const lam = find(legacy, 'ConfigMap', 'alertmanager-cloud-config');
    lam.data['alertmanager.yml.tmpl'] = lam.data['alertmanager.yml.tmpl'].replace('http://egress-proxy:3133', 'http://egress-proxy:3128');
    assert.ok(externalClientProblems(legacy, chart).some((x) => /proxy_url が外向きの中継の自分の受け口でない/.test(x)), '陰性対照: 従来の口へ戻したのを検出しない');
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
  ok('E4: 許可表は送り元ごとに設定に在る宛先を持ち、使わない宛先・他の送り元の宛先を持たない。受け口ごとに届く Pod は 1 つ', () => {
    assert.deepStrictEqual(allowlistProblems(cloud), []);
    const bad = clone(cloud);
    const cm = find(bad, 'ConfigMap', 'egress-proxy-hosts');
    cm.data.hosts = cm.data.hosts.replace('llmgateway api.anthropic.com\n', 'llmgateway api.voyageai.com\nalertmanager api.anthropic.com\nllmgateway discord.com\nnobody x.example.com\nast-report *.example.com\n');
    const p = allowlistProblems(bad);
    for (const want of [/llmgateway の表に LLM の提供元 api\.anthropic\.com/, /クラウドで使わない api\.voyageai\.com/, /送り元 alertmanager の表に LLM の提供元/, /送り元 llmgateway の表に Discord/, /送り元 nobody に受け口が無い/, /1 段下を許す行が/]) assert.ok(p.some((x) => want.test(x)), `陰性対照: ${want} を検出しない:\n${p.join('\n')}`);
    // 受け口へ届く Pod の追加・名前空間だけの要素（名乗りを 1 つに絞れない）・legacy の送り元の追加
    const np = find(bad, 'NetworkPolicy', 'egress-proxy').spec.ingress;
    np[0].from.push({ podSelector: { matchLabels: { app: 'keycloak' } } });
    np.find((r) => r.ports.some((x) => Number(x.port) === 3131)).from.push({ namespaceSelector: { matchLabels: { 'kubernetes.io/metadata.name': 'ai-stock-trading' } } });
    np.find((r) => r.ports.some((x) => Number(x.port) === 3144)).from[0].podSelector.matchLabels.app = 'audit-service';
    const pp = proxyProblems(bad, { mode: 'enforce' });
    for (const want of [/3128 の送り元が 3 つの呼び手だけでない/, /受け口 3131（送り元 llmgateway）へ届くのが/, /受け口 3144（送り元 ast-report）へ届くのが/]) assert.ok(pp.some((x) => want.test(x)), `陰性対照: ${want} を検出しない:\n${pp.join('\n')}`);
    // 受け口の一覧とポートの食い違い
    const bad2 = clone(cloud);
    const e = find(bad2, 'Deployment', 'egress-proxy').spec.template.spec.containers[0].env.find((x) => x.name === 'EGRESS_PROXY_SOURCES');
    e.value = e.value.replace('ast-report:3144', 'ast-report:3150');
    assert.ok(proxyProblems(bad2, { mode: 'enforce' }).some((x) => /送り元と受け口が表と違う/.test(x)), '陰性対照: 受け口の食い違いを検出しない');
    assert.ok(proxyProblems(cloud, { mode: 'enforce', sourceMode: 'enforce' }).some((x) => /送り元ごとの段が enforce でない/.test(x)), '陰性対照: 送り元ごとの段を見ていない');
  });
  ok('E5: 中継の手順の試験', () => child('deploy/cloud/egress/proxy.test.js'));
  ok('E6: report の段の警報は入った日時から 14 日で鳴る（Prometheus・中継の再起動で数え直さない。日時が無ければ鳴る。promtool の単体試験）', () => {
    const { rulesText } = PLATFORM_RULES(cloud);
    assert.strictEqual(rulesText.split(REPORT_MODE_EXPR).length, 2, '陽性対照: EgressProxyReportMode の式が想定の形でない');
    const good = promtoolReportModeRun(rulesText);
    assert.strictEqual(good.status, 0, good.stdout + good.stderr);
    // 陰性対照 1: 前の形（for: 14d で数える）へ戻すと、14 日を越えた・再起動の後・日時が無いの 3 つで鳴らずに赤
    const old = rulesText.replace(REPORT_MODE_EXPR, 'max(egress_proxy_enforcing) == 0').replace(/(alert: EgressProxyReportMode\n\s+expr: [^\n]+\n\s+)for: 1h/, '$1for: 14d');
    assert.notStrictEqual(old, rulesText);
    assert.notStrictEqual(promtoolReportModeRun(old).status, 0, '前の形（for: 14d）でも通る（試験が数え方を見分けていない）');
    // 陰性対照 2: 日時が無いときの枝（absent）を外すと、日時が無い場合で鳴らずに赤
    const noAbsent = rulesText.replace(REPORT_MODE_EXPR, 'max(egress_proxy_enforcing) == 0 and on() ((time() - max(egress_proxy_report_since_timestamp_seconds)) > 14 * 86400)');
    assert.notStrictEqual(promtoolReportModeRun(noAbsent).status, 0, '日時が無いときに鳴らない形でも通る');
    // 陰性対照 3（IADR-0709）: 送り元ごとの段の枝を外すと、送り元ごとの段が report のまま残る場合で鳴らずに赤
    const noSource = rulesText.replace(REPORT_MODE_EXPR, REPORT_MODE_EXPR.replace('(max(egress_proxy_enforcing) == 0 or on() max(egress_proxy_source_enforcing) == 0)', 'max(egress_proxy_enforcing) == 0'));
    assert.notStrictEqual(noSource, rulesText);
    assert.notStrictEqual(promtoolReportModeRun(noSource).status, 0, '送り元ごとの段を見ない形でも通る');
    // 中継は日時を ConfigMap egress-proxy-report-since（optional・kustomize の外）から読む
    const vols = find(cloud, 'Deployment', 'egress-proxy', 'platform-infra').spec.template.spec.volumes;
    const v = vols.find((x) => x.configMap && x.configMap.name === 'egress-proxy-report-since');
    assert.ok(v && String(v.configMap.optional) === 'true' && JSON.stringify(v.configMap.items) === JSON.stringify([{ key: 'since', path: 'since' }]), '中継が report の段の日時を optional の ConfigMap から読まない');
    assert.ok(!cloud.concat(report).some((d) => d.kind === 'ConfigMap' && d.metadata.name === 'egress-proxy-report-since'), '日時の ConfigMap を宣言に置いている（当て直しで日時が変わる）');
  });

  ok('A1: 名指した API サーバーの番地は k3s の起動の旗と確かめの手順の値と同じ。確かめの手順は一致で 0・違いで 1（読むだけ）', () => {
    const checkSh = read('deploy/cloud/apiserver-egress-check.sh');
    assert.deepStrictEqual(apiServerPinProblems(readme, checkSh), []);
    const p = apiServerPinProblems(readme.replace('--advertise-address 127.0.0.1', '--advertise-address 10.0.0.2'), checkSh.replace('PINNED_SERVICE="10.43.0.1:443"', 'PINNED_SERVICE="10.43.0.10:443"'));
    for (const want of [/advertise-address/, /PINNED_SERVICE/]) assert.ok(p.some((x) => want.test(x)), `陰性対照: ${want} を検出しない:\n${p.join('\n')}`);
    const good = runApiServerCheck({ svc: '10.43.0.1:443', eps: '127.0.0.1', port: '6443' });
    assert.strictEqual(good.status, 0, good.stdout + good.stderr);
    for (const bad of [{ svc: '10.43.0.1:443', eps: '10.0.0.2', port: '6443' }, { svc: '10.43.0.1:443', eps: '127.0.0.1 10.0.0.2', port: '6443' }, { svc: '10.43.0.10:443', eps: '127.0.0.1', port: '6443' }, { svc: '', eps: '', port: '' }]) {
      const r = runApiServerCheck(bad);
      assert.strictEqual(r.status, 1, `違う番地（${JSON.stringify(bad)}）で 1 にならない:\n${r.stdout}${r.stderr}`);
    }
    assert.ok(!/\b(apply|delete|patch|create|edit|label|annotate|scale|set|rollout|exec)\b/.test(checkSh.split('\n').filter((l) => /^\s*[^#].*kubectl /.test(l)).join('\n')), '確かめの手順が書き込みの kubectl を持つ');
  });
  ok('O1: collector は送り元の Pod から印を付け（名前空間ごとの読みだけの Role）、LLM ゲートウェイの警報と送り手の数を印で見る', () => {
    const ast = kustomize('deploy/cloud/otlp-sender-identity-ast');
    assert.deepStrictEqual(senderIdentityProblems(cloud, ast), []);
    assert.deepStrictEqual(senderIdentityProblems(infra.concat(find(cloud, 'ConfigMap', 'prometheus-cloud-config') ? [find(cloud, 'ConfigMap', 'prometheus-cloud-config')] : []), ast).filter((x) => !/RoleBinding|Role の/.test(x)), [], '#2401 の infra 単独の描画でも同じ処理を持たない');
    const bad = clone(cloud);
    const cm = find(bad, 'ConfigMap', 'otel-collector-config');
    cm.data['config.yaml'] = cm.data['config.yaml'].replace('processors: [memory_limiter, transform/otlp-sender-strip, k8sattributes/ast, k8sattributes/msp, transform/otlp-sender-identity, batch]', 'processors: [memory_limiter, batch]').replace(/- from: connection\n/, '- from: resource_attribute\n').replace('delete_matching_keys(attributes, "^k8s.")', 'set(attributes["x"], "y")');
    const prom = find(bad, 'ConfigMap', 'prometheus-cloud-config');
    prom.data['platform-rules.yml'] = prom.data['platform-rules.yml'].replace(`changes(max(llm_gateway_start_time_seconds{${LLM_SENDER_SELECTOR}})`, 'changes(max(llm_gateway_start_time_seconds)').replace('count by (k8s_namespace_name, k8s_pod_label_app)', 'count by (job)');
    const role = find(bad, 'Role', 'otel-collector-pod-reader', 'microservices-platform');
    role.rules[0].resources.push('secrets');
    const badAst = clone(ast);
    badAst.push({ kind: 'ClusterRoleBinding', metadata: { name: 'x' }, subjects: [{ kind: 'ServiceAccount', name: 'otel-collector', namespace: 'platform-infra' }] });
    const p = senderIdentityProblems(bad, badAst);
    for (const want of [/pipeline の順が違う/, /送り元を接続だけから引かない/, /k8s\.\* の属性を消さない/, /LlmGatewayRestartLoop の llm_gateway_start_time_seconds が送り元の印で絞らない/, /OtlpSendersMissing が送り元の印で数えない/, /Pod の読みの外を許す/, /ClusterRole/]) assert.ok(p.some((x) => want.test(x)), `陰性対照: ${want} を検出しない:\n${p.join('\n')}`);
  });

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
