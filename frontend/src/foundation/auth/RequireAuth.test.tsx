import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { AuthContext } from './AuthContext';
import type { AuthState } from './AuthContext';
import { RequireAuth } from './RequireAuth';

// Issue #126: 認証ガードの挙動（未認証は /login へ誘導・解決中は読み込み表示・認証済みは子を表示）。
function renderGuard(state: Partial<AuthState>) {
  const value: AuthState = {
    user: null,
    isAuthenticated: false,
    isLoading: false,
    login: async () => {},
    logout: async () => {},
    ...state,
  };
  return render(
    <AuthContext.Provider value={value}>
      <MemoryRouter initialEntries={['/secret']}>
        <Routes>
          <Route
            path="/secret"
            element={
              <RequireAuth>
                <div>SECRET CONTENT</div>
              </RequireAuth>
            }
          />
          <Route path="/login" element={<div>LOGIN PAGE</div>} />
        </Routes>
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

describe('RequireAuth', () => {
  it('redirects unauthenticated users to /login', () => {
    renderGuard({ isAuthenticated: false, isLoading: false });
    expect(screen.getByText('LOGIN PAGE')).toBeInTheDocument();
    expect(screen.queryByText('SECRET CONTENT')).not.toBeInTheDocument();
  });

  it('shows a loading indicator while auth state resolves', () => {
    renderGuard({ isLoading: true });
    expect(screen.getByRole('status')).toHaveTextContent('読み込み中');
  });

  it('renders children when authenticated', () => {
    renderGuard({ isAuthenticated: true, isLoading: false });
    expect(screen.getByText('SECRET CONTENT')).toBeInTheDocument();
  });
});
