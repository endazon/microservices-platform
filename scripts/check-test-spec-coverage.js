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
 * 粒度（**仕様書ファイル × テストクラス（`*Tests.cs` のファイル名）の対**）:
 *   - **アセンブリ（テストプロジェクト）単位では止まらない。** `Platform.Bff.Tests` は複数の仕様書から
 *     参照され続けるため、SC-05 の節が消えても「どこにも無い」にならない。
 *   - **メソッド単位は細かすぎる。** 表の 1 行を消すだけで赤くなり、仕様書の正当な要約を禁じてしまう。
 *   - **クラス名だけを見る形でも足りない**（本 issue の変異試験 M2 で実測）。`DocumentVersioningTests` は
 *     SC-05 と FR-06 の両方が参照しているため、SC-05 の節を丸ごと消しても FR-06 が残る限り緑になった。
 *     **落ちるのは節であり、節は仕様書ファイルに属する。** よって対で固定する。
 *
 * 判定（ratchet。既存ゲートと同じ 3 判定 + warn。前 3 つは**対**、最後はクラスが単位）:
 *   - baseline の対が消えた（テストは実在）→ **fail**（＝今回の欠陥。節の消失）
 *   - baseline の対のクラスが実在しない    → **fail**（baseline を減らし、仕様書の記載も見直す）
 *   - 記載された対が baseline に無い        → **fail**（床を上げっぱなしにする。--update で更新）
 *   - どの仕様書にも載らず baseline にも無いクラス → **warn**（基盤・回帰テストに記載義務は負わせない）
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
 * 記載の単位は「**仕様書ファイル × テストクラス**」の対である。キーは `<spec>::<class>`。
 *
 * **クラス名だけを見る設計では足りない**（本 issue の変異試験で実測した）。`DocumentVersioningTests`
 * は SC-05 と FR-06 の両方が参照しているため、**SC-05 から §状態遷移ガード 節を丸ごと消しても**
 * FR-06 の記載が残る限り「どこかには載っている」で緑になってしまう。落ちるのは**節**であり、
 * 節は仕様書ファイルに属する。よって対で固定する。
 */
function pairKey(specFile, className) {
  return `${specFile}::${className}`;
}

/** 対をファイル名とクラス名へ戻す（表示用）。 */
function splitPair(key) {
  const i = String(key).lastIndexOf('::');
  return { spec: String(key).slice(0, i), name: String(key).slice(i + 2) };
}

/**
 * 実在するクラス名が、どの仕様書ファイルから参照されているかを対の Set として返す。
 * `names` は Iterable<string>、`docs` は [{ file, text }]。
 */
function documentedPairs(names, docs) {
  const out = new Set();
  const list = [...names];
  for (const d of docs) {
    for (const n of list) if (mentionsClass(d.text, n)) out.add(pairKey(d.file, n));
  }
  return out;
}

/** 対の集合から「どこかには載っているクラス」の集合を作る。 */
function namesInPairs(pairs) {
  const out = new Set();
  for (const k of pairs) out.add(splitPair(k).name);
  return out;
}

/**
 * 走査で見つかった**実ファイル数**（`collectTestClasses` の Map<name, paths[]> の値の総和）。
 *
 * 本検査が数える「テストクラス」は**ファイル名をキーに同名を 1 件へ畳んだ後**の数である
 * （`HealthEndpointTests` は 10 プロジェクトに在る）。成功メッセージにクラス名の数だけを出すと、
 * レビュアが「実ファイルを全部見た」と誤読しうる。畳む前の数を併記して母集合を明示する
 * （限界そのものは IADR-0130 §限界 1）。**数は実行時に数える**——ハードコードすると、
 * テストが増減したときに黙って嘘になる。
 */
function countFiles(classes) {
  let n = 0;
  for (const paths of classes.values()) n += paths.length;
  return n;
}

/**
 * ratchet の 4 分類。前 3 つは**対**、最後の 1 つは**クラス**を単位にする。
 *   regressed      : baseline の対が消えた（テストは実在）→ fail（節の消失。**本 issue の欠陥**）
 *   removedTest    : baseline の対のクラスが実在しない    → fail（床の減らし忘れ）
 *   newlyDocumented: 記載された対が baseline に無い        → fail（床の上げ忘れ）
 *   undocumented   : 実在するがどの仕様書にも載らず床にも無いクラス → warn
 */
function classify({ existing, documented, baseline }) {
  const ex = new Set(existing);
  const doc = new Set(documented);
  const base = new Set(baseline);
  const sorted = (s) => [...s].sort();
  const basedNames = namesInPairs(base);
  const docNames = namesInPairs(doc);
  return {
    regressed: sorted(new Set([...base].filter((k) => ex.has(splitPair(k).name) && !doc.has(k)))),
    removedTest: sorted(new Set([...base].filter((k) => !ex.has(splitPair(k).name)))),
    newlyDocumented: sorted(new Set([...doc].filter((k) => !base.has(k)))),
    undocumented: sorted(new Set([...ex].filter((n) => !docNames.has(n) && !basedNames.has(n)))),
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

/** docs/tests/ の Markdown を読み、[{ file, text }] を返す。 */
function collectSpecDocs(root = REPO_ROOT) {
  return walk(SPEC_DIR, (p) => /\.md$/i.test(p), [], root)
    .map((f) => ({ file: f, text: fs.readFileSync(path.join(root, f), 'utf8') }));
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
  const d = parsed && parsed.documented;
  if (!d || typeof d !== 'object' || Array.isArray(d)) {
    throw new Error(`${BASELINE_FILE} に「仕様書ファイル → クラス名の配列」のオブジェクト documented がありません`);
  }
  const pairs = [];
  for (const [spec, names] of Object.entries(d)) {
    if (!Array.isArray(names)) throw new Error(`${BASELINE_FILE} の documented["${spec}"] が配列ではありません`);
    for (const n of names) pairs.push(pairKey(spec, String(n)));
  }
  return pairs;
}

function writeBaseline(pairs, file = path.join(REPO_ROOT, BASELINE_FILE)) {
  const bySpec = {};
  for (const k of [...pairs].sort()) {
    const { spec, name } = splitPair(k);
    (bySpec[spec] = bySpec[spec] || []).push(name);
  }
  const body = {
    $comment: [
      'check-test-spec-coverage.js の床（NFR / issue #510 / IADR-0130）。',
      'キーは docs/tests/ のテスト仕様書、値はその仕様書が参照しているバックエンドテストクラスである。',
      '**クラス名だけでなく「どの仕様書が」まで固定する** —— 同じクラスを複数の仕様書が参照している場合',
      '（DocumentVersioningTests は SC-05 と FR-06 の両方）、クラス名だけを見る床では片方の節を丸ごと',
      '消しても検出できない（#510 の変異試験で実測）。',
      'ここにある対が失われると CI が fail する。記載を増やしたら',
      'node scripts/check-test-spec-coverage.js --update で更新し、差分を PR に載せること。',
      'テストクラスや仕様書を削除・改名したときは、記載を直したうえで同じコマンドで更新する。',
    ].join(' '),
    documented: bySpec,
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

  t('pairKey / splitPair: 仕様書ファイル × クラス名の対を往復できる',
    splitPair(pairKey('docs/tests/SC-05_x.md', 'ATests')).spec === 'docs/tests/SC-05_x.md'
    && splitPair(pairKey('docs/tests/SC-05_x.md', 'ATests')).name === 'ATests');

  t('documentedPairs: 本文に現れる対だけを返す',
    [...documentedPairs(['ATests', 'BTests'], [{ file: 'S1.md', text: '`BTests` のみ' }])].join(',')
      === 'S1.md::BTests');
  t('documentedPairs: 同じクラスを 2 つの仕様書が参照したら対は 2 つ',
    documentedPairs(['ATests'], [{ file: 'S1.md', text: '`ATests`' }, { file: 'S2.md', text: '`ATests`' }]).size === 2);

  // --- ratchet の 4 判定（前 3 つは対、undocumented はクラス単位） -----------------
  const P = pairKey;
  {
    // 今回の欠陥そのもの: テストは実在するのに、ある仕様書から節ごと消えた。
    const r = classify({
      existing: ['XTests', 'YTests'],
      documented: [P('S1.md', 'YTests')],
      baseline: [P('S1.md', 'XTests'), P('S1.md', 'YTests')],
    });
    t('classify: 節の消失は regressed（fail 対象）',
      r.regressed.join(',') === 'S1.md::XTests' && r.removedTest.length === 0
        && r.newlyDocumented.length === 0 && r.undocumented.length === 0, r);
  }
  {
    // **クラス単位の床では取り逃す形**（本 issue の変異試験 M2 が実測した穴）。
    // XTests は S2.md にも載っているため「どこかには載っている」が、S1.md の節は消えている。
    const r = classify({
      existing: ['XTests'],
      documented: [P('S2.md', 'XTests')],
      baseline: [P('S1.md', 'XTests'), P('S2.md', 'XTests')],
    });
    t('classify: 他の仕様書が同じクラスを参照していても、消えた側の節を regressed にする',
      r.regressed.join(',') === 'S1.md::XTests' && r.undocumented.length === 0, r);
  }
  {
    const r = classify({
      existing: ['YTests'],
      documented: [P('S1.md', 'YTests')],
      baseline: [P('S1.md', 'XTests'), P('S1.md', 'YTests')],
    });
    t('classify: テストごと消えたら removedTest（fail 対象・床の減らし忘れ）',
      r.removedTest.join(',') === 'S1.md::XTests' && r.regressed.length === 0, r);
  }
  {
    const r = classify({
      existing: ['XTests', 'ZTests'],
      documented: [P('S1.md', 'XTests'), P('S1.md', 'ZTests')],
      baseline: [P('S1.md', 'XTests')],
    });
    t('classify: 記載を増やしたら newlyDocumented（fail 対象・床の上げ忘れ）',
      r.newlyDocumented.join(',') === 'S1.md::ZTests' && r.regressed.length === 0, r);
  }
  {
    const r = classify({
      existing: ['XTests', 'ZTests'],
      documented: [P('S1.md', 'XTests')],
      baseline: [P('S1.md', 'XTests')],
    });
    t('classify: どの仕様書にも載らない新規テストは undocumented（warn どまり）',
      r.undocumented.join(',') === 'ZTests' && r.regressed.length === 0
        && r.newlyDocumented.length === 0 && r.removedTest.length === 0, r);
  }
  {
    const r = classify({
      existing: ['XTests'], documented: [P('S1.md', 'XTests')], baseline: [P('S1.md', 'XTests')],
    });
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
  t('readBaseline: 負例 — documented がオブジェクトでなければ例外',
    (() => { try { readBaseline(bad); return false; } catch { return true; } })());
  fs.writeFileSync(bad, '{"documented": ["ATests"]}');
  t('readBaseline: 負例 — 旧形式（クラス名の配列）は例外（形式の取り違えを黙って通さない）',
    (() => { try { readBaseline(bad); return false; } catch { return true; } })());
  fs.writeFileSync(bad, '{"documented": {"docs/tests/S1.md": "ATests"}}');
  t('readBaseline: 負例 — 値が配列でなければ例外',
    (() => { try { readBaseline(bad); return false; } catch { return true; } })());
  fs.writeFileSync(bad, '{"documented": {"docs/tests/S1.md": ["ATests"]}}');
  t('readBaseline: 正例 — 対の配列を返す',
    readBaseline(bad).join(',') === 'docs/tests/S1.md::ATests');
  {
    // 往復（write → read）で対が保たれること。床の書き戻しが壊れると検査が静かに空になる。
    const rt = path.join(tmp, 'roundtrip.json');
    writeBaseline([pairKey('docs/tests/S2.md', 'BTests'), pairKey('docs/tests/S1.md', 'ATests')], rt);
    t('writeBaseline → readBaseline: 対が保たれ、順序が安定する',
      readBaseline(rt).sort().join(',') === 'docs/tests/S1.md::ATests,docs/tests/S2.md::BTests');
  }

  // --- 走査の fail-closed（実ファイル木のフィクスチャ） --------------------------
  const fake = fs.mkdtempSync(path.join(os.tmpdir(), 'tsc-tree-'));
  fs.mkdirSync(path.join(fake, 'src', 'unit', 'backend'), { recursive: true });
  fs.mkdirSync(path.join(fake, 'docs', 'tests'), { recursive: true });
  fs.writeFileSync(path.join(fake, 'src', 'unit', 'backend', 'AlphaTests.cs'), '// x');
  fs.writeFileSync(path.join(fake, 'src', 'unit', 'backend', 'BetaTests.cs'), '// x');
  fs.writeFileSync(path.join(fake, 'src', 'unit', 'backend', 'Program.cs'), '// x');
  fs.writeFileSync(path.join(fake, 'docs', 'tests', 'SC-99_x.md'), 'テスト: `AlphaTests`');
  fs.writeFileSync(path.join(fake, 'docs', 'tests', 'FR-99_y.md'), '別の仕様書も `AlphaTests` を挙げる');
  {
    const classes = collectTestClasses(fake);
    const docs = collectSpecDocs(fake);
    const base = [pairKey('docs/tests/SC-99_x.md', 'AlphaTests'), pairKey('docs/tests/FR-99_y.md', 'AlphaTests')];
    t('collectTestClasses: フィクスチャから 2 クラスを拾い Program.cs は拾わない',
      classes.size === 2 && classes.has('AlphaTests') && classes.has('BetaTests'), [...classes.keys()]);
    t('collectSpecDocs: docs/tests/ の md を 2 件読む', docs.length === 2, docs.map((d) => d.file));
    t('countFiles: 同名を畳む前の実ファイル数を返す（成功メッセージの母集合）',
      countFiles(classes) === 2 && countFiles(new Map([['ATests', ['a/ATests.cs', 'b/ATests.cs']]])) === 2,
      countFiles(classes));
    const r = classify({ existing: classes.keys(), documented: documentedPairs(classes.keys(), docs), baseline: base });
    t('走査 → 判定の通し: BetaTests は undocumented（warn）で赤くならない',
      r.undocumented.join(',') === 'BetaTests' && r.regressed.length === 0, r);
    // 変異: **片方の**仕様書から記載を消すと regressed になる（もう片方に残っていても）。
    fs.writeFileSync(path.join(fake, 'docs', 'tests', 'SC-99_x.md'), 'フロントの表しかない');
    const r2 = classify({ existing: classes.keys(), documented: documentedPairs(classes.keys(), collectSpecDocs(fake)), baseline: base });
    t('変異試験（自己試験内）: 片方の仕様書から記載を消すと regressed が立つ',
      r2.regressed.join(',') === 'docs/tests/SC-99_x.md::AlphaTests', r2);
  }
  {
    const empty = fs.mkdtempSync(path.join(os.tmpdir(), 'tsc-empty-'));
    t('fail-closed の入口: 走査対象 0 件を 0 件として返す（main が fail へ変換する）',
      collectTestClasses(empty).size === 0 && collectSpecDocs(empty).length === 0);
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
    const docs = collectSpecDocs();
    t('実ファイル: src/ からテストクラスを 1 件以上・docs/tests/ から md を 1 件以上拾える',
      classes.size > 0 && docs.length > 0, { classes: classes.size, specs: docs.length });
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
  const docs = collectSpecDocs();

  // fail-closed: 「見つからないから素通り」を作らない。
  if (classes.size === 0) {
    console.error('[check-test-spec-coverage] src/ からテストクラス（*Tests.cs）を 1 件も見つけられませんでした。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    process.exit(1);
    return;
  }
  if (docs.length === 0) {
    console.error(`[check-test-spec-coverage] ${SPEC_DIR}/ に Markdown が 1 件もありません。`);
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    process.exit(1);
    return;
  }

  const documented = documentedPairs(classes.keys(), docs);

  if (process.argv.includes('--update')) {
    writeBaseline(documented);
    console.log(`[check-test-spec-coverage] ${BASELINE_FILE} を更新しました（仕様書 × クラスの対 ${documented.size} 件）。` +
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
  const describePair = (k) => {
    const { spec, name } = splitPair(k);
    return `${spec} が挙げていた ${name}（${pathsOf(name) || 'テスト本体は不在'}）`;
  };
  const documentedNamesNow = namesInPairs(documented);

  const summary = process.env.GITHUB_STEP_SUMMARY;
  if (summary) {
    const lines = [
      '### 実在するテスト → テスト仕様書の記載（#510）',
      '',
      `- 走査したテストクラス: **${classes.size}**（\`src/**/*Tests.cs\`。**同名は 1 件として集約。実ファイル ${countFiles(classes)} 件**。` +
        `対象外ユニット: ${[...EXCLUDED_UNITS].join(' / ') || 'なし'}）`,
      `- \`${SPEC_DIR}/\` の仕様書: **${docs.length}**`,
      `- 記載の対（仕様書 × クラス）: **${documented.size}**（床は \`${BASELINE_FILE}\` の **${baseline.length}**）`,
      `- どこかの仕様書に載っているクラス: **${documentedNamesNow.size}**`,
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
    failures.push('[記載の消失] ' + r.regressed.map(describePair).join('\n    ') +
      '\n    テストは実在するのに、当該の仕様書がこのクラスを参照しなくなりました。' +
      '\n    仕様書を「全面改訂」した際に節ごと落ちた可能性が高い（#510 の再発）。該当する節を書き戻してください。' +
      '\n    **他の仕様書に同じクラスの記載が残っていても本検査は落ちる** —— 落ちるのは節であり、節は仕様書に属するためです。' +
      '\n    意図的に記載をやめるなら --update で床を下げ、その理由を PR に書いてください。');
  }
  if (r.removedTest.length) {
    failures.push(`[床の減らし忘れ] ${r.removedTest.map(describePair).join('\n    ')}` +
      `\n    床にありますが、テストクラス自体が src/ に存在しません（削除・改名）。` +
      `\n    ${SPEC_DIR}/ の該当記載を直したうえで --update で床を更新してください。`);
  }
  if (r.newlyDocumented.length) {
    failures.push(`[床の上げ忘れ] ${r.newlyDocumented.join('\n    ')}` +
      '\n    仕様書に記載されましたが床に入っていません。--update で床を上げてください' +
      '（上げておかないと、次に節が落ちても検出できません）。');
  }

  if (failures.length === 0) {
    console.log(`[check-test-spec-coverage] OK: テストクラス ${classes.size} 件` +
      `（同名は 1 件として集約。実ファイル ${countFiles(classes)} 件）のうち ${documentedNamesNow.size} 件が ` +
      `${SPEC_DIR}/ の仕様書 ${docs.length} 件から参照済み（記載の対 ${documented.size} 件が床と一致）。` +
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
  pairKey,
  splitPair,
  documentedPairs,
  namesInPairs,
  countFiles,
  classify,
  collectTestClasses,
  collectSpecDocs,
  readBaseline,
  writeBaseline,
};
