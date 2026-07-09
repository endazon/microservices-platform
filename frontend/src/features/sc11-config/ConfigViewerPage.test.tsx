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

// #139: 構成バージョン履歴（新しい順）。API（/admin/config/history）が並び順・データ源を担い、本画面は表示のみ。
const HISTORY = [
  { gitCommit: 'abcdef1234567', appliedAt: '2026-07-08T00:00:00Z', appliedBy: 'gitops', hadDrift: false },
  { gitCommit: '9876543210fed', appliedAt: '2026-07-05T00:00:00Z', appliedBy: 'argocd', hadDrift: true },
];

interface RouteOpts {
  config?: typeof CONFIG;
  configError?: ApiError;
  drift?: typeof DRIFT_OK | typeof DRIFT_FOUND;
  driftError?: boolean;
  history?: typeof HISTORY;
  historyError?: boolean;
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
    if (path === '/admin/config/history') {
      if (opts.historyError) throw new ApiError('server', 'x', 500);
      return opts.history ?? [];
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
    expect(marks).toHaveLength(2);
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

  // #139: 構成バージョン履歴を新しい順・短縮コミット・ドリフト有無つきで一覧する。
  it('lists config version history with short commit, applied info and drift状態', async () => {
    route({ history: HISTORY });
    render(<ConfigViewerPage />);

    const table = await screen.findByRole('table', { name: '構成バージョン履歴' });
    expect(mocks.apiFetch).toHaveBeenCalledWith('/admin/config/history');
    // 短縮コミット（7 桁）・適用者・ドリフト有無（false→なし / true→あり）が表示される。
    expect(within(table).getByText('abcdef1')).toBeInTheDocument();
    expect(within(table).getByText('9876543')).toBeInTheDocument();
    expect(within(table).getByText('gitops')).toBeInTheDocument();
    expect(within(table).getByText('あり')).toBeInTheDocument();
    expect(within(table).getByText('なし')).toBeInTheDocument();
  });

  it('shows empty history message when there is no applied history', async () => {
    route({ history: [] });
    render(<ConfigViewerPage />);
    expect(await screen.findByText('適用履歴はありません。')).toBeInTheDocument();
  });

  it('degrades the history area only when history fetch fails (config still shown)', async () => {
    route({ historyError: true });
    render(<ConfigViewerPage />);
    expect(await screen.findByText('abcdef1')).toBeInTheDocument();
    expect(screen.getByText('バージョン履歴は利用できません。')).toBeInTheDocument();
  });
});
