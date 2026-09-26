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
 *      compose と k8s の inline の両方を見る。式・評価器を読めないルールは違反にする）。
 *      #1588: 本検査が**積極的に読める形**だけを値の集合へ写し、読めない式（比較を包む算術・集約・関数、
 *      一覧に無い関数など）は「任意の値」へ倒さず**報告する**（意図して残すものは UNVERIFIABLE_ALLOWLIST へ
 *      理由つきで）。評価器とクエリは `condition` → threshold → `expression` の refId の鎖で辿り、
 *      間に math などの段があれば報告する
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
// 6. 式の絞り込みと評価器の組み合わせ（#1577 / #1588 / NFR-21）
//
// Grafana 版は閾値を `expr` ではなく `conditions[].evaluator` に持つ（#1110）。
// そのため Prometheus 版の式（`up == 0`）をそのまま写すと、**絞り込みの後に残る値は 0** であり、
// 評価器 `gt 0` は `0 > 0` で偽になる —— **構文として正当なまま永久に発火しない**
// （`OtelCollectorDown` と `ServiceRequestMetricsAbsent` が #1577 までこの形だった。
//  #1544 の `ResetFloorNoReadyEndpoint` は最初から `up` を生で取り `lt 1` で比べている）。
//
// 見ること: 評価器が読むクエリの式が**発火側で出し得る値の集合**を区間の和で求め、評価器を満たす値の
// 集合と**交わらなければ**違反にする。
//
// 🔴 #1588: **解釈できない式を「任意の値」として黙って通さない。** #1577 の初版は、読めない形を ALL
//    （値を縛らない）へ倒していた —— `(up == 0) * 1`・`up == 0 + 0`・`max(up == 0)`・`clamp_max(up, 0)`・
//    `vector(0)`・`up == 0 or vector(0)`・16 進の定数は、どれも永久に発火しないのに緑だった。
//    いまは**本検査が積極的に読める形だけ**を値の集合へ写し、それ以外は「検証できない」として**報告する**。
//    意図して残す形は `UNVERIFIABLE_ALLOWLIST` へ**理由つきで**載せる（載せたのに検証できる／ルールが無い
//    項目は違反にする。許可リストを腐らせない）。
//
// ★ 読める形（PromQL の優先順位に従う。低い順に `or` → `and` / `unless` → 比較 → `+ -` → `* / % atan2` → `^`）:
//   - `or`: 各辺の値の和集合。`and` / `unless`: **左辺の値が残る**（右辺は存在で絞るだけで値を決めない）。
//   - 括弧: 式全体を包む括弧は剥がして読み直す。
//   - 最上位の比較（1 つだけ。連鎖は報告）:
//       `bool` つき → 値は {0, 1}（絞り込みではない）。
//       片辺が定数（10 進・16 進・指数・Inf / NaN・定数どうしの算術）→ 他辺の値 ∩ 絞り込みの区間。
//       両辺がベクタ（`on` / `ignoring` / `group_*` を含む）→ 左辺の値が残る（右辺は値を決めない）。
//   - 生の選択子（`m{…}`・範囲 `[5m]`・`offset`・`@`）→ 任意の値。
//   - `absent(…)` / `absent_over_time(…)` → {1}。`vector(定数)` → {定数}。
//     `clamp_max` / `clamp_min` / `clamp` → 引数の値の集合を切り詰めた集合（厳密に写す）。
//   - `PASS_THROUGH_FUNCTIONS` の集約・関数と、`+ - * /`（定数は 0 でない有限値）の算術 ——
//     **引数・辺がすべて「任意の値」で、比較を含まないときだけ**任意の値として読む。
// ★ 報告する形（検証できない）: 算術・集約・関数が**比較を包む**形（`(up == 0) * 1`・`max(up == 0)`）、
//   値が縛られた辺に算術・集約を掛ける形（`vector(0) * 2`）、`% ^ atan2`、単項の符号を掛けたベクタ、
//   一覧に無い関数・集約（`count`・`abs` のように値の範囲を縛り得るもの）、その他の読めない形。
// ★ 近似として受容するもの（偽陰性。偽陽性は出さない側）: `PASS_THROUGH_FUNCTIONS` の中にも値の範囲を持つもの
//   がある（`rate` / `increase` は 0 以上）。「任意の値」は上位集合なので偽陽性は出ないが、`lt 0` で比べる
//   ような形は見逃す。そういう評価器は実在しないので、一覧を細かく割らない。
// ★ `condition` と refId を突き合わせる: `condition` → threshold（評価器 1 件）→ `expression` → クエリ、を
//   refId で辿る。間に math / reduce などの段があれば**検証できない**として報告する（今のルールには無い）。
// ★ fail-closed: 式・評価器・refId の鎖を読めないルールは違反にする（読めないまま素通りさせない）。
// ---------------------------------------------------------------------------

/**
 * 検証できない式を意図して残すルール（title → 理由）。**レビューを経て載せること。**
 * 🔴 #1588 の時点で空である —— 実データの 20 件はすべて本検査が積極的に読める。
 */
const UNVERIFIABLE_ALLOWLIST = Object.freeze({});

/**
 * 引数・辺がすべて「任意の値」のとき、出力も任意の値として読んでよい集約・関数。
 * 🔴 **値の範囲を縛るもの（`count`・`abs`・`sgn`・`scalar`・`time` など）は入れない**（入れると見逃しになる）。
 */
const PASS_THROUGH_FUNCTIONS = new Set([
  'sum', 'avg', 'min', 'max', 'topk', 'bottomk', 'quantile',
  'rate', 'irate', 'increase', 'delta', 'idelta', 'deriv', 'predict_linear',
  'histogram_quantile',
  'sum_over_time', 'avg_over_time', 'min_over_time', 'max_over_time', 'last_over_time', 'quantile_over_time',
  'label_replace', 'label_join', 'sort', 'sort_desc',
]);
/** `by` / `without` を取れる集約。 */
const AGGREGATIONS = new Set(['sum', 'avg', 'min', 'max', 'topk', 'bottomk', 'quantile', 'count', 'group', 'stddev', 'stdvar', 'count_values']);
const ABSENT_FUNCTIONS = new Set(['absent', 'absent_over_time']);
/** 選択子の名前として読んではならない語。 */
const PROMQL_KEYWORDS = new Set([
  'bool', 'on', 'ignoring', 'group_left', 'group_right', 'by', 'without', 'offset',
  'and', 'or', 'unless', 'atan2', 'inf', 'nan',
]);

const INF = Number.POSITIVE_INFINITY;

/** 区間 { lo, loInc, hi, hiInc }。点は lo === hi かつ両端を含む。 */
const interval = (lo, loInc, hi, hiInc) => ({ lo, loInc, hi, hiInc });
const point = (v) => interval(v, true, v, true);
const ALL = [interval(-INF, false, INF, false)];
const isAll = (xs) => xs.some((x) => x.lo === -INF && x.hi === INF);

/** 2 区間の交わり（無ければ null）。 */
function intersectInterval(a, b) {
  const lo = Math.max(a.lo, b.lo);
  const hi = Math.min(a.hi, b.hi);
  if (Number.isNaN(lo) || Number.isNaN(hi) || lo > hi) return null;
  const loInc = (a.lo === lo ? a.loInc : true) && (b.lo === lo ? b.loInc : true);
  const hiInc = (a.hi === hi ? a.hiInc : true) && (b.hi === hi ? b.hiInc : true);
  if (lo === hi && !(loInc && hiInc)) return null;
  return interval(lo, loInc, hi, hiInc);
}

/** 2 区間が交わるか。 */
const intersects = (a, b) => intersectInterval(a, b) !== null;

/** 区間の集合どうしが交わるか。 */
const setsIntersect = (xs, ys) => xs.some((x) => ys.some((y) => intersects(x, y)));

/** 区間の集合どうしの交わり。 */
const intersectSets = (xs, ys) => xs.flatMap((x) => ys.map((y) => intersectInterval(x, y)).filter(Boolean));

/** 区間の集合の和（ALL を含めば ALL に畳む）。 */
function unionSets(...sets) {
  const u = sets.flat();
  return isAll(u) ? ALL : u;
}

/** 比較演算子と数値 c から、絞り込みの後に残り得る値の集合を返す。 */
function filterSet(op, c) {
  if (Number.isNaN(c)) return op === '!=' ? ALL : []; // NaN との比較は != だけが真
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

/** 深さ 0 のカンマで分ける（関数の引数）。 */
function splitArgs(s) {
  if (s.trim() === '') return [];
  const out = [];
  let depth = 0;
  let start = 0;
  for (let i = 0; i < s.length; i++) {
    if ('([{'.includes(s[i])) depth++;
    else if (')]}'.includes(s[i])) depth--;
    else if (depth === 0 && s[i] === ',') { out.push(s.slice(start, i).trim()); start = i + 1; }
  }
  out.push(s.slice(start).trim());
  return out;
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

/** 数値リテラル（10 進・指数・16 進・Inf・NaN。符号つき）。 */
const NUMBER_RE = /^[-+]?(?:0x[0-9a-f]+|inf|nan|(?:\d+(?:\.\d*)?|\.\d+)(?:e[-+]?\d+)?)$/i;
function parseNumber(text) {
  const t = text.trim();
  if (!NUMBER_RE.test(t)) return null;
  const sign = t.startsWith('-') ? -1 : 1;
  const body = t.replace(/^[-+]/, '').toLowerCase();
  if (body.startsWith('0x')) return sign * parseInt(body.slice(2), 16);
  if (body === 'inf') return sign * INF;
  if (body === 'nan') return Number.NaN;
  return sign * Number(body);
}

const FLIP = { '==': '==', '!=': '!=', '>': '<', '<': '>', '>=': '<=', '<=': '>=' };

/** 深さ 0 の比較演算子をすべて返す。 */
function topLevelComparisons(s) {
  let depth = 0;
  const found = [];
  for (let i = 0; i < s.length; i++) {
    const ch = s[i];
    if ('([{'.includes(ch)) { depth++; continue; }
    if (')]}'.includes(ch)) { depth--; continue; }
    if (depth !== 0) continue;
    const two = s.slice(i, i + 2);
    if (['==', '!=', '>=', '<='].includes(two)) { found.push({ at: i, op: two }); i++; continue; }
    if (ch === '>' || ch === '<') found.push({ at: i, op: ch });
  }
  return found;
}

/** 二項の `+` / `-` か（単項・指数の符号・`offset -5m` を除く）。 */
function isBinarySign(s, i) {
  let j = i - 1;
  while (j >= 0 && /\s/.test(s[j])) j--;
  if (j < 0 || !/[A-Za-z0-9_\])}."']/.test(s[j])) return false;
  const before = s.slice(0, j + 1);
  if (/(?:^|[^A-Za-z0-9_:.])(?:\d+(?:\.\d*)?|\.\d+)e$/i.test(before)) return false; // 1e-5
  const word = (before.match(/([A-Za-z_]+)$/) || [])[1];
  return !(word && PROMQL_KEYWORDS.has(word.toLowerCase()) && !['inf', 'nan'].includes(word.toLowerCase()));
}

/** 深さ 0 の算術演算子を、優先順位の段（add / mul / pow）ごとに返す。 */
function topLevelArith(s, level) {
  let depth = 0;
  const found = [];
  for (let i = 0; i < s.length; i++) {
    const ch = s[i];
    if ('([{'.includes(ch)) { depth++; continue; }
    if (')]}'.includes(ch)) { depth--; continue; }
    if (depth !== 0) continue;
    if (level === 'add' && (ch === '+' || ch === '-') && isBinarySign(s, i)) found.push({ at: i, op: ch, len: 1 });
    if (level === 'mul') {
      if ('*/%'.includes(ch)) found.push({ at: i, op: ch, len: 1 });
      else if (/^atan2\b/i.test(s.slice(i)) && (i === 0 || !/[A-Za-z0-9_:]/.test(s[i - 1]))) {
        found.push({ at: i, op: 'atan2', len: 5 });
        i += 4;
      }
    }
    if (level === 'pow' && ch === '^') found.push({ at: i, op: '^', len: 1 });
  }
  return found;
}

/** ベクタどうしの照合の修飾（`on (…)` / `ignoring (…)` / `group_left (…)`）を剥がす。 */
function stripMatchingModifiers(text) {
  return text.trim()
    .replace(/^(?:on|ignoring)\s*\([^()]*\)\s*/i, '')
    .replace(/^(?:group_left|group_right)\s*(?:\([^()]*\))?\s*/i, '')
    .trim();
}

/** 二項演算を 1 回だけ割る（加減・乗除は左結合なので最後、冪は右結合なので最初）。 */
function splitBinaryArith(s) {
  for (const level of ['add', 'mul', 'pow']) {
    const ops = topLevelArith(s, level);
    if (ops.length === 0) continue;
    const o = level === 'pow' ? ops[0] : ops[ops.length - 1];
    return { op: o.op, left: s.slice(0, o.at), right: stripMatchingModifiers(s.slice(o.at + o.len)) };
  }
  return null;
}

/** 定数式の値（数値リテラルと、定数どうしの算術・単項の符号）。定数でなければ null。 */
function constantValue(text) {
  const s = unwrapParens(text);
  const n = parseNumber(s);
  if (n !== null) return n;
  if (s === '') return null;
  const b = splitBinaryArith(s);
  if (b) {
    const l = constantValue(b.left);
    const r = constantValue(b.right);
    if (l === null || r === null) return null;
    switch (b.op) {
      case '+': return l + r;
      case '-': return l - r;
      case '*': return l * r;
      case '/': return l / r;
      case '%': return l % r;
      case '^': return l ** r;
      case 'atan2': return Math.atan2(l, r);
      default: return null;
    }
  }
  if (/^[-+]/.test(s)) {
    const v = constantValue(s.slice(1));
    return v === null ? null : (s[0] === '-' ? -v : v);
  }
  return null;
}

const SELECTOR_RE = /^(?:[A-Za-z_:][A-Za-z0-9_:]*\s*(?:\{[^{}]*\})?|\{[^{}]*\})\s*(?:\[[^[\]]*\])?\s*(?:offset\s+-?[0-9a-z]+\s*)?(?:@\s*\S+\s*)?$/i;
function isSelector(s) {
  if (!SELECTOR_RE.test(s)) return false;
  const name = (s.match(/^[A-Za-z_:][A-Za-z0-9_:]*/) || [''])[0].toLowerCase();
  return !PROMQL_KEYWORDS.has(name);
}

/** `name [by|without (…)] (args) [by|without (…)]` を読む。関数呼び出しでなければ null。 */
function parseCall(s) {
  const m = s.match(/^([A-Za-z_][A-Za-z0-9_]*)\s*/);
  if (!m) return null;
  let i = m[0].length;
  let grouping = null;
  const g1 = s.slice(i).match(/^(by|without)\s*\([^()]*\)\s*/i);
  if (g1) { i += g1[0].length; grouping = g1[1]; }
  if (s[i] !== '(') return null;
  let depth = 0;
  let j = i;
  for (; j < s.length; j++) {
    if ('([{'.includes(s[j])) depth++;
    else if (')]}'.includes(s[j])) { depth--; if (depth === 0) break; }
  }
  if (j >= s.length) return null;
  const tail = s.slice(j + 1).trim();
  if (tail !== '') {
    const g2 = tail.match(/^(by|without)\s*\([^()]*\)$/i);
    if (!g2 || grouping) return null;
    grouping = g2[1];
  }
  return { name: m[1].toLowerCase(), args: splitArgs(s.slice(i + 1, j)), grouping };
}

const ok = (set, cmp = false) => ({ set, cmp, why: null });
const unverifiable = (why) => ({ set: null, cmp: true, why });
const clip = (s) => (s.length > 60 ? `${s.slice(0, 60)}…` : s);

const clampMax = (set, c) => {
  const kept = intersectSets(set, [interval(-INF, false, c, true)]);
  return intersectSets(set, [interval(c, false, INF, false)]).length > 0 ? [...kept, point(c)] : kept;
};
const clampMin = (set, c) => {
  const kept = intersectSets(set, [interval(c, true, INF, false)]);
  return intersectSets(set, [interval(-INF, false, c, false)]).length > 0 ? [...kept, point(c)] : kept;
};

function analyzeCall({ name, args, grouping }) {
  if (grouping && !AGGREGATIONS.has(name)) return unverifiable(`${name}(…) に ${grouping} は付けられない`);
  if (ABSENT_FUNCTIONS.has(name)) {
    return args.length === 1 ? ok([point(1)]) : unverifiable(`${name}(…) の引数が 1 つではない`);
  }
  if (name === 'vector') {
    const c = args.length === 1 ? constantValue(args[0]) : null;
    return c === null ? unverifiable('vector(…) の引数が定数ではない') : ok([point(c)]);
  }
  if (name === 'clamp_max' || name === 'clamp_min' || name === 'clamp') {
    const want = name === 'clamp' ? 3 : 2;
    const bounds = args.slice(1).map(constantValue);
    if (args.length !== want || bounds.some((b) => b === null || Number.isNaN(b))) {
      return unverifiable(`${name}(…) の境界が定数ではない`);
    }
    const v = analyze(args[0]);
    if (v.why) return v;
    if (name === 'clamp_max') return ok(clampMax(v.set, bounds[0]), v.cmp);
    if (name === 'clamp_min') return ok(clampMin(v.set, bounds[0]), v.cmp);
    if (bounds[0] > bounds[1]) return ok([], v.cmp); // min > max は空を返す
    return ok(clampMax(clampMin(v.set, bounds[0]), bounds[1]), v.cmp);
  }
  if (PASS_THROUGH_FUNCTIONS.has(name)) {
    for (const a of args) {
      if (a === '""' || constantValue(a) !== null) continue;
      const r = analyze(a);
      if (r.why) return r;
      if (r.cmp) return unverifiable(`${name}(…) が比較を包んでいる —— 比較の後に残る値を集約・関数が変えるので解釈しない`);
      if (!isAll(r.set)) {
        return unverifiable(`${name}(…) の引数の値が縛られている（${describeSet(r.set)}）—— 集約・関数の後の値の集合は解釈しない`);
      }
    }
    return ok(ALL);
  }
  return unverifiable(`関数・集約 ${name}(…) は本検査が読める一覧（PASS_THROUGH_FUNCTIONS ほか）に無い —— 値の範囲を縛り得る`);
}

/**
 * 式が発火側で出し得る値の集合を解析する。純関数（自己試験から直接呼ぶ）。
 * 戻り値: { set, cmp, why } —— `why` が null でなければ**検証できない**（その理由）。
 * `cmp` は式のどこかに比較（`bool` を含む）があるか（算術・集約が比較を包む形を見分けるのに使う）。
 */
function analyze(expr) {
  const s = unwrapParens(expr);
  if (s === '') return unverifiable('空の式');

  const orParts = splitTopLevel(s, /^or\b/i);
  if (orParts.length > 1) {
    const rs = orParts.map(analyze);
    const bad = rs.find((r) => r.why);
    return bad || ok(unionSets(...rs.map((r) => r.set)), rs.some((r) => r.cmp));
  }
  const andParts = splitTopLevel(s, /^(?:and|unless)\b/i);
  if (andParts.length > 1) return analyze(andParts[0]);

  const cmps = topLevelComparisons(s);
  if (cmps.length > 1) return unverifiable(`最上位の比較が ${cmps.length} 個ある（連鎖した比較は解釈しない）`);
  if (cmps.length === 1) {
    const { at, op } = cmps[0];
    const lhs = s.slice(0, at);
    let rhs = s.slice(at + op.length).trim();
    if (/^bool\b/i.test(rhs)) return ok([point(0), point(1)], true);
    rhs = stripMatchingModifiers(rhs);
    const lc = constantValue(lhs);
    const rc = constantValue(rhs);
    if (lc !== null && rc !== null) return unverifiable('両辺が定数の比較（bool なしでは PromQL として成り立たない）');
    if (rc !== null || lc !== null) {
      const side = analyze(rc !== null ? lhs : rhs);
      if (side.why) return side;
      return ok(intersectSets(side.set, rc !== null ? filterSet(op, rc) : filterSet(FLIP[op], lc)), true);
    }
    // ベクタどうし: 左辺の値が残る（右辺は値を決めない）。右辺も読める形であることは求める
    // （`bool` の読み取りが外れたとき `bool 0` を黙ってベクタとして通さない）。
    const left = analyze(lhs);
    if (left.why) return left;
    const right = analyze(rhs);
    return right.why ? right : ok(left.set, true);
  }

  const c = constantValue(s);
  if (c !== null) return ok([point(c)]);

  const b = splitBinaryArith(s);
  if (b) {
    const sides = [b.left, b.right].map((t) => ({ t, c: constantValue(t) }));
    const vecs = sides.filter((x) => x.c === null).map((x) => analyze(x.t));
    const bad = vecs.find((r) => r.why);
    if (bad) return bad;
    if (vecs.some((r) => r.cmp)) {
      return unverifiable(`算術（${b.op}）が比較を包んでいる —— 比較の後に残る値を算術が変えるので解釈しない`);
    }
    if (vecs.some((r) => !isAll(r.set))) {
      return unverifiable(`算術（${b.op}）の辺の値が縛られている —— 算術の後の値の集合は解釈しない`);
    }
    if (!['+', '-', '*', '/'].includes(b.op)) return unverifiable(`算術（${b.op}）は値の範囲を縛り得るので解釈しない`);
    const consts = sides.filter((x) => x.c !== null).map((x) => x.c);
    if (consts.some((k) => !Number.isFinite(k) || (k === 0 && (b.op === '*' || b.op === '/')))) {
      return unverifiable(`算術（${b.op}）の定数が 0 か有限でない —— 値が 1 点へ潰れ得る`);
    }
    return ok(ALL);
  }

  if (/^[-+]/.test(s)) return unverifiable('単項の符号を掛けたベクタは解釈しない');
  if (isSelector(s)) return ok(ALL);
  const call = parseCall(s);
  if (call) return analyzeCall(call);
  return unverifiable(`読めない形: ${clip(s)}`);
}

/**
 * 式が発火側で出し得る値の集合を返す。**検証できないなら null**。
 * 純関数（自己試験から直接呼ぶ）。
 */
function expressionValueSet(expr) {
  const r = analyze(stripPromqlNoise(expr));
  return r.why ? null : r.set;
}

/** 検証できない理由（検証できるなら null）。 */
function expressionUnverifiableReason(expr) {
  return analyze(stripPromqlNoise(expr)).why;
}

/** YAML の 1 行スカラーの引用符を剥がす。 */
function unquoteYamlScalar(raw) {
  const v = raw.trim();
  if (v.length >= 2 && v.startsWith("'") && v.endsWith("'")) return v.slice(1, -1).replace(/''/g, "'");
  if (v.length >= 2 && v.startsWith('"') && v.endsWith('"')) return v.slice(1, -1).replace(/\\(["\\])/g, '$1');
  return v;
}

/** 行の並びから `expr:` を読む（1 行・引用符あり／なし・`|` / `>` のブロック）。 */
function readExprs(body) {
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
  return exprs;
}

/** 行の並びから評価器（`evaluator: { type, params }`）を読む。 */
function readEvaluators(body) {
  return [...body.join('\n').matchAll(
    /evaluator:\s*\{\s*type:\s*([A-Za-z_]+)\s*,\s*params:\s*\[([^\]]*)\]\s*\}/g,
  )].map((m) => ({
    type: m[1],
    params: m[2].split(',').map((x) => x.trim()).filter((x) => x !== '').map(Number),
  }));
}

/** 行の並びから、行独立の `key: value` の最初の値を読む（無ければ null）。 */
function readScalar(body, key) {
  for (const l of body) {
    const m = l.match(new RegExp(`^\\s*(?:-\\s*)?${key}:\\s*(\\S.*?)\\s*$`));
    if (m) return unquoteYamlScalar(m[1]);
  }
  return null;
}

/** ルール本体の `data:` をノード（refId ごと）に切る。 */
function readDataNodes(body) {
  const d = body.findIndex((l) => /^\s*data:\s*$/.test(l));
  if (d < 0) return [];
  const dataIndent = body[d].match(/^\s*/)[0].length;
  let itemIndent = null;
  const items = [];
  for (let i = d + 1; i < body.length; i++) {
    const l = body[i];
    if (l.trim() === '') continue;
    const ind = l.match(/^\s*/)[0].length;
    if (ind <= dataIndent && !(ind === dataIndent && /^\s*-\s/.test(l))) break;
    if (itemIndent === null && /^\s*-\s/.test(l)) itemIndent = ind;
    if (ind === itemIndent && /^\s*-\s/.test(l)) items.push([]);
    if (items.length > 0) items[items.length - 1].push(l);
  }
  return items.map((lines) => ({
    refId: readScalar(lines, 'refId'),
    datasourceUid: readScalar(lines, 'datasourceUid'),
    type: readScalar(lines, 'type'),
    expression: readScalar(lines, 'expression'),
    exprs: readExprs(lines),
    evaluators: readEvaluators(lines),
  }));
}

/**
 * Grafana provisioning をルール単位に切り、各ルールの condition と data のノードを読む。
 * 1 ルール = `title:` の行から次の `title:` の行まで（コメント行は除く）。
 * 互換のため、ルール全体の exprs / evaluators も返す。
 */
function grafanaRuleConditions(text) {
  const lines = text.split('\n').filter((l) => !/^\s*#/.test(l));
  const starts = [];
  lines.forEach((l, i) => { if (/^\s*(?:-\s*)?title:\s*\S+\s*$/.test(l)) starts.push(i); });
  return starts.map((start, k) => {
    const body = lines.slice(start, k + 1 < starts.length ? starts[k + 1] : lines.length);
    const title = body[0].replace(/^\s*(?:-\s*)?title:\s*/, '').trim();
    return {
      title,
      condition: readScalar(body, 'condition'),
      nodes: readDataNodes(body),
      exprs: readExprs(body),
      evaluators: readEvaluators(body),
    };
  });
}

/**
 * `condition` → threshold → `expression` → クエリ、を refId で辿る。
 * 戻り値: { error } か { unverifiable } か { expr, evaluator }。
 */
function resolveConditionChain({ condition, nodes }) {
  if (!condition) return { error: 'condition を読めない' };
  const byRef = new Map();
  for (const n of nodes) {
    if (!n.refId) return { error: 'refId を持たない data の要素がある' };
    if (byRef.has(n.refId)) return { error: `refId ${n.refId} が重複している` };
    byRef.set(n.refId, n);
  }
  const cond = byRef.get(condition);
  if (!cond) return { error: `condition ${condition} を refId に持つ data が無い（refIds: ${[...byRef.keys()].join(', ') || 'なし'}）` };
  if (!BUILTIN_DATASOURCE_UIDS.has(cond.datasourceUid) || cond.type !== 'threshold') {
    return { unverifiable: `condition ${condition} が threshold の式ではない（datasourceUid=${cond.datasourceUid} / type=${cond.type}）` };
  }
  if (cond.evaluators.length !== 1) {
    return { error: `condition ${condition} の評価器（evaluator: { type, params }）を ${cond.evaluators.length} 件読んだ（1 件であること）` };
  }
  if (!cond.expression) return { error: `condition ${condition} の expression を読めない` };
  const src = byRef.get(cond.expression);
  if (!src) return { error: `threshold ${condition} の expression ${cond.expression} を refId に持つ data が無い` };
  if (BUILTIN_DATASOURCE_UIDS.has(src.datasourceUid)) {
    return { unverifiable: `クエリと threshold の間に ${src.type || '不明'} の段（refId ${src.refId}）がある —— 段を通った後の値は解釈しない` };
  }
  if (src.exprs.length !== 1) return { error: `refId ${src.refId} の expr を ${src.exprs.length} 件読んだ（1 件であること）` };
  return { expr: src.exprs[0], evaluator: cond.evaluators[0] };
}

const describeSet = (xs) => (xs.length === 0 ? '∅（空。何も残らない）' : xs.map((x) => (x.lo === x.hi
  ? `${x.lo}`
  : `${x.loInc ? '[' : '('}${x.lo}, ${x.hi}${x.hiInc ? ']' : ')'}`)).join(' ∪ '));

/**
 * 6 の本体。`label` は違反文に付ける写しの名前（compose / k8s inline）。
 * 戻り値: { issues, checked, allowlisted }（checked は組み合わせを判定できたルール数。0 件走査の門に使う）。
 */
function filterEvaluatorIssues(text, label, allowlist = UNVERIFIABLE_ALLOWLIST) {
  const issues = [];
  const allowlisted = [];
  let checked = 0;
  const rules = grafanaRuleConditions(text);
  const report = (title, why) => {
    if (Object.prototype.hasOwnProperty.call(allowlist, title)) { allowlisted.push(title); return; }
    issues.push(
      `[${label}] ルール ${title}: 式を検証できない（${why}）。` +
      '読める形へ書き直すか、UNVERIFIABLE_ALLOWLIST へ理由つきで載せること（#1588。読めないまま素通りさせない）',
    );
  };
  for (const rule of rules) {
    const { title } = rule;
    const chain = resolveConditionChain(rule);
    if (chain.error) { issues.push(`[${label}] ルール ${title}: ${chain.error}（読めないまま素通りさせない）`); continue; }
    if (chain.unverifiable) { report(title, chain.unverifiable); continue; }
    const { type, params } = chain.evaluator;
    const want = evaluatorSet(type, params);
    if (want === null) {
      issues.push(`[${label}] ルール ${title}: 評価器 ${type} [${params.join(', ')}] を解釈できない（型を本検査へ足すこと）`);
      continue;
    }
    const why = expressionUnverifiableReason(chain.expr);
    if (why) { report(title, why); continue; }
    if (Object.prototype.hasOwnProperty.call(allowlist, title)) {
      issues.push(`[${label}] ルール ${title}: UNVERIFIABLE_ALLOWLIST に載っているが検証できる —— 許可リストから外すこと（許可リストを腐らせない）`);
    }
    const got = expressionValueSet(chain.expr);
    checked++;
    if (!setsIntersect(got, want)) {
      issues.push(
        `[${label}] ルール ${title}: expr の絞り込みの後に残る値は ${describeSet(got)} だが、` +
        `評価器 ${type} [${params.join(', ')}] はその値で真にならない —— 永久に発火しない（#1577）。` +
        '絞り込みを外して生の値を評価器で比べること（例: `up` を `lt 1`）',
      );
    }
  }
  const titles = new Set(rules.map((r) => r.title));
  for (const t of Object.keys(allowlist)) {
    if (!titles.has(t)) issues.push(`[${label}] UNVERIFIABLE_ALLOWLIST のルール ${t} が provisioning に無い —— 許可リストから外すこと`);
  }
  return { issues, checked, allowlisted };
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
    filterAllowlisted: composeFilter.allowlisted,
  };
}

function selfTest() {
  const assert = require('assert');
  const base = {
    prom: '      - alert: Foo\n      - alert: Bar\n',
    grafana:
      'apiVersion: 1\ngroups:\n  - rules:\n' +
      '      - uid: foo\n        title: Foo\n        condition: C\n        noDataState: NoData\n        execErrState: Error\n        data:\n          - refId: A\n            datasourceUid: prometheus\n' +
      "            model:\n              refId: A\n              expr: 'up{job=\"x\"}'\n" +
      '          - refId: C\n            datasourceUid: __expr__\n            model:\n              refId: C\n              type: threshold\n              expression: A\n' +
      '              conditions:\n                - evaluator: { type: lt, params: [1] }\n' +
      '      - uid: bar\n        title: Bar\n        condition: C\n        noDataState: NoData\n        execErrState: Error\n        data:\n          - refId: A\n            datasourceUid: prometheus\n' +
      '            model:\n              expr: |\n                sum by (job) (rate(m[5m])) > 0\n' +
      '          - refId: C\n            datasourceUid: __expr__\n            model:\n              type: threshold\n              expression: A\n' +
      '              conditions:\n                - evaluator: { type: gt, params: [0] }\n',
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

  // ---- 6 の見逃しを埋める（#1588）----
  const unverifiableFor = (r, title = 'Foo') => r.issues.some((x) => x.startsWith(`[compose] ルール ${title}:`) && x.includes('式を検証できない'));

  // 🔴 bool の読み取りを消すと `bool 0` が読めない右辺になり「検証できない」へ倒れる —— どちらにしても本件は赤になる。
  t('bool は値を {0, 1} に固定する: x == bool 0 と gt 1 の組み合わせを検出する（#1588・変異試験）', () => {
    const r = findIssues(withRule('up{job="x"} == bool 0', '{ type: gt, params: [1] }'));
    assert.ok(neverFires(r), JSON.stringify(r.issues));
    assert.deepStrictEqual(expressionValueSet('up == bool 0'), [point(0), point(1)]);
  });

  t('右辺の定数の算術（up == 0 + 0）と gt 0 を検出する（#1588・変異試験）', () =>
    assert.ok(neverFires(findIssues(withRule('up{job="x"} == 0 + 0', '{ type: gt, params: [0] }')))));

  t('16 進の定数（up == 0x0）と gt 0 を検出する（#1588・変異試験）', () =>
    assert.ok(neverFires(findIssues(withRule('up{job="x"} == 0x0', '{ type: gt, params: [0] }')))));

  t('定数ベクタ vector(0) と、or vector(0) で 0 だけを足した形を gt 0 で検出する（#1588・変異試験）', () => {
    assert.ok(neverFires(findIssues(withRule('vector(0)', '{ type: gt, params: [0] }'))));
    assert.ok(neverFires(findIssues(withRule('up{job="x"} == 0 or vector(0)', '{ type: gt, params: [0] }'))));
  });

  t('clamp_max(up, 0) と gt 0 を検出する（関数が値を縛る形・#1588・変異試験）', () =>
    assert.ok(neverFires(findIssues(withRule('clamp_max(up{job="x"}, 0)', '{ type: gt, params: [0] }')))));

  t('比較の上に算術が乗る形（(up == 0) * 1）は「検証できない」と報告する（#1588・変異試験）', () => {
    const r = findIssues(withRule('(up{job="x"} == 0) * 1', '{ type: gt, params: [0] }'));
    assert.ok(unverifiableFor(r), JSON.stringify(r.issues));
  });

  t('集約・関数が比較を包む形（max(up == 0)・sum(x == bool 0)）は「検証できない」と報告する（#1588・変異試験）', () => {
    for (const e of ['max(up{job="x"} == 0)', 'sum(up{job="x"} == bool 0)']) {
      const r = findIssues(withRule(e, '{ type: gt, params: [0] }'));
      assert.ok(unverifiableFor(r), `${e}: ${JSON.stringify(r.issues)}`);
    }
  });

  t('一覧に無い関数・値を縛り得る算術・連鎖した比較は「検証できない」と報告する（任意の値へ倒さない・#1588）', () => {
    for (const e of ['count(up{job="x"})', 'abs(up{job="x"})', 'up{job="x"} % 2', '-up{job="x"}', 'up{job="x"} > 1 < 5', 'rate(m[5m]) * 0']) {
      assert.strictEqual(expressionValueSet(e), null, e);
      assert.ok(unverifiableFor(findIssues(withRule(e, '{ type: gt, params: [0] }'))), e);
    }
  });

  t('読める形の陰性対照: 比較の下の算術・集約（実データの DepartmentSyncNotCorrecting / HighHttp5xxRate の形）は任意の値', () => {
    for (const e of [
      '(sum(increase(a[1h])) or vector(0)) + (sum(increase(b[1h])) or vector(0))',
      'sum by (job) (rate(m{c=~"5.."}[5m])) / sum by (job) (rate(m[5m]))',
      'histogram_quantile(0.95, sum by (le) (rate(h_bucket[5m])))',
      'm offset 15m', 'absent_over_time(m[2h])',
    ]) {
      const got = expressionValueSet(e);
      assert.ok(got !== null, `${e}: ${expressionUnverifiableReason(e)}`);
    }
  });

  t('UNVERIFIABLE_ALLOWLIST: 載せたルールの「検証できない」は黙る。載せたのに検証できる／ルールが無い項目は違反（#1588）', () => {
    const g = withRule('max(up{job="x"} == 0)', '{ type: gt, params: [0] }').grafana;
    const allowed = filterEvaluatorIssues(g, 'compose', { Foo: 'レビュー済みの理由' });
    assert.deepStrictEqual(allowed.issues, []);
    assert.deepStrictEqual(allowed.allowlisted, ['Foo']);
    const stale = filterEvaluatorIssues(base.grafana, 'compose', { Foo: '理由' });
    assert.ok(stale.issues.some((x) => x.includes('許可リストから外す')), JSON.stringify(stale.issues));
    const missing = filterEvaluatorIssues(base.grafana, 'compose', { Nope: '理由' });
    assert.ok(missing.issues.some((x) => x.includes('Nope') && x.includes('provisioning に無い')), JSON.stringify(missing.issues));
  });

  // ---- condition と refId の突き合わせ（#1588）----
  const chainIssue = (grafana, needle) => {
    const r = findIssues({ ...base, grafana, k8sInline: grafana });
    return r.issues.some((x) => x.startsWith('[compose] ルール Foo:') && x.includes(needle)) ? r : (() => { throw new Error(`${needle}: ${JSON.stringify(r.issues)}`); })();
  };
  const fooEnd = base.grafana.indexOf('      - uid: bar');
  const mutateFoo = (fn) => fn(base.grafana.slice(0, fooEnd)) + base.grafana.slice(fooEnd);

  t('condition が存在しない refId を指すことを検出する（#1588・変異試験）', () =>
    chainIssue(mutateFoo((f) => f.replace('condition: C', 'condition: B')), 'condition B を refId に持つ data が無い'));

  t('threshold の expression が存在しない refId を指すことを検出する（#1588・変異試験）', () =>
    chainIssue(mutateFoo((f) => f.replace('expression: A', 'expression: Z')), 'expression Z を refId に持つ data が無い'));

  t('refId の重複を検出する（#1588・変異試験）', () =>
    chainIssue(mutateFoo((f) => f.replace('- refId: C', '- refId: A')), 'refId A が重複'));

  t('クエリと threshold の間の math の段は「検証できない」と報告する（#1588・変異試験）', () => {
    const g = mutateFoo((f) => f
      .replace('expression: A\n', 'expression: B\n')
      .replace(
        '          - refId: C\n',
        '          - refId: B\n            datasourceUid: __expr__\n            model:\n              type: math\n              expression: $A * 0\n          - refId: C\n',
      ));
    chainIssue(g, 'math の段（refId B）');
  });

  t('condition がクエリを直に指す（threshold でない）形は「検証できない」と報告する（#1588・変異試験）', () =>
    chainIssue(mutateFoo((f) => f.replace('condition: C', 'condition: A')), 'threshold の式ではない'));

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

  const { issues, promCount, grafanaCount, filterChecked, filterAllowlisted } = findIssues({
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
      `式の絞り込みと評価器の組み合わせ ${filterChecked} 件はいずれも発火し得ます` +
      `（検証できない式を許可リストで残したルール ${filterAllowlisted.length} 件${filterAllowlisted.length ? `: ${filterAllowlisted.join(', ')}` : ''}）。` +
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
  expressionValueSet, expressionUnverifiableReason, evaluatorSet, grafanaRuleConditions, filterEvaluatorIssues,
  resolveConditionChain, UNVERIFIABLE_ALLOWLIST, PASS_THROUGH_FUNCTIONS,
};

if (require.main === module) process.exit(main(process.argv.slice(2)));
