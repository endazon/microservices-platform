import { useState } from 'react';
import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';
import { Link, Outlet, useRouterState } from '@tanstack/react-router';
import { Button, Tag } from '@platform/ui';
import { useAuth } from '@foundation/auth/useAuth';
import { useRoles, hasAnyRole, PlatformRole } from '@foundation/auth/roles';
import { navGroups } from '@foundation/routing/nav';
import type { NavItemView } from '@foundation/routing/nav';
import { breadcrumbTrail, breadcrumbNavLabel } from '@foundation/routing/breadcrumbs';
import type { BreadcrumbSegmentView } from '@foundation/routing/breadcrumbs';
import { BreadcrumbLeafContext } from '@foundation/routing/breadcrumbLeaf';
import { appConfig } from '@foundation/config/runtimeConfig';
import { NotificationBell } from '@foundation/notifications/NotificationBell';
import { AiChatPanel } from '@foundation/ai-chat/AiChatPanel';
import { Notifications } from '@foundation/ui/notifications';
// `@foundation/*` の別名はユニットの区分ごとに切られており、テーマには割り当てが無い
// （`vite.config.ts` / `vitest.config.ts` の alias 一覧）。別名を増やすのは本作業の射程外なので相対で辿る。
import { ThemeToggle } from '../components/theme/ThemeToggle';

// Issue #126 / 05_screens §共通シェル: 認証済み領域の共通シェル。features は Outlet に載る。
// Issue #136 / IADR-0035: ナビはユニットの登録から導出し、権限外の項目は描画しない（存在秘匿）。
// ADR-0031 / IADR-0121 決定 4: 見た目は Tailwind v4 のトークン（@platform/ui）で表す。
//
// 本シェルが持つのは 05_screens §共通シェル のうち #490 の範囲——ブランド表示名・左ナビ（4 グループ）・
// ユーザーアイコン（→ SC-16）・通知——に加え、**パンくず・画面グループのバッジ（#446）**である。
// 🔴 **パンくず・権限バッジの帰属は #446 である。** ここには長く「#452」と書いてあったが誤りで、
// #452 は画面個別（SC-12 / SC-17）の作業であり、共通シェルの部品は #446 が持つ。
// 計画の「画面グループのバッジ」はパンくずの中のグループ段として描く——モックアップに
// 独立したバッジ要素は無い（`<div class="badges">` は SC 番号・進捗・仕様書リンクを持つ
// **モックアップのメタ情報**であり、crumb を持たない SC-13〜16 にも付いている）。
// **右レール AI チャットパネルは移行第 4 段（#788 / IADR-0121 決定 5）で入った**——
// シェル側の追加は `<AiChatPanel />` の 1 要素だけであり、開閉・履歴・SSE はすべて
// `foundation/ai-chat/` が持つ（シェルへ状態を持ち上げると、通知と同じ器がもう 1 つ増える）。
//
// ［2026-09-12 / UI/UX 改善］**骨格を hi-fi モックの 3 カラム grid へ合わせた**
// （`hi-fi/*.html` の `.hf` = `grid-template-columns:178px minmax(0,1fr) 244px`、
//  行は `nav` / `crumb` / `left main right` の 3 段）。従前は「ヘッダ ＋ flex（ナビ｜本文）」で、
// 右レールはどの列にも属さない `position:fixed` の板だった。
//   - **列は 3 つ固定である。** 右レール（AI チャット）は 3 列目そのものを描く
//     （`AiChatPanel` 側の責務。シェルは列を用意して置くだけ）。
//   - **パンくずの帯は「空なら `<nav>` ごと描かない」を維持する。** 段の計算を Layout へ引き上げたのは
//     そのためである——描かないときは行テンプレートも 2 段（`auto 1fr`）へ落とす。
//     grid の自動配置は「行を飛ばす」ことができないので、行数を可変にしないと
//     パンくずの無い画面で本文の行が `auto`（＝縦に伸びない）になる（実測）。
//   - **skip link を DOM 先頭に置く**（WCAG 2.4.1 Bypass Blocks）。178px の左レールは
//     キーボード利用者にとって毎画面 15 タブ前後の障壁である。

/** SC-16 アカウント設定（Keycloak アカウントコンソール）の URL。実行時 config から組み立てる。 */
export function accountConsoleUrl(authority: string): string {
  return `${authority.replace(/\/+$/, '')}/account`;
}

/**
 * ヘッダのロールタグ（hi-fi モックの `.hf-brand` 直後の `tag-outline`）。
 *
 * モックは「システム管理」「管理」「運用」を出し、**素の利用者の画面ではタグを描かない**
 * （SC-01 / SC-02 等の `.hf-nav` に tag が無い。実測）。ここも同じにする——
 * 全員に「利用者」と出すのは情報量ゼロの装飾であり、権限の手掛かりとしても機能しない。
 *
 * 表示は 1 つだけ（**代表ロール**）である。複数持つ利用者には強い側を出す——
 * 並べると 178px の左に収まらず、かつ「どちらで見えているのか」を伝えない。
 */
const REPRESENTATIVE_ROLE_LABELS: ReadonlyArray<readonly [string, MessageDescriptor]> = [
  [PlatformRole.Admin, msg`システム管理`],
  [PlatformRole.Operator, msg`運用`],
];

/** 代表ロールの表示名（該当が無ければ `undefined`＝タグを描かない）。 */
export function representativeRoleLabel(roles: readonly string[]): MessageDescriptor | undefined {
  return REPRESENTATIVE_ROLE_LABELS.find(([role]) => hasAnyRole(roles, role))?.[1];
}

function NavLink({ item }: { item: NavItemView }) {
  // IADR-0124 決定 5: ナビはユニットが公開する**データ**であり、`to` は string 型のため
  // TanStack の型付き union では検査できない。到達性は router.test.ts が実行時に固定する。
  //
  // 🔴 **`activeOptions` を既定（前方一致 ＋ 検索パラメータの部分一致）のままにしない。**
  // 前方一致だと `/settings/risk` に居るとき「設定」（`/settings`）も活性になり、
  // **左レールに `aria-current="page"` が 2 つ立つ**（AST の 3 画面で実測）。
  // 一方 `exact` だけでは検索パラメータまで完全一致を要求するため、`/wiki?page=…` で
  // 「Wiki閲覧」が消灯する（パンくずの親段と同じ罠。breadcrumbs 側のコメント参照）。
  // **パスだけを厳密に見る**のが左レールの正しい判定である。
  //
  // 活性の表示は TanStack が付ける `aria-current="page"`（実測: `STATIC_ACTIVE_PROPS`）と
  // モックの `.rail-l a.cur`（accent の面 ＋ 左端 2px の帯）の 2 本立てである——
  // 色だけに意味を載せない（INDEX 決定 21）。
  // 既定色と活性色を `className` へ同時に載せず `inactiveProps` 側へ置くのは、
  // Tailwind が同じプロパティの 2 クラスを**記述順ではなく生成順**で解決するためである。
  return (
    <Link
      to={item.to as '/ask'}
      activeOptions={{ exact: true, includeSearch: false }}
      className="block rounded-sm px-[9px] py-1"
      activeProps={{
        className: 'bg-accent-soft text-fg shadow-[inset_2px_0_0_var(--color-accent)]',
      }}
      inactiveProps={{ className: 'text-fg-muted hover:bg-surface-muted hover:text-fg' }}
    >
      {item.label}
    </Link>
  );
}

/**
 * パンくず（05_screens §共通シェル「パンくず・権限バッジ」。#446）。
 *
 * 段の組み立ては純関数 `breadcrumbTrail()` が持ち、ここは描画だけを行う
 * （Layout を描かずに段構成を検査できるようにするため）。**段の取得は Layout が行う**
 * ——行テンプレートの段数が「パンくずを描くか」に依るためである（上のコメント参照）。
 *
 * 🔴 **色だけで意味を持たせない**（本リポの規約 / INDEX 決定 21）。グループのバッジは
 * 「管理」「運用」「個人」という**テキスト**を持ち、色を落としても意味が読める。
 * 🔴 **空のときは `<nav>` ごと描かない** —— 空の器が残ると「まだ読み込み中」に見える。
 */
function Breadcrumb({ trail }: { trail: readonly BreadcrumbSegmentView[] }) {
  if (trail.length === 0) return null;

  return (
    <nav
      aria-label={breadcrumbNavLabel()}
      className="col-span-3 border-b border-divider px-n4 py-n2"
    >
      <ol className="flex flex-wrap items-center gap-1.5 text-xs text-fg-muted">
        {trail.map((seg, index) => (
          <li key={`${seg.kind}:${seg.label}`} className="flex items-center gap-1.5">
            {/* 区切りは装飾であり読み上げない（段の区切りは <ol>/<li> の構造が担う）。 */}
            {index > 0 && <span aria-hidden>/</span>}
            {seg.kind === 'group' ? (
              <Tag tone="accent">{seg.label}</Tag>
            ) : seg.kind === 'current' ? (
              <span aria-current="page" className="font-medium text-fg">
                {seg.label}
              </span>
            ) : (
              // 🔴 親の段は「いま居る画面」ではないので、TanStack の活性判定（既定は前方一致）に
              // `aria-current="page"` を付けさせない。SC-04（#1200）は「Wiki」を `/wiki` への親の段に置き、
              // 葉（題名）が現在地になる —— `/wiki?page=…` で既定のままだと親と葉の両方に
              // `aria-current` が立つ（Playwright で実測）。`exact` は検索パラメータまで完全一致を要求する。
              <Link
                to={seg.to as '/ask'}
                activeOptions={{ exact: true }}
                className="hover:underline"
              >
                {seg.label}
              </Link>
            )}
          </li>
        ))}
      </ol>
    </nav>
  );
}

export function Layout() {
  const { user, logout } = useAuth();
  const roles = useRoles();
  // パンくずの動的な葉（SC-03 の文書タイトル）。画面側が `useBreadcrumbLeaf` で与える。
  // setter は useState が返す安定した参照なので、context の値として渡しても再描画を誘発しない。
  const [breadcrumbLeaf, setBreadcrumbLeaf] = useState<string | undefined>(undefined);
  // いま居るルートの完全パス（`/docs/$id` のようにパラメータ表記のまま）。宣言の主キーである。
  //
  // `select` の戻り値型（`TSelected`）は **`Register` 宣言が見えている文脈でしか推論されない**。
  // 雛形（`templates/unit-template/frontend`）の型検査は `router.tsx` を含まないため、
  // そこでは素の `RouterState` に落ちて赤くなる（実測）。**推論に頼らず**、
  // 選択関数の戻り値を明示し、結果も同じ型で受ける。
  const routePath: string | undefined = useRouterState({
    select: (s): string | undefined => s.matches.at(-1)?.fullPath as string | undefined,
  });
  const trail = breadcrumbTrail({ routePath, leaf: breadcrumbLeaf, roles });
  // 表示名は BFF セッションの身元（/bff/auth/me の name = preferred_username）から。
  const name = user?.name || i18n._(msg`ユーザー`);
  // アバターの頭文字。**サロゲートペアを割らない**ため `Array.from` で 1 文字目を取る。
  const initial = Array.from(name)[0] ?? '';
  const roleLabel = representativeRoleLabel(roles);

  // 権限のある項目のみ表示する（requiresAnyRole 未指定は全員に表示）。
  // 絞り込みの結果 0 件になったグループは見出しごと落とす（存在秘匿。IADR-0035）。
  const groups = navGroups()
    .map((g) => ({
      ...g,
      items: g.items.filter((i) => !i.requiresAnyRole || hasAnyRole(roles, ...i.requiresAnyRole)),
    }))
    .filter((g) => g.items.length > 0);

  return (
    <>
      {/* WCAG 2.4.1: 本文へのスキップ。**DOM の先頭**に置き、フォーカスされたときだけ見える。
          grid の子にしないのは、置くだけで列を 1 つ消費する事故を避けるためである
          （`sr-only` は `position:absolute` なので現状は out-of-flow だが、
           見た目の都合でそれが外れた瞬間に骨格が崩れる）。 */}
      <a
        href="#main-content"
        className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded-md focus:border focus:border-border focus:bg-surface focus:px-n3 focus:py-n2 focus:text-sm focus:text-fg focus:shadow-md"
      >
        {i18n._(msg`本文へ移動`)}
      </a>
      <div
        className={`grid min-h-screen grid-cols-[178px_minmax(0,1fr)_244px] bg-bg text-fg ${
          trail.length === 0 ? 'grid-rows-[auto_1fr]' : 'grid-rows-[auto_auto_1fr]'
        }`}
      >
        {/* モックの `.hf-nav`: 3 列すべてに掛かる帯。 */}
        <header className="col-span-3 flex items-center gap-n4 border-b border-divider px-n4 py-n3">
          {/* 05_screens §共通シェル ［2026-08-04 確定］: ブランド表示名は「汎用プラットフォーム」で統一し、
              **ロケールによっても差し替えない**（固有名詞として扱う。en ロケールでも同じ文字列を表示し、
              **翻訳カタログの対象としない**。利用者裁定・質問票 第 1 回 Q13 / planning#184）。
              したがってここは**カタログを経由しないリテラル**である——カタログ経由にすると
              en の msgstr を書き換えるだけで差し替えられてしまい、check-i18n-catalogs.js は
              非空しか見ないため止まらない（IADR-0125 決定 8）。 */}
          <span
            className={
              roleLabel === undefined
                ? 'mr-auto flex items-center gap-[9px] text-[15px] font-medium text-fg'
                : 'flex items-center gap-[9px] text-[15px] font-medium text-fg'
            }
          >
            {/* モックの `.hf-brand i`: 9px の accent の角丸。純粋な装飾なので読み上げない。 */}
            <span aria-hidden className="size-[9px] shrink-0 rounded-[2px] bg-accent" />
            {/* eslint-disable-next-line lingui/no-unlocalized-strings --
                05_screens §共通シェル ［2026-08-04 確定］「翻訳カタログの対象としない」による意図的な例外。 */}
            <span>汎用プラットフォーム</span>
          </span>
          {roleLabel === undefined ? null : (
            <Tag tone="outline" className="mr-auto text-[9.5px]">
              {i18n._(roleLabel)}
            </Tag>
          )}
          {/* ADR-0031 / 利用者裁定（2026-09-12）: テーマ切替はヘッダに置く。 */}
          <ThemeToggle />
          {/* FR-22 / IADR-0215: アプリ内通知の受け皿。**永続する通知**であり、下の
              `<Notifications />`（一過性のトースト）とは別物である。 */}
          <NotificationBell />
          {/* 05_screens §共通シェル: ユーザーアイコンから SC-16（アカウント設定）へ遷移する。
              SC-16 は Keycloak テーマ＝別ホスト配信のため、SPA のルータではなく外部遷移で開く。
              モックの `.avatar` は 26px の丸に頭文字 1 字だけを置く。**字は読み上げない**
              （「田」だけ読まれても意味が無い）——名前は親リンクの `aria-label` が持つ。 */}
          <a
            href={accountConsoleUrl(appConfig().oidc.authority)}
            className="grid size-[26px] shrink-0 place-items-center rounded-full bg-surface-muted text-[10.5px] text-fg-muted hover:text-fg"
            aria-label={i18n._(msg`アカウント設定（${name}）`)}
          >
            <span aria-hidden>{initial}</span>
          </a>
          <Button size="sm" onClick={() => void logout()}>
            {i18n._(msg`サインアウト`)}
          </Button>
        </header>
        {/* モックの `.crumb`: ヘッダの下、3 列に掛かる帯（#446）。 */}
        <Breadcrumb trail={trail} />
        {/* モックの `.rail-l`: 178px の左レール。 */}
        <nav
          className="flex flex-col border-r border-divider p-n3 text-[12.5px]"
          aria-label={i18n._(msg`主要ナビゲーション`)}
        >
          {groups.map((g) => (
            <div key={g.id} className="mb-n3 last:mb-0">
              {/* モックの `.rail-l .rh`: 小さな大文字の見出し。 */}
              <h2 className="mb-n1 px-[9px] text-[10px] uppercase tracking-widest text-fg-muted">
                {g.label}
              </h2>
              <ul className="flex flex-col gap-0.5">
                {g.items.map((i) => (
                  <li key={i.id}>
                    <NavLink item={i} />
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </nav>
        {/* モックの `.main`。**skip link の着地点**なので `tabIndex={-1}` を持つ
            （フラグメント遷移でフォーカスを受け取れる要素にしておく）。 */}
        <main id="main-content" tabIndex={-1} className="min-w-0 px-n6 py-n4">
          <BreadcrumbLeafContext.Provider value={setBreadcrumbLeaf}>
            <Outlet />
          </BreadcrumbLeafContext.Provider>
        </main>
        {/* モックの `.rail-r`: 244px の右レール。**列そのものを AiChatPanel が描く**
            （#788 / IADR-0121 決定 5。開閉・履歴・SSE はすべて `foundation/ai-chat/` の責務）。 */}
        <AiChatPanel />
      </div>
      {/* 一過性のトースト（sonner）。**grid の外に置く**——重なりの層であって骨格の列ではない。 */}
      <Notifications />
    </>
  );
}
