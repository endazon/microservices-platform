#!/usr/bin/env node
'use strict';
/*
 * check-integration-executed.js
 * integration.yml（回収実行・Docker あり）で **`Category=Integration` の試験が 1 件も実走しなかった実行を赤にする**。
 * 「全 skip で緑」を実走した実行と区別する門である。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 背景（NFR / #1788 / IADR-0507。正本は計画 ADR-0090 決定 3・planning#575）:
 *   統合試験の門（IADR-0414 の RequiredServices）は、要る依存を得られなければ理由つきで skip する（ADR-0090 決定 1）。
 *   🔴 その帰結として「依存が 1 つも揃わなくても CI が緑」が作れる。カバレッジ床は単体試験で満たせるため
 *   この形を止めない（ADR-0090 決定 3 が名指しで退けた）。本検査がその守りを置く。
 *
 * 🔴 入力は 2 つ（どちらも integration.yml の試験 step がユニットごとに残す）:
 *   (1) 一覧: `dotnet test <slnx> --no-build --list-tests --filter "Category=Integration"` の出力（<lists>/<unit>.list）。
 *       TRX は xUnit の Trait を書かない（2026-10-08 実測: UnitTest 要素に TestCategory も Properties も無い）ため、
 *       「どの結果が統合試験か」は VSTest の発見（同じフィルタ）に訊く。🔴 実行には --filter を付けない（IADR-0232 改定 3）。
 *   (2) 結果: `--logger trx` が各試験プロジェクトの TestResults/ に残す TRX（<root>/<unit>/backend 配下を走査）。
 *
 * 🔴 判定（ユニットごと。対象外ユニット＝.gitmodules の submodule は lib/excluded-units.js で除く）:
 *   G1 **一覧が読めなければ赤**（発見の見出しが無い＝ビルド失敗・コマンド失敗。「宣言 0 件」と読まない）。
 *   G2 **統合試験の宣言が 1 件以上あるユニットで、実走（合格＋失敗）が下限（既定 1）を割ったら赤。**
 *      これが ADR-0090 決定 3 の要求「1 件も実走しなかった実行を区別する」そのものである。
 *   G3 **壊れた TRX は読み飛ばさず赤**（不明を 0 と読まない）。
 *   G4 **検査対象のどのユニットにも統合試験の宣言が無ければ赤**（Trait・フィルタ・一覧の配線が壊れた形を緑にしない）。
 *   G5 🔴 **「依存を得られない」で skip された統合試験が上限（既定 0）を超えたら赤。** G2 だけでは足りない ——
 *      統合試験の中には依存を要らない試験がある（2026-10-08 実測: Docker を隠すと Knowledge.IntegrationTests の
 *      統合試験 59 件のうち 58 件が skip、1 件〔WolverineBrokerEdgeTests の構成の試験〕は依存なしで合格）。
 *      依存が 1 つも揃わない実行でも「実走 1 件」になり、G2 は空振りする。Docker のある CI で依存不足の skip が
 *      出たら、それは依存が揃っていない実行である。理由の判別は門（RequiredServices / BrokerRequired）の
 *      文言の目印（DEPENDENCY_SKIP_MARKERS）で行い、目印が門の実装に在ることは scripts.repo.test.js が固定する。
 *   🔴 skip 件数そのものの上限は置かない（AST#1200 の「上限 0」と異なる）。MSP には依存が揃った CI でも設計どおり
 *      skip する統合試験がある（例: 外部ブローカを与えたときだけ走る WolverineBrokerEdgeTests の 1 件）。件数の下限
 *      （「59 件以上」等）も置かない —— 試験の増減で腐る導出値である（規則 10）。理由は IADR-0507。
 *
 * 🔴 skip は `<Counters>` から数えない。xUnit v3 ＋ VSTest は skip を `outcome="NotExecuted"` の結果で書くが
 *   `notExecuted="0"` のまま残す（AST#1200 の実測）。結果（UnitTestResult）を 1 件ずつ数える。
 *
 * 使い方:
 *   node scripts/check-integration-executed.js --lists <一覧のディレクトリ> [--root <src>] [--min-executed N] [--max-dependency-skips N]
 *   node scripts/check-integration-executed.js --self-test
 */

const fs = require('fs');
const path = require('path');
const os = require('os');
const { excludedUnits } = require('./lib/excluded-units.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const DEFAULTS = Object.freeze({ root: path.join(REPO_ROOT, 'src'), minExecuted: 1, maxDependencySkips: 0 });
// 「依存を得られない」skip の目印（門の skip 理由の先頭の文言）。🔴 門の文言を変えたらここも変える ——
// scripts.repo.test.js が「各目印が門の実装に在ること」を検査するので、片方だけ変えると落ちる。
const DEPENDENCY_SKIP_MARKERS = Object.freeze([
  'この試験が要るサービスを得られない', // Knowledge.IntegrationTests/Fixtures/RequiredServices.cs（IADR-0414）
  'No broker available', // Knowledge.IntegrationTests/Fixtures/BrokerRequired.cs
]);
const LIST_HEADER = 'The following Tests are available:';
// 一覧に出す skip の件数の上限（ログを溢れさせない。件数そのものは常に全数を出す）。
const LIST_LIMIT = 30;
const SKIP_DIRS = new Set(['node_modules', '.git', 'obj']);

function decodeXmlEntities(s) {
  return String(s)
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&#x([0-9a-f]+);/gi, (_, h) => String.fromCodePoint(parseInt(h, 16)))
    .replace(/&#(\d+);/g, (_, d) => String.fromCodePoint(Number(d)))
    .replace(/&amp;/g, '&');
}

/** 試験名から引数部（Theory の行）を落とした「クラス.メソッド」を返す。一覧と TRX の突合の鍵。 */
function baseName(name) {
  const s = String(name).trim();
  const i = s.indexOf('(');
  return (i < 0 ? s : s.slice(0, i)).trim();
}

/**
 * `dotnet test --list-tests` の出力を解析する。戻り値: { ok, names:Set<baseName> }。
 * 🔴 見出しが 1 つも無ければ ok=false（ビルド失敗・コマンド失敗を「宣言 0 件」と読まない）。
 * 🔴 名前は「最初の見出しより後の、4 空白で始まる行」すべてとする（見出しの直後に続く行だけ、とはしない）。
 *   `dotnet test <slnx>` は試験プロジェクトごとの発見を並列に走らせ、各プロジェクトの出力が同じ標準出力へ
 *   割り込む（2026-10-08 実測: knowledge の slnx で「見出し → 別プロジェクトの Test run for → 見出し」の並び）。
 *   ブロックの連続を前提にすると、割り込まれた側の名前を落として宣言を少なく数える。
 */
function parseList(text) {
  const names = new Set();
  let headers = 0;
  for (const raw of String(text).replace(/\r/g, '').split('\n')) {
    if (raw.trim() === LIST_HEADER) {
      headers++;
      continue;
    }
    if (headers > 0 && /^ {4}\S/.test(raw)) names.add(baseName(raw));
  }
  return { ok: headers > 0, names };
}

/** TRX 1 つから結果を取り出す。🔴 TestRun 要素が無ければ例外（黙って 0 件を返すと G3 が空洞化する）。 */
function parseTrx(text) {
  if (typeof text !== 'string' || !/<TestRun\b/.test(text)) {
    throw new Error('TestRun 要素が無い（TRX として読めない）');
  }
  const results = [];
  for (const raw of text.split(/<UnitTestResult\b/).slice(1)) {
    const headerEnd = raw.indexOf('>');
    if (headerEnd < 0) continue;
    const header = raw.slice(0, headerEnd);
    const attr = (n) => {
      const m = new RegExp(`\\b${n}="([^"]*)"`).exec(header);
      return m ? decodeXmlEntities(m[1]) : null;
    };
    const selfClosing = header.endsWith('/');
    const end = raw.indexOf('</UnitTestResult>');
    const body = selfClosing ? '' : end < 0 ? raw.slice(headerEnd + 1) : raw.slice(headerEnd + 1, end);
    const msg = /<Message>([\s\S]*?)<\/Message>/.exec(body);
    results.push({
      testName: attr('testName') || '(名前不明)',
      outcome: attr('outcome') || '(不明)',
      message: msg ? decodeXmlEntities(msg[1]).trim() : null,
    });
  }
  return results;
}

/** dir 配下の TestResults/ にある *.trx を集める（存在しなければ空）。 */
function listTrxFiles(dir) {
  const out = [];
  const walk = (d) => {
    let entries;
    try {
      entries = fs.readdirSync(d, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      const p = path.join(d, e.name);
      if (e.isDirectory()) {
        if (!SKIP_DIRS.has(e.name)) walk(p);
      } else if (/\.trx$/i.test(e.name) && path.basename(d) === 'TestResults') {
        out.push(p);
      }
    }
  };
  walk(dir);
  return out.sort();
}

/** skip の理由が「依存を得られない」（門の目印を含む）か。 */
function isDependencySkip(message) {
  return typeof message === 'string' && DEPENDENCY_SKIP_MARKERS.some((m) => message.includes(m));
}

/** 1 ユニットを集計する。 */
function collectUnit(unit, listText, root) {
  const list = parseList(listText);
  const files = listTrxFiles(path.join(root, unit, 'backend'));
  const malformed = [];
  const matched = [];
  for (const f of files) {
    let results;
    try {
      results = parseTrx(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
    } catch (e) {
      malformed.push({ file: f, reason: e.message });
      continue;
    }
    for (const r of results) if (list.names.has(baseName(r.testName))) matched.push(r);
  }
  const seen = new Set(matched.map((r) => baseName(r.testName)));
  const skipped = matched.filter((r) => r.outcome === 'NotExecuted');
  return {
    unit,
    listOk: list.ok,
    declared: list.names.size,
    trxFiles: files.length,
    malformed,
    passed: matched.filter((r) => r.outcome === 'Passed').length,
    failed: matched.filter((r) => r.outcome === 'Failed').length,
    skipped,
    dependencySkipped: skipped.filter((r) => isDependencySkip(r.message)).length,
    other: matched.filter((r) => !['Passed', 'Failed', 'NotExecuted'].includes(r.outcome)).length,
    noResult: [...list.names].filter((n) => !seen.has(n)).length,
  };
}

/** 判定する。戻り値: 違反の文（空なら合格）。 */
function judge(units, { minExecuted, maxDependencySkips }) {
  const v = [];
  for (const u of units) {
    if (!u.listOk) {
      v.push(
        `${u.unit}: 統合試験の一覧（--list-tests --filter "Category=Integration"）を読めなかった。` +
          'ビルドかコマンドが失敗している。「宣言 0 件」と読んで緑にはしない。'
      );
      continue;
    }
    if (u.malformed.length > 0) {
      v.push(
        `${u.unit}: 読めなかった TRX が ${u.malformed.length} 件ある（不明を 0 件と読まない）: ` +
          u.malformed.map((m) => `${m.file}（${m.reason}）`).join(' / ')
      );
    }
    const executed = u.passed + u.failed;
    if (u.declared > 0 && executed < minExecuted) {
      v.push(
        `${u.unit}: 統合試験の実走 ${executed} 件（下限 ${minExecuted} 件）。宣言 ${u.declared} 件・skip ${u.skipped.length} 件・` +
          `結果なし ${u.noResult} 件。🔴 実走 0 件の実行を「全 skip で緑」として通さない（ADR-0090 決定 3・IADR-0507）。` +
          'skip の理由（下の一覧）に従って依存（PostgreSQL・ブローカ・Qdrant 等）を与えること。'
      );
    }
    if (u.dependencySkipped > maxDependencySkips) {
      v.push(
        `${u.unit}: 「依存を得られない」で skip された統合試験が ${u.dependencySkipped} 件（上限 ${maxDependencySkips} 件。実走 ${executed} 件）。` +
          '🔴 Docker のある回収実行で依存不足の skip が出たのは、依存が揃っていない実行である。' +
          '依存を要らない試験の実走で「全 skip ではない」に見せない（ADR-0090 決定 3・IADR-0507）。'
      );
    }
  }
  const checked = units.filter((u) => u.listOk);
  if (checked.length > 0 && checked.every((u) => u.declared === 0)) {
    v.push(
      `検査対象のどのユニット（${checked.map((u) => u.unit).join(', ')}）にも統合試験（Category=Integration）の宣言が無い。` +
        'Trait・フィルタ・一覧の配線が壊れた形を緑にはしない。'
    );
  }
  if (units.length === 0) v.push('検査対象のユニットが 1 つも無い（一覧のディレクトリが空か、全ユニットが対象外）。');
  return v;
}

function formatReport(units, skippedUnits, opts, violations) {
  const L = [];
  L.push(
    `実走の下限: ユニットごとに ${opts.minExecuted} 件（統合試験の宣言が 1 件以上あるユニットだけ）／` +
      `依存不足の skip の上限: ${opts.maxDependencySkips} 件`
  );
  L.push('');
  L.push('| ユニット | 宣言 | 実走 | 合格 | 失敗 | skip | うち依存不足 | その他 | 結果なし | TRX |');
  L.push('| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |');
  for (const u of units) {
    if (!u.listOk) {
      L.push(`| ${u.unit} | 一覧なし | — | — | — | — | — | — | — | ${u.trxFiles} |`);
      continue;
    }
    L.push(
      `| ${u.unit} | ${u.declared} | ${u.passed + u.failed} | ${u.passed} | ${u.failed} | ${u.skipped.length} | ${u.dependencySkipped} | ${u.other} | ${u.noResult} | ${u.trxFiles} |`
    );
  }
  for (const s of skippedUnits) L.push(`| ${s} | 対象外（submodule） | — | — | — | — | — | — | — | — |`);
  for (const u of units) {
    if (u.skipped.length === 0) continue;
    L.push('');
    L.push(`### ${u.unit}: skip された統合試験（${u.skipped.length} 件${u.skipped.length > LIST_LIMIT ? `。先頭 ${LIST_LIMIT} 件` : ''}）`);
    for (const s of u.skipped.slice(0, LIST_LIMIT)) {
      L.push(`- ${s.testName}`);
      if (s.message) L.push(`  - 理由: ${s.message.split('\n')[0]}`);
    }
  }
  L.push('');
  L.push(violations.length === 0 ? '判定: 合格（統合試験は実走した。全 skip で緑ではない）' : `判定: 🔴 不合格（${violations.length} 件）`);
  return L.join('\n');
}

/** 一覧のディレクトリを読み、判定して報告する。戻り値は終了コード。 */
function run(listsDir, opts = {}, { quiet = false, excluded = null } = {}) {
  const o = { ...DEFAULTS, ...opts };
  const ex = excluded || excludedUnits();
  let listFiles = [];
  try {
    listFiles = fs.readdirSync(listsDir).filter((f) => f.endsWith('.list')).sort();
  } catch {
    listFiles = [];
  }
  const units = [];
  const skippedUnits = [];
  for (const f of listFiles) {
    const unit = f.slice(0, -'.list'.length);
    if (ex.has(unit)) {
      skippedUnits.push(unit);
      continue;
    }
    units.push(collectUnit(unit, fs.readFileSync(path.join(listsDir, f), 'utf8'), o.root));
  }
  const violations = judge(units, o);
  if (!quiet) {
    const report = formatReport(units, skippedUnits, o, violations);
    console.log('===== 統合試験の実走検査（全 skip で緑にしない。IADR-0507） =====');
    console.log(report);
    for (const v of violations) console.log(`::error title=統合試験の実走検査::${v}`);
    const summaryPath = process.env.GITHUB_STEP_SUMMARY;
    if (summaryPath) {
      try {
        fs.appendFileSync(summaryPath, `\n## 統合試験の実走検査（全 skip で緑にしない）\n\n${report}\n`);
      } catch (e) {
        console.log(`[check-integration-executed] 実行サマリへ書けなかった: ${e.message}`);
      }
    }
  }
  return violations.length > 0 ? 1 : 0;
}

// ---------------------------------------------------------------- 自己試験

const NS = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010';

function trx(results) {
  const res = results
    .map((r, i) =>
      r.message
        ? `<UnitTestResult testId="t${i}" testName="${r.name}" outcome="${r.outcome}"><Output><ErrorInfo><Message>${r.message}</Message></ErrorInfo></Output></UnitTestResult>`
        : `<UnitTestResult testId="t${i}" testName="${r.name}" outcome="${r.outcome}" />`
    )
    .join('\n');
  // 実測どおり、skip があっても Counters の notExecuted は 0 のままにしておく（Counters を信じない形を固定する）。
  const executed = results.filter((r) => r.outcome !== 'NotExecuted').length;
  return (
    `﻿<?xml version="1.0" encoding="utf-8"?>\n<TestRun id="x" xmlns="${NS}">\n` +
    `<ResultSummary outcome="Completed"><Counters total="${results.length}" executed="${executed}" passed="${executed}" failed="0" notExecuted="0" /></ResultSummary>\n` +
    `<Results>\n${res}\n</Results>\n</TestRun>\n`
  );
}

function listOutput(names, { header = true } = {}) {
  const lines = ['Test run for /w/src/knowledge/backend/Tests/K.IntegrationTests/bin/Release/net10.0/K.IntegrationTests.dll (.NETCoreApp,Version=v10.0)'];
  if (header) lines.push(LIST_HEADER);
  if (header && names.length === 0) lines.push('No test matches the given testcase filter `Category=Integration` in /w/x.dll');
  for (const n of names) lines.push(`    ${n}`);
  return lines.join('\n') + '\n';
}

function selfTest() {
  let passed = 0;
  const failures = [];
  const t = (name, fn) => {
    try {
      fn();
      passed++;
    } catch (e) {
      failures.push(`${name}: ${e.message}`);
    }
  };
  const eq = (a, b, what) => {
    if (a !== b) throw new Error(`${what}: 期待 ${JSON.stringify(b)} / 実際 ${JSON.stringify(a)}`);
  };
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'int-exec-'));
  let seq = 0;
  // 1 ケース = 1 つの src 相当（root）と一覧のディレクトリ。units: { unit: { list, trx: [results...] | {name: text} } }
  const fixture = (units) => {
    const base = path.join(tmp, String(seq++));
    const root = path.join(base, 'src');
    const lists = path.join(base, 'lists');
    fs.mkdirSync(lists, { recursive: true });
    for (const [unit, spec] of Object.entries(units)) {
      if (spec.list !== undefined) fs.writeFileSync(path.join(lists, `${unit}.list`), spec.list);
      for (const [rel, body] of Object.entries(spec.files || {})) {
        const p = path.join(root, unit, 'backend', rel);
        fs.mkdirSync(path.dirname(p), { recursive: true });
        fs.writeFileSync(p, body);
      }
    }
    return { root, lists };
  };
  const NONE = new Set();
  const go = (fx, opts = {}, excluded = NONE) => run(fx.lists, { root: fx.root, ...opts }, { quiet: true, excluded });
  const P = (name) => ({ name, outcome: 'Passed' });
  const F = (name) => ({ name, outcome: 'Failed' });
  const DEP = `${DEPENDENCY_SKIP_MARKERS[0]}: PostgreSQL。…PLATFORM_TEST_POSTGRES=…`;
  const S = (name, message = DEP) => ({ name, outcome: 'NotExecuted', message });
  const C = (name) => S(name, 'PLATFORM_TEST_RABBITMQ 未設定のため判定対象外'); // 依存不足ではない条件 skip
  const TR = 'Tests/K.IntegrationTests/TestResults/a.trx';
  const UT = 'Tests/K.Tests/TestResults/b.trx';

  t('統合試験が実走し依存不足の skip が無ければ合格（条件 skip が混じっても skip 件数の上限は置かない）', () => {
    const fx = fixture({ knowledge: { list: listOutput(['K.I.A.one', 'K.I.A.two']), files: { [TR]: trx([P('K.I.A.one'), C('K.I.A.two')]) } } });
    eq(go(fx), 0, '終了コード');
  });

  t('🔴 依存を要らない試験だけが実走し、残りが依存不足で skip した実行は赤（2026-10-08 実測の形）', () => {
    const fx = fixture({
      knowledge: {
        list: listOutput(['K.I.A.config', 'K.I.A.one', 'K.I.A.two']),
        files: { [TR]: trx([P('K.I.A.config'), S('K.I.A.one'), S('K.I.A.two', 'No broker available – start Docker, or set X')]) },
      },
    });
    const u = collectUnit('knowledge', fs.readFileSync(path.join(fx.lists, 'knowledge.list'), 'utf8'), fx.root);
    eq(u.dependencySkipped, 2, '依存不足の skip 件数（両方の門の目印を拾う）');
    eq(go(fx), 1, '終了コード');
  });

  t('依存不足の skip の上限を緩めれば、その範囲は通す（上限の効きを固定する）', () => {
    const fx = fixture({ knowledge: { list: listOutput(['K.I.A.one', 'K.I.A.two']), files: { [TR]: trx([P('K.I.A.one'), S('K.I.A.two')]) } } });
    eq(go(fx, { maxDependencySkips: 1 }), 0, '終了コード');
    eq(go(fx), 1, '既定（0）では赤');
  });

  t('🔴 統合試験が全 skip（実走 0）なら赤 —— 本検査が止める中心の形', () => {
    const fx = fixture({ knowledge: { list: listOutput(['K.I.A.one', 'K.I.A.two']), files: { [TR]: trx([S('K.I.A.one'), S('K.I.A.two')]) } } });
    eq(go(fx), 1, '終了コード');
  });

  t('🔴 単体試験が実走していても、統合試験が全 skip なら赤（単体の実走で埋めない）', () => {
    const fx = fixture({
      knowledge: {
        list: listOutput(['K.I.A.one']),
        files: { [TR]: trx([C('K.I.A.one'), P('K.I.Unit.x')]), [UT]: trx([P('K.U.B.one'), P('K.U.B.two')]) },
      },
    });
    eq(go(fx), 1, '終了コード');
  });

  t('🔴 skip は Counters ではなく結果から数える', () => {
    const fx = fixture({ knowledge: { list: listOutput(['K.I.A.one', 'K.I.A.two']), files: { [TR]: trx([P('K.I.A.one'), C('K.I.A.two')]) } } });
    const u = collectUnit('knowledge', fs.readFileSync(path.join(fx.lists, 'knowledge.list'), 'utf8'), fx.root);
    eq(u.skipped.length, 1, 'skip 件数');
    eq(u.passed, 1, '合格件数');
  });

  t('失敗した統合試験は実走に数える（合否はテスト step が判定する）', () => {
    const fx = fixture({ knowledge: { list: listOutput(['K.I.A.one']), files: { [TR]: trx([F('K.I.A.one')]) } } });
    eq(go(fx), 0, '終了コード');
  });

  t('Theory の行（引数つき）は「クラス.メソッド」で一覧と突き合わせる', () => {
    const fx = fixture({
      knowledge: { list: listOutput(['K.I.A.theory(x: 1)']), files: { [TR]: trx([P('K.I.A.theory(x: 1)'), P('K.I.A.theory(x: 2)')]) } },
    });
    const u = collectUnit('knowledge', fs.readFileSync(path.join(fx.lists, 'knowledge.list'), 'utf8'), fx.root);
    eq(u.passed, 2, '合格件数');
    eq(go(fx), 0, '終了コード');
  });

  t('統合試験の宣言が 0 件のユニットは判定しない（他のユニットが実走していれば合格）', () => {
    const fx = fixture({
      knowledge: { list: listOutput(['K.I.A.one']), files: { [TR]: trx([P('K.I.A.one')]) } },
      platform: { list: listOutput([]), files: { [UT]: trx([P('P.U.one')]) } },
    });
    eq(go(fx), 0, '終了コード');
  });

  t('🔴 宣言のあるユニットが 1 つでも全 skip なら赤（ユニットごとに数える）', () => {
    const fx = fixture({
      knowledge: { list: listOutput(['K.I.A.one']), files: { [TR]: trx([P('K.I.A.one')]) } },
      platform: { list: listOutput(['P.I.one']), files: { [TR]: trx([S('P.I.one')]) } },
    });
    eq(go(fx), 1, '終了コード');
  });

  t('🔴 どのユニットにも宣言が無ければ赤（Trait・一覧の配線の破れを緑にしない）', () => {
    const fx = fixture({ platform: { list: listOutput([]), files: { [UT]: trx([P('P.U.one')]) } } });
    eq(go(fx), 1, '終了コード');
  });

  t('🔴 一覧の見出しが無ければ赤（ビルド失敗の出力を「宣言 0 件」と読まない）', () => {
    // 他のユニットが合格していても、一覧の読めないユニットがあれば赤（G4 の全体判定に紛れさせない）。
    const fx = fixture({
      knowledge: { list: listOutput(['K.I.A.one']), files: { [TR]: trx([P('K.I.A.one')]) } },
      platform: { list: 'error MSB1009: Project file does not exist.\n', files: { [TR]: trx([P('P.I.one')]) } },
    });
    eq(go(fx), 1, '終了コード');
  });

  t('🔴 宣言はあるのに TRX が無ければ赤（--logger trx が外れた形）', () => {
    const fx = fixture({ knowledge: { list: listOutput(['K.I.A.one']) } });
    eq(go(fx), 1, '終了コード');
  });

  t('🔴 壊れた TRX は読み飛ばさず赤', () => {
    const fx = fixture({ knowledge: { list: listOutput(['K.I.A.one']), files: { [TR]: trx([P('K.I.A.one')]), [UT]: 'garbage' } } });
    eq(go(fx), 1, '終了コード');
  });

  t('TestResults/ の外の .trx は数えない（取り違えた古い成果物を拾わない）', () => {
    const fx = fixture({
      knowledge: { list: listOutput(['K.I.A.one']), files: { [TR]: trx([C('K.I.A.one')]), 'stray/x.trx': trx([P('K.I.A.one')]) } },
    });
    eq(go(fx), 1, '終了コード');
  });

  t('対象外ユニット（submodule）は判定に入れない', () => {
    const fx = fixture({
      knowledge: { list: listOutput(['K.I.A.one']), files: { [TR]: trx([P('K.I.A.one')]) } },
      'ai-stock-trading': { list: listOutput(['A.I.one']), files: { [TR]: trx([S('A.I.one')]) } },
    });
    eq(go(fx, {}, new Set(['ai-stock-trading'])), 0, '終了コード');
  });

  t('一覧のディレクトリが空（存在しない）なら赤', () => {
    eq(run(path.join(tmp, 'nope'), {}, { quiet: true, excluded: NONE }), 1, '終了コード');
  });

  t('実走の下限を引き上げれば、それを割ったら赤', () => {
    const fx = fixture({ knowledge: { list: listOutput(['K.I.A.one', 'K.I.A.two']), files: { [TR]: trx([P('K.I.A.one'), C('K.I.A.two')]) } } });
    eq(go(fx, { minExecuted: 2 }), 1, '終了コード');
  });

  t('報告にユニットごとの宣言・実走・skip と、skip の試験名・理由を出す', () => {
    const fx = fixture({
      knowledge: { list: listOutput(['K.I.日本語の名前']), files: { [TR]: trx([S('K.I.日本語の名前', 'No broker available &amp; PLATFORM_TEST_RABBITMQ')]) } },
    });
    const u = collectUnit('knowledge', fs.readFileSync(path.join(fx.lists, 'knowledge.list'), 'utf8'), fx.root);
    const report = formatReport([u], ['ai-stock-trading'], DEFAULTS, judge([u], DEFAULTS));
    for (const want of ['| knowledge | 1 | 0 | 0 | 0 | 1 | 1 | 0 | 0 | 1 |', 'K.I.日本語の名前', '理由: No broker available & PLATFORM_TEST_RABBITMQ', 'ai-stock-trading | 対象外', '不合格']) {
      if (!report.includes(want)) throw new Error(`報告に「${want}」が無い:\n${report}`);
    }
  });

  t('一覧の解析は最初の見出しより後の 4 空白行を名前にする（並列出力の割り込みで名前を落とさない）', () => {
    const text = `Build started\n    not a test\n${LIST_HEADER}\n    A.B.c\nTest run for y\n    A.B.d(x: 1)\n${LIST_HEADER}\nNo test matches the given testcase filter\n    C.D.e\n`;
    const l = parseList(text);
    eq(l.ok, true, 'ok');
    eq([...l.names].sort().join(','), 'A.B.c,A.B.d,C.D.e', '名前');
  });

  fs.rmSync(tmp, { recursive: true, force: true });

  if (failures.length > 0) {
    console.error(`[check-integration-executed] 自己試験 ${failures.length} 件 NG`);
    for (const f of failures) console.error(`  - ${f}`);
    process.exit(1);
  }
  console.log(`[check-integration-executed] 自己試験 ${passed} 件 OK`);
}

// ---------------------------------------------------------------- main

function parseArgs(argv) {
  const opts = {};
  let lists = null;
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--lists') lists = argv[++i];
    else if (a === '--root') opts.root = path.resolve(argv[++i]);
    else if (a === '--min-executed') {
      const n = Number(argv[++i]);
      if (!Number.isInteger(n) || n < 1) throw new Error(`--min-executed には 1 以上の整数を与えること（実際: ${argv[i]}）`);
      opts.minExecuted = n;
    } else if (a === '--max-dependency-skips') {
      const n = Number(argv[++i]);
      if (!Number.isInteger(n) || n < 0) throw new Error(`--max-dependency-skips には 0 以上の整数を与えること（実際: ${argv[i]}）`);
      opts.maxDependencySkips = n;
    } else throw new Error(`未知の引数: ${a}`);
  }
  return { lists, opts };
}

function main() {
  if (process.argv.includes('--self-test')) {
    selfTest();
    return;
  }
  let parsed;
  try {
    parsed = parseArgs(process.argv.slice(2));
  } catch (e) {
    console.error(e.message);
    process.exit(2);
  }
  if (!parsed.lists) {
    console.error('使い方: node scripts/check-integration-executed.js --lists <一覧のディレクトリ> [--root <src>] [--min-executed N] [--max-dependency-skips N]');
    process.exit(2);
  }
  process.exit(run(parsed.lists, parsed.opts));
}

if (require.main === module) main();

module.exports = { parseList, parseTrx, baseName, isDependencySkip, collectUnit, judge, formatReport, run, DEFAULTS, DEPENDENCY_SKIP_MARKERS };
