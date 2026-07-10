#!/usr/bin/env node
'use strict';
/*
 * check-unit-dependencies.js
 * ユニット依存方向の機械検査（FR-14, IADR-0027 / IADR-0056 / IADR-0057, Issue #231）。
 * src/README.md「依存規則」を CI で機械強制する。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 検査するルール（backend）:
 *   1) ユニット外参照: 各 .csproj の ProjectReference を解決し、参照元/先ユニット（src/<unit>/ の
 *      第 1 セグメント）が異なる場合、
 *        - 参照先が platform/backend/Shared/（Contracts / Infrastructure）なら許可、
 *        - 参照元が Tests プロジェクトで参照先が platform サービス（統合テスト例外）なら許可、
 *        - それ以外（特に platform → 可変ユニット）は違反。
 *   2) Foundation → Composable: Foundation/ 配下 .cs に `using <ns>.Composable(.|;)` が現れたら違反。
 *
 * フロントの合成点制約（合成点以外の @knowledge / @features import 禁止）は ESLint
 * （src/eslint.config.js の no-restricted-imports）で検査する（lint ジョブ）。本スクリプト対象外。
 *
 * 使い方:
 *   node scripts/check-unit-dependencies.js            # src/ を走査。違反があれば終了コード 1。
 *   node scripts/check-unit-dependencies.js --self-test # 検査ロジック自体の自己試験。
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const SRC_DIR = 'src';
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', 'dist', 'coverage']);

// --- 純粋ロジック（scripts.test.js から単体テストする） -------------------------

// posix 区切りへ正規化する。
function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

// リポジトリ相対パス（posix）から所属ユニット（src/<unit>/ の <unit>）を返す。src 外は null。
function pathUnit(relPath) {
  const m = toPosix(relPath).match(/^src\/([^/]+)\//);
  return m ? m[1] : null;
}

// 参照先が platform/backend/Shared/ 配下（ユニット外から参照を許可する 2 プロジェクト）か。
function isSharedProject(relPath) {
  return /^src\/platform\/backend\/Shared\//.test(toPosix(relPath));
}

// csproj がテストプロジェクトか（*.Tests.csproj もしくは Tests/ 配下）。統合テスト例外の判定に使う。
function isTestsProject(relPath) {
  const p = toPosix(relPath);
  return /\.Tests\.csproj$/i.test(p) || /(^|\/)[Tt]ests\//.test(p);
}

// ProjectReference 1 件を分類する。{ ok: boolean, reason: string }。
// from / to はいずれもリポジトリ相対（posix）の csproj パス。
function classifyProjectReference(fromCsproj, toCsproj) {
  const fromUnit = pathUnit(fromCsproj);
  const toUnit = pathUnit(toCsproj);
  // src 外（想定外）の参照は本検査の対象外とする。
  if (!fromUnit || !toUnit) return { ok: true, reason: 'out-of-src' };
  // 同一ユニット内の参照は常に許可。
  if (fromUnit === toUnit) return { ok: true, reason: 'intra-unit' };
  // 以降はユニットをまたぐ参照。
  if (toUnit === 'platform' && isSharedProject(toCsproj)) {
    return { ok: true, reason: 'allowed-shared' };
  }
  // 統合テスト例外: Tests プロジェクトが platform のサービスを検証対象として参照する場合のみ許可。
  if (toUnit === 'platform' && isTestsProject(fromCsproj)) {
    return { ok: true, reason: 'integration-test-exception' };
  }
  if (fromUnit === 'platform') {
    return { ok: false, reason: `platform → 可変ユニット(${toUnit}) の参照は禁止（一方向依存）` };
  }
  return {
    ok: false,
    reason: `ユニット外参照は platform/backend/Shared/ の 2 プロジェクトのみ許可（${fromUnit} → ${toUnit}）`,
  };
}

// Foundation/ 配下 .cs の Composable への using を検出する。違反 using 行の配列を返す。
function scanFoundationComposable(relPath, content) {
  if (!/(^|\/)Foundation\//.test(toPosix(relPath))) return [];
  const violations = [];
  const re = /^\s*(?:global\s+)?using\s+(?:static\s+)?([A-Za-z_][\w.]*\.Composable(?:\.[\w.]+)?)\s*;/gm;
  let m;
  while ((m = re.exec(content))) violations.push(m[0].trim());
  return violations;
}

// --- ファイル走査 -------------------------------------------------------------

function walk(dir, pred, out) {
  let ents;
  try { ents = fs.readdirSync(dir, { withFileTypes: true }); } catch (e) { return out; }
  for (const ent of ents) {
    if (ent.isDirectory()) {
      if (SKIP_DIRS.has(ent.name)) continue;
      walk(path.join(dir, ent.name), pred, out);
    } else if (ent.isFile() && pred(ent.name)) {
      out.push(path.join(dir, ent.name));
    }
  }
  return out;
}

function repoRel(abs) {
  return toPosix(path.relative(REPO_ROOT, abs));
}

// csproj の ProjectReference Include を解決してリポジトリ相対 posix パスの配列で返す。
function projectReferencesOf(csprojAbs) {
  let content = '';
  try { content = fs.readFileSync(csprojAbs, 'utf8'); } catch (e) { return []; }
  const dir = path.dirname(csprojAbs);
  const out = [];
  const re = /ProjectReference\s+Include="([^"]+)"/g;
  let m;
  while ((m = re.exec(content))) {
    const resolved = path.resolve(dir, m[1].replace(/\\/g, path.sep));
    out.push(repoRel(resolved));
  }
  return out;
}

function checkTree() {
  const violations = [];
  // 1) ProjectReference の方向検査。
  const csprojs = walk(path.join(REPO_ROOT, SRC_DIR), (n) => n.endsWith('.csproj'), []);
  for (const abs of csprojs) {
    const from = repoRel(abs);
    for (const to of projectReferencesOf(abs)) {
      const r = classifyProjectReference(from, to);
      if (!r.ok) violations.push({ kind: 'project-reference', from, to, reason: r.reason });
    }
  }
  // 2) Foundation → Composable の using 検査。
  const csFiles = walk(path.join(REPO_ROOT, SRC_DIR), (n) => n.endsWith('.cs'), []);
  for (const abs of csFiles) {
    const rel = repoRel(abs);
    if (!/(^|\/)Foundation\//.test(rel)) continue;
    let content = '';
    try { content = fs.readFileSync(abs, 'utf8'); } catch (e) { continue; }
    for (const line of scanFoundationComposable(rel, content)) {
      violations.push({ kind: 'foundation-composable', from: rel, to: '(Composable)', reason: line });
    }
  }
  return violations;
}

// --- 自己試験（--self-test） --------------------------------------------------

function selfTest() {
  const cases = [];
  const expectOk = (name, actual) => cases.push({ name, pass: actual.ok === true, actual });
  const expectViolation = (name, actual) => cases.push({ name, pass: actual.ok === false, actual });

  // 許可: 可変ユニット → platform Shared。
  expectOk('knowledge → platform Shared.Contracts は許可', classifyProjectReference(
    'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj',
    'src/platform/backend/Shared/KnowledgePlatform.Shared.Contracts/KnowledgePlatform.Shared.Contracts.csproj'));
  // 許可: 同一ユニット内（テスト → 実装）。
  expectOk('同一ユニット内参照は許可', classifyProjectReference(
    'src/knowledge/backend/Services/DocumentService/tests/DocumentService.Api.Tests/DocumentService.Api.Tests.csproj',
    'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj'));
  // 許可: 統合テスト例外（可変ユニットの Tests → platform サービス）。
  expectOk('統合テスト → platform サービスは許可', classifyProjectReference(
    'src/knowledge/backend/Tests/KnowledgePlatform.IntegrationTests/KnowledgePlatform.IntegrationTests.csproj',
    'src/platform/backend/Services/AuthorizationService/src/AuthorizationService.Api/AuthorizationService.Api.csproj'));
  // 違反: platform → 可変ユニット。
  expectViolation('platform → knowledge は違反', classifyProjectReference(
    'src/platform/backend/Bff/KnowledgePlatform.Bff/KnowledgePlatform.Bff.csproj',
    'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj'));
  // 違反: 可変ユニット（非テスト） → platform の非 Shared サービス。
  expectViolation('knowledge サービス → platform サービス（非 Shared）は違反', classifyProjectReference(
    'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj',
    'src/platform/backend/Services/AuthorizationService/src/AuthorizationService.Api/AuthorizationService.Api.csproj'));
  // 違反: 可変ユニット同士のコード参照。
  expectViolation('可変ユニット間の参照は違反', classifyProjectReference(
    'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj',
    'src/analytics/backend/Services/ReportService/src/ReportService.Api/ReportService.Api.csproj'));
  // Foundation → Composable の using 検出。
  cases.push({
    name: 'Foundation 配下の using .Composable を検出',
    pass: scanFoundationComposable(
      'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/Foundation/Endpoints/X.cs',
      'namespace X;\nusing DocumentService.Api.Composable.Steps;\n').length === 1,
  });
  cases.push({
    name: 'Foundation 配下でも Composable を含まない using は無視',
    pass: scanFoundationComposable(
      'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/Foundation/Endpoints/X.cs',
      'using DocumentService.Api.Foundation.Domain;\n').length === 0,
  });
  cases.push({
    name: 'Foundation 外の using .Composable は無視（合成点等）',
    pass: scanFoundationComposable(
      'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/Program.cs',
      'using DocumentService.Api.Composable.Steps;\n').length === 0,
  });

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[check-unit-dependencies] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-unit-dependencies] 自己試験 ${cases.length} 件 OK。`);
}

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }
  const violations = checkTree();
  if (violations.length === 0) {
    console.log('[check-unit-dependencies] OK: ユニット依存方向の違反はありません。');
    process.exit(0);
  }
  console.error(`[check-unit-dependencies] 依存方向の違反 ${violations.length} 件を検出しました:`);
  for (const v of violations) {
    if (v.kind === 'project-reference') {
      console.error(`\n  [ProjectReference] ${v.from}\n    → ${v.to}\n    ${v.reason}`);
    } else {
      console.error(`\n  [Foundation→Composable] ${v.from}\n    ${v.reason}`);
    }
  }
  console.error('\n依存規則は src/README.md「依存規則」/ IADR-0027 / IADR-0056 を参照してください。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  pathUnit,
  isSharedProject,
  isTestsProject,
  classifyProjectReference,
  scanFoundationComposable,
  projectReferencesOf,
  checkTree,
};
