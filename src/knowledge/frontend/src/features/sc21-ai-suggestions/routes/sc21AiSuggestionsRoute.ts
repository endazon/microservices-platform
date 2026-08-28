import { msg } from '@lingui/core/macro';
import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { FeatureBreadcrumb, PlanNavItem } from '@foundation/routing/featureRegistry';

// SC-21, UC-10, FR-18/FR-05: AI 提案一覧（05_screens: ルート /ai-suggestions・既定 ?state=pending）。
//
// 🔴 **本画面は棚卸し用の「従」であり、書き込みを一切しない。** 承認・却下の主導線は
// SC-03（文書詳細）であり、本画面は各行から SC-03 へ遷移させるだけである
// （05_screens §SC-21 入力/バリデーション 第 3 行）。**一括承認のボタンは置かない**（FR-18）。
//
// **ロール限定は無い**（05_screens §共通シェル: 利用者グループは ABAC の権限内で全利用者が
// 利用できる）。可視性はサーバ側が決め、権限のない文書に関する提案は件数にも現れない。
//
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const AiSuggestionListPage = lazyRouteComponent(
  () => import('../components/AiSuggestionListPage'),
  'AiSuggestionListPage',
);

/**
 * 状態フィルタの選択肢（05_screens §SC-21 入力/バリデーション）。
 *
 * 🔴 `all` は**状態の値ではなくフィルタの解除**である（後段の `AiSuggestionEndpoints.AnyState`）。
 */
export const STATE_OPTIONS = ['pending', 'approved', 'rejected', 'all'] as const;
export type StateOption = (typeof STATE_OPTIONS)[number];

/**
 * 種類フィルタの選択肢（すべて／リンク／タグ）。
 *
 * 🔴 **リンク提案とタグ提案で画面を分けない**（05_screens §SC-21「描いてはいけないもの」）。
 * 分けると片方が忘れられるためである。`all` は「絞らない」を意味し、後段へは送らない。
 */
export const KIND_OPTIONS = ['all', 'link', 'tag'] as const;
export type KindOption = (typeof KIND_OPTIONS)[number];

export interface AiSuggestionSearch {
  state: StateOption;
  kind: KindOption;
}

export const createSc21AiSuggestionsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/ai-suggestions',
    // SC-21, IADR-0124: **URL が絞り込みの単一情報源**である（SC-18 と同じ作法）。
    // クライアント状態ストアを持ち込まない —— 共有・再読込・戻るのいずれでも同じ一覧になる。
    //
    // URL は外部由来なので正規化する。**未知の値は既定へ倒す** —— 選択肢しか無い UI に
    // 「エラー状態」を持ち込まない（手打ちの `?state=maybe` で画面を壊さない）。
    // 値域の防壁はサーバ（400）に在り、ここは丸めるだけである。
    validateSearch: (raw: Record<string, unknown>): AiSuggestionSearch => ({
      // 05_screens §SC-21: **既定は pending**（URL に無くても pending である）。
      state: STATE_OPTIONS.find((s) => s === raw.state) ?? 'pending',
      kind: KIND_OPTIONS.find((k) => k === raw.kind) ?? 'all',
    }),
    component: AiSuggestionListPage,
  });

// 05_screens §共通シェル: 左ナビ「利用者」グループの「AI提案」（hi-fi モックの左レール準拠）。
export const sc21AiSuggestionsNav: PlanNavItem = {
  id: 'sc21-ai-suggestions',
  label: msg`AI提案`,
  to: '/ai-suggestions',
  group: 'user',
};

// 05_screens §共通シェル / #446: パンくず `ホーム / AI 提案`（crumb 実測。**モックは中黒に空白を持つ**。
// 左ナビは「AI提案」で空白が無い——モック側の表記ゆれであり、パンくずは実測どおりにする）。
export const sc21AiSuggestionsBreadcrumb: FeatureBreadcrumb = {
  routePath: '/ai-suggestions',
  group: 'user',
  label: msg`AI 提案`,
};
