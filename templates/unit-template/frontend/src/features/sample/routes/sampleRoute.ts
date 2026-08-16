import { msg } from '@lingui/core/macro';
import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';

// テンプレート: feature のルート定義。計画 13_frontend-stack §ディレクトリ構成 の `routes/`。
//
// ADR-0031 / IADR-0124 決定 1: 可変ユニットは **`(shell: ShellRoute) => Route` のファクトリ**で
// ルートを公開する。platform を import せず、共通シェルを引数で受け取る——これが
// 「platform → 可変ユニットの参照禁止」（IADR-0056 決定 3）を保ったまま型付きルート木を組む唯一の形である。
//
// **旧契約 `FeatureModule { id, routes: {path, element}[], nav }` は使わない。** あれは本リポジトリから
// 変更できないユニット（`src/ai-stock-trading`。IADR-0120）のための互換ブリッジであり、
// `src/README.md` §ユニットをサブモジュールとして追加する場合 項 4 が「新規ユニットでは使わない」と
// 定めている。旧契約で書いたルートは型付きルート木の外側に置かれ、`<Link to>` の静的検査が効かない。
//
// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const SamplePage = lazyRouteComponent(() => import('../components/SamplePage'), 'SamplePage');

export const createSampleRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    // 共通シェル配下の絶対表記。実ユニットでは計画（05_screens）のルート表の値を使う。
    path: '/sample',
    component: SamplePage,
  });

// 05_screens §共通シェル: 左ナビへ出す項目。
//
// **`group` は必須である**（型 `PlanNavItem` が `tsc` で強制する）。総称としてのフォールバック
// （「その他」）は ［2026-08-04 確定］で廃止されたため、宣言し忘れた項目は**どのグループにも属さず
// 静かに消える**——ルートは載るのに左ナビに出ない、という追跡しにくい壊れ方をする。
// 値は計画の 4 グループ（`user` / `personal` / `admin` / `ops`）から選ぶ。
//
// **本リポジトリの計画に属さないユニット**（別プロジェクト）は `group` を宣言せず、代わりに合成点の
// `unitNavGroups` へ**ユニットの機能名**を見出しとするグループを 1 要素足す（IADR-0125 決定 9）。
//
// ADR-0031 / IADR-0125 決定 6: 表示名は **MessageDescriptor**（`msg` マクロ）で持ち、解決は描画時に行う。
// ここは**モジュール初期化時**に評価されるため、翻訳済みの文字列で持つとロケール切替に追随しない。
export const sampleNav: PlanNavItem = {
  id: 'sample',
  label: msg`サンプル`,
  to: '/sample',
  group: 'user',
};
