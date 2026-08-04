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
 *   1. 順方向（#453）: docs/tests/ に仕様書が在る起点 ID（FR-xx / UC-xx / SC-xx / NFR）のうち、
 *      src/ のテストから 1 件も参照されていないものを「未写像」として報告する。
 *   2. 逆方向（#472）: 計画レンジ（.claude/rules/traceability.md「起点 ID の種別」節）にあるのに
 *      docs/tests/ に仕様書が無い ID を warn で列挙する。1 だけでは「仕様書を作らなければ何も
 *      言われない」fail-open が残り、着手時の取りこぼしが永久に沈黙するためである。
 *      さらにそのうち **src/ のテストが既に参照している ID**（＝実装先行）は allowlist の
 *      specMissing と突き合わせて fail にする（後述の ratchet）。
 *
 * 計画レンジを .claude/rules/traceability.md からパースする理由（#472 の設計判断）:
 *   同ファイルは「計画側で ID が増減したら本節を追随させる」と自ら宣言するレンジの正であり、
 *   監査 / trace-check も既にこの節を基準にしている。レンジを別の設定ファイルへ写すと同じ事実の
 *   情報源が 2 つになり、片方だけ更新されても誰も気付かない——本 issue が塞ごうとしている穴と
 *   同型の穴を新設することになる。計画リポジトリ（planning/）は submodule で未 populate があり得るため
 *   直接は読めない（skip すれば 0 件検査に戻る）。
 *   パースの脆さは (a) 節スコープの限定（後段の AST 採番レンジを構造的に排除）、(b) バッククォート
 *   囲みの `X-nn..nn` だけを拾う、(c) 拾えなければ fail、の 3 点で抑える。
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
 *   逆方向（#472）も同じ形を採る。着手時点の実測が **実装先行 7 件（UC-01〜UC-07）** で 0 ではない
 *   ため素の fail は初回から赤くなる。よって allowlist の specMissing へ既知の 7 件を明示し、
 *   **新規の悪化だけを fail** させる（未着手の FR/UC/SC が仕様書を持たないのは正当なので warn のまま）。
 *
 * 使い方:
 *   node scripts/check-test-traceability.js
 *   node scripts/check-test-traceability.js --self-test
 */
const fs = require('fs');
const path = require('path');
const { warn, notice } = require('./lib/ci-annotate');
const { excludedUnits, makeIsExcludedPath } = require('./lib/excluded-units.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const SPEC_DIR = 'docs/tests';
const SRC_DIR = 'src';
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', 'dist', 'coverage']);

/**
 * 検査対象外のユニット。他プロジェクト（AST）は独自の計画 ID 体系を持ち、本リポジトリの
 * docs/tests/ の FR/SC とは名前空間が異なる（.claude/rules/traceability.md）。
 *
 * 値は .gitmodules（src/<unit> の submodule）から導出する。3 検査器が同じ集合を独立に
 * ハードコードしていた形は、submodule ユニットの追加（IADR-0056 決定 6）で 3 箇所同時に
 * 狭すぎになるため単一情報源へ寄せた（issue #473。規則は scripts/lib/excluded-units.js）。
 */
const EXCLUDED_UNITS = excludedUnits({ root: REPO_ROOT });

/** テストファイルとみなす拡張子パターン。 */
const TEST_FILE = /(Tests?\.cs|\.(test|spec)\.(ts|tsx|js|jsx))$/i;

/**
 * 計画レンジ（FR/UC/SC の採番範囲）の単一情報源。同ファイルの当該節だけを見る。
 * **節スコープの限定は必須**である——同ファイルの後段には AST（別プロジェクト）の採番レンジ
 * （FR-01..20 / UC-01..07 / SC-01..03）が書かれており、全文を舐めると混ざる。
 */
const RULES_FILE = '.claude/rules/traceability.md';
const PLAN_RANGE_HEADING = '## 起点 ID の種別';

/** 計画レンジを展開する ID 種別。NFR は連番を持たないためレンジの対象外（ADR はテスト仕様書の対象外）。 */
const PLAN_KINDS = ['FR', 'UC', 'SC'];

// --- 純粋ロジック ---------------------------------------------------------------

function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

const isExcludedPath = makeIsExcludedPath(EXCLUDED_UNITS);

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

// --- 計画レンジ（逆方向検査の基準・#472） ---------------------------------------

/**
 * `.claude/rules/traceability.md` から「起点 ID の種別」節の本文だけを切り出す。
 * 次の `## ` 見出しの直前まで。見つからなければ null。
 */
function planRangeSection(md) {
  // 先頭に改行を足して「ファイル冒頭が当該見出し」の場合も同じ探索で拾えるようにする。
  const text = `\n${String(md).replace(/\r\n/g, '\n')}`;
  const start = text.indexOf(`\n${PLAN_RANGE_HEADING}\n`);
  if (start < 0) return null;
  const bodyStart = start + PLAN_RANGE_HEADING.length + 2;
  const next = text.slice(bodyStart).search(/\n## /);
  return next < 0 ? text.slice(bodyStart) : text.slice(bodyStart, bodyStart + next);
}

/**
 * 節の本文から `FR-01..21` の形（バッククォート囲み）のレンジを拾う。
 * 戻り値: { FR: { from, to }, UC: {...}, SC: {...} }。ADR-xxxx は対象外。
 */
function parsePlanRanges(sectionText) {
  const out = {};
  const re = /`(FR|UC|SC)-(\d+)\.\.(\d+)`/g;
  let m;
  while ((m = re.exec(String(sectionText))) !== null) {
    const [, kind, from, to] = m;
    if (!PLAN_KINDS.includes(kind)) continue;
    out[kind] = { from: Number(from), to: Number(to) };
  }
  return out;
}

/**
 * レンジをゼロ埋め ID の配列へ展開する。**壊れた入力は例外にする**（後述 readPlanIds）。
 */
function expandPlanIds(ranges) {
  const ids = [];
  for (const kind of PLAN_KINDS) {
    const r = ranges[kind];
    if (!r) throw new Error(`計画レンジに ${kind} が見つかりません（期待する書式: \`${kind}-01..NN\`）`);
    if (!(r.from >= 1) || !(r.to >= r.from)) {
      throw new Error(`計画レンジ ${kind} の範囲が不正です: ${JSON.stringify(r)}`);
    }
    for (let n = r.from; n <= r.to; n++) ids.push(`${kind}-${String(n).padStart(2, '0')}`);
  }
  return ids;
}

/**
 * 実ファイルから計画 ID を読む。
 *
 * **読めない／拾えないときは例外**にする（main() が fail へ変換する）。逆方向検査だけを黙って
 * skip すると「計画レンジ 0 件・不足 0 件」という最も安全に見える出力で素通りし、本検査が塞ごうと
 * している fail-open そのものへ戻る。RULES_FILE は submodule ではなく本リポジトリの追跡ファイルなので、
 * 読めないのは環境差ではなく規約側の破壊（節の改名・書式変更）である。
 */
function readPlanIds(rulesPath = path.join(REPO_ROOT, RULES_FILE)) {
  let md;
  try {
    md = fs.readFileSync(rulesPath, 'utf8');
  } catch {
    throw new Error(`${RULES_FILE} を読めません（計画レンジの単一情報源）: ${toPosix(rulesPath)}`);
  }
  const section = planRangeSection(md);
  if (section === null) throw new Error(`${RULES_FILE} に「${PLAN_RANGE_HEADING}」節が見つかりません`);
  return expandPlanIds(parsePlanRanges(section));
}

/** 計画レンジにあるのに docs/tests/ に仕様書が無い ID（warn 対象）。 */
function missingSpecIds(planIds, specIds) {
  return [...planIds].filter((id) => !specIds.has(id)).sort();
}

/**
 * 仕様書が無い計画 ID のうち、src/ のテストが既に参照しているもの（＝実装先行）。
 * 未着手（テストも無い）とは性質が異なり、こちらは ratchet で fail 対象にする。
 */
function implementedWithoutSpec(missing, testIds) {
  return [...missing].filter((id) => testIds.has(id)).sort();
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

/** allowlist ファイルを読む。無ければ空オブジェクト。 */
function readAllowlistFile() {
  try {
    return JSON.parse(fs.readFileSync(path.join(__dirname, 'test-traceability-allowlist.json'), 'utf8'));
  } catch {
    return {};
  }
}

/** allowlist（未写像を許す起点 ID）を読む。無ければ空。 */
function readAllowlist() {
  return readAllowlistFile().pending || [];
}

/** allowlist（テスト仕様書が無いまま実装が先行することを許す起点 ID・#472）を読む。無ければ空。 */
function readSpecMissingAllowlist() {
  return readAllowlistFile().specMissing || [];
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

  // --- 逆方向検査（#472）: 計画レンジのパースと不足の列挙 -------------------------

  // 実ファイルの構造を模したフィクスチャ。**後段に AST の採番レンジを置く**のが要点で、
  // 節スコープを外した実装ならここで別プロジェクトのレンジを拾って落ちる。
  const RULES_FIXTURE = [
    '---', 'paths:', '  - "**/*"', '---', '',
    '# トレーサビリティ（追跡可能性）の規約', '',
    '## 起点 ID の種別', '',
    '- `FR-xx`: 機能要求（計画リポ `02_requirements/`）',
    '- `NFR`: 非機能要件',
    '本リポジトリではそれが **MSP（microservices-platform）** であり、ID レンジは',
    '`FR-01..21` / `UC-01..11` / `SC-01..21` / `ADR-0001..0039`（`ADR-0035` は番号予約のみ）',
    'である（planning `df8bce5` 時点）。', '',
    '## 複数プロジェクトを跨ぐ場合の ID 修飾', '',
    '**AST 側が自前で採番しているレンジは `FR-01..20` / `UC-01..07` / `SC-01..03`**（submodule pin 時点）',
  ].join('\n');

  const fixtureSection = planRangeSection(RULES_FIXTURE);
  t('planRangeSection: 「起点 ID の種別」節だけを切り出す（次の ## の直前まで）',
    fixtureSection !== null && fixtureSection.includes('`FR-01..21`') && !fixtureSection.includes('AST 側'));
  t('planRangeSection: 節が無ければ null（fail-loud の入口）',
    planRangeSection('# 見出しのみ\n\n本文') === null);

  {
    const r = parsePlanRanges(fixtureSection);
    t('parsePlanRanges: 正例 — FR/UC/SC のレンジを拾う',
      JSON.stringify(r) === '{"FR":{"from":1,"to":21},"UC":{"from":1,"to":11},"SC":{"from":1,"to":21}}', r);
    t('parsePlanRanges: 負例 — ADR-0001..0039 はテスト仕様書の対象外なので拾わない', r.ADR === undefined);
  }
  t('parsePlanRanges: 負例 — 全文を渡しても AST レンジで上書きされないのは節スコープの責務',
    JSON.stringify(parsePlanRanges(RULES_FIXTURE).FR) === '{"from":1,"to":20}');

  {
    const ids = expandPlanIds(parsePlanRanges(fixtureSection));
    t('expandPlanIds: 21 + 11 + 21 = 53 件へゼロ埋め展開する',
      ids.length === 53 && ids[0] === 'FR-01' && ids[20] === 'FR-21' && ids[21] === 'UC-01'
      && ids[52] === 'SC-21', ids.length);
  }
  t('expandPlanIds: 負例 — 種別が欠けたら例外（黙って 0 件検査に戻さない）',
    (() => { try { expandPlanIds({ FR: { from: 1, to: 3 } }); return false; } catch { return true; } })());
  t('expandPlanIds: 負例 — to < from は例外',
    (() => { try { expandPlanIds({ FR: { from: 5, to: 1 }, UC: { from: 1, to: 1 }, SC: { from: 1, to: 1 } }); return false; } catch { return true; } })());
  t('readPlanIds: 負例 — 読めないファイルは例外', (() => {
    try { readPlanIds(path.join(REPO_ROOT, 'no-such-rules-file.md')); return false; } catch { return true; }
  })());

  t('missingSpecIds: 正例 — 計画レンジにあって仕様書が無い ID を返す',
    JSON.stringify(missingSpecIds(['FR-01', 'FR-02', 'UC-01'], new Set(['FR-01']))) === '["FR-02","UC-01"]');
  t('missingSpecIds: 負例 — 全 ID に仕様書があれば空',
    missingSpecIds(['FR-01'], new Set(['FR-01', 'NFR'])).length === 0);

  t('implementedWithoutSpec: 正例 — 仕様書は無いがテストが参照済みの ID だけを返す',
    JSON.stringify(implementedWithoutSpec(['FR-16', 'UC-01'], new Set(['UC-01', 'FR-01']))) === '["UC-01"]');
  t('implementedWithoutSpec: 負例 — 未着手（テスト参照も無い）は対象外',
    implementedWithoutSpec(['FR-16'], new Set(['FR-01'])).length === 0);

  // 実ファイルでの固定。レンジが読めなくなる／数が変わることを検知する（数が変わったら本行を直す）。
  {
    const ids = readPlanIds();
    t('実ファイル: .claude/rules/traceability.md から計画 ID 53 件を読める',
      ids.length === 53 && ids.includes('FR-21') && ids.includes('UC-11') && ids.includes('SC-21'), ids.length);
  }

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

  // 逆方向（#472）。計画レンジが読めない＝規約側の破壊なので、黙って skip せず即 fail する。
  let planIds;
  try {
    planIds = readPlanIds();
  } catch (e) {
    console.error(`[check-test-traceability] 計画レンジを取得できませんでした: ${e.message}`);
    console.error(`  逆方向検査（仕様書の無い計画 ID の列挙）はレンジ無しでは 0 件検査になり、` +
      `検査しているつもりで何も見ていない状態になるため fail させています。`);
    console.error(`  ${RULES_FILE} の「${PLAN_RANGE_HEADING}」節に \`FR-01..21\` の書式でレンジを書いてください。`);
    process.exit(1);
    return;
  }
  const missingSpec = missingSpecIds(planIds, specIds);
  const specFirst = planIds.length - missingSpec.length;
  const implFirst = implementedWithoutSpec(missingSpec, testIds);
  const specMissing = classifyAgainstAllowlist(implFirst, readSpecMissingAllowlist());

  const summary = process.env.GITHUB_STEP_SUMMARY;
  if (summary) {
    const lines = [
      '### 受け入れ基準 → テストの写像（#453 / #472）',
      '',
      `- 仕様書のある起点 ID: **${specIds.size}**`,
      `- テストから参照済み: **${mapped}**`,
      `- 未写像: **${unmapped.length}**${unmapped.length ? ` — ${unmapped.join(' / ')}` : ''}`,
      '',
      `- 計画レンジの ID: **${planIds.length}**（\`${RULES_FILE}\` より）`,
      `- うちテスト仕様書あり: **${specFirst}**`,
      `- 仕様書なし（warn）: **${missingSpec.length}**${missingSpec.length ? ` — ${missingSpec.join(' / ')}` : ''}`,
      `  - うち実装先行（テストは参照済み・**fail 対象**）: **${implFirst.length}**` +
        `${implFirst.length ? ` — ${implFirst.join(' / ')}` : ''}`,
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

  // 未着手 FR/UC/SC が仕様書を持たないのは正当なので fail にしない（warn で取りこぼしを可視化する）。
  if (missingSpec.length) {
    warn(`計画レンジ ${planIds.length} 件のうち ${missingSpec.length} 件に docs/tests/ のテスト仕様書がありません: ` +
      `${missingSpec.join(' / ')}。着手する issue は docs/tests/<ID>_*.md を作成すること` +
      `（docs/tests/TEST_STRATEGY.md「各ドメイン issue が守ること」1）。`);
  }
  if (specMissing.pending.length) {
    notice(`テスト仕様書の無いまま実装が先行している起点 ID ${specMissing.pending.length} 件（allowlist 済み）: ` +
      `${specMissing.pending.join(' / ')}。仕様書を書いたら scripts/test-traceability-allowlist.json の ` +
      'specMissing から削除すること。');
  }

  const failures = [];
  if (blocked.length) {
    failures.push(`[未写像] ${blocked.join(' / ')}\n    docs/tests/ に仕様書があるのに、テスト側のコメントに当該 ID がありません。` +
      '\n    テストを書くか、段取り上どうしても後回しにするなら scripts/test-traceability-allowlist.json へ理由とともに追加してください。');
  }
  if (stale.length) {
    failures.push(`[allowlist 減らし忘れ] ${stale.join(' / ')}\n    既にテストから参照されています。allowlist の該当行を削除してください。`);
  }
  if (specMissing.blocked.length) {
    failures.push(`[実装先行・仕様書なし] ${specMissing.blocked.join(' / ')}\n    src/ のテストが当該 ID を参照しているのに、docs/tests/ にテスト仕様書がありません。` +
      '\n    docs/tests/<ID>_<概要>.md を作るか、段取り上どうしても後回しにするなら scripts/test-traceability-allowlist.json の specMissing へ理由とともに追加してください。');
  }
  if (specMissing.stale.length) {
    failures.push(`[specMissing 減らし忘れ] ${specMissing.stale.join(' / ')}\n    既に仕様書があるか、テストからの参照が無くなっています。allowlist の specMissing から該当行を削除してください。`);
  }

  if (failures.length === 0) {
    console.log(`[check-test-traceability] OK: 仕様書のある起点 ID ${specIds.size} 件中 ${mapped} 件が写像済み` +
      `（未写像 ${pending.length} 件はすべて allowlist 済み）。` +
      `\n  計画レンジ ${planIds.length} 件中 ${specFirst} 件にテスト仕様書あり` +
      `（仕様書なし ${missingSpec.length} 件は warn。うち実装先行 ${implFirst.length} 件はすべて allowlist 済み）。`);
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
  RULES_FILE,
  PLAN_RANGE_HEADING,
  PLAN_KINDS,
  isExcludedPath,
  specIdOf,
  idsInText,
  unmappedIds,
  classifyAgainstAllowlist,
  readAllowlist,
  readSpecMissingAllowlist,
  collectSpecIds,
  collectTestIds,
  planRangeSection,
  parsePlanRanges,
  expandPlanIds,
  readPlanIds,
  missingSpecIds,
  implementedWithoutSpec,
};
