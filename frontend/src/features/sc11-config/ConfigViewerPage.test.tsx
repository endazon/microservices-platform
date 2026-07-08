import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { ApiError } from '@foundation/api/ApiError';

// SC-11 #137/#138, FR-15: 実効構成の表示、ドリフト一覧・0件OK、秘匿(404)/異常系を検証する。
// データソース /bff/admin/config と /bff/admin/config/drift をパスで振り分けてモックする。
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
const DRIFT_OK = { hasDrift: false, checkedAt: '2026-07-08T00:05:00Z', findings: [] };
// 深刻度は実 API（DriftDetector）が返す Warning / Info を用いる（IADR-0029）。
const DRIFT_FOUND = {
  hasDrift: true,
  checkedAt: '2026-07-08T00:05:00Z',
  findings: [
    { kind: 'StaleStage', severity: 'Warning', target: 'legacy', detail: '宣言に無い段が残留' },
    { kind: 'Unverifiable', severity: 'Info', target: 'ingest', detail: '照合できない要素' },
  ],
};

interface RouteOpts {
  config?: typeof CONFIG;
  configError?: ApiError;
  drift?: typeof DRIFT_OK | typeof DRIFT_FOUND;
  driftError?: boolean;
}
function route(opts: RouteOpts = {}) {
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/admin/config') {
      if (opts.configError) throw opts.configError;
      return opts.config ?? CONFIG;
    }
    if (path === '/admin/config/drift') {
      if (opts.driftError) throw new ApiError('server', 'x', 500);
      return opts.drift ?? DRIFT_OK;
    }
    throw new ApiError('unknown', 'x', 0);
  });
}

// 注意: アロー式の暗黙 return を避ける。mockReset() はモックを返し、それを beforeEach が返すと
// vitest がテスト後のティアダウン関数として実行して apiFetch(undefined) を呼ぶ（ルーティング用
// フォールバックの throw を誤発火させる）。ブロック本体で undefined を返す。
beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('ConfigViewerPage (SC-11 #137/#138)', () => {
  it('fetches /admin/config and renders effective configuration', async () => {
    route();
    render(<ConfigViewerPage />);

    expect(await screen.findByText('abcdef1')).toBeInTheDocument();
    expect(mocks.apiFetch).toHaveBeenCalledWith('/admin/config');
    expect(screen.getByText(/consumer: IngestConsumer/)).toBeInTheDocument();
    expect(screen.getByText('doc.normalized')).toBeInTheDocument();
    expect(screen.getByText('openai')).toBeInTheDocument();
    expect(screen.getByText(/sharepoint: 有効/)).toBeInTheDocument();
  });

  it('marks disabled stages (terminal output shown)', async () => {
    route();
    render(<ConfigViewerPage />);
    expect(await screen.findByText(/legacy/)).toBeInTheDocument();
    expect(screen.getByText(/（終端）/)).toBeInTheDocument();
  });

  it('shows OK when there is no drift (0 findings)', async () => {
    route({ drift: DRIFT_OK });
    render(<ConfigViewerPage />);
    expect(await screen.findByText(/ドリフトなし（OK）/)).toBeInTheDocument();
  });

  it('lists drift findings with kind/severity/target and colors by real severity (Warning/Info)', async () => {
    route({ drift: DRIFT_FOUND });
    render(<ConfigViewerPage />);
    expect(await screen.findByText('StaleStage')).toBeInTheDocument();
    expect(screen.getByText('宣言に無い段が残留')).toBeInTheDocument();
    // 実 API が返す深刻度（Warning=橙 / Info=灰）に着色されることを検証する。
    expect(screen.getByText('Warning')).toHaveStyle({ color: '#e67e22' });
    expect(screen.getByText('Info')).toHaveStyle({ color: '#7f8c8d' });
    expect(screen.getByText(/宣言との差分（ドリフト）: ドリフト 2 件/)).toBeInTheDocument();
  });

  // #138 §(1): ドリフト対象（finding.target）に一致する実効構成の段は、警告リンク付きで強調され、
  // (2) のドリフト明細（#drift-section）へリンクする。
  it('emphasizes effective-config stages matching a drift target with a link to the drift detail', async () => {
    route({ drift: DRIFT_FOUND });
    render(<ConfigViewerPage />);
    const pipeline = await screen.findByRole('list', { name: 'パイプライン段' });
    // legacy・ingest の2段がドリフト対象なので、いずれもドリフトリンクが付く。
    const marks = within(pipeline).getAllByRole('link', { name: /ドリフト/ });
    expect(marks.length).toBeGreaterThanOrEqual(1);
    marks.forEach((m) => expect(m).toHaveAttribute('href', '#drift-section'));
  });

  it('degrades the drift area only when drift fetch fails (config still shown)', async () => {
    route({ driftError: true });
    render(<ConfigViewerPage />);
    expect(await screen.findByText('abcdef1')).toBeInTheDocument();
    expect(screen.getByText('ドリフト情報は利用できません。')).toBeInTheDocument();
  });

  it('shows a neutral message on 404 config (existence hidden)', async () => {
    route({ configError: new ApiError('notFound', 'x', 404) });
    render(<ConfigViewerPage />);
    expect(await screen.findByText('構成情報は利用できません。')).toBeInTheDocument();
  });

  it('shows an alert on server error', async () => {
    route({ configError: new ApiError('server', 'x', 500) });
    render(<ConfigViewerPage />);
    expect(await screen.findByRole('alert')).toHaveTextContent('取得に失敗');
  });
});
