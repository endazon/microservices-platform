import { describe, it, expect } from 'vitest';
import { router } from './router';
import { navItems } from './nav';
import { rootRoute, shellRoute, catchAllRoute } from './shell';
import { ENTRY_ROUTE_PATH } from './entryPath';
import { NotFound } from '@foundation/ui/NotFound';

// ADR-0031 / IADR-0124: ルート木の配線を固定する。
// - 決定 6: 計画（05_screens §共通シェル「ルートパス」）のルートが木に存在すること
// - 決定 5: ナビ項目（ユニットが公開する**データ**）の遷移先が木に解決すること
//   — `<Link to>` の静的検査はデータ駆動のナビには効かないため、その穴を実行時に塞ぐ
// - 決定 2: 旧契約ブリッジ（AST）のルートも木に載ること

/** 05_screens §共通シェル「ルートパス（wireframe の URL バー準拠）」のうち、本 SPA が持つもの。 */
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
];

const fullPaths = () => Object.keys(router.routesByPath);

describe('route tree (05_screens §共通シェル のルートパス)', () => {
  it.each(PLANNED_ROUTES)('mounts %s at %s', (_sc, path) => {
    expect(fullPaths()).toContain(path);
  });

  it('mounts the authentication routes', () => {
    expect(fullPaths()).toEqual(expect.arrayContaining(['/login', '/callback']));
  });

  it('keeps the old paths gone (they were not the planned ones)', () => {
    const stale = ['/results', '/documents', '/datasources', '/conversions', '/analysis', '/ops', '/config'];
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

describe('navigation targets resolve (IADR-0124 決定 5)', () => {
  it('publishes at least one nav item per unit group', () => {
    expect(navItems().length).toBeGreaterThan(0);
  });

  it.each(navItems().map((i) => [i.id, i.to] as const))(
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
    ['/callback', '認証導線'],
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
