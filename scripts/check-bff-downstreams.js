#!/usr/bin/env node
'use strict';
/*
 * check-bff-downstreams.js
 * BFF（Platform.Bff）が集約する各 downstream の「実効の上流 URL」が、デプロイ manifest で
 * メッシュ規約のポート（8080）に解決されることを機械検査する（FR-01/FR-02 ほか, IADR-0089 / Issue #342）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。check-image-mapping.js / check-unit-dependencies.js と同型。
 *
 * 背景（Issue #342）: BFF は named HttpClient の BaseAddress を
 *   Configuration["Services:<Name>"] ?? "http://<host>:<port>"（コード既定）で決める。
 * コード既定は「ローカル開発ポート（5001-5009）」で、各デプロイ manifest（Helm values の bff.extraEnv /
 * docker-compose.yml の bff env）が実 Service ポート :8080 へ上書きする、というのが確立パターン。
 * DataSourceService だけがこの上書きから漏れ、コード既定 5002 のまま live へ出て 21 秒タイムアウト→502 を
 * 引き起こした。本スクリプトは「downstream ごとの実効 URL（override ?? default）が :8080 か」を突合し、
 * 同種の再ドリフト（上書き漏れ・ポート違い）を CI で止める。
 *
 * 不変条件: BFF の全 downstream named client について、各デプロイ manifest での実効ポート == 8080。
 *   実効 URL = manifest の Services__<Name> 上書き（あれば）、無ければ Program.cs のコード既定。
 * AST 系（Configuration/RiskManagement/MarketMonitor）や conversion はコード既定が既に :8080 のため
 * 上書き無しでも合格する（過検出しない）。
 *
 * 使い方:
 *   node scripts/check-bff-downstreams.js             # 実ファイルを突合。ドリフトがあれば終了コード 1。
 *   node scripts/check-bff-downstreams.js --self-test # 検査ロジック自体の自己試験。
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const PROGRAM_PATH = 'src/platform/backend/Bff/Platform.Bff/Program.cs';

/**
 * 検査対象の「呼び出し元」（#958 で BFF 1 件から拡張）。
 *
 * 🔴 **母集合が Platform.Bff/Program.cs の 1 ファイルに限定されていたため、サービス間
 * （service → service）の named client を誰も見ていなかった。** その死角で WikiService と
 * AiAnalysisService が AuthorizationService を :5005 で呼んでいた —— #342 / IADR-0089 と
 * まったく同じ形の 2 回目である（CLAUDE.md「同型の事故が 2 回起きたら」）。
 *
 * 🔴 **compose と helm でサービスキーの綴りが違う**（`wiki-service` / `wiki`）。両方を持つ。
 * 🔴 **上書きの探索は必ず「呼び出し元のブロック内」で行う。** 全文検索にすると
 * **BFF 用の上書きを呼び出し元の上書きと取り違えて「違反 0 件」と誤答する**
 * （本 issue の調査中に実際に 1 度誤答した）。extractServiceBlock を必ず通すこと。
 */
const CALLERS = [
  {
    label: 'Platform.Bff',
    program: PROGRAM_PATH,
    compose: 'bff',
    helm: 'bff',
  },
  {
    label: 'AiAnalysisService',
    program: 'src/knowledge/backend/Services/AiAnalysisService/Program.cs',
    compose: 'aianalysis-service',
    helm: 'aianalysis',
  },
  {
    label: 'GraphService',
    program: 'src/knowledge/backend/Services/GraphService/Program.cs',
    compose: 'graph-service',
    helm: 'graph',
  },
  {
    label: 'WikiService',
    program: 'src/knowledge/backend/Services/WikiService/Program.cs',
    compose: 'wiki-service',
    helm: 'wiki',
  },
  // #970: 二段検索の段（グラフ近傍展開）で RetrievalService → GraphService の
  // service → service 呼び出し元になった。コード既定 :8080（後発サービスの規約）のため
  // manifest の上書きは不要だが、CALLERS に無いとドリフトを誰も見ない（#958 と同じ死角）。
  {
    label: 'RetrievalService',
    program: 'src/knowledge/backend/Services/RetrievalService/Program.cs',
    compose: 'retrieval-service',
    helm: 'retrieval',
  },
];
const VALUES_PATH = 'deploy/helm/microservices-platform/values.yaml';
const COMPOSE_PATH = 'deploy/docker-compose.yml';
const EXPECTED_PORT = 8080; // メッシュ内の全 downstream Service の実ポート（k8s values services.*.port / compose expose）。

// --- 純粋ロジック（自己試験する） --------------------------------------------

// Program.cs から named HttpClient の { name -> defaultUrl } を抽出する。
// 形: builder.Services.AddHttpClient("<Name>", c => ... ?? "http://host:port");
// 名前無しの AddHttpClient()（既定ファクトリ登録）は対象外（"name", を要求）。
function parseProgramDefaults(csText) {
  const map = new Map();
  const re = /AddHttpClient\(\s*"([^"]+)"\s*,\s*c\s*=>[\s\S]*?\?\?\s*"([^"]+)"/g;
  let m;
  while ((m = re.exec(String(csText)))) {
    map.set(m[1], m[2]);
  }
  return map;
}

// YAML/compose テキストから、top-level `services:` 直下の指定サービス（例 bff）ブロックの本文行を返す。
// サービスキーより深いインデントの行のみを収集し、同じ/浅いインデントの兄弟キーで打ち切る（再フォーマット耐性）。
function extractServiceBlock(yamlText, serviceName) {
  const lines = String(yamlText).split('\n').map((l) => l.replace(/\r$/, ''));
  let inServices = false;
  let serviceIndent = null; // services 直下キーのインデント。
  let keyIndent = null; // 対象サービスキーのインデント（block 収集の基準）。
  const block = [];
  for (const line of lines) {
    if (line.trim() === '') { if (keyIndent !== null) block.push(line); continue; }
    const indent = line.length - line.trimStart().length;
    if (indent === 0) {
      // 列 0 のトップレベルキー。services ブロックの開始/終了を切り替える。
      if (keyIndent !== null) return block; // 対象ブロック収集中に列 0 が来たら終了。
      inServices = /^services:\s*$/.test(line);
      serviceIndent = null;
      continue;
    }
    if (!inServices) continue;
    if (serviceIndent === null) serviceIndent = indent; // services 直下の最初のキーで確定。
    if (keyIndent !== null) {
      if (indent > keyIndent) { block.push(line); continue; }
      return block; // 兄弟/親インデント → 対象サービスブロック終了。
    }
    // まだ対象サービスに入っていない。services 直下でキー名が一致したら収集開始。
    if (indent === serviceIndent) {
      const name = line.trim().replace(/:\s*(#.*)?$/, '');
      if (name === serviceName) keyIndent = indent;
    }
  }
  return block;
}

// Helm values の bff ブロック本文（extractServiceBlock 出力）から Services__<Name> 上書き { name -> url } を抽出する。
// extraEnv はリスト: `- name: Services__X` の次以降に `value: "URL"`。
function parseHelmServicesEnv(blockLines) {
  const map = new Map();
  let pendingName = null;
  for (const raw of blockLines) {
    const body = raw.trim().replace(/\s+#.*$/, '');
    const nm = body.match(/^-?\s*name:\s*Services__(\S+)\s*$/);
    if (nm) { pendingName = nm[1]; continue; }
    if (pendingName) {
      const vm = body.match(/^value:\s*["']?([^"'#\s]+)["']?\s*$/);
      if (vm) { map.set(pendingName, vm[1]); pendingName = null; }
    }
  }
  return map;
}

// compose の bff ブロック本文から Services__<Name> 上書き { name -> url } を抽出する。
// environment はマップ: `Services__X: URL`（インライン/引用を許容）。
function parseComposeServicesEnv(blockLines) {
  const map = new Map();
  for (const raw of blockLines) {
    const body = raw.trim().replace(/\s+#.*$/, '');
    const em = body.match(/^Services__(\S+):\s*["']?([^"'#\s]+)["']?\s*$/);
    if (em) map.set(em[1], em[2]);
  }
  return map;
}

// URL からポート番号を返す（明示ポート必須。無ければ null）。
function portOf(url) {
  const m = String(url).match(/^[a-z]+:\/\/[^/:]+:(\d+)/i);
  return m ? Number(m[1]) : null;
}

// downstream 既定と manifest 上書きから、各 downstream の実効ポートを突合し違反 { env, name, detail } を返す。
function computeViolations({ defaults, overridesByEnv, expectedPort = EXPECTED_PORT }) {
  const violations = [];
  for (const [env, overrides] of Object.entries(overridesByEnv)) {
    for (const [name, defUrl] of defaults) {
      const effective = overrides.has(name) ? overrides.get(name) : defUrl;
      const port = portOf(effective);
      if (port !== expectedPort) {
        violations.push({
          env,
          name,
          detail:
            `[${env}] downstream '${name}' の実効上流が ${effective}（ポート ${port ?? '不明'}）で、` +
            `期待ポート ${expectedPort} と不一致。` +
            (overrides.has(name)
              ? ` manifest の Services__${name} 上書きを http://<host>:${expectedPort} に是正してください。`
              : ` Program.cs 既定が :${expectedPort} でないため、manifest の当該サービスの env に ` +
                `Services__${name}: http://<host>:${expectedPort} を追加してください` +
                `（env ラベルの先頭が呼び出し元です）。`),
        });
      }
    }
  }
  return violations;
}

// --- ファイル読取・実行 -------------------------------------------------------

function readRepoFile(relPath) {
  return fs.readFileSync(path.join(REPO_ROOT, relPath), 'utf8');
}

function checkTree() {
  const valuesText = readRepoFile(VALUES_PATH);
  const composeText = readRepoFile(COMPOSE_PATH);
  const violations = [];
  let totalDownstreams = 0;

  for (const caller of CALLERS) {
    const defaults = parseProgramDefaults(readRepoFile(caller.program));
    if (defaults.size === 0) {
      violations.push({
        env: '-', name: '-',
        detail: `${caller.program} から named HttpClient の既定を導出できなかった（パーサまたはコードの破綻）`,
      });
      continue;
    }
    totalDownstreams += defaults.size;
    // 🔴 上書きは**呼び出し元のブロック内**でのみ探す（全文検索にしない）。
    const helm = parseHelmServicesEnv(extractServiceBlock(valuesText, caller.helm));
    const compose = parseComposeServicesEnv(extractServiceBlock(composeText, caller.compose));
    violations.push(...computeViolations({
      defaults,
      overridesByEnv: {
        [`${caller.label} / helm values.yaml`]: helm,
        [`${caller.label} / docker-compose.yml`]: compose,
      },
    }));
  }

  // 0 件走査で静かに緑にしない（#664 の門）。呼び出し元が全部空振りしたら走査が壊れている。
  if (totalDownstreams === 0) {
    violations.push({
      env: '-', name: '-',
      detail: `呼び出し元 ${CALLERS.length} 件のどこからも named HttpClient を導出できなかった（走査の破綻）`,
    });
  }
  return violations;
}

// --- 自己試験（--self-test） --------------------------------------------------

function selfTest() {
  const cases = [];
  const expect = (name, pass, actual, expected) => cases.push({ name, pass, actual, expected });

  // parseProgramDefaults: 複数行にまたがる AddHttpClient から name/既定 URL を抽出する。
  const csFixture = [
    'builder.Services.AddHttpClient();',
    'builder.Services.AddHttpClient("DashboardService", c =>',
    '    c.BaseAddress = new Uri(builder.Configuration["Services:DashboardService"]',
    '        ?? "http://dashboard-service:5009"));',
    'builder.Services.AddHttpClient("DataSourceService", c =>',
    '    c.BaseAddress = new Uri(builder.Configuration["Services:DataSourceService"]',
    '        ?? "http://datasource-service:5002"));',
    'builder.Services.AddHttpClient("ConfigurationService", c =>',
    '    c.BaseAddress = new Uri(builder.Configuration["Services:ConfigurationService"]',
    '        ?? "http://configuration-service:8080"));',
  ].join('\n');
  const defs = parseProgramDefaults(csFixture);
  expect('program: 名前付き 3 件を抽出（無名は除外）', defs.size === 3, [...defs.keys()]);
  expect('program: DataSource の既定 URL を抽出', defs.get('DataSourceService') === 'http://datasource-service:5002', defs.get('DataSourceService'));

  // extractServiceBlock + parseHelmServicesEnv: bff の extraEnv から Services__ を抽出。
  const valuesFixture = [
    'services:',
    '  datasource:',
    '    port: 8080',
    '  bff:',
    '    extraEnv:',
    '      - name: Services__DashboardService',
    '        value: "http://dashboard-service:8080"',
    '      - name: Services__DataSourceService',
    '        value: "http://datasource-service:8080"',
    '  frontend:',
    '    port: 8080',
    'minio:',
    '  enabled: true',
  ].join('\n');
  const helm = parseHelmServicesEnv(extractServiceBlock(valuesFixture, 'bff'));
  expect('helm: bff の Services__ 2 件のみ抽出（他サービス混入なし）', helm.size === 2, [...helm.keys()]);
  expect('helm: DataSource 上書き URL を抽出', helm.get('DataSourceService') === 'http://datasource-service:8080', helm.get('DataSourceService'));

  // インデント幅が違っても抽出できる（再フォーマット耐性）。
  const valuesReindented = [
    'services:',
    '    bff:',
    '        extraEnv:',
    '            - name: Services__DataSourceService',
    '              value: http://datasource-service:8080',
    '    frontend: {}',
  ].join('\n');
  const helmRe = parseHelmServicesEnv(extractServiceBlock(valuesReindented, 'bff'));
  expect('helm: インデント違いでも抽出', helmRe.get('DataSourceService') === 'http://datasource-service:8080', [...helmRe]);

  // compose: bff の environment マップから Services__ を抽出。別サービスの Services__ は混入しない。
  const composeFixture = [
    'services:',
    '  retrieval-service:',
    '    environment:',
    '      Services__RetrievalService: http://retrieval-service:8080', // 別サービス（bff でない）→ 混入しない
    '  bff:',
    '    environment:',
    '      Services__DashboardService: http://dashboard-service:8080',
    '      Services__DataSourceService: http://datasource-service:8080',
    '  postgres:',
    '    image: postgres:16',
  ].join('\n');
  const compose = parseComposeServicesEnv(extractServiceBlock(composeFixture, 'bff'));
  expect('compose: bff の Services__ 2 件のみ抽出（別サービス除外）', compose.size === 2, [...compose.keys()]);
  expect('compose: DataSource 上書き URL を抽出', compose.get('DataSourceService') === 'http://datasource-service:8080', compose.get('DataSourceService'));

  // computeViolations: 上書き漏れ（datasource 既定 5002・上書き無し）→ 違反。
  const defaults = new Map([
    ['DashboardService', 'http://dashboard-service:5009'],
    ['DataSourceService', 'http://datasource-service:5002'],
    ['ConfigurationService', 'http://configuration-service:8080'],
  ]);
  const missOverrides = new Map([['DashboardService', 'http://dashboard-service:8080']]);
  const missV = computeViolations({ defaults, overridesByEnv: { env1: missOverrides } });
  expect('drift: 上書き漏れの DataSource（実効 5002）を検出', missV.some((v) => v.name === 'DataSourceService'), missV);
  expect('drift: 既定 8080 の Configuration は上書き無しでも合格', !missV.some((v) => v.name === 'ConfigurationService'), missV);
  expect('drift: 8080 上書き済みの Dashboard は合格', !missV.some((v) => v.name === 'DashboardService'), missV);

  // computeViolations: 全 downstream 実効 8080 → 違反 0。
  const okOverrides = new Map([
    ['DashboardService', 'http://dashboard-service:8080'],
    ['DataSourceService', 'http://datasource-service:8080'],
  ]);
  const okV = computeViolations({ defaults, overridesByEnv: { env1: okOverrides } });
  expect('drift: 全 downstream 実効 8080 は違反 0', okV.length === 0, okV);

  // computeViolations: 上書きがポート違い（8081）→ 違反。
  const wrongV = computeViolations({ defaults, overridesByEnv: { env1: new Map([['DataSourceService', 'http://datasource-service:8081']]) } });
  expect('drift: 上書きのポート違い（8081）を検出', wrongV.some((v) => v.name === 'DataSourceService'), wrongV);

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; console.error('    actual:', JSON.stringify(c.actual), c.expected !== undefined ? `expected: ${JSON.stringify(c.expected)}` : ''); }
  }
  if (failed) { console.error(`[check-bff-downstreams] 自己試験 ${failed} 件 失敗。`); process.exit(1); }
  console.log(`[check-bff-downstreams] 自己試験 ${cases.length} 件 OK。`);
}

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }
  const violations = checkTree();
  if (violations.length === 0) {
    console.log(`[check-bff-downstreams] OK: 呼び出し元 ${CALLERS.length} 件の全 downstream の実効上流が :${EXPECTED_PORT} に解決されています（ドリフト 0）。`);
    process.exit(0);
  }
  console.error(`[check-bff-downstreams] BFF downstream の上流ポートに ${violations.length} 件のドリフトを検出しました:`);
  for (const v of violations) console.error(`\n  [${v.env}] ${v.name}: ${v.detail}`);
  console.error('\n設計の根拠は .ai-context/adr/IADR-0089_bff-datasource-upstream-port.md を参照。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  EXPECTED_PORT,
  parseProgramDefaults,
  extractServiceBlock,
  parseHelmServicesEnv,
  parseComposeServicesEnv,
  portOf,
  computeViolations,
  checkTree,
};
