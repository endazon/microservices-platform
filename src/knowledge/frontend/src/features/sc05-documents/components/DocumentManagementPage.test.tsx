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
 * FR-06, IADR-0537（#1923）: 一覧（`GET /bff/documents`）の応答は 1 ページ（`items` / `total` / `offset` / `limit`）である。
 * 既定は全件が 1 ページに収まる形。総件数・位置を変える試験は引数で与える。
 */
function listPage(
  items: unknown[],
  page: {
    total?: number;
    offset?: number;
    limit?: number;
    unfilteredTotal?: number;
    statusCounts?: Record<string, number>;
  } = {},
) {
  const total = page.total ?? items.length;
  return jsonResponse({
    items,
    total,
    offset: page.offset ?? 0,
    limit: page.limit ?? 50,
    unfilteredTotal: page.unfilteredTotal ?? total,
    statusCounts: page.statusCounts ?? {},
  });
}

/** 一覧の取得（GET）の要求だけを数える（`/documents?offset=…&limit=…`）。 */
const listCalls = () =>
  mocks.apiRequest.mock.calls.filter(
    ([path, init]) =>
      String(path).startsWith('/documents?') && (init as RequestInit | undefined)?.method === 'GET',
  );

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
    mocks.apiRequest.mockResolvedValue(listPage([DRAFT_DOC]));
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
      '/documents?offset=0&limit=50',
      expect.objectContaining({ method: 'GET' }),
    );
  });

  // UC-03 基本フロー 1: 管理者が文書を登録し、属性・タグを設定する。
  it('creates a document with the required confidentiality attribute and tags', async () => {
    withTagDictionary(() => listPage([]));
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
    withTagDictionary(() => listPage([]));
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
    withTagDictionary(() => listPage([DRAFT_DOC]));
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
    withTagDictionary(() => listPage([legacy]));
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
    mocks.apiRequest.mockResolvedValue(listPage([]));
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
    mocks.apiRequest.mockResolvedValue(listPage([DRAFT_DOC]));
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
    mocks.apiRequest.mockResolvedValue(listPage([DRAFT_DOC]));
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '編集' }));
    expect(listCalls()).toHaveLength(1);

    await user.click(screen.getByRole('button', { name: '保存' }));

    await waitFor(() => expect(listCalls()).toHaveLength(2));
  });

  // 版競合（409）は「他の更新と競合した」ことが読める形で伝える（消えるトーストにしない）。
  it('explains a 409 version conflict', async () => {
    mocks.apiRequest.mockImplementation((_path: string, init?: RequestInit) =>
      init?.method === 'PUT'
        ? Promise.reject(new ApiError('conflict', '競合が発生しました。', 409))
        : Promise.resolve(listPage([DRAFT_DOC])),
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
    mocks.apiRequest.mockResolvedValue(listPage([DRAFT_DOC, ARCHIVED_DOC]));
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
    mocks.apiRequest.mockResolvedValue(listPage([DRAFT_DOC]));
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
        : Promise.resolve(listPage([DRAFT_DOC])),
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
        : Promise.resolve(listPage([DRAFT_DOC])),
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

  // FR-06, NFR-16, IADR-0537（#1897・#1923）: BFF が後段から一覧を引き切れないときは **502** を返す。
  // 画面は**エラーとして**出し、0 件の表示（「文書はまだありません。」）へ寄せない —— 本当の 0 件と区別できなくなる（#1897 の本質）。
  it('shows the list failure (502) as an error, not as an empty list', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) => {
      if (path.startsWith('/documents?'))
        throw new ApiError('server', 'サーバでエラーが発生しました。', 502);
      return listPage([]);
    });
    await renderPage();

    expect(await screen.findByText('文書を取得できませんでした。')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '再試行' })).toBeInTheDocument();
    expect(screen.queryByText('文書はまだありません。')).not.toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'ページ送り' })).not.toBeInTheDocument();
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

  // **［2026-09-12］文言が変わった**（`QueryState` ＋ `EmptyState` への統一）。
  // 空は失敗ではないので再試行を促さず、**次の一手**（新規登録）を示す。実装が正・テストを追随。
  it('shows a neutral message with a next step when there is no document', async () => {
    mocks.apiRequest.mockResolvedValue(listPage([]));
    await renderPage();

    expect(await screen.findByText('文書はまだありません。')).toBeInTheDocument();
    expect(
      screen.getByText('「＋ 新規登録」から最初の文書を登録してください。'),
    ).toBeInTheDocument();
    // 0 件は失敗ではない（再試行ボタンも role="alert" も出さない）。
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  // SC-05（hi-fi モック / 2026-09-12）: **公開ライフサイクルの列。** 行に「公開」「アーカイブ」の
  // ボタンが並ぶだけでは、いまどの状態なのかが読めない。色だけで意味を持たせない
  // （`StatusBadge` がアイコン ＋ テキストを強制する）。
  it('shows the lifecycle state of each document as a badge', async () => {
    mocks.apiRequest.mockResolvedValue(listPage([DRAFT_DOC, ARCHIVED_DOC]));
    await renderPage();

    expect(await screen.findByRole('columnheader', { name: '状態' })).toBeInTheDocument();
    const draftRow = screen.getByRole('link', { name: DRAFT_DOC.title }).closest('tr')!;
    expect(within(draftRow).getByText('下書き')).toBeInTheDocument();
    const archivedRow = screen.getByRole('link', { name: ARCHIVED_DOC.title }).closest('tr')!;
    expect(within(archivedRow).getByText('アーカイブ済み')).toBeInTheDocument();
  });

  // 存在秘匿（IADR-0009 / IADR-0035）: ロールを持たない利用者へ画面の存在を示さない。
  it('hides the screen from a user without any role', async () => {
    mocks.apiRequest.mockResolvedValue(listPage([DRAFT_DOC]));
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
    mocks.apiRequest.mockResolvedValue(listPage([DRAFT_DOC]));
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
    mocks.apiRequest.mockResolvedValue(listPage([DRAFT_DOC]));
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
    mocks.apiRequest.mockResolvedValue(listPage([DRAFT_DOC]));
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
    mocks.apiRequest.mockResolvedValue(listPage([DRAFT_DOC]));
    await renderPage();

    expect(await screen.findByRole('columnheader', { name: '機密区分' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: '版' })).toBeInTheDocument();
    expect(screen.queryByRole('columnheader', { name: '変換' })).not.toBeInTheDocument();
  });

  it('renders in English when the en locale is active', async () => {
    mocks.apiRequest.mockResolvedValue(listPage([DRAFT_DOC]));
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
  // ［2026-10-14 / #1923］一覧はページになった。`okPage` が「`items` が配列でなければ空・総件数 0」へ同じく詰める。
  it('degrades to the empty state when the list response has no body (204)', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) => {
      if (path.startsWith('/documents')) return noContent();
      return listPage([]);
    });
    await renderPage();

    expect(await screen.findByText('文書はまだありません。')).toBeInTheDocument();
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
    mocks.apiRequest.mockResolvedValue(listPage([SCOPED_DOC]));
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

  // ---- FR-06, SC-05, IADR-0537（#1923 段 1）: ページネーション ----
  //
  // 総件数は BFF が ABAC で絞った後の数（画面はそれを描くだけ）。送り方はオフセット（`offset` / `limit`）。

  // 総件数と表示範囲を出し、「次へ」で次の窓を要求する。先頭のページでは「前へ」を押せない。
  it('shows the total and the range, and requests the next page', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) =>
      path.includes('offset=50')
        ? listPage([ARCHIVED_DOC], { total: 120, offset: 50 })
        : listPage([DRAFT_DOC], { total: 120 }),
    );
    const user = userEvent.setup();
    await renderPage();

    expect(await screen.findByText('全 120 件中 1–50 件')).toBeInTheDocument();
    const nav = within(screen.getByRole('navigation', { name: 'ページ送り' }));
    expect(nav.getByRole('button', { name: '前へ' })).toBeDisabled();
    expect(nav.getByRole('button', { name: '1 ページ目' })).toHaveAttribute('aria-current', 'page');
    expect(nav.getByRole('button', { name: '3 ページ目' })).toBeInTheDocument();

    await user.click(nav.getByRole('button', { name: '次へ' }));

    expect(await screen.findByRole('link', { name: '製品ロードマップ 2027' })).toBeInTheDocument();
    expect(mocks.apiRequest).toHaveBeenCalledWith(
      '/documents?offset=50&limit=50',
      expect.objectContaining({ method: 'GET' }),
    );
    expect(screen.getByText('全 120 件中 51–100 件')).toBeInTheDocument();
  });

  // 件数を選ぶと、その件数で先頭のページから引き直す（25 / 50 / 100。既定 50）。
  it('requests the first page again with the chosen page size', async () => {
    mocks.apiRequest.mockResolvedValue(listPage([DRAFT_DOC], { total: 120 }));
    const user = userEvent.setup();
    await renderPage();

    const size = await screen.findByLabelText('表示件数');
    expect(size).toHaveValue('50');
    expect(
      within(size)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['25', '50', '100']);

    await user.selectOptions(size, '25');

    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        '/documents?offset=0&limit=25',
        expect.objectContaining({ method: 'GET' }),
      ),
    );
  });

  // 次のページを読む間は、前のページの行を出したままにし（読み込み中の表示へ切り替えない）、ページ送りを止める（busy）。
  // 🔴 変異で確かめた: `keepPreviousData` を外すと行が消え、`busy` を外すと「次へ」が押せるままになり、どちらもここで落ちる。
  it('keeps the previous page on screen and the pager busy while the next page loads', async () => {
    let release: (value: unknown) => void = () => {};
    const nextPage = new Promise((resolve) => {
      release = resolve;
    });
    mocks.apiRequest.mockImplementation(async (path: string) =>
      path.includes('offset=50') ? nextPage : listPage([DRAFT_DOC], { total: 120 }),
    );
    const user = userEvent.setup();
    await renderPage();

    await user.click(
      within(await screen.findByRole('navigation', { name: 'ページ送り' })).getByRole('button', {
        name: '次へ',
      }),
    );

    // 読み込み中: 前のページの行が残り、読み込み中の表示に替わらない。ページ送りと件数の選択は止まる。
    const nav = within(screen.getByRole('navigation', { name: 'ページ送り' }));
    await waitFor(() => expect(nav.getByRole('button', { name: '次へ' })).toBeDisabled());
    expect(screen.getByRole('link', { name: '経費精算規程' })).toBeInTheDocument();
    expect(screen.queryByText('文書を読み込み中…')).not.toBeInTheDocument();
    expect(nav.getByRole('button', { name: '前へ' })).toBeDisabled();
    expect(nav.getByRole('button', { name: '1 ページ目' })).toBeDisabled();
    expect(screen.getByLabelText('表示件数')).toBeDisabled();

    release(listPage([ARCHIVED_DOC], { total: 120, offset: 50 }));

    expect(await screen.findByRole('link', { name: '製品ロードマップ 2027' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: '経費精算規程' })).not.toBeInTheDocument();
    await waitFor(() =>
      expect(
        within(screen.getByRole('navigation', { name: 'ページ送り' })).getByRole('button', {
          name: '次へ',
        }),
      ).toBeEnabled(),
    );
  });

  // 最後のページでは「次へ」を押せない（先頭の「前へ」と対。画面仕様 §ページング）。
  it('disables "next" on the last page', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) =>
      path.includes('offset=100')
        ? listPage([ARCHIVED_DOC], { total: 120, offset: 100 })
        : listPage([DRAFT_DOC], { total: 120 }),
    );
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '3 ページ目' }));

    expect(await screen.findByRole('link', { name: '製品ロードマップ 2027' })).toBeInTheDocument();
    expect(screen.getByText('全 120 件中 101–120 件')).toBeInTheDocument();
    const nav = within(screen.getByRole('navigation', { name: 'ページ送り' }));
    expect(nav.getByRole('button', { name: '次へ' })).toBeDisabled();
    expect(nav.getByRole('button', { name: '前へ' })).toBeEnabled();
    expect(nav.getByRole('button', { name: '3 ページ目' })).toHaveAttribute('aria-current', 'page');
  });

  // 削除などで総件数が減り、いまのページが末尾を越えたら、末尾のページへ戻す（空のページを出したままにしない）。
  it('moves back to the last page when the current page falls beyond the total', async () => {
    let shrunk = false;
    mocks.apiRequest.mockImplementation(async (path: string) => {
      if (path.includes('offset=100')) {
        shrunk = true;
        return listPage([], { total: 40, offset: 100 });
      }
      return listPage([DRAFT_DOC], { total: shrunk ? 40 : 120 });
    });
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '3 ページ目' }));

    expect(await screen.findByText('全 40 件中 1–40 件')).toBeInTheDocument();
    const offsets = mocks.apiRequest.mock.calls
      .map(([path]) => String(path))
      .filter((path) => path.startsWith('/documents?'));
    expect(offsets).toEqual([
      '/documents?offset=0&limit=50',
      '/documents?offset=100&limit=50',
      '/documents?offset=0&limit=50',
    ]);
  });

  // ── #1923 段 2（IADR-0545）: 並べ替え・表題の検索・状態の絞り込み・件数の要約・列 ─────────────

  /** 一覧の要求のパス（`/documents?…`）だけを順に取り出す。 */
  const listPaths = () =>
    mocks.apiRequest.mock.calls
      .map(([path]) => String(path))
      .filter((path) => path.startsWith('/documents?'));

  // 並べ替えを変えると `sort` を載せて先頭のページから引き直す。既定（更新日時が新しい順）は載せない（従前の URL）。
  it('requests the chosen sort from the first page and omits the default', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) =>
      listPage([DRAFT_DOC], { total: 120, offset: path.includes('offset=50') ? 50 : 0 }),
    );
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '2 ページ目' }));
    await waitFor(() => expect(listPaths()).toContain('/documents?offset=50&limit=50'));
    await user.selectOptions(screen.getByLabelText('並べ替え'), 'title-asc');

    await waitFor(() =>
      expect(listPaths()).toContain('/documents?offset=0&limit=50&sort=title-asc'),
    );
    expect(listPaths()[0]).toBe('/documents?offset=0&limit=50');
  });

  // 表題の検索は入力が止まってから 1 回だけ引く（1 打鍵ごとに台帳を 1 周させない）。前後の空白は送らない。
  it('requests the title query once after typing stops, trimmed', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) =>
      path.includes('q=')
        ? listPage([DRAFT_DOC], { total: 1, unfilteredTotal: 120 })
        : listPage([DRAFT_DOC, ARCHIVED_DOC], { total: 120 }),
    );
    const user = userEvent.setup();
    await renderPage();

    await user.type(await screen.findByLabelText('表題で検索'), ' 経費 ');

    expect(await screen.findByText('絞り込み 1 件（全 120 件）中 1–1 件')).toBeInTheDocument();
    const queried = listPaths().filter((path) => path.includes('q='));
    expect(queried).toEqual([`/documents?offset=0&limit=50&q=${encodeURIComponent('経費')}`]);
  });

  // 状態で絞ると `status` を載せる。選択肢には状態別の件数（絞り込み前）を添える。解除で条件を外して先頭から引く。
  it('filters by status with per-status counts and clears the filters', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) =>
      path.includes('status=archived')
        ? listPage([ARCHIVED_DOC], {
            total: 1,
            unfilteredTotal: 3,
            statusCounts: { draft: 2, archived: 1 },
          })
        : listPage([DRAFT_DOC, ARCHIVED_DOC], {
            total: 3,
            statusCounts: { draft: 2, archived: 1 },
          }),
    );
    const user = userEvent.setup();
    await renderPage();

    const status = await screen.findByLabelText('状態');
    await waitFor(() =>
      expect(
        within(status).getByRole('option', { name: 'アーカイブ済み（1 件）' }),
      ).toBeInTheDocument(),
    );
    expect(within(status).getByRole('option', { name: 'すべて（3 件）' })).toBeInTheDocument();
    expect(within(status).getByRole('option', { name: '公開中（0 件）' })).toBeInTheDocument();
    await user.selectOptions(status, 'archived');

    expect(await screen.findByText('絞り込み 1 件（全 3 件）中 1–1 件')).toBeInTheDocument();
    expect(listPaths()).toContain('/documents?offset=0&limit=50&status=archived');

    await user.click(screen.getAllByRole('button', { name: '条件を解除' })[0]!);
    expect(await screen.findByText('全 3 件中 1–3 件')).toBeInTheDocument();
    expect(listPaths().at(-1)).toBe('/documents?offset=0&limit=50');
  });

  // 絞り込みの結果だけが 0 件なら「文書はまだありません。」ではなく条件の側の空を出し、解除の口を置く。
  it('distinguishes "no match" from "no documents yet"', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) =>
      path.includes('status=published')
        ? listPage([], { total: 0, unfilteredTotal: 2 })
        : listPage([DRAFT_DOC, ARCHIVED_DOC]),
    );
    const user = userEvent.setup();
    await renderPage();

    await user.selectOptions(await screen.findByLabelText('状態'), 'published');

    expect(await screen.findByText('条件に一致する文書はありません。')).toBeInTheDocument();
    expect(screen.queryByText('文書はまだありません。')).not.toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    await user.click(screen.getAllByRole('button', { name: '条件を解除' }).at(-1)!);
    expect(await screen.findByRole('link', { name: '経費精算規程' })).toBeInTheDocument();
  });

  // 列: 本文の状態（色だけでなく文言）と更新日時。長い表題は省略し、全文はホバー（title）とリンクの名前に残す。
  it('shows the body state and the update time, and keeps the full title on hover', async () => {
    const longTitle = '長い表題'.repeat(40);
    mocks.apiRequest.mockResolvedValue(
      listPage([
        { ...DRAFT_DOC, title: longTitle, markdownUri: 'storage://docs/a.md', hasBody: true },
        { ...ARCHIVED_DOC, markdownUri: 'storage://docs/b.md', hasBody: false },
        {
          ...DRAFT_DOC,
          id: 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee',
          title: '手入力',
          markdownUri: null,
        },
      ]),
    );
    await renderPage();

    const link = await screen.findByRole('link', { name: longTitle });
    expect(link).toHaveAttribute('title', longTitle);
    expect(link).toHaveAttribute('href', `/docs/${DOC_ID}`);
    const table = within(screen.getByRole('table'));
    expect(table.getByRole('columnheader', { name: '本文' })).toBeInTheDocument();
    expect(table.getByText('本文あり')).toBeInTheDocument();
    expect(table.getByText('本文なし（原本を参照）')).toBeInTheDocument();
    expect(table.getByText('本文未設定')).toBeInTheDocument();
    // 時刻は実行環境のタイムゾーンで出る（`formatDateTime`）。書式（`YYYY/MM/DD HH:mm`）が 3 行に出ることだけを見る。
    expect(table.getAllByText(/^2026\/0[78]\/\d{2} \d{2}:\d{2}$/)).toHaveLength(3);
  });
});
