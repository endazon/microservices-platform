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
 * compose 専用（k8s 非デプロイ）のビルド対象は COMPOSE_ONLY で明示除外でき、除外リスト自体の腐り
 * （対象消失・MAPPING への二重掲載）も検査する（IADR-0068）。#313/IADR-0078 で frontend を k8s 化した
 * ため現在 COMPOSE_ONLY は空。除外機構は将来の compose 専用対象に備えて残す。
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
// 将来 k8s に載せる場合はここから外し、MAPPING＋Helm values＋deployment を追加する。
// #313 / IADR-0078: frontend を k8s chart 配信（templates/frontend.yaml＋values の frontend ブロック＋
// MAPPING）へ移行したため除外を解消した。現在 compose 専用の対象は無い（空）。除外機構自体は残す
// （将来の compose 専用ツール等に備える）。自己試験は composeOnly を明示引数化して機構を検証する。
const COMPOSE_ONLY = [];

// --- 純粋ロジック（scripts.test.js から単体テストする） -------------------------

// deploy/docker-compose.yml のテキストから、build 定義を持つサービスの { service, dockerfile } を返す。
// build.dockerfile の「リテラル値」のみを抽出する（補間・アンカーは dockerfile リテラルに影響しない。
// 実ビルドはせず対応表の突合に用いるため、限定テキスト解析で足りる。IADR-0068 参照）。
// ネスト判定はインデント「幅」を固定値に決め打ちせず相対比較で行う（再フォーマット耐性）。
// 行末の YAML インラインコメント（` # ...`）は除去してからキー判定する（`build:  # foo` 等で
// build ブロックを取り逃さないため）。build 定義を YAML マージキー（`<<: *anchor`）で継承する形は
// 非対応（現 compose では `<<:` は environment 内のみ。将来 build を anchor 化する場合は要拡張）。
// #283 / IADR-0070: build 定義から dockerfile に加え context・build args も抽出する。
// AST のような「単一パラメータ化 Dockerfile＋ユニットルート context」を突合できるようにするため。
// context 省略時は '.'（compose の既定＝compose ファイルのあるディレクトリ。実運用では全サービスが明示）。
function parseComposeBuildTargets(yamlText) {
  const targets = [];
  const KEY = /^[A-Za-z0-9._-]+:\s*$/; // 値を持たないブロックマッピングのキー行。
  let inServices = false; // top-level `services:` ブロック内か。
  let serviceIndent = null; // サービスキー（services 直下）のインデント幅。
  let currentService = null; // 直近のサービスキー。
  let buildIndent = null; // 直近サービスの `build:` のインデント幅（null = build ブロック外）。
  let argsIndent = null; // build 内 `args:` のインデント幅（null = args ブロック外）。
  let current = null; // 構築中の build 定義（{ service, context, dockerfile, args }）。

  // build 定義（dockerfile を持つもののみ）を確定して push する。
  const flush = () => {
    if (current && current.dockerfile) targets.push(current);
    current = null;
    argsIndent = null;
  };

  for (const raw of String(yamlText).split('\n')) {
    const line = raw.replace(/\r$/, '');
    if (line.trim() === '' || /^\s*#/.test(line)) continue; // コメント・空行は状態を変えない。
    const indent = line.length - line.trimStart().length;
    // インラインコメント（空白 + #）を除去（YAML はインライン # の前に空白が要る）。
    const body = line.trim().replace(/\s+#.*$/, '');
    if (indent === 0) {
      // 列 0 のキー（services / volumes / x-*）。services ブロックの開始/終了を切り替える。
      flush();
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
      flush();
      currentService = body.replace(/:\s*$/, '');
      buildIndent = null;
      continue;
    }
    if (!currentService) continue;
    // build より浅い/同じインデントのキーが来たら build ブロックを抜ける（確定）。
    if (buildIndent !== null && indent <= buildIndent) {
      flush();
      buildIndent = null;
    }
    if (indent > serviceIndent && /^build:\s*$/.test(body)) {
      buildIndent = indent;
      current = { service: currentService, context: '.', dockerfile: null, args: {} };
      argsIndent = null;
      continue;
    }
    if (buildIndent !== null && indent > buildIndent && current) {
      // args サブブロックを抜けたか。
      if (argsIndent !== null && indent <= argsIndent) argsIndent = null;
      if (argsIndent === null && /^args:\s*$/.test(body)) {
        argsIndent = indent;
        continue;
      }
      if (argsIndent !== null && indent > argsIndent) {
        // build args（マップ形式 KEY: VALUE のみ対応。シーケンス形式 "- K=V" は非対応）。
        const am = body.match(/^([A-Za-z0-9._-]+):\s*(\S.*?)\s*$/);
        if (am) current.args[am[1]] = am[2];
        continue;
      }
      const dm = body.match(/^dockerfile:\s*(\S+)\s*$/);
      if (dm) {
        current.dockerfile = dm[1];
        continue;
      }
      const cm = body.match(/^context:\s*(\S+)\s*$/);
      if (cm) {
        current.context = cm[1];
        continue;
      }
    }
  }
  flush();
  return targets;
}

// scripts/k8s-local-images.sh のテキストから MAPPING=( ... ) の { image, context, dockerfile, args } を返す。
// エントリは 2 フィールド "image|dockerfile"（context='.'・args なし・従来）または
// 4 フィールド "image|context|dockerfile|k=v,k=v"（context 指定・dockerfile は context 相対・args 付き）。
function parseMappingEntries(bashText) {
  const text = String(bashText);
  const start = text.indexOf('MAPPING=(');
  if (start < 0) return [];
  const rest = text.slice(start);
  const endRel = rest.search(/\n\)/); // 行頭の閉じ括弧。
  const block = endRel >= 0 ? rest.slice(0, endRel) : rest;
  const out = [];
  // 少なくとも 1 つの `|` を含む引用文字列（＝エントリ）のみを対象にする。
  const re = /"([^"]*\|[^"]*)"/g;
  let m;
  while ((m = re.exec(block))) {
    const parts = m[1].split('|').map((s) => s.trim());
    const image = parts[0];
    if (parts.length >= 3) {
      out.push({ image, context: parts[1], dockerfile: parts[2], args: parseArgsCsv(parts[3] || '') });
    } else {
      out.push({ image, context: '.', dockerfile: parts[1], args: {} });
    }
  }
  return out;
}

// "k=v,k2=v2" 形式の build args を { k: v } へ分解する（値にカンマは含めない前提。値中の = は最初のみで分割）。
function parseArgsCsv(csv) {
  const args = {};
  for (const pair of String(csv).split(',')) {
    const s = pair.trim();
    if (!s) continue;
    const eq = s.indexOf('=');
    if (eq < 0) continue;
    args[s.slice(0, eq).trim()] = s.slice(eq + 1).trim();
  }
  return args;
}

// compose の build.context は compose ファイル（deploy/）相対、MAPPING の context はリポルート相対。
// deploy/ はリポルート直下のため、compose 側の先頭 '../' を 1 段だけ剥がしてリポルート相対へ正規化する。
function normalizeComposeContext(ctx) {
  if (!ctx || ctx === '.' || ctx === '..') return '.';
  if (ctx.startsWith('../')) return ctx.slice(3).replace(/\/$/, '') || '.';
  return ctx.replace(/\/$/, '');
}

// build args マップの同値判定（キー集合と各値が一致するか）。
function argsEqual(a = {}, b = {}) {
  const ak = Object.keys(a).sort();
  const bk = Object.keys(b).sort();
  if (ak.length !== bk.length) return false;
  return ak.every((k, i) => bk[i] === k && String(a[k]) === String(b[k]));
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
  const composeByService = new Map(composeTargets.map((t) => [t.service, t]));
  const mappingByImage = new Map(mappingEntries.map((e) => [e.image, e]));

  // 5. 除外リストの腐り: COMPOSE_ONLY が compose の build 定義に実在するか。
  for (const svc of composeOnly) {
    if (!composeByService.has(svc)) {
      violations.push({
        kind: 'compose-only-stale',
        detail: `COMPOSE_ONLY の '${svc}' が compose の build 定義に存在しない（除外リストが腐っている）`,
      });
    }
  }

  // 1,3: compose 側から見た MAPPING 欠落・Dockerfile/context/args 不一致（除外サービスはスキップ）。
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
    const me = mappingByImage.get(expectedImage);
    if (me.dockerfile !== t.dockerfile) {
      violations.push({
        kind: 'dockerfile-mismatch',
        detail: `'${t.service}' の Dockerfile 不一致: MAPPING='${me.dockerfile}' / compose='${t.dockerfile}'`,
      });
    }
    // #283 / IADR-0070: build context の不一致（compose は deploy 相対、MAPPING はルート相対で正規化して比較）。
    const composeCtx = normalizeComposeContext(t.context);
    const mappingCtx = me.context || '.';
    if (composeCtx !== mappingCtx) {
      violations.push({
        kind: 'context-mismatch',
        detail: `'${t.service}' の build context 不一致: MAPPING='${mappingCtx}' / compose(正規化)='${composeCtx}'`,
      });
    }
    // #283 / IADR-0070: build args の不一致（単一 Dockerfile を args で切替える AST 型で別イメージ化を防ぐ）。
    if (!argsEqual(t.args, me.args)) {
      violations.push({
        kind: 'args-mismatch',
        detail:
          `'${t.service}' の build args 不一致: ` +
          `MAPPING='${JSON.stringify(me.args || {})}' / compose='${JSON.stringify(t.args || {})}'`,
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

  // パーサ: 行末インラインコメントが付いても build/dockerfile を取り逃さない（false negative 回帰）。
  const composeCommented = [
    'services:',
    '  document-service:',
    '    build:  # ローカル k8s と共通',
    '      context: ..',
    '      dockerfile: src/a/Dockerfile  # マルチステージ',
    '    expose:',
    '      - "8080"',
    '',
  ].join('\n');
  const parsedCommented = parseComposeBuildTargets(composeCommented);
  cases.push({
    name: 'compose: build:/dockerfile: の行末コメントを無視して抽出',
    pass: parsedCommented.length === 1 && parsedCommented[0].dockerfile === 'src/a/Dockerfile',
    actual: parsedCommented,
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
  // #313 / IADR-0078: 除外機構は production 既定（空の COMPOSE_ONLY）に依存せず composeOnly を明示して検証する。
  expectCount('drift: 整合（compose 専用除外）は違反 0', computeDrift({ mappingEntries: okMapping, composeTargets: okCompose, composeOnly: ['frontend'] }).length, 0);

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

  // computeDrift: 除外の二重掲載（compose 専用サービスを MAPPING に載せた）。#313: composeOnly を明示。
  const dupV = computeDrift({
    mappingEntries: [
      { image: 'microservices-platform/document-service', dockerfile: 'src/a/Dockerfile' },
      { image: 'microservices-platform/frontend', dockerfile: 'src/platform/frontend/Dockerfile' },
    ],
    composeTargets: okCompose,
    composeOnly: ['frontend'],
  });
  cases.push({ name: 'drift: compose 専用除外の二重掲載を検出', pass: dupV.some((v) => v.kind === 'compose-only-in-mapping'), actual: dupV });

  // computeDrift: 除外リストの腐り（除外対象が compose から消えた）。#313: composeOnly を明示。
  const rotV = computeDrift({
    mappingEntries: okMapping,
    composeTargets: [{ service: 'document-service', dockerfile: 'src/a/Dockerfile' }], // frontend 無し
    composeOnly: ['frontend'],
  });
  cases.push({ name: 'drift: 除外リストの腐り（対象消失）を検出', pass: rotV.some((v) => v.kind === 'compose-only-stale'), actual: rotV });

  // --- #283 / IADR-0070: context/args 対応の追加試験 -----------------------------

  // パーサ: build.context と build.args を抽出する（AST 型の単一 Dockerfile＋args）。
  // 合成フィクスチャ（値は検査の成否に対して任意）だが、実 compose からの貼り付けなので現行値へ揃える。
  // #570: AST が *.Worker → *.Api へ改名したのに合わせて更新（検査ロジックは不変）。
  const composeAst = [
    'services:',
    '  configuration-service:',
    '    build:',
    '      context: ../src/ai-stock-trading',
    '      dockerfile: backend/Dockerfile',
    '      args:',
    '        SERVICE_PROJECT: backend/Services/ConfigurationService/src/ConfigurationService.Api/ConfigurationService.Api.csproj',
    '        SERVICE_DLL: ConfigurationService.Api.dll',
    '    expose:',
    '      - "8080"',
    '',
  ].join('\n');
  const parsedAst = parseComposeBuildTargets(composeAst);
  cases.push({
    name: 'compose: context/args を持つ AST 型を抽出',
    pass:
      parsedAst.length === 1 &&
      parsedAst[0].context === '../src/ai-stock-trading' &&
      parsedAst[0].dockerfile === 'backend/Dockerfile' &&
      parsedAst[0].args.SERVICE_DLL === 'ConfigurationService.Api.dll' &&
      parsedAst[0].args.SERVICE_PROJECT.endsWith('ConfigurationService.Api.csproj'),
    actual: parsedAst[0],
  });

  // パーサ: MAPPING の 4 フィールド（context/dockerfile/args）と 2 フィールド（既定）を分解する。
  const bashAst = [
    'MAPPING=(',
    '  "microservices-platform/document-service|src/a/Dockerfile"',
    '  "microservices-platform/configuration-service|src/ai-stock-trading|backend/Dockerfile|SERVICE_PROJECT=backend/x.csproj,SERVICE_DLL=x.dll"',
    ')',
  ].join('\n');
  const parsedBashAst = parseMappingEntries(bashAst);
  cases.push({
    name: 'MAPPING: 2 フィールドは context=. 既定・4 フィールドは context/args を分解',
    pass:
      parsedBashAst.length === 2 &&
      parsedBashAst[0].context === '.' &&
      parsedBashAst[0].dockerfile === 'src/a/Dockerfile' &&
      parsedBashAst[1].context === 'src/ai-stock-trading' &&
      parsedBashAst[1].dockerfile === 'backend/Dockerfile' &&
      parsedBashAst[1].args.SERVICE_DLL === 'x.dll',
    actual: parsedBashAst,
  });

  // computeDrift: AST 型（context/args 一致）は違反 0。compose の '../src/..' は正規化して MAPPING の 'src/..' と一致。
  const astCompose = [
    {
      service: 'configuration-service',
      context: '../src/ai-stock-trading',
      dockerfile: 'backend/Dockerfile',
      args: { SERVICE_PROJECT: 'p.csproj', SERVICE_DLL: 'x.dll' },
    },
  ];
  const astMapping = [
    {
      image: 'microservices-platform/configuration-service',
      context: 'src/ai-stock-trading',
      dockerfile: 'backend/Dockerfile',
      args: { SERVICE_DLL: 'x.dll', SERVICE_PROJECT: 'p.csproj' },
    },
  ];
  expectCount(
    'drift: AST 型（context/args 一致）は違反 0',
    computeDrift({ mappingEntries: astMapping, composeTargets: astCompose, composeOnly: [] }).length,
    0,
  );

  // computeDrift: build context 不一致を検出。
  const ctxV = computeDrift({
    mappingEntries: [
      { image: 'microservices-platform/configuration-service', context: 'src/other', dockerfile: 'backend/Dockerfile', args: { SERVICE_DLL: 'x.dll', SERVICE_PROJECT: 'p.csproj' } },
    ],
    composeTargets: astCompose,
    composeOnly: [],
  });
  cases.push({ name: 'drift: build context 不一致を検出', pass: ctxV.some((v) => v.kind === 'context-mismatch'), actual: ctxV });

  // computeDrift: build args 不一致を検出（単一 Dockerfile を args で切替える型の別イメージ化防止）。
  const argsV = computeDrift({
    mappingEntries: [
      { image: 'microservices-platform/configuration-service', context: 'src/ai-stock-trading', dockerfile: 'backend/Dockerfile', args: { SERVICE_DLL: 'WRONG.dll', SERVICE_PROJECT: 'p.csproj' } },
    ],
    composeTargets: astCompose,
    composeOnly: [],
  });
  cases.push({ name: 'drift: build args 不一致を検出', pass: argsV.some((v) => v.kind === 'args-mismatch'), actual: argsV });

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
