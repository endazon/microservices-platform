#!/usr/bin/env node
'use strict';
/*
 * archive-bodyless-documents.js
 *
 * FR-06, UC-03, SC-05 / IADR-0549 (#1931 項目 3。オーナー裁定 B・2026-10-15):
 *   9/26 の SeaweedFS への切り替えより前に作られ、本文のオブジェクトが無い（NoSuchKey）文書を、既存の状態「アーカイブ」へ移す
 *   **一回限りの管理操作**の駆動器。台帳と表題は残す。下流（取り込み・Wiki 同期）へは単票のアーカイブと同じイベントで伝わる。
 *
 * 方式（IADR-0549）:
 *   - 確かめとアーカイブは DocumentService の管理者だけの口 `POST /documents/archive-bodyless` が行う
 *     （本文の有無はオブジェクトストレージへの HEAD 相当で確かめる。本文を読まない。「無い」は NoSuchKey だけ）。
 *     本スクリプトは**量の制御・進捗・中断と再開・キューと DLQ の監視**だけを持つ（再発行の駆動器 republish-document-updated.js と同じ作法）。
 *     ［2026-10-17 / #1973 / IADR-0555］駆動の本体は lib/admin-page-driver.js へ切り出した（境界語を剥がす口と共有する）。
 *     ここに残るのは、この口に固有の引数（--created-before）・状態ファイルの絞り込み・集計・文言だけである。挙動は変えていない。
 *   - 認証は Keycloak の client_credentials（client `abac-seeder`。platform-admin を持つ）。I/O は再発行の駆動器の関数を再利用する。
 *   - 🔴 `--created-before` は**必須**（既定を持たない）。切り替え後の文書を巻き込む実行を組み立てられないようにする。
 *   - 🔴 冪等。アーカイブ済みは口の母集合から外れるので、2 回流しても 2 回目は何もしない（止まった後の --resume も安全）。
 *
 * 🔴 **稼働クラスタへ当たる。** `--live`（または LIVE=1）が無ければ何もせずに終わる（#1550）。
 *
 * 実行方法（PoC の手順は IADR-0549 §PoC の手順）:
 *   node scripts/archive-bodyless-documents.js --help
 *   node scripts/archive-bodyless-documents.js --live --dry-run --operator alice --created-before 2026-09-27T00:00:00Z
 *   node scripts/archive-bodyless-documents.js --live --operator alice --reason "#1931 項目 3（カナリア）" --created-before 2026-09-27T00:00:00Z --canary 20 --max-pages 1
 *   node scripts/archive-bodyless-documents.js --live --resume --operator alice --reason "#1931 項目 3"
 *
 * 主な環境変数（再発行の駆動器と同じ名前を読む）:
 *   REPUBLISH_DOCUMENT_URL / REPUBLISH_KC_URL（与えれば port-forward を張らない）
 *   REPUBLISH_NS / REPUBLISH_INFRA_NS / REPUBLISH_REALM / REPUBLISH_CLIENT_ID / REPUBLISH_CLIENT_SECRET / REPUBLISH_DLQ / REPUBLISH_POLL_MS
 *   ARCHIVE_WATCH_QUEUES（既定 ingestion-service.DocumentUpdated,wiki-service.DocumentUpdated,graph-service.DocumentUpdated）
 *
 * 終了コード: 0=完了（または --max-pages で区切って止めた） / 1=失敗・安全のための停止（状態は残る） /
 *            2=前提未整備（k8s へ到達できない・キューを読めない等） / 3=明示の指定が無い（#1550）
 */

const { requireLiveOptIn } = require('./lib/live-opt-in.js');
const driver = require('./lib/admin-page-driver.js');

const ROUTE = '/documents/archive-bodyless';
const DEFAULTS = Object.freeze({
  ...driver.COMMON_DEFAULTS,
  stateFile: 'archive-bodyless-documents.state.json',
});
const STATE_VERSION = 1;
const STATE_KIND = 'archive-bodyless';

const USAGE = `使い方: node scripts/archive-bodyless-documents.js --live [オプション]
  --created-before <ISO8601>    この時刻より前に作られた文書だけ（必須。--resume では状態ファイルのものを使う）
${driver.COMMON_USAGE}
  （--dry-run はアーカイブせず、全ページの本文の有無を確かめて件数と内訳を出す。状態ファイルの既定は ./${DEFAULTS.stateFile}）`;

// --- 純粋関数（実機なしで試験できるように切り出す） ---------------------------------

/** 引数を解析する。知らない引数・値の欠けた引数は落とす（黙って既定へ倒さない）。 */
function parseArgs(argv) {
  const o = { ...driver.commonOptions(DEFAULTS.stateFile), createdBefore: null };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--created-before') {
      const raw = driver.argValue(argv, i, a);
      const t = Date.parse(raw);
      if (Number.isNaN(t)) throw new Error(`--created-before は ISO 8601 の時刻で指定してください（${raw}）。`);
      o.createdBefore = new Date(t).toISOString();
      i++;
      continue;
    }
    const used = driver.parseCommonArg(o, argv, i);
    if (used === 0) throw new Error(`知らない引数です: ${a}（--help を参照）`);
    i += used - 1;
  }
  if (o.help) return o;
  driver.checkCommonCombination(o);
  if (o.resume && o.createdBefore) throw new Error('--resume では --created-before を指定しません（状態ファイルのものを使う）。');
  return o;
}

/**
 * 実行の前提（必須の指定）。`--live` の判定（exit 3）より後で見る —— 指定の無い素の実行は、何が欠けていても何もせずに終わる（#1550）。
 * 🔴 `--created-before` は必須（既定を持たない）。アーカイブする実行（再開を含む）は理由も必須。違反を文字列で返す（null なら続けてよい）。
 */
function preconditionProblem(o) {
  if (o.help) return null;
  if (!o.resume && !o.createdBefore)
    return '--created-before <ISO8601> は必須です（この時刻より前に作られた文書だけが対象。既定はありません）。';
  if (o.dryRun || o.reason) return null;
  return 'アーカイブする実行では --reason <理由> が必須です（口のログへ残す。dry-run では任意）。';
}

/** 口へ送る要求の本文。 */
function buildRequestBody({ dryRun, createdBefore, limit, cursor, requestedBy, reason }) {
  const body = { dryRun: Boolean(dryRun), createdBefore, limit };
  if (cursor) body.cursor = cursor;
  if (requestedBy) body.requestedBy = requestedBy;
  if (reason) body.reason = reason;
  return body;
}

const emptyTotals = () => ({ pages: 0, archived: 0, bodyMissing: 0, bodyPresent: 0, skippedPrivateNote: 0 });

/** ページの応答を累計へ積む（新しい累計を返す）。 */
function addTotals(t, res) {
  return {
    pages: t.pages + 1,
    archived: t.archived + (res.archived || 0),
    bodyMissing: t.bodyMissing + (res.bodyMissing || 0),
    bodyPresent: t.bodyPresent + (res.bodyPresent || 0),
    skippedPrivateNote: t.skippedPrivateNote + (res.skippedPrivateNote || 0),
  };
}

/** dry-run の要約（全ページを辿った累計と、最初のページの母集合の件数から作る）。 */
function summarizeDryRun(first, totals, createdBefore) {
  return [
    `createdBefore ${createdBefore} より前に作られた文書のうち:`,
    `  確かめた（未アーカイブ・本文の参照が storage://）: ${first.matched} 件`,
    `    本文のオブジェクトが無い（NoSuchKey）＝アーカイブの対象: ${totals.bodyMissing} 件`,
    `    本文が在る（触らない）: ${totals.bodyPresent} 件`,
    `    個人資料（対象外。触らない）: ${totals.skippedPrivateNote} 件`,
    `  既にアーカイブ済み（対象外）: ${first.alreadyArchived} 件`,
    `  本文の参照が無い・storage:// でない（対象外）: ${first.notStored} 件`,
  ];
}

/** 新規の走査の状態。 */
function newState(o, now = new Date()) {
  return driver.commonNewState({ kind: STATE_KIND, version: STATE_VERSION, filters: { createdBefore: o.createdBefore }, totals: emptyTotals() }, now);
}

/** 読んだ状態ファイルが続けてよい形か。違反を文字列で返す（null なら続けてよい）。 */
function stateProblem(state) {
  const common = driver.commonStateProblem(state, STATE_KIND, STATE_VERSION);
  if (common) return common;
  if (typeof state.filters.createdBefore !== 'string' || Number.isNaN(Date.parse(state.filters.createdBefore)))
    return '状態ファイルに絞り込み（createdBefore）がありません。';
  if (typeof state.totals.archived !== 'number') return '状態ファイルの累計が不正です。';
  return null;
}

/** 1 ページの応答を状態へ積む（新しい状態を返す）。confirmedCursor は動かさない。 */
const applyPage = (state, res) => driver.applyPage(state, res, addTotals);
const { watchedDepth } = driver;

function formatProgress(state, res, elapsedMs) {
  const t = state.totals;
  return `[page ${t.pages}] 確かめた ${res.selected}（本文なし ${res.bodyMissing} / 本文あり ${res.bodyPresent} / 個人資料 ${res.skippedPrivateNote}）`
    + ` アーカイブ ${res.archived} ・残り ${Math.max(0, res.remaining - res.selected)} ・累計 アーカイブ ${t.archived} ・${Math.round(elapsedMs / 1000)}s`;
}

const PROFILE = Object.freeze({
  name: 'archive-bodyless-documents',
  // NFR, #1550: 稼働クラスタへ当たる前の明示の指定の確認（本体が --help の後に呼ぶ）。
  requireLive: (argv) => requireLiveOptIn('archive-bodyless-documents', argv, { offline: '--help' }),
  route: ROUTE,
  usage: USAGE,
  verb: 'アーカイブ',
  parseArgs,
  preconditionProblem,
  newState,
  stateProblem,
  filtersOf: (o) => ({ createdBefore: o.createdBefore }),
  describeFilters: (f) => `createdBefore ${f.createdBefore}`,
  buildRequestBody: ({ filters, ...rest }) => buildRequestBody({ ...rest, createdBefore: filters.createdBefore }),
  emptyTotals,
  addTotals,
  changedOf: (res) => res.archived || 0,
  changedTotal: (t) => t.archived,
  dryRunPageLine: (t, res) =>
    `  [dry-run page ${t.pages}] 確かめた ${res.selected}（本文なし ${res.bodyMissing} / 本文あり ${res.bodyPresent} / 個人資料 ${res.skippedPrivateNote}）`,
  summarizeDryRun: (first, totals, filters) => summarizeDryRun(first, totals, filters.createdBefore),
  formatProgress,
  canaryLine: (n) => `カナリア ${n} 件: 見張るキューが空になり、DLQ は増えていません。SC-05 で「アーカイブ済み」に入ったことを確かめてから続けてください。`,
  dlqHint: ['アーカイブのイベントを受けた下流（取り込み・Wiki 同期・グラフ）のどれかが失敗しています。ログで原因を確かめてから --resume。'],
  doneLines: (t) => [
    `完了: アーカイブ ${t.archived} 件 / 本文あり（触らない） ${t.bodyPresent} 件 / 個人資料（触らない） ${t.skippedPrivateNote} 件（${t.pages} ページ）。`,
    '確かめ方: もう一度 --dry-run を流し、「アーカイブの対象」が 0 件、「既にアーカイブ済み」が増えていること。',
  ],
});

// --- 実行（I/O は lib/admin-page-driver.js） -------------------------------------

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
  applyPage,
  watchedDepth,
  formatProgress,
};

if (require.main === module) {
  main(process.argv.slice(2))
    .then((code) => process.exit(code))
    .catch((e) => {
      process.stderr.write(`[archive-bodyless-documents] ${e.message}\n`);
      process.exit(1);
    });
}
