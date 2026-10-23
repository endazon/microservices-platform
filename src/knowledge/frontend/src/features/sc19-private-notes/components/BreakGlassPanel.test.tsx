import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-19 主要素 8, SC-03, UC-11, FR-19, 計画 ADR-0036 D-08, planning#841, [[IADR-0612]] (#2046 の 3 本目):
// 緊急アクセス（Break-glass）の導線・確認ダイアログ・残り時間・本人の早期終了・監査ログ。
//
// IADR-0135 決定 4: 生成コードは mutator（bffFetch）→ **apiRequest** を通るため、モックは apiRequest に当てる
// （`ShareTargetsDialog.test.tsx` と同じ作法。MSW は使わない）。
//
// 🔴 **否定形は陽性対照と対で置く**（利用者名が DOM に出ない ↔ 表示名は出る、導線が出ない ↔ 出る行がある）。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc19PrivateNotesRoute } from '../routes/sc19PrivateNotesRoute';

const GB = 1024 ** 3;
const NOW = new Date('2026-10-24T03:00:00Z');
const NOTE_ID = '00000000-0000-0000-0000-000000000001';
const DELETED_ID = '00000000-0000-0000-0000-000000000002';
const OTHER_DOC_ID = '00000000-0000-0000-0000-0000000000ff';

function note(id: string, title: string, deleted = false) {
  return {
    id,
    title,
    vaultPath: `${title}.md`,
    version: 1,
    bytes: 1024,
    contentHash: 'abc',
    includeInSearch: false,
    includeInGraph: false,
    includeInAi: false,
    deleted,
    deletedAt: deleted ? '2026-10-20T00:00:00Z' : null,
    purgeAt: deleted ? '2027-01-18T00:00:00Z' : null,
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-02T00:00:00Z',
    visibility: 'private',
    sharedUserCount: 0,
    sharedGroupCount: 0,
    syncState: 'target',
    tags: [],
  };
}

const NOTES = [note(NOTE_ID, '設計メモ'), note(DELETED_ID, '古い下書き', true)];

/** 有効な発動（発動から 3 分経過・残り 12 分）。 */
function grant(overrides: Record<string, unknown> = {}) {
  return {
    id: 'grant-1',
    documentId: NOTE_ID,
    nomineeUserId: 'admin-hanako',
    reason: '端末紛失のため',
    activatedAt: '2026-10-24T02:57:00Z',
    expiresAt: '2026-10-24T03:12:00Z',
    endedAt: null,
    endsAt: '2026-10-24T03:12:00Z',
    active: true,
    ...overrides,
  };
}

const CANDIDATES = [
  { username: 'admin-hanako', displayName: '花子 ハナコ', enabled: true },
  // 🔴 自分自身（`tester`）は候補から除く。
  { username: 'tester', displayName: 'テスター本人', enabled: true },
];

const RESOLVED = [
  { username: 'admin-hanako', displayName: '花子 ハナコ', enabled: true },
  { username: 'tester', displayName: 'テスター本人', enabled: true },
  { username: 'owner-jiro', displayName: '次郎 ジロウ', enabled: true },
];

function auditEntry(overrides: Record<string, unknown>) {
  return {
    id: 'a-1',
    grantId: 'grant-1',
    occurredAt: '2026-10-24T02:57:00Z',
    operation: 'activate',
    actorUserId: 'tester',
    documentId: NOTE_ID,
    ownerUserId: 'tester',
    nomineeUserId: 'admin-hanako',
    reason: '端末紛失のため',
    activatedAt: '2026-10-24T02:57:00Z',
    endsAt: '2026-10-24T03:12:00Z',
    viewTarget: null,
    ...overrides,
  };
}

interface Responses {
  status?: unknown;
  activate?: unknown;
  end?: unknown;
  audit?: unknown;
}

/** 応答の組み立て。`ApiError` を渡すと拒否する（`apiRequest` は非 2xx を ApiError で投げる）。 */
function respond({
  status = { active: null },
  activate = grant(),
  end = grant({ active: false, endedAt: '2026-10-24T03:01:00Z' }),
  audit = [],
}: Responses = {}) {
  const reply = (value: unknown, code = 200) =>
    value instanceof ApiError ? Promise.reject(value) : Promise.resolve(jsonResponse(value, code));
  mocks.apiRequest.mockImplementation((path: string, init?: { method?: string }) => {
    const method = init?.method ?? 'GET';
    if (path === '/private-notes' && method === 'GET') {
      return Promise.resolve(
        jsonResponse({ usage: { usedBytes: 0.1 * GB, limitBytes: GB, percent: 10 }, notes: NOTES }),
      );
    }
    if (path === `/private-notes/${NOTE_ID}/break-glass` && method === 'GET') return reply(status);
    if (path === `/private-notes/${NOTE_ID}/break-glass` && method === 'POST')
      return reply(activate, 201);
    if (path === `/private-notes/${NOTE_ID}/break-glass/end` && method === 'POST')
      return reply(end);
    if (path.startsWith('/break-glass/audit')) return reply(audit);
    if (path.startsWith('/users/lookup')) return Promise.resolve(jsonResponse(CANDIDATES));
    if (path === '/users/resolve') return Promise.resolve(jsonResponse(RESOLVED));
    return Promise.resolve(jsonResponse({}));
  });
}

function calls(): { path: string; method: string; body: unknown }[] {
  return mocks.apiRequest.mock.calls.map((c) => ({
    path: c[0] as string,
    method: (c[1] as { method?: string } | undefined)?.method ?? 'GET',
    body: (() => {
      const raw = (c[1] as { body?: string } | undefined)?.body;
      return typeof raw === 'string' ? JSON.parse(raw) : raw;
    })(),
  }));
}

const breakGlassCalls = () =>
  calls().filter((c) => c.path.includes('break-glass') || c.path.startsWith('/users/lookup'));

async function settle(ms = 400) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

type User = ReturnType<typeof userEvent.setup>;

async function renderPage(roles: string[] = []) {
  await renderUnitRoute((shell) => [createSc19PrivateNotesRoute(shell)], {
    initialEntry: '/my/notes',
    roles,
  });
  await screen.findAllByText('設計メモ');
}

async function openPanel(user: User) {
  await user.click(screen.getByRole('button', { name: '緊急アクセス（Break-glass）' }));
  await screen.findByLabelText(/対象の資料/);
}

async function selectTarget(user: User) {
  await user.selectOptions(screen.getByLabelText(/対象の資料/), NOTE_ID);
}

async function openDialog(user: User) {
  await openPanel(user);
  await selectTarget(user);
  await user.click(
    await screen.findByRole('button', { name: 'この資料に緊急アクセスを発動する…' }),
  );
  return screen.findByRole('dialog');
}

async function pickNominee(user: User) {
  const dialog = within(screen.getByRole('dialog'));
  await user.type(dialog.getByLabelText(/閲覧を許す管理者を名前で検索する/), 'ha');
  await settle();
  await user.click(await dialog.findByRole('button', { name: '花子 ハナコ' }));
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.setSystemTime(NOW);
  mocks.apiRequest.mockReset();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('SC-19 緊急アクセス: 導線（#2046）', () => {
  it('開くまで状態も監査ログも引かない（陽性対照: 開くと対象の欄が出る）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await renderPage();

    expect(breakGlassCalls()).toEqual([]);
    expect(screen.queryByLabelText(/対象の資料/)).not.toBeInTheDocument();

    await openPanel(user);
    expect(screen.getByLabelText(/対象の資料/)).toBeInTheDocument();
    // 資料を選ぶまで状態は引かない。監査ログは「表示する」まで引かない。
    expect(breakGlassCalls()).toEqual([]);
  });

  it('対象に選べるのは利用中の自分の資料だけ（削除済みは出さない）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await renderPage();
    await openPanel(user);

    const select = screen.getByLabelText(/対象の資料/);
    const options = within(select)
      .getAllByRole('option')
      .map((o) => o.textContent);
    expect(options).toEqual(['資料を選んでください', '設計メモ']);
  });

  it('第三者が発動する導線・全利用者の資料を見る導線は無い（陽性対照: 本人の発動の導線は在る）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await renderPage(['platform-admin']);
    await openPanel(user);
    await selectTarget(user);

    expect(
      await screen.findByRole('button', { name: 'この資料に緊急アクセスを発動する…' }),
    ).toBeInTheDocument();
    expect(screen.queryByLabelText(/所有者|利用者を選/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /全利用者|他の利用者/ })).not.toBeInTheDocument();
  });
});

describe('SC-19 緊急アクセス: 確認ダイアログ（理由必須・指名・監査ログの明示）', () => {
  it('確認ダイアログに「この操作は監査ログに記録されます」と 15 分の失効を出す', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await renderPage();
    const dialog = within(await openDialog(user));

    expect(dialog.getByText(/この操作は監査ログに記録されます。/)).toBeInTheDocument();
    expect(dialog.getByText(/15\s*分で自動失効し、延長できません/)).toBeInTheDocument();
    expect(dialog.getByText(/設計メモ/)).toBeInTheDocument();
  });

  it('理由が空（空白だけを含む）では発動できず、必須であることを文言で示す', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await renderPage();
    const dialog = within(await openDialog(user));
    await pickNominee(user);

    const submit = dialog.getByRole('button', { name: /発動する$/ });
    expect(submit).toBeDisabled();

    const reason = dialog.getByLabelText(/発動理由/);
    await user.type(reason, '   ');
    await user.tab();
    expect(dialog.getByText('理由は必須です。空欄では発動できません。')).toBeInTheDocument();
    expect(submit).toBeDisabled();
    expect(reason).toHaveAttribute('aria-invalid', 'true');
    expect(calls().filter((c) => c.method === 'POST' && c.path.includes('break-glass'))).toEqual(
      [],
    );
  });

  it('理由が 500 文字を超えると発動できない（境界: 500 文字は送れる）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await renderPage();
    const dialog = within(await openDialog(user));
    await pickNominee(user);
    const reason = dialog.getByLabelText(/発動理由/);
    const submit = dialog.getByRole('button', { name: /発動する$/ });

    await user.click(reason);
    await user.paste('あ'.repeat(501));
    expect(dialog.getByText('理由は 500 文字以内で入力してください。')).toBeInTheDocument();
    expect(dialog.getByText('501 / 500 文字')).toBeInTheDocument();
    expect(submit).toBeDisabled();

    await user.type(reason, '{Backspace}');
    expect(dialog.queryByText('理由は 500 文字以内で入力してください。')).not.toBeInTheDocument();
    expect(submit).toBeEnabled();
  });

  it('指名先は名簿の候補から選ぶ（自分は候補に出ない・利用者名は出さない）。未選択では発動できない', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await renderPage();
    const dialog = within(await openDialog(user));
    await user.type(dialog.getByLabelText(/発動理由/), '端末紛失のため');

    expect(dialog.getByRole('button', { name: /発動する$/ })).toBeDisabled();
    expect(
      dialog.getByText(/指名できるのはシステム管理者（platform-admin）1 人だけです/),
    ).toBeInTheDocument();

    await user.type(dialog.getByLabelText(/閲覧を許す管理者を名前で検索する/), 'ha');
    await settle();
    // 陽性対照: 表示名は出る。
    expect(await dialog.findByRole('button', { name: '花子 ハナコ' })).toBeInTheDocument();
    // 🔴 自分自身は候補に出ない・利用者名は DOM に出ない。
    expect(dialog.queryByRole('button', { name: 'テスター本人' })).not.toBeInTheDocument();
    expect(document.body.textContent).not.toContain('admin-hanako');
  });

  it('発動すると理由（前後の空白を落とす）と指名先の利用者名を送り、閉じて完了を告げる', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await renderPage();
    const dialog = within(await openDialog(user));
    await user.type(dialog.getByLabelText(/発動理由/), '  端末紛失のため  ');
    await pickNominee(user);

    await user.click(dialog.getByRole('button', { name: '花子 ハナコ に閲覧を許して発動する' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    const posts = calls().filter((c) => c.method === 'POST' && c.path.includes('break-glass'));
    expect(posts).toEqual([
      {
        path: `/private-notes/${NOTE_ID}/break-glass`,
        method: 'POST',
        body: { reason: '端末紛失のため', nomineeUserId: 'admin-hanako' },
      },
    ]);
    expect(await screen.findByText('緊急アクセスを発動しました。')).toBeInTheDocument();
  });

  it.each([
    [
      '400（管理者でない）は後段の文言をそのまま出す',
      ApiError.fromStatus(400, ['指名できるのはシステム管理者（platform-admin）だけです。']),
      '指名できるのはシステム管理者（platform-admin）だけです。',
    ],
    [
      '409 は重ね発動・削除済みの文言',
      ApiError.fromStatus(409, ['raw']),
      'この資料には有効な緊急アクセスが既にあるか、削除済みのため発動できません。',
    ],
    [
      '503 は確認できず発動していないことを告げる',
      ApiError.fromStatus(503),
      '指名先がシステム管理者かを確認できませんでした。発動していません。時間をおいて再度お試しください。',
    ],
    [
      '403 は書き込み権限が無い',
      ApiError.fromStatus(403),
      'この資料に書き込む権限がないため発動できません。',
    ],
  ])('拒否: %s（ダイアログは閉じない）', async (_label, error, message) => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({ activate: error });
    await renderPage();
    const dialog = within(await openDialog(user));
    await user.type(dialog.getByLabelText(/発動理由/), '端末紛失のため');
    await pickNominee(user);
    await user.click(dialog.getByRole('button', { name: /発動する$/ }));

    expect(await dialog.findByRole('alert')).toHaveTextContent(message);
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.queryByText('緊急アクセスを発動しました。')).not.toBeInTheDocument();
  });

  it('キャンセルでは何も送らない', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await renderPage();
    const dialog = within(await openDialog(user));
    await user.click(dialog.getByRole('button', { name: 'キャンセル' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(calls().filter((c) => c.method === 'POST' && c.path.includes('break-glass'))).toEqual(
      [],
    );
  });
});

describe('SC-19 緊急アクセス: 有効中の表示と本人の早期終了', () => {
  it('残り時間・指名先（表示名）・理由を文言で出す（色だけに頼らない）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({ status: { active: grant() } });
    await renderPage();
    await openPanel(user);
    await selectTarget(user);

    expect(await screen.findByText('緊急アクセス有効中')).toBeInTheDocument();
    // 発動から 3 分経過（偽の時計は実時間ぶん少し進むので、秒は固定しない）。
    expect(screen.getByText(/残り 1[12] 分 \d+ 秒/)).toBeInTheDocument();
    expect(await screen.findByText('花子 ハナコ')).toBeInTheDocument();
    expect(screen.getByText('端末紛失のため')).toBeInTheDocument();
    // 有効中は重ねて発動させない。
    expect(
      screen.queryByRole('button', { name: 'この資料に緊急アクセスを発動する…' }),
    ).not.toBeInTheDocument();
    expect(document.body.textContent).not.toContain('admin-hanako');

    // 時計が進むと残り時間も減る。
    await settle(61_000);
    expect(screen.getByText(/残り 10 分 \d+ 秒/)).toBeInTheDocument();
  });

  it('本人は 15 分より前に終えられる（終了の口へ送り、完了を告げる）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({ status: { active: grant() } });
    await renderPage();
    await openPanel(user);
    await selectTarget(user);

    await user.click(await screen.findByRole('button', { name: '今すぐ終了する' }));

    expect(await screen.findByText('緊急アクセスを終了しました。')).toBeInTheDocument();
    expect(calls().filter((c) => c.method === 'POST' && c.path.includes('break-glass'))).toEqual([
      { path: `/private-notes/${NOTE_ID}/break-glass/end`, method: 'POST', body: undefined },
    ]);
  });

  it('終了に失敗したら文言で告げる', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({ status: { active: grant() }, end: ApiError.fromStatus(404) });
    await renderPage();
    await openPanel(user);
    await selectTarget(user);
    await user.click(await screen.findByRole('button', { name: '今すぐ終了する' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      '緊急アクセスを終了できませんでした。',
    );
  });

  it('残り 0 になったら状態を引き直す（失効はサーバ側の記録で決まる）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({ status: { active: grant({ endsAt: '2026-10-24T03:00:05Z' }) } });
    await renderPage();
    await openPanel(user);
    await selectTarget(user);
    await screen.findByText('緊急アクセス有効中');
    const before = calls().filter(
      (c) => c.path === `/private-notes/${NOTE_ID}/break-glass` && c.method === 'GET',
    ).length;

    respond({ status: { active: null } });
    await settle(7_000);

    expect(await screen.findByText('有効な緊急アクセスはありません')).toBeInTheDocument();
    const after = calls().filter(
      (c) => c.path === `/private-notes/${NOTE_ID}/break-glass` && c.method === 'GET',
    ).length;
    expect(after).toBeGreaterThan(before);
  });
});

describe('SC-19 緊急アクセス: 監査ログ', () => {
  it('「表示する」まで引かない。本人の行は題名・表示名で出し、操作種別を文言で出す', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({
      audit: [
        auditEntry({
          id: 'a-3',
          operation: 'view',
          actorUserId: 'admin-hanako',
          viewTarget: 'content',
          occurredAt: '2026-10-24T02:59:00Z',
        }),
        auditEntry({
          id: 'a-2',
          operation: 'view',
          actorUserId: 'admin-hanako',
          viewTarget: 'document',
          occurredAt: '2026-10-24T02:58:00Z',
        }),
        auditEntry({ id: 'a-1' }),
      ],
    });
    await renderPage();
    await openPanel(user);
    expect(calls().some((c) => c.path.startsWith('/break-glass/audit'))).toBe(false);

    await user.click(screen.getByRole('button', { name: '監査ログを表示する' }));
    const table = within(await screen.findByRole('table', { name: /緊急アクセスの監査ログ/ }));

    expect(calls().filter((c) => c.path.startsWith('/break-glass/audit'))).toEqual([
      { path: '/break-glass/audit?limit=50', method: 'GET', body: undefined },
    ]);
    expect(table.getByText('閲覧（本文）')).toBeInTheDocument();
    expect(table.getByText('閲覧（属性）')).toBeInTheDocument();
    expect(table.getByText('発動')).toBeInTheDocument();
    expect(table.getAllByText('設計メモ')).toHaveLength(3);
    expect((await table.findAllByText('花子 ハナコ')).length).toBeGreaterThan(0);
    // 🔴 利用者名（識別子）は出さない（陽性対照: 表示名は上で在る）。
    expect(document.body.textContent).not.toContain('admin-hanako');
    // 本人（管理者でない）には「指名された資料を閲覧する」は出ない。
    expect(table.queryByRole('link', { name: '指名された資料を閲覧する' })).not.toBeInTheDocument();
    expect(
      screen.getByText(/自分の資料に対する緊急アクセスの記録が表示されます/),
    ).toBeInTheDocument();
  });

  it('管理者には全件の説明を出し、自分が指名された有効な発動の行だけに SC-03 への導線を出す', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({
      audit: [
        // 自分（tester）が指名された有効な発動（他人の資料）→ 導線あり。
        auditEntry({
          id: 'b-1',
          grantId: 'g-open',
          documentId: OTHER_DOC_ID,
          actorUserId: 'owner-jiro',
          ownerUserId: 'owner-jiro',
          nomineeUserId: 'TESTER',
        }),
        // 自分が指名されたが終了済み（終了の行がある）→ 導線なし。
        auditEntry({
          id: 'b-2',
          grantId: 'g-ended',
          operation: 'end',
          documentId: OTHER_DOC_ID,
          actorUserId: 'owner-jiro',
          ownerUserId: 'owner-jiro',
          nomineeUserId: 'tester',
        }),
        auditEntry({
          id: 'b-3',
          grantId: 'g-ended',
          documentId: OTHER_DOC_ID,
          actorUserId: 'owner-jiro',
          ownerUserId: 'owner-jiro',
          nomineeUserId: 'tester',
        }),
        // 自分が指名されたが失効済み → 導線なし。
        auditEntry({
          id: 'b-4',
          grantId: 'g-expired',
          documentId: OTHER_DOC_ID,
          actorUserId: 'owner-jiro',
          ownerUserId: 'owner-jiro',
          nomineeUserId: 'tester',
          endsAt: '2026-10-24T02:59:59Z',
        }),
        // 他の管理者が指名された有効な発動 → 導線なし。
        auditEntry({
          id: 'b-5',
          grantId: 'g-other',
          documentId: OTHER_DOC_ID,
          actorUserId: 'owner-jiro',
          ownerUserId: 'owner-jiro',
          nomineeUserId: 'admin-hanako',
        }),
      ],
    });
    await renderPage(['platform-admin']);
    await openPanel(user);
    await user.click(screen.getByRole('button', { name: '監査ログを表示する' }));
    const table = within(await screen.findByRole('table', { name: /緊急アクセスの監査ログ/ }));

    expect(
      screen.getByText(/システム管理者には、すべての利用者の緊急アクセスの記録が表示されます/),
    ).toBeInTheDocument();
    const links = table.getAllByRole('link', { name: '指名された資料を閲覧する' });
    expect(links).toHaveLength(1);
    expect(links[0]).toHaveAttribute('href', `/docs/${OTHER_DOC_ID}?via=break-glass`);
    // 🔴 他人の資料の題名は引かない（識別子だけを出す）。
    expect(table.getAllByText(OTHER_DOC_ID).length).toBe(5);
    expect(await table.findAllByText('次郎 ジロウ')).not.toHaveLength(0);
  });

  it('記録が無ければ空の状態を出す', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({ audit: [] });
    await renderPage();
    await openPanel(user);
    await user.click(screen.getByRole('button', { name: '監査ログを表示する' }));

    expect(await screen.findByText('緊急アクセスの記録はありません。')).toBeInTheDocument();
  });
});
