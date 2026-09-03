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
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn(), setOption: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

// ADR-0071 §結果（#1197）: 「しきい値未満の語が出ない」検査は**図にも**要る。
// jsdom には canvas も実 SVG 描画も無く、器の中身は空のままなので、
// **器のテキストを見ても何も確かめたことにならない**（何を渡しても緑になる）。
// `echartsLoader` へモックを当て、**図へ渡った option そのもの**を見る
// （`EChart.test.tsx` / `GraphCanvas.test.tsx` と同じ構図）。
vi.mock('../../../lib/echarts/echartsLoader', () => ({
  loadECharts: vi.fn().mockResolvedValue({
    init: () => ({ setOption: mocks.setOption, dispose: vi.fn(), resize: vi.fn() }),
  }),
}));

/** 図へ渡った option を 1 本の文字列にする（系列名・軸ラベルを横断で検索するため）。 */
function chartOptionsText() {
  return mocks.setOption.mock.calls.map((c) => JSON.stringify(c[0])).join('\n');
}

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
  // FR-10, SC-10, ADR-0071 決定 1・2（#1197）: 検索傾向の出現件数の下限。
  searchTermMinCount: 3,
};

async function renderPage(roles: readonly string[] = ['platform-admin']) {
  return renderUnitRoute((shell) => [createSc10OperationsRoute(shell)], {
    initialEntry: '/admin/ops',
    roles,
  });
}

beforeEach(() => {
  mocks.apiRequest.mockReset();
  // **図の記録もテストごとに空へ戻す**——残すと、前のテストで渡った語を後のテストが拾う。
  mocks.setOption.mockReset();
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

  // ───────────────────────────────────────────────────────────────────────
  // SC-10, FR-10, ADR-0071 決定 1・2（#1197）: 検索傾向の出現件数しきい値。
  // ───────────────────────────────────────────────────────────────────────

  // ★ ADR-0071 決定 2: **現在のしきい値を画面に併記する。**
  // 値が変われば見える語も変わるため、数字だけでは時系列の比較が成り立たない。
  it('states the search-term threshold alongside the trend card', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(SUMMARY));
    await renderPage();

    expect(await screen.findByText('3 件以上検索された語のみを表示します。')).toBeInTheDocument();
  });

  // ★ 併記の値は**契約が返した値**である（画面の定数ではない）。
  // 定数を焼き込んだ実装ではこのテストが落ちる。
  it('takes the stated threshold from the contract rather than a hard-coded default', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse({ ...SUMMARY, searchTermMinCount: 7 }));
    await renderPage();

    expect(await screen.findByText('7 件以上検索された語のみを表示します。')).toBeInTheDocument();
  });

  // ★ ADR-0071 §結果「SC-10 の画面テストに『しきい値未満の語が出ない』検査が要る」。
  //
  // **しきい値未満の語を含むサマリを渡す。** 後段が既にふるっている前提であっても、
  // 画面が素通しなら**後段の取りこぼしがそのまま運用者へ出る**（[[IADR-0044]] の多層防御）。
  // **表と棒グラフの両方**を見る——片方だけだと、図に残ったまま気付けない。
  it('omits terms below the threshold from both the table and the chart', async () => {
    mocks.apiRequest.mockResolvedValue(
      jsonResponse({
        ...SUMMARY,
        topSearchTerms: [
          { term: '経費精算', count: 51 },
          { term: '田中の評価面談メモ', count: 2 },
        ],
      }),
    );
    await renderPage();

    // **陽性対照**: しきい値以上の語は在る（描画そのものが空振りしていない）。
    const trend = within(await screen.findByRole('table', { name: '検索傾向（上位語）の一覧' }));
    expect(trend.getByText('経費精算')).toBeInTheDocument();
    await waitFor(() => expect(chartOptionsText()).toContain('経費精算'));

    // 表にも図にも出ない。
    expect(screen.queryByText('田中の評価面談メモ')).not.toBeInTheDocument();
    expect(chartOptionsText()).not.toContain('田中の評価面談メモ');
  });

  // ★ しきい値未満の語**しか**無い期間は、「まだありません」へ倒す。
  // 🔴 **「その他 1 件」に相当する行を出さない**（ADR-0071 決定 1。M 自体が推測の材料になる）。
  it('says there is no trend yet when every term is below the threshold', async () => {
    mocks.apiRequest.mockResolvedValue(
      jsonResponse({ ...SUMMARY, topSearchTerms: [{ term: '私信', count: 2 }] }),
    );
    await renderPage();

    expect(await screen.findByText('検索傾向はまだありません。')).toBeInTheDocument();
    expect(screen.queryByText('私信')).not.toBeInTheDocument();
    expect(screen.queryByText(/その他/)).not.toBeInTheDocument();
    // **併記は空でも消えない**——0 件はしきい値の効果が最も強く出た状態であり、
    // そこで数字が消えると「なぜ空なのか」が読めなくなる。
    expect(screen.getByText('3 件以上検索された語のみを表示します。')).toBeInTheDocument();
  });

  // ★ 🔴 **稼働 k3s で実測した事故の再現**（#1197 / [[IADR-0357]] 決定 3 の追記）。
  //
  // しきい値を知らない**旧 BFF** が後段に居ると、応答 JSON に `searchTermMinCount` が**無い**
  // （生成型は `number` と言うが実体は `undefined`）。`count >= undefined` は**全件 false** であり、
  // 素で使うと**一覧が丸ごと空になる**——「知らないものを消す」向きで、いちばん避けたい壊れ方である。
  // **0 へ倒し、ふるわず、下限も名乗らない。**
  it('shows every term and states no threshold when the field is missing (older BFF)', async () => {
    const withoutThreshold = { ...SUMMARY };
    delete (withoutThreshold as Partial<typeof SUMMARY>).searchTermMinCount;
    mocks.apiRequest.mockResolvedValue(jsonResponse(withoutThreshold));
    await renderPage();

    const trend = within(await screen.findByRole('table', { name: '検索傾向（上位語）の一覧' }));
    expect(trend.getByText('経費精算')).toBeInTheDocument();
    expect(trend.getByText('就業規則')).toBeInTheDocument();
    expect(screen.queryByText(/件以上検索された語のみを表示します。/)).not.toBeInTheDocument();
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
  //
  // ★［2026-08-29 追記 / #443］🔴 **保留とは独立に、出してはならない理由が 1 本増えた。**
  // 同節の 7 指標のうち、**観測値を実際に生産している経路があるのは 1 件（孤立文書数）だけ**である
  // （#443 で 1 件目を配線した。実測日 2026-08-29）。集計 API は**観測値の無い指標も 0 件で返す**——
  // これは「指標が消えたのか 0 なのか」を画面が区別できるようにするための設計であり、
  // **測っている前提**の話である。**生産者の無い 6 指標を 0 と描くと「問題が無い」と読める。**
  // 未計測を「健全」と表示するのは真逆の誤読であり、節を作れば必ず起きる。
  //
  // **したがって、IADR-0119 の保留が解けても、この 8 行をまとめて外してはならない。**
  // **外してよいのは、その指標に生産者がある行だけである**（[[IADR-0299]] 決定 6）。
  // 生産されない理由は指標ごとに違い（永続化が未設計／前提機能が未実装／観測値モデルの制約／
  // 別経路〔Grafana〕で観測済み）、解ける順序も別である。
  //
  // ★［2026-09-03 追記 / #1186］**陳腐化文書数の生産者ができた**（planning#494 の裁定で
  // しきい値が確定し、本文更新起点の判定を入れた。[[IADR-0353]]）。**それでも節は開かない** ——
  // 生産者の無い指標が 3 件（未解決リンク／未要約クラスタ／辺の型ごとの使用件数）残るためである。
  // planning#494 自身が「**生産者の無い指標を 0 件として並べてはならない**」「**節を開く条件は
  // 別の判断である**」「**実装側がこの理由で節を置かず否定テストを残したのは正しい**」と明記した。
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

  // ADR-0031 §採用技術一覧（チャート = ECharts）/ #788:
  // **図は表を置き換えない。** 図が読めない利用者（スクリーンリーダ・色覚特性）と、
  // 図の遅延読み込みが済んでいない瞬間の両方で、同じ数値が表から読める必要がある。
  it('draws the charts in addition to the tables, not instead of them', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(SUMMARY));
    await renderPage();

    expect(
      await screen.findByRole('img', { name: '利用状況（日次）の推移グラフ' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('img', { name: '検索傾向（上位語）の棒グラフ' })).toBeInTheDocument();
    // 表は残っている（同じ数値が読める）。
    expect(screen.getByRole('table', { name: '利用状況（日次）の一覧' })).toBeInTheDocument();
    const trendTable = screen.getByRole('table', { name: '検索傾向（上位語）の一覧' });
    expect(within(trendTable).getByText('経費精算')).toBeInTheDocument();
  });

  // ADR-0031 §採用技術一覧（テーブル = TanStack Table）/ #788:
  // 並べ替えが効き、向きが `aria-sort` で読める（INDEX 決定 21「色だけで意味を持たせない」）。
  it('sorts the search-trend table and exposes the direction', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(SUMMARY));
    const user = userEvent.setup();
    await renderPage();

    const table = await screen.findByRole('table', { name: '検索傾向（上位語）の一覧' });
    const header = within(table).getByRole('columnheader', { name: /検索語/ });
    expect(header).toHaveAttribute('aria-sort', 'none');

    await user.click(within(header).getByRole('button'));
    expect(header).toHaveAttribute('aria-sort', 'ascending');
    const firstTerm = within(within(table).getAllByRole('rowgroup')[1]).getAllByRole('row')[0]
      .textContent;
    expect(firstTerm).toContain('就業規則');
  });

  // ★ #544: ナビも**ルートゲートと同じ範囲**でなければならない——
  // 揃っていないと「ナビに出ないのに URL では開ける」か「押すと NotFound」のどちらかになる。
  it('exposes a nav entry limited to the admin and operator roles in the ops group', () => {
    expect(sc10OperationsNav.requiresAnyRole).toEqual(['platform-admin', 'platform-operator']);
    // 05_screens §共通シェル: SC-10 は「運用」グループ。
    expect(sc10OperationsNav.group).toBe('ops');
  });
});
