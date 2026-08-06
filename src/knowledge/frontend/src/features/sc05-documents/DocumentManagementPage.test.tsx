import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse, noContent } from '@foundation/testing/bffResponse';

// SC-05, UC-03, FR-06/FR-09: 文書管理画面の再実装（#503）＋ 生成フックへの載せ替え（#519）。
//
// IADR-0135 決定 4（#519）: 生成コードは mutator（`bffFetch`）→ **`apiRequest`** を通るため、
// モックは `apiRequest` に当てる（`apiFetch` を差し替えても効かない）。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

/** 生成コードが送った JSON 本文を読む。 */
function sentBody(call: unknown[]): unknown {
  return JSON.parse(String((call[1] as RequestInit).body));
}

import { createSc05DocumentsRoute } from './index';

const DOC_ID = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
const DRAFT_DOC = {
  id: DOC_ID,
  title: '経費精算規程',
  status: 'draft',
  markdownUri: null,
  version: 3,
  attributes: { confidentiality: 'internal', department: 'finance' },
  tags: ['経理', '規程'],
  createdAt: '2026-07-01T00:00:00Z',
  updatedAt: '2026-08-01T00:00:00Z',
};
const ARCHIVED_DOC = {
  ...DRAFT_DOC,
  id: 'dddddddd-dddd-dddd-dddd-dddddddddddd',
  title: '製品ロードマップ 2027',
  status: 'archived',
  attributes: { confidentiality: 'confidential' },
  tags: [],
};

async function renderPage(roles: readonly string[] = ['platform-admin']) {
  return renderUnitRoute((shell) => [createSc05DocumentsRoute(shell)], {
    initialEntry: '/admin/documents',
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

describe('DocumentManagementPage (SC-05)', () => {
  // 05_screens §SC-05: 一覧はタイトル・機密区分・版（版列＝現行版の表示）。詳細・版履歴は SC-03。
  it('lists documents with confidentiality and the current version, linking to SC-03', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([DRAFT_DOC]));
    await renderPage();

    const link = await screen.findByRole('link', { name: '経費精算規程' });
    expect(link).toHaveAttribute('href', `/docs/${DOC_ID}`);
    const table = within(screen.getByRole('table'));
    // 機密区分は生値のまま出す（表示名が計画にある値は 4 値中 2 値だけ。planning#197 で裁定待ち）。
    expect(table.getByText('internal')).toBeInTheDocument();
    expect(table.getByText('v3')).toBeInTheDocument();
    expect(mocks.apiRequest).toHaveBeenCalledWith('/documents', expect.objectContaining({ method: 'GET' }));
  });

  // UC-03 基本フロー 1: 管理者が文書を登録し、属性・タグを設定する。
  it('creates a document with the required confidentiality attribute and tags', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
    const user = userEvent.setup();
    await renderPage();

    await user.type(screen.getByLabelText(/タイトル/), '新しい規程');
    await user.selectOptions(screen.getByLabelText(/機密区分（ABAC属性）/), 'confidential');
    await user.type(screen.getByLabelText('タグ'), '経理');
    await user.click(screen.getByRole('button', { name: '追加' }));
    await user.click(screen.getByRole('button', { name: '保存' }));

    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        '/documents',
        expect.objectContaining({ method: 'POST' }),
      ),
    );
    expect(sentBody(mocks.apiRequest.mock.calls.find(([, init]) => (init as RequestInit).method === 'POST')!)).toEqual({
      title: '新しい規程',
      attributes: { confidentiality: 'confidential' },
      tags: ['経理'],
    });
    expect(await screen.findByText('文書を登録しました。')).toBeInTheDocument();
  });

  // UC-03 例外フロー: 必須属性が未設定の場合は保存を拒否する（タイトルが空では保存できない）。
  it('refuses to save until the required title is filled (UC-03 exception flow)', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
    const user = userEvent.setup();
    await renderPage();

    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled();
    // 空白だけの入力も必須未設定として扱う。
    await user.type(screen.getByLabelText(/タイトル/), '   ');
    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled();
    expect(mocks.apiRequest).toHaveBeenCalledTimes(1); // 一覧の取得のみ

    expect(
      screen.getByText('必須属性が未設定の場合は保存を拒否します（UC-03 例外フロー）。'),
    ).toBeInTheDocument();
  });

  // FR-06 のバージョン管理: 更新は現在版を expectedVersion として送る（楽観ロック）。
  it('updates a document with the optimistic-lock version and the change note', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([DRAFT_DOC]));
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '編集' }));
    await user.type(screen.getByLabelText('変更メモ'), '締め日を修正');
    await user.click(screen.getByRole('button', { name: '保存' }));

    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        `/documents/${DOC_ID}`,
        expect.objectContaining({ method: 'PUT' }),
      ),
    );
    expect(sentBody(mocks.apiRequest.mock.calls.find(([, init]) => (init as RequestInit).method === 'PUT')!)).toEqual({
      title: '経費精算規程',
      // 既存の属性（部門）を落とさずに機密区分を保つ。
      attributes: { confidentiality: 'internal', department: 'finance' },
      tags: ['経理', '規程'],
      expectedVersion: 3,
      changeNote: '締め日を修正',
    });
    expect(await screen.findByText('文書を更新しました。')).toBeInTheDocument();
  });

  // IADR-0127 決定 5: 更新系の成功後は invalidateQueries だけを行う（手書きの再取得を持たない）。
  // これが外れると、保存したのに一覧が古いまま残る（旧実装が load() を手で呼び直していた箇所）。
  it('refetches the list after a successful save', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([DRAFT_DOC]));
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '編集' }));
    expect(mocks.apiRequest.mock.calls.filter(([path]) => path === '/documents')).toHaveLength(1);

    await user.click(screen.getByRole('button', { name: '保存' }));

    await waitFor(() =>
      expect(mocks.apiRequest.mock.calls.filter(([path]) => path === '/documents')).toHaveLength(2),
    );
  });

  // 版競合（409）は「他の更新と競合した」ことが読める形で伝える（消えるトーストにしない）。
  it('explains a 409 version conflict', async () => {
    mocks.apiRequest.mockImplementation((_path: string, init?: RequestInit) =>
      init?.method === 'PUT'
        ? Promise.reject(new ApiError('conflict', '競合が発生しました。', 409))
        : Promise.resolve(jsonResponse([DRAFT_DOC])),
    );
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '編集' }));
    await user.click(screen.getByRole('button', { name: '保存' }));

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('版が変わっています'));
    // INDEX 決定 21: 深刻度は色（琥珀の警告）だけでなくラベルの文言でも伝える。
    expect(screen.getByRole('alert')).toHaveTextContent('注意');
    expect(screen.getByRole('alert')).not.toHaveTextContent('エラー');
  });

  // 公開は未公開状態（draft / normalized）のみ。アーカイブ済みの誤再公開を防ぐ（サーバも 409）。
  it('offers publish only for unpublished documents', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([DRAFT_DOC, ARCHIVED_DOC]));
    const user = userEvent.setup();
    await renderPage();

    // まず両方の行が描かれていることを確かめる。
    expect(await screen.findByRole('link', { name: '経費精算規程' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '製品ロードマップ 2027' })).toBeInTheDocument();
    // 公開できるのは draft の 1 行だけ、アーカイブできるのも archived 以外の 1 行だけ。
    expect(screen.getAllByRole('button', { name: '公開' })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'アーカイブ' })).toHaveLength(1);

    await user.click(screen.getByRole('button', { name: '公開' }));
    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        `/documents/${DOC_ID}/publish`,
        expect.objectContaining({ method: 'POST' }),
      ),
    );
  });

  it('deletes a document', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([DRAFT_DOC]));
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '削除' }));

    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        `/documents/${DOC_ID}`,
        expect.objectContaining({ method: 'DELETE' }),
      ),
    );
    expect(await screen.findByText('文書を削除しました。')).toBeInTheDocument();
  });

  // IADR-0041 / IADR-0009: スコープ外・不在はいずれも 404 で返る。画面は中立に扱う。
  it('reports a 404 neutrally (out of scope and missing are not distinguished)', async () => {
    mocks.apiRequest.mockImplementation((_path: string, init?: RequestInit) =>
      init?.method === 'DELETE'
        ? Promise.reject(new ApiError('notFound', '見つかりませんでした。', 404))
        : Promise.resolve(jsonResponse([DRAFT_DOC])),
    );
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '削除' }));

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('見つかりませんでした'),
    );
    // 「権限がありません」を示唆する文言を出さない。
    expect(screen.queryByText(/権限がありません/)).not.toBeInTheDocument();
  });

  // IADR-0127 決定 7: 画面は直近の操作の結果だけを出す。TanStack Query は「別の」ミューテーションの
  // 成功では他方の isError を戻さないため、これが外れると成功バナーと古い失敗バナーが同時に出る。
  it('shows only the latest operation result (a stale failure banner does not survive)', async () => {
    mocks.apiRequest.mockImplementation((_path: string, init?: RequestInit) =>
      init?.method === 'DELETE'
        ? Promise.reject(
            new ApiError('conflict', '競合が発生しました。', 409, [
              'この文書は参照中のため削除できません。',
            ]),
          )
        : Promise.resolve(jsonResponse([DRAFT_DOC])),
    );
    const user = userEvent.setup();
    await renderPage();

    // 削除が 409 で失敗する。
    await user.click(await screen.findByRole('button', { name: '削除' }));
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('参照中のため削除'));

    // 続けて別の操作（編集 → 保存）が成功する。
    await user.click(screen.getByRole('button', { name: '編集' }));
    await user.click(screen.getByRole('button', { name: '保存' }));

    expect(await screen.findByText('文書を更新しました。')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows an alert when the list cannot be loaded', async () => {
    mocks.apiRequest.mockRejectedValue(new ApiError('server', 'サーバでエラーが発生しました。', 500));
    await renderPage();

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('サーバでエラーが発生しました'),
    );
  });

  it('shows a neutral message when there is no document', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
    await renderPage();

    expect(await screen.findByText('文書はありません。')).toBeInTheDocument();
  });

  // 存在秘匿（IADR-0009 / IADR-0035）: ロールを持たない利用者へ画面の存在を示さない。
  it('hides the screen from a user without any role', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([DRAFT_DOC]));
    await renderPage([]);

    expect(screen.queryByRole('heading', { name: '文書一覧' })).not.toBeInTheDocument();
    expect(mocks.apiRequest).not.toHaveBeenCalled();
  });

  // **実装しない要素**（画面仕様書 §hi-fi モックアップとの対応 #6）。
  // まず「見えるはずの条件」——一覧が描画され、機密区分と版の列が出ている状態——を確かめてから、
  // 契約に無い「変換」列が無いことを見る。
  it('does not render the conversion column (no contract links a document to its job)', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([DRAFT_DOC]));
    await renderPage();

    expect(await screen.findByRole('columnheader', { name: '機密区分' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: '版' })).toBeInTheDocument();
    expect(screen.queryByRole('columnheader', { name: '変換' })).not.toBeInTheDocument();
  });

  it('renders in English when the en locale is active', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([DRAFT_DOC]));
    activate('en');
    await renderPage();

    expect(await screen.findByRole('heading', { name: 'Documents' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument();
  });

  // SC-05, IADR-0135 決定 7［2026-08-06 追記］: 一覧が**本文なし**（204）で返っても画面は落ちない。
  //
  // 載せ替え前は `apiFetch` が空ボディで `undefined` を返すため `items = data ?? []` が実効ガード
  // だった。生成物の `bffFetch` は空ボディで `{}` を返すので `??` は発火せず、`items.length === 0`
  // も `{}.length === undefined` で救えず、`items.map` が `TypeError` を投げていた。
  // `okArray` が「配列でなければ空配列」まで詰めることで、載せ替え前と同じ縮退に戻る。
  it('degrades to the empty state when the list response has no body (204)', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) => {
      if (path.startsWith('/documents')) return noContent();
      return jsonResponse([]);
    });
    await renderPage();

    expect(await screen.findByText('文書はありません。')).toBeInTheDocument();
  });
});
