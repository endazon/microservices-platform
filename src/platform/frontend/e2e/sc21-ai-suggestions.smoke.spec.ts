import { test, expect } from '@playwright/test';
import type { AiSuggestion, EdgeTypeCatalogItem } from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-21 (#918 / #1139): AI 提案一覧（`/ai-suggestions`）のスクリーンレベル・スモーク。
//
// 🔴 **#918 の起票時の要求「一括承認が存在しないことを E2E で固定する」を、ここで満たす。**
// 当時は「認証済みの画面を E2E で実走できない」と判断して単体テストへ委譲したが、
// **その判断の前提が誤りだった**（#1099 が実測で覆した。IADR-0330）。行を描いたうえで
// 「一括選択も一括承認も行内承認も無い」を実ブラウザ・実ビルド成果物の上で固定する。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない。** 未知パスの受け皿（catchAllRoute）は
// `RequireAuth` 配下に居るため、ルートを消しても未認証なら同じく `/login` へ行く（#918 の実測）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。

const edgeTypes: EdgeTypeCatalogItem[] = [
  { id: 'relates-to', name: '関連する', layer: 'core', isSymmetric: true },
];

const linkSuggestion: AiSuggestion = {
  id: 'sug-1',
  kind: 'link',
  sourceDocumentId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
  targetDocumentId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
  edgeTypeId: 'relates-to',
  rationale: '同じ申請様式を参照している',
  state: 'pending',
  rejectedCount: 0,
  sourceDocumentTitle: '経費精算マニュアル',
  targetDocumentTitle: '出張旅費規程',
};

const tagSuggestion: AiSuggestion = {
  id: 'sug-2',
  kind: 'tag',
  sourceDocumentId: 'cccccccc-cccc-cccc-cccc-cccccccccccc',
  tagValue: '経費',
  rationale: '本文に経費の語が頻出する',
  state: 'pending',
  rejectedCount: 0,
  sourceDocumentTitle: '稟議フロー',
};

const handlers = {
  'GET /graph/suggestions': [linkSuggestion, tagSuggestion],
  'GET /graph/edge-types': edgeTypes,
};

test('unauthenticated visit to /ai-suggestions redirects to /login', async ({ page }) => {
  await page.goto('/ai-suggestions?state=pending');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-21: every row offers the document-detail hand-off and no approval of its own', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-21 主アクター「全利用者」。**ロール限定は無い**（管理ロールを与えない）。
    user: sessionUser([]),
    handlers,
  });

  await page.goto('/ai-suggestions?state=pending');

  // ★ 陽性対照: 見出し・位置づけの固定文言・左ナビ「AI提案」。
  await expect(page.getByRole('heading', { name: 'AI 提案一覧', level: 1 })).toBeVisible();
  await expect(page.getByTestId('suggestions-help')).toContainText(
    'まとめて承認する操作は提供していません。',
  );
  await expect(page.getByRole('link', { name: 'AI提案' })).toBeVisible();

  // ★ 陽性対照: リンク提案とタグ提案が**同じ一覧**に並ぶ（種類でルートを分けない）。
  await expect(page.getByText('経費精算マニュアル → 出張旅費規程（関連する）')).toBeVisible();
  await expect(page.getByText('稟議フロー に「経費」を付与')).toBeVisible();
  // 主要素 4: **全行が必ず SC-03 への導線を持つ。** 行数と導線の数が一致することまで見る
  // ——「1 行だけ導線がある」を件数の一致で落とす。
  await expect(page.getByRole('link', { name: '文書詳細で確認' })).toHaveCount(2);

  // ★ 陰性対照（#918 の要求そのもの）: **一括選択・一括承認・行内承認を描かない。**
  // 上の陽性対照が行の存在を示しているので、「何も描けていないから 0 件」ではないと言い切れる。
  await expect(page.getByRole('checkbox')).toHaveCount(0);
  await expect(page.getByRole('button', { name: /承認/ })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /却下/ })).toHaveCount(0);

  // ★ 陰性対照: 承認の口を**呼びにも行かない**（一覧から承認端点を叩く実装はここで落ちる）。
  const keys = traffic.calls.map((c) => c.key);
  expect(keys.filter((k) => k.includes('/approve') || k.includes('/reject'))).toEqual([]);

  expectBffTrafficIsComplete(traffic);
});
