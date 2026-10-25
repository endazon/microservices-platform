import { test, expect } from './support/cspGuard';
import type { DashboardSummaryDto } from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-10 (#504 / #1139): 運用ダッシュボード（`/admin/ops`）のスクリーンレベル・スモーク。
//
// 🔴 **本画面は platform-admin / platform-operator 限定である**（IADR-0039）。
// 権限外では `RequireRole` が `NotFound` を描き、画面の存在を示さない（存在秘匿。IADR-0009 / IADR-0035）。
// **この出し分けは未認証のスモークでは 1 度も踏まれない。**
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない**（catch-all が認証ガード配下。#918）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// 図の写像・期間切替・品質指標の丸めは Vitest（単体）が引き続き担う。

// 🔴 **0 件の要約を返す。** SLO とコストは契約に無く、画面も出さない（下の陰性対照）。
const summary: DashboardSummaryDto = {
  totalSearches: 0,
  totalAnswers: 0,
  usageTrend: [],
  topSearchTerms: [],
  quality: { up: 0, down: 0, total: 0, satisfactionRate: 0 },
  // FR-10, SC-10, ADR-0071 決定 2（#1197）: 検索傾向の出現件数の下限（既定 3）。
  searchTermMinCount: 3,
};

test('unauthenticated visit to /admin/ops redirects to /login', async ({ page }) => {
  await page.goto('/admin/ops');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-10: an operator reaches the screen and its navigation entry', async ({ page }) => {
  const traffic = await installBffSession(page, {
    // 🔴 **運用者で測る。** 管理者だけで測ると `anyOf` から operator が落ちても気づけない。
    user: sessionUser(['platform-operator']),
    handlers: { 'GET /dashboard/summary': summary },
  });

  await page.goto('/admin/ops');

  // ★ 陽性対照: 画面が描かれ、左ナビ「ダッシュボード」も出る（05_screens §共通シェル）。
  await expect(page.getByRole('heading', { name: '運用ダッシュボード', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: 'ダッシュボード' })).toBeVisible();
  // ★ 陽性対照: **0 件は 0 件として言う。** 取得失敗を空表示へ寄せる実装はここで区別される。
  await expect(page.getByText('期間内の利用はありません。')).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('SC-10: a user with no administrative role gets the same not-found page', async ({ page }) => {
  // ★ 陰性対照: **応答を 1 つも用意しない。** 管理端点を呼べば `unhandled` に載って落ちる。
  const traffic = await installBffSession(page, { user: sessionUser([]) });

  await page.goto('/admin/ops');

  // IADR-0009: 不在も権限による秘匿も同じ画面で応答する。
  await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
  await expect(page.getByRole('heading', { name: '運用ダッシュボード' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'ダッシュボード' })).toHaveCount(0);

  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /dashboard/summary');
  expectBffTrafficIsComplete(traffic);
});

// ───────────── M-094・M-095（#2022 / E-17）: 検索傾向のしきい値・未生産の指標を並べない・外部ツールの導線 ─────────────
//
// 期待値は計画 05_screens §SC-10 から起こした。
// - 検索傾向は出現件数が **3 件未満の語を出さない**（落とす。「その他 M 件」へ集約しない）。**現在のしきい値を併記する。**
// - KPI カードは利用状況（件数）だけ。**SLO 達成率・検索 p95・LLM コストは KPI に並べず、Grafana への導線に留める**。
// - **生産者が無い指標を 0 件として並べない**（その 0 は「問題なし」と読める）。
// - Grafana／Kiali／Jaeger・Tempo と SC-11 への導線を置く。外部ツールの接続先は実行時 config（opsLinks）が与える。

const withTerms: DashboardSummaryDto = {
  ...summary,
  totalSearches: 17,
  totalAnswers: 9,
  topSearchTerms: [
    { term: '経費精算', count: 12 },
    { term: '出張旅費', count: 3 },
    { term: '社長の個人メモ', count: 2 },
  ],
};

test('M-094: 3 件未満の検索語は伏せ（その他にも集約しない）、しきい値を併記し、未生産の指標や SLO・コストを数値で並べない', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: { 'GET /dashboard/summary': withTerms },
  });

  await page.goto('/admin/ops');
  const trend = page.getByRole('table', { name: '検索傾向（上位語）の一覧' });
  // ★ 陽性対照: しきい値以上の 2 語は出る。
  await expect(trend.getByRole('cell', { name: '経費精算' })).toBeVisible();
  await expect(trend.getByRole('cell', { name: '出張旅費' })).toBeVisible();
  // ★ しきい値未満は落とす。「その他」へも集約しない。しきい値を併記する。
  await expect(page.getByText('社長の個人メモ')).toHaveCount(0);
  await expect(page.getByText(/その他/)).toHaveCount(0);
  await expect(page.getByText('3 件以上検索された語のみを表示します。')).toBeVisible();

  // ★ KPI は件数の 3 つだけ（SLO・p95・LLM コストは数値で並べない）。
  await expect(page.getByText('検索総数')).toBeVisible();
  await expect(page.getByText(/SLO 達成率|p95|LLM コスト|利用実績/)).toHaveCount(0);
  await expect(
    page.getByText('利用状況・検索傾向・回答品質（SLO・コストは Grafana で参照）'),
  ).toBeVisible();
  // ★ 生産者の無い指標（ナレッジ健全性）を 0 で並べない —— 節そのものを出さない。
  await expect(
    page.getByText(/ナレッジ健全性|孤立文書|未解決リンク|未要約クラスタ|陳腐化文書/),
  ).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('M-095: 外部ツールの導線は実行時 config の URL を持ち、構成ビューアへの導線も置く（遷移はしない）', async ({
  page,
}) => {
  await page.route('**/config.js', (route) =>
    route.fulfill({
      contentType: 'application/javascript',
      body: `window.__APP_CONFIG__ = {
        bffBaseUrl: '/bff',
        oidc: { authority: 'http://localhost:8080/realms/platform' },
        opsLinks: {
          grafanaUrl: 'https://grafana.example.test/d/platform',
          kialiUrl: 'https://kiali.example.test/',
          jaegerUrl: 'https://tracing.example.test/search',
        },
      };`,
    }),
  );
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: { 'GET /dashboard/summary': summary },
  });

  await page.goto('/admin/ops');
  const expected: [string, string][] = [
    ['Grafana ↗', 'https://grafana.example.test/d/platform'],
    ['Kiali ↗', 'https://kiali.example.test/'],
    ['Jaeger / Tempo ↗', 'https://tracing.example.test/search'],
  ];
  for (const [name, href] of expected) {
    const link = page.getByRole('link', { name });
    await expect(link).toHaveAttribute('href', href);
    // 外部ツールは別タブで開く（本画面から遷移しない）。
    await expect(link).toHaveAttribute('target', '_blank');
  }
  await expect(page.getByRole('link', { name: '構成ビューア →' })).toHaveAttribute(
    'href',
    '/admin/config-viewer',
  );
  // 本画面からの設定変更は無い（書き込みの要求を出さない）。
  expect(traffic.calls.filter((c) => c.method !== 'GET')).toEqual([]);

  expectBffTrafficIsComplete(traffic);
});

test('M-095: opsLinks が無い配備では外部ツールのリンクを出さず、未設定であることを示す', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: { 'GET /dashboard/summary': summary },
  });

  await page.goto('/admin/ops');
  await expect(page.getByText('外部ツールの導線は未設定です。')).toBeVisible();
  await expect(page.getByRole('link', { name: /Grafana ↗|Kiali ↗|Jaeger/ })).toHaveCount(0);
  // 陽性対照: SC-11 への導線は config に依らず出る。
  await expect(page.getByRole('link', { name: '構成ビューア →' })).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});
