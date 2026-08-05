import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';

// SC-10 → SC-11, UC-05: 運用の導線（計画 05_screens 遷移図 `SC10 --> SC11`）。
// **2 ルートを 1 本のルータへ載せて**実際に遷移する（画面単体のテストでは導線が繋がっているかを見られない）。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiFetch: mocks.apiFetch,
}));

import { createSc10OperationsRoute } from './sc10-operations';
import { createSc11ConfigRoute } from './sc11-config';

const SUMMARY = {
  totalSearches: 12,
  totalAnswers: 3,
  usageTrend: [],
  topSearchTerms: [],
  quality: { up: 2, down: 1, total: 3, satisfactionRate: 0.66 },
};

const CONFIG = {
  version: { gitCommit: 'a3f81c2', appliedAt: '2026-07-22T14:05:00Z', appliedBy: 'argocd' },
  pipeline: [],
  eventBindings: [],
  ports: [],
  connectors: [],
};

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path.startsWith('/dashboard/summary')) return SUMMARY;
    if (path === '/admin/config/drift') {
      return { hasDrift: false, checkedAt: '2026-08-05T00:00:00Z', findings: [] };
    }
    if (path === '/admin/config/history') return [];
    return CONFIG;
  });
});

describe('operations flow (SC-10 → SC-11)', () => {
  // 計画の遷移図 `SC10 --> SC11`。管理者は運用ダッシュボードから構成ビューアへ辿れる。
  it('walks from the operations dashboard to the configuration viewer', async () => {
    const user = userEvent.setup();
    await renderUnitRoute(
      (shell) => [createSc10OperationsRoute(shell), createSc11ConfigRoute(shell)],
      { initialEntry: '/admin/ops', roles: ['platform-admin'] },
    );

    expect(await screen.findByRole('heading', { name: '運用ダッシュボード' })).toBeInTheDocument();

    await user.click(screen.getByRole('link', { name: '構成ビューア →' }));

    expect(await screen.findByRole('heading', { name: '実効構成' })).toBeInTheDocument();
    expect(screen.getByText('コミット a3f81c2')).toBeInTheDocument();
  });

  // 運用者は SC-11 へ直接到達できるが、SC-10 は存在しないものとして扱われる
  // （IADR-0129 決定 4。閲覧ロールの差異は環流の提案 7 で裁定を仰いでいる）。
  it('lets an operator reach SC-11 directly while SC-10 stays hidden', async () => {
    const operatorAtConfig = await renderUnitRoute(
      (shell) => [createSc10OperationsRoute(shell), createSc11ConfigRoute(shell)],
      { initialEntry: '/admin/config-viewer', roles: ['platform-operator'] },
    );
    expect(await screen.findByRole('heading', { name: '実効構成' })).toBeInTheDocument();
    operatorAtConfig.unmount();

    await renderUnitRoute(
      (shell) => [createSc10OperationsRoute(shell), createSc11ConfigRoute(shell)],
      { initialEntry: '/admin/ops', roles: ['platform-operator'] },
    );
    expect(
      await screen.findByRole('heading', { name: '見つかりませんでした' }),
    ).toBeInTheDocument();
  });
});
