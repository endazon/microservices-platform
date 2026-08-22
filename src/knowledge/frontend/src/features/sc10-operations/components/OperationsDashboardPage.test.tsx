import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { resetAppConfigCache } from '@foundation/config/runtimeConfig';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-10, UC-05, FR-10: 運用ダッシュボードの再実装（#504）＋ 生成フックへの載せ替え（#519）。
// サマリ表示・外部ツール導線・SC-11 導線・存在秘匿の中立化・**着手保留の要素が無いこと**を固定する。
//
// IADR-0135 決定 4（#519）: 生成コードは mutator（`bffFetch`）→ **`apiRequest`** を通るため、
// モックは `apiRequest` に当てる（`apiFetch` を差し替えても効かない）。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc10OperationsRoute, sc10OperationsNav } from '../routes/sc10OperationsRoute';

const SUMMARY = {
  totalSearches: 1840,
  totalAnswers: 312,
  usageTrend: [
    { date: '2026-08-04', eventType: 'search', count: 120 },
    { date: '2026-08-04', eventType: 'answer', count: 44 },
  ],
  topSearchTerms: [
    { term: '経費精算', count: 51 },
    { term: '就業規則', count: 33 },
  ],
  quality: { up: 82, down: 18, total: 100, satisfactionRate: 0.82 },
};

async function renderPage(roles: readonly string[] = ['platform-admin']) {
  return renderUnitRoute((shell) => [createSc10OperationsRoute(shell)], {
    initialEntry: '/admin/ops',
    roles,
  });
}

beforeEach(() => {
  mocks.apiRequest.mockReset();
  resetAppConfigCache();
  window.__APP_CONFIG__ = undefined;
});

afterEach(() => {
  window.__APP_CONFIG__ = undefined;
  resetAppConfigCache();
  act(() => {
    activate('ja');
  });
});

describe('OperationsDashboardPage (SC-10)', () => {
  // FR-10: 利用状況・検索傾向・回答品質を可視化する。
  it('shows the usage, trend and answer-quality summary', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(SUMMARY));
    await renderPage();

    expect(await screen.findByRole('heading', { name: '運用ダッシュボード' })).toBeInTheDocument();
    // NFR, ADR-0031 / IADR-0134: 見出しは画面の静形、値は useQuery の解決後に出る。
    // 遅延ルート（lazyRouteComponent）では画面の mount が 1 tick 遅れるぶん
    // 取得の解決も後ろへずれるため、値は findBy* で待つ（見出しの findBy* では待てない）。
    expect(await screen.findByText('1840')).toBeInTheDocument();
    expect(screen.getByText('312')).toBeInTheDocument();
    expect(screen.getByText('82%')).toBeInTheDocument();

    const usage = within(screen.getByRole('table', { name: '利用状況（日次）の一覧' }));
    expect(usage.getByText('検索')).toBeInTheDocument();
    expect(usage.getByText('AI 回答')).toBeInTheDocument();

    const trend = within(screen.getByRole('table', { name: '検索傾向（上位語）の一覧' }));
    expect(trend.getByText('経費精算')).toBeInTheDocument();

    expect(mocks.apiRequest).toHaveBeenCalledWith('/dashboard/summary?days=7', expect.anything());
  });

  // 契約の期間指定は ?days= の 1 本。既定は 7 日（BFF の既定と揃える）。
  it('starts at seven days and sends the selected period to the API', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(SUMMARY));
    const user = userEvent.setup();
    await renderPage();
    await screen.findByText('1840');

    expect(screen.getByLabelText('集計期間')).toHaveValue('7');
    await user.selectOptions(screen.getByLabelText('集計期間'), '30');

    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        '/dashboard/summary?days=30',
        expect.anything(),
      ),
    );
  });

  // 未知のイベント種別を握り潰さない（`—`・「不明」へ丸めない）。
  it('shows an unknown usage event type verbatim', async () => {
    mocks.apiRequest.mockResolvedValue(
      jsonResponse({
        ...SUMMARY,
        usageTrend: [{ date: '2026-08-04', eventType: 'export', count: 3 }],
      }),
    );
    await renderPage();

    const usage = within(await screen.findByRole('table', { name: '利用状況（日次）の一覧' }));
    expect(usage.getByText('export')).toBeInTheDocument();
  });

  it('says the period has no usage rather than showing an empty table', async () => {
    mocks.apiRequest.mockResolvedValue(
      jsonResponse({ ...SUMMARY, usageTrend: [], topSearchTerms: [] }),
    );
    await renderPage();

    expect(await screen.findByText('期間内の利用はありません。')).toBeInTheDocument();
    expect(screen.getByText('検索傾向はまだありません。')).toBeInTheDocument();
  });

  // IADR-0129 決定 3 / IADR-0009: 403 と 404 は**同じ**中立文言。文言から権限の有無を読ませない。
  it('shows the same neutral message for a 403', async () => {
    mocks.apiRequest.mockRejectedValue(new ApiError('forbidden', '権限がありません。', 403));
    await renderPage();

    expect(await screen.findByText('運用ダッシュボードは利用できません。')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows the same neutral message for a 404', async () => {
    mocks.apiRequest.mockRejectedValue(new ApiError('notFound', '見つかりませんでした。', 404));
    await renderPage();

    expect(await screen.findByText('運用ダッシュボードは利用できません。')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  // 5xx は中立化しない（系の状態であって資源の存在ではない。運用者が障害を見逃さないようにする）。
  it('surfaces a server failure as an alert instead of the neutral message', async () => {
    mocks.apiRequest.mockRejectedValue(
      new ApiError('server', 'サーバでエラーが発生しました。', 500),
    );
    await renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent('サーバでエラーが発生しました。');
    expect(screen.queryByText('運用ダッシュボードは利用できません。')).not.toBeInTheDocument();
  });

  // 実行時 config で注入されたツールだけを出す（未設定は描かない）。
  it('renders only the observability tools that runtime config injects', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(SUMMARY));
    window.__APP_CONFIG__ = {
      opsLinks: { grafanaUrl: 'https://grafana.example', jaegerUrl: 'https://jaeger.example' },
    };
    resetAppConfigCache();
    await renderPage();

    expect(await screen.findByRole('link', { name: /Grafana/ })).toHaveAttribute(
      'href',
      'https://grafana.example',
    );
    expect(screen.getByRole('link', { name: /Jaeger \/ Tempo/ })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Kiali/ })).not.toBeInTheDocument();
  });

  it('says the tool links are not configured when none is injected', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(SUMMARY));
    await renderPage();

    expect(await screen.findByText('外部ツールの導線は未設定です。')).toBeInTheDocument();
  });

  // 計画の遷移図 SC10 --> SC11。IADR-0129 決定 4: 権限で出し分けない（到達しない分岐を作らない）。
  it('always offers the link to SC-11 for anyone who can open this screen', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(SUMMARY));
    await renderPage();

    expect(await screen.findByRole('link', { name: '構成ビューア →' })).toHaveAttribute(
      'href',
      '/admin/config-viewer',
    );
  });

  // IADR-0119: 「ナレッジ健全性」節は**着手保留の要求**に属するため画面に出さない
  // （どの要求かは IADR-0119 と画面仕様書が持つ。**保留対象の ID をここへ書くと
  //  check-test-traceability.js が「実装が先行している」と誤って報告する**——
  //  その ID は、当該機能に着手する issue が初めて書く）。
  // **まず「見えるはずの条件」で描画されていることを確かめてから**無いことを assert する
  // （#502 の M3 の教訓）。
  it('does not render the knowledge-health section (its requirement is on hold)', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(SUMMARY));
    await renderPage();

    // 見えるはずのもの（サマリ）が在ることを先に確かめる。
    expect(await screen.findByText('1840')).toBeInTheDocument();

    expect(screen.queryByText(/ナレッジ健全性/)).not.toBeInTheDocument();
    expect(screen.queryByText(/孤立文書/)).not.toBeInTheDocument();
    expect(screen.queryByText(/未解決リンク/)).not.toBeInTheDocument();
    expect(screen.queryByText(/未要約クラスタ/)).not.toBeInTheDocument();
    expect(screen.queryByText(/陳腐化文書/)).not.toBeInTheDocument();
    // 辺の型ごとの使用件数・フォールバック警告・除外の注記も同じ節に属する。
    expect(screen.queryByText(/辺の型/)).not.toBeInTheDocument();
    expect(screen.queryByText(/フォールバック/)).not.toBeInTheDocument();
    expect(screen.queryByText(/個人資料/)).not.toBeInTheDocument();
  });

  // 契約に無い KPI（SLO・LLM コスト）は「—」のカードすら置かない。
  // **KPI カードは契約から出せる 3 枚だけ**であることを、カードの見出しの集合で固定する
  // （「SLO」という語そのものは副題「…SLO・コストは Grafana で参照」に出るため、
  //  テキスト検索では区別できない——**カードが在るか**を見る）。
  it('renders only the three KPI cards the contract can fill', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(SUMMARY));
    await renderPage();

    expect(await screen.findByText('1840')).toBeInTheDocument();

    // KPI カードの見出し（CardTitle）は h2。一覧・詳細ツールの見出しも h2 なので、
    // KPI が増減したらこの集合が動く。
    expect(screen.getAllByRole('heading', { level: 2 }).map((h) => h.textContent)).toEqual([
      '検索総数',
      '回答総数',
      '満足率',
      '利用状況（日次）',
      '検索傾向（上位語）',
      '詳細ツール',
    ]);

    expect(screen.queryByRole('heading', { name: 'SLO' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'LLMコスト' })).not.toBeInTheDocument();
    expect(screen.queryByText(/p95/)).not.toBeInTheDocument();
    // モックの「人/日」（一意利用者数）も契約に無い。件数だけを出す。
    expect(screen.queryByText(/人\/日/)).not.toBeInTheDocument();
  });

  // ADR-0031: 文言は Lingui のカタログ（ja / en）。
  it('renders in English when the en locale is active', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(SUMMARY));
    await renderPage();
    await screen.findByText('1840');

    act(() => {
      activate('en');
    });

    expect(
      await screen.findByRole('heading', { name: 'Operations dashboard' }),
    ).toBeInTheDocument();
  });
});

// IADR-0009 / IADR-0035: 存在秘匿。SC-10 は **platform-admin または platform-operator** である。
// **［2026-08-09 / #544］計画を正として広げた**（計画 §SC-10「運用者・管理者ロール限定」。
// 裁定 Q19 / Q28）。従前はデータ源 /bff/dashboard/summary と後段 DashboardService が
// ともに AdminOnly のため据え置いていたが、**3 層すべてを同時に広げたので解けた**
// （[[IADR-0129]] 決定 4 の追記を参照）。
describe('SC-10 access control (#504)', () => {
  it('grants access to platform-admin', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(SUMMARY));
    await renderPage(['platform-admin']);
    expect(await screen.findByRole('heading', { name: '運用ダッシュボード' })).toBeInTheDocument();
  });

  // ★ #544: **運用者にも開く**（計画 §SC-10「運用者・管理者ロール限定」。裁定 Q19 / Q28）。
  //
  // **従前は運用者も NotFound だった**——データ源と後段が `AdminOnly` のままで画面だけ広げると
  // 「開くと必ず 403 になる画面」になるため据え置いていた。**#544 で 3 層すべてを広げたので解除する。**
  it('grants access to platform-operator', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(SUMMARY));
    await renderPage(['platform-operator']);
    expect(await screen.findByRole('heading', { name: '運用ダッシュボード' })).toBeInTheDocument();
  });

  // ★ **広げすぎない。** 一般利用者には従来どおり存在を秘匿する（IADR-0009 / IADR-0035）。
  //
  // **この対が無いと「広げる」作業は検査にならない**——権限を全開にしても
  // `grants access to …` は緑のまま通るためである。
  it('hides existence (NotFound) for a plain user', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(SUMMARY));
    await renderPage(['user']);
    expect(
      await screen.findByRole('heading', { name: '見つかりませんでした' }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: '運用ダッシュボード' })).not.toBeInTheDocument();
    // 権限外ではサマリ API を呼ばない（要求の有無から存在を推測させない）。
    expect(mocks.apiRequest).not.toHaveBeenCalled();
  });

  it('produces the same not-found markup as a plain absence', async () => {
    const { NotFound } = await import('@foundation/ui/NotFound');
    const { render } = await import('@testing-library/react');

    await renderPage(['user']);
    const forbidden = (await screen.findByRole('heading', { name: '見つかりませんでした' }))
      .parentElement?.outerHTML;

    const absent = render(<NotFound />);
    expect(forbidden).toBeTruthy();
    expect(forbidden).toBe(absent.container.firstElementChild?.outerHTML);
  });

  // ★ #544: ナビも**ルートゲートと同じ範囲**でなければならない——
  // 揃っていないと「ナビに出ないのに URL では開ける」か「押すと NotFound」のどちらかになる。
  it('exposes a nav entry limited to the admin and operator roles in the ops group', () => {
    expect(sc10OperationsNav.requiresAnyRole).toEqual(['platform-admin', 'platform-operator']);
    // 05_screens §共通シェル: SC-10 は「運用」グループ。
    expect(sc10OperationsNav.group).toBe('ops');
  });
});
