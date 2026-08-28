import { describe, it, expect } from 'vitest';
import { i18n } from '@lingui/core';
import { breadcrumbTrail, breadcrumbs } from './breadcrumbs';
import type { FeatureBreadcrumb } from './featureRegistry';
import { ENTRY_ROUTE_PATH } from './entryPath';
// 実アプリのルータを import すると、合成点の登録（ナビ・パンくず）が副作用で走る。
import { router } from './router';
import { navItems } from './nav';

// 05_screens §共通シェル「パンくず・権限バッジ」（#446）。
//
// 🔴 **本ファイルは Layout を描画しない。** 段の組み立て（グループ段の有無・親画面・動的な葉・
// リンクの有無・存在秘匿）は純関数の責務であり、描画テストと混ぜると
// 「どちらが壊れたのか」が読めなくなる。描画は Layout.test.tsx が見る。

const ADMIN = 'platform-admin';
const OPERATOR = 'platform-operator';

/** テスト専用の宣言集合（登録済みのものとは独立に組む）。 */
const DECLARATIONS: readonly FeatureBreadcrumb[] = [
  // 利用者グループ・グループ段なし・自分の段だけ（SC-01 相当）。
  { routePath: '/ask', group: 'user', label: '検索・チャット質問' },
  // 利用者グループ・親画面あり（SC-02 相当）。
  {
    routePath: '/search',
    group: 'user',
    parents: [{ label: '検索・チャット質問', to: '/ask' }],
    label: '検索結果',
  },
  // 動的な葉（SC-03 相当）。**自分の段を宣言しない。**
  { routePath: '/docs/$id', group: 'user', parents: [{ label: '検索結果', to: '/search' }] },
  // 管理グループ（SC-05 相当）。
  {
    routePath: '/admin/documents',
    group: 'admin',
    label: '文書管理',
    requiresAnyRole: [ADMIN, OPERATOR],
  },
  // 運用グループ・親画面あり（SC-11 相当）。
  {
    routePath: '/admin/config-viewer',
    group: 'ops',
    parents: [{ label: 'ダッシュボード', to: '/admin/ops' }],
    label: '構成ビューア',
    requiresAnyRole: [ADMIN, OPERATOR],
  },
  // 個人グループ（SC-19 相当）。ロール限定なし。
  { routePath: '/my/notes', group: 'personal', label: '個人資料' },
];

const trail = (routePath: string | undefined, roles: string[] = [], leaf?: string) =>
  breadcrumbTrail({ routePath, roles, leaf, declarations: DECLARATIONS });

const labels = (routePath: string | undefined, roles: string[] = [], leaf?: string) =>
  trail(routePath, roles, leaf).map((s) => s.label);

describe('breadcrumbTrail: 段の構成（モックアップの crumb 実測に対応）', () => {
  it('builds ホーム / <画面名> for a 利用者 screen (SC-01: no group segment)', () => {
    expect(labels('/ask')).toEqual(['ホーム', '検索・チャット質問']);
  });

  it('builds ホーム / 管理 / <画面名> for an 管理 screen (SC-05)', () => {
    expect(labels('/admin/documents', [ADMIN])).toEqual(['ホーム', '管理', '文書管理']);
  });

  it('builds ホーム / 個人 / <画面名> for a 個人 screen (SC-19)', () => {
    expect(labels('/my/notes')).toEqual(['ホーム', '個人', '個人資料']);
  });

  it('inserts the parent screen segment (SC-02: ホーム / 検索・チャット質問 / 検索結果)', () => {
    expect(labels('/search')).toEqual(['ホーム', '検索・チャット質問', '検索結果']);
  });

  it('inserts group and parent together (SC-11: ホーム / 運用 / ダッシュボード / 構成ビューア)', () => {
    expect(labels('/admin/config-viewer', [OPERATOR])).toEqual([
      'ホーム',
      '運用',
      'ダッシュボード',
      '構成ビューア',
    ]);
  });

  // 🔴 モックアップ実測: 利用者グループの画面（SC-01/02/03/04/08/18/21）にグループ段は無い。
  // 「利用者」という段を足す変異をここで殺す。
  it('never emits a group segment for the 利用者 group', () => {
    for (const path of ['/ask', '/search', '/docs/$id']) {
      expect(trail(path).some((s) => s.kind === 'group')).toBe(false);
      expect(labels(path, [], 'x')).not.toContain('利用者');
    }
  });

  it('emits exactly one group segment for the other three groups', () => {
    for (const [path, roles] of [
      ['/admin/documents', [ADMIN]],
      ['/admin/config-viewer', [ADMIN]],
      ['/my/notes', []],
    ] as const) {
      expect(trail(path, [...roles]).filter((s) => s.kind === 'group')).toHaveLength(1);
    }
  });
});

describe('breadcrumbTrail: リンクにする段としない段', () => {
  it('links ホーム to the entry route (05_screens: 既定ルート)', () => {
    const home = trail('/admin/documents', [ADMIN])[0];
    expect(home.kind).toBe('home');
    expect(home.to).toBe(ENTRY_ROUTE_PATH);
  });

  it('never links the current segment (それが現在地だから)', () => {
    for (const [path, roles] of [
      ['/ask', []],
      ['/search', []],
      ['/admin/documents', [ADMIN]],
      ['/admin/config-viewer', [OPERATOR]],
    ] as const) {
      const current = trail(path, [...roles]).at(-1);
      expect(current?.kind).toBe('current');
      expect(current?.to).toBeUndefined();
    }
  });

  it('links the parent screen segment (モックの SC-03/07/11/12 と同じ)', () => {
    const parent = trail('/search').find((s) => s.kind === 'parent');
    expect(parent?.to).toBe('/ask');
  });

  // グループ段は「バッジ」であって遷移先ではない（モックの crumb でも <a> ではない）。
  it('does not link the group segment', () => {
    const group = trail('/admin/config-viewer', [ADMIN]).find((s) => s.kind === 'group');
    expect(group?.label).toBe('運用');
    expect(group?.to).toBeUndefined();
  });
});

describe('breadcrumbTrail: 動的な葉（SC-03）', () => {
  it('omits the leaf until the screen supplies it (未確定の文字列を描かない)', () => {
    expect(labels('/docs/$id')).toEqual(['ホーム', '検索結果']);
    expect(trail('/docs/$id').some((s) => s.kind === 'current')).toBe(false);
  });

  it('renders the supplied leaf as the current segment', () => {
    const t = trail('/docs/$id', [], '経費精算規程 v3.2');
    expect(t.map((s) => s.label)).toEqual(['ホーム', '検索結果', '経費精算規程 v3.2']);
    expect(t.at(-1)?.kind).toBe('current');
    expect(t.at(-1)?.to).toBeUndefined();
  });

  // 🔴 葉は「自分の段を宣言していない画面」にしか効かない。宣言のある画面が
  // 前の画面の葉を引き継ぐと、パンくずが別の画面の名前を出す。
  it('ignores a stale leaf on screens that declare their own label', () => {
    expect(labels('/ask', [], '経費精算規程 v3.2')).toEqual(['ホーム', '検索・チャット質問']);
  });
});

describe('breadcrumbTrail: 存在秘匿（IADR-0009）', () => {
  it('returns nothing for an unknown route path', () => {
    expect(trail('/no-such-screen', [ADMIN])).toEqual([]);
    expect(trail(undefined, [ADMIN])).toEqual([]);
  });

  // 🔴 権限外でパンくずを描くと、NotFound の外側から「そのパスは実在し、運用グループの
  // 構成ビューアである」ことが読める。**未知パスと同じ（＝何も描かない）にする。**
  it('returns nothing for a role-gated screen the user cannot see', () => {
    expect(trail('/admin/config-viewer', ['user'])).toEqual([]);
    expect(trail('/admin/documents', [])).toEqual([]);
  });

  it('renders it for a user who holds any one of the required roles', () => {
    expect(labels('/admin/config-viewer', [OPERATOR])).toContain('構成ビューア');
    expect(labels('/admin/config-viewer', [ADMIN])).toContain('構成ビューア');
    expect(labels('/admin/config-viewer', ['user', ADMIN])).toContain('構成ビューア');
  });

  it('produces the identical (empty) result for unknown and forbidden paths', () => {
    expect(trail('/admin/config-viewer', ['user'])).toEqual(trail('/no-such-screen', ['user']));
  });
});

describe('breadcrumbTrail: 文言の解決', () => {
  // ナビ項目と同じ理由（nav.ts の resolveNavLabel）: MessageDescriptor は描画時に解決する。
  // ロケールを切り替えたら、同じ宣言から別の文字列が出ること。
  it('resolves MessageDescriptor labels at call time (locale switches follow)', () => {
    const declarations = breadcrumbs();
    const before = breadcrumbTrail({ routePath: '/admin/ops', roles: [ADMIN], declarations });
    expect(before.map((s) => s.label)).toEqual(['ホーム', '運用', 'ダッシュボード']);
    i18n.activate('en');
    try {
      const after = breadcrumbTrail({ routePath: '/admin/ops', roles: [ADMIN], declarations });
      expect(after.map((s) => s.label)).not.toEqual(before.map((s) => s.label));
      expect(after).toHaveLength(before.length);
    } finally {
      i18n.activate('ja');
    }
  });
});

// 実アプリの登録内容に対する検査。ここが「宣言し忘れた画面」を捕まえる唯一の場所である
// （宣言が無い画面はパンくずが出ないだけで、テストも型検査も静かに通る）。
describe('registered breadcrumbs (実アプリの宣言)', () => {
  const registered = () => breadcrumbs();
  const routeFullPaths = () => Object.keys(router.routesByPath);

  it('registers a declaration for every screen route the plan lists', () => {
    // 05_screens §共通シェル「ルートパス」。SC-04（/wiki）は SPA 側の遷移導線として持つ。
    const planned = [
      '/ask',
      '/search',
      '/docs/$id',
      '/wiki',
      '/admin/documents',
      '/admin/sources',
      '/admin/conversions',
      '/analyze',
      '/admin/abac',
      '/admin/ops',
      '/admin/config-viewer',
      '/admin/mcp-clients',
      '/admin/users',
      '/graph',
      '/my/notes',
      '/my/obsidian',
      '/ai-suggestions',
    ];
    expect(
      registered()
        .map((d) => d.routePath)
        .sort(),
    ).toEqual([...planned].sort());
  });

  it('points every declaration at a route that exists in the tree', () => {
    for (const declaration of registered()) {
      expect(routeFullPaths()).toContain(declaration.routePath);
    }
  });

  it('points every parent segment at a route that exists in the tree', () => {
    for (const declaration of registered()) {
      for (const parent of declaration.parents ?? []) {
        expect(routeFullPaths()).toContain(parent.to);
      }
    }
  });

  // 🔴 存在秘匿の担保は「パンくずの宣言とナビ項目が同じロールを要求すること」である。
  // 片方だけ緩めると、ナビに出ない画面のパンくずだけが出る（あるいはその逆）。
  it('requires the same roles as the matching nav item', () => {
    const navByPath = new Map(navItems().map((i) => [i.to, i.requiresAnyRole ?? []]));
    for (const declaration of registered()) {
      const navRoles = navByPath.get(declaration.routePath);
      if (navRoles === undefined) continue; // SC-03 はナビ項目を持たない
      expect([...(declaration.requiresAnyRole ?? [])].sort()).toEqual([...navRoles].sort());
    }
  });

  // SC-03 はナビに出ないが、パンくずは持つ。**別々の登録面である**ことの回帰防止。
  it('declares a breadcrumb for SC-03 even though it has no nav item', () => {
    expect(navItems().some((i) => i.to === '/docs/$id')).toBe(false);
    expect(registered().some((d) => d.routePath === '/docs/$id')).toBe(true);
  });

  // 動的な葉を持つ画面だけが label を省略できる。他の画面の省略は「段が出ない」欠陥である。
  it('omits the static label only for the screen with a dynamic leaf (SC-03)', () => {
    const withoutLabel = registered().filter((d) => d.label === undefined);
    expect(withoutLabel.map((d) => d.routePath)).toEqual(['/docs/$id']);
  });
});
