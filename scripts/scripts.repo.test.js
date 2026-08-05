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

  // --- check-doc-links: コードファイルへのリンクも検査対象（Issue #470） ----------
  //
  // LINK_EXT にコード拡張子が無かったため、仕様書からコードへの live link は一切検査されず、
  // 破損したまま「OK: 384 件」と報告された（検査器を作る PR が、検査器の穴で自分の参照切れを
  // 見逃した）。正例（実在 → OK）と負例（不在 → 検出）を対で固定する。

  const {
    LINK_EXT: DOC_LINK_EXT,
    isBrokenRef: isBrokenDocRef,
    collectBroken: collectBrokenDocLinks,
  } = require('./check-doc-links.js');

  const CODE_EXTS = ['js', 'mjs', 'cjs', 'ts', 'tsx', 'cs', 'csproj', 'props', 'targets', 'slnx', 'sh'];

  ok('LINK_EXT はコードファイルの拡張子を含む（#470）', () => {
    for (const ext of CODE_EXTS) {
      assert.ok(DOC_LINK_EXT.test(`a.${ext}`), `.${ext} が検査対象に入っていない`);
    }
    // 既存の対象（仕様書・図・スキーマ）を落としていないこと。
    for (const ext of ['md', 'yaml', 'yml', 'json', 'puml', 'mmd', 'png', 'jpeg', 'svg', 'drawio']) {
      assert.ok(DOC_LINK_EXT.test(`a.${ext}`), `.${ext} の検査が落ちている`);
    }
    // 無関係な拡張子まで広げていないこと（誤検知の芽）。
    for (const ext of ['txt', 'tsv', 'log', 'lock']) {
      assert.ok(!DOC_LINK_EXT.test(`a.${ext}`), `.${ext} は検査対象にしない`);
    }
  });

  ok('.js リンクは正例で OK・負例で検出（#470）', () => {
    const here = __dirname;
    assert.strictEqual(isBrokenDocRef('./check-doc-links.js', here), false, '実在する .js を破損としない');
    assert.strictEqual(isBrokenDocRef('./__no_such_script__.js', here), true, '不在の .js を検出する');
    // 対象外の拡張子は従来どおり素通し（実在しなくても検出しない）。
    assert.strictEqual(isBrokenDocRef('./__no_such__.txt', here), false);
  });

  ok('collectBroken は本文・フロントマター・インラインコードの .js を拾う（#470）', () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'doclinks-code-'));
    fs.writeFileSync(path.join(dir, 'real.js'), '// fixture\n');
    const md = path.join(dir, 'a.md');
    fs.writeFileSync(
      md,
      '---\nrelated_specs:\n  - ./real.js\n  - ./fm-missing.js\n---\n\n' +
        '# A\n\n[ok](./real.js) と [ng](./missing.js)。\n\nインラインの `./inline-missing.js`。\n'
    );
    const broken = collectBrokenDocLinks(md);
    assert.ok(!broken.includes('./real.js'), '実在する .js を報告しない');
    for (const x of ['./missing.js', './fm-missing.js', './inline-missing.js']) {
      assert.ok(broken.includes(x), `${x} を検出していない: ${JSON.stringify(broken)}`);
    }
    fs.rmSync(dir, { recursive: true, force: true });
  });

  // 自己試験そのものが緑であること（子プロセスで終了コードを実測する）。
  ok('check-doc-links --self-test は exit 0（#470）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-doc-links.js'), '--self-test'], {
      encoding: 'utf8',
    });
    assert.strictEqual(r.status, 0, `自己試験が失敗した:\n${r.stdout}${r.stderr}`);
    assert.match(String(r.stdout), /自己試験 \d+ 件 OK/);
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

  // --- check-test-traceability: 逆方向検査（計画レンジ・Issue #472） --------------

  // 実ファイルの構造を模したフィクスチャ。**後段に AST（別プロジェクト）の採番レンジを置く**のが要点。
  // 節スコープを外した実装はここで AST のレンジを拾い、計画レンジを取り違える。
  const RULES_FIXTURE = [
    '---', 'paths:', '  - "**/*"', '---', '',
    '## 起点 ID の種別', '',
    '本リポジトリではそれが **MSP** であり、ID レンジは',
    '`FR-01..21` / `UC-01..11` / `SC-01..21` / `ADR-0001..0039`（`ADR-0035` は番号予約のみ）',
    'である。', '',
    '## 複数プロジェクトを跨ぐ場合の ID 修飾', '',
    '**AST 側が自前で採番しているレンジは `FR-01..20` / `UC-01..07` / `SC-01..03`**',
  ].join('\n');

  ok('planRangeSection / parsePlanRanges: 「起点 ID の種別」節だけを見る（AST レンジを拾わない）', () => {
    const section = trace.planRangeSection(RULES_FIXTURE);
    assert.ok(section !== null && !section.includes('AST 側'), '節スコープが後段まで伸びている');
    assert.deepStrictEqual(trace.parsePlanRanges(section), {
      FR: { from: 1, to: 21 }, UC: { from: 1, to: 11 }, SC: { from: 1, to: 21 },
    });
    // ADR-xxxx はテスト仕様書の対象外なので拾わない。
    assert.strictEqual(trace.parsePlanRanges(section).ADR, undefined);
    // 節が無ければ null（fail-loud の入口）。
    assert.strictEqual(trace.planRangeSection('# 見出しのみ\n\n本文'), null);
  });

  // NFR: レンジが読めなくなると逆方向検査は「計画 0 件・不足 0 件」という最も安全に見える出力で
  // 素通りする（#472 が塞ごうとしている fail-open そのもの）。壊れた入力は例外にすることで固定する。
  ok('expandPlanIds / readPlanIds: 壊れた入力は例外（黙って 0 件検査に戻さない）', () => {
    assert.throws(() => trace.expandPlanIds({ FR: { from: 1, to: 3 } }), /UC/);
    assert.throws(() => trace.expandPlanIds({ FR: { from: 5, to: 1 }, UC: { from: 1, to: 1 }, SC: { from: 1, to: 1 } }), /範囲/);
    assert.throws(() => trace.readPlanIds(path.join(__dirname, '..', 'no-such-rules-file.md')), /読めません/);
  });

  ok('missingSpecIds / implementedWithoutSpec: 未着手と実装先行を切り分ける', () => {
    const missing = trace.missingSpecIds(['FR-01', 'FR-16', 'UC-01'], new Set(['FR-01', 'NFR']));
    assert.deepStrictEqual(missing, ['FR-16', 'UC-01']);
    // テストが参照済みのものだけが fail 対象（実装先行）。未着手は warn のまま。
    assert.deepStrictEqual(trace.implementedWithoutSpec(missing, new Set(['UC-01'])), ['UC-01']);
    assert.deepStrictEqual(trace.implementedWithoutSpec(missing, new Set(['FR-01'])), []);
  });

  ok('実ファイル: 計画レンジ 53 件を読み、実装先行はすべて allowlist 済み（specMissing の残置も無い）', () => {
    const planIds = trace.readPlanIds();
    assert.strictEqual(planIds.length, 53, `計画レンジの件数が変わった: ${planIds.length}`);
    for (const id of ['FR-21', 'UC-11', 'SC-21']) assert.ok(planIds.includes(id), `${id} が欠けている`);
    const missing = trace.missingSpecIds(planIds, trace.collectSpecIds());
    const implFirst = trace.implementedWithoutSpec(missing, trace.collectTestIds());
    const { blocked, stale } = trace.classifyAgainstAllowlist(implFirst, trace.readSpecMissingAllowlist());
    assert.deepStrictEqual(blocked, [], `仕様書なしで実装が先行（allowlist 外）: ${blocked.join(' / ')}`);
    assert.deepStrictEqual(stale, [], `specMissing の減らし忘れ: ${stale.join(' / ')}`);
  });

  ok('check-test-traceability --self-test は exit 0（逆方向検査の正例・負例を含む）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-test-traceability.js'), '--self-test'], { encoding: 'utf8' });
    assert.strictEqual(r.status, 0, `self-test が失敗:\n${r.stdout}\n${r.stderr}`);
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

  // NFR: 床が null へ戻ると check-coverage-floor は集計するだけで判定しなくなる（fail-open）。
  // 「配線済み・未武装」は緑のまま穴が開いた状態であり、退行として検知できない。ここで固定する。
  ok('coverage-floor.json: 床が武装されている（null へ戻る退行を止める）', () => {
    const floorPath = path.join(__dirname, '..', 'src', 'coverage-floor.json');
    const floor = JSON.parse(fs.readFileSync(floorPath, 'utf8')).backend;
    for (const metric of ['line', 'branch']) {
      assert.strictEqual(
        typeof floor[metric], 'number',
        `backend.${metric} が数値でない（null は未武装＝fail-open）: ${JSON.stringify(floor[metric])}`,
      );
      assert.ok(floor[metric] > 0, `backend.${metric} は正の値であること: ${floor[metric]}`);
    }
  });

  // NFR: 床が武装されていても、テストプロジェクトが coverlet.collector を参照していなければ
  // `dotnet test --collect:"XPlat Code Coverage"` は Cobertura を 1 件も出さない。レポート 0 件は
  // fail-open（warn で素通り）のため、床は緑のまま静かに無効化される——#453 の実測でまさに
  // これが起きていた（MSP 14 プロジェクト中 0 件が参照。CI が拾っていた 38 件はすべて AST）。
  // 床の null 化と同じ性質の穴であり、参照が外れる退行をここで固定する。
  ok('テストプロジェクトはすべて coverlet.collector を参照する（カバレッジの無音失効を止める）', () => {
    const repoRoot = path.join(__dirname, '..');
    // ai-stock-trading は別プロジェクト（submodule）であり床の対象外。
    // check-coverage-floor.js / check-test-traceability.js の EXCLUDED_UNITS と同じ切り分け。
    const units = ['platform', 'knowledge'];
    const skipDirs = new Set(['node_modules', 'bin', 'obj', '.git', 'dist', 'coverage']);
    const found = [];
    const walk = (dir) => {
      let entries = [];
      try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch (e) { return; }
      for (const e of entries) {
        const full = path.join(dir, e.name);
        if (e.isDirectory()) {
          if (!skipDirs.has(e.name)) walk(full);
        } else if (/Tests\.csproj$/.test(e.name)) {
          found.push(full);
        }
      }
    };
    for (const u of units) walk(path.join(repoRoot, 'src', u));

    // ratchet: テストプロジェクトを増やしたらこの実数を更新する。「N 件以上」にすると
    // 走査が壊れて 0 件になったときにテストが空振りで green になる（穴を塞ぐのが本テストの目的）。
    assert.strictEqual(
      found.length, 14,
      `テストプロジェクトの検出数が想定と異なる（走査の破損 or 増減。増えたなら本数を更新する）: ${found.length} 件\n` +
        found.map((f) => path.relative(repoRoot, f)).join('\n'),
    );

    const missing = found.filter((f) => {
      const xml = fs.readFileSync(f, 'utf8');
      return !/<PackageReference\s+Include="coverlet\.collector"/.test(xml);
    });
    assert.deepStrictEqual(
      missing.map((f) => path.relative(repoRoot, f)), [],
      'coverlet.collector を参照しないテストプロジェクトがある（XPlat Code Coverage が何も出力せず床が無効化される）',
    );
  });

  // --- check-coverage-floor: 合成点テスト経由の混入を filename 帰属で除く（#468 / IADR-0123） ---
  //
  // NFR（#468）: Platform.Bff は BFF の合成点として AST の Bff エンドポイントを ProjectReference するため、
  // src/platform/ 配下に出るレポートの**中身**に AST のクラスが入る。レポートファイルのパスによる除外
  // （isExcludedPath）はこれに届かない。ここで固定するのは次の 3 点である。
  //   1. <class filename> でユニットへ帰属させ、除外ユニットの行が集計から落ちること
  //   2. filename の形（相対 / 絶対 / <sources> 結合）に依らず帰属できること
  //      ——coverlet は base path で始まらないファイルを**絶対パスのまま**書くため、片方に決め打つと
  //        フィルタが何にもマッチせず「除外したつもりで素通り」になる（黙って混入が残る）
  //   3. 二重記載（<methods> 配下 と class 直下）を class 直下だけで数えること

  const AST_UNIT = [...cov.EXCLUDED_UNITS][0];

  /** class 1 件ぶんの XML（<methods> 配下と class 直下に同じ行を書く coverlet の形）。 */
  const coberturaClass = (name, filename, lines) => {
    const body = lines.map(([n, h]) => `<line number="${n}" hits="${h}" />`).join('');
    return `<class name="${name}" filename="${filename}">` +
      `<methods><method name="M"><lines>${body}</lines></method></methods>` +
      `<lines>${body}</lines></class>`;
  };
  const coberturaReport = (classes, { sources = [], attrs = '' } = {}) =>
    `<?xml version="1.0"?><coverage ${attrs}>` +
    (sources.length ? `<sources>${sources.map((s) => `<source>${s}</source>`).join('')}</sources>` : '') +
    `<packages><package name="Platform.Bff"><classes>${classes.join('')}</classes></package></packages></coverage>`;

  ok('parseCobertura: 二重記載は class 直下の <lines> だけを数える（<methods> 配下は内訳）', () => {
    const xml = coberturaReport([
      coberturaClass('Platform.Bff.X', 'src/platform/backend/Bff/X.cs', [[1, 1], [2, 0], [3, 4]]),
    ]);
    const p = cov.parseCobertura(xml);
    // 素朴に全 <line> を数えると 6 行になる（PR #464 のレビューで 266/230 と実測が割れた原因）。
    assert.strictEqual(p.lines, 3);
    assert.strictEqual(p.covered, 2);
  });

  ok('parseCobertura: 除外ユニットへ帰属した行を集計から落とす（相対 filename）', () => {
    const xml = coberturaReport([
      coberturaClass('Platform.Bff.X', 'src/platform/backend/Bff/X.cs', [[1, 1], [2, 0]]),
      coberturaClass('AiStockTrading.Bff.Endpoints.MonitorBffEndpoints',
        `src/${AST_UNIT}/backend/Bff/MonitorBffEndpoints.cs`, [[1, 3], [2, 3], [3, 3]]),
    ]);
    const p = cov.parseCobertura(xml);
    assert.strictEqual(p.lines, 2, '集計は platform の 2 行だけ');
    assert.strictEqual(p.excluded.lines, 3);
    assert.strictEqual(p.excluded.covered, 3, '混入行はすべて被覆済み＝実測値を押し上げる方向にしか働かない');
    assert.strictEqual(p.excluded.classes.length, 1);
    assert.match(p.excluded.classes[0].name, /MonitorBffEndpoints/);
  });

  ok('parseCobertura: 絶対 filename でも帰属する（base path で始まらないファイルは絶対のまま書かれる）', () => {
    const xml = coberturaReport([
      coberturaClass('X', `/home/runner/work/msp/msp/src/${AST_UNIT}/backend/Bff/X.cs`, [[1, 1]]),
    ], { sources: ['/home/runner/work/msp/msp/src/platform/backend/Bff/Platform.Bff/'] });
    const p = cov.parseCobertura(xml);
    assert.strictEqual(p.excluded.lines, 1);
    assert.strictEqual(p.diagnostics.how.absolute, 1);
  });

  ok('parseCobertura: <sources> と結合して帰属する（base path が src/ より深い場合）', () => {
    const xml = coberturaReport([
      coberturaClass('X', `${AST_UNIT}/backend/Bff/X.cs`, [[1, 1]]),
      coberturaClass('Y', 'platform/backend/Bff/Y.cs', [[1, 0]]),
    ], { sources: ['/home/runner/work/msp/msp/src/'] });
    const p = cov.parseCobertura(xml);
    assert.strictEqual(p.excluded.lines, 1);
    assert.strictEqual(p.lines, 1);
    assert.strictEqual(p.diagnostics.how['source-joined'], 2);
  });

  ok('parseCobertura: 帰属できない行は集計に残す（黙って落とさない）', () => {
    const xml = coberturaReport([coberturaClass('X', 'Foo/Bar.cs', [[1, 1], [2, 1]])]);
    const p = cov.parseCobertura(xml);
    assert.strictEqual(p.lines, 2);
    assert.strictEqual(p.excluded.lines, 0);
    assert.strictEqual(p.diagnostics.how.unattributed, 1);
  });

  ok('attributionMessages: 帰属 0 件は warn（フィルタが何にもマッチしない＝素通りの検出）', () => {
    const agg = cov.aggregateReports([cov.parseCobertura(
      coberturaReport([coberturaClass('X', 'Foo/Bar.cs', [[1, 1]])]))]);
    const msgs = cov.attributionMessages(agg);
    assert.ok(msgs.some((m) => m.level === 'warn' && /帰属できませんでした/.test(m.text)),
      `帰属 0 件で warn が出ていない: ${JSON.stringify(msgs)}`);
  });

  ok('attributionMessages: 除外 0 行は notice に留める（恒常的な警告にしない）', () => {
    const agg = cov.aggregateReports([cov.parseCobertura(
      coberturaReport([coberturaClass('X', 'src/platform/backend/X.cs', [[1, 1]])]))]);
    const msgs = cov.attributionMessages(agg);
    assert.ok(msgs.some((m) => m.level === 'notice' && /0 行でした/.test(m.text)));
    assert.deepStrictEqual(msgs.filter((m) => m.level === 'warn'), []);
  });

  ok('attributionMessages: class の外にある <line> は warn（除外できない行の可視化）', () => {
    const agg = cov.aggregateReports([cov.parseCobertura('<coverage><line number="1" hits="1" /></coverage>')]);
    assert.ok(cov.attributionMessages(agg).some((m) => m.level === 'warn' && /<class> にも属さない/.test(m.text)));
  });

  ok('aggregateReports: 除外前の値は coverlet 自身の lines-valid と照合できる（IADR-0123 決定 4）', () => {
    const xml = coberturaReport([
      coberturaClass('X', 'src/platform/backend/X.cs', [[1, 1], [2, 0]]),
      coberturaClass('Y', `src/${AST_UNIT}/backend/Y.cs`, [[1, 1]]),
    ], { attrs: 'lines-valid="3" lines-covered="2"' });
    const agg = cov.aggregateReports([cov.parseCobertura(xml)]);
    assert.strictEqual(agg.totals.lines, 2);
    assert.strictEqual(agg.excluded.lines, 1);
    assert.strictEqual(agg.beforeExclusion.lines, 3);
    assert.strictEqual(agg.diagnostics.reported.lines, 3, 'coverlet の lines-valid と一致する（前提の裏づけ）');
    assert.strictEqual(agg.diagnostics.reported.covered, 2);
  });

  // NFR（#468）: 混入行数の確定と床の置き直しは CI ログの診断出力から読み取る。
  // 出力が壊れると「測り直す手段」が黙って失われるため、鍵となる文言と数値をここで固定する。
  ok('formatDiagnostics: 除外行数・除外前の実測・解釈の内訳・除外クラス一覧が出る', () => {
    const xml = coberturaReport([
      coberturaClass('Platform.Bff.X', 'src/platform/backend/X.cs', [[1, 1], [2, 0]]),
      coberturaClass('AiStockTrading.Bff.Endpoints.MonitorBffEndpoints',
        `src/${AST_UNIT}/backend/Bff/MonitorBffEndpoints.cs`, [[1, 1]]),
    ]);
    const text = cov.formatDiagnostics(cov.aggregateReports([cov.parseCobertura(xml)])).join('\n');
    assert.match(text, /除外（filename 帰属・#468）/);
    assert.match(text, /1 行（被覆 1）/);          // 混入行数（確定値の読み取り口）
    assert.match(text, /除外前: line 66\.67%（2\/3）/); // 除外前の実測（床の置き直しの突き合わせ）
    assert.match(text, /そのまま\(相対\) 2/);       // filename の解釈
    assert.match(text, /MonitorBffEndpoints/);      // 除外したクラス
  });

  // NFR（#468）: CI 初回実走（run 1144 / commit 594117a）で **行は完全一致・分岐だけ乖離**した。
  // 本実装の「分岐」は <line> の condition-coverage の分母/分子、coverlet の branches-valid は別定義
  // であり、一致を期待しない（IADR-0123 決定 4 の［2026-08-04 追記］）。同列に「乖離」と出すと
  // 期待される差が異常に見えるため、書き分けをここで固定する。
  ok('formatDiagnostics: 行の乖離は要調査・分岐の差は定義差として書き分ける', () => {
    const report = (attrs) => coberturaReport([
      `<class name="X" filename="src/platform/backend/X.cs"><lines>` +
      '<line number="1" hits="1" branch="true" condition-coverage="50% (1/2)" /></lines></class>',
    ], { attrs });
    const same = cov.formatDiagnostics(cov.aggregateReports([cov.parseCobertura(
      report('lines-valid="1" lines-covered="1" branches-valid="4" branches-covered="1"'))])).join('\n');
    assert.ok(same.includes('lines-valid 1（本実装 1・一致）'), same);
    assert.ok(same.includes('差 -2（定義差・期待される乖離）'), same);
    assert.ok(!same.includes('**乖離'), `分岐の差を行と同列の「乖離」で出している:\n${same}`);

    const drift = cov.formatDiagnostics(cov.aggregateReports([cov.parseCobertura(
      report('lines-valid="9" lines-covered="9" branches-valid="2" branches-covered="1"'))])).join('\n');
    assert.ok(drift.includes('**乖離 -8・要調査**'), drift);

    // branches-covered も出す（coverlet 側の実際の値が CI ログから読めること）。
    assert.ok(same.includes('branches-covered 1（本実装 1・一致）'), same);

    // 床の値は src/coverage-floor.json が単一情報源（IADR-0118 決定 1）。診断へ数値を焼き込むと
    // ratchet で床を上げた瞬間に同じログの中で自己矛盾する。引数の floor を反映すること。
    const withFloor = cov.formatDiagnostics(cov.aggregateReports([cov.parseCobertura(
      report('lines-valid="1" lines-covered="1" branches-valid="4" branches-covered="1"'))]), { branch: 18 }).join('\n');
    assert.ok(withFloor.includes('床 18 はこの方式'), withFloor);
    assert.ok(!withFloor.includes('床 17'), `床の値をハードコードしている:\n${withFloor}`);

    // 分岐が一致していれば注記は出さない（恒常的なノイズにしない）。
    const branchSame = cov.formatDiagnostics(cov.aggregateReports([cov.parseCobertura(
      report('lines-valid="1" lines-covered="1" branches-valid="2" branches-covered="1"'))])).join('\n');
    assert.ok(!branchSame.includes('※ 分岐は'), branchSame);
  });

  // NFR（#468 / IADR-0123 決定 5）: 分岐は定義差のため coverlet 値との照合が反証力を持たない。
  // 分岐側の二重記載排除が壊れても値が増えるだけで CI ログには何も現れない（無音の失敗）。
  // 「全 <line>（<methods> 重複込み）」と「class 直下のみ」の比を観測点として出すことを固定する。
  ok('formatDiagnostics: 二重記載の観測（全 <line> と class 直下の比）を出す', () => {
    const xml = coberturaReport([
      coberturaClass('Platform.Bff.X', 'src/platform/backend/X.cs', [[1, 1], [2, 0]]),
    ]);
    const text = cov.formatDiagnostics(cov.aggregateReports([cov.parseCobertura(xml)])).join('\n');
    assert.ok(text.includes('全 <line>（<methods> 重複込み）= 行 4'), text);
    assert.ok(text.includes('class 直下のみ（除外前の集計）= 行 2'), text);
    assert.ok(text.includes('比 行 2.00'), text);
  });

  ok('check-coverage-floor --self-test は exit 0（帰属・二重記載・warn 経路を含む）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-coverage-floor.js'), '--self-test'], { encoding: 'utf8' });
    assert.strictEqual(r.status, 0, `self-test が失敗:\n${r.stdout}\n${r.stderr}`);
  });

  // 実ツリーへレポートを 1 件だけ置いて素実行する。関数単位の試験では「走査 → 除外 → 出力」の
  // 配線（レポートが実際に読まれ、診断が CI ログへ出ること）を確かめられないため。
  // coverage*.xml は .gitignore 済みだが、異常終了時に残さないよう finally で必ず撤去する。
  ok('素実行: 実ツリーのレポートから AST 由来の行を落とし、診断を出して exit 0', () => {
    const { spawnSync } = require('child_process');
    const repoRoot = path.join(__dirname, '..');
    const dir = path.join(repoRoot, 'src', 'platform', 'backend', '.coverage-floor-probe', 'TestResults', 'probe');
    const file = path.join(dir, 'coverage.cobertura.xml');
    try {
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(file, coberturaReport([
        coberturaClass('Platform.Bff.X', 'platform/backend/Bff/X.cs', [[1, 1], [2, 0], [3, 1]]),
        coberturaClass('AiStockTrading.Bff.Endpoints.MonitorBffEndpoints',
          `${AST_UNIT}/backend/Bff/MonitorBffEndpoints.cs`, [[1, 1], [2, 1]]),
      ], { sources: ['/home/runner/work/msp/msp/src/'], attrs: 'lines-valid="5" lines-covered="4"' }));

      const r = spawnSync(process.execPath, [path.join(__dirname, 'check-coverage-floor.js')], { encoding: 'utf8' });
      assert.strictEqual(r.status, 0, `素実行が失敗:\n${r.stdout}\n${r.stderr}`);
      assert.match(r.stdout, /レポート 1 件を集計: line 66\.67%（2\/3）/);
      assert.match(r.stdout, /由来 1 クラス \/ 2 行（被覆 2）/);
      assert.match(r.stdout, /除外前: line 80%（4\/5）/);
      assert.match(r.stdout, /lines-valid 5（本実装 5・一致）/);
    } finally {
      fs.rmSync(path.join(repoRoot, 'src', 'platform', 'backend', '.coverage-floor-probe'), { recursive: true, force: true });
    }
  });

  // --- check-backend-libraries: ADR-0030 ライブラリ標準の機械強制（Issue #455） ---

  const backendLibs = require('./check-backend-libraries.js');

  ok('bannedInCsproj: 不採用パッケージを検出し採用パッケージは無視する', () => {
    assert.deepStrictEqual(
      backendLibs.bannedInCsproj('<PackageReference Include="MassTransit.RabbitMQ" /><PackageReference Include="FluentValidation" />'),
      ['MassTransit']);
    assert.deepStrictEqual(backendLibs.bannedInCsproj('<PackageReference Include="Riok.Mapperly" />'), []);
  });

  ok('bannedInSource: using の各形（global / static / エイリアス）を拾い、ブロック構文は拾わない', () => {
    assert.deepStrictEqual(
      backendLibs.bannedInSource('global using Serilog;\nusing static FluentAssertions.AssertionExtensions;\nusing M = MassTransit.IBus;\n'),
      ['FluentAssertions', 'MassTransit', 'Serilog']);
    assert.deepStrictEqual(backendLibs.bannedInSource('using (var x = new MassTransitThing()) { }\n'), []);
  });

  ok('matchesBanned: 前方一致はドット区切りのときだけ効く（Serilog vs SerilogExtras）', () => {
    assert.strictEqual(backendLibs.matchesBanned('Serilog.AspNetCore', 'Serilog'), true);
    assert.strictEqual(backendLibs.matchesBanned('SerilogExtras', 'Serilog'), false);
  });

  ok('classifyAgainstBaseline: 新規混入は added（fail 対象）', () => {
    const r = backendLibs.classifyAgainstBaseline({ 'a.csproj': ['MassTransit'] }, {});
    assert.strictEqual(r.added.length, 1);
    assert.strictEqual(r.known.length, 0);
    assert.strictEqual(r.stale.length, 0);
  });

  ok('classifyAgainstBaseline: baseline どおりは known（warn のみ）', () => {
    const r = backendLibs.classifyAgainstBaseline({ 'a.csproj': ['MassTransit'] }, { 'a.csproj': ['MassTransit'] });
    assert.strictEqual(r.known.length, 1);
    assert.strictEqual(r.added.length, 0);
    assert.strictEqual(r.stale.length, 0);
  });

  ok('classifyAgainstBaseline: 解消済みなのに baseline に残るのは stale（減らし忘れ検出）', () => {
    const r = backendLibs.classifyAgainstBaseline({}, { 'a.csproj': ['MassTransit'] });
    assert.strictEqual(r.stale.length, 1);
    assert.strictEqual(r.added.length, 0);
  });

  ok('domainViolations: Domain は外部依存ゼロ・共有カーネル参照のみ許可', () => {
    const p = 'src/platform/backend/X.Domain.csproj';
    assert.strictEqual(backendLibs.domainViolations(p, '<PackageReference Include="FluentValidation" />').length, 1);
    assert.strictEqual(
      backendLibs.domainViolations(p, '<ProjectReference Include="../Shared/Platform.Shared.Kernel/Platform.Shared.Kernel.csproj" />').length, 0);
    assert.strictEqual(
      backendLibs.domainViolations(p, '<ProjectReference Include="../X.Infrastructure/X.Infrastructure.csproj" />').length, 1);
    // Domain 以外は対象外
    assert.strictEqual(backendLibs.domainViolations('src/platform/backend/X.Api.csproj', '<PackageReference Include="MediatR" />').length, 0);
  });

  ok('isExcludedPath: ADR-0030 は MSP の決定であり ai-stock-trading（別プロジェクト）は対象外', () => {
    assert.strictEqual(backendLibs.isExcludedPath('src/ai-stock-trading/backend/Services/X/src/X.Api/X.Api.csproj'), true);
    assert.strictEqual(backendLibs.isExcludedPath('src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj'), false);
    assert.strictEqual(backendLibs.isExcludedPath('src/knowledge/backend/Shared/Knowledge.Contracts/Knowledge.Contracts.csproj'), false);
  });

  ok('xunitRunnerMismatch: xunit.v3 と CPM の runner 2.x の同居を検出（PR #463 レビュー指摘の回帰）', () => {
    const v3 = '<PackageReference Include="xunit.v3" /><PackageReference Include="xunit.runner.visualstudio" />';
    assert.strictEqual(backendLibs.centralVersionOf('<PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />', 'xunit.runner.visualstudio'), '2.8.2');
    assert.strictEqual(backendLibs.majorOf('2.8.2'), 2);
    assert.strictEqual(backendLibs.majorOf('3.1.5'), 3);
    assert.strictEqual(backendLibs.xunitRunnerMismatch('t/X.Tests.csproj', v3, '2.8.2').length, 1);
    assert.strictEqual(backendLibs.xunitRunnerMismatch('t/X.Tests.csproj', v3, '3.1.5').length, 0);
    // v2 の組み合わせ・runner 非参照・CPM 未定義はいずれも判定しない
    assert.strictEqual(backendLibs.xunitRunnerMismatch('t/X.Tests.csproj',
      '<PackageReference Include="xunit" /><PackageReference Include="xunit.runner.visualstudio" />', '2.8.2').length, 0);
    assert.strictEqual(backendLibs.xunitRunnerMismatch('t/X.Tests.csproj', '<PackageReference Include="xunit.v3" />', '2.8.2').length, 0);
    assert.strictEqual(backendLibs.xunitRunnerMismatch('t/X.Tests.csproj', v3, null).length, 0);
  });

  // --- check-backend-libraries: 検出漏れの是正（Issue #471） ---

  ok('BANNED: Kiota は実在 ID（Microsoft.Kiota.*）で登録され、旧 "Kiota" の死にエントリが残っていない', () => {
    // 'Kiota' は完全一致にも 'Kiota.' 前方一致にも当たらず 1 件も検出できなかった（#471）。
    assert.strictEqual(backendLibs.BANNED.includes('Kiota'), false);
    assert.strictEqual(backendLibs.bannedNameOf('Microsoft.Kiota.Abstractions'), 'Microsoft.Kiota');
    assert.deepStrictEqual(
      backendLibs.bannedInCsproj('<PackageReference Include="Microsoft.Kiota.Abstractions" Version="1.0.0" />'),
      ['Microsoft.Kiota']);
  });

  ok('BANNED: ADR-0030 棚卸し表の不採用・置換対象（Kafka / RabbitMQ 素クライアント・Key Vault・Argon2）を含む', () => {
    for (const id of ['Confluent.Kafka', 'RabbitMQ.Client', 'Azure.Security.KeyVault.Secrets',
      'Azure.Extensions.AspNetCore.Configuration.Secrets', 'Konscious.Security.Cryptography.Argon2',
      'Isopoh.Cryptography.Argon2']) {
      assert.notStrictEqual(backendLibs.bannedNameOf(id), null, `${id} が BANNED に無い`);
    }
    // 採用側・無関係を巻き込まない（前方一致の境界）。
    for (const id of ['WolverineFx.Kafka', 'WolverineFx.RabbitMQ', 'Azure.Identity',
      'Konscious.Security.Cryptography.Blake2', 'Isopoh.Cryptography.Blake2b']) {
      assert.strictEqual(backendLibs.bannedNameOf(id), null, `${id} を誤検出している`);
    }
  });

  ok('isScannedBuildFile: props / targets（雛形の .sample 含む）も走査対象', () => {
    for (const p of ['src/x/X.csproj', 'src/Directory.Build.props', 'src/Directory.Build.targets',
      'src/x/Custom.props', 'templates/unit-template/backend/Directory.Packages.props.sample']) {
      assert.strictEqual(backendLibs.isScannedBuildFile(p), true, `${p} が対象外`);
    }
    for (const p of ['src/x/X.cs', 'src/x/backend.slnx', 'src/x/README.md']) {
      assert.strictEqual(backendLibs.isScannedBuildFile(p), false, `${p} が対象になっている`);
    }
  });

  ok('PackageVersion は違反にせず GlobalPackageReference は違反にする（CPM 走査追加の偽陽性防止）', () => {
    // Directory.Packages.props は baseline 消化まで不採用パッケージの**版定義**を正当に持つ。
    // ここを違反にすると走査対象への追加だけで 42 件の偽陽性が出る（#471）。
    assert.deepStrictEqual(
      backendLibs.bannedInCsproj('<PackageVersion Include="MassTransit" Version="8.4.1" />'
        + '<PackageVersion Include="Serilog.AspNetCore" Version="10.0.0" />'), []);
    // 一方 GlobalPackageReference は全プロジェクトへ参照を注入するため違反。
    assert.deepStrictEqual(
      backendLibs.bannedInCsproj('<GlobalPackageReference Include="Serilog" Version="4.0.0" />'), ['Serilog']);
  });

  ok('実ファイル: CPM の props（本体・雛形）は不採用パッケージの版定義を持つが違反 0', () => {
    for (const rel of ['src/Directory.Packages.props', 'src/Directory.Build.props',
      'templates/unit-template/backend/Directory.Packages.props.sample',
      'templates/unit-template/backend/Directory.Build.props.sample']) {
      const xml = fs.readFileSync(path.join(__dirname, '..', rel), 'utf8');
      assert.deepStrictEqual(backendLibs.bannedInCsproj(xml), [], `${rel} で偽陽性`);
    }
  });

  ok('--self-test は exit 0（検出漏れ 3 種の実地確認を含む）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-backend-libraries.js'), '--self-test'], { encoding: 'utf8' });
    assert.strictEqual(r.status, 0, `self-test が失敗:\n${r.stdout}\n${r.stderr}`);
  });

  ok('実ファイル: 新規混入 0 件・Domain 依存規律 OK（baseline との突合）', () => {
    const { current, domain } = backendLibs.scanTree();
    const baseline = JSON.parse(fs.readFileSync(path.join(__dirname, 'backend-library-baseline.json'), 'utf8')).projects;
    const { added, stale } = backendLibs.classifyAgainstBaseline(current, baseline);
    assert.deepStrictEqual(added, [], `baseline に無い新規混入: ${JSON.stringify(added)}`);
    assert.deepStrictEqual(stale, [], `解消済みなのに baseline に残る行: ${JSON.stringify(stale)}`);
    assert.deepStrictEqual(domain, [], `Domain 依存規律違反: ${JSON.stringify(domain)}`);
  });

  // --- lib/excluded-units: 除外ユニットの単一情報源（Issue #473） -----------------

  const excl = require('./lib/excluded-units.js');
  const testTrace = require('./check-test-traceability.js');
  const coverageFloor = require('./check-coverage-floor.js');

  const sortedUnits = (s) => [...s].sort();

  ok('除外ユニットは .gitmodules の src/<unit> から導出される（planning は含まない）', () => {
    const gitmodules = fs.readFileSync(path.join(__dirname, '..', '.gitmodules'), 'utf8');
    const derived = sortedUnits(excl.excludedUnitsFromText(gitmodules));
    assert.deepStrictEqual(derived, ['ai-stock-trading'], `導出結果: ${JSON.stringify(derived)}`);
    // planning はリポジトリ直下の submodule でユニットではない（issue #473 の注意点）。
    assert.strictEqual(derived.includes('planning'), false);
    // 実リポジトリのルートから読んでも同じ結果になること。
    assert.deepStrictEqual(sortedUnits(excl.excludedUnits()), derived);
  });

  // 単一情報源であることの核: 3 検査器が同じ集合を持つ。ハードコード時代は 3 箇所を人手で
  // 揃える運用であり、submodule ユニットが増えると 3 箇所同時に狭すぎになった（#473）。
  ok('3 検査器の EXCLUDED_UNITS が単一情報源から導出され一致する', () => {
    const derived = sortedUnits(excl.excludedUnits());
    for (const [name, mod] of [
      ['check-backend-libraries', backendLibs],
      ['check-test-traceability', testTrace],
      ['check-coverage-floor', coverageFloor],
    ]) {
      assert.deepStrictEqual(sortedUnits(mod.EXCLUDED_UNITS), derived, `${name} の除外集合が導出値と異なる`);
      assert.strictEqual(mod.isExcludedPath('src/ai-stock-trading/backend/x/XTests.cs'), true, `${name}: AST が対象内`);
      assert.strictEqual(mod.isExcludedPath('src/platform/backend/x/XTests.cs'), false, `${name}: platform が対象外`);
      assert.strictEqual(mod.isExcludedPath('src/Directory.Packages.props'), false, `${name}: src 直下を除外している`);
    }
  });

  // 逆戻り防止: ハードコードへ戻すと .gitmodules への自動追随が黙って失われる
  // （check-doc-links の `planning/` 固定判定を .gitmodules 由来へ一般化した #139 と同じ作法）。
  ok('3 検査器に除外ユニットのハードコードが残っていない', () => {
    for (const f of ['check-backend-libraries.js', 'check-test-traceability.js', 'check-coverage-floor.js']) {
      const src = fs.readFileSync(path.join(__dirname, f), 'utf8');
      // クォート形は両対応にする。片方だけだと `new Set(["ai-stock-trading"])` が素通りし、
      // 「逆戻りを検出するテスト」自体が逆戻りを見逃す（監査指摘）。
      assert.doesNotMatch(src, /new Set\(\[\s*["']ai-stock-trading["']/, `${f} にハードコードが残っている`);
      assert.match(src, /require\('\.\/lib\/excluded-units\.js'\)/, `${f} がヘルパを参照していない`);
    }
  });

  ok('仮の submodule を .gitmodules に足すと除外が自動追随する（フィクスチャ）', () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), 'excluded-units-repo-'));
    fs.writeFileSync(path.join(base, '.gitmodules'),
      '[submodule "planning"]\n\tpath = planning\n\turl = x\n'
      + '[submodule "src/ai-stock-trading"]\n\tpath = src/ai-stock-trading\n\turl = x\n'
      + '[submodule "src/next-unit"]\n\tpath = src/next-unit\n\turl = x\n');
    assert.deepStrictEqual(sortedUnits(excl.excludedUnits({ root: base })), ['ai-stock-trading', 'next-unit']);
    const isExcluded = excl.makeIsExcludedPath(excl.excludedUnits({ root: base }));
    assert.strictEqual(isExcluded('src/next-unit/backend/x/X.csproj'), true);
    assert.strictEqual(isExcluded('src/knowledge/backend/x/X.csproj'), false);
    fs.rmSync(path.join(base, '.gitmodules'));
    fs.rmdirSync(base);
  });

  // fail-closed: 読めないときに空集合を返すと、別プロジェクトを自リポジトリの規約で検査してしまう。
  ok('.gitmodules が読めなければ例外（空集合＝fail-open にしない）', () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), 'excluded-units-missing-'));
    assert.throws(() => excl.excludedUnits({ root: base }), /\.gitmodules を読めませんでした/);
    fs.rmdirSync(base);
  });

  ok('lib/excluded-units.js --self-test は exit 0', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'lib', 'excluded-units.js'), '--self-test'], { encoding: 'utf8' });
    assert.strictEqual(r.status, 0, `self-test が失敗:\n${r.stdout}\n${r.stderr}`);
  });

  // --- check-cpm-versions: CPM のバージョン直書き禁止（Issue #467） ---------------

  const cpm = require('./check-cpm-versions.js');
  const cpmViolations = (xml) => cpm.inlineVersionFindings('src/x/X.csproj', xml).violations;
  const cpmOverrides = (xml) => cpm.inlineVersionFindings('src/x/X.csproj', xml).overrides;

  ok('CPM: Version 属性の直書きを違反として検出する', () => {
    assert.deepStrictEqual(cpmViolations('<PackageReference Include="X" Version="1.0.0" />'),
      [{ project: 'src/x/X.csproj', package: 'X', source: 'attribute', value: '1.0.0' }]);
    // 属性の順序に依存しない。
    assert.strictEqual(cpmViolations('<PackageReference Version="1.0.0" Include="X" />').length, 1);
  });

  ok('CPM: 子要素形（MSBuild メタデータ記法）の直書きも違反', () => {
    // 属性だけを見る実装だと素通りする経路。MSBuild では属性形と等価である。
    const found = cpmViolations('<PackageReference Include="X"><Version>2.0.0</Version></PackageReference>');
    assert.strictEqual(found.length, 1);
    assert.strictEqual(found[0].source, 'element');
    assert.strictEqual(found[0].value, '2.0.0');
  });

  ok('CPM: Update 形・プロパティ参照・空文字・単一引用符・条件付き ItemGroup も違反', () => {
    assert.strictEqual(cpmViolations('<PackageReference Update="X" Version="1.0.0" />')[0].package, 'X');
    assert.strictEqual(cpmViolations('<PackageReference Include="X" Version="$(XVersion)" />').length, 1);
    assert.strictEqual(cpmViolations('<PackageReference Include="X" Version="" />').length, 1);
    assert.strictEqual(cpmViolations("<PackageReference Include='X' Version='1.0.0' />").length, 1);
    assert.strictEqual(cpmViolations(
      '<ItemGroup Condition="\'$(TargetFramework)\'==\'net10.0\'">'
      + '<PackageReference Include="X" Version="1.0.0" /></ItemGroup>').length, 1);
  });

  ok('CPM: PackageVersion / GlobalPackageReference（中央定義）は違反にしない', () => {
    // 走査対象は .csproj のみだが、要素名の見分け自体を境界として固定しておく。
    assert.deepStrictEqual(cpmViolations('<PackageVersion Include="X" Version="1.0.0" />'), []);
    assert.deepStrictEqual(cpmViolations('<GlobalPackageReference Include="X" Version="1.0.0" />'), []);
    assert.deepStrictEqual(cpmViolations('<PackageReferenceFoo Include="X" Version="1.0.0" />'), []);
  });

  ok('CPM: コメントアウトされた例示と属性値の中の Version= は違反にしない', () => {
    assert.deepStrictEqual(cpmViolations('<!-- <PackageReference Include="X" Version="1.0.0" /> -->'), []);
    assert.deepStrictEqual(cpmViolations('<PackageReference Include="X" Condition="\'$(C)\'==\'Version=1\'" />'), []);
  });

  ok('CPM: VersionOverride は許可（違反 0）しつつ使用箇所を警告として拾う', () => {
    assert.deepStrictEqual(cpmViolations('<PackageReference Include="X" VersionOverride="1.0.0" />'), []);
    assert.strictEqual(cpmOverrides('<PackageReference Include="X" VersionOverride="1.0.0" />').length, 1);
    assert.strictEqual(
      cpmOverrides('<PackageReference Include="X"><VersionOverride>1.0.0</VersionOverride></PackageReference>').length, 1);
  });

  ok('CPM: 走査対象は .csproj（雛形の .sample 含む）のみ', () => {
    for (const p of ['src/x/X.csproj', 'templates/unit-template/backend/x/X.csproj.sample']) {
      assert.strictEqual(cpm.isScannedProjectFile(p), true, `${p} が対象外`);
    }
    // props / targets には正当な版記述（PackageVersion / GlobalPackageReference）があるため対象外。
    for (const p of ['src/Directory.Packages.props', 'src/Directory.Build.props',
      'src/Directory.Build.targets', 'src/x/X.cs', 'src/x/backend.slnx']) {
      assert.strictEqual(cpm.isScannedProjectFile(p), false, `${p} が対象になっている`);
    }
  });

  ok('CPM: 除外ユニットはハードコードせず lib/excluded-units.js から導出する', () => {
    const src = fs.readFileSync(path.join(__dirname, 'check-cpm-versions.js'), 'utf8');
    assert.doesNotMatch(src, /new Set\(\[\s*["']ai-stock-trading["']/);
    assert.match(src, /require\('\.\/lib\/excluded-units\.js'\)/);
    assert.strictEqual(cpm.isExcludedPath('src/ai-stock-trading/backend/x/X.csproj'), true);
    assert.strictEqual(cpm.isExcludedPath('src/platform/backend/x/X.csproj'), false);
  });

  ok('CPM: 実リポジトリは違反 0 件で templates/ も走査対象に入っている', () => {
    const r = cpm.scanTree();
    assert.deepStrictEqual(r.violations, [], `バージョン直書き: ${JSON.stringify(r.violations)}`);
    assert.ok(r.projects.length > 0, '走査対象が 0 件（0 件検査への退行）');
    assert.ok(r.projects.some((p) => p.startsWith('templates/')), 'templates/ が走査対象に入っていない');
  });

  ok('CPM: --self-test は exit 0（負例の実地走査を含む）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-cpm-versions.js'), '--self-test'], { encoding: 'utf8' });
    assert.strictEqual(r.status, 0, `self-test が失敗:\n${r.stdout}\n${r.stderr}`);
  });

  // 受け入れ基準（#467）「.csproj にバージョン直書きが入った PR が CI で止まる」の実測。
  // 純関数の試験だけでは「走査経路に乗っているか」「素実行が exit 1 になるか」を確かめられないため、
  // 直書きを持つ .csproj を実ツリーへ一時的に置き、子プロセスの終了コードを見る。
  // 追跡ファイルは書き換えない（テストが異常終了しても既存ファイルを壊さないよう、新規ファイルを
  // 置いて finally で消す方式にする）。
  ok('CPM: 直書きのある .csproj を置くと素実行が exit 1（負例の実効性）', () => {
    const { spawnSync } = require('child_process');
    const probe = path.join(__dirname, '..', 'src', 'platform', 'backend', 'cpm-check-probe.csproj');
    const run = () => spawnSync(process.execPath, [path.join(__dirname, 'check-cpm-versions.js')], { encoding: 'utf8' });
    assert.strictEqual(run().status, 0, '設置前は exit 0 のはず');
    fs.writeFileSync(probe,
      '<Project><ItemGroup><PackageReference Include="Probe" Version="9.9.9" /></ItemGroup></Project>\n');
    try {
      const r = run();
      assert.strictEqual(r.status, 1, `直書きを置いたのに exit ${r.status}`);
      assert.match(String(r.stderr), /バージョン直書き 1 件/);
    } finally {
      fs.rmSync(probe, { force: true });
    }
    assert.strictEqual(run().status, 0, '撤去後に exit 0 へ戻らない');
  });

  // 受け入れ基準（#467）「VersionOverride の使用箇所が実行サマリに出る」の実測。
  // inlineVersionFindings() の検出だけを試験すると、出力側 reportOverrides() が壊れても緑のままになる
  // （警告が出ないことは終了コードに現れないため、CI も緑で通る）。よって出力経路ごと子プロセスで見る。
  ok('CPM: VersionOverride は exit 0 のまま実行サマリとアノテーションへ出る（出力経路）', () => {
    const { spawnSync } = require('child_process');
    const probe = path.join(__dirname, '..', 'src', 'platform', 'backend', 'cpm-override-probe.csproj');
    const summary = path.join(os.tmpdir(), `cpm-summary-${process.pid}-${Date.now()}.md`);
    fs.writeFileSync(probe,
      '<Project><ItemGroup><PackageReference Include="OverrideProbe" VersionOverride="9.9.9" />'
      + '</ItemGroup></Project>\n');
    try {
      const r = spawnSync(process.execPath, [path.join(__dirname, 'check-cpm-versions.js')], {
        encoding: 'utf8',
        // GITHUB_ACTIONS=true で ci-annotate が workflow コマンド（::warning::）を stdout へ出す。
        env: { ...process.env, GITHUB_ACTIONS: 'true', GITHUB_STEP_SUMMARY: summary },
      });
      // 許可であって違反ではない: 終了コードは 0 のまま。
      assert.strictEqual(r.status, 0, `VersionOverride で exit ${r.status}（許可のはず）`);
      assert.match(String(r.stdout), /::warning::CPM の VersionOverride を 1 件使用しています/);
      assert.match(String(r.stdout), /OverrideProbe=9\.9\.9/);
      const written = fs.readFileSync(summary, 'utf8');
      assert.match(written, /### CPM: VersionOverride の使用箇所/);
      assert.match(written, /OverrideProbe/);
      assert.match(written, /9\.9\.9/);
    } finally {
      fs.rmSync(probe, { force: true });
      fs.rmSync(summary, { force: true });
    }
    // 撤去後はサマリへも警告へも出ない（プローブが残って恒常的に警告が出る状態にしない）。
    const after = spawnSync(process.execPath, [path.join(__dirname, 'check-cpm-versions.js')], {
      encoding: 'utf8', env: { ...process.env, GITHUB_ACTIONS: 'true' },
    });
    assert.strictEqual(after.status, 0);
    assert.doesNotMatch(String(after.stdout), /VersionOverride を [1-9]/);
  });

  // --- check-contract-schema: Shared.Contracts の後方互換（Issue #465 / IADR-0122） -----

  const contracts = require('./check-contract-schema.js');
  const cTypes = (src) => contracts.extractTypes(src);
  const cOne = (src) => { const r = cTypes(src); return r[Object.keys(r)[0]]; };
  const cSnap = (types) => ({ 'src/x/backend/Shared/X.Contracts': types });
  const cDiff = (a, b) => contracts.diffSnapshots(cSnap(a), cSnap(b));
  const cRec = (members) => ({ kind: 'record', members });
  const cReq = (type, position) => ({ source: 'positional', type, required: true, position });
  const cOpt = (type, position) => ({ source: 'positional', type, required: false, position });

  ok('契約: 位置引数 record の型・必須・位置を抽出する', () => {
    const e = cTypes('namespace N;\npublic record E(Guid Id, string? Note = null);')['N.E'];
    assert.strictEqual(e.kind, 'record');
    assert.strictEqual(e.members.Id.required, true);
    assert.strictEqual(e.members.Id.position, 0);
    assert.strictEqual(e.members.Note.required, false);
    assert.strictEqual(e.members.Note.type, 'string?');
  });

  ok('契約: ジェネリクス内のカンマで位置引数を割らない（配列型も読む）', () => {
    // 素朴な split(',') だと Dictionary<string, List<string>> が 2 引数に割れて契約が壊れて記録される。
    assert.strictEqual(
      cOne('namespace N;\npublic record E(Dictionary<string, List<string>>? A = null);').members.A.type,
      'Dictionary<string,List<string>>?');
    assert.strictEqual(cOne('namespace N;\npublic record E(float[] V);').members.V.type, 'float[]');
  });

  ok('契約: enum の暗黙序数を計算する（並べ替えを値の変更として捕まえるため）', () => {
    const e = cOne('namespace N;\npublic enum E { A, B, C }');
    assert.deepStrictEqual([e.members.A.value, e.members.B.value, e.members.C.value], [0, 1, 2]);
    const f = cOne('namespace N;\npublic enum F { A = 5, B }');
    assert.deepStrictEqual([f.members.A.value, f.members.B.value], [5, 6]);
  });

  ok('契約: const の値と型・メンバーの属性まで固定する', () => {
    // "queued" 等のリテラルは配線そのもの。属性はシリアライズ表現（enum の文字列化・JSON 名）を変える。
    assert.strictEqual(
      cOne('namespace N;\npublic static class C { public const string A = "queued"; }').members.A.value,
      '"queued"');
    assert.deepStrictEqual(
      cOne('namespace N;\n[JsonConverter(typeof(JsonStringEnumConverter<E>))]\npublic enum E { A }').attributes,
      ['JsonConverter(typeof(JsonStringEnumConverter<E>))']);
    assert.deepStrictEqual(
      cOne('namespace N;\npublic class D { [JsonPropertyName("t")]\n public string T { get; init; } = ""; }')
        .members.T.attributes, ['JsonPropertyName("t")']);
  });

  ok('契約: internal 型・public メソッド・コメント中の宣言はスキーマに含めない', () => {
    assert.strictEqual(Object.keys(cTypes('namespace N;\ninternal record E(string A);')).length, 0);
    assert.strictEqual(
      cOne('namespace N;\npublic static class C { public const string A = "a";\n'
        + ' public static bool IsValid(string? x) => true; }').members.IsValid, undefined);
    assert.strictEqual(
      Object.keys(cTypes('namespace N;\n// public record Ghost(string A);\npublic record E(string A);')).length, 1);
  });

  ok('契約: 文字列リテラル中の // をコメントと誤認しない', () => {
    // storage:// のようなリテラルを持つ const を壊さないための境界。
    assert.match(contracts.stripComments('var x = "storage://a"; // c'), /storage:\/\/a/);
  });

  ok('契約: 削除・型変更・必須化・並べ替えは破壊的', () => {
    assert.strictEqual(cDiff({ 'N.A': cRec({ X: cReq('int', 0) }) }, { 'N.A': cRec({}) })[0].severity, 'breaking');
    assert.strictEqual(
      cDiff({ 'N.A': cRec({ X: cReq('int', 0) }) }, { 'N.A': cRec({ X: cReq('long', 0) }) })[0].kind,
      'memberTypeChanged');
    assert.strictEqual(
      cDiff({ 'N.A': cRec({ X: cOpt('int', 0) }) }, { 'N.A': cRec({ X: cReq('int', 0) }) })[0].kind,
      'memberRequired');
    assert.strictEqual(
      cDiff({ 'N.A': cRec({ X: cReq('int', 0), Y: cReq('int', 1) }) },
        { 'N.A': cRec({ X: cReq('int', 1), Y: cReq('int', 0) }) })
        .filter((c) => c.kind === 'memberReordered').length, 2);
  });

  ok('契約: 既定値付きの追加は非破壊・既定値の無い追加は破壊的', () => {
    // 既定値が無ければ旧発行者のメッセージが必須項目を欠く（＝後方互換ではない）。逃げ道は既定値を付けること。
    assert.strictEqual(cDiff({ 'N.A': cRec({}) }, { 'N.A': cRec({ X: cOpt('int', 0) }) })[0].severity, 'additive');
    assert.strictEqual(cDiff({ 'N.A': cRec({}) }, { 'N.A': cRec({ X: cReq('int', 0) }) })[0].kind,
      'memberAddedRequired');
  });

  ok('契約: 承認エントリは 5 項目すべてを必須にする', () => {
    const good = { key: 'typeRemoved:N.A', reason: 'r', approvedBy: 'a', issue: '#1', date: '2026-08-04' };
    assert.deepStrictEqual(contracts.validateApprovals([good]), []);
    for (const f of ['key', 'reason', 'approvedBy', 'issue', 'date']) {
      assert.ok(contracts.validateApprovals([{ ...good, [f]: '' }]).some((e) => e.includes(f)),
        `${f} が空でも通ってしまう（理由・承認者の残らない承認は記録にならない）`);
    }
    assert.ok(contracts.validateApprovals([{ ...good, date: '2026/08/04' }]).length > 0);
    assert.ok(contracts.validateApprovals([{ ...good, issue: '123' }]).length > 0);
  });

  ok('契約: 承認は破壊的変更を通し、対応する変更が無ければ stale として検出する', () => {
    const baseline = { projects: cSnap({ 'N.A': cRec({ X: cReq('int', 0) }) }) };
    const removed = { projects: cSnap({ 'N.A': cRec({}) }) };
    const key = 'memberRemoved:N.A.X';
    const approval = { key, reason: 'r', approvedBy: 'a', issue: '#465', date: '2026-08-04' };
    const e0 = contracts.evaluate({ snapshot: removed, baseline, allowlist: { approvals: [] } });
    assert.strictEqual(e0.unapproved.length, 1);
    assert.strictEqual(e0.unapproved[0].key, key);
    const e1 = contracts.evaluate({ snapshot: removed, baseline, allowlist: { approvals: [approval] } });
    assert.strictEqual(e1.unapproved.length, 0);
    assert.strictEqual(e1.approved.length, 1);
    const e2 = contracts.evaluate({
      snapshot: { projects: baseline.projects }, baseline, allowlist: { approvals: [approval] },
    });
    assert.strictEqual(e2.stale.length, 1, '承認だけが残ると次の破壊的変更を黙って通す');
  });

  ok('契約: 承認の記録は baseline の $acceptedBreakingChanges へ追記され消えない', () => {
    const nb = contracts.nextBaseline({
      snapshot: { projects: { p: {} } },
      baseline: { $acceptedBreakingChanges: [{ key: 'old' }], projects: {} },
      approved: [{ key: 'typeRemoved:N.A', reason: 'r', approvedBy: 'a', issue: '#1', date: '2026-08-04' }],
    });
    assert.strictEqual(nb.$acceptedBreakingChanges.length, 2);
    assert.strictEqual(contracts.emptyAllowlist().approvals.length, 0);
  });

  ok('契約: 除外ユニットはハードコードせず lib/excluded-units.js から導出する', () => {
    const src = fs.readFileSync(path.join(__dirname, 'check-contract-schema.js'), 'utf8');
    assert.doesNotMatch(src, /new Set\(\[\s*["']ai-stock-trading["']/);
    assert.match(src, /require\('\.\/lib\/excluded-units\.js'\)/);
    assert.strictEqual(contracts.isExcludedPath('src/ai-stock-trading/backend/Shared/X.Contracts'), true);
    assert.strictEqual(contracts.isExcludedPath('src/platform/backend/Shared/Platform.Shared.Contracts'), false);
  });

  ok('契約: 実リポジトリは baseline と一致し、契約プロジェクトを 2 件検出する', () => {
    const found = contracts.findContractProjects();
    assert.deepStrictEqual(found, [
      'src/knowledge/backend/Shared/Knowledge.Contracts',
      'src/platform/backend/Shared/Platform.Shared.Contracts',
    ], '走査経路の退行（0 件検査・対象取りこぼし）');
    const r = contracts.evaluate({
      snapshot: contracts.buildSnapshot(),
      baseline: JSON.parse(fs.readFileSync(contracts.BASELINE_FILE, 'utf8')),
      allowlist: JSON.parse(fs.readFileSync(contracts.ALLOWLIST_FILE, 'utf8')),
    });
    assert.deepStrictEqual(r.changes, [], `baseline との差分: ${JSON.stringify(r.changes)}`);
    assert.deepStrictEqual(r.stale, []);
    assert.deepStrictEqual(r.approvalErrors, []);
  });

  ok('契約: --self-test は exit 0（負例の実地走査を含む）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-contract-schema.js'), '--self-test'],
      { encoding: 'utf8' });
    assert.strictEqual(r.status, 0, `self-test が失敗:\n${r.stdout}\n${r.stderr}`);
  });

  // 受け入れ基準（#465）「後方互換を壊す契約変更が CI で止まる」の実測。
  // 純関数の試験だけでは「走査経路に乗っているか」「素実行が exit 1 になるか」を確かめられないため、
  // 破壊的変更を持つ .cs を実ツリーへ一時的に置き、子プロセスの終了コードを見る。
  // 追跡ファイルは書き換えない（テストが異常終了しても既存の契約を壊さないよう、新規ファイルを
  // 置いて finally で消す方式にする）。
  ok('契約: 契約に型を足すと素実行が exit 1（走査経路が実ツリーに効いていること）', () => {
    const { spawnSync } = require('child_process');
    const probe = path.join(__dirname, '..', 'src', 'knowledge', 'backend', 'Shared',
      'Knowledge.Contracts', 'Events', 'ContractCheckProbe.cs');
    const run = () => spawnSync(process.execPath,
      [path.join(__dirname, 'check-contract-schema.js')], { encoding: 'utf8' });
    assert.strictEqual(run().status, 0, '設置前は exit 0 のはず');
    // 非破壊（型の追加）でも baseline と差分がある限り止める＝契約変更を必ず PR の diff に載せる設計。
    fs.writeFileSync(probe,
      'namespace Knowledge.Contracts.Events;\npublic record ContractCheckProbe(Guid Id);\n');
    try {
      const r = run();
      assert.strictEqual(r.status, 1, `型を足したのに exit ${r.status}`);
      assert.match(String(r.stderr), /typeAdded:Knowledge\.Contracts\.Events\.ContractCheckProbe/);
      assert.match(String(r.stderr), /--update/);
    } finally {
      fs.rmSync(probe, { force: true });
    }
    assert.strictEqual(run().status, 0, '撤去後に exit 0 へ戻らない');
  });

  // 受け入れ基準（#465）「破壊的変更が CI で止まり、承認で通る」の出力経路まで含めた実測。
  // 判定関数だけを試験すると、承認済みの可視化（reportApproved）が壊れても終了コードに現れない。
  ok('契約: 破壊的変更は素実行で exit 1・承認済みは実行サマリとアノテーションへ出る（出力経路）', () => {
    const { spawnSync } = require('child_process');
    const baselinePath = contracts.BASELINE_FILE;
    const allowPath = contracts.ALLOWLIST_FILE;
    const baselineOrig = fs.readFileSync(baselinePath, 'utf8');
    const allowOrig = fs.readFileSync(allowPath, 'utf8');
    const summary = path.join(os.tmpdir(), `contract-summary-${process.pid}-${Date.now()}.md`);
    const run = (env = {}) => spawnSync(process.execPath,
      [path.join(__dirname, 'check-contract-schema.js')],
      { encoding: 'utf8', env: { ...process.env, ...env } });
    // baseline 側へ「実在しないメンバー」を足すと、実ツリーとの差分は削除（＝破壊的）として現れる。
    // 実ファイル（契約そのもの）は一切書き換えない。
    const key = 'memberRemoved:Knowledge.Contracts.Events.IngestionCompleted.__Probe';
    try {
      const b = JSON.parse(baselineOrig);
      b.projects['src/knowledge/backend/Shared/Knowledge.Contracts']
        ['Knowledge.Contracts.Events.IngestionCompleted'].members.__Probe =
          { source: 'positional', type: 'int', required: true, position: 9 };
      fs.writeFileSync(baselinePath, JSON.stringify(b, null, 2) + '\n');

      const unapproved = run();
      assert.strictEqual(unapproved.status, 1, '未承認の破壊的変更で exit 1 にならない');
      assert.match(String(unapproved.stderr), /後方互換を壊す契約変更が 1 件あります/);
      assert.match(String(unapproved.stderr), new RegExp(key.replace(/\./g, '\\.')));
      assert.match(String(unapproved.stderr), /contract-breaking-allowlist\.json/);

      const a = JSON.parse(allowOrig);
      a.approvals = [{ key, reason: '受け入れ基準の実測', approvedBy: 'test', issue: '#465', date: '2026-08-04' }];
      fs.writeFileSync(allowPath, JSON.stringify(a, null, 2) + '\n');

      const approved = run({ GITHUB_ACTIONS: 'true', GITHUB_STEP_SUMMARY: summary });
      // 承認しても baseline が古いままなら止める（--update で差分を PR に載せさせるため）。
      assert.strictEqual(approved.status, 1, '承認済みでも baseline 未更新なら止めるはず');
      assert.match(String(approved.stdout), /::warning::承認済みの破壊的な契約変更が 1 件あります/);
      assert.match(String(approved.stderr), /--update/);
      const written = fs.readFileSync(summary, 'utf8');
      assert.match(written, /### 契約: 承認済みの破壊的変更/);
      assert.match(written, /受け入れ基準の実測/);

      // 対応する変更が無い承認（stale）は fail する。
      fs.writeFileSync(baselinePath, baselineOrig);
      const stale = run();
      assert.strictEqual(stale.status, 1, 'stale な承認で exit 1 にならない');
      assert.match(String(stale.stderr), /対応する変更が無い承認が 1 件残っています/);
    } finally {
      fs.writeFileSync(baselinePath, baselineOrig);
      fs.writeFileSync(allowPath, allowOrig);
      fs.rmSync(summary, { force: true });
    }
    assert.strictEqual(run().status, 0, '復元後に exit 0 へ戻らない');
  });
  // --- Issue #496 / ADR-0031 / IADR-0125: i18n カタログ検査と静的 egress 検査 -------

  // 各スクリプトの --self-test（正例・負例を含む）を子プロセスで走らせる。
  // 本体の純粋ロジックはそこで網羅しているので、ここでは「自己試験が通ること」と
  // 「本リポの実データに対して現に green であること」を固定する。
  for (const script of ['check-i18n-catalogs.js', 'check-static-egress.js']) {
    ok(`${script} --self-test が通る`, () => {
      const { spawnSync } = require('child_process');
      const r = spawnSync(process.execPath, [path.join(__dirname, script), '--self-test'], {
        encoding: 'utf8',
      });
      assert.strictEqual(r.status, 0, `${script} の自己試験が失敗した:\n${r.stdout}\n${r.stderr}`);
      assert.match(String(r.stdout), /all passed/);
    });
  }

  ok('check-i18n-catalogs: 本リポのカタログに未翻訳が無い（実データ）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(
      process.execPath,
      [path.join(__dirname, 'check-i18n-catalogs.js')],
      { encoding: 'utf8' },
    );
    assert.strictEqual(r.status, 0, `未翻訳・fuzzy・obsolete が残っている:\n${r.stderr}`);
  });

  // 退行防止: Lingui のカタログ検査が見ているロケール集合が、i18n の実装が対応すると
  // 宣言しているロケール集合と一致していること。片方だけ増えると「宣言はしたが訳が無い」
  // あるいは「訳はあるが読み込まれない」状態が静かに生まれる。
  ok('lingui.config.ts の locales と foundation/i18n の SUPPORTED_LOCALES が一致する', () => {
    const { parseLinguiConfig } = require('./check-i18n-catalogs.js');
    const root = path.resolve(__dirname, '..');
    const cfg = parseLinguiConfig(fs.readFileSync(path.join(root, 'src/lingui.config.ts'), 'utf8'));
    const i18nSrc = fs.readFileSync(
      path.join(root, 'src/platform/frontend/src/foundation/i18n/index.ts'),
      'utf8',
    );
    const m = /SUPPORTED_LOCALES\s*=\s*\[([^\]]*)\]/.exec(i18nSrc);
    assert.ok(m, 'foundation/i18n から SUPPORTED_LOCALES を読み取れない');
    const supported = m[1]
      .split(',')
      .map((x) => x.trim().replace(/^['"]|['"]$/g, ''))
      .filter(Boolean);
    assert.deepStrictEqual(
      [...supported].sort(),
      [...cfg.locales].sort(),
      'lingui.config.ts の locales と SUPPORTED_LOCALES が食い違っている',
    );
  });

  // 退行防止: 08_data-egress-policy が禁じる代表的なホストが、検査器の禁止リストから
  // 抜け落ちていないこと（リストを空にしても自己試験の大半は通ってしまうため）。
  ok('check-static-egress: 計画が名指しする禁止先が検査対象に入っている', () => {
    const { FORBIDDEN_HOSTS, inspectFile } = require('./check-static-egress.js');
    // 08_data-egress-policy §SPA フロントエンド: 外部CDN・Webフォント（Google Fonts等）・
    // 解析（analytics）・エラー報告SaaS。
    for (const host of ['fonts.googleapis.com', 'www.google-analytics.com', 'cdn.jsdelivr.net']) {
      assert.ok(FORBIDDEN_HOSTS.includes(host), `${host} が禁止リストに無い`);
      assert.ok(
        inspectFile('bundle.js', `x="https://${host}/a"`).some((h) => h.kind === 'forbidden-host'),
        `${host} を検出できない`,
      );
    }
  });

  // --- Issue #510 / IADR-0130: 実在するテストがテスト仕様書に載っているかの検査 --------
  //
  // ここに置く理由: .github/workflows/ は GitHub App 権限では編集できないため、専用ジョブを
  // 足せない。ci.yml の scripts-tests ジョブ（REQUIRE_REPO_TESTS=1）が本 companion を実行する
  // ので、**自己試験と実データの本走をここへ置くことで CI ゲートになる**（check-i18n-catalogs.js
  // の実データ検査と同じ結線）。専用ステップ化は任意の改善として別途行う。

  ok('check-test-spec-coverage --self-test が通る（ratchet 4 判定・fail-closed の負例を含む）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(
      process.execPath,
      [path.join(__dirname, 'check-test-spec-coverage.js'), '--self-test'],
      { encoding: 'utf8' },
    );
    assert.strictEqual(r.status, 0, `自己試験が失敗:\n${r.stdout}\n${r.stderr}`);
    assert.match(String(r.stdout), /自己試験 \d+ 件 OK/);
  });

  ok('check-test-spec-coverage: 本リポの実データが green（節の消失が無い）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(
      process.execPath,
      [path.join(__dirname, 'check-test-spec-coverage.js')],
      { encoding: 'utf8' },
    );
    assert.strictEqual(
      r.status,
      0,
      `docs/tests/ の記載が床を割っている（#510 の再発の可能性）:\n${r.stdout}\n${r.stderr}`,
    );
  });

  // 退行防止: #510 で復帰させた 4 クラスが床に残っていること。
  // 床は --update で誰でも下げられるため、「節を消して床も下げる」で黙らせる経路が残る。
  // 本 issue の核心にあたるクラスだけは、床とは別に固定して差分をレビューへ強制的に出す。
  ok('#510 で復帰させたバックエンドテストが床に残っている', () => {
    const { readBaseline } = require('./check-test-spec-coverage.js');
    const documented = new Set(readBaseline());
    for (const name of [
      'BffDocumentWriteEndpointTests', // SC-05 §BFF（書き込み）
      'BffDataSourceEndpointTests', // SC-06 §BFF
      'DocumentVersioningTests', // SC-05 §状態遷移ガード（ドメイン）
      'DocumentEndpointVersioningTests', // SC-05 §状態遷移ガード（API）
    ]) {
      assert.ok(
        documented.has(name),
        `${name} が scripts/test-spec-coverage-baseline.json から消えている` +
          '（#510 が復帰させた記載を再び落としていないか docs/tests/SC-05・SC-06 を確認すること）',
      );
    }
  });
};
