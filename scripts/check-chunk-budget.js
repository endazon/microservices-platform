#!/usr/bin/env node
'use strict';
/*
 * check-chunk-budget.js
 * SPA のバンドル分割（manualChunks）の**規則構成**と**初期ロード量**の機械検査
 * （NFR / ADR-0031 / IADR-0134 / issue #556）。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * ■ なぜ要るか
 *   #512（PR #549）が SPA のバンドルをルート単位に分割し、Vite の 500 kB 警告を解消した。
 *   分割の設計は `vite.config.ts` の `manualChunks` の 3 規則（vendor-react / ui / vendor-query）
 *   に依存しているが、**その規則構成を守る機械が無かった**。#512 の変異試験は、規則を外しても
 *   ビルドが成功し警告も出ない（＝素通りする）ことを実測している。
 *
 * ■ ★ 何が実際に規則の欠落を捕まえるか（#556 で再実測。**issue の設計案では捕まらなかった**）
 *   issue #556 は判定を 3 種（1 チャンク 500 kB 超 = fail / 初期ロード合計の ratchet = fail /
 *   1 kB 未満の遅延チャンク数の増加 = warn）と書いていた。**この 3 種では規則の欠落を落とせない。**
 *   本 issue の着手時に 2 規則それぞれを外して実測した結果が下表である（baseline: 初期ロード
 *   合計 577.68 kB / 最大チャンク 274.46 kB / 1 kB 未満の遅延チャンク 5 本）。
 *
 *   | 変異 | 最大チャンク | 初期ロード合計 | 1 kB 未満 | 当該チャンク |
 *   | --- | --- | --- | --- | --- |
 *   | `ui` 規則を外す        | 306.69 kB（< 500） | **544.87 kB（減る）** | 5 → 9 | **消える** |
 *   | `vendor-react` を外す  | 458.92 kB（< 500） | 578.55 kB（**+0.87 kB = +0.15%**） | 5 → 5（不変） | **消える** |
 *
 *   - **500 kB 判定はどちらでも発火しない**（458.92 kB が上限の 91.8% で止まる）。
 *   - **ratchet は `ui` で「減る」ため発火しない。** 規則を外すと初期ロードが増えるとは限らない
 *     ——`ui` の 65 kB は index へ吸収されるが、同時に遅延側へ散ることで初期分が減る。
 *   - **`vendor-react` の ratchet 発火は +0.15% の紙一重**であり、検出器として当てにできない。
 *   - **1 kB 未満の本数は `vendor-react` では 1 本も動かない。**
 *
 *   よって**規則の欠落を確実に捕まえるのは「名前つきチャンクが実在するか」だけ**である。
 *   本スクリプトはこれを**判定 1（fail）**として最初に置く。issue の 3 種は判定 2〜4 として
 *   併せ持つ（それぞれ別の退行——巨大チャンクの再発・初期ロードの肥大・往復の増加——を見る）。
 *
 * ■ 判定
 *   1. **必須チャンクの実在**（fail）    baseline.requiredChunks の各名前が dist/assets に在ること
 *   2. **1 チャンクの上限**（fail）      baseline.maxChunkBytes を超えるチャンクが無いこと
 *   3. **初期ロード合計の ratchet**（fail）baseline.initialTotalBytes を超えないこと
 *   4. **1 kB 未満の遅延チャンク数**（warn）baseline.smallLazyChunks から増えていないこと
 *
 *   判定 3 の床は `--update` で更新する（#512 の分割見直し等、意図的に増やすとき）。
 *   判定 4 が warn なのは、往復の増加が**性能の劣化ではあっても壊れではない**ためである
 *   （fail にすると遅延ルートを 1 本足すたびに他人の PR が止まる）。
 *
 * ■ 初期ロードの定義
 *   `dist/index.html` が**読み込み時に取得する JS** ——エントリの <script type="module" src> と
 *   <link rel="modulepreload" href> の和。ここを「assets/ の全 JS」にすると遅延チャンクまで数えて
 *   しまい、ルート分割を進めるほど床が上がるという逆向きの指標になる。
 *   **CSS は数えない** ——`manualChunks` は JS の分割規則であり、CSS は影響を受けない
 *   （数えると Tailwind の増減で JS の規則検査が揺れる）。
 *
 * ■ 段階ポリシー（fail-open / fail-closed）——check-static-egress.js と同じ作法
 *   - 引数なし: dist が無ければ warn を出して exit 0（ビルド前のローカル実行を赤くしない）。
 *   - `--require`: dist が無ければ **fail**（「成果物が無いから素通り」で静かに失効するのを防ぐ）。
 *     **CI はこちらを使う。**
 *
 * 使い方:
 *   node scripts/check-chunk-budget.js
 *   node scripts/check-chunk-budget.js --require
 *   node scripts/check-chunk-budget.js --dist <dir> --require
 *   node scripts/check-chunk-budget.js --update
 *   node scripts/check-chunk-budget.js --self-test
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const { warn } = require('./lib/ci-annotate');

const REPO_ROOT = path.resolve(__dirname, '..');
const DEFAULT_DIST = 'src/platform/frontend/dist';
const BASELINE_PATH = path.join(__dirname, 'chunk-budget-baseline.json');

/** kB 表示（Vite と同じ 1000 進。ログを Vite のビルド出力と突き合わせられるようにする）。 */
const kb = (bytes) => `${(bytes / 1000).toFixed(2)} kB`;

/**
 * Vite が出すチャンクのファイル名は `<name>-<hash>.js`。
 *
 * **検出しないこと（意図的な穴）**: ハッシュには `-` が現れる（実測: `CHRHn5b-` / `BY-IZaSY`）ため、
 * `ui-widgets-extra-<hash>.js` のような**同じ接頭辞を持つ別チャンク**を `ui` の実在と誤認する。
 * ハッシュ長を 8 に固定すれば分離できる（本リポの実測はすべて 8 文字）が、**採らない** ——
 * Vite / Rollup がハッシュ長を変えた瞬間に「必須チャンクが無い」という**偽陽性**で他人の PR を
 * 全部止めることになる。`.claude/rules/traceability.md` および [[IADR-0145]] 決定 3 の方針
 * （**検出漏れは開示してよいが、偽陽性は塞ぐ**）に従い、緩い側を採って穴を明記する。
 * 実害は「規則を外したうえで、外した名前と同じ接頭辞の遅延チャンクを作った場合」に限られる。
 */
function chunkNameMatcher(name) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return new RegExp(`^${escaped}-[A-Za-z0-9_-]+\\.js$`);
}

/**
 * index.html が読み込み時に取得する JS の集合を返す（`/assets/foo-hash.js` 形式）。
 * 取りこぼすと初期ロードを過小に数えるため、entry と modulepreload の両方を見る。
 */
function parseInitialJs(html) {
  const found = new Set();
  for (const m of html.matchAll(/<script[^>]*\ssrc="([^"]+\.js)"/g)) found.add(m[1]);
  // **属性の順序に依存させない。** `rel` が `href` より前に来る前提で 1 本の正規表現にすると、
  // 順序が変わった瞬間に初期ロードを**過小に数える**（＝床が下がり、判定 3 が沈黙側へ倒れる）。
  // タグを丸ごと取ってから 2 つの属性を別々に見る。
  for (const m of html.matchAll(/<link\b[^>]*>/g)) {
    const tag = m[0];
    if (!/\srel="modulepreload"/.test(tag)) continue;
    const href = tag.match(/\shref="([^"]+\.js)"/);
    if (href) found.add(href[1]);
  }
  return found;
}

/** dist を走査して測定値を返す。 */
function measure(distDir) {
  const indexHtml = path.join(distDir, 'index.html');
  if (!fs.existsSync(indexHtml)) throw new Error(`index.html が無い: ${indexHtml}`);
  const assetsDir = path.join(distDir, 'assets');
  if (!fs.existsSync(assetsDir)) throw new Error(`assets/ が無い: ${assetsDir}`);

  const initial = parseInitialJs(fs.readFileSync(indexHtml, 'utf8'));
  const files = fs
    .readdirSync(assetsDir)
    .filter((f) => f.endsWith('.js'))
    .map((f) => ({ file: f, bytes: fs.statSync(path.join(assetsDir, f)).size }));

  const isInitial = (f) => initial.has(`/assets/${f}`);
  const initialChunks = files.filter((c) => isInitial(c.file));
  const lazyChunks = files.filter((c) => !isInitial(c.file));

  return {
    files,
    initialChunks,
    lazyChunks,
    initialTotalBytes: initialChunks.reduce((s, c) => s + c.bytes, 0),
    largest: files.slice().sort((a, b) => b.bytes - a.bytes)[0] ?? null,
    smallLazyChunks: lazyChunks.filter((c) => c.bytes < 1000).length,
  };
}

/** 測定値と baseline を突き合わせ、違反（fail）と警告（warn）を返す。純関数——self-test で使う。 */
function evaluate(m, baseline) {
  const failures = [];
  const warnings = [];

  // 判定 1: 必須チャンクの実在。**規則の欠落を捕まえる唯一の判定**（冒頭の実測表）。
  for (const name of baseline.requiredChunks) {
    const re = chunkNameMatcher(name);
    if (!m.files.some((c) => re.test(c.file))) {
      failures.push(
        `必須チャンク "${name}" が成果物に存在しない` +
          `（vite.config.ts の manualChunks から ${name} の規則が外れた可能性が高い）`
      );
    }
  }

  // 判定 2: 1 チャンクの上限。
  for (const c of m.files) {
    if (c.bytes > baseline.maxChunkBytes) {
      failures.push(
        `チャンクが上限を超えている: ${c.file} = ${kb(c.bytes)} > ${kb(baseline.maxChunkBytes)}`
      );
    }
  }

  // 判定 3: 初期ロード合計の ratchet。
  if (m.initialTotalBytes > baseline.initialTotalBytes) {
    const delta = m.initialTotalBytes - baseline.initialTotalBytes;
    failures.push(
      `初期ロード合計が床を超えた: ${kb(m.initialTotalBytes)} > ${kb(baseline.initialTotalBytes)}` +
        `（+${kb(delta)}）。意図した増加なら node scripts/check-chunk-budget.js --update で床を更新し、差分を PR に載せること`
    );
  }

  // 判定 4: 1 kB 未満の遅延チャンク数（warn）。
  if (m.smallLazyChunks > baseline.smallLazyChunks) {
    warnings.push(
      `1 kB 未満の遅延チャンクが増えた: ${m.smallLazyChunks} 本 > ${baseline.smallLazyChunks} 本。` +
        `共有モジュールが細かく切り出され、往復だけが増えている可能性がある（manualChunks の規則を確認すること）`
    );
  }

  return { failures, warnings };
}

function loadBaseline() {
  return JSON.parse(fs.readFileSync(BASELINE_PATH, 'utf8'));
}

function parseArgs(argv) {
  const args = { dist: DEFAULT_DIST, require: false, update: false, selfTest: false };
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === '--require') args.require = true;
    else if (argv[i] === '--update') args.update = true;
    else if (argv[i] === '--self-test') args.selfTest = true;
    else if (argv[i] === '--dist') {
      i += 1;
      if (!argv[i]) throw new Error('--dist にはディレクトリを指定する');
      args.dist = argv[i];
    } else throw new Error(`未知の引数: ${argv[i]}`);
  }
  return args;
}

function main(argv) {
  const args = parseArgs(argv);
  if (args.selfTest) return selfTest();

  const distDir = path.isAbsolute(args.dist) ? args.dist : path.join(REPO_ROOT, args.dist);
  if (!fs.existsSync(distDir)) {
    if (args.require) {
      process.stderr.write(`✗ 走査対象が存在しない（--require）: ${args.dist}\n`);
      return 1;
    }
    // fail-open。ビルド前のローカル実行や無関係な PR を赤くしない。
    // 検査が静かに失効するのを防ぐため、CI は --require を使うこと。
    warn(
      `[check-chunk-budget] 成果物が無いため検査を skip した（探した先: ${args.dist}）。` +
        'CI では --require を付けて fail-closed にすること。'
    );
    return 0;
  }

  const m = measure(distDir);
  const baseline = loadBaseline();

  if (args.update) {
    const next = {
      ...baseline,
      initialTotalBytes: m.initialTotalBytes,
      smallLazyChunks: m.smallLazyChunks,
    };
    fs.writeFileSync(BASELINE_PATH, `${JSON.stringify(next, null, 2)}\n`, 'utf8');
    process.stdout.write(
      `[check-chunk-budget] 床を更新した: 初期ロード合計 ${kb(m.initialTotalBytes)} / ` +
        `1 kB 未満の遅延チャンク ${m.smallLazyChunks} 本\n`
    );
    return 0;
  }

  const { failures, warnings } = evaluate(m, baseline);
  for (const w of warnings) warn(`[check-chunk-budget] ${w}`);

  if (failures.length > 0) {
    process.stderr.write('✗ [check-chunk-budget] バンドル分割の検査に失敗しました:\n');
    for (const f of failures) process.stderr.write(`  - ${f}\n`);
    process.stderr.write(
      '\n規則は vite.config.ts の manualChunks と docs/adr/IADR-0134_spa-route-code-splitting-boundaries.md を参照。\n'
    );
    return 1;
  }

  process.stdout.write(
    `[check-chunk-budget] OK: 必須チャンク ${baseline.requiredChunks.length} 本すべて実在 / ` +
      `初期ロード合計 ${kb(m.initialTotalBytes)}（床 ${kb(baseline.initialTotalBytes)}）/ ` +
      `最大チャンク ${m.largest ? kb(m.largest.bytes) : 'なし'}（上限 ${kb(baseline.maxChunkBytes)}）/ ` +
      `1 kB 未満の遅延チャンク ${m.smallLazyChunks} 本\n`
  );
  return 0;
}

/* ------------------------------------------------------------------ self-test */

/**
 * 合成 dist を作る。**実測（冒頭の表）と同じ形の退行を再現する**——self-test が
 * 「作った通りに動く」だけの循環にならないよう、変異は実ビルドで観測した値に合わせている。
 */
function writeFixture(dir, { chunks, initial }) {
  fs.mkdirSync(path.join(dir, 'assets'), { recursive: true });
  const links = initial
    .filter((f) => !f.startsWith('index-'))
    .map((f) => `<link rel="modulepreload" href="/assets/${f}">`)
    .join('');
  const entry = initial.find((f) => f.startsWith('index-'));
  fs.writeFileSync(
    path.join(dir, 'index.html'),
    `<!doctype html><html><head>${links}</head><body><script type="module" src="/assets/${entry}"></script></body></html>`,
    'utf8'
  );
  for (const [file, bytes] of Object.entries(chunks)) {
    fs.writeFileSync(path.join(dir, 'assets', file), Buffer.alloc(bytes, 0x20));
  }
}

function selfTest() {
  const assert = require('assert');
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'chunk-budget-'));
  let passed = 0;
  const ok = (name, fn) => {
    fn();
    passed += 1;
    process.stdout.write(`  ok  ${name}\n`);
  };

  // 実測値をそのまま使う（baseline と同じ形）。
  const BASE = {
    requiredChunks: ['vendor-react', 'ui', 'vendor-query'],
    maxChunkBytes: 500000,
    initialTotalBytes: 577680,
    smallLazyChunks: 5,
  };
  const healthy = {
    initial: ['index-aaaa.js', 'vendor-react-bbbb.js', 'ui-cccc.js', 'vendor-query-dddd.js'],
    chunks: {
      'index-aaaa.js': 274465,
      'vendor-react-bbbb.js': 196690,
      'ui-cccc.js': 65043,
      'vendor-query-dddd.js': 41482,
      'PageA-eeee.js': 14481,
      'tiny1-f1.js': 737,
      'tiny2-f2.js': 267,
      'tiny3-f3.js': 166,
      'tiny4-f4.js': 121,
      'tiny5-f5.js': 107,
    },
  };

  ok('正常な構成は違反 0・警告 0', () => {
    const d = path.join(tmp, 'healthy');
    writeFixture(d, healthy);
    const r = evaluate(measure(d), BASE);
    assert.deepStrictEqual(r.failures, [], `想定外の違反: ${r.failures.join(' / ')}`);
    assert.deepStrictEqual(r.warnings, []);
  });

  ok('modulepreload の抽出は属性の順序に依存しない（href が先でも拾う）', () => {
    // AI レビュー（PR #618 🟢）の指摘。順序を前提にすると初期ロードを過小に数え、
    // 判定 3 が沈黙側へ倒れる。両方の順序で同じ集合になることを固定する。
    const relFirst = '<link rel="modulepreload" href="/assets/a-1.js">';
    const hrefFirst = '<link href="/assets/a-1.js" rel="modulepreload">';
    const withCrossorigin = '<link rel="modulepreload" crossorigin href="/assets/a-1.js">';
    for (const html of [relFirst, hrefFirst, withCrossorigin]) {
      assert.deepStrictEqual([...parseInitialJs(html)], ['/assets/a-1.js'], `拾えていない: ${html}`);
    }
    // stylesheet は初期ロードの JS ではない（拾ってはいけない）。
    assert.deepStrictEqual([...parseInitialJs('<link rel="stylesheet" href="/assets/a-1.js">')], []);
  });

  ok('初期ロードは index.html 由来で数える（遅延チャンクを含めない）', () => {
    const d = path.join(tmp, 'healthy');
    const m = measure(d);
    assert.strictEqual(m.initialTotalBytes, 577680);
    assert.strictEqual(m.lazyChunks.length, 6);
    assert.strictEqual(m.smallLazyChunks, 5);
  });

  // 判定 1 —— **実測した 2 つの変異**。どちらも判定 2・3・4 では捕まらないことを同時に固定する。
  ok('変異 M6: ui 規則の欠落を検出する（500 kB 判定も ratchet も発火しない状況で）', () => {
    const d = path.join(tmp, 'no-ui');
    writeFixture(d, {
      // 実測: index 306.69 kB / 初期合計 544.87 kB（**減る**）/ 1 kB 未満 9 本
      initial: ['index-aaaa.js', 'vendor-react-bbbb.js', 'vendor-query-dddd.js'],
      chunks: {
        'index-aaaa.js': 306690,
        'vendor-react-bbbb.js': 196690,
        'vendor-query-dddd.js': 41482,
        ...Object.fromEntries(Array.from({ length: 9 }, (_, i) => [`tiny${i}-h${i}.js`, 300])),
      },
    });
    const m = measure(d);
    const r = evaluate(m, BASE);
    // 前提: この変異では判定 2 と判定 3 は発火しない（＝判定 1 が無ければ素通りする）。
    assert.ok(m.largest.bytes < BASE.maxChunkBytes, '判定 2 が発火しない前提が崩れている');
    assert.ok(m.initialTotalBytes < BASE.initialTotalBytes, '判定 3 が発火しない前提が崩れている');
    assert.strictEqual(r.failures.length, 1, `違反は 1 件のはず: ${r.failures.join(' / ')}`);
    assert.ok(/必須チャンク "ui" が成果物に存在しない/.test(r.failures[0]));
    assert.strictEqual(r.warnings.length, 1, '1 kB 未満の増加は warn になるはず');
  });

  ok('変異 M7: vendor-react 規則の欠落を検出する（1 kB 未満の本数は 1 本も動かない）', () => {
    const d = path.join(tmp, 'no-vendor-react');
    writeFixture(d, {
      // 実測: index 458.92 kB（< 500）/ 初期合計 578.55 kB（+0.15%）/ 1 kB 未満 5 本（不変）
      initial: ['index-aaaa.js', 'ui-cccc.js', 'vendor-query-dddd.js'],
      chunks: {
        'index-aaaa.js': 458920,
        'ui-cccc.js': 68960,
        'vendor-query-dddd.js': 50670,
        'tiny1-f1.js': 737,
        'tiny2-f2.js': 267,
        'tiny3-f3.js': 166,
        'tiny4-f4.js': 121,
        'tiny5-f5.js': 107,
      },
    });
    const m = measure(d);
    const r = evaluate(m, BASE);
    assert.ok(m.largest.bytes < BASE.maxChunkBytes, '判定 2 が発火しない前提が崩れている');
    assert.strictEqual(m.smallLazyChunks, BASE.smallLazyChunks, '判定 4 が動かない前提が崩れている');
    assert.ok(
      r.failures.some((f) => /必須チャンク "vendor-react" が成果物に存在しない/.test(f)),
      `vendor-react の欠落を検出できていない: ${r.failures.join(' / ')}`
    );
  });

  ok('判定 2: 1 チャンクが上限を超えると fail', () => {
    const d = path.join(tmp, 'huge');
    writeFixture(d, {
      initial: ['index-aaaa.js', 'vendor-react-bbbb.js', 'ui-cccc.js', 'vendor-query-dddd.js'],
      chunks: {
        'index-aaaa.js': 1000,
        'vendor-react-bbbb.js': 1000,
        'ui-cccc.js': 1000,
        'vendor-query-dddd.js': 1000,
        'fat-zzzz.js': 500001,
      },
    });
    const r = evaluate(measure(d), BASE);
    assert.ok(r.failures.some((f) => /上限を超えている: fat-zzzz\.js/.test(f)), r.failures.join(' / '));
  });

  ok('判定 3: 初期ロード合計が床を超えると fail', () => {
    const d = path.join(tmp, 'over');
    writeFixture(d, {
      initial: ['index-aaaa.js', 'vendor-react-bbbb.js', 'ui-cccc.js', 'vendor-query-dddd.js'],
      chunks: {
        'index-aaaa.js': 274466, // baseline + 1 byte
        'vendor-react-bbbb.js': 196690,
        'ui-cccc.js': 65043,
        'vendor-query-dddd.js': 41482,
      },
    });
    const r = evaluate(measure(d), BASE);
    assert.ok(r.failures.some((f) => /初期ロード合計が床を超えた/.test(f)), r.failures.join(' / '));
  });

  ok('判定 3: 床ちょうどは通る（境界）', () => {
    const d = path.join(tmp, 'exact');
    writeFixture(d, healthy);
    const r = evaluate(measure(d), BASE);
    assert.deepStrictEqual(r.failures, []);
  });

  ok('判定 4 は warn であり fail にしない（遅延ルート追加で他人の PR を止めない）', () => {
    const d = path.join(tmp, 'many-small');
    writeFixture(d, {
      initial: ['index-aaaa.js', 'vendor-react-bbbb.js', 'ui-cccc.js', 'vendor-query-dddd.js'],
      chunks: {
        'index-aaaa.js': 274465,
        'vendor-react-bbbb.js': 196690,
        'ui-cccc.js': 65043,
        'vendor-query-dddd.js': 41482,
        ...Object.fromEntries(Array.from({ length: 20 }, (_, i) => [`t${i}-h${i}.js`, 100])),
      },
    });
    const r = evaluate(measure(d), BASE);
    assert.deepStrictEqual(r.failures, []);
    assert.strictEqual(r.warnings.length, 1);
  });

  ok('★ 既知の穴: 同じ接頭辞を持つ別チャンクを実在と誤認する（偽陽性を避けるための意図的な緩さ）', () => {
    // **この挙動は仕様である。** 塞ぐにはハッシュ長を固定する必要があり、Vite が長さを変えた
    // 瞬間に偽陽性で全 PR を止める。ここでは「穴が在ること」を明示的に固定し、
    // 将来この挙動が変わったら（＝誰かが塞いだら）テストが落ちて気付けるようにする。
    const d = path.join(tmp, 'lookalike');
    writeFixture(d, {
      initial: ['index-aaaa.js', 'vendor-react-bbbb.js', 'vendor-query-dddd.js'],
      chunks: {
        'index-aaaa.js': 1000,
        'vendor-react-bbbb.js': 1000,
        'vendor-query-dddd.js': 1000,
        // `ui` 規則は外れているが、紛らわしい名前の遅延チャンクが在る。
        'ui-widgets-extra-eeee.js': 1000,
      },
    });
    const r = evaluate(measure(d), BASE);
    assert.ok(
      !r.failures.some((f) => /必須チャンク "ui"/.test(f)),
      '穴が塞がれている。意図的に塞いだのなら本テストを更新すること'
    );
  });

  ok('成果物が無いとき --require は fail・引数なしは warn して exit 0', () => {
    const missing = path.join(tmp, 'does-not-exist');
    assert.strictEqual(main(['--dist', missing, '--require']), 1);
    assert.strictEqual(main(['--dist', missing]), 0);
  });

  ok('baseline は実ファイルとして読め、必須キーが揃っている', () => {
    const b = loadBaseline();
    assert.ok(Array.isArray(b.requiredChunks) && b.requiredChunks.length > 0);
    for (const k of ['maxChunkBytes', 'initialTotalBytes', 'smallLazyChunks']) {
      assert.strictEqual(typeof b[k], 'number', `${k} が数値でない`);
    }
  });

  ok('baseline の requiredChunks が vite.config.ts の manualChunks と一致する', () => {
    // **床とコードの乖離を止める。** 規則を足したのに baseline へ入れ忘れると、
    // 新しい規則だけ検査されないまま「緑」になる（本検査が防ごうとしている状態そのもの）。
    const vite = fs.readFileSync(
      path.join(REPO_ROOT, 'src/platform/frontend/vite.config.ts'),
      'utf8'
    );
    const body = vite.slice(vite.indexOf('manualChunks(id)'));
    const end = body.indexOf('\n        },');
    const region = body.slice(0, end === -1 ? undefined : end);
    // `return '<name>'` の形だけを見てはいけない —— `ui` は三項演算子で返している
    // （`return id.includes('/packages/ui/') ? 'ui' : undefined`）。実際にこの取りこぼしを踏んだ。
    // チャンク名は「パスでもパッケージ名でもない裸の識別子」なので、その形で拾う。
    //
    // ［2026-08-08 / フェーズ末クロス監査］**ただし region 全体から拾ってはいけない。**
    // 従前は region 内の裸の文字列リテラルをすべて拾っていたため、**述語側のリテラルを
    // チャンク名と誤認する**潜在的な偽陽性があった（例: `if (pkg.startsWith('lodash')) return 'vendor-lodash';`
    // を足すと `lodash` まで宣言済みチャンク名として拾い、self-test が落ちる。指示どおり baseline へ
    // `lodash` を入れると、今度は判定 1 が実ビルドで恒久的に fail する）。
    // 現行の規則はたまたま述語が正規表現リテラルなので発火していないだけである。
    // **IADR-0147 決定 4 は「偽陽性は塞ぐ」を方針として掲げている**ので、開示ではなく塞ぐ。
    // チャンク名が現れるのは `return` 句だけなので、抽出をそこへ限定する。
    const returnStatements = Array.from(region.matchAll(/\breturn\b[^;]*;/g)).map((m) => m[0]);
    const declared = new Set(
      returnStatements
        .flatMap((stmt) => Array.from(stmt.matchAll(/'([^']+)'/g)).map((m) => m[1]))
        .filter((s) => !s.includes('/') && !s.startsWith('@'))
    );
    assert.deepStrictEqual(
      [...declared].sort(),
      [...loadBaseline().requiredChunks].sort(),
      'vite.config.ts の manualChunks が返す名前と baseline.requiredChunks がずれている'
    );
  });

  fs.rmSync(tmp, { recursive: true, force: true });
  process.stdout.write(`\n✓ self-test: ${passed} 件すべて通過\n`);
  return 0;
}

module.exports = { measure, evaluate, parseInitialJs, chunkNameMatcher };

if (require.main === module) {
  try {
    process.exit(main(process.argv.slice(2)));
  } catch (err) {
    process.stderr.write(`✗ [check-chunk-budget] ${err.message}\n`);
    process.exit(1);
  }
}
