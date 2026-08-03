#!/usr/bin/env node
'use strict';
/*
 * lib/excluded-units.js
 * 「本リポジトリの実体でないユニット」＝ .gitmodules に登録された src/<unit> の submodule を
 * **単一情報源から導出する**共通ヘルパ（issue #473）。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 背景:
 *   check-backend-libraries.js / check-test-traceability.js / check-coverage-floor.js は、
 *   除外ユニットの集合をそれぞれ独立に `new Set(['ai-stock-trading'])` とハードコードしていた。
 *   値は一致していたが導出経路が 3 本あり、IADR-0056 決定 6（追加の可変機能ユニットは submodule で
 *   リンクする）により次の submodule ユニットが増えた瞬間、3 箇所が**同時に狭すぎ**になる。
 *   そのとき出るのは「検査器が落ちる」ではなく、他プロジェクトのコードを自リポジトリの規約で
 *   検査した赤や、他プロジェクトのカバレッジを自リポジトリの床へ合算した濁りである（IADR-0118 決定 4）。
 *   同じ問題は check-doc-links.js が `planning/` 固定判定で一度踏んでおり、.gitmodules 由来の
 *   一般則へ拡張して解決している（issue #139）。本ヘルパはその先例を除外判定へ適用する。
 *
 * 導出規則:
 *   .gitmodules の `path = ...` のうち **src/ 直下ちょうど 1 階層**（src/<unit>）だけをユニットとみなす。
 *   - リポジトリ直下の `planning` は src/ 配下でないため、規則上ユニットにならない。名指しで弾かない
 *     （名指しは本ヘルパが消そうとしているハードコードと同じ性質を持つ）。
 *   - src/knowledge/frontend/vendor/x のような深い submodule はユニットへ丸めない。丸めると
 *     自リポジトリの主たる成果物が丸ごと検査対象外になる（過剰な除外）。
 *
 * 使い方:
 *   const { excludedUnits, makeIsExcludedPath } = require('./lib/excluded-units.js');
 *   const EXCLUDED_UNITS = excludedUnits();                  // 既定: リポジトリルートの .gitmodules
 *   const isExcludedPath = makeIsExcludedPath(EXCLUDED_UNITS);
 *
 *   node scripts/lib/excluded-units.js --self-test           # ヘルパ自体の自己試験
 */
const fs = require('fs');
const path = require('path');

/** リポジトリルート（scripts/lib/ から 2 つ上）。 */
const REPO_ROOT = path.resolve(__dirname, '..', '..');

function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

/**
 * .gitmodules の**内容**から submodule の path 一覧を得る（純関数）。
 * 解析は check-doc-links.js の submodulePaths() と同じ流儀に揃える（INI パーサは持ち込まない）。
 */
function submodulePaths(gitmodulesText) {
  const out = [];
  const re = /^\s*path\s*=\s*(.+?)\s*$/gm;
  let m;
  while ((m = re.exec(String(gitmodulesText)))) out.push(toPosix(m[1]).replace(/\/+$/, ''));
  return out;
}

/**
 * submodule の path がユニットを指すなら、そのユニット名を返す（違えば null）。
 * ユニットは `src/<unit>` ちょうど 1 階層に限る（上記「導出規則」）。
 */
function unitOfSubmodulePath(submodulePath) {
  const m = toPosix(submodulePath).match(/^src\/([^/]+)$/);
  return m ? m[1] : null;
}

/** .gitmodules の内容から除外ユニット集合を作る（純関数）。 */
function excludedUnitsFromText(gitmodulesText) {
  const units = new Set();
  for (const p of submodulePaths(gitmodulesText)) {
    const unit = unitOfSubmodulePath(p);
    if (unit) units.add(unit);
  }
  return units;
}

/**
 * .gitmodules を読む。**読めなければ例外**（fail-closed）。
 *
 * 空集合を返すと、除外されるはずの別プロジェクトを自リポジトリの規約で検査してしまう。
 * 既定値へフォールバックすると、単一情報源が .gitmodules でなくなり「次の submodule 追加で
 * 狭すぎになる」という issue #473 の劣化がフォールバック経路の中に残り続ける。
 * .gitmodules は追跡ファイルであり、読めないのは「走査対象が本リポジトリでない」異常である。
 */
function readGitmodules(root) {
  const file = path.join(root, '.gitmodules');
  try {
    return fs.readFileSync(file, 'utf8');
  } catch (e) {
    throw new Error(
      `[excluded-units] .gitmodules を読めませんでした（${file}）: ${e.message}。` +
        '除外ユニットを 0 件と見なすと別プロジェクト（submodule）を自リポジトリの規約で検査してしまうため、' +
        '既定値へフォールバックせず停止します。走査対象がリポジトリのルートかを確認してください。'
    );
  }
}

/**
 * 除外ユニット集合を返す。
 *   excludedUnits()                     既定（リポジトリルートの .gitmodules）
 *   excludedUnits({ root })             ルート差し替え（フィクスチャ）
 *   excludedUnits({ gitmodules: text }) 内容を直接注入（純関数として試験する用）
 * 環境変数の口は作らない——実行時に除外を外から広げられると、検査を黙って狭める経路になる。
 */
function excludedUnits({ root = REPO_ROOT, gitmodules = null } = {}) {
  const text = gitmodules === null ? readGitmodules(root) : gitmodules;
  return excludedUnitsFromText(text);
}

/**
 * リポジトリ相対パスが除外ユニット配下かを判定する関数を作る。
 * `src/<unit>/...` の形のみ判定対象（src 直下のファイルは対象外扱いにしない）。
 */
function makeIsExcludedPath(units) {
  return function isExcludedPath(relPath) {
    const m = toPosix(relPath).match(/^src\/([^/]+)\//);
    return m ? units.has(m[1]) : false;
  };
}

// --- 自己試験 -------------------------------------------------------------------

function selfTest() {
  const os = require('os');
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });
  const sorted = (s) => [...s].sort();

  const GITMODULES = [
    '[submodule "planning"]',
    '\tpath = planning',
    '\turl = https://example.invalid/project-planning.git',
    '[submodule "src/ai-stock-trading"]',
    '\tpath = src/ai-stock-trading',
    '\turl = https://example.invalid/ai-stock-trading.git',
    '\tbranch = develop',
    '',
  ].join('\n');

  t('submodulePaths: path 行を全件拾う',
    submodulePaths(GITMODULES).join(',') === 'planning,src/ai-stock-trading',
    submodulePaths(GITMODULES));
  t('submodulePaths: 空文字なら空配列（.gitmodules が空の状態は異常ではない）',
    submodulePaths('').length === 0);
  t('submodulePaths: 区切りは / に正規化し末尾 / を落とす',
    submodulePaths('\tpath = src\\vendor-unit/\n').join(',') === 'src/vendor-unit');

  t('unitOfSubmodulePath: src/<unit> はユニット', unitOfSubmodulePath('src/ai-stock-trading') === 'ai-stock-trading');
  // issue #473 の注意点: リポ直下の planning は「ユニットではない」。名指しでなく位置の規則で落とす。
  t('unitOfSubmodulePath: リポジトリ直下の planning はユニットでない', unitOfSubmodulePath('planning') === null);
  t('unitOfSubmodulePath: src 配下の深い submodule はユニットへ丸めない',
    unitOfSubmodulePath('src/knowledge/frontend/vendor/x') === null);
  t('unitOfSubmodulePath: src そのものはユニットでない', unitOfSubmodulePath('src') === null);
  t('unitOfSubmodulePath: src 以外の直下（deploy/x）はユニットでない', unitOfSubmodulePath('deploy/x') === null);

  t('excludedUnitsFromText: planning を含まず src/<unit> だけを返す',
    sorted(excludedUnitsFromText(GITMODULES)).join(',') === 'ai-stock-trading',
    sorted(excludedUnitsFromText(GITMODULES)));

  // 受け入れ基準 2（issue #473）: .gitmodules に仮の submodule を足すと除外が自動追随する。
  // 3 検査器がハードコードを持っていた時代は、ここで人手の更新が要り、3 箇所とも狭すぎになっていた。
  {
    const added = GITMODULES +
      '[submodule "src/foo-unit"]\n\tpath = src/foo-unit\n\turl = https://example.invalid/foo-unit.git\n';
    t('フィクスチャ: 仮の submodule を足すと除外集合が自動追随する',
      sorted(excludedUnitsFromText(added)).join(',') === 'ai-stock-trading,foo-unit',
      sorted(excludedUnitsFromText(added)));
    t('フィクスチャ: 追随した仮ユニット配下のパスも除外される',
      makeIsExcludedPath(excludedUnitsFromText(added))('src/foo-unit/backend/x/X.csproj'));
  }

  // ルート注入（一時ディレクトリの .gitmodules を読む経路）も同じ結果になること。
  {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'excluded-units-'));
    fs.writeFileSync(path.join(dir, '.gitmodules'),
      GITMODULES + '[submodule "src/bar-unit"]\n\tpath = src/bar-unit\n\turl = x\n');
    t('root 注入: フィクスチャの .gitmodules を読んで導出する',
      sorted(excludedUnits({ root: dir })).join(',') === 'ai-stock-trading,bar-unit',
      sorted(excludedUnits({ root: dir })));
    fs.rmSync(path.join(dir, '.gitmodules'));

    // fail-closed: 読めないときに空集合を返すと、別プロジェクトを自リポの規約で検査してしまう。
    let threw = null;
    try { excludedUnits({ root: dir }); } catch (e) { threw = e; }
    t('.gitmodules を読めなければ例外で停止する（空集合を返さない＝fail-open にしない）',
      threw !== null && /\.gitmodules を読めませんでした/.test(String(threw.message)),
      threw && threw.message);
    fs.rmdirSync(dir);
  }

  t('makeIsExcludedPath: 除外ユニット配下は true',
    makeIsExcludedPath(new Set(['ai-stock-trading']))('src/ai-stock-trading/backend/x/X.csproj'));
  t('makeIsExcludedPath: 対象ユニットは false',
    !makeIsExcludedPath(new Set(['ai-stock-trading']))('src/platform/backend/Bff/Platform.Bff.csproj'));
  t('makeIsExcludedPath: src 直下のファイルは対象外扱いにしない',
    !makeIsExcludedPath(new Set(['ai-stock-trading']))('src/Directory.Packages.props'));
  t('makeIsExcludedPath: 前方一致の別ユニット（ai-stock-trading-x）を巻き込まない',
    !makeIsExcludedPath(new Set(['ai-stock-trading']))('src/ai-stock-trading-x/backend/x/X.csproj'));

  // 実リポジトリの .gitmodules から現に ai-stock-trading が導出されること。解析が壊れて空集合へ
  // 退行した場合はここで落ちる（空集合は「除外なし」＝別プロジェクトの検査になる）。
  t('実リポジトリ: .gitmodules から ai-stock-trading が導出される',
    excludedUnits().has('ai-stock-trading'), sorted(excludedUnits()));

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[excluded-units] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[excluded-units] 自己試験 ${cases.length} 件 OK。`);
}

if (require.main === module) {
  if (process.argv.includes('--self-test')) selfTest();
  else console.log([...excludedUnits()].sort().join('\n'));
}

module.exports = {
  REPO_ROOT,
  toPosix,
  submodulePaths,
  unitOfSubmodulePath,
  excludedUnitsFromText,
  excludedUnits,
  makeIsExcludedPath,
};
