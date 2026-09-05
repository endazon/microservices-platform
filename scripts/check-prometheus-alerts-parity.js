#!/usr/bin/env node
'use strict';
/*
 * check-prometheus-alerts-parity.js — #1246 の取りこぼし / ADR-0006 / NFR-21
 *
 * 経路 A（compose・`deploy/prometheus/alerts.yml`）と経路 B（k8s・
 * `deploy/local/observability/prometheus.yaml` の `alerts.yml` inline）の
 * **アラートルールが 1 対 1 で対応する**ことを機械で見る。
 *
 * ★ 本検査器で確かめられないこと（先に書く）:
 *   - **Prometheus がこのルールを受理するか**は分からない（PromQL の妥当性も見ていない）。
 *     配備時に `/api/v1/rules` を確かめること。
 *   - **アラートが実際に発火するか**も見ていない。式の検証手順と「陽性対照を対で置く」規律は
 *     `deploy/prometheus/alerts.yml` の冒頭にある（#1110）。
 *
 * ★ なぜ別の検査器なのか（既存 2 本と軸が重ならない）:
 *   - `check-grafana-alerting.js`（#665）… **Prometheus のルール ↔ Grafana のルール**の 1 対 1。
 *     経路 A/B のパリティではない。
 *   - `check-grafana-provisioning-parity.js`（#674 / IADR-0168）… 経路 A/B のパリティだが
 *     **Grafana の provisioning 配下だけ**が対象で、Prometheus 側は入っていない。
 *   本検査器は **Prometheus 側の経路 A/B パリティ**を持つ。
 *
 * ★ 🔴 **突合するのは 群名 ＋ ルール名 ＋ expr ＋ for ＋ severity だけである。**
 *   `summary` / `description` は突合しない —— 経路 B の inline は**意図的に凝縮した文面**を持ち
 *   （生のメトリクス名を平叙へ言い換え、末尾の ADR 引用を落としている）、
 *   バイト一致を課すと常に赤になる。実測（本検査器の新設時・基点 4eff9bb4）では、
 *   共通 10 ルールのすべてで群名・expr・for・severity が一致し、差は description だけだった。
 *
 * ★ コメント行は両側とも落としてから読む（`#` 始まりの行）。compose 側の follow-up 群
 *   （exporter 依存でコメントアウトされている `platform-messaging` 等）は両側で除かれる。
 *
 * fail-closed（#664 / IADR-0130）: 走査結果が 0 件なら fail する。
 *   「検査しているつもりで何も見ていない」状態を緑で返さない。
 *
 * 実行: node scripts/check-prometheus-alerts-parity.js [--self-test]
 */
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const COMPOSE = path.join(ROOT, 'deploy', 'prometheus', 'alerts.yml');
const K8S = path.join(ROOT, 'deploy', 'local', 'observability', 'prometheus.yaml');

/** 経路 B の ConfigMap から `alerts.yml: |` ブロックだけを取り出す。 */
function extractK8sInline(text) {
  const start = text.indexOf('\n  alerts.yml: |');
  if (start < 0) return '';
  const rest = text.slice(start + 1);
  // 次のドキュメント区切り（行頭 `---`）までが inline の範囲。
  const end = rest.search(/\n---\s*$|\n---\n/);
  return end < 0 ? rest : rest.slice(0, end);
}

/**
 * ルールを読み出す。YAML パーサは使わない（本リポの検査器は Node 標準のみで動かす方針）。
 * コメント行を落としてから、`- name:` / `- alert:` / `expr:` / `for:` / `severity:` を拾う。
 * 🔴 `expr:` はブロックスカラー（`|`）を取り得るので、後続の字下げ行を連結して 1 本の文字列にする。
 */
function parseRules(text) {
  const lines = text.split('\n').filter((l) => !/^\s*#/.test(l));
  const rules = [];
  let group = null;
  let pendingExprIndent = null;
  for (const raw of lines) {
    if (pendingExprIndent !== null) {
      const m = raw.match(/^(\s*)(\S.*)$/);
      if (m && m[1].length > pendingExprIndent) {
        rules[rules.length - 1].expr += (rules[rules.length - 1].expr ? ' ' : '') + m[2].trim();
        continue;
      }
      pendingExprIndent = null;
    }
    const g = raw.match(/^\s*-\s*name:\s*(\S+)\s*$/);
    if (g) { group = g[1]; continue; }
    const a = raw.match(/^(\s*)-\s*alert:\s*(\S+)\s*$/);
    if (a) { rules.push({ group, alert: a[2], expr: '', for: null, severity: null }); continue; }
    if (rules.length === 0) continue;
    const cur = rules[rules.length - 1];
    const e = raw.match(/^(\s*)expr:\s*(.*)$/);
    if (e && cur.expr === '') {
      const inline = e[2].trim();
      if (inline === '|' || inline === '>') pendingExprIndent = e[1].length;
      else cur.expr = inline;
      continue;
    }
    const f = raw.match(/^\s*for:\s*(\S+)\s*$/);
    if (f && cur.for === null) { cur.for = f[1]; continue; }
    const s = raw.match(/severity:\s*(\w+)/);
    if (s && cur.severity === null) { cur.severity = s[1]; continue; }
  }
  return rules;
}

/** 突合。返すのは違反の説明文の配列と、両側の件数。 */
function findIssues({ compose, k8sInline }) {
  const a = parseRules(compose);
  const b = parseRules(k8sInline);
  const issues = [];
  const byName = new Map(b.map((r) => [r.alert, r]));

  for (const x of a) {
    const y = byName.get(x.alert);
    if (!y) {
      issues.push(
        `経路 B の inline に '${x.alert}'（群 ${x.group}）が無い。` +
        ' compose へ足したときに deploy/local/observability/prometheus.yaml も同時に直すこと。',
      );
      continue;
    }
    if (x.group !== y.group) {
      issues.push(`'${x.alert}' の群が違う（compose: ${x.group} / k8s: ${y.group}）。`);
    }
    if (x.expr !== y.expr) {
      issues.push(`'${x.alert}' の expr が違う。\n      compose: ${x.expr}\n      k8s    : ${y.expr}`);
    }
    if (x.for !== y.for) {
      issues.push(`'${x.alert}' の for が違う（compose: ${x.for} / k8s: ${y.for}）。`);
    }
    if (x.severity !== y.severity) {
      issues.push(`'${x.alert}' の severity が違う（compose: ${x.severity} / k8s: ${y.severity}）。`);
    }
  }
  const aNames = new Set(a.map((r) => r.alert));
  for (const y of b) {
    if (!aNames.has(y.alert)) {
      issues.push(`経路 B の inline に compose へ無い '${y.alert}' がある（消し忘れか、逆向きの乖離）。`);
    }
  }
  return { issues, composeCount: a.length, k8sCount: b.length };
}

/* ---------------------------------------------------------------- self-test */

const SELF_TEST_COMPOSE = `groups:
  - name: g1
    rules:
      # コメントは落とされる
      - alert: A
        expr: up == 0
        for: 2m
        labels: { severity: critical }
        annotations:
          summary: "compose の長い文面"
      - alert: B
        expr: |
          histogram_quantile(0.95, x) > 5
        for: 10m
        labels: { severity: warning }
`;

const SELF_TEST_K8S = `  alerts.yml: |
    groups:
      - name: g1
        rules:
          - alert: A
            expr: up == 0
            for: 2m
            labels: { severity: critical }
            annotations:
              summary: "k8s の凝縮した文面"
          - alert: B
            expr: |
              histogram_quantile(0.95, x) > 5
            for: 10m
            labels: { severity: warning }
`;

function selfTest() {
  const failures = [];
  const ran = [];
  // 🔴 **ケース名を出力する。** 件数だけを見る回帰試験は、変異ケースを消しても通りつづける
  //   （#657 で実際に起きた誤り。scripts.repo.test.js の check-grafana-alerting 節が同じ教訓を持つ）。
  //   scripts.repo.test.js は**この名前を名指しで**確かめる。
  const check = (name, fn) => {
    ran.push(name);
    try { fn(); } catch (e) { failures.push(`${name}: ${e.message}`); }
  };
  const assert = (cond, msg) => { if (!cond) throw new Error(msg); };

  check('陽性: 文面だけが違っても違反 0 件', () => {
    const { issues, composeCount, k8sCount } = findIssues({
      compose: SELF_TEST_COMPOSE, k8sInline: SELF_TEST_K8S,
    });
    assert(composeCount === 2 && k8sCount === 2, `件数が 2/2 でない（${composeCount}/${k8sCount}）`);
    assert(issues.length === 0, `違反が出た: ${issues.join(' / ')}`);
  });

  check('変異: k8s からルールを 1 件消すと違反', () => {
    const mutated = SELF_TEST_K8S.replace(/ {10}- alert: B[\s\S]*$/, '');
    const { issues } = findIssues({ compose: SELF_TEST_COMPOSE, k8sInline: mutated });
    assert(issues.some((i) => i.includes("'B'")), `B の欠落を検出していない: ${issues.join(' / ')}`);
  });

  check('変異: expr を変えると違反', () => {
    const mutated = SELF_TEST_K8S.replace('up == 0', 'up == 1');
    const { issues } = findIssues({ compose: SELF_TEST_COMPOSE, k8sInline: mutated });
    assert(issues.some((i) => i.includes('expr')), `expr の差を検出していない: ${issues.join(' / ')}`);
  });

  check('変異: for を変えると違反', () => {
    const mutated = SELF_TEST_K8S.replace('for: 2m', 'for: 5m');
    const { issues } = findIssues({ compose: SELF_TEST_COMPOSE, k8sInline: mutated });
    assert(issues.some((i) => i.includes('for')), `for の差を検出していない: ${issues.join(' / ')}`);
  });

  check('変異: severity を変えると違反', () => {
    const mutated = SELF_TEST_K8S.replace('severity: critical', 'severity: warning');
    const { issues } = findIssues({ compose: SELF_TEST_COMPOSE, k8sInline: mutated });
    assert(issues.some((i) => i.includes('severity')), `severity の差を検出していない: ${issues.join(' / ')}`);
  });

  check('変異: 群名を変えると違反', () => {
    const mutated = SELF_TEST_K8S.replace('- name: g1', '- name: g2');
    const { issues } = findIssues({ compose: SELF_TEST_COMPOSE, k8sInline: mutated });
    assert(issues.some((i) => i.includes('群')), `群の差を検出していない: ${issues.join(' / ')}`);
  });

  check('変異: k8s にだけ余分なルールがあると違反（逆向きの乖離）', () => {
    const mutated = `${SELF_TEST_K8S}          - alert: C\n            expr: vector(1)\n            for: 1m\n            labels: { severity: warning }\n`;
    const { issues } = findIssues({ compose: SELF_TEST_COMPOSE, k8sInline: mutated });
    assert(issues.some((i) => i.includes("'C'")), `余分な C を検出していない: ${issues.join(' / ')}`);
  });

  check('ブロックスカラーの expr が 1 本に連結される', () => {
    const rules = parseRules(SELF_TEST_COMPOSE);
    const b = rules.find((r) => r.alert === 'B');
    assert(b.expr === 'histogram_quantile(0.95, x) > 5', `expr の連結が違う: ${b.expr}`);
  });

  check('extractK8sInline が alerts.yml ブロックだけを取る', () => {
    const doc = `apiVersion: v1\ndata:\n${SELF_TEST_K8S}---\nkind: Deployment\n`;
    const got = extractK8sInline(doc);
    assert(got.includes('- alert: A'), 'alerts.yml ブロックを取れていない');
    assert(!got.includes('kind: Deployment'), '次のドキュメントまで拾っている');
  });

  for (const name of ran) console.log(`  [case] ${name}`);
  if (failures.length) {
    console.error('[check-prometheus-alerts-parity] self-test 失敗:');
    for (const f of failures) console.error(`  - ${f}`);
    return 1;
  }
  console.log(`[check-prometheus-alerts-parity] self-test OK（${ran.length} 件）`);
  return 0;
}

/* -------------------------------------------------------------------- main */

function read(p) {
  try { return fs.readFileSync(p, 'utf8'); } catch { return null; }
}

function main(argv) {
  if (argv.includes('--self-test')) return selfTest();

  const compose = read(COMPOSE);
  const k8s = read(K8S);
  for (const [name, text] of [['deploy/prometheus/alerts.yml', compose],
    ['deploy/local/observability/prometheus.yaml', k8s]]) {
    if (text === null) {
      console.error(`[check-prometheus-alerts-parity] ${name} を読めませんでした。`);
      console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
      return 1;
    }
  }

  const { issues, composeCount, k8sCount } = findIssues({
    compose, k8sInline: extractK8sInline(k8s),
  });

  // #664 / IADR-0130: 0 件走査で緑を返さない。
  if (composeCount === 0 || k8sCount === 0) {
    console.error(
      `[check-prometheus-alerts-parity] ルールを 1 件も拾えませんでした（compose ${composeCount} 件 / k8s ${k8sCount} 件）。`,
    );
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    return 1;
  }

  if (issues.length === 0) {
    console.log(
      `[check-prometheus-alerts-parity] OK: compose ${composeCount} 件 / k8s inline ${k8sCount} 件のルールが` +
      ' 群名・名前・expr・for・severity で 1 対 1 に対応しています。' +
      '（**summary / description は突合対象外**。k8s 側は意図的に凝縮した文面を持つ）',
    );
    return 0;
  }
  console.error(`[check-prometheus-alerts-parity] 違反 ${issues.length} 件:`);
  for (const i of issues) console.error(`  - ${i}`);
  return 1;
}

module.exports = { findIssues, parseRules, extractK8sInline, selfTest };

if (require.main === module) process.exit(main(process.argv.slice(2)));
