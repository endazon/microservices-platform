#!/usr/bin/env node
'use strict';
/*
 * check-secret-injected-options.js
 * **「実値は k8s Secret から環境変数で注入する」と自分で宣言した構成値が、配備で実際に注入されて
 * いること**を機械検査する（NFR / ADR-0032 ほか, IADR-0318 / Issue #1107）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。check-bff-downstreams.js / check-image-mapping.js と同型。
 *
 * ## 背景（同型の事故が 2 回）
 *
 * 1 回目 #1025: NotificationService は実装済みだったのに compose / helm / image-mapping の
 *   **どれにも載っていなかった**。
 * 2 回目 #1107: BFF セッション（`BffSessionOptions`）は実装もテストも揃っていたのに、
 *   **`BffSession__*` が `deploy/` に 1 行も無かった**。`ClientSecret` の既定は空文字なので、
 *   稼働クラスタでは `GET /bff/auth/login` が 500 を返し続けた。
 *   🔴 **単体テストは構成を自分で与えて走るので、この欠落では絶対に落ちない。**
 *
 * `CLAUDE.md`「同型の事故が 2 回起きたら検査を足す」に該当する。
 *
 * ## 不変条件
 *
 * 「Secret から注入する」と **XML doc コメントで宣言している**構成プロパティは、
 * `<SectionName>__<Property>` として
 *   (a) helm チャート（`deploy/helm/**`）に **secretKeyRef 由来の env** として現れ、
 *   (b) compose（`deploy/docker-compose.yml`）に **変数展開（`${...}`）の env** として現れる。
 * 平文リテラルでの注入は (a)(b) いずれでも違反にする —— **宣言は「Secret から」だからである。**
 *
 * ## 設計の要点
 *
 * - **列挙を持たない。** 対象はコードの側の宣言から導く。次に同じ性質の構成値が増えたとき、
 *   このファイルへ名前を書き足す必要が無い（書き足す設計だと、書き忘れが静かに検査を素通りする）。
 * - **0 件走査で緑を返さない。** 宣言の書式が変わって母集合が空になったら exit 1 にする
 *   （「何も無い」と「問題が無い」を同じ出力にしない。#797 の「沈黙の exit 0」）。
 * - **配備経路を 2 つとも見る。** #1107 の母集合の引き直しで、issue 本文の「宣言ファイル領域」に
 *   compose が無かった（helm だけ直すと compose 側は 500 のまま残る）。
 *
 * 使い方:
 *   node scripts/check-secret-injected-options.js             # 実ファイルを突合。違反があれば終了コード 1。
 *   node scripts/check-secret-injected-options.js --self-test # 検査ロジック自体の自己試験。
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');

/** 走査対象（本リポジトリが所有する backend。submodule は対象外）。 */
const SCAN_ROOTS = ['src/platform/backend', 'src/knowledge/backend'];

/** 除外パス（POSIX 区切りの部分一致）。 */
const EXCLUDED_DIRS = ['/bin/', '/obj/', '/Tests/', '/tests/', 'src/ai-stock-trading/'];

/**
 * 🔴 **母集合の入口となる宣言。** コード側がこの語で「Secret から注入する」と宣言する。
 * 語を変えるなら本定数も変えること —— 変え忘れは母集合 0 件になり、下の fail-closed が止める。
 */
const DECLARATION_MARKER = 'k8s Secret から環境変数で注入する';

/** 配備経路（両方を見る。片方だけ直すと、もう片方が同じ事故のまま残る）。 */
const HELM_DIR = 'deploy/helm';
const COMPOSE_FILE = 'deploy/docker-compose.yml';

/** ディレクトリを再帰的に走査して拡張子の合うファイルを返す。 */
function listFiles(absDir, ext, acc = []) {
  if (!fs.existsSync(absDir)) return acc;
  for (const entry of fs.readdirSync(absDir, { withFileTypes: true })) {
    const abs = path.join(absDir, entry.name);
    const rel = path.relative(REPO_ROOT, abs).split(path.sep).join('/');
    if (EXCLUDED_DIRS.some((d) => `/${rel}/`.includes(d))) continue;
    if (entry.isDirectory()) listFiles(abs, ext, acc);
    else if (entry.name.endsWith(ext)) acc.push(rel);
  }
  return acc;
}

/**
 * C# の Options クラス 1 ファイルから「Secret 注入を宣言したプロパティ」を取り出す。
 * 返すのは `{ section, property, env }` の配列。
 *
 * SectionName（`public const string SectionName = "BffSession";`）が無いファイルは、
 * 環境変数名を決められないので対象外とする（構成セクションではない Options 以外のクラス）。
 */
function parseDeclaredSecretOptions(source) {
  const section = /const\s+string\s+SectionName\s*=\s*"([^"]+)"/.exec(source);
  if (!section) return [];
  const lines = source.split(/\r?\n/);
  const found = [];
  // 直前の連続する doc コメント（/// …）だけを 1 プロパティの宣言として扱う。
  // 空行や他のメンバを挟んだら宣言は切れる（別プロパティの doc を誤って引き継がない）。
  let docBuffer = [];
  for (const line of lines) {
    const trimmed = line.trim();
    if (trimmed.startsWith('///')) { docBuffer.push(trimmed); continue; }
    const prop = /^public\s+[\w?<>,\[\]\s.]+?\s+(\w+)\s*\{\s*get;/.exec(trimmed);
    if (prop) {
      if (docBuffer.join('\n').includes(DECLARATION_MARKER)) {
        found.push({ section: section[1], property: prop[1], env: `${section[1]}__${prop[1]}` });
      }
      docBuffer = [];
      continue;
    }
    if (trimmed !== '') docBuffer = [];
  }
  return found;
}

/**
 * helm の YAML から「secretKeyRef 由来で注入されている env 名」を集める。
 * `- name: X` の後ろ数行に `secretKeyRef` が現れるものだけを採る（平文 `value:` は採らない）。
 * テンプレートは Go テンプレートなので YAML パーサへは通せない。行ベースで読む。
 */
function parseHelmSecretEnvNames(yaml) {
  const lines = yaml.split(/\r?\n/);
  const names = new Set();
  for (let i = 0; i < lines.length; i++) {
    const m = /^\s*-?\s*name:\s*([A-Za-z_][A-Za-z0-9_]*__[A-Za-z0-9_]+)\s*$/.exec(lines[i]);
    if (!m) continue;
    // 次の env（`- name:`）に当たるまでの範囲に secretKeyRef があるか。
    for (let j = i + 1; j < Math.min(i + 6, lines.length); j++) {
      if (/^\s*-\s*name:\s*/.test(lines[j])) break;
      if (/secretKeyRef/.test(lines[j])) { names.add(m[1]); break; }
    }
  }
  return names;
}

/**
 * compose の YAML から「変数展開で注入されている env 名」を集める。
 * `X__Y: ${VAR...}` の形だけを採る（平文リテラルは採らない）。
 */
function parseComposeInterpolatedEnvNames(yaml) {
  const names = new Set();
  for (const line of yaml.split(/\r?\n/)) {
    const m = /^\s*([A-Za-z_][A-Za-z0-9_]*__[A-Za-z0-9_]+)\s*:\s*(.+?)\s*$/.exec(line);
    if (m && m[2].includes('${')) names.add(m[1]);
  }
  return names;
}

/** 宣言と配備を突合して違反を返す（純関数。自己試験はここを叩く）。 */
function computeViolations({ declared, helmSecretEnvs, composeEnvs }) {
  const violations = [];
  for (const d of declared) {
    if (!helmSecretEnvs.has(d.env)) {
      violations.push({ env: d.env, where: 'helm', detail: `${HELM_DIR}/** に secretKeyRef 由来の env が無い` });
    }
    if (!composeEnvs.has(d.env)) {
      violations.push({ env: d.env, where: 'compose', detail: `${COMPOSE_FILE} に変数展開（\${...}）の env が無い` });
    }
  }
  return violations;
}

/** 実ファイルを読み、宣言の母集合と配備の実態を突合する。 */
function checkTree() {
  const declared = [];
  for (const root of SCAN_ROOTS) {
    for (const rel of listFiles(path.join(REPO_ROOT, root), 'Options.cs')) {
      const source = fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');
      for (const d of parseDeclaredSecretOptions(source)) declared.push({ ...d, file: rel });
    }
  }
  // fail-closed: 宣言が 1 件も見つからないのは「問題が無い」ではなく「走査が壊れた」である。
  if (declared.length === 0) {
    return {
      declared,
      fatal: `宣言（doc コメントの「${DECLARATION_MARKER}」）が 1 件も見つかりません。`
        + ' 走査対象・宣言の語が変わった可能性があります（0 件走査を緑にしない）。',
      violations: [],
    };
  }

  const helmSecretEnvs = new Set();
  for (const rel of [...listFiles(path.join(REPO_ROOT, HELM_DIR), '.yaml'), ...listFiles(path.join(REPO_ROOT, HELM_DIR), '.yml')]) {
    for (const n of parseHelmSecretEnvNames(fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8'))) helmSecretEnvs.add(n);
  }
  const composePath = path.join(REPO_ROOT, COMPOSE_FILE);
  if (!fs.existsSync(composePath)) {
    return { declared, fatal: `${COMPOSE_FILE} が見つかりません（走査が壊れています）。`, violations: [] };
  }
  const composeEnvs = parseComposeInterpolatedEnvNames(fs.readFileSync(composePath, 'utf8'));

  return { declared, fatal: null, violations: computeViolations({ declared, helmSecretEnvs, composeEnvs }) };
}

function selfTest() {
  const cases = [];
  const expect = (name, pass, actual) => cases.push({ name, pass, actual });

  const optionsFixture = [
    'namespace X;',
    'public sealed class SampleOptions',
    '{',
    '    public const string SectionName = "Sample";',
    '',
    '    /// <summary>ただの設定。</summary>',
    '    public string Authority { get; set; } = "http://a";',
    '',
    '    /// <summary>',
    `    /// 実値は ${DECLARATION_MARKER}（minio と同じ形）。`,
    '    /// </summary>',
    '    public string ClientSecret { get; set; } = string.Empty;',
    '}',
  ].join('\n');
  const declared = parseDeclaredSecretOptions(optionsFixture);
  expect('宣言のあるプロパティだけを採る', declared.length === 1 && declared[0].property === 'ClientSecret', declared);
  expect('env 名は Section__Property', declared[0] && declared[0].env === 'Sample__ClientSecret', declared[0]);

  // doc は直前の連続コメントだけ。空行を挟んだ次のプロパティへ引き継がない。
  const leakFixture = [
    'public sealed class LeakOptions',
    '{',
    '    public const string SectionName = "Leak";',
    '    /// <summary>',
    `    /// ${DECLARATION_MARKER}`,
    '    /// </summary>',
    '    public string Secret { get; set; } = string.Empty;',
    '',
    '    public string Plain { get; set; } = "x";',
    '}',
  ].join('\n');
  const leak = parseDeclaredSecretOptions(leakFixture);
  expect('後続プロパティへ doc が漏れない', leak.length === 1 && leak[0].property === 'Secret', leak);

  // SectionName の無いクラスは対象外（env 名を決められない）。
  expect('SectionName 無しは対象外', parseDeclaredSecretOptions(`/// ${DECLARATION_MARKER}\npublic string A { get; set; }`).length === 0, null);

  const helmFixture = [
    '            - name: Sample__Authority',
    '              value: "http://a"',
    '            - name: Sample__ClientSecret',
    '              valueFrom:',
    '                secretKeyRef:',
    '                  name: sample-oidc',
    '                  key: client-secret',
  ].join('\n');
  const helmEnvs = parseHelmSecretEnvNames(helmFixture);
  expect('helm: secretKeyRef 由来だけを採る', helmEnvs.has('Sample__ClientSecret') && !helmEnvs.has('Sample__Authority'), [...helmEnvs]);

  const composeFixture = [
    '      Sample__ClientSecret: ${SAMPLE_SECRET:-sample-dev-secret-change-me}',
    '      Sample__Plain: literal-value',
  ].join('\n');
  const composeEnvs = parseComposeInterpolatedEnvNames(composeFixture);
  expect('compose: 変数展開だけを採る', composeEnvs.has('Sample__ClientSecret') && !composeEnvs.has('Sample__Plain'), [...composeEnvs]);

  const ok = computeViolations({ declared, helmSecretEnvs: helmEnvs, composeEnvs });
  expect('両経路が揃っていれば違反 0', ok.length === 0, ok);

  // #1107 の再現: helm にも compose にも無い（＝当時の develop の状態）→ 2 件の違反。
  const gap = computeViolations({ declared, helmSecretEnvs: new Set(), composeEnvs: new Set() });
  expect('#1107 の欠落（両経路とも無し）を検出', gap.length === 2, gap);

  // 平文で注入していても「Secret から注入する」宣言は満たさない。
  const plainOnly = computeViolations({
    declared,
    helmSecretEnvs: new Set(),
    composeEnvs: parseComposeInterpolatedEnvNames('      Sample__ClientSecret: hardcoded'),
  });
  expect('平文注入は違反のまま', plainOnly.some((v) => v.where === 'compose'), plainOnly);

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) { console.error(`[check-secret-injected-options] 自己試験 ${failed} 件 失敗。`); process.exit(1); }
  console.log(`[check-secret-injected-options] 自己試験 ${cases.length} 件 OK。`);
}

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }
  const { declared, fatal, violations } = checkTree();
  if (fatal) {
    console.error(`[check-secret-injected-options] ${fatal}`);
    process.exit(1);
  }
  if (violations.length === 0) {
    console.log(`[check-secret-injected-options] OK: Secret 注入を宣言した構成値 ${declared.length} 件が、helm と compose の両方で注入されています。`);
    process.exit(0);
  }
  console.error(`[check-secret-injected-options] ${violations.length} 件の注入漏れを検出しました:`);
  for (const v of violations) console.error(`\n  [${v.where}] ${v.env}: ${v.detail}`);
  console.error('\n設計の根拠は .ai-context/adr/IADR-0318_bff-session-deploy-config.md を参照。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  DECLARATION_MARKER,
  parseDeclaredSecretOptions,
  parseHelmSecretEnvNames,
  parseComposeInterpolatedEnvNames,
  computeViolations,
  checkTree,
};
