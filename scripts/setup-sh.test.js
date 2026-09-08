#!/usr/bin/env node
'use strict';
/*
 * setup-sh.test.js
 * Issue #1349 / IADR-0087 / IADR-0180: scripts/setup.sh が「ビルド・テストを実走できる状態」を
 * 用意していることを、**発行コマンド列**で固定する smoke test。
 *
 * 方式（IADR-0087 と同型）: bash stub-on-PATH（setup.sh は無改変）。
 *   外部バイナリ（git / pnpm / dotnet / curl）を PATH 上の「記録スタブ」へ差し替え、
 *   副作用ゼロで setup.sh を実行し、発行コマンド列を採取してアサートする。
 *   実際に submodule を取りに行くことも、依存を入れることもしない。
 *
 *   🔴 **`git config` だけは実物へ委譲する。** submodule のパスは `.gitmodules` から
 *   **導出**されるのが決定事項（#1349 決定 2）であり、そこを固定値のスタブで返すと
 *   「導出している」ことを試験できない（雛形の `.gitmodules` を書き換えたら
 *   発行されるパスも変わる、という主張が空になる）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ・bash と git は前提ツール）。
 * 実行: node scripts/setup-sh.test.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const SETUP = path.join(REPO_ROOT, 'scripts', 'setup.sh');

let passed = 0;
function test(name, fn) {
  try {
    fn();
    passed += 1;
    console.log(`  ok  ${name}`);
  } catch (err) {
    console.error(`  NG  ${name}`);
    throw err;
  }
}

// bash が無い環境（素の Windows cmd 等）では実走できない。**黙って緑にしない** ——
// 何を測っていないかを出して非ゼロで終える（IADR-0130 の「0 件で緑にしない」と同じ向き）。
function requireBash() {
  const probe = spawnSync('bash', ['-c', 'echo ok'], { encoding: 'utf8' });
  if (probe.error || probe.status !== 0) {
    console.error('[setup-sh.test] bash が実行できないため測定できません（CI は ubuntu なので走ります）。');
    process.exit(1);
  }
}

// 実物の git（`git config` の委譲先）。スタブより先に解決しておく。
function realGitPath() {
  const probe = spawnSync('bash', ['-lc', 'command -v git'], { encoding: 'utf8' });
  const found = (probe.stdout || '').trim().split('\n')[0];
  assert.ok(found, 'git が見つからない（本試験の前提ツールである）');
  return found;
}

requireBash();
const REAL_GIT = realGitPath();

/**
 * 雛形リポジトリを作り、スタブ付きで setup.sh を走らせて発行コマンド列を返す。
 * @param {{ withPnpm?: boolean, gitFails?: boolean, gitmodules?: string }} opts
 */
function run(opts = {}) {
  const { withPnpm = true, gitFails = false, gitmodules = null } = opts;
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'setup-sh-'));
  const bin = path.join(root, 'bin');
  fs.mkdirSync(bin);
  fs.mkdirSync(path.join(root, 'src'));
  const log = path.join(root, 'calls.log');

  // 雛形の宣言。**`src/` 直下でない submodule も 1 つ置く**（取らないことを測るため）。
  fs.writeFileSync(path.join(root, '.gitmodules'), gitmodules ?? [
    '[submodule "src/ai-stock-trading"]',
    '\tpath = src/ai-stock-trading',
    '\turl = https://example.invalid/ast.git',
    '[submodule "public/theme"]',
    '\tpath = public/theme',
    '\turl = https://example.invalid/theme.git',
    '',
  ].join('\n'));

  // restore ループが回るための最小の入力（順序の主張に要る）。
  fs.writeFileSync(path.join(root, 'app.slnx'), '<Solution />\n');
  fs.writeFileSync(path.join(root, 'src', 'pnpm-workspace.yaml'), "packages:\n  - 'platform/frontend'\n");
  fs.writeFileSync(path.join(root, 'src', 'Directory.Build.props'),
    '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n');

  const stub = (name, body) => {
    const file = path.join(bin, name);
    fs.writeFileSync(file, `#!/usr/bin/env bash\n${body}\n`, { mode: 0o755 });
    fs.chmodSync(file, 0o755);
  };

  // git: 記録しつつ、`config` だけは実物へ委譲する（上の 🔴）。
  stub('git', [
    `printf 'git %s\\n' "$*" >> ${JSON.stringify(log)}`,
    'if [ "${1:-}" = "config" ]; then',
    `  exec ${JSON.stringify(REAL_GIT)} "$@"`,
    'fi',
    gitFails ? 'echo "fatal: could not read Username" >&2; exit 128' : 'exit 0',
  ].join('\n'));

  stub('dotnet', `printf 'dotnet %s\\n' "$*" >> ${JSON.stringify(log)}\nexit 0`);
  stub('curl', `printf 'curl %s\\n' "$*" >> ${JSON.stringify(log)}\nexit 1`);
  if (withPnpm) {
    stub('pnpm', `printf 'pnpm(%s) %s\\n' "$(basename "$PWD")" "$*" >> ${JSON.stringify(log)}\nexit 0`);
  }

  // 🔴 「pnpm が無い環境」は**継承 PATH から実物を取り除いて**作る ——
  // スタブを置かないだけでは、開発機や CI に入っている実物の pnpm がそのまま見つかり、
  // 「無ければスキップする」を測ったつもりで**実物を走らせてしまう**（実測で踏んだ）。
  const inherited = (process.env.PATH || '').split(path.delimiter);
  const usable = withPnpm
    ? inherited
    : inherited.filter((dir) => !['pnpm', 'pnpm.cmd', 'pnpm.exe', 'pnpm.CMD']
      .some((exe) => dir && fs.existsSync(path.join(dir, exe))));

  const result = spawnSync('bash', [SETUP], {
    cwd: root,
    encoding: 'utf8',
    env: { ...process.env, PATH: [bin, ...usable].join(path.delimiter), HOME: root },
  });

  const calls = fs.existsSync(log)
    ? fs.readFileSync(log, 'utf8').split('\n').filter(Boolean)
    : [];
  return { status: result.status, stdout: result.stdout || '', calls, root };
}

console.log('setup-sh.test.js');

// ── submodule（#1349 決定 1・2） ────────────────────────────────────────────

test('ユニット submodule を初期化する（.gitmodules から導出する）', () => {
  const { calls } = run();
  assert.ok(
    calls.includes('git submodule update --init src/ai-stock-trading'),
    `submodule の初期化が発行されていない:\n${calls.join('\n')}`,
  );
});

test('🔴 初期化は restore より前に発行される（後ろだとその回の restore は失敗したままになる）', () => {
  const { calls } = run();
  const sub = calls.findIndex((c) => c.startsWith('git submodule update --init'));
  const restore = calls.findIndex((c) => c.startsWith('dotnet restore'));
  assert.ok(sub >= 0, 'submodule の初期化が無い');
  assert.ok(restore >= 0, '★ 陽性対照 —— restore も実際に発行されている');
  assert.ok(sub < restore, `順序が逆である（submodule=${sub} / restore=${restore}）:\n${calls.join('\n')}`);
});

test('`src/` 直下でない submodule は取らない（CI の導出式と同じ）', () => {
  const { calls } = run();
  assert.ok(
    !calls.some((c) => c.includes('public/theme')),
    `src/ 以外の submodule まで取っている:\n${calls.join('\n')}`,
  );
});

test('パスは固定値ではなく `.gitmodules` の宣言に従う', () => {
  const { calls } = run({
    gitmodules: [
      '[submodule "src/other-unit"]',
      '\tpath = src/other-unit',
      '\turl = https://example.invalid/other.git',
      '',
    ].join('\n'),
  });
  assert.ok(
    calls.includes('git submodule update --init src/other-unit'),
    `宣言したパスが使われていない（直書きの疑い）:\n${calls.join('\n')}`,
  );
  assert.ok(
    !calls.some((c) => c.includes('ai-stock-trading')),
    `宣言に無いパスを取りに行っている（直書き）:\n${calls.join('\n')}`,
  );
});

test('submodule の取得に失敗しても exit 0 で継続し、restore まで進む（fail-open）', () => {
  const { status, calls } = run({ gitFails: true });
  assert.strictEqual(status, 0, 'fail-open が壊れている（セットアップがセッションを止めている）');
  assert.ok(
    calls.some((c) => c.startsWith('dotnet restore')),
    `失敗の後に restore へ進んでいない:\n${calls.join('\n')}`,
  );
});

// ── Node 依存（#1349 決定 3） ───────────────────────────────────────────────

test('pnpm workspace の依存を `src/` で導入する（--frozen-lockfile）', () => {
  const { calls } = run();
  const install = calls.find((c) => c.startsWith('pnpm('));
  assert.ok(install, `pnpm install が発行されていない:\n${calls.join('\n')}`);
  assert.ok(install.includes('install --frozen-lockfile'), `ロックを更新する形になっている: ${install}`);
  assert.ok(install.startsWith('pnpm(src)'), `pnpm を src/ 以外で実行している: ${install}`);
});

test('pnpm が無ければ発行せず、exit 0 で終わる（corepack が動かない環境がある）', () => {
  const { status, calls, stdout } = run({ withPnpm: false });
  assert.strictEqual(status, 0);
  assert.ok(!calls.some((c) => c.startsWith('pnpm')), 'pnpm が無いのに発行している');
  assert.ok(
    stdout.includes('pnpm が無いため'),
    `何をしなかったかがログに出ていない（黙ってスキップしている）:\n${stdout}`,
  );
});

// ── 既存の挙動を壊していないこと ───────────────────────────────────────────

test('restore ループは従来どおり全ソリューションを回る', () => {
  const { calls } = run();
  assert.ok(calls.some((c) => c.includes('restore') && c.includes('app.slnx')));
});

test('矛盾していた npm のコメントアウトは残っていない（pnpm workspace と食い違う手順）', () => {
  const text = fs.readFileSync(SETUP, 'utf8');
  assert.ok(!/npm ci/.test(text), 'npm ci の記述が残っている（本リポジトリは pnpm workspace）');
});

test('python のコメントアウトは残っている（opt-in である根拠を別の試験が引いている）', () => {
  const text = fs.readFileSync(SETUP, 'utf8');
  assert.ok(/python3 -m pip/.test(text), 'python の opt-in 例が消えている（scripts.repo.test.js が引いている）');
});

console.log(`\n✓ ${passed} tests passed`);
