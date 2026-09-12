import { test, expect } from '@playwright/test';
import type { Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import type {
  AttributeValuesResponse,
  DocumentDto,
  PrivateNoteDto,
  PrivateNoteListResponse,
  TagDictionaryResponse,
} from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// NFR-12（アクセシビリティ）/ ADR-0031, ADR-0032: **実ブラウザ・実ビルド成果物に対する
// アクセシビリティの機械検査**（axe-core / WCAG 2.1 A・AA ＋ best-practice）。
//
// ■ 走査面は 5 つ（#1438 で 3 → 5）。共通シェル / SC-01 / SC-05 / **存在秘匿の 404** /
//   **SC-19 の確認ダイアログ**。後ろ 2 つは「畳まれた状態・例外の状態でしか描かれない DOM」であり、
//   **通常の画面を何面足しても届かない**——面を増やすときはこの軸（状態の種類）で選ぶ。
//
// ■ 静的検査（`eslint-plugin-jsx-a11y`）との役割分担
//   ESLint が見るのは **JSX のソースの形**（`<img>` に alt があるか、`<div onClick>` でないか）だけで、
//   **描いた結果**は 1 つも見ない。色のコントラスト比・ランドマークの重複・`aria-labelledby` の
//   指し先の実在・見出しの階層は、DOM と算出スタイルが揃って初めて判定できる。
//   **両方が要る**——片方だけでは「ソースは綺麗だが読めない画面」か「読めるが規約違反」のどちらかを見逃す。
//
// ■ 🔴 **ダークとライトの両方で測る。** 本リポジトリは 2 テーマを持ち（`lib/theme/theme.ts`）、
//   色は `[data-theme]` の段で差し替わる。**片方だけ測ると、もう片方のコントラスト不足は永久に緑である。**
//   テーマは `<html data-theme>` へ直接書く——`ThemeToggle` を押す経路は巡回の順序が OS 設定に依存し
//   （`nextTheme` の注記）、E2E から「いまどちらを測っているか」を決め打ちできない。
//
// ■ 何を測らない検査か（開示）
//   axe が自動で判定できるのは WCAG の一部である（目安として全体の 3〜4 割）。
//   「見出しが内容を表しているか」「操作順が意味に沿っているか」は人が見る。
//   キーボードだけで到達・退出できるかは `keyboard-navigation.smoke.spec.ts` が実際に踏む。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。

/** 適用されるテーマ。`system` は測らない——CI の OS 設定に依存し、どちらを測ったか決まらない。 */
const THEMES = ['light', 'dark'] as const;

/** 対象範囲フィルタが引く属性の候補値（SC-01 / SC-08 が共有する部品）。 */
const attributeValues: AttributeValuesResponse = { values: [] };

const doc: DocumentDto = {
  id: 'doc-1',
  title: '経費精算マニュアル',
  status: 'published',
  version: 3,
  attributes: { department: 'sales' },
  tags: ['tag-1'],
  createdAt: '2026-08-01T00:00:00Z',
  updatedAt: '2026-08-02T00:00:00Z',
};

const tags: TagDictionaryResponse = { tags: [{ id: 'tag-1', name: '経費', usageCount: 1 }] };

const GB = 1024 ** 3;

/** SC-19（個人資料）の 1 件。確認ダイアログを開くためだけに使う（`keyboard-navigation` と同じ形）。 */
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

/**
 * いま描かれている画面を axe に掛け、違反が 0 件であることを確かめる。
 *
 * 🔴 **`violations.length` ではなく内訳の配列を突き合わせる。** 件数だけを比べると、
 * 落ちたときのメッセージが `1 !== 0` になり、**どの規則がどの要素で落ちたのかが出力に残らない**
 * （原因の特定に再実行が要る）。規則 ID と対象セレクタまで組み立てて比較する。
 *
 * ■ 🔴 **タグに `best-practice` を含める**（#1438 / IADR-0442 決定 3）。
 *   `wcag2a` / `wcag2aa` だけでは **ランドマークの規則が 1 つも評価されない** ——
 *   `landmark-no-duplicate-main`・`landmark-main-is-top-level`・`region`・`landmark-unique` は
 *   いずれも `best-practice` タグにしか属さない。**入れ子の `<main>` は WCAG の失敗条件ではなく、
 *   したがって WCAG タグだけの走査では永久に緑である**（#1438 の陰性対照で実測した）。
 *
 * ■ `disableRules` は**規則単位の最後の逃げ道**である。ファイル単位・面単位の抑制はしない（裁定 7）。
 *   使うときは**構造上直せない理由**を IADR に書くこと。現状の使用箇所は 0 件である。
 */
async function expectNoAxeViolations(
  page: Page,
  surface: string,
  options: { disableRules?: string[] } = {},
): Promise<void> {
  // 🔴 **遷移（transition）を止めてから測る。** テーマを切り替えると色は 150ms かけて補間され、
  // その**途中の色**を axe が読むとコントラスト比が実際より低く出る（実測: 同じ要素・同じ設定で
  // ダークだけが落ち、ダークへ直接入ると落ちない——差は「切り替え直後かどうか」だけだった）。
  // 待ち時間を置く形では直さない: 何 ms 待てば足りるかは端末の速さで変わり、**CI でだけ落ちる**検査になる。
  await page.addStyleTag({
    content: '*, *::before, *::after { transition: none !important; animation: none !important; }',
  });

  for (const theme of THEMES) {
    // `<html data-theme>` を直接書く（`applyTheme()` が本番でしていることと同じ）。
    //
    // 🔴 **`globalThis` 経由で書く。** e2e の tsconfig は `lib: ["ES2023"]` で DOM を含まない
    // （`tsconfig.node.json`）。`sc04-wiki.smoke.spec.ts` と同じ作法である。**DOM lib を足す方向では
    // 直さない** —— 足すと **Node 側で走るテスト本体**でも `document` が型検査を通るようになり、
    // 実行時にしか落ちない書き方を型が許してしまう。
    await page.evaluate((value) => {
      const dom = globalThis as unknown as {
        document: { documentElement: { dataset: Record<string, string> } };
      };
      dom.document.documentElement.dataset.theme = value;
    }, theme);

    const builder = new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'best-practice']);
    const results = await (
      options.disableRules?.length ? builder.disableRules(options.disableRules) : builder
    ).analyze();
    const found = results.violations.map(
      (v) => `${v.id}[${v.impact ?? '-'}] ${v.nodes.map((n) => n.target.join(' ')).join(' / ')}`,
    );

    // ★ 陰性対照: 違反が無いこと。
    expect(found, `${surface} / ${theme}`).toEqual([]);
    // ★ 陽性対照: **axe が実際に走ったこと。** 注入に失敗しても `violations` は空配列になるため、
    // 「何も見ずに緑」と区別できない（`bundle-splitting.smoke.spec.ts` の ① と同じ理由）。
    // 合格した検査が 1 件も無い画面はあり得ない。
    expect(results.passes.length, `${surface} / ${theme} で axe の合格判定が 0 件`).toBeGreaterThan(
      0,
    );
  }
}

test('共通シェル: ヘッダ・左ナビ・パンくず・右レール・通知に WCAG 2.1 A/AA ＋ best-practice の違反が無い', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: { 'POST /attribute-values': attributeValues },
  });

  await page.goto('/ask');
  await expect(page.getByRole('heading', { name: 'ナレッジ検索・AI質問', level: 1 })).toBeVisible();

  // シェルの畳まれた部分も開いて測る。**閉じたままの器は axe の対象にならない**
  // （`hidden` な部分木は判定されない）ため、開かないと右レールと通知一覧は 1 度も測られない。
  await page.getByRole('button', { name: 'AI チャットを開く' }).click();
  await expect(page.getByRole('complementary', { name: 'AI チャットパネル' })).toBeVisible();
  await page.getByRole('button', { name: /通知（未読/ }).click();
  await expect(page.getByRole('region', { name: '通知一覧' })).toBeVisible();

  await expectNoAxeViolations(page, '共通シェル');

  expectBffTrafficIsComplete(traffic);
});

test('SC-01: 検索・AI質問の画面に WCAG 2.1 A/AA ＋ best-practice の違反が無い', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-01 主アクター「全利用者」。**ロール限定は無い**（管理ロールを与えない）。
    user: sessionUser([]),
    handlers: { 'POST /attribute-values': attributeValues },
  });

  await page.goto('/ask');
  await expect(page.getByRole('heading', { name: 'ナレッジ検索・AI質問', level: 1 })).toBeVisible();

  await expectNoAxeViolations(page, 'SC-01');

  expectBffTrafficIsComplete(traffic);
});

test('SC-05: 文書管理の画面（表・フィルタ）に WCAG 2.1 A/AA ＋ best-practice の違反が無い', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // 🔴 **運用者で測る。** 表・操作列は権限のある利用者にしか描かれない（IADR-0039）。
    user: sessionUser(['platform-operator']),
    handlers: { 'GET /documents': [doc], 'GET /tags': tags },
  });

  await page.goto('/admin/documents');
  await expect(page.getByRole('heading', { name: '文書一覧', level: 1 })).toBeVisible();
  // 表が描かれてから測る（空状態と表では検査される要素が別物である）。
  await expect(page.getByRole('cell', { name: '経費精算マニュアル' })).toBeVisible();

  await expectNoAxeViolations(page, 'SC-05');

  expectBffTrafficIsComplete(traffic);
});

// #1438 / IADR-0442 決定 3: 走査面を 3 → 5 へ。足した 2 面はどちらも
// **「壊れても他のどのテストも赤くならない」経路**である ——
// 存在秘匿の 404 は 9 本の spec が通るが、axe に掛けていたものは 1 本も無かった。

test('存在秘匿の 404: 権限外で開いた管理画面（NotFound）に WCAG 2.1 A/AA ＋ best-practice の違反が無い', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // IADR-0035 / IADR-0009: 管理ロールを与えない利用者で管理画面を開く。
    // `RequireRole` が `NotFound` を**共通シェルの内側**に描く（＝入れ子が起きる面そのもの）。
    user: sessionUser([]),
  });

  await page.goto('/admin/config-viewer');
  await expect(page.getByRole('heading', { name: '見つかりませんでした', level: 1 })).toBeVisible();

  await expectNoAxeViolations(page, '存在秘匿の 404');

  // ★ 陰性対照: 権限外では画面の API を 1 件も呼ばない（呼ぶと応答の有無から存在が読める）。
  // `installBffSession` は身元と通知だけを既定で返すので、構成 API を呼べば `unhandled` に積まれ、
  // 下の `expectBffTrafficIsComplete` が落ちる。
  expect(traffic.calls.map((c) => c.key)).toEqual(['GET /auth/me', 'GET /notifications']);

  expectBffTrafficIsComplete(traffic);
});

test('SC-19: 確認ダイアログを開いた状態に WCAG 2.1 A/AA ＋ best-practice の違反が無い', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: { 'GET /private-notes': noteList },
  });

  await page.goto('/my/notes');
  // 役割と表示名だけで引く（testid へ逃げない）。開き方は `keyboard-navigation.smoke.spec.ts` に倣う。
  await page.getByRole('button', { name: '削除する' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  // **開き切ってから測る。** 開く途中の DOM を読むと、閉じ込め前の状態が判定される。
  await expect(dialog.getByRole('button', { name: 'やめる' })).toBeFocused();

  await expectNoAxeViolations(page, 'SC-19 確認ダイアログ');

  // ★ 陰性対照: 測っただけで降りる。破壊的操作の要求は 1 件も出ない。
  expect(traffic.calls.map((c) => c.key)).not.toContain('DELETE /private-notes/note-1');

  expectBffTrafficIsComplete(traffic);
});
