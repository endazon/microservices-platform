import { msg } from '@lingui/core/macro';
import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';

// SC-20, UC-11, FR-20: Obsidian 連携設定（05_screens: ルート /my/obsidian）。
//
// 🔴 **管理者承認のステップを置かない**（05_screens §SC-20「描いてはいけないもの」）。
// 私物端末を認めるため、端末は複数・本人管理である。**個別失効は端末紛失時の唯一の防御線**であり、
// 状態を理由に隠さない。
//
// 🔴 **他利用者の同期設定を見る導線・組織文書を同期する導線を置かない**（同上）。
//
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const ObsidianSettingsPage = lazyRouteComponent(
  () => import('../components/ObsidianSettingsPage'),
  'ObsidianSettingsPage',
);

export const createSc20ObsidianSettingsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/my/obsidian',
    // 絞り込みも並べ替えも無い画面なので、URL に持つ状態は無い。
    // **発行直後のトークンは URL に載せない**（履歴・共有・再読込のいずれでも漏れるため）。
    component: ObsidianSettingsPage,
  });

// 05_screens §共通シェル: 左ナビ「個人」グループの「Obsidian連携」。
export const sc20ObsidianSettingsNav: PlanNavItem = {
  id: 'sc20-obsidian-settings',
  label: msg`Obsidian連携`,
  to: '/my/obsidian',
  group: 'personal',
};
