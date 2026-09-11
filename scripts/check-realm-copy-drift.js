#!/usr/bin/env node
'use strict';
/*
 * check-realm-copy-drift.js
 * **基盤レルムの宣言（正本）と AST 専用レルムの宣言（写し）のずれ**を機械検査する
 * （#1412 / 計画 AST/ADR-0038 決定 3・フォローアップ 2 / IADR-0434）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。check-unit-service-ownership.js と同型。
 *
 * 背景: 計画 AST/ADR-0038 決定 3 は「`trading-owner` / `trading-service` と連結配備のクライアントの
 * **正本は基盤レルムの宣言**（deploy/keycloak/microservices-platform-realm.json）であり、AST 専用レルムの
 * 同名ロールは**写し**」と定めた。同 ADR の統制表は決定 3 について実現手段も暫定手段も「🔴 無い」と書き、
 * §残るもの が「**写しのずれを検知する手段が無い**」を残件に挙げている。
 *   🔴 **連結配備では基盤レルム側しか読まれない。** だから写しが古くなっても連結配備の挙動には出ない ——
 *   出るのは単体 E2E であり、それは統制ではなく副作用である（同 ADR の表現）。
 * 突合をこちら（基盤リポ）に置く理由: **AST の CI は基盤リポを読めないが、基盤の CI は submodule
 * `src/ai-stock-trading` を持つ。** 両辺が揃うのは基盤側だけである。
 *
 * 突合する客体（**列挙を書かず接頭辞で導出する**。#1412 の要求）:
 *   - realm ロール: 名前が `trading-` で始まるもの（両レルムの**和集合**）。
 *   - クライアント: `clientId` が `ai-stock-trading-` で始まるもの（両レルムの**和集合**）。
 *   🔴 **交差集合ではなく和集合である。** 交差で取ると、**写しから 1 つ落としたときに交差が縮むだけで
 *   赤にならない**（検出したい事故がそのまま素通りする）。
 *
 * 比較するフィールド:
 *   - ロール: 両レルムでの存在 / `composite` / `composites` / `attributes` / `description` の**有無**
 *   - クライアント: 両レルムでの存在 / `serviceAccountsEnabled` / `directAccessGrantsEnabled` /
 *     `publicClient` / `standardFlowEnabled` / **service account の realm ロール付与**
 *     （`users[].serviceAccountClientId` から引く）
 *
 * 比較しないもの（**理由つきで固定する**。黙って落とさない）:
 *   - 🔴 **`secret` は読まない・出さない。** 両レルムとも dev 既定値を持つため、突合すると差分メッセージへ
 *     平文が出る。読むフィールドを allowlist で固定し、自己試験で「報告に secret が現れない」ことを固定する。
 *   - `description` / `name` の**本文**。両者は独立に書かれた散文である（基盤側は日本語の根拠＋ issue 番号、
 *     AST 側は英語の dev 注記）。**バイト一致を課すと常時赤になり、検査器ごと無視されるようになる** ——
 *     `check-prometheus-alerts-parity.js` が `summary` / `description` を突合しないのと同じ判断。
 *     **「片方だけ説明が消える」ことは有無で捕まえる。**
 *   - `redirectUris` / `webOrigins`。連結配備と単体起動で**正当に違い得る**（AST 専用レルムは単体 E2E 用）。
 *
 * 片側だけに在ってよいものは `ONE_SIDED_CLIENTS` / `ONE_SIDED_ROLES` へ**理由つきで宣言する**。
 * 宣言に無い名前が片側だけに在れば赤、宣言に在れば **notice で必ず見せる**（exit には影響させない）。
 * `check-unit-service-ownership.js` の `NAME_COLLISION_EXEMPT` と同型 —— **黙って効く除外を作らない。**
 *
 * 縮退: AST 側 realm を**見つけられないとき**だけ `::warning::` で「**突合していない**」と明示して exit 0。
 *   🔴 **「差分 0 件」とは書かない**（`check-planning-adr-range.js` の `scanned: 0` の教訓 —— 0 は
 *   「ずれが無い」ではなく「検査が動いていない」）。基盤側 realm の欠落では縮退しない（追跡下のファイルである）。
 *
 * 使い方:
 *   node scripts/check-realm-copy-drift.js             # 実ファイルを突合。差分があれば終了コード 1。
 *   node scripts/check-realm-copy-drift.js --self-test # 検査ロジック自体の自己試験。
 */
const fs = require('fs');
const path = require('path');
const { warn } = require('./lib/ci-annotate');

const REPO_ROOT = path.resolve(__dirname, '..');

// 正本（計画 AST/ADR-0038 決定 3）。
const PLATFORM_REALM = 'deploy/keycloak/microservices-platform-realm.json';

// 写しの置き場の候補。🔴 **1 本に決め打ちしない** —— #1412 本文は `deploy/` 配下と書いているが、
// submodule pin db3cfe8 の実物は `infra/keycloak/realm-export.json` である。AST 側が移しても
// 追随できるよう、候補ディレクトリを走査して `*realm*.json` を拾う（見つからなければ縮退）。
const AST_REALM_DIRS = [
  'src/ai-stock-trading/infra/keycloak',
  'src/ai-stock-trading/deploy/keycloak',
];
const AST_REALM_PATTERN = /realm.*\.json$/i;

// 突合の客体を導出する接頭辞（列挙は書かない）。
const ROLE_PREFIX = 'trading-';
const CLIENT_PREFIX = 'ai-stock-trading-';

// 比較するクライアントのフラグと **Keycloak の既定値**。片方が明示・片方が省略という書き分けだけで
// 赤にしないため、比較の前に既定へ正規化する（意味が同じだからである）。
const CLIENT_FLAGS = [
  'serviceAccountsEnabled',
  'directAccessGrantsEnabled',
  'publicClient',
  'standardFlowEnabled',
];
const CLIENT_FLAG_DEFAULTS = {
  serviceAccountsEnabled: false,
  directAccessGrantsEnabled: true,
  publicClient: false,
  standardFlowEnabled: true,
};

// 片側のレルムにしか無くてよいクライアント（clientId → 理由）。**理由が書けないなら足さない。**
const ONE_SIDED_CLIENTS = new Map([
  [
    'ai-stock-trading-dev',
    'AST 単体起動・単体 E2E 専用の public client（AST/IADR-0050）。連結配備では使わないため基盤レルムに置かない。',
  ],
  [
    'ai-stock-trading-kb-writer',
    'KB 書き込みの cross-unit s2s。基盤レルム専用である（AST/IADR-0093。AST レルムのトークンは issuer 不一致で通らない）。',
  ],
  [
    'ai-stock-trading-llm-caller',
    'LlmGateway 呼び出しの cross-unit s2s。基盤レルム専用である（AST#724 / MSP#1364）。',
  ],
]);

// 片側のレルムにしか無くてよい realm ロール（name → 理由）。**現在 0 件**（実測: `trading-owner` /
// `trading-service` はどちらのレルムにも在る）。機構だけ用意し、足すときは理由を書く。
const ONE_SIDED_ROLES = new Map();

// --- 純粋ロジック（scripts.test.js / --self-test から単体テストする） -------------

// 配列・オブジェクトを順序非依存の安定表現へ畳む（`composites` / `attributes` の深い一致に使う）。
// realm JSON のこれらの値は**順序に意味が無い**ため、並び替えだけで赤にしない。
function stable(value) {
  if (Array.isArray(value)) return value.map(stable).sort(compareJson);
  if (value && typeof value === 'object') {
    const out = {};
    for (const key of Object.keys(value).sort()) out[key] = stable(value[key]);
    return out;
  }
  return value === undefined ? null : value;
}

function compareJson(a, b) {
  const sa = JSON.stringify(a);
  const sb = JSON.stringify(b);
  return sa < sb ? -1 : sa > sb ? 1 : 0;
}

function sameShape(a, b) {
  return JSON.stringify(stable(a)) === JSON.stringify(stable(b));
}

// realm ロールを name → ロール定義の Map で返す。
function realmRoles(realm) {
  const list = (realm && realm.roles && realm.roles.realm) || [];
  const out = new Map();
  for (const role of list) if (role && typeof role.name === 'string') out.set(role.name, role);
  return out;
}

// クライアントを clientId → クライアント定義の Map で返す。
function realmClients(realm) {
  const list = (realm && realm.clients) || [];
  const out = new Map();
  for (const client of list) {
    if (client && typeof client.clientId === 'string') out.set(client.clientId, client);
  }
  return out;
}

// clientId の service account に付いた realm ロールを昇順の配列で返す。
// `users[]` に該当の service account が居なければ null（「未宣言」と「空」を区別する）。
function serviceAccountRealmRoles(realm, clientId) {
  const users = (realm && realm.users) || [];
  const user = users.find((u) => u && u.serviceAccountClientId === clientId);
  if (!user) return null;
  return [...new Set(user.realmRoles || [])].sort();
}

// 2 つの名前集合から、接頭辞に一致する名前の**和集合**を昇順で返す。
function unionWithPrefix(namesA, namesB, prefix) {
  const all = new Set([...namesA, ...namesB].filter((n) => n.startsWith(prefix)));
  return [...all].sort();
}

// クライアントのフラグを Keycloak の既定へ正規化する（未指定 → 既定値）。
function normalizedFlag(client, flag) {
  const raw = client ? client[flag] : undefined;
  return typeof raw === 'boolean' ? raw : CLIENT_FLAG_DEFAULTS[flag];
}

// 差分 1 件の表現。**両辺の値を必ず持つ**（片側だけ出すと読み手が原本を開き直すことになる）。
function difference(kind, name, field, platformValue, astValue) {
  return { kind, name, field, platform: platformValue, ast: astValue };
}

// 存在の突合。宣言済みの片側在りは notice へ、宣言に無い片側在りは差分へ。
function comparePresence(kind, name, onPlatform, onAst, exempt, differences, notices) {
  if (onPlatform && onAst) return true;
  const reason = exempt.get(name);
  if (reason) {
    notices.push({
      kind,
      name,
      side: onPlatform ? 'platform' : 'ast',
      reason,
    });
    return false;
  }
  differences.push(
    difference(kind, name, '存在', onPlatform ? '在る' : '無い', onAst ? '在る' : '無い')
  );
  return false;
}

// 基盤レルム（正本）と AST 専用レルム（写し）を突き合わせ、差分と notice を返す。
function compareRealms(platform, ast) {
  const differences = [];
  const notices = [];

  // --- realm ロール --------------------------------------------------------
  const pRoles = realmRoles(platform);
  const aRoles = realmRoles(ast);
  const roleNames = unionWithPrefix([...pRoles.keys()], [...aRoles.keys()], ROLE_PREFIX);
  for (const name of roleNames) {
    const p = pRoles.get(name);
    const a = aRoles.get(name);
    if (!comparePresence('role', name, Boolean(p), Boolean(a), ONE_SIDED_ROLES, differences, notices)) {
      continue;
    }
    if (Boolean(p.composite) !== Boolean(a.composite)) {
      differences.push(difference('role', name, 'composite', Boolean(p.composite), Boolean(a.composite)));
    }
    if (!sameShape(p.composites || null, a.composites || null)) {
      differences.push(difference('role', name, 'composites', p.composites || null, a.composites || null));
    }
    if (!sameShape(p.attributes || null, a.attributes || null)) {
      differences.push(difference('role', name, 'attributes', p.attributes || null, a.attributes || null));
    }
    // 🔴 本文は比べない（散文である）。**説明が片方だけ消えたこと**は有無で捕まえる。
    const pHasDesc = typeof p.description === 'string' && p.description.trim() !== '';
    const aHasDesc = typeof a.description === 'string' && a.description.trim() !== '';
    if (pHasDesc !== aHasDesc) {
      differences.push(
        difference('role', name, 'description の有無', pHasDesc ? '在る' : '無い', aHasDesc ? '在る' : '無い')
      );
    }
  }

  // --- クライアント --------------------------------------------------------
  const pClients = realmClients(platform);
  const aClients = realmClients(ast);
  const clientIds = unionWithPrefix([...pClients.keys()], [...aClients.keys()], CLIENT_PREFIX);
  for (const clientId of clientIds) {
    const p = pClients.get(clientId);
    const a = aClients.get(clientId);
    if (
      !comparePresence('client', clientId, Boolean(p), Boolean(a), ONE_SIDED_CLIENTS, differences, notices)
    ) {
      continue;
    }
    for (const flag of CLIENT_FLAGS) {
      const pv = normalizedFlag(p, flag);
      const av = normalizedFlag(a, flag);
      if (pv !== av) differences.push(difference('client', clientId, flag, pv, av));
    }
    const pRolesOfSa = serviceAccountRealmRoles(platform, clientId);
    const aRolesOfSa = serviceAccountRealmRoles(ast, clientId);
    if (!sameShape(pRolesOfSa, aRolesOfSa)) {
      differences.push(
        difference(
          'client',
          clientId,
          'service account の realm ロール',
          pRolesOfSa === null ? '（users[] に宣言なし）' : pRolesOfSa,
          aRolesOfSa === null ? '（users[] に宣言なし）' : aRolesOfSa
        )
      );
    }
  }

  return {
    differences,
    notices,
    compared: { roles: roleNames.length, clients: clientIds.length },
  };
}

// 差分 1 件を人が読む形へ。**両辺の値を必ず出す。** secret はそもそも収集していないので出得ない。
function formatDifference(d) {
  const render = (v) => (typeof v === 'string' ? v : JSON.stringify(v));
  const label = d.kind === 'role' ? 'realm ロール' : 'クライアント';
  return [
    `  [差分] ${label} ${d.name} の ${d.field}`,
    `    正本（${PLATFORM_REALM}）: ${render(d.platform)}`,
    `    写し（AST 専用レルム）    : ${render(d.ast)}`,
  ].join('\n');
}

function formatNotice(n) {
  const label = n.kind === 'role' ? 'realm ロール' : 'クライアント';
  const side = n.side === 'platform' ? '基盤レルムのみ' : 'AST 専用レルムのみ';
  return `  [片側宣言] ${label} ${n.name}（${side}）: ${n.reason}`;
}

// --- 実ファイル突合 -----------------------------------------------------------

function readJson(absPath) {
  const text = fs.readFileSync(absPath, 'utf8');
  return JSON.parse(text);
}

// AST 専用レルムの宣言を探す。見つからなければ null（submodule 未取得とみなして縮退する）。
function findAstRealm(root = REPO_ROOT) {
  for (const dir of AST_REALM_DIRS) {
    const abs = path.join(root, dir);
    if (!fs.existsSync(abs) || !fs.statSync(abs).isDirectory()) continue;
    const hit = fs
      .readdirSync(abs)
      .filter((f) => AST_REALM_PATTERN.test(f))
      .sort();
    if (hit.length > 0) return path.join(dir, hit[0]).split(path.sep).join('/');
  }
  return null;
}

// --- 自己試験 ----------------------------------------------------------------

// 合成フィクスチャ。**secret を必ず持たせる**（報告に現れないことを固定するため）。
const SECRET_CANARY = 'canary-secret-must-never-be-printed';

function fixturePlatform() {
  return {
    realm: 'platform',
    roles: {
      realm: [
        { name: 'platform-admin', description: '基盤側だけのロール（接頭辞に一致しないので対象外）' },
        { name: 'trading-owner', description: 'owner' },
        { name: 'trading-service', description: 'service' },
      ],
    },
    clients: [
      { clientId: 'bff', publicClient: false, secret: SECRET_CANARY },
      {
        clientId: 'ai-stock-trading-svc',
        publicClient: false,
        standardFlowEnabled: false,
        directAccessGrantsEnabled: false,
        serviceAccountsEnabled: true,
        secret: SECRET_CANARY,
      },
      {
        clientId: 'ai-stock-trading-owner',
        publicClient: false,
        standardFlowEnabled: false,
        directAccessGrantsEnabled: false,
        serviceAccountsEnabled: true,
        secret: SECRET_CANARY,
      },
    ],
    users: [
      { username: 'service-account-ai-stock-trading-svc', serviceAccountClientId: 'ai-stock-trading-svc', realmRoles: ['trading-service'] },
      { username: 'service-account-ai-stock-trading-owner', serviceAccountClientId: 'ai-stock-trading-owner', realmRoles: ['trading-owner'] },
    ],
  };
}

function fixtureAst() {
  const copy = JSON.parse(JSON.stringify(fixturePlatform()));
  copy.realm = 'ai-stock-trading';
  // 基盤固有の客体は写しに無い（接頭辞に一致しないので突合の母集合に入らない）。
  copy.roles.realm = copy.roles.realm.filter((r) => r.name.startsWith(ROLE_PREFIX));
  copy.clients = copy.clients.filter((c) => c.clientId.startsWith(CLIENT_PREFIX));
  return copy;
}

function withoutRole(realm, name) {
  const copy = JSON.parse(JSON.stringify(realm));
  copy.roles.realm = copy.roles.realm.filter((r) => r.name !== name);
  return copy;
}

function patchClient(realm, clientId, patch) {
  const copy = JSON.parse(JSON.stringify(realm));
  for (const c of copy.clients) if (c.clientId === clientId) Object.assign(c, patch);
  return copy;
}

function selfTest() {
  const P = fixturePlatform();
  const A = fixtureAst();
  const report = (r) =>
    [...r.differences.map(formatDifference), ...r.notices.map(formatNotice)].join('\n');

  const cases = [
    // --- 陽性対照 ---------------------------------------------------------
    ['陽性対照: 同一の写しなら差分 0 件', () => compareRealms(P, A).differences.length === 0],
    ['陽性対照: 突合した客体を数えている（0 件を緑にしない）', () => {
      const r = compareRealms(P, A);
      return r.compared.roles === 2 && r.compared.clients === 2;
    }],
    ['接頭辞に一致しない客体は突合しない（platform-admin / bff）', () => {
      const r = compareRealms(P, A);
      return !report(r).includes('platform-admin') && !report(r).includes('bff');
    }],

    // --- 陰性対照 ---------------------------------------------------------
    ['陰性対照: 写しからロールを 1 つ落とすと差分', () => {
      const r = compareRealms(P, withoutRole(A, 'trading-owner'));
      return r.differences.length === 1 && r.differences[0].field === '存在' && r.differences[0].ast === '無い';
    }],
    ['陰性対照: 正本からロールを 1 つ落としても差分（向きは対称）', () => {
      const r = compareRealms(withoutRole(P, 'trading-service'), A);
      return r.differences.length === 1 && r.differences[0].platform === '無い';
    }],
    ['陰性対照: serviceAccountsEnabled の反転で差分', () => {
      const r = compareRealms(P, patchClient(A, 'ai-stock-trading-svc', { serviceAccountsEnabled: false }));
      return r.differences.some((d) => d.field === 'serviceAccountsEnabled' && d.platform === true && d.ast === false);
    }],
    ['陰性対照: directAccessGrantsEnabled の反転で差分', () => {
      const r = compareRealms(P, patchClient(A, 'ai-stock-trading-owner', { directAccessGrantsEnabled: true }));
      return r.differences.some((d) => d.field === 'directAccessGrantsEnabled');
    }],
    ['陰性対照: publicClient の反転で差分', () => {
      const r = compareRealms(P, patchClient(A, 'ai-stock-trading-svc', { publicClient: true }));
      return r.differences.some((d) => d.field === 'publicClient');
    }],
    ['陰性対照: standardFlowEnabled の反転で差分', () => {
      const r = compareRealms(P, patchClient(A, 'ai-stock-trading-svc', { standardFlowEnabled: true }));
      return r.differences.some((d) => d.field === 'standardFlowEnabled');
    }],
    ['陰性対照: service account の realm ロール付与が違えば差分', () => {
      const a = JSON.parse(JSON.stringify(A));
      for (const u of a.users) if (u.serviceAccountClientId === 'ai-stock-trading-svc') u.realmRoles = ['trading-owner'];
      const r = compareRealms(P, a);
      return r.differences.some((d) => d.field === 'service account の realm ロール');
    }],
    ['陰性対照: service account 利用者ごと消えれば差分（未宣言と空を区別する）', () => {
      const a = JSON.parse(JSON.stringify(A));
      a.users = a.users.filter((u) => u.serviceAccountClientId !== 'ai-stock-trading-owner');
      const r = compareRealms(P, a);
      return r.differences.some(
        (d) => d.field === 'service account の realm ロール' && d.ast === '（users[] に宣言なし）'
      );
    }],
    ['陰性対照: 宣言に無いクライアントが片側だけに在れば差分', () => {
      const a = JSON.parse(JSON.stringify(A));
      a.clients.push({ clientId: 'ai-stock-trading-newcomer', publicClient: false });
      const r = compareRealms(P, a);
      return r.differences.some((d) => d.name === 'ai-stock-trading-newcomer' && d.field === '存在');
    }],
    ['陰性対照: description が片方だけ消えれば差分（本文は比べない）', () => {
      const a = JSON.parse(JSON.stringify(A));
      for (const role of a.roles.realm) if (role.name === 'trading-owner') delete role.description;
      const r = compareRealms(P, a);
      return r.differences.some((d) => d.field === 'description の有無');
    }],
    ['陰性対照: composite / composites の違いで差分', () => {
      const a = JSON.parse(JSON.stringify(A));
      for (const role of a.roles.realm) {
        if (role.name === 'trading-owner') {
          role.composite = true;
          role.composites = { realm: ['trading-service'] };
        }
      }
      const r = compareRealms(P, a);
      return r.differences.some((d) => d.field === 'composite') && r.differences.some((d) => d.field === 'composites');
    }],
    ['陰性対照: attributes の違いで差分', () => {
      const a = JSON.parse(JSON.stringify(A));
      for (const role of a.roles.realm) if (role.name === 'trading-service') role.attributes = { tier: ['gold'] };
      const r = compareRealms(P, a);
      return r.differences.some((d) => d.field === 'attributes');
    }],

    // --- 書き分けだけで赤にしない（既定への正規化） ------------------------
    ['明示 false と省略（既定 false）は差分にならない', () => {
      const a = JSON.parse(JSON.stringify(A));
      for (const c of a.clients) if (c.clientId === 'ai-stock-trading-svc') delete c.publicClient;
      return compareRealms(P, a).differences.length === 0;
    }],
    ['明示 true と省略（既定 true）は差分にならない', () => {
      const p = patchClient(P, 'ai-stock-trading-svc', { standardFlowEnabled: true });
      const a = JSON.parse(JSON.stringify(A));
      for (const c of a.clients) if (c.clientId === 'ai-stock-trading-svc') delete c.standardFlowEnabled;
      return compareRealms(p, a).differences.some((d) => d.field === 'standardFlowEnabled') === false;
    }],
    ['attributes / composites は順序の違いで差分にならない', () => {
      const p = JSON.parse(JSON.stringify(P));
      const a = JSON.parse(JSON.stringify(A));
      for (const role of p.roles.realm) if (role.name === 'trading-owner') role.attributes = { k: ['x', 'y'] };
      for (const role of a.roles.realm) if (role.name === 'trading-owner') role.attributes = { k: ['y', 'x'] };
      return compareRealms(p, a).differences.length === 0;
    }],

    // --- 片側宣言（黙って効く除外を作らない） ------------------------------
    ['宣言済みの片側在りは差分ではなく notice', () => {
      const p = JSON.parse(JSON.stringify(P));
      p.clients.push({ clientId: 'ai-stock-trading-kb-writer', serviceAccountsEnabled: true, secret: SECRET_CANARY });
      const r = compareRealms(p, A);
      return (
        r.differences.length === 0 &&
        r.notices.some((n) => n.name === 'ai-stock-trading-kb-writer' && n.side === 'platform')
      );
    }],
    ['片側宣言の理由が空でない（理由を書けないなら足さない）', () =>
      [...ONE_SIDED_CLIENTS.values(), ...ONE_SIDED_ROLES.values()].every((r) => typeof r === 'string' && r.length >= 20)],

    // --- secret 非漏洩 ----------------------------------------------------
    ['報告に secret が現れない（差分ありの経路でも）', () => {
      const a = patchClient(A, 'ai-stock-trading-svc', { serviceAccountsEnabled: false, secret: 'another-' + SECRET_CANARY });
      const r = compareRealms(P, a);
      const text = report(r);
      return r.differences.length > 0 && !text.includes(SECRET_CANARY);
    }],
    ['secret はそもそも差分の対象にならない（値だけ違っても緑）', () => {
      const a = patchClient(A, 'ai-stock-trading-svc', { secret: 'rotated-' + SECRET_CANARY });
      return compareRealms(P, a).differences.length === 0;
    }],

    // --- 写しの置き場の走査 ------------------------------------------------
    ['未取得の submodule では写しを見つけられない（縮退の条件）', () => {
      const os = require('os');
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'realmcopy-'));
      try {
        return findAstRealm(dir) === null;
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    }],
    ['置き場を決め打ちせず走査で見つける（infra/ でも deploy/ でも）', () => {
      const os = require('os');
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'realmcopy-'));
      try {
        const target = path.join(dir, 'src/ai-stock-trading/deploy/keycloak');
        fs.mkdirSync(target, { recursive: true });
        fs.writeFileSync(path.join(target, 'realm-export.json'), '{}');
        return findAstRealm(dir) === 'src/ai-stock-trading/deploy/keycloak/realm-export.json';
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    }],
  ];

  let failed = 0;
  for (const [name, fn] of cases) {
    let pass = false;
    try {
      pass = fn() === true;
    } catch (e) {
      pass = false;
    }
    if (!pass) {
      failed++;
      console.error(`  ✗ ${name}`);
    }
  }
  if (failed) {
    console.error(`[check-realm-copy-drift] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-realm-copy-drift] 自己試験 ${cases.length} 件 OK。`);
}

function main() {
  if (process.argv.includes('--self-test')) {
    selfTest();
    return;
  }

  const platformAbs = path.join(REPO_ROOT, PLATFORM_REALM);
  if (!fs.existsSync(platformAbs)) {
    // 正本の欠落では縮退しない（本リポジトリの追跡下ファイルであり、無いのは異常である）。
    console.error(`[check-realm-copy-drift] ${PLATFORM_REALM} が見つかりません（正本の欠落は縮退させません）。`);
    process.exit(1);
  }

  const astRel = findAstRealm();
  if (astRel === null) {
    warn(
      '[check-realm-copy-drift] 突合していません（skip）: AST 専用レルムの宣言を見つけられませんでした。' +
        ` submodule src/ai-stock-trading が未取得です。探した場所: ${AST_REALM_DIRS.join(' / ')}。` +
        ' これは「差分 0 件」ではありません —— 突合が走る場所は ci.yml の static-checks-units です。'
    );
    process.exit(0);
  }

  let platform;
  let ast;
  try {
    platform = readJson(platformAbs);
    ast = readJson(path.join(REPO_ROOT, astRel));
  } catch (e) {
    console.error(`[check-realm-copy-drift] realm JSON を読めません: ${e.message}`);
    process.exit(1);
  }

  const result = compareRealms(platform, ast);
  for (const n of result.notices) console.log(formatNotice(n));

  if (result.differences.length === 0) {
    console.log(
      `[check-realm-copy-drift] OK: 正本（${PLATFORM_REALM}）と写し（${astRel}）に差分はありません` +
        `（突合: realm ロール ${result.compared.roles} 件 / クライアント ${result.compared.clients} 件、片側宣言 ${result.notices.length} 件）。`
    );
    process.exit(0);
  }

  console.error(
    `[check-realm-copy-drift] 写しのずれ ${result.differences.length} 件を検出しました` +
      `（正本: ${PLATFORM_REALM} / 写し: ${astRel}）:`
  );
  for (const d of result.differences) {
    console.error('');
    console.error(formatDifference(d));
  }
  console.error('');
  console.error('計画 AST/ADR-0038 決定 3: `trading-owner` / `trading-service` と連結配備のクライアントの正本は');
  console.error('基盤レルムの宣言であり、AST 専用レルムの同名ロールは写しです。**正本の側に合わせて写しを直してください**');
  console.error('（写しに合わせて正本を変えるのは、決定 3 を覆す変更であり計画側の裁定が要ります）。');
  console.error('根拠は .ai-context/adr/IADR-0434_realm-copy-drift-detection.md を参照してください。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  stable,
  sameShape,
  realmRoles,
  realmClients,
  serviceAccountRealmRoles,
  unionWithPrefix,
  normalizedFlag,
  compareRealms,
  formatDifference,
  formatNotice,
  findAstRealm,
  PLATFORM_REALM,
  AST_REALM_DIRS,
  ROLE_PREFIX,
  CLIENT_PREFIX,
  CLIENT_FLAGS,
  CLIENT_FLAG_DEFAULTS,
  ONE_SIDED_CLIENTS,
  ONE_SIDED_ROLES,
};
