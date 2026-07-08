import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { User } from 'oidc-client-ts';
import { AuthContext } from '@foundation/auth/AuthContext';
import type { AuthState } from '@foundation/auth/AuthContext';
import { Layout } from './Layout';

// Issue #136 / IADR-0034: ナビは features の登録から導出し、権限外の項目は描画しない（存在秘匿）。

function makeJwt(payload: unknown): string {
  const b64url = (obj: unknown) =>
    btoa(JSON.stringify(obj)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `h.${b64url(payload)}.sig`;
}

function renderLayout(roles: string[]) {
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
  return render(
    <AuthContext.Provider value={value}>
      <MemoryRouter>
        <Layout />
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

describe('Layout navigation (role-gated)', () => {
  it('always shows the Home link', () => {
    renderLayout([]);
    expect(screen.getByRole('link', { name: 'ホーム' })).toBeInTheDocument();
  });

  it('shows the 運用 (SC-10) link for platform-admin', () => {
    renderLayout(['platform-admin']);
    expect(screen.getByRole('link', { name: '運用' })).toBeInTheDocument();
  });

  it('hides the 運用 link for users without the admin role (existence hidden)', () => {
    renderLayout(['user']);
    expect(screen.queryByRole('link', { name: '運用' })).not.toBeInTheDocument();
  });
});
