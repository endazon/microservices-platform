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

function main() {
  if (!fs.existsSync(PLAN)) {
    console.log(
      '  warn  [check-feedback-status-sync] planning が未 populate のため skip した（探した先: planning/draft/feedback）。' +
        'この範囲は検査されていない。',
    );
    return 0;
  }

  const records = fs
    .readdirSync(IMPL)
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
    const planPath = path.join(PLAN, f);
    if (!fs.existsSync(planPath)) {
      // 写しを持たない記録は検査対象外（Issue 経路・別日付での収録など）
      unpaired += 1;
      continue;
    }
    compared += 1;

    const implStatus = frontmatterValue(fs.readFileSync(path.join(IMPL, f), 'utf8'), 'status');
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
module.exports = { main };
