#!/usr/bin/env node
'use strict';
/*
 * setup.test.js
 * NFR（運用保守）/ issue #1349: scripts/setup.sh が submodule 初期化と pnpm install を
 * 正しく（順序・パス・cwd・fail-open）行うことを固定する smoke test。
 *
 * 方式（k8s-local-up.test.js / IADR-0087 と同じ stub-on-PATH。scripts/setup.sh は無改変）:
 *   外部バイナリ（git / dotnet / pnpm）を PATH 上の「記録スタブ」へ差し替え、副作用ゼロで
 *   setup.sh を実行し、発行コマンド列を採取して分岐をアサートする。
 *
 * 🔴 **setup.sh は opt-in フラグを 1 つも持たない** ため、k8s-local-up.test.js の
 * OPTIN_TOKENS / matchesToken / SYNTHETIC_CONTAMINATION（opt-in ゲート横断の検出力測定機構）は
 * 移入しない —— この機構は「立てたときだけ現れるべき行」を横断的に固定するためのものであり、
 * setup.sh には対応する分岐が無い（findings で明示された制約）。
 *
 * dotnet は「在ることにする」記録スタブへ差し替える。これにより setup.sh L37-74 の
 * .NET SDK 自己修復ブロック（issue #824 / IADR-0180）は `command -v dotnet` が真になった
 * 時点で丸ごとスキップされ、curl / dotnet-install.sh のネットワーク依存が本テストへ
 * 混ざらない（このブロック自体の固定は本ファイルの射程外）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ・bash は前提ツール）。実行: node scripts/setup.test.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const SETUP_SCRIPT = path.join('scripts', 'setup.sh'); // REPO_ROOT 相対（cwd=REPO_ROOT で実行）

// --- stub-on-PATH ハーネス ---------------------------------------------------

// git 記録スタブ。setup.sh が呼ぶのは `git submodule update --init src/ai-stock-trading` の
// 1 箇所だけである。STUB_GIT_FAIL=1 で非 0 を返し、submodule 初期化失敗時の fail-open を検証する。
const GIT_STUB = [
  '#!/usr/bin/env bash',
  'echo "git $*" >> "$STUB_LOG"',
  'if [ "${STUB_GIT_FAIL:-}" = "1" ]; then exit 1; fi',
  'exit 0',
  '',
].join('\n');

// dotnet 記録スタブ。「在る」ことにして自己修復ブロック（L37-74）を丸ごとスキップさせ、
// 続く自動発見ループ（L85-93 相当）の `dotnet restore <sln>` 呼び出しだけを記録する。
const DOTNET_STUB = [
  '#!/usr/bin/env bash',
  'echo "dotnet $*" >> "$STUB_LOG"',
  'exit 0',
  '',
].join('\n');

// pnpm 記録スタブ。cwd を行へ埋め込む —— setup.sh は `(cd src && pnpm install …)` と
// サブシェルで cwd を変えてから呼ぶため、スタブ自身の $PWD が「cwd=src で呼ばれたか」の証拠になる。
// STUB_PNPM_FAIL=1 で非 0 を返し、pnpm install 失敗時の fail-open を検証する。
const PNPM_STUB = [
  '#!/usr/bin/env bash',
  'echo "pnpm $* @cwd=$PWD" >> "$STUB_LOG"',
  'if [ "${STUB_PNPM_FAIL:-}" = "1" ]; then exit 1; fi',
  'exit 0',
  '',
].join('\n');

const STUBS = { git: GIT_STUB, dotnet: DOTNET_STUB, pnpm: PNPM_STUB };

/**
 * origPath から、指定した実行ファイル名（Windows の .exe/.cmd/.ps1 拡張も含む）を
 * 直接収める PATH セグメントを取り除く。「pnpm 不在の環境」を移植性を保ったまま作るための
 * ヘルパー（Volta 等のツールバージョン管理がインストールした実体を隠す）。
 * @param {string} name 実行ファイルのベース名（拡張子なし）
 * @param {string} origPath 元の PATH
 * @returns {string}
 */
function pathWithoutBinary(name, origPath) {
  const candidates = new Set([name, `${name}.exe`, `${name}.cmd`, `${name}.ps1`]);
  return origPath
    .split(path.delimiter)
    .filter((dir) => {
      if (!dir) return true;
      let entries;
      try {
        entries = fs.readdirSync(dir);
      } catch {
        return true; // 読めないディレクトリは除外しない（無害側に倒す）
      }
      return !entries.some((f) => candidates.has(f));
    })
    .join(path.delimiter);
}

/**
 * 与えた env・stub 集合・cwd で setup.sh を実行し、採取したコマンド列を返す。
 * @param {object} [opts]
 * @param {Record<string,string>} [opts.env] 追加環境変数
 * @param {string[]} [opts.stubs] PATH へ置くスタブ名（既定は全部）
 * @param {string} [opts.cwd] REPO_ROOT 相対 cwd で実行したい場合の絶対パス（既定 REPO_ROOT）
 * @returns {{ status: number|null, lines: string[], stdout: string, stderr: string }}
 */
function runSetup(opts = {}) {
  const { env: extraEnv = {}, stubs = Object.keys(STUBS), cwd = REPO_ROOT } = opts;
  const workdir = fs.mkdtempSync(path.join(os.tmpdir(), 'setup-sh-smoke-'));
  const binDir = path.join(workdir, 'bin');
  fs.mkdirSync(binDir);
  const logFile = path.join(workdir, 'commands.log');
  fs.writeFileSync(logFile, '');

  for (const name of stubs) {
    const p = path.join(binDir, name);
    fs.writeFileSync(p, STUBS[name]);
    fs.chmodSync(p, 0o755);
  }

  const origPath = process.env.PATH || process.env.Path || '';
  // 除いたスタブ名は「その実体を隠した PATH」を使う（pnpm 不在テスト用）。
  let filteredPath = origPath;
  for (const name of Object.keys(STUBS)) {
    if (!stubs.includes(name)) filteredPath = pathWithoutBinary(name, filteredPath);
  }

  const env = {
    ...process.env,
    PATH: binDir + path.delimiter + filteredPath, // stub を優先しつつ coreutils/bash は温存
    STUB_LOG: logFile,
    ...extraEnv,
  };

  // setup.sh はリポジトリルート相対のパス（src/Directory.Build.props・自動発見の find .）を
  // 前提にするため、スクリプト自身は常に REPO_ROOT から絶対パスで呼ぶ。cwd を変えたい試験
  // （src/package.json 不在）は spawnSync の cwd で切り替える。
  const scriptAbs = path.join(REPO_ROOT, SETUP_SCRIPT);
  const r = spawnSync('bash', [scriptAbs], { cwd, env, encoding: 'utf8' });

  const raw = fs.readFileSync(logFile, 'utf8');
  const lines = raw.split('\n').filter((l) => l.length > 0);
  try {
    fs.rmSync(workdir, { recursive: true, force: true });
  } catch {
    /* best-effort cleanup */
  }
  return { status: r.status, lines, stdout: r.stdout || '', stderr: r.stderr || '' };
}

// --- テストランナー（k8s-local-up.test.js と同型） ----------------------------

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

// 事前条件: bash が利用可能で、スクリプトが正常終了すること（ハーネス自体の健全性）。
const DEFAULT = runSetup();
ok('前提: 既定実行は exit 0（stub 下で副作用なく完走）', () => {
  assert.strictEqual(DEFAULT.status, 0, `setup.sh が非0終了: ${DEFAULT.stderr}`);
});

// 受け入れ基準 1: submodule 未初期化の素の環境でも `git submodule update --init` 相当が呼ばれる。
// パスを 1 本に絞っていること（`.gitmodules` の全 submodule 一括ではない）も固定する。
ok('submodule 初期化が正しいパス（src/ai-stock-trading）ちょうど 1 回で呼ばれる', () => {
  const hits = DEFAULT.lines.filter((l) => l.startsWith('git submodule update --init '));
  assert.strictEqual(hits.length, 1, `submodule init の呼び出し回数が 1 ではない: ${JSON.stringify(hits)}`);
  assert.strictEqual(hits[0], 'git submodule update --init src/ai-stock-trading');
});

// 受け入れ基準 2: pnpm install --frozen-lockfile が src/ 配下（cwd）で呼ばれる。
ok('pnpm install --frozen-lockfile が cwd=src で呼ばれる', () => {
  const hit = DEFAULT.lines.find((l) => l.startsWith('pnpm install --frozen-lockfile'));
  assert.ok(hit, `pnpm install の呼び出しが無い: ${JSON.stringify(DEFAULT.lines)}`);
  const cwd = hit.split('@cwd=')[1];
  assert.ok(cwd, `cwd の記録が無い行: ${hit}`);
  // git-bash の $PWD は POSIX 形式（例 /c/.../src）で返る。末尾が src であることだけを見る
  // （REPO_ROOT の絶対パス自体は環境依存のため比較しない）。
  assert.ok(/[\\/]src$/.test(cwd), `pnpm の cwd が src で終わっていない: ${cwd}`);
});

// 順序: submodule 初期化は自動発見の dotnet restore（Platform.Bff.csproj が submodule を
// ProjectReference する）より前でなければならない（issue #1349 の実測どおり）。
ok('submodule 初期化は dotnet restore の自動発見ループより前に実行される', () => {
  const submoduleIdx = DEFAULT.lines.findIndex((l) => l.startsWith('git submodule update --init '));
  const restoreIdx = DEFAULT.lines.findIndex((l) => l.startsWith('dotnet restore '));
  assert.ok(submoduleIdx >= 0, 'submodule init 行が見つからない');
  assert.ok(restoreIdx >= 0, 'dotnet restore 行が見つからない（自動発見の回帰）');
  assert.ok(submoduleIdx < restoreIdx, `submodule 初期化が restore より後になっている: submodule=${submoduleIdx} restore=${restoreIdx}`);
});

// 回帰なし: 既存の自動発見（platform / knowledge の backend.slnx）は変わらず restore される。
ok('回帰なし: 既存の自動発見 restore ループはそのまま動く', () => {
  assert.ok(
    DEFAULT.lines.some((l) => l === 'dotnet restore ./src/platform/backend/backend.slnx'),
    'platform の backend.slnx が restore されない',
  );
});

// 受け入れ基準 4a: submodule 初期化が失敗しても setup.sh 全体は exit 0（fail-open）で継続する。
ok('fail-open: submodule 初期化が失敗しても exit 0 のまま継続する', () => {
  const res = runSetup({ env: { STUB_GIT_FAIL: '1' } });
  assert.strictEqual(res.status, 0, `STUB_GIT_FAIL=1 で非0終了: ${res.stderr}`);
  assert.ok(res.stdout.includes('submodule 初期化に失敗しました（継続）'), 'fail-open のログが無い');
  // submodule が失敗しても、後続の pnpm install は独立して実行される（一連の処理を道連れにしない）。
  assert.ok(res.lines.some((l) => l.startsWith('pnpm install --frozen-lockfile')), 'submodule 失敗が後続 pnpm を止めた');
});

// 受け入れ基準 4b: pnpm install が失敗しても setup.sh 全体は exit 0（fail-open）で継続する。
ok('fail-open: pnpm install が失敗しても exit 0 のまま継続する', () => {
  const res = runSetup({ env: { STUB_PNPM_FAIL: '1' } });
  assert.strictEqual(res.status, 0, `STUB_PNPM_FAIL=1 で非0終了: ${res.stderr}`);
  assert.ok(res.stdout.includes('pnpm install でエラー（継続）'), 'fail-open のログが無い');
  assert.ok(res.stdout.includes('セットアップ完了'), '後続処理（Python 例示ブロック・完了ログ）まで到達していない');
});

// 受け入れ基準 4c: pnpm 自体が無い環境では pnpm を一切呼ばず、スキップして exit 0 で継続する
// （技術非依存の安全設計。CLAUDE.md「該当しないスタックでは何もせず正常終了する」）。
ok('pnpm 不在: pnpm を呼ばずスキップし exit 0 のまま継続する', () => {
  const res = runSetup({ stubs: ['git', 'dotnet'] });
  assert.strictEqual(res.status, 0, `pnpm 不在で非0終了: ${res.stderr}`);
  assert.ok(!res.lines.some((l) => l.startsWith('pnpm ')), 'pnpm 不在なのに pnpm が呼ばれた');
  assert.ok(res.stdout.includes('pnpm または src/package.json が無いため'), 'スキップのログが無い');
  // submodule 初期化・dotnet restore は pnpm の有無に関わらず独立して動く。
  assert.ok(res.lines.some((l) => l.startsWith('git submodule update --init ')), 'pnpm 不在で submodule init まで止まった');
});

// src/package.json が無い cwd（＝ pnpm workspace ルートを認識できない環境）でも同様にスキップする。
// git 初期化ステップは相対パス `src/ai-stock-trading` を引数として渡すだけ（stub なので cwd に
// 依存した実処理はしない）ため、cwd を変えても他の分岐を壊さずにこの 1 点だけを検証できる。
ok('src/package.json 不在: pnpm セットアップをスキップし exit 0 のまま継続する', () => {
  const emptyCwd = fs.mkdtempSync(path.join(os.tmpdir(), 'setup-sh-nopkg-'));
  const res = runSetup({ cwd: emptyCwd });
  assert.strictEqual(res.status, 0, `src/package.json 不在で非0終了: ${res.stderr}`);
  assert.ok(!res.lines.some((l) => l.startsWith('pnpm ')), 'src/package.json 不在なのに pnpm が呼ばれた');
  assert.ok(res.stdout.includes('pnpm または src/package.json が無いため'), 'スキップのログが無い');
  try {
    fs.rmSync(emptyCwd, { recursive: true, force: true });
  } catch {
    /* best-effort cleanup */
  }
});

process.stdout.write(`\n${passed} 件成功\n`);
