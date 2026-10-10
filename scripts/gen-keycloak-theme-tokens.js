#!/usr/bin/env node
'use strict';
/*
 * gen-keycloak-theme-tokens.js
 * SC-13〜16, ADR-0026, ADR-0031, IADR-0532: Keycloak テーマ（deploy/keycloak/themes/platform）の色・角丸・フォントを
 * SPA の共有 UI（src/packages/ui/src/styles.css。IADR-0435 の二層トークン）から**生成**する。外部依存ゼロ（Node 標準のみ）。
 *
 * なぜ生成か: Keycloak の画面は SPA と別オリジン・別ビルドで描かれる。値を手で写すと、SPA 側の是正
 * （例: IADR-0435 決定 4 のダーク accent の持ち上げ）が Keycloak 側へ届かず、**見た目だけが静かにずれる**。
 * 単一情報源は styles.css であり、テーマ側の tokens.css は生成物（コミットする。CI が --check で差分を止める）。
 *
 * 読むもの（styles.css）:
 *   - ダーク（既定）= 最初の `:root { … }`、ライト = `:root[data-theme='light'] { … }` の意味トークン（--color-*）。
 *   - `@theme { … }` のフォント・角丸・意味色の原色。
 * 書くもの:
 *   - deploy/keycloak/themes/platform/{login,account}/resources/css/tokens.css（同一内容）
 * 確かめるもの（--check のみ。生成はしない）:
 *   - 値を直書きするしかないファイル（メールの外枠＝ライトのみ・アカウントのロゴ／favicon の SVG＝ダーク∪ライト）の
 *     16 進色が**意味トークンの値に限られる**こと（CSS 変数が届かない。ずれはここで止める）。
 *   - login の「このデバイスを記憶（N日）」の N が realm の ssoSessionMaxLifespanRememberMe と一致すること。
 *
 * 使い方:
 *   node scripts/gen-keycloak-theme-tokens.js            # 生成（上書き）
 *   node scripts/gen-keycloak-theme-tokens.js --check    # 差分があれば exit 1（CI）
 *   node scripts/gen-keycloak-theme-tokens.js --self-test
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const SOURCE = 'src/packages/ui/src/styles.css';
const THEME_ROOT = 'deploy/keycloak/themes/platform';
const OUTPUTS = [`${THEME_ROOT}/login/resources/css/tokens.css`, `${THEME_ROOT}/account/resources/css/tokens.css`];
// 値を直書きするしかないファイル（CSS 変数を解さない）と、許す色の集合。
//   メール = ライトだけ（メールクライアントは prefers-color-scheme を確実には解さない）。
//   SVG（ロゴ・favicon。<img> として読まれ、ページの CSS 変数が届かない）= ダーク ∪ ライト。
const LITERAL_COLOR_FILES = [
  { file: `${THEME_ROOT}/email/html/template.ftl`, schemes: ['light'] },
  { file: `${THEME_ROOT}/account/resources/logo.svg`, schemes: ['dark', 'light'] },
  { file: `${THEME_ROOT}/account/resources/favicon.svg`, schemes: ['dark', 'light'] },
];
const REALM = 'deploy/keycloak/microservices-platform-realm.json';
const LOGIN_MESSAGES = [`${THEME_ROOT}/login/messages/messages_ja.properties`, `${THEME_ROOT}/login/messages/messages_en.properties`];

// テーマが使う意味トークン（IADR-0435 決定 1 の第 2 層）。styles.css に 1 つでも欠ければ生成を止める（黙って欠けた CSS を出さない）。
const SEMANTIC = [
  'bg', 'surface', 'surface-muted', 'border', 'divider', 'fg', 'fg-muted',
  'brand', 'brand-fg', 'accent', 'accent-soft', 'success', 'warning', 'danger',
];
// 第 1 層（@theme）から写す素材。
const MATERIAL = ['font-sans', 'font-mono', 'radius-sm', 'radius-md', 'radius-lg'];

/** CSS のコメントを除く（値の抽出だけが目的。文字列内の `/*` は styles.css に無い）。 */
function stripComments(css) {
  return css.replace(/\/\*[\s\S]*?\*\//g, '');
}

/** `selector {` に続く最初のブロック本文を返す（入れ子なし前提）。無ければ null。 */
function blockAfter(css, selectorRe) {
  const m = selectorRe.exec(css);
  if (!m) return null;
  const start = css.indexOf('{', m.index);
  const end = css.indexOf('}', start);
  if (start < 0 || end < 0) return null;
  return css.slice(start + 1, end);
}

/** ブロック本文から `--name: value;` を Map にする（値の空白は 1 個へ畳む）。 */
function declarations(body) {
  const out = new Map();
  const re = /--([a-z0-9-]+)\s*:\s*([^;]+);/gi;
  let m;
  while ((m = re.exec(body)) !== null) out.set(m[1], m[2].replace(/\s+/g, ' ').trim());
  return out;
}

/** styles.css の本文からトークンを抽出する。欠けがあれば例外（fail-loud）。 */
function extractTokens(cssText) {
  const css = stripComments(cssText);
  // `@theme {`（inline ではない方）。`@theme inline {` を拾わないよう、直後が `{` のものに限る。
  const theme = blockAfter(css, /@theme\s*\{/);
  // 最初の素の `:root {`（`:root[` や `:root:not(` ではない）。
  const dark = blockAfter(css, /(^|\n)\s*:root\s*\{/);
  const light = blockAfter(css, /:root\[data-theme='light'\]\s*\{/);
  if (theme === null || dark === null || light === null) {
    throw new Error(`${SOURCE} から @theme / :root / :root[data-theme='light'] のいずれかを読めない`);
  }
  const t = declarations(theme);
  const d = declarations(dark);
  const l = declarations(light);
  const missing = [];
  for (const k of MATERIAL) if (!t.has(k)) missing.push(`@theme --${k}`);
  for (const k of SEMANTIC) {
    if (!d.has(`color-${k}`)) missing.push(`:root --color-${k}`);
    if (!l.has(`color-${k}`)) missing.push(`:root[data-theme='light'] --color-${k}`);
  }
  if (missing.length) throw new Error(`${SOURCE} にトークンが無い: ${missing.join(', ')}`);
  return {
    material: MATERIAL.map((k) => [k, t.get(k)]),
    dark: SEMANTIC.map((k) => [`color-${k}`, d.get(`color-${k}`)]),
    light: SEMANTIC.map((k) => [`color-${k}`, l.get(`color-${k}`)]),
  };
}

/** tokens.css の本文を作る。既定はダーク（SPA の `:root` と同じ）、OS がライトならライト。 */
function renderTokensCss(tokens) {
  const decl = (pairs, indent) => pairs.map(([k, v]) => `${indent}--${k}: ${v};`).join('\n');
  return [
    '/*',
    ' * 生成物 —— 手で編集しない。`node scripts/gen-keycloak-theme-tokens.js` が',
    ` * ${SOURCE}（SPA の共有 UI のトークン。IADR-0435）から作る。CI が --check で差分を止める（IADR-0532）。`,
    ' *',
    ' * SPA と同じく既定はダーク、OS がライトならライト（prefers-color-scheme）。Keycloak は別オリジンのため',
    ' * SPA の「表示」切替（localStorage）は届かない。OS 設定に従う＝SPA の既定（system）と同じ振る舞いである。',
    ' */',
    ':root {',
    '  color-scheme: dark light;',
    decl(tokens.material, '  '),
    decl(tokens.dark, '  '),
    '}',
    '',
    '@media (prefers-color-scheme: light) {',
    '  :root {',
    '    color-scheme: light;',
    decl(tokens.light, '    '),
    '  }',
    '}',
    '',
  ].join('\n');
}

/** text の 16 進色のうち、指定した明暗の意味トークンの値に無いものを返す。 */
function foreignColors(text, tokens, schemes = ['light']) {
  const allowed = new Set(
    schemes.flatMap((s) => tokens[s]).map(([, v]) => v.toLowerCase()).filter((v) => /^#[0-9a-f]{3,8}$/.test(v)),
  );
  const found = text.match(/#[0-9a-fA-F]{6}\b|#[0-9a-fA-F]{3}\b/g) || [];
  return [...new Set(found.map((c) => c.toLowerCase()))].filter((c) => !allowed.has(c));
}

/** 「このデバイスを記憶（N日）」等の N と realm の記憶期間（日）を突き合わせる。齟齬の説明を返す。 */
function rememberMeGaps(messagesByFile, realm) {
  const gaps = [];
  const seconds = realm.ssoSessionMaxLifespanRememberMe;
  const days = Number.isInteger(seconds) ? seconds / 86400 : NaN;
  for (const [file, text] of messagesByFile) {
    const line = /^rememberMe=(.*)$/m.exec(text);
    if (!line) continue;
    const n = /(\d+)\s*(日|days?)/.exec(line[1]);
    if (!n) continue;
    if (Number(n[1]) !== days) {
      gaps.push(`${file}: rememberMe の ${n[1]} 日が realm の ssoSessionMaxLifespanRememberMe（${seconds} 秒 = ${days} 日）と食い違う`);
    }
  }
  return gaps;
}

const read = (rel) => fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');

function main(argv) {
  const check = argv.includes('--check');
  const tokens = extractTokens(read(SOURCE));
  const expected = renderTokensCss(tokens);
  const problems = [];
  for (const out of OUTPUTS) {
    const abs = path.join(REPO_ROOT, out);
    const current = fs.existsSync(abs) ? fs.readFileSync(abs, 'utf8') : null;
    if (current === expected) continue;
    if (check) problems.push(`${out} が ${SOURCE} から生成した内容と一致しない（node scripts/gen-keycloak-theme-tokens.js で再生成する）`);
    else {
      fs.mkdirSync(path.dirname(abs), { recursive: true });
      fs.writeFileSync(abs, expected);
      process.stdout.write(`wrote ${out}\n`);
    }
  }
  for (const { file, schemes } of LITERAL_COLOR_FILES) {
    if (!fs.existsSync(path.join(REPO_ROOT, file))) {
      if (check) problems.push(`${file} が無い`);
      continue;
    }
    for (const c of foreignColors(read(file), tokens, schemes)) {
      problems.push(`${file} の色 ${c} が意味トークン（${schemes.join(' / ')}）の値に無い（${SOURCE} の値だけを使う）`);
    }
  }
  const msgs = LOGIN_MESSAGES.filter((f) => fs.existsSync(path.join(REPO_ROOT, f))).map((f) => [f, read(f)]);
  problems.push(...rememberMeGaps(msgs, JSON.parse(read(REALM))));
  for (const p of problems) process.stderr.write(`✗ ${p}\n`);
  if (problems.length) return 1;
  process.stdout.write(`✓ Keycloak テーマのトークンは ${SOURCE} と一致する（${OUTPUTS.length} ファイル）\n`);
  return 0;
}

function selfTest() {
  const assert = require('assert');
  const cases = [];
  const ok = (name, fn) => cases.push([name, fn]);
  const SAMPLE = [
    '/* c :root { --color-bg: #bad; } */',
    "@import 'tailwindcss';",
    '@theme { --font-sans: system-ui, sans-serif; --font-mono: ui-monospace; --radius-sm: 4px; --radius-md: 8px; --radius-lg: 14px; }',
    '@theme inline { --color-bg: var(--color-bg); }',
    ':root {',
    ...SEMANTIC.map((k) => `  --color-${k}: #10101${SEMANTIC.indexOf(k) % 10};`),
    '}',
    '@media (prefers-color-scheme: light) { :root:not([data-theme=\'dark\']) { --color-bg: #f00; } }',
    ":root[data-theme='light'] {",
    ...SEMANTIC.map((k) => `  --color-${k}: #fefef${SEMANTIC.indexOf(k) % 10};`),
    '}',
  ].join('\n');

  ok('extractTokens: ダークは最初の素の :root、ライトは data-theme=light（コメント内の値・@theme inline を拾わない）', () => {
    const t = extractTokens(SAMPLE);
    assert.strictEqual(new Map(t.dark).get('color-bg'), '#101010');
    assert.strictEqual(new Map(t.light).get('color-bg'), '#fefef0');
    assert.strictEqual(new Map(t.material).get('radius-lg'), '14px');
  });
  ok('extractTokens: 意味トークンが 1 つでも欠ければ例外（欠けた CSS を黙って出さない）', () => {
    assert.throws(() => extractTokens(SAMPLE.replace('--color-danger: #10101', '--color-dangerx: #10101')), /--color-danger/);
  });
  ok('renderTokensCss: 既定ダーク＋ prefers-color-scheme: light の 2 段で、外部参照を含まない', () => {
    const css = renderTokensCss(extractTokens(SAMPLE));
    assert.ok(/:root \{[\s\S]*--color-bg: #101010;/.test(css));
    assert.ok(/@media \(prefers-color-scheme: light\) \{\n {2}:root \{[\s\S]*--color-bg: #fefef0;/.test(css));
    assert.ok(!/url\(|@import|https?:/.test(css));
  });
  ok('foreignColors: 指定した明暗の値は許し、それ以外の 16 進色を名指す（変異試験）', () => {
    const t = extractTokens(SAMPLE);
    assert.deepStrictEqual(foreignColors('<td style="color:#FEFEF0;background:#fefef1">', t, ['light']), []);
    assert.deepStrictEqual(foreignColors('<td style="color:#0066cc">', t, ['light']), ['#0066cc']);
    // ダークの値はメール（ライトのみ）では違反、SVG（ダーク∪ライト）では許す。
    assert.deepStrictEqual(foreignColors('fill:#101010', t, ['light']), ['#101010']);
    assert.deepStrictEqual(foreignColors('fill:#101010', t, ['dark', 'light']), []);
  });
  ok('rememberMeGaps: 日数が realm と一致すれば 0 件、食い違えば名指す', () => {
    const realm = { ssoSessionMaxLifespanRememberMe: 2592000 };
    assert.deepStrictEqual(rememberMeGaps([['ja', 'rememberMe=記憶（30日）']], realm), []);
    assert.strictEqual(rememberMeGaps([['en', 'rememberMe=Remember (14 days)']], realm).length, 1);
    assert.deepStrictEqual(rememberMeGaps([['ja', 'rememberMe=記憶する']], realm), []);
  });
  ok('実データ: styles.css から抽出でき、生成物はダーク accent の持ち上げ（IADR-0435 決定 4）を写している', () => {
    const t = extractTokens(read(SOURCE));
    assert.strictEqual(new Map(t.dark).get('color-accent'), new Map(t.dark).get('color-brand'));
    assert.ok(renderTokensCss(t).includes(`--color-accent: ${new Map(t.dark).get('color-accent')};`));
  });

  let failed = 0;
  for (const [name, fn] of cases) {
    try { fn(); process.stdout.write(`  ✓ ${name}\n`); } catch (e) { failed += 1; process.stdout.write(`  ✗ ${name}\n    ${e.message}\n`); }
  }
  process.stdout.write(`\n${cases.length - failed}/${cases.length} passed\n`);
  return failed ? 1 : 0;
}

module.exports = { extractTokens, renderTokensCss, foreignColors, rememberMeGaps, SEMANTIC, OUTPUTS };

if (require.main === module) {
  const argv = process.argv.slice(2);
  try {
    process.exit(argv.includes('--self-test') ? selfTest() : main(argv));
  } catch (e) {
    process.stderr.write(`✗ ${e.message}\n`);
    process.exit(1);
  }
}
