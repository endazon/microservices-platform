#!/usr/bin/env node
'use strict';
/*
 * check-trace-blocks.js
 * `docs/` 配下 Markdown の trace ブロック（`<!-- trace: ... -->`）・trace-table ブロック
 * （`<!-- trace-table: ... -->`）の文法・値域を検査する（計画 ADR-0048 決定 4 の実現手段）。
 *
 * 正本: project-planning `projects/microservices-platform/07_adr/ADR-0048_impl-docs-restructure.md`
 * 決定 4（本リポジトリは planning に依存しないため隣接クローンか GitHub URL で読む。ADR-0048 決定 2）。
 *
 * 対象: `docs/**\/*.md`。**`.ai-context/` は対象外**——凍結記録は frontmatter が状態を持ち、
 * 本文プロズに計画 ID・IADR・planning への言及が残っていてよい（`.ai-context/README.md`
 * 「`docs/` との違い」表）。走査は `check-doc-links.js` の `mdFiles()`（隠しディレクトリを
 * 暗黙にスキップしない実装）を再利用する。
 *
 * 検査する 5 点（決定 4 の書式）:
 *   1. trace ブロックの文法: `<!-- trace:` で始まり `-->` で終わる。位置は frontmatter 直後・
 *      H1 前。1 文書に複数あれば違反（「1 文書 1 ブロック」）。
 *   2. 許可キーは ids / adrs / iadrs / specs / issues のみ。未知キーは error。
 *   3. trace-table ブロックの文法（`rowN: a, b` の連番行。trace-table はテーブルへ隣接して
 *      置くため 1 文書に複数あってよい）。
 *   4. ID の値域: FR/UC/SC は `readPlanIds()`（check-test-traceability.js）の宣言レンジ、
 *      ADR（計画）は同じ節から本検査器が読む `ADR-0001..NNNN` レンジ、IADR は `.ai-context/adr/`
 *      のファイル実在、specs は `.ai-context/specs/` のベース名実在。**修飾付き
 *      （`<英数の短縮名>:` / `<英数の短縮名>/`。具体名はハードコードしない — ADR-0048 決定 9・
 *      利用者裁定 2026-08-21）は external として実在検査しない**。NFR は無採番許容（既存規約どおり
 *      実在性は検査しない）。issues は書式のみ検査する（GitHub 実在は外部依存ゼロの制約上見ない）。
 *   5. trace ブロックを持たない文書自体は許容する（必須化はしない）。ただし本文の HTML コメント外・
 *      コードフェンス外に**可視の**計画 ID / IADR / 修飾付き issue 参照が残っていれば error
 *      （grep-zero の常設化）。除外の詳細は `lib/trace-blocks.js` の `stripExemptSpans` を参照。
 *
 * 実装ノート（AST 側への差分吸収ポイント）:
 *   - 計画 ADR レンジのパースは `check-test-traceability.js` の `readPlanIds()`（FR/UC/SC のみ）を
 *     再利用しつつ、ADR レンジは同ファイルが公開する `planRangeSection()` の出力へ本検査器**独自**の
 *     正規表現（`` `ADR-(\d+)\.\.(\d+)` ``）を当てて読む——`parsePlanRanges()` は意図的に ADR を対象外
 *     にしている（テスト仕様書の対象外という別の理由）ため、そちらは書き換えない。
 *   - AST リポジトリでは `.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節に ADR レンジが
 *     無い（`FR-01..21` / `UC-01..07` / `SC-01..03` のみで ADR レンジの宣言が無い）。本検査器の
 *     `planAdrRange()` は ADR レンジが見つからない場合 **null を返し、adrs キー・trace-table 内の
 *     ADR トークンの実在検査を skip する**（fail-open）。AST 側で有効化するには、まず計画 ADR レンジを
 *     traceability.repo.md へ追記すること（本リポジトリのように fail-closed にはしない——存在しない
 *     宣言を前提に fail-closed にすると AST は導入初日から検査器ごと落ちる）。
 *
 * 使い方:
 *   node scripts/check-trace-blocks.js               # docs/ 配下を検査
 *   node scripts/check-trace-blocks.js --self-test    # 検査ロジック自体の自己試験
 */
const fs = require('fs');
const path = require('path');
const { notice } = require('./lib/ci-annotate.js');
const { mdFiles } = require('./check-doc-links.js');
const { readPlanIds, planRangeSection, RULES_FILE } = require('./check-test-traceability.js');
const {
  ALLOWED_TRACE_KEYS,
  frontMatterEnd,
  firstH1Index,
  findTraceBlocks,
  findTraceTableBlocks,
  parseTraceBlockBody,
  parseTraceTableBody,
  classifyIdToken,
  validateIdExistence,
  validateKindForKey,
  validateSpecExistence,
  validateIssueToken,
  findBareReferences,
} = require('./lib/trace-blocks.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const DOCS_DIR = path.join(REPO_ROOT, 'docs');
const ADR_DIR = path.join(REPO_ROOT, '.ai-context', 'adr');
const SPECS_DIR = path.join(REPO_ROOT, '.ai-context', 'specs');

// --- 値域コンテキストの構築 ---------------------------------------------------------

/** `.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節から計画 ADR レンジを読む。
 *  見つからなければ null（AST 実装ノート参照。fail-open）。 */
function planAdrRange(rulesPath = path.join(REPO_ROOT, RULES_FILE)) {
  let md;
  try {
    md = fs.readFileSync(rulesPath, 'utf8');
  } catch {
    return null;
  }
  const section = planRangeSection(md);
  if (section === null) return null;
  const m = /`ADR-(\d+)\.\.(\d+)`/.exec(section);
  if (!m) return null;
  return { from: Number(m[1]), to: Number(m[2]) };
}

/** `.ai-context/adr/IADR-XXXX_*.md` から実在する IADR ID の集合を作る。 */
function collectIadrIds(dir = ADR_DIR) {
  const ids = new Set();
  let entries;
  try {
    entries = fs.readdirSync(dir);
  } catch {
    return ids;
  }
  for (const name of entries) {
    const m = /^(IADR-\d{4})[_.]/.exec(name);
    if (m) ids.add(m[1]);
  }
  return ids;
}

/** `.ai-context/specs/*.md` からベース名（拡張子なし）の集合を作る。 */
function collectSpecIds(dir = SPECS_DIR) {
  const ids = new Set();
  let entries;
  try {
    entries = fs.readdirSync(dir);
  } catch {
    return ids;
  }
  for (const name of entries) {
    if (name.toLowerCase() === 'readme.md') continue;
    if (name.endsWith('.md')) ids.add(name.slice(0, -3));
  }
  return ids;
}

/** 検査コンテキストを組み立てる。 */
function buildContext() {
  return {
    planIds: new Set(readPlanIds()),
    adrRange: planAdrRange(),
    iadrIds: collectIadrIds(),
    specIds: collectSpecIds(),
  };
}

// --- 1 ファイルの検査 ---------------------------------------------------------------

/**
 * 1 文書を検査し、違反メッセージの配列を返す（純関数。ctx は buildContext() の戻り値）。
 * relPath はエラーメッセージに使うだけで判定には影響しない。
 */
function checkDoc(relPath, text, ctx) {
  const violations = [];
  const add = (msg) => violations.push(msg);

  const fmEnd = frontMatterEnd(text);
  const h1Idx = firstH1Index(text, fmEnd);
  const windowEnd = h1Idx === -1 ? Infinity : h1Idx;

  // --- trace ブロック本体 ---------------------------------------------------------
  const { blocks: traceBlocks, errors: traceScanErrors } = findTraceBlocks(text);
  for (const e of traceScanErrors) add(e.message);

  if (traceBlocks.length > 1) {
    add(`trace ブロックが ${traceBlocks.length} 件あります（1 文書 1 ブロックの規約違反）`);
  }
  for (const block of traceBlocks) {
    if (!(block.start >= fmEnd && block.start < windowEnd)) {
      add(
        `trace ブロックの位置が不正です（frontmatter 直後・H1 前に置くこと。` +
          `文書中の位置: ${block.start}、許容範囲: [${fmEnd}, ${windowEnd === Infinity ? 'H1なし' : windowEnd})）`
      );
    }
    const { entries, errors } = parseTraceBlockBody(block.body);
    for (const e of errors) add(`trace ブロック: ${e}`);
    for (const entry of entries) {
      for (const raw of entry.items) {
        if (entry.key === 'specs') {
          const err = validateSpecExistence(raw, ctx.specIds);
          if (err) add(`trace ブロック specs: ${err}`);
          continue;
        }
        if (entry.key === 'issues') {
          const err = validateIssueToken(raw);
          if (err) add(`trace ブロック issues: ${err}`);
          continue;
        }
        const tok = classifyIdToken(raw);
        const kindErr = validateKindForKey(tok, entry.key);
        if (kindErr) {
          add(`trace ブロック ${entry.key}: ${kindErr}`);
          continue;
        }
        const err = validateIdExistence(tok, ctx);
        if (err) add(`trace ブロック ${entry.key}: ${err}`);
      }
    }
  }

  // --- trace-table ブロック（0 件以上、位置制約なし） -------------------------------
  const { blocks: tableBlocks, errors: tableScanErrors } = findTraceTableBlocks(text);
  for (const e of tableScanErrors) add(e.message);
  for (const block of tableBlocks) {
    const { rows, errors } = parseTraceTableBody(block.body);
    for (const e of errors) add(`trace-table ブロック: ${e}`);
    for (const row of rows) {
      for (const raw of row.items) {
        const tok = classifyIdToken(raw);
        const err = validateIdExistence(tok, ctx);
        if (err) add(`trace-table row${row.n}: ${err}`);
      }
    }
  }

  // --- grep-zero: 本文に残る可視の計画 ID / IADR / 修飾付き issue 参照 -----------------
  for (const ref of findBareReferences(text, fmEnd)) {
    add(`本文に可視の参照が残っています（行 ${ref.line}）: ${ref.text}`);
  }

  return violations;
}

// --- 実行 -----------------------------------------------------------------------

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }

  const ctx = buildContext();
  if (ctx.adrRange === null) {
    notice(
      `${RULES_FILE} に計画 ADR レンジ（\`ADR-0001..NNNN\` の書式）が見つかりません。` +
        'adrs キー・trace-table 内の ADR トークンの実在検査を skip します（fail-open）。'
    );
  }

  const files = mdFiles(DOCS_DIR);
  if (files.length === 0) {
    console.error('[check-trace-blocks] docs/ 配下に Markdown が 1 件もありません。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    process.exit(1);
  }

  let total = 0;
  const report = [];
  for (const fp of files) {
    const text = fs.readFileSync(fp, 'utf8');
    const rel = path.relative(REPO_ROOT, fp).replace(/\\/g, '/');
    const violations = checkDoc(rel, text, ctx);
    if (violations.length) {
      total += violations.length;
      report.push({ rel, violations });
    }
  }

  if (total === 0) {
    console.log(`[check-trace-blocks] OK: ${files.length} 件の Markdown に trace ブロックの違反はありません。`);
    process.exit(0);
  }

  console.error(
    `[check-trace-blocks] ${report.length} ファイルに違反 ${total} 件を検出しました:`
  );
  for (const r of report) {
    console.error(`\n  ${r.rel}`);
    for (const v of r.violations) console.error(`    - ${v}`);
  }
  console.error(
    '\n書式・値域の正本は計画 ADR-0048 決定 4。位置は frontmatter 直後・H1 前、許可キーは ' +
      `${ALLOWED_TRACE_KEYS.join('/')} のみ。`
  );
  process.exit(1);
}

// --- 自己試験 -------------------------------------------------------------------

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });

  const CTX = {
    planIds: new Set(['FR-01', 'FR-02', 'UC-01', 'SC-01']),
    adrRange: { from: 1, to: 5 },
    iadrIds: new Set(['IADR-0001']),
    specIds: new Set(['20260627_x']),
  };

  const VALID_DOC = [
    '---',
    'title: x',
    'type: functional-spec',
    'status: completed',
    '---',
    '<!-- trace:',
    'ids: [FR-01, UC-01]',
    'adrs: [ADR-0003]',
    'iadrs: [IADR-0001, FUTURE:IADR-9999]',
    'specs: [20260627_x]',
    'issues: [#100, planning#200]',
    '-->',
    '',
    '# 見出し',
    '',
    '本文。',
  ].join('\n');

  t('checkDoc: 正例 — 規約どおりの文書は違反 0 件', checkDoc('docs/a.md', VALID_DOC, CTX).length === 0,
    checkDoc('docs/a.md', VALID_DOC, CTX));

  t('checkDoc: 正例 — trace ブロックを持たない文書はそれ自体では違反にならない',
    checkDoc('docs/b.md', '---\ntitle: x\n---\n\n# 見出し\n\n本文。\n', CTX).length === 0);

  // --- 位置 ---------------------------------------------------------------------
  t('checkDoc: 負例 — H1 の後ろに trace ブロックがあると位置違反',
    /位置が不正/.test(checkDoc('docs/c.md',
      '---\ntitle: x\n---\n\n# 見出し\n\n<!-- trace:\nids: [FR-01]\n-->\n', CTX).join(' ')));
  t('checkDoc: 負例 — frontmatter の内側（閉じの --- より前）に trace ブロックがあると位置違反',
    /位置が不正/.test(checkDoc('docs/d.md',
      '---\ntitle: x\n<!-- trace:\nids: [FR-01]\n-->\n---\n\n# 見出し\n', CTX).join(' ')));
  t('checkDoc: 負例 — trace ブロックが 2 件あると「1 文書 1 ブロック」違反',
    /1 文書 1 ブロック/.test(checkDoc('docs/e.md',
      '---\ntitle: x\n---\n<!-- trace:\nids: [FR-01]\n-->\n<!-- trace:\nids: [FR-02]\n-->\n# 見出し\n', CTX).join(' ')));

  // --- 文法 ---------------------------------------------------------------------
  t('checkDoc: 負例 — 未知キーは error',
    /未知のキー/.test(checkDoc('docs/f.md',
      '---\ntitle: x\n---\n<!-- trace:\nfoo: [FR-01]\n-->\n# 見出し\n', CTX).join(' ')));
  t('checkDoc: 負例 — 閉じていない trace ブロックは error',
    /閉じられていません/.test(checkDoc('docs/g.md',
      '---\ntitle: x\n---\n<!-- trace:\nids: [FR-01]\n\n# 見出し\n本文\n', CTX).join(' ')));
  t('checkDoc: 負例 — trace-table の欠番 row は error',
    /連番/.test(checkDoc('docs/h.md',
      '---\ntitle: x\n---\n# 見出し\n\n本文\n<!-- trace-table:\nrow1: FR-01\nrow3: FR-02\n-->\n', CTX).join(' ')));

  // --- 値域 ---------------------------------------------------------------------
  t('checkDoc: 負例 — 計画レンジ外の FR は違反',
    /計画レンジに存在しない/.test(checkDoc('docs/i.md',
      '---\ntitle: x\n---\n<!-- trace:\nids: [FR-99]\n-->\n# 見出し\n', CTX).join(' ')));
  t('checkDoc: 負例 — 計画 ADR レンジ外は違反',
    /計画 ADR レンジ/.test(checkDoc('docs/j.md',
      '---\ntitle: x\n---\n<!-- trace:\nadrs: [ADR-0099]\n-->\n# 見出し\n', CTX).join(' ')));
  t('checkDoc: 負例 — 実在しない IADR は違反',
    /実在しない IADR/.test(checkDoc('docs/k.md',
      '---\ntitle: x\n---\n<!-- trace:\niadrs: [IADR-9999]\n-->\n# 見出し\n', CTX).join(' ')));
  t('checkDoc: 負例 — 実在しない specs は違反',
    /実在しない仕様書/.test(checkDoc('docs/l.md',
      '---\ntitle: x\n---\n<!-- trace:\nspecs: [no-such]\n-->\n# 見出し\n', CTX).join(' ')));
  t('checkDoc: 負例 — 書式不正の issues は違反',
    /issue 参照の書式/.test(checkDoc('docs/m.md',
      '---\ntitle: x\n---\n<!-- trace:\nissues: [123]\n-->\n# 見出し\n', CTX).join(' ')));
  t('checkDoc: 正例 — 修飾付き ID は external として実在検査しない',
    checkDoc('docs/n.md',
      '---\ntitle: x\n---\n<!-- trace:\nids: [FUTURE/FR-999]\nadrs: [FUTURE:ADR-9999]\n-->\n# 見出し\n', CTX).length === 0);
  t('checkDoc: 正例 — NFR（無採番）は実在検査しない',
    checkDoc('docs/o.md',
      '---\ntitle: x\n---\n<!-- trace:\nids: [NFR, NFR-99]\n-->\n# 見出し\n', CTX).length === 0);
  t('checkDoc: 負例 — ids キーに ADR を書くとキー種別違反',
    /系の ID しか書けません/.test(checkDoc('docs/p.md',
      '---\ntitle: x\n---\n<!-- trace:\nids: [ADR-0001]\n-->\n# 見出し\n', CTX).join(' ')));

  // --- grep-zero ------------------------------------------------------------------
  t('checkDoc: 負例 — 本文に裸の FR 参照が残っていれば error（trace ブロック未変換）',
    /可視の参照が残っています/.test(checkDoc('docs/q.md',
      '---\ntitle: x\n---\n\n# 見出し\n\nFR-01 の仕様どおりに実装した。\n', CTX).join(' ')));
  t('checkDoc: 負例 — 本文に裸の IADR 参照が残っていれば error',
    /IADR-0001/.test(checkDoc('docs/r.md',
      '---\ntitle: x\n---\n\n# 見出し\n\n実装 ADR: IADR-0001 を参照。\n', CTX).join(' ')));
  t('checkDoc: 負例 — 本文に修飾付き issue 参照（planning#N）が裸で残っていれば error',
    /planning#200/.test(checkDoc('docs/s.md',
      '---\ntitle: x\n---\n\n# 見出し\n\n経緯は planning#200 を参照。\n', CTX).join(' ')));
  t('checkDoc: 正例 — 二重角括弧の IADR 参照（[[IADR-0001]]）は grep-zero の対象外',
    checkDoc('docs/t.md',
      '---\ntitle: x\n---\n\n# 見出し\n\n（[[IADR-0001]] 決定 3）。\n', CTX).length === 0);
  t('checkDoc: 正例 — Markdown リンクのリンクテキスト（[FR-01](path)）は grep-zero の対象外',
    checkDoc('docs/u.md',
      '---\ntitle: x\n---\n\n# 見出し\n\n参照: [FR-01](./FR-01.md)\n', CTX).length === 0);
  t('checkDoc: 正例 — 同一リポジトリの裸 issue 番号（#100）は grep-zero の対象外',
    checkDoc('docs/v.md',
      '---\ntitle: x\n---\n\n# 見出し\n\n詳細は #100 を参照。\n', CTX).length === 0);
  t('checkDoc: 正例 — コードフェンス内の FR 参照は grep-zero の対象外',
    checkDoc('docs/w.md',
      '---\ntitle: x\n---\n\n# 見出し\n\n```\n// FR-01: 例示コード\n```\n', CTX).length === 0);

  // --- planAdrRange / collectIadrIds / collectSpecIds（実データからの読み出し） --------
  {
    const range = planAdrRange();
    t('planAdrRange: 実リポジトリから ADR レンジを読める', range !== null && range.from === 1 && range.to >= 1, range);
  }
  t('collectIadrIds: 存在しないディレクトリなら空集合（fail-open で例外にしない）',
    collectIadrIds(path.join(REPO_ROOT, '__no_such_dir__')).size === 0);
  t('collectSpecIds: 実リポジトリの .ai-context/specs から複数件読める',
    collectSpecIds().size > 0);
  t('collectIadrIds: 実リポジトリの .ai-context/adr から複数件読める（README.md は含めない）',
    collectIadrIds().size > 0 && !collectIadrIds().has('README'));

  // --- 実データ: docs/ 全走査が例外を投げずに完走すること（合否そのものは main の関心事） ---
  {
    const ctx = buildContext();
    let threw = null;
    let scanned = 0;
    try {
      for (const fp of mdFiles(DOCS_DIR)) {
        const rel = path.relative(REPO_ROOT, fp).replace(/\\/g, '/');
        checkDoc(rel, fs.readFileSync(fp, 'utf8'), ctx);
        scanned++;
      }
    } catch (e) {
      threw = e;
    }
    t('実データ: docs/ 全件の走査ロジックが例外を投げずに完走する', threw === null && scanned > 0,
      threw ? threw.stack : scanned);
  }

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[check-trace-blocks] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-trace-blocks] 自己試験 ${cases.length} 件 OK。`);
}

if (require.main === module) main();

module.exports = {
  planAdrRange,
  collectIadrIds,
  collectSpecIds,
  buildContext,
  checkDoc,
  selfTest,
};
