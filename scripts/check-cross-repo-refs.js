#!/usr/bin/env node
'use strict';
/*
 * check-cross-repo-refs.js
 * NFR / issue #507: 他リポジトリの issue / PR 番号の修飾が規約どおりかを機械検査する。
 *
 * 規約は .claude/rules/traceability.md「クロスリポジトリの issue / PR 番号の修飾」。
 * 短縮形（planning#NNN / AST#NNN）へ寄せ、フルパス形式（<owner>/<repo>#NNN）だけを例外として許す。
 *
 * 検出する 2 つの型:
 *   型 1（長い表記）: リポジトリ名の裸書き。短縮形でもフルパス形式でもない「第 3 の表記」。
 *                     実害は無いが規約が明示的に禁じる表記ゆれ（機械的突合が揺れる）。
 *   型 2（列挙裸）  : 修飾付き参照の**直後**に続く裸の #NNN。先頭だけ修飾して後続を裸にする形で、
 *                     裸の番号が**本リポジトリの実在 issue へ静かに誤リンクする**（実害あり）。
 *                     PR #561 が、規約の書いてある当のファイルの中で犯した型である。
 *
 * 検出しない（＝偽陽性を出さない）もの:
 *   - 本リポジトリ自身の issue 参照。単独の `#454`、自リポ列挙 `#450（FR-17/18）・#451（FR-19/20）`。
 *     **修飾語が直前に無い**ので構造的に掛からない。
 *   - フルパス形式 `endazon/project-planning#50`（規約が許す）。負の後読みで除く。
 *   - スカッシュ既定件名の末尾 ` (#123)`。空白のみを区切りとして採らないため掛からない。
 *   - Markdown のインラインコード／コードフェンスの中（--markdown 時）。GitHub は**そこで自動リンク
 *     しない**ので実害が無く、かつ反例（規約の「誤: ...」）や是正記録の引用を書けなくなる。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-cross-repo-refs.js              # 追跡下の *.md を走査（既定）
 *   node scripts/check-cross-repo-refs.js --self-test  # 検査ロジック自体の自己試験
 *   node scripts/check-cross-repo-refs.js <file>...    # ファイル指定
 *
 * CI への載せ方（.github/workflows/ は GitHub App 権限で編集できないため、既存の呼び出し口へ相乗りする。
 * IADR-0140）:
 *   - scripts/scripts.repo.test.js から --self-test ＋ 実データ走査（ci.yml の scripts-tests ジョブ）
 *   - scripts/check-commit-messages.js から件名・本文・PR タイトル（ci.yml の commit-messages / pr-title.yml）
 */
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');

// 短縮形（規約が正とする書き方）。
const SHORT_NAMES = ['planning', 'AST'];
// 長い表記（第 3 の表記＝型 1）。短縮形との対応は .claude/rules/traceability.md にある。
const LONG_NAMES = { 'project-planning': 'planning', 'ai-stock-trading': 'AST' };

// 型 1: リポジトリ名の裸書き。直前が \w / - / `/` なら別物（`endazon/project-planning#50` は
// 規約が許すフルパス形式、`my-ai-stock-trading#1` は別語）なので負の後読みで除く。
const LONG_RE = /(?<![\w/-])(project-planning|ai-stock-trading)#(\d+)/g;

// 修飾付き参照 1 個ぶん（短縮形・長い表記・フルパス形式のいずれか）。
const QUALIFIED = String.raw`(?:[A-Za-z][\w.-]*\/)?(?:planning|AST|project-planning|ai-stock-trading)#\d+`;
// 列挙の区切り。**空白のみは採らない**（スカッシュ既定件名の ` (#123)` と衝突するため）。
const SEP = String.raw`[ \t]*[/／,，、・･][ \t]*`;
// 型 2: 修飾付き参照の直後に「区切り + 裸の #数字」が 1 個以上続く形。
const ENUM_RE = new RegExp(`(${QUALIFIED})((?:${SEP}#\\d+)+)`, 'g');

/**
 * Markdown のコードフェンスとインラインコードを**同じ長さの空白**へ潰す。
 * 長さを保つのは、行番号・桁位置を元テキストと一致させたまま走査するためである。
 */
function maskCode(text) {
  const out = [];
  let fenced = false;
  for (const line of String(text).split('\n')) {
    if (/^\s*(```|~~~)/.test(line)) {
      fenced = !fenced;
      out.push(' '.repeat(line.length));
      continue;
    }
    if (fenced) {
      out.push(' '.repeat(line.length));
      continue;
    }
    out.push(line.replace(/`[^`]*`/g, (m) => ' '.repeat(m.length)));
  }
  return out.join('\n');
}

/** 文字オフセットから 1 始まりの行番号を返す。 */
function lineNumberAt(text, index) {
  let n = 1;
  for (let i = 0; i < index && i < text.length; i++) if (text[i] === '\n') n++;
  return n;
}

/**
 * 1 つのテキストから違反を集める。
 * @param {string} text
 * @param {{markdown?: boolean}} opts markdown=true でコードスパン／フェンスを対象外にする。
 * @returns {{kind: 'long'|'enum', line: number, matched: string, suggestion: string}[]}
 */
function findViolations(text, opts = {}) {
  const src = String(text == null ? '' : text);
  // 走査は「潰した側」に対して行う。潰した文字は空白なので #NNN が消え、コード内は掛からない。
  const scan = opts.markdown ? maskCode(src) : src;
  const out = [];

  LONG_RE.lastIndex = 0;
  let m;
  while ((m = LONG_RE.exec(scan))) {
    out.push({
      kind: 'long',
      line: lineNumberAt(scan, m.index),
      matched: m[0],
      suggestion: `${LONG_NAMES[m[1]]}#${m[2]}`,
    });
  }

  ENUM_RE.lastIndex = 0;
  while ((m = ENUM_RE.exec(scan))) {
    // 先頭の修飾語（短縮形へ正規化した名前）を後続の裸番号へ配る。
    const head = m[1];
    const nameMatch = head.match(/(?:^|\/)(planning|AST|project-planning|ai-stock-trading)#/);
    const short = nameMatch ? LONG_NAMES[nameMatch[1]] || nameMatch[1] : 'planning';
    const fixed = m[0].replace(/(^|[/／,，、・･][ \t]*)#(\d+)/g, (whole, pre, num) =>
      pre === '' ? whole : `${pre}${short}#${num}`
    );
    out.push({
      kind: 'enum',
      line: lineNumberAt(scan, m.index),
      matched: m[0],
      suggestion: fixed,
    });
  }

  out.sort((a, b) => a.line - b.line || a.matched.localeCompare(b.matched));
  return out;
}

/** git 管理下の *.md（submodule 配下を除く）を列挙する。git を使えなければ null。 */
function trackedMarkdown(root = REPO_ROOT) {
  let raw;
  try {
    raw = execFileSync(
      'git',
      ['-C', root, 'ls-files', '--', '*.md', ':!planning', ':!src/ai-stock-trading'],
      { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }
    );
  } catch (e) {
    return null;
  }
  return raw.split('\n').map((s) => s.trim()).filter(Boolean);
}

/** ファイル群を検査し、{file, violations} の配列を返す。 */
function checkFiles(files, root = REPO_ROOT) {
  const report = [];
  for (const rel of files) {
    let text;
    try {
      text = fs.readFileSync(path.isAbsolute(rel) ? rel : path.join(root, rel), 'utf8');
    } catch (e) {
      continue;
    }
    const violations = findViolations(text, { markdown: /\.md$/i.test(rel) });
    if (violations.length) report.push({ file: rel, violations });
  }
  return report;
}

function formatReport(report) {
  const lines = [];
  for (const r of report) {
    lines.push(`\n  ${r.file}`);
    for (const v of r.violations) {
      const label = v.kind === 'long' ? '長い表記' : '列挙形の修飾漏れ';
      lines.push(`    ${r.file}:${v.line}  [${label}] ${v.matched}  →  ${v.suggestion}`);
    }
  }
  return lines.join('\n');
}

// --- 自己試験 -------------------------------------------------------------------
//
// 正のケース（検出すべき）と負のケース（検出してはならない）を**対で**固定する。
// 負のケースが本体である——偽陽性を 1 件でも出すと、正当な自リポ参照（#454 等）が止まり、
// 検査そのものが外される。増減するときは必ず対で足すこと。

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });
  const kinds = (text, opts) => findViolations(text, opts).map((v) => v.kind);

  // --- 正のケース: 型 1（長い表記） ---
  t('型1: project-planning#50 を検出', kinds('計画への環流: project-planning#50。').join() === 'long');
  t('型1: ai-stock-trading#122 を検出', kinds('AST chart は ai-stock-trading#122 で追加').join() === 'long');
  t('型1: Markdown リンクのテキストでも検出', kinds(
    '[project-planning#50](https://github.com/endazon/project-planning/issues/50)', { markdown: true }
  ).join() === 'long');
  t('型1: 行頭でも検出', kinds('project-planning#22 → 本 #260').join() === 'long');
  t('型1: 是正案は短縮形を出す',
    findViolations('project-planning#50')[0].suggestion === 'planning#50');
  t('型1: ai-stock-trading の是正案は AST',
    findViolations('ai-stock-trading#122')[0].suggestion === 'AST#122');

  // --- 正のケース: 型 2（列挙形の修飾漏れ） ---
  t('型2: planning#206 / #207 を検出（PR #561 が犯した形）',
    kinds('planning pin を planning#206 / #207 へ進め').join() === 'enum');
  t('型2: 3 連以上も 1 件として検出', kinds('planning#201 / #202 / #203 の反映').join() === 'enum');
  t('型2: 空白なしのスラッシュ（AST#217/#208）も検出', kinds('AST#217/#208 から参照').join() === 'enum');
  t('型2: 中黒（planning#146・#149）も検出', kinds('planning#146・#149 の三つ組').join() === 'enum');
  t('型2: 読点・全角スラッシュも区切りとして扱う',
    kinds('planning#146、#149').join() === 'enum' && kinds('planning#146／#149').join() === 'enum');
  t('型2: 長い表記が先頭でも検出（型1 と二重に上がる）',
    kinds('project-planning#22 / #24').sort().join() === 'enum,long');
  t('型2: 是正案は各番号を修飾する',
    findViolations('planning#206 / #207')[0].suggestion === 'planning#206 / planning#207');
  t('型2: 是正案は 3 連でもすべて修飾する',
    findViolations('planning#201 / #202 / #203')[0].suggestion
      === 'planning#201 / planning#202 / planning#203');
  t('型2: 先頭が AST なら AST を配る',
    findViolations('AST#217 / #208')[0].suggestion === 'AST#217 / AST#208');

  // --- 負のケース: 偽陽性を出してはならない ---
  t('負例: 正しい列挙（planning#206 / planning#207）は検出しない',
    kinds('planning#206 / planning#207 へ進め').length === 0);
  t('負例: 本リポジトリの単独参照 #454 は検出しない', kinds('親 issue は #454 である。').length === 0);
  t('負例: 本リポジトリの issue 列挙は検出しない（修飾語が直前に無い）',
    kinds('#450（FR-17/18）・#451（FR-19/20）の保留は解除されない').length === 0);
  t('負例: フルパス形式 endazon/project-planning#50 は規約が許すので検出しない',
    kinds('endazon/project-planning#50 を参照').length === 0);
  t('負例: フルパス形式の列挙も検出しない',
    kinds('endazon/ai-stock-trading#291 / endazon/ai-stock-trading#296').length === 0);
  t('負例: スカッシュ既定件名の末尾 (#123) は検出しない',
    kinds('chore(NFR): planning#206 を反映 (#561)').length === 0);
  t('負例: 「半角スペース + (#123)」という書式例も検出しない',
    kinds('**末尾の PR 番号**: 半角スペース + (#123) はスカッシュマージ既定件名として許容。').length === 0);
  t('負例: 修飾語と裸番号の間に文があれば検出しない（列挙ではない）',
    kinds('planning#206 を反映した。あわせて #207 も確認した。').length === 0);
  t('負例: URL 中のリポジトリ名は検出しない',
    kinds('https://github.com/endazon/project-planning/issues/50').length === 0);
  t('負例: ID 修飾（AST/FR-17）は issue 番号ではないので検出しない',
    kinds('AST/FR-17 と AST/SC-01 は別採番である').length === 0);
  t('負例: 語の一部（my-ai-stock-trading#1）は検出しない',
    kinds('my-ai-stock-trading#1').length === 0);
  t('負例: 日付やバージョンの / は列挙ではない', kinds('2026/08/07 の #507').length === 0);

  // --- Markdown モード: コードスパン／フェンスは対象外（反例・引用を書けること） ---
  t('md: インラインコードの反例は検出しない（規約の「誤: ...」）',
    kinds('誤: `planning#146 / #149 / #160`。正: `planning#146 / planning#149 / planning#160`。',
      { markdown: true }).length === 0);
  t('md: インラインコードの長い表記も検出しない',
    kinds('検索式は `project-planning#50` である', { markdown: true }).length === 0);
  t('md: コードフェンスの中は検出しない',
    kinds('前文\n```console\n$ echo "planning#206 / #207"\n$ echo project-planning#50\n```\n後文',
      { markdown: true }).length === 0);
  t('md: コードスパンの外は検出する（潰しすぎていない）',
    kinds('`ok` planning#206 / #207 `ok`', { markdown: true }).join() === 'enum');
  t('md: コードスパンを潰しても行番号がずれない', (() => {
    const v = findViolations('1 行目 `code`\n2 行目\nplanning#206 / #207', { markdown: true });
    return v.length === 1 && v[0].line === 3;
  })());
  t('非 md モードではバッククォートを潰さない（コミットメッセージは自動リンクが効く）',
    kinds('`planning#206 / #207`').join() === 'enum');

  // --- 複数行・複数件 ---
  t('複数行から全件を拾い、行番号を返す', (() => {
    const v = findViolations('a\nproject-planning#50\nb\nplanning#206 / #207\n');
    return v.length === 2 && v[0].line === 2 && v[1].line === 4;
  })());

  // --- 実ファイル走査の経路（fixture） ---
  {
    const os = require('os');
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'crossrepo-selftest-'));
    fs.writeFileSync(path.join(dir, 'ng.md'), '# x\n\nproject-planning#50 と planning#206 / #207。\n');
    fs.writeFileSync(path.join(dir, 'ok.md'), '# y\n\nplanning#206 / planning#207 と #454。\n');
    const rep = checkFiles(['ng.md', 'ok.md'], dir);
    t('checkFiles: 違反ファイルだけを報告する', rep.length === 1 && rep[0].file === 'ng.md',
      rep.map((r) => r.file));
    t('checkFiles: 1 ファイル内の 2 型を両方報告する',
      rep[0] && rep[0].violations.length === 2, rep[0] && rep[0].violations);
    fs.rmSync(dir, { recursive: true, force: true });
  }

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) {
      failed++;
      if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual));
    }
  }
  if (failed) {
    console.error(`[check-cross-repo-refs] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-cross-repo-refs] 自己試験 ${cases.length} 件 all passed。`);
}

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) {
    selfTest();
    return;
  }
  const explicit = argv.filter((x) => !x.startsWith('--'));
  let files = explicit;
  if (files.length === 0) {
    files = trackedMarkdown();
    if (files === null) {
      // git を使えない環境（tarball 展開等）では検査をスキップする（fail-open）。
      // 黙って 0 件検査へ落ちたことが分かるよう理由を出す。
      console.error('[check-cross-repo-refs] git ls-files を実行できないため走査をスキップした。');
      process.exit(0);
    }
  }
  const report = checkFiles(files);
  const total = report.reduce((n, r) => n + r.violations.length, 0);
  if (total === 0) {
    console.log(`[check-cross-repo-refs] OK: ${files.length} 件の Markdown に他リポジトリ参照の表記違反はありません。`);
    process.exit(0);
  }
  console.error(`[check-cross-repo-refs] 他リポジトリ参照の表記違反 ${total} 件を検出しました:`);
  console.error(formatReport(report));
  console.error(
    '\n規約（.claude/rules/traceability.md）: 他リポジトリの issue / PR 番号は短縮形' +
      `（${SHORT_NAMES.join(' / ')}#NNN）へ揃え、**列挙形でも各番号を修飾する**。\n` +
      '意図的に誤例を書く場合はインラインコード（`...`）かコードフェンスに入れること' +
      '（GitHub はそこで自動リンクせず、実害が無い）。\n'
  );
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  findViolations,
  checkFiles,
  formatReport,
  maskCode,
  trackedMarkdown,
  selfTest,
  LONG_RE,
  ENUM_RE,
  SHORT_NAMES,
  LONG_NAMES,
};
