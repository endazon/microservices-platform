import { test, expect } from './support/cspGuard';
import type { CitationDto, PrivateNoteDto } from '../src/lib/api/generated/bff.schemas';
import {
  installBffSession,
  sessionUser,
  expectBffTrafficIsComplete,
  sse,
} from './support/bffSession';

// NFR / IADR-0661（#2039 / E-80）: 視覚回帰。**「見え方そのものが規約」の 3 面だけ**を基準画像と突き合わせる
// （E2E 基準書 §8）。対象は NotFound（存在秘匿の 404）・SC-19 の削除確認ダイアログ・SC-01 の出典ラベル。
//
// 🔴 **公式 Docker イメージの中でしか走らせない。** スクリーンショットは OS・フォント・ブラウザで揺れる。
// 基準画像（`visual.spec.ts-snapshots/`）は `pnpm run test:e2e:visual`（イメージは `package.json` で版を固定）で
// 作り、同じ経路で比べる。`playwright.config.ts` は `PW_VISUAL=1` のときだけ project `visual` を持ち、
// `smoke` は本ファイルを拾わない —— **PR の必須チェック（`frontend.yml` の `e2e`）には入らない。**
//
// 機能の正しさ（文言・遷移・通信）は各画面の `sc<NN>-*.smoke.spec.ts` が持つ。ここは**見え方だけ**を見る。
// 揮発する値（日時・ユーザー名・件数）を写さないよう、写す範囲を各面の要素に絞る。撮る直前にポインタを
// 画面の角へ外し、hover の状態を写さない（基準書 §8 の出典である公式文書の作法）。

const VISUAL = { tag: ['@p2', '@visual'] };

// IADR-0009 / IADR-0442: 権限外で開いた管理画面は共通シェルの内側に NotFound を描く。
test(
  'NotFound: 権限外で開いた管理画面の存在秘匿の 404 が基準画像と一致する',
  VISUAL,
  async ({ page }) => {
    const traffic = await installBffSession(page, { user: sessionUser([]) });

    await page.goto('/admin/config-viewer');
    const notFound = page.getByRole('region', { name: '見つかりませんでした' });
    await expect(notFound.getByRole('heading', { level: 1 })).toBeVisible();

    await page.mouse.move(0, 0);
    await expect(notFound).toHaveScreenshot('notfound.png');

    expectBffTrafficIsComplete(traffic);
  },
);

// SC-19, FR-19: 05_screens §SC-19「削除の確認ダイアログ（論理削除）」。
test('SC-19: 個人資料の削除確認ダイアログが基準画像と一致する', VISUAL, async ({ page }) => {
  const note: PrivateNoteDto = {
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
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes': {
        usage: { usedBytes: 1024 ** 2, limitBytes: 1024 ** 3, percent: 1 },
        notes: [note],
      },
    },
  });

  await page.goto('/my/notes');
  await page.getByRole('button', { name: '削除する' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('削除しても容量は空きません（90 日間保管されます）');

  await page.mouse.move(0, 0);
  await expect(dialog).toHaveScreenshot('sc19-delete-confirm.png');

  expectBffTrafficIsComplete(traffic);
});

// SC-01, FR-04: 05_screens §SC-01「区別の表示方法」—— 出典の種別はアイコン＋ラベル、機密区分は表示名で出す。
test('SC-01: 出典の種別ラベルと機密区分の表示が基準画像と一致する', VISUAL, async ({ page }) => {
  const citation = (
    number: number,
    documentId: string,
    documentTitle: string,
    confidentiality: string,
  ): CitationDto => ({
    number,
    documentId,
    documentTitle,
    chunkId: `chunk-${number}`,
    sourceUri: null,
    score: 0.9 - number / 10,
    snippet: `${documentTitle}の抜粋`,
    confidentiality,
    docScope: 'organization',
  });
  // 機密区分は契約の値集合 4 値をすべて写す（表示名は用語集「機密区分」が正）。
  const citations = [
    citation(1, 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '経費精算マニュアル', 'internal'),
    citation(2, 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', '出張旅費規程', 'public'),
    citation(3, 'cccccccc-cccc-cccc-cccc-cccccccccccc', '役員報酬規程', 'confidential'),
    citation(4, 'dddddddd-dddd-dddd-dddd-dddddddddddd', '個別人事記録', 'restricted'),
  ];
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'POST /attribute-values': { values: [] },
      'POST /analysis/ask/stream': sse([
        { event: 'citations', data: { citations } },
        { event: 'token', data: { text: '締め日は毎月 25 日です[1]。' } },
        {
          event: 'done',
          data: {
            answerId: '11111111-1111-1111-1111-111111111111',
            model: 'stub-model',
            inputTokens: 10,
            outputTokens: 5,
          },
        },
      ]),
      'GET /wiki/pages': [],
    },
  });

  await page.goto('/ask');
  await page.getByRole('textbox', { name: '質問・キーワード' }).fill('経費精算の締め日は？');
  await page.getByRole('button', { name: '送信', exact: true }).click();
  const list = page
    .getByRole('main')
    .getByRole('list')
    .filter({ has: page.getByRole('listitem').filter({ hasText: /^\[1\]/ }) });
  await expect(list.getByRole('listitem')).toHaveCount(citations.length);

  await page.mouse.move(0, 0);
  await expect(list).toHaveScreenshot('sc01-citation-labels.png');

  expectBffTrafficIsComplete(traffic);
});
