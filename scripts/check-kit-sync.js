#!/usr/bin/env node
'use strict';
/*
 * check-kit-sync.js
 * impl-handoff-kit `repo-template` との分類（A/B/C）に基づく追随の機械検査
 * （NFR / IADR-0115 決定 2 / issue #713）。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * ■ なぜ要るか
 *   IADR-0115 決定 2 は「分類 A は diff でバイト一致を機械判定できる」を利点に挙げているが、
 *   **どのファイルがどの分類かを記した表がリポジトリに無かった**ため、その判定を回す対象が
 *   決まらなかった。#712（feedback/README.md の節が丸ごと欠落）と #721（pin だけ進めて
 *   キット追随を伴わなかった）は、いずれもこの穴から入った。
 *
 * ■ 何を見るか
 *   1. **分類 A はキットとバイト一致である**（本検査の主目的）
 *   2. **キットの全ファイルが分類表に載っている**（新しく増えたファイルを黙って見逃さない）
 *   3. **分類表に載っているのに実在しないファイルが無い**（表が現実から離れていない）
 *
 * ■ 何を見ないか（明示する）
 *   - **分類 B のデルタが妥当か** —— 4 種のどれに当たるかは表に書くが、**内容の妥当性は
 *     人が判断する**。文字列一致では意味を読めない。
 *   - **キット側に新しい規範が入ったこと** —— IADR-0189 決定 5 のとおり機械では捕まえられない。
 *     本検査が見るのは**バイト一致**であって規範の移動ではない。
 *   - **分類 C の内容** —— 定義上、同期しない。
 *
 * ■ fail-open の条件（#713 受け入れ基準）
 *   `planning` submodule が未 populate のときは **skip（exit 0）** する。ローカル環境差で
 *   CI を落とさないため（check-doc-links.js と同じ扱い）。**ただし 0 件走査では緑にしない**
 *   —— populate されているのに対象が 0 件なら **fail** する（#664 の門）。
 */

const fs = require('fs');
const path = require('path');

const REPO = path.join(__dirname, '..');
const KIT = path.join(REPO, 'planning/tools/impl-handoff-kit/repo-template');
const TABLE = path.join(__dirname, 'kit-sync-classification.json');

function main() {
  if (!fs.existsSync(KIT)) {
    console.log(
      '  warn  [check-kit-sync] planning が未 populate のため skip した（探した先: planning/tools/impl-handoff-kit/repo-template）。' +
        'この範囲は検査されていない。',
    );
    return 0;
  }

  const table = JSON.parse(fs.readFileSync(TABLE, 'utf8'));
  const A = table.classes.A;
  const B = Object.keys(table.classes.B);
  const C = table.classes.C;
  const NA = table.notApplicable;
  const classified = new Set([...A, ...B, ...C, ...NA]);

  // キット配下の全ファイルを引く（拡張子で絞らない。母集合の規則 3）
  const kitFiles = [];
  (function walk(dir) {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      if (e.name === '.git') continue;
      const p = path.join(dir, e.name);
      if (e.isDirectory()) walk(p);
      else kitFiles.push(path.relative(KIT, p));
    }
  })(KIT);

  const errors = [];

  // ★ 0 件走査で静かに緑にしない（#664）
  if (kitFiles.length === 0) {
    errors.push('キット配下のファイルが 0 件だった。走査が空振りしている（populate の確認が要る）');
  }

  // 2. キットの全ファイルが表に載っているか
  for (const f of kitFiles) {
    if (!classified.has(f)) {
      errors.push(
        `[unclassified] ${f} が分類表に無い。キットに増えたファイルである可能性がある` +
          ' —— A / B / C のどれかへ分類すること（IADR-0115 決定 2）',
      );
    }
  }

  // 3. 表に在るのに実在しないもの（表が現実から離れていないか）
  for (const f of [...A, ...B, ...C]) {
    if (!fs.existsSync(path.join(REPO, f))) {
      errors.push(`[missing] ${f} が分類表に在るが本リポに実在しない。表を追随させること`);
    }
    if (!fs.existsSync(path.join(KIT, f))) {
      errors.push(`[not-in-kit] ${f} が分類表に在るがキットに実在しない。表を追随させること`);
    }
  }

  // 1. 分類 A のバイト一致（本検査の主目的）
  let checkedA = 0;
  for (const f of A) {
    const kp = path.join(KIT, f);
    const rp = path.join(REPO, f);
    if (!fs.existsSync(kp) || !fs.existsSync(rp)) continue;
    checkedA += 1;
    if (!fs.readFileSync(kp).equals(fs.readFileSync(rp))) {
      errors.push(
        `[drift] ${f} が分類 A なのにキットとバイト一致でない。` +
          'キット原文で上書きするか、固有デルタとして分類 B へ移して理由を書くこと（IADR-0115 決定 2）',
      );
    }
  }

  // ★ 分類 A が 0 件なら、検査が実質何も見ていない
  if (checkedA === 0) {
    errors.push('分類 A の照合対象が 0 件だった。検査が実質何も見ていない（#664 の門）');
  }

  if (errors.length > 0) {
    console.error(`[check-kit-sync] 追随の違反 ${errors.length} 件を検出しました:`);
    for (const e of errors) console.error(`    ${e}`);
    return 1;
  }

  console.log(
    `[check-kit-sync] OK: キット ${kitFiles.length} 件を分類表と突合しました` +
      `（A ${A.length} 件はバイト一致 / B ${B.length} 件は固有デルタ / C ${C.length} 件は同期しない` +
      ` / 対象外 ${NA.length} 件）。`,
  );
  return 0;
}

if (require.main === module) process.exit(main());
module.exports = { main };
