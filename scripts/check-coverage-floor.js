#!/usr/bin/env node
'use strict';
/*
 * check-coverage-floor.js
 * バックエンドのカバレッジ床（floor）を強制する（NFR, Issue #453）。外部依存ゼロ。
 *
 * 背景:
 *   フロントは src/vitest.config.ts の thresholds を frontend-tests.yml が強制している（IADR-0034）。
 *   一方バックエンドは ci.yml が `--collect:"XPlat Code Coverage"` で**収集はするが閾値を強制して
 *   いなかった**（閾値強制はコメントアウトされた例として置かれたままだった）。全面再実装（#454）で
 *   11 サービスを作り直す間、テストが薄いまま置き換わっても CI が緑のままになる穴であり、
 *   #453 の受け入れ観点「カバレッジ floor が再実装前の水準を下回ったままマージできない」が
 *   塞ごうとしているのはここである。
 *
 * 方式:
 *   reportgenerator 等のツール導入を要さず、`dotnet test --collect:"XPlat Code Coverage"` が出力する
 *   Cobertura XML（src 配下の coverage.cobertura.xml すべて）を直接読み、行/分岐の被覆率を集計する。
 *   ツール不要のため CI が速く、オフラインでも動く。
 *
 *   集計は各ファイルの line-rate を平均するのではなく、**全ファイルの行数で加重**する
 *   （小さいファイルが多いと単純平均は実態より高く出るため）。Cobertura の <lines> を数える。
 *
 * 使い方:
 *   node scripts/check-coverage-floor.js                 # 既定の探索パスから集計し床と比較
 *   node scripts/check-coverage-floor.js --report-only   # 集計だけ行い、床未達でも exit 0
 *   node scripts/check-coverage-floor.js --self-test
 */
const fs = require('fs');
const path = require('path');
const { notice, warn } = require('./lib/ci-annotate');

const REPO_ROOT = path.resolve(__dirname, '..');
const FLOOR_FILE = path.join(REPO_ROOT, 'src', 'coverage-floor.json');
const SEARCH_ROOT = 'src';
const SKIP_DIRS = new Set(['node_modules', '.git', 'dist']);

/**
 * 集計対象外のユニット。ci.yml の build-and-test は全ユニットの backend.slnx を自動発見して
 * test するため（AST を含む）、除外しないと AST のカバレッジが合算される。
 *
 * AST は独自の計画・ADR を持つ別プロジェクト（submodule）であり、本床の目的は
 * 「#454 で platform / knowledge を作り直す間の退行を止める」ことである。合算すると双方向に濁る:
 *   - AST 側のテストが厚ければ platform / knowledge の実際の退行を薄めて隠す
 *   - AST の pin 更新だけで、無関係な PR の床判定が動く
 * PR 本文が「単純平均は実態より高く出る」として単一プロジェクト内で加重平均を採ったのと同じ問題が、
 * プロジェクト間でも起きる（PR #464 のレビュー指摘）。check-test-traceability.js /
 * check-backend-libraries.js の EXCLUDED_UNITS と同じ切り分けに揃える。
 */
const EXCLUDED_UNITS = new Set(['ai-stock-trading']);

/** リポジトリ相対パスが集計対象外ユニット配下か。 */
function isExcludedPath(relPath) {
  const m = String(relPath).replace(/\\/g, '/').match(/^src\/([^/]+)\//);
  return m ? EXCLUDED_UNITS.has(m[1]) : false;
}

// --- 純粋ロジック ---------------------------------------------------------------

function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

/**
 * Cobertura XML から <line number=".." hits=".." branch="true" condition-coverage="50% (1/2)"> を数え、
 * { lines, covered, branches, coveredBranches } を返す。
 * 属性の順序に依存しないよう、行要素ごとに個別へ属性を取り出す。
 */
function parseCobertura(xml) {
  const text = String(xml);
  let lines = 0;
  let covered = 0;
  let branches = 0;
  let coveredBranches = 0;

  const lineRe = /<line\b([^>]*)\/?>/g;
  let m;
  while ((m = lineRe.exec(text)) !== null) {
    const attrs = m[1];
    const hits = /\bhits\s*=\s*"(\d+)"/.exec(attrs);
    if (!hits) continue;
    lines++;
    if (Number(hits[1]) > 0) covered++;

    // 分岐: condition-coverage="75% (3/4)" の分母・分子を採る。
    const cc = /\bcondition-coverage\s*=\s*"[^"(]*\((\d+)\/(\d+)\)"/.exec(attrs);
    if (cc) {
      coveredBranches += Number(cc[1]);
      branches += Number(cc[2]);
    }
  }
  return { lines, covered, branches, coveredBranches };
}

/** 複数レポートの合算。 */
function mergeTotals(totalsList) {
  return totalsList.reduce(
    (a, b) => ({
      lines: a.lines + b.lines,
      covered: a.covered + b.covered,
      branches: a.branches + b.branches,
      coveredBranches: a.coveredBranches + b.coveredBranches,
    }),
    { lines: 0, covered: 0, branches: 0, coveredBranches: 0 },
  );
}

/** 被覆率（%）を小数第 2 位までで返す。分母 0 のときは null（「測れていない」を 100% と誤解させない）。 */
function rate(covered, total) {
  if (!total) return null;
  return Math.round((covered / total) * 10000) / 100;
}

/** 床との比較。floor 未満なら違反を返す。rate が null（未計測）の項目は判定しない。 */
function compareToFloor(totals, floor) {
  const violations = [];
  const line = rate(totals.covered, totals.lines);
  const branch = rate(totals.coveredBranches, totals.branches);
  if (line !== null && floor.line != null && line < floor.line) {
    violations.push({ metric: 'line', actual: line, floor: floor.line });
  }
  if (branch !== null && floor.branch != null && branch < floor.branch) {
    violations.push({ metric: 'branch', actual: branch, floor: floor.branch });
  }
  return { line, branch, violations };
}

// --- ファイル走査 ---------------------------------------------------------------

function walk(dir, predicate, acc = []) {
  const abs = path.join(REPO_ROOT, dir);
  let entries;
  try {
    entries = fs.readdirSync(abs, { withFileTypes: true });
  } catch {
    return acc;
  }
  for (const e of entries) {
    if (SKIP_DIRS.has(e.name)) continue;
    const rel = toPosix(path.join(dir, e.name));
    if (e.isDirectory()) walk(rel, predicate, acc);
    else if (predicate(rel)) acc.push(rel);
  }
  return acc;
}

/**
 * Cobertura レポートを探す。除外前後の内訳も返す——0 件のときに「探索そのものが空振りしたのか、
 * 除外で全部落ちたのか」を切り分けられないと、fail-open の warn が原因不明のまま素通りする。
 */
function findReportsDetailed() {
  const all = walk(SEARCH_ROOT, (p) => /coverage\.cobertura\.xml$/i.test(p));
  const included = all.filter((p) => !isExcludedPath(p));
  return { all, included, excluded: all.filter((p) => isExcludedPath(p)) };
}

function findReports() {
  return findReportsDetailed().included;
}

function readFloor() {
  try {
    return JSON.parse(fs.readFileSync(FLOOR_FILE, 'utf8')).backend || {};
  } catch {
    return {};
  }
}

// --- 自己試験 -------------------------------------------------------------------

const FIXTURE = `<?xml version="1.0"?>
<coverage>
  <packages><package><classes><class><lines>
    <line number="1" hits="1" />
    <line number="2" hits="0" />
    <line number="3" hits="5" branch="true" condition-coverage="50% (1/2)" />
    <line number="4" hits="2" branch="true" condition-coverage="100% (2/2)" />
  </lines></class></classes></package></packages>
</coverage>`;

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });

  const totals = parseCobertura(FIXTURE);
  t('parseCobertura: 行数と被覆行を数える', totals.lines === 4 && totals.covered === 3, totals);
  t('parseCobertura: 分岐を condition-coverage から数える', totals.branches === 4 && totals.coveredBranches === 3, totals);
  t('parseCobertura: 属性順が違っても拾う',
    parseCobertura('<line hits="1" number="9" />').lines === 1);
  t('parseCobertura: hits の無い line は数えない',
    parseCobertura('<line number="1" />').lines === 0);
  t('parseCobertura: 空入力でも壊れない', parseCobertura('').lines === 0);

  t('rate: 3/4 は 75', rate(3, 4) === 75);
  t('rate: 分母 0 は null（未計測を 100% と誤らせない）', rate(0, 0) === null);

  t('mergeTotals: 合算する',
    mergeTotals([totals, totals]).lines === 8 && mergeTotals([totals, totals]).covered === 6);

  {
    const r = compareToFloor(totals, { line: 80, branch: 70 });
    t('compareToFloor: 行が床未満なら違反（75 < 80）',
      r.violations.length === 1 && r.violations[0].metric === 'line', r);
  }
  {
    const r = compareToFloor(totals, { line: 75, branch: 75 });
    t('compareToFloor: 床ちょうどは違反にしない（境界）',
      r.violations.length === 0, r);
  }
  {
    const r = compareToFloor(totals, { line: 90, branch: 90 });
    t('compareToFloor: 行・分岐とも未満なら 2 件', r.violations.length === 2, r);
  }
  {
    const r = compareToFloor({ lines: 0, covered: 0, branches: 0, coveredBranches: 0 }, { line: 80, branch: 70 });
    t('compareToFloor: 未計測（分母 0）は判定しない', r.violations.length === 0 && r.line === null, r);
  }

  // 集計対象ユニットの切り分け（別プロジェクトの submodule は合算しない。PR #464 レビュー指摘）。
  t('isExcludedPath: ai-stock-trading 配下は集計対象外',
    isExcludedPath('src/ai-stock-trading/backend/Services/X/tests/X.Tests/TestResults/g/coverage.cobertura.xml'));
  t('isExcludedPath: platform / knowledge は集計対象',
    !isExcludedPath('src/platform/backend/Bff/Platform.Bff.Tests/TestResults/g/coverage.cobertura.xml')
      && !isExcludedPath('src/knowledge/backend/Tests/X/TestResults/g/coverage.cobertura.xml'));

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[check-coverage-floor] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-coverage-floor] 自己試験 ${cases.length} 件 OK。`);
}

// --- 実行 -----------------------------------------------------------------------

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }
  const reportOnly = process.argv.includes('--report-only');

  const { all, included: reports, excluded } = findReportsDetailed();
  if (reports.length === 0) {
    // fail-open: レポートが無い＝テストを走らせていない文脈（ローカル実行等）。
    // ここで fail にすると「カバレッジと無関係な PR が赤くなる」ため警告に留める。
    //
    // **ただし黙って素通りさせない。** 「探索が空振りした」のか「除外で全部落ちた」のかを
    // 切り分けられる情報を必ず出す（原因不明の warn は、この検査が無いのと同じである）。
    const sample = all.slice(0, 5).map((p) => `    ${p}`).join('\n');
    warn(`[check-coverage-floor] 集計対象の Cobertura レポートが 0 件でした（探索起点 ${SEARCH_ROOT}/）。`
      + ` 検出 ${all.length} 件 / 除外 ${excluded.length} 件（除外ユニット: ${[...EXCLUDED_UNITS].join(', ')}）。`
      + (all.length === 0
        ? ' 1 件も見つかっていないため、dotnet test --collect:"XPlat Code Coverage" が未実行か、出力先が探索起点の外である可能性が高い。'
        : ' 検出はしているため、すべて除外ユニット配下だったことになる。'));
    if (all.length) console.error(`検出したレポート（先頭 5 件）:\n${sample}`);
    process.exit(0);
  }

  const totals = mergeTotals(reports.map((r) => parseCobertura(fs.readFileSync(path.join(REPO_ROOT, r), 'utf8'))));
  const floor = readFloor();
  const { line, branch, violations } = compareToFloor(totals, floor);

  const fmt = (v) => (v === null ? '未計測' : `${v}%`);
  console.log(`[check-coverage-floor] レポート ${reports.length} 件を集計: line ${fmt(line)}（${totals.covered}/${totals.lines}） / ` +
    `branch ${fmt(branch)}（${totals.coveredBranches}/${totals.branches}）。床: line ${floor.line ?? '未設定'} / branch ${floor.branch ?? '未設定'}`);

  const summary = process.env.GITHUB_STEP_SUMMARY;
  if (summary) {
    const lines = [
      '### バックエンドのカバレッジ（#453）',
      '',
      '| 指標 | 実測 | 床 |',
      '| --- | --- | --- |',
      `| line | ${fmt(line)} | ${floor.line ?? '未設定'} |`,
      `| branch | ${fmt(branch)} | ${floor.branch ?? '未設定'} |`,
      '',
      '床は `src/coverage-floor.json`。テストを増やしたら床を引き上げること（ratchet）。',
    ];
    try { fs.appendFileSync(summary, lines.join('\n') + '\n'); } catch { /* サマリ不可でも検査は続ける */ }
  }

  if (floor.line == null && floor.branch == null) {
    notice('[check-coverage-floor] 床が未設定です（src/coverage-floor.json）。実測値をもとに設定してください。');
    process.exit(0);
  }
  if (violations.length === 0) {
    console.log('[check-coverage-floor] OK: 床を下回っていません。');
    process.exit(0);
  }
  const detail = violations.map((v) => `${v.metric}: 実測 ${v.actual}% < 床 ${v.floor}%`).join(' / ');
  if (reportOnly) {
    warn(`[check-coverage-floor] 床を下回っています（--report-only のため exit 0）: ${detail}`);
    process.exit(0);
  }
  console.error(`[check-coverage-floor] カバレッジが床を下回っています: ${detail}`);
  console.error('テストを追加して床を満たすか、床を下げる正当な理由を作業仕様書に記してください（床の引き下げは退行です）。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = { EXCLUDED_UNITS, isExcludedPath, findReportsDetailed, parseCobertura, mergeTotals, rate, compareToFloor, findReports, readFloor };
