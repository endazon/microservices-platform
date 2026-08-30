import { describe, it, expect } from 'vitest';
import { act, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nProvider } from '@lingui/react';
import { i18n } from '@lingui/core';
import { RouterProvider } from '@tanstack/react-router';
import { AuthContext } from '@foundation/auth/AuthContext';
import type { AuthState } from '@foundation/auth/AuthContext';
// 実アプリのルータを使う（合成点のナビ登録もこの import の副作用で行われる）。
import { router } from '@foundation/routing/router';
import { accountConsoleUrl } from './Layout';
import { resetAppConfigCache } from '@foundation/config/runtimeConfig';

// Issue #136 / IADR-0035: ナビはユニットの登録から導出し、権限外の項目は描画しない（存在秘匿）。
// 05_screens §共通シェル / IADR-0124 決定 6・7: ブランド表示名・4 グループ・ユーザーアイコン（→ SC-16）。

/**
 * 共通シェルを実アプリのルータの上で描画する。
 * 既定の器は SC-04（純表示の画面）。存在秘匿の検証では未知パス・権限外パスを渡す。
 */
async function renderLayout(roles: string[], path = '/wiki') {
  const value: AuthState = {
    // ADR-0032: 身元は /bff/auth/me の形（トークンは無い）。
    user: { name: 'tester', subject: 'tester', roles },
    isAuthenticated: true,
    isLoading: false,
    login: async () => {},
    logout: async () => {},
  };
  window.history.pushState({}, '', path);
  // FR-22 / IADR-0215 決定 2: 共通シェルは通知ベル（`NotificationBell`）を持ち、TanStack Query を読む。
  // 実アプリ（`App.tsx`）と同じく `QueryClientProvider` で包む——包まないとシェル全体が
  // 「No QueryClient set」で落ち、ナビの検証まで巻き添えになる（実測）。
  // 検査用クライアントは描画のたびに作り、再試行もキャッシュの持ち越しもさせない。
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: 0, gcTime: 0, refetchOnWindowFocus: false },
      mutations: { retry: false },
    },
  });
  // #788: 共通シェルは右レール AI チャットパネルを持ち、そこは `<Trans>` を使う。
  // 実アプリ（`App.tsx`）と同じく `I18nProvider` で包む——包まないと
  // 「useLingui hook was used without I18nProvider」でシェル全体が落ち、
  // ナビの検証まで巻き添えになる（実測。`NotificationBell.test.tsx` と同じ作法）。
  const result = render(
    <I18nProvider i18n={i18n}>
      <QueryClientProvider client={queryClient}>
        <AuthContext.Provider value={value}>
          <RouterProvider router={router} />
        </AuthContext.Provider>
      </QueryClientProvider>
    </I18nProvider>,
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
  // #502: SC-01 のナビ表示名は hi-fi モックの左レール準拠で「検索・質問」である。
  // ラベルは Lingui の MessageDescriptor で持ち、描画時に解決される（nav.ts の resolveNavLabel）。
  it('always shows the SC-01 entry point link', async () => {
    await renderLayout([]);
    expect(await within(nav()).findByRole('link', { name: '検索・質問' })).toBeInTheDocument();
  });

  // #504: SC-10 のナビ表示名は hi-fi モックの左レール準拠で「ダッシュボード」である
  // （従前は「運用ダッシュボード」と表示していた）。
  it('shows the ダッシュボード (SC-10) link for platform-admin', async () => {
    await renderLayout(['platform-admin']);
    expect(await within(nav()).findByRole('link', { name: 'ダッシュボード' })).toBeInTheDocument();
  });

  it('hides the ダッシュボード link for users without a privileged role (existence hidden)', async () => {
    await renderLayout(['user']);
    await within(nav()).findByRole('link', { name: '検索・質問' });
    expect(within(nav()).queryByRole('link', { name: 'ダッシュボード' })).not.toBeInTheDocument();
  });

  // SC-11 #140: 構成ビューアは ConfigViewer（管理者・運用者）のみメニュー表示（存在秘匿）。
  //
  // **［2026-08-09 / #544］運用者は SC-10（ダッシュボード）も見える。**
  // 従前は「運用者は AdminOnly の SC-10 は見えない」ことを併せて固定していたが、
  // 計画 §SC-10 を正として 3 層を広げた（裁定 Q19 / Q28。[[IADR-0129]] 決定 4 の追記）。
  it('shows both the 構成ビューア (SC-11) and ダッシュボード (SC-10) links for platform-operator', async () => {
    await renderLayout(['platform-operator']);
    expect(await within(nav()).findByRole('link', { name: '構成ビューア' })).toBeInTheDocument();
    expect(within(nav()).getByRole('link', { name: 'ダッシュボード' })).toBeInTheDocument();
  });

  it('hides the 構成ビューア link for non-privileged users (existence hidden)', async () => {
    await renderLayout(['user']);
    await within(nav()).findByRole('link', { name: '検索・質問' });
    expect(within(nav()).queryByRole('link', { name: '構成ビューア' })).not.toBeInTheDocument();
  });
});

// 05_screens §共通シェル: 左ナビは 4 グループ（利用者／個人／管理／運用）。項目が 0 件のグループは
// 見出しごと描画しない（権限で隠れているのか未実装なのかを読み違えさせないため）。
describe('Layout navigation groups (05_screens §共通シェル)', () => {
  it('groups links under the planned headings for an admin', async () => {
    await renderLayout(['platform-admin']);
    await within(nav()).findByRole('link', { name: '検索・質問' });
    expect(within(nav()).getByRole('heading', { name: '利用者' })).toBeInTheDocument();
    expect(within(nav()).getByRole('heading', { name: '管理' })).toBeInTheDocument();
    expect(within(nav()).getByRole('heading', { name: '運用' })).toBeInTheDocument();
  });

  it('omits the 管理 heading for a non-privileged user (no empty group headings)', async () => {
    await renderLayout(['user']);
    await within(nav()).findByRole('link', { name: '検索・質問' });
    expect(within(nav()).queryByRole('heading', { name: '管理' })).not.toBeInTheDocument();
    // 「個人」グループ（個人資料・Obsidian 連携）は**ロール限定が無い**ので、
    // 権限の無い利用者にも出る。空グループを描かない規則の陽性対照としてここに置く
    // （従前は両画面が未実装で、この見出しはどのロールでも出なかった）。
    expect(within(nav()).getByRole('heading', { name: '個人' })).toBeInTheDocument();
    expect(within(nav()).getByRole('link', { name: '個人資料' })).toBeInTheDocument();
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
    expect(headings).toEqual(['利用者', '個人', '管理', '運用', '株式自動売買']);
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

  // 🔴 SC-16 / CLAUDE.md「接続先はビルドに焼き込まず実行時 config で注入する」:
  // 上の 2 件だけでは**シェルが `accountConsoleUrl` を経由せず URL を直書きしても落ちない**
  // （href の末尾 `/account` と純関数の振る舞いは、直書きでも両方満たされる。変異試験で実測）。
  // 描画された href を**実行時 config の値そのもの**に結び付けて、その逃げ道を塞ぐ。
  it('derives the rendered SC-16 href from the injected runtime authority (no build-time baking)', async () => {
    const saved = window.__APP_CONFIG__;
    window.__APP_CONFIG__ = {
      ...saved,
      oidc: { authority: 'https://idp.test.invalid/realms/mutant', clientId: 'platform-spa' },
    };
    resetAppConfigCache();
    try {
      await renderLayout([]);
      const link = await screen.findByRole('link', { name: /アカウント設定/ });
      expect(link).toHaveAttribute('href', 'https://idp.test.invalid/realms/mutant/account');
    } finally {
      window.__APP_CONFIG__ = saved;
      resetAppConfigCache();
    }
  });
});

// 05_screens §共通シェル「パンくず・権限バッジ: 上部にパンくずと画面グループのバッジ
// （管理／システム管理／運用）を表示する」（#446）。
//
// 段の組み立ては純関数（breadcrumbs.test.ts）が固定しているので、ここが見るのは
// **描画の契約**——ランドマーク・リンクの有無・aria-current・バッジのテキスト——である。
function crumb() {
  return screen.getByRole('navigation', { name: 'パンくず' });
}

describe('Layout breadcrumb (05_screens §共通シェル)', () => {
  it('renders ホーム / <画面名> for a 利用者 screen (SC-04: no group segment)', async () => {
    await renderLayout([], '/wiki');
    const list = within(crumb()).getByRole('list');
    expect(
      within(list)
        .getAllByRole('listitem')
        .map((li) => li.textContent),
    ).toEqual(['ホーム', '/Wiki']);
  });

  it('links ホーム to the entry route and leaves the current segment unlinked', async () => {
    await renderLayout([], '/wiki');
    expect(within(crumb()).getByRole('link', { name: 'ホーム' })).toHaveAttribute('href', '/ask');
    // 現在地はリンクではない（`/wiki` を指す <a> がパンくずの中に無いこと）。
    expect(within(crumb()).queryByRole('link', { name: 'Wiki' })).not.toBeInTheDocument();
  });

  it('marks the current segment with aria-current="page"', async () => {
    await renderLayout(['platform-admin'], '/admin/documents');
    const current = within(crumb()).getByText('文書管理');
    expect(current).toHaveAttribute('aria-current', 'page');
    // 現在地は 1 つだけである（グループ段やホームに付けない）。
    expect(crumb().querySelectorAll('[aria-current="page"]')).toHaveLength(1);
  });

  // 🔴 計画「画面グループのバッジ（管理／システム管理／運用）」。モックアップに独立した
  // バッジ要素は無く、crumb のグループ段がそれである。**2 つ目のバッジを作らない。**
  // 🔴 状態を色だけで表さない（本リポの規約）——バッジは「管理」というテキストを持つ。
  it('renders the screen group as a badge segment carrying its own text', async () => {
    await renderLayout(['platform-admin'], '/admin/documents');
    const list = within(crumb()).getByRole('list');
    expect(
      within(list)
        .getAllByRole('listitem')
        .map((li) => li.textContent),
    ).toEqual(['ホーム', '/管理', '/文書管理']);
    // グループ段はリンクではない（遷移先を持たない分類の名前である）。
    expect(within(crumb()).queryByRole('link', { name: '管理' })).not.toBeInTheDocument();
  });

  it('renders the 運用 group and the parent screen for SC-11', async () => {
    await renderLayout(['platform-operator'], '/admin/config-viewer');
    const list = within(crumb()).getByRole('list');
    expect(
      within(list)
        .getAllByRole('listitem')
        .map((li) => li.textContent),
    ).toEqual(['ホーム', '/運用', '/ダッシュボード', '/構成ビューア']);
    // 親画面の段はリンクである（モックの SC-11 crumb と同じ）。
    expect(within(crumb()).getByRole('link', { name: 'ダッシュボード' })).toHaveAttribute(
      'href',
      '/admin/ops',
    );
  });

  it('renders no group segment for the 利用者 group screens', async () => {
    await renderLayout([], '/ask');
    expect(within(crumb()).queryByText('利用者')).not.toBeInTheDocument();
    expect(within(within(crumb()).getByRole('list')).getAllByRole('listitem')).toHaveLength(2);
  });

  // 🔴 存在秘匿（IADR-0009）: パンくずは「そのパスが実在し、どのグループの何の画面か」を
  // 名指しする。権限外では 1 段も描かない——未知パスと区別が付かないこと。
  it('renders no breadcrumb at all for a role-gated screen the user cannot see', async () => {
    await renderLayout(['user'], '/admin/config-viewer');
    await screen.findByRole('heading', { name: '見つかりませんでした' });
    expect(screen.queryByRole('navigation', { name: 'パンくず' })).not.toBeInTheDocument();
    expect(screen.queryByText('構成ビューア')).not.toBeInTheDocument();
  });

  it('renders no breadcrumb for an unknown path', async () => {
    await renderLayout(['user'], '/no-such-screen');
    await screen.findByRole('heading', { name: '見つかりませんでした' });
    expect(screen.queryByRole('navigation', { name: 'パンくず' })).not.toBeInTheDocument();
  });
});

// IADR-0009 / IADR-0124 決定 8: 存在秘匿。「不在（未知パス）」と「権限による秘匿（RequireRole）」で
// 描画が割れると、シェルが出るかどうかで資源の存在を推測できてしまう。同じ画面になることを固定する。
describe('existence hiding: unknown path and forbidden path render alike (IADR-0009)', () => {
  const heading = () => screen.getByRole('heading', { name: '見つかりませんでした' });

  it('renders NotFound inside the common shell for an unknown path', async () => {
    await renderLayout(['user'], '/no-such-screen');
    expect(
      await screen.findByRole('heading', { name: '見つかりませんでした' }),
    ).toBeInTheDocument();
    // シェル（ナビ・ブランド）が付いた状態で出る。
    expect(nav()).toBeInTheDocument();
    expect(screen.getByText('汎用プラットフォーム')).toBeInTheDocument();
  });

  it('renders NotFound inside the common shell for a role-gated path (SC-11)', async () => {
    await renderLayout(['user'], '/admin/config-viewer');
    expect(
      await screen.findByRole('heading', { name: '見つかりませんでした' }),
    ).toBeInTheDocument();
    expect(nav()).toBeInTheDocument();
    expect(screen.getByText('汎用プラットフォーム')).toBeInTheDocument();
  });

  /**
   * 比べるのは**共通シェルの本文領域（Outlet の器）**である。
   *
   * NFR / [[IADR-0134]]: 以前は見出しの親（NotFound 自身の `<main>`）を比べていたが、
   * それだと **NotFound を包む要素の違いが比較の外に落ちる**——変異試験で、未知パス側だけを
   * `<div>` で包んでも素通りすることを実測した。包む要素が違えば「シェルが出るかどうか」と
   * 同種の手がかりになるため、器ごと比べる。器は Layout の `<main>`、その中の
   * `<main>` が NotFound（DOM 順で外側が先）。
   */
  const outletContainer = () => screen.getAllByRole('main')[0];

  it('produces the same not-found markup in both cases', async () => {
    const unknown = await renderLayout(['user'], '/no-such-screen');
    expect(heading()).toBeInTheDocument();
    const unknownHtml = outletContainer().outerHTML;
    unknown.unmount();

    await renderLayout(['user'], '/admin/config-viewer');
    expect(heading()).toBeInTheDocument();
    const forbiddenHtml = outletContainer().outerHTML;

    expect(unknownHtml).toBeTruthy();
    expect(forbiddenHtml).toBe(unknownHtml);
  });
});
