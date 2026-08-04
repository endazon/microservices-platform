import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import type { UserManager } from 'oidc-client-ts';

// ADR-0031 / IADR-0124 決定 9: OIDC の `state.returnTo` は**認可サーバを往復して戻る外部由来の値**である。
// 本スイートは CallbackPage を実際に描画し、返ってきた returnTo に応じて
// どこへ navigate するかを固定する（オープンリダイレクト対策の配線検査）。

const mocks = vi.hoisted(() => ({ navigate: vi.fn() }));
vi.mock('@tanstack/react-router', () => ({ useNavigate: () => mocks.navigate }));

import { CallbackPage } from './CallbackPage';
import { ENTRY_ROUTE_PATH } from '@foundation/routing/entryPath';

/** signinRedirectCallback が指定の state を返す UserManager スタブ。 */
function managerReturning(state: unknown): UserManager {
  return { signinRedirectCallback: async () => ({ state }) } as unknown as UserManager;
}

function failingManager(): UserManager {
  return {
    signinRedirectCallback: async () => {
      throw new Error('boom');
    },
  } as unknown as UserManager;
}

beforeEach(() => {
  mocks.navigate.mockReset();
});

describe('CallbackPage: 正当な内部パスへは戻す', () => {
  it.each(['/search', '/admin/ops', '/docs/abc'])('returns to %s', async (returnTo) => {
    render(<CallbackPage manager={managerReturning({ returnTo })} />);
    await waitFor(() =>
      expect(mocks.navigate).toHaveBeenCalledWith({ to: returnTo, replace: true }),
    );
  });
});

describe('CallbackPage: 外部への遷移を弾き主入口へ倒す（オープンリダイレクト対策）', () => {
  it.each([
    ['//evil.com'],
    ['https://evil.com'],
    ['http://evil.com'],
    ['javascript:alert(1)'],
    // バックスラッシュはブラウザの URL 解釈でスラッシュと同一視される（IADR-0124 §実測）。
    ['/\\evil.com'],
    ['\\\\evil.com'],
    ['evil.com'],
  ])('falls back to the entry screen for %s', async (returnTo) => {
    render(<CallbackPage manager={managerReturning({ returnTo })} />);
    await waitFor(() =>
      expect(mocks.navigate).toHaveBeenCalledWith({ to: ENTRY_ROUTE_PATH, replace: true }),
    );
    // 外部 origin へ navigate していないこと。
    expect(mocks.navigate).not.toHaveBeenCalledWith(
      expect.objectContaining({ to: returnTo }),
    );
  });
});

describe('CallbackPage: state が無い・型が違う場合も主入口へ倒す', () => {
  it.each([
    ['state なし', undefined],
    ['returnTo なし', {}],
    ['returnTo が数値', { returnTo: 42 }],
    ['returnTo が配列', { returnTo: ['/search'] }],
    ['returnTo が空文字', { returnTo: '' }],
  ])('falls back for %s', async (_label, state) => {
    render(<CallbackPage manager={managerReturning(state)} />);
    await waitFor(() =>
      expect(mocks.navigate).toHaveBeenCalledWith({ to: ENTRY_ROUTE_PATH, replace: true }),
    );
  });
});

describe('CallbackPage: 表示', () => {
  it('shows a neutral progress message while exchanging the code', () => {
    render(<CallbackPage manager={managerReturning({ returnTo: '/search' })} />);
    expect(screen.getByRole('status')).toHaveTextContent('サインイン処理中');
  });

  it('shows a neutral error message when the exchange fails (no navigation)', async () => {
    render(<CallbackPage manager={failingManager()} />);
    expect(await screen.findByRole('alert')).toHaveTextContent('サインインに失敗しました');
    expect(mocks.navigate).not.toHaveBeenCalled();
  });
});
