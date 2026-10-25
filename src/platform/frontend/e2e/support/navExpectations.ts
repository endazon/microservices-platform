// NFR, IADR-0035, IADR-0337（#2016 / E-11 → #2027 / E-40 で切り出した）: **役割ごとの左ナビの期待値**と、
// 左ナビをアクセシビリティツリーから読む部品。L1（`shell.smoke.spec.ts`。スタブの roles）と live（`live/shell.ro.spec.ts`。
// 試験アカウント）が**同じ表**を見る —— 2 箇所に写すと片方だけ古くなる。
import type { Page } from '@playwright/test';

/**
 * 05_screens §共通シェル（左ナビ 4 グループ・アクセス制御の割当）から起こした、**役割ごとの左ナビの期待値**。
 * グループ名 → 「表示名 → 遷移先」。**鍵の並びは計画の並び**である（グループの並び・グループ内の並びとも。#2189）。
 *
 * - 利用者・個人のグループは全利用者に出る（ABAC の権限内。ロール限定なし）。
 * - 管理: SC-05/06/07 は管理者・運用者、SC-09・SC-12・SC-17 はシステム管理者だけ。
 * - 運用: SC-10・SC-11・SC-22 は運用者・管理者。
 * - SC-03 はナビに置かない。項目が 0 件になったグループは見出しごと出さない（存在秘匿。IADR-0035）。
 * - 可変機能ユニットのグループ（「株式自動売買」）は、そのユニットのロールを持たない 3 役割には出ない。
 */
const USER_GROUP = {
  '検索・質問': '/ask',
  結果一覧: '/search',
  Wiki: '/wiki',
  AI分析: '/analyze',
  ナレッジグラフ: '/graph',
  AI提案: '/ai-suggestions',
};
const PERSONAL_GROUP = { 個人資料: '/my/notes', Obsidian連携: '/my/obsidian' };
const ADMIN_VIEW_ITEMS = {
  文書管理: '/admin/documents',
  データソース: '/admin/sources',
  変換ジョブ: '/admin/conversions',
};
const ADMIN_ONLY_ITEMS = {
  ABAC設定: '/admin/abac',
  ユーザー管理: '/admin/users',
  MCP管理: '/admin/mcp-clients',
};
const OPS_GROUP = {
  ダッシュボード: '/admin/ops',
  構成ビューア: '/admin/config-viewer',
  '秘密情報・接続設定の管理': '/admin/secrets',
};

export type NavTable = Record<string, Record<string, string>>;

/** 役割の区分ごとの左ナビの期待値（L1 はスタブの roles で、live は試験アカウントで同じ表を見る）。 */
export const NAV_NO_ROLE: NavTable = { 利用者: USER_GROUP, 個人: PERSONAL_GROUP };
export const NAV_OPERATOR: NavTable = {
  利用者: USER_GROUP,
  個人: PERSONAL_GROUP,
  管理: ADMIN_VIEW_ITEMS,
  運用: OPS_GROUP,
};
export const NAV_ADMIN: NavTable = {
  利用者: USER_GROUP,
  個人: PERSONAL_GROUP,
  管理: { ...ADMIN_VIEW_ITEMS, ...ADMIN_ONLY_ITEMS },
  運用: OPS_GROUP,
};

/**
 * 左ナビ（`navigation "主要ナビゲーション"`）を、**アクセシビリティツリーから**グループ → 「表示名 → 遷移先」へ読む。
 *
 * 🔴 DOM の構造（`div > h2 + ul`）や CSS には依存しない。見出し（level 2）の後に続くリンクを、その見出しの
 * グループへ数える —— 支援技術の利用者が読む順序そのものである。
 */
export async function readNav(page: Page): Promise<NavTable> {
  const snapshot = await page
    .getByRole('navigation', { name: '主要ナビゲーション' })
    .ariaSnapshot();
  const table: NavTable = {};
  let group: string | null = null;
  let link: string | null = null;
  for (const line of snapshot.split('\n')) {
    const heading = /- heading "([^"]+)" \[level=2\]/.exec(line);
    if (heading) {
      group = heading[1];
      table[group] = {};
      continue;
    }
    const name = /- link "([^"]+)"/.exec(line);
    if (name) {
      link = name[1];
      if (group === null) throw new Error(`見出しの前にリンクがある: ${link}`);
      table[group][link] = '';
      continue;
    }
    const url = /- \/url: (\S+)/.exec(line);
    if (url && group !== null && link !== null) {
      table[group][link] = url[1];
      link = null;
    }
  }
  return table;
}
