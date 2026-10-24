import { test, expect } from './support/cspGuard';
import type { Request } from '@playwright/test';
import type {
  AttributeValuesResponse,
  PrivateNoteDto,
  PrivateNoteListResponse,
} from '../src/lib/api/generated/bff.schemas';
import {
  installBffSession,
  sessionUser,
  expectBffTrafficIsComplete,
  reply,
} from './support/bffSession';
import {
  NAV_ADMIN,
  NAV_NO_ROLE,
  NAV_OPERATOR,
  readNav,
  type NavTable,
} from './support/navExpectations';

// NFR, ADR-0031, ADR-0032, IADR-0035, IADR-0337 (#2016 / E-11): **共通シェルの横断規約**を実ブラウザで固定する。
// 網羅表（#2013）の M-011（CSRF ヘッダ）・M-012（左ナビの出し分け）・M-014（待ち・失敗・再試行）・
// M-015（画面単位のエラー境界）。各画面の spec は「自分の画面」を見るので、**どの画面にも属さない規約**はここに置く。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。

const attributeValues: AttributeValuesResponse = { values: [] };

// ───────────────────────────── M-012: 左ナビの出し分け ─────────────────────────────
// 期待値の表と読み方は `support/navExpectations.ts`（live の `live/shell.ro.spec.ts` と共有する）。
const EXPECTED_NAV: Record<string, { roles: string[]; nav: NavTable }> = {
  'ロール無し（一般利用者）': { roles: [], nav: NAV_NO_ROLE },
  運用者: { roles: ['platform-operator'], nav: NAV_OPERATOR },
  システム管理者: { roles: ['platform-admin'], nav: NAV_ADMIN },
};

for (const [role, { roles, nav }] of Object.entries(EXPECTED_NAV)) {
  test(`M-012: ${role}の左ナビは、計画の役割ごとの項目の集合と並びに一致する（SC-03 は無く、空のグループは見出しごと出ない）`, async ({
    page,
  }) => {
    const traffic = await installBffSession(page, {
      user: sessionUser(roles),
      handlers: { 'POST /attribute-values': attributeValues },
    });

    await page.goto('/ask');
    await expect(
      page.getByRole('heading', { name: 'ナレッジ検索・AI質問', level: 1 }),
    ).toBeVisible();
    const navigation = page.getByRole('navigation', { name: '主要ナビゲーション' });
    // ナビが描き終わってから読む（グループの数が期待と一致するまで待つ）。
    await expect(navigation.getByRole('heading', { level: 2 })).toHaveCount(
      Object.keys(nav).length,
    );

    // ★ 本体: グループの集合と、各グループの「表示名 → 遷移先」の集合が一致する。
    const actual = await readNav(page);
    expect(actual).toEqual(nav);
    // ★ 並び（SC-05, SC-09, SC-12 / #2189）: `toEqual` は鍵の順を比べないので、別に比べる。
    // 05_screens §共通シェル の並び（グループは 利用者 → 個人 → 管理 → 運用、「管理」の中は
    // … ABAC設定 → ユーザー管理 → MCP管理）。支援技術が読む順序（アクセシビリティツリー）で比べる。
    expect(Object.keys(actual), 'グループの並び').toEqual(Object.keys(nav));
    for (const [group, items] of Object.entries(nav)) {
      expect(Object.keys(actual[group] ?? {}), `「${group}」グループ内の並び`).toEqual(
        Object.keys(items),
      );
    }

    // ★ 陰性対照: 文書詳細（SC-03）はナビに無い。可変機能ユニットのグループも、そのロールが無ければ出ない。
    await expect(navigation.getByRole('link', { name: /文書詳細/ })).toHaveCount(0);
    await expect(navigation.getByRole('heading', { name: '株式自動売買' })).toHaveCount(0);

    expectBffTrafficIsComplete(traffic);
  });
}

// ───────────────────────── M-014: 待ち（status）・失敗（alert）・再試行 ─────────────────────────

function note(): PrivateNoteDto {
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
    visibility: 'private',
    sharedUserCount: 0,
    sharedGroupCount: 0,
    syncState: 'target',
    tags: [],
  };
}

const GB = 1024 ** 3;
const noteList: PrivateNoteListResponse = {
  usage: { usedBytes: Math.round(0.01 * GB), limitBytes: GB, percent: 1 },
  notes: [note()],
};

test('M-014: 取得中は role="status"、失敗は role="alert" と再試行ボタンで示し、押すと同じ取得を 1 回やり直して復帰する', async ({
  page,
}) => {
  // 5xx は自動で 1 回だけ再送される（`shouldRetryQuery`。MAX_QUERY_RETRIES = 1）。そこで**最初の 2 回**を 500 にして
  // 失敗の表示まで持ち込み、3 回目（＝利用者が押した再試行）から本体を返す。
  let attempts = 0;
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes': () => {
        attempts += 1;
        return attempts <= 2 ? reply(500, { detail: 'boom' }) : noteList;
      },
    },
  });
  // 1 回目の応答を止め、待ちの表示（role="status"）を観測してから流す。`installBffSession` の後に張った
  // route が先に当たり、`fallback()` で土台へ渡す（土台の記録・応答はそのまま使う）。
  let release: () => void = () => {};
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let held = false;
  await page.route('**/bff/private-notes', async (route) => {
    if (!held) {
      held = true;
      await gate;
    }
    await route.fallback();
  });

  await page.goto('/my/notes');

  // ★ 待ち: 「読み込み中…」を role="status" で示す（回転だけの印に意味を持たせない）。
  await expect(page.getByRole('status').filter({ hasText: '読み込み中' })).toBeVisible();
  release();

  // ★ 失敗: role="alert" ＋ 何が起きたか ＋ 再試行ボタン。失敗時に空状態を出さない（判定順は 失敗 → 待ち → 空 → 本体）。
  const alert = page.getByRole('alert');
  await expect(alert).toContainText('個人資料の一覧を取得できませんでした。');
  await expect(page.getByText('個人資料がまだありません。')).toHaveCount(0);
  // 自動の再送は 5xx に対して 1 回だけ（初回 ＋ 1 回 = 2 回）。
  expect(attempts).toBe(2);

  // ★ 再試行: 押すと同じ取得を 1 回だけやり直し、本体へ復帰する。
  await alert.getByRole('button', { name: '再試行' }).click();
  await expect(page.getByRole('cell', { name: '設計メモ' })).toBeVisible();
  await expect(page.getByRole('alert')).toHaveCount(0);
  expect(attempts).toBe(3);

  // ★ 共通シェルは失敗の間も残る（失敗は画面の中の表示であり、ナビ・ヘッダを巻き込まない）。
  await expect(page.getByRole('navigation', { name: '主要ナビゲーション' })).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

for (const { status, retried } of [
  { status: 404, retried: false },
  { status: 403, retried: false },
  { status: 408, retried: true },
  { status: 429, retried: true },
]) {
  test(`M-014: ${status} の失敗は${retried ? '自動で 1 回だけ再送する' : '自動で再送しない'}（4xx は再送せず、408・429 だけ例外）`, async ({
    page,
  }) => {
    let attempts = 0;
    const traffic = await installBffSession(page, {
      user: sessionUser([]),
      handlers: {
        'GET /private-notes': () => {
          attempts += 1;
          return reply(status, { detail: 'stub' });
        },
      },
    });

    await page.goto('/my/notes');
    await expect(page.getByRole('alert')).toContainText('個人資料の一覧を取得できませんでした。');
    // 失敗の表示が出た時点で、自動の再送はもう終わっている（再送を待ってから失敗を描く）。
    expect(attempts).toBe(retried ? 2 : 1);
    await expect(page.getByRole('alert').getByRole('button', { name: '再試行' })).toBeVisible();

    expectBffTrafficIsComplete(traffic);
  });
}

// ───────────────────────────── M-015: 画面単位のエラー境界 ─────────────────────────────

test('M-015: 契約に反した応答で画面が落ちても、共通シェルは残り、「再読み込み」「ホームへ戻る」が出る', async ({
  page,
}) => {
  let broken = true;
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      // 🔴 契約違反の本文（`notes` が配列でない）。画面の描画が例外を投げる形を作る。
      'GET /private-notes': () =>
        broken ? { usage: noteList.usage, notes: 'not-an-array' } : noteList,
      'POST /attribute-values': attributeValues,
    },
  });
  const pageErrors: string[] = [];
  page.on('pageerror', (e) => pageErrors.push(e.message));

  await page.goto('/my/notes');

  // ★ 画面の位置（main の中）にフォールバックが出る（role="alert"・何が起きたか・2 つの次の一手）。
  const fallback = page.getByRole('main').getByRole('alert');
  await expect(fallback).toContainText('予期しないエラーが発生しました。');
  await expect(fallback.getByRole('button', { name: '再読み込み' })).toBeVisible();
  await expect(fallback.getByRole('link', { name: 'ホームへ戻る' })).toBeVisible();
  // ★ 1 画面のクラッシュが全体を落とさない: 左ナビとヘッダ（アカウント設定）は残る。
  await expect(page.getByRole('navigation', { name: '主要ナビゲーション' })).toBeVisible();
  await expect(page.getByRole('link', { name: /アカウント設定/ })).toBeVisible();

  // ★ 再読み込み: 応答が直れば、その画面へ戻る。
  broken = false;
  await fallback.getByRole('button', { name: '再読み込み' }).click();
  await expect(page.getByRole('cell', { name: '設計メモ' })).toBeVisible();

  // ★ ホームへ戻る: もう一度落としてから押すと、主入口（SC-01）へ遷移する。
  broken = true;
  await page.reload();
  await expect(fallback).toContainText('予期しないエラーが発生しました。');
  await fallback.getByRole('link', { name: 'ホームへ戻る' }).click();
  await expect(page).toHaveURL(/\/ask(\?|$)/);
  await expect(page.getByRole('heading', { name: 'ナレッジ検索・AI質問', level: 1 })).toBeVisible();

  // 境界が捕まえた例外はページの未捕捉の例外にならない（捕まえていなければ pageerror が出る）。
  expect(pageErrors).toEqual([]);

  expectBffTrafficIsComplete(traffic);
});

// ───────────────────────────── M-011: CSRF ヘッダ ─────────────────────────────

test('M-011: 状態を変える要求（POST）は X-MSP-CSRF ヘッダを付けて /bff へ送る', async ({
  page,
}) => {
  let notes: PrivateNoteDto[] = [];
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes': () => ({ ...noteList, notes }),
      'POST /private-notes': (call) => {
        const body = call.body as { title: string };
        notes = [{ ...note(), id: 'note-new', title: body.title }];
        return notes[0];
      },
    },
  });
  // 要求の見出しは BffCall に載らないので、ブラウザが送った要求そのものを観測する。
  const bffRequests: Request[] = [];
  page.on('request', (r) => {
    if (new URL(r.url()).pathname.startsWith('/bff/')) bffRequests.push(r);
  });

  await page.goto('/my/notes');
  await expect(page.getByText('個人資料がまだありません。')).toBeVisible();
  await page.locator('#note-title').fill('新しい設計メモ');
  await page.getByRole('button', { name: '作成する' }).click();
  await expect(page.getByRole('cell', { name: '新しい設計メモ' })).toBeVisible();

  const posts = bffRequests.filter((r) => r.method() === 'POST');
  // ★ 陽性対照: POST を実際に観測した（0 件なら下の every が空振りで緑になる）。
  expect(posts.map((r) => new URL(r.url()).pathname)).toEqual(['/bff/private-notes']);
  // ★ 本体: すべての POST に X-MSP-CSRF が付く（値に意味は無いが、空でない）。
  for (const r of posts) {
    expect((await r.allHeaders())['x-msp-csrf'], r.url()).toBeTruthy();
  }
  // 同一オリジンの相対パスへ送る（絶対 URL・別オリジンへは送らない）。
  for (const r of bffRequests) expect(new URL(r.url()).origin).toBe(new URL(page.url()).origin);

  expectBffTrafficIsComplete(traffic);
});
