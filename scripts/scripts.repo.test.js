#!/usr/bin/env node
'use strict';
/*
 * scripts.repo.test.js
 * 本リポジトリ固有スクリプトの単体テスト。scripts.test.js（impl-handoff-kit の配布物）から
 * 自動で読み込まれる companion ファイルである（IADR-0115 / planning#112 / planning#116）。
 *
 * ここへ書く理由: scripts.test.js はキットの更新のたびに差し替わるため、固有テストを直接
 * 追記すると同期のたびに手動マージが要り、キットが同じテストを取り込んだ際に重複も生じる。
 * 本ファイルへ分離することで scripts.test.js をキットとバイト一致に保てる。
 *
 * **必ずコミットすること。** 未追跡だと CI（clean checkout）に存在せず、固有テストが黙って
 * 走らなくなる（scripts.test.js が未追跡を検出して警告する）。消失そのものは ci.yml の
 * scripts-tests ジョブに REQUIRE_REPO_TESTS=1 を設定して検出する。
 *
 * 実行: node scripts/scripts.test.js（本ファイル単体では実行しない）
 */
module.exports = ({ ok, assert }) => {
  // --- check-doc-links: planning submodule の扱い（Issue #232） -----------------

  const { parseArgs: parseDocLinkArgs, planningPopulated } = require('./check-doc-links.js');
  const fs = require('fs');
  const path = require('path');
  const os = require('os');

  ok('parseArgs は --require-planning を解釈', () => {
    assert.strictEqual(parseDocLinkArgs([]).requirePlanning, false);
    assert.strictEqual(parseDocLinkArgs(['--require-planning']).requirePlanning, true);
    assert.strictEqual(parseDocLinkArgs(['--dir', 'docs']).dir, 'docs');
  });

  ok('planningPopulated は projects/ の実在で判定', () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), 'doclinks-'));
    // 未 populate（空プレースホルダ）: false
    fs.mkdirSync(path.join(base, 'planning'), { recursive: true });
    assert.strictEqual(planningPopulated(base), false);
    // populate 済み（projects/ あり）: true
    fs.mkdirSync(path.join(base, 'planning', 'projects'), { recursive: true });
    assert.strictEqual(planningPopulated(base), true);
    // 後片付け（非再帰）
    fs.rmdirSync(path.join(base, 'planning', 'projects'));
    fs.rmdirSync(path.join(base, 'planning'));
    fs.rmdirSync(base);
  });

  // fail-loud の中核: --require-planning かつ未 populate なら main() は exit 1（子プロセスで終了コード検証）。
  ok('--require-planning は未 populate で exit 1（fail-loud）', () => {
    const { spawnSync } = require('child_process');
    const base = fs.mkdtempSync(path.join(os.tmpdir(), 'doclinks-noplanning-'));
    const script = path.join(__dirname, 'check-doc-links.js');
    const r = spawnSync(process.execPath, [script, '--require-planning'], {
      env: { ...process.env, DOC_LINKS_ROOT: base }, // planning/projects が無い＝未 populate を再現
      encoding: 'utf8',
    });
    assert.strictEqual(r.status, 1, `未 populate では exit 1 のはずが ${r.status}`);
    assert.match(String(r.stderr), /require-planning/);
    fs.rmdirSync(base);
  });

  // --- check-unit-dependencies: ユニット依存方向の検査（Issue #231） -------------

  const {
    pathUnit,
    isSharedProject,
    isTestsProject,
    isBffCompositionHost,
    isUnitBffEndpoints,
    classifyProjectReference,
    scanFoundationComposable,
  } = require('./check-unit-dependencies.js');

  const KNOWLEDGE_DOC =
    'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj';
  const PLATFORM_BFF = 'src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj';
  const SHARED_CONTRACTS =
    'src/platform/backend/Shared/Platform.Shared.Contracts/Platform.Shared.Contracts.csproj';
  const PLATFORM_AUTH =
    'src/platform/backend/Services/AuthorizationService/src/AuthorizationService.Api/AuthorizationService.Api.csproj';
  const INTEGRATION_TESTS =
    'src/knowledge/backend/Tests/Knowledge.IntegrationTests/Knowledge.IntegrationTests.csproj';

  ok('pathUnit は src/<unit>/ を返す', () => {
    assert.strictEqual(pathUnit(KNOWLEDGE_DOC), 'knowledge');
    assert.strictEqual(pathUnit(PLATFORM_BFF), 'platform');
    assert.strictEqual(pathUnit('docs/adr/README.md'), null);
  });

  ok('isSharedProject は platform/backend/Shared 配下のみ true', () => {
    assert.strictEqual(isSharedProject(SHARED_CONTRACTS), true);
    assert.strictEqual(isSharedProject(PLATFORM_AUTH), false);
  });

  ok('isTestsProject は *.Tests.csproj / tests/（大文字小文字問わず）を検出', () => {
    assert.strictEqual(isTestsProject(INTEGRATION_TESTS), true);
    assert.strictEqual(isTestsProject('src/knowledge/backend/Services/X/tests/X.Api.Tests/X.Api.Tests.csproj'), true);
    assert.strictEqual(isTestsProject(KNOWLEDGE_DOC), false);
  });

  ok('可変ユニット → platform Shared は許可', () =>
    assert.strictEqual(classifyProjectReference(KNOWLEDGE_DOC, SHARED_CONTRACTS).ok, true));

  ok('統合テスト → platform サービスは許可（例外）', () =>
    assert.strictEqual(classifyProjectReference(INTEGRATION_TESTS, PLATFORM_AUTH).ok, true));

  ok('platform → 可変ユニットは違反', () =>
    assert.strictEqual(classifyProjectReference(PLATFORM_BFF, KNOWLEDGE_DOC).ok, false));

  // 例外3（BFF 合成点）: Platform.Bff → 可変ユニットの <unit>/backend/Bff/ のみ許可（IADR-0063）。
  const KNOWLEDGE_BFF = 'src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/Knowledge.Bff.Endpoints.csproj';
  ok('isBffCompositionHost / isUnitBffEndpoints', () => {
    assert.strictEqual(isBffCompositionHost(PLATFORM_BFF), true);
    assert.strictEqual(isBffCompositionHost(PLATFORM_AUTH), false);
    assert.strictEqual(isUnitBffEndpoints(KNOWLEDGE_BFF), true);
    assert.strictEqual(isUnitBffEndpoints(KNOWLEDGE_DOC), false); // Services 配下は BFF エンドポイントでない
    assert.strictEqual(isUnitBffEndpoints('src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj'), false); // platform は対象外
  });
  ok('例外3: BFF 合成点 → knowledge BFF エンドポイントは許可', () =>
    assert.strictEqual(classifyProjectReference(PLATFORM_BFF, KNOWLEDGE_BFF).ok, true));
  ok('例外3外: platform サービス → knowledge BFF は違反', () =>
    assert.strictEqual(classifyProjectReference(PLATFORM_AUTH, KNOWLEDGE_BFF).ok, false));
  ok('例外3外: BFF 合成点 → knowledge サービスは違反', () =>
    assert.strictEqual(classifyProjectReference(PLATFORM_BFF, KNOWLEDGE_DOC).ok, false));

  ok('可変ユニット（非テスト） → platform 非 Shared は違反', () =>
    assert.strictEqual(classifyProjectReference(KNOWLEDGE_DOC, PLATFORM_AUTH).ok, false));

  ok('Foundation 配下の using .Composable を違反として検出', () => {
    const v = scanFoundationComposable(
      'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/Foundation/X.cs',
      'using DocumentService.Api.Composable.Steps;\n',
    );
    assert.strictEqual(v.length, 1);
  });

  ok('Foundation 外 / Composable を含まない using は無視', () => {
    assert.strictEqual(
      scanFoundationComposable('src/.../Foundation/X.cs', 'using DocumentService.Api.Foundation.Domain;\n').length,
      0,
    );
    assert.strictEqual(
      scanFoundationComposable('src/.../Program.cs', 'using DocumentService.Api.Composable.Steps;\n').length,
      0,
    );
  });

  ok('Foundation 配下のエイリアス / static using .Composable も検出', () => {
    assert.strictEqual(
      scanFoundationComposable('src/.../Foundation/X.cs', 'using Step = DocumentService.Api.Composable.Steps.SomeStep;\n')
        .length,
      1,
    );
    assert.strictEqual(
      scanFoundationComposable('src/.../Foundation/X.cs', 'using static DocumentService.Api.Composable.Helpers;\n').length,
      1,
    );
  });

  // --- check-image-mapping: MAPPING ↔ compose build ドリフト検査（Issue #275 / IADR-0068） ---

  const {
    parseComposeBuildTargets,
    parseMappingEntries,
    computeDrift,
  } = require('./check-image-mapping.js');

  const IMG_COMPOSE = [
    'services:',
    '  document-service:',
    '    build:',
    '      context: ..',
    '      dockerfile: src/a/Dockerfile',
    '    expose:',
    '      - "8080"',
    '  postgres:',
    '    image: postgres:16-alpine',
    '  frontend:',
    '    build:',
    '      context: ..',
    '      dockerfile: src/platform/frontend/Dockerfile',
    'volumes:',
    '  document-data:',
    '',
  ].join('\n');

  ok('parseComposeBuildTargets は build を持つサービスのみ抽出（infra/ブロック外を除く）', () => {
    const t = parseComposeBuildTargets(IMG_COMPOSE);
    assert.strictEqual(t.length, 2);
    // Issue #283: context/args も抽出する（既定 args は空・context は compose 記載どおり）。
    assert.deepStrictEqual(t[0], { service: 'document-service', context: '..', dockerfile: 'src/a/Dockerfile', args: {} });
    assert.strictEqual(t[1].service, 'frontend');
  });

  ok('parseComposeBuildTargets は build:/dockerfile: の行末コメントを無視する', () => {
    const yaml = [
      'services:',
      '  document-service:',
      '    build:  # comment',
      '      context: ..',
      '      dockerfile: src/a/Dockerfile  # comment',
      '    expose:',
      '      - "8080"',
    ].join('\n');
    const t = parseComposeBuildTargets(yaml);
    assert.strictEqual(t.length, 1);
    assert.deepStrictEqual(t[0], { service: 'document-service', context: '..', dockerfile: 'src/a/Dockerfile', args: {} });
  });

  ok('parseMappingEntries は MAPPING=( ... ) 内の "image|dockerfile" のみ抽出', () => {
    const bash = [
      '# comment',
      'MAPPING=(',
      '  "microservices-platform/document-service|src/a/Dockerfile"',
      '  "microservices-platform/bff|src/b/Dockerfile"',
      ')',
      'echo "microservices-platform/outside|ignored"',
    ].join('\n');
    const e = parseMappingEntries(bash);
    assert.strictEqual(e.length, 2);
    // Issue #283: 2 フィールドエントリは context='.'（リポルート）・args={} 既定へ分解する。
    assert.deepStrictEqual(e[0], { image: 'microservices-platform/document-service', context: '.', dockerfile: 'src/a/Dockerfile', args: {} });
  });

  const IMG_OK_COMPOSE = [
    { service: 'document-service', dockerfile: 'src/a/Dockerfile' },
    { service: 'frontend', dockerfile: 'src/platform/frontend/Dockerfile' },
  ];
  const IMG_OK_MAPPING = [{ image: 'microservices-platform/document-service', dockerfile: 'src/a/Dockerfile' }];

  ok('computeDrift: 整合（compose 専用除外）は違反 0', () => {
    // #313 / IADR-0078: 除外機構は production 既定（空の COMPOSE_ONLY）に依存せず composeOnly を明示して検証する。
    assert.strictEqual(computeDrift({ mappingEntries: IMG_OK_MAPPING, composeTargets: IMG_OK_COMPOSE, composeOnly: ['frontend'] }).length, 0);
  });

  ok('computeDrift: 新サービスの MAPPING 欠落を検出', () => {
    const v = computeDrift({
      mappingEntries: IMG_OK_MAPPING,
      composeTargets: [...IMG_OK_COMPOSE, { service: 'new-service', dockerfile: 'src/n/Dockerfile' }],
    });
    assert.ok(v.some((x) => x.kind === 'missing-mapping'));
  });

  ok('computeDrift: Dockerfile 不一致を検出', () => {
    const v = computeDrift({
      mappingEntries: [{ image: 'microservices-platform/document-service', dockerfile: 'src/OLD/Dockerfile' }],
      composeTargets: IMG_OK_COMPOSE,
    });
    assert.ok(v.some((x) => x.kind === 'dockerfile-mismatch'));
  });

  ok('computeDrift: stale な MAPPING エントリを検出', () => {
    const v = computeDrift({
      mappingEntries: [
        ...IMG_OK_MAPPING,
        { image: 'microservices-platform/removed-service', dockerfile: 'src/x/Dockerfile' },
      ],
      composeTargets: IMG_OK_COMPOSE,
    });
    assert.ok(v.some((x) => x.kind === 'stale-mapping'));
  });

  ok('computeDrift: chart-image の接頭辞違い（命名不整合）を検出', () => {
    const v = computeDrift({
      mappingEntries: [{ image: 'wrong-prefix/document-service', dockerfile: 'src/a/Dockerfile' }],
      composeTargets: IMG_OK_COMPOSE,
    });
    assert.ok(v.some((x) => x.kind === 'naming'));
  });

  ok('computeDrift: compose 専用除外の MAPPING 二重掲載を検出', () => {
    // #313 / IADR-0078: composeOnly を明示して除外機構を検証（frontend は現在 k8s 化済み・MAPPING 掲載が正）。
    const v = computeDrift({
      mappingEntries: [
        ...IMG_OK_MAPPING,
        { image: 'microservices-platform/frontend', dockerfile: 'src/platform/frontend/Dockerfile' },
      ],
      composeTargets: IMG_OK_COMPOSE,
      composeOnly: ['frontend'],
    });
    assert.ok(v.some((x) => x.kind === 'compose-only-in-mapping'));
  });

  ok('computeDrift: 除外リストの腐り（除外対象が compose から消失）を検出', () => {
    // #313 / IADR-0078: composeOnly を明示して除外機構を検証する。
    const v = computeDrift({
      mappingEntries: IMG_OK_MAPPING,
      composeTargets: [{ service: 'document-service', dockerfile: 'src/a/Dockerfile' }],
      composeOnly: ['frontend'],
    });
    assert.ok(v.some((x) => x.kind === 'compose-only-stale'));
  });

  // --- check-realm-constraints: realm フィールド長検査（Issue #18 再発防止） ---

  const {
    charLen,
    collectFields,
    findViolations,
    checkRealmText,
    collectMissingUrls,
    REQUIRED_CLIENT_URLS,
  } = require('./check-realm-constraints.js');

  ok('charLen はコードポイント数（マルチバイトも 1 文字 = 1）', () => {
    assert.strictEqual(charLen('あ'.repeat(300)), 300);
    assert.strictEqual(charLen(null), 0);
  });

  ok('findViolations: 255 文字は合格・256 文字は違反', () => {
    const ok255 = collectFields({ clients: [{ clientId: 'x', description: 'a'.repeat(255) }] });
    const over = collectFields({ clients: [{ clientId: 'x', description: 'a'.repeat(256) }] });
    assert.strictEqual(findViolations(ok255).length, 0);
    assert.strictEqual(findViolations(over).length, 1);
  });

  ok('collectFields は client/role/group/realm を横断走査する', () => {
    const long = 'a'.repeat(256);
    const v = findViolations(collectFields({
      realm: 'r', displayName: long,
      roles: { realm: [{ name: 'a', description: long }], client: { c: [{ name: 'b', description: long }] } },
      groups: [{ name: 'g', subGroups: [{ name: long }] }],
    }));
    assert.strictEqual(v.length, 4);
  });

  ok('collectFields は clientScopes / protocolMappers も走査する', () => {
    const long = 'a'.repeat(256);
    const v = findViolations(collectFields({
      clients: [{ clientId: 'x', protocolMappers: [{ name: long }] }],
      clientScopes: [{ name: 'ok', description: long, protocolMappers: [{ name: long }] }],
    }));
    assert.strictEqual(v.length, 3);
  });

  ok('checkRealmText: 欠損フィールドは例外を投げず無視', () => {
    assert.strictEqual(
      checkRealmText(JSON.stringify({ clients: [{ clientId: 'x' }], roles: {}, groups: null })).length,
      0,
    );
  });

  // --- check-realm-constraints: 経路ごとに必須の URL の欠落検査（Issue #385 再発防止） ---

  const REQ_FIXTURE = {
    'wiki-js': { redirectUris: ['http://localhost:3300/*', 'http://localhost:3001/*'] },
  };

  ok('collectMissingUrls: 必須 URL が揃っていれば欠落なし', () => {
    const realm = {
      clients: [{ clientId: 'wiki-js', redirectUris: ['http://localhost:3001/*', 'http://localhost:3300/*'] }],
    };
    assert.strictEqual(collectMissingUrls(realm, REQ_FIXTURE).length, 0);
  });

  ok('collectMissingUrls: k8s port-forward 用 3300 の欠落を検出する（#385 の回帰）', () => {
    const realm = { clients: [{ clientId: 'wiki-js', redirectUris: ['http://localhost:3001/*'] }] };
    const missing = collectMissingUrls(realm, REQ_FIXTURE);
    assert.strictEqual(missing.length, 1);
    assert.strictEqual(missing[0].url, 'http://localhost:3300/*');
    assert.strictEqual(missing[0].path, 'clients[wiki-js].redirectUris');
  });

  ok('collectMissingUrls: 対象 client が無い realm では検査しない', () => {
    assert.strictEqual(collectMissingUrls({ clients: [{ clientId: 'other' }] }, REQ_FIXTURE).length, 0);
    assert.strictEqual(collectMissingUrls({}, REQ_FIXTURE).length, 0);
  });

  ok('collectMissingUrls: フィールド欠損は必須 URL 全件を欠落として返す', () => {
    assert.strictEqual(collectMissingUrls({ clients: [{ clientId: 'wiki-js' }] }, REQ_FIXTURE).length, 2);
  });

  ok('実 realm の wiki-js は経路別の必須 URL（50000/3300/3001/wiki-js:3000）を満たす', () => {
    const realmPath = path.join(__dirname, '..', 'deploy', 'keycloak', 'microservices-platform-realm.json');
    const realm = JSON.parse(fs.readFileSync(realmPath, 'utf8'));
    assert.deepStrictEqual(collectMissingUrls(realm), []);
    // 経路の取り違え（#385）防止: 4 経路すべてが表に載っていること
    assert.strictEqual(REQUIRED_CLIENT_URLS['wiki-js'].redirectUris.length, 4);
  });

  // --- check-unit-service-ownership: AST 所有サービスの重複デプロイ検査（Issue #407 再発防止） ---

  const {
    parseServiceKeys,
    parseEnabledFlags,
    effectiveEnabled,
    findDuplicateOwnership,
    AST_OWNED_FALLBACK,
  } = require('./check-unit-service-ownership.js');

  const MSP_BASE_FIXTURE = [
    'services:',
    '  document:',
    '    enabled: true',
    '  risk-management:',
    '    enabled: false',
    '    image: microservices-platform/risk-management-service',
    '  market-monitor:',
    '    enabled: false',
    'networkPolicy:',
    '  enabled: true',
  ].join('\n');

  const AST_CHART_FIXTURE = [
    'services:',
    '  risk-management:',
    '    image: ai-stock-trading/risk-management-service',
    '  market-monitor:',
    '    image: ai-stock-trading/market-monitor-service',
    '  trade-decision:',
    '    image: ai-stock-trading/trade-decision-service',
  ].join('\n');

  ok('parseServiceKeys は top-level services 直下のキーのみを返す（深い階層・後続 top-level を拾わない）', () => {
    assert.deepStrictEqual(parseServiceKeys(MSP_BASE_FIXTURE), ['document', 'risk-management', 'market-monitor']);
    assert.deepStrictEqual(parseServiceKeys(AST_CHART_FIXTURE), ['risk-management', 'market-monitor', 'trade-decision']);
  });

  ok('parseServiceKeys: services ブロックが無ければ空', () => {
    assert.deepStrictEqual(parseServiceKeys('global:\n  image:\n    registry: k3d-local\n'), []);
  });

  ok('parseEnabledFlags は enabled の明示値のみを拾う（未指定は欠落＝undefined）', () => {
    const flags = parseEnabledFlags(MSP_BASE_FIXTURE);
    assert.strictEqual(flags.get('document'), true);
    assert.strictEqual(flags.get('risk-management'), false);
    assert.strictEqual(flags.get('market-monitor'), false);
    assert.strictEqual(parseEnabledFlags(AST_CHART_FIXTURE).has('risk-management'), false);
  });

  ok('effectiveEnabled: values-local の enabled: true が本番像の false を上書きする（Helm のマップ deep-merge）', () => {
    const override = 'services:\n  risk-management:\n    enabled: true\n';
    const eff = effectiveEnabled(MSP_BASE_FIXTURE, override);
    assert.strictEqual(eff.has('risk-management'), true);
    assert.strictEqual(eff.has('market-monitor'), false, '上書きの無い false は無効のまま');
    assert.strictEqual(eff.has('document'), true, '本番像の true は維持される');
  });

  ok('effectiveEnabled: 上書きが enabled を書かなければ本番像の値が残る', () => {
    const override = 'services:\n  risk-management:\n    extraEnv:\n      - { name: X, value: "1" }\n';
    assert.strictEqual(effectiveEnabled(MSP_BASE_FIXTURE, override).has('risk-management'), false);
  });

  ok('effectiveEnabled: 上書き側にしか無いサービスも評価対象になる', () => {
    const override = 'services:\n  newcomer:\n    enabled: true\n';
    assert.strictEqual(effectiveEnabled(MSP_BASE_FIXTURE, override).has('newcomer'), true);
  });

  ok('findDuplicateOwnership: AST 所有サービスが MSP で有効なら違反（#407 の回帰）', () => {
    const v = findDuplicateOwnership(new Set(['document', 'risk-management', 'market-monitor']), ['risk-management', 'market-monitor', 'trade-decision']);
    assert.deepStrictEqual(v, ['market-monitor', 'risk-management']);
  });

  ok('findDuplicateOwnership: MSP 固有サービスは AST と同名でなければ違反にならない', () => {
    assert.deepStrictEqual(findDuplicateOwnership(new Set(['document', 'wiki', 'bff']), AST_OWNED_FALLBACK), []);
  });

  ok('findDuplicateOwnership: MSP 側で無効なら AST 所有でも違反にならない（本番像 fail-safe 既定）', () => {
    assert.deepStrictEqual(findDuplicateOwnership(effectiveEnabled(MSP_BASE_FIXTURE, ''), parseServiceKeys(AST_CHART_FIXTURE)), []);
  });

  ok('AST_OWNED_FALLBACK は submodule 未取得時のフォールバックとして 3 画面系を含む', () => {
    for (const s of ['configuration', 'risk-management', 'market-monitor']) {
      assert.ok(AST_OWNED_FALLBACK.includes(s), `${s} が欠けている`);
    }
  });

  ok('実ファイル: 経路B(values-local) で AST 所有サービスが有効化されていない（#407 の回帰）', () => {
    const { checkTree } = require('./check-unit-service-ownership.js');
    assert.deepStrictEqual(checkTree(), []);
  });

  // --- check-test-traceability: 受け入れ基準 → テストの写像（Issue #453） ---------

  const trace = require('./check-test-traceability.js');

  ok('specIdOf: 仕様書ファイル名から起点 ID を取り出す（NFR は連番を丸める）', () => {
    assert.strictEqual(trace.specIdOf('FR-01_data-source-catalog.md'), 'FR-01');
    assert.strictEqual(trace.specIdOf('SC-11_configuration-viewer.md'), 'SC-11');
    assert.strictEqual(trace.specIdOf('NFR-01_performance-load-test.md'), 'NFR');
    assert.strictEqual(trace.specIdOf('TEST_STRATEGY.md'), null);
  });

  ok('idsInText: 修飾付き（AST/FR-17）を除外し裸の ID だけ拾う', () => {
    assert.deepStrictEqual([...trace.idsInText('// FR-03, UC-01: 検索')].sort(), ['FR-03', 'UC-01']);
    assert.strictEqual(trace.idsInText('// AST/FR-17: 別プロジェクト').has('FR-17'), false);
    assert.deepStrictEqual([...trace.idsInText('// AST/FR-17 と FR-03')], ['FR-03']);
    assert.strictEqual(trace.idsInText('XFR-01').size, 0); // 単語の一部は拾わない
    assert.strictEqual(trace.idsInText('// FR-3').has('FR-03'), true); // ゼロ埋め正規化
  });

  ok('classifyAgainstAllowlist: 未写像は blocked、allowlist 内は pending、写像済み残置は stale', () => {
    assert.deepStrictEqual(trace.classifyAgainstAllowlist(['FR-17'], []).blocked, ['FR-17']);
    assert.deepStrictEqual(trace.classifyAgainstAllowlist(['FR-17'], ['FR-17']).pending, ['FR-17']);
    assert.deepStrictEqual(trace.classifyAgainstAllowlist([], ['FR-17']).stale, ['FR-17']);
    const mixed = trace.classifyAgainstAllowlist(['FR-17', 'SC-18'], ['FR-17']);
    assert.deepStrictEqual(mixed.blocked, ['SC-18']);
    assert.deepStrictEqual(mixed.pending, ['FR-17']);
  });

  ok('実ファイル: 仕様書のある起点 ID がすべて写像済み（allowlist の残置も無い）', () => {
    const unmapped = trace.unmappedIds(trace.collectSpecIds(), trace.collectTestIds());
    const { blocked, stale } = trace.classifyAgainstAllowlist(unmapped, trace.readAllowlist());
    assert.deepStrictEqual(blocked, [], `未写像（allowlist 外）: ${blocked.join(' / ')}`);
    assert.deepStrictEqual(stale, [], `allowlist の減らし忘れ: ${stale.join(' / ')}`);
  });

  // --- check-coverage-floor: バックエンドのカバレッジ床（Issue #453） -------------

  const cov = require('./check-coverage-floor.js');

  const COBERTURA_FIXTURE = [
    '<coverage><packages><package><classes><class><lines>',
    '<line number="1" hits="1" />',
    '<line number="2" hits="0" />',
    '<line number="3" hits="5" branch="true" condition-coverage="50% (1/2)" />',
    '<line number="4" hits="2" branch="true" condition-coverage="100% (2/2)" />',
    '</lines></class></classes></package></packages></coverage>',
  ].join('\n');

  ok('parseCobertura: 行・分岐を数える（属性順・hits 欠落・空入力に耐える）', () => {
    const t = cov.parseCobertura(COBERTURA_FIXTURE);
    assert.strictEqual(t.lines, 4);
    assert.strictEqual(t.covered, 3);
    assert.strictEqual(t.branches, 4);
    assert.strictEqual(t.coveredBranches, 3);
    assert.strictEqual(cov.parseCobertura('<line hits="1" number="9" />').lines, 1);
    assert.strictEqual(cov.parseCobertura('<line number="1" />').lines, 0);
    assert.strictEqual(cov.parseCobertura('').lines, 0);
  });

  ok('rate: 分母 0 は null（未計測を 100% と誤らせない）', () => {
    assert.strictEqual(cov.rate(3, 4), 75);
    assert.strictEqual(cov.rate(0, 0), null);
  });

  ok('findReportsDetailed: 検出/除外の内訳を返す（0 件の原因を切り分けられること）', () => {
    const d = cov.findReportsDetailed();
    // 実リポジトリでは dotnet test 未実行なら 0 件。内訳の整合だけを検証する。
    assert.strictEqual(d.included.length + d.excluded.length, d.all.length);
    assert.ok(d.excluded.every((p) => cov.isExcludedPath(p)));
    assert.ok(d.included.every((p) => !cov.isExcludedPath(p)));
  });

  ok('isExcludedPath（カバレッジ）: AST を合算しない（PR #464 レビュー指摘）', () => {
    assert.strictEqual(cov.isExcludedPath('src/ai-stock-trading/backend/x/TestResults/g/coverage.cobertura.xml'), true);
    assert.strictEqual(cov.isExcludedPath('src/platform/backend/x/TestResults/g/coverage.cobertura.xml'), false);
    assert.strictEqual(cov.isExcludedPath('src/knowledge/backend/x/TestResults/g/coverage.cobertura.xml'), false);
  });

  ok('idsInText: //FR-03（スペース無し）は拾い、AST/FR-17（修飾付き）は拾わない', () => {
    assert.strictEqual(trace.idsInText('//FR-03: x').has('FR-03'), true);
    assert.strictEqual(trace.idsInText('// AST/FR-17').has('FR-17'), false);
    assert.strictEqual(trace.idsInText('XFR-01').size, 0);
  });

  ok('compareToFloor: 床未満は違反・床ちょうどは違反にしない・未計測は判定しない', () => {
    const t = cov.parseCobertura(COBERTURA_FIXTURE); // line 75% / branch 75%
    assert.strictEqual(cov.compareToFloor(t, { line: 80, branch: 70 }).violations.length, 1);
    assert.strictEqual(cov.compareToFloor(t, { line: 75, branch: 75 }).violations.length, 0);
    assert.strictEqual(cov.compareToFloor(t, { line: 90, branch: 90 }).violations.length, 2);
    const empty = { lines: 0, covered: 0, branches: 0, coveredBranches: 0 };
    assert.strictEqual(cov.compareToFloor(empty, { line: 80, branch: 70 }).violations.length, 0);
  });
};
