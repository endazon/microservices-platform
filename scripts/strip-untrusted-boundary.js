#!/usr/bin/env node
'use strict';
/*
 * strip-untrusted-boundary.js
 *
 * FR-06, UC-03 / IADR-0555 (#1973。AST#1359 / AST#1360 の後始末):
 *   AST が 2026-10-16 まで境界語（`<<<UNTRUSTED_DATA` … `UNTRUSTED_DATA>>>`）で囲んだまま KB へ保存した文書（project = ai-stock-trading）の
 *   表題・本文から、**先頭と末尾の枠だけ**を剥がす**一回限りの管理操作**の駆動器。剥がした文書は、単票の更新と同じイベントで
 *   索引（語彙・埋め込み）と Wiki へ同期し直される。
 *
 * 方式（IADR-0555）:
 *   - 確かめと書き換えは DocumentService の管理者だけの口 `POST /documents/strip-untrusted-boundary` が行う
 *     （本文を読み、枠の形なら剥がした本文を新しいキーへ書いて台帳の参照を付け替える。状態・属性・タグは動かさない）。
 *     本スクリプトは**量の制御・進捗・中断と再開・キューと DLQ の監視**だけを持つ（一括アーカイブの駆動器と同じ本体 lib/admin-page-driver.js）。
 *   - 🔴 冪等。剥がした後の値は枠の形をしないので、2 回流しても 2 回目は何も変えない（止まった後の --resume も安全）。
 *   - 🔴 剥がすと取り込みが索引を作り直す（埋め込みの費用がかかる）。カナリアと見張るキューの深さで量を抑える。
 *
 * 🔴 **稼働クラスタへ当たる。** `--live`（または LIVE=1）が無ければ何もせずに終わる（#1550）。
 *
 * 実行方法（PoC の手順は IADR-0555 §PoC の手順）:
 *   node scripts/strip-untrusted-boundary.js --help
 *   node scripts/strip-untrusted-boundary.js --live --dry-run --operator alice
 *   node scripts/strip-untrusted-boundary.js --live --operator alice --reason "#1973（カナリア）" --canary 20 --max-pages 1
 *   node scripts/strip-untrusted-boundary.js --live --resume --operator alice --reason "#1973"
 *
 * 主な環境変数（一括アーカイブの駆動器と同じ名前を読む）:
 *   REPUBLISH_DOCUMENT_URL / REPUBLISH_KC_URL（与えれば port-forward を張らない。張るときは kubectl で document-service と keycloak へ）
 *   REPUBLISH_NS / REPUBLISH_INFRA_NS / REPUBLISH_REALM / REPUBLISH_CLIENT_ID / REPUBLISH_CLIENT_SECRET / REPUBLISH_DLQ / REPUBLISH_POLL_MS
 *   ARCHIVE_WATCH_QUEUES（既定 ingestion-service.DocumentUpdated,wiki-service.DocumentUpdated,graph-service.DocumentUpdated）
 *
 * 終了コード: 0=完了（または --max-pages で区切って止めた） / 1=失敗・安全のための停止（状態は残る） /
 *            2=前提未整備（k8s へ到達できない・キューを読めない等） / 3=明示の指定が無い（#1550）
 */

const { requireLiveOptIn } = require('./lib/live-opt-in.js');
const driver = require('./lib/admin-page-driver.js');

const ROUTE = '/documents/strip-untrusted-boundary';
const DEFAULTS = Object.freeze({
  ...driver.COMMON_DEFAULTS,
  stateFile: 'strip-untrusted-boundary.state.json',
});
const STATE_VERSION = 1;
const STATE_KIND = 'strip-untrusted-boundary';
// 口が見る母集合（口の側で固定。ここでは説明に使うだけ）。
const TARGET = 'project = ai-stock-trading の未アーカイブの組織文書';

const USAGE = `使い方: node scripts/strip-untrusted-boundary.js --live [オプション]
${driver.COMMON_USAGE}
  （--dry-run は書き換えず、全ページの表題・本文を確かめて件数と対象の文書 ID を出す。状態ファイルの既定は ./${DEFAULTS.stateFile}）`;

// --- 純粋関数（実機なしで試験できるように切り出す） ---------------------------------

/** 引数を解析する。知らない引数・値の欠けた引数は落とす（黙って既定へ倒さない）。 */
function parseArgs(argv) {
  const o = driver.commonOptions(DEFAULTS.stateFile);
  for (let i = 0; i < argv.length; i++) {
    const used = driver.parseCommonArg(o, argv, i);
    if (used === 0) throw new Error(`知らない引数です: ${argv[i]}（--help を参照）`);
    i += used - 1;
  }
  if (o.help) return o;
  driver.checkCommonCombination(o);
  return o;
}

/** 実行の前提。書き換える実行（再開を含む）は理由が必須。違反を文字列で返す（null なら続けてよい）。 */
function preconditionProblem(o) {
  if (o.help || o.dryRun || o.reason) return null;
  return '書き換える実行では --reason <理由> が必須です（口のログへ残す。dry-run では任意）。';
}

/** 口へ送る要求の本文（絞り込みは口の側で固定なので送らない）。 */
function buildRequestBody({ dryRun, limit, cursor, requestedBy, reason }) {
  const body = { dryRun: Boolean(dryRun), limit };
  if (cursor) body.cursor = cursor;
  if (requestedBy) body.requestedBy = requestedBy;
  if (reason) body.reason = reason;
  return body;
}

const emptyTotals = () => ({
  pages: 0, selected: 0, wrapped: 0, titleWrapped: 0, bodyWrapped: 0, bodyMissing: 0, notStored: 0, titleLeftWrapped: 0, stripped: 0,
});

/** ページの応答を累計へ積む（新しい累計を返す）。 */
function addTotals(t, res) {
  const n = (k) => t[k] + (res[k] || 0);
  return {
    pages: t.pages + 1,
    selected: n('selected'),
    wrapped: n('wrapped'),
    titleWrapped: n('titleWrapped'),
    bodyWrapped: n('bodyWrapped'),
    bodyMissing: n('bodyMissing'),
    notStored: n('notStored'),
    titleLeftWrapped: n('titleLeftWrapped'),
    stripped: n('stripped'),
  };
}

/** 対象の文書の 1 行（ID と、表題・本文のどちらに枠があったか）。 */
function itemLine(item) {
  const parts = [item.title ? '表題' : null, item.body ? '本文' : null].filter(Boolean).join('・');
  return `    ${item.id}（${parts}）`;
}

/** dry-run の要約（全ページを辿った累計から作る）。 */
function summarizeDryRun(totals) {
  return [
    `${TARGET}のうち:`,
    `  確かめた: ${totals.selected} 件`,
    `    枠を剥がす対象: ${totals.wrapped} 件（表題 ${totals.titleWrapped} 件・本文 ${totals.bodyWrapped} 件）`,
    `    本文のオブジェクトが無い（表題だけを見た）: ${totals.bodyMissing} 件`,
    `    本文の参照が無い・storage:// でない（表題だけを見た）: ${totals.notStored} 件`,
    `    剥がすと空になるので表題を残す: ${totals.titleLeftWrapped} 件`,
  ];
}

function newState(o, now = new Date()) {
  return driver.commonNewState({ kind: STATE_KIND, version: STATE_VERSION, filters: {}, totals: emptyTotals() }, now);
}

function stateProblem(state) {
  const common = driver.commonStateProblem(state, STATE_KIND, STATE_VERSION);
  if (common) return common;
  if (typeof state.totals.stripped !== 'number') return '状態ファイルの累計が不正です。';
  return null;
}

function formatProgress(state, res, elapsedMs) {
  const t = state.totals;
  return `[page ${t.pages}] 確かめた ${res.selected}（枠あり ${res.wrapped}: 表題 ${res.titleWrapped} / 本文 ${res.bodyWrapped}）`
    + ` 剥がした ${res.stripped} ・累計 剥がした ${t.stripped} ・${Math.round(elapsedMs / 1000)}s`;
}

const PROFILE = Object.freeze({
  name: 'strip-untrusted-boundary',
  // NFR, #1550: 稼働クラスタへ当たる前の明示の指定の確認（本体が --help の後に呼ぶ）。
  requireLive: (argv) => requireLiveOptIn('strip-untrusted-boundary', argv, { offline: '--help' }),
  route: ROUTE,
  usage: USAGE,
  verb: '書き換え',
  parseArgs,
  preconditionProblem,
  newState,
  stateProblem,
  filtersOf: () => ({}),
  describeFilters: () => TARGET,
  buildRequestBody: ({ filters: _filters, ...rest }) => buildRequestBody(rest),
  emptyTotals,
  addTotals,
  changedOf: (res) => res.stripped || 0,
  changedTotal: (t) => t.stripped,
  // 🔴 dry-run はページごとに対象の文書 ID を並べる（件数と ID の一覧を出すだけ。#1973）。
  dryRunPageLine: (t, res) => [
    `  [dry-run page ${t.pages}] 確かめた ${res.selected}（枠あり ${res.wrapped}: 表題 ${res.titleWrapped} / 本文 ${res.bodyWrapped}）`,
    ...(res.items || []).map(itemLine),
  ].join('\n'),
  summarizeDryRun: (_first, totals) => summarizeDryRun(totals),
  formatProgress,
  canaryLine: (n) => `カナリア ${n} 件: 見張るキューが空になり、DLQ は増えていません。SC-05 と Wiki で表題から境界語が消えたことを確かめてから続けてください。`,
  dlqHint: ['書き換えのイベントを受けた下流（取り込み・Wiki 同期・グラフ）のどれかが失敗しています。ログで原因を確かめてから --resume。'],
  doneLines: (t) => [
    `完了: 剥がした ${t.stripped} 件（表題 ${t.titleWrapped} 件・本文 ${t.bodyWrapped} 件）/ 確かめた ${t.selected} 件（${t.pages} ページ）。`,
    '確かめ方: もう一度 --dry-run を流し、「枠を剥がす対象」が 0 件であること。',
  ],
});

const main = (argv) => driver.run(PROFILE, argv);

module.exports = {
  main,
  DEFAULTS,
  STATE_VERSION,
  STATE_KIND,
  parseArgs,
  preconditionProblem,
  buildRequestBody,
  addTotals,
  summarizeDryRun,
  newState,
  stateProblem,
  formatProgress,
  itemLine,
  PROFILE,
};

if (require.main === module) {
  main(process.argv.slice(2))
    .then((code) => process.exit(code))
    .catch((e) => {
      process.stderr.write(`[strip-untrusted-boundary] ${e.message}\n`);
      process.exit(1);
    });
}
