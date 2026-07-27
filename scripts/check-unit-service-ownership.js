#!/usr/bin/env node
'use strict';
/*
 * check-unit-service-ownership.js
 * 可変ユニット（AST）が所有するサービスを MSP 側でも有効化する「重複デプロイ」を機械検査する
 * （FR-14, IADR-0107 / Issue #407）。外部依存ゼロ（Node 標準モジュールのみ）。
 * check-unit-dependencies.js / check-image-mapping.js / check-bff-downstreams.js と同型。
 *
 * 背景（Issue #407）: 経路B で `risk-management-service` が microservices-platform と ai-stock-trading の
 * 2 namespace へ重複デプロイされ、両者が同一 RabbitMQ（ExternalName 経由で同じ platform-infra 実体）の
 * `TradeDecisionMade` キューを consumers=4 で奪い合い、取引判断を無言で取りこぼした。
 * 本番像 values.yaml は AST 3 サービスを enabled: false（fail-safe 既定）で持つが、経路B の
 * values-local.yaml（#284 / IADR-0076）が enabled: true へ上書きし、AST chart が常時デプロイする
 * 同じサービスと二重化していたことが原因（原因A）。
 *
 * 不変条件: AST chart が所有するサービス名の集合 ∩ MSP 側 values の実効 enabled 集合 == 空。
 *   MSP 側の実効 enabled = 本番像 values.yaml の enabled を、経路B values-local.yaml の enabled で上書き
 *   （Helm のマップ deep-merge と同じ規則。上書き側が enabled を書かなければ基底の値が残る）。
 *
 * AST 所有サービス一覧は submodule の chart values から動的に読む。submodule 未取得（CI の多くのジョブは
 * submodule を取得しない）の場合は AST_OWNED_FALLBACK へ縮退し、検査を落とさない（fail-safe）。
 *
 * 使い方:
 *   node scripts/check-unit-service-ownership.js             # 実ファイルを突合。違反があれば終了コード 1。
 *   node scripts/check-unit-service-ownership.js --self-test # 検査ロジック自体の自己試験。
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const MSP_VALUES = 'deploy/helm/microservices-platform/values.yaml';
const MSP_VALUES_LOCAL = 'deploy/local/values-local.yaml';
const AST_VALUES = 'src/ai-stock-trading/deploy/helm/ai-stock-trading/values.yaml';

// submodule 未取得時のフォールバック（AST chart values の services 直下キー）。
// AST 側にサービスが増えたら追随する。実 chart が読める環境では実値が優先されるため、
// ここが古くても検査は劣化しない（drift は警告として報告する）。
const AST_OWNED_FALLBACK = [
  'audit',
  'backtest',
  'configuration',
  'cost-control',
  'information-collection',
  'market-monitor',
  'notification',
  'order-execution',
  'report',
  'risk-management',
  'trade-decision',
];

// --- 純粋ロジック（scripts.test.js / --self-test から単体テストする） -------------

// 行がコメント／空行か（インデント判定の基準から除外する）。
function isIgnorable(line) {
  const t = line.trim();
  return t === '' || t.startsWith('#');
}

// top-level `services:` 直下のキー（サービス名）を出現順で返す。
// 深い階層のキー（例 datasource.dataSourceSync）や、services ブロックの後続 top-level キーは拾わない。
function parseServiceKeys(yamlText) {
  const lines = String(yamlText).split('\n').map((l) => l.replace(/\r$/, ''));
  let inServices = false;
  let childIndent = null;
  const out = [];
  for (const line of lines) {
    if (isIgnorable(line)) continue;
    const indent = line.length - line.trimStart().length;
    if (!inServices) {
      if (indent === 0 && /^services:\s*$/.test(line)) inServices = true;
      continue;
    }
    if (indent === 0) break; // services ブロックの終わり（次の top-level キー）。
    if (childIndent === null) childIndent = indent;
    if (indent !== childIndent) continue;
    const m = line.match(/^\s*([A-Za-z0-9_-]+):\s*$/);
    if (m) out.push(m[1]);
  }
  return out;
}

// top-level `services:` 直下の各サービスについて、直下に明示された `enabled:` の真偽値を Map で返す。
// 明示されていないサービスはキーごと欠落する（undefined と false を区別するため）。
// サービスより深い階層の `enabled:`（例 datasource.dataSourceSync.enabled）は拾わない。
function parseEnabledFlags(yamlText) {
  const lines = String(yamlText).split('\n').map((l) => l.replace(/\r$/, ''));
  let inServices = false;
  let childIndent = null; // services 直下（サービス名）のインデント。
  let current = null; // 走査中のサービス名。
  let fieldIndent = null; // 走査中サービスの直下フィールドのインデント。
  const out = new Map();
  for (const line of lines) {
    if (isIgnorable(line)) continue;
    const indent = line.length - line.trimStart().length;
    if (!inServices) {
      if (indent === 0 && /^services:\s*$/.test(line)) inServices = true;
      continue;
    }
    if (indent === 0) break;
    if (childIndent === null) childIndent = indent;
    if (indent === childIndent) {
      const m = line.match(/^\s*([A-Za-z0-9_-]+):\s*$/);
      current = m ? m[1] : null;
      fieldIndent = null;
      continue;
    }
    if (current === null) continue;
    if (indent <= childIndent) continue;
    if (fieldIndent === null) fieldIndent = indent; // サービス直下フィールドのインデントを確定。
    if (indent !== fieldIndent) continue; // より深い階層は対象外。
    const m = line.match(/^\s*enabled:\s*(true|false)\s*(?:#.*)?$/);
    if (m) out.set(current, m[1] === 'true');
  }
  return out;
}

// 本番像 values と経路B の上書き values から、実効的に有効なサービス名の Set を返す。
// Helm のマップ deep-merge と同じ規則: 上書き側が enabled を書けばそれが勝ち、書かなければ基底が残る。
// どちらにも無ければ無効（chart の deployment.yaml は `if $svc.enabled` で描画するため）。
function effectiveEnabled(baseText, overrideText) {
  const baseFlags = parseEnabledFlags(baseText);
  const overrideFlags = parseEnabledFlags(overrideText);
  const keys = new Set([...parseServiceKeys(baseText), ...parseServiceKeys(overrideText)]);
  const enabled = new Set();
  for (const key of keys) {
    const value = overrideFlags.has(key) ? overrideFlags.get(key) : baseFlags.get(key);
    if (value === true) enabled.add(key);
  }
  return enabled;
}

// 重複デプロイ（AST 所有サービスが MSP 側で有効）を昇順で返す。
function findDuplicateOwnership(mspEnabled, astOwned) {
  return [...astOwned].filter((s) => mspEnabled.has(s)).sort();
}

// --- 実ファイル突合 -----------------------------------------------------------

function readIfExists(relPath) {
  const abs = path.join(REPO_ROOT, relPath);
  return fs.existsSync(abs) ? fs.readFileSync(abs, 'utf8') : null;
}

// AST 所有サービス一覧を返す。submodule 未取得ならフォールバックへ縮退する。
function astOwnedServices() {
  const text = readIfExists(AST_VALUES);
  if (text === null) return { services: AST_OWNED_FALLBACK, source: 'fallback' };
  const keys = parseServiceKeys(text);
  if (keys.length === 0) return { services: AST_OWNED_FALLBACK, source: 'fallback' };
  return { services: keys, source: AST_VALUES };
}

function checkTree() {
  const base = readIfExists(MSP_VALUES);
  const local = readIfExists(MSP_VALUES_LOCAL);
  if (base === null) throw new Error(`${MSP_VALUES} が見つかりません。`);
  const enabled = effectiveEnabled(base, local === null ? '' : local);
  return findDuplicateOwnership(enabled, astOwnedServices().services);
}

// --- 自己試験 ----------------------------------------------------------------

function selfTest() {
  const MSP = [
    'services:',
    '  document:',
    '    enabled: true',
    '  # AST 所有（本番像は fail-safe 既定で無効）',
    '  risk-management:',
    '    enabled: false',
    '    resources:',
    '      requests: { cpu: 100m }',
    '  market-monitor:',
    '    enabled: false',
    'networkPolicy:',
    '  enabled: true',
  ].join('\n');
  const AST = ['services:', '  risk-management:', '    db: risk_management_svc', '  market-monitor:', '    db: market_monitor_svc'].join('\n');
  // 経路B が datasource の深い階層に enabled を持つ実構成の再現（誤検出しないこと）。
  const NESTED = ['services:', '  datasource:', '    dataSourceSync:', '      enabled: true'].join('\n');

  const cases = [
    ['services 直下キーのみ抽出', () => JSON.stringify(parseServiceKeys(MSP)) === JSON.stringify(['document', 'risk-management', 'market-monitor'])],
    ['services 無しは空', () => parseServiceKeys('global:\n  x: 1\n').length === 0],
    ['enabled の明示値を拾う', () => parseEnabledFlags(MSP).get('document') === true && parseEnabledFlags(MSP).get('risk-management') === false],
    ['enabled 未指定は欠落', () => !parseEnabledFlags(AST).has('risk-management')],
    ['深い階層の enabled を拾わない', () => !parseEnabledFlags(NESTED).has('datasource')],
    ['top-level networkPolicy.enabled を拾わない', () => !parseEnabledFlags(MSP).has('networkPolicy')],
    ['上書きの true が基底の false に勝つ', () => effectiveEnabled(MSP, 'services:\n  risk-management:\n    enabled: true\n').has('risk-management')],
    ['上書きが enabled を書かなければ基底が残る', () => !effectiveEnabled(MSP, 'services:\n  risk-management:\n    extraEnv: []\n').has('risk-management')],
    ['上書き側だけのサービスも評価対象', () => effectiveEnabled(MSP, 'services:\n  newcomer:\n    enabled: true\n').has('newcomer')],
    ['重複を検出する（#407 の回帰）', () => JSON.stringify(findDuplicateOwnership(effectiveEnabled(MSP, 'services:\n  risk-management:\n    enabled: true\n  market-monitor:\n    enabled: true\n'), parseServiceKeys(AST))) === JSON.stringify(['market-monitor', 'risk-management'])],
    ['fail-safe 既定なら違反ゼロ', () => findDuplicateOwnership(effectiveEnabled(MSP, ''), parseServiceKeys(AST)).length === 0],
    ['MSP 固有サービスは違反にならない', () => findDuplicateOwnership(new Set(['document', 'wiki', 'bff']), AST_OWNED_FALLBACK).length === 0],
  ];

  let failed = 0;
  for (const [name, fn] of cases) {
    let pass = false;
    try { pass = fn() === true; } catch (e) { pass = false; }
    if (!pass) { failed++; console.error(`  ✗ ${name}`); }
  }
  if (failed) {
    console.error(`[check-unit-service-ownership] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-unit-service-ownership] 自己試験 ${cases.length} 件 OK。`);
}

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }
  const owned = astOwnedServices();
  if (owned.source === 'fallback') {
    console.warn(`[check-unit-service-ownership] 注意: ${AST_VALUES} を読めないため既知の一覧へ縮退しました（submodule 未取得）。`);
  } else {
    const drift = owned.services.filter((s) => !AST_OWNED_FALLBACK.includes(s));
    if (drift.length > 0) {
      console.warn(`[check-unit-service-ownership] 注意: AST_OWNED_FALLBACK が古くなっています（未収載: ${drift.join(', ')}）。submodule 未取得時の検査精度を保つため追随してください。`);
    }
  }
  const violations = checkTree();
  if (violations.length === 0) {
    console.log(`[check-unit-service-ownership] OK: AST 所有サービスの重複デプロイはありません（所有一覧: ${owned.source}）。`);
    process.exit(0);
  }
  console.error(`[check-unit-service-ownership] 重複デプロイ ${violations.length} 件を検出しました:`);
  for (const s of violations) {
    console.error(`\n  [重複] services.${s}`);
    console.error(`    AST chart（${AST_VALUES}）が ai-stock-trading namespace へデプロイする所有サービスです。`);
    console.error(`    MSP 側（${MSP_VALUES} ＋ ${MSP_VALUES_LOCAL}）の実効 enabled が true になっています。`);
  }
  console.error('\n同一サービスが 2 つの namespace に並ぶと、共有 RabbitMQ の同名キューを奪い合い（Issue #407）、');
  console.error('共有 DB への二重 writer になります。MSP 側は enabled: false（fail-safe 既定）に戻し、');
  console.error('BFF からの到達は deploy/local/aliases/ の ExternalName alias で AST namespace へ向けてください。');
  console.error('根拠は docs/adr/IADR-0107_ast-owned-service-single-deployment.md を参照してください。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  parseServiceKeys,
  parseEnabledFlags,
  effectiveEnabled,
  findDuplicateOwnership,
  astOwnedServices,
  checkTree,
  AST_OWNED_FALLBACK,
};
