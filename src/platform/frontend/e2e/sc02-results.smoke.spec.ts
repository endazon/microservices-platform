import { test, expect } from './support/cspGuard';
import type {
  DocumentDto,
  SearchResponse,
  SearchResultDto,
} from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-02 (#502 / #1139): 検索結果一覧（`/search`）のスクリーンレベル・スモーク。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない。** 未知パスの受け皿（catchAllRoute）は
// `RequireAuth` 配下に居るため、ルートを消しても未認証なら同じく `/login` へ行く（#918 の実測）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// 一覧の中身・SC-03 への遷移は Vitest（単体＋導線テスト searchFlow.test.tsx）も担う。
// ［2026-10-24 / #2018 / E-13］網羅表（#2013）の M-033〜M-035 は、下の後半で実ブラウザでも固定する。

// #1871: 縮退の印は契約上必須（BFF の空応答は縮退なし）。
const empty: SearchResponse = {
  results: [],
  totalHits: 0,
  elapsedMs: 3,
  degraded: false,
  degradedReasons: [],
};

test('unauthenticated visit to /search redirects to /login', async ({ page }) => {
  await page.goto('/search?q=%E7%B5%8C%E8%B2%BB');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-02: an authenticated user sees the neutral empty answer, not a permission hint', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-02 主アクター「全利用者」。**ロール限定は無い**（管理ロールを与えない）。
    user: sessionUser([]),
    handlers: { 'POST /search': empty },
  });

  await page.goto('/search?q=%E7%B5%8C%E8%B2%BB');

  // ★ 陽性対照: 見出しと左ナビ「結果一覧」（05_screens §共通シェル）。検索語が URL から復元され、
  // **開いた時点で問い合わせが走る**（下の traffic で確かめる）。
  await expect(page.getByRole('heading', { name: '検索結果一覧', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: '結果一覧' })).toBeVisible();
  expect(traffic.calls.map((c) => c.key)).toContain('POST /search');

  // ★ 陽性対照: 0 件のときの固定文言（deny-by-default。IADR-0009）。
  // **［2026-09-12］`QueryState` ＋ `EmptyState` への統一で文言が変わった**
  // （空は失敗ではないので、再試行ではなく**条件の変更**を促す）。
  await expect(page.getByText('該当する文書はありません。')).toBeVisible();
  await expect(page.getByText('条件を変えて再検索してください。')).toBeVisible();

  // ★ 陰性対照: **権限外であることを匂わせない**（存在秘匿）。「権限」「閲覧できません」と
  // 書いた瞬間に「在るが見せない」が漏れる —— 0 件と権限外は同じ文言で応答する。
  await expect(page.getByText(/権限/)).toHaveCount(0);
  await expect(page.getByText(/閲覧できません/)).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

// ───────────── M-033〜M-035（#2018 / E-13）: 要求本文・本文なしの表示・SC-03 への遷移 ─────────────
//
// 期待値は計画 05_screens §SC-02（検索モード 3 値・並び順 2 値・「権限内のみ表示」・本文なしの表示・
// 結果クリックで SC-03）と契約 `SearchRequest`（`mode` / `sortBy` は省略で既定）から起こした。

const QUERY = '経費';
const DOC_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const PDF_ID = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

function hit(overrides: Partial<SearchResultDto>): SearchResultDto {
  return {
    chunkId: 'chunk-1',
    documentId: DOC_ID,
    documentTitle: '経費精算マニュアル',
    text: '領収書を添付して申請する。',
    score: 0.82,
    attributes: {},
    tags: [],
    updatedAt: '2026-08-02T00:00:00Z',
    hasBody: true,
    ...overrides,
  };
}

const results: SearchResponse = {
  results: [
    hit({}),
    // FR-12 / ADR-0070 決定 4: テキスト層を持たない PDF。抜粋（text）は空で、hasBody が false。
    hit({
      chunkId: 'chunk-2',
      documentId: PDF_ID,
      documentTitle: '旧経費規程（スキャン）',
      text: '',
      hasBody: false,
    }),
  ],
  totalHits: 2,
  elapsedMs: 3,
  degraded: false,
  degradedReasons: [],
};

/** 検索の要求本文（`topK` は計画に無い実装の値なので比べない）。 */
function searchBodies(traffic: { calls: { key: string; body: unknown }[] }) {
  return traffic.calls
    .filter((c) => c.key === 'POST /search')
    .map((c) => {
      const rest = { ...(c.body as Record<string, unknown>) };
      delete rest.topK;
      return rest;
    });
}

test('M-033: 既定の検索はハイブリッド・関連度で、要求本文は既定の値を省く（契約の既定）', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: { 'POST /search': results },
  });

  await page.goto(`/search?q=${encodeURIComponent(QUERY)}`);
  await expect(page.getByRole('link', { name: '経費精算マニュアル', exact: true })).toBeVisible();

  // ★ 選択肢は計画の 3 値・2 値で、既定はハイブリッド・関連度（05_screens §SC-02 主要素）。
  const mode = page.getByRole('combobox', { name: '検索モード' });
  const sort = page.getByRole('combobox', { name: '並び' });
  await expect(mode.getByRole('option')).toHaveText(['ハイブリッド', 'キーワード', '意味']);
  await expect(sort.getByRole('option')).toHaveText(['関連度', '更新日時の新しい順']);
  await expect(mode).toHaveValue('hybrid');
  await expect(sort).toHaveValue('relevance');
  // ★ 件数の表示は「権限内のみ表示」を明示する（FR-05）。
  await expect(page.getByText('2 件（権限内のみ表示）')).toBeVisible();

  // ★ 要求本文: 既定のときは mode / sortBy を載せない（契約: 省略・未知は hybrid / relevance）。
  expect(searchBodies(traffic)).toEqual([{ query: QUERY }]);

  expectBffTrafficIsComplete(traffic);
});

test('M-033: 検索モードと並び順を切り替えると、選んだ値が要求本文に載って再検索する', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: { 'POST /search': results },
  });

  await page.goto(`/search?q=${encodeURIComponent(QUERY)}`);
  await expect(page.getByRole('link', { name: '経費精算マニュアル', exact: true })).toBeVisible();
  const mode = page.getByRole('combobox', { name: '検索モード' });
  const sort = page.getByRole('combobox', { name: '並び' });

  await mode.selectOption({ label: 'キーワード' });
  await expect(page).toHaveURL(/[?&]mode=keyword(&|$)/);
  await expect.poll(() => searchBodies(traffic).length).toBe(2);

  await mode.selectOption({ label: '意味' });
  await expect.poll(() => searchBodies(traffic).length).toBe(3);

  await sort.selectOption({ label: '更新日時の新しい順' });
  await expect(page).toHaveURL(/[?&]sort=updated(&|$)/);
  await expect.poll(() => searchBodies(traffic).length).toBe(4);

  // 既定へ戻すと、要求本文から再び省く。
  await mode.selectOption({ label: 'ハイブリッド' });
  await expect.poll(() => searchBodies(traffic).length).toBe(5);

  expect(searchBodies(traffic)).toEqual([
    { query: QUERY },
    { query: QUERY, mode: 'keyword' },
    { query: QUERY, mode: 'semantic' },
    { query: QUERY, mode: 'semantic', sortBy: 'updated' },
    { query: QUERY, sortBy: 'updated' },
  ]);
  // ★ 陰性対照: クライアントは ABAC スコープ・属性の絞り込みを勝手に足さない（FR-05）。
  for (const body of searchBodies(traffic)) expect(body).not.toHaveProperty('attributeFilters');

  expectBffTrafficIsComplete(traffic);
});

test('M-034: 本文の無い文書は「本文なし（原本を参照）」で結果に残り、除外されない', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: { 'POST /search': results },
  });

  await page.goto(`/search?q=${encodeURIComponent(QUERY)}`);

  const rows = page.getByRole('table', { name: '検索結果' }).getByRole('row');
  // 見出し行 ＋ 2 件。本文なしの文書も行として残る。
  await expect(rows).toHaveCount(3);
  const bodyless = rows.filter({ hasText: '旧経費規程（スキャン）' });
  await expect(bodyless.getByText('本文なし（原本を参照）', { exact: true })).toBeVisible();
  // ★ 陰性対照: 本文のある文書には付けない（全行に付ける実装を通さない）。
  const withBody = rows.filter({ hasText: '経費精算マニュアル' });
  await expect(withBody.getByText('本文なし（原本を参照）')).toHaveCount(0);
  await expect(withBody.getByText('領収書を添付して申請する。')).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('M-035: 結果の文書名をクリックすると SC-03（文書詳細）へ遷移する', async ({ page }) => {
  const doc: DocumentDto = {
    id: DOC_ID,
    title: '経費精算マニュアル',
    status: 'published',
    version: 1,
    attributes: {},
    tags: [],
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-02T00:00:00Z',
  };
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'POST /search': results,
      [`GET /documents/${DOC_ID}`]: doc,
      [`GET /documents/${DOC_ID}/content`]: { id: DOC_ID, title: doc.title, markdown: '本文' },
      [`GET /documents/${DOC_ID}/versions`]: [],
      'GET /graph/suggestions': [],
      'GET /graph/edge-types': [],
      'GET /wiki/pages': [],
    },
  });

  await page.goto(`/search?q=${encodeURIComponent(QUERY)}`);
  await page.getByRole('link', { name: '経費精算マニュアル', exact: true }).click();

  await expect(page).toHaveURL(new RegExp(`/docs/${DOC_ID}$`));
  await expect(page.getByRole('heading', { name: '経費精算マニュアル', level: 1 })).toBeVisible();
  expect(traffic.calls.map((c) => c.key)).toContain(`GET /documents/${DOC_ID}`);

  expectBffTrafficIsComplete(traffic);
});

test('SC-02: 結果の機密区分は用語集の表示名で出し、契約の生値を出さない', async ({ page }) => {
  const byValue: [string, string][] = [
    ['public', '公開'],
    ['internal', '社内限'],
    ['confidential', '秘'],
    ['restricted', '取扱制限'],
  ];
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'POST /search': {
        ...results,
        results: byValue.map(([value], i) =>
          hit({
            chunkId: `chunk-c${i}`,
            documentId: `0000000${i}-0000-0000-0000-000000000000`,
            documentTitle: `文書${i}`,
            attributes: { confidentiality: value },
          }),
        ),
        totalHits: byValue.length,
      },
    },
  });

  await page.goto(`/search?q=${encodeURIComponent(QUERY)}`);
  const table = page.getByRole('table', { name: '検索結果' });
  for (const [i, [value, label]] of byValue.entries()) {
    const row = table.getByRole('row').filter({ hasText: `文書${i}` });
    await expect(row.getByText(label, { exact: true })).toBeVisible();
    await expect(row.getByText(value, { exact: true })).toHaveCount(0);
  }

  expectBffTrafficIsComplete(traffic);
});
