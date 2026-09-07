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
 *   git ls-files で引いた src 配下の C#（submodule は git ls-files に出ないので自然に対象外）を
 *   **コード部分とコメント部分へ切り分け**、**参照はコメントからだけ / 宣言はコードからだけ**集めて
 *   突き合わせる。
 *
 *   🔴 **切り分けは 1 か所（`scanSegments`）に持ち、両側へ効かせる。**
 *   PR #1330 のレビューが 3 巡かけて見つけた穴は、すべて**境界を決める規則が場所ごとに違う**ことに
 *   由来していた:
 *     1 巡目 **ブロックコメント内の参照**を拾わない（同じ主張をブロックで書けば逃れられる）／
 *            リテラル内の URL をコメント開始と誤る
 *     2 巡目 **コメントアウトされた宣言**を「実在する」と数える
 *            （`// public class GhostTests { }` で、実在しない指し先が黙って通る）
 *     3 巡目 **C# の文字列 3 種（通常 / verbatim / raw）の終端規則を区別していない**
 *   🔴 **文字列リテラルの中は拾わない** —— 拾うと試験名を配列で持つ実装コードが軒並み誤検出になる。
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

const NAME_RE = /\b([A-Za-z0-9_]*[A-Za-z0-9_]Tests)\b/g;
const DECL_RE = /\b(?:class|record|struct|interface)\s+([A-Za-z0-9_]*Tests)\b/g;

/**
 * ファイル全体を「コード」「コメント」の区間へ切り分ける。**走査器はこれ 1 つである。**
 *
 * 🔴 **なぜ 1 つにするか。** PR #1330 のレビューが 3 巡かけて見つけた穴は、すべて
 * **「境界を決める規則が場所ごとに違う」**ことに由来していた ——
 *   1 巡目: 参照側がブロックコメントを見ていない（偽陰性）／リテラル内の URL をコメント開始と誤る（偽陽性）
 *   2 巡目: **宣言側だけ生の行**を見ており、コメントアウトされた宣言を「実在する」と数える（偽陰性）
 *   3 巡目: 境界を決める当の関数が **C# の文字列 3 種の終端規則を区別していない**（偽陰性・偽陽性）
 * **片側だけ直せる形をやめる。** 区間の切り分けをここ 1 か所に持ち、
 * 参照（コメント）も宣言（コード）も**同じ切り分けの結果**から取る。
 *
 * 🔴 **C# の文字列は 3 種あり、終端規則が違う**（3 巡目の指摘。実測: 本リポジトリの src に
 * verbatim 72 / raw 330 出現するので、これは机上の話ではない）:
 *   - 通常  `"…"`      … バックスラッシュがエスケープ。行をまたがない
 *   - verbatim `@"…"`  … **バックスラッシュはただの文字**。`""` だけが引用符のエスケープ。**行をまたぐ**
 *   - raw   `"""…"""`  … 開き引用符と同数以上の連続引用符で閉じる。**行をまたぐ**
 * 区別しないと、**閉じ位置を読み違えて後続の `//` を丸ごと見落とす**（偽陰性）か、
 * **文字列の中身をコメントとして拾う**（偽陽性）。
 *
 * 文字リテラル `'x'` も通常文字列と同じ規則で読み飛ばす（`'"'` で誤らないため）。
 */
function scanSegments(text) {
  const segments = [];
  let line = 1;
  let i = 0;
  const n = text.length;
  const push = (kind, from, to, at) => {
    if (to > from) segments.push({ kind, text: text.slice(from, to), line: at });
  };

  let codeFrom = 0;
  while (i < n) {
    const c = text[i];

    if (c === '\n') { line += 1; i += 1; continue; }

    // --- コメント ---
    if (c === '/' && text[i + 1] === '/') {
      push('code', codeFrom, i, line);
      const end = text.indexOf('\n', i);
      const stop = end < 0 ? n : end;
      push('comment', i, stop, line);
      i = stop;
      codeFrom = i;
      continue;
    }
    if (c === '/' && text[i + 1] === '*') {
      push('code', codeFrom, i, line);
      const startLine = line;
      let j = i + 2;
      while (j < n && !(text[j] === '*' && text[j + 1] === '/')) {
        if (text[j] === '\n') line += 1;
        j += 1;
      }
      // ブロックの中は行ごとに区間を分ける（違反の位置を出現行で報告するため）。
      let segLine = startLine;
      let from = i;
      for (let k = i; k < Math.min(j, n); k += 1) {
        if (text[k] === '\n') {
          push('comment', from, k, segLine);
          segLine += 1;
          from = k + 1;
        }
      }
      push('comment', from, Math.min(j, n), segLine);
      i = Math.min(j + 2, n);
      codeFrom = i;
      continue;
    }

    // --- 文字列リテラル ---
    //
    // 🔴 **接頭辞は `$` と `@` が任意の順序・個数で並ぶ**（PR #1330 レビュー 4 巡目）。
    // C# 8 以降 `$@"…"` と `@$"…"` はどちらも書けるので、**綴りを 1 つずつ列挙すると必ず漏れる**
    // （3 巡目の是正は `@"` だけを見ており、`@$"` 順が通常文字列として読まれていた ——
    // **同じ偽陰性が別の綴りで再現していた**）。接頭辞を**まとめて読み飛ばしてから種別を決める。**
    // raw の補間 `$$"""…"""` も同じ形で入る。
    if (c === '"' || c === '\'' || c === '$' || c === '@') {
      let p = i;
      let hasAt = false;
      while (p < n && (text[p] === '$' || text[p] === '@')) {
        if (text[p] === '@') hasAt = true;
        p += 1;
      }
      // 接頭辞だけで引用符が来ないなら、ただの識別子（`@class` 等）なのでコードとして読み進める。
      if (p >= n || (text[p] !== '"' && text[p] !== '\'')) {
        i = p > i ? p : i + 1;
        continue;
      }
      push('code', codeFrom, i, line);
      const quote = text[p];

      // 🔴 **verbatim の判定が先である**（PR #1330 レビュー 5 巡目）。
      // C# の raw string literal は **`@` 接頭辞を取れない** —— `@` が付いていれば常に verbatim であり、
      // 続く `""` は「raw の開始区切り」ではなく**エスケープされた 1 個の `"`** である。
      // 順序を逆にすると `@"""a"` を「3 連引用符で開く raw」と読み、閉じが見つからないまま
      // **ファイルの残り全体を文字列として飲み込む**（当該ファイルの参照も宣言も丸ごと落ちる）。
      // これまでの 4 巡が 1 行・1 リテラル分の見落としだったのに対し、**発火すると被害がファイル全体**になる。
      if (hasAt) {
        // verbatim: バックスラッシュは素の文字。`""` が引用符のエスケープ。行をまたぐ。
        let j = p + 1;
        while (j < n) {
          if (text[j] === '\n') { line += 1; j += 1; continue; }
          if (text[j] === '"') {
            if (text[j + 1] === '"') { j += 2; continue; }
            j += 1;
            break;
          }
          j += 1;
        }
        i = j;
        codeFrom = i;
        continue;
      }

      // raw string: 開き引用符と**同数以上**の連続引用符で閉じる。行をまたぐ。
      if (quote === '"' && text[p + 1] === '"' && text[p + 2] === '"') {
        let open = 0;
        while (text[p + open] === '"') open += 1;
        let j = p + open;
        while (j < n) {
          if (text[j] === '\n') { line += 1; j += 1; continue; }
          if (text[j] === '"') {
            let run = 0;
            while (text[j + run] === '"') run += 1;
            if (run >= open) { j += run; break; }
            j += run;
            continue;
          }
          j += 1;
        }
        i = j;
        codeFrom = i;
        continue;
      }

      // 通常の文字列 / 文字リテラル: バックスラッシュがエスケープ。行をまたがない。
      let j = p + 1;
      while (j < n && text[j] !== '\n') {
        if (text[j] === '\\') { j += 2; continue; }
        if (text[j] === quote) { j += 1; break; }
        j += 1;
      }
      i = j;
      codeFrom = i;
      continue;
    }

    i += 1;
  }
  push('code', codeFrom, n, line);
  return segments;
}

/**
 * 1 ファイルから「宣言された試験型」と「コメントが指す試験名」を集める。
 *
 * **参照はコメント区間からだけ、宣言はコード区間からだけ**取る（切り分けは `scanSegments` 1 か所）。
 */
function collect(text, rel, declared, referenced) {
  const add = (name, lineNo) => {
    if (!referenced.has(name)) referenced.set(name, []);
    const site = `${rel}:${lineNo}`;
    if (!referenced.get(name).includes(site)) referenced.get(name).push(site);
  };

  for (const s of scanSegments(text)) {
    if (s.kind === 'code') {
      for (const m of s.text.matchAll(DECL_RE)) declared.add(m[1]);
    } else {
      for (const m of s.text.matchAll(NAME_RE)) add(m[1], s.line);
    }
  }
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
    // 🔴 陰性対照（PR #1330 のレビュー）: リテラル内のスラッシュ 2 つをコメント開始と誤らない。
    const url = write('a/Url.cs', 'var u = "http://example.com/GhostInUrlTests";\n');
    const r = scan(dir, [url]);
    t('scan: リテラル内の URL をコメントと誤らない', r.violations.length === 0, r.violations);
  }
  // --- C# の文字列 3 種（PR #1330 レビュー 3 巡目）。終端規則が違うので区別する ---
  const commentsOf = (src) => scanSegments(src).filter((s) => s.kind === 'comment').map((s) => s.text);
  const codesOf = (src) => scanSegments(src).filter((s) => s.kind === 'code').map((s) => s.text).join('');

  t('scanSegments: 通常文字列の後ろのコメントを拾う',
    commentsOf('var u = "http://x"; // AfterNormalTests').join('').includes('AfterNormalTests'),
    commentsOf('var u = "http://x"; // AfterNormalTests'));
  t('scanSegments: 通常文字列の中は拾わない',
    commentsOf('var u = "http://x/InStringTests";').length === 0);
  t('scanSegments: エスケープされた引用符でリテラルが閉じない',
    commentsOf('var u = "a\\"//b"; // AfterEscapeTests').join('').includes('AfterEscapeTests'),
    commentsOf('var u = "a\\"//b"; // AfterEscapeTests'));

  // 🔴 verbatim: バックスラッシュはエスケープではない。
  //   旧実装はここで閉じ引用符を読み飛ばし、**後続のコメントを丸ごと見落としていた**（偽陰性）。
  t('scanSegments: verbatim がバックスラッシュで終わっても閉じを見失わない',
    commentsOf('var p = @"C:\\dir\\"; // AfterVerbatimTests').join('').includes('AfterVerbatimTests'),
    commentsOf('var p = @"C:\\dir\\"; // AfterVerbatimTests'));
  t('scanSegments: verbatim の中の二重引用符では閉じない',
    commentsOf('var p = @"a""b"; // AfterVerbatimEscapeTests').join('').includes('AfterVerbatimEscapeTests'),
    commentsOf('var p = @"a""b"; // AfterVerbatimEscapeTests'));
  t('scanSegments: verbatim の中身は拾わない',
    commentsOf('var p = @"// InVerbatimTests";').length === 0,
    commentsOf('var p = @"// InVerbatimTests";'));

  // 🔴 接頭辞は `$` と `@` が**任意の順序**で並ぶ（4 巡目の指摘）。
  //   綴りを 1 つずつ列挙すると必ず漏れる —— まとめて読み飛ばしてから種別を決める。
  t('scanSegments: $@ 順の補間 verbatim でも閉じを見失わない',
    commentsOf('var p = $@"C:\\{d}\\"; // AfterDollarAtTests').join('').includes('AfterDollarAtTests'),
    commentsOf('var p = $@"C:\\{d}\\"; // AfterDollarAtTests'));
  t('scanSegments: @$ 順の補間 verbatim でも閉じを見失わない',
    commentsOf('var p = @$"C:\\{d}\\"; // AfterAtDollarTests').join('').includes('AfterAtDollarTests'),
    commentsOf('var p = @$"C:\\{d}\\"; // AfterAtDollarTests'));
  t('scanSegments: 補間 verbatim の中身は拾わない（両順）',
    commentsOf('var a = $@"// InDollarAtTests"; var b = @$"// InAtDollarTests";').length === 0,
    commentsOf('var a = $@"// InDollarAtTests"; var b = @$"// InAtDollarTests";'));
  t('scanSegments: 補間 raw string の中身は拾わない',
    commentsOf('var j = $$"""x // InInterpolatedRawTests""";').length === 0,
    commentsOf('var j = $$"""x // InInterpolatedRawTests""";'));

  // 🔴 **verbatim の判定は raw より先である**（5 巡目の指摘）。
  //   C# の raw string は `@` 接頭辞を取れないので、`@` が付いていれば `""` は
  //   「raw の開始区切り」ではなく**エスケープされた 1 個の `"`** である。
  //   逆順だと `@"""a"` を raw と読み、閉じが見つからず**ファイルの残り全体を飲み込む** ——
  //   1〜4 巡が 1 行分の見落としだったのに対し、**発火すると被害がファイル全体**になる。
  {
    const src = 'var s = @"""a"; // AfterAtTripleTests\npublic class LaterDeclTests { }\n';
    t('scanSegments: @""" を raw と誤読してファイルの残りを飲み込まない',
      commentsOf(src).join('').includes('AfterAtTripleTests')
      && codesOf(src).includes('class LaterDeclTests'),
      { comments: commentsOf(src), codes: codesOf(src) });
  }
  for (const pre of ['$@', '@$']) {
    const src = `var s = ${pre}"""a"; // After${pre === '$@' ? 'DA' : 'AD'}TripleTests\n`
      + `public class Later${pre === '$@' ? 'DA' : 'AD'}Tests { }\n`;
    t(`scanSegments: ${pre}""" でも飲み込まない`,
      commentsOf(src).join('').includes('TripleTests') && codesOf(src).includes('class Later'),
      { comments: commentsOf(src), codes: codesOf(src) });
  }
  // 陰性対照: `@` は逐語識別子の接頭辞でもある。文字列でなければコードとして読み進める。
  t('scanSegments: 逐語識別子 @class を文字列と誤らない',
    codesOf('var @class = 1; public class VerbatimIdentTests { }').includes('class VerbatimIdentTests'),
    codesOf('var @class = 1; public class VerbatimIdentTests { }'));

  // 🔴 raw string: 開き引用符と同数以上の連続引用符で閉じる。行をまたぐ。
  t('scanSegments: raw string の中身は拾わない',
    commentsOf('var j = """{"a": "// InRawTests"}""";').length === 0,
    commentsOf('var j = """{"a": "// InRawTests"}""";'));
  t('scanSegments: raw string の後ろのコメントを拾う',
    commentsOf('var j = """x"""; // AfterRawTests').join('').includes('AfterRawTests'),
    commentsOf('var j = """x"""; // AfterRawTests'));
  t('scanSegments: 複数行の raw string を閉じてから続きを読む',
    commentsOf('var j = """\nline // InRawMultilineTests\n"""; // AfterRawMultilineTests')
      .join('') === '// AfterRawMultilineTests',
    commentsOf('var j = """\nline // InRawMultilineTests\n"""; // AfterRawMultilineTests'));

  // コード区間の側も同じ切り分けから取る（宣言の取りこぼし・拾いすぎが無いこと）。
  t('scanSegments: コード区間に宣言が残る',
    codesOf('public class LiveDeclTests { } // x').includes('class LiveDeclTests'));
  t('scanSegments: コメント区間の宣言はコードに出ない',
    !codesOf('// public class DeadDeclTests { }').includes('DeadDeclTests'));
  {
    // 🔴 陽性（PR #1330 のレビュー）: ブロックコメントの中の名前も拾う。
    // ここを拾わないと、同じ主張をブロックコメントで書くだけで検査を逃れられる。
    const blk = write('a/Block.cs', '/* この帰結は GhostInBlockTests が固定する。 */\npublic class Blk { }\n');
    const r = scan(dir, [blk]);
    t('scan: ブロックコメント内の名前を拾う', r.violations.length === 1
      && r.violations[0].name === 'GhostInBlockTests', r.violations);
  }
  {
    // 複数行にまたがるブロックコメントも拾う（行番号は出現行）。
    const blk2 = write('a/Block2.cs', '/*\n * GhostMultilineTests が固定する。\n */\n');
    const r = scan(dir, [blk2]);
    t('scan: 複数行のブロックコメント内も拾い、出現行を返す',
      r.violations.length === 1 && r.violations[0].sites[0] === `${blk2}:2`, r.violations);
  }
  {
    // ブロックが閉じた後のコードは走査しない（対照）。
    const blk3 = write('a/Block3.cs', '/* x */ var s = "GhostAfterBlockTests";\n');
    const r = scan(dir, [blk3]);
    t('scan: ブロックが閉じた後のリテラルは拾わない', r.violations.length === 0, r.violations);
  }
  {
    // 🔴 宣言側の逃げ道を塞ぐ（PR #1330 レビューの 2 つ目の偽陰性）。
    // コメントアウトされた宣言・ブロックコメント内の宣言・リテラル内の宣言はいずれも
    // 「実在する」と数えない —— 数えると、実在しない指し先が黙って通る。
    const commented = write('b/Commented.cs', [
      '// public class CommentedOutTests { }',
      '/* public class BlockDeclTests { } */',
      'var s = "public class StringDeclTests { }";',
    ].join('\n') + '\n');
    const usesThem = write('b/UsesThem.cs', [
      '// CommentedOutTests が固定する。',
      '// BlockDeclTests が固定する。',
      '// StringDeclTests が固定する。',
    ].join('\n') + '\n');
    const r = scan(dir, [commented, usesThem]);
    const names = r.violations.map((v) => v.name).sort();
    t('scan: コメントアウト / リテラル内の宣言は「実在する」と数えない',
      names.join(',') === 'BlockDeclTests,CommentedOutTests,StringDeclTests', r.violations);
  }
  {
    // 陽性対照: 生きている宣言は当然「実在する」と数える。
    const live = write('b/LiveTests.cs', 'namespace N;\npublic class LiveTests { }\n');
    const usesLive = write('b/UsesLive.cs', '// LiveTests が固定する。\n');
    const r = scan(dir, [live, usesLive]);
    t('scan: 生きている宣言は実在すると数える（陽性対照）', r.violations.length === 0, r.violations);
  }
  {
    // 行の途中でブロックが開いて閉じる形でも、コードとコメントを取り違えない。
    const mixed = write('b/Mixed.cs', 'public class MixedTests { } /* GhostInTailTests */\n');
    const usesMixed = write('b/UsesMixed.cs', '// MixedTests が固定する。\n');
    const r = scan(dir, [mixed, usesMixed]);
    t('scan: 同一行のコードとブロックコメントを切り分ける',
      r.violations.length === 1 && r.violations[0].name === 'GhostInTailTests', r.violations);
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

module.exports = { scanSegments, collect, scan, isScanTooSmall, ALLOWED, MIN_SCANNED };
