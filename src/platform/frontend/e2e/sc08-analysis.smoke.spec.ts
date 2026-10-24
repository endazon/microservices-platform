import { test, expect } from './support/cspGuard';
import type { AiAnswerDto, AttributeValuesResponse } from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-08 (#134 / #1139): AI 分析ダッシュボード（`/analyze`）のスクリーンレベル・スモーク。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない。** 未知パスの受け皿（catchAllRoute）は
// `RequireAuth` 配下に居るため、ルートを消しても未認証なら同じく `/login` へ行く（#918 の実測）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// 分析依頼・結果・出典の中身は Vitest（単体）が引き続き担う。

// 対象範囲フィルタが引く属性の候補値（SC-01 / SC-08 が共有する部品）。
const attributeValues: AttributeValuesResponse = { values: [] };

test('unauthenticated visit to /analyze redirects to /login', async ({ page }) => {
  await page.goto('/analyze');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-08: renders for any authenticated user and requests no analysis before it is asked', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-08 主アクター「全利用者」。**ロール限定は無い**（管理ロールを与えない）。
    // 🔴 SC-08 は左ナビ「管理」ではなく**「利用者」グループ**に置く（05_screens §共通シェル）。
    user: sessionUser([]),
    handlers: { 'POST /attribute-values': attributeValues },
  });

  await page.goto('/analyze');

  // ★ 陽性対照: 見出し・副題・左ナビ「AI分析」。
  await expect(page.getByRole('heading', { name: 'AI分析依頼', level: 1 })).toBeVisible();
  await expect(page.getByText('範囲指定の分析依頼・結果・出典')).toBeVisible();
  await expect(page.getByRole('link', { name: 'AI分析' })).toBeVisible();

  // ★ 陰性対照: **開いただけで分析を投げない**（依頼は利用者の操作で初めて走る）。
  // 「何も出ない」だけでは実装が壊れている場合と区別できないため、**上の陽性対照と対で**読むこと。
  const keys = traffic.calls.map((c) => c.key);
  expect(keys.filter((k) => k.startsWith('POST /analysis'))).toEqual([]);

  expectBffTrafficIsComplete(traffic);
});

// ───────────── M-050（#2022 / E-17）: 対象チップ → 依頼文 → 実行 → 出典から SC-03 へ ─────────────
//
// 期待値は計画 05_screens §SC-08 から起こした。
// - 分析対象はタグ・部門・プロジェクトのチップで指定する（SC-01 と同じ候補の口・同じ表現。「フォルダ」は用いない）。
// - 分析内容を入力して実行すると結果を表示し、出典のリンクから SC-03（文書詳細）へ遷移する。
// - 権限外の文書は分析対象に含まれない（存在秘匿）。**黙って除外する** —— 除外の件数や「権限外があった」旨を出さない。
//
// 🔴 スタブは契約の写しである。権限外を除外する判定そのものは後段（AiAnalysisService・ABAC）が固定する。
// ここで固定するのは、選んだ範囲が要求に載ること、応答の出典がそのまま SC-03 への導線になること、
// 画面が除外について何も言わないことである。

const ANSWER: AiAnswerDto = {
  answer: '経費精算の差戻しは、領収書の添付漏れが最も多い。',
  citations: [
    {
      number: 1,
      documentId: 'aaaaaaaa-1111-4111-8111-111111111111',
      documentTitle: '経費精算マニュアル',
      chunkId: 'c-1',
      score: 0.9,
      snippet: '領収書を添付する',
      confidentiality: 'internal',
    },
  ],
  model: 'self-hosted-llm',
  inputTokens: 120,
  outputTokens: 40,
  answerId: 'answer-1',
};

test('M-050: チップで選んだ範囲と依頼文が要求に載り、結果の出典は SC-03 への導線になり、除外については何も言わない', async ({
  page,
}) => {
  const byAxis: Record<string, string[]> = { tags: ['経費'], department: ['finance'], project: [] };
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'POST /attribute-values': (call) => ({
        values: byAxis[(call.body as { key: string }).key] ?? [],
      }),
      'POST /analysis/analyze': ANSWER,
    },
  });

  await page.goto('/analyze');
  const filter = page.getByRole('region', { name: '対象範囲フィルタ' });
  // ★ 候補は応答の値そのもの。候補の無い軸（プロジェクト）は出さない。
  await expect(filter.getByRole('button')).toHaveCount(2);
  await filter.getByRole('button', { name: '経費', exact: true }).click();
  await filter.getByRole('button', { name: 'finance', exact: true }).click();

  await page.getByLabel('分析内容（指示）').fill('差戻しの理由を集計してください');
  await page.getByRole('button', { name: '分析実行' }).click();

  // ★ 結果と出典。出典は文書詳細（/docs/<id>）への導線である。
  await expect(page.getByText(ANSWER.answer)).toBeVisible();
  await expect(page.getByRole('link', { name: '経費精算マニュアル' })).toHaveAttribute(
    'href',
    '/docs/aaaaaaaa-1111-4111-8111-111111111111',
  );
  await expect(page.getByText('モデル: self-hosted-llm')).toBeVisible();

  // ★ 要求: 依頼文・タスク種別・選んだ範囲（タグ・部門）。
  const asks = traffic.calls.filter((c) => c.key === 'POST /analysis/analyze');
  expect(asks).toHaveLength(1);
  expect(asks[0].body).toMatchObject({
    instruction: '差戻しの理由を集計してください',
    range: { attributeFilters: { tags: ['経費'], department: ['finance'] } },
  });
  // ★ 陰性対照: 除外の件数・「権限外」の告知を出さない（存在秘匿）。陽性対照は上の結果と出典である。
  await expect(
    page.getByText(/件を除外|除外しました|権限外の文書が|閲覧できない文書が/),
  ).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});
