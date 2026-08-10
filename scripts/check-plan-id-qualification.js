#!/usr/bin/env node
'use strict';
/*
 * check-plan-id-qualification.js
 * NFR / issue #576: 他プロジェクト（AST）の**計画 ID / ADR ID** の修飾が規約どおりかを機械検査する。
 *
 * 規約は .claude/rules/traceability.md「複数プロジェクトを跨ぐ場合の ID 修飾」。
 * 書式は `<PROJ>/<ID>`（例 `AST/FR-17`）であり、**空白区切り（`AST FR-17`）は規約外**である。
 *
 * **`check-cross-repo-refs.js` とは対象が違う**（#507 / #590 とは別物）:
 *   - `check-cross-repo-refs.js` … **issue / PR 番号**（`AST#24`）の表記
 *   - 本スクリプト               … **計画 ID / ADR ID**（`AST/FR-17`）の修飾
 * 同じ `AST` で始まるので混同しやすいが、規約の別々の節が定めており母集合も別である。
 * **ファイルを分けたのは関心の分離のためでもある**——`check-cross-repo-refs.js` は
 * 「番号の表記」に閉じた道具であり、そこへ ID 修飾を混ぜると責務が割れる。
 *
 * 検出する型（**件数はここに書かない**。列挙そのものが単一情報源。#590 の教訓）:
 *   型 A（空白区切り）: `AST FR-17` / `AST IADR-0048` / **`AST [[IADR-0080]]`** / TAB 区切り。
 *                       規約書式 `AST/FR-17` と混在している。
 *                       **8 番号が本リポジトリの同番号 IADR と衝突していた**（#570 のクロス監査）。
 *                       **wiki リンク形は実際に本リポの Headlamp IADR へ張り付いていた**（#576）。
 *
 * **検出しないこと**（本検査は網羅ではない。#576 で実測して開示した）:
 *   - **型 B（AST 文脈で裸の計画 ID）は検出しない。** issue #576 は「同じコメント塊が `AST` を
 *     含むのに ID が裸」という近傍規則を提案しているが、**偽陽性が避けられない**ため採らない。
 *     `.claude/rules/traceability.md` は「AST の `FR-17`（当時 MSP は FR-15 まで）」と
 *     **誤帰属そのものを説明する地の文**を持ち、`docs/adr/IADR-0071` のように MSP の ID と
 *     AST の ID が同じ段落へ混在する文書も多い。偽陽性を 1 件でも出すと検査は外される
 *     （IADR-0140 決定 3 が裸 `#NNN` の一律検出を同じ理由で棄却している）。
 *     型 B は #576 で是正し、クロス監査の指摘後に `docs/adr/IADR-0071/0072/0075`・
 *     `src/platform/frontend/.../features/index.ts`・`BffEndpointCompositionTests.cs` まで
 *     引き直して 0 件にした（**最初の走査では 32 occurrence を見落としていた**）。
 *     **再混入は人と AI が防ぐ**。
 *   - **列挙の後続 ID**（`AST/SC-02/SC-03` の `SC-03`）は検出しない。**この型は実在した**
 *     ——#576 の一括置換が実際に 4 件作り、手で直した。近傍規則になるため型 B と同じ判断。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-plan-id-qualification.js              # 追跡下の全ファイルを走査
 *   node scripts/check-plan-id-qualification.js --self-test  # 検査ロジック自体の自己試験
 *   node scripts/check-plan-id-qualification.js <file>...    # ファイル指定
 *
 * CI への載せ方（.github/workflows/ は編集不可のため既存の呼び出し口へ相乗りする。IADR-0140 決定 2）:
 *   - scripts/scripts.repo.test.js から --self-test ＋ 実データ走査（ci.yml の scripts-tests ジョブ）
 */
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');

// 他プロジェクトの短縮名。現状 AST（ai-stock-trading）のみ。
const PROJECT_PREFIXES = ['AST'];
// 計画 ID / 実装 ADR ID の種別。
const ID_KINDS = ['IADR', 'ADR', 'FR', 'UC', 'SC'];

// 型 A: `AST` と ID が空白（半角/全角）で離れた形。
// **直前が \w / - / `/` なら別物**（`AST/FR-17` は規約どおり、`FAST FR-1` は別語）。
// **区切りは空白だけではない。** `AST [[IADR-0080]]` のように wiki リンク括弧・バッククォート・
// 全角括弧が挟まる形が実在した（#576 のクロス監査と AI レビューが実測）。**しかも本リポジトリに
// 同番号の IADR が実在するため、wiki リンクが Headlamp の文書へ実際に張り付いていた** ——
// 表記ゆれではなく生きた誤帰属である。TAB も許す（YAML / コードで現実的）。
// 当初は `[ 　]+` ＋ ID 直結しか見ておらず、**軸を 1 本で終わらせた**ため丸ごと落ちていた。
const ID_LEAD = String.raw`[\[\`*_（(「『]{0,2}`;
const SPACED_ID_RE = new RegExp(
  String.raw`(?<![\w/-])(${PROJECT_PREFIXES.join('|')})[ 　\t]+${ID_LEAD}((?:${ID_KINDS.join('|')})-\d+)`,
  'g'
);

/** 走査から外すパス。submodule と、生成物・記録（下記の理由）。 */
const EXCLUDED_PATH_RE =
  /^(planning\/|src\/ai-stock-trading\/|CHANGELOG\.md$|docs\/specs\/|feedback\/|docs\/superpowers\/)/;

/** 文字オフセットから 1 始まりの行番号を返す。 */
function lineNumberAt(text, index) {
  let n = 1;
  for (let i = 0; i < index && i < text.length; i++) if (text[i] === '\n') n++;
  return n;
}

// Markdown のコードスパン／フェンスを潰す関数は `check-cross-repo-refs.js` が持っているので
// **再実装せず借りる**（2 箇所に別々の実装を置くと、片方だけ直したとき挙動が割れる。同スクリプトは
// 二重バッククォート・閉じないフェンスの穴を実測して塞いだ経緯がある）。
const { maskCode } = require('./check-cross-repo-refs.js');

/**
 * 1 つのテキストから違反を集める。
 * @param {string} text
 * @param {{markdown?: boolean}} opts markdown=true でコードスパン／フェンスを対象外にする。
 * @returns {{kind: 'spaced-id', line: number, matched: string, suggestion: string}[]}
 *
 * **`.md` では引用を対象外にする**（[[IADR-0140]] 決定 1 と同じ「literal な引用は表記規約の
 * 対象外」という定義）。これが無いと、**規約自身が反例として書く `AST FR-17` を違反にしてしまい、
 * 規約を書けない検査になる**（実測した）。
 */
function findPlanIdViolations(text, opts = {}) {
  const raw = String(text == null ? '' : text);
  // 潰した文字は同じ長さの空白なので、行番号・桁位置は元テキストと一致したままである。
  const src = opts.markdown ? maskCode(raw) : raw;
  const out = [];
  SPACED_ID_RE.lastIndex = 0;
  let m;
  while ((m = SPACED_ID_RE.exec(src))) {
    out.push({
      kind: 'spaced-id',
      line: lineNumberAt(src, m.index),
      matched: m[0],
      suggestion: `${m[1]}/${m[2]}`,
    });
  }
  out.sort((a, b) => a.line - b.line || a.matched.localeCompare(b.matched));
  return out;
}

/**
 * git 管理下の全ファイル（submodule・生成物・記録を除く）を列挙する。git を使えなければ null。
 * **拡張子で絞らない**——#570 は `--include` で `.sh` / `.js` を落として取りこぼした（規則 3）。
 */
function trackedFiles(root = REPO_ROOT) {
  let raw;
  try {
    raw = execFileSync('git', ['-C', root, 'ls-files'], {
      encoding: 'utf8',
      maxBuffer: 64 * 1024 * 1024,
    });
  } catch (e) {
    return null;
  }
  // **検査器自身は走査しない。** ヘッダの説明と自己試験のフィクスチャが「検出対象の文字列そのもの」
  // を持つので、含めると必ず自分で落ちる（[[IADR-0140]] 決定 4 が `.js` へ広げない理由として
  // 挙げていた当の型。#576 の CI で実際に発火した）。
  // **除外リストではなく `__filename` から導出する**ので腐らない —— ファイル名を変えても追随する。
  // **ローカルでは気づけなかった**: 新設直後は untracked で `git ls-files` に載らず、
  // コミットして追跡下に入った瞬間に初めて自分を走査した。
  const selfPath = path.relative(root, __filename).split(path.sep).join('/');
  return raw
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean)
    .filter((p) => p !== selfPath && !EXCLUDED_PATH_RE.test(p));
}

/** ファイル群を検査し、{file, violations} の配列を返す。 */
function checkFiles(files, root = REPO_ROOT) {
  const report = [];
  for (const rel of files) {
    let text;
    try {
      text = fs.readFileSync(path.isAbsolute(rel) ? rel : path.join(root, rel), 'utf8');
    } catch (e) {
      continue; // バイナリ・削除済みは飛ばす
    }
    const violations = findPlanIdViolations(text, { markdown: /\.md$/i.test(rel) });
    if (violations.length) report.push({ file: rel, violations });
  }
  return report;
}

function formatReport(report) {
  const lines = [];
  for (const r of report) {
    lines.push(`\n  ${r.file}`);
    for (const v of r.violations) {
      lines.push(`    ${r.file}:${v.line}  [空白区切りの ID 修飾] ${v.matched}  →  ${v.suggestion}`);
    }
  }
  return lines.join('\n');
}

// --- 自己試験 -------------------------------------------------------------------
//
// 正のケース（検出すべき）と負のケース（検出してはならない）を**対で**固定する。
// 負のケースが本体である——偽陽性を 1 件でも出すと、正当な記述が止まり検査が外される。

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });
  const kinds = (text) => findPlanIdViolations(text).map((v) => v.kind);

  // --- 正のケース: 型 A（空白区切り） ---
  t('型A: AST FR-17 を検出', kinds('AST FR-17 は取引前提条件である').join() === 'spaced-id');
  t('型A: AST IADR-0048 を検出', kinds('（AST IADR-0048 決定3）').join() === 'spaced-id');
  t('型A: AST ADR-0006 も検出', kinds('AST ADR-0006（Hetzner k3s）').join() === 'spaced-id');
  t('型A: AST SC-02 も検出', kinds('AST SC-02 監視銘柄').join() === 'spaced-id');
  t('型A: AST UC-06 も検出', kinds('AST UC-06 のフロー').join() === 'spaced-id');
  t('型A: 全角空白でも検出', kinds('AST　FR-17').join() === 'spaced-id');
  t('型A: 複数空白でも検出', kinds('AST   FR-17').join() === 'spaced-id');
  t('型A: 是正案は / 形', findPlanIdViolations('AST FR-17')[0]?.suggestion === 'AST/FR-17');
  t('型A: IADR の是正案', findPlanIdViolations('AST IADR-0048')[0]?.suggestion === 'AST/IADR-0048');
  t('型A: 1 行に 2 件あれば 2 件返す', findPlanIdViolations('AST FR-17 と AST SC-01').length === 2);
  t('型A: 行番号を返す', (() => {
    const v = findPlanIdViolations('1 行目\n2 行目\nAST FR-17');
    return v.length === 1 && v[0].line === 3;
  })());

  // **区切りに記号が挟まる形**（#576 のクロス監査 / AI レビューが実測。当初の走査式が丸ごと落とした）。
  // `AST [[IADR-0080]]` は**本リポジトリの実在 IADR（Headlamp）へ wiki リンクが張り付いていた** ——
  // 表記ゆれではなく生きた誤帰属だったので、正例として常設する。
  t('型A: wiki リンク形 AST [[IADR-0080]] を検出', kinds('AST [[IADR-0080]]（AST フロント）').join() === 'spaced-id');
  t('型A: wiki リンク形の是正案は素の AST/ 形',
    findPlanIdViolations('AST [[IADR-0084]]')[0]?.suggestion === 'AST/IADR-0084');
  t('型A: TAB 区切りも検出（YAML / コードで現実的）', kinds('AST\tFR-17').join() === 'spaced-id');
  t('型A: バッククォート挟みも検出', kinds('AST `FR-17` を参照').join() === 'spaced-id');
  t('型A: 全角括弧挟みも検出', kinds('AST （FR-17）').join() === 'spaced-id');

  // --- 負のケース: 偽陽性を出してはならない ---
  t('負例: 規約どおりの AST/FR-17 は検出しない', kinds('AST/FR-17 と AST/SC-01 は別採番').length === 0);
  t('負例: AST/IADR-0048 も検出しない', kinds('（AST/IADR-0048 決定3）').length === 0);
  t('負例: MSP の裸 ID は検出しない（本リポジトリの名前空間）',
    kinds('FR-17 は知識グラフ探索である').length === 0);
  t('負例: 語の一部（FAST / LAST）は検出しない',
    kinds('FAST FR-1').length === 0 && kinds('LAST SC-02').length === 0);
  t('負例: AST の直後が ID でなければ検出しない', kinds('AST の設定画面').length === 0);
  t('負例: AST#24（issue 番号）は本検査の対象外（check-cross-repo-refs.js が見る）',
    kinds('AST#24 で追跡している').length === 0);
  t('負例: AST 単独は検出しない', kinds('AST ユニットは submodule である').length === 0);
  t('負例: ハイフンの無い語（AST FRAGMENT）は検出しない', kinds('AST FRAGMENT').length === 0);
  // 直前が `/` の `AST` はパスの一部（`src/AST` 等）なので検出しない。
  // **この負例は自己試験を書いたとき主張を取り違えていた**——「検出する」と書いて落ちた。
  // 実挙動（後読みで除外）が正しく、`check-cross-repo-refs.js` の owner 判定とも揃っている。
  t('負例: 直前が / なら別物（パスの一部）', kinds('src/AST FR-17').length === 0);
  t('負例: 直前がハイフンでも別語', kinds('non-AST FR-17').length === 0);

  // --- Markdown モード: 引用は対象外（IADR-0140 決定 1 と同じ定義） ---
  // **これが無いと規約を書けない検査になる**——規約自身が反例として `AST FR-17` を書くため。
  // 実際に `.claude/rules/traceability.md` と本スクリプトのエラーメッセージで発火した（#576）。
  const mdKinds = (text) => findPlanIdViolations(text, { markdown: true }).map((v) => v.kind);
  t('md: インラインコードの反例は検出しない（規約が反例を書けること）',
    mdKinds('誤: `AST' + ' FR-17`。正: `AST/FR-17`。').length === 0);
  t('md: コードフェンスの中も検出しない',
    mdKinds('前文\n```\nAST' + ' FR-17\n```\n後文').length === 0);
  t('md: コードスパンの外は検出する（潰しすぎていない）',
    mdKinds('`ok` AST' + ' FR-17 `ok`').join() === 'spaced-id');
  t('md: 潰しても行番号がずれない', (() => {
    const v = findPlanIdViolations('1 行目 `code`\n2 行目\nAST' + ' FR-17', { markdown: true });
    return v.length === 1 && v[0].line === 3;
  })());
  t('非 md モードでは潰さない（コード・設定のコメントは引用ではない）',
    kinds('`AST' + ' FR-17`').join() === 'spaced-id');
  t('負例: 数字の無い ID 種別は検出しない', kinds('AST FR-').length === 0);

  // **自己除外が外れていないこと。** 外すと検査器自身のヘッダと自己試験フィクスチャで必ず落ちる。
  // ローカルでは新設直後 untracked のため気づけず、**CI で初めて発火した**（#576）ので常設する。
  t('自己除外: trackedFiles に検査器自身が含まれない', (() => {
    const files = trackedFiles();
    if (files === null) return true; // git を使えない環境は対象外（fail-open と揃える）
    // **リテラルで書かない。** ファイル名を変えたとき、実装が壊れていてもテストが常に真になる
    // （vacuous truth）。導出したうえで「自ファイルは追跡下に在る」ことも併せて主張する
    // ——両方が揃って初めて「追跡下だが除外されている」を固定できる。
    const self = path.relative(REPO_ROOT, __filename).split(path.sep).join('/');
    const tracked = execFileSync('git', ['-C', REPO_ROOT, 'ls-files', '--', self], {
      encoding: 'utf8',
    }).trim();
    return tracked !== '' && !files.includes(self);
  })());

  // --- 実ファイル走査の経路（fixture） ---
  {
    const os = require('os');
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'planid-selftest-'));
    fs.writeFileSync(path.join(dir, 'ng.md'), '# x\n\n（AST IADR-0048 決定3）と AST FR-17。\n');
    fs.writeFileSync(path.join(dir, 'ok.md'), '# y\n\nAST/IADR-0048 と AST/FR-17 と FR-14。\n');
    const rep = checkFiles(['ng.md', 'ok.md'], dir);
    t('checkFiles: 違反ファイルだけを報告する', rep.length === 1 && rep[0].file === 'ng.md',
      rep.map((r) => r.file));
    t('checkFiles: 1 ファイル内の 2 件を両方報告する',
      rep[0] && rep[0].violations.length === 2, rep[0] && rep[0].violations);
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
    console.error(`[check-plan-id-qualification] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-plan-id-qualification] 自己試験 ${cases.length} 件 all passed。`);
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
    files = trackedFiles();
    if (files === null) {
      // git を使えない環境（tarball 展開等）ではスキップする（fail-open）。
      // 黙って 0 件検査へ落ちたことが分かるよう理由を出す。
      console.error('[check-plan-id-qualification] git ls-files を実行できないため走査をスキップした。');
      process.exit(0);
    }
  }
  // #664 / IADR-0130 の作法: **0 件走査で緑を返さない**（fail-closed）。
  // 走査対象を 1 件も拾えないのは「検査しているつもりで何も見ていない」状態であり、
  // 退行を止めているという記録だけが残る（#592 の初版がこれで、変異試験で辛うじて捕まえた）。
  if (files.length === 0) {
    console.error('[check-plan-id-qualification] 走査対象のファイルを 1 件も見つけられませんでした。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    process.exit(1);
  }
  const report = checkFiles(files);
  const total = report.reduce((n, r) => n + r.violations.length, 0);
  if (total === 0) {
    console.log(`[check-plan-id-qualification] OK: ${files.length} 件に他プロジェクト ID の修飾違反はありません。`);
    process.exit(0);
  }
  console.error(`[check-plan-id-qualification] 他プロジェクト ID の修飾違反 ${total} 件を検出しました:`);
  console.error(formatReport(report));
  console.error(
    '\n規約（.claude/rules/traceability.md「複数プロジェクトを跨ぐ場合の ID 修飾」）: ' +
      '他プロジェクトの計画 ID は `<PROJ>/<ID>`（例 `AST/FR-17`）で書く。' +
      '空白区切り（`AST' + ' FR-17`）は規約外である。\n'
  );
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  findPlanIdViolations,
  checkFiles,
  formatReport,
  trackedFiles,
  selfTest,
  SPACED_ID_RE,
  PROJECT_PREFIXES,
  ID_KINDS,
  EXCLUDED_PATH_RE,
};
