import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse, noContent } from '@foundation/testing/bffResponse';

// SC-11, FR-15, ADR-0018: 構成ビューアの再実装（#504）＋ 生成フックへの載せ替え（#519）。
// 実効構成・ドリフト・構成バージョン履歴の表示と、領域ごとの縮退を固定する。
//
// IADR-0135 決定 4（#519）: 3 本とも orval 生成フックで呼ぶため、モックは **`apiRequest`** に当てる
// ——生成コードは mutator（`bffFetch`）→ `apiRequest` を通り、`apiFetch` を差し替えても効かない。
// **パス文字列は載せ替えの前後で変わらない**（`bffFetch` が `/bff` 接頭辞を外して渡す）。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc11ConfigRoute } from './index';

const CONFIG = {
  version: {
    gitCommit: 'a3f81c2ffffffffffffffffffffffffffffffffff',
    appliedAt: '2026-07-22T14:05:00Z',
    appliedBy: 'argocd',
  },
  pipeline: [
    {
      name: 'convert',
      service: 'conversion-service',
      consumer: 'RawDocumentFetchedConsumer',
      input: 'RawDocumentFetched',
      outputs: ['DocumentNormalized'],
      enabled: true,
    },
    {
      name: 'embed',
      service: 'embedding-service',
      consumer: 'DocumentNormalizedConsumer',
      input: 'DocumentNormalized',
      outputs: ['DocumentEmbedded'],
      enabled: true,
    },
    {
      name: 'wiki-sync',
      service: 'wiki-sync-service',
      consumer: 'DocumentEmbeddedConsumer',
      input: 'DocumentEmbedded',
      outputs: [],
      enabled: false,
    },
  ],
  eventBindings: [
    { event: 'DocumentNormalized', publishers: ['conversion-service'], subscribers: ['embedding-service'] },
  ],
  ports: [{ port: 'embedding', implementation: 'VoyageAI', target: 'voyage-3.5' }],
  connectors: [
    { name: 'filesystem', enabled: true },
    { name: 'wiki', enabled: false },
  ],
};

const DRIFT = {
  hasDrift: true,
  checkedAt: '2026-07-22T14:10:00Z',
  findings: [
    {
      kind: 'BindingMismatch',
      severity: 'Warning',
      target: 'embed',
      detail: '宣言 64 / 実効 32（手動変更の疑い）',
    },
  ],
};

const NO_DRIFT = { hasDrift: false, checkedAt: '2026-07-22T14:10:00Z', findings: [] };

const HISTORY = [
  { gitCommit: 'a3f81c2000000', appliedAt: '2026-07-22T14:05:00Z', appliedBy: 'argocd', hadDrift: true },
  { gitCommit: '9d02b77000000', appliedAt: '2026-07-15T09:12:00Z', appliedBy: 'argocd', hadDrift: false },
];

/** パスごとに応答（または失敗）を差し替えるモック。3 本を独立に壊せるようにする。 */
function mockApi(
  overrides: Partial<Record<'config' | 'drift' | 'history', unknown | Error>> = {},
) {
  mocks.apiRequest.mockImplementation(async (path: string) => {
    const pick = (value: unknown) => {
      if (value instanceof Error) throw value;
      return jsonResponse(value);
    };
    if (path === '/admin/config/drift') return pick(overrides.drift ?? DRIFT);
    if (path === '/admin/config/history') return pick(overrides.history ?? HISTORY);
    return pick(overrides.config ?? CONFIG);
  });
}

async function renderPage(roles: readonly string[] = ['platform-operator']) {
  return renderUnitRoute((shell) => [createSc11ConfigRoute(shell)], {
    initialEntry: '/admin/config-viewer',
    roles,
  });
}

beforeEach(() => {
  mocks.apiRequest.mockReset();
});

afterEach(() => {
  act(() => {
    activate('ja');
  });
});

describe('ConfigViewerPage (SC-11)', () => {
  // 計画 §SC-11 §主要素: 実効構成（段・接続・ポート・コネクタ）＋ 構成バージョン。
  it('shows the effective configuration with the config version in the header', async () => {
    mockApi();
    await renderPage();

    expect(await screen.findByRole('heading', { name: '実効構成' })).toBeInTheDocument();
    // コミット ID は短縮 7 桁で示す（完全な値は title 属性）。
    // NFR, ADR-0031 / IADR-0134: 見出しは画面の静形、値は useQuery の解決後に出る。
    // 遅延ルート（lazyRouteComponent）では画面の mount が 1 tick 遅れるぶん
    // 取得の解決も後ろへずれるため、値は findBy* で待つ（見出しの findBy* では待てない）。
    expect(await screen.findByText('コミット a3f81c2')).toBeInTheDocument();
    expect(screen.getByText('適用者 argocd')).toBeInTheDocument();

    const stages = within(screen.getByRole('list', { name: 'パイプライン段' }));
    expect(stages.getByText('convert')).toBeInTheDocument();
    expect(stages.getByText('embed')).toBeInTheDocument();
    expect(stages.getByText('wiki-sync')).toBeInTheDocument();

    expect(screen.getByText('DocumentNormalized')).toBeInTheDocument();
    expect(screen.getByText('VoyageAI')).toBeInTheDocument();
    expect(screen.getByText('filesystem')).toBeInTheDocument();

    expect(mocks.apiRequest).toHaveBeenCalledWith('/admin/config', expect.anything());
    expect(mocks.apiRequest).toHaveBeenCalledWith('/admin/config/drift', expect.anything());
    expect(mocks.apiRequest).toHaveBeenCalledWith('/admin/config/history', expect.anything());
  });

  // 段の終端は「（終端）」で示す（outputs が空）。INDEX 決定 21: 無効段は淡色だけで示さない。
  it('marks a terminal stage and labels a disabled stage with text, not only opacity', async () => {
    mockApi();
    await renderPage();

    const stages = within(await screen.findByRole('list', { name: 'パイプライン段' }));
    expect(stages.getByText(/（終端）/)).toBeInTheDocument();
    // 「無効」は StatusBadge（アイコン ＋ テキスト）で出る。
    expect(stages.getAllByText('無効').length).toBeGreaterThan(0);
  });

  // 計画 §SC-11 §主要素: 宣言との差分表示（ドリフト警告）。実効構成側にも強調する。
  it('lists the drift findings and highlights the affected stage', async () => {
    mockApi();
    await renderPage();

    expect(await screen.findByText('接続の不一致')).toBeInTheDocument();
    expect(screen.getByText('宣言 64 / 実効 32（手動変更の疑い）')).toBeInTheDocument();
    // ヘッダの全体状態（色 ＋ アイコン ＋ テキスト）。
    expect(screen.getByText('ドリフト 1 件')).toBeInTheDocument();
    // 実効構成側の強調は明細セクションへのリンクとして出る。
    expect(screen.getByRole('link', { name: '⚠ ドリフト（明細へ）' })).toHaveAttribute(
      'href',
      '#sc11-drift-section',
    );
  });

  // 0 件でも「検出が実行済みであること」を確認時刻とともに示す（未検出と未実行を区別する）。
  it('states that there is no drift, with the time the check ran', async () => {
    mockApi({ drift: NO_DRIFT });
    await renderPage();

    expect(await screen.findByText('ドリフトなし（OK）。')).toBeInTheDocument();
    expect(screen.getByText('ドリフトなし')).toBeInTheDocument();
    expect(screen.getByText(/確認時刻/)).toBeInTheDocument();
  });

  // #139 / IADR-0046: 履歴は新しい順。hadDrift は 3 値（あり / なし / 不明）。
  it('lists the configuration version history in the order the API returns', async () => {
    mockApi();
    await renderPage();

    const table = within(await screen.findByRole('table', { name: '構成バージョン履歴の一覧' }));
    expect(table.getByText('a3f81c2')).toBeInTheDocument();
    expect(table.getByText('9d02b77')).toBeInTheDocument();
    expect(table.getByText('あり')).toBeInTheDocument();
    expect(table.getByText('なし')).toBeInTheDocument();
  });

  it('says the history is empty rather than hiding the section', async () => {
    mockApi({ history: [] });
    await renderPage();

    expect(await screen.findByText('適用履歴はありません。')).toBeInTheDocument();
  });

  // IADR-0129 決定 5: 3 本は独立。ドリフトが落ちても構成は出し続ける。
  // 決定 3 は**従の問い合わせにも効く**——5xx は中立化せず `role="alert"` の障害として出す
  // （中立文言へ寄せると運用者が「権限が無い」と誤読して障害を見逃す）。
  it('degrades only the drift section when the drift query fails', async () => {
    mockApi({ drift: new ApiError('server', 'サーバでエラーが発生しました。', 500) });
    await renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent('サーバでエラーが発生しました。');
    expect(screen.queryByText('ドリフト情報は利用できません。')).not.toBeInTheDocument();
    // 構成は表示され続ける。
    expect(screen.getByRole('list', { name: 'パイプライン段' })).toBeInTheDocument();
    // 取得できていないので全体バッジは出さない（「0 件」と紛れるため）。
    expect(screen.queryByText('ドリフトなし')).not.toBeInTheDocument();
  });

  // 404 は「不在」と「権限による秘匿」を区別しない中立文言へ寄せる（決定 3）。
  it('shows the neutral drift message for a 404 without an alert', async () => {
    mockApi({ drift: new ApiError('notFound', 'nope', 404) });
    await renderPage();

    expect(await screen.findByText('ドリフト情報は利用できません。')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('list', { name: 'パイプライン段' })).toBeInTheDocument();
  });

  it('degrades only the history section when the history query fails', async () => {
    mockApi({ history: new ApiError('server', 'サーバでエラーが発生しました。', 500) });
    await renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent('サーバでエラーが発生しました。');
    expect(screen.queryByText('バージョン履歴は利用できません。')).not.toBeInTheDocument();
    expect(screen.getByRole('list', { name: 'パイプライン段' })).toBeInTheDocument();
  });

  it('shows the neutral history message for a 404 without an alert', async () => {
    mockApi({ history: new ApiError('notFound', 'nope', 404) });
    await renderPage();

    expect(await screen.findByText('バージョン履歴は利用できません。')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  // IADR-0009: 404 は「不在」と「権限による秘匿」を区別しない中立文言にする。
  it('shows a neutral message for a 404 without revealing whether it exists', async () => {
    mockApi({ config: new ApiError('notFound', 'nope', 404) });
    await renderPage();

    expect(await screen.findByText('構成情報は利用できません。')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  // 5xx は中立化しない（系の状態であって資源の存在ではない）。
  it('surfaces a server failure as an alert instead of the neutral message', async () => {
    mockApi({ config: new ApiError('server', 'サーバでエラーが発生しました。', 500) });
    await renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent('サーバでエラーが発生しました。');
    expect(screen.queryByText('構成情報は利用できません。')).not.toBeInTheDocument();
  });

  // 実効構成が取れなければ他の 2 領域も出さない（何に対する差分か読めないため）。
  // **ドリフトは成功したまま**にして、実効構成の失敗だけで隠れることを見る（既定の DRIFT は 1 件）。
  it('hides the drift and history sections when the effective config is unavailable', async () => {
    mockApi({ config: new ApiError('notFound', 'nope', 404) });
    await renderPage();

    await screen.findByText('構成情報は利用できません。');
    expect(screen.queryByText('接続の不一致')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('table', { name: '構成バージョン履歴の一覧' }),
    ).not.toBeInTheDocument();
    // ヘッダのバッジも出さない。**明細が無いのに件数だけが残る**のが IADR-0129 決定 5 が
    // 名指しで禁じた形である（3 本のクエリは独立に走るため実際に起こる組み合わせ）。
    expect(screen.queryByText('ドリフト 1 件')).not.toBeInTheDocument();
  });

  // 同じことを 5xx でも見る（404 は「秘匿」、5xx は「障害」であり経路が違う）。
  it('hides the drift badge when the effective config fails with a server error', async () => {
    mockApi({ config: new ApiError('server', 'サーバでエラーが発生しました。', 500) });
    await renderPage();

    await screen.findByRole('alert');
    expect(screen.queryByText('ドリフト 1 件')).not.toBeInTheDocument();
  });

  // 参照専用。構成を変更する操作は画面に存在しない。**先に見えるはずの要素を確かめてから**
  // 無いことを見る（#502 の M3 の教訓）。
  it('offers no way to change the configuration (read-only screen)', async () => {
    mockApi();
    await renderPage();

    // 見えるはずのもの（再取得ボタンと注記）が在ることを先に確かめる。
    expect(await screen.findByRole('button', { name: '再取得' })).toBeInTheDocument();
    expect(screen.getByText(/構成の変更は Git 経由（GitOps）に限ります。/)).toBeInTheDocument();
    // 参照以外のボタンは無い。
    const buttons = screen.getAllByRole('button').map((b) => b.textContent);
    expect(buttons).toEqual(['再取得']);
  });

  // 再取得は 3 本を取り直す（手書きの再取得は持たない。invalidateQueries だけ）。
  it('refetches all three queries when the refresh button is pressed', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('heading', { name: '実効構成' });
    const before = mocks.apiRequest.mock.calls.length;

    await user.click(screen.getByRole('button', { name: '再取得' }));

    await waitFor(() => expect(mocks.apiRequest.mock.calls.length).toBe(before + 3));
  });

  // SC-11, IADR-0135 決定 7［2026-08-06 追記］: 履歴が**本文なし**（204）で返っても画面は落ちない。
  //
  // このテストは「配列を期待する面に配列でない値が入る経路が型の外に実在する」（決定 7）ことを
  // **画面の挙動として**固定する。`bffFetch` は本文が空なら `{}` を返すため、
  // `okData(res) ?? []` では `??` が発火せず `{}` がそのまま `entries.map` へ届いていた
  // （＝ルートごとクラッシュした）。`okArray` が「配列でなければ空配列」まで詰めることで縮退する。
  it('degrades to the empty-history message when the history response has no body (204)', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) => {
      if (path === '/admin/config/history') return noContent();
      if (path === '/admin/config/drift') return jsonResponse(NO_DRIFT);
      return jsonResponse(CONFIG);
    });
    await renderPage();

    // 履歴だけが縮退し、他の領域（実効構成）は通常どおり描画される。
    expect(await screen.findByText('適用履歴はありません。')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '実効構成' })).toBeInTheDocument();
  });

  // ADR-0031: 文言は Lingui のカタログ（ja / en）。
  it('renders in English when the en locale is active', async () => {
    mockApi();
    await renderPage();
    await screen.findByRole('heading', { name: '実効構成' });

    act(() => {
      activate('en');
    });

    expect(
      await screen.findByRole('heading', { name: 'Effective configuration' }),
    ).toBeInTheDocument();
  });
});
