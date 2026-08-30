import { msg } from '@lingui/core/macro';
import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { FeatureBreadcrumb, PlanNavItem } from '@foundation/routing/featureRegistry';
import { normalizeAiSuggestionSearch } from '../types/suggestionVocabulary';

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

export const createSc21AiSuggestionsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/ai-suggestions',
    // SC-21, IADR-0124: **URL が絞り込みの単一情報源**である（SC-18 と同じ作法）。
    // クライアント状態ストアを持ち込まない —— 共有・再読込・戻るのいずれでも同じ一覧になる。
    //
    // 正規化そのものは `types/suggestionVocabulary.ts` の純関数が持つ（画面を描かずに固定できる）。
    validateSearch: normalizeAiSuggestionSearch,
    component: AiSuggestionListPage,
  });

// 05_screens §共通シェル: 左ナビ「利用者」グループの「AI提案」（hi-fi モックの左レール準拠）。
export const sc21AiSuggestionsNav: PlanNavItem = {
  id: 'sc21-ai-suggestions',
  label: msg`AI提案`,
  to: '/ai-suggestions',
  group: 'user',
};

// 05_screens §共通シェル / #446: パンくず `ホーム / AI提案`。
// 🔴 **モックの crumb は「AI 提案」（空白あり）だが、左ナビは「AI提案」（空白なし）**である。
// SC-18（crumb「知識グラフ」／左ナビ「ナレッジグラフ」）とまったく同じ型の食い違いなので、
// **同じ基準で裁く** —— 計画が「同じものに 2 つの名前があると食い違う」ことを名指しで避けている
// （§用語）以上、シェルの中で 1 つの名前に揃える。表記ゆれは計画へ環流する。
//
// 🔴 **当初は SC-18 と逆に「モック実測どおり」を採っていた**（AI レビューが検出）。
// 空白 1 つの違いなので実害は小さいが、**同一画面の左ナビとパンくずが別々の表記を同時に出す**形になり、
// SC-18 で採った基準をここでは適用していなかった。**基準は画面ごとに変えない。**
export const sc21AiSuggestionsBreadcrumb: FeatureBreadcrumb = {
  routePath: '/ai-suggestions',
  group: 'user',
  label: msg`AI提案`,
};
