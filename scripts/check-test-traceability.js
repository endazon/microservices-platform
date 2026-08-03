#!/usr/bin/env node
'use strict';
/*
 * check-test-traceability.js
 * 受け入れ基準 → テストの写像を機械検査する（NFR, Issue #453）。外部依存ゼロ。
 *
 * 規約（docs/tests/TEST_STRATEGY.md）:
 *   テストの直前のコメントに起点 ID を書く。例:
 *     // FR-03, UC-01: ハイブリッド検索は語彙一致とベクトル類似の両方を返す
 *     [Fact] public async Task 検索は語彙一致とベクトル類似の両方を返す() { ... }
 *   テスト名ではなくコメントを採るのは、日本語のテスト名（本リポジトリの既存慣習）と両立し、
 *   .claude/rules/traceability.md「テスト名またはコメントに起点 ID を残す」にそのまま乗るためである。
 *
 * 検査内容:
 *   docs/tests/ に仕様書が在る起点 ID（FR-xx / UC-xx / SC-xx / NFR）のうち、src/ のテストから
 *   1 件も参照されていないものを「未写像」として報告する。
 *
 * 判定方針（ratchet）:
 *   着手時点の実測が **27/27 写像済み（未写像 0）** であったため、warn 開始ではなく最初から fail で
 *   強制する。ただし「仕様書を先に書き、テストは次の PR」という正当な段取りを塞がないよう、
 *   scripts/test-traceability-allowlist.json に **未写像を許す ID を明示**できる（#455 の
 *   backend-library-baseline と同型）。
 *     - allowlist に無い未写像            → fail（写像の退行を止める）
 *     - allowlist にある未写像            → warn（残件として実行サマリに出す）
 *     - allowlist にあるのに写像済みになった → fail（allowlist の減らし忘れを検出）
 *   3 番目により、床は下げられるが上げっぱなしにできない。
 *
 * 使い方:
 *   node scripts/check-test-traceability.js
 *   node scripts/check-test-traceability.js --self-test
 */
const fs = require('fs');
const path = require('path');
const { warn, notice } = require('./lib/ci-annotate');

const REPO_ROOT = path.resolve(__dirname, '..');
const SPEC_DIR = 'docs/tests';
const SRC_DIR = 'src';
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', 'dist', 'coverage']);

/**
 * 検査対象外のユニット。他プロジェクト（AST）は独自の計画 ID 体系を持ち、本リポジトリの
 * docs/tests/ の FR/SC とは名前空間が異なる（.claude/rules/traceability.md）。
 */
const EXCLUDED_UNITS = new Set(['ai-stock-trading']);

/** テストファイルとみなす拡張子パターン。 */
const TEST_FILE = /(Tests?\.cs|\.(test|spec)\.(ts|tsx|js|jsx))$/i;

// --- 純粋ロジック ---------------------------------------------------------------

function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

function isExcludedPath(relPath) {
  const m = toPosix(relPath).match(/^src\/([^/]+)\//);
  return m ? EXCLUDED_UNITS.has(m[1]) : false;
}

/**
 * テスト仕様書のファイル名から起点 ID を取り出す。
 * 例: 'FR-01_data-source-catalog.md' → 'FR-01' / 'SC-11_configuration-viewer.md' → 'SC-11'
 * NFR-01 のような連番付き NFR は 'NFR' に丸める（計画側は NFR を細分番号で持たないため）。
 */
function specIdOf(fileName) {
  const m = String(fileName).match(/^(FR|SC|UC)-(\d+)/i);
  if (m) return `${m[1].toUpperCase()}-${m[2].padStart(2, '0')}`;
  if (/^NFR/i.test(String(fileName))) return 'NFR';
  return null;
}

/**
 * 本文から起点 ID の参照を集める。**修飾付き（AST/FR-17 等）は除外する** — 他プロジェクトへの
 * 参照であり、自名前空間の写像ではない（.claude/rules/traceability.md）。
 */
function idsInText(text) {
  const out = new Set();
  // 直前が単語文字なら別語（XFR-01）。直前が「単語文字 + /」なら修飾付き（AST/FR-17）で他プロジェクト。
  // `//FR-03`（スペース無しのコメント）は修飾ではないので拾う——`/` の前が単語文字でないため。
  const re = /(?<!\w)(?<!\w\/)((?:FR|UC|SC)-\d+|NFR)\b/g;
  let m;
  while ((m = re.exec(String(text))) !== null) {
    const id = m[1];
    out.add(id === 'NFR' ? 'NFR' : id.replace(/^(\w+)-(\d+)$/, (_, k, n) => `${k}-${n.padStart(2, '0')}`));
  }
  return out;
}

/**
 * 未写像（仕様書は在るがテストからの参照が無い）ID を返す。
 * specIds / testIds はいずれも Set。
 */
function unmappedIds(specIds, testIds) {
  return [...specIds].filter((id) => !testIds.has(id)).sort();
}

/**
 * 未写像 ID と allowlist を突き合わせ、3 分類して返す（ratchet）。
 *   blocked : allowlist に無い未写像 → fail
 *   pending : allowlist どおりの未写像 → warn
 *   stale   : allowlist にあるが実は写像済み（減らし忘れ）→ fail
 */
function classifyAgainstAllowlist(unmapped, allowlist) {
  const un = new Set(unmapped);
  const al = new Set(allowlist);
  return {
    blocked: [...un].filter((id) => !al.has(id)).sort(),
    pending: [...un].filter((id) => al.has(id)).sort(),
    stale: [...al].filter((id) => !un.has(id)).sort(),
  };
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

/** docs/tests/ の仕様書から起点 ID の集合を作る。 */
function collectSpecIds() {
  const ids = new Set();
  for (const f of walk(SPEC_DIR, (p) => /\.md$/i.test(p))) {
    const id = specIdOf(path.basename(f));
    if (id) ids.add(id);
  }
  return ids;
}

/** allowlist（未写像を許す起点 ID）を読む。無ければ空。 */
function readAllowlist() {
  try {
    return JSON.parse(fs.readFileSync(path.join(__dirname, 'test-traceability-allowlist.json'), 'utf8')).pending || [];
  } catch {
    return [];
  }
}

/** src/ のテストファイルから参照されている起点 ID の集合を作る。 */
function collectTestIds() {
  const ids = new Set();
  for (const f of walk(SRC_DIR, (p) => TEST_FILE.test(p) && !isExcludedPath(p))) {
    for (const id of idsInText(fs.readFileSync(path.join(REPO_ROOT, f), 'utf8'))) ids.add(id);
  }
  return ids;
}

// --- 自己試験 -------------------------------------------------------------------

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });

  t('specIdOf: FR 仕様書からゼロ埋め ID', specIdOf('FR-01_data-source-catalog.md') === 'FR-01');
  t('specIdOf: SC 仕様書', specIdOf('SC-11_configuration-viewer.md') === 'SC-11');
  t('specIdOf: NFR は連番を落として NFR に丸める', specIdOf('NFR-01_performance-load-test.md') === 'NFR');
  t('specIdOf: 対象外のファイル名は null', specIdOf('README.md') === null);

  t('idsInText: コメント中の ID を拾う',
    [...idsInText('// FR-03, UC-01: 検索')].sort().join(',') === 'FR-03,UC-01');
  t('idsInText: 修飾付き（AST/FR-17）は除外する',
    !idsInText('// AST/FR-17: 別プロジェクト').has('FR-17'));
  t('idsInText: 修飾付きと裸が混在しても裸だけ拾う',
    [...idsInText('// AST/FR-17 と FR-03')].sort().join(',') === 'FR-03');
  t('idsInText: ゼロ埋めして正規化する', idsInText('// FR-3').has('FR-03'));
  t('idsInText: NFR を拾う', idsInText('// NFR: 性能').has('NFR'));
  t('idsInText: 単語の一部（XFR-01）は拾わない', idsInText('XFR-01').size === 0);
  t('idsInText: スペース無しの //FR-03 も拾う（修飾ではないため）', idsInText('//FR-03: x').has('FR-03'));
  t('idsInText: 行頭の ID も拾う', idsInText('FR-05 のテスト').has('FR-05'));

  t('unmappedIds: テストに無い仕様 ID を返す',
    JSON.stringify(unmappedIds(new Set(['FR-01', 'FR-02']), new Set(['FR-02']))) === '["FR-01"]');
  t('unmappedIds: すべて写像済みなら空',
    unmappedIds(new Set(['FR-01']), new Set(['FR-01', 'FR-09'])).length === 0);

  t('isExcludedPath: AST 配下は対象外',
    isExcludedPath('src/ai-stock-trading/backend/x/XTests.cs') && !isExcludedPath('src/knowledge/backend/x/XTests.cs'));

  // ratchet の 3 判定。
  {
    const r = classifyAgainstAllowlist(['FR-17'], []);
    t('allowlist に無い未写像は blocked（fail 対象）', r.blocked.length === 1 && r.pending.length === 0 && r.stale.length === 0);
  }
  {
    const r = classifyAgainstAllowlist(['FR-17'], ['FR-17']);
    t('allowlist どおりの未写像は pending（warn）', r.pending.length === 1 && r.blocked.length === 0 && r.stale.length === 0);
  }
  {
    const r = classifyAgainstAllowlist([], ['FR-17']);
    t('写像済みなのに allowlist に残るのは stale（fail 対象）', r.stale.length === 1 && r.blocked.length === 0);
  }
  {
    const r = classifyAgainstAllowlist(['FR-17', 'SC-18'], ['FR-17']);
    t('一部だけ allowlist 済みなら残りが blocked', r.blocked.join(',') === 'SC-18' && r.pending.join(',') === 'FR-17');
  }

  t('TEST_FILE: C# / Vitest / Playwright の命名を拾う',
    TEST_FILE.test('DocumentServiceTests.cs') && TEST_FILE.test('a.test.tsx') && TEST_FILE.test('b.spec.ts')
      && !TEST_FILE.test('Program.cs'));

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[check-test-traceability] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-test-traceability] 自己試験 ${cases.length} 件 OK。`);
}

// --- 実行 -----------------------------------------------------------------------

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }

  const specIds = collectSpecIds();
  const testIds = collectTestIds();
  const unmapped = unmappedIds(specIds, testIds);
  const mapped = specIds.size - unmapped.length;

  const summary = process.env.GITHUB_STEP_SUMMARY;
  if (summary) {
    const lines = [
      '### 受け入れ基準 → テストの写像（#453）',
      '',
      `- 仕様書のある起点 ID: **${specIds.size}**`,
      `- テストから参照済み: **${mapped}**`,
      `- 未写像: **${unmapped.length}**${unmapped.length ? ` — ${unmapped.join(' / ')}` : ''}`,
      '',
      '規約は `docs/tests/TEST_STRATEGY.md`。テストの直前のコメントに起点 ID を書くこと。',
    ];
    try { fs.appendFileSync(summary, lines.join('\n') + '\n'); } catch { /* サマリ不可でも検査は続ける */ }
  }

  const { blocked, pending, stale } = classifyAgainstAllowlist(unmapped, readAllowlist());

  if (pending.length) {
    notice(`受け入れ基準 → テストの写像の残件 ${pending.length} 件（allowlist 済み）: ${pending.join(' / ')}。` +
      '対応するテストを書いたら scripts/test-traceability-allowlist.json から削除すること。');
  }

  const failures = [];
  if (blocked.length) {
    failures.push(`[未写像] ${blocked.join(' / ')}\n    docs/tests/ に仕様書があるのに、テスト側のコメントに当該 ID がありません。` +
      '\n    テストを書くか、段取り上どうしても後回しにするなら scripts/test-traceability-allowlist.json へ理由とともに追加してください。');
  }
  if (stale.length) {
    failures.push(`[allowlist 減らし忘れ] ${stale.join(' / ')}\n    既にテストから参照されています。allowlist の該当行を削除してください。`);
  }

  if (failures.length === 0) {
    console.log(`[check-test-traceability] OK: 仕様書のある起点 ID ${specIds.size} 件中 ${mapped} 件が写像済み` +
      `（未写像 ${pending.length} 件はすべて allowlist 済み）。`);
    process.exit(0);
  }
  console.error(`[check-test-traceability] 写像の違反 ${failures.length} 件を検出しました:`);
  for (const f of failures) console.error(`\n  ${f}`);
  console.error('\n規約は docs/tests/TEST_STRATEGY.md を参照してください。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  EXCLUDED_UNITS,
  TEST_FILE,
  isExcludedPath,
  specIdOf,
  idsInText,
  unmappedIds,
  classifyAgainstAllowlist,
  readAllowlist,
  collectSpecIds,
  collectTestIds,
};
