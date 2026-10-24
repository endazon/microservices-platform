import { test, expect } from './support/cspGuard';
import type {
  DocumentShareDto,
  GroupSummaryDto,
  PrivateNoteDto,
  PrivateNoteListResponse,
  UserSummaryDto,
} from '../src/lib/api/generated/bff.schemas';
import {
  installBffSession,
  sessionUser,
  expectBffTrafficIsComplete,
  reply,
} from './support/bffSession';

// SC-19, UC-11, FR-19, FR-21 (#1099): 個人資料管理（`/my/notes`）のスクリーンレベル・スモーク。
//
// 🔴 **UC-11 が E2E で 1 度も踏まれていなかった**（#452 §退行防止 は UC-11 を名指ししている）。
// 本ファイルは**実ブラウザ・実ビルド成果物の上で UC-11 の導線を実際に踏む**。
// セッションは `/bff/auth/me` の応答で成立させる（土台と限界は `support/bffSession.ts`）。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない。** 未知パスの受け皿（catchAllRoute）は
// `RequireAuth` 配下に居るため、ルートを消しても未認証なら同じく `/login` へ行く（#918 の実測）。
// ルートの実在は、下のセッション付きの本体（見出しを待つ 3 本）と `router.test.ts` が固定する ——
// パスを変えると catch-all が `NotFound` を描き、見出しの待ちが落ちる（#1099 で変異試験した）。

const GB = 1024 ** 3;

function note(overrides: Partial<PrivateNoteDto> = {}): PrivateNoteDto {
  return {
    id: 'note-1',
    title: '設計メモ',
    vaultPath: '設計メモ.md',
    version: 1,
    bytes: 12_288,
    includeInSearch: false,
    includeInGraph: false,
    includeInAi: false,
    deleted: false,
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-02T00:00:00Z',
    // #1441: 公開範囲・同期状態・タグ（契約 `PrivateNoteDto` の 5 項目）。
    visibility: 'private',
    sharedUserCount: 0,
    sharedGroupCount: 0,
    syncState: 'target',
    tags: [],
    ...overrides,
  };
}

function listOf(notes: PrivateNoteDto[], percent = 1): PrivateNoteListResponse {
  return {
    usage: { usedBytes: Math.round((percent / 100) * GB), limitBytes: GB, percent },
    notes,
  };
}

test('unauthenticated visit to /my/notes redirects to /login', async ({ page }) => {
  await page.goto('/my/notes');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-19: renders for any authenticated user and offers no body editor', async ({ page }) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-19 主アクター「全利用者」。**ロール限定は無い**（管理ロールを与えない）。
    user: sessionUser([]),
    handlers: { 'GET /private-notes': listOf([note()]) },
  });

  await page.goto('/my/notes');

  // ★ 陽性対照: 画面ラベルは「個人資料」で固定（05_screens §用語）。
  await expect(page.getByRole('heading', { name: '個人資料', level: 1 })).toBeVisible();
  // 05_screens §SC-19「業務関連資料としての扱い」の固定文言。**折りたたみの中に隠さない。**
  await expect(page.getByText('個人資料は業務関連資料として扱われます。')).toBeVisible();
  // 05_screens §SC-19「個人資料であることの表示」: 記号とラベルが意味を担う（色だけに頼らない）。
  await expect(page.getByText('👤 個人資料（自分のみ）')).toBeVisible();

  // ★ 陰性対照 1: **本画面に本文の編集手段を置かない**（ADR-0046 D-02。本文は Obsidian 経路だけ）。
  // 「編集導線が無い」は「何も見えない」でも成立してしまうため、**上の陽性対照と対で**読むこと。
  await expect(page.locator('textarea')).toHaveCount(0);
  await expect(page.locator('[contenteditable="true"]')).toHaveCount(0);
  // ★ 陰性対照 2: 他人の非公開資料の件数・存在を示唆する表示を置かない（05_screens §描いてはいけないもの）。
  await expect(page.getByText(/閲覧できません/)).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('SC-19/UC-11 basic flow 1 and 4: a new note starts with every exposure toggle off', async ({
  page,
}) => {
  let notes: PrivateNoteDto[] = [];
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes': () => listOf(notes),
      'POST /private-notes': (call) => {
        const body = call.body as { title: string; vaultPath: string | null };
        // UC-11 基本フロー 1: 作成直後は**露出 3 トグルがすべて OFF**（後段の既定。契約の注記と同じ）。
        const created = note({
          id: 'note-new',
          title: body.title,
          vaultPath: body.vaultPath ?? '',
        });
        notes = [created];
        return created;
      },
    },
  });

  await page.goto('/my/notes');
  // 空状態（05_screens §SC-19 主要素 7）。ここを待たずに入力すると、一覧の初回取得と競合する。
  await expect(page.getByText('個人資料がまだありません。')).toBeVisible();

  // 「タイトル」は新規作成欄と絞り込み欄の両方にあるため id で引く（両者は別の要素である）。
  await page.locator('#note-title').fill('新しい設計メモ');
  await page.getByRole('button', { name: '作成する' }).click();

  // ★ 陽性対照: 作成が一覧へ反映される（IADR-0127 決定 5 の invalidate 経路まで実走している）。
  await expect(page.getByRole('cell', { name: '新しい設計メモ' })).toBeVisible();
  await expect(
    page.getByRole('status').filter({ hasText: '個人資料を作成しました。' }),
  ).toBeVisible();

  // ★ 陽性対照: UC-11 基本フロー 1「3 トグルがすべて OFF」。
  await expect(page.getByRole('checkbox', { name: '横断検索に含める' })).not.toBeChecked();
  await expect(page.getByRole('checkbox', { name: 'ナレッジグラフに表示する' })).not.toBeChecked();
  await expect(page.getByRole('checkbox', { name: 'AI の入力に含める' })).not.toBeChecked();

  // ★ 陰性対照: 作成要求は**本文を運ばない**（ADR-0046 D-02。本文の口が生えたらここが落ちる）。
  const created = traffic.calls.find((c) => c.key === 'POST /private-notes');
  expect(created?.body).toEqual({ title: '新しい設計メモ', vaultPath: null });

  expectBffTrafficIsComplete(traffic);
});

test('SC-19/UC-11 exception flow: a full quota blocks creation only, and warns that deleting frees nothing', async ({
  page,
}) => {
  let current = note();
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      // 05_screens §SC-19「保存容量と版履歴」: 100% 到達。
      'GET /private-notes': () => listOf([current], 100),
      'PUT /private-notes/note-1/exposure': (call) => {
        current = { ...current, ...(call.body as Partial<PrivateNoteDto>) };
        return current;
      },
    },
  });

  await page.goto('/my/notes');

  // ★ 陽性対照: 100% の固定文言と、**新規作成の導線の無効化**。
  await expect(
    page.getByText('保存容量の上限に達しました。新しい資料は作成できませんが、'),
  ).toBeVisible();
  await expect(page.locator('#note-title')).toBeDisabled();
  await expect(page.getByRole('button', { name: '作成する' })).toBeDisabled();

  // ★ 陰性対照: **既存資料の更新は止めない**（計画が明記する非対称。
  // 「上限に達したら全部止める」実装はここで落ちる —— 陽性対照だけでは区別できない）。
  const includeInSearch = page.getByRole('checkbox', { name: '横断検索に含める' });
  await expect(includeInSearch).toBeEnabled();
  await expect(includeInSearch).not.toBeChecked();
  await includeInSearch.click();
  // 露出は 3 つとも独立に運ばれる（05_screens §SC-20 主要素 8。変えない 2 つは現在値）。
  await expect(includeInSearch).toBeChecked();
  const exposure = traffic.calls.filter((c) => c.key === 'PUT /private-notes/note-1/exposure');
  expect(exposure).toHaveLength(1);
  expect(exposure[0].body).toEqual({
    includeInSearch: true,
    includeInGraph: false,
    includeInAi: false,
  });

  // 05_screens §SC-19「削除の確認ダイアログ（論理削除）」の固定文言。
  await page.getByRole('button', { name: '削除する' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('削除しても容量は空きません（90 日間保管されます）');

  expectBffTrafficIsComplete(traffic);
});

test('SC-19 (#1441): the list shows visibility, sync state and tags, and never names who it is shared with', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      // 契約が運ぶのは**状態と件数だけ**である（相手の識別子も表示名も無い）。
      'GET /private-notes': listOf([
        note({
          id: 'note-shared',
          title: '共有した手順書',
          vaultPath: '共有した手順書.md',
          visibility: 'groups',
          sharedUserCount: 1,
          sharedGroupCount: 2,
          syncState: 'conflict',
          tags: ['議事録'],
        }),
      ]),
    },
  });

  await page.goto('/my/notes');

  // ★ 陽性対照: 3 列がいずれも見出しとして在り、値は**色ではなく文言**で読める。
  await expect(page.getByRole('columnheader', { name: '公開範囲' })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: '同期状態' })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: 'タグ' })).toBeVisible();
  const row = page.getByRole('row', { name: /共有した手順書/ });
  await expect(row.getByText('グループ指定（2 件）')).toBeVisible();
  await expect(row.getByText('競合あり')).toBeVisible();
  await expect(row.getByText('議事録')).toBeVisible();

  // ★ 陰性対照: **行には**指定先（共有相手）の名前を出さない（一覧の応答が運ばない値である）。
  // 「置いていない」だけでは何も描かない実装と区別できないため、上の陽性対照と対で読むこと。
  // ［2026-09-12 / #1445］相手の一覧は**行操作から開くダイアログ**が持つ（下の spec）。
  await expect(page.getByText(/共有先:/)).toHaveCount(0);
  await expect(page.getByRole('dialog')).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('SC-19 (#1441): visibility, sync state and tag filters live in the URL', async ({ page }) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes': listOf([
        note({ id: 'note-private', title: '下書き', tags: ['設計'] }),
        note({
          id: 'note-groups',
          title: '部門の方針メモ',
          vaultPath: '部門の方針メモ.md',
          visibility: 'groups',
          sharedGroupCount: 3,
          syncState: 'excluded',
          tags: ['議事録'],
        }),
      ]),
    },
  });

  await page.goto('/my/notes');
  await expect(page.getByRole('cell', { name: '下書き' })).toBeVisible();

  // 🔴 絞り込みの単一情報源は URL である（再読込・共有で同じ一覧になる）。
  await page.getByLabel('公開範囲で絞り込む').selectOption('groups');
  await expect(page).toHaveURL(/visibility=groups/);
  await expect(page.getByRole('cell', { name: '部門の方針メモ' })).toBeVisible();
  await expect(page.getByRole('cell', { name: '下書き' })).toHaveCount(0);

  // ★ 陽性対照: 絞り込みは URL だけで再現できる（画面の一時状態に置いていない）。
  await page.goto('/my/notes?tag=設計');
  await expect(page.getByRole('cell', { name: '下書き' })).toBeVisible();
  await expect(page.getByRole('cell', { name: '部門の方針メモ' })).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('SC-19/UC-11 (#1445): share targets are listed by display name, added by search, and revoked — never by user id', async ({
  page,
}) => {
  let shares: DocumentShareDto[] = [
    {
      subjectType: 'user',
      subjectId: 'hanako',
      grantedBy: 'e2e',
      createdAt: '2026-09-02T00:00:00Z',
    },
  ];
  const directory: UserSummaryDto[] = [
    { username: 'hanako', displayName: '花子 ハナコ', enabled: true },
    { username: 'jiro', displayName: '次郎 ジロウ', enabled: true },
  ];
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes': () =>
        listOf([
          note({
            id: 'note-1',
            visibility: 'users',
            sharedUserCount: shares.length,
            sharedGroupCount: 0,
          }),
        ]),
      'GET /private-notes/note-1/shares': () => shares,
      'POST /private-notes/note-1/shares': (call) => {
        const body = call.body as { subjectType: string; subjectId: string };
        shares = [...shares, { ...body, grantedBy: 'e2e', createdAt: '2026-09-12T00:00:00Z' }];
        return shares[shares.length - 1];
      },
      'DELETE /private-notes/note-1/shares/user/hanako': () => {
        shares = shares.filter((s) => s.subjectId !== 'hanako');
        return reply(204, {});
      },
      // 表示名は `resolve`（POST だが照会）で引く。`lookup` は有効な利用者だけを返す。
      'POST /users/resolve': (call) => {
        const body = call.body as { usernames: string[] };
        return directory.filter((u) => body.usernames.includes(u.username));
      },
      'GET /users/lookup': () => directory,
    },
  });

  await page.goto('/my/notes');
  await expect(page.getByRole('cell', { name: '設計メモ' })).toBeVisible();

  // ★ 陰性対照: 開く前は共有先を 1 度も引かない（閉じている間は問い合わせない）。
  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /private-notes/note-1/shares');

  await page.getByRole('button', { name: '共有先を変更する' }).click();
  const dialog = page.getByRole('dialog');

  // ★ 陽性対照: 現在の指定先が**表示名で**並ぶ。
  await expect(dialog.getByText('花子 ハナコ')).toBeVisible();
  // 🔴 陰性対照 1: ADR-0098 決定 1 —— 利用者識別子は画面に出ない。
  await expect(dialog.getByText('hanako')).toHaveCount(0);
  // ［2026-09-12 / #1447］種別の切替が在り、**既定は個人**である（グループの導線は下の spec が踏む）。
  await expect(dialog.getByRole('radio', { name: '個人' })).toBeChecked();
  await expect(dialog.getByRole('radio', { name: 'グループ' })).not.toBeChecked();

  // 検索 → 候補（表示名だけ）→ 追加。**すでに共有済みの花子は候補に出ない。**
  await dialog.getByLabel('名前で検索する').fill('次郎');
  const options = dialog.getByRole('option');
  await expect(options).toHaveCount(1);
  await expect(options.first()).toHaveText('次郎 ジロウ');
  await options.first().click();
  await dialog.getByRole('button', { name: '追加' }).click();

  await expect(dialog.getByText('次郎 ジロウ')).toBeVisible();
  // 送った本文は `subjectType: 'user'` 固定である（グループを送る経路が無いことの実測）。
  const granted = traffic.calls.find((c) => c.key === 'POST /private-notes/note-1/shares');
  expect(granted?.body).toEqual({ subjectType: 'user', subjectId: 'jiro' });

  // 取り消し（花子の行）。**取り消すと一覧から消える。**
  await dialog
    .getByRole('listitem')
    .filter({ hasText: '花子 ハナコ' })
    .getByRole('button', { name: '取り消す' })
    .click();
  await expect(dialog.getByText('花子 ハナコ')).toHaveCount(0);
  expect(traffic.calls.map((c) => c.key)).toContain(
    'DELETE /private-notes/note-1/shares/user/hanako',
  );

  // Esc で降りられる（キーボードだけで閉じられる）。
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('SC-19/UC-11 (#1447): group targets are searched, added and revoked by display name — never by group id', async ({
  page,
}) => {
  // 🔴 **Keycloak のグループ ID は画面に出してはならない**（ADR-0098 決定 1）。`g-` 接頭辞と
  // UUID の両方の形を含めて、実装が一部だけ隠しても緑にならないようにする。
  const SALES_GROUP = 'g-9f8e7d6c-5b4a-4321-8765-0a1b2c3d4e5f';
  const DEV_GROUP = 'g-8e7d6c5b-4a39-4210-9876-1b2c3d4e5f60';
  let shares: DocumentShareDto[] = [
    {
      subjectType: 'group',
      subjectId: SALES_GROUP,
      grantedBy: 'e2e',
      createdAt: '2026-09-02T00:00:00Z',
    },
  ];
  const groups: GroupSummaryDto[] = [
    { id: SALES_GROUP, displayName: '営業部', path: '/teams/sales' },
    { id: DEV_GROUP, displayName: '開発部', path: '/teams/dev' },
  ];
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes': () =>
        listOf([
          note({
            id: 'note-1',
            visibility: 'groups',
            sharedUserCount: 0,
            sharedGroupCount: shares.length,
          }),
        ]),
      'GET /private-notes/note-1/shares': () => shares,
      'POST /private-notes/note-1/shares': (call) => {
        const body = call.body as { subjectType: string; subjectId: string };
        shares = [...shares, { ...body, grantedBy: 'e2e', createdAt: '2026-09-12T00:00:00Z' }];
        return shares[shares.length - 1];
      },
      [`DELETE /private-notes/note-1/shares/group/${SALES_GROUP}`]: () => {
        shares = shares.filter((s) => s.subjectId !== SALES_GROUP);
        return reply(204, {});
      },
      // 表示名は `resolve`（POST だが照会）で引く。**無い ID は応答から落ちる。**
      'POST /groups/resolve': (call) => {
        const body = call.body as { ids: string[] };
        return groups.filter((g) => body.ids.includes(g.id));
      },
      'GET /groups/lookup': () => groups,
    },
  });

  await page.goto('/my/notes');
  await expect(page.getByRole('cell', { name: '設計メモ' })).toBeVisible();

  await page.getByRole('button', { name: '共有先を変更する' }).click();
  const dialog = page.getByRole('dialog');

  // ★ 陽性対照: 既存のグループ共有が**表示名（＋パス）で**並ぶ。
  await expect(dialog.getByText('営業部')).toBeVisible();
  await expect(dialog.getByText('/teams/sales')).toBeVisible();
  // ［2026-09-12 / #1447］暫定手段の解除: **「本画面では変更できません」の告知は無い。**
  await expect(dialog.getByText(/本画面では変更できません/)).toHaveCount(0);

  // グループへ切り替えて検索 → 候補（表示名 ＋ パス）→ 追加。**共有済みの営業部は候補に出ない。**
  await dialog.getByRole('radio', { name: 'グループ' }).click();
  await dialog.getByLabel('グループ名で検索する').fill('開発');
  const options = dialog.getByRole('option');
  await expect(options).toHaveCount(1);
  await expect(options.first()).toContainText('開発部');
  await options.first().click();
  await dialog.getByRole('button', { name: '追加' }).click();

  await expect(dialog.getByText('開発部')).toBeVisible();
  // 送った本文は `subjectType: 'group'` である（グループ指定が実際に効くようになった）。
  const granted = traffic.calls.find((c) => c.key === 'POST /private-notes/note-1/shares');
  expect(granted?.body).toEqual({ subjectType: 'group', subjectId: DEV_GROUP });

  // 取り消し（営業部の行）。**取り消すと一覧から消える。**
  await dialog
    .getByRole('listitem')
    .filter({ hasText: '営業部' })
    .getByRole('button', { name: '取り消す' })
    .click();
  await expect(dialog.getByText('営業部')).toHaveCount(0);
  expect(traffic.calls.map((c) => c.key)).toContain(
    `DELETE /private-notes/note-1/shares/group/${SALES_GROUP}`,
  );

  // 🔴 陰性対照: グループ識別子は本文にも属性値にも現れない（表示名とパスが在ることと対で読む）。
  const html = await dialog.innerHTML();
  for (const id of [SALES_GROUP, DEV_GROUP]) {
    expect(html, id).not.toContain(id);
  }

  expectBffTrafficIsComplete(traffic);
});

// SC-19 主要素 8, UC-11, FR-19, 計画 ADR-0036 D-08, planning#841 / IADR-0612 (#2046): 緊急アクセス（Break-glass）。
// 本人が資料 1 件を選び、理由（必須）と指名する管理者（名簿の候補）を入れ、「この操作は監査ログに記録されます」の
// 確認ダイアログから発動し、残り時間を見て、15 分より前に終える —— の一続きを実ブラウザで踏む。
test('SC-19: owner activates break-glass for one note and ends it early (#2046)', async ({
  page,
}) => {
  type Grant = Record<string, unknown>;
  let active: Grant | null = null;
  const activations: unknown[] = [];
  const directory: UserSummaryDto[] = [
    { username: 'hanako', displayName: '花子 ハナコ', enabled: true },
  ];
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes': listOf([note()]),
      'GET /private-notes/note-1/break-glass': () => ({ active }),
      'POST /private-notes/note-1/break-glass': (call) => {
        activations.push(call.body);
        const now = Date.now();
        active = {
          id: 'grant-1',
          documentId: 'note-1',
          nomineeUserId: 'hanako',
          reason: (call.body as { reason: string }).reason,
          activatedAt: new Date(now).toISOString(),
          expiresAt: new Date(now + 15 * 60_000).toISOString(),
          endedAt: null,
          endsAt: new Date(now + 15 * 60_000).toISOString(),
          active: true,
        };
        return reply(201, active);
      },
      'POST /private-notes/note-1/break-glass/end': () => {
        const ended = { ...active, active: false, endedAt: new Date().toISOString() };
        active = null;
        return ended;
      },
      'GET /users/lookup': () => directory,
      'POST /users/resolve': (call) => {
        const body = call.body as { usernames: string[] };
        return directory.filter((u) => body.usernames.includes(u.username));
      },
    },
  });

  await page.goto('/my/notes');
  await expect(page.getByRole('cell', { name: '設計メモ' })).toBeVisible();
  // ★ 陰性対照: 欄を開くまで緊急アクセスの口を 1 度も引かない。
  expect(traffic.calls.some((c) => c.key.includes('break-glass'))).toBe(false);

  await page.getByRole('button', { name: '緊急アクセス（Break-glass）' }).click();
  await page.getByLabel('対象の資料').selectOption('note-1');
  await page.getByRole('button', { name: 'この資料に緊急アクセスを発動する…' }).click();

  const dialog = page.getByRole('dialog');
  // 計画 §SC-19 の固定文言（確認ダイアログ）。
  await expect(dialog.getByText(/この操作は監査ログに記録されます。/)).toBeVisible();
  // 理由が空・指名先が未選択では発動できない。
  await expect(dialog.getByRole('button', { name: '発動する' })).toBeDisabled();
  await dialog.getByLabel('発動理由').fill('端末紛失のため');
  await dialog.getByLabel('閲覧を許す管理者を名前で検索する').fill('ha');
  await dialog.getByRole('button', { name: '花子 ハナコ' }).click();
  // 🔴 利用者名（識別子）は画面に出ない。
  await expect(dialog.getByText('hanako')).toHaveCount(0);
  await dialog.getByRole('button', { name: '花子 ハナコ に閲覧を許して発動する' }).click();

  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(activations).toEqual([{ reason: '端末紛失のため', nomineeUserId: 'hanako' }]);
  // 有効中は文言（バッジと残り時間）で示す（色だけに頼らない）。
  await expect(page.getByText('緊急アクセス有効中')).toBeVisible();
  await expect(page.getByText(/残り \d+ 分 \d+ 秒/)).toBeVisible();

  await page.getByRole('button', { name: '今すぐ終了する' }).click();
  await expect(page.getByText('有効な緊急アクセスはありません')).toBeVisible();
  expect(traffic.calls.map((c) => c.key)).toContain('POST /private-notes/note-1/break-glass/end');

  expectBffTrafficIsComplete(traffic);
});

// ───────────── M-058〜M-060（#2021 / E-16）: 容量の 3 段・緊急アクセスの理由・ゴミ箱の一括操作 ─────────────
//
// 期待値は計画 05_screens §SC-19 から起こした。
// - 容量は使用量と上限を示し、**80% と 95% の 2 段で警告する**（95% は警告色。表示の強さを変える）。
//   100% は新規作成だけを拒否する（固定文言は上の「full quota」のテストが固定する）。内訳に「うち削除済み」を併記する。
// - 緊急アクセスは**理由入力が必須**で、**発動から 15 分で自動失効**する旨を明示する。
// - 削除済み（`?tab=trash`）: 各項目の復元・完全削除、**一括選択と一括操作**（復元・完全削除）。
//   完全削除の確認ダイアログは ①復元できない ②90 日待てば自動で完全削除 ③解放される容量 の 3 点を示す。
//   完全削除が近い項目（残り 7 日以内）は強調する（色だけでなく文言も変える）。

test('M-058: 容量は 80% で予告、95% で警告を出し分け、どちらも新規作成は止めない', async ({
  page,
}) => {
  let percent = 79;
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: { 'GET /private-notes': () => listOf([note()], percent) },
  });
  const notice80 = '保存容量の使用量が 80% を超えました。';
  const warning95 =
    '保存容量の使用量が 95% を超えました。上限に達すると新しい資料を作成できなくなります。';

  // 79%: どちらも出ない（陽性対照: 容量の 1 文は出ている）。
  await page.goto('/my/notes');
  const quota = page.getByRole('region', { name: '保存容量' });
  await expect(quota).toContainText(/保存容量: [\d.]+ \/ 1\.00 GB（うち削除済み [\d.]+ GB）/);
  await expect(quota.getByText(notice80)).toHaveCount(0);
  await expect(quota.getByText(warning95)).toHaveCount(0);

  // 80%: 予告だけ。
  percent = 80;
  await page.reload();
  await expect(quota.getByText(notice80, { exact: false })).toBeVisible();
  await expect(quota.getByText(warning95)).toHaveCount(0);
  await expect(page.getByRole('button', { name: '作成する' })).toBeVisible();
  await expect(page.locator('#note-title')).toBeEnabled();

  // 95%: 警告だけ（予告とは別の文言）。まだ新規作成は止めない（止めるのは 100% だけ）。
  percent = 95;
  await page.reload();
  await expect(quota.getByText(warning95)).toBeVisible();
  await expect(quota.getByText(notice80)).toHaveCount(0);
  await expect(page.locator('#note-title')).toBeEnabled();
  await expect(page.getByText('保存容量の上限に達しました。')).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('M-059: 緊急アクセスは指名先を選んでも理由が空（空白だけ）なら発動できず、15 分で自動失効する旨を示す', async ({
  page,
}) => {
  const directory: UserSummaryDto[] = [
    { username: 'hanako', displayName: '花子 ハナコ', enabled: true },
  ];
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes': listOf([note()]),
      'GET /private-notes/note-1/break-glass': { active: null },
      'GET /users/lookup': () => directory,
      'POST /users/resolve': () => directory,
    },
  });

  await page.goto('/my/notes');
  await page.getByRole('button', { name: '緊急アクセス（Break-glass）' }).click();
  await page.getByLabel('対象の資料').selectOption('note-1');
  await page.getByRole('button', { name: 'この資料に緊急アクセスを発動する…' }).click();
  const dialog = page.getByRole('dialog');

  // ★ 15 分で自動失効する旨（延長できない）を、ダイアログの中に常に出す。
  await expect(dialog).toContainText('アクセスは発動から 15 分で自動失効し、延長できません。');

  // 指名先だけを選ぶ（陽性対照: 指名先が選ばれ、発動のボタンの名前が変わる）。
  await dialog.getByLabel('閲覧を許す管理者を名前で検索する').fill('ha');
  await dialog.getByRole('button', { name: '花子 ハナコ' }).click();
  const activate = dialog.getByRole('button', { name: '花子 ハナコ に閲覧を許して発動する' });
  await expect(activate).toBeVisible();

  // ★ 理由が空なら発動できず、空白だけでも同じ。触れた後は「必須」の理由を文言で示す。
  await expect(activate).toBeDisabled();
  const reason = dialog.getByLabel('発動理由');
  await reason.fill('   ');
  await reason.blur();
  await expect(activate).toBeDisabled();
  await expect(dialog.getByText('理由は必須です。空欄では発動できません。')).toBeVisible();

  // 理由を入れると発動できるようになる（押さない —— 発動の経路は上の #2046 のテストが固定する）。
  await reason.fill('端末紛失のため');
  await expect(activate).toBeEnabled();
  expect(traffic.calls.filter((c) => c.key === 'POST /private-notes/note-1/break-glass')).toEqual(
    [],
  );

  expectBffTrafficIsComplete(traffic);
});

test('M-060: 削除済みタブは残り日数と間近の強調を出し、一括の完全削除は 3 点を示す確認を挟み、やめれば送らない', async ({
  page,
}) => {
  const day = 24 * 60 * 60 * 1000;
  const trashed = (id: string, title: string, daysLeft: number) =>
    note({
      id,
      title,
      vaultPath: `${title}.md`,
      bytes: 60 * 1024 * 1024,
      deleted: true,
      syncState: 'excluded',
      deletedAt: new Date(Date.now() - (90 - daysLeft) * day).toISOString(),
      purgeAt: new Date(Date.now() + daysLeft * day - 60_000).toISOString(),
    });
  let notes = [note(), trashed('old-1', '古い議事録', 3), trashed('old-2', '下書きの残骸', 40)];
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes': () => listOf(notes, 30),
      'POST /private-notes/purge': (call) => {
        const ids = (call.body as { ids: string[] }).ids;
        notes = notes.filter((n) => !ids.includes(n.id));
        return reply(204, null);
      },
    },
  });

  await page.goto('/my/notes?tab=trash');
  const table = page.getByRole('table', { name: '削除済みの個人資料' });
  const rowOf = (title: string) => table.getByRole('row').filter({ hasText: title });
  // ★ 残り日数と、残り 7 日以内の強調（文言も変える）。
  await expect(rowOf('古い議事録').getByText('まもなく完全削除（残り 3 日）')).toBeVisible();
  await expect(rowOf('下書きの残骸').getByText('残り 40 日', { exact: true })).toBeVisible();

  // 一括操作は選ぶまで押せない。
  const bulkPurge = page.getByRole('button', { name: '選択した資料を完全に削除する' });
  const bulkRestore = page.getByRole('button', { name: '選択した資料を復元する' });
  await expect(bulkPurge).toBeDisabled();
  await expect(bulkRestore).toBeDisabled();
  await table.getByRole('checkbox', { name: '古い議事録 を選択' }).check();
  await table.getByRole('checkbox', { name: '下書きの残骸 を選択' }).check();
  await expect(bulkRestore).toBeEnabled();

  // ★ 確認の 3 点。やめれば送らない。
  await bulkPurge.click();
  const dialog = page.getByRole('dialog').or(page.getByRole('alertdialog'));
  await expect(dialog).toContainText(
    'この操作は元に戻せません。削除後はいかなる方法でも復元できません。',
  );
  await expect(dialog).toContainText('90 日待てば自動的に完全削除されます。');
  await expect(dialog).toContainText('対象: 2 件 ／ 解放される容量:');
  await dialog.getByRole('button', { name: 'やめる' }).click();
  await expect(dialog).toHaveCount(0);
  const purges = () => traffic.calls.filter((c) => c.key === 'POST /private-notes/purge');
  expect(purges()).toHaveLength(0);

  // 確認すれば 2 件を 1 回で送る（一括）。
  await bulkPurge.click();
  await dialog.getByRole('button', { name: '完全に削除する' }).click();
  await expect(page.getByText('個人資料を完全に削除しました。')).toBeVisible();
  expect(purges()).toHaveLength(1);
  expect((purges()[0].body as { ids: string[] }).ids.sort()).toEqual(['old-1', 'old-2']);
  await expect(page.getByText('削除済みの個人資料はありません。')).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('M-060: 削除済みの資料を選んで一括で復元すると、選んだ分だけ復元を送る', async ({ page }) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes': listOf(
        [
          note(),
          note({
            id: 'old-1',
            title: '古い議事録',
            deleted: true,
            syncState: 'excluded',
            deletedAt: '2026-10-01T00:00:00Z',
            purgeAt: '2026-12-30T00:00:00Z',
          }),
          note({
            id: 'old-2',
            title: '下書きの残骸',
            deleted: true,
            syncState: 'excluded',
            deletedAt: '2026-10-01T00:00:00Z',
            purgeAt: '2026-12-30T00:00:00Z',
          }),
        ],
        30,
      ),
      'POST /private-notes/old-1/restore': () => note({ id: 'old-1', title: '古い議事録' }),
    },
  });

  await page.goto('/my/notes?tab=trash');
  const table = page.getByRole('table', { name: '削除済みの個人資料' });
  await table.getByRole('checkbox', { name: '古い議事録 を選択' }).check();
  await page.getByRole('button', { name: '選択した資料を復元する' }).click();
  await expect(page.getByText('選択した個人資料を復元しました。')).toBeVisible();
  // ★ 選んだ 1 件だけを復元する（選んでいない資料には触れない）。完全削除は送らない。
  expect(traffic.calls.filter((c) => c.method === 'POST').map((c) => c.key)).toEqual([
    'POST /private-notes/old-1/restore',
  ]);

  expectBffTrafficIsComplete(traffic);
});
