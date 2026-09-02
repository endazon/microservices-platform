import { test, expect } from '@playwright/test';
import type { SyncDeviceDto } from '../src/lib/api/generated/bff.schemas';
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
    handlers: { 'GET /private-notes/devices': [device()] },
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
