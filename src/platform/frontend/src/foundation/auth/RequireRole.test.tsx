import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { AuthContext } from './AuthContext';
import type { AuthState } from './AuthContext';
import { RequireRole } from './RequireRole';

// IADR-0035 / IADR-0009: 権限外は NotFound（存在秘匿）。読み込み中は中立表示。
// ADR-0032 / IADR-0273: 身元は /bff/auth/me の形（roles 配列。トークンは無い）。

function renderGuard(state: Partial<AuthState>) {
  const value: AuthState = {
    user: null,
    isAuthenticated: true,
    isLoading: false,
    login: async () => {},
    logout: async () => {},
    ...state,
  };
  return render(
    <AuthContext.Provider value={value}>
      <RequireRole anyOf={['platform-admin']}>
        <div>ADMIN CONTENT</div>
      </RequireRole>
    </AuthContext.Provider>,
  );
}

function userWithRoles(roles: string[]) {
  return { name: 'tester', subject: 'tester', roles };
}

describe('RequireRole', () => {
  it('renders children when the user has a required role', () => {
    renderGuard({ user: userWithRoles(['platform-admin']) });
    expect(screen.getByText('ADMIN CONTENT')).toBeInTheDocument();
  });

  it('hides existence (NotFound) when the user lacks the role', () => {
    renderGuard({ user: userWithRoles(['user']) });
    expect(screen.queryByText('ADMIN CONTENT')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '見つかりませんでした' })).toBeInTheDocument();
  });

  it('hides existence for an unauthenticated-looking (no roles) user', () => {
    renderGuard({ user: null });
    expect(screen.queryByText('ADMIN CONTENT')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '見つかりませんでした' })).toBeInTheDocument();
  });

  it('shows a neutral loading indicator while auth resolves', () => {
    renderGuard({ isLoading: true });
    expect(screen.getByRole('status')).toHaveTextContent('読み込み中');
  });
});
