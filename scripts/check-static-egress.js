#!/usr/bin/env node
'use strict';
/*
 * check-static-egress.js
 * 静的ビルド成果物が**外部オリジンから何も取りに行かない**ことの機械検査
 * （ADR-0031 / 08_data-egress-policy / Issue #496 / IADR-0125 決定 5）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 計画の根拠（06_technical/08_data-egress-policy.md §非LLM外部送信の統制）:
 *   | SPA フロントエンド | 外部CDN・Webフォント（Google Fonts等）・解析（analytics）・エラー報告SaaS への送信 |
 *   | すべてのアセット（JS/CSS/フォント/アイコン）を**自己ホストでバンドル**する |
 *
 * なぜ「設定」で終わらせないか:
 *   egress の統制は**退行しやすい**。Storybook のアドオンを 1 つ足す、依存を更新する——それだけで
 *   外部フォントや解析スクリプトが復活し得るが、**画面は正常に見える**。禁止されているのは通信で
 *   あって設定ではないので、通信の材料（＝ビルド成果物）を直接見る。
 *
 * 検出するもの（＝実際に取りに行く参照）:
 *   1. HTML のリソースタグ: <link href> / <script src> / <img src|srcset> / <iframe src> /
 *      <source src|srcset> / <embed src> / <video src> / <audio src> / <track src> / <use href>
 *      **<a href> は対象外**（利用者が押して初めて起きる遷移であり、読み込み時の取得ではない）。
 *   2. CSS の @import と url()。HTML の <style>（インライン CSS）にも当てる——Storybook の
 *      静的ビルドは index.html のインライン style から @font-face を参照する（実測）。
 *   3. 既知の禁止ホスト（フォント CDN・汎用 CDN・analytics・エラー報告 SaaS）は、
 *      どのファイルのどこに現れても違反とする。JS の中に文字列で埋め込まれた取得先を捕まえるため。
 *
 * 検出しないもの（＝取りに行かない URL 文字列）:
 *   XML 名前空間（http://www.w3.org/2000/svg）・JSON Schema の $schema・ドキュメントへのリンク。
 *   **除外は用途ではなくパターンで書く**——「これは大丈夫」と個別に許していくと、
 *   許可リストそのものが検査の無効化装置になる。
 *
 * 段階ポリシー（fail-open / fail-closed の使い分け）:
 *   - 引数なし: 既知の成果物ディレクトリのうち**存在するものだけ**を走査する。1 つも無ければ
 *     warn を出して exit 0（ビルド前のローカル実行や無関係な PR を赤くしない）。
 *   - `--require <dir>`: 当該ディレクトリが**無ければ fail**。CI はこちらを使う
 *     （「成果物が無いから素通り」で検査が静かに失効するのを防ぐ）。
 *
 * 使い方:
 *   node scripts/check-static-egress.js
 *   node scripts/check-static-egress.js --require src/packages/ui/storybook-static
 *   node scripts/check-static-egress.js --self-test
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const { warn, notice } = require('./lib/ci-annotate');

const REPO_ROOT = path.resolve(__dirname, '..');

/**
 * 既定の走査対象（リポジトリ相対）。08_data-egress-policy の統制対象は「SPA フロントエンド」
 * そのものであり、カタログ（Storybook）だけを見ても片手落ちである。
 */
const DEFAULT_TARGETS = ['src/packages/ui/storybook-static', 'src/platform/frontend/dist'];

/** 走査するファイル拡張子（テキストとして読めるもの）。 */
const TEXT_EXT = new Set(['.html', '.htm', '.css', '.js', '.mjs', '.cjs', '.json', '.svg', '.map']);

const SKIP_DIRS = new Set(['node_modules', '.git']);

/**
 * 既知の禁止ホスト。**用途（CDN / フォント / 解析 / エラー報告）ごとに列挙**する。
 * ここに無いホストでも、リソースタグ・CSS で参照していれば規則 1・2 が捕まえる。
 * 本リストの役目は「JS の文字列に埋め込まれた取得先」を落とすことである。
 */
const FORBIDDEN_HOSTS = [
  // Web フォント
  'fonts.googleapis.com',
  'fonts.gstatic.com',
  'use.typekit.net',
  // 汎用 CDN
  'cdn.jsdelivr.net',
  'unpkg.com',
  'cdnjs.cloudflare.com',
  'ajax.googleapis.com',
  'esm.sh',
  // アイコン CDN
  'use.fontawesome.com',
  'kit.fontawesome.com',
  // analytics
  'www.google-analytics.com',
  'www.googletagmanager.com',
  'plausible.io',
  'static.hotjar.com',
  // エラー報告 SaaS
  'browser.sentry-cdn.com',
  'ingest.sentry.io',
];

// --- 純粋ロジック（scripts/scripts.repo.test.js から単体テストする） ---------------

/** 参照先が外部オリジンか（絶対 URL またはプロトコル相対）。 */
function isExternalRef(value) {
  const v = String(value).trim();
  if (v === '') return false;
  // プロトコル相対（//example.com/...）。`//` で始まるが `///` は Windows のパス表記なので除く。
  if (/^\/\/[^/]/.test(v)) return true;
  return /^https?:\/\//i.test(v);
}

/** HTML のリソースタグから外部参照を拾う。<a href> は対象外（遷移であって取得ではない）。 */
const RESOURCE_TAG_PATTERNS = [
  { tag: 'link', attr: 'href' },
  { tag: 'script', attr: 'src' },
  { tag: 'img', attr: 'src' },
  { tag: 'img', attr: 'srcset' },
  { tag: 'iframe', attr: 'src' },
  { tag: 'source', attr: 'src' },
  { tag: 'source', attr: 'srcset' },
  { tag: 'embed', attr: 'src' },
  { tag: 'video', attr: 'src' },
  { tag: 'audio', attr: 'src' },
  { tag: 'track', attr: 'src' },
  { tag: 'use', attr: 'href' },
  { tag: 'use', attr: 'xlink:href' },
];

function findHtmlResourceRefs(content) {
  const hits = [];
  for (const { tag, attr } of RESOURCE_TAG_PATTERNS) {
    const re = new RegExp(
      `<${tag}\\b[^>]*?\\b${attr.replace(':', '\\:')}\\s*=\\s*["']([^"']*)["']`,
      'gi',
    );
    let m;
    while ((m = re.exec(content)) !== null) {
      // srcset は "url 1x, url 2x" の並びである。
      for (const candidate of m[1].split(',')) {
        const url = candidate.trim().split(/\s+/)[0];
        if (isExternalRef(url)) hits.push({ kind: 'resource', detail: `<${tag} ${attr}>`, url });
      }
    }
  }
  return hits;
}

/** CSS の @import / url() から外部参照を拾う。 */
function findCssRefs(content) {
  const hits = [];
  const importRe = /@import\s+(?:url\(\s*)?["']?([^"')\s;]+)/gi;
  let m;
  while ((m = importRe.exec(content)) !== null) {
    if (isExternalRef(m[1])) hits.push({ kind: 'css', detail: '@import', url: m[1] });
  }
  const urlRe = /url\(\s*["']?([^"')]+)["']?\s*\)/gi;
  while ((m = urlRe.exec(content)) !== null) {
    if (isExternalRef(m[1])) hits.push({ kind: 'css', detail: 'url()', url: m[1] });
  }
  return hits;
}

/** 既知の禁止ホストへの言及を拾う（どのファイルのどこに現れても違反）。 */
function findForbiddenHosts(content) {
  const hits = [];
  for (const host of FORBIDDEN_HOSTS) {
    if (content.includes(host)) hits.push({ kind: 'forbidden-host', detail: host, url: host });
  }
  return hits;
}

/** 1 ファイルを検査する。拡張子に応じて適用する規則を変える。 */
function inspectFile(relPath, content) {
  const ext = path.extname(relPath).toLowerCase();
  const hits = [];

  // 規則 1: HTML と SVG。JS にも適用する——バンドルが HTML 断片を文字列で持ち、
  // 実行時に注入する形の取得を捕まえるためである。
  if (ext !== '.css') hits.push(...findHtmlResourceRefs(content));
  // 規則 2: CSS。**HTML にも当てる**——`<style>` のインライン CSS に @font-face が置かれる形が
  // 実在する（Storybook の静的ビルドは index.html のインライン style からフォントを参照している）。
  // JS にもインライン CSS（`@import` 付きの文字列）が入り得るので当てる。
  hits.push(...findCssRefs(content));
  // 規則 3: 既知の禁止ホストは全種類に当てる。
  hits.push(...findForbiddenHosts(content));

  // 同じ (kind, url) の重複は 1 件へ畳む（バンドルの繰り返しでノイズになるため）。
  const seen = new Set();
  return hits.filter((h) => {
    const key = `${h.kind}\0${h.detail}\0${h.url}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  }).map((h) => ({ ...h, file: relPath }));
}

/** ディレクトリ配下のテキストファイルを列挙する（相対パスの配列）。 */
function listTextFiles(dir, base = dir) {
  const out = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (SKIP_DIRS.has(entry.name)) continue;
      out.push(...listTextFiles(path.join(dir, entry.name), base));
    } else if (TEXT_EXT.has(path.extname(entry.name).toLowerCase())) {
      out.push(path.relative(base, path.join(dir, entry.name)).split(path.sep).join('/'));
    }
  }
  return out.sort();
}

/** 1 ディレクトリを走査する。 */
function scanDirectory(absDir) {
  const files = listTextFiles(absDir);
  const violations = [];
  for (const rel of files) {
    const content = fs.readFileSync(path.join(absDir, rel), 'utf8');
    violations.push(...inspectFile(rel, content));
  }
  return { fileCount: files.length, violations };
}

/** 引数を解釈する。 */
function parseArgs(argv) {
  const required = [];
  const extra = [];
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === '--require') {
      const value = argv[i + 1];
      if (!value) throw new Error('--require にはディレクトリを指定する');
      required.push(value);
      i += 1;
    } else if (!argv[i].startsWith('--')) {
      extra.push(argv[i]);
    }
  }
  return { required, extra };
}

// --- 実行 ---------------------------------------------------------------------

function main(argv) {
  const root = process.env.STATIC_EGRESS_ROOT || REPO_ROOT;
  let args;
  try {
    args = parseArgs(argv);
  } catch (e) {
    process.stderr.write(`✗ ${e.message}\n`);
    return 1;
  }

  const requested = [...args.required, ...args.extra];
  const targets = requested.length > 0 ? requested : DEFAULT_TARGETS;
  const requiredSet = new Set(args.required);

  const scanned = [];
  const violations = [];
  let missingRequired = 0;

  for (const target of targets) {
    const abs = path.resolve(root, target);
    if (!fs.existsSync(abs)) {
      if (requiredSet.has(target)) {
        process.stderr.write(`✗ 走査対象が存在しない（--require）: ${target}\n`);
        missingRequired += 1;
      }
      continue;
    }
    const result = scanDirectory(abs);
    scanned.push({ target, fileCount: result.fileCount });
    violations.push(...result.violations.map((v) => ({ ...v, target })));
  }

  if (missingRequired > 0) return 1;

  if (scanned.length === 0) {
    // 段階ポリシー: 明示指定が無い場合の「成果物が無い」は fail にしない。
    // 代わりに warn を出し、CI では --require で fail-closed にする。
    warn(
      'check-static-egress: 走査できるビルド成果物が 1 つも無い' +
        `（探した先: ${targets.join(' / ')}）。CI では --require で指定して fail-closed にすること。`,
    );
    return 0;
  }

  if (violations.length > 0) {
    process.stderr.write(
      `✗ ビルド成果物に外部オリジンへの参照が ${violations.length} 件ある` +
        '（08_data-egress-policy: 全アセットを自己ホストでバンドルする）。\n',
    );
    for (const v of violations) {
      process.stderr.write(`  - ${v.target}/${v.file}: ${v.detail} -> ${v.url}\n`);
    }
    return 1;
  }

  for (const s of scanned) {
    process.stdout.write(
      `[check-static-egress] OK: ${s.target}（${s.fileCount} ファイル）に外部オリジンからの取得はありません。\n`,
    );
  }
  notice(`check-static-egress: ${scanned.length} 個の成果物ディレクトリを走査した。`);
  return 0;
}

// --- 自己試験 -----------------------------------------------------------------

function selfTest() {
  const assert = require('assert');
  const failures = [];
  const ok = (name, fn) => {
    try {
      fn();
      process.stdout.write(`  ok   ${name}\n`);
    } catch (e) {
      failures.push(name);
      process.stdout.write(`  FAIL ${name}: ${e.message}\n`);
    }
  };

  ok('isExternalRef: 絶対 URL とプロトコル相対を外部と判定', () => {
    assert.strictEqual(isExternalRef('https://example.com/a.js'), true);
    assert.strictEqual(isExternalRef('http://example.com/a.js'), true);
    assert.strictEqual(isExternalRef('//example.com/a.js'), true);
  });

  ok('isExternalRef: 相対・絶対パス・data: は外部でない', () => {
    assert.strictEqual(isExternalRef('./assets/a.js'), false);
    assert.strictEqual(isExternalRef('/assets/a.js'), false);
    assert.strictEqual(isExternalRef('data:image/svg+xml;base64,AAAA'), false);
    assert.strictEqual(isExternalRef(''), false);
  });

  ok('HTML: <a href> の外部リンクは違反にしない（遷移であって取得ではない）', () => {
    const hits = inspectFile('index.html', '<a href="https://storybook.js.org/docs/x">docs</a>');
    assert.deepStrictEqual(hits, []);
  });

  ok('HTML: 外部の <script src> を検出する（負例）', () => {
    const hits = inspectFile('index.html', '<script src="https://cdn.example.com/x.js"></script>');
    assert.strictEqual(hits.length, 1);
    assert.strictEqual(hits[0].kind, 'resource');
    assert.strictEqual(hits[0].detail, '<script src>');
  });

  ok('HTML: 外部 Web フォントの <link href> を検出する（負例）', () => {
    const hits = inspectFile(
      'index.html',
      '<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter">',
    );
    // リソースタグとしても、既知の禁止ホストとしても当たる。
    assert.ok(hits.some((h) => h.kind === 'resource'));
    assert.ok(hits.some((h) => h.kind === 'forbidden-host'));
  });

  ok('HTML: 自己ホストの参照は違反にしない（正例）', () => {
    const hits = inspectFile(
      'index.html',
      '<link rel="stylesheet" href="./assets/a.css"><script src="./assets/a.js"></script>' +
        '<img src="/logo.svg">',
    );
    assert.deepStrictEqual(hits, []);
  });

  ok('SVG の名前空間 URI は違反にしない（取りに行かない URL 文字列）', () => {
    const hits = inspectFile('icon.svg', '<svg xmlns="http://www.w3.org/2000/svg"><use href="#a"/></svg>');
    assert.deepStrictEqual(hits, []);
  });

  ok('JSON Schema の $schema は違反にしない', () => {
    const hits = inspectFile('components.json', '{"$schema":"https://ui.shadcn.com/schema.json"}');
    assert.deepStrictEqual(hits, []);
  });

  ok('CSS: 外部 @import と url() を検出する（負例）', () => {
    const imported = inspectFile('a.css', "@import url('https://fonts.googleapis.com/css2');");
    assert.ok(imported.some((h) => h.kind === 'css' && h.detail === '@import'));
    const url = inspectFile('a.css', '@font-face{src:url(https://cdn.example.com/f.woff2)}');
    assert.ok(url.some((h) => h.kind === 'css' && h.detail === 'url()'));
  });

  ok('HTML の <style> 内の外部 @font-face も検出する（負例）', () => {
    const hits = inspectFile(
      'index.html',
      '<style>@font-face{font-family:X;src:url(https://fonts.gstatic.com/s/x.woff2)}</style>',
    );
    assert.ok(hits.some((h) => h.kind === 'css' && h.detail === 'url()'));
  });

  ok('HTML の <style> 内の自己ホスト @font-face は違反にしない（正例。実在の形）', () => {
    const hits = inspectFile(
      'index.html',
      "<style>@font-face{font-family:X;src:url('./sb-common-assets/nunito-sans-regular.woff2')}</style>",
    );
    assert.deepStrictEqual(hits, []);
  });

  ok('CSS: 自己ホストの url() は違反にしない（正例）', () => {
    const hits = inspectFile('a.css', "@font-face{src:url('./fonts/a.woff2')}");
    assert.deepStrictEqual(hits, []);
  });

  ok('JS 内の文字列でも既知の禁止ホストを検出する（負例）', () => {
    const hits = inspectFile('bundle.js', 'const u = "https://www.google-analytics.com/collect";');
    assert.ok(hits.some((h) => h.kind === 'forbidden-host'));
  });

  ok('同じ参照の繰り返しは 1 件へ畳む', () => {
    const hits = inspectFile(
      'index.html',
      '<script src="https://cdn.example.com/x.js"></script>'.repeat(3),
    );
    assert.strictEqual(hits.length, 1);
  });

  ok('parseArgs: --require を解釈する', () => {
    assert.deepStrictEqual(parseArgs(['--require', 'a']).required, ['a']);
    assert.deepStrictEqual(parseArgs(['b']).extra, ['b']);
    assert.throws(() => parseArgs(['--require']), /--require/);
  });

  // main() の段階ポリシーを実ディレクトリで確認する。
  ok('main: --require で指定した先が無ければ exit 1（fail-closed）', () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), 'egress-'));
    try {
      process.env.STATIC_EGRESS_ROOT = base;
      assert.strictEqual(main(['--require', 'no-such-dir']), 1);
    } finally {
      delete process.env.STATIC_EGRESS_ROOT;
      fs.rmSync(base, { recursive: true, force: true });
    }
  });

  ok('main: 引数なしで成果物が無ければ warn して exit 0（fail-open）', () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), 'egress-'));
    try {
      process.env.STATIC_EGRESS_ROOT = base;
      assert.strictEqual(main([]), 0);
    } finally {
      delete process.env.STATIC_EGRESS_ROOT;
      fs.rmSync(base, { recursive: true, force: true });
    }
  });

  ok('main: 成果物に外部参照があれば exit 1', () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), 'egress-'));
    try {
      const dist = path.join(base, 'dist');
      fs.mkdirSync(dist, { recursive: true });
      fs.writeFileSync(
        path.join(dist, 'index.html'),
        '<script src="https://cdn.example.com/x.js"></script>',
      );
      process.env.STATIC_EGRESS_ROOT = base;
      assert.strictEqual(main(['--require', 'dist']), 1);
      // 外部参照を除けば通る。
      fs.writeFileSync(path.join(dist, 'index.html'), '<script src="./x.js"></script>');
      assert.strictEqual(main(['--require', 'dist']), 0);
    } finally {
      delete process.env.STATIC_EGRESS_ROOT;
      fs.rmSync(base, { recursive: true, force: true });
    }
  });

  process.stdout.write(
    failures.length === 0
      ? '\n[check-static-egress --self-test] all passed\n'
      : `\n[check-static-egress --self-test] ${failures.length} failed\n`,
  );
  return failures.length === 0 ? 0 : 1;
}

module.exports = {
  DEFAULT_TARGETS,
  FORBIDDEN_HOSTS,
  isExternalRef,
  findHtmlResourceRefs,
  findCssRefs,
  findForbiddenHosts,
  inspectFile,
  listTextFiles,
  scanDirectory,
  parseArgs,
  main,
};

if (require.main === module) {
  const argv = process.argv.slice(2);
  process.exit(argv.includes('--self-test') ? selfTest() : main(argv));
}
