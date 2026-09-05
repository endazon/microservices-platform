import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { blobResponse, jsonResponse, noContent } from '@foundation/testing/bffResponse';

// SC-07, UC-06, FR-12: 変換ジョブ画面の再実装（#503）。
// 計画（05_screens §SC-07 §データソース・2026-08-04 確定）の 4 状態モデル・状態フィルタ・
// **再変換は管理者ロール限定**・同一ジョブの直列化に従うことを固定する。
//
// IADR-0135 決定 4（#519）: 生成コードは mutator（`bffFetch`）→ **`apiRequest`** を通るため、
// モックは `apiRequest` に当てる（`apiFetch` を差し替えても効かない）。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc07ConversionsRoute } from '../routes/sc07ConversionsRoute';

const DOC_ID = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const FAILED_JOB = {
  id: '9812abcd-0000-0000-0000-000000000001',
  sourceId: 's1',
  sourceType: 'filesystem',
  originalPath: '障害対応手順書.docx',
  status: 'failed',
  error: '図コード化失敗',
  documentId: null,
  markdownUri: null,
  attempts: 3,
  createdAt: '2026-08-01T00:00:00Z',
  updatedAt: '2026-08-01T01:00:00Z',
};
const SUCCEEDED_JOB = {
  ...FAILED_JOB,
  id: '9805abcd-0000-0000-0000-000000000002',
  originalPath: '経費精算規程.docx',
  status: 'succeeded',
  error: null,
  documentId: DOC_ID,
};
const PROCESSING_JOB = {
  ...FAILED_JOB,
  id: '9800abcd-0000-0000-0000-000000000003',
  originalPath: '組織図2026.pptx',
  status: 'processing',
  error: null,
};

// #651: 図が画像保持へ縮退した成功ジョブ。**`status` は `succeeded` である**——縮退は
// ジョブの失敗ではない（`05_screens:320`「ジョブ状態モデルは 4 値である」）。
const RETAINED_JOB = {
  ...SUCCEEDED_JOB,
  id: '9807abcd-0000-0000-0000-000000000004',
  originalPath: 'ネットワーク構成図.pptx',
  diagramsCoded: 2,
  diagramsRetained: 1,
  hasCorrection: false,
};

/** 補正済みの図を持つジョブ（再変換すると補正が失われる側）。 */
const CORRECTED_JOB = {
  ...RETAINED_JOB,
  id: '9809abcd-0000-0000-0000-000000000005',
  originalPath: '業務フロー.docx',
  status: 'failed',
  hasCorrection: true,
};

/**
 * #1192: テキスト層を持たない PDF。**`status` は `succeeded`** で、内訳 `hasBody` が false になる
 * （計画 ADR「PDF の本文抽出は pandoc の外に置く」決定 3。失敗にもデッドレターにもしない）。
 */
const BODY_ABSENT_JOB = {
  ...SUCCEEDED_JOB,
  id: '9813abcd-0000-0000-0000-000000000007',
  originalPath: '押印済み契約書.pdf',
  documentId: 'cccccccc-cccc-cccc-cccc-cccccccccccc',
  diagramsCoded: 0,
  diagramsRetained: 0,
  hasCorrection: false,
  deadLettered: false,
  hasBody: false,
};

/** 失敗かつ縮退あり。**再変換と人手補正の両方の導線が同じ行に出る**（決定 7 の試験に要る）。 */
const FAILED_RETAINED_JOB = {
  ...FAILED_JOB,
  id: '9811abcd-0000-0000-0000-000000000006',
  originalPath: '設備一覧.xlsx',
  diagramsCoded: 0,
  diagramsRetained: 1,
  hasCorrection: false,
};

const RETAINED_FIGURE = {
  figureId: 'fig-1',
  coded: false,
  language: null,
  code: null,
  imageUri: 'storage://figures/fig-1.png',
  imageContentType: 'image/png',
  caption: '拠点間の接続',
  corrected: false,
  correctedAt: null,
};

/** コード化済みの図。**2 ペインの対象にしない**（Phase 1 は縮退のやり直しに限る）。 */
const CODED_FIGURE = {
  ...RETAINED_FIGURE,
  figureId: 'fig-2',
  coded: true,
  language: 'mermaid',
  code: 'graph TD; A-->B;',
  imageUri: null,
  caption: '状態遷移',
};

const PNG = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

/** ジョブ一覧・図一覧・画像を経路で振り分ける（画像だけが非 JSON である）。 */
function mockConversionApi(
  jobs: unknown[],
  opts: {
    figures?: unknown[];
    onRetry?: () => Promise<unknown>;
    onCorrect?: () => Promise<unknown>;
  } = {},
) {
  mocks.apiRequest.mockImplementation(async (path: string) => {
    if (path.includes('/image')) return blobResponse(PNG);
    if (path.includes('/correction')) {
      return opts.onCorrect
        ? opts.onCorrect()
        : jsonResponse({ figureId: 'fig-1', markdownUri: 'storage://m.md', correctedFigures: 1 });
    }
    if (path.endsWith('/retry')) return opts.onRetry ? opts.onRetry() : noContent();
    if (path.includes('/figures'))
      return jsonResponse(opts.figures ?? [RETAINED_FIGURE, CODED_FIGURE]);
    return jsonResponse(jobs);
  });
}

/** 既定は管理者（再変換ボタンが出る側）。運用者・無権限は各テストで明示する。 */
async function renderPage(roles: readonly string[] = ['platform-admin']) {
  return renderUnitRoute((shell) => [createSc07ConversionsRoute(shell)], {
    initialEntry: '/admin/conversions',
    roles,
  });
}

/**
 * jsdom は `URL.createObjectURL` を実装しない（#651 の実測）。右ペインは Blob をオブジェクト URL
 * にして表示するので、**スタブを置かないと画像の描画経路そのものを試験できない**。
 */
const OBJECT_URL = 'blob:mock/figure-1';

beforeEach(() => {
  mocks.apiRequest.mockReset();
  vi.stubGlobal(
    'URL',
    Object.assign(globalThis.URL, {
      createObjectURL: vi.fn(() => OBJECT_URL),
      revokeObjectURL: vi.fn(),
    }),
  );
});

afterEach(() => {
  act(() => {
    activate('ja');
  });
});

describe('ConversionJobsPage (SC-07)', () => {
  // UC-06 代替フロー（2026-08-04 追記）: 管理者が SC-07 で変換ジョブの一覧・状態・失敗一覧を確認する。
  // INDEX 決定 21: 状態は色だけで意味を持たせない（アイコン ＋ テキストを伴う）。
  it('lists jobs with the four-value status model', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([FAILED_JOB, SUCCEEDED_JOB, PROCESSING_JOB]));
    await renderPage();

    expect(await screen.findByText('障害対応手順書.docx')).toBeInTheDocument();
    // 状態バッジは表の中で見る（同じ文言が絞り込みの選択肢にも出るため）。
    const table = within(screen.getByRole('table'));
    expect(table.getByText('失敗')).toBeInTheDocument();
    expect(table.getByText('完了')).toBeInTheDocument();
    expect(table.getByText('変換中')).toBeInTheDocument();
    // 失敗の理由は備考列に出る（状態バッジは 4 値のまま保つ）。
    expect(screen.getByText('図コード化失敗')).toBeInTheDocument();
    expect(mocks.apiRequest).toHaveBeenCalledWith(
      '/conversion/jobs',
      expect.objectContaining({ method: 'GET' }),
    );
  });

  // 計画確定: 照会 API は状態でのフィルタを備える（「失敗のみ」フィルタの実体）。
  it('sends the status filter to the query API', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([FAILED_JOB]));
    const user = userEvent.setup();
    await renderPage();
    await screen.findByText('障害対応手順書.docx');

    await user.selectOptions(screen.getByLabelText('状態で絞り込み'), 'failed');

    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        '/conversion/jobs?status=failed',
        expect.objectContaining({ method: 'GET' }),
      ),
    );
  });

  // 既定は「すべて」（モックの一覧が failed 以外を含むため。画面仕様書 §絞り込みの既定値）。
  it('starts with the "all" filter so the first view is not narrowed', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
    await renderPage();

    expect(await screen.findByLabelText('状態で絞り込み')).toHaveValue('');
    expect(mocks.apiRequest).toHaveBeenCalledWith(
      '/conversion/jobs',
      expect.objectContaining({ method: 'GET' }),
    );
  });

  // 05_screens §SC-07（2026-08-04 確定）: 再変換の実行権限は管理者ロールに限る。
  it('lets an administrator retry a failed job', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([FAILED_JOB]));
    const user = userEvent.setup();
    await renderPage(['platform-admin']);

    await user.click(await screen.findByRole('button', { name: '再変換' }));

    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        `/conversion/jobs/${FAILED_JOB.id}/retry`,
        expect.objectContaining({ method: 'POST' }),
      ),
    );
    expect(await screen.findByText('再変換を受け付けました。')).toBeInTheDocument();
  });

  // IADR-0127 決定 5: 再変換の成功後は invalidateQueries だけを行う（手書きの再取得を持たない）。
  // これが外れると `failed` → `queued` へ動いたジョブが一覧に古い状態で残る。
  it('refetches the list after a successful retry', async () => {
    mocks.apiRequest.mockImplementation((path: string) =>
      path.endsWith('/retry')
        ? Promise.resolve(noContent())
        : Promise.resolve(jsonResponse([FAILED_JOB])),
    );
    const user = userEvent.setup();
    await renderPage(['platform-admin']);

    await user.click(await screen.findByRole('button', { name: '再変換' }));

    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.filter(([path]) => path === '/conversion/jobs'),
      ).toHaveLength(2),
    );
  });

  // **権限別の出し分け。** 運用者は画面を見られるが再変換は実行できない（計画 2026-08-04 確定）。
  // 無言でボタンを消すと「状態のせいで押せない」と読めるため、理由を書く（IADR-0127 決定 1）。
  it('hides the retry button from an operator and says why', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([FAILED_JOB]));
    await renderPage(['platform-operator']);

    // まず「見えるはずの条件」で描画されていることを確かめる（失敗ジョブの行が在ること）。
    expect(await screen.findByText('障害対応手順書.docx')).toBeInTheDocument();
    expect(within(screen.getByRole('table')).getByText('失敗')).toBeInTheDocument();
    // そのうえで再変換ボタンが無いことと、理由が示されることを見る。
    expect(screen.queryByRole('button', { name: '再変換' })).not.toBeInTheDocument();
    expect(screen.getByText('再変換は管理者のみ実行できます')).toBeInTheDocument();
  });

  // 計画確定: 再変換できるのは failed のみ（直列化。processing 中の要求は拒否される）。
  it('offers no retry for jobs that are not failed', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([PROCESSING_JOB, SUCCEEDED_JOB]));
    await renderPage(['platform-admin']);

    expect(await screen.findByText('組織図2026.pptx')).toBeInTheDocument();
    expect(within(screen.getByRole('table')).getByText('変換中')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '再変換' })).not.toBeInTheDocument();
  });

  // 直列化の実体。UI 制御をすり抜けた要求（他の管理者が先に押した等）はサーバが 409 で拒否する。
  it('explains the 409 rejection as a serialisation conflict', async () => {
    mocks.apiRequest.mockImplementation((path: string) =>
      path.endsWith('/retry')
        ? Promise.reject(new ApiError('conflict', '競合が発生しました。', 409))
        : Promise.resolve(jsonResponse([FAILED_JOB])),
    );
    const user = userEvent.setup();
    await renderPage(['platform-admin']);

    await user.click(await screen.findByRole('button', { name: '再変換' }));

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(
        'このジョブは再変換できません（実行中、または失敗以外の状態です）。',
      ),
    );
    // INDEX 決定 21: 深刻度は色（琥珀の警告）だけでなくラベルの文言でも伝える
    // ——「エラー」のままだと、色を除いたときに 409（拒否）と 5xx（障害）の区別が消える。
    expect(screen.getByRole('alert')).toHaveTextContent('注意');
    expect(screen.getByRole('alert')).not.toHaveTextContent('エラー');
  });

  // 完了ジョブから変換結果（SC-03）へ遷移できる（計画の遷移図 SC07 -- 変換結果 --> SC03）。
  it('links a succeeded job to its document (SC-03)', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([SUCCEEDED_JOB]));
    await renderPage();

    expect(await screen.findByRole('link', { name: '変換結果の文書を開く' })).toHaveAttribute(
      'href',
      `/docs/${DOC_ID}`,
    );
  });

  // BFF は後段障害を空一覧へ縮退させない（502）。画面も「ジョブ無し」と見せない。
  it('shows an error instead of an empty list when the query fails', async () => {
    mocks.apiRequest.mockRejectedValue(
      new ApiError('server', 'サーバでエラーが発生しました。', 500),
    );
    await renderPage();

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('サーバでエラーが発生しました'),
    );
    expect(screen.queryByText('該当する変換ジョブはありません。')).not.toBeInTheDocument();
  });

  it('shows a neutral message when there is no job', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
    await renderPage();

    expect(await screen.findByText('該当する変換ジョブはありません。')).toBeInTheDocument();
  });

  // 存在秘匿（IADR-0009 / IADR-0035）: ロールを持たない利用者へ画面の存在を示さない。
  it('hides the screen from a user without any role', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([FAILED_JOB]));
    await renderPage([]);

    expect(
      screen.queryByRole('heading', { name: '変換ジョブ（pandoc＋LLM）' }),
    ).not.toBeInTheDocument();
    expect(mocks.apiRequest).not.toHaveBeenCalled();
  });

  // 導線: SC-06 へ戻れる（計画のパンくずが示す階層の逆方向）。
  it('links back to SC-06', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
    await renderPage();

    expect(screen.getByRole('link', { name: '← データソース管理へ戻る' })).toHaveAttribute(
      'href',
      '/admin/sources',
    );
  });

  // **［2026-08-10 / #651］この試験は反転させた。**
  //
  // 従前は「人手補正の 2 ペインが**無い**こと」を、理由「保存先の契約が無い」とともに固定していた。
  // 契約は #543 で、画面は #651 で載ったので、**その理由はもう成り立たない**。
  // 一方で **Phase 2（変換結果 Markdown 全体の編集）は計画が繰り延べたまま**であり
  // （`05_screens:330`）、**そこが射程外であることの回帰は残す価値がある**——本口
  // （`POST …/correction`）はコード片しか受け付けないので、全体編集欄を置くと契約を超える。
  it('offers per-figure code editing but no whole-Markdown editor (Phase 2 is out of scope)', async () => {
    mockConversionApi([RETAINED_JOB]);
    const user = userEvent.setup();
    await renderPage(['platform-admin']);

    await user.click(await screen.findByRole('button', { name: '人手補正' }));

    // Phase 1 の編集欄は**図ごと**である。
    expect(await screen.findByLabelText('図コード: fig-1')).toBeInTheDocument();
    // Phase 2 の要素は無い。
    expect(screen.queryByLabelText('変換結果 Markdown')).not.toBeInTheDocument();
    expect(screen.queryByText('原本プレビュー')).not.toBeInTheDocument();
  });

  it('renders in English when the en locale is active', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([FAILED_JOB]));
    activate('en');
    await renderPage();

    expect(
      await screen.findByRole('heading', { name: 'Conversion jobs (pandoc + LLM)' }),
    ).toBeInTheDocument();
    expect(within(screen.getByRole('table')).getByText('Failed')).toBeInTheDocument();
  });

  // SC-07, IADR-0135 決定 7［2026-08-06 追記］: 一覧が**本文なし**（204）で返っても画面は落ちない。
  //
  // 載せ替え前は `apiFetch` が空ボディで `undefined` を返すため `items = data ?? []` が実効ガード
  // だった。生成物の `bffFetch` は空ボディで `{}` を返すので `??` は発火せず、`items.length === 0`
  // も `{}.length === undefined` で救えず、`items.map` が `TypeError` を投げていた。
  // `okArray` が「配列でなければ空配列」まで詰めることで、載せ替え前と同じ縮退に戻る。

  // ── #651: 人手補正（Phase 1 = 図のコード化）。契約は #543 / IADR-0154。

  // 受け入れ基準「状態・備考・標識が契約から導出されている（`status` は 4 値のまま）」。
  // hi-fi:420「✕ 図コード化失敗（画像保持へ縮退済み）」／hi-fi:422「Mermaid 2図」「補正あり」。
  it('derives the retained/coded/corrected markers from the contract without a fifth status', async () => {
    mockConversionApi([{ ...RETAINED_JOB, hasCorrection: true }]);
    await renderPage(['platform-admin']);

    const table = within(await screen.findByRole('table'));
    // **`status` は succeeded のまま**。縮退はジョブの失敗ではない。
    expect(table.getByText('完了')).toBeInTheDocument();
    expect(table.getByText('図コード化失敗（画像保持へ縮退済み）')).toBeInTheDocument();
    expect(table.getByText('補正あり')).toBeInTheDocument();
    expect(table.getByText('Mermaid 2図')).toBeInTheDocument();
  });

  // #1192 / 計画 ADR「PDF の本文抽出は pandoc の外に置く」決定 3: テキスト層を持たない PDF は
  // **「本文なしで完了」を理由つきで**表示し、`failed` の列（再変換の対象）に並ばない。
  // 状態バッジは「完了」のまま（4 値モデル不変）。色だけで意味を持たせない（標識はテキスト付き）。
  it('shows a succeeded PDF without a text layer as completed-without-body, not as failed', async () => {
    mockConversionApi([BODY_ABSENT_JOB]);
    await renderPage(['platform-admin']);

    const table = within(await screen.findByRole('table'));
    expect(table.getByText('押印済み契約書.pdf')).toBeInTheDocument();
    // **`status` は succeeded のまま**。失敗ではない。
    expect(table.getByText('完了')).toBeInTheDocument();
    expect(table.queryByText('失敗')).not.toBeInTheDocument();
    expect(table.getByText('本文なしで完了')).toBeInTheDocument();
    // 理由つき（備考列）。
    expect(
      table.getByText('テキスト層が無いため本文を抽出できませんでした（原本を参照）'),
    ).toBeInTheDocument();
    // 再変換の対象に並ばない（管理者でもボタンは出ない）。結果への導線はある（原本参照のみの文書）。
    expect(screen.queryByRole('button', { name: '再変換' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '人手補正' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: '変換結果の文書を開く' })).toBeInTheDocument();
  });

  // 陽性対照: 本文ありの完了ジョブに「本文なしで完了」を出さない（導出が「常に真」になっていない側）。
  it('shows no body-absent marker for a succeeded job with a body', async () => {
    mockConversionApi([SUCCEEDED_JOB]);
    await renderPage(['platform-admin']);

    expect(await screen.findByText('経費精算規程.docx')).toBeInTheDocument();
    expect(screen.queryByText('本文なしで完了')).not.toBeInTheDocument();
  });

  // 縮退していないジョブに標識を出さない（導出が「常に真」になっていないことの側）。
  it('shows no correction affordance for a job with no retained figures', async () => {
    mockConversionApi([SUCCEEDED_JOB]);
    await renderPage(['platform-admin']);

    expect(await screen.findByText('経費精算規程.docx')).toBeInTheDocument();
    expect(screen.queryByText('図コード化失敗（画像保持へ縮退済み）')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '人手補正' })).not.toBeInTheDocument();
  });

  // 受け入れ基準「運用者には人手補正の導線が出ない」。**権限別の対**で固定する。
  // 無言で消さず理由を書く（IADR-0127 決定 1。人手補正は 3 口とも admin のみ＝IADR-0154 決定 6）。
  it('hides the manual-correction affordance from an operator and says why', async () => {
    mockConversionApi([RETAINED_JOB]);
    await renderPage(['platform-operator']);

    // まず「見えるはずの条件」で描画されていることを確かめる。
    expect(await screen.findByText('ネットワーク構成図.pptx')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '人手補正' })).not.toBeInTheDocument();
    expect(screen.getByText('人手補正は管理者のみ実行できます')).toBeInTheDocument();
  });

  // 受け入れ基準「管理者が 2 ペインを開き、コードを投稿して本文が差し替わる」。
  // **右ペインの画像は `/image` から取る**（`imageUri` の `storage://` を img に入れない）。
  it('lets an administrator open the two-pane editor and post a correction', async () => {
    mockConversionApi([RETAINED_JOB]);
    const user = userEvent.setup();
    await renderPage(['platform-admin']);

    await user.click(await screen.findByRole('button', { name: '人手補正' }));

    // 左ペイン: 縮退した図だけが編集対象になる（コード化済みの fig-2 は出さない）。
    const editor = await screen.findByLabelText('図コード: fig-1');
    expect(screen.queryByLabelText('図コード: fig-2')).not.toBeInTheDocument();

    // 右ペイン: 画像は BFF の /image から取得する。
    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        `/conversion/jobs/${RETAINED_JOB.id}/figures/fig-1/image`,
        expect.objectContaining({ method: 'GET' }),
      ),
    );
    // **要求が飛んだことだけでは足りない。** バイト列が解析層を通って `<img>` まで届いたことを見る
    // ——`bffFetch` が非 JSON を `JSON.parse` していた時期（#651 で直すまで）、要求は飛ぶが
    // ここで必ず失敗していた。**この行が無いと、本 issue が直した欠陥のままテストが通る**
    // （変異試験で実測: blob 分岐を戻してもこのテストは緑のままだった）。
    const img = await screen.findByAltText('補正対象の図の元画像');
    expect(img.getAttribute('src')).toBe(OBJECT_URL);
    // `storage://` をそのまま src に入れていないこと。
    expect(img.getAttribute('src') ?? '').not.toContain('storage://');

    await user.type(editor, 'graph TD; X-->Y;');
    await user.click(screen.getByRole('button', { name: '補正して再登録' }));

    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        `/conversion/jobs/${RETAINED_JOB.id}/figures/fig-1/correction`,
        expect.objectContaining({ method: 'POST' }),
      ),
    );
    expect(await screen.findByText('図を補正して再登録しました。')).toBeInTheDocument();
  });

  // 受け入れ基準「補正のあるジョブの再変換で確認を求め、**件数を示す**」。
  //
  // **確認は 409 を受けてから出す**（IADR-0154 決定 4）。先にダイアログを出して通ったら投げる形に
  // すると、一覧取得から押下までの間に入った補正を無確認で消す。件数もサーバの値でなければ嘘になる。
  it('asks for confirmation only after the server rejects the retry with 409, showing the count', async () => {
    let discarded = false;
    mockConversionApi([CORRECTED_JOB], {
      onRetry: () =>
        discarded
          ? Promise.resolve(noContent())
          : Promise.reject(
              new ApiError('conflict', '競合が発生しました。', 409, [], {
                error: 'corrections_would_be_lost',
                correctedFigures: 3,
              }),
            ),
    });
    const user = userEvent.setup();
    await renderPage(['platform-admin']);

    await user.click(await screen.findByRole('button', { name: '再変換' }));

    // 件数を翻訳済みの文へ差し込む（サーバの日本語をそのまま出さない）。
    // **確認の同定は role ではなく「破棄ボタンが在ること」で行う。** 本確認は非モーダルなので
    // `alertdialog` を名乗らない（実装しない振る舞いを role で宣言しない）。
    const dialog = await screen.findByRole('alert');
    expect(
      within(dialog).getByText(/このジョブには人手補正が\s*3\s*件あります/),
    ).toBeInTheDocument();

    // 確認するまで discardCorrections は飛ばない。
    expect(
      mocks.apiRequest.mock.calls.filter((call) => String(call[0]).includes('discardCorrections')),
    ).toHaveLength(0);

    discarded = true;
    await user.click(within(dialog).getByRole('button', { name: '補正を破棄して再変換' }));

    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        `/conversion/jobs/${CORRECTED_JOB.id}/retry?discardCorrections=true`,
        expect.objectContaining({ method: 'POST' }),
      ),
    );
    expect(await screen.findByText('再変換を受け付けました。')).toBeInTheDocument();
  });

  // **確認へキーボードで到達できること**（AI レビュー 🟡 / [[IADR-0157]] 決定 2）。
  //
  // 確認は表より**前**に描かれるのに対し、それを出す「再変換」ボタンは**表の行の中**にある。
  // つまり確認のボタンは焦点位置より **DOM 上で手前**にあり、**前方 Tab では届かない**。
  // 焦点を移していないと、読み上げは届くのに操作手段が無い利用者が出る。
  it('moves focus to the confirmation so it is reachable without Shift+Tab', async () => {
    mockConversionApi([CORRECTED_JOB], {
      onRetry: () =>
        Promise.reject(
          new ApiError('conflict', '競合が発生しました。', 409, [], {
            error: 'corrections_would_be_lost',
            correctedFigures: 1,
          }),
        ),
    });
    const user = userEvent.setup();
    await renderPage(['platform-admin']);

    await user.click(await screen.findByRole('button', { name: '再変換' }));

    // **焦点は非破壊側（取り消す）へ移る。** 既定の焦点は `Enter` の当たり先でもあるため、
    // 破壊側（補正を破棄して再変換）に置くと、反射的な連打で補正が消える
    // （ARIA APG の Alert/Confirm・ネイティブダイアログの慣習。AI レビュー 2 巡目の 🟡）。
    const cancel = await screen.findByRole('button', { name: '取り消す' });
    await waitFor(() => expect(cancel).toHaveFocus());
    expect(screen.getByRole('button', { name: '補正を破棄して再変換' })).not.toHaveFocus();
  });

  // 取り消したら何も起きない（確認が飾りでないことの側）。
  it('sends nothing when the discard confirmation is cancelled', async () => {
    mockConversionApi([CORRECTED_JOB], {
      onRetry: () =>
        Promise.reject(
          new ApiError('conflict', '競合が発生しました。', 409, [], {
            error: 'corrections_would_be_lost',
            correctedFigures: 2,
          }),
        ),
    });
    const user = userEvent.setup();
    await renderPage(['platform-admin']);

    await user.click(await screen.findByRole('button', { name: '再変換' }));
    // 確認が出ていることを確かめてから取り消す（出ていないのに「送らなかった」では意味が無い）。
    expect(await screen.findByRole('button', { name: '補正を破棄して再変換' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: '取り消す' }));

    // 確認が消え、破棄の要求は 1 度も飛んでいない。
    await waitFor(() =>
      expect(
        screen.queryByRole('button', { name: '補正を破棄して再変換' }),
      ).not.toBeInTheDocument(),
    );
    expect(
      mocks.apiRequest.mock.calls.filter((call) => String(call[0]).includes('discardCorrections')),
    ).toHaveLength(0);
  });

  // `not_retryable` の 409 は**確認ではなく拒否**である。両者を取り違えると
  // 「実行中のジョブに補正破棄を勧める」ことになる。
  it('treats a not_retryable 409 as a refusal, not as a discard confirmation', async () => {
    mockConversionApi([FAILED_JOB], {
      onRetry: () =>
        Promise.reject(
          new ApiError('conflict', '競合が発生しました。', 409, [], { error: 'not_retryable' }),
        ),
    });
    const user = userEvent.setup();
    await renderPage(['platform-admin']);

    await user.click(await screen.findByRole('button', { name: '再変換' }));

    expect(
      await screen.findByText('このジョブは再変換できません（実行中、または失敗以外の状態です）。'),
    ).toBeInTheDocument();
    // **確認ではない**——破棄ボタンが出ていないことで区別する。
    expect(screen.queryByRole('button', { name: '補正を破棄して再変換' })).not.toBeInTheDocument();
  });

  // IADR-0127 決定 7: 画面は**直近の操作の結果だけ**を出す。#503 の時点ではミューテーションが
  // retry の 1 本だけで、この穴は開きようがなかった。#651 で 2 本目（補正投稿）が入ったので、
  // 「別の操作が成功しても古い失敗バナーが残る」形になっていないことを固定する。
  it('drops the previous failure banner once another operation succeeds', async () => {
    // **別々のミューテーション**でなければこの穴は再現しない。TanStack Query は同じ
    // ミューテーションを再実行すれば自分の `isError` を戻すので、「retry が失敗 → correct が成功」
    // という**組み合わせ**でしか `beginOperation()` の要否は測れない
    // （実測: 同じミューテーションを 2 回叩く形では、reset を外しても緑のままだった）。
    mockConversionApi([FAILED_RETAINED_JOB], {
      onRetry: () => Promise.reject(new ApiError('server', 'サーバでエラーが発生しました。', 500)),
    });
    const user = userEvent.setup();
    await renderPage(['platform-admin']);

    // 1) 再変換を失敗させる。
    await user.click(await screen.findByRole('button', { name: '再変換' }));
    expect(await screen.findByRole('alert')).toBeInTheDocument();

    // 2) 別の操作（人手補正）を成功させる。
    await user.click(screen.getByRole('button', { name: '人手補正' }));
    const editor = await screen.findByLabelText('図コード: fig-1');
    await user.type(editor, 'graph TD;');
    await user.click(screen.getByRole('button', { name: '補正して再登録' }));

    // 成功だけが残り、**retry の古い失敗バナーは消えている**。
    expect(await screen.findByText('図を補正して再登録しました。')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  });

  // 図の一覧が取れないときに空パネルを見せない（「補正すべき図が無い」と読めてしまう）。
  it('shows an error instead of an empty panel when the figures cannot be loaded', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) => {
      if (path.includes('/figures'))
        throw new ApiError('server', 'サーバでエラーが発生しました。', 500);
      return jsonResponse([RETAINED_JOB]);
    });
    const user = userEvent.setup();
    await renderPage(['platform-admin']);

    await user.click(await screen.findByRole('button', { name: '人手補正' }));

    expect(await screen.findByText('図の一覧を取得できませんでした。')).toBeInTheDocument();
    expect(screen.queryByText('補正が必要な図はありません。')).not.toBeInTheDocument();
  });

  // コード化済みの図しか無いジョブでは、対象が無いことを明示する（黙って空にしない）。
  it('says there is nothing to correct when every figure is already coded', async () => {
    mockConversionApi([RETAINED_JOB], { figures: [CODED_FIGURE] });
    const user = userEvent.setup();
    await renderPage(['platform-admin']);

    await user.click(await screen.findByRole('button', { name: '人手補正' }));

    expect(await screen.findByText('補正が必要な図はありません。')).toBeInTheDocument();
  });

  // 画像が無い図（404）でも右ペインは中立に倒れる。**空白にしない**——読み込み中と区別が付かなくなる。
  // 404 は不在と秘匿を区別しない（IADR-0009）。
  it('degrades the image pane neutrally when the figure has no image (404)', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) => {
      if (path.includes('/image')) throw new ApiError('notFound', '見つかりませんでした。', 404);
      if (path.includes('/figures')) return jsonResponse([RETAINED_FIGURE]);
      return jsonResponse([RETAINED_JOB]);
    });
    const user = userEvent.setup();
    await renderPage(['platform-admin']);

    await user.click(await screen.findByRole('button', { name: '人手補正' }));

    expect(await screen.findByText('元の画像を表示できません。')).toBeInTheDocument();
  });

  // 補正済みの図は、既存のコードと記法を編集欄へ引き継ぎ「補正あり」を示す
  // （やり直しのたびに白紙から書き直させない）。
  it('prefills the editor from an already corrected figure', async () => {
    mockConversionApi([RETAINED_JOB], {
      figures: [
        { ...RETAINED_FIGURE, corrected: true, language: 'plantuml', code: '@startuml\n@enduml' },
      ],
    });
    const user = userEvent.setup();
    await renderPage(['platform-admin']);

    await user.click(await screen.findByRole('button', { name: '人手補正' }));

    expect(await screen.findByLabelText('図コード: fig-1')).toHaveValue('@startuml\n@enduml');
    expect(screen.getByLabelText('記法')).toHaveValue('plantuml');
    expect(screen.getByText('補正あり')).toBeInTheDocument();
  });

  it('degrades to the empty state when the list response has no body (204)', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) => {
      if (path.startsWith('/conversion/jobs')) return noContent();
      return jsonResponse([]);
    });
    await renderPage();

    expect(await screen.findByText('該当する変換ジョブはありません。')).toBeInTheDocument();
  });
});
