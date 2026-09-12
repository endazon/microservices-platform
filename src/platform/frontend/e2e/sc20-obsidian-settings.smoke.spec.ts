import { test, expect } from '@playwright/test';
import type {
  SyncConflictDetailDto,
  SyncConflictSummaryDto,
  SyncDeviceDto,
  SyncSettingsDto,
} from '../src/lib/api/generated/bff.schemas';
import {
  installBffSession,
  sessionUser,
  expectBffTrafficIsComplete,
  reply,
} from './support/bffSession';

// SC-20, UC-11, FR-20, ADR-0037 (#1099): Obsidian 連携設定（`/my/obsidian`）のスモーク。
//
// 🔴 **UC-11 基本フロー 2 の「本文の編集手段」はこの画面だけである**（ADR-0046 D-02 / D-04。
// 個人資料は Wiki.js へ同期しないため、連携を設定していない利用者に本文の編集手段は無い）。
// その導線が E2E で 1 度も踏まれていなかった。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない**（catch-all が認証ガード配下に居る。#918）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝これは契約の写しであって後段ではない）は `support/bffSession.ts`。

const DAY = 86_400_000;

function device(overrides: Partial<SyncDeviceDto> = {}): SyncDeviceDto {
  return {
    id: 'device-1',
    deviceName: '自宅 PC',
    issuedAt: new Date(Date.now() - 3 * DAY).toISOString(),
    expiresAt: new Date(Date.now() + 27 * DAY).toISOString(),
    revoked: false,
    lastSyncAt: new Date(Date.now() - DAY).toISOString(),
    active: true,
    ...overrides,
  };
}

// #1442: 同期対象範囲（主要素 3）と競合（主要素 5）。**どちらも画面を開いた時点で引く**ので、
// セッション付きの面はすべて応答を用意する（用意し忘れは `expectBffTrafficIsComplete` が落とす）。
const EMPTY_SETTINGS: SyncSettingsDto = { targetFolders: [], updatedAt: null };

function conflict(overrides: Partial<SyncConflictSummaryDto> = {}): SyncConflictSummaryDto {
  return {
    id: 'conflict-1',
    noteId: 'note-1',
    title: '設計メモ',
    vaultPath: '仕事/メモ/設計メモ.md',
    detectedAt: new Date(Date.now() - DAY).toISOString(),
    deviceId: 'device-1',
    deviceName: '会議室の PC',
    localBaseVersion: 3,
    serverVersion: 5,
    ...overrides,
  };
}

/** 新しい区画が既定で引く 2 面（中身を見ない spec 用の空応答）。 */
const QUIET_SYNC_PANELS = {
  'GET /private-notes/sync-settings': EMPTY_SETTINGS,
  'GET /private-notes/conflicts': [] as SyncConflictSummaryDto[],
};

test('unauthenticated visit to /my/obsidian redirects to /login', async ({ page }) => {
  await page.goto('/my/obsidian');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-20: states the sync scope and asks for no administrator approval', async ({ page }) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-20 主アクター「Obsidian を使う利用者本人」。ロール限定は無い。
    user: sessionUser([]),
    handlers: { 'GET /private-notes/devices': [device()], ...QUIET_SYNC_PANELS },
  });

  await page.goto('/my/obsidian');

  // ★ 陽性対照: 計画の固定文言 3 段落のうち、範囲と削除の 2 つ。
  await expect(page.getByRole('heading', { name: 'Obsidian 連携設定', level: 1 })).toBeVisible();
  await expect(page.getByText('同期できるのは、あなたが作成した個人資料のみです。')).toBeVisible();
  // この一文が無いと利用者は復元可能性を過信する（計画が「必ず含める」と書いている）。
  await expect(
    page.getByText('90 日を過ぎると自動的に完全削除され、復元できなくなります。'),
  ).toBeVisible();
  // 主要素 1: 端末一覧と、端末紛失時の防御線（個別失効・一括失効）。
  await expect(page.getByRole('cell', { name: '自宅 PC' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'この端末を失効する' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'すべての端末を失効する' })).toBeEnabled();

  // ★ 陰性対照 1: **端末登録に管理者承認のステップを置かない**（05_screens §SC-20 描いてはいけないもの）。
  // 私物端末を認めるための裁定であり、承認段が生えたらここで落ちる。
  await expect(page.getByText(/承認/)).toHaveCount(0);
  // ★ 陰性対照 2: 他利用者の同期設定を見る導線・組織文書を同期する導線を置かない（同上）。
  await expect(page.getByText(/他の利用者の端末|組織文書を同期/)).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('SC-20/UC-11: an issued sync token is shown once and never comes back from the list', async ({
  page,
}) => {
  let devices: SyncDeviceDto[] = [];
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      ...QUIET_SYNC_PANELS,
      'GET /private-notes/devices': () => devices,
      'POST /private-notes/devices': (call) => {
        const body = call.body as { deviceName: string };
        const created = device({ id: 'device-new', deviceName: body.deviceName });
        devices = [created];
        // 🔴 平文が載るのはこの応答だけである（ADR-0037 決定 12・15）。
        // **201 でなければ画面は平文を描かない**（生成フックの成功枝の状態コード）。
        return reply(201, {
          deviceId: created.id,
          deviceName: created.deviceName,
          token: 'plaintext-sync-token-e2e',
          expiresAt: created.expiresAt,
        });
      },
    },
  });

  await page.goto('/my/obsidian');
  await expect(page.getByText('接続している端末はまだありません。')).toBeVisible();

  await page.getByLabel('端末名（任意）').fill('社用ノート PC');
  await page.getByRole('button', { name: 'トークンを発行する' }).click();

  // ★ 陽性対照: 発行直後に一度だけ平文を出し、再表示できない旨を同じ枠の中に置く（主要素 2）。
  await expect(page.getByText('plaintext-sync-token-e2e')).toBeVisible();
  await expect(page.getByText('このトークンを表示できるのは今回だけです。')).toBeVisible();
  await expect(page.getByRole('cell', { name: '社用ノート PC' })).toBeVisible();

  // ★ 陰性対照 1: 一覧の応答は平文を運ばない（運んだら「一度だけ」が嘘になる）。
  const listed = traffic.calls.filter((c) => c.key === 'GET /private-notes/devices');
  expect(listed.length).toBeGreaterThan(0);
  expect(JSON.stringify(devices)).not.toContain('plaintext-sync-token-e2e');

  // ★ 陰性対照 2: 再読込すると平文は戻らない（ローカル状態にしか無い＝ URL にも保存先にも無い）。
  await page.reload();
  await expect(page.getByRole('cell', { name: '社用ノート PC' })).toBeVisible();
  await expect(page.getByText('plaintext-sync-token-e2e')).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('SC-20 (#1442): removing a target folder stops syncing and says it is not a deletion', async ({
  page,
}) => {
  let settings: SyncSettingsDto = {
    targetFolders: [
      { path: '仕事/メモ', noteCount: 12, lastSyncAt: new Date(Date.now() - DAY).toISOString() },
      { path: '日誌', noteCount: 3, lastSyncAt: null },
    ],
    updatedAt: new Date(Date.now() - DAY).toISOString(),
  };
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes/devices': [device()],
      'GET /private-notes/conflicts': [],
      'GET /private-notes/sync-settings': () => settings,
      'PUT /private-notes/sync-settings': (call) => {
        const body = call.body as { targetFolders: string[] };
        settings = {
          targetFolders: body.targetFolders.map((path) => ({
            path,
            noteCount: 0,
            lastSyncAt: null,
          })),
          updatedAt: new Date().toISOString(),
        };
        return settings;
      },
    },
  });

  await page.goto('/my/obsidian');
  const panel = page.getByRole('region', { name: '同期対象範囲' });

  // ★ 陽性対照: フォルダの一覧（パス・配下の資料数・最終同期）が出る（主要素 3）。
  await expect(panel.getByRole('cell', { name: '仕事/メモ' })).toBeVisible();
  await expect(panel.getByRole('cell', { name: '12' })).toBeVisible();
  // 05_screens §SC-20 主要素 3: 業務関連資料の固定文言は**この区画の中**にある。
  await expect(panel.getByText('同期した資料は業務関連資料として扱われます。')).toBeVisible();

  await panel
    .getByRole('row', { name: /仕事\/メモ/ })
    .getByRole('button', { name: '対象フォルダから外す' })
    .click();

  // 🔴 「外す」と「削除する」を読み分けられること（ADR-0037 決定 4）。
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('これは削除ではありません');
  await expect(dialog).toContainText('フォルダ配下の資料はサーバに残り');
  await dialog.getByRole('button', { name: '対象から外す' }).click();

  // ★ 陽性対照: 外した 1 件を除いた**全量**が送られ、一覧へ反映される。
  await expect(panel.getByRole('cell', { name: '仕事/メモ' })).toHaveCount(0);
  await expect(panel.getByRole('cell', { name: '日誌' })).toBeVisible();
  const put = traffic.calls.find((c) => c.key === 'PUT /private-notes/sync-settings');
  expect(put?.body).toEqual({ targetFolders: ['日誌'] });
  // ★ 陰性対照: 資料を消す口は 1 つも叩いていない。
  expect(traffic.calls.map((c) => c.key)).not.toContain('DELETE /private-notes/note-1');

  expectBffTrafficIsComplete(traffic);
});

test('SC-20/UC-11 (#1442): a conflict is resolved by the person from three explicit choices', async ({
  page,
}) => {
  let conflicts: SyncConflictSummaryDto[] = [conflict()];
  const detail: SyncConflictDetailDto = {
    ...conflict(),
    localContent: '端末で書いた本文',
    serverContent: 'サーバに保存されている本文',
  };
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes/devices': [device()],
      'GET /private-notes/sync-settings': EMPTY_SETTINGS,
      'GET /private-notes/conflicts': () => conflicts,
      'GET /private-notes/conflicts/conflict-1': detail,
      'POST /private-notes/conflicts/conflict-1/resolve': () => {
        conflicts = [];
        return {
          conflictId: 'conflict-1',
          noteId: 'note-1',
          resolution: 'local',
          noteVersion: 6,
          createdNoteId: null,
        };
      },
    },
  });

  await page.goto('/my/obsidian');
  const panel = page.getByRole('region', { name: '同期の競合' });

  // ★ 陽性対照: 未解決の競合が一覧に出る（主要素 5）。
  // タイトルとパスは同じ語を含むので `exact` で引き分ける（部分一致だと 2 セルに当たる）。
  await expect(panel.getByRole('cell', { name: '設計メモ', exact: true })).toBeVisible();
  await expect(panel.getByRole('cell', { name: '仕事/メモ/設計メモ.md' })).toBeVisible();
  await expect(panel.getByText('ローカル 3 版 ／ サーバ 5 版')).toBeVisible();
  // ★ 陰性対照 1: 一覧の応答は本文を運ばない（運ぶと「詳細だけが返す」が嘘になる）。
  await expect(page.getByText('端末で書いた本文')).toHaveCount(0);

  await panel.getByRole('button', { name: '本文を見て解決する' }).click();

  // 2 ペイン（ローカル版／サーバ版）を並べて読める。
  const resolution = page.getByRole('region', { name: '競合の解決' });
  await expect(resolution.getByLabel('ローカル版の本文')).toContainText('端末で書いた本文');
  await expect(resolution.getByLabel('サーバ版の本文')).toContainText('サーバに保存されている本文');

  // ★ 陰性対照 2: **自動解決の択を置かない**（ADR-0037 決定 7）。3 択が在ることと対で読む。
  await expect(resolution.getByRole('button', { name: 'ローカルを採用' })).toBeVisible();
  await expect(resolution.getByRole('button', { name: 'サーバを採用' })).toBeVisible();
  await expect(resolution.getByRole('button', { name: '両方を残す（別名保存）' })).toBeVisible();
  await expect(page.getByRole('button', { name: /自動|後勝ち/ })).toHaveCount(0);

  await resolution.getByRole('button', { name: 'ローカルを採用' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('端末の本文が新しい版になります');
  await dialog.getByRole('button', { name: 'ローカルを採用する' }).click();

  // ★ 陽性対照: 解決すると一覧から消え、択が要求として届く。
  await expect(page.getByText('競合はありません。')).toBeVisible();
  const resolved = traffic.calls.find(
    (c) => c.key === 'POST /private-notes/conflicts/conflict-1/resolve',
  );
  expect(resolved?.body).toEqual({ resolution: 'local' });

  expectBffTrafficIsComplete(traffic);
});
