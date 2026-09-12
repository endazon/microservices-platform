import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';

// Issue #126 / IADR-0009: 存在秘匿。404（不在または権限による秘匿）は同一の画面で応答し、
// 資源の存在有無を推測させない。

/**
 * 見出しの id（`aria-labelledby` の指し先）。
 *
 * 🔴 **固定値である。`useId()` を使ってはならない。** React の生成 id は**描画位置**で変わるため、
 * 「未知パスと権限による秘匿で markup が完全に一致する」ことを `outerHTML` で固定している
 * `Layout.test.tsx` が割れる —— その一致こそが存在秘匿の担保そのものである。
 * `NotFound` は 1 画面に 1 つしか描かれないので、固定 id で衝突しない。
 */
const NOT_FOUND_HEADING_ID = 'not-found-heading';

/**
 * 存在秘匿の 404 本体。**共通シェル（`Layout`）の内側に置かれる前提の部品**である。
 *
 * ［2026-09-12 / UI/UX 改善］見た目をトークンへ寄せた（従前は素のインラインスタイル）。
 * ［2026-09-12 / #1438・IADR-0442 決定 1］**外側を `<main>` から `<section>` へ変えた。**
 * 従前は `Layout` の `<main id="main-content">` の中にもう 1 つ `<main>` が入る
 * ランドマークの重複で、axe の `landmark-no-duplicate-main` /
 * `landmark-main-is-top-level` / `landmark-unique` が落ちる状態だった（#1438 で実測）。
 * `<section>` ＋ 名前は `region` ランドマークになり、**`main` の中でも入れ子違反にならない**。
 *
 * 🔴 **要素の入れ替え以外は変えない。** 未知パスと権限による秘匿が**同一の markup** で出ることを
 * `Layout.test.tsx` が outerHTML の一致で固定しており、ここの構造は存在秘匿の担保そのものである。
 * **文言・役割・見出しレベルは一字も変えない。**
 */
export function NotFound() {
  return (
    <section aria-labelledby={NOT_FOUND_HEADING_ID} className="px-n4 py-n8 text-center">
      <h1 id={NOT_FOUND_HEADING_ID} className="mb-n2 text-[17px] font-medium text-fg">
        {i18n._(msg`見つかりませんでした`)}
      </h1>
      <p className="text-xs text-fg-muted">
        {i18n._(msg`お探しのページは存在しないか、アクセスできません。`)}
      </p>
    </section>
  );
}

/**
 * 共通シェルの**外**で 404 を描くときの器（`rootRoute.notFoundComponent`。IADR-0442 決定 2）。
 *
 * `NotFound` 自身が `<main>` を手放したため、シェルの外で描くと**ページにランドマークが 1 つも
 * 無くなる**。そこを埋めるのが本コンポーネントである。`LoginPage` / `ErrorBoundary` と同じく、
 * 「ルータの外・シェルの外で 1 枚だけ出る画面は自分で `<main>` を持つ」という形に揃う。
 *
 * 🔴 **存在秘匿は損なわれない。** URL で到達できる未知パスは必ず**シェル配下**の `catchAllRoute`
 * が受け（IADR-0124 決定 8。配線は `router.test.ts` が固定）、権限による秘匿も `RequireRole` が
 * シェルの内側に描く。**この器が出るのは「シェルの外で `notFound()` が投げられた場合」だけ**で、
 * 利用者が URL を突いて 2 つの応答を比較できる面ではない。
 */
export function NotFoundPage() {
  return (
    <main className="px-n4 py-n8">
      <NotFound />
    </main>
  );
}
