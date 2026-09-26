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
 *      間に math などの段があれば報告する。
 *      #1595: provisioning は YAML の木として読む（複数行に続く plain の expr・行末コメント・評価器の
 *      `{ params, type }` の順を読む。読めない YAML は違反）。決して値を返さない式（空集合）を健全に認識できる
 *      形だけ違反にし、同型で判定できない形は報告する。`or on (…)`・サブクエリ・`@` と `offset` の順・
 *      `:offset` で終わる名前・単項の符号と `^` の優先順位・±Inf・`expression: $A`・許可リストの空の理由も扱う
 *      （射程の詳細は下の「6.」の節）
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
  return [...text.matchAll(/^\s*-\s*alert:\s*(\S+)(?:\s+#.*)?\s*$/gm)].map((m) => m[1]);
}

/** Grafana provisioning のルール名（title）を拾う。コメント行は除く。 */
function grafanaRuleTitles(text) {
  // `- title: X`（配列要素の先頭に書く形）と `title: X`（行独立）の**両方**を拾う。
  // 片方だけにすると、書き方を変えただけで 0 件走査になる（#664 の教訓）。
  // 行末コメント（`title: X # …`）も読む（#1595。検査 6 の YAML の読み取りと食い違わせない）。
  return [...text.matchAll(/^\s*(?:-\s*)?title:\s*(\S+)(?:\s+#.*)?\s*$/gm)].map((m) => m[1]);
}

/**
 * provisioning が参照する datasourceUid を拾う。
 * **`- datasourceUid: X` と `datasourceUid: X` の両方**を拾う（title と同じ理由。
 * 片方だけにすると、書き方を変えただけで 0 件走査になる）。
 */
function referencedDatasourceUids(text) {
  return [...text.matchAll(/^\s*(?:-\s*)?datasourceUid:\s*(\S+)(?:\s+#.*)?\s*$/gm)].map((m) => m[1]);
}

/**
 * datasources.yaml が宣言する uid を拾う。
 * **字下げの深さを固定しない** —— `^\s{4}` のような決め打ちは、書式を整えただけで 0 件になる。
 * `jsonData` 配下の `datasourceUid:` とは**キー名が違う**ので取り違えない。
 */
function declaredDatasourceUids(text) {
  return [...text.matchAll(/^\s*(?:-\s*)?uid:\s*(\S+)(?:\s+#.*)?\s*$/gm)].map((m) => m[1]);
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
// 6. 式の絞り込みと評価器の組み合わせ（#1577 / #1588 / #1595 / NFR-21）
//
// Grafana 版は閾値を `expr` ではなく `conditions[].evaluator` に持つ（#1110）。
// そのため Prometheus 版の式（`up == 0`）をそのまま写すと、**絞り込みの後に残る値は 0** であり、
// 評価器 `gt 0` は `0 > 0` で偽になる —— **構文として正当なまま永久に発火しない**
// （`OtelCollectorDown` と `ServiceRequestMetricsAbsent` が #1577 までこの形だった。
//  #1544 の `ResetFloorNoReadyEndpoint` は最初から `up` を生で取り `lt 1` で比べている）。
//
// 見ること: 評価器が読むクエリの式が**発火側で出し得る値の集合**を区間の和で求め、評価器を満たす値の
// 集合と**交わらなければ**違反にする。**値の集合が空（式が決して値を返さない）なら、それも違反にする**（#1595）。
//
// 🔴 #1588: **解釈できない式を「任意の値」として黙って通さない。** #1577 の初版は、読めない形を ALL
//    （値を縛らない）へ倒していた —— `(up == 0) * 1`・`up == 0 + 0`・`max(up == 0)`・`clamp_max(up, 0)`・
//    `vector(0)`・`up == 0 or vector(0)`・16 進の定数は、どれも永久に発火しないのに緑だった。
//    いまは**本検査が積極的に読める形だけ**を値の集合へ写し、それ以外は「検証できない」として**報告する**。
//    意図して残す形は `UNVERIFIABLE_ALLOWLIST` へ**空でない理由つきで**載せる（載せたのに検証できる／ルールが無い／
//    理由が空の項目は違反にする。許可リストを腐らせない）。
//
// ★ 読める形（PromQL の優先順位に従う。低い順に `or` → `and` / `unless` → 比較 → `+ -` → `* / % atan2` → 単項の符号 → `^`）:
//   - `or`: 各辺の値の和集合。右辺の `on (…)` / `ignoring (…)` は剥がす（#1595。`x == 0 or on() vector(0)` の定番形）。
//   - `and` / `unless`: **左辺の値が残る**（右辺は存在で絞るだけ）。右辺も読める形であることを求める。空の扱いは下の「空集合」。
//   - 括弧: 式全体を包む括弧は剥がして読み直す。
//   - 最上位の比較（1 つだけ。連鎖は報告）:
//       `bool` つき → 値は {0, 1}（絞り込みではない。辺は読める形であること）。
//       片辺が定数（10 進・16 進・指数・Inf / NaN・定数どうしの算術）→ 他辺の値 ∩ 絞り込みの区間。
//       両辺がベクタ（`on` / `ignoring` / `group_*` を含む）→ 左辺の値が残る（右辺は値を決めない）。
//   - 生の選択子（`m{…}`・範囲 `[5m]`・`offset` と `@` は**どちらの順でも**）→ 任意の値。
//     名前が `:offset` / `:bool` のように語で終わるメトリクスも選択子として読む（#1595。語境界に `:` を含める）。
//   - サブクエリ（`<式>[30m:1m]`。関数呼び出し・括弧にも付く。後ろの `offset` / `@` を含む）→ 中の式の値（#1595）。
//   - `absent(…)` / `absent_over_time(…)` → {1}。`vector(定数)` → {定数}。
//     `clamp_max` / `clamp_min` / `clamp` → 引数の値の集合を切り詰めた集合（厳密に写す）。
//   - `PASS_THROUGH_FUNCTIONS` の集約・関数と、`+ - * /`（定数は 0 でない有限値）の算術 ——
//     **引数・辺がすべて「任意の値」で、比較を含まないときだけ**任意の値として読む。
//   - 同じ式どうしの差・商（`E - E` → {0}・`E / E` → {1}。E の系列が名前を除いたラベルで一意に決まる形だけ。#1595）。
// ★ 「任意の値」は **±Inf を含む閉区間 [-Inf, +Inf]**（#1595。除算は ±Inf を返し得る。`x == +Inf` を「決して発火しない」と読まない）。
//   NaN は値の集合に入れない（NaN はどの評価器でも ne 以外で真にならない。ne の評価器で NaN が真になる端は近似として受容する）。
// ★ 単項の符号は `^` より弱い（#1595。PromQL では `-2 ^ 2` は -4。`2 ^ -1` の右辺の符号は右辺の中で読む）。
//
// ★ 空集合（決して値を返さない式。#1595）—— **健全に認識できる形だけ**を空と判定し、判定できない形は「検証できない」と
//   報告する。**完全を目指さない**（下に挙げない形は「値を返し得る」として扱う＝見逃し側。偽陽性は出さない）:
//   (a) 空の伝播: `and` のどちらかの辺・算術のベクタの辺・集約 / 関数の引数・比較の辺が空なら空。`or` は和。
//       `unless` の左辺が空なら空、右辺が空なら左辺のまま。
//   (b) 同じ式の自己矛盾: `E op1 c1 and E op2 c2` → 値は両方の絞り込みの交わり（E の系列が名前を除いたラベルで一意に
//       決まり、照合の修飾が無いときだけ。そうでないのに交わりで値が変わる形は「検証できない」）。
//       `E unless E`・`E op c unless E` → 空、`E unless E op c` → E の値 ∩ 絞り込みの補集合（系列は自分自身と必ず
//       照合するので、修飾の有無によらず健全）。NaN の標本だけが残り得る形（補集合が空でも `!=` 以外の絞り込みは
//       NaN を通さない）は「検証できない」。
//   (c) ラベルの矛盾: 照合に使うラベルについて、両辺の選択子の等号の照合子（`job="a"` と `job="b"`）や、ラベルを
//       持たないことが確定している辺（`vector(…)`・`sum(…)`・`sum by (l)(…)` の外のラベル・`absent(…)`）と食い違えば、
//       `and`・ベクタどうしの比較・算術は空（`unless` は何も除かない）。`on (…)` / `ignoring (…)` の射程を守る。
//       ラベルを追うのは選択子・範囲の関数・集約・比較・`and` / `unless` の左辺・`vector`・`absent` まで。
//       ベクタどうしの算術・比較と `or` の後はラベルを「不明」とし、矛盾を主張しない。
// ★ 報告する形（検証できない）: 算術・集約・関数が**比較を包む**形（`(up == 0) * 1`・`max(up == 0)`）、
//   値が縛られた辺に算術・集約を掛ける形（`vector(0) * 2`）、`% ^ atan2`、単項の符号を掛けたベクタ、
//   一覧に無い関数・集約（`count`・`abs` のように値の範囲を縛り得るもの）、上の (b) で健全に判定できない形、
//   `and` / `unless` / `or` の読めない右辺、その他の読めない形。
// ★ 近似として受容するもの（偽陰性。偽陽性は出さない側）: `PASS_THROUGH_FUNCTIONS` の中にも値の範囲を持つもの
//   がある（`rate` / `increase` は 0 以上）。「任意の値」は上位集合なので偽陽性は出ないが、`lt 0` で比べる
//   ような形は見逃す。そういう評価器は実在しないので、一覧を細かく割らない。
// ★ `condition` と refId を突き合わせる: `condition` → threshold（評価器 1 件）→ `expression` → クエリ、を
//   refId で辿る。間に math / reduce などの段があれば**検証できない**として報告する（今のルールには無い）。
//   threshold の `expression: $A` は**違反**（Grafana の threshold は `$` を剥がさず、そのまま refId として引く。
//   `$` を剥がすのは reduce / resample だけ。grafana/grafana@92b769af の pkg/expr/threshold.go と commands.go で確かめた）。
// ★ provisioning は**木として**読む（下の YAML の読み取り）。行の正規表現で拾うと、複数行に続く plain の `expr:` を
//   1 行目だけで読み、`gt 0` と組んでも通していた（#1595 の既存の不具合）。評価器は `{ type, params }` でも
//   `{ params, type }`（Grafana のエクスポート順）でもブロックの写像でも読む。行末コメントも読む。
// ★ fail-closed: 式・評価器・refId の鎖・YAML を読めないルールは違反にする（読めないまま素通りさせない）。
// ---------------------------------------------------------------------------

/**
 * 検証できない式を意図して残すルール（title → 理由）。**レビューを経て、空でない理由つきで載せること**
 * （#1595: 理由が空・空白だけ・文字列でない項目は違反にし、その項目では黙らせない）。
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
/** 系列を作り直す（出力のラベルが by / without で決まる）集約。topk / bottomk は入力の系列をそのまま返すので含めない。 */
const REGROUPING_AGGREGATIONS = new Set(['sum', 'avg', 'min', 'max', 'quantile', 'count', 'group', 'stddev', 'stdvar']);
/** ベクタの引数が最後に来る関数・集約（それ以外は先頭）。 */
const VECTOR_ARG_LAST = new Set(['topk', 'bottomk', 'quantile', 'quantile_over_time', 'histogram_quantile']);
/** 範囲の選択子を取り、名前を落として他のラベルを保つ関数（同じ名前の選択子なら系列は一意のまま）。 */
const NAME_DROPPING_RANGE_FUNCTIONS = new Set([
  'rate', 'irate', 'increase', 'delta', 'idelta', 'deriv', 'predict_linear',
  'sum_over_time', 'avg_over_time', 'min_over_time', 'max_over_time', 'last_over_time', 'quantile_over_time',
]);
const ABSENT_FUNCTIONS = new Set(['absent', 'absent_over_time']);
/** 選択子の名前として読んではならない語。 */
const PROMQL_KEYWORDS = new Set([
  'bool', 'on', 'ignoring', 'group_left', 'group_right', 'by', 'without', 'offset',
  'and', 'or', 'unless', 'atan2', 'inf', 'nan',
]);
/** 語の終わり。🔴 `:` を含める（#1595。`job:up:bool` の `bool`・`foo:offset` の `offset` を語として読まない）。 */
const KW_END = '(?![A-Za-z0-9_:])';
const OR_RE = new RegExp(`^or${KW_END}`, 'i');
const SET_OP_RE = new RegExp(`^(?:and|unless)${KW_END}`, 'i');
const BOOL_RE = new RegExp(`^bool${KW_END}`, 'i');
const ATAN2_RE = new RegExp(`^atan2${KW_END}`, 'i');
const STRING_TOKEN_RE = /^"\d+"$/;

const INF = Number.POSITIVE_INFINITY;

/** 区間 { lo, loInc, hi, hiInc }。点は lo === hi かつ両端を含む。 */
const interval = (lo, loInc, hi, hiInc) => ({ lo, loInc, hi, hiInc });
const point = (v) => interval(v, true, v, true);
/** 任意の値。🔴 ±Inf を含む閉区間（#1595）。 */
const ALL = Object.freeze([Object.freeze(interval(-INF, true, INF, true))]);
const isEmptyInterval = (x) => Number.isNaN(x.lo) || Number.isNaN(x.hi) || x.lo > x.hi || (x.lo === x.hi && !(x.loInc && x.hiInc));

/** 区間の集合を正規化する（空の区間を落とし、並べ、重なり・接する区間を併合する）。 */
function normalizeSet(xs) {
  const ys = xs.filter((x) => x && !isEmptyInterval(x)).map((x) => ({ ...x }))
    .sort((a, b) => (a.lo < b.lo ? -1 : a.lo > b.lo ? 1 : Number(b.loInc) - Number(a.loInc)));
  const out = [];
  for (const y of ys) {
    const last = out[out.length - 1];
    if (last && (y.lo < last.hi || (y.lo === last.hi && (last.hiInc || y.loInc)))) {
      if (y.hi > last.hi) { last.hi = y.hi; last.hiInc = y.hiInc; } else if (y.hi === last.hi) last.hiInc = last.hiInc || y.hiInc;
    } else {
      out.push(y);
    }
  }
  return out;
}

const isAll = (xs) => {
  const n = normalizeSet(xs);
  return n.length === 1 && n[0].lo === -INF && n[0].loInc && n[0].hi === INF && n[0].hiInc;
};

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
const intersectSets = (xs, ys) => normalizeSet(xs.flatMap((x) => ys.map((y) => intersectInterval(x, y)).filter(Boolean)));

/** [-Inf, +Inf] の中での補集合。 */
function complementSet(xs) {
  const out = [];
  let lo = -INF;
  let loInc = true;
  for (const x of normalizeSet(xs)) {
    out.push(interval(lo, loInc, x.lo, !x.loInc));
    lo = x.hi;
    loInc = !x.hiInc;
  }
  out.push(interval(lo, loInc, INF, true));
  return normalizeSet(out);
}

const sameSet = (a, b) => {
  const x = normalizeSet(a);
  const y = normalizeSet(b);
  return x.length === y.length && x.every((p, i) => p.lo === y[i].lo && p.hi === y[i].hi && p.loInc === y[i].loInc && p.hiInc === y[i].hiInc);
};

/** 区間の集合の和（正規化する。ALL を含めば ALL に畳まれる）。 */
const unionSets = (...sets) => normalizeSet(sets.flat());

/** 比較演算子と数値 c から、絞り込みの後に残り得る値の集合を返す（±Inf を端に含む。#1595）。 */
function filterSet(op, c) {
  if (Number.isNaN(c)) return op === '!=' ? ALL : []; // NaN との比較は != だけが真
  switch (op) {
    case '==': return normalizeSet([point(c)]);
    case '!=': return normalizeSet([interval(-INF, true, c, false), interval(c, false, INF, true)]);
    case '>': return normalizeSet([interval(c, false, INF, true)]);
    case '>=': return normalizeSet([interval(c, true, INF, true)]);
    case '<': return normalizeSet([interval(-INF, true, c, false)]);
    case '<=': return normalizeSet([interval(-INF, true, c, true)]);
    default: return ALL;
  }
}

/** 評価器（Grafana の threshold）を満たす値の集合。解釈できない型・引数は null。±Inf の値でも真になり得る（Go の比較）。 */
function evaluatorSet(type, params) {
  const [a, b] = params;
  const need = (n) => params.length >= n && params.slice(0, n).every((x) => Number.isFinite(x));
  switch (type) {
    case 'gt': return need(1) ? [interval(a, false, INF, true)] : null;
    case 'lt': return need(1) ? [interval(-INF, true, a, false)] : null;
    case 'gte': return need(1) ? [interval(a, true, INF, true)] : null;
    case 'lte': return need(1) ? [interval(-INF, true, a, true)] : null;
    case 'eq': return need(1) ? [point(a)] : null;
    case 'ne': return need(1) ? filterSet('!=', a) : null;
    case 'within_range': return need(2) ? [interval(a, false, b, false)] : null;
    case 'within_range_included': return need(2) ? [interval(a, true, b, true)] : null;
    case 'outside_range': return need(2) ? [interval(-INF, true, a, false), interval(b, false, INF, true)] : null;
    case 'outside_range_included': return need(2) ? [interval(-INF, true, a, true), interval(b, true, INF, true)] : null;
    default: return null;
  }
}

/** PromQL の文字列リテラルを復号する（Go の strconv.Unquote 相当。読めないエスケープは null）。 */
function decodePromqlString(raw) {
  const q = raw[0];
  const body = raw.slice(1, -1);
  if (q === '`') return body;
  const esc = { a: '\x07', b: '\b', f: '\f', n: '\n', r: '\r', t: '\t', v: '\v', '\\': '\\', "'": "'", '"': '"' };
  let bad = false;
  const out = body.replace(/\\(x[0-9a-fA-F]{2}|[0-7]{3}|u[0-9a-fA-F]{4}|U[0-9a-fA-F]{8}|.)/g, (m, e) => {
    if (e.length > 1 && /^[xuU]/.test(e)) return String.fromCodePoint(parseInt(e.slice(1), 16));
    if (e.length === 3) return String.fromCodePoint(parseInt(e, 8));
    if (Object.prototype.hasOwnProperty.call(esc, e)) return esc[e];
    bad = true;
    return m;
  });
  return bad ? null : out;
}

/**
 * 文字列リテラルを `"<番号>"` に置き換え（**同じ中身は同じ番号**）、行コメントを消す。
 * 1 回の走査で行う（コメントの中の引用符・文字列の中の `#` を取り違えない）。
 * 🔴 #1595: 以前は中身を捨てて `""` に潰していた —— ラベルの照合（`job="a"` と `job="b"`）も、
 *    同じ式かどうかの判定も、中身が要る。
 */
function maskPromql(expr) {
  const strings = [];
  const ids = new Map();
  let text = '';
  for (let i = 0; i < expr.length; i++) {
    const ch = expr[i];
    if (ch === '#') {
      while (i < expr.length && expr[i] !== '\n') i++;
      text += '\n';
      continue;
    }
    if (ch === '"' || ch === "'" || ch === '`') {
      let j = i + 1;
      while (j < expr.length && expr[j] !== ch) {
        if (ch !== '`' && expr[j] === '\\') j++;
        j++;
      }
      if (j >= expr.length) return { text: null, strings, error: '閉じていない文字列リテラルがある' };
      const value = decodePromqlString(expr.slice(i, j + 1));
      if (value === null) return { text: null, strings, error: '読めないエスケープを含む文字列リテラルがある' };
      if (!ids.has(value)) { ids.set(value, strings.length); strings.push(value); }
      text += `"${ids.get(value)}"`;
      i = j;
      continue;
    }
    text += ch;
  }
  return { text, strings, error: null };
}

const NO_CTX = Object.freeze({ strings: [] });

/** 深さ 0 の位置で `re`（先頭一致・語境界つき）に当たる箇所で分け、各辺・演算子・位置を返す。 */
function splitTopLevelOps(s, re) {
  const parts = [];
  const ops = [];
  const ats = [];
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
        ops.push(m[0].toLowerCase());
        ats.push(i);
        i += m[0].length - 1;
        start = i + 1;
      }
    }
  }
  parts.push(s.slice(start));
  return { parts, ops, ats };
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

/** 二項の `+` / `-` か（単項・指数の符号・`offset -5m`・`bool -1` を除く）。 */
function isBinarySign(s, i) {
  let j = i - 1;
  while (j >= 0 && /\s/.test(s[j])) j--;
  if (j < 0 || !/[A-Za-z0-9_\])}."':]/.test(s[j])) return false;
  const before = s.slice(0, j + 1);
  if (/(?:^|[^A-Za-z0-9_:.])(?:\d+(?:\.\d*)?|\.\d+)e$/i.test(before)) return false; // 1e-5
  // 🔴 #1595: 語は `:` を含めて取る（`foo:offset - 1` の `offset` を修飾子として読まない）。
  const word = (before.match(/([A-Za-z_:][A-Za-z0-9_:]*)$/) || [])[1];
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
      else if (ATAN2_RE.test(s.slice(i)) && (i === 0 || !/[A-Za-z0-9_:]/.test(s[i - 1]))) {
        found.push({ at: i, op: 'atan2', len: 5 });
        i += 4;
      }
    }
    if (level === 'pow' && ch === '^') found.push({ at: i, op: '^', len: 1 });
  }
  return found;
}

/** `a, b, "c"` のラベル名の並び（引用符のラベル名は復号する）。 */
function splitLabelList(text, ctx = NO_CTX) {
  return text.split(',').map((l) => l.trim()).filter((l) => l !== '').map((l) => {
    const m = l.match(/^"(\d+)"$/);
    return m ? ctx.strings[Number(m[1])] : l;
  });
}

/** ベクタどうしの照合の修飾（`on (…)` / `ignoring (…)` / `group_left (…)`）を読んで剥がす。 */
function parseMatchingModifiers(text, ctx = NO_CTX) {
  let t = text.trim();
  let kind = null;
  let labels = [];
  let group = null;
  let m = t.match(/^(on|ignoring)\s*\(([^()]*)\)\s*/i);
  if (m) { kind = m[1].toLowerCase(); labels = splitLabelList(m[2], ctx); t = t.slice(m[0].length); }
  m = t.match(new RegExp(`^(group_left|group_right)${KW_END}\\s*(?:\\(([^()]*)\\))?\\s*`, 'i'));
  if (m) { group = m[1].toLowerCase(); t = t.slice(m[0].length); }
  return { kind, labels, group, rest: t.trim() };
}

/**
 * 二項演算を 1 回だけ割る（加減・乗除は左結合なので最後、冪は右結合なので最初）。
 * 🔴 #1595: 加減・乗除が無く先頭に符号があれば**単項**として返す —— 単項の符号は `^` より弱い（`-2 ^ 2` は -4）。
 */
function splitBinaryArith(s, ctx = NO_CTX) {
  for (const level of ['add', 'mul']) {
    const ops = topLevelArith(s, level);
    if (ops.length === 0) continue;
    const o = ops[ops.length - 1];
    const mod = parseMatchingModifiers(s.slice(o.at + o.len), ctx);
    return { op: o.op, left: s.slice(0, o.at), right: mod.rest, mod };
  }
  const t = s.trim();
  if (/^[-+]/.test(t)) return { unary: t[0], operand: t.slice(1) };
  const pow = topLevelArith(s, 'pow');
  if (pow.length > 0) {
    const o = pow[0];
    const mod = parseMatchingModifiers(s.slice(o.at + o.len), ctx);
    return { op: o.op, left: s.slice(0, o.at), right: mod.rest, mod };
  }
  return null;
}

/** 定数式の値（数値リテラルと、定数どうしの算術・単項の符号）。定数でなければ null。 */
function constantValue(text, ctx = NO_CTX) {
  const s = unwrapParens(text);
  const n = parseNumber(s);
  if (n !== null) return n;
  if (s === '') return null;
  const b = splitBinaryArith(s, ctx);
  if (!b) return null;
  if (b.unary) {
    const v = constantValue(b.operand, ctx);
    return v === null ? null : (b.unary === '-' ? -v : v);
  }
  const l = constantValue(b.left, ctx);
  const r = constantValue(b.right, ctx);
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

/** 選択子の後ろの修飾子（`offset` と `@` は**どちらの順でも**書ける。#1595）。 */
const SELECTOR_MODIFIERS = '(?:\\s*(?:offset\\s+-?[0-9a-z]+|@\\s*(?:start\\(\\s*\\)|end\\(\\s*\\)|[-+]?[0-9][0-9.]*(?:e[-+]?[0-9]+)?)))*';
const SELECTOR_RE = new RegExp(
  `^(?:[A-Za-z_:][A-Za-z0-9_:]*\\s*(?:\\{[^{}]*\\})?|\\{[^{}]*\\})\\s*(?:\\[[^[\\]]*\\])?${SELECTOR_MODIFIERS}\\s*$`, 'i',
);
function isSelector(s) {
  if (!SELECTOR_RE.test(s)) return false;
  const name = (s.match(/^[A-Za-z_:][A-Za-z0-9_:]*/) || [''])[0].toLowerCase();
  return !PROMQL_KEYWORDS.has(name);
}

/** ラベルの制約 { exact, eq, any }: eq は等号で確定した値（'' は持たない）、exact は eq / any の外のラベルを持たないこと。 */
const labelsOf = (exact, eq = new Map(), any = new Set()) => ({ exact, eq, any });

/** 選択子の名前と、等号の照合子が確定させるラベル。選択子でなければ null。 */
function selectorInfo(s, ctx = NO_CTX) {
  const t = s.trim();
  if (!isSelector(t)) return null;
  const nameM = t.match(/^[A-Za-z_:][A-Za-z0-9_:]*/);
  let name = nameM ? nameM[0] : null;
  const eq = new Map();
  const conflicted = new Set();
  const braces = t.match(/\{([^{}]*)\}/);
  if (braces) {
    for (const part of braces[1].split(',')) {
      const m = part.match(/^\s*([A-Za-z_][A-Za-z0-9_]*)\s*(=~|!~|!=|=)\s*"(\d+)"\s*$/);
      if (!m || m[2] !== '=') continue;
      const v = ctx.strings[Number(m[3])];
      if (m[1] === '__name__') { if (name === null) name = v; continue; }
      if (eq.has(m[1]) && eq.get(m[1]) !== v) conflicted.add(m[1]);
      eq.set(m[1], v);
    }
  }
  for (const l of conflicted) eq.delete(l); // 矛盾する照合子は制約として使わない（保守側）
  return { name, labels: labelsOf(false, eq) };
}

function labelValue(L, l) {
  if (!L) return null;
  if (L.eq.has(l)) return L.eq.get(l);
  if (L.any.has(l)) return null;
  return L.exact ? '' : null;
}

/** 照合に使うラベルで、両辺の確定した値が食い違うか（食い違えば 1 系列も照合しない）。 */
function labelsConflict(L, R, mod) {
  if (!L || !R) return false;
  let names;
  if (mod && mod.kind === 'on') {
    names = mod.labels;
  } else {
    const s = new Set([...L.eq.keys(), ...R.eq.keys(), ...L.any, ...R.any]);
    if (mod && mod.kind === 'ignoring') for (const l of mod.labels) s.delete(l);
    names = [...s];
  }
  return names.some((l) => {
    if (l === '__name__') return false;
    const a = labelValue(L, l);
    const b = labelValue(R, l);
    return a !== null && b !== null && a !== b;
  });
}

/** `name [by|without (…)] (args) [by|without (…)]` を読む。関数呼び出しでなければ null。 */
function parseCall(s, ctx = NO_CTX) {
  const m = s.match(/^([A-Za-z_][A-Za-z0-9_]*)\s*/);
  if (!m) return null;
  let i = m[0].length;
  let grouping = null;
  let groupLabels = [];
  const g1 = s.slice(i).match(/^(by|without)\s*\(([^()]*)\)\s*/i);
  if (g1) { i += g1[0].length; grouping = g1[1].toLowerCase(); groupLabels = splitLabelList(g1[2], ctx); }
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
    const g2 = tail.match(/^(by|without)\s*\(([^()]*)\)$/i);
    if (!g2 || grouping) return null;
    grouping = g2[1].toLowerCase();
    groupLabels = splitLabelList(g2[2], ctx);
  }
  return { name: m[1].toLowerCase(), args: splitArgs(s.slice(i + 1, j)), grouping, groupLabels };
}

/** サブクエリ `<式>[範囲:刻み]`（後ろの `offset` / `@` を含む）なら中の式を返す。そうでなければ null（#1595）。 */
const SUBQUERY_TAIL_RE = new RegExp(`${SELECTOR_MODIFIERS}\\s*$`, 'i');
function splitSubquery(s) {
  const t = s.trim();
  const tail = t.match(SUBQUERY_TAIL_RE);
  const head = tail ? t.slice(0, tail.index) : t;
  if (!head.endsWith(']')) return null;
  let depth = 0;
  let k = head.length - 1;
  for (; k >= 0; k--) {
    const ch = head[k];
    if (')]}'.includes(ch)) depth++;
    else if ('([{'.includes(ch)) { depth--; if (depth === 0) break; }
  }
  if (k < 0 || head[k] !== '[' || !head.slice(k + 1, -1).includes(':')) return null;
  const inner = head.slice(0, k).trim();
  return inner === '' ? null : inner;
}

/** 同じ式かどうかを比べるための正規化（空白の差だけを消す。語どうしは繋げない）。 */
const normExpr = (t) => unwrapParens(t).replace(/\s+/g, ' ').replace(/\s*([(){}[\],=!~<>])\s*/g, '$1').trim();

/**
 * 式の系列が「名前を除いたラベル」で一意に決まるか（`and` の照合で他の系列と取り違えないか）。保守側に倒す:
 * 名前を持つ選択子・`vector(定数)`・系列を作り直す集約（`by (__name__)` を除く）・`histogram_quantile`・
 * 名前を持つ選択子に掛けた範囲の関数・一意な式に掛けた topk / bottomk だけを一意とする。
 */
function nameUnique(text, ctx = NO_CTX) {
  const s = unwrapParens(text);
  const sel = selectorInfo(s, ctx);
  if (sel) return sel.name !== null;
  const call = parseCall(s, ctx);
  if (!call) return false;
  if (call.name === 'vector') return call.args.length === 1 && constantValue(call.args[0], ctx) !== null;
  if (REGROUPING_AGGREGATIONS.has(call.name)) return !(call.grouping === 'by' && call.groupLabels.includes('__name__'));
  if (call.name === 'topk' || call.name === 'bottomk') return call.args.length === 2 && nameUnique(call.args[1], ctx);
  if (call.name === 'histogram_quantile') return true;
  if (NAME_DROPPING_RANGE_FUNCTIONS.has(call.name)) {
    const vi = VECTOR_ARG_LAST.has(call.name) ? call.args.length - 1 : 0;
    const arg = selectorInfo(call.args[vi] || '', ctx);
    return arg !== null && arg.name !== null;
  }
  return false;
}

/**
 * `E op 定数` / `定数 op E`（bool・照合の修飾なし）か素の `E` なら、E と絞り込みの集合を返す。そうでなければ null。
 * 空集合の (b)「同じ式の自己矛盾」の判定に使う。
 */
function filterForm(text, ctx = NO_CTX) {
  const s = unwrapParens(text);
  if (splitTopLevelOps(s, OR_RE).parts.length > 1 || splitTopLevelOps(s, SET_OP_RE).parts.length > 1) return null;
  const cmps = topLevelComparisons(s);
  if (cmps.length === 0) return { eText: s, base: normExpr(s), filter: ALL, op: null };
  if (cmps.length > 1) return null;
  const { at, op } = cmps[0];
  const lhs = s.slice(0, at);
  const rhsRaw = s.slice(at + op.length).trim();
  if (BOOL_RE.test(rhsRaw)) return null;
  const mod = parseMatchingModifiers(rhsRaw, ctx);
  if (mod.kind || mod.group) return null;
  const lc = constantValue(lhs, ctx);
  const rc = constantValue(mod.rest, ctx);
  if ((lc === null) === (rc === null)) return null;
  const e = rc !== null ? lhs : mod.rest;
  return { eText: e.trim(), base: normExpr(e), filter: rc !== null ? filterSet(op, rc) : filterSet(FLIP[op], lc), op };
}

const ok = (set, cmp = false, labels = null) => ({ set: normalizeSet(set), cmp, why: null, labels });
const unverifiable = (why) => ({ set: null, cmp: true, why, labels: null });
const clip = (s) => (s.length > 60 ? `${s.slice(0, 60)}…` : s);

const clampMax = (set, c) => {
  const kept = intersectSets(set, [interval(-INF, true, c, true)]);
  return intersectSets(set, [interval(c, false, INF, true)]).length > 0 ? normalizeSet([...kept, point(c)]) : kept;
};
const clampMin = (set, c) => {
  const kept = intersectSets(set, [interval(c, true, INF, true)]);
  return intersectSets(set, [interval(-INF, true, c, false)]).length > 0 ? normalizeSet([...kept, point(c)]) : kept;
};

/** 関数・集約の出力のラベルの制約（inner はベクタの引数の制約）。 */
function callLabels(name, inner, grouping, groupLabels) {
  if (REGROUPING_AGGREGATIONS.has(name)) {
    if (grouping === 'by') {
      const eq = new Map();
      const any = new Set();
      for (const l of groupLabels) {
        const v = labelValue(inner, l);
        if (v !== null) eq.set(l, v); else any.add(l);
      }
      return labelsOf(true, eq, any);
    }
    if (grouping === 'without') {
      if (!inner) return null;
      return labelsOf(
        inner.exact,
        new Map([...inner.eq].filter(([l]) => !groupLabels.includes(l))),
        new Set([...inner.any].filter((l) => !groupLabels.includes(l))),
      );
    }
    return labelsOf(true);
  }
  if (name === 'label_replace' || name === 'label_join') return null;
  if (name === 'histogram_quantile') {
    if (!inner) return null;
    const eq = new Map(inner.eq);
    eq.delete('le');
    const any = new Set(inner.any);
    any.delete('le');
    return labelsOf(inner.exact, eq, any);
  }
  return inner; // topk / bottomk / sort と範囲の関数はラベルを保つ（名前だけ落とす）
}

function analyzeCall({ name, args, grouping, groupLabels }, ctx) {
  if (grouping && !AGGREGATIONS.has(name)) return unverifiable(`${name}(…) に ${grouping} は付けられない`);
  if (ABSENT_FUNCTIONS.has(name)) {
    if (args.length !== 1) return unverifiable(`${name}(…) の引数が 1 つではない`);
    // absent の出力のラベルは、引数の選択子の等号の照合子（名前を除く）だけ。選択子でなければラベルを持たない。
    const sel = selectorInfo(args[0], ctx);
    return ok([point(1)], false, labelsOf(true, sel ? new Map(sel.labels.eq) : new Map()));
  }
  if (name === 'vector') {
    const c = args.length === 1 ? constantValue(args[0], ctx) : null;
    return c === null ? unverifiable('vector(…) の引数が定数ではない') : ok([point(c)], false, labelsOf(true));
  }
  if (name === 'clamp_max' || name === 'clamp_min' || name === 'clamp') {
    const want = name === 'clamp' ? 3 : 2;
    const bounds = args.slice(1).map((a) => constantValue(a, ctx));
    if (args.length !== want || bounds.some((b) => b === null || Number.isNaN(b))) {
      return unverifiable(`${name}(…) の境界が定数ではない`);
    }
    const v = analyze(args[0], ctx);
    if (v.why) return v;
    if (name === 'clamp_max') return ok(clampMax(v.set, bounds[0]), v.cmp, v.labels);
    if (name === 'clamp_min') return ok(clampMin(v.set, bounds[0]), v.cmp, v.labels);
    if (bounds[0] > bounds[1]) return ok([], v.cmp, v.labels); // min > max は空を返す
    return ok(clampMax(clampMin(v.set, bounds[0]), bounds[1]), v.cmp, v.labels);
  }
  if (PASS_THROUGH_FUNCTIONS.has(name)) {
    const vi = VECTOR_ARG_LAST.has(name) ? args.length - 1 : 0;
    let inner = null;
    for (let k = 0; k < args.length; k++) {
      const a = args[k];
      if (STRING_TOKEN_RE.test(a) || constantValue(a, ctx) !== null) continue;
      const r = analyze(a, ctx);
      if (r.why) return r;
      if (r.set.length === 0) return ok([], r.cmp); // 空の入力の集約・関数は空（#1595）
      if (r.cmp) return unverifiable(`${name}(…) が比較を包んでいる —— 比較の後に残る値を集約・関数が変えるので解釈しない`);
      if (!isAll(r.set)) {
        return unverifiable(`${name}(…) の引数の値が縛られている（${describeSet(r.set)}）—— 集約・関数の後の値の集合は解釈しない`);
      }
      if (k === vi) inner = r.labels;
    }
    return ok(ALL, false, callLabels(name, inner, grouping, groupLabels));
  }
  return unverifiable(`関数・集約 ${name}(…) は本検査が読める一覧（PASS_THROUGH_FUNCTIONS ほか）に無い —— 値の範囲を縛り得る`);
}

/** `and` / `unless` の 1 段（左結合）。leftText は左辺のテキスト、left はその解析結果。 */
function combineSetOp(leftText, left, op, rightRaw, ctx) {
  const mod = parseMatchingModifiers(rightRaw, ctx);
  if (mod.group) return unverifiable(`${op} に group_* は付けられない`);
  const right = analyze(mod.rest, ctx);
  if (right.why) return right;
  const keep = (set) => ok(set, left.cmp, left.labels);
  const L = filterForm(leftText, ctx);
  const R = filterForm(mod.rest, ctx);
  const same = L !== null && R !== null && L.base === R.base;
  const nanL = L !== null && (L.op === null || L.op === '!=');
  const nanR = R !== null && (R.op === null || R.op === '!=');
  if (op === 'and') {
    if (left.set.length === 0 || right.set.length === 0) return keep([]);
    if (labelsConflict(left.labels, right.labels, mod)) return keep([]);
    if (!same) return keep(left.set);
    const refined = intersectSets(left.set, R.filter);
    if (sameSet(refined, left.set)) return keep(left.set);
    if (mod.kind !== null || !nameUnique(L.eText, ctx)) {
      return unverifiable(
        '同じ式を and で重ねて絞る形だが、系列が 1 対 1 に照合することを確かめられない' +
        '（名前を持つ選択子などに限り、照合の修飾が無いときだけ交わりとして読む）',
      );
    }
    if (refined.length === 0 && nanL && nanR) return unverifiable('同じ式を and で重ねて絞った後に NaN の標本だけが残り得る');
    return keep(refined);
  }
  // unless: 左辺の系列は、自分自身が右辺に居れば必ず除かれる（修飾の有無によらない）。
  if (left.set.length === 0) return keep([]);
  if (right.set.length === 0 || labelsConflict(left.labels, right.labels, mod)) return keep(left.set);
  if (!same) return keep(left.set);
  const refined = intersectSets(left.set, complementSet(R.filter));
  if (refined.length === 0 && nanL && !nanR) return unverifiable('同じ式を unless で除いた後に NaN の標本だけが残り得る');
  return keep(refined);
}

/**
 * 式が発火側で出し得る値の集合を解析する。純関数（自己試験から直接呼ぶ）。
 * 戻り値: { set, cmp, why, labels } —— `why` が null でなければ**検証できない**（その理由）。
 * `cmp` は式のどこかに比較（`bool` を含む）があるか（算術・集約が比較を包む形を見分けるのに使う）。
 * `labels` は出力の系列のラベルの制約（不明なら null）。空集合の (c) に使う。
 * `expr` は `maskPromql` を通したテキスト（文字列リテラルは `"<番号>"`）。
 */
function analyze(expr, ctx = NO_CTX) {
  const s = unwrapParens(expr);
  if (s === '') return unverifiable('空の式');

  const or = splitTopLevelOps(s, OR_RE);
  if (or.parts.length > 1) {
    const rs = [];
    for (let k = 0; k < or.parts.length; k++) {
      let part = or.parts[k];
      if (k > 0) {
        // #1595: `or on (…)` / `or ignoring (…)` の修飾は値を変えないので剥がす。
        const mod = parseMatchingModifiers(part, ctx);
        if (mod.group) return unverifiable('or に group_* は付けられない');
        part = mod.rest;
      }
      const r = analyze(part, ctx);
      if (r.why) return r;
      rs.push(r);
    }
    return ok(unionSets(...rs.map((r) => r.set)), rs.some((r) => r.cmp), null);
  }

  const su = splitTopLevelOps(s, SET_OP_RE);
  if (su.parts.length > 1) {
    let acc = analyze(su.parts[0], ctx);
    for (let k = 1; k < su.parts.length && !acc.why; k++) {
      acc = combineSetOp(s.slice(0, su.ats[k - 1]), acc, su.ops[k - 1], su.parts[k], ctx);
    }
    return acc;
  }

  const cmps = topLevelComparisons(s);
  if (cmps.length > 1) return unverifiable(`最上位の比較が ${cmps.length} 個ある（連鎖した比較は解釈しない）`);
  if (cmps.length === 1) {
    const { at, op } = cmps[0];
    const lhs = s.slice(0, at);
    let rhs = s.slice(at + op.length).trim();
    const isBool = BOOL_RE.test(rhs);
    if (isBool) rhs = rhs.replace(BOOL_RE, '');
    const mod = parseMatchingModifiers(rhs, ctx);
    rhs = mod.rest;
    const lc = constantValue(lhs, ctx);
    const rc = constantValue(rhs, ctx);
    if (lc !== null && rc !== null) {
      if (!isBool) return unverifiable('両辺が定数の比較（bool なしでは PromQL として成り立たない）');
      return ok([point(setsIntersect([point(lc)], filterSet(op, rc)) ? 1 : 0)], true, null);
    }
    if (rc !== null || lc !== null) {
      const side = analyze(rc !== null ? lhs : rhs, ctx);
      if (side.why) return side;
      if (isBool) return ok(side.set.length === 0 ? [] : [point(0), point(1)], true, side.labels);
      return ok(intersectSets(side.set, rc !== null ? filterSet(op, rc) : filterSet(FLIP[op], lc)), true, side.labels);
    }
    // ベクタどうし: 左辺の値が残る（右辺は値を決めない）。右辺も読める形であることは求める
    // （`bool` の読み取りが外れたとき `bool 0` を黙ってベクタとして通さない）。
    const left = analyze(lhs, ctx);
    if (left.why) return left;
    const right = analyze(rhs, ctx);
    if (right.why) return right;
    if (left.set.length === 0 || right.set.length === 0 || labelsConflict(left.labels, right.labels, mod)) return ok([], true, null);
    return ok(isBool ? [point(0), point(1)] : left.set, true, null);
  }

  const c = constantValue(s, ctx);
  if (c !== null) return ok([point(c)]);

  const b = splitBinaryArith(s, ctx);
  if (b && b.unary) return unverifiable('単項の符号を掛けたベクタは解釈しない');
  if (b) {
    const sides = [b.left, b.right].map((t) => ({ t, c: constantValue(t, ctx) }));
    const vecs = sides.filter((x) => x.c === null).map((x) => analyze(x.t, ctx));
    const bad = vecs.find((r) => r.why);
    if (bad) return bad;
    const cmp = vecs.some((r) => r.cmp);
    // #1595: 空の辺を持つ算術は空。ラベルが食い違う辺どうしの算術も空（1 系列も照合しない）。
    if (vecs.some((r) => r.set.length === 0)) return ok([], cmp);
    if (vecs.length === 2 && labelsConflict(vecs[0].labels, vecs[1].labels, b.mod)) return ok([], cmp);
    // #1595: 同じ式どうしの差・商は定数（系列は自分自身と照合する）。
    if (vecs.length === 2 && (b.op === '-' || b.op === '/') && b.mod.kind === null && !b.mod.group &&
      normExpr(b.left) === normExpr(b.right) && nameUnique(b.left, ctx)) {
      return ok([point(b.op === '-' ? 0 : 1)], cmp);
    }
    if (cmp) return unverifiable(`算術（${b.op}）が比較を包んでいる —— 比較の後に残る値を算術が変えるので解釈しない`);
    if (vecs.some((r) => !isAll(r.set))) {
      return unverifiable(`算術（${b.op}）の辺の値が縛られている —— 算術の後の値の集合は解釈しない`);
    }
    if (!['+', '-', '*', '/'].includes(b.op)) return unverifiable(`算術（${b.op}）は値の範囲を縛り得るので解釈しない`);
    const consts = sides.filter((x) => x.c !== null).map((x) => x.c);
    if (consts.some((k) => !Number.isFinite(k) || (k === 0 && (b.op === '*' || b.op === '/')))) {
      return unverifiable(`算術（${b.op}）の定数が 0 か有限でない —— 値が 1 点へ潰れ得る`);
    }
    return ok(ALL, false, vecs.length === 1 ? vecs[0].labels : null);
  }

  // #1595: サブクエリ（関数呼び出し・括弧にも付く）は中の式の値を保つ。
  const sq = splitSubquery(s);
  if (sq !== null) return analyze(sq, ctx);

  const sel = selectorInfo(s, ctx);
  if (sel) return ok(ALL, false, sel.labels);
  const call = parseCall(s, ctx);
  if (call) return analyzeCall(call, ctx);
  return unverifiable(`読めない形: ${clip(s)}`);
}

/** 生の式を解析する（文字列リテラルとコメントを先に処理する）。 */
function analyzeExpr(expr) {
  const m = maskPromql(expr);
  if (m.error) return unverifiable(m.error);
  return analyze(m.text, { strings: m.strings });
}

/**
 * 式が発火側で出し得る値の集合を返す。**検証できないなら null**。空の配列は「決して値を返さない」。
 * 純関数（自己試験から直接呼ぶ）。
 */
function expressionValueSet(expr) {
  const r = analyzeExpr(expr);
  return r.why ? null : r.set;
}

/** 検証できない理由（検証できるなら null）。 */
function expressionUnverifiableReason(expr) {
  return analyzeExpr(expr).why;
}

// ---------------------------------------------------------------------------
// YAML の読み取り（#1595）
//
// 検査 6 は provisioning を**木として**読む。#1588 までは行を正規表現で拾っていたため、複数行に続く plain の
// `expr:`（`expr: up` の次の行の `== 0`）を 1 行目だけで読み、`gt 0` と組んでも通していた。
// scripts/ は依存を入れずに node だけで走る（CI の static-checks は install しない。YAML を読む他の検査器も
// 同じく自前で読む）ので YAML ライブラリは使えない。そこで**本ファイルの書式が使う YAML の部分集合**を読み、
// 部分集合の外は**読めないとして報告する**（fail-closed）。
//   読む: ブロックの写像・列（`- key: v` の詰めた形・キーと同じ字下げの列を含む）／フローの写像・列（複数行・入れ子）／
//         plain（複数行の続き・行末コメント・core schema の型）／一重・二重引用符（複数行の折り畳み・エスケープ）／
//         `|` / `>`（字下げと chomping の指示子）／先頭の `---`。
//   読まない（報告する）: アンカー・エイリアス・タグ・複合キー・複数文書・ディレクティブ・タブの字下げ・重複キー・
//         plain の値の中の「: 」・コメントの後の続き。
// ---------------------------------------------------------------------------
class YamlSubsetError extends Error {}

/** 引用符の閉じる位置（開きは 0 文字目）。閉じなければ -1。 */
function findQuoteEnd(text, q) {
  for (let k = 1; k < text.length; k++) {
    if (q === '"' && text[k] === '\\') { k++; continue; }
    if (text[k] === q) {
      if (q === "'" && text[k + 1] === "'") { k++; continue; }
      return k;
    }
  }
  return -1;
}

const YAML_ESCAPES = {
  0: '\0', a: '\x07', b: '\b', t: '\t', '\t': '\t', n: '\n', v: '\v', f: '\f', r: '\r', e: '\x1b',
  ' ': ' ', '"': '"', '/': '/', '\\': '\\', N: '\u0085', _: ' ', L: ' ', P: ' ',
};

/** 引用符の中身（行は `\n` で区切り、各行の前後の空白は剥がしてある）を折り畳んで復号する。 */
function decodeYamlQuoted(body, q, fail) {
  const segs = body.split('\n').map((x, i, all) => (all.length === 1 ? x : i === 0 ? x.replace(/[ \t]+$/, '') : i === all.length - 1 ? x.replace(/^[ \t]+/, '') : x.trim()));
  let out = segs[0];
  let empties = 0;
  for (let k = 1; k < segs.length; k++) {
    const s = segs[k];
    if (s === '' && k < segs.length - 1) { empties++; continue; }
    if (q === '"' && /(?:^|[^\\])(?:\\\\)*\\$/.test(out)) out = out.slice(0, -1) + '\n'.repeat(empties) + s; // エスケープした改行
    else out += (empties === 0 ? ' ' : '\n'.repeat(empties)) + s;
    empties = 0;
  }
  if (q === "'") return out.replace(/''/g, "'");
  return out.replace(/\\(x[0-9a-fA-F]{2}|u[0-9a-fA-F]{4}|U[0-9a-fA-F]{8}|[\s\S])/g, (m, e) => {
    if (e.length > 1) return String.fromCodePoint(parseInt(e.slice(1), 16));
    if (Object.prototype.hasOwnProperty.call(YAML_ESCAPES, e)) return YAML_ESCAPES[e];
    return fail(`読めないエスケープ \\${e}`);
  });
}

/** plain の値の型（YAML 1.2 core schema）。 */
function resolveYamlPlain(s) {
  if (/^(?:~|null|Null|NULL)$/.test(s)) return null;
  if (/^(?:true|True|TRUE)$/.test(s)) return true;
  if (/^(?:false|False|FALSE)$/.test(s)) return false;
  if (/^[-+]?[0-9]+$/.test(s)) return Number(s);
  if (/^0o[0-7]+$/.test(s)) return parseInt(s.slice(2), 8);
  if (/^0x[0-9a-fA-F]+$/.test(s)) return parseInt(s.slice(2), 16);
  if (/^[-+]?(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)(?:[eE][-+]?[0-9]+)?$/.test(s)) return Number(s);
  if (/^[-+]?\.(?:inf|Inf|INF)$/.test(s)) return s.startsWith('-') ? -INF : INF;
  if (/^\.(?:nan|NaN|NAN)$/.test(s)) return Number.NaN;
  return s;
}

/** YAML の部分集合を木（写像はオブジェクト・列は配列・スカラーは文字列 / 数 / 真偽 / null）へ読む。読めなければ YamlSubsetError。 */
function parseYamlSubset(text) {
  const lines = text.replace(/\r\n?/g, '\n').split('\n').map((raw, i) => {
    const indent = raw.match(/^ */)[0].length;
    return { no: i + 1, indent, text: raw.slice(indent).replace(/\s+$/, ''), raw };
  });
  let pos = 0;
  const fail = (i, why) => { throw new YamlSubsetError(`${i === null || i >= lines.length ? '' : `${lines[i].no} 行目: `}${why}`); };

  const nextSig = () => {
    while (pos < lines.length) {
      const t = lines[pos].text.trimStart();
      if (t === '' || t.startsWith('#')) { pos++; continue; }
      if (lines[pos].text !== t) fail(pos, 'タブで字下げしている（YAML は字下げにタブを使えない）');
      return pos;
    }
    return -1;
  };
  const isSeqItem = (t) => /^-(?:\s|$)/.test(t);
  const restOf = (r) => { const t = r.trim(); return t.startsWith('#') ? '' : t; };

  /** `key: rest` を割る。キーの行でなければ null。 */
  function splitKey(t, i) {
    const c = t[0];
    if (c === undefined || '[{|>&*!%@`#'.includes(c)) return null;
    if (c === '?' && /^\?(?:\s|$)/.test(t)) fail(i, '複合キーは読まない');
    if (isSeqItem(t)) return null;
    if (c === '"' || c === "'") {
      const end = findQuoteEnd(t, c);
      if (end < 0) return null;
      const m = t.slice(end + 1).match(/^\s*:(?:\s+|$)/);
      if (!m) return null;
      return { key: decodeYamlQuoted(t.slice(1, end), c, (w) => fail(i, w)), rest: restOf(t.slice(end + 1 + m[0].length)) };
    }
    const m = t.match(/^(.*?)\s*:(?:\s+|$)/);
    if (!m || /(?:^|\s)#/.test(m[1])) return null;
    return { key: m[1], rest: restOf(t.slice(m[0].length)) };
  }

  function parseBlockNode(parentIndent, allowSeqAtParent = false) {
    const i = nextSig();
    if (i < 0) return null;
    const L = lines[i];
    if (L.indent < parentIndent || (L.indent === parentIndent && !(allowSeqAtParent && isSeqItem(L.text)))) return null;
    if (isSeqItem(L.text)) return parseSeq(L.indent);
    if (splitKey(L.text, i) !== null) return parseMap(L.indent);
    pos = i + 1;
    return parseInline(L.text, i, parentIndent);
  }

  function parseMap(indent) {
    const obj = {};
    for (;;) {
      const i = nextSig();
      if (i < 0) break;
      const L = lines[i];
      if (L.indent < indent) break;
      if (L.indent > indent) fail(i, '字下げが合わない');
      if (isSeqItem(L.text)) fail(i, '写像の中に列の要素がある');
      const kv = splitKey(L.text, i);
      if (kv === null) fail(i, `キーを読めない: ${clip(L.text)}`);
      if (kv.key === '__proto__' || Object.prototype.hasOwnProperty.call(obj, kv.key)) fail(i, `キー ${kv.key} が重複している`);
      pos = i + 1;
      obj[kv.key] = kv.rest === '' ? parseBlockNode(indent, true) : parseInline(kv.rest, i, indent);
    }
    return obj;
  }

  function parseSeq(indent) {
    const arr = [];
    for (;;) {
      const i = nextSig();
      if (i < 0) break;
      const L = lines[i];
      if (L.indent < indent) break;
      if (L.indent > indent) fail(i, '字下げが合わない');
      if (!isSeqItem(L.text)) break;
      const rest = L.text.slice(1).trimStart();
      const col = indent + (L.text.length - rest.length);
      if (rest === '' || rest.startsWith('#')) { pos = i + 1; arr.push(parseBlockNode(indent)); continue; }
      if (isSeqItem(rest) || splitKey(rest, i) !== null) {
        lines[i] = { ...L, indent: col, text: rest }; // 詰めた形: 要素の中身を字下げ col の行として読み直す
        pos = i;
        arr.push(isSeqItem(rest) ? parseSeq(col) : parseMap(col));
        continue;
      }
      pos = i + 1;
      arr.push(parseInline(rest, i, indent));
    }
    return arr;
  }

  function parseInline(t, i, parentIndent) {
    const c = t[0];
    if (c === '&' || c === '*' || c === '!') fail(i, 'アンカー・エイリアス・タグは読まない');
    if (c === '|' || c === '>') return parseBlockScalar(t, i, parentIndent);
    if (c === '"' || c === "'") return parseQuoted(t, i, parentIndent);
    if (c === '[' || c === '{') return parseFlow(t, i, parentIndent);
    if (c === '@' || c === '`' || c === '%') fail(i, `予約された指示子 ${c} で始まる値は読まない`);
    return parsePlain(t, i, parentIndent);
  }

  function cutPlainComment(t) {
    const m = t.match(/(?:^|\s)#/);
    return m ? { body: t.slice(0, m.index).trimEnd(), comment: true } : { body: t.trimEnd(), comment: false };
  }
  function checkPlain(body, i) {
    if (/:(?:\s|$)/.test(body)) fail(i, `plain の値に「: 」がある（写像の行が値の続きとして読まれている）: ${clip(body)}`);
  }

  /** plain の値。字下げが parentIndent より深い行は値の続き（空白 1 つへ折り畳む。空行は改行）。 */
  function parsePlain(t, i, parentIndent) {
    const first = cutPlainComment(t);
    checkPlain(first.body, i);
    let out = first.body;
    let comment = first.comment;
    let empties = 0;
    let last = pos;
    for (let j = pos; j < lines.length; j++) {
      const L = lines[j];
      const u = L.text.trimStart();
      if (u === '') { empties++; continue; }
      if (L.indent <= parentIndent) break;
      if (u.startsWith('#')) { comment = true; last = j + 1; continue; }
      if (comment) fail(j, 'コメントの後に plain の値の続きがある');
      if (L.text !== u) fail(j, 'タブで字下げしている（YAML は字下げにタブを使えない）');
      const c = cutPlainComment(u);
      checkPlain(c.body, j);
      out += empties === 0 ? ` ${c.body}` : '\n'.repeat(empties) + c.body;
      comment = c.comment;
      empties = 0;
      last = j + 1;
    }
    pos = last;
    return resolveYamlPlain(out);
  }

  function parseQuoted(t, i, parentIndent) {
    const q = t[0];
    let buf = t;
    let j = i;
    for (;;) {
      const end = findQuoteEnd(buf, q);
      if (end >= 0) {
        const after = buf.slice(end + 1);
        if (!/^(?:\s*|\s+#.*)$/.test(after)) fail(j, `引用符の後に余分な文字がある: ${clip(after.trim())}`);
        pos = j + 1;
        return decodeYamlQuoted(buf.slice(1, end), q, (w) => fail(i, w));
      }
      j++;
      if (j >= lines.length) fail(i, '引用符が閉じていない');
      const L = lines[j];
      const u = L.raw.trim();
      if (u !== '' && L.indent <= parentIndent) fail(j, '引用符の値の続きの字下げが足りない');
      buf += `\n${u}`;
    }
  }

  function parseBlockScalar(header, i, parentIndent) {
    const m = header.match(/^([|>])(?:([1-9])([+-])?|([+-])([1-9])?)?(?:\s+#.*)?$/);
    if (!m) fail(i, `ブロックスカラーの見出しを読めない: ${clip(header)}`);
    const style = m[1];
    const ind = m[2] || m[5];
    const chomp = m[3] || m[4] || '';
    let contentIndent = ind ? Math.max(parentIndent, 0) + Number(ind) : null;
    const body = [];
    let j = i + 1;
    for (; j < lines.length; j++) {
      const L = lines[j];
      if (L.raw.trim() === '') { body.push(''); continue; }
      if (contentIndent === null) {
        if (L.indent <= parentIndent) break;
        contentIndent = L.indent;
      }
      if (L.indent < contentIndent) break;
      body.push(L.raw.slice(contentIndent));
    }
    pos = j;
    let end = body.length;
    while (end > 0 && body[end - 1] === '') end--;
    const trailing = body.length - end;
    const content = body.slice(0, end);
    if (content.length === 0) return chomp === '+' ? '\n'.repeat(trailing) : '';
    let s;
    if (style === '|') {
      s = content.join('\n');
    } else {
      s = '';
      let started = false;
      let empties = 0;
      let lastMore = false;
      for (const line of content) {
        if (line === '') { if (started) empties++; else s += '\n'; continue; }
        const more = /^[ \t]/.test(line);
        if (!started) s += line;
        else if (!more && !lastMore) s += empties === 0 ? ` ${line}` : '\n'.repeat(empties) + line;
        else s += '\n'.repeat(empties + 1) + line;
        started = true;
        empties = 0;
        lastMore = more;
      }
    }
    if (chomp === '-') return s;
    if (chomp === '+') return s + '\n'.repeat(1 + trailing);
    return `${s}\n`;
  }

  /** フローの写像・列。括弧が閉じるまで行を足し（引用符とコメントを見分ける）、1 本の文字列として読む。 */
  function parseFlow(t, i, parentIndent) {
    let buf = '';
    let depth = 0;
    let q = null;
    let j = i;
    let u = t;
    for (;;) {
      let closed = false;
      for (let k = 0; k < u.length; k++) {
        const ch = u[k];
        if (q) {
          if (q === '"' && ch === '\\') { buf += ch + (u[k + 1] || ''); k++; continue; }
          if (ch === q) {
            if (q === "'" && u[k + 1] === "'") { buf += "''"; k++; continue; }
            q = null;
          }
          buf += ch;
          continue;
        }
        if (ch === '#' && (k === 0 || /\s/.test(u[k - 1]))) break;
        if (ch === '"' || ch === "'") q = ch;
        if (ch === '[' || ch === '{') depth++;
        if (ch === ']' || ch === '}') {
          depth--;
          if (depth === 0) {
            buf += ch;
            const after = u.slice(k + 1);
            if (!/^(?:\s*|\s+#.*)$/.test(after)) fail(j, `フローの後に余分な文字がある: ${clip(after.trim())}`);
            closed = true;
            break;
          }
        }
        buf += ch;
      }
      if (closed) break;
      j++;
      if (j >= lines.length) fail(i, 'フローの括弧が閉じていない');
      const L = lines[j];
      u = L.raw.trim();
      if (u !== '' && L.indent <= parentIndent) fail(j, 'フローの続きの字下げが足りない');
      buf += '\n';
    }
    pos = j + 1;
    return parseFlowText(buf, (w) => fail(i, w));
  }

  function parseFlowText(src, flowFail) {
    let k = 0;
    const ws = () => { while (k < src.length && /\s/.test(src[k])) k++; };
    const scalar = (isKey) => {
      const c = src[k];
      if (c === '"' || c === "'") {
        const end = findQuoteEnd(src.slice(k), c);
        if (end < 0) flowFail('フローの中の引用符が閉じていない');
        const v = decodeYamlQuoted(src.slice(k + 1, k + end), c, flowFail);
        k += end + 1;
        return v;
      }
      if (c === undefined || '&*!|>@`%'.includes(c)) flowFail(`フローの中の値を読めない（${c === undefined ? '終わり' : c}）`);
      const s0 = k;
      while (k < src.length) {
        const ch = src[k];
        if (',[]{}'.includes(ch)) break;
        if (ch === ':' && (k + 1 >= src.length || /[\s,[\]{}]/.test(src[k + 1]))) break;
        k++;
      }
      const raw = src.slice(s0, k).trim().replace(/[ \t]*\n[ \t]*/g, ' ');
      if (raw === '') flowFail('フローの中に空の要素がある');
      return isKey ? raw : resolveYamlPlain(raw);
    };
    const value = () => {
      ws();
      if (src[k] === '[') {
        k++;
        const arr = [];
        for (;;) {
          ws();
          if (src[k] === ']') { k++; return arr; }
          arr.push(value());
          ws();
          if (src[k] === ',') { k++; continue; }
          if (src[k] === ']') { k++; return arr; }
          flowFail('フローの列を読めない（, か ] が無い）');
        }
      }
      if (src[k] === '{') {
        k++;
        const obj = {};
        for (;;) {
          ws();
          if (src[k] === '}') { k++; return obj; }
          const key = String(scalar(true));
          ws();
          if (src[k] !== ':') flowFail(`フローの写像のキー ${key} の後に : が無い`);
          k++;
          ws();
          const v = src[k] === ',' || src[k] === '}' ? null : value();
          if (key === '__proto__' || Object.prototype.hasOwnProperty.call(obj, key)) flowFail(`キー ${key} が重複している`);
          obj[key] = v;
          ws();
          if (src[k] === ',') { k++; continue; }
          if (src[k] === '}') { k++; return obj; }
          flowFail('フローの写像を読めない（, か } が無い）');
        }
      }
      return scalar(false);
    };
    const v = value();
    ws();
    if (k < src.length) flowFail('フローの後に余分な文字がある');
    return v;
  }

  const first = nextSig();
  if (first >= 0 && lines[first].text.startsWith('%')) fail(first, 'ディレクティブは読まない');
  if (first >= 0 && lines[first].indent === 0 && /^---(?:\s|$)/.test(lines[first].text)) {
    if (restOf(lines[first].text.slice(3)) !== '') fail(first, '`---` の行に内容を置く形は読まない');
    pos = first + 1;
  }
  const root = parseBlockNode(-1);
  const rest = nextSig();
  if (rest >= 0) {
    fail(rest, /^(?:---|\.\.\.)(?:\s|$)/.test(lines[rest].text) ? '複数文書は読まない' : '字下げが合わない（読み残しがある）');
  }
  return root;
}

const isYamlMap = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const yamlScalarText = (v) => (v === null || v === undefined || typeof v === 'object' ? null : String(v));

/** data の 1 要素（refId・datasourceUid と model の type・expression・expr・conditions[].evaluator）。 */
function readDataNode(d) {
  const item = isYamlMap(d) ? d : {};
  const model = isYamlMap(item.model) ? item.model : {};
  const conditions = Array.isArray(model.conditions) ? model.conditions : [];
  return {
    refId: yamlScalarText(item.refId),
    datasourceUid: yamlScalarText(item.datasourceUid),
    type: yamlScalarText(model.type),
    expression: yamlScalarText(model.expression),
    // expr が文字列でなければ null を 1 件入れる（鎖の側で違反にする）。
    exprs: model.expr === undefined ? [] : [typeof model.expr === 'string' ? model.expr : null],
    // Grafana の params は []float64（数でない要素は受理されない）。数でなければ NaN にして「解釈できない」へ倒す。
    evaluators: conditions.map((c) => {
      const e = isYamlMap(c) && isYamlMap(c.evaluator) ? c.evaluator : null;
      if (!e) return { type: null, params: [] };
      return {
        type: yamlScalarText(e.type),
        params: Array.isArray(e.params) ? e.params.map((x) => (typeof x === 'number' ? x : Number.NaN)) : [],
      };
    }),
  };
}

/**
 * Grafana provisioning を YAML の木として読み、各ルールの title・condition・data のノードを返す（#1595）。
 * 読めなければ YamlSubsetError を投げる。互換のため、ルール全体の exprs / evaluators も返す。
 */
function grafanaRuleConditions(text) {
  const doc = parseYamlSubset(text);
  if (!isYamlMap(doc) || !Array.isArray(doc.groups)) throw new YamlSubsetError('トップレベルに groups の列が無い');
  const rules = [];
  doc.groups.forEach((g, gi) => {
    if (!isYamlMap(g) || !Array.isArray(g.rules)) throw new YamlSubsetError(`groups[${gi}] に rules の列が無い`);
    g.rules.forEach((r, ri) => {
      if (!isYamlMap(r)) throw new YamlSubsetError(`groups[${gi}].rules[${ri}] が写像ではない`);
      const nodes = Array.isArray(r.data) ? r.data.map(readDataNode) : [];
      rules.push({
        title: yamlScalarText(r.title) ?? `(title なし: groups[${gi}].rules[${ri}])`,
        condition: yamlScalarText(r.condition),
        nodes,
        exprs: nodes.flatMap((n) => n.exprs),
        evaluators: nodes.flatMap((n) => n.evaluators),
      });
    });
  });
  return rules;
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
    return { error: `condition ${condition} の評価器（conditions[].evaluator）を ${cond.evaluators.length} 件読んだ（Grafana の threshold は 1 件だけを受理する）` };
  }
  if (cond.evaluators[0].type === null) {
    return { error: `condition ${condition} の評価器（evaluator: { type, params }）を読めない` };
  }
  if (!cond.expression) return { error: `condition ${condition} の expression を読めない` };
  if (cond.expression.startsWith('$')) {
    return {
      error: `threshold ${condition} の expression が ${cond.expression} —— Grafana の threshold は $ を剥がさず、そのまま refId として引く` +
        '（剥がすのは reduce / resample だけ）ので、この式は評価できない。refId をそのまま書くこと（例: expression: A。#1595）',
    };
  }
  const src = byRef.get(cond.expression);
  if (!src) return { error: `threshold ${condition} の expression ${cond.expression} を refId に持つ data が無い` };
  if (BUILTIN_DATASOURCE_UIDS.has(src.datasourceUid)) {
    return { unverifiable: `クエリと threshold の間に ${src.type || '不明'} の段（refId ${src.refId}）がある —— 段を通った後の値は解釈しない` };
  }
  if (src.exprs.length !== 1) return { error: `refId ${src.refId} の expr を ${src.exprs.length} 件読んだ（1 件であること）` };
  if (src.exprs[0] === null) return { error: `refId ${src.refId} の expr が文字列ではない` };
  return { expr: src.exprs[0], evaluator: cond.evaluators[0] };
}

const fmtNum = (v) => (v === INF ? '+Inf' : v === -INF ? '-Inf' : `${v}`);
const describeSet = (xs) => (xs.length === 0 ? '∅（空。何も残らない）' : xs.map((x) => (x.lo === x.hi
  ? fmtNum(x.lo)
  : `${x.loInc ? '[' : '('}${fmtNum(x.lo)}, ${fmtNum(x.hi)}${x.hiInc ? ']' : ')'}`)).join(' ∪ '));

/** 許可リストの理由が空でない文字列か（#1595）。 */
const validAllowlistReason = (reason) => typeof reason === 'string' && reason.trim() !== '';

/**
 * 6 の本体。`label` は違反文に付ける写しの名前（compose / k8s inline）。
 * 戻り値: { issues, checked, allowlisted }（checked は組み合わせを判定できたルール数。0 件走査の門に使う）。
 */
function filterEvaluatorIssues(text, label, allowlist = UNVERIFIABLE_ALLOWLIST) {
  const issues = [];
  const allowlisted = [];
  let checked = 0;
  const has = (t) => Object.prototype.hasOwnProperty.call(allowlist, t);
  for (const t of Object.keys(allowlist)) {
    if (!validAllowlistReason(allowlist[t])) {
      issues.push(
        `[${label}] UNVERIFIABLE_ALLOWLIST のルール ${t} の理由が空 —— レビューを経た理由を書くこと` +
        '（理由の無い除外は黙って効く抜け道になるので、この項目では黙らせない。#1595）',
      );
    }
  }
  let rules;
  try {
    rules = grafanaRuleConditions(text);
  } catch (e) {
    if (!(e instanceof YamlSubsetError)) throw e;
    issues.push(`[${label}] provisioning を YAML として読めない（${e.message}）—— 式と評価器を 1 件も確かめられない（読めないまま素通りさせない。#1595）`);
    return { issues, checked, allowlisted };
  }
  const report = (title, why) => {
    if (has(title) && validAllowlistReason(allowlist[title])) { allowlisted.push(title); return; }
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
      issues.push(
        `[${label}] ルール ${title}: 評価器 ${type} [${params.join(', ')}] を解釈できない` +
        (params.some((x) => Number.isNaN(x)) ? '（params は数で書くこと。Grafana は []float64 として読む）' : '（型を本検査へ足すこと）'),
      );
      continue;
    }
    const why = expressionUnverifiableReason(chain.expr);
    if (why) { report(title, why); continue; }
    if (has(title)) {
      issues.push(`[${label}] ルール ${title}: UNVERIFIABLE_ALLOWLIST に載っているが検証できる —— 許可リストから外すこと（許可リストを腐らせない）`);
    }
    const got = expressionValueSet(chain.expr);
    checked++;
    if (got.length === 0) {
      issues.push(
        `[${label}] ルール ${title}: expr は決して値を返さない（空集合）—— 評価器 ${type} [${params.join(', ')}] は一度も真にならず、` +
        '永久に発火しない（noDataState によっては常に NoData / Alerting になる。#1595）。空になる絞り込み・照合を外すこと',
      );
    } else if (!setsIntersect(got, want)) {
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
    // #1595: 変異は YAML として正しい形にする（`evaluator: type: lt` は YAML として読めず、別の違反で赤になる）。
    const r = findIssues(withRule('up{job="x"}', 'lt'));
    assert.ok(r.issues.some((x) => x.includes('評価器（evaluator: { type, params }）を読めない')), JSON.stringify(r.issues));
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

  // ---- #1595: YAML の読み取り ----
  /** 式が検証でき、値が任意（±Inf を含む全域）であること。検証できなければ理由つきで落とす。 */
  const allValues = (e) => {
    const s = expressionValueSet(e);
    assert.ok(s !== null, `${e}: 検証できない（${expressionUnverifiableReason(e)}）`);
    return isAll(s);
  };
  const EXPR_LINE = "              expr: 'up{job=\"x\"}'";
  const withExprLines = (lines, evaluator = '{ type: gt, params: [0] }') => {
    const g = base.grafana.replace(EXPR_LINE, lines).replace('{ type: lt, params: [1] }', evaluator);
    return { ...base, grafana: g, k8sInline: g };
  };
  const neverFiresFor = (r, title = 'Foo') => ['compose', 'k8s inline'].every((label) =>
    r.issues.some((x) => x.startsWith(`[${label}] ルール ${title}:`) && x.includes('永久に発火しない')));

  // 🔴 既存の不具合: `expr: up` の次の行の `== 0` を読まず、`up` だけとして `gt 0` と組んで通していた。
  t('YAML: 複数行に続く plain の expr（expr: up の次の行の == 0）を続きまで読み、gt 0 との組み合わせを検出する（#1595・変異試験）', () => {
    const r = findIssues(withExprLines('              expr: up{job="x"}\n                == 0'));
    assert.ok(neverFiresFor(r), JSON.stringify(r.issues));
    assert.deepStrictEqual(grafanaRuleConditions(withExprLines('              expr: up\n                == 0').grafana)[0].exprs, ['up == 0']);
  });

  t('YAML: 行末コメント・>- / |2- のブロック・複数行の引用符を読み、同じ式として判定する（#1595）', () => {
    for (const lines of [
      '              expr: up == 0 # 行末コメント',
      '              expr: >-\n                up\n                == 0',
      '              expr: |2-\n                up\n                  == 0',
      "              expr: 'up\n                == 0'",
      '              expr: "up\n                == 0"   # 引用符の後のコメント',
    ]) {
      const r = findIssues(withExprLines(lines));
      assert.ok(neverFiresFor(r), `${lines}: ${JSON.stringify(r.issues)}`);
    }
    // 行末コメントのある refId / type / title / condition も読む（陰性対照: 生の up を lt 1 で比べる形は通る）。
    const g = base.grafana
      .replace('title: Foo', 'title: Foo # コメント').replace('condition: C\n', 'condition: C   # コメント\n')
      .replace('- refId: A\n', '- refId: A # コメント\n').replace('type: threshold', 'type: threshold # コメント');
    assert.deepStrictEqual(findIssues({ ...base, grafana: g, k8sInline: g }).issues, []);
  });

  t('YAML: Grafana のエクスポート順 { params, type }・ブロックの写像・複数行のフローの評価器を読む（#1595・変異試験）', () => {
    for (const ev of [
      '{ params: [0], type: gt }',
      '\n                    params:\n                      - 0\n                    type: gt',
      '{\n                    params: [0],\n                    type: gt }',
    ]) {
      const r = findIssues(withExprLines('              expr: up == 0', ev));
      assert.ok(neverFiresFor(r), `${ev}: ${JSON.stringify(r.issues)}`);
    }
    // params が数でない（Grafana は []float64 として読む）→ 解釈できない
    const s = findIssues(withExprLines('              expr: up', '{ params: ["1"], type: lt }'));
    assert.ok(s.issues.some((x) => x.includes('params は数で書くこと')), JSON.stringify(s.issues));
  });

  t('YAML: 読めない YAML（値の続きの「: 」・タブの字下げ・アンカー・重複キー）は写しごとに違反にする（fail-closed・#1595）', () => {
    for (const lines of [
      '              expr: up\n                == 0: x',
      '\t            expr: up',
      '              expr: &e up',
      '              expr: up\n              expr: up',
    ]) {
      const r = findIssues(withExprLines(lines));
      for (const label of ['compose', 'k8s inline']) {
        assert.ok(r.issues.some((x) => x.startsWith(`[${label}] provisioning を YAML として読めない`)), `${JSON.stringify(lines)}: ${JSON.stringify(r.issues)}`);
      }
    }
  });

  t('threshold の expression: $A を違反にする（Grafana の threshold は $ を剥がさない・#1595・変異試験）', () =>
    chainIssue(mutateFoo((f) => f.replace('expression: A', 'expression: $A')), 'Grafana の threshold は $ を剥がさず'));

  // ---- #1595: 空集合（決して値を返さない式）----
  const emptyFor = (r) => r.issues.some((x) => x.startsWith('[compose] ルール Foo:') && x.includes('決して値を返さない'));
  t('空集合: 同じ選択子の矛盾する絞り込み（up == 0 and up == 1）と up unless up を「決して値を返さない」として検出する（#1595・変異試験）', () => {
    for (const e of ['up{job="x"} == 0 and up{job="x"} == 1', 'up{job="x"} unless up{job="x"}', 'up{job="x"} == 0 unless up{job="x"}',
      'sum(up) > 0 and sum(up) < 0', 'rate(m[5m]) == 0 and rate(m[5m]) == 1', 'max(up{job="x"} == 0 and up{job="x"} == 1)',
      // 空の伝播: 右辺が空の and は空（右辺の中の矛盾は左辺と別の式でも効く）
      'up{job="x"} and (m == 0 and m == 1)', '(m == 0 and m == 1) + up{job="x"}']) {
      assert.deepStrictEqual(expressionValueSet(e), [], e);
      assert.ok(emptyFor(findIssues(withRule(e, '{ type: lt, params: [100] }'))), e);
    }
    // 陰性対照: 交わりが残る形は交わりの値（空ではない）。unless は補集合で絞る。
    assert.deepStrictEqual(expressionValueSet('up == 0 and up >= 0'), [point(0)]);
    assert.deepStrictEqual(expressionValueSet('up unless up != 0'), [point(0)]);
  });

  t('空集合: ラベルの矛盾（x{job="a"} and on(job) y{job="b"}・x{job="a"} and vector(1)）を検出し、on() / ignoring(job) の陰性対照は通す（#1595）', () => {
    for (const e of ['up{job="a"} and on(job) m{job="b"}', 'up{job="a"} and m{job="b"}', 'up{job="a"} and vector(1)',
      'sum by (job) (up{job="a"}) and on (job) sum by (job) (m{job="b"})', 'absent(up{job="a"}) and on(job) m{job="b"}',
      'up{job="a"} + m{job="b"}', 'up{job="a"} > m{job="b"}']) {
      assert.deepStrictEqual(expressionValueSet(e), [], e);
    }
    for (const e of ['up{job="a"} and on() m{job="b"}', 'up{job="a"} and ignoring(job) m{job="b"}', "up{job='a'} and m{job=\"a\"}",
      'sum by (job) (up) and on(job) m{job="b"}', 'up{job="a"} and on() vector(1)', 'up{job="a"} unless m{job="b"}']) {
      assert.ok(allValues((e)), e);
    }
  });

  t('空集合: 健全に判定できない同型（名前の無い選択子・照合の修飾つき・NaN だけが残り得る unless）は「検証できない」と報告する（#1595）', () => {
    for (const e of ['{job="a"} == 0 and {job="a"} == 1', 'up == 0 and on(job) up == 1', 'up unless up >= -Inf',
      'sum by (__name__) ({job="a"}) == 0 and sum by (__name__) ({job="a"}) == 1']) {
      assert.strictEqual(expressionValueSet(e), null, e);
      assert.ok(unverifiableFor(findIssues(withRule(e, '{ type: lt, params: [100] }'))), e);
    }
  });

  t('同じ式の差 up - up は {0}: gt 0 との組み合わせを検出する（#1595・変異試験）', () => {
    assert.deepStrictEqual(expressionValueSet('up - up'), [point(0)]);
    assert.deepStrictEqual(expressionValueSet('sum(rate(m[5m])) / sum(rate(m[5m]))'), [point(1)]);
    assert.ok(neverFires(findIssues(withRule('up{job="x"} - up{job="x"}', '{ type: gt, params: [0] }'))));
    // 陰性対照: 名前の無い選択子・照合の修飾つきは定数に畳まない（任意の値のまま）
    assert.ok(allValues(('{job="a"} - {job="a"}')));
    assert.ok(allValues(('up - on(job) up')));
  });

  t('or on() / or ignoring() の右辺の修飾を剥がして読む（x == 0 or on() vector(0) を gt 0 で検出・lt 1 は通す。#1595）', () => {
    for (const e of ['up{job="x"} == 0 or on() vector(0)', 'up{job="x"} == 0 or ignoring(job) vector(0)']) {
      assert.deepStrictEqual(expressionValueSet(e), [point(0)], e);
      assert.ok(neverFires(findIssues(withRule(e, '{ type: gt, params: [0] }'))), e);
      assert.deepStrictEqual(findIssues(withRule(e, '{ type: lt, params: [1] }')).issues, [], e);
    }
  });

  t('単項の符号は ^ より弱い（-2 ^ 2 は -4。#1595）', () => {
    assert.deepStrictEqual(expressionValueSet('vector(-2 ^ 2)'), [point(-4)]);
    assert.deepStrictEqual(expressionValueSet('vector(2 ^ -1)'), [point(0.5)]);
    assert.deepStrictEqual(expressionValueSet('vector(-2 * 3)'), [point(-6)]);
    // up == -2 ^ 2 は {-4}: gt 0 で「永久に発火しない」、lt 0 では通る（4 と読むと逆になる）
    assert.ok(neverFires(findIssues(withRule('up{job="x"} == -2 ^ 2', '{ type: gt, params: [0] }'))));
    assert.deepStrictEqual(findIssues(withRule('up{job="x"} == -2 ^ 2', '{ type: lt, params: [0] }')).issues, []);
  });

  t('任意の値は ±Inf を含む（up == +Inf を「決して発火しない」と読まない・#1595）', () => {
    assert.deepStrictEqual(expressionValueSet('up == +Inf'), [point(INF)]);
    assert.deepStrictEqual(findIssues(withRule('up{job="x"} == +Inf', '{ type: gt, params: [0] }')).issues, []);
    assert.deepStrictEqual(findIssues(withRule('up{job="x"} == -Inf', '{ type: lt, params: [0] }')).issues, []);
  });

  t('関数呼び出しへのサブクエリ（max_over_time(rate(x[5m])[30m:1m])）を読み、比較を包むサブクエリは報告する（#1595）', () => {
    for (const e of ['max_over_time(rate(m[5m])[30m:1m])', 'max_over_time(rate(m[5m])[30m:1m] offset 5m)', 'rate(m[5m])[30m:]']) {
      assert.ok(allValues((e)), `${e}: ${expressionUnverifiableReason(e)}`);
    }
    assert.ok(unverifiableFor(findIssues(withRule('max_over_time((up{job="x"} == 0)[30m:1m])', '{ type: gt, params: [0] }'))));
  });

  t('@ を offset より前に書いた選択子（x @ 123 offset 5m）を読む（#1595）', () => {
    for (const e of ['m @ 123 offset 5m', 'm offset 5m @ start()', 'rate(m[5m] @ end() offset 1h)']) {
      assert.ok(allValues((e)), `${e}: ${expressionUnverifiableReason(e)}`);
    }
  });

  t('名前が :offset / :bool で終わるメトリクスを修飾子として読まない（#1595・変異試験）', () => {
    assert.ok(allValues(('job:m:offset - 1')), expressionUnverifiableReason('job:m:offset - 1'));
    assert.deepStrictEqual(expressionValueSet('job:up:bool == 0'), [point(0)]);
    assert.ok(neverFires(findIssues(withRule('job:up:bool == 0', '{ type: gt, params: [0] }'))));
    assert.ok(allValues(('up == bool:m')), '`== bool:m` はベクタどうしの比較（bool 修飾子ではない）');
  });

  t('UNVERIFIABLE_ALLOWLIST: 理由が空・空白だけ・文字列でない項目は違反にし、その項目では黙らせない（#1595・変異試験）', () => {
    const g = withRule('max(up{job="x"} == 0)', '{ type: gt, params: [0] }').grafana;
    for (const reason of ['', '   ', null, 1]) {
      const r = filterEvaluatorIssues(g, 'compose', { Foo: reason });
      assert.ok(r.issues.some((x) => x.includes('ルール Foo の理由が空')), `${JSON.stringify(reason)}: ${JSON.stringify(r.issues)}`);
      assert.ok(r.issues.some((x) => x.includes('式を検証できない')), `${JSON.stringify(reason)}: 空の理由で黙らせた`);
      assert.deepStrictEqual(r.allowlisted, []);
    }
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
