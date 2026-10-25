import { test, expect } from './support/cspGuard';
import type { Page } from '@playwright/test';
import type { ConversionJobDto } from '../src/lib/api/generated/bff.schemas';
import {
  installBffSession,
  sessionUser,
  expectBffTrafficIsComplete,
  reply,
} from './support/bffSession';

// SC-07 (#503 / #1139): 変換ジョブ（`/admin/conversions`）のスクリーンレベル・スモーク。
//
// 🔴 **本画面は platform-admin / platform-operator 限定である**（IADR-0039 / IADR-0042）。
// 権限外では `RequireRole` が `NotFound` を描き、画面の存在を示さない（存在秘匿。IADR-0009）。
// **この出し分けは未認証のスモークでは 1 度も踏まれない。**
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない**（catch-all が認証ガード配下。#918）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// 再変換の権限別出し分け・図の補正・状態遷移は Vitest（単体）が引き続き担う。

test('unauthenticated visit to /admin/conversions redirects to /login', async ({ page }) => {
  await page.goto('/admin/conversions');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-07: an operator reaches the screen and its navigation entry', async ({ page }) => {
  const traffic = await installBffSession(page, {
    // 🔴 **運用者で測る。** 管理者だけで測ると `anyOf` から operator が落ちても気づけない。
    user: sessionUser(['platform-operator']),
    handlers: { 'GET /conversion/jobs': [] },
  });

  await page.goto('/admin/conversions');

  // ★ 陽性対照: 画面が描かれ、左ナビ「変換ジョブ」も出る（05_screens §共通シェル）。
  await expect(
    page.getByRole('heading', { name: '変換ジョブ（pandoc＋LLM）', level: 1 }),
  ).toBeVisible();
  await expect(page.getByRole('link', { name: '変換ジョブ' })).toBeVisible();
  // ★ 陽性対照: **0 件は 0 件として言う。** 取得失敗を空表示へ寄せる実装はここで区別される。
  await expect(page.getByText('該当する変換ジョブはありません。')).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('SC-07: a user with no administrative role gets the same not-found page', async ({ page }) => {
  // ★ 陰性対照: **応答を 1 つも用意しない。** 管理端点を呼べば `unhandled` に載って落ちる。
  const traffic = await installBffSession(page, { user: sessionUser([]) });

  await page.goto('/admin/conversions');

  // IADR-0009: 不在も権限による秘匿も同じ画面で応答する。
  await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
  await expect(page.getByRole('heading', { name: '変換ジョブ（pandoc＋LLM）' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: '変換ジョブ' })).toHaveCount(0);

  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /conversion/jobs');
  expectBffTrafficIsComplete(traffic);
});

// ───────────── M-084・M-085（#2019 / E-14）: 4 状態・失敗のみ・デッドレター・再変換の拒否と権限 ─────────────
//
// 期待値は計画 05_screens §SC-07（ジョブ状態 4 値・「失敗のみ」フィルタ・デッドレターは failed の内訳・
// 実行中の再変換は拒否・補正ありの再変換は明示確認・再変換と人手補正は管理者限定）から起こした。

function job(overrides: Partial<ConversionJobDto>): ConversionJobDto {
  return {
    id: 'aaaaaaaa-0000-0000-0000-000000000000',
    sourceId: 'src-1',
    sourceType: 'filesystem',
    originalPath: '規程集/経費精算.docx',
    status: 'succeeded',
    error: null,
    documentId: null,
    markdownUri: null,
    attempts: 1,
    deadLettered: false,
    maxAttempts: 5,
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:00:00Z',
    diagramsCoded: 0,
    diagramsRetained: 0,
    hasCorrection: false,
    hasBody: true,
    ...overrides,
  };
}

const JOBS: ConversionJobDto[] = [
  job({ id: 'queued00-0000-0000-0000-000000000000', originalPath: 'a.docx', status: 'queued' }),
  job({ id: 'process0-0000-0000-0000-000000000000', originalPath: 'b.docx', status: 'processing' }),
  job({ id: 'success0-0000-0000-0000-000000000000', originalPath: 'c.docx', status: 'succeeded' }),
  job({
    id: 'failed00-0000-0000-0000-000000000000',
    originalPath: 'd.pdf',
    status: 'failed',
    error: '形式に対応していません',
    attempts: 5,
    deadLettered: true,
  }),
];
const rowOf = (page: Page, path: string) =>
  page.getByRole('table', { name: '変換ジョブの一覧' }).getByRole('row').filter({ hasText: path });

test('M-084: 4 状態を文字で描き分け、失敗のジョブにはデッドレターの標識と試行回数が付く', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: { 'GET /conversion/jobs': JOBS },
  });

  await page.goto('/admin/conversions');
  // ★ 状態 4 値（queued / processing / succeeded / failed）は、それぞれ別の文字で示す（色だけに頼らない）。
  await expect(rowOf(page, 'a.docx').getByText('待機中', { exact: true })).toBeVisible();
  await expect(rowOf(page, 'b.docx').getByText('変換中', { exact: true })).toBeVisible();
  await expect(rowOf(page, 'c.docx').getByText('完了', { exact: true })).toBeVisible();
  await expect(rowOf(page, 'd.pdf').getByText('失敗', { exact: true })).toBeVisible();
  // ★ デッドレターは failed の内訳（5 値目ではない）。分母は契約の maxAttempts。
  await expect(rowOf(page, 'd.pdf').getByText('デッドレター（試行 5/5）')).toBeVisible();
  // ★ 陰性対照: 失敗していないジョブにデッドレターは付かない。
  for (const p of ['a.docx', 'b.docx', 'c.docx']) {
    await expect(rowOf(page, p).getByText(/デッドレター/)).toHaveCount(0);
  }

  expectBffTrafficIsComplete(traffic);
});

test('M-084: 「失敗」で絞り込むと status=failed で引き直し、選択肢は「すべて」と 4 状態である', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: {
      'GET /conversion/jobs': (call) =>
        new URLSearchParams(call.search).get('status') === 'failed'
          ? JOBS.filter((j) => j.status === 'failed')
          : JOBS,
    },
  });

  await page.goto('/admin/conversions');
  const filter = page.getByRole('combobox', { name: '状態で絞り込み' });
  await expect(filter.getByRole('option')).toHaveText([
    'すべて',
    '待機中',
    '変換中',
    '完了',
    '失敗',
  ]);
  await expect(filter).toHaveValue('');

  await filter.selectOption({ label: '失敗' });
  await expect(rowOf(page, 'a.docx')).toHaveCount(0);
  await expect(rowOf(page, 'd.pdf')).toBeVisible();

  const searches = traffic.calls
    .filter((c) => c.key === 'GET /conversion/jobs')
    .map((c) => c.search);
  expect(searches[0]).toBe('');
  expect(searches.at(-1)).toBe('?status=failed');

  expectBffTrafficIsComplete(traffic);
});

test('M-085: 運用者には再変換・人手補正のボタンが無く、管理者限定である旨を文字で示す', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: {
      'GET /conversion/jobs': [
        ...JOBS,
        job({
          id: 'retain00-0000-0000-0000-000000000000',
          originalPath: 'e.pptx',
          status: 'succeeded',
          diagramsRetained: 1,
        }),
      ],
    },
  });

  await page.goto('/admin/conversions');
  await expect(rowOf(page, 'd.pdf')).toBeVisible();
  await expect(page.getByRole('button', { name: '再変換', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: '人手補正', exact: true })).toHaveCount(0);
  await expect(rowOf(page, 'd.pdf').getByText('再変換は管理者のみ実行できます')).toBeVisible();
  await expect(rowOf(page, 'e.pptx').getByText('人手補正は管理者のみ実行できます')).toBeVisible();
  expect(traffic.calls.filter((c) => c.method !== 'GET')).toEqual([]);

  expectBffTrafficIsComplete(traffic);
});

test('M-085: 再変換は失敗のジョブにだけ出し、実行中で拒否（409）されたら再変換できない旨を示す', async ({
  page,
}) => {
  const failedId = 'failed00-0000-0000-0000-000000000000';
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /conversion/jobs': JOBS,
      // 一覧を取ってから押すまでの間に、別の操作で実行中になった（直列化による拒否）。
      [`POST /conversion/jobs/${failedId}/retry`]: reply(409, { error: 'not_retryable' }),
    },
  });

  await page.goto('/admin/conversions');
  // 陽性対照を先に待つ —— 描画前に「0 件」を数えると空振りで緑になる。
  await expect(
    rowOf(page, 'd.pdf').getByRole('button', { name: '再変換', exact: true }),
  ).toBeVisible();
  // ★ 実行中・待機中・完了には再変換を出さない（失敗だけ）。
  for (const p of ['a.docx', 'b.docx', 'c.docx']) {
    await expect(rowOf(page, p).getByRole('button', { name: '再変換', exact: true })).toHaveCount(
      0,
    );
  }
  await rowOf(page, 'd.pdf').getByRole('button', { name: '再変換', exact: true }).click();

  await expect(page.getByRole('alert')).toContainText(
    'このジョブは再変換できません（実行中、または失敗以外の状態です）。',
  );
  expect(traffic.calls.filter((c) => c.method === 'POST').map((c) => c.key)).toEqual([
    `POST /conversion/jobs/${failedId}/retry`,
  ]);

  expectBffTrafficIsComplete(traffic);
});

test('M-085: 補正のあるジョブの再変換は、補正が失われる旨を示して明示確認を求め、確認したときだけ破棄を指定して送る', async ({
  page,
}) => {
  const id = 'failed00-0000-0000-0000-000000000000';
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /conversion/jobs': [JOBS[3]].map((j) => ({ ...j, hasCorrection: true })),
      [`POST /conversion/jobs/${id}/retry`]: (call) =>
        new URLSearchParams(call.search).get('discardCorrections') === 'true'
          ? reply(202, {})
          : reply(409, { error: 'corrections_would_be_lost', correctedFigures: 2 }),
    },
  });

  await page.goto('/admin/conversions');
  await rowOf(page, 'd.pdf').getByRole('button', { name: '再変換', exact: true }).click();

  // ★ 確認: 失われる件数（サーバの値）と、破棄・取り消しの 2 つの選択肢。既定の焦点は取り消し側。
  const confirm = page.getByRole('alert').filter({ hasText: '補正は失われます' });
  await expect(confirm).toContainText('このジョブには人手補正が 2 件あります。');
  await expect(confirm.getByRole('button', { name: '取り消す' })).toBeFocused();

  // 確かめるのは振る舞いの 2 点だけである（確認の前に素の POST が何回出るかは実装の流れなので固定しない）。
  //   ① 取り消したときは、破棄指定つきの POST を送らない。
  //   ② 確認したあとで、破棄指定つきの POST を 1 回だけ送る。
  const discardRetries = () =>
    traffic.calls.filter(
      (c) =>
        c.key === `POST /conversion/jobs/${id}/retry` &&
        new URLSearchParams(c.search).get('discardCorrections') === 'true',
    );

  // ① 取り消す。確認が閉じ、破棄指定つきの POST は 1 件も無い。
  await confirm.getByRole('button', { name: '取り消す' }).click();
  await expect(confirm).toHaveCount(0);
  expect(discardRetries()).toHaveLength(0);

  // ② もう一度押して、確認したうえで破棄する。破棄指定つきの POST がちょうど 1 回出る。
  await rowOf(page, 'd.pdf').getByRole('button', { name: '再変換', exact: true }).click();
  const discard = page.getByRole('button', { name: '補正を破棄して再変換' });
  await expect(discard).toBeVisible();
  // 確認のボタンを押す前は、まだ破棄指定つきの POST を送っていない（確認より前に送れば赤）。
  expect(discardRetries()).toHaveLength(0);
  await discard.click();
  await expect(
    page.getByRole('status').filter({ hasText: '再変換を受け付けました。' }),
  ).toBeVisible();
  expect(discardRetries()).toHaveLength(1);

  expectBffTrafficIsComplete(traffic);
});
