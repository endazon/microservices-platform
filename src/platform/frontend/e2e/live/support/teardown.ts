// NFR / ADR-0032 / IADR-0624（#2025）: live 層の**後始末**。`playwright.live.config.ts` の `globalTeardown` として、
// live の実行の終わりに（成功でも失敗でも）1 回走る。
//
// 🔴 なぜ要るか: 層の project は storageState（BFF のセッション cookie）で動き、失敗した test の trace（`retain-on-failure`）と
// `trading` の全 test の trace（`on`）は、リクエストの `Cookie` ヘッダを**必ず**記録する（Playwright に外す設定は無い）。trace は
// 基準書 §9 で必須なので止めない。代わりに、実行が終わった時点で 6 役割のセッションを **BFF のログアウトで失効させ**、
// state のファイルを消す。trace に残った cookie は、その時点で使えなくなる。
//
// ログアウトの経路（`AuthBffEndpoints.cs`）: **GET** `/bff/auth/logout?sid=<sid>`。CSRF はセッションの `sid` との一致で防ぐ
// （ヘッダは要らない）。sid は `/bff/auth/me` の `logoutUrl` が配る。認可サーバへの往復はトップレベルの遷移でしか完結しないので、
// ブラウザのページで開く。失効したことは、**同じ state の cookie で `/bff/auth/me` が 401** になることで確かめる。
import { chmodSync, existsSync, rmSync } from 'node:fs';
import { chromium, request, type Browser } from '@playwright/test';
import { parseLiveEnv, ROLES, stateFile, type LiveEnv, type Role } from './env';
import { DEFAULT_AUTH_DIR } from './paths';

export interface RevokeResult {
  /** 失効させた役割。 */
  revoked: Role[];
  /** state が無かった役割（setup がその役割で失敗した・前提未整備で流れなかった）。 */
  missing: Role[];
  /** `E2E_KEEP_SESSIONS` で失効させずに残した役割（state は 0600。後始末は AST の live の teardown。#2231）。 */
  kept: Role[];
  /** 失効を確かめられなかった役割（名前だけ。値は持たない）。 */
  failed: { role: Role; reason: string }[];
}

/** 6 役割のセッションを BFF のログアウトで失効させ、state のファイルを消す。値は返さない・出さない。 */
export async function revokeSessions(
  cfg: LiveEnv,
  browser: Browser,
  chmod: (path: string, mode: number) => void = chmodSync,
): Promise<RevokeResult> {
  const result: RevokeResult = { revoked: [], missing: [], kept: [], failed: [] };
  for (const role of ROLES) {
    const file = stateFile(cfg.authDir, role);
    if (!existsSync(file)) {
      result.missing.push(role);
      continue;
    }
    // #2231 / IADR-0628: 明示した役割（取引オーナーだけ）は失効させずに残す。他の役割は必ず失効させる。
    // 🔴 権限を変えられなかったら失敗に積んで**次の役割へ進む**（ここで止まると、後ろの役割が失効しないまま残る。最後に exit 1）。
    if (cfg.keepSessions.includes(role)) {
      try {
        chmod(file, 0o600);
        result.kept.push(role);
      } catch (e) {
        result.failed.push({
          role,
          reason: `残す state の権限を 0600 にできない（${e instanceof Error ? e.message.split('\n')[0] : String(e)}）`,
        });
      }
      continue;
    }
    try {
      const context = await browser.newContext({
        storageState: file,
        baseURL: cfg.edgeUrl,
        ignoreHTTPSErrors: cfg.insecureTls,
      });
      try {
        const me = await context.request.get('/bff/auth/me', { failOnStatusCode: false });
        if (me.status() === 200) {
          const logoutUrl = ((await me.json()) as { logoutUrl?: string | null }).logoutUrl;
          if (!logoutUrl) throw new Error('/bff/auth/me が logoutUrl を返さない');
          const page = await context.newPage();
          // 認可サーバを往復して SPA へ戻る。途中の応答が失敗なら、そこで止まる（戻り先を待ち続けない）。
          const landed = await page.goto(logoutUrl, { timeout: 30_000 });
          if (!landed || landed.status() >= 400) {
            throw new Error(`ログアウトの応答が ${landed ? landed.status() : '無い'}`);
          }
          await page.waitForURL(
            (url) => url.origin === cfg.edgeUrl && !url.pathname.startsWith('/bff/'),
            { timeout: 30_000 },
          );
        } else if (me.status() !== 401) {
          throw new Error(`/bff/auth/me（ログアウトの前）が ${me.status()}`);
        }
      } finally {
        await context.close();
      }
      // 失効の確かめ: **同じ state の cookie**（ログアウトでブラウザから消えた後も trace には残る値）で 401 になること。
      const probe = await request.newContext({
        storageState: file,
        baseURL: cfg.edgeUrl,
        ignoreHTTPSErrors: cfg.insecureTls,
      });
      try {
        const after = await probe.get('/bff/auth/me', { failOnStatusCode: false });
        if (after.status() !== 401)
          throw new Error(`ログアウトの後も同じ cookie で /bff/auth/me が ${after.status()}`);
      } finally {
        await probe.dispose();
      }
      result.revoked.push(role);
    } catch (e) {
      result.failed.push({
        role,
        reason: e instanceof Error ? e.message.split('\n')[0] : String(e),
      });
    } finally {
      rmSync(file, { force: true });
    }
  }
  return result;
}

export default async function globalTeardown(): Promise<void> {
  const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);
  if (!cfg.live) return;
  const browser = await chromium.launch();
  let result: RevokeResult;
  try {
    result = await revokeSessions(cfg, browser);
  } finally {
    await browser.close();
  }
  process.stdout.write(
    `[e2e-live] セッションを失効させた: ${result.revoked.join(', ') || '（なし）'}` +
      (result.missing.length ? `／state が無かった: ${result.missing.join(', ')}` : '') +
      (result.kept.length
        ? `／失効させずに残した（E2E_KEEP_SESSIONS。後始末は AST の live の teardown）: ${result.kept.join(', ')}`
        : '') +
      '\n',
  );
  if (result.failed.length) {
    process.stderr.write(
      `[e2e-live] セッションを失効させられなかった役割（trace の cookie がセッションの寿命まで有効のまま）:\n  - ${result.failed
        .map((f) => `e2e-${f.role}: ${f.reason}`)
        .join('\n  - ')}\n`,
    );
    process.exit(1);
  }
}
