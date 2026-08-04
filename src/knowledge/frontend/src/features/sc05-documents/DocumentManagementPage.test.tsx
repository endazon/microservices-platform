import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { ApiError } from '@foundation/api/ApiError';

// SC-05, FR-06: 文書管理が一覧・作成・編集（楽観ロック）・公開・削除を BFF 経由で行うこと、
// 必須属性（機密区分）を含むペイロード、版競合（409）の通知・再読込、異常系を検証する。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { createSc05DocumentsRoute } from './index';
import { createSc03DocumentRoute } from '../sc03-document';

const ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const DOCS = [
  { id: ID, title: '経費規程 2025', status: 'draft', version: 3, attributes: { confidentiality: 'internal' }, tags: ['hr'], updatedAt: '2026-07-01T00:00:00Z' },
];

// ADR-0031 / IADR-0124: 実ルート定義（RequireRole 込み）の上で描画する。
// SC-03 のルートも同居させ、一覧からの遷移先 href が実定義どおりに解決されることを見る。
async function renderPage() {
  return renderUnitRoute(
    (shell) => [createSc05DocumentsRoute(shell), createSc03DocumentRoute(shell)],
    { initialEntry: '/admin/documents', roles: ['platform-admin'] },
  );
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('DocumentManagementPage (SC-05)', () => {
  it('lists documents linking to SC-03 detail', async () => {
    mocks.apiFetch.mockResolvedValue(DOCS);
    await renderPage();

    const link = await screen.findByRole('link', { name: '経費規程 2025' });
    expect(link).toHaveAttribute('href', `/docs/${ID}`);
    expect(screen.getByText('v3')).toBeInTheDocument();
  });

  it('creates a document with the required confidentiality attribute', async () => {
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // load
    mocks.apiFetch.mockResolvedValueOnce({ id: 'new' }); // create
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // reload
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('link', { name: '経費規程 2025' });

    const form = screen.getByRole('form', { name: '文書作成' });
    await user.type(within(form).getByLabelText('タイトル（必須）'), '新規文書');
    await user.selectOptions(within(form).getByLabelText('機密区分（必須）'), 'confidential');
    await user.type(within(form).getByLabelText('タグ（カンマ区切り）'), 'hr, legal');
    await user.click(within(form).getByRole('button', { name: '作成する' }));

    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith('/documents', {
        method: 'POST',
        json: { title: '新規文書', attributes: { confidentiality: 'confidential' }, tags: ['hr', 'legal'] },
      }),
    );
  });

  it('does not show the publish button for archived documents', async () => {
    // SC-05 仕様: アーカイブ済みは再公開ボタンを出さない（状態遷移の意図を守る）。
    const archived = [{ ...DOCS[0], status: 'archived' }];
    mocks.apiFetch.mockResolvedValue(archived);
    await renderPage();
    await screen.findByRole('link', { name: '経費規程 2025' });

    expect(screen.queryByRole('button', { name: '公開' })).not.toBeInTheDocument();
  });

  it('shows the publish button for normalized (pipeline-produced) documents', async () => {
    // SC-05 仕様: 公開は draft だけでなく normalized（変換パイプライン由来）でも可能。
    const normalized = [{ ...DOCS[0], status: 'normalized' }];
    mocks.apiFetch.mockResolvedValue(normalized);
    await renderPage();
    await screen.findByRole('link', { name: '経費規程 2025' });

    expect(screen.getByRole('button', { name: '公開' })).toBeInTheDocument();
  });

  it('publishes a draft document', async () => {
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // load
    mocks.apiFetch.mockResolvedValueOnce(undefined); // publish
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // reload
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('link', { name: '経費規程 2025' });

    await user.click(screen.getByRole('button', { name: '公開' }));

    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith(`/documents/${ID}/publish`, { method: 'POST' }),
    );
  });

  it('edits a document with optimistic concurrency (expectedVersion)', async () => {
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // load
    mocks.apiFetch.mockResolvedValueOnce({ id: ID }); // put
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // reload
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('link', { name: '経費規程 2025' });

    await user.click(screen.getByRole('button', { name: '編集' }));
    const form = screen.getByRole('form', { name: '文書編集' });
    await user.clear(within(form).getByLabelText('タイトル（必須）'));
    await user.type(within(form).getByLabelText('タイトル（必須）'), '経費規程 2025 改訂');
    await user.click(within(form).getByRole('button', { name: '保存する' }));

    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith(
        `/documents/${ID}`,
        expect.objectContaining({ method: 'PUT', json: expect.objectContaining({ expectedVersion: 3, title: '経費規程 2025 改訂' }) }),
      ),
    );
  });

  it('shows a conflict notice and reloads on 409 version conflict', async () => {
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // load
    mocks.apiFetch.mockRejectedValueOnce(new ApiError('unknown', '要求が失敗しました（409）。', 409)); // put -> conflict
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // reload
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('link', { name: '経費規程 2025' });

    await user.click(screen.getByRole('button', { name: '編集' }));
    await user.click(within(screen.getByRole('form', { name: '文書編集' })).getByRole('button', { name: '保存する' }));

    expect(await screen.findByText(/競合しました/)).toBeInTheDocument();
  });

  it('shows validation detail messages from ApiError.details on create (400)', async () => {
    // #177: 400 検証エラーの詳細（Problem 本文由来）を SC-09 と統一して画面表示する。
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // load
    mocks.apiFetch.mockRejectedValueOnce(
      new ApiError('validation', '入力内容に誤りがあります。', 400, ['タイトルは必須です。']),
    ); // create -> 400 with details
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('link', { name: '経費規程 2025' });

    const form = screen.getByRole('form', { name: '文書作成' });
    // 非空タイトルでクライアント必須ガードを通し、サーバ側検証（400 詳細）の表示を確認する。
    await user.type(within(form).getByLabelText('タイトル（必須）'), '仮タイトル');
    await user.click(within(form).getByRole('button', { name: '作成する' }));

    expect(await within(form).findByText('タイトルは必須です。')).toBeInTheDocument();
  });

  it('shows conflict detail message from ApiError.details on 409', async () => {
    // #177: 409 に detail 本文があれば平易な既定文言ではなくその詳細を表示する。
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // load
    mocks.apiFetch.mockRejectedValueOnce(
      new ApiError('conflict', '競合が発生しました。', 409, ['公開済みの文書はアーカイブできません。']),
    ); // archive -> 409 with details
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // reload
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('link', { name: '経費規程 2025' });

    await user.click(screen.getByRole('button', { name: 'アーカイブ' }));

    expect(await screen.findByText('公開済みの文書はアーカイブできません。')).toBeInTheDocument();
  });

  it('shows an alert when the list fails to load', async () => {
    mocks.apiFetch.mockRejectedValue(new Error('boom'));
    await renderPage();

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('取得に失敗'));
  });
});
