#!/usr/bin/env node
'use strict';
/*
 * check-backend-libraries.js
 * バックエンドアプリケーション層のライブラリ標準（計画 ADR-0030 / 12_backend-application-stack）の
 * 機械強制（NFR, Issue #455）。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 検査するルール:
 *   1) 不採用ライブラリの参照禁止: .csproj の PackageReference と .cs の using の双方を走査する。
 *      現行実装は MassTransit / FluentAssertions / Serilog を広範に使用中のため、即時に全件 fail
 *      させると「成果物は正しいのに赤」が常態化する（IADR-0115）。よって **ratchet 方式**を採る。
 *        - baseline に無いプロジェクトでの違反 → fail（新規混入を止める）
 *        - baseline にあるプロジェクトの違反   → warn（残件として実行サマリに出す）
 *        - baseline にあるのに違反が消えた     → fail（baseline の減らし忘れを検出する）
 *      3 番目が要点である。カバレッジ ratchet と同じく **床は下げられるが上げっぱなしにできない**。
 *      各サービスの再実装 issue（#438〜#451）は移行と同時に baseline から自プロジェクトを削除する。
 *   2) Domain 層の外部依存ゼロ: *.Domain.csproj は PackageReference を持てない
 *      （ProjectReference は共有カーネル Platform.Shared.Kernel のみ許可）。ADR-0030 選定基準 3 の機械化。
 *      現時点で *.Domain プロジェクトは存在しないため、既知違反ゼロで開始でき ratchet を要さない。
 *
 * 使い方:
 *   node scripts/check-backend-libraries.js             # src/ を走査。違反があれば終了コード 1。
 *   node scripts/check-backend-libraries.js --self-test # 検査ロジック自体の自己試験。
 *   node scripts/check-backend-libraries.js --write-baseline # 現状を baseline へ書き出す（初回のみ）。
 */
const fs = require('fs');
const path = require('path');
const { warn, notice } = require('./lib/ci-annotate');

const REPO_ROOT = path.resolve(__dirname, '..');
const SRC_DIR = 'src';
const BASELINE_FILE = path.join(__dirname, 'backend-library-baseline.json');
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', 'dist', 'coverage']);

/**
 * 検査対象外のユニット。ADR-0030 は **MSP（microservices-platform）の計画 ADR** であり、
 * src/ai-stock-trading は独自の計画リポジトリと ADR を持つ**別プロジェクト**（submodule）である。
 * 他プロジェクトへ MSP の標準を適用するのは誤りであり、submodule のため本リポジトリからは
 * 是正もできない（.claude/rules/traceability.md「複数プロジェクトを跨ぐ場合」と同じ切り分け）。
 */
const EXCLUDED_UNITS = new Set(['ai-stock-trading']);

/** リポジトリ相対パスが検査対象外ユニット配下か。 */
function isExcludedPath(relPath) {
  const m = toPosix(relPath).match(/^src\/([^/]+)\//);
  return m ? EXCLUDED_UNITS.has(m[1]) : false;
}

/**
 * 不採用ライブラリ（計画 12_backend-application-stack の棚卸し表で ★不採用 / 置換対象）。
 * 値はパッケージ ID かつ using の名前空間の先頭セグメント群として扱う。
 * 判定は「完全一致」または「<名前>. で始まる」（Serilog → Serilog.AspNetCore は該当、
 * SerilogFoo は非該当）。
 */
const BANNED = [
  'MediatR',
  'AutoMapper',
  'Mapster',
  'MassTransit',
  'FluentAssertions',
  'Serilog',
  'Hellang.Middleware.ProblemDetails',
  'OneOf',
  'CSharpFunctionalExtensions',
  'Z.EntityFramework.Extensions',
  'Hangfire',
  'OpenIddict',
  'BCrypt.Net-Next',
  'DotNetEnv',
  'BouncyCastle.Cryptography',
  'Kiota',
  'NSwag',
];

/** 共有カーネル（Domain が唯一 ProjectReference を許される先）。 */
const SHARED_KERNEL = 'Platform.Shared.Kernel';

/**
 * xUnit v3 と runner の版整合を検査する対象。
 *
 * xunit.runner.visualstudio は v2 用（2.x）と v3 用（3.x）で別系列である。**CPM は 1 パッケージに
 * 1 バージョンしか持てない**ため、CPM の runner が 2.x に固定されている状態で xunit.v3 を参照する
 * プロジェクトを作ると、非互換の runner と組み合わさる。
 *
 * この誤りは通常の CI では捕まらない。ci.yml のビルド対象は src/<unit>/backend/backend.slnx のみで
 * **templates/ は対象外**であり、雛形をコピーして最初のサービスを作った人が dotnet test を走らせて
 * 初めて表面化する（PR #463 のレビューで実際に指摘された）。よって templates/ も走査する。
 */
const XUNIT_V3 = 'xunit.v3';
const XUNIT_RUNNER = 'xunit.runner.visualstudio';
const TEMPLATE_DIR = 'templates';

// --- 純粋ロジック（scripts.test.js から単体テストする） -------------------------

/** posix 区切りへ正規化する。 */
function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

/**
 * name が禁止対象 banned に該当するか。完全一致、または `banned.` 前方一致。
 * 「Serilog」が「Serilog.AspNetCore」に一致し「SerilogExtras」には一致しないこと（境界）が要点。
 */
function matchesBanned(name, banned) {
  const n = String(name).trim();
  return n === banned || n.startsWith(banned + '.');
}

/** name に該当する禁止ライブラリ名を返す（無ければ null）。最長一致を優先する。 */
function bannedNameOf(name, bannedList = BANNED) {
  let best = null;
  for (const b of bannedList) {
    if (matchesBanned(name, b) && (best === null || b.length > best.length)) best = b;
  }
  return best;
}

/** .csproj 本文から PackageReference の Include 値を列挙する。 */
function packageReferencesOf(content) {
  const out = [];
  const re = /<PackageReference\b[^>]*\bInclude\s*=\s*"([^"]+)"/g;
  let m;
  while ((m = re.exec(String(content))) !== null) out.push(m[1]);
  return out;
}

/** .csproj 本文から ProjectReference の Include 値を列挙する。 */
function projectReferencesOf(content) {
  const out = [];
  const re = /<ProjectReference\b[^>]*\bInclude\s*=\s*"([^"]+)"/g;
  let m;
  while ((m = re.exec(String(content))) !== null) out.push(m[1]);
  return out;
}

/**
 * .cs 本文から using が導入する名前空間を列挙する。
 * `using X;` / `global using X;` / `using static X.Y;` / `using Alias = X.Y;` を扱う。
 * エイリアス形は右辺（実体側）を採る。ブロック構文 `using (var x = ...)` は除外する。
 */
function usingNamespacesOf(content) {
  const out = [];
  const re = /^[ \t]*(?:global[ \t]+)?using[ \t]+(?:static[ \t]+)?(?:([A-Za-z_][\w]*)[ \t]*=[ \t]*)?([A-Za-z_][\w.]*)[ \t]*;/gm;
  let m;
  while ((m = re.exec(String(content))) !== null) out.push(m[2]);
  return out;
}

/** .csproj 本文から検出した禁止ライブラリ名（重複なし・ソート済み）。 */
function bannedInCsproj(content, bannedList = BANNED) {
  const hits = new Set();
  for (const id of packageReferencesOf(content)) {
    const b = bannedNameOf(id, bannedList);
    if (b) hits.add(b);
  }
  return [...hits].sort();
}

/** .cs 本文から検出した禁止ライブラリ名（重複なし・ソート済み）。 */
function bannedInSource(content, bannedList = BANNED) {
  const hits = new Set();
  for (const ns of usingNamespacesOf(content)) {
    const b = bannedNameOf(ns, bannedList);
    if (b) hits.add(b);
  }
  return [...hits].sort();
}

/** Directory.Packages.props 本文から指定パッケージの中央バージョンを返す（無ければ null）。 */
function centralVersionOf(propsContent, packageId) {
  const re = new RegExp(`<PackageVersion\\b[^>]*\\bInclude\\s*=\\s*"${packageId.replace(/\./g, '\\.')}"[^>]*\\bVersion\\s*=\\s*"([^"]+)"`, 'i');
  const m = re.exec(String(propsContent));
  return m ? m[1] : null;
}

/** バージョン文字列のメジャー番号（数値）。取れなければ null。 */
function majorOf(version) {
  const m = /^(\d+)\./.exec(String(version || ''));
  return m ? Number(m[1]) : null;
}

/**
 * xunit.v3 を参照しているのに CPM の xunit.runner.visualstudio が v3 系（3.x）でない場合を違反として返す。
 * runnerVersion が null（CPM に定義が無い＝各 csproj が版を持つ）のときは判定しない。
 */
function xunitRunnerMismatch(relPath, csprojContent, runnerVersion) {
  const refs = packageReferencesOf(csprojContent);
  if (!refs.includes(XUNIT_V3)) return [];
  if (!refs.includes(XUNIT_RUNNER)) return [];
  const major = majorOf(runnerVersion);
  if (major === null || major >= 3) return [];
  return [{
    kind: 'xunit-runner-mismatch',
    project: toPosix(relPath),
    detail: `${XUNIT_V3} を参照していますが CPM の ${XUNIT_RUNNER} は ${runnerVersion}（v2 系）です`,
  }];
}

/** リポジトリ相対パスが Domain プロジェクトの .csproj か。 */
function isDomainProject(relPath) {
  return /\.Domain\.csproj$/i.test(toPosix(relPath));
}

/**
 * Domain 層の依存規律違反を返す。PackageReference は 1 件でも違反。
 * ProjectReference は共有カーネル以外を違反とする。
 */
function domainViolations(relPath, content) {
  if (!isDomainProject(relPath)) return [];
  const out = [];
  for (const id of packageReferencesOf(content)) {
    out.push({ kind: 'domain-package', project: toPosix(relPath), detail: id });
  }
  for (const ref of projectReferencesOf(content)) {
    const base = path.basename(toPosix(ref)).replace(/\.csproj$/i, '');
    if (base !== SHARED_KERNEL) {
      out.push({ kind: 'domain-project', project: toPosix(relPath), detail: ref });
    }
  }
  return out;
}

/**
 * 現状（プロジェクト → 禁止ライブラリ名の配列）と baseline を突き合わせ、3 分類して返す。
 *   added   : baseline に無い（プロジェクトごと新規 / 既知プロジェクトでの新ライブラリ）→ fail
 *   known   : baseline どおり残っている → warn
 *   stale   : baseline にあるが現状に無い（減らし忘れ）→ fail
 */
function classifyAgainstBaseline(current, baseline) {
  const added = [];
  const known = [];
  const stale = [];
  const projects = new Set([...Object.keys(current), ...Object.keys(baseline)]);
  for (const project of [...projects].sort()) {
    const cur = new Set(current[project] || []);
    const base = new Set(baseline[project] || []);
    for (const lib of [...cur].sort()) {
      if (base.has(lib)) known.push({ project, lib });
      else added.push({ project, lib });
    }
    for (const lib of [...base].sort()) {
      if (!cur.has(lib)) stale.push({ project, lib });
    }
  }
  return { added, known, stale };
}

// --- ファイル走査 ---------------------------------------------------------------

/** dir 配下を再帰的に走査し、条件に合うファイルの相対パスを返す。 */
function walk(dir, predicate, acc = []) {
  const abs = path.join(REPO_ROOT, dir);
  let entries;
  try {
    entries = fs.readdirSync(abs, { withFileTypes: true });
  } catch {
    return acc;
  }
  for (const e of entries) {
    if (SKIP_DIRS.has(e.name)) continue;
    const rel = toPosix(path.join(dir, e.name));
    if (e.isDirectory()) walk(rel, predicate, acc);
    else if (predicate(rel)) acc.push(rel);
  }
  return acc;
}

/**
 * .cs ファイルが属するプロジェクト（最も近い祖先の .csproj のディレクトリ相対パス）を返す。
 * 見つからなければ null。
 */
function owningProject(relCsPath, csprojPaths) {
  const p = toPosix(relCsPath);
  let best = null;
  for (const proj of csprojPaths) {
    const dir = toPosix(path.dirname(proj)) + '/';
    if (p.startsWith(dir) && (best === null || dir.length > best.dir.length)) {
      best = { dir, proj };
    }
  }
  return best ? best.proj : null;
}

/** src/ を走査し、プロジェクト → 禁止ライブラリ名配列 と Domain 違反を返す。 */
function scanTree() {
  const csprojPaths = walk(SRC_DIR, (p) => /\.csproj$/i.test(p) && !isExcludedPath(p));
  const current = {};
  const domain = [];
  const add = (project, libs) => {
    if (!libs.length) return;
    const set = new Set(current[project] || []);
    for (const l of libs) set.add(l);
    current[project] = [...set].sort();
  };

  // CPM の runner 版（xunit.v3 との整合判定に使う）。
  let runnerVersion = null;
  try {
    runnerVersion = centralVersionOf(fs.readFileSync(path.join(REPO_ROOT, 'src', 'Directory.Packages.props'), 'utf8'), XUNIT_RUNNER);
  } catch { /* CPM を読めない構成では版整合の判定を行わない */ }

  for (const proj of csprojPaths) {
    const content = fs.readFileSync(path.join(REPO_ROOT, proj), 'utf8');
    add(proj, bannedInCsproj(content));
    domain.push(...domainViolations(proj, content));
    domain.push(...xunitRunnerMismatch(proj, content, runnerVersion));
  }
  // templates/ は ci.yml のビルド対象外のため、ここで走査しないと雛形の版不整合が誰にも捕まらない。
  // 不採用ライブラリの baseline 対象にはしない（雛形は src/ の残件ではないため）。
  for (const proj of walk(TEMPLATE_DIR, (p) => /\.csproj$/i.test(p))) {
    const content = fs.readFileSync(path.join(REPO_ROOT, proj), 'utf8');
    domain.push(...domainViolations(proj, content));
    domain.push(...xunitRunnerMismatch(proj, content, runnerVersion));
    const banned = bannedInCsproj(content);
    if (banned.length) {
      domain.push({ kind: 'template-banned', project: toPosix(proj), detail: banned.join(' / ') });
    }
  }
  for (const cs of walk(SRC_DIR, (p) => /\.cs$/i.test(p) && !isExcludedPath(p))) {
    const proj = owningProject(cs, csprojPaths);
    if (!proj) continue;
    const content = fs.readFileSync(path.join(REPO_ROOT, cs), 'utf8');
    add(proj, bannedInSource(content));
  }
  return { current, domain };
}

/** baseline を読む。無ければ空。 */
function readBaseline() {
  try {
    return JSON.parse(fs.readFileSync(BASELINE_FILE, 'utf8')).projects || {};
  } catch {
    return {};
  }
}

// --- 自己試験 -------------------------------------------------------------------

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });

  // 境界: 前方一致は「.」区切りのときだけ効く。
  t('Serilog は Serilog.AspNetCore に一致する', matchesBanned('Serilog.AspNetCore', 'Serilog'));
  t('Serilog は SerilogExtras に一致しない', !matchesBanned('SerilogExtras', 'Serilog'));
  t('MassTransit は MassTransit.RabbitMQ に一致する', matchesBanned('MassTransit.RabbitMQ', 'MassTransit'));
  t('bannedNameOf は非該当で null', bannedNameOf('Npgsql.EntityFrameworkCore.PostgreSQL') === null);

  // csproj の抽出。
  t('PackageReference から禁止パッケージを検出',
    JSON.stringify(bannedInCsproj('<PackageReference Include="MassTransit.RabbitMQ" />')) === '["MassTransit"]');
  t('Version 属性が先にあっても Include を拾う',
    JSON.stringify(bannedInCsproj('<PackageReference Version="1.0" Include="FluentAssertions" />')) === '["FluentAssertions"]');
  t('採用ライブラリのみなら空',
    bannedInCsproj('<PackageReference Include="FluentValidation" />').length === 0);

  // using の抽出。
  t('using MassTransit; を検出',
    JSON.stringify(bannedInSource('using MassTransit;\n')) === '["MassTransit"]');
  t('global using / static / エイリアスも検出',
    JSON.stringify(bannedInSource('global using Serilog;\nusing static FluentAssertions.AssertionExtensions;\nusing M = MassTransit.IBus;\n'))
      === '["FluentAssertions","MassTransit","Serilog"]');
  t('using ブロック構文は名前空間として拾わない',
    bannedInSource('using (var x = new MassTransitThing()) { }\n').length === 0);
  t('コメント行内の語は using 形でなければ拾わない',
    bannedInSource('// MassTransit をやめる\n').length === 0);

  // Domain 層の依存規律。
  t('Domain の PackageReference は違反',
    domainViolations('src/platform/backend/X.Domain.csproj', '<PackageReference Include="FluentValidation" />').length === 1);
  t('Domain の共有カーネル参照は許可',
    domainViolations('src/platform/backend/X.Domain.csproj',
      '<ProjectReference Include="../../Shared/Platform.Shared.Kernel/Platform.Shared.Kernel.csproj" />').length === 0);
  t('Domain の共有カーネル以外の ProjectReference は違反',
    domainViolations('src/platform/backend/X.Domain.csproj',
      '<ProjectReference Include="../X.Infrastructure/X.Infrastructure.csproj" />').length === 1);
  t('Domain 以外の csproj は本検査の対象外',
    domainViolations('src/platform/backend/X.Api.csproj', '<PackageReference Include="FluentValidation" />').length === 0);

  // ratchet の 3 判定。
  {
    const r = classifyAgainstBaseline({ 'a.csproj': ['MassTransit'] }, {});
    t('baseline に無い違反は added（fail 対象）', r.added.length === 1 && r.known.length === 0 && r.stale.length === 0);
  }
  {
    const r = classifyAgainstBaseline({ 'a.csproj': ['MassTransit'] }, { 'a.csproj': ['MassTransit'] });
    t('baseline どおりの違反は known（warn）', r.known.length === 1 && r.added.length === 0 && r.stale.length === 0);
  }
  {
    const r = classifyAgainstBaseline({}, { 'a.csproj': ['MassTransit'] });
    t('違反が消えたのに baseline に残るのは stale（fail 対象）', r.stale.length === 1 && r.added.length === 0);
  }
  {
    const r = classifyAgainstBaseline({ 'a.csproj': ['MassTransit', 'Serilog'] }, { 'a.csproj': ['MassTransit'] });
    t('既知プロジェクトでの新ライブラリ追加も added', r.added.length === 1 && r.added[0].lib === 'Serilog' && r.known.length === 1);
  }

  // 所属プロジェクトの解決（最も深い .csproj を採る）。
  t('owningProject は最も近い祖先の csproj を返す',
    owningProject('src/a/b/c/X.cs', ['src/a/A.csproj', 'src/a/b/B.csproj']) === 'src/a/b/B.csproj');
  t('owningProject は配下に無ければ null',
    owningProject('src/z/X.cs', ['src/a/A.csproj']) === null);

  // xUnit v3 と CPM の runner 版の整合（PR #463 レビュー指摘の回帰防止）。
  const V3_PROJ = '<PackageReference Include="xunit.v3" /><PackageReference Include="xunit.runner.visualstudio" />';
  t('centralVersionOf: CPM から版を取り出す',
    centralVersionOf('<PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />', 'xunit.runner.visualstudio') === '2.8.2');
  t('centralVersionOf: 未定義は null',
    centralVersionOf('<PackageVersion Include="xunit" Version="2.9.3" />', 'xunit.runner.visualstudio') === null);
  t('majorOf: メジャー番号', majorOf('2.8.2') === 2 && majorOf('3.1.5') === 3 && majorOf('') === null);
  t('xunit.v3 ＋ runner 2.x は違反（雛形で実際に作り込んだ不整合）',
    xunitRunnerMismatch('templates/x/X.Tests.csproj', V3_PROJ, '2.8.2').length === 1);
  t('xunit.v3 ＋ runner 3.x は適合',
    xunitRunnerMismatch('templates/x/X.Tests.csproj', V3_PROJ, '3.1.5').length === 0);
  t('xunit（v2）＋ runner 2.x は適合',
    xunitRunnerMismatch('templates/x/X.Tests.csproj',
      '<PackageReference Include="xunit" /><PackageReference Include="xunit.runner.visualstudio" />', '2.8.2').length === 0);
  t('runner を参照しなければ判定しない（v3 単体は自己実行できる）',
    xunitRunnerMismatch('templates/x/X.Tests.csproj', '<PackageReference Include="xunit.v3" />', '2.8.2').length === 0);
  t('CPM に runner 定義が無ければ判定しない',
    xunitRunnerMismatch('templates/x/X.Tests.csproj', V3_PROJ, null).length === 0);

  // 検査対象ユニットの切り分け（別プロジェクトの submodule は対象外）。
  t('ai-stock-trading 配下は検査対象外',
    isExcludedPath('src/ai-stock-trading/backend/Services/X/src/X.Api/X.Api.csproj'));
  t('platform は検査対象', !isExcludedPath('src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj'));
  t('knowledge は検査対象', !isExcludedPath('src/knowledge/backend/Shared/Knowledge.Contracts/Knowledge.Contracts.csproj'));
  t('src 直下のファイルは対象外扱いにしない', !isExcludedPath('src/Directory.Packages.props'));

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[check-backend-libraries] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-backend-libraries] 自己試験 ${cases.length} 件 OK。`);
}

// --- 実行 -----------------------------------------------------------------------

function writeBaseline() {
  const { current } = scanTree();
  const body = {
    $comment: [
      'ADR-0030 の不採用ライブラリに対する既知違反の baseline（Issue #455）。',
      'ratchet: 新規混入は fail、ここに載る残件は warn、消えたのに残っていれば fail。',
      '各サービスの再実装 issue（#438〜#451）は移行と同時に自プロジェクトの行を削除すること。',
      'baseline が空になったら Directory.Packages.props から不採用パッケージを削除する。',
    ],
    projects: current,
  };
  fs.writeFileSync(BASELINE_FILE, JSON.stringify(body, null, 2) + '\n');
  console.log(`[check-backend-libraries] baseline を書き出しました: ${Object.keys(current).length} プロジェクト`);
}

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }
  if (process.argv.includes('--write-baseline')) { writeBaseline(); return; }

  const { current, domain } = scanTree();
  const baseline = readBaseline();
  const { added, known, stale } = classifyAgainstBaseline(current, baseline);

  if (known.length) {
    const byProject = new Set(known.map((k) => k.project));
    notice(`ADR-0030 不採用ライブラリの残件: ${known.length} 件 / ${byProject.size} プロジェクト（baseline 済み）。` +
      '各サービスの再実装 issue で解消し baseline から削除すること。');
    const summary = process.env.GITHUB_STEP_SUMMARY;
    if (summary) {
      const lines = ['### ADR-0030 不採用ライブラリの残件（baseline）', '', '| プロジェクト | ライブラリ |', '| --- | --- |'];
      for (const k of known) lines.push(`| \`${k.project}\` | ${k.lib} |`);
      try { fs.appendFileSync(summary, lines.join('\n') + '\n'); } catch { /* サマリ書けなくても検査は続ける */ }
    }
  }

  const failures = [];
  for (const a of added) {
    failures.push(`[新規混入] ${a.project}\n    不採用ライブラリ「${a.lib}」への参照が baseline に無い状態で追加されています。`);
  }
  for (const s of stale) {
    failures.push(`[baseline 減らし忘れ] ${s.project}\n    「${s.lib}」の参照は既に解消済みです。baseline の該当行を削除してください。`);
  }
  for (const d of domain) {
    if (d.kind === 'xunit-runner-mismatch') {
      failures.push(`[xUnit 版不整合] ${d.project}\n    ${d.detail}。` +
        'v3 へ移るには CPM の runner も 3.x にする必要があり、CPM は 1 パッケージ 1 バージョンのため' +
        '全テストプロジェクトが同時に移る。切替は独立した issue で行うこと。');
      continue;
    }
    if (d.kind === 'template-banned') {
      failures.push(`[雛形に不採用ライブラリ] ${d.project}\n    「${d.detail}」。` +
        '雛形は新サービスの出発点であり、不採用ライブラリを持ち込ませてはならない（ADR-0030）。');
      continue;
    }
    const what = d.kind === 'domain-package' ? 'PackageReference' : `ProjectReference（${SHARED_KERNEL} 以外）`;
    failures.push(`[Domain 依存規律] ${d.project}\n    ${what}「${d.detail}」。Domain 層は外部依存ゼロ（ADR-0030 選定基準 3）。`);
  }

  if (failures.length === 0) {
    console.log(`[check-backend-libraries] OK: 新規混入 0 件 / Domain 依存規律 OK（既知残件 ${known.length} 件は baseline 済み）。`);
    process.exit(0);
  }
  console.error(`[check-backend-libraries] 違反 ${failures.length} 件を検出しました:`);
  for (const f of failures) console.error(`\n  ${f}`);
  console.error('\n標準は計画 ADR-0030 / 12_backend-application-stack、実装側の要点は docs/tech/tech-requirements.md を参照してください。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  BANNED,
  EXCLUDED_UNITS,
  isExcludedPath,
  centralVersionOf,
  majorOf,
  xunitRunnerMismatch,
  matchesBanned,
  bannedNameOf,
  packageReferencesOf,
  projectReferencesOf,
  usingNamespacesOf,
  bannedInCsproj,
  bannedInSource,
  isDomainProject,
  domainViolations,
  classifyAgainstBaseline,
  owningProject,
  scanTree,
};
