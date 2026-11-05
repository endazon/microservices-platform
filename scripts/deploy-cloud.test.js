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
 *   AC6 MSP の描画は LLM ゲートウェイの Deployment だけで、取引ユニットからの入りの NetworkPolicy を持つ。
 *
 * kubectl / helm が無ければ失敗にする（fail-closed。CI の static-checks は両方を持つ。check-deploy-manifests と同じ）。
 * 判定の関数は export し、scripts/scripts.repo.test.js が宣言そのもの（kustomize は resources だけで変換を持たない）にも当てる。
 * 外部依存ゼロ（Node 標準モジュール ＋ kubectl ＋ helm）。実行: node scripts/deploy-cloud.test.js
 */
const assert = require('assert');
const fs = require('fs');
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

/** AC1・AC2・AC3 の共通の判定。問題の一覧（空なら合格）を返す。 */
function commonProblems(docs, where) {
  const out = [];
  const text = JSON.stringify(docs);
  for (const d of docs) {
    const id = `${where}: ${d.kind}/${d.metadata && d.metadata.namespace ? `${d.metadata.namespace}/` : ''}${d.metadata && d.metadata.name}`;
    const names = [d.metadata && d.metadata.name, labelApp(d)].filter(Boolean).map(String);
    if (d.kind !== 'NetworkPolicy') {
      for (const f of FORBIDDEN) if (names.some((n) => n.includes(f))) out.push(`${id}: 持ち込まない資源（${f}）`);
    }
    if (d.kind === 'Secret') out.push(`${id}: Secret を描いている（資格情報は bootstrap-secrets.sh が乱数で作る）`);
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
  return out;
}

/** 基盤の描画の判定（AC1〜AC5）。 */
function infraProblems(docs, localDocs) {
  const out = commonProblems(docs, 'infra');
  const kc = docs.find((d) => d.kind === 'Deployment' && d.metadata.name === 'keycloak');
  if (!kc) out.push('infra: Keycloak の Deployment が無い');
  else {
    const args = (kc.spec.template.spec.containers[0].args || []).map(String);
    if (args.includes('start-dev')) out.push('infra: Keycloak の引数に start-dev がある');
    if (!args.includes('start')) out.push('infra: Keycloak の引数に start が無い');
  }
  for (const want of ['postgres', 'rabbitmq', 'keycloak', 'otel-collector']) {
    if (!docs.some((d) => d.kind === 'Deployment' && d.metadata.name === want)) out.push(`infra: ${want} の Deployment が無い`);
  }
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

/** MSP の chart の描画の判定（AC1〜AC3・AC6）。 */
function chartProblems(docs) {
  const out = commonProblems(docs, 'chart');
  const deps = docs.filter((d) => ['Deployment', 'StatefulSet', 'DaemonSet', 'Job', 'CronJob'].includes(d.kind));
  const names = deps.map((d) => d.metadata.name).sort();
  if (JSON.stringify(names) !== JSON.stringify(['llmgateway-service'])) out.push(`chart: ワークロードが LLM ゲートウェイだけでない（${names.join(', ')}）`);
  const np = docs.filter((d) => d.kind === 'NetworkPolicy');
  if (!np.some((d) => JSON.stringify(d.spec || {}).includes('ai-stock-trading') && JSON.stringify(d.spec.podSelector || {}).includes('llmgateway-service'))) {
    out.push('chart: 取引ユニットから LLM ゲートウェイへの入りの NetworkPolicy が無い');
  }
  if (docs.some((d) => d.kind === 'Namespace')) out.push('chart: Namespace を描いている（deploy/cloud/infra/namespaces.yaml が作る）');
  return out;
}

function renderInfra() {
  return docsOf(run('kubectl', ['kustomize', 'deploy/cloud/infra']));
}
function renderLocalInfra() {
  return docsOf(run('kubectl', ['kustomize', 'deploy/local/infra']));
}
function renderChart(extra = []) {
  return docsOf(run('helm', ['template', 'msp', CHART, '-n', 'microservices-platform', '-f', 'deploy/cloud/values-cloud.yaml',
    '--set-string', `global.image.tag=${IMAGE_TAG}`, ...extra]));
}

module.exports = { commonProblems, infraProblems, chartProblems, docsOf, FORBIDDEN, DEV_LITERALS };

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
  ok('AC1〜AC5: 基盤の描画に持ち込まない資源・外への口・既定の資格情報・start-dev が無く、外部のイメージは deploy/local/infra と同じ digest', () => {
    assert.deepStrictEqual(infraProblems(renderInfra(), renderLocalInfra()), []);
  });
  ok('AC1〜AC3・AC6: MSP の描画は LLM ゲートウェイだけで、取引ユニットからの入りを絞り、外への口・既定の資格情報が無い', () => {
    assert.deepStrictEqual(chartProblems(renderChart()), []);
  });
  ok('陰性対照: 既定の values（クラウドの値なし）の描画は判定に落ちる（判定が空回りしていない）', () => {
    const docs = docsOf(run('helm', ['template', 'msp', CHART, '-n', 'microservices-platform', '--set-string', `global.image.tag=${IMAGE_TAG}`]));
    assert.ok(chartProblems(docs).some((p) => /持ち込まない資源/.test(p)), '既定の描画で持ち込まない資源を検出しない');
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
