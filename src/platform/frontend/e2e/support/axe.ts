import AxeBuilder from '@axe-core/playwright';
import type { Page, TestInfo } from '@playwright/test';
import { test as base, expect } from './cspGuard';
import { KNOWN_AXE_VIOLATIONS } from './axeKnownViolations';

// NFR-30（WCAG 2.2 レベル AA）/ ADR-0031 (#2015): **axe の走査を 1 か所に束ねる土台。**
//
// ■ タグは計画 NFR-30 の 5 つ ＋ `best-practice`（`WCAG_22_AA_TAGS`）。
//   NFR-30 は「機械検査（axe のタグ `wcag2a`・`wcag2aa`・`wcag21a`・`wcag21aa`・`wcag22aa`）が検出する
//   違反を 0 件に保つ」と定める。**どれか 1 つを落とすと、その版で加わった達成基準は永久に緑である**
//   （#2015 以前は `wcag2a`・`wcag2aa` だけで、2.1・2.2 の達成基準を 1 つも評価していなかった）。
//   `best-practice` は WCAG の適合タグではないが、ランドマークの規則（`region`・`landmark-*`）は
//   このタグにしか属さないので外さない（IADR-0442 決定 3）。
//
// ■ 🔴 **ダークとライトの両方で測る。** 色は `[data-theme]` の段で差し替わるため、片方だけ測ると
//   もう片方のコントラスト不足は永久に緑である。テーマは `<html data-theme>` へ直接書く
//   （`ThemeToggle` を押す経路は巡回の順序が OS 設定に依存し、どちらを測ったか決め打ちできない）。
//
// ■ 既知の違反は「指紋」で受け入れる（`axeKnownViolations.ts`。1 件ずつ理由つき）。
//   指紋は `<規則 ID> <対象セレクタ>` の 1 行で、**一致を求める**（部分集合ではない）——
//   増えれば落ち、**直して減っても台帳から消すまで落ちる**（受け入れが恒久化しないため）。

/** 計画 NFR-30 の機械検査のタグ（＋ランドマークを見る `best-practice`）。 */
const WCAG_22_AA_TAGS = [
  'wcag2a',
  'wcag2aa',
  'wcag21a',
  'wcag21aa',
  'wcag22aa',
  'best-practice',
] as const;

/** 適用されるテーマ。`system` は測らない——CI の OS 設定に依存し、どちらを測ったか決まらない。 */
const THEMES = ['light', 'dark'] as const;

type AxeFixtures = {
  /** NFR-30 のタグを付けた `AxeBuilder` を作る（面を絞るときは呼び出し側で `include()` する）。 */
  makeAxeBuilder: () => AxeBuilder;
  /** いま描かれている面を両テーマで走査し、違反が 0 件か台帳の指紋と一致することを確かめる。 */
  expectAxeClean: (surface: string) => Promise<void>;
};

export const test = base.extend<AxeFixtures>({
  // 第 2 引数は Playwright の慣用では `use` だが、react-hooks の規則が React の `use` と取り違えるため名前を変える。
  makeAxeBuilder: async ({ page }, provide) => {
    await provide(() => new AxeBuilder({ page }).withTags([...WCAG_22_AA_TAGS]));
  },
  expectAxeClean: async ({ page, makeAxeBuilder }, provide, testInfo) => {
    await provide((surface) => scan(page, makeAxeBuilder, surface, testInfo));
  },
});

export { expect };

async function scan(
  page: Page,
  makeAxeBuilder: () => AxeBuilder,
  surface: string,
  testInfo: TestInfo,
): Promise<void> {
  // 🔴 **遷移（transition）を止めてから測る。** テーマを切り替えると色は 150ms かけて補間され、
  // その**途中の色**を axe が読むとコントラスト比が実際より低く出る（#1438 の実測）。
  // 待ち時間を置く形では直さない: 何 ms 待てば足りるかは端末の速さで変わり、CI でだけ落ちる。
  await page.addStyleTag({
    content: '*, *::before, *::after { transition: none !important; animation: none !important; }',
  });

  for (const theme of THEMES) {
    // 🔴 **`globalThis` 経由で書く。** e2e の tsconfig は `lib: ["ES2023"]` で DOM を含まない
    // （`tsconfig.node.json`）。DOM lib を足すと、Node 側で走るテスト本体でも `document` が型検査を
    // 通るようになり、実行時にしか落ちない書き方を型が許してしまう。
    await page.evaluate((value) => {
      const dom = globalThis as unknown as {
        document: { documentElement: { dataset: Record<string, string> } };
      };
      dom.document.documentElement.dataset.theme = value;
    }, theme);

    const results = await makeAxeBuilder().analyze();
    await testInfo.attach(`axe ${surface} ${theme}`, {
      body: JSON.stringify(results.violations, null, 2),
      contentType: 'application/json',
    });

    // 🔴 **件数ではなく指紋の配列を突き合わせる。** 件数だけだと落ちたときに `1 !== 0` しか残らず、
    // どの規則がどの要素で落ちたのかを知るのに再実行が要る。
    const found = results.violations
      .flatMap((v) => v.nodes.map((n) => `${v.id} ${n.target.join(' ')}`))
      .sort();
    const known = (KNOWN_AXE_VIOLATIONS[`${surface} / ${theme}`] ?? [])
      .map((k) => k.fingerprint)
      .sort();
    expect(found, `${surface} / ${theme}`).toEqual(known);

    // ★ 陽性対照: **NFR-30 のタグで走ったこと。** `makeAxeBuilder` を経ずに組んだ builder や
    // タグの書き換えで 2.1・2.2 の達成基準が黙って落ちると、違反 0 件のまま緑になる。
    expect(results.toolOptions.runOnly, `${surface} / ${theme} の走査タグ`).toEqual({
      type: 'tag',
      values: [...WCAG_22_AA_TAGS],
    });
    // ★ 陽性対照: **axe が実際に走ったこと。** 注入に失敗しても `violations` は空配列になるため、
    // 「何も見ずに緑」と区別できない。合格した検査が 1 件も無い画面はあり得ない。
    expect(results.passes.length, `${surface} / ${theme} で axe の合格判定が 0 件`).toBeGreaterThan(
      0,
    );
  }
}
