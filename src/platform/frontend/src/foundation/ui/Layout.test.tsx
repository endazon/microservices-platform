import { describe, it, expect } from 'vitest';
import { act, render, screen, within } from '@testing-library/react';
import { RouterProvider } from '@tanstack/react-router';
import type { User } from 'oidc-client-ts';
import { AuthContext } from '@foundation/auth/AuthContext';
import type { AuthState } from '@foundation/auth/AuthContext';
// 実アプリのルータを使う（合成点のナビ登録もこの import の副作用で行われる）。
import { router } from '@foundation/routing/router';
import { accountConsoleUrl } from './Layout';

// Issue #136 / IADR-0035: ナビはユニットの登録から導出し、権限外の項目は描画しない（存在秘匿）。
// 05_screens §共通シェル / IADR-0124 決定 6・7: ブランド表示名・4 グループ・ユーザーアイコン（→ SC-16）。

function makeJwt(payload: unknown): string {
  const b64url = (obj: unknown) =>
    btoa(JSON.stringify(obj)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `h.${b64url(payload)}.sig`;
}

/**
 * 共通シェルを実アプリのルータの上で描画する。
 * 既定の器は SC-04（純表示の画面）。存在秘匿の検証では未知パス・権限外パスを渡す。
 */
async function renderLayout(roles: string[], path = '/wiki') {
  const user = {
    access_token: makeJwt({ realm_access: { roles } }),
    profile: { preferred_username: 'tester' },
  } as unknown as User;
  const value: AuthState = {
    user,
    isAuthenticated: true,
    isLoading: false,
    login: async () => {},
    logout: async () => {},
  };
  window.history.pushState({}, '', path);
  const result = render(
    <AuthContext.Provider value={value}>
      <RouterProvider router={router} />
    </AuthContext.Provider>,
  );
  // TanStack Router の初期描画は非同期（マッチの解決を待つ）。
  await act(async () => {
    await router.load();
  });
  return result;
}

function nav() {
  return screen.getByRole('navigation', { name: '主要ナビゲーション' });
}

describe('Layout navigation (role-gated)', () => {
  it('always shows the SC-01 entry point link', async () => {
    await renderLayout([]);
    expect(await within(nav()).findByRole('link', { name: '検索 / AI質問' })).toBeInTheDocument();
  });

  it('shows the 運用ダッシュボード (SC-10) link for platform-admin', async () => {
    await renderLayout(['platform-admin']);
    expect(
      await within(nav()).findByRole('link', { name: '運用ダッシュボード' }),
    ).toBeInTheDocument();
  });

  it('hides the 運用ダッシュボード link for users without the admin role (existence hidden)', async () => {
    await renderLayout(['user']);
    await within(nav()).findByRole('link', { name: '検索 / AI質問' });
    expect(within(nav()).queryByRole('link', { name: '運用ダッシュボード' })).not.toBeInTheDocument();
  });

  // SC-11 #140: 構成ビューアは ConfigViewer（管理者・運用者）のみメニュー表示（存在秘匿）。
  it('shows the 構成ビューア (SC-11) link for platform-operator', async () => {
    await renderLayout(['platform-operator']);
    expect(await within(nav()).findByRole('link', { name: '構成ビューア' })).toBeInTheDocument();
    // 運用者は AdminOnly の運用ダッシュボードは見えない。
    expect(within(nav()).queryByRole('link', { name: '運用ダッシュボード' })).not.toBeInTheDocument();
  });

  it('hides the 構成ビューア link for non-privileged users (existence hidden)', async () => {
    await renderLayout(['user']);
    await within(nav()).findByRole('link', { name: '検索 / AI質問' });
    expect(within(nav()).queryByRole('link', { name: '構成ビューア' })).not.toBeInTheDocument();
  });
});

// 05_screens §共通シェル: 左ナビは 4 グループ（利用者／個人／管理／運用）。項目が 0 件のグループは
// 見出しごと描画しない（権限で隠れているのか未実装なのかを読み違えさせないため）。
describe('Layout navigation groups (05_screens §共通シェル)', () => {
  it('groups links under the planned headings for an admin', async () => {
    await renderLayout(['platform-admin']);
    await within(nav()).findByRole('link', { name: '検索 / AI質問' });
    expect(within(nav()).getByRole('heading', { name: '利用者' })).toBeInTheDocument();
    expect(within(nav()).getByRole('heading', { name: '管理' })).toBeInTheDocument();
    expect(within(nav()).getByRole('heading', { name: '運用' })).toBeInTheDocument();
  });

  it('omits the 管理 heading for a non-privileged user (no empty group headings)', async () => {
    await renderLayout(['user']);
    await within(nav()).findByRole('link', { name: '検索 / AI質問' });
    expect(within(nav()).queryByRole('heading', { name: '管理' })).not.toBeInTheDocument();
    // 「個人」グループの画面（個人資料・Obsidian 連携）は未実装のため、どのロールでも見出しは出ない。
    expect(within(nav()).queryByRole('heading', { name: '個人' })).not.toBeInTheDocument();
  });

  // 05_screens §共通シェル ［2026-08-04 確定］:
  //   「本計画に属さない可変機能ユニットの画面は、実装側でグループを設けて分類してよい。
  //     **ただしグループ名は『ユニットの機能名』とする**（例: ai-stock-trading → 「株式自動売買」）。
  //     並び順は計画の 4 グループの後とする。**総称としての『その他』は使わない**」
  // 計画は理由も述べている——左ナビのグループ名は利用者が機能を探す唯一の手掛かりであり、
  // 何が入っているか分からない名前を置くと導線が失われる。ここを固定しないと、
  // 「グループ名を総称へ戻す」という退行がテストを緑のまま通り抜ける。
  it('puts non-plan unit screens under the unit feature name, never a generic heading', async () => {
    // AST（ai-stock-trading）の 3 画面は trading-owner ロールでのみ表示される。
    await renderLayout(['trading-owner']);
    const unitHeading = await within(nav()).findByRole('heading', { name: '株式自動売買' });
    expect(unitHeading).toBeInTheDocument();

    // 見出しの配下（同じグループの <div>）に AST の画面リンクが載っていること。
    const group = unitHeading.parentElement as HTMLElement;
    expect(within(group).getByRole('link', { name: '設定' })).toBeInTheDocument();
    expect(within(group).getByRole('link', { name: 'リスク設定' })).toBeInTheDocument();
    expect(within(group).getByRole('link', { name: '統制状態' })).toBeInTheDocument();

    // 総称の見出しが存在しないこと（計画が名指しで禁じた文言）。
    expect(within(nav()).queryByRole('heading', { name: 'その他' })).not.toBeInTheDocument();
  });

  it('orders the unit feature group after the four plan groups', async () => {
    await renderLayout(['platform-admin', 'trading-owner']);
    await within(nav()).findByRole('heading', { name: '株式自動売買' });
    const headings = within(nav())
      .getAllByRole('heading')
      .map((h) => h.textContent);
    // 計画の 4 グループ（表示されるもの）→ ユニットの機能名、の順。
    expect(headings).toEqual(['利用者', '管理', '運用', '株式自動売買']);
  });
});

// 05_screens §共通シェル: ブランド表示名とユーザーアイコン（→ SC-16 アカウント設定）。
describe('Layout common shell (brand / SC-16)', () => {
  it('shows the planned brand name', async () => {
    await renderLayout([]);
    expect(await screen.findByText('汎用プラットフォーム')).toBeInTheDocument();
  });

  it('links the user icon to the Keycloak account console (SC-16)', async () => {
    await renderLayout([]);
    const link = await screen.findByRole('link', { name: /アカウント設定/ });
    expect(link).toHaveAttribute('href', expect.stringMatching(/\/account$/));
  });

  it('builds the SC-16 URL from the runtime OIDC authority (trailing slashes tolerated)', () => {
    expect(accountConsoleUrl('https://auth.example/realms/platform')).toBe(
      'https://auth.example/realms/platform/account',
    );
    expect(accountConsoleUrl('https://auth.example/realms/platform/')).toBe(
      'https://auth.example/realms/platform/account',
    );
  });
});

// IADR-0009 / IADR-0124 決定 8: 存在秘匿。「不在（未知パス）」と「権限による秘匿（RequireRole）」で
// 描画が割れると、シェルが出るかどうかで資源の存在を推測できてしまう。同じ画面になることを固定する。
describe('existence hiding: unknown path and forbidden path render alike (IADR-0009)', () => {
  const heading = () => screen.getByRole('heading', { name: '見つかりませんでした' });

  it('renders NotFound inside the common shell for an unknown path', async () => {
    await renderLayout(['user'], '/no-such-screen');
    expect(await screen.findByRole('heading', { name: '見つかりませんでした' })).toBeInTheDocument();
    // シェル（ナビ・ブランド）が付いた状態で出る。
    expect(nav()).toBeInTheDocument();
    expect(screen.getByText('汎用プラットフォーム')).toBeInTheDocument();
  });

  it('renders NotFound inside the common shell for a role-gated path (SC-11)', async () => {
    await renderLayout(['user'], '/admin/config-viewer');
    expect(await screen.findByRole('heading', { name: '見つかりませんでした' })).toBeInTheDocument();
    expect(nav()).toBeInTheDocument();
    expect(screen.getByText('汎用プラットフォーム')).toBeInTheDocument();
  });

  it('produces the same not-found markup in both cases', async () => {
    const unknown = await renderLayout(['user'], '/no-such-screen');
    const unknownHtml = heading().parentElement?.outerHTML;
    unknown.unmount();

    await renderLayout(['user'], '/admin/config-viewer');
    const forbiddenHtml = heading().parentElement?.outerHTML;

    expect(unknownHtml).toBeTruthy();
    expect(forbiddenHtml).toBe(unknownHtml);
  });
});
