import { useState } from 'react';
import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { Link, Outlet, useRouterState } from '@tanstack/react-router';
import { CircleUserRound } from 'lucide-react';
import { Button, Tag } from '@platform/ui';
import { useAuth } from '@foundation/auth/useAuth';
import { useRoles, hasAnyRole } from '@foundation/auth/roles';
import { navGroups } from '@foundation/routing/nav';
import type { NavItemView } from '@foundation/routing/nav';
import { breadcrumbTrail, breadcrumbNavLabel } from '@foundation/routing/breadcrumbs';
import { BreadcrumbLeafContext } from '@foundation/routing/breadcrumbLeaf';
import { appConfig } from '@foundation/config/runtimeConfig';
import { NotificationBell } from '@foundation/notifications/NotificationBell';
import { AiChatPanel } from '@foundation/ai-chat/AiChatPanel';
import { Notifications } from '@foundation/ui/notifications';

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

/** SC-16 アカウント設定（Keycloak アカウントコンソール）の URL。実行時 config から組み立てる。 */
export function accountConsoleUrl(authority: string): string {
  return `${authority.replace(/\/+$/, '')}/account`;
}

function NavLink({ item }: { item: NavItemView }) {
  // IADR-0124 決定 5: ナビはユニットが公開する**データ**であり、`to` は string 型のため
  // TanStack の型付き union では検査できない。到達性は router.test.ts が実行時に固定する。
  return (
    <Link
      to={item.to as '/ask'}
      className="block rounded px-2 py-1 text-sm text-[--color-brand] hover:underline"
    >
      {item.label}
    </Link>
  );
}

/**
 * パンくず（05_screens §共通シェル「パンくず・権限バッジ」。#446）。
 *
 * 段の組み立ては純関数 `breadcrumbTrail()` が持ち、ここは描画だけを行う
 * （Layout を描かずに段構成を検査できるようにするため）。
 *
 * 🔴 **色だけで意味を持たせない**（本リポの規約 / INDEX 決定 21）。グループのバッジは
 * 「管理」「運用」「個人」という**テキスト**を持ち、色を落としても意味が読める。
 * 🔴 **空のときは `<nav>` ごと描かない** —— 空の器が残ると「まだ読み込み中」に見える。
 */
function Breadcrumb({ leaf }: { leaf: string | undefined }) {
  const roles = useRoles();
  // いま居るルートの完全パス（`/docs/$id` のようにパラメータ表記のまま）。宣言の主キーである。
  //
  // `select` の戻り値型（`TSelected`）は **`Register` 宣言が見えている文脈でしか推論されない**。
  // 雛形（`templates/unit-template/frontend`）の型検査は `router.tsx` を含まないため、
  // そこでは素の `RouterState` に落ちて赤くなる（実測）。**推論に頼らず**、
  // 選択関数の戻り値を明示し、結果も同じ型で受ける。
  const routePath: string | undefined = useRouterState({
    select: (s): string | undefined => s.matches.at(-1)?.fullPath as string | undefined,
  });
  const trail = breadcrumbTrail({ routePath, leaf, roles });
  if (trail.length === 0) return null;

  return (
    <nav aria-label={breadcrumbNavLabel()} className="mb-3">
      <ol className="flex flex-wrap items-center gap-1.5 text-xs text-[--color-fg-muted]">
        {trail.map((seg, index) => (
          <li key={`${seg.kind}:${seg.label}`} className="flex items-center gap-1.5">
            {/* 区切りは装飾であり読み上げない（段の区切りは <ol>/<li> の構造が担う）。 */}
            {index > 0 && <span aria-hidden>/</span>}
            {seg.kind === 'group' ? (
              <Tag tone="accent">{seg.label}</Tag>
            ) : seg.kind === 'current' ? (
              <span aria-current="page" className="font-medium text-[--color-fg]">
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
  // 表示名は BFF セッションの身元（/bff/auth/me の name = preferred_username）から。
  const name = user?.name || i18n._(msg`ユーザー`);

  // 権限のある項目のみ表示する（requiresAnyRole 未指定は全員に表示）。
  // 絞り込みの結果 0 件になったグループは見出しごと落とす（存在秘匿。IADR-0035）。
  const groups = navGroups()
    .map((g) => ({
      ...g,
      items: g.items.filter((i) => !i.requiresAnyRole || hasAnyRole(roles, ...i.requiresAnyRole)),
    }))
    .filter((g) => g.items.length > 0);

  return (
    <div className="min-h-screen bg-[--color-surface] text-[--color-fg]">
      <header className="flex items-center justify-between border-b border-[--color-border] px-4 py-2">
        {/* 05_screens §共通シェル ［2026-08-04 確定］: ブランド表示名は「汎用プラットフォーム」で統一し、
            **ロケールによっても差し替えない**（固有名詞として扱う。en ロケールでも同じ文字列を表示し、
            **翻訳カタログの対象としない**。利用者裁定・質問票 第 1 回 Q13 / planning#184）。
            したがってここは**カタログを経由しないリテラル**である——カタログ経由にすると
            en の msgstr を書き換えるだけで差し替えられてしまい、check-i18n-catalogs.js は
            非空しか見ないため止まらない（IADR-0125 決定 8）。 */}
        {/* eslint-disable-next-line lingui/no-unlocalized-strings --
            05_screens §共通シェル ［2026-08-04 確定］「翻訳カタログの対象としない」による意図的な例外。 */}
        <span className="text-sm font-semibold text-[--color-fg]">汎用プラットフォーム</span>
        <div className="flex items-center gap-3">
          {/* FR-22 / IADR-0215: アプリ内通知の受け皿。**永続する通知**であり、下の
              `<Notifications />`（一過性のトースト）とは別物である。 */}
          <NotificationBell />
          {/* 05_screens §共通シェル: ユーザーアイコンから SC-16（アカウント設定）へ遷移する。
              SC-16 は Keycloak テーマ＝別ホスト配信のため、SPA のルータではなく外部遷移で開く。 */}
          <a
            href={accountConsoleUrl(appConfig().oidc.authority)}
            className="flex items-center gap-1.5 text-sm text-[--color-fg-muted] hover:underline"
            aria-label={i18n._(msg`アカウント設定（${name}）`)}
          >
            <CircleUserRound className="size-5" aria-hidden />
            <span>{name}</span>
          </a>
          <Button size="sm" onClick={() => void logout()}>
            {i18n._(msg`サインアウト`)}
          </Button>
        </div>
      </header>
      <div className="flex">
        <nav
          className="w-56 shrink-0 border-r border-[--color-border] p-3"
          aria-label={i18n._(msg`主要ナビゲーション`)}
        >
          {groups.map((g) => (
            <div key={g.id} className="mb-4">
              <h2 className="mb-1 px-2 text-xs font-semibold tracking-wide text-[--color-fg-muted]">
                {g.label}
              </h2>
              <ul>
                {g.items.map((i) => (
                  <li key={i.id}>
                    <NavLink item={i} />
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </nav>
        <main className="grow p-4">
          {/* 05_screens §共通シェル: パンくずと画面グループのバッジは本文の上に置く（#446）。 */}
          <Breadcrumb leaf={breadcrumbLeaf} />
          <BreadcrumbLeafContext.Provider value={setBreadcrumbLeaf}>
            <Outlet />
          </BreadcrumbLeafContext.Provider>
        </main>
      </div>
      <Notifications />
      {/* 05_screens §共通シェル: 右レール AI チャットパネル（#788）。
          既定は閉じており、閉じている間はランチャーのボタンだけを描く。 */}
      <AiChatPanel />
    </div>
  );
}
