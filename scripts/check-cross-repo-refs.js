#!/usr/bin/env node
'use strict';
/*
 * check-cross-repo-refs.js
 * NFR / issue #507: 他リポジトリの issue / PR 番号の修飾が規約どおりかを機械検査する。
 *
 * 規約は .claude/rules/traceability.md「クロスリポジトリの issue / PR 番号の修飾」。
 * 短縮形（planning#NNN / AST#NNN）へ寄せ、フルパス形式（<owner>/<repo>#NNN）だけを例外として許す。
 *
 * 検出する型（**件数はここに書かない** —— 数を書くと型を足したとき片方が黙って古くなる。
 * 下の列挙そのものが単一情報源である。#590 / [[IADR-0141]]「参照点を 1 つに畳む」）:
 *   型 1（長い表記）  : リポジトリ名の裸書き（第 3 の表記）。短縮形でもフルパス形式でもない。
 *   型 2（列挙裸）    : 修飾付き参照の**直後**に続く裸の #NNN。先頭だけ修飾して後続を裸にする形。
 *                       PR #561 が、規約の書いてある当のファイルの中で犯した型である。
 *   型 3（空白区切り）: 修飾語と番号が空白で離れた形。規約の書式は詰めた形であり、空白が入ると
 *                       機械的突合に掛からない。#507 のクロス監査（2026-08-07）が実測した型。
 *   型 4（owner 誤り）: フルパス形式だが owner が既知集合（endazon）でない形。定期監査 2 回目
 *                       （adr-guardian・2026-08-07）が実測した型。**.md で唯一「実害」が出る型**
 *                       である —— 型 1〜3 は .md では表記ゆれに留まるのに対し、フルパス形式だけが
 *                       .md で自動リンクになるため、owner を誤ると**死んだリンクが描画される**。
 *
 * **型 4 は「フルパス形式一般」ではなく「自組織が持つリポジトリ名」に限定して検査する**（#590 の
 * 母集合の実測）。フルパス形式に見えるスラッシュには owner でないものが多数あり、限定しないと
 * 以下がすべて偽陽性になる:
 *   - `anthropics/claude-code-action#723` … 第三者リポジトリへの正当な参照（owner が endazon で
 *     ないのが**正しい**）
 *   - `owner/repo#123`                    … 書式を説明するリテラルな見本（実装・自己試験・仕様書）
 *   - `AST#186/AST#192`                   … `/` は**列挙の区切り**であって owner ではない
 *   - `…spa-router-shell.md#2`            … Markdown のアンカーリンク
 *
 * **自動リンクが効く面と効かない面を区別すること**（クロス監査の実測。IADR-0140 決定 1・4）:
 *   - `.md` のレンダリングでは、裸の `#NNN` も短縮形の修飾も**自動リンクにならない**。
 *     `.md` で自動リンクするのはフルパス形式（`endazon/<repo>` + `#` + 番号）だけである。
 *     したがって `.md` における**型 1〜3 の害は表記ゆれ（機械的突合の不安定）**であって誤リンクではない。
 *     **型 4 だけは別で、.md でも誤リンク（死んだリンク）という実害が出る**（#590）。
 *   - issue / PR / コミットメッセージの本文では裸の `#NNN` が**本リポジトリの issue へ自動リンクする**。
 *     こちらは誤リンクという実害が出る面であり、`check-commit-messages.js` 経由で検査する。
 *
 * 検出しない（＝偽陽性を出さない）もの:
 *   - 本リポジトリ自身の issue 参照。単独の `#454`、自リポ列挙 `#450（FR-17/18）・#451（FR-19/20）`。
 *     **修飾語が直前に無い**ので構造的に掛からない。
 *   - **自リポジトリを指す修飾語**（`MSP` / `microservices-platform` の直後）。裸の `#NNN` が
 *     本リポジトリを指すのは**正しい**ので、型 3 の修飾語集合から意図的に外してある（実測 22 件）。
 *   - フルパス形式 `endazon/project-planning#50`（規約が許す）。負の後読みで除く。
 *   - スカッシュ既定件名の末尾 ` (#123)`。空白のみを列挙の区切りとして採らないため掛からない。
 *   - Markdown のインラインコード／コードフェンスの中（--markdown 時）。**反例（規約の「誤: ...」）や
 *     是正記録が引用する「誤った文字列そのもの」を書けなくしないため**（除外リストではなく
 *     「literal な引用は表記規約の対象外」という定義）。実測でも code span 内は平文で描画される。
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
// 列挙の区切り（句読点）。**空白のみは採らない**（スカッシュ既定件名の ` (#123)` と衝突するため）。
const SEP_PUNCT = String.raw`[/／,，、・･]`;
// 列挙の区切り（注記括弧）。#586 が採った「PR 番号に裁定依頼 issue 番号を添える」記法
//   `PR planning#244〔裁定依頼 planning#237〕`
// の開き括弧である（.claude/rules/traceability.md「関連番号を添える注記」）。規約は
// **`〔〕` の中の番号は先頭と同じ他リポジトリを指す**と定めるため、中を裸の `#NNN` にした形は
// 型 2（先頭だけ修飾）として検出する。短い見出し語（`裁定依頼` 等）を挟む形も同じ扱いにする。
//
// **全角丸括弧 `（` は入れない。** 日本語の地の文で従属節を開く一般的な記号であり、その直後の
// 裸 `#NNN` が**本リポジトリを指すのは正しい**（規約: 裸の `#NNN` は常に本リポジトリ）。
// 実測すると偽陽性が出る —— `feedback/20260805_sc05-07-admin-contract-gaps.md:44` の
// `planning#197（#502 由来）` の `#502` は本リポジトリの issue であり、止めてはならない。
// 対して `〔` を入れた場合の追加検出は追跡下の *.md 全件で **0 件**（＝偽陽性なし）である。
// 見出し語の長さ上限 16 は、節や文をまたいで拾わないための保険（超える形は検出しない側へ倒す）。
//
// **見出し語に修飾語（planning / AST / …）が現れたら区切りとして採らない。** 採らないと
// 採用形 `〔裁定依頼 planning#237〕` の `〔裁定依頼 planning` までを区切りと読んで
// **正しい書き方そのものを違反にしてしまう**（自己試験の負例で固定）。修飾語が現れる形は、
// 詰まっていれば正（検出不要）、空白で離れていれば型 3 の担当である。
const SEP_BRACKET =
  String.raw`〔(?:(?!planning|AST|project-planning|ai-stock-trading)[^#〔〕\n]){0,16}`;
const SEP = String.raw`[ \t]*(?:${SEP_PUNCT}|${SEP_BRACKET})[ \t]*`;
// 型 2: 修飾付き参照の直後に「区切り + 裸の #数字」が 1 個以上続く形。
const ENUM_RE = new RegExp(`(${QUALIFIED})((?:${SEP}#\\d+)+)`, 'g');
// 是正案を組み立てるときの「区切り + 裸の #数字」。**ENUM_RE と同じ区切り定義から作る**
// （2 箇所に別々の文字集合を書くと、片方だけ足したときに是正案が黙って壊れる）。
const ENUM_FIX_RE = new RegExp(String.raw`(^|(?:${SEP_PUNCT}|${SEP_BRACKET})[ \t]*)#(\d+)`, 'g');

// 型 4: フルパス形式の owner。規約（.claude/rules/traceability.md「本リポジトリでの名前空間」）が
// 定める既知の owner はただ 1 つ `endazon` である（実測: フルパス形式 30 件・URL 形式 245 件すべて）。
const KNOWN_OWNERS = ['endazon'];
// **自組織が持つリポジトリ名**。この 3 つに限って owner を検査する（上のコメントの理由）。
// `microservices-platform` は自リポジトリだが、フルパス形式で書かれること自体は規約が許すため
// 対象に含める（owner を誤れば同じく死んだリンクになる）。
const OWNED_REPOS = ['project-planning', 'ai-stock-trading', 'microservices-platform'];
// 短縮形への対応。自リポジトリは裸の `#NNN` が正（規約: 裸の #NNN は常に本リポジトリ）。
const OWNED_REPO_SHORT = {
  'project-planning': 'planning',
  'ai-stock-trading': 'AST',
  'microservices-platform': '',
};
// 直前が \w / - / `/` なら owner ではない（URL 中の `github.com/<owner>/…` を含む）。
// URL 形式の owner は本検査の対象外である（#590 で実測し、違反 0 件であることを開示したうえで
// 射程外とした）。
const OWNER_RE = new RegExp(
  String.raw`(?<![\w/-])([A-Za-z][\w.-]*)\/(${OWNED_REPOS.join('|')})#(\d+)`,
  'g'
);

// 型 3: 修飾語と番号が空白で離れた形（間に PR / issue の語が入る形も含む）。
// **自リポジトリを指す修飾語（MSP / microservices-platform）は入れない**——その直後の裸 `#NNN` は
// 本リポジトリを指しており正しい。入れると正当な記述を 22 件止める（実測）。
const SPACED_RE =
  /(?<![\w/-])(planning|AST|project-planning|ai-stock-trading)[ \t]+(?:PR[ \t]+|issue[ \t]+)?#(\d+)/g;

// フェンス行（``` / ~~~ で始まる行）。maskCode の状態遷移と unbalancedFenceLine の単一情報源。
const FENCE_LINE_RE = /^\s*(```|~~~)/;

/**
 * Markdown のコードフェンスとインラインコードを**同じ長さの空白**へ潰す。
 * 長さを保つのは、行番号・桁位置を元テキストと一致させたまま走査するためである。
 */
function maskCode(text) {
  const out = [];
  let fenced = false;
  for (const line of String(text).split('\n')) {
    if (FENCE_LINE_RE.test(line)) {
      fenced = !fenced;
      out.push(' '.repeat(line.length));
      continue;
    }
    if (fenced) {
      out.push(' '.repeat(line.length));
      continue;
    }
    // **バッククォートの本数を合わせて対応付ける**（CommonMark のコードスパン）。
    // 単一バッククォートだけを見ていた頃は、二重バッククォートのスパンが素通りして
    // 偽陽性になっていた（#507 クロス監査 Y2 の実測）。
    out.push(line.replace(/(`+)(?:(?!\1)[\s\S])*?\1/g, (m) => ' '.repeat(m.length)));
  }
  return out.join('\n');
}

/**
 * フェンスが閉じていない Markdown の、最後のフェンス行番号を返す（閉じていれば null）。
 * maskCode は行ベースのトグルなので、閉じないフェンスが 1 本あると**以降のファイル全体が
 * 検査対象外**になる。黙って見逃さないよう違反として上げる（#507 クロス監査 Y2）。
 */
function unbalancedFenceLine(text) {
  let count = 0;
  let last = 0;
  String(text).split('\n').forEach((line, i) => {
    if (FENCE_LINE_RE.test(line)) {
      count++;
      last = i + 1;
    }
  });
  return count % 2 === 1 ? last : null;
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
 * @returns {{kind: 'long'|'enum'|'spaced'|'owner'|'fence', line: number, matched: string, suggestion: string}[]}
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
    ENUM_FIX_RE.lastIndex = 0;
    const fixed = m[0].replace(ENUM_FIX_RE, (whole, pre, num) =>
      pre === '' ? whole : `${pre}${short}#${num}`
    );
    out.push({
      kind: 'enum',
      line: lineNumberAt(scan, m.index),
      matched: m[0],
      suggestion: fixed,
    });
  }

  SPACED_RE.lastIndex = 0;
  while ((m = SPACED_RE.exec(scan))) {
    const short = LONG_NAMES[m[1]] || m[1];
    out.push({
      kind: 'spaced',
      line: lineNumberAt(scan, m.index),
      matched: m[0],
      suggestion: `${short}#${m[2]}`,
    });
  }

  OWNER_RE.lastIndex = 0;
  while ((m = OWNER_RE.exec(scan))) {
    if (KNOWN_OWNERS.includes(m[1])) continue; // 規約が許すフルパス形式。
    const short = OWNED_REPO_SHORT[m[2]];
    out.push({
      kind: 'owner',
      line: lineNumberAt(scan, m.index),
      matched: m[0],
      // 規約は短縮形へ寄せることを求めるため、owner を直すのではなく短縮形を提案する
      // （自リポジトリ = microservices-platform は裸の `#NNN` が正）。
      suggestion: `${short}#${m[3]}`,
    });
  }

  // 閉じないフェンスは「検査していない範囲」を生む。Markdown モードでのみ見る
  // （コミットメッセージにフェンスの概念は無い）。
  if (opts.markdown) {
    const fenceLine = unbalancedFenceLine(src);
    if (fenceLine !== null) {
      out.push({
        kind: 'fence',
        line: fenceLine,
        matched: '(閉じていないコードフェンス)',
        suggestion: 'フェンスを閉じる（閉じないと以降のファイル全体が黙って検査対象外になる）',
      });
    }
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
      const label = {
        long: '長い表記',
        enum: '列挙形の修飾漏れ',
        spaced: '空白区切りの修飾',
        owner: 'フルパス形式の owner 誤り',
        fence: '閉じないコードフェンス',
      }[v.kind];
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

  // --- 正のケース: 型 2 の `〔〕` 注記（#586。PR #593 レビュー Y6 の変異試験が突いた死角） ---
  // 採用形は `PR planning#244〔裁定依頼 planning#237〕`。この記法を足したとき、`SEP` に `〔` が
  // 無かったため **変異 A（〔〕の中を裸にする）と変異 C（先頭を裸にする）が無検出**だった。
  // 変異ごとに 1 件ずつ、**採用形（負例）と対で**固定する。
  t('型2〔〕: 変異A（〔〕の中を裸にする）を検出する',
    kinds('planning `3e58b97` = PR planning#244〔裁定依頼 #237〕。').join() === 'enum');
  t('型2〔〕: 見出し語が無い形（〔#237〕）も検出する',
    kinds('PR planning#244〔#237〕').join() === 'enum');
  t('型2〔〕: 変異A の是正案は〔〕の中を修飾する',
    findViolations('PR planning#244〔裁定依頼 #237〕')[0].suggestion
      === 'planning#244〔裁定依頼 planning#237');
  t('型2〔〕: 先頭が AST なら〔〕の中へ AST を配る',
    findViolations('AST#196〔関連 #197〕')[0].suggestion === 'AST#196〔関連 AST#197');
  t('型2〔〕: 変異B（〔〕の中を空白で修飾）は従来どおり型 3 で上がる',
    kinds('PR planning#244〔裁定依頼 planning #237〕').join() === 'spaced');
  // 変異 C（先頭を裸にする＝`PR #244〔裁定依頼 planning#237〕`）は**本検査の対象外**である。
  // 先頭の裸 `#244` は規約上「本リポジトリの PR」を意味する正しい表記であり、意味の取り違えは
  // 構文からは判定できない（`planning#197（#502 由来）` と同型）。負例として固定し、
  // 「検出しないことが仕様」であることを明示する。
  t('負例: 変異C（先頭が裸）は検出しない —— 裸の #NNN は本リポジトリを指すのが規約',
    kinds('PR #244〔裁定依頼 planning#237〕').length === 0);
  t('負例: 採用形（〔〕の中も修飾済み）は検出しない',
    kinds('planning `3e58b97` = PR planning#244〔裁定依頼 planning#237〕。実測して確認').length === 0);
  t('負例: 全角丸括弧の直後の裸 #NNN は本リポジトリ参照なので検出しない',
    kinds('planning#197（#502 由来）は別論点である').length === 0);
  t('負例: 〔〕の中に他リポ番号が無ければ検出しない',
    kinds('planning#244〔裁定依頼〕を反映した').length === 0);
  t('負例: 〔〕の見出し語が長すぎる場合は拾わない（節をまたいで拾わないための上限）',
    kinds('planning#244〔この括弧の中はとても長い見出し語になっている場合 #237〕').length === 0);

  // --- 正のケース: 型 3（空白区切りの修飾。#507 クロス監査 R1 が実測した「第 4 の表記」） ---
  t('型3: 空白区切り（AST + 空白 + 番号）を検出', kinds('Tier 3（AST #24）で追加').join() === 'spaced');
  t('型3: 間に PR の語が入る形も検出', kinds('計画大改定（planning PR #144）').join() === 'spaced');
  t('型3: 間に issue の語が入る形も検出',
    kinds('ai-stock-trading issue #104 を参照').join() === 'spaced');
  t('型3: タブ区切りも検出', kinds('AST\t#24').join() === 'spaced');
  t('型3: 是正案は空白を詰めた短縮形', findViolations('AST #24')[0].suggestion === 'AST#24');
  t('型3: 長い表記 + 空白は短縮形へ寄せる',
    findViolations('project-planning PR #144')[0].suggestion === 'planning#144');
  // 空白区切り + 列挙は **2 段**で解ける。型 2 の先頭は空白なしの修飾付き参照を求めるため、
  // 1 回目は型 3 だけが上がる。型 3 を直すと 2 回目に型 2 が上がる。この段階性を固定する。
  t('型3: 空白区切り + 列挙は 1 回目に型 3 だけが上がる',
    kinds('AST #196/#197 を参照').join() === 'spaced');
  t('型3: 型 3 を直すと 2 回目に型 2 が上がる',
    kinds('AST#196/#197 を参照').join() === 'enum');

  // --- 正のケース: 型 4（フルパス形式の owner 誤り。#590） ---
  // **正例と負例を対で置くこと**が本型の要である —— 型 4 を足したせいで規約が許すフルパス形式
  // （`endazon/...`）や、owner でないスラッシュ（第三者リポ・見本・列挙・アンカー）まで
  // 落ちては本末転倒だからである。下の負例群がその歯止めであり、消してはならない。
  t('型4: 誤 owner（endodazon）を検出する —— #590 の実在違反',
    kinds('AST endodazon/ai-stock-trading#106（T2）').join() === 'owner');
  t('型4: project-planning 側の誤 owner も検出する',
    kinds('endazonn/project-planning#207 を参照').join() === 'owner');
  t('型4: 自リポジトリ（microservices-platform）の誤 owner も検出する',
    kinds('endodazon/microservices-platform#56 を参照').join() === 'owner');
  // 是正案の照合は `?.` で引く。**検出そのものが壊れたとき、スタックトレースではなく
  // 「どのケースが落ちたか」を名前つきで出すため**（`[0].suggestion` を素で書くと
  // 未検出時に TypeError で落ち、失敗したケース名が出ない）。#590 の変異試験で実測した。
  t('型4: 是正案は短縮形（ai-stock-trading → AST）',
    findViolations('endodazon/ai-stock-trading#106')[0]?.suggestion === 'AST#106');
  t('型4: 是正案は短縮形（project-planning → planning）',
    findViolations('endazonn/project-planning#207')[0]?.suggestion === 'planning#207');
  t('型4: 自リポジトリの是正案は裸の #NNN（規約: 裸の #NNN は常に本リポジトリ）',
    findViolations('endodazon/microservices-platform#56')[0]?.suggestion === '#56');
  t('型4: Markdown のコードスパンの中は検出しない（反例を書けること）',
    kinds('誤: `endodazon/ai-stock-trading#106`', { markdown: true }).length === 0);

  // --- 負のケース（型 4）: #590 で実測した「owner に見えて owner でない」形 ---
  t('負例4: 正しい owner のフルパス形式は通す（endazon/ai-stock-trading#106）',
    kinds('endazon/ai-stock-trading#106 を参照').length === 0);
  t('負例4: 正しい owner の自リポ参照も通す（feedback/ の実在 4 件）',
    kinds('実装 Issue: endazon/microservices-platform#56, 親 #48').length === 0);
  t('負例4: 第三者リポジトリは owner が endazon でなくても通す',
    kinds('参考: anthropics/claude-code-action#723）。').length === 0);
  t('負例4: 書式を説明するリテラルな見本 owner/repo#123 は通す',
    kinds('`issue` は `#123` か `owner/repo#123` の形式を検査する').length === 0);
  t('負例4: 列挙の区切りスラッシュ（AST#186/AST#192）を owner と読まない',
    kinds('（AST#186/AST#192/AST#194/AST#195）これらは').length === 0);
  t('負例4: Markdown のアンカーリンク（.md#2）を owner 付き参照と読まない',
    kinds('[作業仕様書 §2](../specs/20260804_issue-490_spa-router-shell.md#2-ルートパス)').length === 0);
  t('負例4: URL 形式は型 4 の対象外（#590 で射程外と開示。owner は / の直後で後読みが効く）',
    kinds('https://github.com/endodazon/project-planning#50').length === 0);

  // --- 負のケース: 偽陽性を出してはならない ---
  // **自リポジトリの修飾語は型 3 に含めない。** 裸の #NNN が本リポジトリを指すのは正しい。
  t('負例: MSP + 空白 + 番号（自リポジトリの修飾語）は検出しない',
    kinds('本 issue は MSP #283 である').length === 0);
  t('負例: microservices-platform + 空白 + 番号も検出しない',
    kinds('microservices-platform #232 と同根').length === 0);
  t('負例: 修飾語と番号の間に助詞があれば型 3 に掛からない',
    kinds('AST は #24 で追跡している').length === 0);
  t('負例: 語の一部（FAST / planning-kit）は検出しない',
    kinds('FAST #24').length === 0 && kinds('planning-kit #1').length === 0);
  t('負例: 修飾なしの「PR #123」「issue #454」は自リポ参照なので検出しない',
    kinds('PR #123 と issue #454 を参照').length === 0);
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

  // --- Y2（#507 クロス監査）: コードスパン除外「実装」の穴。正例・負例を対で固定する ---
  t('md: 二重バッククォートのコードスパンも潰す（偽陽性を出さない）',
    kinds('反例は ``planning#146 / #149`` である', { markdown: true }).length === 0);
  t('md: 二重バッククォートの中に単一バッククォートがあっても潰す',
    kinds('`` 誤: `planning#146 / #149 / #160` `` を参照', { markdown: true }).length === 0);
  t('md: 行中の三重バッククォートのスパンも潰す',
    kinds('参照は ```project-planning#50``` である', { markdown: true }).length === 0);
  t('md: 二重バッククォートの**外**は従来どおり検出する（潰しすぎていない）',
    kinds('``ok`` project-planning#50', { markdown: true }).join() === 'long');
  t('md: 行頭の ``` はスパンではなくフェンス開始として扱う（CommonMark）', (() => {
    const v = findViolations('```project-planning#50```\n', { markdown: true });
    return v.length === 1 && v[0].kind === 'fence';
  })());
  t('md: 閉じないフェンスは fence 違反として上げる（黙って盲目化しない）', (() => {
    const v = findViolations('前文\n```console\n$ echo x\n\n本文 project-planning#50\n', { markdown: true });
    return v.length === 1 && v[0].kind === 'fence' && v[0].line === 2;
  })());
  t('md: 閉じたフェンスなら fence 違反を出さない',
    kinds('前文\n```console\n$ echo x\n```\n後文\n', { markdown: true }).length === 0);
  t('非 md モードでは fence 判定をしない（コミットメッセージにフェンスの概念が無い）',
    kinds('```\nx\n').length === 0);
  t('unbalancedFenceLine: 偶数なら null・奇数なら最後のフェンス行',
    unbalancedFenceLine('a\n```\nb\n```\nc') === null && unbalancedFenceLine('a\n```\nb') === 2);

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
  unbalancedFenceLine,
  trackedMarkdown,
  selfTest,
  LONG_RE,
  ENUM_RE,
  SPACED_RE,
  OWNER_RE,
  SHORT_NAMES,
  LONG_NAMES,
  KNOWN_OWNERS,
  OWNED_REPOS,
  SEP,
};
