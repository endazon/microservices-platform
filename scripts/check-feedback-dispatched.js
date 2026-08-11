#!/usr/bin/env node
'use strict';
/*
 * check-feedback-dispatched.js — 計画リポジトリへ「送付されていない」環流記録を警告する。
 *
 * 背景（planning issue #217 / #218 / #219 / #220 / #221 / #222）:
 *   `/plan-feedback` は 2 つの作業から成る ——(1) `feedback/<日付>_<概要>.md` に記録を作る、
 *   (2) 計画リポジトリへ issue として起票する。**(1) だけ行われて (2) が漏れる事故が繰り返し
 *   起きている。** 記録には `status: open` と「未送付。計画リポジトリへ issue として起票する」
 *   と書かれたまま PR がマージされ、実装リポの `feedback/` に滞留した。
 *
 *   2026-08-06 の未到達棚卸しでは、**6 件が最長 1 か月近く滞留していた**（うち 1 件は
 *   実装コードのコメントが「計画へ環流済み」と述べていたのに該当ファイルすら無かった）。
 *   PR がマージされても検出されない点で、通常の未反映よりも見つけにくい。
 *
 * 方針:
 *   - **警告のみ。ジョブは落とさない**（exit 0）。起票は人手の判断を伴うため、ブロックにすると
 *     回避策（記録を作らない）を誘発する。統制の目的は「気付けること」であって「止めること」
 *     ではない。
 *   - GitHub Actions 上ではアノテーションとして出す（`lib/ci-annotate.js`）。ジョブログを
 *     開かなくても PR の Checks 画面で気付ける。
 *   - **厳格化は opt-in**（`STRICT_FEEDBACK_DISPATCH=1` で警告を失敗として扱う）。
 *
 * 起票済みと見なす条件（いずれか 1 つ）:
 *   a. フロントマターに `planning_issue:` があり値が空でない
 *   b. 本文に自リポジトリ以外の GitHub issue への URL がある（`GITHUB_REPOSITORY` で自リポを判定）
 *   c. 本文に「起票済み」がある
 *
 * 警告する条件（いずれか）:
 *   1. フロントマターが `status: open` で、上記の起票済みの証拠が無い
 *   2. 本文に「未送付」がある（`status` を問わない。記録自身の自己申告であり最も強い信号）
 *
 * 使い方:
 *   node scripts/check-feedback-dispatched.js            # feedback/ を検査
 *   node scripts/check-feedback-dispatched.js --self-test
 */

const fs = require('fs');
const path = require('path');
const { warn, notice } = require('./lib/ci-annotate.js');

/** 検査対象のディレクトリ（リポジトリ直下からの相対）。 */
const FEEDBACK_DIR = 'feedback';

/** フロントマターの本体（`---` に挟まれた部分）を返す。無ければ空文字。 */
function frontMatterOf(text) {
  const m = /^---\r?\n([\s\S]*?)\r?\n---/.exec(text);
  return m ? m[1] : '';
}

/** フロントマターから 1 キーの値を取り出す（クォート・前後空白は落とす）。 */
function fmValue(text, key) {
  const fm = frontMatterOf(text);
  const re = new RegExp(`^${key}\\s*:\\s*(.*)$`, 'm');
  const m = re.exec(fm);
  if (!m) return '';
  return m[1].trim().replace(/^['"]|['"]$/g, '').trim();
}

/**
 * 本文中の GitHub issue URL のうち、自リポジトリ以外を指すものを返す。
 * selfRepo は `owner/repo`（`GITHUB_REPOSITORY` の形）。空なら「すべて他リポ扱い」とする
 * ——ローカル実行で自リポを特定できないときに、起票済みを未起票と誤判定しないためである。
 */
function foreignIssueLinks(text, selfRepo) {
  const out = [];
  const re = /https?:\/\/github\.com\/([\w.-]+)\/([\w.-]+)\/issues\/(\d+)/g;
  let m;
  while ((m = re.exec(text)) !== null) {
    const repo = `${m[1]}/${m[2]}`;
    if (selfRepo && repo.toLowerCase() === selfRepo.toLowerCase()) continue;
    out.push(`${repo}#${m[3]}`);
  }
  return out;
}

/** 1 ファイルを判定する。`{ dispatched, reasons }` を返す。 */
function inspect(text, selfRepo) {
  const status = fmValue(text, 'status').toLowerCase();
  const links = foreignIssueLinks(text, selfRepo);
  const hasPlanningIssueKey = fmValue(text, 'planning_issue') !== '';
  const saysFiled = text.includes('起票済み');
  const saysNotSent = text.includes('未送付');

  const dispatched = hasPlanningIssueKey || links.length > 0 || saysFiled;

  const reasons = [];
  if (saysNotSent) {
    reasons.push('本文に「未送付」がある（記録自身が未起票と述べている）');
  }
  if (status === 'open' && !dispatched) {
    reasons.push('`status: open` だが計画リポジトリの issue への参照が無い');
  }
  return { dispatched, status, links, reasons };
}

/**
 * 検査対象から外すファイル名（小文字で比較）。
 * `TEMPLATE.md` は雛形であり `status: open` を持つのが正常であるため、除外しないと常時 warn になる。
 */
const EXCLUDED = new Set(['readme.md', 'template.md']);

/** feedback/ 配下の Markdown を列挙する（存在しなければ空配列）。 */
function listFeedbackFiles(root) {
  const dir = path.join(root, FEEDBACK_DIR);
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return [];
  }
  return entries
    .filter((e) => e.isFile() && e.name.endsWith('.md') && !EXCLUDED.has(e.name.toLowerCase()))
    .map((e) => path.join(dir, e.name))
    .sort();
}

function selfTest() {
  const assert = require('assert');
  const SELF = 'endazon/ai-stock-trading';

  // 起票済み: 他リポの issue URL がある
  assert.strictEqual(
    inspect('---\nstatus: open\n---\n[#209](https://github.com/endazon/project-planning/issues/209)', SELF)
      .reasons.length,
    0,
    '他リポの issue URL があれば起票済みと見なす'
  );

  // 自リポの issue URL は起票の証拠にならない
  assert.ok(
    inspect('---\nstatus: open\n---\n本文 https://github.com/endazon/ai-stock-trading/issues/375', SELF)
      .reasons.length > 0,
    '自リポの issue URL は計画への起票ではない'
  );

  // 「未送付」は status を問わず警告する
  assert.ok(
    inspect('---\nstatus: accepted\n---\n未送付。計画リポジトリへ issue として起票する', SELF).reasons.length > 0,
    '「未送付」は status に関わらず警告する'
  );

  // planning_issue キーがあれば起票済み
  assert.strictEqual(
    inspect('---\nstatus: open\nplanning_issue: 209\n---\n本文', SELF).reasons.length,
    0,
    'planning_issue キーがあれば起票済みと見なす'
  );

  // 空値の planning_issue は証拠にならない
  assert.ok(
    inspect('---\nstatus: open\nplanning_issue:\n---\n本文', SELF).reasons.length > 0,
    '空値の planning_issue は起票の証拠にならない'
  );

  // open 以外で起票の証拠が無くても、「未送付」が無ければ警告しない
  assert.strictEqual(
    inspect('---\nstatus: accepted\n---\n本文', SELF).reasons.length,
    0,
    'open 以外は起票済みの証拠を求めない'
  );

  // selfRepo が空なら、どの issue URL も起票の証拠として扱う（誤検出を避ける）
  assert.strictEqual(
    inspect('---\nstatus: open\n---\nhttps://github.com/endazon/ai-stock-trading/issues/1', '').reasons.length,
    0,
    'selfRepo 不明時は誤検出しない側へ倒す'
  );

  console.log('[check-feedback-dispatched] self-test OK');
}

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) {
    selfTest();
    process.exit(0);
  }

  const root = process.cwd();
  const files = listFeedbackFiles(root);

  if (files.length === 0) {
    notice(
      `[check-feedback-dispatched] ${FEEDBACK_DIR}/ が無いか Markdown がありません。検査をスキップします。`
    );
    process.exit(0);
  }

  const selfRepo = process.env.GITHUB_REPOSITORY || '';
  const findings = [];
  for (const fp of files) {
    const text = fs.readFileSync(fp, 'utf8');
    const r = inspect(text, selfRepo);
    if (r.reasons.length > 0) findings.push({ fp: path.relative(root, fp), reasons: r.reasons });
  }

  if (findings.length === 0) {
    console.log(
      `[check-feedback-dispatched] OK: ${files.length} 件の環流記録に未送付のものはありません。`
    );
    process.exit(0);
  }

  const lines = findings.map((f) => `${f.fp}: ${f.reasons.join(' / ')}`);
  warn(
    `[check-feedback-dispatched] 計画リポジトリへ未送付の可能性がある環流記録が ${findings.length} 件あります。` +
      `${lines.join('  ')}  ` +
      '記録を作るだけでは計画へ届きません。計画リポジトリへ issue として起票し、記録に issue への URL を残してください。'
  );
  for (const f of findings) console.error(`  - ${f.fp}\n      ${f.reasons.join('\n      ')}`);

  if (process.env.STRICT_FEEDBACK_DISPATCH === '1') {
    console.error('[check-feedback-dispatched] STRICT_FEEDBACK_DISPATCH=1 のため失敗として扱います。');
    process.exit(1);
  }
  process.exit(0);
}

if (require.main === module) main();

module.exports = {
  frontMatterOf,
  fmValue,
  foreignIssueLinks,
  inspect,
  listFeedbackFiles,
  selfTest,
  FEEDBACK_DIR,
  EXCLUDED,
};
