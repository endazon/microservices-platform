import { test, expect } from './support/cspGuard';
import type {
  DocumentDto,
  DocumentListPageDto,
  TagDictionaryResponse,
} from '../src/lib/api/generated/bff.schemas';
import type { Page } from '@playwright/test';
import {
  installBffSession,
  sessionUser,
  expectBffTrafficIsComplete,
  reply,
} from './support/bffSession';

// SC-05 (#503 / #1139): 文書管理（`/admin/documents`）のスクリーンレベル・スモーク。
//
// 🔴 **本画面は platform-admin / platform-operator 限定である**（IADR-0039）。
// 権限外では `RequireRole` が `NotFound` を描き、画面の存在を示さない（存在秘匿。IADR-0009）。
// **この出し分けは未認証のスモークでは 1 度も踏まれない** —— ロールを与えて初めて分岐する。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない。** 未知パスの受け皿（catchAllRoute）は
// `RequireAuth` 配下に居るため、ルートを消しても未認証なら同じく `/login` へ行く（#918 の実測）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝これは契約の写しであって後段ではない）は `support/bffSession.ts`。
// 一覧の中身・並び・エラー状態は Vitest（単体）が引き続き担う。

const doc: DocumentDto = {
  id: 'doc-1',
  title: '経費精算マニュアル',
  status: 'published',
  version: 3,
  attributes: { department: 'sales' },
  tags: ['tag-1'],
  createdAt: '2026-08-01T00:00:00Z',
  updatedAt: '2026-08-02T00:00:00Z',
};

const tags: TagDictionaryResponse = { tags: [{ id: 'tag-1', name: '経費', usageCount: 1 }] };

// FR-06, IADR-0537（#1923）: 一覧は 1 ページ（`items` / `total` / `offset` / `limit`）で返る。
// キーはクエリを含まない（`support/bffSession.ts`）。要求の `offset` / `limit` は下の本体で確かめる。
// ［2026-10-15 / #1923 段 2］絞り込み前の件数と状態別の件数も返る（IADR-0545）。
const firstPage: DocumentListPageDto = {
  items: [doc],
  total: 1,
  offset: 0,
  limit: 50,
  unfilteredTotal: 1,
  statusCounts: { published: 1 },
  // ［2026-10-17 / #1923・IADR-0561］データソース別・プロジェクト別の件数（手入力の 1 件）。
  sourceCounts: { manual: 1 },
  projectCounts: { '(none)': 1 },
  // ［2026-10-20 / #1993・IADR-0565］ページの文書の索引の状態。
  indexStatuses: { 'doc-1': 'indexed' },
};

const handlers = { 'GET /documents': firstPage, 'GET /tags': tags };

test('unauthenticated visit to /admin/documents redirects to /login', async ({ page }) => {
  await page.goto('/admin/documents');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-05: an operator reaches the screen and its navigation entry', async ({ page }) => {
  const traffic = await installBffSession(page, {
    // 🔴 **運用者で測る。** 管理者だけで測ると `anyOf` から operator が落ちても気づけない。
    user: sessionUser(['platform-operator']),
    handlers,
  });

  await page.goto('/admin/documents');

  // ★ 陽性対照: 画面が描かれ、左ナビ「文書管理」も出る（05_screens §共通シェル）。
  await expect(page.getByRole('heading', { name: '文書一覧', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: '文書管理' })).toBeVisible();
  await expect(page.getByRole('cell', { name: '経費精算マニュアル' })).toBeVisible();

  // ★ 陰性対照: 空状態の文言は**行があるときに出てはならない**
  // （取得できた一覧を 0 件表示へ寄せる実装がここで落ちる）。
  // **［2026-09-12］`EmptyState` への統一で文言が変わった**（空は次の一手を示す）。
  await expect(page.getByText('文書はまだありません。')).toHaveCount(0);
  // 一覧はページの引数つきで引く（既定 50 件の先頭）。総件数と表示範囲が出る。
  // ［2026-10-17 / #1923・IADR-0561］既定のタブは「人の文書・報告書」（`origin=curated`）。
  expect(traffic.calls.find((c) => c.key === 'GET /documents')?.search).toBe(
    '?offset=0&limit=50&origin=curated',
  );
  await expect(page.getByText('絞り込み 1 件（全 1 件）中 1–1 件')).toBeVisible();
  await expect(page.getByRole('button', { name: '人の文書・報告書（1 件）' })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
  await expect(page.getByRole('columnheader', { name: 'データソース' })).toBeVisible();
  // #1993（IADR-0565）: 索引の状態の列（BFF がページ分を取り込み側に引いた値）。
  await expect(page.getByRole('columnheader', { name: '索引' })).toBeVisible();
  await expect(page.getByRole('cell', { name: '索引済み' })).toBeVisible();
  // #1923 段 2（IADR-0545）: 絞り込みの帯（表題の検索・状態・並べ替え）と本文・更新日時の列が出る。
  const filters = page.getByRole('search', { name: '文書の絞り込み' });
  await expect(filters.getByLabel('表題で検索')).toBeVisible();
  await expect(filters.getByLabel('状態')).toBeVisible();
  await expect(filters.getByLabel('並べ替え')).toBeVisible();
  // ［2026-10-17 / IADR-0561］データソース・機密区分・タグ・プロジェクト・保存日の絞り込みも帯に並ぶ。
  await expect(filters.getByLabel('データソース')).toBeVisible();
  await expect(filters.getByLabel('保存日（から）')).toBeVisible();
  await expect(page.getByRole('columnheader', { name: '本文' })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: '更新日時' })).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('SC-05: a user with no administrative role gets the same not-found page', async ({ page }) => {
  // ★ 陰性対照: **応答を 1 つも用意しない。** 管理端点を呼んでしまえば `unhandled` に載り、
  // 下の `expectBffTrafficIsComplete` が落ちる —— 「権限が無いのに取りに行った」を検出する。
  const traffic = await installBffSession(page, { user: sessionUser([]) });

  await page.goto('/admin/documents');

  // IADR-0009: 不在も権限による秘匿も同じ画面で応答する。
  await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
  await expect(page.getByRole('heading', { name: '文書一覧' })).toHaveCount(0);
  // 左ナビにも項目が出ない（出た時点で「その画面がある」ことが漏れる）。
  await expect(page.getByRole('link', { name: '文書管理' })).toHaveCount(0);

  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /documents');
  expectBffTrafficIsComplete(traffic);
});

// ───────────── M-072〜M-074（#2019 / E-14）: 作成フォームの検証・admin と operator の境界・413 ─────────────
//
// 期待値は計画 05_screens §SC-05（タイトル必須・機密区分は定義済み区分のみ・doc_scope は作成時のみで編集不可・
// タグは辞書の値のみ・公開／アーカイブ／削除は管理者限定（「管理系 3 画面の閲覧ロール」の破壊的操作の列挙））と
// FR-21（1 MB 超は 413）から起こした。

const draftDoc: DocumentDto = {
  ...doc,
  id: 'doc-2',
  title: '出張旅費規程（案）',
  status: 'draft',
  version: 1,
  attributes: { confidentiality: 'internal', doc_scope: 'organization' },
  tags: [],
};
const twoDocs: DocumentListPageDto = {
  ...firstPage,
  items: [doc, draftDoc],
  total: 2,
  unfilteredTotal: 2,
  statusCounts: { published: 1, draft: 1 },
  sourceCounts: { manual: 2 },
  projectCounts: { '(none)': 2 },
  indexStatuses: { 'doc-1': 'indexed', 'doc-2': 'indexed' },
};
const rowOf = (page: Page, title: string) =>
  page.getByRole('table', { name: '文書の一覧' }).getByRole('row').filter({ hasText: title });
const DESTRUCTIVE = ['編集', '公開', 'アーカイブ', '削除'];

test('M-073: 運用者には文書の登録・編集・公開・アーカイブ・削除のボタンが 1 つも無く、管理者限定の旨を示す', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: { 'GET /documents': twoDocs, 'GET /tags': tags },
  });

  await page.goto('/admin/documents');
  // 陽性対照: 一覧は描けている（閲覧は運用者にも開く）。
  await expect(rowOf(page, '出張旅費規程（案）')).toBeVisible();
  for (const name of [...DESTRUCTIVE, '＋ 新規登録']) {
    await expect(page.getByRole('button', { name, exact: true })).toHaveCount(0);
  }
  await expect(page.getByRole('columnheader', { name: '操作' })).toHaveCount(0);
  await expect(page.getByRole('form', { name: '文書登録' })).toHaveCount(0);
  await expect(
    page.getByText('文書の登録・編集・公開・アーカイブ・削除は管理者のみ実行できます'),
  ).toBeVisible();
  expect(traffic.calls.filter((c) => c.method !== 'GET')).toEqual([]);

  expectBffTrafficIsComplete(traffic);
});

test('M-073: 管理者は下書きを公開でき（draft → active）、公開済みには公開を出さずアーカイブを出す', async ({
  page,
}) => {
  let published = false;
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /documents': () =>
        published ? { ...twoDocs, items: [doc, { ...draftDoc, status: 'published' }] } : twoDocs,
      'GET /tags': tags,
      'POST /documents/doc-2/publish': () => {
        published = true;
        return { ...draftDoc, status: 'published' };
      },
    },
  });

  await page.goto('/admin/documents');
  const draft = rowOf(page, '出張旅費規程（案）');
  const live = rowOf(page, '経費精算マニュアル');
  for (const name of DESTRUCTIVE) {
    await expect(draft.getByRole('button', { name, exact: true })).toBeVisible();
  }
  // ★ 公開済みには「公開」を出さない（誤再公開を防ぐ）。アーカイブ・削除は出る。
  await expect(live.getByRole('button', { name: '公開', exact: true })).toHaveCount(0);
  await expect(live.getByRole('button', { name: 'アーカイブ', exact: true })).toBeVisible();

  await draft.getByRole('button', { name: '公開', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: '文書を公開しました。' })).toBeVisible();
  await expect(draft.getByRole('button', { name: '公開', exact: true })).toHaveCount(0);
  expect(traffic.calls.filter((c) => c.method !== 'GET').map((c) => c.key)).toEqual([
    'POST /documents/doc-2/publish',
  ]);

  expectBffTrafficIsComplete(traffic);
});

test('M-072: 作成フォームはタイトルが空なら保存できず、機密区分は定義済みの 4 区分だけ、タグは辞書の値だけを選べ、doc_scope の欄を持たない', async ({
  page,
}) => {
  let createdBody: unknown;
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /documents': firstPage,
      'GET /tags': {
        tags: [
          { id: 'tag-1', name: '経費', usageCount: 1 },
          { id: 'tag-2', name: '規程', usageCount: 0 },
        ],
      },
      'POST /documents': (call) => {
        createdBody = call.body;
        return reply(201, { ...draftDoc, id: 'doc-3', title: '新しい規程' });
      },
    },
  });

  await page.goto('/admin/documents');
  const form = page.getByRole('form', { name: '文書登録' });
  const save = form.getByRole('button', { name: '保存' });

  // ★ 必須（タイトル）が欠ければ保存を拒否する（UC-03 例外フロー）。空白だけも同じ。
  await expect(save).toBeDisabled();
  await form.getByLabel('タイトル').fill('   ');
  await expect(save).toBeDisabled();

  // ★ 機密区分は定義済みの 4 区分だけ（自由入力の口は無い）。
  await expect(form.getByLabel('機密区分（ABAC属性）').getByRole('option')).toHaveCount(4);
  // ★ タグは辞書の値だけ（候補の外は選べない）。
  const tagSelect = form.getByLabel('タグ', { exact: true });
  const tagOptions = await tagSelect.getByRole('option').allTextContents();
  expect(tagOptions.filter((o) => o !== 'タグを選択').sort()).toEqual(['経費', '規程'].sort());
  // ★ doc_scope は本画面が作る組織文書では選ばせない（作成時のみ・以後は編集不可。ADR-0058）。
  await expect(form.getByLabel(/文書スコープ|doc_scope/)).toHaveCount(0);

  await form.getByLabel('タイトル').fill('新しい規程');
  await tagSelect.selectOption({ label: '規程' });
  await form.getByRole('button', { name: '追加' }).click();
  await save.click();
  await expect(page.getByRole('status').filter({ hasText: '文書を登録しました。' })).toBeVisible();

  expect(createdBody).toMatchObject({ title: '新しい規程', tags: ['規程'] });
  expect((createdBody as { attributes: Record<string, string> }).attributes).toHaveProperty(
    'confidentiality',
  );
  // クライアントは doc_scope を送らない（値は作成時にサーバ側で organization に決まる）。
  expect((createdBody as { attributes: Record<string, string> }).attributes).not.toHaveProperty(
    'doc_scope',
  );

  expectBffTrafficIsComplete(traffic);
});

test('M-072: 編集では doc_scope の欄が出ず、保存しても既存の doc_scope をそのまま保つ', async ({
  page,
}) => {
  let updatedBody: unknown;
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /documents': twoDocs,
      'GET /tags': tags,
      'PUT /documents/doc-2': (call) => {
        updatedBody = call.body;
        return { ...draftDoc, version: 2 };
      },
    },
  });

  await page.goto('/admin/documents');
  await rowOf(page, '出張旅費規程（案）')
    .getByRole('button', { name: '編集', exact: true })
    .click();
  const form = page.getByRole('form', { name: '文書編集' });
  await expect(form.getByLabel(/文書スコープ|doc_scope/)).toHaveCount(0);
  await form.getByLabel('タイトル').fill('出張旅費規程');
  await form.getByRole('button', { name: '保存' }).click();
  await expect(page.getByRole('status').filter({ hasText: '文書を更新しました。' })).toBeVisible();

  expect(updatedBody).toMatchObject({
    title: '出張旅費規程',
    expectedVersion: 1,
    attributes: { doc_scope: 'organization' },
  });

  expectBffTrafficIsComplete(traffic);
});

test('M-074: 保存が 413（本文が大きすぎる）で拒否されたら alert で知らせ、成功の告知を出さない', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /documents': firstPage,
      'GET /tags': tags,
      'POST /documents': reply(413, { title: 'Payload Too Large', status: 413 }),
    },
  });

  await page.goto('/admin/documents');
  const form = page.getByRole('form', { name: '文書登録' });
  await form.getByLabel('タイトル').fill('大きな文書');
  await form.getByRole('button', { name: '保存' }).click();

  await expect(page.getByRole('alert')).toBeVisible();
  await expect(page.getByText('文書を登録しました。')).toHaveCount(0);
  // 入力は失われない（フォームは開いたまま）。
  await expect(form.getByLabel('タイトル')).toHaveValue('大きな文書');

  expectBffTrafficIsComplete(traffic);
});
