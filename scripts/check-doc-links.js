#!/usr/bin/env node
'use strict';
/*
 * check-doc-links.js
 * docs/ と .ai-context/ 配下の Markdown に含まれる相対リンクの実在を検査する（リンク切れ再発防止）。
 * 検査対象:
 *   - フロントマター（先頭 --- ... ---）のリスト項目パス（plan_refs / related_specs / related など）
 *   - 本文の Markdown リンク [text](path)
 *   - 本文のインラインコード内の相対パス表記 `../path.ext`
 * 対象外（誤検知回避）:
 *   - 外部 URL（http/https/mailto ほかスキーム付き）・アンカー(#...)・ルート絶対パス(/...)
 *   - テンプレ変数（${...} / {{...}} / <...>）
 *   - 未チェックアウトの submodule（`.gitmodules` から導出。src/ai-stock-trading 等）配下のリンク
 *
 * **本リポジトリは planning に依存しない**（ADR-0048 決定 2）。かつて持っていた
 * planning submodule 未 populate 時の分岐（`--require-planning` / `planningPopulated()`）は
 * submodule そのものを撤去したため退役した（in-repo 完結化）。
 *
 * 走査ルートは既定で **`docs/` と `.ai-context/`（AI 向け文脈資料・凍結記録）の両方**。
 * `.ai-context/` は名前がドットで始まるが、本スクリプトの再帰（`fs.readdirSync`）は
 * 隠しディレクトリを暗黙にスキップしない。**既定のルート一覧に明示で加えていること自体**が
 * 「glob ベースのツールは隠しディレクトリを既定で無視し得る」という落とし穴への備えである
 * （ADR-0048 の実装メモ）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。破損リンクがあれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-doc-links.js               # 既定: docs と .ai-context の両方を走査
 *   node scripts/check-doc-links.js --dir docs     # 走査ルートを明示指定（複数回指定可）
 *   node scripts/check-doc-links.js --self-test    # 検査ロジック自体の自己試験。
 *   # CI 例: - run: node scripts/check-doc-links.js
 */
const fs = require('fs');
const path = require('path');
const { notice } = require('./lib/ci-annotate.js');

// 既定はリポジトリルート。テストで未 populate 状態を再現するため DOC_LINKS_ROOT で上書き可能にする。
const REPO_ROOT = process.env.DOC_LINKS_ROOT
  ? path.resolve(process.env.DOC_LINKS_ROOT)
  : path.resolve(__dirname, '..');

// 参照として実在検査を行う拡張子（仕様書・図・スキーマ・**コードファイル**）。
// コード拡張子（js/ts/cs/csproj/props/slnx/sh ほか）が抜けていた間、仕様書からコードへの
// live link は一切検査されず、破損したまま「OK: 384 件」と報告された（MSP#470 / planning#167。
// 検査器を作る PR が、検査器の穴で自分の参照切れを見逃した型）。
// `txt` / `log` / `lock` 等の汎用拡張子は誤検知リスクのため**意図的に対象外**とし、その方針を
// 下の自己試験（--self-test）で固定してある。スタック固有分（cs/csproj/props/targets/slnx は
// .NET、ts/tsx は TS）もキット既定に含める（在っても他スタックで誤検知しない拡張子のみ）。
// 増減するときは self-test の正例・負例を必ず対で更新すること。
const LINK_EXT = /\.(md|ya?ml|json|puml|mmd|png|jpe?g|svg|drawio|js|mjs|cjs|ts|tsx|cs|csproj|props|targets|slnx|sh)$/i;

// 既定の走査ルート（リポジトリルートからの相対パス）。
const DEFAULT_DIRS = ['docs', '.ai-context'];

function parseArgs(argv) {
  const a = { dirs: [] };
  for (let i = 0; i < argv.length; i++) {
    const x = argv[i];
    if (x === '--dir') a.dirs.push(argv[++i]);
    else if (x.startsWith('--dir=')) a.dirs.push(x.slice(6));
  }
  if (a.dirs.length === 0) a.dirs = DEFAULT_DIRS.slice();
  return a;
}

// .gitmodules から submodule の path 一覧を得る。
function submodulePaths(root = REPO_ROOT) {
  try {
    const txt = fs.readFileSync(path.join(root, '.gitmodules'), 'utf8');
    const out = [];
    const re = /^\s*path\s*=\s*(.+?)\s*$/gm;
    let m;
    while ((m = re.exec(txt))) out.push(m[1].replace(/\\/g, '/'));
    return out;
  } catch (e) {
    return [];
  }
}

// 解決済み絶対パスが未 populate（空プレースホルダ）な submodule 配下にあれば、その submodule の
// パスを返す（無ければ null）。トークン不要の PR CI は submodule を populate しないため、
// その配下のリンクは検査対象外にする（populate 済みなら通常どおり実在検査する）。
// かつては `planning/` 固定で判定していたが、それでは planning 以外の submodule
// （ユニットを submodule で取り込む構成等）配下のリンクが PR CI で破損と誤検知された。
// .gitmodules 由来の一般則へ拡張してある。
//
// 真偽値ではなく**対象を返す**のは、どの submodule を何件飛ばしたかを報告するためである。
// 黙って除外すると「検査していない範囲があること」が出力から読み取れない（issue planning#139）。
function unpopulatedSubmoduleOf(resolvedAbs, root = REPO_ROOT) {
  const rel = path.relative(root, resolvedAbs).replace(/\\/g, '/');
  for (const sub of submodulePaths(root)) {
    if (rel === sub || rel.startsWith(sub + '/')) {
      const subAbs = path.join(root, sub);
      let populated = false;
      try {
        populated = fs.existsSync(subAbs) && fs.readdirSync(subAbs).length > 0;
      } catch (e) {
        populated = false;
      }
      if (!populated) return sub;
    }
  }
  return null;
}

function underUnpopulatedSubmodule(resolvedAbs, root = REPO_ROOT) {
  return unpopulatedSubmoduleOf(resolvedAbs, root) !== null;
}

function mdFiles(dir) {
  let out = [];
  let ents;
  try { ents = fs.readdirSync(dir, { withFileTypes: true }); } catch (e) { return out; }
  for (const ent of ents) {
    const p = path.join(dir, ent.name);
    if (ent.isDirectory()) out = out.concat(mdFiles(p));
    else if (ent.isFile() && ent.name.endsWith('.md')) out.push(p);
  }
  return out;
}

// 相対リンク候補を1つ検査。実在しなければ true（＝リンク切れ）。判定不能・対象外は false。
function isBrokenRef(ref, baseDir, onSkip) {
  if (!ref) return false;
  let t = String(ref).trim().replace(/^["'`]|["'`]$/g, '').trim();
  if (!t) return false;
  if (/^(https?:|mailto:|#|\/|[a-z]+:\/\/)/i.test(t)) return false; // 外部/アンカー/絶対
  if (t.startsWith('<') || t.includes('${') || t.includes('{{')) return false; // テンプレ変数
  // `.ai-context/` frontmatter の `plan_refs` は `planning:<計画リポ相対パス>` という非パス表記へ
  // 変換済み（ADR-0048 決定 3(c)）。パスではなく識別子なので実在検査の対象外とする。
  if (/^planning:/.test(t)) return false;
  // 同様に、旧 feedback/（ADR-0048 決定 5 で撤去。環流は planning の GitHub issue へ一本化）を
  // 指していた frontmatter リスト項目は `feedback:<basename>` という非パス表記へ変換済み。
  if (/^feedback:/.test(t)) return false;
  t = t.split('#')[0].split('?')[0].trim();
  if (!t) return false;
  // **同一ディレクトリのベアファイル名（`./` も `/` も無い形）も相対リンクである**（planning#337）。
  // かつては `./` `../` で始まるか `/` を含むものしか相対と見なさず、`IADR-0118_xxx.md` の形が
  // **一切検査されていなかった** —— 実在しないファイルを指すリンクを足しても
  // `OK: … 破損した相対リンクはありません` で緑になった。**`.ai-context/adr/` の §関連 はほぼこの形で
  // 書かれる**ため、最も壊れやすい箇所がまるごと対象外だったことになる。
  // **2 つの実装リポジトリが独立に同じ穴を踏んで同じ修正へ至った**（endazon/ai-stock-trading#399 /
  // endazon/microservices-platform#609）。**実測件数はここに書かない** —— リンクを 1 本足しただけで
  // 黙って古くなるためである。
  //
  // 誤検出の抑えは `LINK_EXT`（直後）が担う —— 拡張子を持たない語（`README`）や
  // `Foo.Bar` のような識別子は `LINK_EXT` に掛からず、相対リンクとして扱われない。
  // `!t.includes('/')` を明示して従来の節と互いに素にしてある（何が新たに対象へ入ったかを読めるように）。
  const bareFileName = !t.includes('/') && LINK_EXT.test(t);
  const looksRelative =
    t.startsWith('./') || t.startsWith('../') || (t.includes('/') && !t.startsWith('/')) || bareFileName;
  if (!looksRelative) return false;
  if (!LINK_EXT.test(t)) return false;
  const resolved = path.resolve(baseDir, t);
  // 未チェックアウトの submodule 配下は検査しない。CI の actions/checkout（サブモジュール
  // 取得なし）は submodule を「空のプレースホルダディレクトリ」として作るため、存在チェック
  // だけでは未チェックアウトを判別できない。中身が空（＝未 populate）なら対象外とする。
  const skippedSub = unpopulatedSubmoduleOf(resolved);
  if (skippedSub) {
    // 除外したことを呼び出し側へ知らせる。件数を報告しないと「破損リンクはありません」が
    // 検査していない範囲まで含んだ断定になる（issue planning#139）。
    if (onSkip) onSkip(skippedSub);
    return false;
  }
  try { return !fs.existsSync(resolved); } catch (e) { return false; }
}

// 1ファイルの破損リンクを収集。
function collectBroken(fp, onSkip) {
  let content = '';
  try { content = fs.readFileSync(fp, 'utf8'); } catch (e) { return []; }
  const baseDir = path.dirname(fp);
  const broken = new Set();
  let m;
  // 1) フロントマターのリスト項目パス
  const fm = content.match(/^---\n([\s\S]*?)\n---/);
  if (fm) {
    const re = /^\s*-\s*(.+)$/gm;
    while ((m = re.exec(fm[1]))) {
      // 引用符（"..." / '...' / `...`）を外し、末尾の注記（例: 「... .md (FR-01)」）も除去してから判定する
      const val = m[1].trim()
        .replace(/^["'`]|["'`]$/g, '').trim()
        .replace(/\s*\([^)]*\)\s*$/, '').trim();
      if (LINK_EXT.test(val) && isBrokenRef(val, baseDir, onSkip)) broken.add(val);
    }
  }
  // 2) 本文の Markdown リンク [text](path)
  const linkRe = /\]\(([^)]+)\)/g;
  while ((m = linkRe.exec(content))) {
    if (isBrokenRef(m[1], baseDir, onSkip)) broken.add(m[1].trim());
  }
  // 3) 本文のインラインコード内の相対パス `./ ../`
  const codeRe = /`([^`]+)`/g;
  while ((m = codeRe.exec(content))) {
    const v = m[1].trim();
    if ((v.startsWith('./') || v.startsWith('../')) && LINK_EXT.test(v) && isBrokenRef(v, baseDir, onSkip)) broken.add(v);
  }
  return Array.from(broken);
}

// --- 自己試験 -------------------------------------------------------------------
//
// 検査対象の拡張子を広げるたび、正例（実在 → OK）と負例（不在 → 検出）を対で足す。
// 「検査しているつもりで何も見ていない」状態（planning#167）を回帰させないための最小の歯止め。

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });
  const os = require('os');

  // LINK_EXT: 既存の対象（仕様書・図・スキーマ）は従来どおり。
  t('LINK_EXT: .md / .yaml / .json / .svg は対象', ['a.md', 'a.yaml', 'a.yml', 'a.json', 'a.svg']
    .every((x) => LINK_EXT.test(x)));
  // LINK_EXT: コードファイル（MSP#470 / planning#167 で追加）。
  for (const ext of ['js', 'mjs', 'cjs', 'ts', 'tsx', 'cs', 'csproj', 'props', 'targets', 'slnx', 'sh']) {
    t(`LINK_EXT: .${ext} は対象（planning#167）`, LINK_EXT.test(`a.${ext}`));
  }
  t('LINK_EXT: 対象外の拡張子は素通し（誤検知しない）',
    !LINK_EXT.test('a.txt') && !LINK_EXT.test('a.tsv') && !LINK_EXT.test('a'));

  // isBrokenRef の正例／負例。baseDir は scripts/ 自身（実在する .js が確実にある）。
  const here = __dirname;
  t('正例: 実在する .js への相対リンクは破損でない',
    isBrokenRef('./check-doc-links.js', here) === false);
  t('正例: 一段上がる .js リンクも解決する',
    isBrokenRef('../scripts/check-doc-links.js', here) === false);
  t('負例: 実在しない .js への相対リンクは破損として検出する',
    isBrokenRef('./__no_such_script__.js', here) === true);
  for (const ext of ['mjs', 'cjs', 'ts', 'tsx', 'cs', 'csproj', 'props', 'targets', 'slnx', 'sh']) {
    t(`負例: 実在しない .${ext} も検出する`, isBrokenRef(`./__no_such__.${ext}`, here) === true);
  }
  t('対象外: 拡張子が対象外なら実在しなくても検出しない',
    isBrokenRef('./__no_such__.txt', here) === false);

  // --- 同一ディレクトリのベアファイル名（`./` も `/` も無い形。planning#337） ----------------
  //
  // **この対が無かったことが穴を長く開けたままにした直接の原因である。**
  // `.ai-context/adr/` の §関連 はほぼこの形で書かれており、実データに多数あるが、`looksRelative` が
  // `/` の有無しか見ていなかったため**全件が無検査**だった。
  t('正例: 同一ディレクトリの実在ファイルをベア名で指しても破損でない',
    isBrokenRef('check-doc-links.js', here) === false);
  t('負例: 同一ディレクトリの不在ファイルをベア名で指すと検出する',
    isBrokenRef('__no_such_script__.js', here) === true);
  t('負例: .md も同じ（ADR の §関連 で実際に踏んだ型）',
    isBrokenRef('__no_such_adr__.md', here) === true);
  t('誤検出しない: 拡張子を持たない語はベア名でも相対リンクと見なさない',
    isBrokenRef('README', here) === false && isBrokenRef('IADR-0118', here) === false);
  t('誤検出しない: 対象外拡張子の識別子はベア名でも検出しない',
    isBrokenRef('Foo.Bar', here) === false && isBrokenRef('__no_such__.txt', here) === false);

  t('対象外: 外部 URL・アンカー・ルート絶対パスは検出しない',
    ['https://example.com/a.js', '#section', '/etc/a.js'].every((x) => isBrokenRef(x, here) === false));
  t('対象外: テンプレ変数を含む表記は検出しない',
    isBrokenRef('${DIR}/a.js', here) === false && isBrokenRef('<path>/a.js', here) === false);
  t('対象外: plan_refs の非パス表記 planning:... は検出しない（ADR-0048 決定 3c）',
    isBrokenRef('planning:projects/microservices-platform/07_adr/ADR-0001_x.md', here) === false);
  t('対象外: 旧 feedback/ の非パス表記 feedback:... は検出しない（ADR-0048 決定 5）',
    isBrokenRef('feedback:20260101_example.md', here) === false);
  t('アンカー・クエリ付きでも本体パスで判定する',
    isBrokenRef('./check-doc-links.js#L30', here) === false
      && isBrokenRef('./__no_such_script__.js#L1', here) === true);

  // collectBroken: Markdown リンク／インラインコード／フロントマターの 3 経路で .js を拾う。
  {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'doclinks-selftest-'));
    const okJs = path.join(dir, 'real.js');
    fs.writeFileSync(okJs, '// fixture\n');
    const md = path.join(dir, 'a.md');
    fs.writeFileSync(
      md,
      '---\nrelated_specs:\n  - ./real.js\n  - ./fm-missing.js\n---\n\n' +
        '# A\n\n[ok](./real.js) と [ng](./missing.js)。\n\n' +
        'インラインコードの `./inline-missing.js` も拾う。\n'
    );
    const broken = collectBroken(md).sort();
    t('collectBroken: 実在する .js リンクは報告しない（正例）', !broken.includes('./real.js'), broken);
    t('collectBroken: 本文の .js リンク切れを検出（負例）', broken.includes('./missing.js'), broken);
    t('collectBroken: フロントマターの .js も検出', broken.includes('./fm-missing.js'), broken);
    t('collectBroken: インラインコードの .js も検出', broken.includes('./inline-missing.js'), broken);
    fs.rmSync(dir, { recursive: true, force: true });
  }

  // --- 走査ルート: ドット始まりのディレクトリを既定で拾う（.ai-context/ 対策） -------------
  {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'doclinks-dotdir-'));
    const hidden = path.join(dir, '.ai-context', 'adr');
    fs.mkdirSync(hidden, { recursive: true });
    fs.writeFileSync(path.join(hidden, 'IADR-0001_x.md'), '# X\n');
    const found = mdFiles(path.join(dir, '.ai-context'));
    t('mdFiles: ドット始まりディレクトリの配下も走査する（隠しディレクトリをスキップしない）',
      found.length === 1 && found[0].endsWith('IADR-0001_x.md'), found);
    fs.rmSync(dir, { recursive: true, force: true });
  }

  // --- parseArgs: 既定は docs と .ai-context の両方（ADR-0048 決定 1） ----------------------
  {
    t('parseArgs: 引数無しは既定で docs と .ai-context の両方',
      JSON.stringify(parseArgs([]).dirs) === JSON.stringify(['docs', '.ai-context']));
    t('parseArgs: --dir を複数回渡すとその通りに走査ルートを積む',
      JSON.stringify(parseArgs(['--dir', 'docs', '--dir', '.ai-context']).dirs) === JSON.stringify(['docs', '.ai-context']));
  }

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[check-doc-links] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-doc-links] 自己試験 ${cases.length} 件 OK。`);
}

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }
  const a = parseArgs(process.argv.slice(2));
  const seen = new Set();
  const files = [];
  for (const d of a.dirs) {
    for (const fp of mdFiles(path.isAbsolute(d) ? d : path.join(REPO_ROOT, d))) {
      if (!seen.has(fp)) { seen.add(fp); files.push(fp); }
    }
  }
  // ★ 0 件走査で緑を返さない（fail-closed。planning#337）。走査対象を 1 件も拾えないのは
  // 「検査しているつもりで何も見ていない」状態であり、**退行を止めているという記録だけが残る**。
  if (files.length === 0) {
    console.error(`[check-doc-links] ${a.dirs.join(', ')} 配下に Markdown が 1 件もありません。`);
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    process.exit(1);
  }
  let total = 0;
  const report = [];
  // 未 populate な submodule（src/ai-stock-trading 等）配下として除外したリンクを submodule 別に数える。
  const skipped = new Map();
  const onSkip = (sub) => skipped.set(sub, (skipped.get(sub) || 0) + 1);
  for (const fp of files) {
    const b = collectBroken(fp, onSkip);
    if (b.length) {
      total += b.length;
      report.push({ fp, links: b });
    }
  }

  // 検査対象外にした範囲を必ず知らせる（issue planning#139）。
  // これを黙っていると「破損した相対リンクはありません」が、実際には検査していない範囲まで
  // 含んだ断定になる。
  const skippedTotal = [...skipped.values()].reduce((n, v) => n + v, 0);
  let skipNote = '';
  if (skippedTotal > 0) {
    const detail = [...skipped.entries()].map(([sub, n]) => `${sub}: ${n} 件`).join(', ');
    skipNote = `（未 populate の submodule 配下 ${skippedTotal} 件は対象外 — ${detail}）`;
    notice(
      `未 populate の submodule 配下 ${skippedTotal} 件のリンクを検査対象外にした（${detail}）。` +
        'この範囲は本実行では検査されていない。PR 段階で検査するには checkout に submodules を付けること'
    );
  }

  if (total === 0) {
    console.log(
      `[check-doc-links] OK: ${files.length} 件の Markdown（走査ルート: ${a.dirs.join(', ')}）に破損した相対リンクはありません${skipNote}。`
    );
    process.exit(0);
  }
  console.error(`[check-doc-links] 破損リンク ${total} 件を検出しました:`);
  for (const r of report) {
    console.error(`\n  ${r.fp}`);
    for (const l of r.links) console.error(`    - ${l}`);
  }
  console.error('\n相対パスの綴り・階層（例: docs/functional/ からは ../../.ai-context/adr/... ）を確認してください。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  parseArgs,
  submodulePaths,
  underUnpopulatedSubmodule,
  unpopulatedSubmoduleOf,
  isBrokenRef,
  collectBroken,
  mdFiles,
  selfTest,
  LINK_EXT,
  DEFAULT_DIRS,
};
