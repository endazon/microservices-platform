#!/usr/bin/env node
'use strict';
/*
 * check-e2e-skips.js — NFR / ADR-0090 / #2026 / IADR-0625
 *
 * live 層の Playwright（`e2e-live.yml`）の JSON レポートを読み、**全 skip を赤にする**。外部依存ゼロ（Node 標準モジュールのみ）。
 * 基準書は docs/tests/E2E_STANDARDS.md §6（門は依存ごとに訊き、全部が skip なら緑にしない）・§11（成果物に skip 一覧）。
 *
 * 判定（いずれかで exit 1）:
 *   1. レポートを読めない・形が違う・test が 0 件（0 件の集計を緑にしない）。
 *   2. **test を持つ project のうち、全部が skip の project がある**（前提〔seed・門〕が満たされず 1 件も確かめていない層）。
 *   3. 全 project を通して、skip でない test が 0 件。
 * 警告だけ（exit 0）:
 *   - 選んだ層の project に test が 0 件（spec がまだ無い層。setup だけが流れた）。名前を出す。
 * 出力: project ごとの件数と、skip した test と理由（`test.skip(cond, '理由')` の注記）を表で出す（step summary）。
 *
 * 使い方:
 *   node scripts/check-e2e-skips.js --report <playwright.json> [--layers live-ro,live-rw]
 *   node scripts/check-e2e-skips.js --self-test
 */
const fs = require('fs');
const { emit } = require('./lib/ci-annotate.js');

const STATUSES = ['expected', 'unexpected', 'flaky', 'skipped'];

function parseArgs(argv) {
  const a = { selfTest: false, report: null, layers: [] };
  for (let i = 0; i < argv.length; i++) {
    const t = argv[i];
    if (t === '--self-test') a.selfTest = true;
    else if (t === '--report') a.report = argv[++i];
    else if (t === '--layers') a.layers = String(argv[++i] || '').split(',').map((s) => s.trim()).filter(Boolean);
    else throw new Error(`未知の引数: ${t}`);
  }
  return a;
}

/** JSON レポートから project ごとの件数と skip の一覧を作る（純関数）。 */
function tally(report) {
  if (!report || typeof report !== 'object' || !Array.isArray(report.suites)) {
    throw new Error('Playwright の JSON レポートの形ではない（suites が無い）');
  }
  const projects = {};
  const skips = [];
  let total = 0;
  const walk = (suite, titles) => {
    const next = suite.__top ? titles : [...titles, suite.title];
    for (const spec of suite.specs || []) {
      for (const t of spec.tests || []) {
        if (!STATUSES.includes(t.status)) throw new Error(`未知の test の status: ${t.status}`);
        total += 1;
        const name = t.projectName || '';
        projects[name] = projects[name] || { expected: 0, unexpected: 0, flaky: 0, skipped: 0 };
        projects[name][t.status] += 1;
        if (t.status === 'skipped') {
          const reason = (t.annotations || []).filter((x) => x.type === 'skip' || x.type === 'fixme').map((x) => x.description || '（理由なし）').join(' / ');
          skips.push({ project: name, id: [name, spec.file, ...next, spec.title].join(' › '), reason: reason || '（理由なし）' });
        }
      }
    }
    for (const child of suite.suites || []) walk(child, next);
  };
  for (const top of report.suites) walk({ ...top, __top: true }, []);
  if (total === 0) throw new Error('test が 0 件である（0 件の集計を緑にしない）');
  return { total, projects, skips };
}

/** 判定（純関数）。errors が空なら緑。 */
function judge(t, layers = []) {
  const errors = [];
  const warnings = [];
  for (const [name, c] of Object.entries(t.projects)) {
    const n = c.expected + c.unexpected + c.flaky + c.skipped;
    if (n > 0 && c.skipped === n) errors.push(`project ${name} の test ${n} 件がすべて skip（前提が満たされず何も確かめていない）`);
  }
  const ran = Object.values(t.projects).reduce((s, c) => s + c.expected + c.unexpected + c.flaky, 0);
  if (ran === 0) errors.push('skip でない test が 0 件である');
  for (const layer of layers) {
    if (!t.projects[layer]) warnings.push(`選んだ層の project ${layer} に test が 0 件（spec がまだ無い。setup だけが流れた）`);
  }
  return { errors, warnings };
}

const cell = (s) => String(s).replace(/[\\|`]/g, (c) => `\\${c}`).replace(/\s*\r?\n\s*/g, ' ');

function renderSummary(t, verdict) {
  const lines = ['### live E2E の skip', '', '| project | expected | unexpected | flaky | skipped |', '| --- | --- | --- | --- | --- |'];
  for (const [name, c] of Object.entries(t.projects)) lines.push(`| ${cell(name)} | ${c.expected} | ${c.unexpected} | ${c.flaky} | ${c.skipped} |`);
  if (t.skips.length) {
    lines.push('', '| skip した test | 理由 |', '| --- | --- |');
    for (const s of t.skips) lines.push(`| ${cell(s.id)} | ${cell(s.reason)} |`);
  }
  for (const w of verdict.warnings) lines.push('', `- ⚠️ ${cell(w)}`);
  for (const e of verdict.errors) lines.push('', `- 🔴 ${cell(e)}`);
  return `${lines.join('\n')}\n`;
}

// ---- 自己試験 -------------------------------------------------------------------------------------------

function fixture(rows) {
  return {
    suites: [{
      title: 'a.ro.spec.ts',
      file: 'a.ro.spec.ts',
      specs: rows.map(([project, status, reason], i) => ({
        title: `t${i}`,
        file: 'a.ro.spec.ts',
        line: i + 1,
        tests: [{ projectName: project, status, annotations: reason ? [{ type: 'skip', description: reason }] : [] }],
      })),
    }],
  };
}

function selfTest() {
  const cases = [
    {
      name: '一部が skip（理由つき）でも、走った test があれば緑。skip の一覧に理由が出る',
      run: () => { const t = tally(fixture([['live-setup', 'expected'], ['live-ro', 'expected'], ['live-ro', 'skipped', 'abac-seed 未投入']])); return { t, v: judge(t, ['live-ro']) }; },
      expect: ({ t, v }) => v.errors.length === 0 && t.skips.length === 1 && t.skips[0].reason === 'abac-seed 未投入',
    },
    {
      name: '🔴 ある project の test がすべて skip なら赤（全 skip を緑にしない）',
      run: () => judge(tally(fixture([['live-setup', 'expected'], ['live-ro', 'skipped', 'x'], ['live-ro', 'skipped', 'y']])), ['live-ro']),
      expect: (v) => v.errors.length === 1 && /live-ro/.test(v.errors[0]),
    },
    {
      name: '🔴 skip でない test が 0 件なら赤',
      run: () => judge(tally(fixture([['live-setup', 'skipped', 'x']]))),
      expect: (v) => v.errors.some((e) => /0 件/.test(e)),
    },
    {
      name: '🔴 test が 0 件のレポートは例外（0 件の集計を緑にしない）',
      run: () => { try { tally({ suites: [] }); return 'no-throw'; } catch (e) { return e.message; } },
      expect: (m) => /0 件/.test(m),
    },
    {
      name: '🔴 レポートの形でなければ例外',
      run: () => { try { tally({}); return 'no-throw'; } catch (e) { return e.message; } },
      expect: (m) => /suites/.test(m),
    },
    {
      name: '選んだ層に test が 0 件なら警告だけ（setup だけが流れた。spec がまだ無い）',
      run: () => judge(tally(fixture([['live-setup', 'expected']])), ['live-ro']),
      expect: (v) => v.errors.length === 0 && v.warnings.length === 1 && /live-ro/.test(v.warnings[0]),
    },
    {
      name: '表のセルは \\ と | と ` と改行で壊れない',
      run: () => renderSummary(tally(fixture([['live-ro', 'expected'], ['live-ro', 'skipped', 'a|b\\c\n`d`']])), { errors: [], warnings: [] }),
      expect: (md) => md.split('\n').filter((l) => l.includes('a\\|b')).length === 1 && md.includes('\\`d\\`'),
    },
  ];
  let failed = 0;
  for (const c of cases) {
    let got;
    try { got = c.run(); } catch (e) { failed++; process.stderr.write(`  NG  ${c.name}\n      例外: ${e.message}\n`); continue; }
    if (c.expect(got)) process.stdout.write(`  ok  ${c.name}\n`);
    else { failed++; process.stderr.write(`  NG  ${c.name}\n      got=${JSON.stringify(got)}\n`); }
  }
  if (failed) { process.stderr.write(`\n✗ 検証器の自己試験が ${failed} 件失敗した\n`); return 1; }
  process.stdout.write(`✓ 検証器の自己試験 ${cases.length} 件すべて合格\n`);
  return 0;
}

function main(argv) {
  const args = parseArgs(argv.slice(2));
  if (args.selfTest) process.exit(selfTest());
  if (!args.report) {
    process.stderr.write('usage: check-e2e-skips.js --report <playwright.json> [--layers live-ro,…] | --self-test\n');
    process.exit(2);
  }
  const t = tally(JSON.parse(fs.readFileSync(args.report, 'utf8')));
  const v = judge(t, args.layers);
  if (process.env.GITHUB_STEP_SUMMARY) fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, renderSummary(t, v));
  for (const w of v.warnings) emit('warning', w, { stream: process.stderr, prefix: '  warning  ' });
  for (const s of t.skips) process.stdout.write(`  skip  ${s.id} — ${s.reason}\n`);
  if (v.errors.length) {
    for (const e of v.errors) emit('error', e, { stream: process.stderr, prefix: '  error  ' });
    process.exit(1);
  }
  process.stdout.write(`✓ live E2E: ${t.total} 件（skip ${t.skips.length} 件）。全 skip の project は無い\n`);
}

if (require.main === module) {
  try { main(process.argv); } catch (e) {
    emit('error', `check-e2e-skips: ${e.message || e}`, { stream: process.stderr, prefix: '  error  ' });
    process.exit(1);
  }
}

module.exports = { tally, judge, renderSummary, selfTest };
