import { test, expect } from '@playwright/test';
import type { Locator, Page } from '@playwright/test';
import type {
  AttributeValuesResponse,
  PrivateNoteDto,
  PrivateNoteListResponse,
} from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// NFR-12（アクセシビリティ）/ SC-01, SC-19, UC-11 / ADR-0031: **キーボードだけで操作できるか**を
// 実ブラウザで踏む（WCAG 2.1.1 Keyboard / 2.1.2 No Keyboard Trap / 2.4.1 Bypass Blocks /
// 2.4.3 Focus Order）。
//
// ■ 🔴 **axe では測れない層である。** `a11y.smoke.spec.ts` が見るのは「ある瞬間の DOM の静止画」で
//   あり、**タブ順・フォーカスの移動・退出**は 1 つも判定できない（axe 自身が「キーボード操作は
//   自動検査の対象外」と明示している）。skip link が在ることは axe でも判るが、**押して本文へ着く**
//   ことは押さないと判らない。**両方を持って初めて片側が塞がる。**
//
// ■ 測るもの（いずれも「壊れても他のどのテストも赤くならない」経路である）
//   1. skip link: 最初の Tab で現れ、Enter で `#main-content` へフォーカスが移ること（2.4.1）。
//      178px の左レールは、無ければ毎画面 10 タブ以上の障壁になる。
//   2. 左ナビ: Tab で辿り着け、Enter で遷移すること（2.1.1）。マウス専用の遷移を作らせない。
//   3. 確認ダイアログ: 初期フォーカスが**取消**にあり、Tab がダイアログの外へ出ず、
//      Esc で閉じて**トリガへフォーカスが戻る**こと（2.1.2 / 2.4.3）。
//      取り返しのつかない操作から、キーボードだけで確実に降りられることを固定する。
//
// ■ 引き方は**役割と表示名だけ**である（`getByTestId` へ逃げない）。テストが役割で引けない実装は、
//   支援技術からも引けない —— 引けなくなったときに直すべきなのは実装の側である。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。

const GB = 1024 ** 3;

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
  // #1441: 公開範囲・同期状態・タグ（契約 `PrivateNoteDto` の 5 項目）。
  visibility: 'private',
  sharedUserCount: 0,
  sharedGroupCount: 0,
  syncState: 'target',
  tags: [],
};

const noteList: PrivateNoteListResponse = {
  usage: { usedBytes: Math.round(0.01 * GB), limitBytes: GB, percent: 1 },
  notes: [note],
};

/** 対象範囲フィルタが引く属性の候補値（SC-01 が使う）。 */
const attributeValues: AttributeValuesResponse = { values: [] };

/**
 * 目的の要素にフォーカスが載るまで Tab を押す。
 *
 * 🔴 **上限を置き、超えたら失敗させる。** 無制限に押すと、到達できない実装でも
 * 「押し続けているうちにたまたま載った」形で緑になり得る。上限は
 * 「ヘッダ（テーマ切替・通知・アカウント・サインアウト）＋ パンくず ＋ 左ナビ」を
 * 十分に覆う値にする —— **到達する段数そのものは固定しない**（ナビの項目数は権限で変わる）。
 */
async function tabUntilFocused(page: Page, target: Locator, limit = 40): Promise<number> {
  // 「その要素であり、かつフォーカスされている」を 1 本の locator で表す。
  // `document.activeElement` を読む形にしないのは、e2e の tsconfig が DOM lib を持たないためである
  // （`a11y.smoke.spec.ts` の注記と同じ制約。こちらは Playwright だけで書ける）。
  const focusedTarget = target.and(page.locator(':focus'));
  for (let presses = 1; presses <= limit; presses += 1) {
    await page.keyboard.press('Tab');
    if ((await focusedTarget.count()) > 0) return presses;
  }
  throw new Error(
    `Tab を ${limit} 回押しても目的の要素へフォーカスが載らない（キーボードで到達できない）`,
  );
}

test('WCAG 2.4.1: 最初の Tab で skip link が現れ、Enter で本文へフォーカスが移る', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: { 'GET /private-notes': noteList },
  });

  await page.goto('/my/notes');
  await expect(page.getByRole('heading', { name: '個人資料', level: 1 })).toBeVisible();

  const skipLink = page.getByRole('link', { name: '本文へ移動' });

  // ★ 陰性対照: 押す前は**見えていない**（常時見えていると、視覚利用者には毎画面に無意味な帯が残る）。
  //
  // 🔴 **`toBeHidden()` では測れない。** `sr-only` は `display:none` ではなく
  // 「1px へ潰して clip する」実装であり、Playwright の可視判定（空でない矩形を持つか）は
  // **visible と答える**（実測）。`sr-only` というクラス名を突き合わせる形にもしない
  // ——それは実装の綴りであって挙動ではない。**見える大きさになるか**で測る。
  const collapsed = await skipLink.boundingBox();
  expect(collapsed?.width ?? 0, 'skip link が最初から見えている').toBeLessThanOrEqual(1);

  // ★ 陽性対照: **1 回目**の Tab で載り、そこで初めて見える大きさになる
  // （DOM の先頭に置く意味はここにある。2 回目以降では遅い）。
  await page.keyboard.press('Tab');
  await expect(skipLink).toBeFocused();
  const expanded = await skipLink.boundingBox();
  expect(
    expanded?.width ?? 0,
    'フォーカスしても skip link が見える大きさにならない',
  ).toBeGreaterThan(1);

  await page.keyboard.press('Enter');

  // 着地点は `<main id="main-content" tabIndex={-1}>`。**フォーカスが実際に移る**ことまで見る
  // —— URL のフラグメントだけを見ると、着地点が受け取れない実装でも緑になる。
  await expect(page.getByRole('main')).toBeFocused();

  expectBffTrafficIsComplete(traffic);
});

test('WCAG 2.1.1: 左ナビへ Tab で到達でき、Enter で画面が遷移する', async ({ page }) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /private-notes': noteList,
      'POST /attribute-values': attributeValues,
    },
  });

  await page.goto('/my/notes');
  await expect(page.getByRole('heading', { name: '個人資料', level: 1 })).toBeVisible();

  // 左ナビ（`<nav aria-label="主要ナビゲーション">`）の中のリンクだけを引く
  // —— パンくずにも同名のリンクが出る画面があるため、器で絞る。
  const mainNav = page.getByRole('navigation', { name: '主要ナビゲーション' });
  const searchLink = mainNav.getByRole('link', { name: '検索・質問' });

  await tabUntilFocused(page, searchLink);
  await page.keyboard.press('Enter');

  // ★ 陽性対照: 実際に遷移する（`onClick` だけで遷移する実装はここで落ちる）。
  await expect(page).toHaveURL(/\/ask$/);
  await expect(page.getByRole('heading', { name: 'ナレッジ検索・AI質問', level: 1 })).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('WCAG 2.1.2 / 2.4.3: 確認ダイアログは取消から始まり、Tab が外へ出ず、Esc でトリガへ戻る', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: { 'GET /private-notes': noteList },
  });

  await page.goto('/my/notes');
  const trigger = page.getByRole('button', { name: '削除する' });
  await expect(trigger).toBeVisible();

  // **キーボードで開く**（クリックで開くと、押した指のフォーカスが混ざって復帰の検証が鈍る）。
  await trigger.focus();
  await page.keyboard.press('Enter');

  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();

  // ★ 陽性対照: 初期フォーカスは**取消**（`DialogContent` の `initialFocus`）。
  // 実行側に載ると、開いた直後の Enter で破壊的操作が走る。
  const cancel = dialog.getByRole('button', { name: 'やめる' });
  const confirm = dialog.getByRole('button', { name: '削除する' });
  await expect(cancel).toBeFocused();

  // ★ 陰性対照: Tab はダイアログの中で巡回し、**背後の画面へ出ない**（2.1.2 の裏返し＝
  // 閉じ込めが効いていること）。出る実装では、下の 2 つの `toBeFocused` が両方とも落ちる。
  await page.keyboard.press('Tab');
  await expect(confirm).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(cancel).toBeFocused();

  // ★ 陽性対照: Esc で閉じ、**トリガへフォーカスが戻る**。戻らないとフォーカスは body へ落ち、
  // キーボード利用者は毎回ページの先頭からやり直すことになる。
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(trigger).toBeFocused();

  // ★ 陰性対照: 取消で降りた以上、削除の要求は 1 件も出ない。
  expect(traffic.calls.map((c) => c.key)).not.toContain('DELETE /private-notes/note-1');

  expectBffTrafficIsComplete(traffic);
});
