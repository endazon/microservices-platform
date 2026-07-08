import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { User } from 'oidc-client-ts';
import { AuthContext } from '@foundation/auth/AuthContext';
import type { AuthState } from '@foundation/auth/AuthContext';
import { ApiError } from '@foundation/api/ApiError';

// SC-10, FR-10, UC-05: BFF 集約の表示・存在秘匿・外部導線・異常系を検証する。
// apiFetch と実行時 config はモックし、ロール判定は実 roles.ts を通す（access_token から復号）。
const mocks = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  opsLinks: {} as { grafanaUrl?: string; jaegerUrl?: string; kialiUrl?: string },
}));

vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));
vi.mock('@foundation/config/runtimeConfig', () => ({
  appConfig: () => ({ opsLinks: mocks.opsLinks }),
}));

import { OperationsDashboardPage } from './OperationsDashboardPage';

const SUMMARY = {
  totalSearches: 12,
  totalAnswers: 8,
  usageTrend: [{ date: '2026-07-08', eventType: 'search', count: 5 }],
  topSearchTerms: [{ term: 'ABAC', count: 3 }],
  quality: { up: 6, down: 2, total: 8, satisfactionRate: 0.75 },
};

function makeJwt(payload: unknown): string {
  const b64url = (obj: unknown) =>
    btoa(JSON.stringify(obj)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `h.${b64url(payload)}.sig`;
}

function renderPage(roles: string[]) {
  const user = { access_token: makeJwt({ realm_access: { roles } }) } as unknown as User;
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
        <OperationsDashboardPage />
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mocks.opsLinks = {};
});

describe('OperationsDashboardPage (SC-10)', () => {
  it('renders the summary (totals, satisfaction, usage, trends) on 200', async () => {
    mocks.apiFetch.mockResolvedValue(SUMMARY);
    renderPage(['platform-admin']);

    expect(await screen.findByText('12')).toBeInTheDocument(); // 検索総数
    expect(screen.getByText('8')).toBeInTheDocument(); // 回答総数
    expect(screen.getByText('75%')).toBeInTheDocument(); // 満足率
    expect(screen.getByText(/2026-07-08 \/ search: 5/)).toBeInTheDocument();
    expect(screen.getByText(/ABAC: 3/)).toBeInTheDocument();
  });

  it('shows only configured external tool links', async () => {
    mocks.opsLinks = { grafanaUrl: 'https://grafana.example', jaegerUrl: 'https://jaeger.example' };
    mocks.apiFetch.mockResolvedValue(SUMMARY);
    renderPage(['platform-admin']);

    expect(await screen.findByRole('link', { name: 'Grafana' })).toHaveAttribute(
      'href',
      'https://grafana.example',
    );
    expect(screen.getByRole('link', { name: 'Jaeger' })).toBeInTheDocument();
    // Kiali は未設定＝非表示。
    expect(screen.queryByRole('link', { name: 'Kiali' })).not.toBeInTheDocument();
  });

  it('shows the SC-11 config-viewer link for ConfigViewer roles', async () => {
    mocks.apiFetch.mockResolvedValue(SUMMARY);
    renderPage(['platform-operator']);
    expect(await screen.findByRole('link', { name: /構成ビューア/ })).toHaveAttribute('href', '/config');
  });

  it('hides the SC-11 link for non-ConfigViewer users', async () => {
    mocks.apiFetch.mockResolvedValue(SUMMARY);
    renderPage(['user']);
    await screen.findByText('検索総数');
    expect(screen.queryByRole('link', { name: /構成ビューア/ })).not.toBeInTheDocument();
  });

  it('shows a neutral forbidden message on 403', async () => {
    mocks.apiFetch.mockRejectedValue(new ApiError('forbidden', 'x', 403));
    renderPage(['user']);
    expect(await screen.findByText(/権限がありません/)).toBeInTheDocument();
  });

  it('shows a neutral not-available message on 404 (existence hidden)', async () => {
    mocks.apiFetch.mockRejectedValue(new ApiError('notFound', 'x', 404));
    renderPage(['platform-admin']);
    expect(await screen.findByText(/利用できません/)).toBeInTheDocument();
  });

  it('shows an alert on server/network error', async () => {
    mocks.apiFetch.mockRejectedValue(new ApiError('server', 'x', 500));
    renderPage(['platform-admin']);
    expect(await screen.findByRole('alert')).toHaveTextContent('取得に失敗');
  });
});
