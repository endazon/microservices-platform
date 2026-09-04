#!/usr/bin/env node
'use strict';
/*
 * check-scaffolding-frames.js
 * NFR / ADR-0069 決定 5 / issue #1195:
 * **追跡下に「`.gitkeep` のみのディレクトリ」（＝空枠）が存在しないこと**を検査する。
 *
 * 述語は 1 つだけである（ADR-0069 決定 5 が明示的にそう限った）:
 *
 *     追跡下のあるディレクトリの配下（任意の深さ）に `.gitkeep` 以外の追跡ファイルが 1 件も無く、
 *     かつそのディレクトリ直下に `.gitkeep` がある → **空枠**。
 *
 * 🔴 **区分ごとの不変条件は検査しない**（i18n カタログの網羅・feature 区分の実体・
 * 「関心はあるが置き場所が違う」型の検出）。IADR-0321 決定 4 が「3 件は伸ばし忘れとしては同型でも
 * 検査すべき不変条件が違う」と指摘したのは正しく、だからこそ本検査器は
 * **撤回済み規範の残置という 1 つの述語**だけを見る。
 *
 * なぜ置くか（同型の事故が 3 回起きた。CLAUDE.md「検査器の追加は 2 回起きたら」を満たす）:
 *   #1066（feature 分割の作り忘れ）→ #1100 / IADR-0321（feature 内部 30 件）→
 *   #1122 / IADR-0325（ユニット直下 24 件）。planning#490 の環流記録が
 *   「フロントエンドに同型の入口が残る」と名指しで予告していた入口が、3 度使われた。
 *   IADR-0321 決定 4 と IADR-0325 決定 5 はいずれも「機械検査は追加しない」としたが、
 *   ADR-0069 決定 5 がその判断を置き換えた。
 *
 * 🔴 **子孫まで見る理由。** `a/.gitkeep` と `a/b/x.ts` が同居する形では、ディレクトリは実体で
 * 存在しており `.gitkeep` は何も keep していない（IADR-0325 決定 2 が 11 件を撤去した型）。
 * これを「空枠」と同じ 1 述語で扱うと、**枠ではないものまで枠と呼ぶ**ことになる。
 * よって「配下に .gitkeep 以外の追跡ファイルが 1 件も無い」を空枠の定義とする。
 * `a/.gitkeep` と `a/b/.gitkeep` の入れ子は**両方とも空枠**である（どちらも実体を持たない）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-scaffolding-frames.js
 *   node scripts/check-scaffolding-frames.js --self-test
 */
const path = require('path');
const { execFileSync } = require('child_process');

// 走査母集合を git ls-files から引くので、**未追跡ファイルは対象外**である（クラス B）。
// 作業ツリーに未コミットの変更があると CI の結果と一致し得ないため、その旨を警告する
// （#683 / IADR-0183）。lib が無くても本体は動かす（fail-open）。
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

/** 枠置きに使われるファイル名。**別名を足すときは、足したぶんが枠として扱われる。** */
const KEEP_FILENAME = '.gitkeep';

/**
 * 射程外として空枠を許すディレクトリと、その理由。
 *
 * 🔴 **各行に理由を書く**（ADR-0069 決定 5）。理由が空・短すぎるものは本検査器自身が fail にする
 * —— **黙って外す道を用意しない**（check-route-manifest.js の除外宣言と同じ作法）。
 * **足したディレクトリはそのぶん検査されなくなる。**
 *
 * ADR-0069 決定 1 は「本リポジトリ自身の `/new-project` が置く `.gitkeep` も射程外」と述べているが、
 * 本リポジトリの `.claude/commands/` に `new-project` は無く、`.claude/` 配下に `.gitkeep` を
 * 置くものも無い（実測 0 件）。**先回りで足さない。** 実際に置かれたときに人が判断して足す。
 */
const ALLOWED_EMPTY_FRAMES = new Map([
  ['docs/batch', 'docs/README.md が宣言する文書種別（バッチ仕様書）の出力先。まだ 1 件も書かれていない'],
  ['docs/errors', 'docs/README.md が宣言する文書種別（エラー仕様書）の出力先。まだ 1 件も書かれていない'],
  ['docs/infra', 'docs/README.md が宣言する文書種別（インフラ仕様書）の出力先。まだ 1 件も書かれていない'],
  [
    'docs/integration',
    'docs/README.md が宣言する文書種別（外部連携仕様書）の出力先。まだ 1 件も書かれていない',
  ],
]);

/** 除外の理由に要る最小の長さ。「-」「TODO」のような実質空の理由を通さないための下限。 */
const MIN_REASON_LENGTH = 10;

/** 追跡下のファイル一覧（submodule は git ls-files に出ないので自然に対象外）。 */
function trackedFiles(root = REPO_ROOT) {
  return execFileSync('git', ['-C', root, 'ls-files'], { encoding: 'utf8', maxBuffer: 1 << 28 })
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean);
}

/**
 * 空枠を探す。
 *
 * @param {string[]} files 追跡下のファイル（`/` 区切りの相対パス）
 * @returns {{dir: string, keepFile: string}[]} 空枠のディレクトリ（昇順）
 */
function findEmptyFrames(files) {
  // 「実体を持つ」ディレクトリの集合を先に作る。
  // .gitkeep 以外の追跡ファイルがあれば、そのファイルの**全祖先**が実体を持つ。
  const hasSubstance = new Set();
  const keepDirs = new Set();
  for (const f of files) {
    const i = f.lastIndexOf('/');
    const dir = i < 0 ? '' : f.slice(0, i);
    const base = i < 0 ? f : f.slice(i + 1);
    if (base === KEEP_FILENAME) {
      keepDirs.add(dir);
      continue;
    }
    // 祖先をすべて辿る（'' はリポジトリルート）。
    let cur = dir;
    for (;;) {
      hasSubstance.add(cur);
      if (cur === '') break;
      const j = cur.lastIndexOf('/');
      cur = j < 0 ? '' : cur.slice(0, j);
    }
  }
  const frames = [];
  for (const dir of [...keepDirs].sort()) {
    if (hasSubstance.has(dir)) continue;
    frames.push({ dir, keepFile: dir === '' ? KEEP_FILENAME : `${dir}/${KEEP_FILENAME}` });
  }
  return frames;
}

/**
 * 除外リストの健全性を見る。**除外そのものが腐ると、検査は静かに効かなくなる。**
 *
 * @returns {string[]} 問題のメッセージ（空なら健全）
 */
function inspectAllowlist(allowed = ALLOWED_EMPTY_FRAMES, files = null) {
  const problems = [];
  const list = files === null ? null : new Set(files);
  for (const [dir, reason] of allowed) {
    if (typeof reason !== 'string' || reason.trim().length < MIN_REASON_LENGTH) {
      problems.push(`除外 "${dir}" の理由が短すぎる（${MIN_REASON_LENGTH} 文字以上を書くこと）: ${JSON.stringify(reason)}`);
    }
    if (list && !list.has(`${dir}/${KEEP_FILENAME}`)) {
      problems.push(`除外 "${dir}" に ${KEEP_FILENAME} が無い（除外リストが腐っている。実在しない行は消すこと）`);
    }
  }
  return problems;
}

// 0 件走査で静かに緑にしない（#664 の門）。追跡下は実測 3000 件超。
const MIN_SCANNED = 500;

/** 走査件数が下限を下回ったか（＝走査が空振りしている疑い）。 */
function isScanTooSmall(scanned, min = MIN_SCANNED) {
  return scanned < min;
}

/** 走査本体。{violations, allowed, scanned} を返す。 */
function scan(files, allowed = ALLOWED_EMPTY_FRAMES) {
  const frames = findEmptyFrames(files);
  const violations = frames.filter((f) => !allowed.has(f.dir));
  const allowedHits = frames.filter((f) => allowed.has(f.dir));
  return { violations, allowed: allowedHits, scanned: files.length };
}

// --- 自己試験 -------------------------------------------------------------------

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });

  // --- findEmptyFrames の述語 ---
  t(
    '空枠を検出する（.gitkeep だけのディレクトリ）',
    findEmptyFrames(['a/.gitkeep']).length === 1
  );
  t(
    '同階層に実体があれば空枠ではない',
    findEmptyFrames(['a/.gitkeep', 'a/x.ts']).length === 0
  );
  t(
    '🔴 子孫に実体があれば空枠ではない（.gitkeep は何も keep していないが、枠ではない）',
    findEmptyFrames(['a/.gitkeep', 'a/b/x.ts']).length === 0
  );
  {
    const r = findEmptyFrames(['a/.gitkeep', 'a/b/.gitkeep']);
    t('🔴 入れ子の枠は両方とも空枠（どちらも実体を持たない）', r.length === 2, r);
  }
  t('.gitkeep が無ければ何も出ない', findEmptyFrames(['a/x.ts', 'b/y.ts']).length === 0);
  t('空入力でも壊れない', findEmptyFrames([]).length === 0);
  {
    const r = findEmptyFrames(['z/.gitkeep', 'a/.gitkeep']);
    t('出力は昇順（差分が安定する）', r[0].dir === 'a' && r[1].dir === 'z', r);
  }
  {
    const r = findEmptyFrames(['a/b/c/.gitkeep', 'a/b/c/d/e.ts']);
    t('深い子孫の実体も数える', r.length === 0, r);
  }
  {
    const r = findEmptyFrames(['a/.gitkeep']);
    t('違反はディレクトリと .gitkeep のパスを持つ', r[0].dir === 'a' && r[0].keepFile === 'a/.gitkeep', r);
  }

  // --- 除外リスト ---
  {
    const files = ['docs/batch/.gitkeep', 'src/x.ts'];
    const r = scan(files, new Map([['docs/batch', '文書種別の出力先。まだ 1 件も書かれていない']]));
    t('除外に載っているディレクトリは違反にしない', r.violations.length === 0 && r.allowed.length === 1, r);
  }
  {
    // ★ 陽性対照。除外から外すと**同じ入力で**検出される（検査器が実際に走っている証明）。
    const files = ['docs/batch/.gitkeep', 'src/x.ts'];
    const r = scan(files, new Map());
    t('★ 除外を外すと同じ入力で 1 件検出する（陽性対照）', r.violations.length === 1, r);
  }
  {
    const p = inspectAllowlist(new Map([['a', '短い']]));
    t('★ 理由が短い除外は問題として出る', p.length === 1, p);
  }
  {
    const p = inspectAllowlist(new Map([['a', '']]));
    t('★ 理由が空の除外は問題として出る', p.length === 1, p);
  }
  {
    const p = inspectAllowlist(new Map([['a', '十分に長い理由をここへ書いてある']]), ['b/.gitkeep']);
    t('★ 実在しない除外行は問題として出る（腐った除外を残さない）', p.length === 1, p);
  }
  {
    const p = inspectAllowlist(
      new Map([['a', '十分に長い理由をここへ書いてある']]),
      ['a/.gitkeep']
    );
    t('実在する除外行は問題にならない', p.length === 0, p);
  }
  t(
    '実データの除外リストは全行が健全（理由の長さ）',
    inspectAllowlist(ALLOWED_EMPTY_FRAMES).length === 0,
    inspectAllowlist(ALLOWED_EMPTY_FRAMES)
  );

  // --- 走査件数の門 ---
  t('MIN_SCANNED は 0 件走査を緑にしない下限を持つ', MIN_SCANNED > 0);
  t('★ isScanTooSmall: 0 件走査は fail 側', isScanTooSmall(0) === true);
  t('★ isScanTooSmall: 下限ちょうど手前は fail 側（境界）', isScanTooSmall(MIN_SCANNED - 1) === true);
  t(
    'isScanTooSmall: 下限以上は通す',
    isScanTooSmall(MIN_SCANNED) === false && isScanTooSmall(5000) === false
  );

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) {
      failed += 1;
      if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual));
    }
  }
  if (failed) {
    console.error(`[check-scaffolding-frames] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-scaffolding-frames] 自己試験 ${cases.length} 件 OK。`);
}

// --- 実行 -----------------------------------------------------------------------

function main() {
  if (process.argv.includes('--self-test')) {
    selfTest();
    return;
  }

  warnIfResultMayDifferFromCi('check-scaffolding-frames.js', MODE.TRACKED);

  let files;
  try {
    files = trackedFiles();
  } catch (e) {
    console.error(`[check-scaffolding-frames] 追跡下のファイル一覧を取得できませんでした: ${e.message}`);
    process.exit(1);
  }

  const { violations, allowed, scanned } = scan(files);

  console.log(
    `[check-scaffolding-frames] 追跡下 ${scanned} 件を走査 / 射程外として ${allowed.length} 件を除外`
  );

  // 0 件走査で静かに緑にしない。
  if (isScanTooSmall(scanned)) {
    console.error(
      `[check-scaffolding-frames] 走査件数が ${scanned} 件しかありません（下限 ${MIN_SCANNED}）。`
        + ' 走査が空振りしているか、実行位置がリポジトリ外です。'
    );
    process.exit(1);
  }

  // 除外リスト自体の健全性（理由が書かれているか・腐っていないか）。
  const allowlistProblems = inspectAllowlist(ALLOWED_EMPTY_FRAMES, files);
  if (allowlistProblems.length) {
    console.error('[check-scaffolding-frames] 除外リスト（ALLOWED_EMPTY_FRAMES）に問題があります:');
    for (const p of allowlistProblems) console.error(`    ${p}`);
    process.exit(1);
  }

  if (violations.length === 0) {
    console.log(
      `[check-scaffolding-frames] OK: ${KEEP_FILENAME} のみのディレクトリはありません（ADR-0069 決定 5）。`
    );
    process.exit(0);
  }

  console.error(
    `[check-scaffolding-frames] ${KEEP_FILENAME} のみのディレクトリ（空枠）が ${violations.length} 件あります:`
  );
  for (const v of violations) console.error(`    ${v.keepFile}`);
  console.error('');
  console.error('計画 ADR-0069 決定 1 は、空枠を置かないと定めました（射程は feature 内部・');
  console.error('ユニット直下・雛形の 3 者すべて）。枠だけの状態は機械にも目視にも「区分が揃っている」と');
  console.error('見え、**適合の見え方**を作ります —— しかも「関心はあるが置き場所が違う」型の非適合を');
  console.error('隠したままにします（同 決定 3）。**区分は必要になった時点で実体とともに作ってください。**');
  console.error('');
  console.error('文書種別の出力先のように射程外のものは、scripts/check-scaffolding-frames.js の');
  console.error('ALLOWED_EMPTY_FRAMES へ**理由つきで**足してください（足したぶんは検査されなくなります）。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  ALLOWED_EMPTY_FRAMES,
  KEEP_FILENAME,
  MIN_REASON_LENGTH,
  MIN_SCANNED,
  trackedFiles,
  findEmptyFrames,
  inspectAllowlist,
  isScanTooSmall,
  scan,
};
