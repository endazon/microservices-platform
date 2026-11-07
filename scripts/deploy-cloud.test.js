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
 *   IADR-0693（認可サーバーへの通信の TLS）:
 *     KT1 Keycloak は HTTPS（8443）だけで受ける（KC_HOSTNAME は https・KC_HTTP_ENABLED=false・8080 の口が無い・証明書は Secret keycloak-tls
 *         を fsGroup で 0440・probe は HTTPS）。
 *     KT2 Keycloak の入りは TCP/8443 だけで、送り元は取引ユニットの Keycloak を呼ぶ Pod（名前空間 かつ Pod）と LLM ゲートウェイだけ。
 *         LLM ゲートウェイの出も 8443 だけ。
 *     KT3 併存の重ね（deploy/cloud-transition/keycloak-tls-coexist）は 8080 と 8443 を受け（LLM ゲートウェイの出も両方）、hostname を固定しない。
 *         既定の描画には無い。
 *     KT4 クラウドの chart の受け手は Authority が https・Auth__RequireHttpsMetadata=true・CA を信頼ストアに足す。既定・ローカルは変えない。
 *     KT5 global.auth.tls.enabled で Authority が https でなければ描画で止まる。
 *     KT6 PKI の role・ESO・policy（keycloak の発行は基盤の側だけ。取引ユニットは CA の証明書だけを読む）。
 *   IADR-0696（秘匿管理への通信の TLS）:
 *     OT1 OpenBao の受け口は TLS の 8443 だけ（listener に tls_disable が無く、証明書と鍵は Secret openbao-tls。平文の listener・
 *         8200 のポートが無い・probe は HTTPS・Pod の中の bao の宛先も https で CA を持つ）。Service と入りも 8443 だけ。
 *     OT2 ESO のストア（SecretStore・VaultDynamicSecret）は https://openbao.openbao.svc:8443 で、CA を ConfigMap openbao-ca で検証する。
 *     OT3 併存の重ね（deploy/cloud-transition/openbao-tls-coexist）は 8443 に加えて平文の 8200 も受ける（送り元は ESO の controller だけ）。
 *         既定の描画には無い。
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
 *   - 受け口に入れるのは名前空間 external-secrets の controller だけ。入り・出とも既定で拒否。
 *   - Secret・ClusterSecretStore・PushSecret を描かない。外への口を持たない（commonProblems）。
 *   - IADR-0696 OT1・OT2: 受け口は TLS の 8443 だけ。ESO は https で CA を検証する（openbaoTlsProblems）。
 *     coexist は併存の重ねの描画を見るとき（平文の 8200 も受ける）。
 */
function vaultProblems(docs, esoDocs, localVaultImage, { coexist = false } = {}) {
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
    const want = coexist ? `[${OPENBAO_TLS_PORT},${OPENBAO_PLAIN_PORT}]` : `[${OPENBAO_TLS_PORT}]`;
    if (JSON.stringify((r.ports || []).map((p) => Number(p.port))) !== want) out.push(`OpenBao の入りのポートが ${want} でない: ${JSON.stringify(r.ports)}`);
  }
  if (ingress.length === 0) out.push('陽性対照: OpenBao の入りの許可が無い（ESO が届かない）');
  for (const d of esoDocs) if (/^Cluster/.test(d.kind) || d.kind === 'PushSecret') out.push(`${d.kind}/${d.metadata.name} を描いている（名前空間ごとの SecretStore だけ）`);
  return [...out, ...openbaoTlsProblems(docs, esoDocs, { coexist })];
}

const OPENBAO_TLS_PORT = 8443;
const OPENBAO_PLAIN_PORT = 8200;
const OPENBAO_SERVER = `https://openbao.openbao.svc:${OPENBAO_TLS_PORT}`;
const OPENBAO_CA_PROVIDER = { type: 'ConfigMap', name: 'openbao-ca', key: 'ca.crt' };

/** OpenBao の設定（HCL）の listener "tcp" の塊を、中身の文字列として取り出す（入れ子の { } を数える）。 */
function hclListeners(hcl) {
  const out = [];
  const re = /^\s*listener\s+"tcp"\s*\{/gm;
  let m;
  while ((m = re.exec(hcl))) {
    let depth = 1;
    let i = m.index + m[0].length;
    const start = i;
    for (; i < hcl.length && depth > 0; i++) {
      if (hcl[i] === '{') depth++;
      else if (hcl[i] === '}') depth--;
    }
    out.push(hcl.slice(start, i - 1));
  }
  return out;
}
const hclValue = (block, key) => {
  const m = new RegExp(`^\\s*${key}\\s*=\\s*"?([^"\\n]*)"?`, 'm').exec(block);
  return m ? m[1].trim() : undefined;
};

/**
 * IADR-0696 OT1〜OT3: OpenBao の受け口が TLS だけであること（併存の重ねは平文の 8200 も受ける）と、ESO が https で CA を検証すること。
 *   docs は deploy/cloud/vault（または併存の重ね）の描画、esoDocs は deploy/cloud/vault/eso の描画（無ければ ESO を見ない）。
 */
function openbaoTlsProblems(docs, esoDocs, { coexist = false } = {}) {
  const out = [];
  const hcl = docs.filter((d) => d.kind === 'ConfigMap' && d.metadata.namespace === 'openbao').flatMap((d) => Object.values(d.data || {}).map(String)).join('\n');
  const listeners = hclListeners(hcl);
  const tls = listeners.filter((b) => !/^(true|1)$/.test(String(hclValue(b, 'tls_disable') || '')));
  const plain = listeners.filter((b) => /^(true|1)$/.test(String(hclValue(b, 'tls_disable') || '')));
  const port = (b) => Number(String(hclValue(b, 'address') || '').split(':').pop());
  if (JSON.stringify(tls.map(port)) !== `[${OPENBAO_TLS_PORT}]`) out.push(`OpenBao の TLS の listener が ${OPENBAO_TLS_PORT} の 1 つでない: ${JSON.stringify(tls.map(port))}`);
  for (const b of tls) {
    if (hclValue(b, 'tls_cert_file') !== '/openbao/tls/tls.crt' || hclValue(b, 'tls_key_file') !== '/openbao/tls/tls.key') out.push('OpenBao の TLS の listener の証明書・鍵が Secret openbao-tls のマウント（/openbao/tls）でない');
    if (hclValue(b, 'tls_min_version') !== 'tls13') out.push(`OpenBao の TLS の listener の最小の版が tls13 でない: ${hclValue(b, 'tls_min_version')}`);
  }
  const wantPlain = coexist ? `[${OPENBAO_PLAIN_PORT}]` : '[]';
  if (JSON.stringify(plain.map(port)) !== wantPlain) out.push(`OpenBao の平文の listener が ${wantPlain} でない（平文の口が閉じていない）: ${JSON.stringify(plain.map(port))}`);
  if (!/^\s*api_addr\s*=\s*"https:\/\//m.test(hcl)) out.push('OpenBao の api_addr が https でない');
  // 最終の段に併存の段の残り（平文の設定の ConfigMap・2 つ目の設定ファイル）が無いこと。
  if (!coexist && docs.some((d) => d.kind === 'ConfigMap' && d.metadata.name === 'openbao-config-coexist')) out.push('最終の段に併存の段の ConfigMap openbao-config-coexist が残っている');
  const dep = docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'openbao');
  if (dep) {
    const spec = dep.spec.template.spec;
    const c = (spec.containers || [])[0] || {};
    const env = Object.fromEntries((c.env || []).map((e) => [e.name, e.value]));
    if (env.BAO_ADDR !== `https://127.0.0.1:${OPENBAO_TLS_PORT}` || env.BAO_CACERT !== '/openbao/tls/ca.crt') out.push(`Pod の中の bao の宛先が TLS の口と CA でない: ${env.BAO_ADDR} ${env.BAO_CACERT}`);
    if ((c.env || []).some((e) => /SKIP_VERIFY|TLS_SKIP/i.test(e.name))) out.push('OpenBao の Pod に検証を外す env がある');
    const ports = (c.ports || []).map((p) => Number(p.containerPort)).sort();
    const wantPorts = coexist ? [OPENBAO_PLAIN_PORT, OPENBAO_TLS_PORT] : [OPENBAO_TLS_PORT];
    if (!coexist && [...(c.command || []), ...(c.args || [])].some((a) => /coexist/.test(String(a)))) out.push('最終の段の OpenBao が併存の段の設定ファイルを読む');
    if (JSON.stringify(ports) !== JSON.stringify(wantPorts)) out.push(`OpenBao の受け口が ${JSON.stringify(wantPorts)} でない: ${JSON.stringify(ports)}`);
    const probe = (c.readinessProbe || {}).httpGet || {};
    if (probe.scheme !== 'HTTPS' || Number(probe.port) !== OPENBAO_TLS_PORT) out.push(`OpenBao の readiness が HTTPS の ${OPENBAO_TLS_PORT} でない`);
    const mount = (c.volumeMounts || []).find((v) => v.mountPath === '/openbao/tls');
    const vol = mount && (spec.volumes || []).find((v) => v.name === mount.name);
    if (!vol || !vol.secret || vol.secret.secretName !== 'openbao-tls' || vol.secret.optional) out.push('受け口の証明書が Secret openbao-tls（optional でない）のマウントでない');
  }
  const svc = docs.find((d) => d.kind === 'Service' && d.metadata.name === 'openbao');
  const svcPorts = svc ? (svc.spec.ports || []).map((p) => Number(p.port)).sort() : [];
  const wantSvc = coexist ? [OPENBAO_PLAIN_PORT, OPENBAO_TLS_PORT] : [OPENBAO_TLS_PORT];
  if (JSON.stringify(svcPorts) !== JSON.stringify(wantSvc)) out.push(`OpenBao の Service のポートが ${JSON.stringify(wantSvc)} でない: ${JSON.stringify(svcPorts)}`);
  // OT2: ESO のストア（SecretStore の vault・VaultDynamicSecret の provider）。
  const providers = (esoDocs || []).flatMap((d) => {
    if (d.kind === 'SecretStore') return [[`SecretStore ${d.metadata.namespace}/${d.metadata.name}`, ((d.spec || {}).provider || {}).vault || {}]];
    if (d.kind === 'VaultDynamicSecret') return [[`VaultDynamicSecret ${d.metadata.namespace}/${d.metadata.name}`, (d.spec || {}).provider || {}]];
    return [];
  });
  if (esoDocs && providers.length === 0) out.push('陽性対照: ESO のストアが無い');
  for (const [id, v] of providers) {
    if (v.server !== OPENBAO_SERVER) out.push(`${id}: 宛先が ${OPENBAO_SERVER} でない: ${v.server}`);
    const ca = v.caProvider || {};
    if (Object.keys(ca).length !== 3 || Object.entries(OPENBAO_CA_PROVIDER).some(([k, x]) => ca[k] !== x)) out.push(`${id}: CA を ConfigMap openbao-ca で検証しない: ${JSON.stringify(v.caProvider || null)}`);
    if (Object.keys(v).some((k) => /insecure|skip/i.test(k))) out.push(`${id}: 検証を外す指定がある`);
  }
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
  const holders = (realm.clients || []).filter((c) => (c.defaultClientScopes || []).includes('ai-stock-trading-api-audience')).map((c) => c.clientId).sort();
  // IADR-0700: owner と、取引ユニットのサービスごとのクライアント（SERVICE_CLIENTS）だけ。旧の共有クライアント（ai-stock-trading-svc）は持たない。
  const { SERVICE_CLIENTS } = require('../deploy/cloud/realm-for-cloud.js');
  const want = ['ai-stock-trading-owner', ...SERVICE_CLIENTS].sort();
  if (JSON.stringify(holders) !== JSON.stringify(want)) out.push(`audience のスコープの持ち手が owner とサービスごとのクライアントだけでない: ${holders.join(', ')}`);
  if (!((realm.roles || {}).realm || []).some((r) => r.name === 'trading-bot')) out.push('realm ロール trading-bot が無い');
  const bots = (realm.users || []).filter((u) => (u.realmRoles || []).includes('trading-bot')).map((u) => u.username);
  if (JSON.stringify(bots) !== '["service-account-ai-stock-trading-owner"]') out.push(`trading-bot の持ち手が owner のサービスアカウントだけでない: ${bots.join(', ')}`);
  return out;
}

/**
 * NFR-18, IADR-0689 決定 12: クラウドの段 2 の切り替え（ブローカの権限 narrow・LLM ゲートウェイの 3 つ）。
 *   - LLM ゲートウェイ: Llm__RouteValidation__Mode=Fail・Llm__CallerPolicy__Mode=Enforce・Llm__CallLimits__Mode=Enforce。
 *   - ブローカ: 起動の直後の復元が vhost 全体（'.*' 3 つ）を当てず、生成物 rabbitmq-permissions の行だけを当てる。表は非 optional。
 */
const STAGE2_LLM_ENV = Object.freeze({ Llm__RouteValidation__Mode: 'Fail', Llm__CallerPolicy__Mode: 'Enforce', Llm__CallLimits__Mode: 'Enforce' });
function llmGatewayEnv(docs) {
  const d = docs.find((x) => x.kind === 'Deployment' && /llmgateway/.test(x.metadata.name));
  const c = d ? (d.spec.template.spec.containers || [])[0] || {} : {};
  return Object.fromEntries((c.env || []).filter((e) => typeof e.value === 'string').map((e) => [e.name, e.value]));
}
function stage2Problems(chartDocs, infraDocs) {
  const out = [];
  const env = llmGatewayEnv(chartDocs);
  for (const [k, v] of Object.entries(STAGE2_LLM_ENV)) if (env[k] !== v) out.push(`LLM ゲートウェイの ${k} が ${v} でない（${env[k]}）`);
  const boot = infraDocs.find((d) => d.kind === 'ConfigMap' && d.metadata.name === 'rabbitmq-boot');
  const script = boot ? String(boot.data['restore-users.sh']) : '';
  if (/set_permissions[^\n]*'\.\*' '\.\*' '\.\*'/.test(script)) out.push('ブローカの復元が vhost 全体の権限（段 1）を当てる');
  if (!/\$PERMS_DIR\/permissions\.txt/.test(script)) out.push('ブローカの復元が絞った権限の表を読まない');
  const dep = infraDocs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'rabbitmq');
  const vol = dep ? (dep.spec.template.spec.volumes || []).find((v) => v.configMap && v.configMap.name === 'rabbitmq-permissions') : null;
  if (!vol) out.push('ブローカが ConfigMap rabbitmq-permissions をマウントしない');
  else if (String(vol.configMap.optional) === 'true') out.push('絞った権限の表が optional（無ければ権限なしで黙って立つ）');
  if (!infraDocs.some((d) => d.kind === 'ConfigMap' && d.metadata.name === 'rabbitmq-permissions')) out.push('ConfigMap rabbitmq-permissions を描かない');
  return out;
}

/**
 * NFR-18, NFR-21, IADR-0692: クラウドの LLM ゲートウェイの当日の費用の上限（所有者の裁定 2026-11-07。合計 1 日 20 USD）。
 *   - 既定（呼び手の項目が無い呼び手のそれぞれ）0.5 USD・取引ユニットの呼び手 18 USD。ナレッジの 4 つの呼び手は既定。
 *   - 合計 = 取引ユニット ＋ 既定 × ナレッジの呼び手の数 が 20 USD（許可表に無い呼び手は許可表が拒む）。
 *   - 上限はプロセスごとなので、LLM ゲートウェイは 1 レプリカで HPA を描かない。
 */
const CLOUD_DAILY_COST_TOTAL_USD = 20;
const CLOUD_LLM_COST_ENV = Object.freeze({
  Llm__CallLimits__Default__DailyCostLimit: '0.5',
  'Llm__CallLimits__Callers__ai-stock-trading-llm-caller__DailyCostLimit': '18',
});
const KNOWLEDGE_LLM_CALLERS = Object.freeze(['aianalysis-service', 'graph-service', 'retrieval-service', 'conversion-service']);
function cloudCostLimitProblems(chartDocs) {
  const out = [];
  const env = llmGatewayEnv(chartDocs);
  for (const [k, v] of Object.entries(CLOUD_LLM_COST_ENV)) if (env[k] !== v) out.push(`LLM ゲートウェイの ${k} が ${v} でない（${env[k]}）`);
  const num = (k) => Number(env[k]);
  const def = num('Llm__CallLimits__Default__DailyCostLimit');
  if (!(def > 0)) out.push('当日の費用の上限の既定が正の値でない（段 2 では起動しない・未設定なら費用が無制限）');
  // 呼び手の項目は取引ユニットだけ（ナレッジの呼び手は既定。項目を足すと合計が変わる）。
  const callerKeys = Object.keys(env).filter((k) => /^Llm__CallLimits__Callers__.+__DailyCostLimit$/i.test(k));
  if (JSON.stringify(callerKeys) !== '["Llm__CallLimits__Callers__ai-stock-trading-llm-caller__DailyCostLimit"]') {
    out.push(`呼び手ごとの当日の費用の上限が取引ユニットの呼び手だけでない: ${callerKeys.join(', ')}`);
  }
  const total = num('Llm__CallLimits__Callers__ai-stock-trading-llm-caller__DailyCostLimit') + def * KNOWLEDGE_LLM_CALLERS.length;
  if (Math.abs(total - CLOUD_DAILY_COST_TOTAL_USD) > 1e-9) out.push(`当日の費用の上限の合計が ${CLOUD_DAILY_COST_TOTAL_USD} USD でない（${total}）`);
  if (Object.keys(env).some((k) => /^Llm__CallLimits__.*PurposeDailyCostLimits/i.test(k))) out.push('用途ごとの当日の費用の上限を描いている（合計の前提が変わる）');
  const d = chartDocs.find((x) => x.kind === 'Deployment' && /llmgateway/.test(x.metadata.name));
  if (!d || Number(d.spec.replicas) !== 1) out.push(`LLM ゲートウェイが 1 レプリカでない（上限はプロセスごと。${d ? d.spec.replicas : '描かれない'}）`);
  if (chartDocs.some((x) => x.kind === 'HorizontalPodAutoscaler')) out.push('HPA を描いている（上限がレプリカ数倍になる）');
  return out;
}

/**
 * NFR-21, IADR-0692: 月次予算（Llm:Budget:MonthlyLimits。用途別）は、当日の費用の上限から導く。
 *   月次予算(用途) = 呼び手の日の上限のうち、その用途へ割り当てた額 × 30 日 × 0.5（日の上限の半分の速さを超えたら知らせる）。
 *   取引ユニットの用途 other（stage0-recording・policy-revision・report-regeneration は単価表の用途に無く other に丸まる）は
 *   月次予算に置けない（検証器が拒む）ので、割り当てだけを持ち、月次予算は置かない。
 */
const MONTHLY_BUDGET_FACTOR = 30 * 0.5;
const DAILY_COST_ALLOCATION = Object.freeze({
  'ai-stock-trading-llm-caller': { 'trade-decision': 9, 'trade-decision-screening': 2, 'report-daily': 2, 'report-weekly': 1.5, 'report-monthly': 0.5, other: 3 },
  'aianalysis-service': { 'rag-answer': 0.25, analysis: 0.25 },
  'graph-service': { 'graph-suggestion': 0.25, 'graph-cluster-summary': 0.25 },
  'retrieval-service': { rerank: 0.5 },
  'conversion-service': { 'diagram-coding': 0.5 },
});
function monthlyBudgetProblems(appsettings, env) {
  const out = [];
  const cap = (caller) => Number(env[`Llm__CallLimits__Callers__${caller}__DailyCostLimit`] ?? env.Llm__CallLimits__Default__DailyCostLimit);
  const want = {};
  for (const [caller, shares] of Object.entries(DAILY_COST_ALLOCATION)) {
    const sum = Object.values(shares).reduce((a, b) => a + b, 0);
    if (Math.abs(sum - cap(caller)) > 1e-9) out.push(`${caller} の割り当ての合計（${sum}）が日の上限（${cap(caller)}）と違う`);
    for (const [purpose, v] of Object.entries(shares)) if (purpose !== 'other') want[purpose] = v * MONTHLY_BUDGET_FACTOR;
  }
  const got = ((appsettings.Llm || {}).Budget || {}).MonthlyLimits || {};
  for (const [purpose, v] of Object.entries(want)) if (got[purpose] !== v) out.push(`月次予算 ${purpose} が ${v} でない（${got[purpose]}）`);
  for (const purpose of Object.keys(got)) if (!(purpose in want)) out.push(`割り当てに無い用途の月次予算がある: ${purpose}`);
  return out;
}

/**
 * NFR-18, IADR-0692: k3s の導入（README §1-4）は版を固定し、取得物を sha256 で照合してから入れる。
 *   - 取得したスクリプトを照合せずにシェルへ流す形（`curl … | sh` 等）が、クラウドの手順書のどこにも無い。
 *   - README の版は、表（scripts/lib/pinned-artifacts.sh の k3s / k3s-install）に在る。照合（sha256sum -c）が導入より前。
 *   - インストーラには取得をさせない（INSTALL_K3S_SKIP_DOWNLOAD=true）。k3s の起動の旗は既存の値のまま。
 */
const K3S_FLAGS = Object.freeze(['server', '--disable traefik', '--disable servicelb', '--secrets-encryption',
  '--bind-address 127.0.0.1', '--advertise-address 127.0.0.1', '--node-ip 127.0.0.1', '--write-kubeconfig-mode 0600']);
const PIPE_TO_SHELL = /\b(?:curl|wget)\b[^\n|]*\|\s*(?:sudo\s+)?(?:[A-Za-z_][A-Za-z0-9_]*=(?:"[^"]*"|'[^']*'|[^\s"']\S*)\s+)*(?:sh|bash|zsh)\b/;
// 🔴 代入の値の 3 つの選択肢は互いに重ならない（引用符で始まる値は引用の形だけが当たる）。重なると、代入を長く並べた行で
//   繰り返し（`(…\s+)*`）と掛け合わさって指数的に戻る（下の試験が、病的な入力ですぐ返ることを見る）。
function pipeToShellProblems(files) {
  const out = [];
  for (const { rel, text } of files) {
    // 行継続（\ 改行）を 1 行へ畳んでから見る。
    text.replace(/\\\n/g, ' ').split('\n').forEach((line, i) => { if (PIPE_TO_SHELL.test(line)) out.push(`${rel}: 取得物を照合せずにシェルへ流す: ${line.trim().slice(0, 120)}`); });
  }
  return out;
}
function k3sInstallProblems(readme, pinnedLib) {
  const out = [];
  const sec = (/### 1-4\. k3s\n([\s\S]*?)\n### /.exec(readme) || [])[1] || '';
  if (!sec) return ['README に §1-4 k3s が無い'];
  const ver = (/^K3S_VERSION=(\S+)$/m.exec(sec) || [])[1];
  if (!ver) out.push('§1-4 に K3S_VERSION= が無い');
  for (const t of ['k3s', 'k3s-install']) {
    const m = new RegExp(`^\\s*${t.replace('-', '\\-')}@(\\S+?)\\) printf '%s %s' '(https://[^']+)' '([0-9a-f]{64})'`, 'm').exec(pinnedLib);
    if (!m) { out.push(`pinned-artifacts.sh の表に ${t} が無い`); continue; }
    if (m[1] !== ver) out.push(`${t} の表の版（${m[1]}）が README の版（${ver}）と違う`);
    if (!m[2].includes(encodeURIComponent(ver || ''))) out.push(`${t} の取得元が版のタグを指さない: ${m[2]}`);
    if (/get\.k3s\.io|\/main\/|\/master\//.test(m[2])) out.push(`${t} の取得元が版で固定されない: ${m[2]}`);
  }
  if (ver && sec.split(ver).length - 1 < 3) out.push('§1-4 のノード側の手順の版が K3S_VERSION と違う');
  const check = sec.indexOf('sha256sum -c k3s.sha256', sec.indexOf('ノードで'));
  const install = sec.indexOf('install -m 0755');
  const run = sec.indexOf('sh ./k3s-install-');
  if (check < 0 || install < 0 || run < 0 || !(check < install && install < run)) out.push('ノードで照合（sha256sum -c）してから導入する順になっていない');
  if (!/INSTALL_K3S_SKIP_DOWNLOAD=true INSTALL_K3S_EXEC=/.test(sec)) out.push('インストーラに取得をさせている（INSTALL_K3S_SKIP_DOWNLOAD=true が無い）');
  const exec = ((/INSTALL_K3S_EXEC="([^"]*)"/.exec(sec.replace(/\\\n\s*/g, ' ')) || [])[1] || '').trim().split(/\s+(?=--)/);
  if (JSON.stringify(exec) !== JSON.stringify(K3S_FLAGS)) out.push(`k3s の起動の旗が既存の値と違う: ${exec.join(' ')}`);
  out.push(...pipeToShellProblems([{ rel: 'deploy/cloud/README.md', text: readme }]));
  return out;
}


/** IADR-0693: Keycloak を呼ぶ取引ユニットの Pod（取引ユニットの chart の values-cloud の描画で Keycloak の URL を持つ Deployment）。 */
const KEYCLOAK_AST_CALLERS = [
  'audit-service', 'backtest-service', 'configuration-service', 'cost-control-service', 'information-collection-service', 'market-monitor-service',
  'notification-service', 'order-execution-service', 'report-service', 'risk-management-service', 'trade-decision-service',
];
const KEYCLOAK_HTTPS_PORT = 8443;
const KEYCLOAK_HTTP_PORT = 8080;
const KEYCLOAK_ISSUER = 'https://keycloak:8443/realms/platform';

/** KT1〜KT3: Keycloak の受け口・Service・入りと LLM ゲートウェイの出。coexist は併存の重ねの描画を見るとき。 */
function keycloakTlsProblems(docs, { coexist = false } = {}) {
  const out = [];
  const kc = docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'keycloak');
  if (!kc) return ['infra: Keycloak の Deployment が無い'];
  const spec = kc.spec.template.spec;
  const c = spec.containers.find((x) => x.name === 'keycloak') || spec.containers[0];
  const env = Object.fromEntries((c.env || []).map((e) => [e.name, e.value === undefined ? undefined : String(e.value)]));
  if (coexist) {
    if ('KC_HOSTNAME' in env) out.push('併存: hostname を固定している（KC_HOSTNAME。http で引いた呼び手の発行元が合わない）');
    if (env.KC_HOSTNAME_STRICT !== 'false') out.push('併存: KC_HOSTNAME_STRICT が false でない');
    if (env.KC_HTTP_ENABLED !== 'true') out.push('併存: KC_HTTP_ENABLED が true でない');
  } else {
    if (`${env.KC_HOSTNAME}/realms/platform` !== KEYCLOAK_ISSUER) out.push(`infra: Keycloak の KC_HOSTNAME が https://keycloak:8443 でない（${env.KC_HOSTNAME}）`);
    if (env.KC_HTTP_ENABLED !== 'false') out.push(`infra: Keycloak の平文の口が閉じていない（KC_HTTP_ENABLED=${env.KC_HTTP_ENABLED}）`);
    if ('KC_HOSTNAME_STRICT' in env) out.push('infra: Keycloak の hostname の固定を緩めている（KC_HOSTNAME_STRICT。併存の段の形）');
  }
  const tlsVol = (spec.volumes || []).find((v) => v.secret && v.secret.secretName === 'keycloak-tls');
  const tlsMount = tlsVol && (c.volumeMounts || []).find((m) => m.name === tlsVol.name);
  if (!tlsMount) out.push('infra: Keycloak の証明書が Secret keycloak-tls から来ない');
  else {
    const dir = String(tlsMount.mountPath).replace(/\/$/, '');
    for (const k of ['KC_HTTPS_CERTIFICATE_FILE', 'KC_HTTPS_CERTIFICATE_KEY_FILE']) {
      if (!String(env[k] || '').startsWith(`${dir}/`)) out.push(`infra: Keycloak の ${k} が Secret keycloak-tls のマウントを指さない`);
    }
    if (String(tlsMount.readOnly) !== 'true') out.push('infra: Keycloak の証明書のマウントが読み取り専用でない');
    if (!['288', '0440', '440'].includes(String(tlsVol.secret.defaultMode))) out.push(`infra: Keycloak の鍵のファイルが 0440 でない（${tlsVol.secret.defaultMode}）`);
    if (!(spec.securityContext && String(spec.securityContext.fsGroup) === '1000')) out.push('infra: Keycloak の Pod に fsGroup 1000 が無い（0440 の鍵を読めない）');
  }
  for (const pr of ['startupProbe', 'livenessProbe', 'readinessProbe']) {
    if (!(c[pr] && c[pr].httpGet && c[pr].httpGet.scheme === 'HTTPS')) out.push(`infra: Keycloak の ${pr} が HTTPS でない（管理の口も TLS になる）`);
  }
  const want = coexist ? [KEYCLOAK_HTTP_PORT, KEYCLOAK_HTTPS_PORT] : [KEYCLOAK_HTTPS_PORT];
  const cports = (c.ports || []).map((p) => Number(p.containerPort)).filter((p) => p !== 9000).sort((a, b) => a - b);
  if (JSON.stringify(cports) !== JSON.stringify(want)) out.push(`infra: Keycloak の受け口が ${want.join('・')} だけでない（${cports.join(', ')}）`);
  const svc = docs.find((d) => d.kind === 'Service' && d.metadata.name === 'keycloak' && d.metadata.namespace === 'platform-infra');
  const sports = svc ? (svc.spec.ports || []).map((p) => Number(p.targetPort || p.port)).sort((a, b) => a - b) : [];
  if (JSON.stringify(sports) !== JSON.stringify(want)) out.push(`infra: Keycloak の Service のポートが ${want.join('・')} だけでない（${sports.join(', ')}）`);
  // 入り: Keycloak を選ぶ許可はちょうど 1 枚。各要素は名前空間 かつ Pod（AND）。名前空間だけの要素は名前空間の全 Pod へ開く。
  const isKc = (d) => d.kind === 'NetworkPolicy' && d.metadata.namespace === 'platform-infra'
    && JSON.stringify(d.spec.podSelector || {}) === JSON.stringify({ matchLabels: { app: 'keycloak' } }) && (d.spec.ingress || []).length;
  const allows = docs.filter(isKc);
  if (allows.length !== 1) out.push(`infra: Keycloak への入りの許可がちょうど 1 枚でない（${allows.length}）`);
  for (const d of allows) {
    for (const rule of d.spec.ingress) {
      const seen = {};
      for (const f of rule.from || []) {
        const ns = f.namespaceSelector && f.namespaceSelector.matchLabels && f.namespaceSelector.matchLabels['kubernetes.io/metadata.name'];
        const pods = f.podSelector && f.podSelector.matchLabels && f.podSelector.matchLabels.app
          ? [String(f.podSelector.matchLabels.app)]
          : ((f.podSelector && f.podSelector.matchExpressions) || []).filter((e) => e.key === 'app' && e.operator === 'In').flatMap((e) => e.values.map(String));
        if (!ns || !f.podSelector || !pods.length) {
          out.push(`infra: ${d.metadata.name} に「名前空間 かつ Pod」でない送り元がある（${JSON.stringify(f)}）`);
          continue;
        }
        seen[ns] = [...(seen[ns] || []), ...pods].sort();
      }
      // IADR-0700: 稼働中の realm へ機密クライアントを足す Job（deploy/cloud/keycloak-clients。流すときだけ在る）。
      const expected = { [AST_NS]: [...KEYCLOAK_AST_CALLERS].sort(), [MSP_NS]: ['llmgateway-service'], 'platform-infra': ['keycloak-add-clients'] };
      if (JSON.stringify(Object.keys(seen).sort().map((k) => [k, seen[k]])) !== JSON.stringify(Object.keys(expected).sort().map((k) => [k, expected[k]]))) {
        out.push(`infra: ${d.metadata.name} の送り元が取引ユニットの Keycloak を呼ぶ Pod と LLM ゲートウェイだけでない（${JSON.stringify(seen)}）`);
      }
      const ports = (rule.ports || []).map((x) => Number(x.port)).sort((a, b) => a - b);
      if (JSON.stringify(ports) !== JSON.stringify(want)) out.push(`infra: ${d.metadata.name} のポートが ${want.join('・')} だけでない（${ports.join(', ')}）`);
    }
  }
  const egress = docs.find((d) => d.kind === 'NetworkPolicy' && d.metadata.name === 'llmgateway-egress');
  const toKc = egress ? (egress.spec.egress || []).filter((r) => (r.to || []).some((t) => t.podSelector && t.podSelector.matchLabels && t.podSelector.matchLabels.app === 'keycloak')) : [];
  const eports = toKc.flatMap((r) => (r.ports || []).map((x) => Number(x.port))).sort((a, b) => a - b);
  // 併存の間は、まだ平文の構成で動く（か切り戻した）LLM ゲートウェイも鍵・トークンを取れるよう、出も 8080 と 8443 の両方。
  if (JSON.stringify(eports) !== JSON.stringify(want)) out.push(`infra: LLM ゲートウェイから Keycloak への出が ${want.join('・')} だけでない（${eports.join(', ')}）`);
  return out;
}

/** KT4: クラウドの chart の受け手（LLM ゲートウェイ）は https の Authority を検証つきで使う。 */
function authTlsProblems(docs) {
  const out = [];
  const dep = docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'llmgateway-service');
  if (!dep) return ['chart: LLM ゲートウェイの Deployment が無い'];
  const spec = dep.spec.template.spec;
  const c = spec.containers[0];
  const env = Object.fromEntries((c.env || []).map((e) => [e.name, e.value === undefined ? undefined : String(e.value)]));
  if (env.Auth__Authority !== KEYCLOAK_ISSUER) out.push(`chart: Auth__Authority が ${KEYCLOAK_ISSUER} でない（${env.Auth__Authority}）`);
  if ('Auth__MetadataAddress' in env && !/^https:\/\//.test(String(env.Auth__MetadataAddress))) out.push('chart: Auth__MetadataAddress が https でない');
  if (env.Auth__RequireHttpsMetadata !== 'true') out.push(`chart: Auth__RequireHttpsMetadata が true でない（${env.Auth__RequireHttpsMetadata}）`);
  if (String(env.Auth__ValidIssuers || '').split(/[\s,]+/).some((x) => /^http:\/\//.test(x))) out.push('chart: Auth__ValidIssuers に平文の発行元がある（移す間だけ --set-string で足す）');
  if (env.SSL_CERT_DIR !== '/etc/ssl/certs:/etc/platform-infra-ca') out.push(`chart: SSL_CERT_DIR が既定のストア ＋ 基盤の CA でない（${env.SSL_CERT_DIR}）`);
  const caMount = (c.volumeMounts || []).find((m) => m.mountPath === '/etc/platform-infra-ca');
  const caVol = caMount && (spec.volumes || []).find((v) => v.name === caMount.name);
  if (!(caVol && caVol.configMap && caVol.configMap.name === 'platform-infra-ca')) out.push('chart: 基盤の CA（ConfigMap platform-infra-ca）をマウントしていない');
  return out;
}

/** KT6: PKI の role・ESO・policy。texts は { lib, platformInfraEso, mspEso, astEso, configure }。 */
function keycloakPkiProblems(esoDocs, texts) {
  const out = [];
  const gen = esoDocs.find((d) => d.kind === 'VaultDynamicSecret' && d.metadata.name === 'keycloak-tls');
  if (!gen) out.push('eso: VaultDynamicSecret keycloak-tls が無い');
  else {
    if (gen.metadata.namespace !== 'platform-infra') out.push('eso: keycloak-tls の発行が platform-infra の名前空間でない');
    if (gen.spec.path !== 'pki_cloud/issue/keycloak') out.push(`eso: keycloak-tls の発行の口が pki_cloud/issue/keycloak でない（${gen.spec.path}）`);
    const p = gen.spec.parameters || {};
    if (p.common_name !== 'keycloak' || p.alt_names !== 'keycloak.platform-infra,keycloak.platform-infra.svc,keycloak.platform-infra.svc.cluster.local') out.push('eso: keycloak-tls の名前が従来の 4 つの形でない');
    if (gen.spec.provider.auth.kubernetes.role !== 'platform-infra-eso') out.push('eso: keycloak-tls を基盤の側の role 以外で発行する');
  }
  const es = esoDocs.find((d) => d.kind === 'ExternalSecret' && d.metadata.name === 'keycloak-tls');
  const data = es && es.spec.target.template && es.spec.target.template.data;
  if (!es || !data || JSON.stringify(Object.keys(data).sort()) !== JSON.stringify(['tls.crt', 'tls.key'])) out.push('eso: ExternalSecret keycloak-tls が tls.crt・tls.key だけを写さない');
  if (!/(^|\s|")keycloak:platform-infra(\s|"|$)/.test((texts.lib.match(/^OB_PKI_ROLES="([^"]*)"/m) || [])[0] || '')) out.push('vault: lib.sh の OB_PKI_ROLES に keycloak:platform-infra が無い');
  if (!/ob_pki_roles b/.test(texts.configure)) out.push('vault: configure.sh が lib.sh の role の一覧を書かない');
  const issues = (t) => [...t.matchAll(/^path\s+"pki_cloud\/issue\/([^"]+)"/gm)].map((m) => m[1]).sort();
  if (JSON.stringify(issues(texts.platformInfraEso)) !== JSON.stringify(['keycloak', 'postgres', 'rabbitmq'])) out.push(`vault: 基盤の側の発行の範囲が postgres・rabbitmq・keycloak でない（${issues(texts.platformInfraEso).join(', ')}）`);
  if (issues(texts.mspEso).includes('keycloak')) out.push('vault: MSP の側が keycloak の証明書を発行できる');
  if (/pki_cloud\//.test(texts.astEso)) out.push('vault: 取引ユニットの側が PKI に触れる（CA の証明書は KV の cloud/shared/platform-infra-ca で読む）');
  if (!/^path\s+"cloud\/data\/shared\/platform-infra-ca"\s+\{\s*capabilities\s*=\s*\["read"\]\s*\}/m.test(texts.astEso)) out.push('vault: 取引ユニットの側が CA の証明書（cloud/shared/platform-infra-ca）を読めない');
  return out;
}

/**
 * NFR-18, NFR-05, IADR-0695: 取引ユニットの DB のサービスごとの利用者（AC7）。
 *   - 7 つのサービス（ConfigMap の手順の SERVICES・Postgres への入りの 7 つ・初期化の 7 つの DB）・ESO の Secret ast-db-<サービス>・Job の env・
 *     2 つの policy の名指しが同じ集合である（どれかに足し忘れると、そのサービスだけ共有の利用者のまま・Job が起動しない）。
 *   - Job: イメージは Postgres と同じ digest、TLS は verify-full、値は secretKeyRef だけ、Postgres への入りは Job の Pod の 5432 だけ。
 */
const AST_DB_SERVICES = ['audit', 'configuration', 'cost-control', 'market-monitor', 'order-execution', 'report', 'risk-management'];
function astDbUsersProblems(infraDocs, esoDocs, job, policies) {
  const out = [];
  const sorted = (a) => [...a].sort().join(',');
  const want = sorted(AST_DB_SERVICES);
  const cm = infraDocs.find((d) => d.kind === 'ConfigMap' && d.metadata.name === 'postgres-ast-db-users');
  const script = cm && cm.data && String(cm.data['ast-db-users.sh'] || '');
  if (!script) return ['db-users: ConfigMap postgres-ast-db-users の手順が無い'];
  const m = script.match(/^SERVICES="([^"]+)"$/m);
  const pairs = m ? m[1].split(/\s+/).map((e) => e.split(':')) : [];
  if (sorted(pairs.map((p) => p[0])) !== want) out.push(`db-users: 手順の SERVICES が 7 つのサービスでない（${pairs.map((p) => p[0]).join(', ')}）`);
  for (const [svc, db] of pairs) if (db !== `${svc.replace(/-/g, '_')}_svc`) out.push(`db-users: ${svc} の DB が ${db}`);
  const init = infraDocs.find((d) => d.kind === 'ConfigMap' && d.metadata.name === 'postgres-init');
  const dbs = [...String((init && init.data && init.data['01-create-databases.sh']) || '').matchAll(/CREATE DATABASE (\w+_svc) /g)].map((x) => x[1]);
  if (sorted(dbs) !== sorted(pairs.map((p) => p[1]))) out.push(`db-users: 初期化の DB（${dbs.join(', ')}）と手順の DB が違う`);
  const pgIn = infraDocs.find((d) => d.kind === 'NetworkPolicy' && d.metadata.name === 'postgres-ingress-clients');
  const callers = pgIn ? pgIn.spec.ingress[0].from.flatMap((f) => (((f.podSelector || {}).matchExpressions || [])[0] || {}).values || []) : [];
  if (sorted(callers.map((c) => c.replace(/-service$/, ''))) !== want) out.push(`db-users: Postgres に入る取引ユニットのサービス（${callers.join(', ')}）と 7 つが違う`);
  const es = esoDocs.filter((d) => d.kind === 'ExternalSecret' && /^ast-db-/.test(d.metadata.name));
  if (sorted(es.map((d) => d.metadata.name.replace(/^ast-db-/, ''))) !== want) out.push(`db-users: ESO の ast-db-<サービス> が 7 つでない（${es.map((d) => d.metadata.name).join(', ')}）`);
  for (const d of es) {
    const svc = d.metadata.name.replace(/^ast-db-/, '');
    const keys = (d.spec.data || []).map((x) => `${x.secretKey}=${x.remoteRef.key}#${x.remoteRef.property}`).sort().join(',');
    if (keys !== `password=shared/ast-db-${svc}#password,username=shared/ast-db-${svc}#username`) out.push(`db-users: ${d.metadata.name} の写し方が違う（${keys}）`);
    if (d.spec.target.deletionPolicy !== 'Retain' || d.spec.target.name !== d.metadata.name) out.push(`db-users: ${d.metadata.name} の target が Retain・同じ名前でない`);
  }
  for (const [name, text] of Object.entries(policies)) {
    const named = [...text.matchAll(/^path\s+"cloud\/data\/shared\/ast-db-([^"]+)"\s+\{\s*capabilities\s*=\s*\["read"\]\s*\}/gm)].map((x) => x[1]);
    if (sorted(named) !== want) out.push(`db-users: policy ${name} の名指し（${named.join(', ')}）が 7 つでない`);
    if (/^path\s+"cloud\/data\/shared\/[^"]*\*/m.test(text)) out.push(`db-users: policy ${name} の cloud/shared にワイルドカードがある`);
  }
  if (!job || job.kind !== 'Job') return [...out, 'db-users: Job の宣言が無い'];
  const ps = job.spec.template.spec;
  const c = ps.containers[0];
  const pg = infraDocs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'postgres');
  if (!pg || c.image !== pg.spec.template.spec.containers[0].image) out.push('db-users: Job のイメージが Postgres と同じ digest でない');
  const env = Object.fromEntries((c.env || []).map((e) => [e.name, e]));
  if ((env.PGSSLMODE || {}).value !== 'verify-full') out.push('db-users: Job の TLS が verify-full でない');
  if ((env.PGHOST || {}).value !== 'postgres') out.push('db-users: Job の PGHOST が Service postgres でない（証明書の名前の照合）');
  for (const e of c.env || []) {
    if (/^(PGUSER|PGPASSWORD|AST_DB_)/.test(e.name) && !(e.valueFrom && e.valueFrom.secretKeyRef)) out.push(`db-users: Job の ${e.name} が Secret から来ない`);
  }
  for (const svc of AST_DB_SERVICES) {
    const k = svc.replace(/-/g, '_').toUpperCase();
    for (const [kind, key] of [['USER', 'username'], ['PASSWORD', 'password']]) {
      const ref = ((env[`AST_DB_${kind}_${k}`] || {}).valueFrom || {}).secretKeyRef || {};
      if (ref.name !== `ast-db-${svc}` || ref.key !== key || ref.optional) out.push(`db-users: Job の AST_DB_${kind}_${k} が Secret ast-db-${svc} の ${key}（非 optional）でない`);
    }
  }
  if (JSON.stringify(c.args) !== '["apply"]') out.push('db-users: Job の段（args）の既定が apply でない（ast-db-users.sh が差し替える行）');
  if (String(ps.automountServiceAccountToken) !== 'false') out.push('db-users: Job が ServiceAccount のトークンを載せる');
  const label = (job.spec.template.metadata.labels || {}).app;
  const np = infraDocs.find((d) => d.kind === 'NetworkPolicy' && d.metadata.name === 'postgres-ingress-ast-db-users');
  const from = np && np.spec.ingress.length === 1 && np.spec.ingress[0].from;
  if (!(np && (np.spec.podSelector.matchLabels || {}).app === 'postgres' && from && from.length === 1 && !from[0].namespaceSelector
    && (from[0].podSelector.matchLabels || {}).app === label && (np.spec.ingress[0].ports || []).map((x) => `${x.protocol}/${x.port}`).join() === 'TCP/5432')) {
    out.push('db-users: Postgres への入りが Job の Pod（同じ名前空間）の 5432 だけでない');
  }
  return out;
}

/**
 * NFR-18, IADR-0697: 基盤の Pod の権限を最小にする（クラウドの描画のすべての Pod）。
 *   - どのコンテナ（initContainer を含む）も root でない（runAsNonRoot と、0 でない数の runAsUser。Pod かコンテナのどちらか。コンテナの側が勝つ）。
 *   - allowPrivilegeEscalation=false・capabilities.drop に ALL・足すものは HARDENING_ALLOWED_CAPS の中だけ（今は空）・privileged でない。
 *   - seccomp は RuntimeDefault（Pod かコンテナ）。ServiceAccount のトークンを載せない（基盤の Pod はどれも API サーバーを呼ばない）。
 *   - hostPID・hostIPC・hostNetwork を持たない。
 */
const WORKLOAD_KINDS = ['Deployment', 'StatefulSet', 'DaemonSet', 'Job', 'CronJob', 'ReplicaSet', 'Pod'];
/** 足してよい capability（理由つき）。今は無い（どのイメージも 1024 より上で受け、所有の付け替えも要らない＝実測。IADR-0697）。 */
const HARDENING_ALLOWED_CAPS = Object.freeze({});
const podSpecOf = (d) => (d.kind === 'Pod' ? d.spec : podSpec(d));
function podHardeningProblems(docs, where) {
  const out = [];
  for (const d of docs.filter((x) => WORKLOAD_KINDS.includes(x.kind))) {
    const id = `${where}: ${d.kind}/${d.metadata && d.metadata.name}`;
    const ps = podSpecOf(d);
    if (!ps) { out.push(`${id}: Pod の宣言が読めない`); continue; }
    const psc = ps.securityContext || {};
    if (String(ps.automountServiceAccountToken) !== 'false') out.push(`${id}: ServiceAccount のトークンを載せる（automountServiceAccountToken: false が無い）`);
    for (const k of ['hostPID', 'hostIPC', 'hostNetwork']) if (String(ps[k]) === 'true') out.push(`${id}: ${k}`);
    for (const c of [...(ps.initContainers || []), ...(ps.containers || [])]) {
      const sc = c.securityContext || {};
      const pick = (k) => (sc[k] !== undefined ? sc[k] : psc[k]);
      const cid = `${id} の ${c.name}`;
      if (String(pick('runAsNonRoot')) !== 'true') out.push(`${cid}: runAsNonRoot が true でない`);
      const uid = pick('runAsUser');
      if (uid === undefined || !/^[0-9]+$/.test(String(uid)) || String(uid) === '0') out.push(`${cid}: runAsUser が 0 でない数で書かれていない（${uid}。名前の利用者は runAsNonRoot で検められない）`);
      if (String(sc.allowPrivilegeEscalation) !== 'false') out.push(`${cid}: allowPrivilegeEscalation が false でない`);
      if (String(sc.privileged) === 'true') out.push(`${cid}: privileged`);
      const caps = sc.capabilities || {};
      if (!(caps.drop || []).map(String).includes('ALL')) out.push(`${cid}: capabilities.drop に ALL が無い`);
      for (const a of (caps.add || []).map(String)) if (!Object.prototype.hasOwnProperty.call(HARDENING_ALLOWED_CAPS, a)) out.push(`${cid}: capability ${a} を足している（理由つきの許可に無い）`);
      const seccomp = (sc.seccompProfile && sc.seccompProfile.type) || (psc.seccompProfile && psc.seccompProfile.type);
      if (seccomp !== 'RuntimeDefault') out.push(`${cid}: seccompProfile が RuntimeDefault でない（${seccomp || '未指定'}）`);
    }
  }
  return out;
}

/**
 * NFR-18, IADR-0697: NET_RAW を持つコンテナ（実行時の既定の capability に NET_RAW が含まれる）。
 * drop に ALL か NET_RAW があり、add に NET_RAW・ALL が無いときだけ持たない。privileged は持つ。
 */
function netRawHolders(docs, where) {
  const out = [];
  for (const d of docs.filter((x) => WORKLOAD_KINDS.includes(x.kind))) {
    const ps = podSpecOf(d) || {};
    for (const c of [...(ps.initContainers || []), ...(ps.containers || [])]) {
      const sc = c.securityContext || {};
      const caps = sc.capabilities || {};
      const drop = (caps.drop || []).map(String);
      const add = (caps.add || []).map(String);
      const dropped = drop.includes('ALL') || drop.includes('NET_RAW');
      if (String(sc.privileged) === 'true' || !dropped || add.includes('NET_RAW') || add.includes('ALL')) out.push(`${where}: ${d.kind}/${d.metadata.name} の ${c.name} が NET_RAW を持つ`);
    }
  }
  return out;
}

/** NFR-18, IADR-0697: 基盤と MSP の名前空間の Pod Security Admission は enforce・warn・audit とも restricted。 */
const PSA_RESTRICTED_NAMESPACES = ['platform-infra', 'microservices-platform'];
function namespacePsaProblems(docs) {
  const out = [];
  for (const name of PSA_RESTRICTED_NAMESPACES) {
    const ns = docs.find((d) => d.kind === 'Namespace' && d.metadata.name === name);
    if (!ns) { out.push(`Namespace ${name} が無い`); continue; }
    const labels = ns.metadata.labels || {};
    for (const mode of ['enforce', 'warn', 'audit']) {
      const v = labels[`pod-security.kubernetes.io/${mode}`];
      if (v !== 'restricted') out.push(`Namespace ${name} の Pod Security の ${mode} が restricted でない（${v}）`);
    }
  }
  return out;
}

/**
 * NFR-18, NFR-21, IADR-0697: collector（otel-collector）の OTLP の入りの送り元。取引ユニットの chart の values-cloud の描画で
 * OTLP の宛先の env（Otlp__Endpoint）を持つ Deployment（OpenD は持たない）と、MSP の LLM ゲートウェイ。宛先はどれも gRPC（4317）。
 */
const OTLP_AST_SENDERS = [
  'audit-service', 'backtest-service', 'configuration-service', 'cost-control-service', 'information-collection-service', 'market-monitor-service',
  'notification-service', 'order-execution-service', 'report-service', 'risk-management-service', 'trade-decision-service',
];
const OTLP_MSP_SENDERS = ['llmgateway-service'];
const OTLP_GRPC_PORT = 4317;
function collectorIngressProblems(docs) {
  const out = [];
  const isCollector = (d) => d.kind === 'NetworkPolicy' && d.metadata.namespace === 'platform-infra'
    && JSON.stringify(d.spec.podSelector || {}) === JSON.stringify({ matchLabels: { app: 'otel-collector' } }) && (d.spec.ingress || []).length;
  const allows = docs.filter(isCollector);
  const seen = {};
  const otlpPorts = new Set();
  for (const d of allows) {
    for (const rule of d.spec.ingress) {
      const ports = (rule.ports || []).map((x) => Number(x.port));
      if (!ports.length) out.push(`infra: ${d.metadata.name} がポートを絞っていない`);
      if (!(rule.from || []).length) out.push(`infra: ${d.metadata.name} が送り元を絞っていない`);
      // Prometheus（同じ名前空間）の自己テレメトリの収集（8888）だけは別の許可。ほかの口は OTLP の送り手の規則として数える。
      const selfScrape = JSON.stringify(ports) === '[8888]' && (rule.from || []).every((f) => !f.namespaceSelector && JSON.stringify(f.podSelector) === JSON.stringify({ matchLabels: { app: 'prometheus' } }));
      if (selfScrape) continue;
      for (const p of ports) otlpPorts.add(p);
      for (const f of rule.from || []) {
        const ns = f.namespaceSelector && f.namespaceSelector.matchLabels && f.namespaceSelector.matchLabels['kubernetes.io/metadata.name'];
        const pods = f.podSelector && f.podSelector.matchLabels && f.podSelector.matchLabels.app
          ? [String(f.podSelector.matchLabels.app)]
          : ((f.podSelector && f.podSelector.matchExpressions) || []).filter((e) => e.key === 'app' && e.operator === 'In').flatMap((e) => e.values.map(String));
        const others = f.podSelector && Object.keys(f.podSelector.matchLabels || {}).filter((k) => k !== 'app').length;
        if (!ns || !f.podSelector || !pods.length || others || f.ipBlock) {
          out.push(`infra: ${d.metadata.name} に「名前空間 かつ Pod」でない送り元がある（${JSON.stringify(f)}）`);
          continue;
        }
        seen[ns] = [...new Set([...(seen[ns] || []), ...pods])].sort();
      }
    }
  }
  const expected = { [AST_NS]: [...OTLP_AST_SENDERS].sort(), [MSP_NS]: [...OTLP_MSP_SENDERS].sort() };
  const norm = (o) => JSON.stringify(Object.keys(o).sort().map((k) => [k, o[k]]));
  if (norm(seen) !== norm(expected)) out.push(`infra: collector の OTLP の送り元が一覧（取引ユニットの OTLP を送る Pod と LLM ゲートウェイ）と一致しない（${JSON.stringify(seen)}）`);
  if (JSON.stringify([...otlpPorts].sort((a, b) => a - b)) !== JSON.stringify([OTLP_GRPC_PORT])) out.push(`infra: collector の OTLP の入りのポートが ${OTLP_GRPC_PORT} だけでない（${[...otlpPorts].join(', ')}）`);
  // 出の側: LLM ゲートウェイから collector へも 4317 だけ（送り元の一覧と同じ口）。
  const egress = docs.find((d) => d.kind === 'NetworkPolicy' && d.metadata.name === 'llmgateway-egress');
  const toCol = egress ? (egress.spec.egress || []).filter((r) => (r.to || []).some((t) => t.podSelector && t.podSelector.matchLabels && t.podSelector.matchLabels.app === 'otel-collector')) : [];
  const eports = toCol.flatMap((r) => (r.ports || []).map((x) => Number(x.port)));
  if (JSON.stringify(eports) !== JSON.stringify([OTLP_GRPC_PORT])) out.push(`infra: LLM ゲートウェイから collector への出が ${OTLP_GRPC_PORT} だけでない（${eports.join(', ')}）`);
  return out;
}

/**
 * NFR-21, IADR-0697: collector の受け取りの上限と、受け取りを拒んだ・系列が途絶えたときの警報。
 *   - receiver の gRPC に 1 回の大きさ・同時の流れの上限、memory_limiter（上限と spike）が pipeline の先頭。
 *   - 警報 OtelCollectorRefusingData・OtelCollectorExportFailing・OtlpMetricsPipelineSilent・OtlpSendersMissing・PrometheusHeadSeriesHigh が在り、
 *     OtlpSendersMissing の閾値は送り元の一覧の数と同じ。
 */
function collectorLimitProblems(docs) {
  const out = [];
  const cm = docs.find((d) => d.kind === 'ConfigMap' && d.metadata.name === 'otel-collector-config');
  const cfg = cm ? String(cm.data['config.yaml']) : '';
  if (!/grpc:\n\s+endpoint: 0\.0\.0\.0:4317\n\s+max_recv_msg_size_mib: \d+\n\s+max_concurrent_streams: \d+/.test(cfg)) out.push('collector: gRPC の受け取りの上限（max_recv_msg_size_mib・max_concurrent_streams）が無い');
  if (!/memory_limiter:\n\s+check_interval: \S+\n\s+limit_mib: \d+\n\s+spike_limit_mib: \d+/.test(cfg)) out.push('collector: memory_limiter の上限（limit_mib・spike_limit_mib）が無い');
  for (const sig of ['traces', 'metrics', 'logs']) {
    if (!new RegExp(`${sig}:\\n\\s+receivers: \\[otlp\\]\\n\\s+processors: \\[memory_limiter,`).test(cfg)) out.push(`collector: ${sig} の pipeline の先頭が memory_limiter でない`);
  }
  const prom = docs.find((d) => d.kind === 'ConfigMap' && d.metadata.name === 'prometheus-cloud-config');
  const rules = prom ? String(prom.data['platform-rules.yml']) : '';
  for (const a of ['OtelCollectorRefusingData', 'OtelCollectorExportFailing', 'OtlpMetricsPipelineSilent', 'OtlpSendersMissing', 'PrometheusHeadSeriesHigh']) {
    if (!new RegExp(`- alert: ${a}\\n`).test(rules)) out.push(`警報 ${a} が無い`);
  }
  for (const m of ['otelcol_receiver_refused_metric_points', 'otelcol_receiver_refused_spans', 'otelcol_receiver_refused_log_records']) if (!rules.includes(m)) out.push(`警報が ${m} を見ない`);
  const senders = OTLP_AST_SENDERS.length + OTLP_MSP_SENDERS.length;
  const th = /- alert: OtlpSendersMissing\n\s+expr: .*target_info.*< (\d+)\n/.exec(rules);
  if (!th || Number(th[1]) !== senders) out.push(`警報 OtlpSendersMissing の閾値が送り元の数（${senders}）でない（${th ? th[1] : '読めない'}）`);
  // 注釈（summary・description）に書いた数も送り元の数と同じ（閾値だけ直して文言が古いまま残らない）。
  const block = (/- alert: OtlpSendersMissing\n([\s\S]*?)(?=\n\s+- alert: |\n\s+- name: |$)/.exec(rules) || [])[1] || '';
  const said = [...block.matchAll(/(\d+) 個(?:のはず|に足りない)/g)].map((x) => Number(x[1]));
  if (said.length < 2) out.push(`警報 OtlpSendersMissing の注釈に送り元の数が読めない（${said.length} 箇所）`);
  for (const n of said) if (n !== senders) out.push(`警報 OtlpSendersMissing の注釈の数（${n}）が送り元の数（${senders}）でない`);
  return out;
}

module.exports = { openbaoTlsProblems, OPENBAO_SERVER, OPENBAO_CA_PROVIDER, podHardeningProblems, netRawHolders, namespacePsaProblems, collectorIngressProblems, collectorLimitProblems, OTLP_AST_SENDERS, OTLP_MSP_SENDERS, HARDENING_ALLOWED_CAPS, astDbUsersProblems, AST_DB_SERVICES, keycloakTlsProblems, authTlsProblems, keycloakPkiProblems, KEYCLOAK_AST_CALLERS, k3sInstallProblems, pipeToShellProblems, monthlyBudgetProblems, DAILY_COST_ALLOCATION, cloudCostLimitProblems, CLOUD_LLM_COST_ENV, stage2Problems, llmGatewayEnv, STAGE2_LLM_ENV, vaultProblems, cloudRealmAudienceProblems, commonProblems, keycloakExposureProblems, EDGE_KINDS, infraProblems, chartProblems, gatewayListenerProblems, mspIngressProblems, postgresHbaProblems, rabbitmqPersistenceProblems, docsOf, FORBIDDEN, DEV_LITERALS };

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
  ok('IADR-0689 決定 12: クラウドの描画は段 2（ブローカの narrow・LLM ゲートウェイの Fail / Enforce / Enforce）。既定とローカルの描画は段 1 のまま', () => {
    assert.deepStrictEqual(stage2Problems(renderChart(), renderInfra()), []);
    // Deployment の描画だけを読む（ほかの資源には、この試験の YAML の読み手が扱わない書き方がある）。
    const deployments = (extra) => docsOf(run('helm', ['template', 'msp', CHART, '-n', 'microservices-platform', ...extra, '-s', 'templates/deployment.yaml', '--set-string', `global.image.tag=${IMAGE_TAG}`]));
    const base = llmGatewayEnv(deployments([]));
    const local = llmGatewayEnv(deployments(['-f', 'deploy/local/values-local.yaml']));
    assert.ok(Object.keys(base).length > 0 && Object.keys(local).length > 0, '陽性対照: 既定・ローカルの LLM ゲートウェイの env を読めていない');
    for (const k of Object.keys(STAGE2_LLM_ENV)) {
      assert.strictEqual(base[k], undefined, `既定の描画に ${k} がある（段 1 を変えた）`);
      assert.strictEqual(local[k], undefined, `ローカルの描画に ${k} がある（段 1 を変えた）`);
    }
    // 生成物の表は、ローカルの permissions.txt の取引ユニットの行と同じ
    const gen = require('./gen-rabbitmq-permissions.js');
    const want = gen.renderCloud([...gen.readPermissions().values()], gen.readUsers());
    assert.strictEqual(fs.readFileSync(path.join(REPO_ROOT, gen.CLOUD_PERMS_REL), 'utf8'), want, 'rabbitmq-permissions.yaml が生成物と違う（gen-rabbitmq-permissions.js --write）');
  });
  ok('IADR-0692: クラウドの描画は LLM の当日の費用の上限（合計 1 日 20 USD・取引ユニットが主）を持ち、1 レプリカ。既定とローカルの描画は上限を持たない', () => {
    assert.deepStrictEqual(cloudCostLimitProblems(renderChart()), []);
    const deployments = (extra) => docsOf(run('helm', ['template', 'msp', CHART, '-n', 'microservices-platform', ...extra, '-s', 'templates/deployment.yaml', '--set-string', `global.image.tag=${IMAGE_TAG}`]));
    const base = llmGatewayEnv(deployments([]));
    const local = llmGatewayEnv(deployments(['-f', 'deploy/local/values-local.yaml']));
    assert.ok(Object.keys(base).length > 0 && Object.keys(local).length > 0, '陽性対照: 既定・ローカルの LLM ゲートウェイの env を読めていない');
    for (const env of [base, local]) {
      const costKeys = Object.keys(env).filter((k) => /^Llm__CallLimits__/i.test(k));
      assert.deepStrictEqual(costKeys, [], `既定・ローカルの描画に LLM の上限の env がある（変えない）: ${costKeys.join(', ')}`);
    }
  });
  ok('IADR-0692: 月次予算（用途別）は当日の費用の上限の割り当てから導いた値で、割り当ての合計は呼び手ごとの日の上限に等しい', () => {
    const app = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'src/platform/backend/Services/LlmGateway/appsettings.json'), 'utf8'));
    const env = llmGatewayEnv(renderChart());
    assert.deepStrictEqual(monthlyBudgetProblems(app, env), []);
    // 陰性対照: 金額のずれ・余分な用途・日の上限の変更を検出する。
    const bad = JSON.parse(JSON.stringify(app));
    bad.Llm.Budget.MonthlyLimits['trade-decision'] = 200;
    bad.Llm.Budget.MonthlyLimits.default = 1;
    const p = monthlyBudgetProblems(bad, { ...env, 'Llm__CallLimits__Callers__ai-stock-trading-llm-caller__DailyCostLimit': '17' });
    for (const want of [/trade-decision が 135 でない/, /割り当てに無い用途/, /ai-stock-trading-llm-caller の割り当ての合計/]) assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
  });
  ok('IADR-0692: k3s は版を固定し、取得物を sha256 で照合してから入れる。クラウドの手順書に取得物をシェルへ流す形が無い', () => {
    const readme = fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/README.md'), 'utf8');
    const lib = fs.readFileSync(path.join(REPO_ROOT, 'scripts/lib/pinned-artifacts.sh'), 'utf8');
    assert.deepStrictEqual(k3sInstallProblems(readme, lib), []);
    // 走査の母集合: deploy/cloud の全ファイルと、docs/operations のクラウドの手順書。
    const walk = (rel) => fs.readdirSync(path.join(REPO_ROOT, rel), { withFileTypes: true })
      .flatMap((e) => (e.isDirectory() ? walk(`${rel}/${e.name}`) : [`${rel}/${e.name}`]));
    const files = [...walk('deploy/cloud'), ...fs.readdirSync(path.join(REPO_ROOT, 'docs/operations')).filter((f) => /^cloud-/.test(f)).map((f) => `docs/operations/${f}`)];
    assert.ok(files.length > 10 && files.some((f) => f.startsWith('docs/operations/cloud-')), `陽性対照: 走査の母集合が小さい（${files.length}）`);
    assert.deepStrictEqual(pipeToShellProblems(files.map((rel) => ({ rel, text: fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8') }))), []);
  });
  ok('IADR-0692 陰性対照: curl | sh・照合の欠落と順序・取得の許可・旗の変更・版のずれを検出する', () => {
    const readme = fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/README.md'), 'utf8');
    const lib = fs.readFileSync(path.join(REPO_ROOT, 'scripts/lib/pinned-artifacts.sh'), 'utf8');
    const cases = [
      [readme.replace('INSTALL_K3S_SKIP_DOWNLOAD=true INSTALL_K3S_EXEC="server', 'curl -sfL https://get.k3s.io | INSTALL_K3S_EXEC="server'), /シェルへ流す/],
      [readme.replace('cd /root && sha256sum -c k3s.sha256', 'cd /root'), /照合（sha256sum -c）してから/],
      [readme.replace('INSTALL_K3S_SKIP_DOWNLOAD=true ', ''), /取得をさせている/],
      [readme.replace('--secrets-encryption \\\n', ''), /起動の旗が既存の値と違う/],
      [readme.replace('K3S_VERSION=v1.35.10+k3s1', 'K3S_VERSION=v1.35.11+k3s1'), /表の版/],
    ];
    for (const [text, want] of cases) {
      assert.notStrictEqual(text, readme, `変異の置換元が無い（試験が古い）: ${want}`);
      const p = k3sInstallProblems(text, lib);
      assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
    }
    assert.ok(k3sInstallProblems(readme, lib.replace(/^\s*k3s-install@.*$/m, '')).some((x) => /表に k3s-install が無い/.test(x)));
    for (const bad of ['curl -sfL https://x | sh -', 'curl -sfL https://get.k3s.io | INSTALL_K3S_EXEC="server \\\n  --disable traefik" sh -','wget -qO- https://x | sudo bash', 'curl -fsSL https://x | FOO=1 sh -s -']) {
      assert.strictEqual(pipeToShellProblems([{ rel: 'x', text: bad }]).length, 1, bad);
    }
    assert.strictEqual(pipeToShellProblems([{ rel: 'x', text: 'curl -s https://x | jq .' }]).length, 0);
  });
  ok('IADR-0692: 取得物をシェルへ流す形の検出は、代入を長く並べた病的な行でもすぐ返る（指数的な戻りが無い）', () => {
    for (const sep of [' ', '\t']) {
      const line = `curl x | ${'A=""'.concat(sep).repeat(5000)}`;
      for (const tail of ['', 'x', 'sh -']) {
        const t0 = process.hrtime.bigint();
        const n = pipeToShellProblems([{ rel: 'x', text: line + tail }]).length;
        const ms = Number(process.hrtime.bigint() - t0) / 1e6;
        assert.ok(ms < 1000, `病的な入力で ${ms.toFixed(0)} ms かかった（sep=${JSON.stringify(sep)} tail=${tail}）`);
        assert.strictEqual(n, tail === 'sh -' ? 1 : 0, `病的な入力の判定が違う（tail=${tail}）`);
      }
    }
  });
  ok('IADR-0692 陰性対照: 上限の欠落・既定の欠落・合計のずれ・ナレッジの呼び手の項目・2 レプリカ・HPA を検出する', () => {
    const drop = (name) => {
      const docs = JSON.parse(JSON.stringify(renderChart()));
      const c = docs.find((x) => x.kind === 'Deployment' && /llmgateway/.test(x.metadata.name)).spec.template.spec.containers[0];
      return { docs, c, drop: () => { c.env = c.env.filter((e) => e.name !== name); } };
    };
    const a = drop('Llm__CallLimits__Default__DailyCostLimit'); a.drop();
    assert.ok(cloudCostLimitProblems(a.docs).some((p) => /既定が正の値でない/.test(p)), '既定の欠落を検出しない');
    const b = drop('Llm__CallLimits__Callers__ai-stock-trading-llm-caller__DailyCostLimit'); b.drop();
    assert.ok(cloudCostLimitProblems(b.docs).some((p) => /ai-stock-trading-llm-caller__DailyCostLimit/.test(p)), '取引ユニットの上限の欠落を検出しない');
    const c = drop('x'); c.c.env.find((e) => e.name === 'Llm__CallLimits__Callers__ai-stock-trading-llm-caller__DailyCostLimit').value = '30';
    assert.ok(cloudCostLimitProblems(c.docs).some((p) => /合計が 20 USD でない/.test(p)), '合計のずれを検出しない');
    const d = drop('x'); d.c.env.push({ name: 'Llm__CallLimits__Callers__graph-service__DailyCostLimit', value: '5' });
    assert.ok(cloudCostLimitProblems(d.docs).some((p) => /取引ユニットの呼び手だけでない/.test(p)), 'ナレッジの呼び手の項目を検出しない');
    const e = drop('x'); e.docs.find((x) => x.kind === 'Deployment' && /llmgateway/.test(x.metadata.name)).spec.replicas = 2;
    e.docs.push({ kind: 'HorizontalPodAutoscaler', metadata: { name: 'llmgateway' }, spec: {} });
    const pe = cloudCostLimitProblems(e.docs);
    for (const want of [/1 レプリカでない/, /HPA/]) assert.ok(pe.some((p) => want.test(p)), `${want} を検出しない:\n${pe.join('\n')}`);
    // values-cloud から上限を外した描画（段 2 だけ）も落ちる（描画の経路で空振りしない）。
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'deploy-cloud-cost-'));
    let stage2Only;
    try {
      const f = path.join(tmp, 'stage2-only.yaml');
      fs.writeFileSync(f, 'services:\n  llmgateway:\n    extraEnvAppend:\n'
        + Object.entries(STAGE2_LLM_ENV).map(([k, v]) => `      - { name: ${k}, value: "${v}" }\n`).join(''));
      stage2Only = renderChart(['-f', f]);
    } finally {
      fs.rmSync(tmp, { recursive: true, force: true });
    }
    assert.ok(cloudCostLimitProblems(stage2Only).length > 0, '上限の無い段 2 の描画を検出しない');
  });
  ok('IADR-0689 決定 12 陰性対照: 段 1 の権限・表の欠落・optional・LLM の段 1 を検出する', () => {
    const infra = JSON.parse(JSON.stringify(renderInfra()));
    const boot = infra.find((d) => d.kind === 'ConfigMap' && d.metadata.name === 'rabbitmq-boot');
    boot.data['restore-users.sh'] = "rabbitmqctl -q set_permissions -p / u '.*' '.*' '.*'";
    infra.find((d) => d.kind === 'Deployment' && d.metadata.name === 'rabbitmq').spec.template.spec.volumes
      .find((v) => v.configMap && v.configMap.name === 'rabbitmq-permissions').configMap.optional = true;
    const chart = renderChart(['--set', 'services.llmgateway.extraEnvAppend=null']);
    const p = stage2Problems(chart, infra);
    for (const want of [/vhost 全体の権限/, /表を読まない/, /optional/, /RouteValidation__Mode/, /CallerPolicy__Mode/, /CallLimits__Mode/]) assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
  });
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
    for (const want of [/dev モード/, /ESO の controller の外の送り元/, /入りのポートが \[8443\] でない/, /ClusterSecretStore/, /Secret を描いている/]) assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
  });
  const renderOpenbaoCoexist = () => docsOf(run('kubectl', ['kustomize', 'deploy/cloud-transition/openbao-tls-coexist']));
  ok('IADR-0696 OT1・OT2: OpenBao の受け口は TLS の 8443 だけ（平文の listener・8200 が無い）で、ESO の全てのストアは https で CA（ConfigMap openbao-ca）を検証する', () => {
    assert.deepStrictEqual(openbaoTlsProblems(renderVault(), renderEso()), []);
    const e = renderEso();
    assert.strictEqual(e.filter((d) => d.kind === 'SecretStore' || d.kind === 'VaultDynamicSecret').length, 6, '陽性対照: ストアの数（SecretStore 2・VaultDynamicSecret 4）');
  });
  ok('IADR-0696 OT1・OT2 陰性対照: 平文の listener・平文の宛先・CA の欠落・検証を外す指定・HTTP の probe・8200 のポートを検出する', () => {
    const v = JSON.parse(JSON.stringify(renderVault()));
    const e = JSON.parse(JSON.stringify(renderEso()));
    const cfg = v.find((d) => d.kind === 'ConfigMap' && d.metadata.name === 'openbao-config');
    cfg.data['cloud.hcl'] = String(cfg.data['cloud.hcl']).replace(/tls_min_version = "tls13"/, 'tls_min_version = "tls13"\n      tls_disable = true');
    const dep = v.find((d) => d.kind === 'Deployment' && d.metadata.name === 'openbao');
    const c = dep.spec.template.spec.containers[0];
    c.readinessProbe.httpGet.scheme = 'HTTP';
    c.env.find((x) => x.name === 'BAO_ADDR').value = 'http://127.0.0.1:8200';
    c.ports.push({ containerPort: 8200 });
    v.find((d) => d.kind === 'Service' && d.metadata.name === 'openbao').spec.ports.push({ port: 8200 });
    const store = e.find((d) => d.kind === 'SecretStore');
    store.spec.provider.vault.server = 'http://openbao.openbao.svc:8200';
    delete e.find((d) => d.kind === 'VaultDynamicSecret').spec.provider.caProvider;
    e.filter((d) => d.kind === 'VaultDynamicSecret')[1].spec.provider.insecureSkipVerify = true;
    const p = openbaoTlsProblems(v, e);
    for (const want of [/TLS の listener が 8443 の 1 つでない/, /readiness が HTTPS/, /Pod の中の bao の宛先が TLS/, /受け口が \[8443\] でない/, /Service のポートが \[8443\] でない/, /宛先が https:\/\/openbao\.openbao\.svc:8443 でない/, /CA を ConfigMap openbao-ca で検証しない/, /検証を外す指定/]) {
      assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
    }
    // 平文の listener を足しただけ（TLS の listener は残す）でも落ちる
    const v2 = JSON.parse(JSON.stringify(renderVault()));
    const cfg2 = v2.find((d) => d.kind === 'ConfigMap' && d.metadata.name === 'openbao-config');
    cfg2.data['cloud.hcl'] += '\nlistener "tcp" {\n  address = "0.0.0.0:8200"\n  tls_disable = true\n}\n';
    assert.ok(openbaoTlsProblems(v2, renderEso()).some((x) => /平文の口が閉じていない/.test(x)), '平文の listener の追加を検出しない');
  });
  ok('IADR-0696 OT3: 併存の重ねは 8443 に加えて平文の 8200 も受け（送り元は ESO の controller だけ）、最終の段の判定では落ちる', () => {
    const co = renderOpenbaoCoexist();
    assert.deepStrictEqual(vaultProblems(co, renderEso(), localVaultImage(), { coexist: true }), []);
    const p = vaultProblems(co, renderEso(), localVaultImage());
    for (const want of [/平文の口が閉じていない/, /受け口が \[8443\] でない/, /Service のポートが \[8443\] でない/, /入りのポートが \[8443\] でない/, /ConfigMap openbao-config-coexist が残っている/, /併存の段の設定ファイルを読む/]) assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
    // 併存の判定に最終の段を当てると、平文の口の欠落で落ちる（まだ http のストアが届かない形）
    assert.ok(vaultProblems(renderVault(), renderEso(), localVaultImage(), { coexist: true }).some((x) => /平文の listener が \[8200\] でない/.test(x)), '併存の段の平文の欠落を検出しない');
    // 重ねは OpenBao の名前空間の外を描かない
    assert.deepStrictEqual([...new Set(co.map((d) => d.metadata.namespace).filter(Boolean))].sort(), ['openbao']);
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
    assert.ok(cloudRealmAudienceProblems(more).some((x) => /持ち手が owner とサービスごとのクライアントだけでない/.test(x)));
    const norole = buildCloudRealm(realm, secrets);
    norole.roles.realm = norole.roles.realm.filter((r) => r.name !== 'trading-bot');
    assert.ok(cloudRealmAudienceProblems(norole).some((x) => /trading-bot が無い/.test(x)));
  });
  ok('IADR-0700: クラウドの realm にサービスごとの機密クライアント 7 つ（サービスアカウントだけ・宛先は取引ユニットの API だけ・trading-service だけ）が足され、secret は互いに違う', () => {
    const { buildCloudRealm, CLOUD_CLIENTS, SERVICE_CLIENTS, DECLARED_CLOUD_CLIENTS } = require('../deploy/cloud/realm-for-cloud.js');
    const realm = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'deploy/keycloak/microservices-platform-realm.json'), 'utf8'));
    assert.deepStrictEqual([...SERVICE_CLIENTS], ['backtest', 'cost-control', 'information-collection', 'market-monitor', 'order-execution', 'report', 'trade-decision'].map((x) => `ai-stock-trading-svc-${x}`));
    assert.deepStrictEqual([...CLOUD_CLIENTS], [...DECLARED_CLOUD_CLIENTS, ...SERVICE_CLIENTS]);
    for (const id of SERVICE_CLIENTS) assert.ok(!realm.clients.some((c) => c.clientId === id), `宣言（ローカル）に ${id} が在る（クラウドの realm にだけ足す）`);
    const secrets = Object.fromEntries(CLOUD_CLIENTS.map((c) => [c, ['probe', c, 'z'.repeat(32)].join('-')]));
    const problems = (out) => {
      const p = [];
      for (const id of SERVICE_CLIENTS) {
        const c = out.clients.find((x) => x.clientId === id);
        if (!c) { p.push(`${id} が無い`); continue; }
        const flags = { publicClient: false, standardFlowEnabled: false, directAccessGrantsEnabled: false, serviceAccountsEnabled: true, implicitFlowEnabled: false };
        for (const [k, v] of Object.entries(flags)) if (c[k] !== v) p.push(`${id} の ${k} が ${v} でない`);
        if ((c.redirectUris || []).length || (c.webOrigins || []).length) p.push(`${id} にリダイレクト・オリジンがある`);
        if (JSON.stringify(c.defaultClientScopes) !== JSON.stringify(['roles', 'ai-stock-trading-api-audience'])) p.push(`${id} の既定のスコープが roles と取引ユニットの宛先だけでない: ${c.defaultClientScopes}`);
        if ((c.optionalClientScopes || []).length) p.push(`${id} に任意のスコープがある`);
        if ((c.protocolMappers || []).length) p.push(`${id} に個別の mapper がある`);
        const u = out.users.filter((x) => x.serviceAccountClientId === id);
        if (u.length !== 1 || u[0].username !== `service-account-${id}` || JSON.stringify(u[0].realmRoles) !== '["trading-service"]' || u[0].clientRoles || u[0].groups) {
          p.push(`${id} のサービスアカウントが trading-service だけでない: ${JSON.stringify(u)}`);
        }
      }
      const vals = out.clients.filter((c) => CLOUD_CLIENTS.includes(c.clientId)).map((c) => c.secret);
      if (new Set(vals).size !== CLOUD_CLIENTS.length) p.push('secret が互いに違わない');
      const svc = out.clients.find((c) => c.clientId === 'ai-stock-trading-svc');
      if (!svc || (svc.defaultClientScopes || []).includes('ai-stock-trading-api-audience')) p.push('旧の共有クライアントが無いか、取引ユニットの宛先を持つ');
      return p;
    };
    const out = buildCloudRealm(realm, secrets);
    assert.deepStrictEqual(problems(out), []);
    assert.deepStrictEqual(problems(buildCloudRealm(realm, {}, { template: true })).filter((x) => !/secret が互いに違わない/.test(x)), [], '雛形でも同じ形');
    // 陰性対照: 標準フロー・共通の宛先・ロールの追加・1 つ抜く
    const mut = (f) => { const o = buildCloudRealm(realm, secrets); f(o); return problems(o); };
    assert.ok(mut((o) => { o.clients.find((c) => c.clientId === SERVICE_CLIENTS[0]).standardFlowEnabled = true; }).some((x) => /standardFlowEnabled/.test(x)));
    assert.ok(mut((o) => { o.clients.find((c) => c.clientId === SERVICE_CLIENTS[1]).defaultClientScopes.push('platform-api-audience'); }).some((x) => /既定のスコープ/.test(x)));
    assert.ok(mut((o) => { o.users.find((u) => u.serviceAccountClientId === SERVICE_CLIENTS[2]).realmRoles.push('trading-owner'); }).some((x) => /trading-service だけでない/.test(x)));
    assert.ok(mut((o) => { o.clients = o.clients.filter((c) => c.clientId !== SERVICE_CLIENTS[3]); }).some((x) => /が無い/.test(x)));
    // 宣言の側の前提が崩れたら組まない（宣言にサービスごとのクライアントを置く・旧の共有クライアントを公開にする・secret を渡さない）
    const bad = JSON.parse(JSON.stringify(realm));
    bad.clients.push({ ...bad.clients.find((c) => c.clientId === 'ai-stock-trading-svc'), clientId: SERVICE_CLIENTS[0] });
    assert.throws(() => buildCloudRealm(bad, secrets), /クラウドの realm にだけ足す/);
    const pub = JSON.parse(JSON.stringify(realm));
    pub.clients.find((c) => c.clientId === 'ai-stock-trading-svc').standardFlowEnabled = true;
    assert.throws(() => buildCloudRealm(pub, secrets), /standardFlowEnabled/);
    assert.throws(() => buildCloudRealm(realm, { ...secrets, [SERVICE_CLIENTS[4]]: undefined }), /secret が渡されていない/);
  });
  ok('IADR-0700: 稼働中の realm へ無い機密クライアントだけを足す Job（宣言・入口・手順）の形と、手順の試験', () => {
    const job = docsOf(fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/keycloak-clients/job.yaml'), 'utf8'))[0];
    const pod = podSpec(job);
    const c = pod.containers[0];
    const env = Object.fromEntries(c.env.map((e) => [e.name, e]));
    assert.strictEqual(job.metadata.namespace, 'platform-infra');
    assert.strictEqual(Number(job.spec.backoffLimit), 0);
    assert.strictEqual(String(pod.automountServiceAccountToken), 'false');
    assert.deepStrictEqual([env.MODE.value, env.KC_URL.value, env.REALM.value, env.NODE_EXTRA_CA_CERTS.value], ['check', 'https://keycloak:8443', 'platform', '/etc/platform-infra-ca/ca.crt']);
    for (const k of ['KC_ADMIN_USER', 'KC_ADMIN_PASSWORD']) assert.strictEqual(env[k].valueFrom.secretKeyRef.name, 'keycloak-admin', `${k} が Secret から読まない`);
    assert.ok(c.env.every((e) => !CREDENTIAL_ENV.test(e.name) || e.valueFrom), '資格の env が字面');
    assert.deepStrictEqual(pod.volumes.find((v) => v.name === 'realm').secret.secretName, 'keycloak-realm-import');
    const sc = c.securityContext;
    assert.ok(String(sc.runAsNonRoot) === 'true' && String(sc.allowPrivilegeEscalation) === 'false' && String(sc.readOnlyRootFilesystem) === 'true' && JSON.stringify(sc.capabilities.drop) === '["ALL"]' && sc.seccompProfile.type === 'RuntimeDefault', '制限の securityContext でない');
    const localJob = docsOf(fs.readFileSync(path.join(REPO_ROOT, 'deploy/local/keycloak-setup/realm-reconcile-job.yaml'), 'utf8'))[0];
    assert.strictEqual(c.image, podSpec(localJob).containers[0].image, 'ローカルの realm の後追いの Job と同じ digest でない');
    assert.match(c.image, /@sha256:[0-9a-f]{64}$/);
    assert.strictEqual(labelApp(job.spec.template), 'keycloak-add-clients');
    // 入口は段の行を 1 つだけ差し替える（apply・check のほかは受けない）
    const sh = fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/keycloak-clients/keycloak-clients.sh'), 'utf8');
    assert.match(sh, /apply \| check\) ;;/);
    assert.match(sh, /live_opt_in_require "keycloak-clients\.sh"/);
    assert.ok(!/--from-literal|echo "\$KC_ADMIN/.test(sh), '入口が値を扱う');
    const r = spawnSync('bash', [path.join(REPO_ROOT, 'deploy/cloud/keycloak-clients/keycloak-clients.sh'), 'apply'], { encoding: 'utf8' });
    assert.notStrictEqual(r.status, 0, '--live なしで動いた');
    const t = spawnSync(process.execPath, [path.join(REPO_ROOT, 'deploy/cloud/keycloak-clients/add-clients.test.js')], { encoding: 'utf8' });
    assert.strictEqual(t.status, 0, `add-clients.test.js が落ちた:\n${t.stdout}${t.stderr}`);
  });
  ok('陰性対照: 基盤の描画へ start-dev・LoadBalancer・字面のパスワードを足すと判定に落ちる', () => {
    const docs = JSON.parse(JSON.stringify(renderInfra()));
    docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'keycloak').spec.template.spec.containers[0].args = ['start-dev'];
    docs.find((d) => d.kind === 'Service' && d.metadata.name === 'keycloak').spec.type = 'LoadBalancer';
    docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'postgres').spec.template.spec.containers[0].env.push({ name: 'POSTGRES_PASSWORD', value: 'x' });
    const p = infraProblems(docs, null);
    for (const want of [/start-dev/, /LoadBalancer/, /POSTGRES_PASSWORD が字面/]) assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
  });
  const renderCoexist = () => docsOf(run('kubectl', ['kustomize', 'deploy/cloud-transition/keycloak-tls-coexist']));
  const pkiTexts = () => ({
    lib: fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/vault/lib.sh'), 'utf8'),
    configure: fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/vault/configure.sh'), 'utf8'),
    platformInfraEso: fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/vault/policies/platform-infra-eso.hcl'), 'utf8'),
    mspEso: fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/vault/policies/msp-eso.hcl'), 'utf8'),
    astEso: fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/vault/policies/ast-eso.hcl'), 'utf8'),
  });
  ok('IADR-0693 KT1・KT2: Keycloak は HTTPS（8443）だけで受け、入りは取引ユニットの Keycloak を呼ぶ Pod と LLM ゲートウェイの 8443 だけ', () => {
    assert.deepStrictEqual(keycloakTlsProblems(renderInfra()), []);
    // deploy/cloud（全体）でも同じ（infra だけを当てる形に依らない）
    assert.deepStrictEqual(keycloakTlsProblems(docsOf(run('kubectl', ['kustomize', 'deploy/cloud']))), []);
  });
  ok('IADR-0693 KT3: 併存の重ねは 8080 と 8443 を受けて hostname を固定せず、それ以外は最終の段と同じ（基盤の判定も通る）', () => {
    const co = renderCoexist();
    assert.deepStrictEqual(keycloakTlsProblems(co, { coexist: true }), []);
    // 陰性対照: 最終の段の判定に当てると、平文の口と hostname の緩みで落ちる（既定の描画に併存の形が無いことの裏）
    const p = keycloakTlsProblems(co);
    for (const want of [/KC_HOSTNAME が https/, /平文の口が閉じていない/, /hostname の固定を緩めている/, /受け口が 8443 だけでない/, /Service のポートが 8443 だけでない/, /keycloak-ingress-clients のポートが 8443 だけでない/, /Keycloak への出が 8443 だけでない/]) {
      assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
    }
    // 陰性対照: 併存の判定に最終の段を当てると、出の 8080 の欠落（平文の呼び手が鍵を取れない形）で落ちる。
    assert.ok(keycloakTlsProblems(renderInfra(), { coexist: true }).some((x) => /Keycloak への出が 8080・8443 だけでない/.test(x)), '併存の段の出の 8080 の欠落を検出しない');
    // LLM ゲートウェイの出のうち Keycloak への規則のほか（名前解決・collector・提供元の API）は、最終の段と同じ（重ねが変えない）。
    const otherEgress = (ds) => ds.find((d) => d.kind === 'NetworkPolicy' && d.metadata.name === 'llmgateway-egress').spec.egress
      .filter((r) => !(r.to || []).some((t) => t.podSelector && t.podSelector.matchLabels && t.podSelector.matchLabels.app === 'keycloak'));
    assert.deepStrictEqual(otherEgress(co), otherEgress(renderInfra()), '併存の重ねが LLM ゲートウェイの Keycloak 以外への出を変えた');
    // 重ねは deploy/cloud（観測を含む）に重なるので、観測の Prometheus（infra だけの判定では持ち込まない名前）だけを除いて当てる。
    assert.deepStrictEqual(infraProblems(co, null).filter((x) => !/持ち込まない資源（prometheus）/.test(x)), []);
  });
  ok('IADR-0693 KT1・KT2 陰性対照: 平文の口・http の hostname・証明書の欠落・HTTP の probe・名前空間だけの送り元・8080 の出を検出する', () => {
    const docs = JSON.parse(JSON.stringify(renderInfra()));
    const kc = docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'keycloak');
    const c = kc.spec.template.spec.containers[0];
    c.env.find((e) => e.name === 'KC_HTTP_ENABLED').value = 'true';
    c.env.find((e) => e.name === 'KC_HOSTNAME').value = 'http://keycloak:8080';
    c.ports.push({ containerPort: 8080 });
    c.readinessProbe.httpGet.scheme = 'HTTP';
    kc.spec.template.spec.volumes = kc.spec.template.spec.volumes.filter((v) => !(v.secret && v.secret.secretName === 'keycloak-tls'));
    const np = docs.find((d) => d.kind === 'NetworkPolicy' && d.metadata.name === 'keycloak-ingress-clients');
    np.spec.ingress[0].from.push({ namespaceSelector: { matchLabels: { 'kubernetes.io/metadata.name': AST_NS } } });
    np.spec.ingress[0].from[0].podSelector.matchExpressions[0].values.push('opend');
    docs.find((d) => d.kind === 'NetworkPolicy' && d.metadata.name === 'llmgateway-egress').spec.egress[1].ports = [{ protocol: 'TCP', port: 8080 }];
    const p = keycloakTlsProblems(docs);
    for (const want of [/平文の口が閉じていない/, /KC_HOSTNAME が https/, /受け口が 8443 だけでない/, /readinessProbe が HTTPS でない/, /Secret keycloak-tls から来ない/, /「名前空間 かつ Pod」でない送り元/, /Keycloak を呼ぶ Pod と LLM ゲートウェイだけでない/, /Keycloak への出が 8443 だけでない/]) {
      assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
    }
  });
  ok('IADR-0693 KT4: クラウドの chart の LLM ゲートウェイは https の Authority・RequireHttpsMetadata・基盤の CA を持つ。既定・ローカルの描画は変えない', () => {
    assert.deepStrictEqual(authTlsProblems(renderChart()), []);
    const infraHost = renderInfra().find((d) => d.kind === 'Deployment' && d.metadata.name === 'keycloak').spec.template.spec.containers[0].env.find((e) => e.name === 'KC_HOSTNAME').value;
    assert.strictEqual(`${infraHost}/realms/platform`, llmGatewayEnv(renderChart()).Auth__Authority, '受け手の Authority と Keycloak の発行元がずれる');
    const deployments = (extra) => docsOf(run('helm', ['template', 'msp', CHART, '-n', 'microservices-platform', ...extra, '-s', 'templates/deployment.yaml', '--set-string', `global.image.tag=${IMAGE_TAG}`]));
    for (const [name, docs] of [['既定', deployments([])], ['ローカル', deployments(['-f', 'deploy/local/values-local.yaml'])]]) {
      const ds = docs.filter((d) => d.kind === 'Deployment');
      assert.ok(ds.length > 0, `陽性対照: ${name}の描画に Deployment が無い`);
      for (const d of ds) {
        const env = Object.fromEntries((d.spec.template.spec.containers[0].env || []).map((e) => [e.name, e.value]));
        assert.strictEqual(env.Auth__RequireHttpsMetadata, undefined, `${name}の ${d.metadata.name} に Auth__RequireHttpsMetadata がある（変えない）`);
        if ('Auth__Authority' in env) assert.match(String(env.Auth__Authority), /^http:\/\/keycloak:8080\//, `${name}の ${d.metadata.name} の Authority が変わった`);
      }
    }
  });
  ok('IADR-0693 KT4・KT5 陰性対照: RequireHttpsMetadata・CA・https の欠落と平文の発行元の併記を検出し、https でない Authority は描画で止まる', () => {
    const plain = authTlsProblems(renderChart(['--set', 'global.auth.tls.enabled=false', '--set-string', 'global.auth.authority=http://keycloak:8080/realms/platform']));
    for (const want of [/Auth__Authority が/, /RequireHttpsMetadata が true でない/, /SSL_CERT_DIR/, /platform-infra-ca/]) {
      assert.ok(plain.some((p) => want.test(p)), `${want} を検出しない:\n${plain.join('\n')}`);
    }
    const both = authTlsProblems(renderChart(['--set-string', 'global.auth.validIssuers=http://keycloak:8080/realms/platform']));
    assert.ok(both.some((p) => /平文の発行元/.test(p)), '移す間の併記が既定に残ったことを検出しない');
    for (const bad of [['global.auth.authority', 'http://keycloak:8080/realms/platform', /authority は https/], ['global.auth.metadataAddress', 'http://keycloak:8080/realms/platform/.well-known/openid-configuration', /metadataAddress は https/]]) {
      const r = spawnSync('helm', ['template', 'msp', CHART, '-n', 'microservices-platform', '-f', 'deploy/cloud/values-cloud.yaml',
        '--set-string', `global.image.tag=${IMAGE_TAG}`, '--set-string', `${bad[0]}=${bad[1]}`], { cwd: REPO_ROOT, encoding: 'utf8' });
      assert.notStrictEqual(r.status, 0, `${bad[0]} が http なのに描画が止まらない`);
      assert.match(r.stderr, bad[2]);
    }
  });
  ok('IADR-0693 変異（KT4）: chart の RequireHttpsMetadata・CA のマウント・SSL_CERT_DIR を外すと判定が赤になる', () => {
    const src = fs.readFileSync(path.join(REPO_ROOT, CHART, 'templates', 'deployment.yaml'), 'utf8');
    const cases = [
      ['            - name: Auth__RequireHttpsMetadata\n              value: "true"', '            - name: Auth__RequireHttpsMetadata\n              value: "false"', /RequireHttpsMetadata が true でない/],
      ['{{- $infraCaMount := or $dbTls $mqTls $authTls }}', '{{- $infraCaMount := or $dbTls $mqTls }}', /platform-infra-ca/],
      ['          {{- if not $mqTls }}\n            - name: SSL_CERT_DIR', '          {{- if false }}\n            - name: SSL_CERT_DIR', /SSL_CERT_DIR/],
    ];
    for (const [from, to, want] of cases) {
      assert.ok(src.includes(from), `変異の置換元が無い（試験が古い）: ${from}`);
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'deploy-cloud-kt-'));
      try {
        fs.cpSync(path.join(REPO_ROOT, CHART), dir, { recursive: true });
        fs.writeFileSync(path.join(dir, 'templates', 'deployment.yaml'), src.replace(from, to));
        const p = authTlsProblems(renderChart([], dir));
        assert.ok(p.some((x) => want.test(x)), `${want} の変異を捕まえない:\n${p.join('\n')}`);
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    }
  });
  ok('IADR-0695 AC7: 取引ユニットの DB のサービスごとの利用者 — 7 つのサービスが手順・初期化の DB・Postgres への入り・ESO・Job の env・2 つの policy で揃い、Job は TLS（verify-full）と Secret だけで動く', () => {
    const job = docsOf(fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/db-users/job.yaml'), 'utf8')).find((d) => d.kind === 'Job');
    const pol = (n) => fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/vault/policies', `${n}.hcl`), 'utf8');
    const policies = { 'ast-eso': pol('ast-eso'), 'platform-infra-eso': pol('platform-infra-eso') };
    const infra = renderInfra();
    const eso = renderEso();
    assert.deepStrictEqual(astDbUsersProblems(infra, eso, job, policies), []);
    // 陰性対照: サービスの足し忘れ・写し方のずれ・ワイルドカード・TLS の緩め・値の字面・入りの広げ・ほかの Secret
    const clone = (x) => JSON.parse(JSON.stringify(x));
    const i2 = clone(infra);
    const n2 = i2.find((d) => d.kind === 'NetworkPolicy' && d.metadata.name === 'postgres-ingress-ast-db-users');
    n2.spec.ingress[0].from[0] = { namespaceSelector: {} };
    const e2 = clone(eso).filter((d) => d.metadata.name !== 'ast-db-report');
    e2.find((d) => d.metadata.name === 'ast-db-audit').spec.data[0].remoteRef.key = 'shared/ast-db';
    const j2 = clone(job);
    const c2 = j2.spec.template.spec.containers[0];
    c2.env.find((e) => e.name === 'PGSSLMODE').value = 'require';
    c2.env.find((e) => e.name === 'AST_DB_PASSWORD_REPORT').valueFrom.secretKeyRef.name = 'ast-db';
    c2.env.push({ name: 'AST_DB_PASSWORD_X', value: 'x' });
    const p = astDbUsersProblems(i2, e2, j2, { 'ast-eso': policies['ast-eso'].replace(/^path "cloud\/data\/shared\/ast-db-report".*$/m, ''), 'platform-infra-eso': `${policies['platform-infra-eso']}\npath "cloud/data/shared/ast-db-*" { capabilities = ["read"] }\n` });
    for (const want of [/ESO の ast-db-<サービス> が 7 つでない/, /ast-db-audit の写し方/, /policy ast-eso の名指し/, /policy platform-infra-eso の cloud\/shared にワイルドカード/, /verify-full でない/, /AST_DB_PASSWORD_REPORT が Secret ast-db-report/, /AST_DB_PASSWORD_X が Secret から来ない/, /Postgres への入りが Job の Pod/]) {
      assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
    }
    const i3 = clone(infra);
    const cm = i3.find((d) => d.kind === 'ConfigMap' && d.metadata.name === 'postgres-ast-db-users');
    cm.data['ast-db-users.sh'] = cm.data['ast-db-users.sh'].replace(' report:report_svc', '');
    assert.ok(astDbUsersProblems(i3, eso, job, policies).some((x) => /手順の SERVICES が 7 つのサービスでない/.test(x)), '手順の SERVICES の欠けを検出しない');
    // 基盤の描画の共通の判定（Secret を描かない・資格情報の env の字面・持ち込まない資源）は Job にも効く
    assert.deepStrictEqual(commonProblems([job], 'db-users'), []);
  });
  ok('IADR-0693 KT6: keycloak の証明書は基盤の側だけが PKI の role keycloak で発行し、取引ユニットは CA の証明書だけを読む', () => {
    assert.deepStrictEqual(keycloakPkiProblems(renderEso(), pkiTexts()), []);
    const t = pkiTexts();
    const e = JSON.parse(JSON.stringify(renderEso()));
    e.find((d) => d.kind === 'VaultDynamicSecret' && d.metadata.name === 'keycloak-tls').spec.path = 'pki_cloud/issue/postgres';
    const p = keycloakPkiProblems(e, {
      ...t,
      lib: t.lib.replace('keycloak:platform-infra ', ''),
      platformInfraEso: t.platformInfraEso.replace('path "pki_cloud/issue/keycloak"', '# x'),
      mspEso: `${t.mspEso}\npath "pki_cloud/issue/keycloak" { capabilities = ["update"] }\n`,
      astEso: `${t.astEso}\npath "pki_cloud/issue/keycloak" { capabilities = ["update"] }\n`,
    });
    for (const want of [/発行の口が pki_cloud\/issue\/keycloak でない/, /OB_PKI_ROLES に keycloak/, /基盤の側の発行の範囲/, /MSP の側が keycloak/, /取引ユニットの側が PKI/]) {
      assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
    }
  });
  // NFR-18, NFR-21, IADR-0697: 基盤の Pod の権限の最小化と、collector の受け口の絞り。
  const renderCloudAll = () => docsOf(run('kubectl', ['kustomize', 'deploy/cloud']));
  const dbUsersJob = () => docsOf(fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/db-users/job.yaml'), 'utf8'));
  const allPodRenders = () => [
    ['deploy/cloud', renderCloudAll()],
    ['deploy/cloud/db-users', dbUsersJob()],
    ['chart', renderChart()],
    ['keycloak-tls-coexist', docsOf(run('kubectl', ['kustomize', 'deploy/cloud-transition/keycloak-tls-coexist']))],
  ];
  ok('IADR-0697 H1: クラウドの描画の全 Pod（initContainer を含む）が root でなく、capability を全部落とし（足す物なし）、権限の昇格を許さず、seccomp は RuntimeDefault、SA のトークンを載せない', () => {
    let n = 0;
    for (const [where, docs] of allPodRenders()) {
      n += docs.filter((d) => WORKLOAD_KINDS.includes(d.kind)).length;
      assert.deepStrictEqual(podHardeningProblems(docs, where), []);
    }
    // 陽性対照: 基盤の 7 つ（Postgres・RabbitMQ・Keycloak・collector・Prometheus・Alertmanager・バックアップ）＋ db-users の Job ＋ LLM ゲートウェイ ＋ 併存の重ねの 7 つ
    assert.ok(n >= 16, `陽性対照: 数えた Pod の宣言が少ない（${n}）`);
  });
  ok('IADR-0697 H2: NET_RAW を持つ Pod が無い（クラウドの描画の全コンテナ）', () => {
    for (const [where, docs] of allPodRenders()) assert.deepStrictEqual(netRawHolders(docs, where), []);
  });
  ok('IADR-0697 H3: 基盤と MSP の名前空間の Pod Security Admission は enforce・warn・audit とも restricted', () => {
    assert.deepStrictEqual(namespacePsaProblems(renderInfra()), []);
  });
  ok('IADR-0697 H4: collector の OTLP の入りは、取引ユニットの OTLP を送る 11 個の Pod と LLM ゲートウェイだけ（名前空間 かつ Pod）・gRPC の 4317 だけ。OpenD は入らない', () => {
    const docs = renderCloudAll();
    assert.deepStrictEqual(collectorIngressProblems(docs), []);
    assert.ok(!OTLP_AST_SENDERS.includes('opend'), 'OpenD を送り元に入れている');
  });
  ok('IADR-0697 H5: collector の受け取りの上限（gRPC・memory_limiter）と、受け取りの拒否・系列の途絶え・系列の数の警報が在る（送り手の数の閾値は一覧と同じ）', () => {
    assert.deepStrictEqual(collectorLimitProblems(renderCloudAll()), []);
  });
  ok('IADR-0697 陰性対照: securityContext を外す・NET_RAW を足す・root で走らせる・SA のトークンを載せる・PSA を緩める・collector の入りを名前空間だけへ広げる・OpenD を足す・4318 を開ける・警報を外すと、判定が赤になる', () => {
    const clone = (x) => JSON.parse(JSON.stringify(x));
    const docs = clone(renderCloudAll());
    const dep = (n) => docs.find((d) => d.kind === 'Deployment' && d.metadata.name === n);
    delete dep('postgres').spec.template.spec.containers[0].securityContext;
    dep('rabbitmq').spec.template.spec.initContainers[0].securityContext.capabilities.add = ['NET_RAW'];
    dep('keycloak').spec.template.spec.securityContext.runAsUser = 0;
    dep('otel-collector').spec.template.spec.automountServiceAccountToken = true;
    delete dep('prometheus').spec.template.spec.securityContext.seccompProfile;
    const cj = docs.find((d) => d.kind === 'CronJob');
    cj.spec.jobTemplate.spec.template.spec.containers[0].securityContext.allowPrivilegeEscalation = true;
    const p = podHardeningProblems(docs, 'x');
    for (const want of [/postgres の postgres: allowPrivilegeEscalation/, /postgres の postgres: capabilities\.drop に ALL が無い/, /rabbitmq の tls-files: capability NET_RAW/, /keycloak の keycloak: runAsUser/, /otel-collector: ServiceAccount のトークン/, /prometheus の prometheus: seccompProfile/, /platform-backup-postgres の backup: allowPrivilegeEscalation/]) {
      assert.ok(p.some((x) => want.test(x)), `${want} を検出しない:\n${p.join('\n')}`);
    }
    const nr = netRawHolders(docs, 'x');
    for (const want of [/postgres の postgres が NET_RAW/, /rabbitmq の tls-files が NET_RAW/]) assert.ok(nr.some((x) => want.test(x)), `${want} を検出しない:\n${nr.join('\n')}`);
    const ns = clone(renderInfra());
    ns.find((d) => d.kind === 'Namespace' && d.metadata.name === 'platform-infra').metadata.labels['pod-security.kubernetes.io/enforce'] = 'baseline';
    assert.ok(namespacePsaProblems(ns).some((x) => /platform-infra の Pod Security の enforce が restricted でない/.test(x)), 'PSA の緩めを検出しない');
    const np = docs.find((d) => d.kind === 'NetworkPolicy' && d.metadata.name === 'otel-collector-ingress-clients');
    np.spec.ingress[0].from.push({ namespaceSelector: { matchLabels: { 'kubernetes.io/metadata.name': AST_NS } } });
    np.spec.ingress[0].from[0].podSelector.matchExpressions[0].values.push('opend');
    np.spec.ingress[0].ports.push({ protocol: 'TCP', port: 4318 });
    const c = collectorIngressProblems(docs);
    for (const want of [/「名前空間 かつ Pod」でない送り元/, /一覧.*と一致しない/, /ポートが 4317 だけでない/]) assert.ok(c.some((x) => want.test(x)), `${want} を検出しない:\n${c.join('\n')}`);
    const prom = docs.find((d) => d.kind === 'ConfigMap' && d.metadata.name === 'prometheus-cloud-config');
    prom.data['platform-rules.yml'] = String(prom.data['platform-rules.yml']).replace('- alert: OtelCollectorRefusingData', '- alert: X').replace(/(target_info\[10m\]\)\)\) or vector\(0\)\) < )12/, '$111').replace('（12 個のはず）', '（13 個のはず）');
    const col = docs.find((d) => d.kind === 'ConfigMap' && d.metadata.name === 'otel-collector-config');
    col.data['config.yaml'] = String(col.data['config.yaml']).replace(/\n\s+max_recv_msg_size_mib: \d+/, '');
    const l = collectorLimitProblems(docs);
    for (const want of [/OtelCollectorRefusingData が無い/, /OtlpSendersMissing の閾値/, /OtlpSendersMissing の注釈の数（13）/, /gRPC の受け取りの上限/]) assert.ok(l.some((x) => want.test(x)), `${want} を検出しない:\n${l.join('\n')}`);
  });
  ok('IADR-0697 変異: 宣言のファイルから securityContext・capability の全落とし・collector の Pod の名指しを外すと、描画の判定が赤になる', () => {
    const cases = [
      ['postgres.yaml', '          securityContext:\n            allowPrivilegeEscalation: false\n            capabilities: { drop: [ALL] }\n          env:', '          env:', (d) => podHardeningProblems(d, 'm'), /postgres の postgres: allowPrivilegeEscalation/],
      ['rabbitmq.yaml', '            readOnlyRootFilesystem: true\n            capabilities: { drop: [ALL] }\n          volumeMounts:', '            readOnlyRootFilesystem: true\n          volumeMounts:', (d) => netRawHolders(d, 'm'), /rabbitmq の tls-files が NET_RAW/],
      ['rabbitmq.yaml', '      automountServiceAccountToken: false\n', '', (d) => podHardeningProblems(d, 'm'), /rabbitmq: ServiceAccount のトークン/],
      ['namespaces.yaml', 'pod-security.kubernetes.io/enforce: restricted', 'pod-security.kubernetes.io/enforce: baseline', namespacePsaProblems, /enforce が restricted でない/],
      ['networkpolicy.yaml', '        - namespaceSelector: { matchLabels: { kubernetes.io/metadata.name: microservices-platform } }\n          podSelector: { matchLabels: { app: llmgateway-service } }\n      ports:\n        - { protocol: TCP, port: 4317 }\n---', '        - namespaceSelector: { matchLabels: { kubernetes.io/metadata.name: microservices-platform } }\n      ports:\n        - { protocol: TCP, port: 4317 }\n---', (d) => collectorIngressProblems([...d, ...renderCloudAll().filter((x) => x.kind === 'NetworkPolicy' && /otel-collector-ingress-prometheus/.test(x.metadata.name))]), /「名前空間 かつ Pod」でない送り元/],
    ];
    for (const [file, from, to, judge, want] of cases) {
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'deploy-cloud-harden-'));
      try {
        fs.cpSync(path.join(REPO_ROOT, 'deploy/cloud/infra'), dir, { recursive: true });
        const f = path.join(dir, file);
        const src = fs.readFileSync(f, 'utf8');
        assert.ok(src.includes(from), `変異の置換元が無い（試験が古い）: ${file}: ${from}`);
        fs.writeFileSync(f, src.replace(from, to));
        const p = judge(docsOf(run('kubectl', ['kustomize', dir])));
        assert.ok(p.some((x) => want.test(x)), `${file} の変異（${want}）を捕まえない:\n${p.join('\n')}`);
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    }
  });
  if (failed) {
    console.error(`✗ ${failed} 件が失敗した`);
    process.exit(1);
  }
  console.log('✓ deploy/cloud の描画は受け入れ基準を満たす');
}
