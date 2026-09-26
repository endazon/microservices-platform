#!/usr/bin/env node
'use strict';
/*
 * check-grafana-alerting.js — #665 / ADR-0006 / NFR-21
 *
 * SLO アラートの**暫定**の一次検知（Grafana 統合アラート）が、Prometheus のアラートルールと
 * 食い違っていないことを機械で見る。
 *
 * ★ 本検査器で確かめられないこと（先に書く）:
 *   **「Grafana がこの provisioning を受理するか」は分からない。**
 *   実装環境で Grafana を起動できなかった（docker daemon へ到達不可。#665 §判断 0）。
 *   **配備時に `/api/v1/provisioning/alert-rules` が 20 件返すことを別途確かめること。**
 *   （#1204 で `RagFirstTokenP95High` を足して 5 → 6、#1202 で `…SeriesAbsent` 3 件を足して 6 → 9、
 *    #1246 で `…ProducerAbsent` 2 件を足して 9 → 11、#1203 で `RagLatencySeriesAbsent` を足して 11 → 12、
 *    #1233 で `UnitDocumentsMissingProjectAttribute` を足して 12 → 13、
 *    #1245 PR-B で mail-relay のキュー 3 件（滞留 2 ＋ 系列の不在 1）を足して 13 → 16、
 *    #1111 で `LlmMonthlyBudgetExceeded` を足して 16 → 17、
 *    #1544 で床の器の全滅 `ResetFloorNoReadyEndpoint` と不在 `ResetFloorUpSeriesAbsent` を足して 17 → 19、
 *    #1573 で部門の同期の `DepartmentSyncNotCorrecting` を足して 19 → 20。
 *    🔴 **件数は導出値なので数え直すこと** —— この行は #1246 の 2 件を取りこぼして
 *    「9 件」のまま 2 世代残っていた。**走査ではなく計算し直す。**）
 *   ここで見るのは下の 6 点だけである。
 *
 * 検査:
 *   1. ルール数が deploy/prometheus/alerts.yml と一致する
 *   2. ルール名（alert: / title:）が 1 対 1 で対応する
 *   3. 各ルールの datasourceUid が datasources に実在する（`__expr__` は Grafana 組込み）
 *   4. compose と k8s の inline が同内容である（二重管理の乖離を止める）
 *   5. 必須キーが揃っている（apiVersion / groups / 各ルールの title・condition・data）
 *   6. 式の絞り込みの後に残る値で評価器が真になり得る（#1577。`== 0` で絞って `gt 0` で比べる形を止める。
 *      compose と k8s の inline の両方を見る。式・評価器を読めないルールは違反にする）
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

// ---------------------------------------------------------------------------
// 6. 式の絞り込みと評価器の組み合わせ（#1577 / NFR-21）
//
// Grafana 版は閾値を `expr` ではなく `conditions[].evaluator` に持つ（#1110）。
// そのため Prometheus 版の式（`up == 0`）をそのまま写すと、**絞り込みの後に残る値は 0** であり、
// 評価器 `gt 0` は `0 > 0` で偽になる —— **構文として正当なまま永久に発火しない**
// （`OtelCollectorDown` と `ServiceRequestMetricsAbsent` が #1577 までこの形だった。
//  #1544 の `ResetFloorNoReadyEndpoint` は最初から `up` を生で取り `lt 1` で比べている）。
//
// 見ること: 式の**最上位の比較**（`X == 0` 等。`bool` なし・片辺が数値リテラル）から、
// 発火側で残り得る値の集合を区間で求め、評価器を満たす値の集合と**交わらなければ**違反にする。
//
// ★ 読み方の規則（PromQL の優先順位に従う。低い順に `or` → `and` / `unless` → 比較 → 算術）:
//   - `or` の各辺は値を出す（どれか 1 辺でも発火し得れば違反にしない）。
//   - `and` / `unless` は**左辺の値を残す**。右辺の絞り込みは値を決めない。
//   - 比較に `bool` が付けば値は 0 / 1 である（絞り込みではない）。
//   - 比較の両辺がベクタ（リテラルでない）なら、左辺の値が残るだけで値は縛られない。
//   - 式全体を包む括弧は剥がして読み直す。
// ★ 見ていないもの（偽陰性は受容する。偽陽性を出さない側へ倒す）:
//   比較の結果にさらに算術・集約を掛けた形（`(up == 0) * 1`・`max(up == 0)` 等）は値を縛らないものとして読む。
// ★ fail-closed: 式または評価器を読めないルールは違反にする（読めないまま素通りさせない）。
// ---------------------------------------------------------------------------

const INF = Number.POSITIVE_INFINITY;

/** 区間 { lo, loInc, hi, hiInc }。点は lo === hi かつ両端を含む。 */
const interval = (lo, loInc, hi, hiInc) => ({ lo, loInc, hi, hiInc });
const point = (v) => interval(v, true, v, true);
const ALL = [interval(-INF, false, INF, false)];

/** 2 区間が交わるか。 */
function intersects(a, b) {
  const lo = Math.max(a.lo, b.lo);
  const hi = Math.min(a.hi, b.hi);
  if (lo < hi) return true;
  if (lo > hi) return false;
  const loInc = (a.lo === lo ? a.loInc : true) && (b.lo === lo ? b.loInc : true);
  const hiInc = (a.hi === hi ? a.hiInc : true) && (b.hi === hi ? b.hiInc : true);
  return loInc && hiInc;
}

/** 区間の集合どうしが交わるか。 */
const setsIntersect = (xs, ys) => xs.some((x) => ys.some((y) => intersects(x, y)));

/** 比較演算子と数値 c から、絞り込みの後に残り得る値の集合を返す。 */
function filterSet(op, c) {
  switch (op) {
    case '==': return [point(c)];
    case '!=': return [interval(-INF, false, c, false), interval(c, false, INF, false)];
    case '>': return [interval(c, false, INF, false)];
    case '>=': return [interval(c, true, INF, false)];
    case '<': return [interval(-INF, false, c, false)];
    case '<=': return [interval(-INF, false, c, true)];
    default: return ALL;
  }
}

/** 評価器（Grafana の threshold）を満たす値の集合。解釈できない型・引数は null。 */
function evaluatorSet(type, params) {
  const [a, b] = params;
  const need = (n) => params.length >= n && params.slice(0, n).every((x) => Number.isFinite(x));
  switch (type) {
    case 'gt': return need(1) ? [interval(a, false, INF, false)] : null;
    case 'lt': return need(1) ? [interval(-INF, false, a, false)] : null;
    case 'gte': return need(1) ? [interval(a, true, INF, false)] : null;
    case 'lte': return need(1) ? [interval(-INF, false, a, true)] : null;
    case 'eq': return need(1) ? [point(a)] : null;
    case 'ne': return need(1) ? filterSet('!=', a) : null;
    case 'within_range': return need(2) ? [interval(a, false, b, false)] : null;
    case 'within_range_included': return need(2) ? [interval(a, true, b, true)] : null;
    case 'outside_range': return need(2) ? [interval(-INF, false, a, false), interval(b, false, INF, false)] : null;
    case 'outside_range_included': return need(2) ? [interval(-INF, false, a, true), interval(b, true, INF, false)] : null;
    default: return null;
  }
}

/** 文字列リテラルと行コメントを潰す（ラベル値の中の `==` や `or` を拾わない）。 */
function stripPromqlNoise(expr) {
  return expr
    .replace(/"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*'|`[^`]*`/g, '""')
    .replace(/#[^\n]*/g, '');
}

/** 深さ 0 の位置で `re`（先頭一致・語境界つき）に当たる箇所で分け、各辺のテキストを返す。 */
function splitTopLevel(s, re) {
  const parts = [];
  let depth = 0;
  let start = 0;
  for (let i = 0; i < s.length; i++) {
    const ch = s[i];
    if ('([{'.includes(ch)) depth++;
    else if (')]}'.includes(ch)) depth--;
    else if (depth === 0 && (i === 0 || !/[A-Za-z0-9_:]/.test(s[i - 1]))) {
      const m = s.slice(i).match(re);
      if (m) {
        parts.push(s.slice(start, i));
        i += m[0].length - 1;
        start = i + 1;
      }
    }
  }
  parts.push(s.slice(start));
  return parts;
}

/** 式全体を包む括弧を剥がす（`(a) + (b)` のような形は剥がさない）。 */
function unwrapParens(s) {
  let t = s.trim();
  for (;;) {
    if (!t.startsWith('(') || !t.endsWith(')')) return t;
    let depth = 0;
    for (let i = 0; i < t.length; i++) {
      if (t[i] === '(') depth++;
      else if (t[i] === ')') depth--;
      if (depth === 0 && i < t.length - 1) return t;
    }
    t = t.slice(1, -1).trim();
  }
}

const NUMBER_RE = /^[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[-+]?\d+)?$/i;
const FLIP = { '==': '==', '!=': '!=', '>': '<', '<': '>', '>=': '<=', '<=': '>=' };

/** 深さ 0 の比較演算子を末尾から探す（比較は左結合なので最後のものが最外）。 */
function lastTopLevelComparison(s) {
  let depth = 0;
  let found = null;
  for (let i = 0; i < s.length; i++) {
    const ch = s[i];
    if ('([{'.includes(ch)) { depth++; continue; }
    if (')]}'.includes(ch)) { depth--; continue; }
    if (depth !== 0) continue;
    const two = s.slice(i, i + 2);
    if (['==', '!=', '>=', '<='].includes(two)) { found = { at: i, op: two }; i++; continue; }
    if (ch === '>' || ch === '<') found = { at: i, op: ch };
  }
  return found;
}

/**
 * 式が発火側で出し得る値の集合を返す。**値を縛らないなら ALL**。
 * 純関数（自己試験から直接呼ぶ）。
 */
function expressionValueSet(expr) {
  const s = unwrapParens(stripPromqlNoise(expr));
  // `or`: 各辺の和集合。
  const orParts = splitTopLevel(s, /^or\b/i);
  if (orParts.length > 1) return orParts.flatMap((p) => expressionValueSet(p));
  // `and` / `unless`: 最初の辺（左辺）の値が残る。
  const andParts = splitTopLevel(s, /^(?:and|unless)\b/i);
  if (andParts.length > 1) return expressionValueSet(andParts[0]);

  const cmp = lastTopLevelComparison(s);
  if (!cmp) return ALL;
  const lhs = unwrapParens(s.slice(0, cmp.at));
  const rest = s.slice(cmp.at + cmp.op.length).trim();
  if (/^bool\b/i.test(rest)) return [point(0), point(1)];
  // `on (...)` / `ignoring (...)` / `group_left` 等の修飾はベクタどうしの比較である。
  if (/^(?:on|ignoring|group_left|group_right)\b/i.test(rest)) return ALL;
  const rhs = unwrapParens(rest);
  if (NUMBER_RE.test(rhs) && !NUMBER_RE.test(lhs)) return filterSet(cmp.op, Number(rhs));
  if (NUMBER_RE.test(lhs) && !NUMBER_RE.test(rhs)) return filterSet(FLIP[cmp.op], Number(lhs));
  return ALL;
}

/** YAML の 1 行スカラーの引用符を剥がす。 */
function unquoteYamlScalar(raw) {
  const v = raw.trim();
  if (v.length >= 2 && v.startsWith("'") && v.endsWith("'")) return v.slice(1, -1).replace(/''/g, "'");
  if (v.length >= 2 && v.startsWith('"') && v.endsWith('"')) return v.slice(1, -1).replace(/\\(["\\])/g, '$1');
  return v;
}

/**
 * Grafana provisioning をルール単位に切り、各ルールの expr と評価器を読む。
 * 1 ルール = `title:` の行から次の `title:` の行まで（コメント行は除く）。
 * expr は 1 行（引用符あり・なし）と `|` / `>` のブロックの両方を読む。
 */
function grafanaRuleConditions(text) {
  const lines = text.split('\n').filter((l) => !/^\s*#/.test(l));
  const starts = [];
  lines.forEach((l, i) => { if (/^\s*(?:-\s*)?title:\s*\S+\s*$/.test(l)) starts.push(i); });
  return starts.map((start, k) => {
    const body = lines.slice(start, k + 1 < starts.length ? starts[k + 1] : lines.length);
    const title = body[0].replace(/^\s*(?:-\s*)?title:\s*/, '').trim();
    const exprs = [];
    for (let i = 0; i < body.length; i++) {
      const m = body[i].match(/^(\s*)expr:\s*(.*)$/);
      if (!m) continue;
      if (/^[|>][-+]?\s*$/.test(m[2])) {
        const block = [];
        for (let j = i + 1; j < body.length; j++) {
          if (body[j].trim() === '') { block.push(''); continue; }
          if (body[j].match(/^\s*/)[0].length <= m[1].length) break;
          block.push(body[j].trim());
        }
        exprs.push(block.join('\n').trim());
      } else {
        exprs.push(unquoteYamlScalar(m[2]));
      }
    }
    const evaluators = [...body.join('\n').matchAll(
      /evaluator:\s*\{\s*type:\s*([A-Za-z_]+)\s*,\s*params:\s*\[([^\]]*)\]\s*\}/g,
    )].map((m) => ({
      type: m[1],
      params: m[2].split(',').map((x) => x.trim()).filter((x) => x !== '').map(Number),
    }));
    return { title, exprs, evaluators };
  });
}

const describeSet = (xs) => xs.map((x) => (x.lo === x.hi
  ? `${x.lo}`
  : `${x.loInc ? '[' : '('}${x.lo}, ${x.hi}${x.hiInc ? ']' : ')'}`)).join(' ∪ ');

/**
 * 6 の本体。`label` は違反文に付ける写しの名前（compose / k8s inline）。
 * 戻り値: { issues, checked }（checked は組み合わせを判定できたルール数。0 件走査の門に使う）。
 */
function filterEvaluatorIssues(text, label) {
  const issues = [];
  let checked = 0;
  for (const { title, exprs, evaluators } of grafanaRuleConditions(text)) {
    if (exprs.length !== 1) {
      issues.push(`[${label}] ルール ${title}: expr を ${exprs.length} 件読んだ（本検査は 1 ルール 1 クエリだけを解釈する。読めないまま素通りさせない）`);
      continue;
    }
    if (evaluators.length !== 1) {
      issues.push(`[${label}] ルール ${title}: 評価器（evaluator: { type, params }）を ${evaluators.length} 件読んだ（1 件であること。読めないまま素通りさせない）`);
      continue;
    }
    const { type, params } = evaluators[0];
    const want = evaluatorSet(type, params);
    if (want === null) {
      issues.push(`[${label}] ルール ${title}: 評価器 ${type} [${params.join(', ')}] を解釈できない（型を本検査へ足すこと）`);
      continue;
    }
    const got = expressionValueSet(exprs[0]);
    checked++;
    if (!setsIntersect(got, want)) {
      issues.push(
        `[${label}] ルール ${title}: expr の絞り込みの後に残る値は ${describeSet(got)} だが、` +
        `評価器 ${type} [${params.join(', ')}] はその値で真にならない —— 永久に発火しない（#1577）。` +
        '絞り込みを外して生の値を評価器で比べること（例: `up` を `lt 1`）',
      );
    }
  }
  return { issues, checked };
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

  // 6: 式の絞り込みと評価器の組み合わせ（#1577）。**写しの両方**を見る
  //    （4 が同内容を見るが、乖離しているときに片方の違反を黙らせない）。
  const composeFilter = filterEvaluatorIssues(grafana, 'compose');
  issues.push(...composeFilter.issues);
  if (k8sInline !== null) issues.push(...filterEvaluatorIssues(k8sInline, 'k8s inline').issues);
  if (titles.length > 0 && composeFilter.checked === 0) {
    issues.push('式と評価器の組み合わせを 1 件も判定できなかった（0 件走査。検査しているつもりで何も見ていない）');
  }
  return {
    issues, promCount: promNames.length, grafanaCount: titles.length, filterChecked: composeFilter.checked,
  };
}

function selfTest() {
  const assert = require('assert');
  const base = {
    prom: '      - alert: Foo\n      - alert: Bar\n',
    grafana:
      'apiVersion: 1\ngroups:\n  - rules:\n' +
      '      - uid: foo\n        title: Foo\n        condition: C\n        noDataState: NoData\n        execErrState: Error\n        data:\n          - datasourceUid: prometheus\n' +
      "            model:\n              expr: 'up{job=\"x\"}'\n" +
      '          - datasourceUid: __expr__\n            model:\n              conditions:\n                - evaluator: { type: lt, params: [1] }\n' +
      '      - uid: bar\n        title: Bar\n        condition: C\n        noDataState: NoData\n        execErrState: Error\n        data:\n          - datasourceUid: prometheus\n' +
      '            model:\n              expr: |\n                sum by (job) (rate(m[5m])) > 0\n' +
      '          - datasourceUid: __expr__\n            model:\n              conditions:\n                - evaluator: { type: gt, params: [0] }\n',
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

  // ---- 6: 式の絞り込みと評価器の組み合わせ（#1577）----
  const withRule = (expr, evaluator) => {
    const g = base.grafana
      .replace("expr: 'up{job=\"x\"}'", expr.includes('\n') ? `expr: |\n${expr.split('\n').map((l) => `                ${l}`).join('\n')}` : `expr: '${expr}'`)
      .replace('{ type: lt, params: [1] }', evaluator);
    return { ...base, grafana: g, k8sInline: g };
  };
  const neverFires = (r) => r.issues.some((x) => x.includes('永久に発火しない'));

  t('== 0 の絞り込みと gt 0 の評価器の組み合わせを検出する（#1577・変異試験）', () => {
    const r = findIssues(withRule('up{job="x"} == 0', '{ type: gt, params: [0] }'));
    assert.ok(neverFires(r), JSON.stringify(r.issues));
    assert.ok(r.issues.some((x) => x.startsWith('[compose] ルール Foo')), JSON.stringify(r.issues));
    assert.ok(r.issues.some((x) => x.startsWith('[k8s inline] ルール Foo')), JSON.stringify(r.issues));
  });

  t('and の左辺の == 0 を検出する（右辺の > 0 は値を決めない。#1577・変異試験）', () => {
    const r = findIssues(withRule(
      'sum by (job) (rate(m[5m])) == 0\nand on (job) (sum by (job) (m offset 15m) > 0)',
      '{ type: gt, params: [0] }',
    ));
    assert.ok(neverFires(r), JSON.stringify(r.issues));
  });

  t('< 1 の絞り込みと gt 1 の評価器も検出する（== 0 以外の同型・変異試験）', () => {
    const r = findIssues(withRule('up{job="x"} < 1', '{ type: gt, params: [1] }'));
    assert.ok(neverFires(r), JSON.stringify(r.issues));
  });

  t('生の値を lt 1 で比べる形は違反にしない（#1544 の形）', () =>
    assert.ok(!neverFires(findIssues(base))));

  t('bool つきの比較は 0 / 1 を返すので違反にしない', () => {
    const r = findIssues(withRule(
      'sum by (job) (rate(m[5m])) == bool 0\nand on (job) (sum by (job) (m offset 15m) > 0)',
      '{ type: gt, params: [0] }',
    ));
    assert.deepStrictEqual(r.issues, []);
  });

  t('> 0 の絞り込みと gt 0 は違反にしない', () =>
    assert.deepStrictEqual(findIssues(withRule('increase(m[1h]) > 0', '{ type: gt, params: [0] }')).issues, []));

  t('or の片側でも発火し得れば違反にしない', () =>
    assert.deepStrictEqual(findIssues(withRule('up{job="x"} == 0 or absent(up{job="x"})', '{ type: gt, params: [0] }')).issues, []));

  t('括弧の中の or / 比較は最上位の比較を隠さない', () =>
    assert.deepStrictEqual(findIssues(withRule(
      '(sum(increase(a[1h])) or vector(0)) + (sum(increase(b{o=~"x|y"}[1h])) or vector(0)) > 0',
      '{ type: gt, params: [0] }',
    )).issues, []));

  t('ベクタどうしの比較（on 修飾）は値を縛らないので違反にしない', () =>
    assert.deepStrictEqual(findIssues(withRule(
      'sum by (p) (increase(c[30d])) > on (p) max by (p) (limit)',
      '{ type: gt, params: [0] }',
    )).issues, []));

  t('ラベル値の中の == や or を比較・集合演算として読まない', () =>
    assert.deepStrictEqual(findIssues(withRule('m{a="x == 0 or y"}', '{ type: gt, params: [0] }')).issues, []));

  t('評価器を読めないルールを検出する（fail-closed・変異試験）', () => {
    const r = findIssues(withRule('up{job="x"}', 'type: lt'));
    assert.ok(r.issues.some((x) => x.includes('評価器')), JSON.stringify(r.issues));
  });

  t('解釈できない評価器の型を検出する（fail-closed・変異試験）', () => {
    const r = findIssues(withRule('up{job="x"}', '{ type: unknown_type, params: [1] }'));
    assert.ok(r.issues.some((x) => x.includes('解釈できない')), JSON.stringify(r.issues));
  });

  t('区間の交わり: 端点は両方が含むときだけ交わる', () => {
    assert.ok(!setsIntersect([point(0)], evaluatorSet('gt', [0])));
    assert.ok(setsIntersect([point(0)], evaluatorSet('lt', [1])));
    assert.ok(setsIntersect(filterSet('>=', 1), evaluatorSet('gte', [1])));
    assert.ok(!setsIntersect(filterSet('>', 1), evaluatorSet('lte', [1])));
    assert.ok(!setsIntersect([point(5)], evaluatorSet('within_range', [1, 5])));
    assert.ok(setsIntersect([point(5)], evaluatorSet('within_range_included', [1, 5])));
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

  const { issues, promCount, grafanaCount, filterChecked } = findIssues({
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
      'datasourceUid は実在し、compose と k8s は同内容で、' +
      `式の絞り込みと評価器の組み合わせ ${filterChecked} 件はいずれも発火し得ます。` +
      '（**Grafana が受理するかは本検査の対象外**。配備時に /api/v1/provisioning/alert-rules を確かめること）',
    );
    return 0;
  }
  console.error(`[check-grafana-alerting] 違反 ${issues.length} 件:`);
  for (const i of issues) console.error(`  - ${i}`);
  return 1;
}

module.exports = {
  findIssues, promAlertNames, grafanaRuleTitles, extractK8sInline, selfTest,
  expressionValueSet, evaluatorSet, grafanaRuleConditions, filterEvaluatorIssues,
};

if (require.main === module) process.exit(main(process.argv.slice(2)));
