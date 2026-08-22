#!/usr/bin/env node
'use strict';
/*
 * check-nul-bytes.js
 * NFR / issue #956: 追跡下のファイルへ **生の NUL バイト（0x00）** が混入していないか検査する。
 *
 * 背景（実際に 3 回起きた）:
 *   #900 の check-coverage-floor.js → #919 の check-backend-libraries.js →
 *   **その是正を記録した作業仕様書**（同一 PR 内で再発）。
 *   いずれも「エスケープ表記（バックスラッシュ + u0000）と書いたつもりが実バイトになる」という同じ機序である。
 *
 * なぜ検査するか:
 *   生の NUL が入ると **git と grep がファイルをバイナリ扱いする**。
 *     - `git diff` が中身を出さない（Binary files differ）→ **レビューできない**
 *     - `grep` が行番号を出さない（Binary file … matches）→ **走査・母集合の引き直しが機能しない**
 *   🔴 **3 回とも実行時挙動は正常で、テストも検査器も緑だった。** 気付いたのは grep が
 *   `Binary file … matches` を返したときで、偶然に近い。**混入したまま気付かずマージされ得る。**
 *
 * 🔴 検査範囲は「追跡下すべて」。拡張子の許可リストにしない（#956 決定 1）:
 *   許可リスト方式は **新しいテキスト拡張子が黙って検査対象から外れる**。実測でも
 *   .gitkeep / Dockerfile / .hcl / .po / .template など 126 件が「テキスト系候補」から漏れていた。
 *   **この失敗は無音である。** 追跡下すべてなら、将来バイナリを追加したときに落ちるが、
 *   **その失敗は目に見える**（下の BINARY_EXTENSIONS へ足す判断を人がする）。
 *   **無音の見逃しより、目に見える誤検出を選ぶ**（IADR-0138 が構造的な根拠を選んだのと同じ判断）。
 *
 *   着手前の実測: 追跡下 1995 件 / NUL を含むもの **0 件** / バイナリ拡張子 **0 件**
 *   （png/jpg/pdf/zip/dll/exe/woff/ttf ほかいずれも 0）。**このリポジトリは追跡下が全部テキストである。**
 *
 * 🔴 検査器はバイトで読む。復号しない（#956 決定 2）:
 *   **自分が検出したいもので自分が壊れないようにする。** NUL を含むファイルを 'utf8' で読むと
 *   内容が化け、実装によっては例外にもなる。Buffer で読み、バイト列を走査するだけにする。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-nul-bytes.js
 *   node scripts/check-nul-bytes.js --self-test
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
 * 正当に NUL を含み得るバイナリの拡張子。**現時点では空である**（実測: 追跡下 0 件）。
 *
 * 🔴 **空であること自体が正解の状態である**（未整備ではない）。バイナリを追跡下へ足して
 * 本検査が落ちたら、**そのときに人が判断してここへ足す**。黙って除外を広げないための形であり、
 * 「あとで足せる逃げ道」ではない —— 足した拡張子は**そのぶん検査されなくなる**。
 */
const BINARY_EXTENSIONS = new Set([]);

/** 追跡下のファイル一覧（submodule は git ls-files に出ないので自然に対象外）。 */
function trackedFiles(root = REPO_ROOT) {
  return execFileSync('git', ['-C', root, 'ls-files'], { encoding: 'utf8', maxBuffer: 1 << 28 })
    .split('\n').map((s) => s.trim()).filter(Boolean);
}

/**
 * バッファ内の最初の NUL の位置を返す（無ければ -1）。
 * 🔴 **復号しない。** バイト列のまま走査する（決定 2）。
 */
function firstNulOffset(buf) {
  for (let i = 0; i < buf.length; i += 1) if (buf[i] === 0) return i;
  return -1;
}

/** バイトオフセットから行番号を求める（1 始まり）。NUL の位置を人が探せるようにするため。 */
function lineOfOffset(buf, offset) {
  let line = 1;
  for (let i = 0; i < offset && i < buf.length; i += 1) if (buf[i] === 0x0A) line += 1;
  return line;
}

/** 1 ファイルを検査する。NUL があれば {rel, offset, line}、無ければ null。 */
function inspectFile(root, rel) {
  const ext = path.extname(rel).toLowerCase();
  if (BINARY_EXTENSIONS.has(ext)) return null;
  let buf;
  try {
    buf = fs.readFileSync(path.join(root, rel));
  } catch {
    return null; // 読めない（削除済み・シンボリックリンク等）は対象外
  }
  const offset = firstNulOffset(buf);
  if (offset === -1) return null;
  return { rel, offset, line: lineOfOffset(buf, offset) };
}

/** 走査本体。{violations, scanned, skipped} を返す。 */
function scan(root = REPO_ROOT, files = null) {
  const list = files || trackedFiles(root);
  const violations = [];
  let scanned = 0;
  let skipped = 0;
  for (const rel of list) {
    if (BINARY_EXTENSIONS.has(path.extname(rel).toLowerCase())) { skipped += 1; continue; }
    scanned += 1;
    const v = inspectFile(root, rel);
    if (v) violations.push(v);
  }
  return { violations, scanned, skipped };
}

// 0 件走査で静かに緑にしない（#664 の門と同じ作法。決定 3）。
// 追跡下は実測 1995 件。半分を大きく下回ったら走査が壊れているとみなす。
const MIN_SCANNED = 500;

/**
 * 走査件数が下限を下回ったか（＝走査が空振りしている疑い）。
 *
 * 🔴 **純粋関数として切り出してある。** main() の中に埋めたままだと、REPO_ROOT が __dirname 基準の
 * ため **cwd を変えても門を試験できない**（実測: 空リポジトリで実行しても本リポジトリを走査して緑になる）。
 * 門があること自体を自己試験と変異試験で確かめられるようにする。
 */
function isScanTooSmall(scanned, min = MIN_SCANNED) {
  return scanned < min;
}

// --- 自己試験 -------------------------------------------------------------------

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });
  const os = require('os');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'nul-selftest-'));

  const write = (name, buf) => { fs.writeFileSync(path.join(dir, name), buf); return name; };
  write('clean.md', Buffer.from('# 見出し\nふつうの本文\n', 'utf8'));
  write('clean.cs', Buffer.from('public class A { }\n', 'utf8'));
  const dirty = write('dirty.md', Buffer.concat([
    Buffer.from('1 行目\n2 行目 ', 'utf8'), Buffer.from([0x00]), Buffer.from(' 続き\n', 'utf8'),
  ]));

  t('firstNulOffset: NUL が無ければ -1', firstNulOffset(Buffer.from('abc', 'utf8')) === -1);
  t('firstNulOffset: 先頭の NUL を見つける', firstNulOffset(Buffer.from([0x00, 0x61])) === 0);
  t('firstNulOffset: 途中の NUL を見つける', firstNulOffset(Buffer.from([0x61, 0x00, 0x62])) === 1);
  t('firstNulOffset: 空バッファでも壊れない', firstNulOffset(Buffer.alloc(0)) === -1);

  {
    const v = inspectFile(dir, dirty);
    t('inspectFile: NUL を含むファイルを検出する', v !== null, v);
    t('inspectFile: 行番号を返す（NUL は 2 行目にある）', v && v.line === 2, v);
    t('inspectFile: バイトオフセットを返す', v && typeof v.offset === 'number' && v.offset > 0, v);
  }
  t('inspectFile: きれいなファイルは null', inspectFile(dir, 'clean.md') === null);
  t('inspectFile: 存在しないファイルでも壊れない（null）', inspectFile(dir, 'no-such-file.md') === null);

  {
    const r = scan(dir, ['clean.md', 'clean.cs', dirty]);
    t('scan: 違反 1 件・走査 3 件', r.violations.length === 1 && r.scanned === 3, r);
    t('scan: 違反は dirty.md', r.violations[0].rel === dirty, r.violations);
  }
  {
    const r = scan(dir, ['clean.md', 'clean.cs']);
    t('scan: きれいなら違反 0 件', r.violations.length === 0, r);
  }

  {
    // 🔴 **検査器が「自分が検出したいもの」で壊れないこと。**
    // NUL を大量に含むファイル（＝実質バイナリ）を渡しても、復号せずバイト走査するので例外にならない。
    const bin = write('binary.md', Buffer.from([0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0xFF, 0x00]));
    let threw = false;
    let v = null;
    try { v = inspectFile(dir, bin); } catch { threw = true; }
    t('★ 検査器はバイナリを渡されても例外を投げない（復号しない）', !threw && v !== null, { threw, v });
  }

  {
    // BINARY_EXTENSIONS に載っている拡張子は検査しない（現時点では空なので、動作だけを確かめる）。
    const saved = [...BINARY_EXTENSIONS];
    BINARY_EXTENSIONS.add('.bin');
    const b = write('x.bin', Buffer.from([0x00, 0x01]));
    const r = scan(dir, [b]);
    t('BINARY_EXTENSIONS の拡張子は走査せず skipped に数える',
      r.violations.length === 0 && r.skipped === 1 && r.scanned === 0, r);
    BINARY_EXTENSIONS.clear();
    for (const e of saved) BINARY_EXTENSIONS.add(e);
    t('★ BINARY_EXTENSIONS は空である（空であること自体が正解の状態）',
      BINARY_EXTENSIONS.size === 0, [...BINARY_EXTENSIONS]);
  }

  t('MIN_SCANNED は 0 件走査を緑にしない下限を持つ', MIN_SCANNED > 0);
  t('★ isScanTooSmall: 0 件走査は fail 側（静かに緑にしない）', isScanTooSmall(0) === true);
  t('★ isScanTooSmall: 下限ちょうどは fail 側（境界）', isScanTooSmall(MIN_SCANNED - 1) === true);
  t('isScanTooSmall: 下限以上は通す', isScanTooSmall(MIN_SCANNED) === false && isScanTooSmall(2000) === false);

  fs.rmSync(dir, { recursive: true, force: true });

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed += 1; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[check-nul-bytes] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-nul-bytes] 自己試験 ${cases.length} 件 OK。`);
}

// --- 実行 -----------------------------------------------------------------------

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }

  warnIfResultMayDifferFromCi('check-nul-bytes.js', MODE.TRACKED);

  let result;
  try {
    result = scan();
  } catch (e) {
    console.error(`[check-nul-bytes] 追跡下のファイル一覧を取得できませんでした: ${e.message}`);
    process.exit(1);
  }
  const { violations, scanned, skipped } = result;

  console.log(`[check-nul-bytes] 追跡下 ${scanned} 件を走査`
    + (skipped ? ` / バイナリ拡張子として ${skipped} 件を除外（${[...BINARY_EXTENSIONS].join(', ')}）` : ''));

  // 0 件走査で静かに緑にしない（決定 3）。
  if (isScanTooSmall(scanned)) {
    console.error(`[check-nul-bytes] 走査件数が ${scanned} 件しかありません（下限 ${MIN_SCANNED}）。`
      + ' 走査が空振りしているか、実行位置がリポジトリ外です。');
    process.exit(1);
  }

  if (violations.length === 0) {
    console.log('[check-nul-bytes] OK: 生の NUL バイトを含むファイルはありません。');
    process.exit(0);
  }

  console.error(`[check-nul-bytes] 生の NUL バイトを含むファイルが ${violations.length} 件あります:`);
  for (const v of violations) {
    console.error(`    ${v.rel}  （${v.line} 行目 / バイトオフセット ${v.offset}）`);
  }
  console.error('');
  console.error('生の NUL が入ると git と grep がファイルをバイナリ扱いします —— git diff が中身を出さず、');
  console.error('grep が行番号を出さないため、**レビューも走査もできなくなります**（#956。実際に 3 回起きた）。');
  console.error('エスケープ表記（バックスラッシュ + u0000）で書くつもりが実バイトになっていないか確認してください。');
  console.error('正当なバイナリを追加した場合は、scripts/check-nul-bytes.js の BINARY_EXTENSIONS へ');
  console.error('その拡張子を足してください（足したぶんは検査されなくなります）。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  BINARY_EXTENSIONS,
  MIN_SCANNED,
  trackedFiles,
  isScanTooSmall,
  firstNulOffset,
  lineOfOffset,
  inspectFile,
  scan,
};
