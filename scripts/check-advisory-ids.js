#!/usr/bin/env node
'use strict';
/*
 * check-advisory-ids.js
 * NFR / issue #2479 / IADR-0690: 追跡下のファイルの本文に出る **GitHub の advisory の識別子
 * （`GHSA-` で始まるもの）** は、**公開の第三者 advisory の allowlist にあるものだけ**を許す。
 * それ以外は 1 件でも終了コード 1。
 *
 * なぜ allowlist だけなのか:
 *   本リポジトリは公開である。非公開の指摘の識別子を公開の場に置かない（利用者裁定 2026-11-06）。
 *   検査器に非公開の識別子（やその派生物）を書くと、検査器そのものが漏えいになる。
 *   「許すものだけを列挙し、それ以外を落とす」形なら、検査器は非公開の識別子を一切持たない。
 *   **ハッシュ照合は入れない**（利用者裁定）。規則は取引ユニット（AST）の同名の検査器とそろえる。
 *
 * 照合の規則:
 *   - 完全形（前置きの後に英数字 4 文字の区画が 3 つ）は allowlist と完全一致するときだけ許す。
 *   - 2 区画の形（先頭の 2 区画だけ）は、allowlist の完全形の先頭 2 区画と一致するときだけ許す
 *     （先頭の区画だけで照合すると、2 区画目が何であっても通ってしまう）。
 *   - 短縮形（先頭の区画だけで、後ろに区画が続かない）は、allowlist の先頭の区画と一致するときだけ許す。
 *   - 大文字小文字は区別しない（小文字の前置きも拾う）。
 *   - `GHSA-...` のような例示（英数字 4 文字が続かないもの）は識別子ではないので拾わない。
 *
 * 捕まえないもの（残余。IADR-0690）:
 *   前置きの無い断片（識別子の一部を含む名前・接頭辞など）。書き手とレビューで守る。
 *
 * allowlist に足してよいのは、**公開済みの第三者 advisory（依存パッケージの既知脆弱性）だけ**である。
 * 自リポジトリ・取引ユニットの非公開の指摘は足さず、中立表記（「非公開の指摘（MSP）」など）で書く。
 *
 * 走査対象は `git ls-files` の全ファイル（拡張子で絞らない。submodule の中は出ない）。NUL を含む
 * ファイルはバイナリとして飛ばす。`git ls-files` が失敗したとき・走査件数が下限を下回ったときは
 * 終了コード 1（0 件走査で静かに緑にしない。check-nul-bytes.js / IADR-0247 と同じ作法）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 使い方:
 *   node scripts/check-advisory-ids.js
 *   node scripts/check-advisory-ids.js --self-test
 */
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

// 走査母集合を git ls-files から引くので、**未追跡ファイルは対象外**である。
// 作業ツリーに未コミットの変更があると CI の結果と一致し得ないため、その旨を警告する（check-nul-bytes.js と同じ）。
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
 * 公開の第三者 advisory（依存パッケージの既知脆弱性。dependabot・依存の CVE の文脈で引いたもの）。
 * **12 件ちょうど。** 足すときは理由を添え、自己試験と `scripts.repo.test.js` の件数も直す。
 */
const ALLOWLIST = [
  'GHSA-2fmv-rv2c-59x7',
  'GHSA-5xrq-8626-4rwp',
  'GHSA-67mh-4wv8-2f99',
  'GHSA-69fq-xp46-6x23',
  'GHSA-7hqp-7c5m-4vq6',
  'GHSA-846f-f764-76mx',
  'GHSA-c399-9853-6jmj',
  'GHSA-fx2h-pf6j-xcff',
  'GHSA-jjmj-jmhj-qwj2',
  'GHSA-q939-rpr3-3284',
  'GHSA-r955-8q5j-3h83',
  'GHSA-v5pm-xwqc-g5wc',
];

/** 拾う形。完全形 → 2 区画の形 → 短縮形の順に、区画が続く限り長く取る。 */
const ID_SOURCE = 'GHSA-([0-9a-z]{4})(?:-([0-9a-z]{4})(?:-([0-9a-z]{4}))?)?(?![0-9a-z])';

// 0 件走査で静かに緑にしない（check-nul-bytes.js と同じ下限）。
const MIN_SCANNED = 500;

function buildAllow(allowlist = ALLOWLIST) {
  const full = new Set(allowlist.map((s) => s.toLowerCase()));
  const two = new Set(allowlist.map((s) => s.toLowerCase().split('-').slice(0, 3).join('-')));
  const head = new Set(allowlist.map((s) => s.toLowerCase().split('-')[1]));
  return { full, two, head };
}

/** 1 ファイル分の違反を返す（行番号付き）。 */
function findViolations(text, allowlist = ALLOWLIST) {
  const allow = buildAllow(allowlist);
  const hits = [];
  String(text).split(/\r?\n/).forEach((line, i) => {
    const re = new RegExp(ID_SOURCE, 'gi');
    let m;
    while ((m = re.exec(line)) !== null) {
      const id = m[0].toLowerCase();
      const ok = m[3]
        ? allow.full.has(id)
        : m[2]
          ? allow.two.has(id)
          : allow.head.has(m[1].toLowerCase());
      if (!ok) hits.push({ line: i + 1, id: m[0] });
    }
  });
  return hits;
}

/** 追跡下のファイル一覧（submodule は git ls-files に出ないので自然に対象外）。 */
function trackedFiles(root = REPO_ROOT) {
  return execFileSync('git', ['-C', root, 'ls-files', '-z'], { encoding: 'utf8', maxBuffer: 1 << 28 })
    .split('\0').filter(Boolean);
}

/** 走査本体。{violations, scanned} を返す。 */
function scan(root = REPO_ROOT, files = null, allowlist = ALLOWLIST) {
  const list = files || trackedFiles(root);
  const violations = [];
  let scanned = 0;
  for (const rel of list) {
    let buf;
    try {
      buf = fs.readFileSync(path.join(root, rel));
    } catch {
      continue; // 読めない（削除済み・submodule の口・シンボリックリンク等）は対象外
    }
    if (buf.includes(0)) continue; // バイナリ
    scanned += 1;
    for (const hit of findViolations(buf.toString('utf8'), allowlist)) violations.push({ rel, ...hit });
  }
  return { violations, scanned };
}

/** 走査件数が下限を下回ったか（＝走査が空振りしている疑い）。 */
function isScanTooSmall(scanned, min = MIN_SCANNED) {
  return scanned < min;
}

// --- 自己試験 -------------------------------------------------------------------

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });

  // 🔴 陰性対照の架空の識別子は**ここで組み立てる**（同じ文字の繰り返しで、実在しない形が明らかなもの）。
  // 字面で書くと、実ツリーの検査を自分で赤にする。
  const fakeHead = 'w'.repeat(4);
  const fakeFull = ['GHSA', fakeHead, fakeHead, fakeHead].join('-');
  const fakeShort = ['GHSA', fakeHead].join('-');
  const [, head0] = ALLOWLIST[0].split('-');
  const two2 = ALLOWLIST[2].split('-').slice(0, 3).join('-');
  const head2 = ALLOWLIST[2].split('-')[1];

  t('ALLOWLIST は 12 件ちょうど・重複なし', ALLOWLIST.length === 12 && new Set(ALLOWLIST).size === 12, ALLOWLIST.length);
  t('ALLOWLIST はすべて GHSA の完全形', ALLOWLIST.every((s) => /^GHSA-[0-9a-z]{4}-[0-9a-z]{4}-[0-9a-z]{4}$/.test(s)));

  t('allowlist の完全形を許す', findViolations(`依存の既知脆弱性（${ALLOWLIST[0]}）`).length === 0);
  t('allowlist の完全形（小文字・URL の中）を許す',
    findViolations(`https://github.com/advisories/${ALLOWLIST[0].toLowerCase()}`).length === 0);
  t('allowlist の短縮形を許す', findViolations(`# GHSA-${head0}: 依存の更新`).length === 0);
  t('allowlist の 2 区画の形を許す', findViolations(`（${two2}）・${two2.toLowerCase()}_topic`).length === 0);

  {
    const v = findViolations(`a\n# Refs ${fakeFull}\n`);
    t('★ 陰性対照: allowlist に無い架空の完全形を検出し、行番号を返す',
      v.length === 1 && v[0].line === 2 && v[0].id === fakeFull, v);
  }
  t('★ 陰性対照: allowlist に無い架空の短縮形を検出する', findViolations(`# ${fakeShort}: 説明`).length === 1);
  t('★ 陰性対照: ファイル名の形（日付_識別子_主題）も検出する',
    findViolations(`specs/20990101_${fakeShort}_topic.md`).length === 1);
  t('★ 陰性対照: 先頭の区画だけ allowlist と同じでも、完全形が違えば検出する',
    findViolations(['GHSA', head0, fakeHead, fakeHead].join('-')).length === 1);
  {
    const fakeTwo = ['GHSA', head2, fakeHead].join('-');
    const v = findViolations(`# Refs ${fakeTwo}: 説明`);
    t('★ 陰性対照: 先頭の区画が allowlist と同じで 2 区画目が違う 2 区画の形を検出する',
      v.length === 1 && v[0].id === fakeTwo, v);
    t('★ 陰性対照: 別の allowlist の 2 区画目と組み合わせた 2 区画の形も検出する',
      findViolations(['GHSA', head2, ALLOWLIST[3].split('-')[2]].join('-')).length === 1);
    t('★ 陰性対照: 3 区画目にならない後続（短い語）でも 2 区画の形として照合する',
      findViolations(`${fakeTwo}-ab`).length === 1);
  }
  t('小文字の前置きも検出する', findViolations(fakeFull.toLowerCase()).length === 1);
  t('識別子でない例示（GHSA-...）は検出しない',
    findViolations('URL(https://github.com/advisories/GHSA-...)を含む').length === 0);

  {
    const os = require('os');
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'advisory-ids-selftest-'));
    try {
      fs.writeFileSync(path.join(dir, 'ok.md'), `${ALLOWLIST[1]}\n`);
      fs.writeFileSync(path.join(dir, 'ng.sh'), `# x\n# ${fakeFull}\n`);
      fs.writeFileSync(path.join(dir, 'bin.dat'), Buffer.concat([Buffer.from(fakeFull), Buffer.from([0x00])]));
      const r = scan(dir, ['ok.md', 'ng.sh', 'bin.dat', 'missing.md']);
      t('scan: 違反は ng.sh の 2 行目だけ（バイナリ・存在しないファイルは対象外）',
        r.violations.length === 1 && r.violations[0].rel === 'ng.sh' && r.violations[0].line === 2 && r.scanned === 2, r);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  }

  t('★ isScanTooSmall: 0 件走査は fail 側（静かに緑にしない）', isScanTooSmall(0) === true);
  t('★ isScanTooSmall: 下限ちょうど未満は fail 側（境界）', isScanTooSmall(MIN_SCANNED - 1) === true);
  t('isScanTooSmall: 下限以上は通す', isScanTooSmall(MIN_SCANNED) === false);

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed += 1; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[check-advisory-ids] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-advisory-ids] 自己試験 ${cases.length} 件 OK。`);
}

// --- 実行 -----------------------------------------------------------------------

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }

  warnIfResultMayDifferFromCi('check-advisory-ids.js', MODE.TRACKED);

  let result;
  try {
    result = scan();
  } catch (e) {
    console.error(`[check-advisory-ids] 追跡下のファイル一覧を取得できませんでした（git ls-files）: ${e.message}`);
    process.exit(1);
  }
  const { violations, scanned } = result;
  console.log(`[check-advisory-ids] 追跡下 ${scanned} 件を走査`);

  if (isScanTooSmall(scanned)) {
    console.error(`[check-advisory-ids] 走査件数が ${scanned} 件しかありません（下限 ${MIN_SCANNED}）。`
      + ' 走査が空振りしているか、実行位置がリポジトリ外です。');
    process.exit(1);
  }

  if (violations.length === 0) {
    console.log(`[check-advisory-ids] OK: advisory の識別子は公開の第三者 allowlist（${ALLOWLIST.length} 件）にあるものだけです。`);
    process.exit(0);
  }

  console.error(`[check-advisory-ids] allowlist に無い advisory の識別子が ${violations.length} 件あります:`);
  for (const v of violations) console.error(`    ${v.rel}:${v.line}  ${v.id}`);
  console.error('');
  console.error('非公開の指摘の識別子は公開のファイルへ書かず、「非公開の指摘（MSP）」などの中立表記で書いてください（IADR-0690）。');
  console.error('公開済みの第三者 advisory（依存パッケージの既知脆弱性）なら、理由を添えて ALLOWLIST に足してください。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = { ALLOWLIST, ID_SOURCE, MIN_SCANNED, findViolations, trackedFiles, scan, isScanTooSmall };
