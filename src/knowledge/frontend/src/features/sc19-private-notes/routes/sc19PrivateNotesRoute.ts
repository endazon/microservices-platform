import { msg } from '@lingui/core/macro';
import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';

// SC-19, UC-11, FR-19: 個人資料管理（05_screens: ルート /my/notes・削除済みタブは /my/notes?tab=trash）。
//
// 🔴 **本画面は本文を持たない。** リッチエディタも本文編集の導線も描かない
// （ADR-0046 D-02。本文を書く経路は Obsidian 連携＝SC-20 だけである）。
//
// 🔴 **管理者導線を置かない。** 表示範囲は本人が所有する資料に限り、他人の資料は件数にも現れない
// （05_screens §SC-19「描いてはいけないもの」・主アクター）。**ロール限定も無い**
// （全利用者が使う。可視性はサーバ側が主体で決める）。
//
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const PrivateNotesPage = lazyRouteComponent(
  () => import('../components/PrivateNotesPage'),
  'PrivateNotesPage',
);

/**
 * タブの選択肢（05_screens §SC-19 主要素 9「『削除済み』フィルタまたは別タブ」）。
 *
 * 🔴 **タブは絞りであって別の問い合わせではない。** 後段は削除済みも同じ一覧に載せて返すので、
 * `active` / `trash` は同じ応答の描き分けにすぎない（容量の内訳と件数バッジも同じ応答から数える）。
 */
// **export しない**（値を外から使う画面が無い。型 `TabOption` だけを公開する）。
const TAB_OPTIONS = ['active', 'trash'] as const;
export type TabOption = (typeof TAB_OPTIONS)[number];

export interface PrivateNotesSearch {
  tab: TabOption;
  /** タイトルの部分一致（05_screens §SC-19 主要素 6）。空文字は絞り込みなし。 */
  q: string;
}

export const createSc19PrivateNotesRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/my/notes',
    // SC-19, IADR-0124: **URL が絞り込みの単一情報源**である（SC-18 / SC-21 と同じ作法）。
    // 計画が `?tab=trash` を明示しているので、タブもクライアント状態ではなく URL に持つ ——
    // 共有・再読込・戻るのいずれでも同じ一覧になる。
    //
    // URL は外部由来なので正規化する。**未知の値は既定へ倒す**（手打ちの `?tab=all` で画面を壊さない）。
    validateSearch: (raw: Record<string, unknown>): PrivateNotesSearch => ({
      tab: TAB_OPTIONS.find((t) => t === raw.tab) ?? 'active',
      q: typeof raw.q === 'string' ? raw.q : '',
    }),
    component: PrivateNotesPage,
  });

// 05_screens §共通シェル: 左ナビ「個人」グループの「個人資料」。
// 🔴 **項目名は「個人資料」で固定**（§用語。「マイスペース」「個人メモ」は使わない）。
export const sc19PrivateNotesNav: PlanNavItem = {
  id: 'sc19-private-notes',
  label: msg`個人資料`,
  to: '/my/notes',
  group: 'personal',
};
