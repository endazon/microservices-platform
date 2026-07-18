#!/usr/bin/env node
'use strict';
/*
 * check-image-mapping.js
 * scripts/k8s-local-images.sh の MAPPING（chart-image ↔ Dockerfile）と deploy/docker-compose.yml の
 * build 定義（サービス ↔ Dockerfile）の対応を機械検査する（NFR 運用・保守, ADR-0007, IADR-0068 / Issue #275）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。check-unit-dependencies.js / validate-pipeline-config.js と同型。
 *
 * 背景: images.yml（#268 / IADR-0067）は compose を単一情報源にイメージ「ビルド可否」を担保するが、
 * k8s-local-images.sh の MAPPING は別のビルド対象リストで、両者のドリフトは検査されていなかった。
 * 本スクリプトは「対応表の整合」のみを見る（ビルド可否は images.yml が担う。IADR-0068 参照）。
 *
 * frontend は k8s チャート非デプロイの compose 専用ビルド対象のため COMPOSE_ONLY で明示除外し、
 * 除外リスト自体の腐り（対象消失・MAPPING への二重掲載）も検査する（IADR-0068）。
 *
 * 使い方:
 *   node scripts/check-image-mapping.js             # 実ファイルを突合。ドリフトがあれば終了コード 1。
 *   node scripts/check-image-mapping.js --self-test # 検査ロジック自体の自己試験。
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const COMPOSE_PATH = 'deploy/docker-compose.yml';
const SCRIPT_PATH = 'scripts/k8s-local-images.sh';

// chart-image の接頭辞（deploy/helm/.../values.yaml・deploy/local/values-local.yaml と一致）。
const IMAGE_PREFIX = 'microservices-platform';
// k8s チャート非デプロイの compose 専用ビルド対象（IADR-0068）。値は compose のサービス名。
// 将来 k8s に載せる場合はここから外し、MAPPING＋Helm values.services＋deployment を追加する。
const COMPOSE_ONLY = ['frontend'];

// --- 純粋ロジック（scripts.test.js から単体テストする） -------------------------

// deploy/docker-compose.yml のテキストから、build 定義を持つサービスの { service, dockerfile } を返す。
// build.dockerfile の「リテラル値」のみを抽出する（補間・アンカーは dockerfile リテラルに影響しない。
// 実ビルドはせず対応表の突合に用いるため、限定テキスト解析で足りる。IADR-0068 参照）。
// ネスト判定はインデント「幅」を固定値に決め打ちせず相対比較で行う（再フォーマット耐性）。
function parseComposeBuildTargets(yamlText) {
  const targets = [];
  const KEY = /^[A-Za-z0-9._-]+:\s*$/; // 値を持たないブロックマッピングのキー行。
  let inServices = false; // top-level `services:` ブロック内か。
  let serviceIndent = null; // サービスキー（services 直下）のインデント幅。
  let currentService = null; // 直近のサービスキー。
  let buildIndent = null; // 直近サービスの `build:` のインデント幅（null = build ブロック外）。
  for (const raw of String(yamlText).split('\n')) {
    const line = raw.replace(/\r$/, '');
    if (line.trim() === '' || /^\s*#/.test(line)) continue; // コメント・空行は状態を変えない。
    const indent = line.length - line.trimStart().length;
    const body = line.trim();
    if (indent === 0) {
      // 列 0 のキー（services / volumes / x-*）。services ブロックの開始/終了を切り替える。
      inServices = /^services:\s*$/.test(line);
      currentService = null;
      buildIndent = null;
      serviceIndent = null;
      continue;
    }
    if (!inServices) continue;
    // services 直下（最も浅い 2 段目）のインデントを最初のキーで確定する。
    if (serviceIndent === null && KEY.test(body)) serviceIndent = indent;
    if (indent === serviceIndent && KEY.test(body)) {
      currentService = body.replace(/:\s*$/, '');
      buildIndent = null;
      continue;
    }
    if (!currentService) continue;
    // build より浅い/同じインデントのキーが来たら build ブロックを抜ける。
    if (buildIndent !== null && indent <= buildIndent) buildIndent = null;
    if (indent > serviceIndent && /^build:\s*$/.test(body)) {
      buildIndent = indent;
      continue;
    }
    if (buildIndent !== null && indent > buildIndent) {
      const m = body.match(/^dockerfile:\s*(\S+)\s*$/);
      if (m) targets.push({ service: currentService, dockerfile: m[1] });
    }
  }
  return targets;
}

// scripts/k8s-local-images.sh のテキストから MAPPING=( ... ) の { image, dockerfile } を返す。
function parseMappingEntries(bashText) {
  const text = String(bashText);
  const start = text.indexOf('MAPPING=(');
  if (start < 0) return [];
  const rest = text.slice(start);
  const endRel = rest.search(/\n\)/); // 行頭の閉じ括弧。
  const block = endRel >= 0 ? rest.slice(0, endRel) : rest;
  const out = [];
  const re = /"([^"|]+)\|([^"]+)"/g;
  let m;
  while ((m = re.exec(block))) {
    out.push({ image: m[1].trim(), dockerfile: m[2].trim() });
  }
  return out;
}

// MAPPING と compose build 定義のドリフトを検査し、違反 { kind, detail } の配列を返す。
function computeDrift({
  mappingEntries,
  composeTargets,
  composeOnly = COMPOSE_ONLY,
  imagePrefix = IMAGE_PREFIX,
}) {
  const violations = [];
  const onlySet = new Set(composeOnly);
  const prefix = `${imagePrefix}/`;
  const composeByService = new Map(composeTargets.map((t) => [t.service, t.dockerfile]));
  const mappingByImage = new Map(mappingEntries.map((e) => [e.image, e.dockerfile]));

  // 5. 除外リストの腐り: COMPOSE_ONLY が compose の build 定義に実在するか。
  for (const svc of composeOnly) {
    if (!composeByService.has(svc)) {
      violations.push({
        kind: 'compose-only-stale',
        detail: `COMPOSE_ONLY の '${svc}' が compose の build 定義に存在しない（除外リストが腐っている）`,
      });
    }
  }

  // 1,3: compose 側から見た MAPPING 欠落・Dockerfile 不一致（除外サービスはスキップ）。
  for (const t of composeTargets) {
    if (onlySet.has(t.service)) continue;
    const expectedImage = `${prefix}${t.service}`;
    if (!mappingByImage.has(expectedImage)) {
      violations.push({
        kind: 'missing-mapping',
        detail: `compose の build 定義 '${t.service}' に対応する MAPPING エントリ（${expectedImage}）が無い`,
      });
      continue;
    }
    const mdf = mappingByImage.get(expectedImage);
    if (mdf !== t.dockerfile) {
      violations.push({
        kind: 'dockerfile-mismatch',
        detail: `'${t.service}' の Dockerfile 不一致: MAPPING='${mdf}' / compose='${t.dockerfile}'`,
      });
    }
  }

  // 2,4,6: MAPPING 側から見た命名不整合・除外の二重掲載・stale。
  for (const e of mappingEntries) {
    if (!e.image.startsWith(prefix)) {
      violations.push({
        kind: 'naming',
        detail: `MAPPING の chart-image '${e.image}' は '${prefix}<compose-service 名>' に一致しない`,
      });
      continue;
    }
    const service = e.image.slice(prefix.length);
    if (onlySet.has(service)) {
      violations.push({
        kind: 'compose-only-in-mapping',
        detail: `compose 専用除外の '${service}' が MAPPING に掲載されている（除外なら MAPPING から外す）`,
      });
      continue;
    }
    if (!composeByService.has(service)) {
      violations.push({
        kind: 'stale-mapping',
        detail: `MAPPING の '${e.image}' に対応する compose の build 定義（サービス '${service}'）が無い（stale）`,
      });
    }
  }

  return violations;
}

// --- ファイル読取・実行 -------------------------------------------------------

function readRepoFile(relPath) {
  return fs.readFileSync(path.join(REPO_ROOT, relPath), 'utf8');
}

function checkTree() {
  const composeTargets = parseComposeBuildTargets(readRepoFile(COMPOSE_PATH));
  const mappingEntries = parseMappingEntries(readRepoFile(SCRIPT_PATH));
  // 導出が空になると「何も検査せず緑」になる。それを失敗として顕在化させる（images.yml と同思想）。
  if (composeTargets.length === 0) {
    return [{ kind: 'empty', detail: `${COMPOSE_PATH} から build 定義を導出できなかった（パーサまたは compose の破綻）` }];
  }
  if (mappingEntries.length === 0) {
    return [{ kind: 'empty', detail: `${SCRIPT_PATH} から MAPPING を導出できなかった（パーサまたはスクリプトの破綻）` }];
  }
  return computeDrift({ mappingEntries, composeTargets });
}

// --- 自己試験（--self-test） --------------------------------------------------

function selfTest() {
  const cases = [];
  const expectCount = (name, actual, expected) =>
    cases.push({ name, pass: actual === expected, actual, expected });

  // パーサ: compose の build 定義抽出。
  const composeFixture = [
    'services:',
    '  document-service:',
    '    build:',
    '      context: ..',
    '      dockerfile: src/a/Dockerfile',
    '    expose:',
    '      - "8080"',
    '  postgres:', // build を持たない（infra）→ 抽出されない。
    '    image: postgres:16-alpine',
    '  frontend:',
    '    build:',
    '      context: ..',
    '      dockerfile: src/platform/frontend/Dockerfile',
    '    ports:',
    '      - "3100:8080"',
    'volumes:',
    '  document-data:', // services ブロック外。誤って拾わない。
    '    build:',
    '      dockerfile: nope/Dockerfile',
    '',
  ].join('\n');
  const parsedCompose = parseComposeBuildTargets(composeFixture);
  expectCount('compose: build を持つ 2 サービスのみ抽出', parsedCompose.length, 2);
  cases.push({
    name: 'compose: document-service の dockerfile を正しく抽出',
    pass: parsedCompose[0] && parsedCompose[0].service === 'document-service' && parsedCompose[0].dockerfile === 'src/a/Dockerfile',
    actual: parsedCompose[0],
  });

  // パーサ: インデント幅が異なっても相対比較で抽出できる（再フォーマット耐性）。
  const composeReindented = [
    'services:',
    '    document-service:', // サービスキーを 4 スペースに
    '        build:',
    '            context: ..',
    '            dockerfile: src/a/Dockerfile',
    '        expose:',
    '            - "8080"',
    '',
  ].join('\n');
  const parsedReindented = parseComposeBuildTargets(composeReindented);
  cases.push({
    name: 'compose: インデント幅が変わっても dockerfile を抽出（相対比較）',
    pass: parsedReindented.length === 1 && parsedReindented[0].dockerfile === 'src/a/Dockerfile',
    actual: parsedReindented,
  });

  // パーサ: MAPPING 抽出（コメント行・閉じ括弧を無視）。
  const bashFixture = [
    '# chart-image : Dockerfile パス',
    'MAPPING=(',
    '  "microservices-platform/document-service|src/a/Dockerfile"',
    '  "microservices-platform/bff|src/b/Dockerfile"',
    ')',
    'echo "microservices-platform/should-not|be-parsed" # 括弧外は無視',
  ].join('\n');
  const parsedMapping = parseMappingEntries(bashFixture);
  expectCount('MAPPING: 括弧内の 2 件のみ抽出', parsedMapping.length, 2);

  // computeDrift: 整合ケース（違反 0）。
  const okCompose = [
    { service: 'document-service', dockerfile: 'src/a/Dockerfile' },
    { service: 'frontend', dockerfile: 'src/platform/frontend/Dockerfile' },
  ];
  const okMapping = [{ image: 'microservices-platform/document-service', dockerfile: 'src/a/Dockerfile' }];
  expectCount('drift: 整合（frontend 除外）は違反 0', computeDrift({ mappingEntries: okMapping, composeTargets: okCompose }).length, 0);

  // computeDrift: MAPPING 欠落。
  const missCompose = [
    { service: 'document-service', dockerfile: 'src/a/Dockerfile' },
    { service: 'new-service', dockerfile: 'src/n/Dockerfile' },
  ];
  const missV = computeDrift({ mappingEntries: okMapping, composeTargets: missCompose });
  cases.push({ name: 'drift: 新サービスの MAPPING 欠落を検出', pass: missV.some((v) => v.kind === 'missing-mapping'), actual: missV });

  // computeDrift: Dockerfile 不一致。
  const mismatchV = computeDrift({
    mappingEntries: [{ image: 'microservices-platform/document-service', dockerfile: 'src/OLD/Dockerfile' }],
    composeTargets: okCompose,
  });
  cases.push({ name: 'drift: Dockerfile 不一致を検出', pass: mismatchV.some((v) => v.kind === 'dockerfile-mismatch'), actual: mismatchV });

  // computeDrift: MAPPING の stale エントリ。
  const staleV = computeDrift({
    mappingEntries: [
      { image: 'microservices-platform/document-service', dockerfile: 'src/a/Dockerfile' },
      { image: 'microservices-platform/removed-service', dockerfile: 'src/x/Dockerfile' },
    ],
    composeTargets: okCompose,
  });
  cases.push({ name: 'drift: stale な MAPPING エントリを検出', pass: staleV.some((v) => v.kind === 'stale-mapping'), actual: staleV });

  // computeDrift: 命名不整合（接頭辞違い）。
  const nameV = computeDrift({
    mappingEntries: [{ image: 'wrong-prefix/document-service', dockerfile: 'src/a/Dockerfile' }],
    composeTargets: okCompose,
  });
  cases.push({ name: 'drift: chart-image の接頭辞違いを検出', pass: nameV.some((v) => v.kind === 'naming'), actual: nameV });

  // computeDrift: 除外の二重掲載（frontend を MAPPING に載せた）。
  const dupV = computeDrift({
    mappingEntries: [
      { image: 'microservices-platform/document-service', dockerfile: 'src/a/Dockerfile' },
      { image: 'microservices-platform/frontend', dockerfile: 'src/platform/frontend/Dockerfile' },
    ],
    composeTargets: okCompose,
  });
  cases.push({ name: 'drift: compose 専用除外の二重掲載を検出', pass: dupV.some((v) => v.kind === 'compose-only-in-mapping'), actual: dupV });

  // computeDrift: 除外リストの腐り（除外対象が compose から消えた）。
  const rotV = computeDrift({
    mappingEntries: okMapping,
    composeTargets: [{ service: 'document-service', dockerfile: 'src/a/Dockerfile' }], // frontend 無し
  });
  cases.push({ name: 'drift: 除外リストの腐り（対象消失）を検出', pass: rotV.some((v) => v.kind === 'compose-only-stale'), actual: rotV });

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) {
      failed++;
      console.error('    actual:', JSON.stringify(c.actual), c.expected !== undefined ? `expected: ${c.expected}` : '');
    }
  }
  if (failed) {
    console.error(`[check-image-mapping] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-image-mapping] 自己試験 ${cases.length} 件 OK。`);
}

function main() {
  if (process.argv.includes('--self-test')) {
    selfTest();
    return;
  }
  const violations = checkTree();
  if (violations.length === 0) {
    console.log('[check-image-mapping] OK: MAPPING と compose の build 定義は整合しています（ドリフト 0）。');
    process.exit(0);
  }
  console.error(`[check-image-mapping] MAPPING と compose の build 定義に ${violations.length} 件のドリフトを検出しました:`);
  for (const v of violations) {
    console.error(`\n  [${v.kind}] ${v.detail}`);
  }
  console.error(
    `\n${SCRIPT_PATH} の MAPPING と ${COMPOSE_PATH} の build 定義を突き合わせてください。` +
      '\n設計の根拠は docs/adr/IADR-0068_image-mapping-drift-check.md を参照。',
  );
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  IMAGE_PREFIX,
  COMPOSE_ONLY,
  parseComposeBuildTargets,
  parseMappingEntries,
  computeDrift,
  checkTree,
};
