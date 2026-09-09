#!/usr/bin/env node
'use strict';
/*
 * check-workflow-job-refs.js
 * NFR / issue #1348: **文書が名指しする CI ジョブ名が、ワークフロー実物のジョブとして実在するか**を突合する。
 *
 * なぜ要るか（同型の事故が 2 回。CLAUDE.md「検査器の追加は 2 回起きたら」を満たす）:
 *   1. IADR-0232 決定 6 で 1 検査 = 1 ジョブを `static-checks` へ束ねた際、`scripts/README.md` の表は
 *      追随したが **`docs/DEFINITION_OF_DONE.md` / `docs/ai-workflow.md` の「`doc-links` ジョブ」は残った**
 *      （#1348 指摘 B-3。廃止済みジョブ名を 3 文書が 4 箇所で名指ししていた）。
 *   2. `docs/ai-workflow.md` の必須チェック表は行数が増えた（#936 で `scripts-tests` を追加）のに
 *      「下表の **7 件**」の数だけが残った（#1348 指摘 B-2。実表は CodeQL を除いて 8 行）。
 *   `scripts/README.md:147` 自身が「⚠️ この表と `ci.yml` を突合する機械検査は無い。追随漏れが実際に
 *   1 度起きている」と書いていた。**書いてある「無い」を「在る」にする**のが本検査器である。
 *
 * 見るもの（3 面。いずれも表示テキストであり、`.ai-context/` の凍結記録は走査しない）:
 *   A. `docs/ai-workflow.md` の必須チェック表 —— `| \`<job>\` | \`<workflow>.yml\` |` の各行について、
 *      その workflow ファイルが実在し、`jobs:` 直下に `<job>` が在ること。取り消し線（`~~`）の行は
 *      「必須にしない」と明示された行なので数えない・突合しない。
 *      加えて、本文の「下表の **N 件**」「必須チェック | 下表の N 件」の N が表の行数と一致すること。
 *   B. `scripts/README.md` §検査（CI） のジョブ表 —— `| \`<job>\`…` の各行の `<job>` が
 *      いずれかのワークフローに在ること。
 *   C. 文書全般の「`<job>` ジョブ」という言い回し —— `docs/**\/*.md` / `CLAUDE.md` / `AGENTS.md` /
 *      `AI_SETUP.md` / `scripts/README.md` / `.claude/rules/*.md` を走査し、名指しされたジョブ名が
 *      いずれかのワークフローに在ること。**過去形の言及**（「…はもう存在しない」）は `HISTORICAL_LINE`
 *      に当たる行に限り除外する（無条件に除外すると、生きた指示の中の廃止名を拾えない）。
 *
 * 見ないもの:
 *   - ジョブの `paths:` / `types:` 条件（必須チェックにしてよいかの判定）。それは #705 の回帰試験と
 *     `docs/ai-workflow.md` の注意書きが持つ。
 *   - `check` 名としての表示名（`name:` 属性）。ブランチ保護が引くのはジョブ ID であり、本検査器も ID を見る。
 *
 * fail-closed: ワークフローが 0 件・必須チェック表が 0 行・README のジョブ表が 0 行のいずれも緑にしない。
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-workflow-job-refs.js
 *   node scripts/check-workflow-job-refs.js --self-test
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const WORKFLOW_DIR = '.github/workflows';
const REQUIRED_CHECK_DOC = 'docs/ai-workflow.md';
const SCRIPTS_README = 'scripts/README.md';
/** 面 C の走査対象（表示テキストのみ。凍結記録 `.ai-context/` は含めない）。 */
const MENTION_ROOTS = ['docs', 'CLAUDE.md', 'AGENTS.md', 'AI_SETUP.md', 'scripts/README.md', '.claude/rules'];
/** 過去形の言及として除外する行の形（理由つき）。 */
const HISTORICAL_LINE = [
  { re: /もう存在しない/, why: '廃止済みジョブ名を「もう存在しない」と明示して挙げる行（scripts/README.md の注意書き）' },
];

function toPosix(p) { return String(p).replace(/\\/g, '/'); }

/**
 * ワークフロー YAML の本文から `jobs:` 直下のジョブ ID を抜く（純関数）。
 * YAML パーサは持ち込まない —— 本リポジトリのワークフローは `jobs:` をトップレベルに置き、
 * ジョブ ID は 2 スペース字下げの `<id>:` で始まる（他の検査器も同じ流儀で読む）。
 * `jobs:` より前（`on:` の `push:` / `schedule:` 等）は 2 スペース字下げでも拾わない。
 */
function parseJobIds(yamlText) {
  const ids = [];
  let inJobs = false;
  for (const raw of String(yamlText).split(/\r?\n/)) {
    if (/^jobs:\s*(#.*)?$/.test(raw)) { inJobs = true; continue; }
    if (inJobs && /^\S/.test(raw) && !/^#/.test(raw)) inJobs = false; // 次のトップレベルキー
    if (!inJobs) continue;
    const m = raw.match(/^  ([A-Za-z_][A-Za-z0-9_-]*):\s*(#.*)?$/);
    if (m) ids.push(m[1]);
  }
  return ids;
}

/** `{ 'ci.yml': Set(['lint', ...]), ... }` */
function loadWorkflows(root = REPO_ROOT) {
  const dir = path.join(root, WORKFLOW_DIR);
  const out = {};
  if (!fs.existsSync(dir)) return out;
  for (const f of fs.readdirSync(dir).filter((n) => /\.ya?ml$/.test(n)).sort()) {
    out[f] = new Set(parseJobIds(fs.readFileSync(path.join(dir, f), 'utf8')));
  }
  return out;
}

/**
 * 面 A: 必須チェック表の行を抜く（純関数）。
 * 行の形: `| \`build-and-test\` | \`ci.yml\` | ... |`。取り消し線（`~~`）の行は struck: true。
 * 先頭セルにバッククォートのジョブ名を持ち、2 列目にバッククォートの `.yml` を持つ行だけを表の行とみなす。
 */
function parseRequiredCheckTable(md) {
  const rows = [];
  const lines = String(md).split(/\r?\n/);
  // 表の見出し行（`| 必須にする check 名 | 出所 | …`）から空行までを表とみなす。文書内の他の表
  // （Copilot の有効化手順の表など）を拾わないため、見出しで束ねる。
  const start = lines.findIndex((l) => /^\|\s*必須にする check 名/.test(l));
  if (start < 0) return rows;
  for (let i = start + 1; i < lines.length && lines[i].trim() !== ''; i++) {
    const m = lines[i].match(/^\|\s*(~~)?`([A-Za-z0-9_-]+)`(~~)?\s*\|\s*`([A-Za-z0-9_.-]+\.ya?ml)`\s*\|/);
    if (!m) continue;
    rows.push({ job: m[2], workflow: m[4], struck: Boolean(m[1] || m[3]), line: lines[i] });
  }
  return rows;
}

/** 面 A: 「下表の N 件」「必須チェック | 下表の N 件」の N を全部抜く（純関数）。 */
function parseCountClaims(md) {
  const out = [];
  const re = /下表の\s*\**(\d+)\s*件/g;
  let m;
  while ((m = re.exec(String(md)))) out.push(Number(m[1]));
  return out;
}

/**
 * 面 B: scripts/README.md §検査（CI） のジョブ表からジョブ名を抜く（純関数）。
 * 行の形: `| \`static-checks\` | ...` または `| \`pr-title\`（**...**） | ...`。
 * 節の見出し `## 検査（CI）` から次の `## ` までを対象にする。
 */
function parseReadmeJobTable(md) {
  const lines = String(md).split(/\r?\n/);
  const start = lines.findIndex((l) => /^## 検査（CI）/.test(l));
  if (start < 0) return [];
  // 節の中の「| ジョブ | 実行内容 |」見出しから空行までを表とみなす（節内の他の表は拾わない）。
  const head = lines.findIndex((l, i) => i > start && /^\|\s*ジョブ\s*\|/.test(l));
  if (head < 0) return [];
  const jobs = [];
  for (let i = head + 1; i < lines.length && lines[i].trim() !== ''; i++) {
    if (/^## /.test(lines[i])) break;
    const m = lines[i].match(/^\|\s*`([A-Za-z0-9_-]+)`/);
    if (!m) continue;
    jobs.push({ job: m[1], line: lines[i] });
  }
  return jobs;
}

/** 面 C: 「`<job>` ジョブ」の言い回しを抜く（純関数）。過去形の行は historical: true。 */
function findJobMentions(md) {
  const out = [];
  String(md).split(/\r?\n/).forEach((line, idx) => {
    const re = /`([a-z0-9][a-z0-9_-]*)`\s*ジョブ/g;
    let m;
    while ((m = re.exec(line))) {
      out.push({ job: m[1], lineNo: idx + 1, historical: HISTORICAL_LINE.some((h) => h.re.test(line)) });
    }
  });
  return out;
}

function walkMarkdown(root, rel, acc = []) {
  const abs = path.join(root, rel);
  if (!fs.existsSync(abs)) return acc;
  const st = fs.statSync(abs);
  if (st.isFile()) { if (/\.md$/.test(rel)) acc.push(toPosix(rel)); return acc; }
  for (const name of fs.readdirSync(abs).sort()) {
    if (name === 'node_modules' || name === '.git') continue;
    walkMarkdown(root, path.join(rel, name), acc);
  }
  return acc;
}

/** 全ワークフロー横断でジョブが実在するか。 */
function jobExists(workflows, job) {
  return Object.values(workflows).some((s) => s.has(job));
}

/** 3 面の突合本体（純関数。I/O は呼び出し側）。違反の配列を返す。 */
function findViolations({ workflows, requiredCheckMd, readmeMd, mentionDocs }) {
  const v = [];
  const wfNames = Object.keys(workflows);
  if (wfNames.length === 0) v.push({ where: WORKFLOW_DIR, text: 'ワークフローが 1 件も無い（走査が空振りしている。fail-closed）' });

  // 面 A
  const rows = parseRequiredCheckTable(requiredCheckMd);
  if (rows.length === 0) v.push({ where: REQUIRED_CHECK_DOC, text: '必須チェック表の行を 1 件も読めなかった（fail-closed）' });
  const live = rows.filter((r) => !r.struck);
  for (const r of live) {
    if (!workflows[r.workflow]) { v.push({ where: REQUIRED_CHECK_DOC, text: `必須チェック \`${r.job}\` の出所 \`${r.workflow}\` が ${WORKFLOW_DIR} に無い` }); continue; }
    if (!workflows[r.workflow].has(r.job)) v.push({ where: REQUIRED_CHECK_DOC, text: `必須チェック \`${r.job}\` が \`${r.workflow}\` の jobs: に無い（ジョブ ID を改名した／表が古い）` });
  }
  for (const n of parseCountClaims(requiredCheckMd)) {
    if (n !== live.length) v.push({ where: REQUIRED_CHECK_DOC, text: `「下表の ${n} 件」と書かれているが、表の行数（取り消し線を除く）は ${live.length} 件である` });
  }

  // 面 B
  const readmeJobs = parseReadmeJobTable(readmeMd);
  if (readmeJobs.length === 0) v.push({ where: SCRIPTS_README, text: '§検査（CI） のジョブ表を 1 行も読めなかった（fail-closed）' });
  for (const r of readmeJobs) {
    if (!jobExists(workflows, r.job)) v.push({ where: SCRIPTS_README, text: `ジョブ表の \`${r.job}\` がどのワークフローの jobs: にも無い` });
  }

  // 面 C
  for (const [file, md] of Object.entries(mentionDocs)) {
    for (const m of findJobMentions(md)) {
      if (m.historical) continue;
      if (!jobExists(workflows, m.job)) v.push({ where: `${file}:${m.lineNo}`, text: `「\`${m.job}\` ジョブ」と名指しされているが、そのジョブはどのワークフローにも無い` });
    }
  }
  return v;
}

function loadInputs(root = REPO_ROOT) {
  const read = (rel) => (fs.existsSync(path.join(root, rel)) ? fs.readFileSync(path.join(root, rel), 'utf8') : '');
  const mentionDocs = {};
  for (const r of MENTION_ROOTS) for (const f of walkMarkdown(root, r)) mentionDocs[f] = read(f);
  return { workflows: loadWorkflows(root), requiredCheckMd: read(REQUIRED_CHECK_DOC), readmeMd: read(SCRIPTS_README), mentionDocs };
}

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }
  const inputs = loadInputs();
  const v = findViolations(inputs);
  const wf = Object.keys(inputs.workflows).length;
  const jobs = Object.values(inputs.workflows).reduce((n, s) => n + s.size, 0);
  console.log(`[check-workflow-job-refs] ワークフロー ${wf} 件 / ジョブ ${jobs} 件 / 文書 ${Object.keys(inputs.mentionDocs).length} 件を突合した。`);
  if (v.length === 0) { console.log('[check-workflow-job-refs] OK: 文書が名指しするジョブはすべて実在し、必須チェック表の件数も一致している。'); return; }
  for (const x of v) console.error(`[check-workflow-job-refs] ${x.where}: ${x.text}`);
  console.error(`[check-workflow-job-refs] ${v.length} 件の乖離。ジョブを再編・改名したら文書側（必須チェック表・README のジョブ表・「〜ジョブ」の言及）を追随させること。`);
  process.exit(1);
}

function selfTest() {
  const assert = require('assert');
  let n = 0;
  const ok = (name, fn) => { fn(); n++; console.log(`  ok  ${name}`); };
  const wf = 'name: CI\non:\n  push:\n    branches: [develop]\n  pull_request:\n    types: [opened]\njobs:\n  lint:\n    runs-on: x\n  build-and-test:\n    needs: [lint]\n  # comment\n  static-checks:\n    steps: []\n';
  ok('parseJobIds: jobs: 直下の ID だけを拾う（on: 配下の push: は拾わない）', () => {
    assert.deepStrictEqual(parseJobIds(wf), ['lint', 'build-and-test', 'static-checks']);
  });
  ok('parseJobIds: jobs: の後に別のトップレベルキーが来たら止まる', () => {
    assert.deepStrictEqual(parseJobIds('jobs:\n  a:\n    x: 1\nenv:\n  b: 2\n'), ['a']);
  });
  ok('parseRequiredCheckTable: 見出し行から空行までを表とみなし、生きた行と取り消し線の行を区別する', () => {
    const rows = parseRequiredCheckTable('| `copilot` | `x.example.yml` | 別の表 |\n\n| 必須にする check 名 | 出所 |\n| --- | --- |\n| `lint` | `ci.yml` | x |\n| ~~`CodeQL`~~ | `codeql.yml` | y |\n| `pr-title` | `pr-title.yml` | z |');
    assert.deepStrictEqual(rows.map((r) => [r.job, r.workflow, r.struck]), [['lint', 'ci.yml', false], ['CodeQL', 'codeql.yml', true], ['pr-title', 'pr-title.yml', false]]);
    assert.deepStrictEqual(parseRequiredCheckTable('| `lint` | `ci.yml` |'), [], '見出しの無い表は必須チェック表とみなさない');
  });
  ok('parseCountClaims: 「下表の 7 件」「下表の **8** 件」を数として抜く', () => {
    assert.deepStrictEqual(parseCountClaims('下表の 7 件を必須に。| 必須チェック | 下表の **8** 件 |'), [7, 8]);
  });
  ok('parseReadmeJobTable: §検査（CI） のジョブ表だけを読み、括弧つきの行も読み、節内の他の表は拾わない', () => {
    const md = '## 検査（CI）\n| ジョブ | 実行内容 |\n| --- | --- |\n| `scripts-tests` | a |\n| `pr-title`（**`ci.yml` ではなく**） | b |\n\n| 状態 | 挙動 |\n| `expected` | c |\n## 次\n| `other` | c |';
    assert.deepStrictEqual(parseReadmeJobTable(md).map((r) => r.job), ['scripts-tests', 'pr-title']);
  });
  ok('findJobMentions: 「`x` ジョブ」を拾い、過去形の行は historical', () => {
    const ms = findJobMentions('CI の `doc-links` ジョブで検査する。\n（`doc-links` ジョブは**もう存在しない**）\n`lint` ジョブ');
    assert.deepStrictEqual(ms.map((m) => [m.job, m.historical]), [['doc-links', false], ['doc-links', true], ['lint', false]]);
  });
  const workflows = { 'ci.yml': new Set(['lint', 'build-and-test', 'static-checks']), 'pr-title.yml': new Set(['pr-title']) };
  const goodDoc = '下表の 3 件を必須に。\n| 必須にする check 名 | 出所 |\n| `lint` | `ci.yml` |\n| `build-and-test` | `ci.yml` |\n| ~~`CodeQL`~~ | `codeql.yml` |\n| `pr-title` | `pr-title.yml` |';
  const goodReadme = '## 検査（CI）\n| ジョブ | x |\n| --- | --- |\n| `static-checks` | a |\n| `pr-title`（別） | b |\n';
  ok('findViolations: 整合していれば 0 件', () => {
    assert.deepStrictEqual(findViolations({ workflows, requiredCheckMd: goodDoc, readmeMd: goodReadme, mentionDocs: { 'docs/a.md': 'CI の `lint` ジョブ' } }), []);
  });
  ok('findViolations: 件数の主張が表と違えば落とす（#1348 B-2）', () => {
    const v = findViolations({ workflows, requiredCheckMd: goodDoc.replace('下表の 3 件', '下表の 7 件'), readmeMd: goodReadme, mentionDocs: {} });
    assert.strictEqual(v.length, 1); assert.match(v[0].text, /7 件.*3 件/);
  });
  ok('findViolations: 必須チェック表のジョブがワークフローに無ければ落とす', () => {
    const v = findViolations({ workflows, requiredCheckMd: goodDoc.replace('`lint`', '`lint-old`'), readmeMd: goodReadme, mentionDocs: {} });
    assert.strictEqual(v.length, 1); assert.match(v[0].text, /lint-old/);
  });
  ok('findViolations: 出所のワークフローが無ければ落とす', () => {
    const v = findViolations({ workflows, requiredCheckMd: goodDoc.replace('`pr-title.yml`', '`title.yml`'), readmeMd: goodReadme, mentionDocs: {} });
    assert.strictEqual(v.length, 1); assert.match(v[0].text, /title\.yml/);
  });
  ok('findViolations: 取り消し線の行は突合しない（CodeQL は codeql.yml が無くても落とさない）', () => {
    assert.deepStrictEqual(findViolations({ workflows, requiredCheckMd: goodDoc, readmeMd: goodReadme, mentionDocs: {} }), []);
  });
  ok('findViolations: README のジョブ表の廃止名を落とす', () => {
    const v = findViolations({ workflows, requiredCheckMd: goodDoc, readmeMd: goodReadme + '| `doc-links` | c |\n', mentionDocs: {} });
    assert.strictEqual(v.length, 1); assert.match(v[0].text, /doc-links/);
  });
  ok('findViolations: 文書の「`x` ジョブ」の廃止名を落とす（#1348 B-3）', () => {
    const v = findViolations({ workflows, requiredCheckMd: goodDoc, readmeMd: goodReadme, mentionDocs: { 'docs/DEFINITION_OF_DONE.md': 'CI の `doc-links` ジョブで検査する' } });
    assert.strictEqual(v.length, 1); assert.match(v[0].where, /DEFINITION_OF_DONE\.md:1/);
  });
  ok('findViolations: 過去形の言及（もう存在しない）は落とさない —— 陰性対照', () => {
    assert.deepStrictEqual(findViolations({ workflows, requiredCheckMd: goodDoc, readmeMd: goodReadme, mentionDocs: { 'scripts/README.md': '`doc-links` ジョブは**もう存在しない**' } }), []);
  });
  ok('findViolations: 陽性対照 —— 同じ行から過去形の語を外すと落ちる', () => {
    const v = findViolations({ workflows, requiredCheckMd: goodDoc, readmeMd: goodReadme, mentionDocs: { 'scripts/README.md': '`doc-links` ジョブは在る' } });
    assert.strictEqual(v.length, 1);
  });
  ok('findViolations: fail-closed —— ワークフロー 0 件・表 0 行は緑にしない', () => {
    const v = findViolations({ workflows: {}, requiredCheckMd: '', readmeMd: '', mentionDocs: {} });
    assert.ok(v.length >= 3, JSON.stringify(v));
  });
  ok('実データ: 本リポジトリのワークフローからジョブを読める', () => {
    const w = loadWorkflows();
    assert.ok(Object.keys(w).length > 0); assert.ok(w['ci.yml'] && w['ci.yml'].has('static-checks'));
  });
  console.log(`[check-workflow-job-refs] 自己試験 ${n} 件 OK。`);
}

if (require.main === module) main();

module.exports = { parseJobIds, parseRequiredCheckTable, parseCountClaims, parseReadmeJobTable, findJobMentions, findViolations, loadWorkflows, loadInputs, HISTORICAL_LINE };
