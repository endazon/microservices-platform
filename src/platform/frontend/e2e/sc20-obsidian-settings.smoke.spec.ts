import { test, expect } from './support/cspGuard';
import type {
  SyncConflictDetailDto,
  SyncConflictSummaryDto,
  SyncDeviceDto,
  SyncHistoryEntryDto,
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
const EMPTY_SETTINGS: SyncSettingsDto = {
  targetFolders: [],
  updatedAt: null,
  aiInputDefault: false,
  syncPaused: false,
};

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

// #1446: 同期履歴（主要素 6）。**画面を開いた時点で引く**ので、応答を用意しない spec は
// `expectBffTrafficIsComplete` で落ちる。
function historyEntry(overrides: Partial<SyncHistoryEntryDto> = {}): SyncHistoryEntryDto {
  return {
    id: 'history-1',
    occurredAt: new Date(Date.now() - DAY).toISOString(),
    deviceName: '自宅 PC',
    direction: 'push',
    added: 0,
    updated: 0,
    deleted: 0,
    conflicted: 1,
    outcome: 'failure',
    failureReason: 'version_conflict',
    ...overrides,
  };
}

/** 新しい区画が既定で引く 3 面（中身を見ない spec 用の空応答）。 */
const QUIET_SYNC_PANELS = {
  'GET /private-notes/sync-settings': EMPTY_SETTINGS,
  'GET /private-notes/conflicts': [] as SyncConflictSummaryDto[],
  'GET /private-notes/sync-history': [] as SyncHistoryEntryDto[],
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
    aiInputDefault: false,
    syncPaused: false,
  };
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes/devices': [device()],
      'GET /private-notes/conflicts': [],
      'GET /private-notes/sync-history': [],
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
          aiInputDefault: settings.aiInputDefault,
          syncPaused: false,
        };
        return settings;
      },
    },
  });

  await page.goto('/my/obsidian');
  const panel = page.getByRole('region', { name: '同期対象範囲' });

  // ★ 陽性対照: フォルダの一覧（パス・配下の資料数・最終同期）が出る（主要素 3）。
  await expect(panel.getByRole('cell', { name: '仕事/メモ' })).toBeVisible();
  await expect(panel.getByRole('cell', { name: '12', exact: true })).toBeVisible();
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
    localContentUnavailable: false,
    serverContentUnavailable: false,
    localContentMissing: false,
  };
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes/devices': [device()],
      'GET /private-notes/sync-settings': EMPTY_SETTINGS,
      'GET /private-notes/sync-history': [],
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

test('SC-20 (#1446): the sync history lists a failed sync with what to do next, and never names the note', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes/devices': [device()],
      'GET /private-notes/sync-settings': EMPTY_SETTINGS,
      'GET /private-notes/conflicts': [] as SyncConflictSummaryDto[],
      // ADR-0099 決定 6: **失敗も記録する**（記録しなければ主要素 6 が成立しない）。
      'GET /private-notes/sync-history': [historyEntry()],
    },
  });

  await page.goto('/my/obsidian');
  const panel = page.getByRole('region', { name: '同期履歴' });

  // ★ 陽性対照: 1 行が実行日時・端末・方向・内訳・結果・失敗理由で読める。
  await expect(panel.getByRole('cell', { name: '自宅 PC' })).toBeVisible();
  await expect(panel.getByRole('cell', { name: '送信' })).toBeVisible();
  await expect(panel.getByRole('cell', { name: '競合 1' })).toBeVisible();
  // 結果は色だけに頼らない（文言が残る）。
  await expect(panel.getByRole('cell', { name: '失敗' })).toBeVisible();
  // 🔴 コードそのまま（`version_conflict`）ではなく、次の行動を指す文言を出す。
  await expect(
    panel.getByRole('cell', { name: /上の「同期の競合」から解決してください/ }),
  ).toBeVisible();
  await expect(panel.getByText('version_conflict')).toHaveCount(0);

  // ★ 陰性対照: ADR-0099 決定 5 —— タイトル・パスの列を持たない（端末の列が在ることと対で読む）。
  await expect(panel.getByRole('columnheader', { name: '端末' })).toBeVisible();
  await expect(panel.getByRole('columnheader', { name: 'タイトル' })).toHaveCount(0);
  await expect(panel.getByRole('columnheader', { name: 'パス' })).toHaveCount(0);

  // 表示件数と保持期間を同じ枠で告げる（50 件より前が消えたと読ませない）。
  await expect(
    panel.getByText(
      '直近 50 件を表示します。記録は 3 年間保持されます。資料の名前は履歴に残しません。',
    ),
  ).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('SC-20 (#1447): an empty history reads differently once a device has synced before', async ({
  page,
}) => {
  // ADR-0099 §残るもの の帰結: 履歴は**本機能の配備後の同期から**残る。配備前に同期していた
  // 利用者には、0 件が「記録されていない」のか「同期していない」のか区別できない ——
  // **同じ 0 件に 2 つの意味がある**ので、端末の最終同期で読み分ける。
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      // `device()` は最終同期を持つ（＝配備前に同期していた利用者）。履歴は空である。
      'GET /private-notes/devices': [device()],
      ...QUIET_SYNC_PANELS,
    },
  });

  await page.goto('/my/obsidian');
  const panel = page.getByRole('region', { name: '同期履歴' });

  // ★ 陽性対照: 題は共通（0 件であること自体は同じ事実である）。
  await expect(panel.getByText('同期の記録はまだありません。')).toBeVisible();
  await expect(
    panel.getByText(
      '同期履歴の記録は本機能の配備後の同期から残ります。配備前の同期は表示されません。',
    ),
  ).toBeVisible();
  // 🔴 陰性対照: 「まだ同期していない」と読める従前の案内は出さない（過去に同期しているため誤り）。
  await expect(panel.getByText(/ここに結果が並びます/)).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('SC-20 (#1447): a device that never synced keeps the original empty-state guidance', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes/devices': [device({ lastSyncAt: null })],
      ...QUIET_SYNC_PANELS,
    },
  });

  await page.goto('/my/obsidian');
  const panel = page.getByRole('region', { name: '同期履歴' });

  await expect(
    panel.getByText('Obsidian プラグインから同期すると、ここに結果が並びます。'),
  ).toBeVisible();
  // 🔴 陰性対照: 同期していない利用者へ「配備前の同期」の話をしない（無用な不安を与える）。
  await expect(panel.getByText(/配備前の同期は表示されません/)).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

// ───────────── M-065（#2021 / E-16）: 端末の期限の描き分け ─────────────
//
// 期待値は計画 05_screens §SC-20 主要素 1・2 から起こした。
// - 端末の状態は 有効（残り日数）／期限切れ間近（残り 7 日以内）／期限切れ／失効 の 4 つを**区別して**出す。
//   期限切れは「同期が既に停止している」ことを意味する。期限切れの端末からは再発行の導線を置く。
// - 期限切れ後もプラグイン設定に古いトークンが残る旨の固定文言を置く。
// 🔴 **同期の一時停止／再開（主要素 7）は本 PR の対象外である。** 公開 API に呼ぶ口が無く、実装は「置かない」を
//    選んでいる（画面仕様書 SC-20 §未決事項 2）。契約の追加が要るため、作業仕様書と PR 本文に理由を書いた。

test('M-065: 端末は 有効・期限切れ間近（7 日以内）・期限切れ・失効 を文言で描き分け、期限切れからは再発行できる', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      ...QUIET_SYNC_PANELS,
      'GET /private-notes/devices': [
        device({
          id: 'd-ok',
          deviceName: '自宅 PC',
          expiresAt: new Date(Date.now() + 20 * DAY).toISOString(),
        }),
        device({
          id: 'd-soon',
          deviceName: '会社の PC',
          expiresAt: new Date(Date.now() + 6.5 * DAY).toISOString(),
        }),
        device({
          id: 'd-edge',
          deviceName: '境界の PC',
          expiresAt: new Date(Date.now() + 7.5 * DAY).toISOString(),
        }),
        device({
          id: 'd-expired',
          deviceName: '古いタブレット',
          expiresAt: new Date(Date.now() - DAY).toISOString(),
          active: false,
        }),
        device({ id: 'd-revoked', deviceName: 'なくしたスマホ', revoked: true, active: false }),
      ],
    },
  });

  await page.goto('/my/obsidian');
  const rowOf = (name: string) => page.getByRole('row').filter({ hasText: name });
  await expect(rowOf('自宅 PC').getByText('有効（残り 20 日）')).toBeVisible();
  // ★ 残り 7 日以内は「期限切れ間近」。7 日を超えれば「有効」のまま（境界の両側）。
  await expect(rowOf('会社の PC').getByText('期限切れ間近（残り 7 日）')).toBeVisible();
  await expect(rowOf('境界の PC').getByText('有効（残り 8 日）')).toBeVisible();
  // ★ 期限切れは同期が止まっていることを言い、同じ行から再発行できる。
  await expect(rowOf('古いタブレット').getByText('期限切れ（同期は停止しています）')).toBeVisible();
  await expect(rowOf('古いタブレット').getByRole('button', { name: '再発行する' })).toBeVisible();
  // 失効は期限より優先する（失効した端末に期限の文言を出さない）。再発行も出さない。
  await expect(rowOf('なくしたスマホ').getByText('失効済み')).toBeVisible();
  await expect(rowOf('なくしたスマホ').getByText(/残り|期限切れ/)).toHaveCount(0);
  await expect(rowOf('なくしたスマホ').getByRole('button', { name: '再発行する' })).toHaveCount(0);

  // 固定文言: 期限切れ後もプラグイン設定には古いトークンが残る。
  await expect(page.getByText(/Obsidian\s*プラグインの設定には残ったままです。/)).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

// FR-20, SC-20 主要素 7, 計画 ADR-0145 決定 5, IADR-0668 (#2379): 同期の一時停止・再開は利用者単位の 1 つの操作で、
// 停止中の拒否は同期履歴の 1 行（すべての端末・拒否回数・最初〜最後）に出る。
test('SC-20 (#2379): pausing and resuming sync is one per-user control, and the paused period shows as one history row', async ({
  page,
}) => {
  let settings: SyncSettingsDto = { ...EMPTY_SETTINGS };
  const pausedRow = historyEntry({
    id: 'history-paused',
    deviceName: '',
    direction: '',
    conflicted: 0,
    failureReason: 'sync_paused',
    rejectedCount: 3,
    firstRejectedAt: new Date(Date.now() - 2 * DAY).toISOString(),
    lastRejectedAt: new Date(Date.now() - DAY).toISOString(),
  });
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes/devices': [device()],
      'GET /private-notes/conflicts': [],
      'GET /private-notes/sync-history': [pausedRow],
      'GET /private-notes/sync-settings': () => settings,
      'POST /private-notes/sync-settings/pause': () => {
        settings = { ...settings, syncPaused: true, syncPausedAt: new Date().toISOString() };
        return settings;
      },
      'POST /private-notes/sync-settings/resume': () => {
        settings = { ...settings, syncPaused: false, syncPausedAt: null };
        return settings;
      },
    },
  });

  await page.goto('/my/obsidian');
  const panel = page.getByRole('region', { name: '同期の状態' });

  await expect(panel.getByText('同期: 有効')).toBeVisible();
  await expect(panel).toContainText('すべての端末の同期が止まります');
  await expect(panel).toContainText('トークンは残り、再開すれば同期が戻ります');
  // 🔴 操作は利用者単位で 1 つ。端末の行には無い（陽性対照: 区画には在る）。
  await expect(page.getByRole('button', { name: /一時停止/ })).toHaveCount(1);

  await panel.getByRole('button', { name: '一時停止する' }).click();
  await expect(panel.getByText(/^同期: 一時停止中/)).toBeVisible();
  await panel.getByRole('button', { name: '再開する' }).click();
  await expect(panel.getByText('同期: 有効')).toBeVisible();

  const keys = traffic.calls.map((c) => c.key);
  expect(keys).toContain('POST /private-notes/sync-settings/pause');
  expect(keys).toContain('POST /private-notes/sync-settings/resume');

  // 停止期間の 1 行: すべての端末・拒否 3 回・再開の案内（未知の理由コードとして描かない）。
  const history = page.getByRole('region', { name: '同期履歴' });
  const row = history.getByRole('row', { name: /すべての端末/ });
  await expect(row).toContainText('拒否 3 回');
  await expect(row).toContainText('同じトークンのまま同期が戻ります');
  await expect(row).not.toContainText('理由: sync_paused');

  expectBffTrafficIsComplete(traffic);
});
