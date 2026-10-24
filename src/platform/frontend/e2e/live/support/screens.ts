// NFR / IADR-0035 / IADR-0627（#2029 / E-42）: live-ro で「画面に到達する」を見る部品。画面を開き、本体の見出し（level 1）が出ることと、
// 画面が引く BFF の GET が 200 で返ることを同じ 1 回の表示で見る（UI の到達と実効境界を対で）。文言は ja のカタログから引く。
import { expect, type Page, type Response } from '@playwright/test';
import { ja } from './catalog';

const NOT_FOUND = () => ja('見つかりませんでした');

/** `path`（BFF の pathname）への GET の応答を待つ。query は見ない。 */
function bffGet(page: Page, path: string): Promise<Response> {
  return page.waitForResponse(
    (r) => r.request().method() === 'GET' && new URL(r.url()).pathname === path,
  );
}

/**
 * 画面を開き、見出し（msgid）が出て NotFound が出ないことを見て、`apis` の GET の応答を待つ。状態を pathname で返す
 * （200 であることは test の側で `toEqual` で見る。本文を見たいときは `bodies` から読む）。
 */
export async function openScreen(
  page: Page,
  path: string,
  headingMsgid: string,
  apis: string[],
): Promise<{ statuses: Record<string, number>; bodies: Map<string, Response> }> {
  // 見出しの確認が先に落ちても、待ちの Promise が未処理の reject にならないよう、遷移の前に allSettled へ渡しておく
  // （allSettled は個々の reject を受け止める。見出しで落ちたときに待ちを await して test の時間切れで隠すこともしない）。
  const settled = Promise.allSettled(apis.map((api) => bffGet(page, api)));
  await page.goto(path);
  await expect(page.getByRole('heading', { name: ja(headingMsgid), level: 1 })).toBeVisible();
  await expect(page.getByRole('heading', { name: NOT_FOUND(), level: 1 })).toHaveCount(0);
  const results = await settled;
  // 取りこぼした応答（待ちの失敗）は、その API の名前つきで失敗にする。
  const missed = apis.filter((_, i) => results[i].status === 'rejected');
  expect(missed, '画面が送るはずの GET の応答を受け取れなかった').toEqual([]);
  const bodies = new Map(
    apis.map((api, i) => [api, (results[i] as PromiseFulfilledResult<Response>).value] as const),
  );
  const statuses = Object.fromEntries([...bodies].map(([api, r]) => [api, r.status()]));
  return { statuses, bodies };
}

/** `apis` がすべて 200 の期待値（`toEqual` に渡す）。 */
export const allOk = (apis: string[]) => Object.fromEntries(apis.map((a) => [a, 200]));

/** 権限外の画面を開き、存在秘匿の NotFound が出て、本体の見出しが出ないことを見る。 */
export async function expectHidden(page: Page, path: string, headingMsgid: string) {
  await page.goto(path);
  await expect(page.getByRole('heading', { name: NOT_FOUND(), level: 1 })).toBeVisible();
  await expect(page.getByRole('heading', { name: ja(headingMsgid), level: 1 })).toHaveCount(0);
}
