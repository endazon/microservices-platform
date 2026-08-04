import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import { createRoute } from '@tanstack/react-router';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
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
import { createSc11ConfigRoute } from '../sc11-config';

const SUMMARY = {
  totalSearches: 12,
  totalAnswers: 8,
  usageTrend: [{ date: '2026-07-08', eventType: 'search', count: 5 }],
  topSearchTerms: [{ term: 'ABAC', count: 3 }],
  quality: { up: 6, down: 2, total: 8, satisfactionRate: 0.75 },
};

// 本画面のルート定義は RequireRole（platform-admin 限定）で守られているが、本テストの対象は
// **画面内部の描画とロール別導線**であり、権限外ロール（'user'）でも画面本体を描画する必要がある。
// そこでガードを外したルートを同じパスに置く（ガードの挙動は sc11-config/access.test.tsx と
// foundation の RequireRole.test.tsx が担う）。SC-11 の実ルートは導線の href 解決のため同居させる。
async function renderPage(roles: string[]) {
  return renderUnitRoute(
    (shell) => [
      createRoute({
        getParentRoute: () => shell,
        path: '/admin/ops',
        component: OperationsDashboardPage,
      }),
      createSc11ConfigRoute(shell),
    ],
    { initialEntry: '/admin/ops', roles },
  );
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mocks.opsLinks = {};
});

describe('OperationsDashboardPage (SC-10)', () => {
  it('renders the summary (totals, satisfaction, usage, trends) on 200', async () => {
    mocks.apiFetch.mockResolvedValue(SUMMARY);
    await renderPage(['platform-admin']);

    expect(await screen.findByText('12')).toBeInTheDocument(); // 検索総数
    expect(screen.getByText('8')).toBeInTheDocument(); // 回答総数
    expect(screen.getByText('75%')).toBeInTheDocument(); // 満足率
    expect(screen.getByText(/2026-07-08 \/ search: 5/)).toBeInTheDocument();
    expect(screen.getByText(/ABAC: 3/)).toBeInTheDocument();
  });

  it('shows only configured external tool links', async () => {
    mocks.opsLinks = { grafanaUrl: 'https://grafana.example', jaegerUrl: 'https://jaeger.example' };
    mocks.apiFetch.mockResolvedValue(SUMMARY);
    await renderPage(['platform-admin']);

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
    await renderPage(['platform-operator']);
    expect(await screen.findByRole('link', { name: /構成ビューア/ })).toHaveAttribute(
      'href',
      '/admin/config-viewer',
    );
  });

  it('hides the SC-11 link for non-ConfigViewer users', async () => {
    mocks.apiFetch.mockResolvedValue(SUMMARY);
    await renderPage(['user']);
    await screen.findByText('検索総数');
    expect(screen.queryByRole('link', { name: /構成ビューア/ })).not.toBeInTheDocument();
  });

  it('shows a neutral forbidden message on 403', async () => {
    mocks.apiFetch.mockRejectedValue(new ApiError('forbidden', 'x', 403));
    await renderPage(['user']);
    expect(await screen.findByText(/権限がありません/)).toBeInTheDocument();
  });

  it('shows a neutral not-available message on 404 (existence hidden)', async () => {
    mocks.apiFetch.mockRejectedValue(new ApiError('notFound', 'x', 404));
    await renderPage(['platform-admin']);
    expect(await screen.findByText(/利用できません/)).toBeInTheDocument();
  });

  it('shows an alert on server/network error', async () => {
    mocks.apiFetch.mockRejectedValue(new ApiError('server', 'x', 500));
    await renderPage(['platform-admin']);
    expect(await screen.findByRole('alert')).toHaveTextContent('取得に失敗');
  });
});
