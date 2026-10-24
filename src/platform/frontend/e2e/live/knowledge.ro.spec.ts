// NFR / FR-03 / FR-04 / FR-05 / FR-13 / FR-17 / SC-01 / SC-02 / SC-03 / SC-04 / SC-08 / SC-18 / IADR-0255 / IADR-0627（#2028 / E-41）:
// live-ro の**検索・文書・グラフの到達と ABAC の対照**。網羅表（#2013）の M-029・M-030・M-036・M-037・M-044・M-045・M-048・
// M-051・M-056 を、`live-setup` が作った `e2e-user` / `e2e-noaccess` の storageState で流す。
//
// - 🔴 読み取りだけ。質問・分析は送らない（LLM を呼ぶのは llm 層。E-70）。SC-02 の一覧は画面が開いた時点で `POST /bff/search` を
//   送る（検索は読み取り）。それ以外のフォームは送らない。
// - **ABAC の対照は両側で固定する**（IADR-0627）。seed（`deploy/local/search-seed/documents.json`）は public の 1 件と internal の 1 件を持つ。
//   `e2e-user`（clearance=internal）には 2 件とも見え、`e2e-noaccess`（clearance=public）には public の 1 件だけが見える。
//   public の 1 件が noaccess 側の陽性対照（検索そのものは効いている）であり、internal が出ないことを「検索の故障」と取り違えない
//   （IADR-0255: 200 ＋空と「見えない」は区別できないので、見える側との対比で判定する）。
// - **`E2E_SEARCH_HITS` の門**（IADR-0627）: `1` のときだけヒット・候補・ID を要る test を判定する。無ければその test は理由つきで
//   skip し、到達（200 ＋描画）だけを見る。`e2e-live.yml` の使い捨てスタックは索引が在るので `1` で流す（skip しない）。
// - 索引への取り込みは非同期なので、ヒットを待つところは画面を開き直しながら待つ（`toPass`。時間で待たない）。
import { readFileSync } from 'node:fs';
import { expect, test, type Browser, type Page } from '@playwright/test';
import type {
  AttributeValuesResponse,
  SearchResponse,
} from '../../src/lib/api/generated/bff.schemas';
import { parseLiveEnv, stateFile, type Role } from './support/env';
import { DEFAULT_AUTH_DIR } from './support/paths';

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);

interface SeedDoc {
  title: string;
  attributes: Record<string, string>;
}
/** seed の宣言が単一情報源（タイトル・属性をここへ写さない）。 */
const SEED = JSON.parse(
  readFileSync(
    new URL('../../../../../deploy/local/search-seed/documents.json', import.meta.url),
    'utf8',
  ),
) as { probeTerm: string; documents: SeedDoc[] };
const seedOf = (confidentiality: string): SeedDoc => {
  const doc = SEED.documents.find((d) => d.attributes.confidentiality === confidentiality);
  if (!doc) throw new Error(`seed に confidentiality=${confidentiality} の文書が無い（IADR-0627）`);
  return doc;
};
const PUBLIC_DOC = seedOf('public');
const INTERNAL_DOC = seedOf('internal');
/** internal の文書にだけ在る department の値（候補の対照）。 */
const INTERNAL_ONLY_DEPARTMENT = INTERNAL_DOC.attributes.department;

const HITS_REASON =
  'E2E_SEARCH_HITS=1 が無い（索引が空であり得る環境では、ヒット・候補・文書 ID を要る判定をしない。IADR-0627）';
/** 取り込みを待つ上限（seed は up の中と readiness の後に投入され、取り込みは非同期）。 */
const INDEX_WAIT = { timeout: 120_000, intervals: [3_000, 5_000, 10_000] };
const SLOW_TEST_MS = 180_000;

const isBff = (url: string, path: string) => new URL(url).pathname === path;

/** SC-02 を開き、画面が送った `POST /bff/search` の応答（200）を返す。見出しまで待つ。 */
async function openSearch(page: Page, query: string): Promise<SearchResponse> {
  const responded = page.waitForResponse(
    (r) => isBff(r.url(), '/bff/search') && r.request().method() === 'POST',
  );
  await page.goto(`/search?q=${encodeURIComponent(query)}`);
  const res = await responded;
  expect(res.status(), 'POST /bff/search').toBe(200);
  await expect(page.getByRole('heading', { name: '検索結果一覧', level: 1 })).toBeVisible();
  return (await res.json()) as SearchResponse;
}

/** 検索の応答から、seed のタイトル → 文書 ID を引く。 */
function idsByTitle(res: SearchResponse): Map<string, string> {
  return new Map(res.results.map((r) => [r.documentTitle, r.documentId]));
}

/**
 * `e2e-user` の別の context で、seed の 2 件が検索に出るまで待ち、ID を返す（noaccess の対照の前提: 2 件とも索引に入っている）。
 * これが通らないまま noaccess 側で「internal が出ない」を見ると、取り込み前の空で緑になる。
 */
async function seedIdsAsUser(browser: Browser): Promise<{ publicId: string; internalId: string }> {
  const context = await browser.newContext({
    storageState: stateFile(cfg.authDir, 'user'),
    baseURL: cfg.edgeUrl,
    ignoreHTTPSErrors: cfg.insecureTls,
    locale: 'ja-JP',
  });
  try {
    const page = await context.newPage();
    let ids = new Map<string, string>();
    await expect(async () => {
      ids = idsByTitle(await openSearch(page, SEED.probeTerm));
      expect([...ids.keys()]).toEqual(
        expect.arrayContaining([PUBLIC_DOC.title, INTERNAL_DOC.title]),
      );
    }).toPass(INDEX_WAIT);
    return { publicId: ids.get(PUBLIC_DOC.title)!, internalId: ids.get(INTERNAL_DOC.title)! };
  } finally {
    await context.close();
  }
}

/** 画面を開き、対象範囲の候補（`POST /bff/attribute-values`）の 3 軸の応答を集める。状態と値を軸ごとに返す。 */
async function openWithCandidates(
  page: Page,
  path: string,
  heading: string,
): Promise<Map<string, { status: number; values: string[] }>> {
  const seen = new Map<string, { status: number; values: string[] }>();
  const onResponse = async (r: import('@playwright/test').Response) => {
    if (!isBff(r.url(), '/bff/attribute-values')) return;
    const key = String((r.request().postDataJSON() as { key?: string } | null)?.key ?? '');
    const values = r.ok() ? ((await r.json()) as AttributeValuesResponse).values : [];
    seen.set(key, { status: r.status(), values });
  };
  page.on('response', onResponse);
  await page.goto(path);
  await expect(page.getByRole('heading', { name: heading, level: 1 })).toBeVisible();
  await expect
    .poll(() => [...seen.keys()].sort(), { message: '候補の 3 軸' })
    .toEqual(['department', 'project', 'tags']);
  page.off('response', onResponse);
  return seen;
}

/** `/bff/search`・`/bff/analysis/*`（回答・分析の口。LLM を呼ぶ）への要求を数える。 */
function watchAnswerCalls(page: Page): () => string[] {
  const calls: string[] = [];
  page.on('request', (r) => {
    const path = new URL(r.url()).pathname;
    if (path.startsWith('/bff/search') || path.startsWith('/bff/analysis'))
      calls.push(`${r.method()} ${path}`);
  });
  return () => calls;
}

const as = (role: Role) => ({ storageState: stateFile(cfg.authDir, role) });

test.describe('e2e-user の検索・文書・グラフ', () => {
  test.use(as('user'));

  // SC-01, FR-04, FR-05: M-029。
  test(
    'M-029: 検索・質問の画面に到達し、対象範囲の候補 3 軸が 200 で、質問を送らない',
    { tag: ['@p0', '@live-ro'] },
    async ({ page }) => {
      const answerCalls = watchAnswerCalls(page);
      const seen = await openWithCandidates(page, '/ask', 'ナレッジ検索・AI質問');
      for (const [axis, { status }] of seen) expect(status, `候補 ${axis}`).toBe(200);
      expect(answerCalls(), '回答・分析の口').toEqual([]);
    },
  );

  // SC-01, FR-05, IADR-0627: M-030 の陽性側。
  test(
    'M-030（陽性側）: 候補の部門に、internal の seed 文書にだけ在る値が出る',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }) => {
      test.skip(!cfg.searchHits, HITS_REASON);
      test.setTimeout(SLOW_TEST_MS);
      await expect(async () => {
        const seen = await openWithCandidates(page, '/ask', 'ナレッジ検索・AI質問');
        expect(seen.get('department')?.values).toContain(INTERNAL_ONLY_DEPARTMENT);
      }).toPass(INDEX_WAIT);
    },
  );

  // SC-02, FR-03: M-036（門なし）。索引が空でも、一覧は 200 で描かれる。
  test(
    'M-036: 結果一覧を開くと検索が 200 で返り、一覧の見出しが出る',
    { tag: ['@p0', '@live-ro'] },
    async ({ page }) => {
      const res = await openSearch(page, SEED.probeTerm);
      expect(Array.isArray(res.results), 'results').toBe(true);
    },
  );

  // SC-02, FR-03, FR-05: M-036（門あり）。
  test(
    'M-036: 合言葉で検索すると、public と internal の seed 文書がどちらも一覧に出る',
    { tag: ['@p0', '@live-ro'] },
    async ({ page }) => {
      test.skip(!cfg.searchHits, HITS_REASON);
      test.setTimeout(SLOW_TEST_MS);
      await expect(async () => {
        const titles = [...idsByTitle(await openSearch(page, SEED.probeTerm)).keys()];
        expect(titles).toEqual(expect.arrayContaining([PUBLIC_DOC.title, INTERNAL_DOC.title]));
      }).toPass(INDEX_WAIT);
      for (const title of [PUBLIC_DOC.title, INTERNAL_DOC.title]) {
        await expect(page.getByRole('link', { name: title, exact: true })).not.toHaveCount(0);
      }
    },
  );

  // SC-03, FR-06, FR-05: M-044。
  test(
    'M-044: internal の seed 文書を文書詳細で開ける（見出しが文書名）',
    { tag: ['@p0', '@live-ro'] },
    async ({ page, browser }) => {
      test.skip(!cfg.searchHits, HITS_REASON);
      test.setTimeout(SLOW_TEST_MS);
      const { internalId } = await seedIdsAsUser(browser);
      await page.goto(`/docs/${internalId}`);
      await expect(page.getByRole('heading', { name: INTERNAL_DOC.title, level: 1 })).toBeVisible();
      await expect(page.getByText('文書が見つかりませんでした。', { exact: true })).toHaveCount(0);
    },
  );

  // SC-04, FR-13, ADR-0073: M-048。Wiki の接続先の有無で「ページツリー」か「ページが無い」のどちらかに落ち着き、失敗にならない。
  test(
    'M-048: Wiki 閲覧に到達し、ページの一覧が 200 で、ページツリーか空の表示が出る（失敗の表示は出ない）',
    { tag: ['@p2', '@live-ro'] },
    async ({ page }) => {
      const pages = page.waitForResponse((r) => isBff(r.url(), '/bff/wiki/pages'));
      await page.goto('/wiki');
      await expect(page.getByRole('heading', { name: 'Wiki 閲覧', level: 1 })).toBeVisible();
      expect((await pages).status(), 'GET /bff/wiki/pages').toBe(200);
      await expect(
        page
          .getByRole('navigation', { name: 'ページツリー' })
          .or(page.getByText('閲覧できる Wiki ページはありません。', { exact: true })),
      ).toBeVisible();
      await expect(page.getByText('ページツリーを取得できませんでした。')).toHaveCount(0);
    },
  );

  // SC-08, FR-07, FR-11: M-051。
  test(
    'M-051: AI 分析の画面に到達し、対象範囲の候補 3 軸が 200 で、分析を送らない',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }) => {
      const answerCalls = watchAnswerCalls(page);
      const seen = await openWithCandidates(page, '/analyze', 'AI分析依頼');
      for (const [axis, { status }] of seen) expect(status, `候補 ${axis}`).toBe(200);
      expect(answerCalls(), '回答・分析の口').toEqual([]);
    },
  );

  // SC-18, FR-17: M-056。辺が 0 でもエラーにならない（「関係が無い」の空表示か、グラフの図のどちらか）。
  test(
    'M-056: public の seed 文書を起点にナレッジグラフを開くと、近傍が 200 で返り、起点の名前とグラフか空の表示が出る',
    { tag: ['@p1', '@live-ro'] },
    async ({ page, browser }) => {
      test.skip(!cfg.searchHits, HITS_REASON);
      test.setTimeout(SLOW_TEST_MS);
      const { publicId } = await seedIdsAsUser(browser);
      const neighbors = page.waitForResponse((r) =>
        isBff(r.url(), `/bff/graph/${publicId}/neighbors`),
      );
      await page.goto(`/graph?root=${publicId}`);
      expect((await neighbors).status(), 'GET /bff/graph/{id}/neighbors').toBe(200);
      await expect(page.getByText(`起点: ${PUBLIC_DOC.title}`, { exact: true })).toBeVisible();
      await expect(
        page
          .getByRole('img', { name: /^ナレッジグラフ（ノード \d+ 件・辺 \d+ 本）/ })
          .or(page.getByText(/^この文書には表示できる関係がありません。/)),
      ).toBeVisible();
      await expect(page.getByText('グラフを読み込めませんでした。')).toHaveCount(0);
    },
  );
});

test.describe('e2e-noaccess の検索・文書（ABAC の陰性対照）', () => {
  test.use(as('noaccess'));

  // SC-01, FR-05, IADR-0627: M-030。
  test(
    'M-030: 候補は 200 で、public の文書の部門は出るが、internal の seed 文書にだけ在る部門は出ない',
    { tag: ['@p1', '@live-ro'] },
    async ({ page, browser }) => {
      test.skip(!cfg.searchHits, HITS_REASON);
      test.setTimeout(SLOW_TEST_MS);
      await seedIdsAsUser(browser); // 前提: 2 件とも索引に入っている（取り込み前の空で緑にしない）。
      let department: string[] = [];
      await expect(async () => {
        const seen = await openWithCandidates(page, '/ask', 'ナレッジ検索・AI質問');
        for (const [axis, { status }] of seen) expect(status, `候補 ${axis}`).toBe(200);
        department = seen.get('department')?.values ?? [];
        // 陽性対照: public の文書の部門は noaccess にも出る（候補の口そのものは効いている）。
        expect(department).toContain(PUBLIC_DOC.attributes.department);
      }).toPass(INDEX_WAIT);
      expect(department, '候補の部門').not.toContain(INTERNAL_ONLY_DEPARTMENT);
    },
  );

  // SC-02, FR-05, IADR-0255: M-037。
  test(
    'M-037: 合言葉で検索すると public の seed 文書は出るが、internal の seed 文書は出ない',
    { tag: ['@p0', '@live-ro'] },
    async ({ page, browser }) => {
      test.skip(!cfg.searchHits, HITS_REASON);
      test.setTimeout(SLOW_TEST_MS);
      await seedIdsAsUser(browser); // 前提: user には 2 件とも見えている。
      let titles: string[] = [];
      await expect(async () => {
        titles = [...idsByTitle(await openSearch(page, SEED.probeTerm)).keys()];
        // 陽性対照: 検索は noaccess にも効いている。
        expect(titles).toContain(PUBLIC_DOC.title);
      }).toPass(INDEX_WAIT);
      expect(titles, '一覧の文書名').not.toContain(INTERNAL_DOC.title);
      await expect(page.getByRole('link', { name: PUBLIC_DOC.title, exact: true })).not.toHaveCount(
        0,
      );
      await expect(page.getByRole('link', { name: INTERNAL_DOC.title, exact: true })).toHaveCount(
        0,
      );
    },
  );

  // SC-03, FR-05, IADR-0009: M-045。
  test(
    'M-045: internal の seed 文書の ID を直接開くと「文書が見つかりませんでした」で、BFF は 404。public の文書は開ける',
    { tag: ['@p0', '@live-ro'] },
    async ({ page, browser }) => {
      test.skip(!cfg.searchHits, HITS_REASON);
      test.setTimeout(SLOW_TEST_MS);
      const { publicId, internalId } = await seedIdsAsUser(browser);

      const denied = page.waitForResponse((r) => isBff(r.url(), `/bff/documents/${internalId}`));
      await page.goto(`/docs/${internalId}`);
      expect((await denied).status(), 'GET /bff/documents/{internal}').toBe(404);
      await expect(page.getByText('文書が見つかりませんでした。', { exact: true })).toBeVisible();
      await expect(page.getByRole('heading', { name: INTERNAL_DOC.title })).toHaveCount(0);

      // 陽性対照: 同じ画面で public の文書は開ける（404 が画面の故障ではない）。
      await page.goto(`/docs/${publicId}`);
      await expect(page.getByRole('heading', { name: PUBLIC_DOC.title, level: 1 })).toBeVisible();
    },
  );
});
