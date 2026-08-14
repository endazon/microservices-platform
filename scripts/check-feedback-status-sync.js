#!/usr/bin/env node
'use strict';
/*
 * check-feedback-status-sync.js
 * 環流記録の `status` が計画側の裁定に追随しているかの機械検査
 * （NFR / planning#323 の裁定 / IADR-0187 / issue #737）。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * ■ なぜ要るか
 *   planning#323 の裁定により `status` は **計画側の裁定がどこまで進んだか** を表す
 *   （伝達したかは `dispatched:` が担う。IADR-0187 決定 1）。**値の正は計画側にある**のに、
 *   実装側の記録は人手でしか追随せず、**feedback/README.md 自身が「この語彙を検査する機械は
 *   無い。値の誤りは沈黙する」と明記していた**。その沈黙が実際に 2 回起きた。
 *
 *     1. ai-stock-trading#477 / AST/IADR-0188 —— 実装側 18 件が open、計画側は accepted
 *     2. 本リポ #737 —— 10 件。#721 の移行が `dispatched` の軸だけを確かめ、
 *        新しい `status` の軸（計画側が既に裁定していないか）を確かめなかった
 *
 * ■ 何を見るか
 *   計画側 `draft/feedback/` に **同名の写しを持つ**記録について、**frontmatter の `status` が
 *   一致している**こと。ただ 1 点だけを見る。
 *
 * ■ 何を見ないか（明示する）
 *   - **写しを持たない記録** —— **記録ファイル経路と GitHub Issue 経路は等価**であり
 *     （feedback/README.md §経路ごとの証拠。planning#319）、**写しの不在は伝達漏れを意味しない**。
 *     不一致に数えると恒久的な偽陽性が残る（実測 3 件）。
 *   - **`status` 以外の内容差分** —— 実装側はトリアージ結果節を持たない等、正当な差がある。
 *   - **値そのものの妥当性** —— 語彙外の値・意味の取り違えは、両側が同じ値なら素通りする。
 *     一致を見る検査であって、正しさを見る検査ではない。
 *
 * ■ fail-open の条件
 *   `planning` submodule が未 populate のときは **skip（exit 0）** する（check-doc-links.js と
 *   同じ扱い。ローカル環境差で CI を落とさない）。**ただし 0 件走査では緑にしない** —— populate
 *   されているのに対象が 0 件なら **fail** する（#664 の門）。
 */

const fs = require('fs');
const path = require('path');

const REPO = path.join(__dirname, '..');
const IMPL = path.join(REPO, 'feedback');
const PLAN = path.join(REPO, 'planning/draft/feedback');
// 記録ではないもの（雛形と索引）
const NOT_A_RECORD = new Set(['README.md', 'TEMPLATE.md']);

/** frontmatter の 1 階層目の鍵を読む（YAML パーサは持ち込まない） */
function frontmatterValue(text, key) {
  const m = text.match(new RegExp(`^${key}:\\s*(.*)$`, 'm'));
  return m ? m[1].trim() : null;
}

/**
 * 実装側と計画側の 2 ディレクトリを突合する。**経路を引数に取る**ので fixture で駆動できる
 * —— `--self-test` が R1〜R5 の変異シナリオを実データ無しで回すために要る（#738 レビュー 🟡）。
 */
function compare(implDir, planDir) {
  const records = fs
    .readdirSync(implDir)
    .filter((f) => f.endsWith('.md') && !NOT_A_RECORD.has(f))
    .sort();

  const errors = [];

  // ★ 0 件走査で静かに緑にしない（#664）
  if (records.length === 0) {
    errors.push('feedback/ の記録が 0 件だった。走査が空振りしている');
  }

  let compared = 0;
  let unpaired = 0;

  for (const f of records) {
    const planPath = path.join(planDir, f);
    if (!fs.existsSync(planPath)) {
      // 写しを持たない記録は検査対象外（Issue 経路・別日付での収録など）
      unpaired += 1;
      continue;
    }
    compared += 1;

    const implStatus = frontmatterValue(fs.readFileSync(path.join(implDir, f), 'utf8'), 'status');
    const planStatus = frontmatterValue(fs.readFileSync(planPath, 'utf8'), 'status');

    if (implStatus !== planStatus) {
      errors.push(
        `[stale] ${f}: 実装側 status=${implStatus} / 計画側 status=${planStatus}。` +
          '`status` は計画側の裁定を表す（planning#323 / IADR-0187 決定 1）ので、計画側へ追随させること',
      );
    }
  }

  // ★ 突合できた件数が 0 なら、検査が実質何も見ていない
  if (records.length > 0 && compared === 0) {
    errors.push('計画側に写しを持つ記録が 0 件だった。検査が実質何も見ていない（#664 の門）');
  }

  return { errors, records, compared, unpaired };
}

/**
 * 自己試験 —— **変異試験 R1〜R5 を恒久化したもの**。
 *
 * ★ 当初は「実データに対して main() が 0 を返す」ことしか固定しておらず、
 *   **`!==` を `===` に取り違えても、全記録が同期している限り緑のままだった**（#738 レビュー 🟡）。
 *   **比較そのものを、意図的に食い違わせた fixture で駆動する。**
 */
function selfTest() {
  const os = require('os');
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });

  /** fixture を組む。impl / plan は { ファイル名: status（null なら status 鍵ごと省く） } */
  const build = (impl, plan) => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'fbstatus-selftest-'));
    const implDir = path.join(dir, 'impl');
    const planDir = path.join(dir, 'plan');
    fs.mkdirSync(implDir);
    fs.mkdirSync(planDir);
    const write = (d, spec) => {
      for (const [name, status] of Object.entries(spec)) {
        const fm = status === null ? '' : `status: ${status}\n`;
        fs.writeFileSync(path.join(d, name), `---\ntitle: x\n${fm}---\n\n本文\n`);
      }
    };
    write(implDir, impl);
    write(planDir, plan);
    return { implDir, planDir };
  };

  // --- R1: 追随漏れの本体（比較ロジックを食い違うデータで駆動する） ---
  {
    const { implDir, planDir } = build({ 'a.md': 'open' }, { 'a.md': 'accepted' });
    const r = compare(implDir, planDir);
    t('R1: status が食い違えば [stale] を 1 件返す',
      r.errors.length === 1 && r.errors[0].includes('[stale] a.md'), r.errors);
    t('R1: 双方の値をメッセージに載せる',
      r.errors[0]?.includes('実装側 status=open') && r.errors[0]?.includes('計画側 status=accepted'), r.errors[0]);
  }
  {
    const { implDir, planDir } = build({ 'a.md': 'accepted' }, { 'a.md': 'accepted' });
    t('R1 裏: 一致していれば違反 0 件', compare(implDir, planDir).errors.length === 0);
  }
  {
    // **鍵が無い側**（片方だけ status を持たない）も食い違いである
    const { implDir, planDir } = build({ 'a.md': null }, { 'a.md': 'accepted' });
    const r = compare(implDir, planDir);
    t('R1: 片側に status 鍵が無ければ食い違いとして検出', r.errors.length === 1, r.errors);
  }

  // --- R2: 記録 0 件（走査が空振り） ---
  {
    const { implDir, planDir } = build({}, { 'a.md': 'accepted' });
    const r = compare(implDir, planDir);
    t('R2: 記録 0 件なら門が発火', r.errors.some((e) => e.includes('記録が 0 件だった')), r.errors);
  }

  // --- R3: 突合できた件数が 0（検査が実質何も見ていない） ---
  {
    const { implDir, planDir } = build({ 'a.md': 'open' }, { 'b.md': 'accepted' });
    const r = compare(implDir, planDir);
    t('R3: 写しが 1 件も無ければ門が発火',
      r.errors.some((e) => e.includes('写しを持つ記録が 0 件だった')), r.errors);
  }

  // --- R4: 写しを持たない記録を不一致に数えない（偽陽性を作らない） ---
  {
    const { implDir, planDir } = build(
      { 'paired.md': 'accepted', 'unpaired.md': 'open' },
      { 'paired.md': 'accepted' },
    );
    const r = compare(implDir, planDir);
    t('R4: 写しを持たない記録は違反にしない', r.errors.length === 0, r.errors);
    t('R4: 突合 1 件 / 対象外 1 件として数える', r.compared === 1 && r.unpaired === 1,
      { compared: r.compared, unpaired: r.unpaired });
  }

  // --- R5: 索引と雛形は記録ではない ---
  {
    const { implDir, planDir } = build(
      { 'README.md': 'open', 'TEMPLATE.md': 'open', 'a.md': 'accepted' },
      { 'README.md': 'accepted', 'TEMPLATE.md': 'accepted', 'a.md': 'accepted' },
    );
    const r = compare(implDir, planDir);
    t('R5: 索引・雛形が食い違っても違反にしない', r.errors.length === 0, r.errors);
    t('R5: 索引・雛形を記録として数えない', r.records.length === 1 && r.compared === 1,
      { records: r.records, compared: r.compared });
  }

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) {
      failed++;
      if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual));
    }
  }
  if (failed) {
    console.error(`[check-feedback-status-sync] 自己試験 ${failed} 件 失敗。`);
    return 1;
  }
  console.log(`[check-feedback-status-sync] 自己試験 ${cases.length} 件 all passed。`);
  return 0;
}

function main() {
  if (process.argv.slice(2).includes('--self-test')) return selfTest();

  if (!fs.existsSync(PLAN)) {
    console.log(
      '  warn  [check-feedback-status-sync] planning が未 populate のため skip した（探した先: planning/draft/feedback）。' +
        'この範囲は検査されていない。',
    );
    return 0;
  }

  const { errors, records, compared, unpaired } = compare(IMPL, PLAN);

  if (errors.length > 0) {
    console.error(`[check-feedback-status-sync] status の追随漏れ ${errors.length} 件を検出しました:`);
    for (const e of errors) console.error(`    ${e}`);
    return 1;
  }

  console.log(
    `[check-feedback-status-sync] OK: 記録 ${records.length} 件のうち ${compared} 件を計画側と突合しました` +
      `（写しを持たない ${unpaired} 件は対象外 —— 記録ファイル経路と Issue 経路は等価である）。`,
  );
  return 0;
}

if (require.main === module) process.exit(main());
module.exports = { main, compare, selfTest };
