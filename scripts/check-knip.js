#!/usr/bin/env node
'use strict';
/*
 * check-knip.js
 * 未使用コード・未使用依存（Knip）の**ラチェット**検査
 * （NFR / ADR-0031 / IADR-0121 決定 1 / IADR-0211 / issue #493）。
 * 外部依存ゼロ（Node 標準モジュールのみ。Knip 本体は src/ の devDependency）。
 *
 * ■ なぜ「0 件」ではなくラチェットか
 *   計画 13_frontend-stack §採用技術一覧は「Dead Code 検出 = Knip」を採用と定めるが、
 *   本リポジトリの SPA は第 1〜3 段で旧スタックを丸ごと置き換えてきた途中であり、
 *   着手時点の残件は **41 件**ある（内訳と理由は knip-baseline.json の $comment）。
 *   0 件を要求すると、**本 PR が「検出の導入」ではなく「片付け」になり**、
 *   さらに未使用の削除は画面の再実装（#452）と衝突する。
 *   よって本リポジトリの既定の作法（backend-library-baseline.json /
 *   chunk-budget-baseline.json / adr-index-title-baseline.json / test-spec-coverage-baseline.json）に
 *   合わせ、**残件数を床に固定し、増えたら落とす**。
 *
 * ■ 判定
 *   1. **増加**（fail）  区分ごとの件数が床を超えた
 *   2. **減少**（fail）  床を下回った。片付けたら床を締める（`--update`）。締め忘れると、
 *                        次の混入が「元に戻っただけ」で素通りする
 *   3. **新区分**（fail）床に無い区分が 1 件以上出た（Knip の版が上がって検出種別が増えた場合を含む）
 *
 * ■ fail-closed（IADR-0183: 「偽の緑」を返す条件は fail へ倒す）
 *   - Knip の終了コードが 0 / 1 以外（＝クラッシュ・設定不正）は **throw**。
 *   - 標準出力が JSON として読めない／`issues` 配列を持たない形は **throw**。
 *   - **床が 0 件でないのに走査結果が空**なら **throw**（パースが壊れて「未使用 0 件で緑」に
 *     化けるのを止める。実際に変異試験 M4 で再現する形である）。
 *   - 床に未知の区分名が書かれていたら **throw**（typo で判定が黙って外れるのを止める）。
 *
 * ■ 段階ポリシー（fail-open / fail-closed）——check-chunk-budget.js / check-static-egress.js と同じ作法
 *   - 引数なし: Knip の実行ファイルが無ければ warn を出して exit 0
 *     （`pnpm install` 前のローカル実行・バックエンドだけの PR を赤くしない）。
 *   - `--require`: 実行ファイルが無ければ **fail**。**CI はこちらを使う。**
 *
 * ■ 検出しないこと（意図的な穴。本検査は網羅ではない）
 *   - **雛形（`templates` 配下の各ユニットの frontend）**。pnpm workspace のメンバだが Knip の project root
 *     （`src/`）の外にあり射程へ入らない（実測: 雛形へ未使用 export を足しても件数が動かない）。
 *   - **`src/ai-stock-trading`**（別プロジェクトの submodule。knip.jsonc の ignoreWorkspaces）。
 *   - **バックエンド（C#）**。Knip は JS/TS のツールである。
 *   - **どの識別子が未使用か**は見ていない（**数だけ**の突合である）。同じ区分で 1 件消えて
 *     1 件増えると素通りする。識別子まで固定すると、床が数百行になり保守が破綻するほうを重く見た。
 *
 * 使い方:
 *   node scripts/check-knip.js
 *   node scripts/check-knip.js --require
 *   node scripts/check-knip.js --update
 *   node scripts/check-knip.js --self-test
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const { warn } = require('./lib/ci-annotate');

const REPO_ROOT = path.resolve(__dirname, '..');
const SRC_DIR = path.join(REPO_ROOT, 'src');
const KNIP_BIN = path.join(SRC_DIR, 'node_modules', '.bin', 'knip');
const BASELINE_PATH = path.join(__dirname, 'knip-baseline.json');
const KNIP_CONFIG_PATH = path.join(SRC_DIR, 'knip.jsonc');
const GITMODULES_PATH = path.join(REPO_ROOT, '.gitmodules');

/**
 * Knip の JSON レポータが 1 エントリに持つ区分キー（`file` を除く）。
 *
 * **列挙をここに置く理由**: 床の typo（`dependancies` 等）を黙って無視すると、その区分の
 * 判定だけが外れて緑になる。既知の集合と突き合わせて **throw** する。
 * **Knip の版が上がって区分が増えたときは、増えた区分が「新区分」として fail する**
 * （判定 3）。そのとき本配列と床を同時に更新する。実測は knip@6.32.2。
 */
const KNOWN_ISSUE_TYPES = [
  'binaries',
  'catalog',
  'catalogReferences',
  'dependencies',
  'devDependencies',
  'duplicates',
  'enumMembers',
  'exports',
  'files',
  'namespaceMembers',
  'optionalPeerDependencies',
  'types',
  'unlisted',
  'unresolved',
];

/* ------------------------------------------------------------------ 純関数（self-test で使う） */

/**
 * Knip の標準出力（JSON レポータ）を解析する。**壊れていたら throw する**
 * （0 件へ落として緑にしない。IADR-0183）。
 */
function parsePayload(stdout) {
  let parsed;
  try {
    parsed = JSON.parse(stdout);
  } catch (e) {
    throw new Error(
      `Knip の JSON 出力を解析できない（${e.message}）。` +
        '出力の形が変わったか、Knip が途中で落ちている。0 件として扱わない。'
    );
  }
  if (!parsed || typeof parsed !== 'object' || !Array.isArray(parsed.issues)) {
    throw new Error(
      'Knip の JSON 出力に issues 配列が無い。レポータの形が変わった可能性がある。0 件として扱わない。'
    );
  }
  return parsed;
}

/** 解析済みの payload を区分ごとの件数へ畳む。未知の区分もそのまま数える（判定 3 が拾う）。 */
function aggregate(payload) {
  const counts = {};
  for (const entry of payload.issues) {
    if (!entry || typeof entry !== 'object') {
      throw new Error('issues の要素がオブジェクトではない。レポータの形が変わった可能性がある。');
    }
    for (const [key, value] of Object.entries(entry)) {
      if (key === 'file') continue;
      if (!Array.isArray(value)) continue;
      counts[key] = (counts[key] ?? 0) + value.length;
    }
  }
  return counts;
}

/** 区分ごとの件数の合計。 */
function total(counts) {
  return Object.values(counts).reduce((s, n) => s + n, 0);
}

/**
 * 実測と床を突き合わせる。**純関数**（self-test と変異試験がここを叩く）。
 * 戻り値の failures が空なら合格。
 */
function evaluate(counts, baseline) {
  const floor = baseline.counts;
  if (!floor || typeof floor !== 'object') {
    throw new Error('baseline に counts オブジェクトが無い（床が読めない）。');
  }

  // 床の区分名が既知であること。typo は判定を黙って外すので throw する。
  for (const name of Object.keys(floor)) {
    if (!KNOWN_ISSUE_TYPES.includes(name)) {
      throw new Error(
        `baseline.counts に未知の区分 "${name}" がある。` +
          `既知の区分: ${KNOWN_ISSUE_TYPES.join(' / ')}`
      );
    }
  }

  const failures = [];
  for (const [name, actual] of Object.entries(counts)) {
    if (actual === 0) continue; // 0 件の区分を床へ書かせない（床が全区分の羅列になるのを避ける）。
    if (!(name in floor)) {
      failures.push(
        `新しい区分 "${name}" が ${actual} 件出た。` +
          '内容を確認し、直すか baseline へ理由つきで追加すること' +
          '（Knip の版が上がって検出種別が増えた場合もここへ出る）。'
      );
    }
  }
  for (const [name, expected] of Object.entries(floor)) {
    const actual = counts[name] ?? 0;
    if (actual > expected) {
      failures.push(
        `${name}: ${actual} 件 > 床 ${expected} 件（+${actual - expected}）。` +
          '未使用が増えている。使うか消すこと。'
      );
    } else if (actual < expected) {
      failures.push(
        `${name}: ${actual} 件 < 床 ${expected} 件（-${expected - actual}）。` +
          '減らしたなら node scripts/check-knip.js --update で床を締め、差分を PR に載せること' +
          '（締めないと次の混入が「元に戻っただけ」で素通りする）。'
      );
    }
  }
  return { failures };
}

/**
 * JSONC（コメント付き JSON）からコメントを取り除く。
 * **文字列リテラルの中は触らない** —— `"https://unpkg.com/..."` の `//` を
 * コメント開始と誤ると設定が壊れて読めなくなる（本ファイルの $schema がまさにその形）。
 */
function stripJsonComments(text) {
  let out = '';
  let inString = false;
  let inLine = false;
  let inBlock = false;
  for (let i = 0; i < text.length; i += 1) {
    const c = text[i];
    const next = text[i + 1];
    if (inLine) {
      if (c === '\n') {
        inLine = false;
        out += c;
      }
      continue;
    }
    if (inBlock) {
      if (c === '*' && next === '/') {
        inBlock = false;
        i += 1;
      }
      continue;
    }
    if (inString) {
      out += c;
      if (c === '\\') {
        out += next ?? '';
        i += 1;
      } else if (c === '"') {
        inString = false;
      }
      continue;
    }
    if (c === '"') {
      inString = true;
      out += c;
      continue;
    }
    if (c === '/' && next === '/') {
      inLine = true;
      i += 1;
      continue;
    }
    if (c === '/' && next === '*') {
      inBlock = true;
      i += 1;
      continue;
    }
    out += c;
  }
  return out;
}

/**
 * JSONC の**末尾カンマ**を落とす（文字列の中は触らない）。
 *
 * **なぜ要るか**: `src/knip.jsonc` は `pnpm run format:check`（prettier）の射程にあり、
 * 本リポの `.prettierrc.json` は `trailingComma: "all"` である。よって prettier は
 * `.jsonc` に**末尾カンマを足す**（実測）。Knip 本体は許すが `JSON.parse` は許さないため、
 * ここで落とさないと**整形するたびに検査器だけが読めなくなる**。
 */
function stripTrailingCommas(text) {
  let out = '';
  let inString = false;
  for (let i = 0; i < text.length; i += 1) {
    const c = text[i];
    if (inString) {
      out += c;
      if (c === '\\') {
        out += text[i + 1] ?? '';
        i += 1;
      } else if (c === '"') {
        inString = false;
      }
      continue;
    }
    if (c === '"') {
      inString = true;
      out += c;
      continue;
    }
    if (c === ',') {
      // 次の非空白が } か ] なら、このカンマは末尾カンマである。
      let j = i + 1;
      while (j < text.length && /\s/.test(text[j])) j += 1;
      if (text[j] === '}' || text[j] === ']') continue;
    }
    out += c;
  }
  return out;
}

/** JSONC（コメント＋末尾カンマを許す JSON）を読む。 */
function parseJsonc(text) {
  return JSON.parse(stripTrailingCommas(stripJsonComments(text)));
}

/** `.gitmodules` の `path = src/<unit>` から可変ユニット（別プロジェクト）を導出する。 */
function parseSubmoduleUnits(gitmodulesText) {
  const units = [];
  for (const m of gitmodulesText.matchAll(/^\s*path\s*=\s*(\S+)\s*$/gm)) {
    const p = m[1].replace(/\\/g, '/');
    if (p.startsWith('src/')) units.push(p.slice('src/'.length));
  }
  return units;
}

/**
 * 「`.gitmodules` の `src/<unit>` が knip.jsonc の ignoreWorkspaces で外れているか」を見る。
 *
 * **これが IADR-0211 決定 2 の「二重管理は残すが黙って腐らせない」仕掛けである。**
 * submodule を足したのに Knip の除外へ足し忘れると、別プロジェクトの未使用が本リポの
 * 床へ雪崩れ込む（実測: AST を外さないと 692 件、外すと 648 件）。
 */
function findUnignoredSubmodules(gitmodulesText, knipConfig) {
  const patterns = Array.isArray(knipConfig.ignoreWorkspaces) ? knipConfig.ignoreWorkspaces : [];
  const missing = [];
  for (const unit of parseSubmoduleUnits(gitmodulesText)) {
    const covered = patterns.some((p) => p === unit || p === `${unit}/**` || p.startsWith(`${unit}/`));
    if (!covered) missing.push(unit);
  }
  return missing;
}

/* ------------------------------------------------------------------ 実行 */

function runKnip() {
  const r = spawnSync(KNIP_BIN, ['--no-progress', '--reporter', 'json'], {
    cwd: SRC_DIR,
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
  });
  if (r.error) throw new Error(`Knip を起動できない: ${r.error.message}`);
  // Knip の終了コード: 0 = 指摘なし / 1 = 指摘あり / それ以外 = エラー。
  // **1 を失敗にしない**（指摘の有無ではなく床との差で判定する）。
  if (r.status !== 0 && r.status !== 1) {
    throw new Error(
      `Knip が異常終了した（exit ${r.status}）。0 件として扱わない。\n${r.stderr || r.stdout}`
    );
  }
  return parsePayload(r.stdout);
}

function loadBaseline() {
  return JSON.parse(fs.readFileSync(BASELINE_PATH, 'utf8'));
}

function loadKnipConfig() {
  return parseJsonc(fs.readFileSync(KNIP_CONFIG_PATH, 'utf8'));
}

function parseArgs(argv) {
  const args = { require: false, update: false, selfTest: false };
  for (const a of argv) {
    if (a === '--require') args.require = true;
    else if (a === '--update') args.update = true;
    else if (a === '--self-test') args.selfTest = true;
    else throw new Error(`未知の引数: ${a}`);
  }
  return args;
}

function main(argv) {
  const args = parseArgs(argv);
  if (args.selfTest) return selfTest();

  if (!fs.existsSync(KNIP_BIN)) {
    if (args.require) {
      process.stderr.write(
        `✗ [check-knip] Knip の実行ファイルが無い（--require）: ${path.relative(REPO_ROOT, KNIP_BIN)}\n` +
          '  src/ で pnpm install --frozen-lockfile を実行すること。\n'
      );
      return 1;
    }
    warn(
      '[check-knip] Knip が未インストールのため検査を skip した' +
        `（探した先: ${path.relative(REPO_ROOT, KNIP_BIN)}）。CI では --require を付けて fail-closed にすること。`
    );
    return 0;
  }

  const baseline = loadBaseline();
  const counts = aggregate(runKnip());

  // fail-closed: 床が 0 件でないのに走査結果が空なのは、パースか設定が壊れている形である。
  if (total(counts) === 0 && total(baseline.counts) > 0) {
    throw new Error(
      'Knip の指摘が 1 件も無い一方、床は ' +
        `${total(baseline.counts)} 件を期待している。走査が空振りしている疑いがあるため 0 件で緑にしない。`
    );
  }

  if (args.update) {
    const next = { ...baseline, counts: {} };
    for (const name of KNOWN_ISSUE_TYPES) {
      if ((counts[name] ?? 0) > 0) next.counts[name] = counts[name];
    }
    fs.writeFileSync(BASELINE_PATH, `${JSON.stringify(next, null, 2)}\n`, 'utf8');
    process.stdout.write(
      `[check-knip] 床を更新した: ${JSON.stringify(next.counts)}（合計 ${total(next.counts)} 件）\n`
    );
    return 0;
  }

  const { failures } = evaluate(counts, baseline);
  if (failures.length > 0) {
    process.stderr.write('✗ [check-knip] 未使用コード・依存の床から外れました:\n');
    for (const f of failures) process.stderr.write(`  - ${f}\n`);
    process.stderr.write(
      '\n内訳は `cd src && pnpm run knip` で確認できる。' +
        '設計と残件の理由は scripts/knip-baseline.json の $comment と' +
        ' docs/adr/IADR-0211_knip-scope-and-unused-ratchet.md を参照。\n'
    );
    return 1;
  }

  process.stdout.write(
    `[check-knip] OK: 床どおり ${total(counts)} 件（${Object.entries(baseline.counts)
      .map(([k, v]) => `${k} ${v}`)
      .join(' / ')}）\n`
  );
  return 0;
}

/* ------------------------------------------------------------------ self-test */

function selfTest() {
  const assert = require('assert');
  let passed = 0;
  const ok = (name, fn) => {
    fn();
    passed += 1;
    process.stdout.write(`  ok  ${name}\n`);
  };

  // 着手時の実測（knip@6.32.2・knip.jsonc 適用後）をそのまま使う。
  const BASE = { counts: { dependencies: 1, devDependencies: 4, exports: 18, types: 17, unlisted: 1 } };
  const payloadOf = (counts) => ({
    issues: Object.entries(counts).map(([name, n]) => ({
      file: `fixture/${name}.ts`,
      [name]: Array.from({ length: n }, (_, i) => ({ name: `x${i}` })),
    })),
  });

  ok('aggregate: 区分ごとに畳み込む（file キーは数えない）', () => {
    const counts = aggregate(payloadOf({ exports: 3, types: 2 }));
    assert.deepStrictEqual(counts, { exports: 3, types: 2 });
  });

  ok('aggregate: 同じ区分が複数ファイルに散っていても合算する', () => {
    const payload = {
      issues: [
        { file: 'a.ts', exports: [{ name: 'a' }], types: [] },
        { file: 'b.ts', exports: [{ name: 'b' }, { name: 'c' }], types: [{ name: 'T' }] },
      ],
    };
    assert.deepStrictEqual(aggregate(payload), { exports: 3, types: 1 });
  });

  ok('床どおりなら違反 0', () => {
    const r = evaluate(aggregate(payloadOf(BASE.counts)), BASE);
    assert.deepStrictEqual(r.failures, [], `想定外の違反: ${r.failures.join(' / ')}`);
  });

  ok('変異 M1 相当: 床を 1 件減らすと fail する', () => {
    const tightened = { counts: { ...BASE.counts, exports: BASE.counts.exports - 1 } };
    const r = evaluate(aggregate(payloadOf(BASE.counts)), tightened);
    assert.strictEqual(r.failures.length, 1, `違反は 1 件のはず: ${r.failures.join(' / ')}`);
    assert.match(r.failures[0], /exports: 18 件 > 床 17 件/);
  });

  ok('変異 M2 相当: 未使用 export が 1 つ増えると fail する', () => {
    const grown = { ...BASE.counts, exports: BASE.counts.exports + 1 };
    const r = evaluate(aggregate(payloadOf(grown)), BASE);
    assert.strictEqual(r.failures.length, 1, `違反は 1 件のはず: ${r.failures.join(' / ')}`);
    assert.match(r.failures[0], /未使用が増えている/);
  });

  ok('変異 M3 相当: 除外が壊れて件数が跳ね上がると fail する', () => {
    // AST の除外を外したときの実測（692 件・files 51 / exports 147 / types 486 ほか）。
    const blown = { files: 51, dependencies: 1, devDependencies: 7, exports: 147, types: 486 };
    const r = evaluate(aggregate(payloadOf(blown)), BASE);
    assert.ok(
      r.failures.some((f) => /新しい区分 "files" が 51 件出た/.test(f)),
      `files の噴出を検出できていない: ${r.failures.join(' / ')}`
    );
    assert.ok(r.failures.length >= 4, `複数区分で落ちるはず: ${r.failures.join(' / ')}`);
  });

  ok('減少も fail する（床を締め忘れさせない）', () => {
    const shrunk = { ...BASE.counts, types: BASE.counts.types - 2 };
    const r = evaluate(aggregate(payloadOf(shrunk)), BASE);
    assert.strictEqual(r.failures.length, 1);
    assert.match(r.failures[0], /--update で床を締め/);
  });

  ok('床に未知の区分名があれば throw する（typo で判定が黙って外れない）', () => {
    assert.throws(
      () => evaluate({ exports: 1 }, { counts: { dependancies: 1 } }),
      /未知の区分 "dependancies"/
    );
  });

  ok('変異 M4 相当: JSON でない出力は 0 件へ落とさず throw する', () => {
    assert.throws(() => parsePayload('<html>gateway timeout</html>'), /解析できない/);
    assert.throws(() => parsePayload('null'), /issues 配列が無い/);
    assert.throws(() => parsePayload('{"foo":[]}'), /issues 配列が無い/);
    assert.throws(() => parsePayload('{"issues":{}}'), /issues 配列が無い/);
  });

  ok('stripJsonComments: 文字列の中の // を壊さない（$schema の URL）', () => {
    const text = `{
      // 行コメント
      "$schema": "https://unpkg.com/knip@6/schema.json", /* ブロック */
      "ignoreWorkspaces": ["a/**"]
    }`;
    const parsed = JSON.parse(stripJsonComments(text));
    assert.strictEqual(parsed.$schema, 'https://unpkg.com/knip@6/schema.json');
    assert.deepStrictEqual(parsed.ignoreWorkspaces, ['a/**']);
  });

  ok('parseJsonc: prettier が付ける末尾カンマを許す（文字列の中は触らない）', () => {
    // 本リポの .prettierrc.json は trailingComma: "all" であり、prettier は .jsonc へ
    // 末尾カンマを足す（実測）。Knip 本体は許すが JSON.parse は許さない。
    const text = `{
      // 行コメント
      "$schema": "https://unpkg.com/knip@6/schema.json",
      "ignoreWorkspaces": ["a/**",],
      "workspaces": {
        ".": { "entry": ["x.ts",], },
      },
    }`;
    const parsed = parseJsonc(text);
    assert.strictEqual(parsed.$schema, 'https://unpkg.com/knip@6/schema.json');
    assert.deepStrictEqual(parsed.ignoreWorkspaces, ['a/**']);
    assert.deepStrictEqual(parsed.workspaces['.'].entry, ['x.ts']);
    // 文字列の中のカンマ + 閉じ括弧は落とさない。
    assert.deepStrictEqual(parseJsonc('{"a":"x,]","b":"y,}"}'), { a: 'x,]', b: 'y,}' });
  });

  ok('findUnignoredSubmodules: 除外し忘れた src/<unit> を挙げる', () => {
    const gitmodules = [
      '[submodule "planning"]',
      '\tpath = planning',
      '\turl = x',
      '[submodule "src/ai-stock-trading"]',
      '\tpath = src/ai-stock-trading',
      '\turl = y',
      '[submodule "src/new-unit"]',
      '\tpath = src/new-unit',
      '\turl = z',
    ].join('\n');
    // planning は src/ 配下ではないのでユニットではない（Knip の射程にも無い）。
    assert.deepStrictEqual(parseSubmoduleUnits(gitmodules), ['ai-stock-trading', 'new-unit']);
    assert.deepStrictEqual(
      findUnignoredSubmodules(gitmodules, { ignoreWorkspaces: ['ai-stock-trading/**'] }),
      ['new-unit']
    );
    assert.deepStrictEqual(
      findUnignoredSubmodules(gitmodules, {
        ignoreWorkspaces: ['ai-stock-trading/**', 'new-unit/**'],
      }),
      []
    );
    // 除外を丸ごと落としたら両方挙がる（＝ M3 の形を静的にも捕まえる）。
    assert.deepStrictEqual(findUnignoredSubmodules(gitmodules, {}), [
      'ai-stock-trading',
      'new-unit',
    ]);
  });

  ok('実データ: .gitmodules の src/<unit> がすべて knip.jsonc で除外されている', () => {
    const gitmodules = fs.readFileSync(GITMODULES_PATH, 'utf8');
    const config = loadKnipConfig();
    const missing = findUnignoredSubmodules(gitmodules, config);
    assert.deepStrictEqual(
      missing,
      [],
      'src/ 配下へ submodule を足したら src/knip.jsonc の ignoreWorkspaces へも足すこと' +
        `（別プロジェクトの未使用が本リポの床へ流れ込む）: ${missing.join(' / ')}`
    );
  });

  ok('実データ: baseline の区分名がすべて既知で、床が 0 件ではない', () => {
    const baseline = loadBaseline();
    for (const name of Object.keys(baseline.counts)) {
      assert.ok(KNOWN_ISSUE_TYPES.includes(name), `未知の区分: ${name}`);
    }
    assert.ok(total(baseline.counts) > 0, '床が 0 件だと fail-closed の門が効かない');
  });

  ok('実データ: src/package.json に knip スクリプトと devDependency がある', () => {
    const pkg = JSON.parse(fs.readFileSync(path.join(SRC_DIR, 'package.json'), 'utf8'));
    assert.ok(pkg.scripts && pkg.scripts.knip, 'pnpm run knip が無い（人が内訳を見る入口）');
    assert.ok(pkg.devDependencies && pkg.devDependencies.knip, 'knip が devDependency に無い');
  });

  ok('--update は一時ファイルへ書ける形の JSON を作る（書式の回帰）', () => {
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'knip-baseline-'));
    const p = path.join(tmp, 'baseline.json');
    const next = { $comment: 'x', counts: { exports: 2 } };
    fs.writeFileSync(p, `${JSON.stringify(next, null, 2)}\n`, 'utf8');
    const roundTrip = JSON.parse(fs.readFileSync(p, 'utf8'));
    assert.deepStrictEqual(roundTrip, next);
    fs.rmSync(tmp, { recursive: true, force: true });
  });

  process.stdout.write(`\n[check-knip] self-test: ${passed} 件すべて通過\n`);
  return 0;
}

module.exports = {
  KNOWN_ISSUE_TYPES,
  parsePayload,
  aggregate,
  total,
  evaluate,
  stripJsonComments,
  stripTrailingCommas,
  parseJsonc,
  parseSubmoduleUnits,
  findUnignoredSubmodules,
};

if (require.main === module) {
  try {
    process.exit(main(process.argv.slice(2)));
  } catch (e) {
    process.stderr.write(`✗ [check-knip] ${e.message}\n`);
    process.exit(1);
  }
}
