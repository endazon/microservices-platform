import type { PlanNavItem } from '@foundation/routing/featureRegistry';
import type { ShellRoute } from '@foundation/routing/shell';
import { createSampleRoute, sampleNav } from './sample';

// テンプレート: ユニットの束ね役。platform の合成点
// （`src/platform/frontend/src/features/index.ts`）がここから 2 つを import してスプレッドする
// （ADR-0031 / IADR-0124 決定 1 / IADR-0056）。合成点への追記は次の 3 行である:
//
//   import { createSampleUnitRoutes, sampleUnitNavItems } from '@<unit>/features';
//   createUnitRoutes へ  ...createSampleUnitRoutes(shell)
//   planNavItems へ      ...sampleUnitNavItems
//
// **ルートとナビは別経路である。** 片方だけ足すと「画面は開けるのに左ナビに出ない」
// （あるいはその逆）になる。

/**
 * 本ユニットの画面を 1 本のタプルにして公開する。
 *
 * **戻り値へ型注釈を書いてはならない。** `readonly AnyRoute[]` などを付けた瞬間に
 * ルート ID とパスの union が失われ、`useSearch({ from })` も `<Link to>` も静的検査されなくなる
 * （IADR-0124 §実測）。**タプルであること（`as const`）が型安全の必要条件**であり、
 * `flatMap` や中間変数を挟むのも同じ理由で不可である。画面を足すときはタプルへ 1 行足す。
 */
export const createSampleUnitRoutes = (shell: ShellRoute) => [createSampleRoute(shell)] as const;

/**
 * 左ナビへ出す項目。
 *
 * `PlanNavItem` で受けるため、`group` の宣言漏れは `tsc` が落とす（総称フォールバックが無く、
 * 宣言漏れは「どのグループにも属さず静かに消える」ことを意味するため）。
 * ナビに出さない画面（一覧・検索からのみ到達する詳細など）はここへ載せない。
 */
export const sampleUnitNavItems: readonly PlanNavItem[] = [sampleNav];
