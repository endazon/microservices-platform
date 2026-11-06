#!/usr/bin/env node
'use strict';
/**
 * NFR-18, IADR-0674 (#2401): クラウドの最小構成（deploy/cloud）の**描画**を固定する（クラスタに触れない）。
 *
 *   - 基盤: `kubectl kustomize deploy/cloud/infra`
 *   - MSP: `helm template … -f deploy/cloud/values-cloud.yaml --set-string global.image.tag=<固定の値>`
 *
 * 受け入れ基準（#2401）:
 *   AC1 描画に valkey・qdrant・mailpit・mail-relay・knowledge ユニット・BFF・frontend ほか持ち込まない資源が 1 つも無い。
 *   AC2 描画に LoadBalancer / NodePort の Service・hostPort・hostNetwork が無い。
 *   AC3 描画にリポジトリの既定の資格情報の値が無い（Secret を描かない・資格情報の env は Secret から読む・既知の開発用の字面が無い・
 *       SQL に字面のパスワードが無い）。
 *   AC4 Keycloak の起動の引数に start-dev が無い（start で動く）。
 *   AC5 外部のイメージは deploy/local/infra と同じ digest（写した宣言が古い版に取り残されない）。
 *   AC6 MSP の描画は LLM ゲートウェイの Deployment だけで、NetworkPolicy を描かない。MSP の名前空間の入りは既定で拒否し、
 *       LLM ゲートウェイへは取引ユニットの呼び出し元（名前空間 かつ Pod。AND）の 8080 だけを許す（deploy/cloud/infra が持つ）。
 *   AC13 LLM ゲートウェイの受け口は TLS だけ（ASPNETCORE_URLS が https・証明書は Secret llmgateway-tls・probe は HTTPS・gRPC の h2c の受け口が無い）。
 *   AC14 LLM ゲートウェイはルートのファイルシステムを読み取り専用にし、書き込みは /tmp の emptyDir だけ。
 *   AC15 Keycloak（管理の面を持つ）はクラスタの中だけ: 基盤の Service は ClusterIP と明示し、別名は ExternalName だけ。
 *       描画（基盤・chart）にエッジの資源（Ingress・Gateway・HTTPRoute ほか）が 1 つも無い。
 *   AC11 Postgres は TCP を TLS（hostssl）でだけ受ける（pg_hba に host・hostnossl・trust・password・md5 が無い）。
 *   AC12 RabbitMQ はデータ（/var/lib/rabbitmq）を PVC に置き、ノード名を固定し、Recreate で替わる（再起動でキュー・メッセージを失わない）。
 *
 * kubectl / helm が無ければ失敗にする（fail-closed。CI の static-checks は両方を持つ。check-deploy-manifests と同じ）。
 * 判定の関数は export し、scripts/scripts.repo.test.js が宣言そのもの（kustomize は resources だけで変換を持たない）にも当てる。
 * 外部依存ゼロ（Node 標準モジュール ＋ kubectl ＋ helm）。実行: node scripts/deploy-cloud.test.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const { parseDocuments } = require('./lib/yaml-subset');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const IMAGE_TAG = '0123456789ab';

/** 持ち込まない資源の名前・app ラベル（部分一致）。 */
const FORBIDDEN = [
  'valkey', 'redis', 'qdrant', 'mailpit', 'mail-relay', 'bff', 'frontend', 'seaweedfs', 'wikijs', 'wiki-js', 'embedding',
  'document-service', 'datasource-service', 'conversion-service', 'ingestion-service', 'retrieval-service', 'aianalysis-service',
  'wiki-service', 'graph-service', 'dashboard-service', 'feedback-service', 'notification-service', 'mcp-service',
  'authorization-service', 'synthetic-monitor', 'vault', 'openbao', 'loki', 'tempo', 'grafana', 'prometheus',
];

/** リポジトリの既定の資格情報の字面（宣言・起動器・compose の開発用の値の形）。描画のどこにも現れてはならない。 */
const DEV_LITERALS = ['dev-secret-change-me', 'dev-only-', ['objectstorage', 'dev'].join('-'), 'devroot', 'Developer-2026', 'PocOperator-2026'];

/** 資格情報を運ぶ env の名前。字面の値（value）を持たず、Secret（valueFrom）から読むこと。 */
const CREDENTIAL_ENV = /(PASSWORD|PASS|SECRET|TOKEN|API_KEY|APIKEY|_USER|_USERNAME|USERNAME)$/;

function run(cmd, args) {
  const r = spawnSync(cmd, args, { cwd: REPO_ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  if (r.error || r.status !== 0) throw new Error(`${cmd} ${args.join(' ')} が失敗した（${cmd} が PATH に無い可能性。オフライン描画に要る）:\n${r.stderr || r.error}`);
  return r.stdout;
}

const docsOf = (text) => parseDocuments(text).filter((d) => d && typeof d === 'object' && d.kind);
const labelApp = (d) => (d.metadata && d.metadata.labels && d.metadata.labels.app) || '';
const podSpec = (d) => (d.spec && d.spec.template && d.spec.template.spec) || (d.spec && d.spec.jobTemplate && d.spec.jobTemplate.spec && d.spec.jobTemplate.spec.template && d.spec.jobTemplate.spec.template.spec) || null;
const containersOf = (d) => {
  const s = podSpec(d);
  return s ? [...(s.initContainers || []), ...(s.containers || [])] : [];
};

/** エッジ（クラスタの外からの入口）の資源の kind。クラウドの最小構成は 1 つも描かない（外への口は SSH だけ）。 */
const EDGE_KINDS = ['Ingress', 'IngressClass', 'Gateway', 'HTTPRoute', 'GRPCRoute', 'TLSRoute', 'TCPRoute', 'UDPRoute', 'VirtualService', 'IngressRoute', 'IngressRouteTCP', 'IngressRouteUDP'];

/** AC15: Keycloak はクラスタの中だけ。エッジの資源が無い・Keycloak の Service は ClusterIP（別名は ExternalName で基盤の Service を指すだけ）。 */
function keycloakExposureProblems(docs, where) {
  const out = [];
  for (const d of docs.filter((x) => EDGE_KINDS.includes(x.kind))) out.push(`${where}: エッジの資源を描いている（${d.kind}/${d.metadata && d.metadata.name}）`);
  for (const d of docs.filter((x) => x.kind === 'Service' && x.metadata && x.metadata.name === 'keycloak')) {
    const type = String((d.spec && d.spec.type) || '');
    const ns = d.metadata.namespace;
    if (ns === 'platform-infra') {
      if (type !== 'ClusterIP') out.push(`${where}: Keycloak の Service が ClusterIP と明示されていない（${type || '未指定'}）`);
    } else if (!(type === 'ExternalName' && d.spec.externalName === 'keycloak.platform-infra.svc.cluster.local')) {
      out.push(`${where}: ${ns} の keycloak が基盤の Service への別名（ExternalName）でない（${type || '未指定'}）`);
    }
    if (d.spec && (d.spec.externalIPs || d.spec.loadBalancerIP)) out.push(`${where}: Keycloak の Service が外の IP を持つ`);
  }
  return out;
}

/** AC1・AC2・AC3 の共通の判定。問題の一覧（空なら合格）を返す。 */
function commonProblems(docs, where, { allow = [] } = {}) {
  const out = [];
  const text = JSON.stringify(docs);
  for (const d of docs) {
    const id = `${where}: ${d.kind}/${d.metadata && d.metadata.namespace ? `${d.metadata.namespace}/` : ''}${d.metadata && d.metadata.name}`;
    const names = [d.metadata && d.metadata.name, labelApp(d)].filter(Boolean).map(String);
    if (d.kind !== 'NetworkPolicy') {
      for (const f of FORBIDDEN) if (!allow.includes(f) && names.some((n) => n.includes(f))) out.push(`${id}: 持ち込まない資源（${f}）`);
    }
    if (d.kind === 'Secret') out.push(`${id}: Secret を描いている（資格情報は OpenBao の中で作り ESO が写す。IADR-0689）`);
    if (d.kind === 'Service' && ['LoadBalancer', 'NodePort'].includes(d.spec && d.spec.type)) out.push(`${id}: ${d.spec.type} の Service`);
    const ps = podSpec(d);
    if (ps && String(ps.hostNetwork) === 'true') out.push(`${id}: hostNetwork`);
    for (const c of containersOf(d)) {
      for (const p of c.ports || []) if (p.hostPort !== undefined && p.hostPort !== null) out.push(`${id}: hostPort（${c.name}）`);
      for (const e of c.env || []) {
        if (CREDENTIAL_ENV.test(e.name) && e.value !== undefined && e.value !== null) out.push(`${id}: 資格情報の env ${e.name} が字面の値を持つ（Secret から読む）`);
      }
    }
    if (d.kind === 'ConfigMap') {
      for (const [k, v] of Object.entries(d.data || {})) {
        if (/WITH\s+PASSWORD\s+'/i.test(String(v))) out.push(`${id}: ${k} に字面のパスワードがある`);
      }
    }
  }
  for (const lit of DEV_LITERALS) if (text.includes(lit)) out.push(`${where}: 既定の資格情報の字面（${lit.length} 字）が描画にある`);
  out.push(...keycloakExposureProblems(docs, where));
  return out;
}

const MSP_NS = 'microservices-platform';
const AST_NS = 'ai-stock-trading';
const LLM_GATEWAY_PORT = 8080;
const LLM_GATEWAY_CALLERS = ['report-service', 'trade-decision-service'];

/** AC6: MSP の名前空間の入りの既定拒否と、取引ユニットの呼び出し元 → LLM ゲートウェイ（8080）だけの許可。 */
function mspIngressProblems(docs) {
  const out = [];
  const np = docs.filter((d) => d.kind === 'NetworkPolicy' && d.metadata && d.metadata.namespace === MSP_NS);
  const types = (d) => (d.spec.policyTypes || []).map(String);
  const deny = np.some((d) => JSON.stringify(d.spec.podSelector || {}) === '{}' && types(d).includes('Ingress') && !(d.spec.ingress || []).length);
  if (!deny) out.push(`infra: ${MSP_NS} の入りの既定拒否（podSelector が空・Ingress・規則なし）が無い`);
  const ingressAllows = np.filter((d) => types(d).includes('Ingress') && (d.spec.ingress || []).length);
  const gw = ingressAllows.filter((d) => JSON.stringify(d.spec.podSelector || {}) === JSON.stringify({ matchLabels: { app: 'llmgateway-service' } }));
  if (gw.length !== 1) out.push(`infra: LLM ゲートウェイへの入りの許可がちょうど 1 枚でない（${gw.length}）`);
  for (const d of ingressAllows.filter((x) => !gw.includes(x))) out.push(`infra: ${MSP_NS} に LLM ゲートウェイ以外への入りの許可がある（${d.metadata.name}）`);
  for (const d of gw) {
    for (const rule of d.spec.ingress) {
      for (const f of rule.from || []) {
        const nsOk = f.namespaceSelector && JSON.stringify(f.namespaceSelector) === JSON.stringify({ matchLabels: { 'kubernetes.io/metadata.name': AST_NS } });
        const exprs = (f.podSelector && f.podSelector.matchExpressions) || [];
        const podsOk = exprs.length === 1 && exprs[0].key === 'app' && exprs[0].operator === 'In'
          && JSON.stringify([...exprs[0].values].map(String).sort()) === JSON.stringify([...LLM_GATEWAY_CALLERS].sort());
        if (!nsOk || !podsOk) out.push(`infra: ${d.metadata.name} の送り元が「取引ユニットの名前空間 かつ 呼び出し元の Pod」（同じ要素）でない`);
      }
      if (!(rule.from || []).length) out.push(`infra: ${d.metadata.name} が送り元を絞っていない`);
      const ports = (rule.ports || []).map((x) => `${x.protocol || 'TCP'}/${x.port}`);
      if (JSON.stringify(ports) !== JSON.stringify([`TCP/${LLM_GATEWAY_PORT}`])) out.push(`infra: ${d.metadata.name} のポートが TCP/${LLM_GATEWAY_PORT} だけでない（${ports.join(', ')}）`);
    }
  }
  return out;
}

/** AC11: Postgres は pg_hba で TCP を hostssl だけで受け、弱い認証を持たない。 */
function postgresHbaProblems(docs) {
  const out = [];
  const pg = docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'postgres');
  if (!pg) return out;
  const spec = pg.spec.template.spec;
  const c = spec.containers.find((x) => x.name === 'postgres') || spec.containers[0];
  const args = (c.args || []).map(String);
  const hbaArg = args.find((a) => a.startsWith('hba_file='));
  if (!hbaArg) return ['infra: Postgres が hba_file を指定していない（イメージの既定の pg_hba は平文の TCP も受ける）'];
  const hbaPath = hbaArg.slice('hba_file='.length);
  const mount = (c.volumeMounts || []).find((m) => hbaPath.startsWith(`${String(m.mountPath).replace(/\/$/, '')}/`));
  const vol = mount && (spec.volumes || []).find((v) => v.name === mount.name);
  const cmName = vol && vol.configMap && vol.configMap.name;
  const cm = cmName && docs.find((d) => d.kind === 'ConfigMap' && d.metadata.name === cmName);
  const key = hbaPath.split('/').pop();
  const text = cm && cm.data && cm.data[key];
  if (!text) return [`infra: hba_file（${hbaPath}）を運ぶ ConfigMap が無い`];
  const lines = String(text).split('\n').map((l) => l.replace(/#.*/, '').trim()).filter(Boolean);
  if (!lines.some((l) => /^hostssl\s/.test(l))) out.push('infra: pg_hba に hostssl の行が無い');
  for (const l of lines) {
    const f = l.split(/\s+/);
    if (!['local', 'hostssl'].includes(f[0])) out.push(`infra: pg_hba に TLS でない TCP の行がある（${f[0]}）`);
    if (f[f.length - 1] !== 'scram-sha-256') out.push(`infra: pg_hba の認証が scram-sha-256 でない（${f[f.length - 1]}）`);
  }
  return out;
}

/** AC12: RabbitMQ はデータを PVC に置き、ノード名を固定し、Recreate で替わる。 */
function rabbitmqPersistenceProblems(docs) {
  const out = [];
  const mq = docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'rabbitmq');
  if (!mq) return out;
  const spec = mq.spec.template.spec;
  const c = spec.containers.find((x) => x.name === 'rabbitmq') || spec.containers[0];
  const mount = (c.volumeMounts || []).find((m) => String(m.mountPath).replace(/\/$/, '') === '/var/lib/rabbitmq');
  const vol = mount && (spec.volumes || []).find((v) => v.name === mount.name);
  const claim = vol && vol.persistentVolumeClaim && vol.persistentVolumeClaim.claimName;
  if (!claim) out.push('infra: RabbitMQ の /var/lib/rabbitmq が PVC でない（再起動でキュー・メッセージを失う）');
  else if (!docs.some((d) => d.kind === 'PersistentVolumeClaim' && d.metadata.name === claim)) out.push(`infra: RabbitMQ の PVC ${claim} が宣言に無い`);
  const nodename = (c.env || []).find((e) => e.name === 'RABBITMQ_NODENAME');
  if (!nodename || !nodename.value) out.push('infra: RabbitMQ のノード名（RABBITMQ_NODENAME）が固定されていない（Pod が替わるとデータの置き場が替わる）');
  if (!(mq.spec.strategy && mq.spec.strategy.type === 'Recreate')) out.push('infra: RabbitMQ の Deployment が Recreate でない（ReadWriteOnce の PVC と両立しない）');
  return out;
}

/** 基盤の描画の判定（AC1〜AC6・AC11・AC12・AC15）。 */
function infraProblems(docs, localDocs) {
  const out = commonProblems(docs, 'infra');
  const kc = docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'keycloak');
  if (!kc) out.push('infra: Keycloak の Deployment が無い');
  else {
    const args = (kc.spec.template.spec.containers[0].args || []).map(String);
    if (args.includes('start-dev')) out.push('infra: Keycloak の引数に start-dev がある');
    if (!args.includes('start')) out.push('infra: Keycloak の引数に start が無い');
    // 起動の後のハングを作り直す（liveness）。起動中（realm の取り込み）は startupProbe が liveness を止める。
    const kcc = kc.spec.template.spec.containers[0];
    const probeAt = (p, path) => p && p.httpGet && p.httpGet.path === path && String(p.httpGet.port) === '9000';
    if (!probeAt(kcc.livenessProbe, '/health/live')) out.push('infra: Keycloak の livenessProbe が /health/live（9000）でない');
    if (!probeAt(kcc.startupProbe, '/health/started')) out.push('infra: Keycloak の startupProbe が /health/started（9000）でない（起動中に liveness が作り直す）');
  }
  for (const want of ['postgres', 'rabbitmq', 'keycloak', 'otel-collector']) {
    if (!docs.some((d) => d.kind === 'Deployment' && d.metadata.name === want)) out.push(`infra: ${want} の Deployment が無い`);
  }
  out.push(...mspIngressProblems(docs), ...postgresHbaProblems(docs), ...rabbitmqPersistenceProblems(docs));
  if (localDocs) {
    const imagesOf = (ds) => {
      const m = new Map();
      for (const d of ds) for (const c of containersOf(d)) m.set(String(c.image).split('@')[0], String(c.image));
      return m;
    };
    const local = imagesOf(localDocs);
    for (const [name, ref] of imagesOf(docs)) {
      if (!local.has(name)) out.push(`infra: ${name} は deploy/local/infra に無いイメージ`);
      else if (local.get(name) !== ref) out.push(`infra: ${name} の digest が deploy/local/infra と違う`);
    }
  }
  return out;
}

/** MSP の chart の描画の判定（AC1〜AC3・AC6 の chart の側）。 */
function chartProblems(docs) {
  const out = commonProblems(docs, 'chart');
  const deps = docs.filter((d) => ['Deployment', 'StatefulSet', 'DaemonSet', 'Job', 'CronJob'].includes(d.kind));
  const names = deps.map((d) => d.metadata.name).sort();
  if (JSON.stringify(names) !== JSON.stringify(['llmgateway-service'])) out.push(`chart: ワークロードが LLM ゲートウェイだけでない（${names.join(', ')}）`);
  // AC6: 入り・出の NetworkPolicy は deploy/cloud/infra が持つ（mspIngressProblems）。chart が描くと 2 か所に分かれる。
  for (const d of docs.filter((x) => x.kind === 'NetworkPolicy')) out.push(`chart: NetworkPolicy を描いている（${d.metadata.name}。入り・出は deploy/cloud/infra が持つ）`);
  if (docs.some((d) => d.kind === 'Namespace')) out.push('chart: Namespace を描いている（deploy/cloud/infra/namespaces.yaml が作る）');
  out.push(...gatewayListenerProblems(docs));
  return out;
}

/** AC13・AC14: LLM ゲートウェイの受け口は TLS だけで、ルートのファイルシステムは読み取り専用。 */
function gatewayListenerProblems(docs) {
  const out = [];
  const dep = docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'llmgateway-service');
  if (!dep) return ['chart: LLM ゲートウェイの Deployment が無い'];
  const spec = dep.spec.template.spec;
  const c = spec.containers[0];
  const env = Object.fromEntries((c.env || []).map((e) => [e.name, e.value]));
  if (!/^https:\/\/\+:8080$/.test(String(env.ASPNETCORE_URLS || ''))) out.push(`chart: LLM ゲートウェイの受け口が https だけでない（ASPNETCORE_URLS=${env.ASPNETCORE_URLS}）`);
  for (const k of ['Kestrel__Certificates__Default__Path', 'Kestrel__Certificates__Default__KeyPath']) if (!String(env[k] || '').startsWith('/etc/msp-service-tls/')) out.push(`chart: ${k} が受け口の証明書を指さない`);
  if ('Grpc__Port' in env || (c.ports || []).some((p) => p.name === 'grpc')) out.push('chart: LLM ゲートウェイに gRPC の h2c（平文）の受け口がある');
  for (const pr of ['livenessProbe', 'readinessProbe']) if (!(c[pr] && c[pr].httpGet && c[pr].httpGet.scheme === 'HTTPS')) out.push(`chart: ${pr} が HTTPS でない`);
  const tlsMount = (c.volumeMounts || []).find((m) => m.mountPath === '/etc/msp-service-tls');
  const tlsVol = tlsMount && (spec.volumes || []).find((v) => v.name === tlsMount.name);
  if (!(tlsVol && tlsVol.secret && tlsVol.secret.secretName === 'llmgateway-tls')) out.push('chart: 受け口の証明書が Secret llmgateway-tls から来ない');
  const svc = docs.find((d) => d.kind === 'Service' && d.metadata.name === 'llmgateway-service');
  if (svc && (svc.spec.ports || []).some((p) => p.name === 'grpc' || p.appProtocol === 'grpc')) out.push('chart: LLM ゲートウェイの Service が gRPC のポートを持つ');
  if (!(c.securityContext && String(c.securityContext.readOnlyRootFilesystem) === 'true')) out.push('chart: LLM ゲートウェイの readOnlyRootFilesystem が true でない');
  const tmp = (c.volumeMounts || []).find((m) => m.mountPath === '/tmp');
  const tmpVol = tmp && (spec.volumes || []).find((v) => v.name === tmp.name);
  if (!(tmpVol && tmpVol.emptyDir)) out.push('chart: LLM ゲートウェイの /tmp が emptyDir でない');
  for (const m of c.volumeMounts || []) {
    const v = (spec.volumes || []).find((x) => x.name === m.name);
    if (v && !v.emptyDir && String(m.readOnly) !== 'true') out.push(`chart: LLM ゲートウェイの ${m.mountPath} が読み取り専用でない（書き込みは emptyDir だけ）`);
  }
  return out;
}

function renderInfra() {
  return docsOf(run('kubectl', ['kustomize', 'deploy/cloud/infra']));
}
function renderLocalInfra() {
  return docsOf(run('kubectl', ['kustomize', 'deploy/local/infra']));
}
function renderChart(extra = [], chartDir = CHART) {
  return docsOf(run('helm', ['template', 'msp', chartDir, '-n', 'microservices-platform', '-f', 'deploy/cloud/values-cloud.yaml',
    '--set-string', `global.image.tag=${IMAGE_TAG}`, ...extra]));
}

/**
 * NFR-05, NFR-18, IADR-0689: 秘匿管理（deploy/cloud/vault）の描画の判定。
 *   - OpenBao は dev モードでなく（`-dev`・`*_DEV_ROOT_TOKEN_ID` が無い）、宣言の digest はローカルの OpenBao と同じ。
 *   - 受け口（8200）に入れるのは名前空間 external-secrets の controller だけ。入り・出とも既定で拒否。
 *   - Secret・ClusterSecretStore・PushSecret を描かない。外への口を持たない（commonProblems）。
 */
function vaultProblems(docs, esoDocs, localVaultImage) {
  // 秘匿管理は、deploy/cloud/vault にだけ置く（基盤・chart の描画には引き続き持ち込まない資源として数える）。
  const allow = ['vault', 'openbao'];
  const out = [...commonProblems(docs, 'deploy/cloud/vault', { allow }), ...commonProblems(esoDocs, 'deploy/cloud/vault/eso', { allow })];
  const dep = docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'openbao');
  if (!dep) return [...out, 'OpenBao の Deployment が無い'];
  const c = (dep.spec.template.spec.containers || [])[0] || {};
  const argv = [...(c.command || []), ...(c.args || [])].map(String);
  if (argv.some((a) => /^-dev/.test(a))) out.push(`OpenBao が dev モードで起きる: ${argv.join(' ')}`);
  if (!argv.includes('server')) out.push('OpenBao が server で起きない');
  if ((c.env || []).some((e) => /DEV_ROOT_TOKEN/.test(e.name))) out.push('OpenBao に dev の root の env がある');
  if (localVaultImage && c.image !== localVaultImage) out.push(`OpenBao のイメージがローカルと違う: ${c.image}`);
  if (!/@sha256:[0-9a-f]{64}$/.test(String(c.image))) out.push('OpenBao のイメージを digest で固定していない');
  const cfg = docs.find((d) => d.kind === 'ConfigMap' && d.metadata.name === 'openbao-config');
  const hcl = cfg ? String(cfg.data['cloud.hcl']) : '';
  if (!/storage "raft"/.test(hcl)) out.push('ストレージが raft でない');
  if (!/log_raw\s*=\s*"false"/.test(hcl) || /log_raw\s*=\s*"true"/.test(hcl)) out.push('監査が値を記録し得る（log_raw）');
  const np = docs.filter((d) => d.kind === 'NetworkPolicy' && d.metadata.namespace === 'openbao');
  const deny = np.find((d) => JSON.stringify(d.spec.podSelector) === '{}' && !d.spec.ingress && !d.spec.egress);
  if (!deny || !['Ingress', 'Egress'].every((t) => (deny.spec.policyTypes || []).includes(t))) out.push('openbao の名前空間に入り・出の既定拒否が無い');
  const ingress = np.flatMap((d) => (d.spec.ingress || []));
  for (const r of ingress) {
    const from = r.from || [];
    const onlyEso = from.length === 1 && JSON.stringify(from[0].namespaceSelector) === JSON.stringify({ matchLabels: { 'kubernetes.io/metadata.name': 'external-secrets' } })
      && JSON.stringify(from[0].podSelector) === JSON.stringify({ matchLabels: { 'app.kubernetes.io/name': 'external-secrets' } });
    if (!onlyEso) out.push(`OpenBao の入りに ESO の controller の外の送り元がある: ${JSON.stringify(from)}`);
    if (JSON.stringify((r.ports || []).map((p) => Number(p.port))) !== '[8200]') out.push(`OpenBao の入りのポートが 8200 だけでない: ${JSON.stringify(r.ports)}`);
  }
  if (ingress.length === 0) out.push('陽性対照: OpenBao の入りの許可が無い（ESO が届かない）');
  for (const d of esoDocs) if (/^Cluster/.test(d.kind) || d.kind === 'PushSecret') out.push(`${d.kind}/${d.metadata.name} を描いている（名前空間ごとの SecretStore だけ）`);
  return out;
}

/** IADR-0689 決定 9: クラウドの realm に、取引ユニットの aud / azp の強制の前提（audience のスコープと trading-bot）が残るか。 */
function cloudRealmAudienceProblems(realm) {
  const out = [];
  const scope = (realm.clientScopes || []).find((s) => s.name === 'ai-stock-trading-api-audience');
  if (!scope) out.push('クライアントスコープ ai-stock-trading-api-audience が無い');
  else if (!(scope.protocolMappers || []).some((m) => m.protocolMapper === 'oidc-audience-mapper' && (m.config || {})['included.custom.audience'] === 'ai-stock-trading-api')) {
    out.push('audience のスコープが ai-stock-trading-api を載せない');
  }
  const holders = (realm.clients || []).filter((c) => (c.defaultClientScopes || []).includes('ai-stock-trading-api-audience')).map((c) => c.clientId);
  if (JSON.stringify(holders) !== '["ai-stock-trading-owner"]') out.push(`audience のスコープの持ち手が owner だけでない: ${holders.join(', ')}`);
  if (!((realm.roles || {}).realm || []).some((r) => r.name === 'trading-bot')) out.push('realm ロール trading-bot が無い');
  const bots = (realm.users || []).filter((u) => (u.realmRoles || []).includes('trading-bot')).map((u) => u.username);
  if (JSON.stringify(bots) !== '["service-account-ai-stock-trading-owner"]') out.push(`trading-bot の持ち手が owner のサービスアカウントだけでない: ${bots.join(', ')}`);
  return out;
}

module.exports = { vaultProblems, cloudRealmAudienceProblems, commonProblems, keycloakExposureProblems, EDGE_KINDS, infraProblems, chartProblems, gatewayListenerProblems, mspIngressProblems, postgresHbaProblems, rabbitmqPersistenceProblems, docsOf, FORBIDDEN, DEV_LITERALS };

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
  ok('AC1〜AC6・AC11・AC12・AC15: 基盤の描画に持ち込まない資源・外への口・既定の資格情報・start-dev が無く、外部のイメージは deploy/local/infra と同じ digest。MSP の入りを絞り、Postgres は TLS だけ、RabbitMQ は永続', () => {
    assert.deepStrictEqual(infraProblems(renderInfra(), renderLocalInfra()), []);
  });
  ok('AC1〜AC3・AC6・AC13〜AC15: MSP の描画は LLM ゲートウェイだけで、NetworkPolicy を描かず、外への口・既定の資格情報が無く、受け口は TLS だけ・ルートは読み取り専用', () => {
    assert.deepStrictEqual(chartProblems(renderChart()), []);
  });
  ok('陰性対照: 既定の values（クラウドの値なし）の描画は判定に落ちる（判定が空回りしていない）', () => {
    const docs = docsOf(run('helm', ['template', 'msp', CHART, '-n', 'microservices-platform', '--set-string', `global.image.tag=${IMAGE_TAG}`]));
    assert.ok(chartProblems(docs).some((p) => /持ち込まない資源/.test(p)), '既定の描画で持ち込まない資源を検出しない');
  });
  ok('陰性対照（AC13・AC14）: TLS・読み取り専用を外した描画は判定に落ち、tls と grpcPort の併記は描画で止まる', () => {
    const plain = chartProblems(renderChart(['--set', 'services.llmgateway.tls=null', '--set', 'services.llmgateway.readOnlyRootFilesystem=false']));
    for (const want of [/https だけでない/, /livenessProbe が HTTPS でない/, /readOnlyRootFilesystem が true でない/, /\/tmp が emptyDir でない/]) {
      assert.ok(plain.some((p) => want.test(p)), `${want} を検出しない:\n${plain.join('\n')}`);
    }
    const r = spawnSync('helm', ['template', 'msp', CHART, '-n', 'microservices-platform', '-f', 'deploy/cloud/values-cloud.yaml',
      '--set-string', `global.image.tag=${IMAGE_TAG}`, '--set', 'services.llmgateway.grpcPort=8081'], { cwd: REPO_ROOT, encoding: 'utf8' });
    assert.notStrictEqual(r.status, 0, 'tls と grpcPort の併記が描画で止まらない');
    assert.match(r.stderr, /tls と grpcPort は併記できません/);
  });
  ok('変異（AC13）: chart の受け口の証明書のマウント・probe の HTTPS・https の受け口を外すと判定が赤になる（判定が空振りしない）', () => {
    const src = fs.readFileSync(path.join(REPO_ROOT, CHART, 'templates', 'deployment.yaml'), 'utf8');
    const mutate = (from, to) => {
      assert.ok(src.includes(from), `変異の置換元が無い（試験が古い）: ${from}`);
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'deploy-cloud-'));
      try {
        fs.cpSync(path.join(REPO_ROOT, CHART), dir, { recursive: true });
        fs.writeFileSync(path.join(dir, 'templates', 'deployment.yaml'), src.replace(from, to));
        return chartProblems(renderChart([], dir));
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    };
    const cases = [
      ['            {{- if $tlsListen }}\n            - name: service-tls\n', '            {{- if false }}\n            - name: service-tls\n', /受け口の証明書が Secret llmgateway-tls から来ない/],
      ['              {{- if $tlsListen }}\n              scheme: HTTPS\n', '              {{- if false }}\n              scheme: HTTPS\n', /livenessProbe が HTTPS でない/],
      ['value: {{ printf "https://+:%v" $svc.port | quote }}', 'value: {{ printf "http://+:%v" $svc.port | quote }}', /https だけでない/],
    ];
    for (const [from, to, want] of cases) {
      const p = mutate(from, to);
      assert.ok(p.some((x) => want.test(x)), `${want} の変異を捕まえない:\n${p.join('\n')}`);
    }
  });
  const renderVault = () => docsOf(run('kubectl', ['kustomize', 'deploy/cloud/vault']));
  const renderEso = () => docsOf(run('kubectl', ['kustomize', 'deploy/cloud/vault/eso']));
  const localVaultImage = () => {
    const d = docsOf(fs.readFileSync(path.join(REPO_ROOT, 'deploy/local/vault/vault-dev.yaml'), 'utf8')).find((x) => x.kind === 'Deployment');
    return d.spec.template.spec.containers[0].image;
  };
  ok('IADR-0689: 秘匿管理の描画 — OpenBao は dev モードでなく、ローカルと同じ digest、raft・値を記録しない監査。受け口に入れるのは ESO の controller だけ。Secret・ClusterSecretStore を描かない', () => {
    assert.deepStrictEqual(vaultProblems(renderVault(), renderEso(), localVaultImage()), []);
  });
  ok('IADR-0689 陰性対照: dev モード・別の送り元・別のポート・ClusterSecretStore・Secret を検出する', () => {
    const v = JSON.parse(JSON.stringify(renderVault()));
    const e = JSON.parse(JSON.stringify(renderEso()));
    v.find((d) => d.kind === 'Deployment').spec.template.spec.containers[0].command = ['bao', 'server', '-dev'];
    const ing = v.find((d) => d.kind === 'NetworkPolicy' && (d.spec.ingress || []).length);
    ing.spec.ingress[0].from.push({ namespaceSelector: {} });
    ing.spec.ingress[0].ports.push({ protocol: 'TCP', port: 8201 });
    e.push({ kind: 'ClusterSecretStore', metadata: { name: 'x' }, spec: {} }, { kind: 'Secret', metadata: { name: 'y', namespace: 'platform-infra' } });
    const p = vaultProblems(v, e, localVaultImage());
    for (const want of [/dev モード/, /ESO の controller の外の送り元/, /8200 だけでない/, /ClusterSecretStore/, /Secret を描いている/]) assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
  });
  ok('IADR-0689 決定 9: クラウドの realm に、audience のスコープ（owner の既定のスコープ）と trading-bot（owner のサービスアカウントだけ）が残る', () => {
    const { buildCloudRealm, CLOUD_CLIENTS } = require('../deploy/cloud/realm-for-cloud.js');
    const realm = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'deploy/keycloak/microservices-platform-realm.json'), 'utf8'));
    const secrets = Object.fromEntries(CLOUD_CLIENTS.map((c) => [c, ['probe', c, 'z'.repeat(32)].join('-')]));
    assert.deepStrictEqual(cloudRealmAudienceProblems(buildCloudRealm(realm, secrets)), []);
    assert.deepStrictEqual(cloudRealmAudienceProblems(buildCloudRealm(realm, {}, { template: true })), [], '雛形でも残る');
    // 陰性対照: スコープを外す・持ち手を足す・ロールを外す
    const cut = buildCloudRealm(realm, secrets);
    cut.clientScopes = cut.clientScopes.filter((s) => s.name !== 'ai-stock-trading-api-audience');
    assert.ok(cloudRealmAudienceProblems(cut).some((x) => /スコープ ai-stock-trading-api-audience が無い/.test(x)));
    const more = buildCloudRealm(realm, secrets);
    more.clients.find((c) => c.clientId === 'ai-stock-trading-svc').defaultClientScopes.push('ai-stock-trading-api-audience');
    assert.ok(cloudRealmAudienceProblems(more).some((x) => /持ち手が owner だけでない/.test(x)));
    const norole = buildCloudRealm(realm, secrets);
    norole.roles.realm = norole.roles.realm.filter((r) => r.name !== 'trading-bot');
    assert.ok(cloudRealmAudienceProblems(norole).some((x) => /trading-bot が無い/.test(x)));
  });
  ok('陰性対照: 基盤の描画へ start-dev・LoadBalancer・字面のパスワードを足すと判定に落ちる', () => {
    const docs = JSON.parse(JSON.stringify(renderInfra()));
    docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'keycloak').spec.template.spec.containers[0].args = ['start-dev'];
    docs.find((d) => d.kind === 'Service' && d.metadata.name === 'keycloak').spec.type = 'LoadBalancer';
    docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'postgres').spec.template.spec.containers[0].env.push({ name: 'POSTGRES_PASSWORD', value: 'x' });
    const p = infraProblems(docs, null);
    for (const want of [/start-dev/, /LoadBalancer/, /POSTGRES_PASSWORD が字面/]) assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
  });
  if (failed) {
    console.error(`✗ ${failed} 件が失敗した`);
    process.exit(1);
  }
  console.log('✓ deploy/cloud の描画は受け入れ基準を満たす');
}
