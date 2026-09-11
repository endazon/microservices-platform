import { test, expect } from '@playwright/test';
import type { Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import type {
  AttributeValuesResponse,
  DocumentDto,
  TagDictionaryResponse,
} from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// NFR-12（アクセシビリティ）/ ADR-0031, ADR-0032: **実ブラウザ・実ビルド成果物に対する
// アクセシビリティの機械検査**（axe-core / WCAG 2.1 A・AA）。
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

/**
 * いま描かれている画面を axe に掛け、違反が 0 件であることを確かめる。
 *
 * 🔴 **`violations.length` ではなく内訳の配列を突き合わせる。** 件数だけを比べると、
 * 落ちたときのメッセージが `1 !== 0` になり、**どの規則がどの要素で落ちたのかが出力に残らない**
 * （原因の特定に再実行が要る）。規則 ID と対象セレクタまで組み立てて比較する。
 */
async function expectNoAxeViolations(page: Page, surface: string): Promise<void> {
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

    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
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

test('共通シェル: ヘッダ・左ナビ・パンくず・右レール・通知に WCAG 2.1 A/AA の違反が無い', async ({
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

test('SC-01: 検索・AI質問の画面に WCAG 2.1 A/AA の違反が無い', async ({ page }) => {
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

test('SC-05: 文書管理の画面（表・フィルタ）に WCAG 2.1 A/AA の違反が無い', async ({ page }) => {
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
