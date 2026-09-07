#!/usr/bin/env node
'use strict';
/*
 * check-test-name-references.js
 * NFR / issue #1312: コード注記が **実在しない試験クラス名**を指していないか検査する。
 *
 * 背景（同型の事故が繰り返し起きている）:
 *   「この帰結は <X>Tests が固定する」と書いてあるのに、その名前のクラスが 1 つも無い。
 *   #1311 で 1 件（RenameEdgeTypeOrderTests）、#1312 の起票時に 5 件、
 *   着手時に引き直すと **7 件**（うち 1 件は下記の allowlist）だった。
 *
 * なぜ検査するか:
 *   🔴 **この形は「試験がある」と読ませたまま、実際には何も固定していない。**
 *   指し先が実在しない場合はさらに悪い —— 読み手は名前で検索して見つからず、
 *   「名前が変わったのだろう」と推測して先へ進む。**主張は検証されないまま残る。**
 *
 * 🔴 検査するのは「名前が実在するか」だけである（#1312 決定）:
 *   **「指し先は実在するが、その帰結を固定していない」側は機械で判定できない。**
 *   主張の中身は人が読むしかない。**射程をここで明示し、広げない** ——
 *   広げようとすると誤検出だらけになり、検査器ごと無視されるようになる。
 *
 * 走査:
 *   git ls-files で引いた src 配下の C#（submodule は git ls-files に出ないので自然に対象外）の
 *   **コメント部分**（スラッシュ 2 つ以降）から *Tests を集め、同じ母集合の型宣言と突き合わせる。
 *   文字列リテラル内は拾わない（コメントだけを見る）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-test-name-references.js
 *   node scripts/check-test-name-references.js --self-test
 */
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

// 走査母集合を git ls-files から引くので、**未追跡ファイルは対象外**である。
// 作業ツリーに未コミットの変更があると CI の結果と一致し得ないため、その旨を警告する
// （#683 / IADR-0183 のクラス B）。lib が無くても本体は動かす（fail-open）。
let MODE = {};
let warnIfResultMayDifferFromCi = () => {};
let worktreeStateModule = null;
try {
  worktreeStateModule = require.resolve('./lib/worktree-state.js');
} catch (e) {
  if (!e || e.code !== 'MODULE_NOT_FOUND') throw e;
}
if (worktreeStateModule) {
  ({ MODE, warnIfResultMayDifferFromCi } = require(worktreeStateModule));
}

const REPO_ROOT = path.resolve(__dirname, '..');

/**
 * 指し先の実在を求めない名前。**理由を必ず書く。**
 *
 * 🔴 **「直せないから除外」ではない。** ここに在るのは、
 * **その名前が「実在しない」と述べている当の記述**か、**型名ではない語**である。
 */
const ALLOWED = new Map([
  ['IntegrationTests', 'アセンブリ名（Knowledge.IntegrationTests）であって型名ではない'],
  ['EndpointTests', 'グロブ表記（末尾一致で EndpointTests を指す）であって特定の型ではない'],
  // 🔴 以下 2 件は同じ類型である —— **「その名前の試験は存在しない」と述べている当の記述**。
  //   直すと記録が壊れる。**この類型を足すときは、指し先を作らないことが確定していること**
  //   （名前を消して主張だけ残す形になっていないこと）を確かめてから足す。
  //   実測（#1312）: 是正の記録を書いた行そのものが走査に出る。**記録が母集合を動かす**
  //   （母集合の規則 8）。走査結果は「7 名 → 是正 6 名 → 記録が 1 名を戻す」である。
  ['AiSuggestionWiringTests',
    '［2026-08-28 追記 / #438］の 2 箇所が「そのテストは一度も存在しない」と'
    + '述べている当の記述である。直すと記録を壊す（#1312 で走査の誤検出として除外）'],
  ['PrivateNoteEndpointsMappingTests',
    '#1312 の是正の記録である。この名前の試験は作らず、縮退は'
    + ' PrivateNoteMapperTests の末尾の対（ToDto_WhenTheDocumentIsNotYetReplicated_...）が固定する。'
    + ' 名前を消すと「何を直したか」が追えなくなるので残す'],
]);

/** 0 件走査を緑にしない（IADR-0130）。実測: 追跡下の src 配下 C# は 1000 件超。 */
const MIN_SCANNED = 300;

function trackedCsFiles(root = REPO_ROOT) {
  return execFileSync('git', ['-C', root, 'ls-files', '--', '*.cs'],
    { encoding: 'utf8', maxBuffer: 1 << 28 })
    .split('\n').map((s) => s.trim()).filter(Boolean)
    .filter((f) => f.startsWith('src/'));
}

/** 1 行のコメント部分だけを返す（スラッシュ 2 つ以降。無ければ null）。 */
function commentOf(line) {
  const i = line.indexOf('//');
  return i < 0 ? null : line.slice(i);
}

const NAME_RE = /\b([A-Za-z0-9_]*[A-Za-z0-9_]Tests)\b/g;
const DECL_RE = /\b(?:class|record|struct|interface)\s+([A-Za-z0-9_]*Tests)\b/g;

/** 1 ファイルから「宣言された試験型」と「コメントが指す試験名」を集める。 */
function collect(text, rel, declared, referenced) {
  text.split('\n').forEach((line, i) => {
    for (const m of line.matchAll(DECL_RE)) declared.add(m[1]);
    const c = commentOf(line);
    if (c === null) return;
    for (const m of c.matchAll(NAME_RE)) {
      if (!referenced.has(m[1])) referenced.set(m[1], []);
      referenced.get(m[1]).push(`${rel}:${i + 1}`);
    }
  });
}

function scan(root = REPO_ROOT, files = null) {
  const list = files || trackedCsFiles(root);
  const declared = new Set();
  const referenced = new Map();
  for (const rel of list) {
    let text;
    try {
      text = fs.readFileSync(path.join(root, rel), 'utf8');
    } catch {
      continue;
    }
    collect(text, rel, declared, referenced);
  }
  const violations = [];
  for (const [name, sites] of referenced) {
    if (declared.has(name) || ALLOWED.has(name)) continue;
    violations.push({ name, sites });
  }
  violations.sort((a, b) => a.name.localeCompare(b.name));
  return { violations, scanned: list.length, declared: declared.size, referenced: referenced.size };
}

function isScanTooSmall(scanned, min = MIN_SCANNED) {
  return scanned < min;
}

// --- 自己試験 -------------------------------------------------------------------

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });
  const os = require('os');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'testname-selftest-'));
  const write = (rel, s) => {
    const p = path.join(dir, rel);
    fs.mkdirSync(path.dirname(p), { recursive: true });
    fs.writeFileSync(p, s, 'utf8');
    return rel;
  };

  const good = write('a/Good.cs', '// FR-01: この帰結は RealTests が固定する。\npublic class Good { }\n');
  write('a/RealTests.cs', 'public class RealTests { }\n');
  const bad = write('a/Bad.cs', '// FR-02: この帰結は GhostTests が固定する。\npublic class Bad { }\n');

  t('commentOf: コメントが無い行は null', commentOf('var x = 1;') === null);
  t('commentOf: コメント以降だけを返す', commentOf('var x = 1; // FooTests') === '// FooTests');
  t('commentOf: 行頭コメントも取れる', commentOf('  // BarTests') === '// BarTests');

  {
    const r = scan(dir, [good, 'a/RealTests.cs', bad]);
    t('scan: 実在しない指し先を 1 件検出する', r.violations.length === 1, r.violations);
    t('scan: 検出したのは GhostTests', r.violations[0] && r.violations[0].name === 'GhostTests', r.violations);
    t('scan: 位置を返す', r.violations[0] && r.violations[0].sites[0] === `${bad}:1`, r.violations);
    t('scan: 実在する指し先は違反にしない（陰性対照）',
      !r.violations.some((v) => v.name === 'RealTests'), r.violations);
  }
  {
    // 🔴 陰性対照: 文字列リテラルの中の名前は拾わない（コメントだけを見る）。
    const lit = write('a/Literal.cs', 'var s = "GhostInStringTests";\n');
    const r = scan(dir, [lit]);
    t('scan: 文字列リテラル内の名前は拾わない', r.violations.length === 0, r.violations);
  }
  {
    // 🔴 陰性対照: allowlist の名前は違反にしない。
    const al = write('a/Allowed.cs', '// これは AiSuggestionWiringTests について述べている。\n');
    const r = scan(dir, [al]);
    t('scan: allowlist の名前は違反にしない', r.violations.length === 0, r.violations);
  }
  {
    // 宣言の種別（class 以外）も拾う。
    const rec = write('a/RecTests.cs', 'public sealed record RecTests { }\n');
    const ref2 = write('a/UsesRec.cs', '// RecTests が固定する。\n');
    const r = scan(dir, [rec, ref2]);
    t('scan: record 宣言も「実在する」と数える', r.violations.length === 0, r.violations);
  }

  t('isScanTooSmall: 下限未満は真', isScanTooSmall(1, 300) === true);
  t('isScanTooSmall: 下限以上は偽', isScanTooSmall(300, 300) === false);
  t('ALLOWED: すべての除外に理由が書いてある',
    [...ALLOWED.values()].every((v) => typeof v === 'string' && v.length > 10), [...ALLOWED]);

  fs.rmSync(dir, { recursive: true, force: true });

  const failed = cases.filter((c) => !c.pass);
  for (const c of failed) {
    console.error(`  NG  ${c.name}` + (c.actual === undefined ? '' : ` :: ${JSON.stringify(c.actual)}`));
  }
  if (failed.length) {
    console.error(`[check-test-name-references] 自己試験 ${failed.length}/${cases.length} 件が失敗しました。`);
    process.exit(1);
  }
  console.log(`[check-test-name-references] 自己試験 ${cases.length} 件 OK。`);
}

// --- 実行 -----------------------------------------------------------------------

function main() {
  if (process.argv.includes('--self-test')) {
    selfTest();
    return;
  }

  warnIfResultMayDifferFromCi('check-test-name-references.js', MODE.TRACKED);

  let r;
  try {
    r = scan();
  } catch (e) {
    console.error(`[check-test-name-references] 追跡下のファイル一覧を取得できませんでした: ${e.message}`);
    process.exit(1);
  }

  console.log(`[check-test-name-references] src の C# ${r.scanned} 件を走査`
    + `（注記が指す試験名 ${r.referenced} 名 / 宣言 ${r.declared} 名`
    + ` / 除外 ${[...ALLOWED.keys()].join(', ')}）。`);

  if (isScanTooSmall(r.scanned)) {
    console.error(`[check-test-name-references] 走査件数が ${r.scanned} 件しかありません（下限 ${MIN_SCANNED}）。`
      + ' 走査が空振りしているか、実行位置がリポジトリ外です。');
    process.exit(1);
  }

  if (r.violations.length === 0) {
    console.log('[check-test-name-references] OK: 注記が指す試験名はすべて実在します。');
    return;
  }

  console.error(`[check-test-name-references] ${r.violations.length} 名の指し先が実在しません:`);
  for (const v of r.violations) {
    console.error(`  - ${v.name}`);
    for (const s of v.sites) console.error(`      ${s}`);
  }
  console.error('  指し先を実在する試験名へ直すか、試験を足してください。'
    + ' 主張を取り下げるなら、その理由を注記に書いてください（#1312）。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = { commentOf, collect, scan, isScanTooSmall, ALLOWED, MIN_SCANNED };
