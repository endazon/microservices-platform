import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse, noContent } from '@foundation/testing/bffResponse';

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

import { createSc07ConversionsRoute } from './index';

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

/** 既定は管理者（再変換ボタンが出る側）。運用者・無権限は各テストで明示する。 */
async function renderPage(roles: readonly string[] = ['platform-admin']) {
  return renderUnitRoute((shell) => [createSc07ConversionsRoute(shell)], {
    initialEntry: '/admin/conversions',
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
    expect(mocks.apiRequest).toHaveBeenCalledWith('/conversion/jobs', expect.objectContaining({ method: 'GET' }));
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
    expect(mocks.apiRequest).toHaveBeenCalledWith('/conversion/jobs', expect.objectContaining({ method: 'GET' }));
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
    mocks.apiRequest.mockRejectedValue(new ApiError('server', 'サーバでエラーが発生しました。', 500));
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

    expect(screen.queryByRole('heading', { name: '変換ジョブ（pandoc＋LLM）' })).not.toBeInTheDocument();
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

  // **実装しない要素**（画面仕様書 §hi-fi モックアップとの対応 #10・#12）。
  // まず「見えるはずの条件」——失敗ジョブが在り、管理者として再変換ボタンが出ている状態——で
  // 描画されていることを確かめてから、人手補正の 2 ペインが無いことを見る。
  it('does not render the manual-correction pane (no contract to save into)', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([FAILED_JOB]));
    await renderPage(['platform-admin']);

    expect(await screen.findByRole('button', { name: '再変換' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '人手補正' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '補正して再登録' })).not.toBeInTheDocument();
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
  it('degrades to the empty state when the list response has no body (204)', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) => {
      if (path.startsWith('/conversion/jobs')) return noContent();
      return jsonResponse([]);
    });
    await renderPage();

    expect(await screen.findByText('該当する変換ジョブはありません。')).toBeInTheDocument();
  });

});
