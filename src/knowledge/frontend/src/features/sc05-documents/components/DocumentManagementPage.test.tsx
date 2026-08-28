import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse, noContent } from '@foundation/testing/bffResponse';
import type { BffResponseStub } from '@foundation/testing/bffResponse';

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

import { createSc05DocumentsRoute } from '../routes/sc05DocumentsRoute';

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

// FR-09, SC-05（#449）: タグ辞書（`GET /bff/tags` → `apiRequest('/tags')`）の応答。
// 使用件数は SC-09 の関心であり、本画面は名前しか使わない。
const TAG_DICTIONARY = {
  tags: [
    { id: '11111111-1111-1111-1111-111111111111', name: '経理', usageCount: 3 },
    { id: '22222222-2222-2222-2222-222222222222', name: '規程', usageCount: 5 },
    { id: '33333333-3333-3333-3333-333333333333', name: '人事', usageCount: 1 },
  ],
};

/**
 * `/tags` には辞書を、その他のパスには `rest` の応答を返す。
 *
 * **辞書を返さないと選択肢が空になり、タグを触るテストが「候補なし」で止まる。**
 * 辞書に触れないテストは従来どおり `mockResolvedValue` のままでよい
 * （辞書が空でも他の検証には影響しない）。
 */
function withTagDictionary(rest: (path: string, init?: RequestInit) => BffResponseStub) {
  mocks.apiRequest.mockImplementation(async (path: string, init?: RequestInit) =>
    path.startsWith('/tags') ? jsonResponse(TAG_DICTIONARY) : rest(path, init),
  );
}

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
    // 機密区分は生値のまま出す。**［2026-08-10 追記 / #553］理由は「表示名が 2 値しか無いから」
    // ではなくなった** —— 4 値の表示名は裁定（2026-08-05 Q7・Q8・派生 Q30）で確定しており、
    // **写像の実装先が #541 である**ため、それまでの現状として生値を出している。
    expect(table.getByText('internal')).toBeInTheDocument();
    expect(table.getByText('v3')).toBeInTheDocument();
    expect(mocks.apiRequest).toHaveBeenCalledWith(
      '/documents',
      expect.objectContaining({ method: 'GET' }),
    );
  });

  // UC-03 基本フロー 1: 管理者が文書を登録し、属性・タグを設定する。
  it('creates a document with the required confidentiality attribute and tags', async () => {
    withTagDictionary(() => jsonResponse([]));
    const user = userEvent.setup();
    await renderPage();

    await user.type(screen.getByLabelText(/タイトル/), '新しい規程');
    await user.selectOptions(screen.getByLabelText(/機密区分（ABAC属性）/), 'confidential');
    // #449: タグは辞書からの選択である（自由入力ではない）。
    await user.selectOptions(await screen.findByLabelText('タグ'), '経理');
    await user.click(screen.getByRole('button', { name: '追加' }));
    await user.click(screen.getByRole('button', { name: '保存' }));

    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        '/documents',
        expect.objectContaining({ method: 'POST' }),
      ),
    );
    expect(
      sentBody(
        mocks.apiRequest.mock.calls.find(([, init]) => (init as RequestInit).method === 'POST')!,
      ),
    ).toEqual({
      title: '新しい規程',
      attributes: { confidentiality: 'confidential' },
      tags: ['経理'],
    });
    expect(await screen.findByText('文書を登録しました。')).toBeInTheDocument();
  });

  // ---- FR-06, FR-09, UC-03, SC-05（#449）: タグは既定タグ辞書に整合する ----
  //
  // 計画 05_screens §SC-05 の入力表（確定・2026-08-05。質問票 第12回 Q18）:
  // タグは「**既定タグ辞書に整合**（**辞書は管理系ロールが引ける照会口から取得する**）」。
  // 従前は自由テキスト入力で、辞書に無い値をいくらでも付けられた。

  it('offers tags from the dictionary instead of free text', async () => {
    withTagDictionary(() => jsonResponse([]));
    await renderPage();

    const tagField = await screen.findByLabelText('タグ');
    // 🔴 **入力欄ではなく選択欄である**（`combobox` = <select>）。
    // 自由入力へ戻す退行はここで落ちる。
    expect(tagField.tagName).toBe('SELECT');
    expect(
      within(tagField)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['タグを選択', '経理', '規程', '人事']);

    expect(mocks.apiRequest).toHaveBeenCalledWith(
      '/tags',
      expect.objectContaining({ method: 'GET' }),
    );
  });

  // 既に付いているタグは選択肢から消える（同じタグを 2 度付けられない）。
  it('removes already-attached tags from the choices', async () => {
    withTagDictionary(() => jsonResponse([DRAFT_DOC]));
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '編集' }));
    const tagField = await screen.findByLabelText('タグ');

    // DRAFT_DOC は「経理」「規程」を持つので、残る候補は「人事」だけ。
    expect(
      within(tagField)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['タグを選択', '人事']);
  });

  // 🔴 辞書が取れないとき、**自由入力へフォールバックしない**。
  // フォールバックすると裁定（辞書に整合）の意味が消えるため、追加を止めるほうを選ぶ。
  it('disables adding when the dictionary is unavailable (no free-text fallback)', async () => {
    // `/tags` を含め全パスが 500。辞書は取得できない。
    mocks.apiRequest.mockRejectedValue(
      new ApiError('server', 'サーバでエラーが発生しました。', 500),
    );
    await renderPage();

    const tagField = await screen.findByLabelText('タグ');
    expect(tagField).toBeDisabled();
    expect(tagField.tagName).toBe('SELECT');
    expect(screen.getByRole('button', { name: '追加' })).toBeDisabled();
    expect(screen.getByText('辞書に候補がありません')).toBeInTheDocument();
  });

  // 辞書に無い既存タグ（辞書の改定前に付いたもの）は**表示され、削除できる**。
  // 画面が既存のタグを黙って落とすと、保存時に消えて破壊的になる。
  it('keeps existing tags that are no longer in the dictionary', async () => {
    const legacy = { ...DRAFT_DOC, tags: ['廃止された分類'] };
    withTagDictionary(() => jsonResponse([legacy]));
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '編集' }));

    const form = within(screen.getByRole('form', { name: '文書編集' }));
    expect(form.getByText('廃止された分類')).toBeInTheDocument();
    expect(form.getByRole('button', { name: 'タグ 廃止された分類 を削除' })).toBeInTheDocument();
    // 選択肢には出ない（辞書に無いので新たには付けられない）。
    expect(
      within(await screen.findByLabelText('タグ')).queryByRole('option', {
        name: '廃止された分類',
      }),
    ).not.toBeInTheDocument();
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
    // **書き込みが 1 度も起きていないこと**を見る。
    // **［#449］呼び出し回数では数えない** —— タグ辞書（`/tags`）の取得が増えて 2 回になった。
    // 「読み取りが何本走るか」はこのテストの関心ではなく、**増えるたびに数を直す形は脆い**。
    expect(
      mocks.apiRequest.mock.calls.filter(([, init]) => (init as RequestInit).method !== 'GET'),
    ).toHaveLength(0);

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
    expect(
      sentBody(
        mocks.apiRequest.mock.calls.find(([, init]) => (init as RequestInit).method === 'PUT')!,
      ),
    ).toEqual({
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
    mocks.apiRequest.mockRejectedValue(
      new ApiError('server', 'サーバでエラーが発生しました。', 500),
    );
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

  // ── #629: 破壊的操作は管理者限定（計画 §SC-05・裁定 Q19）───────────────────
  //
  // **実効境界はサーバ側**（`/bff/documents` と後段の `AdminOnly`）であり、ここは表示制御である
  // （[[IADR-0039]] 決定 2）。それでも固定するのは、**押しても 403 になるボタンを置かない**
  // という規則（[[IADR-0127]] 決定 1・#502）が守られていることを見るためである。

  // 受け入れ基準 5: 運用者へ書き込みの導線を 1 つも出さない（5 つとも書き込みである）。
  it('offers no write action to an operator', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([DRAFT_DOC]));
    await renderPage(['platform-operator']);

    // まず「見えるはずの条件」を確かめる——一覧が出ていないと、
    // 下の queryBy が**単に画面が描かれていないだけ**で緑になる。
    expect(await screen.findByRole('link', { name: '経費精算規程' })).toBeInTheDocument();

    for (const name of ['＋ 新規登録', '編集', '公開', 'アーカイブ', '削除']) {
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument();
    }
    expect(screen.queryByRole('columnheader', { name: '操作' })).not.toBeInTheDocument();
    // 無言で消さない（「何もできない壊れた画面」に見せない）。
    expect(
      screen.getByText('文書の登録・編集・公開・アーカイブ・削除は管理者のみ実行できます'),
    ).toBeInTheDocument();
  });

  // 受け入れ基準 4（対）: **閲覧は狭めない**。運用者にも一覧と SC-03 への導線は残る。
  it('still lets an operator read the list and reach SC-03', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([DRAFT_DOC]));
    await renderPage(['platform-operator']);

    expect(await screen.findByRole('link', { name: '経費精算規程' })).toHaveAttribute(
      'href',
      `/docs/${DOC_ID}`,
    );
    expect(screen.getByRole('columnheader', { name: '機密区分' })).toBeInTheDocument();
  });

  // 受け入れ基準 6（対）: 管理者には従来どおり出す（**狭めすぎていない**）。
  // これが無いと、全員から消しても上の 2 件は緑のまま通る。
  it('still offers every write action to an administrator', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([DRAFT_DOC]));
    await renderPage(['platform-admin']);

    for (const name of ['＋ 新規登録', '編集', '公開', 'アーカイブ', '削除']) {
      expect(await screen.findByRole('button', { name })).toBeInTheDocument();
    }
    expect(screen.getByRole('columnheader', { name: '操作' })).toBeInTheDocument();
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

  // SC-05, ADR-0058 決定 3, [[IADR-0278]]: 文書スコープ（doc_scope）は編集不可である。
  //
  // 🔴 **画面で編集できてしまい保存時に初めて弾かれる形にしない**（サーバ側の拒否と併せた二層）。
  // 本画面は入力欄を持たず、**既存の値をそのまま送り返す**ことで不変性を満たす。
  // 入力欄を足すと（あるいは属性を落とすと）ここが赤くなる。
  it('offers no editable control for the document scope and echoes the stored value back', async () => {
    const SCOPED_DOC = {
      ...DRAFT_DOC,
      attributes: { ...DRAFT_DOC.attributes, doc_scope: 'organization' },
    };
    mocks.apiRequest.mockResolvedValue(jsonResponse([SCOPED_DOC]));
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '編集' }));

    // 入力欄が無い（label・role のどちらからも到達できない）。
    expect(screen.queryByLabelText(/文書スコープ|doc_scope/i)).toBeNull();
    expect(screen.queryByRole('combobox', { name: /文書スコープ|doc_scope/i })).toBeNull();

    await user.click(screen.getByRole('button', { name: '保存' }));

    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        `/documents/${DOC_ID}`,
        expect.objectContaining({ method: 'PUT' }),
      ),
    );
    // 🔴 陽性対照: 既存の値を**落とさず同じ値で**送り返す。
    // 落とすとサーバ側の不変性検証が 400 を返す（全置換なので削除も変更である）。
    expect(
      sentBody(
        mocks.apiRequest.mock.calls.find(([, init]) => (init as RequestInit).method === 'PUT')!,
      ),
    ).toMatchObject({
      attributes: { confidentiality: 'internal', department: 'finance', doc_scope: 'organization' },
    });
  });
});
