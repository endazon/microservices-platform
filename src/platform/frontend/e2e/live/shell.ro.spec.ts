// NFR / SC-05 / SC-09 / ADR-0032 / IADR-0035 / IADR-0337 / IADR-0626（#2027 / E-40）: live-ro の**共通シェル**。
// 網羅表（#2013）の M-013（左ナビの出し分けを 4 役割で）と、権限外の画面が存在秘匿の NotFound になることを、
// `live-setup` が作った試験アカウントの storageState（実 Keycloak → 実 BFF のセッション）で見る。
//
// - 期待値の表は L1 の `shell.smoke.spec.ts` と同じもの（`../support/navExpectations.ts`）。L1 はスタブの roles で、ここは
//   実 Keycloak が発行したロールで同じ表になることを見る。
// - 🔴 読み取りだけ（画面を開く・GET を送る）。フォームを送らない。
// - 権限外の判定は**両側**で固定する: 許された役割で本体が出て、許されない役割で NotFound が出る。管理者限定の画面は、
//   UI の出し分けだけでなく**実効境界（BFF の 403）**も同じ役割の組で見る（UI は存在秘匿のためだけの出し分け。IADR-0035）。
import { expect, test, type Page } from '@playwright/test';
import {
  NAV_ADMIN,
  NAV_NO_ROLE,
  NAV_OPERATOR,
  readNav,
  type NavTable,
} from '../support/navExpectations';
import { parseLiveEnv, stateFile, type Role } from './support/env';
import { DEFAULT_AUTH_DIR } from './support/paths';

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);

const NOT_FOUND = '見つかりませんでした';
const DOCUMENTS = '文書一覧';
const ABAC = '管理者設定（ABAC）';
const DOCS_API = '/bff/documents';
const POLICIES_API = '/bff/admin/authz/policies';

interface Case {
  role: Role;
  nav: NavTable;
  /** 運用者の画面（SC-05 文書管理など）を開ける。 */
  operator: boolean;
  /** 管理者限定の画面（SC-09 ABAC 設定など）を開ける。 */
  admin: boolean;
}

/** 4 役割（IADR-0570 の試験アカウント）。`e2e-noaccess` はロールも部門も持たない ABAC の deny 対照。 */
const CASES: Case[] = [
  { role: 'user', nav: NAV_NO_ROLE, operator: false, admin: false },
  { role: 'noaccess', nav: NAV_NO_ROLE, operator: false, admin: false },
  { role: 'operator', nav: NAV_OPERATOR, operator: true, admin: false },
  { role: 'admin', nav: NAV_ADMIN, operator: true, admin: true },
];

/**
 * 画面を開き、`shown` の見出し（level 1）が出て `hidden` の見出しが出ないことを見る。開いている間に送られた
 * `watch`（BFF の path）への要求の数を返す（本体の描画が済んでから数える。時間で待たない。基準書 §4）。
 */
async function openScreen(
  page: Page,
  path: string,
  shown: string,
  hidden: string,
  watch: string,
): Promise<number> {
  let calls = 0;
  page.on('request', (r) => {
    if (new URL(r.url()).pathname === watch) calls += 1;
  });
  await page.goto(path);
  await expect(page.getByRole('heading', { name: shown, level: 1 })).toBeVisible();
  await expect(page.getByRole('heading', { name: hidden, level: 1 })).toHaveCount(0);
  return calls;
}

/** 管理者限定の BFF（AdminOnly）へ同じセッションの cookie で GET を送り、状態を返す（読み取りだけ）。 */
async function policiesStatus(page: Page): Promise<number> {
  const res = await page.request.get(POLICIES_API, { failOnStatusCode: false });
  return res.status();
}

for (const c of CASES) {
  test.describe(`e2e-${c.role} の共通シェル`, () => {
    test.use({ storageState: stateFile(cfg.authDir, c.role) });

    // NFR, IADR-0337: M-013（M-012 を live で）。
    test(
      `M-013: e2e-${c.role} の左ナビは役割ごとの項目の集合と並びに一致し、文書詳細と可変機能ユニットのグループは出ない`,
      { tag: ['@p0', '@live-ro'] },
      async ({ page }) => {
        await page.goto('/ask');
        await expect(
          page.getByRole('heading', { name: 'ナレッジ検索・AI質問', level: 1 }),
        ).toBeVisible();
        const navigation = page.getByRole('navigation', { name: '主要ナビゲーション' });
        await expect(navigation.getByRole('heading', { level: 2 })).toHaveCount(
          Object.keys(c.nav).length,
        );

        const actual = await readNav(page);
        expect(actual).toEqual(c.nav);
        // `toEqual` は鍵の順を比べないので、並び（支援技術が読む順序）を別に比べる。
        expect(Object.keys(actual), 'グループの並び').toEqual(Object.keys(c.nav));
        for (const [group, items] of Object.entries(c.nav)) {
          expect(Object.keys(actual[group] ?? {}), `「${group}」グループ内の並び`).toEqual(
            Object.keys(items),
          );
        }
        await expect(navigation.getByRole('link', { name: /文書詳細/ })).toHaveCount(0);
        await expect(navigation.getByRole('heading', { name: '株式自動売買' })).toHaveCount(0);
      },
    );

    // SC-05, IADR-0035 / IADR-0009: 運用者の画面。許されない役割では NotFound を描き、一覧の取得（GET /documents）を送らない。
    if (c.operator) {
      test(
        `e2e-${c.role} が文書管理を開くと一覧の見出しが出て、一覧の取得は 200 になり、見つかりませんでしたは出ない`,
        { tag: ['@p0', '@live-ro'] },
        async ({ page }) => {
          const listed = page.waitForResponse((r) => new URL(r.url()).pathname === DOCS_API);
          await openScreen(page, '/admin/documents', DOCUMENTS, NOT_FOUND, DOCS_API);
          expect((await listed).status(), `GET ${DOCS_API}`).toBe(200);
        },
      );
    } else {
      test(
        `e2e-${c.role} が文書管理を開くと見つかりませんでしたが出て、一覧の取得を送らない`,
        { tag: ['@p0', '@live-ro'] },
        async ({ page }) => {
          const calls = await openScreen(page, '/admin/documents', NOT_FOUND, DOCUMENTS, DOCS_API);
          expect(calls, `GET ${DOCS_API}`).toBe(0);
        },
      );
    }

    // SC-09, FR-09, IADR-0035: 管理者限定の画面。UI の出し分けと、BFF の実効境界（AdminOnly → 403）を同じ役割の組で見る。
    if (c.admin) {
      test(
        `e2e-${c.role} が ABAC 設定を開くと本体の見出しが出て、ポリシーの一覧の取得は 200 になる`,
        { tag: ['@p0', '@live-ro'] },
        async ({ page }) => {
          await openScreen(page, '/admin/abac', ABAC, NOT_FOUND, POLICIES_API);
          // 実効境界は UI ではなく BFF にある。
          expect(await policiesStatus(page), `GET ${POLICIES_API}`).toBe(200);
        },
      );
    } else {
      test(
        `e2e-${c.role} が ABAC 設定を開くと見つかりませんでしたが出て、ポリシーの一覧の取得は 403 になる`,
        { tag: ['@p0', '@live-ro'] },
        async ({ page }) => {
          await openScreen(page, '/admin/abac', NOT_FOUND, ABAC, POLICIES_API);
          expect(await policiesStatus(page), `GET ${POLICIES_API}`).toBe(403);
        },
      );
    }
  });
}

/**
 * 取引機能のユニット（ai-stock-trading）の 4 画面。共通シェルの直下に載る（`/trading` の接頭辞は無い。`src/app/routing/router.test.ts`）。
 * ロール `trading-owner` を持たない利用者には、存在秘匿の NotFound を描く（AST の `RequireRole`）。
 */
const AST_ROUTES = ['/settings', '/settings/risk', '/controls', '/opend-auth'] as const;

// AST/NFR-06, AST/ADR-0038, IADR-0035: 網羅表（AST#1410）A-002 を AST から移した（AST#1417 / AST/IADR-0589 決定 5）。
// AST の live は取引オーナーの state だけを読む（e2e-user の state を AST に残させない）ので、閲覧者の陰性は本リポジトリの live-ro が持つ。
test.describe('e2e-user（trading-owner を持たない）と取引機能のユニット', () => {
  test.use({ storageState: stateFile(cfg.authDir, 'user') });

  test(
    'A-002: e2e-user が取引機能の 4 画面を開くと、どれも見つかりませんでしたになり、ナビに株式自動売買のグループが無い',
    { tag: ['@p0', '@live-ro'] },
    async ({ page }) => {
      for (const path of AST_ROUTES) {
        await page.goto(path);
        await expect(page.getByRole('heading', { name: NOT_FOUND, level: 1 }), path).toBeVisible();
      }
      await expect(
        page
          .getByRole('navigation', { name: '主要ナビゲーション' })
          .getByRole('heading', { name: '株式自動売買' }),
      ).toHaveCount(0);
    },
  );
});
