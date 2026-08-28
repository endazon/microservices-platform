import { describe, it, expect } from 'vitest';
import { router } from './router';
import { navGroups, navItems, unitNavGroups } from './nav';
import { rootRoute, shellRoute, catchAllRoute } from './shell';
import { ENTRY_ROUTE_PATH } from './entryPath';
import { NotFound } from '@foundation/ui/NotFound';

// ADR-0031 / IADR-0124: ルート木の配線を固定する。
// - 決定 6: 計画（05_screens §共通シェル「ルートパス」）のルートが木に存在すること
// - 決定 5: ナビ項目（ユニットが公開する**データ**）の遷移先が木に解決すること
//   — `<Link to>` の静的検査はデータ駆動のナビには効かないため、その穴を実行時に塞ぐ
// - 決定 2: 旧契約ブリッジ（AST）のルートも木に載ること

/**
 * 05_screens §共通シェル「ルートパス（wireframe の URL バー準拠）」のうち、本 SPA が持つもの。
 *
 * 🔴 **この表は「載っている行」しか検査できない。載せ忘れた画面は誰にも見えない**（#1013 の実測:
 * SC-18 / SC-19 / SC-20 の 3 件が抜けていた）。**足し忘れの検出は
 * `scripts/check-route-manifest.js` が持つ** —— 画面 feature のディレクトリ名（`sc<NN>-*`）と
 * この表を双方向に突き合わせ、片側にしか無い SC 番号を落とす。
 *
 * 🔴 **SC 番号はテスト名のラベルではない。** パスさえ木にあれば Vitest は緑になるため、
 * 番号を取り違えてもここでは捕まらない（同じ検査器の逆方向が捕まえる）。
 */
const PLANNED_ROUTES: ReadonlyArray<readonly [string, string]> = [
  ['SC-01', '/ask'],
  ['SC-02', '/search'],
  ['SC-03', '/docs/$id'],
  ['SC-05', '/admin/documents'],
  ['SC-06', '/admin/sources'],
  ['SC-07', '/admin/conversions'],
  ['SC-08', '/analyze'],
  ['SC-09', '/admin/abac'],
  ['SC-10', '/admin/ops'],
  ['SC-11', '/admin/config-viewer'],
  // #452: SC-12 は管理者限定（RequireRole）だが、木に載るかどうかはロールと独立である。
  ['SC-12', '/admin/mcp-clients'],
  // #452: SC-17 も管理者限定（RequireRole）だが、木に載るかどうかはロールと独立である。
  ['SC-17', '/admin/users'],
  // #917 / #1013: 起点・探索深さはクエリで持つ（例 `/graph?root=D-20481&hops=2`）が、木に載るのはパスだけ。
  ['SC-18', '/graph'],
  // #451 / #1013: 削除済みタブは `/my/notes?tab=trash`。同上でパスだけが木に載る。
  ['SC-19', '/my/notes'],
  ['SC-20', '/my/obsidian'],
  // #918: SC-21 は既定の検索パラメータ（?state=pending）を持つが、木に載るのはパスだけである。
  ['SC-21', '/ai-suggestions'],
];

/**
 * SPA にルートを持つが、**計画のルートパス表には載らない**画面（#1013）。
 *
 * 上の表に無いことは、これまで「足し忘れ」と区別できなかった。**除外を宣言にすることで、
 * 沈黙が主張に変わる** —— `scripts/check-route-manifest.js` は、画面 feature が
 * `PLANNED_ROUTES` にも本表にも無ければ落とす。理由の文字列は必須である。
 */
const SCREENS_NOT_IN_THE_ROUTE_TABLE: ReadonlyArray<readonly [string, string]> = [
  [
    'SC-04',
    '実体は別ホストの Wiki.js（wiki.example.co.jp）であり、計画のルートパス表は SC-04 に SPA ルートを与えていない。SPA 側の /wiki が持つのは遷移導線だけである',
  ],
];

const fullPaths = () => Object.keys(router.routesByPath);

describe('route tree (05_screens §共通シェル のルートパス)', () => {
  it.each(PLANNED_ROUTES)('mounts %s at %s', (_sc, path) => {
    expect(fullPaths()).toContain(path);
  });

  // #1013: 除外の宣言が「表にも書いたうえで除外もする」形に腐ると、除外の意味（＝計画の表に
  // 載らないことの表明）が失われる。**2 つの集合は交わらない。**
  it('keeps the exemptions disjoint from the manifest (a screen is in one list or the other)', () => {
    const planned = new Set(PLANNED_ROUTES.map(([sc]) => sc));
    const both = SCREENS_NOT_IN_THE_ROUTE_TABLE.filter(([sc]) => planned.has(sc)).map(([sc]) => sc);
    expect(both).toEqual([]);
    // 除外は理由とともにしか宣言できない（黙って外す道を残さない）。
    for (const [, reason] of SCREENS_NOT_IN_THE_ROUTE_TABLE) {
      expect(reason.length).toBeGreaterThan(0);
    }
  });

  it('mounts the login route and no SPA-side callback (BFF receives the OIDC callback)', () => {
    // ADR-0032 / IADR-0273 / #439: コールバックは BFF（/bff/auth/callback）が受ける。
    // SPA 側に /callback を復活させないこと（存在すると認可コードが SPA へ届く形に戻り得る）。
    expect(fullPaths()).toContain('/login');
    expect(fullPaths()).not.toContain('/callback');
  });

  it('keeps the old paths gone (they were not the planned ones)', () => {
    const stale = [
      '/results',
      '/documents',
      '/datasources',
      '/conversions',
      '/analysis',
      '/ops',
      '/config',
    ];
    expect(fullPaths().filter((p) => stale.includes(p))).toEqual([]);
  });

  // IADR-0124 決定 6: 計画に home 画面は無い。`/` は SC-01（主入口）へ送る。
  // `buildLocation` では検証にならない——リダイレクトは `beforeLoad` で起きるため、
  // 実際に読み込ませないと「redirect が壊れても緑」になる。
  it('redirects the root path to the entry screen (SC-01)', async () => {
    expect(fullPaths()).toContain('/');
    // 遷移先が木に実在すること（platform が持つ定数がユニットの実装と食い違っていないこと）。
    expect(fullPaths()).toContain(ENTRY_ROUTE_PATH);

    await router.navigate({ to: '/' });
    await router.load();

    expect(router.state.location.pathname).toBe(ENTRY_ROUTE_PATH);
  });
});

// 05_screens §共通シェル ［2026-08-04 確定］: 左ナビは「計画の 4 グループ ＋ ユニットの機能名グループ」で
// 構成される。到達性の検査は**両方**を対象にする——片方（計画グループのみ）にすると、
// 総称グループの廃止に伴って AST 3 画面の到達性検査が静かに外れる（実際に一度外れた）。
const allNavItems = [...navItems(), ...unitNavGroups().flatMap((g) => g.items)];

describe('navigation targets resolve (IADR-0124 決定 5)', () => {
  it('publishes nav items for both the plan groups and the unit groups', () => {
    expect(navItems().length).toBeGreaterThan(0);
    expect(unitNavGroups().flatMap((g) => g.items).length).toBeGreaterThan(0);
  });

  it('covers every rendered nav item (the reachability check has no blind spot)', () => {
    const rendered = navGroups().flatMap((g) => g.items.map((i) => i.id));
    expect([...rendered].sort()).toEqual([...allNavItems.map((i) => i.id)].sort());
  });

  it.each(allNavItems.map((i) => [i.id, i.to] as const))(
    'nav item %s points at an existing route (%s)',
    (_id, to) => {
      expect(fullPaths()).toContain(to);
    },
  );
});

// IADR-0009 / IADR-0124 決定 8: 存在秘匿。未知パスの受け皿を**共通シェル配下**に置き、
// 権限による秘匿（RequireRole → NotFound）と描画を揃える（描画の一致は Layout.test.tsx が固定する）。
// ここでは配線——catch-all が木にあり、かつ実在ルートを横取りしないこと——を固定する。
describe('existence hiding: catch-all wiring (IADR-0009)', () => {
  it('mounts the catch-all under the authenticated shell (not at the root)', () => {
    expect(catchAllRoute.parentRoute).toBe(shellRoute);
    expect(catchAllRoute.options.component).toBe(NotFound);
    // シェル配下から notFound() が投げられた場合も同じ画面にする。
    expect(shellRoute.options.notFoundComponent).toBe(NotFound);
    expect(rootRoute.options.notFoundComponent).toBe(NotFound);
  });

  it.each([
    ['/login', '認証導線'],
    ['/ask', 'ユニットの画面（SC-01）'],
    ['/admin/config-viewer', 'ユニットの画面（SC-11）'],
    ['/settings', '旧契約ブリッジ（AST）'],
  ])('does not hijack %s (%s)', async (path) => {
    await router.navigate({ to: path as '/ask' });
    await router.load();
    const matchedIds = router.state.matches.map((m) => m.routeId);
    expect(matchedIds).not.toContain(catchAllRoute.id);
    expect(router.state.location.pathname).toBe(path);
  });

  it('matches the catch-all for an unknown path', async () => {
    await router.navigate({ to: '/no-such-screen' as '/ask' });
    await router.load();
    expect(router.state.matches.map((m) => m.routeId)).toContain(catchAllRoute.id);
  });
});

describe('legacy unit bridge (IADR-0124 決定 2)', () => {
  it('mounts routes declared with the legacy contract', () => {
    // AST（変更できない別プロジェクト。IADR-0120）の 3 画面。旧契約は相対パスで宣言する。
    expect(fullPaths()).toEqual(
      expect.arrayContaining(['/settings', '/settings/risk', '/controls']),
    );
  });
});
