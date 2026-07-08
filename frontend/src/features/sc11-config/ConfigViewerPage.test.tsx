import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { ApiError } from '@foundation/api/ApiError';

// SC-11 #137, FR-15: 実効構成（バージョン・段・イベント接続・ポート・コネクタ）の表示と、
// 秘匿(404)/異常系を検証する。データソース /bff/admin/config はモックする。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { ConfigViewerPage } from './ConfigViewerPage';

const CONFIG = {
  version: { gitCommit: 'abcdef1234567', appliedAt: '2026-07-08T00:00:00Z', appliedBy: 'gitops' },
  pipeline: [
    { name: 'ingest', service: 'ingestion', consumer: 'IngestConsumer', input: 'doc.received', outputs: ['doc.normalized'], enabled: true },
    { name: 'legacy', service: 'ingestion', consumer: 'OldConsumer', input: 'doc.x', outputs: [], enabled: false },
  ],
  eventBindings: [{ event: 'doc.normalized', publishers: ['ingestion'], subscribers: ['indexing'] }],
  ports: [{ port: 'llm', implementation: 'openai', target: 'gpt-x' }],
  connectors: [{ name: 'sharepoint', enabled: true }],
};

beforeEach(() => mocks.apiFetch.mockReset());

describe('ConfigViewerPage (SC-11 #137)', () => {
  it('fetches /admin/config and renders effective configuration', async () => {
    mocks.apiFetch.mockResolvedValue(CONFIG);
    render(<ConfigViewerPage />);

    // 構成バージョン（短縮 SHA）
    expect(await screen.findByText('abcdef1')).toBeInTheDocument();
    expect(mocks.apiFetch).toHaveBeenCalledWith('/admin/config');
    // 段・イベント接続・ポート・コネクタ
    expect(screen.getByText(/consumer: IngestConsumer/)).toBeInTheDocument();
    expect(screen.getByText('doc.normalized')).toBeInTheDocument();
    expect(screen.getByText('openai')).toBeInTheDocument();
    expect(screen.getByText(/sharepoint: 有効/)).toBeInTheDocument();
  });

  it('marks disabled stages (terminal output shown)', async () => {
    mocks.apiFetch.mockResolvedValue(CONFIG);
    render(<ConfigViewerPage />);
    expect(await screen.findByText(/legacy/)).toBeInTheDocument();
    expect(screen.getByText(/（終端）/)).toBeInTheDocument();
  });

  it('shows a neutral message on 404 (existence hidden)', async () => {
    mocks.apiFetch.mockImplementationOnce(async () => {
      throw new ApiError('notFound', 'x', 404);
    });
    render(<ConfigViewerPage />);
    expect(await screen.findByText('構成情報は利用できません。')).toBeInTheDocument();
  });

  it('shows an alert on server error', async () => {
    mocks.apiFetch.mockImplementationOnce(async () => {
      throw new ApiError('server', 'x', 500);
    });
    render(<ConfigViewerPage />);
    expect(await screen.findByRole('alert')).toHaveTextContent('取得に失敗');
  });
});
