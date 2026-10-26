import { test, expect } from './support/cspGuard';
import type { Page } from '@playwright/test';
import type {
  AttributeValuesResponse,
  CitationDto,
  DocumentContentDto,
  DocumentDto,
  FeedbackDto,
  WikiPageSummary,
  WikiPageView,
} from '../src/lib/api/generated/bff.schemas';
import {
  installBffSession,
  sessionUser,
  expectBffTrafficIsComplete,
  reply,
  sse,
} from './support/bffSession';

// SC-01 (#127 / #1139): 検索・チャット質問（`/ask`）のスクリーンレベル・スモーク。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない。** 未知パスの受け皿（catchAllRoute）は
// `RequireAuth` 配下に居るため、ルートを消しても未認証なら同じく `/login` へ行く（#918 の実測）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
//
// ［2026-10-24 / #2017 / E-12］従前ここには「SSE ストリーミング・出典・フィードバックの挙動は Vitest が担い、
// 本 spec は SSE を張らない」と書いていた。**網羅表（#2013）の M-022〜M-028 を実ブラウザで固定するため、
// 下の後半で SSE の応答（`sse()`）を張る。** ただし `route.fulfill` は本文を一度に返すので、
// 「途中まで流れている」状態（停止・生成中の表示）は引き続き Vitest の担当である。

// 対象範囲フィルタが引く属性の候補値（SC-01 / SC-08 が共有する部品）。
const attributeValues: AttributeValuesResponse = { values: [] };

test('unauthenticated visit to /ask redirects to /login', async ({ page }) => {
  await page.goto('/ask');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-01: renders for any authenticated user and streams no answer before it is asked', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-01 主アクター「全利用者」。**ロール限定は無い**（管理ロールを与えない）。
    user: sessionUser([]),
    handlers: { 'POST /attribute-values': attributeValues },
  });

  await page.goto('/ask');

  // ★ 陽性対照: 見出しと副題（05_screens §SC-01 主要素 1）。
  await expect(page.getByRole('heading', { name: 'ナレッジ検索・AI質問', level: 1 })).toBeVisible();
  await expect(
    page.getByText('横断検索と根拠付きAI回答（ストリーミング・出典表示）'),
  ).toBeVisible();
  // ★ 陽性対照: 左ナビ「検索・質問」も出る（05_screens §共通シェル）。
  await expect(page.getByRole('link', { name: '検索・質問' })).toBeVisible();

  // ★ 陽性対照（A-8）: 右レールの AI チャットは閉じた列にランチャーだけを出し、開くと補完領域になる。
  // 開いただけでは何も送らない（下の陰性対照が「ask」の往復を数える）。
  await page.getByRole('button', { name: 'AI チャットを開く' }).click();
  await expect(page.getByRole('complementary', { name: 'AI チャットパネル' })).toBeVisible();
  await expect(page.getByText('履歴（/ask） 0件')).toBeVisible();

  // ★ 陰性対照: **問う前に回答の口を開けない。** 初期表示でも右レールを開いても `POST /search` も
  // 回答ストリームも走らないこと（走る実装は「開いただけで LLM を叩く」——ADR-0032 の趣旨に反する）。
  // 「何も出ない」だけでは実装が壊れている場合と区別できないため、**上の陽性対照と対で**読むこと。
  const keys = traffic.calls.map((c) => c.key);
  expect(keys).not.toContain('POST /search');
  expect(keys.filter((k) => k.includes('ask'))).toEqual([]);

  expectBffTrafficIsComplete(traffic);
});

// ───────────────────── M-022〜M-028（#2017 / E-12）: 質問・回答・出典・フィードバック・縮退 ─────────────────────
//
// 期待値は計画（05_screens §SC-01・FR-04・FR-08・UC-01 例外フロー・用語集「機密区分」）から起こした。
// 個人資料の出典（👤・SC-19 への遷移）と「個人資料を含める」トグル（M-026）は、契約が口を持たないため
// 本 spec に置かない（作業仕様書 20261024_2017 §除外）。

const ASK_STREAM = 'POST /analysis/ask/stream';
const QUESTION = '経費精算の締め日は？';
const ANSWER_ID = '11111111-1111-1111-1111-111111111111';

const DOC_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const WIKI_DOC_ID = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const SECRET_DOC_ID = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
const RESTRICTED_DOC_ID = 'dddddddd-dddd-dddd-dddd-dddddddddddd';

/** 出典 1 件（契約 `CitationDto`）。 */
function citation(
  number: number,
  documentId: string,
  documentTitle: string,
  confidentiality: string,
): CitationDto {
  return {
    number,
    documentId,
    documentTitle,
    chunkId: `chunk-${number}`,
    sourceUri: null,
    score: 0.9 - number / 10,
    snippet: `${documentTitle}の抜粋`,
    confidentiality,
    docScope: 'organization',
  };
}

/**
 * 出典 4 件。種別は 組織文書（SC-03 へ）と Wiki（SC-04 へ）、機密区分は契約の値集合 4 値をすべて使う。
 * **期待する表示名は用語集「機密区分」が正**（public＝公開 / internal＝社内限 / confidential＝秘 / restricted＝取扱制限）。
 */
const CITATIONS: CitationDto[] = [
  citation(1, DOC_ID, '経費精算マニュアル', 'internal'),
  citation(2, WIKI_DOC_ID, '出張旅費規程', 'public'),
  citation(3, SECRET_DOC_ID, '役員報酬規程', 'confidential'),
  citation(4, RESTRICTED_DOC_ID, '個別人事記録', 'restricted'),
];
const EXPECTED_CITATION_ROWS = [
  { title: '経費精算マニュアル', kind: '組織文書', confidentiality: '社内限' },
  { title: '出張旅費規程', kind: '組織文書', confidentiality: '公開' },
  { title: '役員報酬規程', kind: '組織文書', confidentiality: '秘' },
  { title: '個別人事記録', kind: '組織文書', confidentiality: '取扱制限' },
];

/** Wiki 台帳（権限内）。ここに載る文書 ID の出典だけが Wiki（SC-04 行き）になる。 */
const wikiPage: WikiPageSummary = {
  id: 'page-1',
  documentId: WIKI_DOC_ID,
  title: '出張旅費規程',
  slug: 'ryohi-kitei',
  wikiPath: `doc/${WIKI_DOC_ID}`,
  status: 'Active',
  syncedAt: '2026-08-02T00:00:00Z',
};

/** 後段の順序どおり: citations（先行）→ token * → done。 */
const answeredStream = sse([
  { event: 'citations', data: { citations: CITATIONS } },
  { event: 'token', data: { text: '締め日は' } },
  { event: 'token', data: { text: '毎月 25 日です[1]。' } },
  {
    event: 'done',
    data: { answerId: ANSWER_ID, model: 'stub-model', inputTokens: 10, outputTokens: 5 },
  },
]);

/** 上流が不調のとき BFF が自ら送る中立の `event: error`（HTTP は 200 のまま。openapi §/bff/analysis/ask/stream）。 */
const failedStream = sse([{ event: 'error', data: { message: 'upstream unavailable' } }]);

const FULL_ANSWER = '締め日は毎月 25 日です';

async function ask(page: Page, question = QUESTION): Promise<void> {
  await page.getByRole('textbox', { name: '質問・キーワード' }).fill(question);
  await page.getByRole('button', { name: '送信', exact: true }).click();
}

// 回答と出典の欄（`Panel`）は見出しを持つが名前つきの region ではない。CSS・XPath で親を辿らず、
// **見出しの有無**と **main の中の文字・行**で読む（E2E 基準 §ロケータ）。
const answerPanel = (page: Page) => page.getByRole('main');
const answerHeading = (page: Page) =>
  page.getByRole('heading', { name: 'AI回答（ストリーミング）', level: 2 });
const citationHeading = (page: Page) =>
  page.getByRole('heading', { name: '出典（クリックで文書詳細／Wikiへ）', level: 2 });
/** 出典の行（脚注番号 `[n]` で始まる listitem）。履歴の行は質問文で始まるので混ざらない。 */
const citationRows = (page: Page) =>
  page
    .getByRole('main')
    .getByRole('listitem')
    .filter({ hasText: /^\[\d+\]/ });
const citationPanel = (page: Page) => page.getByRole('main');

test('M-022: 質問を送るとストリームの回答が描かれ、出典の各行に種別のラベルと機密区分の表示名が付く', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'POST /attribute-values': attributeValues,
      [ASK_STREAM]: answeredStream,
      'GET /wiki/pages': [wikiPage],
    },
  });

  await page.goto('/ask');
  await ask(page);

  // ★ 本体: token が到着順に連結され、done で確定する（確定したので 👍/👎 が出る）。
  await expect(answerPanel(page)).toContainText(FULL_ANSWER);
  await expect(page.getByRole('button', { name: '役に立った' })).toBeVisible();

  // ★ 出典: 4 行が届いた順に並び、各行に「組織文書」（Wiki も同じラベル）と機密区分の表示名が出る。
  await expect(citationHeading(page)).toBeVisible();
  const rows = citationRows(page);
  await expect(rows).toHaveCount(EXPECTED_CITATION_ROWS.length);
  for (const [i, row] of EXPECTED_CITATION_ROWS.entries()) {
    const item = rows.filter({ hasText: row.title });
    await expect(item, `${i + 1} 行目`).toContainText(`[${i + 1}]`);
    await expect(item.getByText(row.kind, { exact: true })).toBeVisible();
    await expect(item.getByText(row.confidentiality, { exact: true })).toBeVisible();
    // ★ 陰性対照: 契約の生値（`internal` 等）をそのまま出さない（表示名は用語集が正）。
    await expect(item.getByText(CITATIONS[i].confidentiality, { exact: true })).toHaveCount(0);
  }
  // 区別は色だけに頼らない旨の注記（05_screens §SC-01「区別の表示方法」）。
  await expect(citationPanel(page)).toContainText('アイコン＋ラベルで区別する');

  // ★ 要求: 質問だけを送る（対象範囲を選んでいないので attributeFilters は載らない）。ABAC スコープは送らない。
  const asks = traffic.calls.filter((c) => c.key === ASK_STREAM);
  expect(asks.map((c) => c.body)).toEqual([{ question: QUESTION }]);
  // ★ 陰性対照: 押すまでフィードバックを送らない。
  expect(traffic.calls.map((c) => c.key)).not.toContain('POST /feedback');

  expectBffTrafficIsComplete(traffic);
});

test('M-023: 組織文書の出典をクリックすると SC-03（文書詳細）へ遷移する', async ({ page }) => {
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
  const content: DocumentContentDto = { id: DOC_ID, title: doc.title, markdown: '本文' };
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'POST /attribute-values': attributeValues,
      [ASK_STREAM]: answeredStream,
      'GET /wiki/pages': [wikiPage],
      [`GET /documents/${DOC_ID}`]: doc,
      [`GET /documents/${DOC_ID}/content`]: content,
      [`GET /documents/${DOC_ID}/versions`]: [],
      'GET /graph/suggestions': [],
      'GET /graph/edge-types': [],
    },
  });

  await page.goto('/ask');
  await ask(page);
  await citationPanel(page).getByRole('link', { name: '経費精算マニュアル', exact: true }).click();

  await expect(page).toHaveURL(new RegExp(`/docs/${DOC_ID}$`));
  await expect(page.getByRole('heading', { name: '経費精算マニュアル', level: 1 })).toBeVisible();
  expect(traffic.calls.map((c) => c.key)).toContain(`GET /documents/${DOC_ID}`);

  expectBffTrafficIsComplete(traffic);
});

test('M-023: Wiki の出典をクリックすると SC-04（Wiki 閲覧）のその文書へ遷移し、Wiki でない出典は SC-03 へ向く', async ({
  page,
}) => {
  const view: WikiPageView = {
    ...wikiPage,
    content: '<p>旅費の精算は出張後 1 週間以内に行う。</p>',
  };
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'POST /attribute-values': attributeValues,
      [ASK_STREAM]: answeredStream,
      'GET /wiki/pages': [wikiPage],
      [`GET /wiki/pages/by-doc/${WIKI_DOC_ID}`]: view,
    },
  });

  await page.goto('/ask');
  await ask(page);

  // ★ 陰性対照: 台帳に載っていない出典は SC-04 へ送らない（到達できない導線を出さない）。
  for (const [id, title] of [
    [DOC_ID, '経費精算マニュアル'],
    [SECRET_DOC_ID, '役員報酬規程'],
    [RESTRICTED_DOC_ID, '個別人事記録'],
  ]) {
    await expect(
      citationPanel(page).getByRole('link', { name: title, exact: true }),
    ).toHaveAttribute('href', `/docs/${id}`);
  }

  await citationPanel(page).getByRole('link', { name: '出張旅費規程', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/wiki\\?doc=${WIKI_DOC_ID}$`));
  await expect(page.getByText('旅費の精算は出張後 1 週間以内に行う。')).toBeVisible();
  expect(traffic.calls.map((c) => c.key)).toContain(`GET /wiki/pages/by-doc/${WIKI_DOC_ID}`);

  expectBffTrafficIsComplete(traffic);
});

test('M-024: 確定した回答に 👍 を押すと answerId と rating=up を送り、押し直すと同じ answerId で rating=down を送る', async ({
  page,
}) => {
  const feedback = (rating: 'up' | 'down'): FeedbackDto => ({
    id: 'fb-1',
    answerId: ANSWER_ID,
    rating,
    question: QUESTION,
    userId: 'e2e-subject',
    createdAt: '2026-10-24T00:00:00Z',
    updatedAt: '2026-10-24T00:00:00Z',
  });
  let posted = 0;
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'POST /attribute-values': attributeValues,
      [ASK_STREAM]: answeredStream,
      'GET /wiki/pages': [wikiPage],
      // 1 回目は新規（201）、2 回目は上書き（200）。後段の状態コードを BFF がそのまま透過する。
      'POST /feedback': (call) => {
        posted += 1;
        const rating = (call.body as { rating: 'up' | 'down' }).rating;
        return reply(posted === 1 ? 201 : 200, feedback(rating));
      },
    },
  });

  await page.goto('/ask');
  await ask(page);
  const up = page.getByRole('button', { name: '役に立った' });
  const down = page.getByRole('button', { name: '役に立たなかった' });
  await expect(up).toHaveAttribute('aria-pressed', 'false');

  await up.click();
  await expect(up).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByText('フィードバックを送信しました。')).toBeVisible();

  await down.click();
  await expect(down).toHaveAttribute('aria-pressed', 'true');
  await expect(up).toHaveAttribute('aria-pressed', 'false');
  await expect.poll(() => posted).toBe(2);

  // ★ 本体: 本文は「どの回答に・どの評価を」。同じ回答への 2 回目も同じ answerId で送る（上書きはサーバ側。FR-08）。
  const bodies = traffic.calls.filter((c) => c.key === 'POST /feedback').map((c) => c.body);
  expect(bodies).toEqual([
    expect.objectContaining({ answerId: ANSWER_ID, rating: 'up' }),
    expect.objectContaining({ answerId: ANSWER_ID, rating: 'down' }),
  ]);
  // 質問は任意の文脈として載る（契約 FeedbackRequest.question）。
  for (const body of bodies) expect(body).toMatchObject({ question: QUESTION });
  await expect(page.getByRole('alert')).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('M-025: 対象範囲の候補は候補 API が返した値だけを描き、選んだ値が質問の要求に載る', async ({
  page,
}) => {
  const byAxis: Record<string, string[]> = {
    tags: ['経理', '規程'],
    department: ['finance'],
    project: [],
  };
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'POST /attribute-values': (call) => ({
        values: byAxis[(call.body as { key: string }).key] ?? [],
      }),
      [ASK_STREAM]: answeredStream,
      'GET /wiki/pages': [wikiPage],
    },
  });

  await page.goto('/ask');
  const filter = page.getByRole('region', { name: '対象範囲フィルタ' });

  // ★ 本体: 候補は応答の値そのもの（3 件）。画面は候補へ何も足さない（存在秘匿の境界はサーバ側 1 か所）。
  await expect(filter.getByRole('button')).toHaveCount(3);
  for (const value of ['経理', '規程', 'finance']) {
    await expect(filter.getByRole('button', { name: value, exact: true })).toBeVisible();
  }
  // ★ 陰性対照: 候補の無い軸（プロジェクト）は行ごと出さない。
  await expect(filter.getByText('プロジェクト', { exact: true })).toHaveCount(0);
  // 3 軸（タグ・部門・プロジェクト）をそれぞれ引く。「フォルダ」は引かない（裁定 Q9）。
  const keys = traffic.calls
    .filter((c) => c.key === 'POST /attribute-values')
    .map((c) => (c.body as { key: string }).key);
  expect([...new Set(keys)].sort()).toEqual(['department', 'project', 'tags']);

  await filter.getByRole('button', { name: '経理', exact: true }).click();
  await filter.getByRole('button', { name: 'finance', exact: true }).click();
  await expect(filter).toContainText('2 件で絞り込み中');
  await ask(page);
  await expect(answerPanel(page)).toContainText(FULL_ANSWER);

  const asks = traffic.calls.filter((c) => c.key === ASK_STREAM);
  expect(asks.map((c) => c.body)).toEqual([
    { question: QUESTION, attributeFilters: { tags: ['経理'], department: ['finance'] } },
  ]);

  expectBffTrafficIsComplete(traffic);
});

test('M-027: 空の質問・空白だけの質問は送れず、最大長を超えた入力は受け付けない', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: { 'POST /attribute-values': attributeValues },
  });

  await page.goto('/ask');
  const input = page.getByRole('textbox', { name: '質問・キーワード' });
  const send = page.getByRole('button', { name: '送信', exact: true });

  // ★ 陰性対照: 空・空白のみは送信できない（1 文字以上）。
  await expect(send).toBeDisabled();
  await input.fill('   ');
  await expect(send).toBeDisabled();
  await input.press('Enter');
  // ★ 陽性対照: 1 文字あれば送れる状態になる（ボタンが常に無効なだけの実装を通さない）。
  await input.fill('a');
  await expect(send).toBeEnabled();

  // ★ 最大長: 上限を宣言しており、上限を超える入力は上限で止まる（値は計画に無いので宣言値から読む）。
  const max = Number(await input.getAttribute('maxlength'));
  expect(max).toBeGreaterThan(0);
  await input.fill('あ'.repeat(max + 10));
  await expect(input).toHaveValue('あ'.repeat(max));

  // 空・空白の段で送っていない（送れる段では押していない）。
  expect(traffic.calls.map((c) => c.key)).not.toContain(ASK_STREAM);

  expectBffTrafficIsComplete(traffic);
});

test('M-028: ストリームが error で終わると、検索結果一覧への導線つきの alert に縮退し、再生成で同じ質問をやり直せる', async ({
  page,
}) => {
  let attempts = 0;
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'POST /attribute-values': attributeValues,
      [ASK_STREAM]: () => {
        attempts += 1;
        return attempts === 1 ? failedStream : answeredStream;
      },
      'GET /wiki/pages': [wikiPage],
    },
  });

  await page.goto('/ask');
  await ask(page);

  // ★ 縮退: alert が出て、同じ語で検索結果一覧（SC-02）へ 1 クリックで行ける（UC-01 例外フロー）。
  const alert = page.getByRole('alert');
  await expect(alert).toContainText('AI 回答を生成できませんでした。');
  await expect(alert.getByRole('link', { name: '検索結果一覧を開く →' })).toHaveAttribute(
    'href',
    `/search?q=${encodeURIComponent(QUESTION)}`,
  );
  // ★ 陰性対照: 確定していない回答に 👍/👎 を出さない。出典の欄も出さない。
  await expect(page.getByRole('button', { name: '役に立った' })).toHaveCount(0);
  await expect(citationHeading(page)).toHaveCount(0);
  await expect(answerHeading(page)).toBeVisible();

  // ★ 再試行: 「再生成」は同じ質問を送り直し、成功すれば回答へ戻る（alert は消える）。
  await page.getByRole('button', { name: '再生成' }).click();
  await expect(answerPanel(page)).toContainText(FULL_ANSWER);
  await expect(page.getByRole('alert')).toHaveCount(0);

  const asks = traffic.calls.filter((c) => c.key === ASK_STREAM);
  expect(asks.map((c) => c.body)).toEqual([{ question: QUESTION }, { question: QUESTION }]);

  expectBffTrafficIsComplete(traffic);
});
