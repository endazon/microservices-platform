#!/usr/bin/env node
'use strict';
/*
 * archive-ast-quote-documents.js
 *
 * FR-06, UC-03, SC-05 / IADR-0549（2026-10-17 追記 / #1974。AST#1366 の後始末）:
 *   AST が 2026-10-16 まで 5 分ごとに保存した相場スナップショット（project = ai-stock-trading・属性 kind = Quote）の文書を、
 *   既存の状態「アーカイブ」へ移す**一回限りの管理操作**の駆動器。アーカイブした文書は検索・RAG の候補から外れる（IADR-0551）。
 *
 * 方式（IADR-0549 の追記）:
 *   - 選び方とアーカイブは DocumentService の管理者だけの口 `POST /documents/archive-ast-quotes` が行う（条件は口の側で固定。本文は読まない）。
 *     遷移・下流への伝え方は本文の無い文書の一括アーカイブ（archive-bodyless-documents.js）と同じで、駆動の本体も同じ lib/admin-page-driver.js。
 *     本スクリプトは**量の制御・進捗・中断と再開・キューと DLQ の監視**だけを持つ。
 *   - 🔴 消さない（Delete しない）。台帳と本文は残る。
 *   - 🔴 冪等。アーカイブ済みは口の母集合から外れるので、2 回流しても 2 回目は何もしない（止まった後の --resume も安全）。
 *
 * 🔴 **稼働クラスタへ当たる。** `--live`（または LIVE=1）が無ければ何もせずに終わる（#1550）。
 *
 * 実行方法:
 *   node scripts/archive-ast-quote-documents.js --help
 *   node scripts/archive-ast-quote-documents.js --live --dry-run --operator alice
 *   node scripts/archive-ast-quote-documents.js --live --operator alice --reason "#1974（カナリア）" --canary 20 --max-pages 1
 *   node scripts/archive-ast-quote-documents.js --live --resume --operator alice --reason "#1974"
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

const ROUTE = '/documents/archive-ast-quotes';
const DEFAULTS = Object.freeze({
  ...driver.COMMON_DEFAULTS,
  stateFile: 'archive-ast-quote-documents.state.json',
});
const STATE_VERSION = 1;
const STATE_KIND = 'archive-ast-quotes';
// 口が見る母集合（口の側で固定。ここでは説明に使うだけ）。
const TARGET = 'project = ai-stock-trading かつ kind = Quote の未アーカイブの組織文書';

const USAGE = `使い方: node scripts/archive-ast-quote-documents.js --live [オプション]
${driver.COMMON_USAGE}
  （--dry-run はアーカイブせず、全ページを辿って件数と対象の文書 ID を出す。状態ファイルの既定は ./${DEFAULTS.stateFile}）`;

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

/** 実行の前提。アーカイブする実行（再開を含む）は理由が必須。違反を文字列で返す（null なら続けてよい）。 */
function preconditionProblem(o) {
  if (o.help || o.dryRun || o.reason) return null;
  return 'アーカイブする実行では --reason <理由> が必須です（口のログへ残す。dry-run では任意）。';
}

/** 口へ送る要求の本文（絞り込みは口の側で固定なので送らない）。 */
function buildRequestBody({ dryRun, limit, cursor, requestedBy, reason }) {
  const body = { dryRun: Boolean(dryRun), limit };
  if (cursor) body.cursor = cursor;
  if (requestedBy) body.requestedBy = requestedBy;
  if (reason) body.reason = reason;
  return body;
}

const emptyTotals = () => ({ pages: 0, selected: 0, archived: 0 });

function addTotals(t, res) {
  return { pages: t.pages + 1, selected: t.selected + (res.selected || 0), archived: t.archived + (res.archived || 0) };
}

function newState(o, now = new Date()) {
  return driver.commonNewState({ kind: STATE_KIND, version: STATE_VERSION, filters: {}, totals: emptyTotals() }, now);
}

function stateProblem(state) {
  const common = driver.commonStateProblem(state, STATE_KIND, STATE_VERSION);
  if (common) return common;
  if (typeof state.totals.archived !== 'number') return '状態ファイルの累計が不正です。';
  return null;
}

function formatProgress(state, res, elapsedMs) {
  const t = state.totals;
  return `[page ${t.pages}] 選んだ ${res.selected} ・アーカイブ ${res.archived} ・累計 アーカイブ ${t.archived} ・${Math.round(elapsedMs / 1000)}s`;
}

const PROFILE = Object.freeze({
  name: 'archive-ast-quote-documents',
  // NFR, #1550: 稼働クラスタへ当たる前の明示の指定の確認（本体が --help の後に呼ぶ）。
  requireLive: (argv) => requireLiveOptIn('archive-ast-quote-documents', argv, { offline: '--help' }),
  route: ROUTE,
  usage: USAGE,
  verb: 'アーカイブ',
  parseArgs,
  preconditionProblem,
  newState,
  stateProblem,
  filtersOf: () => ({}),
  describeFilters: () => TARGET,
  buildRequestBody: ({ filters: _filters, ...rest }) => buildRequestBody(rest),
  emptyTotals,
  addTotals,
  changedOf: (res) => res.archived || 0,
  changedTotal: (t) => t.archived,
  // 🔴 dry-run はページごとに対象の文書 ID を並べる（件数と ID の一覧を出すだけ。#1974）。
  dryRunPageLine: (t, res) => [`  [dry-run page ${t.pages}] 対象 ${res.selected}`, ...(res.ids || []).map((id) => `    ${id}`)].join('\n'),
  summarizeDryRun: (_first, totals) => [`${TARGET}のうち、アーカイブの対象: ${totals.selected} 件`],
  formatProgress,
  canaryLine: (n) => `カナリア ${n} 件: 見張るキューが空になり、DLQ は増えていません。SC-05 で「アーカイブ済み」に入ったことを確かめてから続けてください。`,
  dlqHint: ['アーカイブのイベントを受けた下流（取り込み・Wiki 同期・グラフ）のどれかが失敗しています。ログで原因を確かめてから --resume。'],
  doneLines: (t) => [
    `完了: アーカイブ ${t.archived} 件（${t.pages} ページ）。`,
    '確かめ方: もう一度 --dry-run を流し、「アーカイブの対象」が 0 件であること。',
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
  newState,
  stateProblem,
  formatProgress,
  PROFILE,
};

if (require.main === module) {
  main(process.argv.slice(2))
    .then((code) => process.exit(code))
    .catch((e) => {
      process.stderr.write(`[archive-ast-quote-documents] ${e.message}\n`);
      process.exit(1);
    });
}
