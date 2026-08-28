#!/usr/bin/env node
/**
 * NFR, #1012: 既定資格情報の再混入を止めるラチェット。
 *
 * **イメージへ焼かれる構成（appsettings.json）と合成ルート（Program.cs）に、資格情報つきの
 * 接続文字列リテラルを書けなくする。** 書けてしまうと、構成の注入漏れが「起動失敗」ではなく
 * 「既定の資格情報で接続成功」へ倒れ、誤った DB へ書き込んだまま健全に見える（#1012 の欠陥）。
 *
 * 検出するのは **`Username=` / `Password=` を伴う接続文字列**と、資格情報つきの `amqp://user:pass@`
 * である。ホスト名だけの値（`Host=postgres;Database=x`）は資格情報ではないので落とさない
 * ——「秘密を書かせない」検査であって「設定を書かせない」検査ではない。
 *
 * 除外（理由つき）:
 *   - `appsettings.Development.json`: **イメージの本番既定ではない**（`dotnet run` のローカル利便）。
 *   - `src/ai-stock-trading/**`: submodule（本リポジトリの規約の対象外）。
 *   - ビルド生成物（bin / obj 配下）。
 *   - テストプロジェクト: 資格情報を持たないダミーを使う（`Host=localhost;Database=x_test`）。
 *
 * **ラチェット**: 既知の残件（RabbitMQ の `amqp://guest:guest@` 13 箇所）は
 * `scripts/default-credentials-baseline.json` に凍結してある。**増やせないが、減らすのは自由**
 * （baseline に在るのに実在しない行は「直った」と見なし、baseline の更新を促して落とす＝前方一方向）。
 * RabbitMQ を射程から外した理由は作業仕様書 `20260828_issue-1012_default-credentials.md` §対象と除外。
 *
 * 使い方: `node scripts/check-default-credentials.js [--self-test] [--update]`
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 */
'use strict';

const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const BASELINE_PATH = path.join(__dirname, 'default-credentials-baseline.json');

/** 走査対象（配備物へ焼かれる構成と合成ルート）。 */
const SCAN_ROOTS = ['src/platform/backend', 'src/knowledge/backend'];

/** 除外パス（前方一致・POSIX 区切り）。 */
const EXCLUDED_DIRS = ['/bin/', '/obj/', '/tests/', 'src/ai-stock-trading/'];

/** 資格情報つき接続文字列の検出。値の中に利用者名かパスワードが在るものだけを違反にする。 */
const PATTERNS = [
  {
    kind: 'connection-string-credentials',
    re: /"[^"\n]*(?:Username|User ID|Password)\s*=\s*[^";\n]+[^"\n]*"/i,
    hint: 'Username= / Password= を含む接続文字列リテラル',
  },
  {
    kind: 'amqp-credentials',
    re: /amqp:\/\/[^:\/@"\s]+:[^@"\s]+@/i,
    hint: '資格情報つきの amqp:// URL',
  },
];

function isExcluded(relPath) {
  const p = `/${relPath.split(path.sep).join('/')}`;
  return EXCLUDED_DIRS.some((d) => p.includes(d.startsWith('/') ? d : `/${d}`) || p.includes(d));
}

function targetFiles(root) {
  const found = [];
  const walk = (dir) => {
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      const full = path.join(dir, e.name);
      const rel = path.relative(REPO_ROOT, full);
      if (isExcluded(rel)) continue;
      if (e.isDirectory()) {
        walk(full);
        continue;
      }
      // appsettings.Development.json は除外（本番既定ではない）。
      if (e.name === 'Program.cs') found.push(rel);
      else if (/^appsettings(\.[A-Za-z]+)?\.json$/.test(e.name) && !/\.Development\.json$/.test(e.name))
        found.push(rel);
    }
  };
  walk(path.join(REPO_ROOT, root));
  return found;
}

/** 1 ファイルの違反を返す（行番号つき）。 */
function violationsIn(relPath, content) {
  const out = [];
  content.split('\n').forEach((line, i) => {
    for (const p of PATTERNS) {
      if (p.re.test(line)) out.push({ file: relPath, line: i + 1, kind: p.kind, hint: p.hint });
    }
  });
  return out;
}

/** baseline は「ファイル:種別」の集合で持つ（行番号は無関係な編集で動くため鍵にしない）。 */
const keyOf = (v) => `${v.file}::${v.kind}`;

function readBaseline() {
  if (!fs.existsSync(BASELINE_PATH)) return { known: [] };
  return JSON.parse(fs.readFileSync(BASELINE_PATH, 'utf8'));
}

function run(update) {
  const files = SCAN_ROOTS.flatMap(targetFiles);
  if (files.length === 0) {
    console.error('[check-default-credentials] 走査対象が 0 件である（パスの想定が壊れている）。');
    process.exit(1);
  }
  const violations = files.flatMap((f) =>
    violationsIn(f, fs.readFileSync(path.join(REPO_ROOT, f), 'utf8')),
  );
  const seen = new Map();
  for (const v of violations) if (!seen.has(keyOf(v))) seen.set(keyOf(v), v);

  if (update) {
    const baseline = readBaseline();
    baseline.known = [...seen.values()]
      .map((v) => ({ file: v.file, kind: v.kind }))
      .sort((a, b) => keyOf(a).localeCompare(keyOf(b)));
    fs.writeFileSync(BASELINE_PATH, `${JSON.stringify(baseline, null, 2)}\n`);
    console.log(`[check-default-credentials] baseline を更新した（既知 ${baseline.known.length} 件）。`);
    return;
  }

  const baseline = readBaseline();
  const known = new Set(baseline.known.map(keyOf));
  const added = [...seen.values()].filter((v) => !known.has(keyOf(v)));
  const removed = [...known].filter((k) => !seen.has(k));

  if (added.length > 0 || removed.length > 0) {
    console.error('[check-default-credentials] baseline との差分を検出しました:\n');
    for (const v of added) console.error(`  [added]   ${v.file} [${v.kind}] ${v.hint}`);
    for (const k of removed) console.error(`  [removed] ${k} が実在しない（直ったなら --update で baseline を縮める）`);
    console.error(
      '\n接続先は構成（環境変数 / Secret）から注入する。未設定なら起動時に落とすこと（#1012 / IADR-0286）。',
    );
    process.exit(1);
  }
  console.log(
    `[check-default-credentials] OK: ${files.length} 件を走査し、新規の既定資格情報はありません（既知の残件 ${known.size} 件は baseline 済み）。`,
  );
}

function selfTest() {
  const cases = [
    ['違反: 接続文字列のパスワード', '"Host=postgres;Database=x;Username=kp;Password=kp"', true],
    ['違反: 接続文字列の利用者名のみ', '"Host=postgres;Username=kp"', true],
    ['違反: 資格情報つき amqp', '"amqp://guest:guest@rabbitmq:5672"', true],
    ['合格: ホストと DB だけ', '"Host=postgres;Port=5432;Database=graph_svc"', false],
    ['合格: 資格情報なし amqp', '"amqp://rabbitmq:5672"', false],
    ['合格: 注入を促す例外文', '"ConnectionStrings__DefaultConnection で注入する"', false],
  ];
  let failed = 0;
  for (const [name, line, shouldFlag] of cases) {
    const got = violationsIn('x', line).length > 0;
    if (got !== shouldFlag) {
      console.error(`  ✗ ${name}（期待 ${shouldFlag} / 実際 ${got}）`);
      failed++;
    } else console.log(`  ok  ${name}`);
  }
  if (failed > 0) process.exit(1);
  console.log(`✓ self-test: ${cases.length} 件すべて通過`);
}

if (process.argv.includes('--self-test')) selfTest();
else run(process.argv.includes('--update'));
