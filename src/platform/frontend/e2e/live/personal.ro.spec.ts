// NFR / FR-18 / FR-19 / FR-20 / SC-19 / SC-20 / SC-21 / IADR-0627（#2029 / E-42）: live-ro の**個人の画面の到達**。網羅表（#2013）の
// M-061（SC-19 個人資料）・M-066（SC-20 Obsidian 連携）・M-070（SC-21 AI 提案一覧）を、`e2e-user` の storageState で流す。
//
// - 🔴 読み取りだけ（画面を開く・画面が送る GET）。作成・共有・端末登録はしない（L3。E-51）。
// - 各画面で、本体の見出しが出ることと、画面が引く BFF の GET が 200 で返ることを同じ表示で見る（`support/screens.ts`）。
//   一覧が 0 件の空表示でもよい（試験アカウントは個人資料も端末も提案も持たない）。
import { expect, test } from '@playwright/test';
import { parseLiveEnv, stateFile } from './support/env';
import { DEFAULT_AUTH_DIR } from './support/paths';
import { allOk, openScreen } from './support/screens';

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);

test.describe('e2e-user の個人の画面', () => {
  test.use({ storageState: stateFile(cfg.authDir, 'user') });

  // SC-19, FR-19, FR-21: M-061。
  test(
    'M-061: 個人資料に到達し、見出しが出て、一覧の取得が 200 になる',
    { tag: ['@p0', '@live-ro'] },
    async ({ page }) => {
      const apis = ['/bff/private-notes'];
      const { statuses } = await openScreen(page, '/my/notes', '個人資料', apis);
      expect(statuses).toEqual(allOk(apis));
    },
  );

  // SC-20, FR-20: M-066。
  test(
    'M-066: Obsidian 連携設定に到達し、見出しが出て、端末の一覧の取得が 200 になる',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }) => {
      const apis = ['/bff/private-notes/devices'];
      const { statuses } = await openScreen(page, '/my/obsidian', 'Obsidian 連携設定', apis);
      expect(statuses).toEqual(allOk(apis));
    },
  );

  // SC-21, FR-18: M-070。
  test(
    'M-070: AI 提案一覧に到達し、見出しが出て、提案の一覧の取得が 200 になる（0 件の空表示でもよい）',
    { tag: ['@p2', '@live-ro'] },
    async ({ page }) => {
      const apis = ['/bff/graph/suggestions'];
      const { statuses } = await openScreen(page, '/ai-suggestions', 'AI 提案一覧', apis);
      expect(statuses).toEqual(allOk(apis));
    },
  );
});
