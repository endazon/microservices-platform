import { test, expect } from '@playwright/test';
import type {
  DocumentShareDto,
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

test('SC-19/UC-11 (#1445): share targets are listed by display name, added by search, and revoked — never by user id, and never by group', async ({
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
  // 🔴 陰性対照 2: ADR-0098 決定 2 —— グループの導線も文言も無い。
  await expect(dialog.getByText(/グループ/)).toHaveCount(0);
  await expect(dialog.getByRole('combobox')).toHaveCount(0);

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
