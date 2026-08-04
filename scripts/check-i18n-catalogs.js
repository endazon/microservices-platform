#!/usr/bin/env node
'use strict';
/*
 * check-i18n-catalogs.js
 * Lingui カタログの未翻訳キーを検出する（ADR-0031 / Issue #496 / IADR-0125 決定 4）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 保証する内容（1 行で言えること）:
 *   「**すべてのロケールの、すべてのエントリの `msgstr` が非空**であり、
 *     `fuzzy` フラグと `#~`（obsolete）が残っていない」
 *
 * なぜ自前の検査が要るか:
 *   - `lingui extract` の**再生成差分検査**（`git diff --exit-code`）は「カタログを更新したか」しか
 *     見ていない。`extract` は未訳のメッセージを `msgstr ""` の**空エントリとして生成するのが正常動作**
 *     であり、**未翻訳のまま差分検査を通過する**（実測は作業仕様書 §検証）。
 *   - `lingui compile --strict` は欠落を見るが、**sourceLocale（本リポでは ja）は検査対象外**である
 *     （`lingui extract` の統計表でも ja の Missing は `-` と表示される）。ja の訳文が空でも通る。
 *   本検査は上記 2 つの穴を、ロケールによらない単純な不変条件で塞ぐ。3 者は互いを置き換えない。
 *
 * fail-closed:
 *   - 設定（src/lingui.config.ts）が読めない、カタログが 1 つも無い、宣言したロケールの
 *     カタログが欠けている——いずれも **fail** とする。「見つからないから素通り」は、
 *     カタログの置き場が変わったときに検査を黙って無効化する。
 *
 * 使い方:
 *   node scripts/check-i18n-catalogs.js             # 違反があれば終了コード 1
 *   node scripts/check-i18n-catalogs.js --self-test # 検査ロジック自体の自己試験（正例・負例）
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const { warn } = require('./lib/ci-annotate');

const REPO_ROOT = path.resolve(__dirname, '..');
/** Lingui 設定の置き場（ワークスペースルート）。値の正は当該ファイルである。 */
const LINGUI_CONFIG = path.join('src', 'lingui.config.ts');

// --- 純粋ロジック（scripts/scripts.repo.test.js から単体テストする） ---------------

/**
 * `src/lingui.config.ts` から locales とカタログのパステンプレートを読む。
 *
 * TypeScript の設定ファイルを**実行せずに**正規表現で読む。実行するには TS ローダが要り、
 * 「検査器は外部依存ゼロ」という本リポの作法（`scripts/README.md`）を崩すためである。
 * 読み取りに失敗したら fail-closed で例外にする（既定値で代用しない——設定を書き換えて
 * カタログの置き場が変わったときに、検査が古い場所を見て緑になるのを防ぐ）。
 */
function parseLinguiConfig(source) {
  const localesMatch = /locales\s*:\s*\[([^\]]*)\]/.exec(source);
  if (!localesMatch) throw new Error('lingui.config.ts から locales を読み取れない');
  const locales = localesMatch[1]
    .split(',')
    .map((s) => s.trim().replace(/^['"]|['"]$/g, ''))
    .filter(Boolean);
  if (locales.length === 0) throw new Error('lingui.config.ts の locales が空である');

  const pathMatch = /path\s*:\s*['"]([^'"]*\{locale\}[^'"]*)['"]/.exec(source);
  if (!pathMatch) throw new Error('lingui.config.ts から catalogs[].path を読み取れない');

  const sourceLocaleMatch = /sourceLocale\s*:\s*['"]([^'"]+)['"]/.exec(source);

  return {
    locales,
    pathTemplate: pathMatch[1],
    sourceLocale: sourceLocaleMatch ? sourceLocaleMatch[1] : null,
  };
}

/** `<rootDir>/…/{locale}/messages` を実ファイルパス（.po）へ展開する。rootDir は設定ファイルのあるディレクトリ。 */
function catalogPathFor(pathTemplate, rootDir, locale) {
  const expanded = pathTemplate.replace('<rootDir>', rootDir).replace('{locale}', locale);
  return `${expanded}.po`;
}

/**
 * PO ファイルを最小限に解析する。返すのは検査に要る情報だけ（完全な gettext パーサではない）。
 *
 * 扱う対象:
 *   - `msgid` / `msgstr`（複数行の連結を含む）
 *   - `#,` のフラグ行（`fuzzy` を見る）
 *   - `#~` で始まる obsolete エントリ
 * 先頭のヘッダエントリ（`msgid ""`）は翻訳ではないため除く。
 */
function parsePo(content) {
  const entries = [];
  let obsoleteCount = 0;
  let current = null;
  let field = null;

  const flush = () => {
    if (current && current.msgid !== '') entries.push(current);
    current = null;
    field = null;
  };

  for (const rawLine of String(content).split(/\r?\n/)) {
    const line = rawLine.trim();

    if (line.startsWith('#~')) {
      obsoleteCount += 1;
      continue;
    }
    if (line === '') {
      flush();
      continue;
    }
    if (line.startsWith('#')) {
      if (current === null) current = { msgid: null, msgstr: '', fuzzy: false };
      if (line.startsWith('#,') && /\bfuzzy\b/.test(line)) current.fuzzy = true;
      field = null;
      continue;
    }

    let m = /^msgid\s+"((?:[^"\\]|\\.)*)"$/.exec(line);
    if (m) {
      if (current === null) current = { msgid: null, msgstr: '', fuzzy: false };
      current.msgid = m[1];
      field = 'msgid';
      continue;
    }
    m = /^msgstr\s+"((?:[^"\\]|\\.)*)"$/.exec(line);
    if (m) {
      if (current === null) current = { msgid: null, msgstr: '', fuzzy: false };
      current.msgstr = m[1];
      field = 'msgstr';
      continue;
    }
    // 継続行（`"..."` だけの行）は直前のフィールドへ連結する。
    m = /^"((?:[^"\\]|\\.)*)"$/.exec(line);
    if (m && current && field) {
      current[field] += m[1];
      continue;
    }
    // `msgctxt` 等は検査に使わないので読み飛ばす（フィールドの継続だけ切る）。
    field = null;
  }
  flush();

  return { entries, obsoleteCount };
}

/** 1 つのカタログを検査し、違反の配列を返す（空なら適合）。 */
function inspectCatalog(relPath, locale, content) {
  const violations = [];
  const { entries, obsoleteCount } = parsePo(content);

  if (entries.length === 0) {
    violations.push({ file: relPath, locale, kind: 'empty-catalog', msgid: null });
  }
  for (const entry of entries) {
    if (entry.msgid === null) continue;
    if (entry.msgstr === '') {
      violations.push({ file: relPath, locale, kind: 'untranslated', msgid: entry.msgid });
    }
    if (entry.fuzzy) {
      violations.push({ file: relPath, locale, kind: 'fuzzy', msgid: entry.msgid });
    }
  }
  if (obsoleteCount > 0) {
    violations.push({ file: relPath, locale, kind: 'obsolete', msgid: null, count: obsoleteCount });
  }
  return violations;
}

/** 違反 1 件の説明文。 */
function describe(v) {
  switch (v.kind) {
    case 'untranslated':
      return `${v.file}: 未翻訳（msgstr が空）: "${v.msgid}"`;
    case 'fuzzy':
      return `${v.file}: fuzzy フラグが残っている: "${v.msgid}"`;
    case 'obsolete':
      return `${v.file}: obsolete エントリ（#~）が ${v.count} 行残っている。lingui extract --clean で除去する`;
    case 'empty-catalog':
      return `${v.file}: カタログにエントリが 1 件も無い`;
    default:
      return `${v.file}: 未知の違反 ${v.kind}`;
  }
}

// --- 実行 ---------------------------------------------------------------------

function main(argv) {
  const root = process.env.I18N_CATALOGS_ROOT || REPO_ROOT;
  const configPath = path.join(root, LINGUI_CONFIG);

  if (!fs.existsSync(configPath)) {
    process.stderr.write(
      `✗ ${LINGUI_CONFIG} が見つからない。i18n の設定が移動したなら本検査の参照先も追随させること。\n`,
    );
    return 1;
  }

  let config;
  try {
    config = parseLinguiConfig(fs.readFileSync(configPath, 'utf8'));
  } catch (e) {
    process.stderr.write(`✗ ${LINGUI_CONFIG} を解釈できない: ${e.message}\n`);
    return 1;
  }

  const rootDir = path.dirname(configPath);
  const violations = [];
  let checked = 0;

  for (const locale of config.locales) {
    const abs = catalogPathFor(config.pathTemplate, rootDir, locale);
    const rel = path.relative(root, abs).split(path.sep).join('/');
    if (!fs.existsSync(abs)) {
      // fail-closed。宣言したロケールのカタログが無いのは「まだ訳していない」の極端形である。
      violations.push({ file: rel, locale, kind: 'empty-catalog', msgid: null });
      continue;
    }
    checked += 1;
    violations.push(...inspectCatalog(rel, locale, fs.readFileSync(abs, 'utf8')));
  }

  if (checked === 0) {
    process.stderr.write('✗ 検査できたカタログが 1 つも無い（fail-closed）。\n');
    return 1;
  }

  if (violations.length > 0) {
    process.stderr.write(`✗ Lingui カタログに ${violations.length} 件の違反がある。\n`);
    for (const v of violations) process.stderr.write(`  - ${describe(v)}\n`);
    process.stderr.write(
      '\n  未翻訳は各ロケールの messages.po の msgstr を埋めて `pnpm run i18n` を実行する。\n',
    );
    return 1;
  }

  process.stdout.write(
    `[check-i18n-catalogs] OK: ${checked} ロケール（${config.locales.join(' / ')}）のカタログに` +
      ' 未翻訳・fuzzy・obsolete はありません。\n',
  );
  if (config.sourceLocale === null) {
    warn('lingui.config.ts の sourceLocale を読み取れなかった（検査自体は全ロケール一律のため影響しない）。');
  }
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

  const HEADER = 'msgid ""\nmsgstr ""\n"Language: en\\n"\n\n';

  ok('parsePo: ヘッダを翻訳エントリとして数えない', () => {
    const { entries } = parsePo(HEADER);
    assert.strictEqual(entries.length, 0);
  });

  ok('parsePo: msgid / msgstr を読む', () => {
    const { entries } = parsePo(`${HEADER}msgid "hello"\nmsgstr "こんにちは"\n`);
    assert.deepStrictEqual(entries, [{ msgid: 'hello', msgstr: 'こんにちは', fuzzy: false }]);
  });

  ok('parsePo: 複数行の継続を連結する', () => {
    const { entries } = parsePo(`${HEADER}msgid "a"\nmsgstr "one "\n"two"\n`);
    assert.strictEqual(entries[0].msgstr, 'one two');
  });

  ok('parsePo: fuzzy フラグと obsolete を検出する', () => {
    const { entries, obsoleteCount } = parsePo(
      `${HEADER}#, fuzzy\nmsgid "a"\nmsgstr "b"\n\n#~ msgid "old"\n#~ msgstr "furui"\n`,
    );
    assert.strictEqual(entries[0].fuzzy, true);
    assert.strictEqual(obsoleteCount, 2);
  });

  ok('inspectCatalog: 訳文が埋まっていれば違反ゼロ（正例）', () => {
    const v = inspectCatalog('en.po', 'en', `${HEADER}msgid "a"\nmsgstr "A"\n`);
    assert.deepStrictEqual(v, []);
  });

  ok('inspectCatalog: 空の msgstr を未翻訳として検出する（負例）', () => {
    const v = inspectCatalog('en.po', 'en', `${HEADER}msgid "a"\nmsgstr ""\n`);
    assert.strictEqual(v.length, 1);
    assert.strictEqual(v[0].kind, 'untranslated');
    assert.strictEqual(v[0].msgid, 'a');
  });

  ok('inspectCatalog: エントリが 1 件も無いカタログを検出する（負例）', () => {
    const v = inspectCatalog('en.po', 'en', HEADER);
    assert.strictEqual(v.length, 1);
    assert.strictEqual(v[0].kind, 'empty-catalog');
  });

  ok('inspectCatalog: fuzzy / obsolete も違反にする（負例）', () => {
    const v = inspectCatalog(
      'en.po',
      'en',
      `${HEADER}#, fuzzy\nmsgid "a"\nmsgstr "A"\n\n#~ msgid "old"\n`,
    );
    assert.deepStrictEqual(v.map((x) => x.kind).sort(), ['fuzzy', 'obsolete']);
  });

  ok('parseLinguiConfig: locales / path / sourceLocale を読む', () => {
    const cfg = parseLinguiConfig(
      "export default defineConfig({ sourceLocale: 'ja', locales: ['ja', 'en'],\n" +
        "  catalogs: [{ path: '<rootDir>/a/locales/{locale}/messages' }] });",
    );
    assert.deepStrictEqual(cfg.locales, ['ja', 'en']);
    assert.strictEqual(cfg.sourceLocale, 'ja');
    assert.strictEqual(cfg.pathTemplate, '<rootDir>/a/locales/{locale}/messages');
  });

  ok('parseLinguiConfig: 読み取れなければ例外（fail-closed）', () => {
    assert.throws(() => parseLinguiConfig('export default {}'), /locales/);
    assert.throws(
      () => parseLinguiConfig("export default { locales: ['ja'] }"),
      /catalogs\[\]\.path/,
    );
  });

  ok('catalogPathFor: <rootDir> と {locale} を展開する', () => {
    assert.strictEqual(
      catalogPathFor('<rootDir>/a/{locale}/messages', '/repo/src', 'en'),
      '/repo/src/a/en/messages.po',
    );
  });

  // main() の fail-closed（設定が無い / カタログが無い）を実ファイルで確認する。
  ok('main: lingui.config.ts が無ければ exit 1（fail-closed）', () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), 'i18ncat-'));
    try {
      process.env.I18N_CATALOGS_ROOT = base;
      assert.strictEqual(main([]), 1);
    } finally {
      delete process.env.I18N_CATALOGS_ROOT;
      fs.rmSync(base, { recursive: true, force: true });
    }
  });

  ok('main: カタログが欠けていれば exit 1（fail-closed）', () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), 'i18ncat-'));
    try {
      fs.mkdirSync(path.join(base, 'src'), { recursive: true });
      fs.writeFileSync(
        path.join(base, LINGUI_CONFIG),
        "export default defineConfig({ sourceLocale: 'ja', locales: ['ja','en'],\n" +
          "  catalogs: [{ path: '<rootDir>/loc/{locale}/messages' }] });",
      );
      process.env.I18N_CATALOGS_ROOT = base;
      assert.strictEqual(main([]), 1);
    } finally {
      delete process.env.I18N_CATALOGS_ROOT;
      fs.rmSync(base, { recursive: true, force: true });
    }
  });

  process.stdout.write(
    failures.length === 0
      ? '\n[check-i18n-catalogs --self-test] all passed\n'
      : `\n[check-i18n-catalogs --self-test] ${failures.length} failed\n`,
  );
  return failures.length === 0 ? 0 : 1;
}

module.exports = {
  parseLinguiConfig,
  catalogPathFor,
  parsePo,
  inspectCatalog,
  describe,
  main,
};

if (require.main === module) {
  const argv = process.argv.slice(2);
  process.exit(argv.includes('--self-test') ? selfTest() : main(argv));
}
