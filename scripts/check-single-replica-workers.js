#!/usr/bin/env node
'use strict';
/*
 * check-single-replica-workers.js — NFR / FR-02 / IADR-0595 / IADR-0640（#2271 の部分）
 *
 * **1 レプリカの前提でしか正しくないサービスが、配備の値で 2 レプリカ以上にならないこと**を見る暫定の検査。
 *
 * 対象は ingestion-service だけである。取り込みの版の印（Qdrant）は比較交換を持たない。文書ごとの順序は、
 * プロセス内の排他（`DocumentIndexLocks`。IADR-0640 決定 3）だけで保っている。2 レプリカ以上・並行受信になると、
 * 削除の墓石の上書き（IADR-0640 の Q6）と、古い版による新しい版の印の上書き（IADR-0595 の残余）の窓が開く。
 * 本体の解消（比較交換・分散の排他）は #2271（`blocked:env`）で扱う。本検査は、それまで窓が開かないことを機械で見る。
 *
 * 見るもの（chart の値が入る経路を全部見る。作業仕様書 20261031_2271 の母集合）:
 *   1. values ファイル（本番像 values.yaml・経路 B の values-local.yaml・CI の ci/*.yaml）:
 *      `services.<name>.replicas` が 1 を超えない。`scaling.services`（HPA の対象）に <name> が無い。
 *      HPA の対象になると、Deployment は replicas を描かず HPA が所有する（templates/deployment.yaml）ので、replicas だけでは足りない。
 *   2. Argo CD の Application（deploy/argocd/application.yaml）の `helm.parameters`: 同じ 2 つを上書きしていない。
 *   3. スクリプト・ワークフロー（scripts/*.sh・.github/workflows/*.yml）の `--set`: 同じ 2 つを上書きしていない。
 *
 * 🔴 0 件走査で緑にしない: 本番像の values.yaml に `services.<name>` が無ければ失敗にする（名前の変更で黙って空振りしない）。
 * 外部依存ゼロ（YAML は lib/yaml-subset.js で読む）。
 *
 * 使い方:
 *   node scripts/check-single-replica-workers.js
 *   node scripts/check-single-replica-workers.js --self-test
 */
const fs = require('fs');
const path = require('path');
const { parseDocuments } = require('./lib/yaml-subset');

const REPO_ROOT = path.resolve(__dirname, '..');

/** 1 レプリカの前提でしか正しくないサービス（chart の `services.<key>`）と、その理由。足すときは理由と解消先を書く。 */
const SINGLE_REPLICA_SERVICES = {
  ingestion: '版の印に比較交換が無い（IADR-0640 の Q6・IADR-0595 の残余）。解消は #2271',
};

const CHART_VALUES = 'deploy/helm/microservices-platform/values.yaml';
const CI_VALUES_DIR = 'deploy/helm/microservices-platform/ci';
const LOCAL_VALUES = 'deploy/local/values-local.yaml';
const ARGO_APP = 'deploy/argocd/application.yaml';

/** values の 1 文書（オブジェクト）から違反を返す。`source` は出力に名指す出どころ。 */
function valuesProblems(values, source, names = Object.keys(SINGLE_REPLICA_SERVICES)) {
  const out = [];
  const services = (values && values.services) || {};
  for (const name of names) {
    const svc = services[name];
    if (svc && svc.replicas !== undefined && svc.replicas !== null) {
      const n = Number(svc.replicas);
      if (!Number.isInteger(n) || n > 1) {
        out.push(`${source}: services.${name}.replicas が ${svc.replicas}（1 を超えてはならない。${SINGLE_REPLICA_SERVICES[name]}）`);
      }
    }
    const scaled = (values && values.scaling && values.scaling.services) || [];
    if (Array.isArray(scaled) && scaled.includes(name)) {
      out.push(`${source}: scaling.services に ${name} がある（HPA が 2 以上へ増やす。${SINGLE_REPLICA_SERVICES[name]}）`);
    }
  }
  return out;
}

/** Argo CD の Application の `spec.source.helm.parameters` から違反を返す。 */
function argoProblems(app, source, names = Object.keys(SINGLE_REPLICA_SERVICES)) {
  const params = (((app || {}).spec || {}).source || {}).helm?.parameters || [];
  const out = [];
  for (const p of params) {
    for (const name of names) {
      if (p.name === `services.${name}.replicas` && !(Number(p.value) <= 1)) {
        out.push(`${source}: helm.parameters が services.${name}.replicas を ${p.value} にしている`);
      }
      if (/^scaling\.services(\[\d+\])?$/.test(String(p.name)) && String(p.value).includes(name)) {
        out.push(`${source}: helm.parameters が scaling.services に ${name} を入れている`);
      }
    }
  }
  return out;
}

/** スクリプト・ワークフローの本文から `--set` の上書きを探す（行ごと。コメント行も含める —— 例示でも書かせない）。 */
function setFlagProblems(text, source, names = Object.keys(SINGLE_REPLICA_SERVICES)) {
  const out = [];
  text.split('\n').forEach((line, i) => {
    for (const name of names) {
      const m = new RegExp(`services\\.${name}\\.replicas\\s*=\\s*([^\\s,"']+)`).exec(line);
      if (m && !(Number(m[1]) <= 1)) out.push(`${source}:${i + 1}: --set が services.${name}.replicas を ${m[1]} にしている`);
      if (/scaling\.services/.test(line) && new RegExp(`\\b${name}\\b`).test(line.split('scaling.services')[1] || '')) {
        out.push(`${source}:${i + 1}: --set が scaling.services に ${name} を入れている`);
      }
    }
  });
  return out;
}

function readValues(rel) {
  const docs = parseDocuments(fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8'));
  return docs[0] || {};
}

function listFiles(dir, re) {
  const abs = path.join(REPO_ROOT, dir);
  if (!fs.existsSync(abs)) return [];
  return fs.readdirSync(abs).filter((f) => re.test(f)).map((f) => path.posix.join(dir, f)).sort();
}

/** 実データを走査する。{ problems, seen } を返す。 */
function scan() {
  const problems = [];
  const seen = [];
  const chart = readValues(CHART_VALUES);
  for (const name of Object.keys(SINGLE_REPLICA_SERVICES)) {
    if (!chart.services || !chart.services[name]) {
      problems.push(`${CHART_VALUES}: services.${name} が無い（名前が変わったなら本検査の対象も直す。0 件走査で緑にしない）`);
    } else {
      seen.push(`${CHART_VALUES}: services.${name}.replicas = ${chart.services[name].replicas ?? '（未指定＝1）'}`);
    }
  }
  for (const rel of [CHART_VALUES, LOCAL_VALUES, ...listFiles(CI_VALUES_DIR, /\.ya?ml$/)]) {
    problems.push(...valuesProblems(rel === CHART_VALUES ? chart : readValues(rel), rel));
  }
  problems.push(...argoProblems(readValues(ARGO_APP), ARGO_APP));
  const texts = [...listFiles('scripts', /\.sh$/), ...listFiles('scripts/lib', /\.sh$/), ...listFiles('.github/workflows', /\.ya?ml$/)];
  for (const rel of texts) problems.push(...setFlagProblems(fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8'), rel));
  seen.push(`走査: values ${2 + listFiles(CI_VALUES_DIR, /\.ya?ml$/).length} 本・Argo CD 1 本・スクリプトとワークフロー ${texts.length} 本`);
  return { problems, seen };
}

function selfTest() {
  const cases = [];
  const t = (name, pass) => cases.push({ name, pass });
  const base = { services: { ingestion: { replicas: '1' }, document: { replicas: '3' } }, scaling: { services: ['bff', 'document'] } };
  t('陽性: ingestion が 1・HPA の対象外なら違反なし（他のサービスの replicas は見ない）', valuesProblems(base, 'v').length === 0);
  t('陽性: replicas の未指定（既定 1）は違反なし', valuesProblems({ services: { ingestion: {} } }, 'v').length === 0);
  t('★ 陰性: replicas を 2 にすると赤', valuesProblems({ services: { ingestion: { replicas: '2' } } }, 'v').some((p) => /replicas が 2/.test(p)));
  t('★ 陰性: replicas が数でない（"two"）なら赤', valuesProblems({ services: { ingestion: { replicas: 'two' } } }, 'v').length === 1);
  t('★ 陰性: scaling.services に ingestion を足すと赤', valuesProblems({ ...base, scaling: { services: ['bff', 'ingestion'] } }, 'v').some((p) => /scaling\.services/.test(p)));
  const app = (params) => ({ spec: { source: { helm: { parameters: params } } } });
  t('陽性: Argo CD の parameters に上書きが無ければ違反なし', argoProblems(app([{ name: 'config.appliedBy', value: 'argocd' }]), 'a').length === 0);
  t('★ 陰性: Argo CD の parameters で replicas を 2 にすると赤', argoProblems(app([{ name: 'services.ingestion.replicas', value: '2' }]), 'a').length === 1);
  t('★ 陰性: Argo CD の parameters で scaling.services に ingestion を入れると赤', argoProblems(app([{ name: 'scaling.services[3]', value: 'ingestion' }]), 'a').length === 1);
  t('陽性: --set で replicas を 1 にするのは違反なし', setFlagProblems('helm upgrade --set services.ingestion.replicas=1', 's').length === 0);
  t('★ 陰性: --set で replicas を 3 にすると赤（行番号を名指す）', setFlagProblems('a\nhelm upgrade --set services.ingestion.replicas=3', 's').some((p) => /^s:2:/.test(p)));
  t('★ 陰性: --set で scaling.services に ingestion を入れると赤', setFlagProblems("--set 'scaling.services={bff,ingestion}'", 's').length === 1);
  t('陽性: --set の scaling.services に ingestion が無ければ違反なし', setFlagProblems("--set 'scaling.services={bff,document}'", 's').length === 0);
  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) failed += 1;
  }
  if (failed) {
    console.error(`[check-single-replica-workers] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-single-replica-workers] 自己試験 ${cases.length} 件 OK。`);
}

function main() {
  if (process.argv.includes('--self-test')) return selfTest();
  const { problems, seen } = scan();
  for (const s of seen) console.log(`[check-single-replica-workers] ${s}`);
  if (problems.length) {
    console.error(`[check-single-replica-workers] 1 レプリカの前提のサービスが増やされている（${problems.length} 件）:`);
    for (const p of problems) console.error(`  - ${p}`);
    process.exit(1);
  }
  console.log('[check-single-replica-workers] OK: 1 レプリカの前提のサービス（ingestion）は、どの経路でも 1 レプリカのままである。');
}

module.exports = { SINGLE_REPLICA_SERVICES, valuesProblems, argoProblems, setFlagProblems, scan };

if (require.main === module) main();
