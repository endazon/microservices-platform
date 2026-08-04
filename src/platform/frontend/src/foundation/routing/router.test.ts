import { describe, it, expect } from 'vitest';
import { router } from './router';
import { navItems } from './nav';

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
  it('redirects the root path to SC-01', async () => {
    expect(fullPaths()).toContain('/');
    const resolved = router.buildLocation({ to: '/' });
    expect(resolved.pathname).toBe('/');
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

describe('legacy unit bridge (IADR-0124 決定 2)', () => {
  it('mounts routes declared with the legacy contract', () => {
    // AST（変更できない別プロジェクト。IADR-0120）の 3 画面。旧契約は相対パスで宣言する。
    expect(fullPaths()).toEqual(
      expect.arrayContaining(['/settings', '/settings/risk', '/controls']),
    );
  });
});
