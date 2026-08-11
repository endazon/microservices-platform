/**
 * 検査器が「偽の緑」を返す条件を検出して警告する（#683 / IADR-0183）。
 *
 * 背景:
 *   手元で全検査器が緑 → push → CI で赤、が繰り返し起きている。**検査器の欠陥ではなく、
 *   走らせた順序の問題**である。原因は 2 クラスあり、症状はどちらも同じ「偽の緑」である。
 *
 *   | クラス | 読むもの | 見えないもの | 該当 |
 *   | --- | --- | --- | ---: |
 *   | HEAD    | `git show HEAD:` / `git diff …HEAD` | **未コミットの変更** | **2 本**（PR #682 の事故） |
 *   | TRACKED | **走査母集合を `git ls-files` から引く** | **untracked の新規ファイル** | **2 本**（PR #708 の事故） |
 *
 *   作業ツリーを直接読む検査器（29 本）は順序に依存しないので、**何も足さない**。
 *
 *   ★ 該当は**実挙動で測る**（PATH に置いた `git` の shim で実際の呼び出しを記録する）。
 *     文字列 grep で分類すると誤る —— 初回は `check-test-spec-coverage.js` を TRACKED としたが、
 *     当たったのは**エラーメッセージ中の `git ls-files`** で、この検査器は git を一切呼ばない。
 *     境界は 2 つあり、どちらも機械的に引ける（IADR-0183 決定 8・9）:
 *       - `ls-files --error-unmatch <1 ファイル>` は**走査母集合ではない**（専用の警告が別に在る）
 *       - `git log <range>` は**既存の作業ツリー状態を見落とさない**（コミット前に件名は存在しない）
 *
 * 方針（IADR-0183）:
 *   - **失敗させない。** 未コミットで走らせること自体は正当な使い方（書きかけの確認）であり、
 *     落とすと検査器が邪魔者になって外される（#683 の但し書き。PR #652 レビュー 1 巡目と同じ理由）。
 *   - **CI 用の環境分岐を書かない。** CI はクリーンなチェックアウトで走るので該当が 0 件になり、
 *     自然に無音になる。分岐を足すと条件と分岐が二重管理になる。
 *   - **走査範囲で絞り込まない。** 範囲は検査器ごとに違い（`:!planning` 等）、複製は必ず腐る
 *     （IADR-0169 決定 2 が「名指しの除外リストは腐る」として退けたのと同じ理由）。
 *     粗く鳴りすぎる方が、静かに見落とすより安い——**警告であって失敗ではない**ため実害が無い。
 */

'use strict';

const { execFileSync } = require('child_process');
const path = require('path');
const { warn } = require('./ci-annotate');

const REPO_ROOT = path.join(__dirname, '..', '..');

/** 走査対象の読み方。検査器はどちらか一方を宣言する。 */
const MODE = Object.freeze({
  /** `git show HEAD:` / `git diff …HEAD` を読む。未コミットの変更が見えない。 */
  HEAD: 'head',
  /** `git ls-files` を読む。untracked の新規ファイルが見えない。 */
  TRACKED: 'tracked',
});

/**
 * `git status --porcelain` を読む。取れなければ null（git が無い・リポジトリ外）。
 *
 * ★ 取れないときは黙って諦める。ここで落とすと、git の無い環境で検査器そのものが
 *   動かなくなる——本モジュールの目的は警告であって、実行の前提条件ではない。
 */
function porcelain(cwd = REPO_ROOT) {
  try {
    const out = execFileSync('git', ['status', '--porcelain', '--untracked-files=normal'], {
      cwd,
      encoding: 'utf8',
      maxBuffer: 32 * 1024 * 1024,
    });
    return out.split('\n').filter((l) => l.length > 0);
  } catch {
    return null;
  }
}

/** porcelain の 1 行が untracked（`??`）かどうか。 */
function isUntracked(line) {
  return line.startsWith('??');
}

/**
 * 作業ツリーの状態を数える。
 *
 * @returns {{ total: number, untracked: number, available: boolean }}
 */
function inspectWorktree(cwd = REPO_ROOT) {
  const lines = porcelain(cwd);
  if (lines === null) return { total: 0, untracked: 0, available: false };
  return {
    total: lines.length,
    untracked: lines.filter(isUntracked).length,
    available: true,
  };
}

/**
 * 走らせた順序のせいで結果が CI と食い違いうるときに警告する。
 *
 * **終了コードは変えない。** 呼び出し側は戻り値を無視してよい。
 *
 * @param {string} checkerName 検査器の名前（`check-doc-updated.js` 等）
 * @param {'head'|'tracked'} mode 走査対象の読み方（MODE）
 * @param {string} [cwd]
 * @returns {boolean} 警告を出したなら true
 */
function warnIfResultMayDifferFromCi(checkerName, mode, cwd = REPO_ROOT) {
  const { total, untracked, available } = inspectWorktree(cwd);
  if (!available) return false;

  if (mode === MODE.HEAD) {
    if (total === 0) return false;
    warn(
      `[${checkerName}] 作業ツリーに未コミットの変更が ${total} 件ある。` +
        '本検査は **HEAD（コミット済み）** を読むため、この結果は push 後の CI と一致しない。' +
        ' コミットしてから再実行すること（#683 / IADR-0183）。',
    );
    return true;
  }

  if (mode === MODE.TRACKED) {
    if (untracked === 0) return false;
    warn(
      `[${checkerName}] untracked のファイルが ${untracked} 件ある。` +
        '本検査は **追跡下のファイル（git ls-files）** だけを走査するため、それらは対象外である。' +
        ' `git add` してから再実行すること（#683 / IADR-0183）。',
    );
    return true;
  }

  throw new Error(`未知の mode: ${mode}（MODE.HEAD か MODE.TRACKED を渡すこと）`);
}

/** 自己試験。外部依存ゼロ・実リポジトリの状態に依存しない。 */
function selfTest() {
  const assert = require('assert');
  const results = [];
  const ok = (name, fn) => {
    fn();
    results.push(name);
  };

  ok('MODE は 2 値で凍結されている', () => {
    assert.deepStrictEqual(Object.keys(MODE).sort(), ['HEAD', 'TRACKED']);
    assert.ok(Object.isFrozen(MODE));
  });

  ok('untracked の判定は `??` 前置だけを見る', () => {
    assert.strictEqual(isUntracked('?? a.md'), true);
    assert.strictEqual(isUntracked(' M a.md'), false);
    assert.strictEqual(isUntracked('A  a.md'), false);
    // `?` 1 文字の未知形式を untracked と誤判定しない。
    assert.strictEqual(isUntracked('?x a.md'), false);
  });

  ok('未知の mode は例外にする（黙って無視しない）', () => {
    assert.throws(() => warnIfResultMayDifferFromCi('x', 'bogus', REPO_ROOT), /未知の mode/);
  });

  ok('git が取れない場所では警告しない（実行の前提条件にしない）', () => {
    const r = warnIfResultMayDifferFromCi('x', MODE.HEAD, '/nonexistent-path-for-self-test');
    assert.strictEqual(r, false);
  });

  console.log(`[worktree-state] self-test OK (${results.length} 件)`);
  return 0;
}

if (require.main === module) {
  process.exit(process.argv.includes('--self-test') ? selfTest() : 0);
}

module.exports = { MODE, inspectWorktree, isUntracked, warnIfResultMayDifferFromCi, selfTest };
