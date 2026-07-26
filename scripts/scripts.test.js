#!/usr/bin/env node
'use strict';
/*
 * scripts.test.js
 * check-commit-messages.js / gen-changelog.js の主要ロジックの単体テスト（Issue #60）。
 * 外部依存ゼロ（Node 標準 assert のみ）。実行: node scripts/scripts.test.js
 */
const assert = require('assert');
const {
  validateSubject,
  checkSingleTitle,
  findAllowlisted,
  loadAllowlist,
} = require('./check-commit-messages.js');
const { applyOverride, hashMatches } = require('./gen-changelog.js');

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

// --- validateSubject ---------------------------------------------------------

// 起点 ID を持つ正しい件名は合格する。
ok('feat(FR-08) は合格', () => assert.deepStrictEqual(validateSubject('feat(FR-08): ログイン実装'), []));
ok('ci(NFR) は合格', () => assert.deepStrictEqual(validateSubject('ci(NFR): CI 整合'), []));
ok('複数 ID 併記は合格', () => assert.deepStrictEqual(validateSubject('feat(FR-08,UC-03): 実装'), []));
ok('P0 フェーズ ID は合格', () => assert.deepStrictEqual(validateSubject('docs(P0): 骨格仕様'), []));
ok('末尾 PR 番号は許容', () => assert.deepStrictEqual(validateSubject('fix(FR-01): 修正 (#123)'), []));

// 抜け穴（Issue #60 の 🔴 指摘）: 内容変更の種別で起点 ID が無ければ違反として検出する。
ok('feat（ID 無し）は違反', () => {
  const r = validateSubject('feat: 説明');
  assert.strictEqual(r.length >= 1, true, '違反理由が返るべき');
  assert.match(r.join(' '), /起点 ID が無い/);
});
ok('fix（ID 無し）は違反', () => assert.strictEqual(validateSubject('fix: サブプロジェクト更新').length >= 1, true));
ok('docs（ID 無し）は違反', () => assert.strictEqual(validateSubject('docs: 説明追記').length >= 1, true));

// 雑多・ツールチェーン種別は ID 省略を許す。
ok('chore（ID 無し）は合格', () => assert.deepStrictEqual(validateSubject('chore: 依存更新'), []));
ok('style（ID 無し）は合格', () => assert.deepStrictEqual(validateSubject('style: 整形'), []));

// 書式・種別・ID 書式の異常。
ok('形式不一致は違反', () => assert.strictEqual(validateSubject('いきなり日本語件名').length >= 1, true));
ok('未知の種別は違反', () => assert.strictEqual(validateSubject('feet(FR-01): typo type').length >= 1, true));
ok('不正な ID 書式は違反', () => assert.strictEqual(validateSubject('feat(FR08): ハイフン無し').length >= 1, true));
ok('空スコープは違反', () => assert.strictEqual(validateSubject('feat(): 空').length >= 1, true));

// --- check-commit-messages: checkSingleTitle（PR タイトル＝スカッシュ後件名の検査・Issue #125） ---

// stdout/stderr を抑止して戻り値（0=合格/1=違反）のみ検査する。
function silent(fn) {
  const so = process.stdout.write;
  const se = process.stderr.write;
  process.stdout.write = () => true;
  process.stderr.write = () => true;
  try {
    return fn();
  } finally {
    process.stdout.write = so;
    process.stderr.write = se;
  }
}

ok('PR タイトル 正常件名は 0', () =>
  assert.strictEqual(silent(() => checkSingleTitle('feat(FR-08): ログイン実装')), 0));
ok('PR タイトル 末尾(#123)は 0', () =>
  assert.strictEqual(silent(() => checkSingleTitle('fix(FR-01): 修正 (#123)')), 0));
ok('PR タイトル 規約外は 1', () =>
  assert.strictEqual(silent(() => checkSingleTitle('update stuff')), 1));
ok('PR タイトル 起点ID欠落の feat は 1', () =>
  assert.strictEqual(silent(() => checkSingleTitle('feat: 説明 (#42)')), 1));
ok('PR タイトル 空は 0（fail-open）', () =>
  assert.strictEqual(silent(() => checkSingleTitle('   ')), 0));
ok('PR タイトル Revert はスキップ扱いで 0', () =>
  assert.strictEqual(silent(() => checkSingleTitle('Revert "feat(FR-08): x"')), 0));
ok('PR タイトル [skip ci] はスキップ扱いで 0', () =>
  assert.strictEqual(silent(() => checkSingleTitle('なんでも [skip ci]')), 0));

// --- check-commit-messages: findAllowlisted（規約導入前コミットの恒久除外） ---

ok('allowlist は短縮 SHA を前方一致で照合', () => {
  const al = [{ hash: 'd1652dc', reason: 'x' }];
  assert.ok(findAllowlisted('d1652dcf44ba3dfff6c4f5797defc38d1b863ca8', al), '前方一致で除外されるべき');
  assert.strictEqual(findAllowlisted('deadbeefdeadbeef', al), null, '無関係な SHA は除外されない');
});

// 規約導入前の非準拠 5 コミットが commit-allowlist.json で除外されること（本 PR の CI 失敗の回帰）。
ok('規約導入前の非準拠5コミットは allowlist 対象', () => {
  const al = loadAllowlist();
  const known = ['d1652dcf', '394fa1fd', '079490d1', '153810a4', 'd4835097'];
  for (const h of known) {
    assert.ok(findAllowlisted(h, al), `${h} が commit-allowlist.json に無い`);
  }
});

// --- gen-changelog: hashMatches / applyOverride ------------------------------

ok('hashMatches は短縮 SHA を前方一致', () => {
  assert.strictEqual(hashMatches('b4217619abc', 'b421761'), true);
  assert.strictEqual(hashMatches('b421761', 'b4217619abc'), true);
  assert.strictEqual(hashMatches('deadbeef', 'b421761'), false);
});

// 実在の override（b421761）が feat/P0 に補正されること（🔴 指摘の回帰: docs へ誤 remap しない）。
ok('b421761 は feat/P0 へ remap', () => {
  const c = applyOverride({ hash: 'b421761abc', type: 'feat', scope: 'FR-10', desc: '元件名' });
  assert.notStrictEqual(c, null, 'exclude されるべきではない');
  assert.strictEqual(c.type, 'feat', 'docs へ誤 remap してはならない');
  assert.strictEqual(c.scope, 'P0');
});

// override に一致しないコミットは素通しする。
ok('未一致コミットは素通し', () => {
  const c = { hash: 'ffffffff', type: 'fix', scope: 'FR-01', desc: 'x' };
  assert.deepStrictEqual(applyOverride(c), c);
});

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

process.stdout.write(`\n✓ ${passed} tests passed\n`);
