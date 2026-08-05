#!/usr/bin/env node
'use strict';
/*
 * check-test-spec-coverage.js
 * **実在するバックエンドテストが `docs/tests/` のテスト仕様書に載っているか**を機械検査する
 * （NFR, Issue #510）。外部依存ゼロ。
 *
 * なぜ要るか（#510 の実測）:
 *   #503（PR #508）が docs/tests/SC-05〜SC-08 を「全面改訂」した際、フロントエンドの構造で置き換えた
 *   ために **バックエンド試験の節が丸ごと失われた**。テスト自体は消えていない——記載だけが消えた。
 *   同じ現象は SC-07 でも起きたが、そちらは #501（PR #509）が衝突解消の過程で偶然気づいて復元した。
 *   **衝突が起きなかった SC-05 / SC-06 は誰も読み直さず、レビューでも CI でも捕まらなかった。**
 *
 *   既存の check-test-traceability.js は**起点 ID の写像**（FR/UC/SC がテストから 1 件でも参照されて
 *   いるか）を見るため、この欠落を検出しない——SC-05 の ID はフロントのテストから参照され続けるので、
 *   バックエンド節が丸ごと消えても順方向・逆方向のどちらも緑のままである。
 *
 * 検査の向き（#510 が挙げた 2 方向のうち (b)）:
 *   (a) 仕様書が挙げるテスト名が実在するか  → **今回の欠陥は止まらない**（残った記載はすべて実在した）
 *   (b) 実在するテストが仕様書に載っているか → **止まる**。本検査は (b) を実装する。
 *
 * 粒度（テストクラス = `*Tests.cs` のファイル名）:
 *   - **アセンブリ（テストプロジェクト）単位では止まらない。** `Platform.Bff.Tests` は複数の仕様書から
 *     参照され続けるため、SC-05 の節が消えても「どこにも無い」にならない。
 *   - **メソッド単位は細かすぎる。** 表の 1 行を消すだけで赤くなり、仕様書の正当な要約を禁じてしまう。
 *   - よってクラス（ファイル）単位。節が落ちればクラス名がどこからも参照されなくなるので検出でき、
 *     表の行の増減では赤くならない。
 *
 * 判定（ratchet。既存ゲートと同じ 3 判定 + warn）:
 *   - baseline にあるのに今は未記載（テストは実在）→ **fail**（＝今回の欠陥。節の消失）
 *   - baseline にあるがテストごと消えた            → **fail**（baseline を減らし、仕様書の記載も見直す）
 *   - 記載されたのに baseline に無い                → **fail**（床を上げっぱなしにする。--update で更新）
 *   - 実在するが未記載で baseline にも無い          → **warn**（基盤・回帰テストに記載義務は負わせない）
 *
 * fail-closed:
 *   走査結果が 0 件（テストクラス 0 / 仕様書 0）、baseline が読めない・壊れている、のいずれも fail。
 *   「見つからないから素通り」は本検査が塞ごうとしている穴と同型である。
 *
 * 対象をバックエンドに限る理由は docs/adr/IADR-0130_test-spec-coverage-ratchet.md を正とする。
 *
 * 使い方:
 *   node scripts/check-test-spec-coverage.js
 *   node scripts/check-test-spec-coverage.js --update     # baseline を現状で作り直す（差分は PR に載る）
 *   node scripts/check-test-spec-coverage.js --self-test
 */
const fs = require('fs');
const path = require('path');
const { warn } = require('./lib/ci-annotate');
const { excludedUnits, makeIsExcludedPath } = require('./lib/excluded-units.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const SPEC_DIR = 'docs/tests';
const SRC_DIR = 'src';
const BASELINE_FILE = 'scripts/test-spec-coverage-baseline.json';
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', 'dist', 'coverage']);

/** バックエンドの xUnit テストクラスのファイル名。`*Test.cs`（単数）は本リポジトリに存在しない。 */
const TEST_CLASS_FILE = /(^|\/)([A-Za-z0-9_]+Tests)\.cs$/;

/**
 * 検査対象外のユニット（他プロジェクトの submodule）。単一情報源は `.gitmodules`
 * （scripts/lib/excluded-units.js / IADR-0120）。
 */
const EXCLUDED_UNITS = excludedUnits({ root: REPO_ROOT });
const isExcludedPath = makeIsExcludedPath(EXCLUDED_UNITS);

// --- 純粋ロジック ---------------------------------------------------------------

function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

/** パスからテストクラス名を取り出す。テストクラスでなければ null。 */
function testClassNameOf(relPath) {
  const m = toPosix(relPath).match(TEST_CLASS_FILE);
  return m ? m[2] : null;
}

/**
 * 仕様書の本文にクラス名が「識別子として」現れるかを見る。
 *
 * 単純な部分一致だと、あるクラス名が別のクラス名の**接尾辞**であるときに誤って被覆済みと判定する
 * （例: `HealthEndpointTests` は `BffHealthEndpointTests` の一部として現れうる）。前後を単語文字で
 * 挟まれていないことだけを要求する——`/` 区切りのパス、`` `X.cs` ``、`X` の素の言及をいずれも拾い、
 * 接尾辞の巻き込みだけを落とす。
 */
function mentionsClass(text, className) {
  const re = new RegExp(`(?<![A-Za-z0-9_])${className}(?![A-Za-z0-9_])`);
  return re.test(String(text));
}

/**
 * 実在するクラス名のうち、仕様書本文から参照されているものを返す（Set）。
 * `names` は Iterable<string>、`text` は docs/tests/ 全体を連結した本文。
 */
function documentedNames(names, text) {
  const out = new Set();
  for (const n of names) if (mentionsClass(text, n)) out.add(n);
  return out;
}

/**
 * ratchet の 4 分類。
 *   regressed      : baseline にあり、テストは実在するのに今は未記載 → fail（節の消失）
 *   removedTest    : baseline にあるが、テストクラス自体が実在しない → fail（baseline の減らし忘れ）
 *   newlyDocumented: 記載されたのに baseline に無い                 → fail（床の上げ忘れ）
 *   undocumented   : 実在するが未記載で baseline にも無い           → warn
 */
function classify({ existing, documented, baseline }) {
  const ex = new Set(existing);
  const doc = new Set(documented);
  const base = new Set(baseline);
  const sorted = (s) => [...s].sort();
  return {
    regressed: sorted(new Set([...base].filter((n) => ex.has(n) && !doc.has(n)))),
    removedTest: sorted(new Set([...base].filter((n) => !ex.has(n)))),
    newlyDocumented: sorted(new Set([...doc].filter((n) => !base.has(n)))),
    undocumented: sorted(new Set([...ex].filter((n) => !doc.has(n) && !base.has(n)))),
  };
}

// --- ファイル走査 ---------------------------------------------------------------

function walk(dir, predicate, acc = [], root = REPO_ROOT) {
  const abs = path.join(root, dir);
  let entries;
  try {
    entries = fs.readdirSync(abs, { withFileTypes: true });
  } catch {
    return acc;
  }
  for (const e of entries) {
    if (SKIP_DIRS.has(e.name)) continue;
    const rel = toPosix(path.join(dir, e.name));
    if (e.isDirectory()) walk(rel, predicate, acc, root);
    else if (predicate(rel)) acc.push(rel);
  }
  return acc;
}

/**
 * 実在するテストクラスを集める。戻り値は Map<className, string[]（相対パス）>。
 * **同名クラスが複数プロジェクトに在りうる**（`IntrospectionEndpointTests` など）。本検査は
 * ファイル名をキーにするため、同名の集合は 1 件として扱う（限界は IADR-0130 §限界 に明記）。
 */
function collectTestClasses(root = REPO_ROOT) {
  const map = new Map();
  for (const f of walk(SRC_DIR, (p) => TEST_CLASS_FILE.test(p) && !isExcludedPath(p), [], root)) {
    const name = testClassNameOf(f);
    if (!name) continue;
    if (!map.has(name)) map.set(name, []);
    map.get(name).push(f);
  }
  return map;
}

/** docs/tests/ の Markdown を読み、{ text, files } を返す。 */
function collectSpecText(root = REPO_ROOT) {
  const files = walk(SPEC_DIR, (p) => /\.md$/i.test(p), [], root);
  const text = files.map((f) => fs.readFileSync(path.join(root, f), 'utf8')).join('\n');
  return { text, files };
}

/** baseline を読む。**読めない・壊れているは例外**（fail-closed）。 */
function readBaseline(file = path.join(REPO_ROOT, BASELINE_FILE)) {
  let raw;
  try {
    raw = fs.readFileSync(file, 'utf8');
  } catch {
    throw new Error(`${BASELINE_FILE} を読めません（床の単一情報源）: ${toPosix(file)}`);
  }
  let parsed;
  try {
    parsed = JSON.parse(raw);
  } catch (e) {
    throw new Error(`${BASELINE_FILE} を JSON として解釈できません: ${e.message}`);
  }
  if (!parsed || !Array.isArray(parsed.documented)) {
    throw new Error(`${BASELINE_FILE} に配列 documented がありません`);
  }
  return parsed.documented.map(String);
}

function writeBaseline(documented, file = path.join(REPO_ROOT, BASELINE_FILE)) {
  const body = {
    $comment: [
      'check-test-spec-coverage.js の床（NFR / issue #510）。',
      'docs/tests/ のテスト仕様書から参照されているバックエンドテストクラスの一覧である。',
      'ここに載っているクラスが仕様書から参照されなくなると CI が fail する（節の消失を止める）。',
      '記載を増やしたら node scripts/check-test-spec-coverage.js --update で更新し、差分を PR に載せること。',
      'テストクラスを削除したときは、仕様書の該当記載を直したうえで同じコマンドで更新する。',
    ].join(' '),
    documented: [...documented].sort(),
  };
  fs.writeFileSync(file, JSON.stringify(body, null, 2) + '\n');
}

// --- 自己試験 -------------------------------------------------------------------

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });

  t('testClassNameOf: xUnit のテストクラスを拾う',
    testClassNameOf('src/platform/backend/Bff/Platform.Bff.Tests/BffDocumentWriteEndpointTests.cs')
      === 'BffDocumentWriteEndpointTests');
  t('testClassNameOf: テストでない .cs は null',
    testClassNameOf('src/platform/backend/Bff/Platform.Bff/Program.cs') === null);
  t('testClassNameOf: フィクスチャ（BffTestFactory）は対象外',
    testClassNameOf('src/platform/backend/Bff/Platform.Bff.Tests/BffTestFactory.cs') === null);
  t('testClassNameOf: フロントのテストは対象外（本検査はバックエンド限定）',
    testClassNameOf('src/knowledge/frontend/src/features/sc05-documents/DocumentManagementPage.test.tsx') === null);

  t('mentionsClass: 素の言及を拾う', mentionsClass('テスト: `BffDataSourceEndpointTests`', 'BffDataSourceEndpointTests'));
  t('mentionsClass: パス中の言及を拾う',
    mentionsClass('../../src/platform/backend/Bff/Platform.Bff.Tests/BffDataSourceEndpointTests.cs', 'BffDataSourceEndpointTests'));
  t('mentionsClass: 負例 — 接尾辞の巻き込みを拒む（BffHealthEndpointTests は HealthEndpointTests ではない）',
    !mentionsClass('`BffHealthEndpointTests` を見る', 'HealthEndpointTests'));
  t('mentionsClass: 正例 — 同じ本文に素の HealthEndpointTests があれば拾う',
    mentionsClass('`BffHealthEndpointTests` と `HealthEndpointTests`', 'HealthEndpointTests'));
  t('mentionsClass: 負例 — 名前が現れなければ false',
    !mentionsClass('フロントの表しかない本文', 'BffDocumentWriteEndpointTests'));

  t('documentedNames: 本文に現れるものだけを返す',
    [...documentedNames(['A_Tests', 'BTests'], '`BTests` のみ')].join(',') === 'BTests');

  // --- ratchet の 4 判定 ---------------------------------------------------------
  {
    // 今回の欠陥そのもの: テストは実在するのに仕様書から消えた。
    const r = classify({ existing: ['X_Tests', 'YTests'], documented: ['YTests'], baseline: ['X_Tests', 'YTests'] });
    t('classify: 節の消失は regressed（fail 対象）',
      r.regressed.join(',') === 'X_Tests' && r.removedTest.length === 0
        && r.newlyDocumented.length === 0 && r.undocumented.length === 0, r);
  }
  {
    const r = classify({ existing: ['YTests'], documented: ['YTests'], baseline: ['X_Tests', 'YTests'] });
    t('classify: テストごと消えたら removedTest（fail 対象・baseline の減らし忘れ）',
      r.removedTest.join(',') === 'X_Tests' && r.regressed.length === 0, r);
  }
  {
    const r = classify({ existing: ['X_Tests', 'ZTests'], documented: ['X_Tests', 'ZTests'], baseline: ['X_Tests'] });
    t('classify: 記載を増やしたら newlyDocumented（fail 対象・床の上げ忘れ）',
      r.newlyDocumented.join(',') === 'ZTests' && r.regressed.length === 0, r);
  }
  {
    const r = classify({ existing: ['X_Tests', 'ZTests'], documented: ['X_Tests'], baseline: ['X_Tests'] });
    t('classify: 未記載の新規テストは undocumented（warn どまり）',
      r.undocumented.join(',') === 'ZTests' && r.regressed.length === 0
        && r.newlyDocumented.length === 0 && r.removedTest.length === 0, r);
  }
  {
    const r = classify({ existing: ['X_Tests'], documented: ['X_Tests'], baseline: ['X_Tests'] });
    t('classify: 変化が無ければ 4 分類とも空（緑）',
      r.regressed.length + r.removedTest.length + r.newlyDocumented.length + r.undocumented.length === 0, r);
  }

  // --- baseline の fail-closed --------------------------------------------------
  const os = require('os');
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'tsc-baseline-'));
  const bad = path.join(tmp, 'bad.json');
  t('readBaseline: 負例 — 存在しないファイルは例外',
    (() => { try { readBaseline(path.join(tmp, 'nope.json')); return false; } catch { return true; } })());
  fs.writeFileSync(bad, '{ not json');
  t('readBaseline: 負例 — 壊れた JSON は例外',
    (() => { try { readBaseline(bad); return false; } catch { return true; } })());
  fs.writeFileSync(bad, '{"documented": "X"}');
  t('readBaseline: 負例 — documented が配列でなければ例外',
    (() => { try { readBaseline(bad); return false; } catch { return true; } })());
  fs.writeFileSync(bad, '{"documented": ["ATests"]}');
  t('readBaseline: 正例 — 配列を返す', readBaseline(bad).join(',') === 'ATests');

  // --- 走査の fail-closed（実ファイル木のフィクスチャ） --------------------------
  const fake = fs.mkdtempSync(path.join(os.tmpdir(), 'tsc-tree-'));
  fs.mkdirSync(path.join(fake, 'src', 'unit', 'backend'), { recursive: true });
  fs.mkdirSync(path.join(fake, 'docs', 'tests'), { recursive: true });
  fs.writeFileSync(path.join(fake, 'src', 'unit', 'backend', 'AlphaTests.cs'), '// x');
  fs.writeFileSync(path.join(fake, 'src', 'unit', 'backend', 'BetaTests.cs'), '// x');
  fs.writeFileSync(path.join(fake, 'src', 'unit', 'backend', 'Program.cs'), '// x');
  fs.writeFileSync(path.join(fake, 'docs', 'tests', 'SC-99_x.md'), 'テスト: `AlphaTests`');
  {
    const classes = collectTestClasses(fake);
    const spec = collectSpecText(fake);
    t('collectTestClasses: フィクスチャから 2 クラスを拾い Program.cs は拾わない',
      classes.size === 2 && classes.has('AlphaTests') && classes.has('BetaTests'), [...classes.keys()]);
    t('collectSpecText: docs/tests/ の md を 1 件読む', spec.files.length === 1);
    const r = classify({ existing: classes.keys(), documented: documentedNames(classes.keys(), spec.text), baseline: ['AlphaTests'] });
    t('走査 → 判定の通し: BetaTests は undocumented（warn）で赤くならない',
      r.undocumented.join(',') === 'BetaTests' && r.regressed.length === 0, r);
    // 変異: 仕様書から AlphaTests の記載を消すと regressed になる。
    fs.writeFileSync(path.join(fake, 'docs', 'tests', 'SC-99_x.md'), 'フロントの表しかない');
    const spec2 = collectSpecText(fake);
    const r2 = classify({ existing: classes.keys(), documented: documentedNames(classes.keys(), spec2.text), baseline: ['AlphaTests'] });
    t('変異試験（自己試験内）: 記載を消すと regressed が立つ', r2.regressed.join(',') === 'AlphaTests', r2);
  }
  {
    const empty = fs.mkdtempSync(path.join(os.tmpdir(), 'tsc-empty-'));
    t('fail-closed の入口: 走査対象 0 件を 0 件として返す（main が fail へ変換する）',
      collectTestClasses(empty).size === 0 && collectSpecText(empty).files.length === 0);
    fs.rmSync(empty, { recursive: true, force: true });
  }
  fs.rmSync(fake, { recursive: true, force: true });
  fs.rmSync(tmp, { recursive: true, force: true });

  {
    // EXCLUDED_UNITS は Set（scripts/lib/excluded-units.js）。.gitmodules 由来なので中身は環境に依らない。
    const units = [...EXCLUDED_UNITS];
    t('isExcludedPath: 他プロジェクト（submodule ユニット）配下は対象外',
      units.length > 0
      && isExcludedPath(`src/${units[0]}/backend/XTests.cs`)
      && !isExcludedPath('src/knowledge/backend/XTests.cs'), units);
  }

  // 実データでの固定。走査が 0 件へ退行したら（＝検査が静かに失効したら）ここで落ちる。
  {
    const classes = collectTestClasses();
    const spec = collectSpecText();
    t('実ファイル: src/ からテストクラスを 1 件以上・docs/tests/ から md を 1 件以上拾える',
      classes.size > 0 && spec.files.length > 0, { classes: classes.size, specs: spec.files.length });
  }

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[check-test-spec-coverage] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-test-spec-coverage] 自己試験 ${cases.length} 件 OK。`);
}

// --- 実行 -----------------------------------------------------------------------

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }

  const classes = collectTestClasses();
  const spec = collectSpecText();

  // fail-closed: 「見つからないから素通り」を作らない。
  if (classes.size === 0) {
    console.error('[check-test-spec-coverage] src/ からテストクラス（*Tests.cs）を 1 件も見つけられませんでした。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    process.exit(1);
    return;
  }
  if (spec.files.length === 0) {
    console.error(`[check-test-spec-coverage] ${SPEC_DIR}/ に Markdown が 1 件もありません。`);
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    process.exit(1);
    return;
  }

  const documented = documentedNames(classes.keys(), spec.text);

  if (process.argv.includes('--update')) {
    writeBaseline(documented);
    console.log(`[check-test-spec-coverage] ${BASELINE_FILE} を更新しました（documented ${documented.size} 件）。` +
      '\n  差分を PR に載せ、増減の理由をレビューで見えるようにしてください。');
    process.exit(0);
    return;
  }

  let baseline;
  try {
    baseline = readBaseline();
  } catch (e) {
    console.error(`[check-test-spec-coverage] 床を取得できませんでした: ${e.message}`);
    console.error('  床が無いと本検査は 0 件検査に戻るため fail させています。' +
      '\n  node scripts/check-test-spec-coverage.js --update で作り直せます。');
    process.exit(1);
    return;
  }

  const r = classify({ existing: classes.keys(), documented, baseline });
  const pathsOf = (n) => (classes.get(n) || []).join(' / ');

  const summary = process.env.GITHUB_STEP_SUMMARY;
  if (summary) {
    const lines = [
      '### 実在するテスト → テスト仕様書の記載（#510）',
      '',
      `- 走査したテストクラス: **${classes.size}**（\`src/**/*Tests.cs\`。対象外ユニット: ${[...EXCLUDED_UNITS].join(' / ') || 'なし'}）`,
      `- \`${SPEC_DIR}/\` の仕様書: **${spec.files.length}**`,
      `- 仕様書から参照済み: **${documented.size}**（床は \`${BASELINE_FILE}\` の **${baseline.length}**）`,
      `- 記載が消えた（**fail**）: **${r.regressed.length}**${r.regressed.length ? ` — ${r.regressed.join(' / ')}` : ''}`,
      `- 未記載（warn）: **${r.undocumented.length}**${r.undocumented.length ? ` — ${r.undocumented.join(' / ')}` : ''}`,
      '',
      '規約は `docs/tests/TEST_STRATEGY.md`、設計は `docs/adr/IADR-0130_test-spec-coverage-ratchet.md`。',
    ];
    try { fs.appendFileSync(summary, lines.join('\n') + '\n'); } catch { /* サマリ不可でも検査は続ける */ }
  }

  if (r.undocumented.length) {
    // 全件は実行サマリへ出す。注釈へ 40 件超を並べると PR で読まれなくなり、警告そのものが無視される。
    const head = r.undocumented.slice(0, 10);
    const rest = r.undocumented.length - head.length;
    warn(`テスト仕様書に載っていないテストクラス ${r.undocumented.length} 件（warn。基盤・回帰テストは` +
      `記載義務を負わない）: ${head.join(' / ')}${rest > 0 ? ` ほか ${rest} 件（全件は実行サマリ）` : ''}。` +
      `受け入れ基準に紐づくものは ${SPEC_DIR}/<ID>_*.md へ足し、` +
      'node scripts/check-test-spec-coverage.js --update で床を上げること。');
  }

  const failures = [];
  if (r.regressed.length) {
    failures.push('[記載の消失] ' + r.regressed.map((n) => `${n}（${pathsOf(n)}）`).join('\n    ') +
      `\n    テストは実在するのに ${SPEC_DIR}/ のどこからも参照されなくなりました。` +
      '\n    仕様書を「全面改訂」した際に節ごと落ちた可能性が高い（#510 の再発）。該当する節を書き戻してください。' +
      '\n    意図的に記載をやめるなら --update で床を下げ、その理由を PR に書いてください。');
  }
  if (r.removedTest.length) {
    failures.push(`[床の減らし忘れ] ${r.removedTest.join(' / ')}` +
      `\n    床にありますが、テストクラス自体が src/ に存在しません（削除・改名）。` +
      `\n    ${SPEC_DIR}/ の該当記載を直したうえで --update で床を更新してください。`);
  }
  if (r.newlyDocumented.length) {
    failures.push(`[床の上げ忘れ] ${r.newlyDocumented.join(' / ')}` +
      '\n    仕様書に記載されましたが床に入っていません。--update で床を上げてください' +
      '（上げておかないと、次に節が落ちても検出できません）。');
  }

  if (failures.length === 0) {
    console.log(`[check-test-spec-coverage] OK: テストクラス ${classes.size} 件中 ${documented.size} 件が ` +
      `${SPEC_DIR}/ の仕様書 ${spec.files.length} 件から参照済み（床と一致）。` +
      `\n  未記載 ${r.undocumented.length} 件は warn（基盤・回帰テストに記載義務は負わせない）。`);
    process.exit(0);
    return;
  }
  console.error(`[check-test-spec-coverage] 違反 ${failures.length} 件を検出しました:`);
  for (const f of failures) console.error(`\n  ${f}`);
  console.error('\n設計と限界は docs/adr/IADR-0130_test-spec-coverage-ratchet.md を参照してください。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  SPEC_DIR,
  SRC_DIR,
  BASELINE_FILE,
  TEST_CLASS_FILE,
  EXCLUDED_UNITS,
  testClassNameOf,
  mentionsClass,
  documentedNames,
  classify,
  collectTestClasses,
  collectSpecText,
  readBaseline,
  writeBaseline,
};
