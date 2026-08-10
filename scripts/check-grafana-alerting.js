#!/usr/bin/env node
'use strict';
/*
 * check-grafana-alerting.js — #665 / ADR-0006 / NFR
 *
 * SLO アラートの**暫定**の一次検知（Grafana 統合アラート）が、Prometheus のアラートルールと
 * 食い違っていないことを機械で見る。
 *
 * ★ 本検査器で確かめられないこと（先に書く）:
 *   **「Grafana がこの provisioning を受理するか」は分からない。**
 *   実装環境で Grafana を起動できなかった（docker daemon へ到達不可。#665 §判断 0）。
 *   **配備時に `/api/v1/provisioning/alert-rules` が 5 件返すことを別途確かめること。**
 *   ここで見るのは下の 5 点だけである。
 *
 * 検査:
 *   1. ルール数が deploy/prometheus/alerts.yml と一致する
 *   2. ルール名（alert: / title:）が 1 対 1 で対応する
 *   3. 各ルールの datasourceUid が datasources に実在する（`__expr__` は Grafana 組込み）
 *   4. compose と k8s の inline が同内容である（二重管理の乖離を止める）
 *   5. 必須キーが揃っている（apiVersion / groups / 各ルールの title・condition・data）
 *
 * fail-closed（#664 / IADR-0130）: 走査結果が 0 件なら fail する。
 *   「検査しているつもりで何も見ていない」状態を緑で返さない。
 *
 * 実行: node scripts/check-grafana-alerting.js [--self-test]
 */
const fs = require('fs');
const path = require('path');

const REPO = path.join(__dirname, '..');
const PROM_ALERTS = path.join(REPO, 'deploy/prometheus/alerts.yml');
const GRAFANA_ALERTS = path.join(REPO, 'deploy/grafana/provisioning/alerting/slo-alerts.yaml');
const DATASOURCES = path.join(REPO, 'deploy/grafana/provisioning/datasources/datasources.yaml');
const K8S_GRAFANA = path.join(REPO, 'deploy/local/observability/grafana.yaml');

/** Grafana が組込みで持つ式エンジンの uid（datasources に宣言しない）。 */
const BUILTIN_DATASOURCE_UIDS = new Set(['__expr__', '-100']);

/**
 * Prometheus のアラート名を拾う。**字下げを固定しない** ——
 * `^  - alert:` のような決め打ちは字下げが変わると 0 件になる（#665 の着手時に実際に踏んだ）。
 */
function promAlertNames(text) {
  return [...text.matchAll(/^\s*-\s*alert:\s*(\S+)\s*$/gm)].map((m) => m[1]);
}

/** Grafana provisioning のルール名（title）を拾う。コメント行は除く。 */
function grafanaRuleTitles(text) {
  // `- title: X`（配列要素の先頭に書く形）と `title: X`（行独立）の**両方**を拾う。
  // 片方だけにすると、書き方を変えただけで 0 件走査になる（#664 の教訓）。
  return [...text.matchAll(/^\s*(?:-\s*)?title:\s*(\S+)\s*$/gm)].map((m) => m[1]);
}

/**
 * provisioning が参照する datasourceUid を拾う。
 * **`- datasourceUid: X` と `datasourceUid: X` の両方**を拾う（title と同じ理由。
 * 片方だけにすると、書き方を変えただけで 0 件走査になる）。
 */
function referencedDatasourceUids(text) {
  return [...text.matchAll(/^\s*(?:-\s*)?datasourceUid:\s*(\S+)\s*$/gm)].map((m) => m[1]);
}

/**
 * datasources.yaml が宣言する uid を拾う。
 * **字下げの深さを固定しない** —— `^\s{4}` のような決め打ちは、書式を整えただけで 0 件になる。
 * `jsonData` 配下の `datasourceUid:` とは**キー名が違う**ので取り違えない。
 */
function declaredDatasourceUids(text) {
  return [...text.matchAll(/^\s*(?:-\s*)?uid:\s*(\S+)\s*$/gm)].map((m) => m[1]);
}

/** k8s の grafana.yaml から `slo-alerts.yaml: |` の inline 本文を取り出す（字下げを剥がす）。 */
function extractK8sInline(text) {
  const m = text.match(/^\s*slo-alerts\.yaml:\s*\|\s*$/m);
  if (!m) return null;
  const lines = text.slice(m.index + m[0].length).split('\n').slice(1);
  const out = [];
  for (const line of lines) {
    if (line.trim() === '') { out.push(''); continue; }
    if (!line.startsWith('    ')) break;
    out.push(line.slice(4));
  }
  return out.join('\n').trimEnd();
}

/** 内容比較のための正規化（末尾空白と空行の差を無視する）。 */
function normalize(text) {
  return text.split('\n').map((l) => l.replace(/\s+$/, '')).filter((l) => l !== '').join('\n');
}

/** 検査本体。読み込んだテキストを受け取る純関数（自己試験から呼べるようにする）。 */
function findIssues({ prom, grafana, datasources, k8sInline }) {
  const issues = [];
  const promNames = promAlertNames(prom);
  const titles = grafanaRuleTitles(grafana);

  // 1 / 2: 件数と名前の 1 対 1
  const missing = promNames.filter((n) => !titles.includes(n));
  const extra = titles.filter((n) => !promNames.includes(n));
  for (const n of missing) issues.push(`Prometheus にあって Grafana に無いルール: ${n}`);
  for (const n of extra) issues.push(`Grafana にあって Prometheus に無いルール: ${n}`);

  // 3: datasourceUid の実在
  const declared = new Set(declaredDatasourceUids(datasources));
  for (const uid of new Set(referencedDatasourceUids(grafana))) {
    if (BUILTIN_DATASOURCE_UIDS.has(uid)) continue;
    if (!declared.has(uid)) {
      issues.push(`datasourceUid "${uid}" が datasources.yaml に宣言されていない`);
    }
  }

  // 4: compose と k8s の同内容
  if (k8sInline === null) {
    issues.push('k8s の grafana.yaml に slo-alerts.yaml の inline が見つからない（経路 B が無音になる）');
  } else if (normalize(k8sInline) !== normalize(grafana)) {
    issues.push('compose の slo-alerts.yaml と k8s の inline が同内容ではない（二重管理の乖離）');
  }

  // 5: 必須キー
  if (!/^apiVersion:\s*1\s*$/m.test(grafana)) issues.push('slo-alerts.yaml に apiVersion: 1 が無い');
  if (!/^groups:\s*$/m.test(grafana)) issues.push('slo-alerts.yaml に groups: が無い');
  for (const key of ['condition:', 'data:', 'noDataState:', 'execErrState:']) {
    const n = (grafana.match(new RegExp(`^\\s*${key}`, 'gm')) || []).length;
    if (n < titles.length) {
      issues.push(`ルール ${titles.length} 件に対し ${key} が ${n} 件しかない（必須キーの欠落）`);
    }
  }
  return { issues, promCount: promNames.length, grafanaCount: titles.length };
}

function selfTest() {
  const assert = require('assert');
  const base = {
    prom: '      - alert: Foo\n      - alert: Bar\n',
    grafana:
      'apiVersion: 1\ngroups:\n  - rules:\n' +
      '      - uid: foo\n        title: Foo\n        condition: C\n        noDataState: NoData\n        execErrState: Error\n        data:\n          - datasourceUid: prometheus\n          - datasourceUid: __expr__\n' +
      '      - uid: bar\n        title: Bar\n        condition: C\n        noDataState: NoData\n        execErrState: Error\n        data:\n          - datasourceUid: prometheus\n',
    datasources: 'datasources:\n  - name: P\n    uid: prometheus\n',
  };
  base.k8sInline = base.grafana;
  let passed = 0;
  const t = (name, fn) => { fn(); passed++; process.stdout.write(`  ok  ${name}\n`); };

  t('揃っていれば違反 0 件', () => assert.deepStrictEqual(findIssues(base).issues, []));

  t('字下げが変わってもルール名を拾う（^  - alert: の決め打ちをしない）', () =>
    assert.strictEqual(promAlertNames('    - alert: Baz\n').length, 1));

  t('Prometheus にだけあるルールを検出する（変異試験）', () => {
    const r = findIssues({ ...base, prom: base.prom + '      - alert: Baz\n' });
    assert.ok(r.issues.some((x) => x.includes('Grafana に無いルール: Baz')), JSON.stringify(r.issues));
  });

  t('Grafana にだけあるルールを検出する（変異試験）', () => {
    const g = base.grafana + '      - uid: qux\n        title: Qux\n';
    const r = findIssues({ ...base, grafana: g, k8sInline: g });
    assert.ok(r.issues.some((x) => x.includes('Prometheus に無いルール: Qux')), JSON.stringify(r.issues));
  });

  t('宣言されていない datasourceUid を検出する（変異試験）', () => {
    const r = findIssues({ ...base, datasources: 'datasources:\n  - name: P\n    uid: other\n' });
    assert.ok(r.issues.some((x) => x.includes('datasourceUid "prometheus"')), JSON.stringify(r.issues));
  });

  t('__expr__ は組込みなので違反にしない', () =>
    assert.ok(!findIssues(base).issues.some((x) => x.includes('__expr__'))));

  t('compose と k8s の乖離を検出する（変異試験）', () => {
    const r = findIssues({ ...base, k8sInline: base.grafana.replace('title: Bar', 'title: Baz') });
    assert.ok(r.issues.some((x) => x.includes('同内容ではない')), JSON.stringify(r.issues));
  });

  t('k8s の inline が無いことを検出する（経路 B の無音・変異試験）', () => {
    const r = findIssues({ ...base, k8sInline: null });
    assert.ok(r.issues.some((x) => x.includes('inline が見つからない')), JSON.stringify(r.issues));
  });

  t('必須キーの欠落を検出する（変異試験）', () => {
    const g = base.grafana.replace(/\n\s*condition: C/g, '');
    const r = findIssues({ ...base, grafana: g, k8sInline: g });
    assert.ok(r.issues.some((x) => x.includes('condition:')), JSON.stringify(r.issues));
  });

  t('k8s の inline を字下げを剥がして取り出す', () => {
    const got = extractK8sInline('data:\n  slo-alerts.yaml: |\n    apiVersion: 1\n    groups: []\n---\n');
    assert.strictEqual(got, 'apiVersion: 1\ngroups: []');
  });

  process.stdout.write(`\n✓ self-test: ${passed} 件すべて通過\n`);
}

function main(argv) {
  if (argv.includes('--self-test')) { selfTest(); return 0; }

  const read = (p) => { try { return fs.readFileSync(p, 'utf8'); } catch { return null; } };
  const prom = read(PROM_ALERTS);
  const grafana = read(GRAFANA_ALERTS);
  const datasources = read(DATASOURCES);
  const k8s = read(K8S_GRAFANA);

  for (const [name, text] of [['deploy/prometheus/alerts.yml', prom],
    ['deploy/grafana/provisioning/alerting/slo-alerts.yaml', grafana],
    ['deploy/grafana/provisioning/datasources/datasources.yaml', datasources],
    ['deploy/local/observability/grafana.yaml', k8s]]) {
    if (text === null) {
      console.error(`[check-grafana-alerting] ${name} を読めませんでした。`);
      console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
      return 1;
    }
  }

  const { issues, promCount, grafanaCount } = findIssues({
    prom, grafana, datasources, k8sInline: extractK8sInline(k8s),
  });

  // #664 / IADR-0130: 0 件走査で緑を返さない。
  if (promCount === 0 || grafanaCount === 0) {
    console.error(`[check-grafana-alerting] ルールを 1 件も拾えませんでした（Prometheus ${promCount} 件 / Grafana ${grafanaCount} 件）。`);
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    return 1;
  }

  if (issues.length === 0) {
    console.log(
      `[check-grafana-alerting] OK: Prometheus ${promCount} 件 / Grafana ${grafanaCount} 件のルールが 1 対 1 で対応し、` +
      'datasourceUid は実在し、compose と k8s は同内容です。' +
      '（**Grafana が受理するかは本検査の対象外**。配備時に /api/v1/provisioning/alert-rules を確かめること）',
    );
    return 0;
  }
  console.error(`[check-grafana-alerting] 違反 ${issues.length} 件:`);
  for (const i of issues) console.error(`  - ${i}`);
  return 1;
}

module.exports = { findIssues, promAlertNames, grafanaRuleTitles, extractK8sInline, selfTest };

if (require.main === module) process.exit(main(process.argv.slice(2)));
