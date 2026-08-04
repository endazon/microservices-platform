import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';

// SC-07, FR-12: 変換ジョブ画面が状況一覧・失敗の人手補正（再変換）・状況フィルタを BFF 経由で行うこと、
// 異常系を検証する。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { createSc07ConversionsRoute } from './index';
import { createSc03DocumentRoute } from '../sc03-document';
import { createSc06DataSourcesRoute } from '../sc06-datasources';

const JOBS = [
  { id: 'j1', sourceId: 's1', sourceType: 'filesystem', originalPath: '/docs/a.docx', status: 'failed', error: 'pandoc timeout', documentId: null, markdownUri: null, attempts: 2, createdAt: '2026-07-08T00:00:00Z', updatedAt: '2026-07-08T01:00:00Z' },
  { id: 'j2', sourceId: 's2', sourceType: 'wiki', originalPath: '/wiki/b.md', status: 'succeeded', error: null, documentId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', markdownUri: 'storage://b.md', attempts: 1, createdAt: '2026-07-08T00:00:00Z', updatedAt: '2026-07-08T00:30:00Z' },
];

// ADR-0031 / IADR-0124: 実ルート定義（RequireRole 込み）の上で描画する。SC-03 / SC-06 への
// 導線を解決するため、それらのルートも同居させる。
async function renderPage() {
  return renderUnitRoute(
    (shell) => [
      createSc07ConversionsRoute(shell),
      createSc03DocumentRoute(shell),
      createSc06DataSourcesRoute(shell),
    ],
    { initialEntry: '/admin/conversions', roles: ['platform-admin'] },
  );
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('ConversionJobsPage (SC-07)', () => {
  it('lists jobs with status and error, and links a succeeded job to its document', async () => {
    mocks.apiFetch.mockResolvedValue(JOBS);
    await renderPage();

    expect(await screen.findByText('/docs/a.docx')).toBeInTheDocument();
    expect(screen.getByText('pandoc timeout')).toBeInTheDocument();
    // 成功ジョブは生成文書（SC-03）へ遷移できる。
    expect(screen.getByRole('link', { name: '文書を見る' })).toHaveAttribute('href', '/docs/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
  });

  it('retries a failed job (human correction)', async () => {
    mocks.apiFetch.mockResolvedValueOnce(JOBS); // load
    mocks.apiFetch.mockResolvedValueOnce(undefined); // retry
    mocks.apiFetch.mockResolvedValueOnce(JOBS); // reload
    const user = userEvent.setup();
    await renderPage();
    await screen.findByText('/docs/a.docx');

    await user.click(screen.getByRole('button', { name: '再変換' }));

    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith('/conversion/jobs/j1/retry', { method: 'POST' }),
    );
  });

  it('filters by status', async () => {
    mocks.apiFetch.mockResolvedValue(JOBS);
    const user = userEvent.setup();
    await renderPage();
    await screen.findByText('/docs/a.docx');

    await user.selectOptions(screen.getByLabelText('状況で絞り込み'), 'failed');

    await waitFor(() => expect(mocks.apiFetch).toHaveBeenCalledWith('/conversion/jobs?status=failed'));
  });

  it('shows an alert when the list fails to load', async () => {
    mocks.apiFetch.mockRejectedValue(new Error('boom'));
    await renderPage();

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('取得に失敗'));
  });
});
