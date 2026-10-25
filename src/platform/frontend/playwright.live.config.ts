import { existsSync } from 'node:fs';
import { defineConfig, devices } from '@playwright/test';
import { parseLiveEnv } from './e2e/live/support/env';
import { liveProjects } from './e2e/live/support/projects';
import { DEFAULT_AUTH_DIR, LOCAL_ENV_FILE } from './e2e/live/support/paths';

// NFR / IADR-0569 決定 3・4 / IADR-0624（#2025）: **live 層（稼働中の PoC・統合スタック）の config**。
// L1（`playwright.config.ts`）とは別のファイルにする —— L1 の `webServer`（プレビューの起動）と `baseURL` が live に掛からない。
//
// - 🔴 **`E2E_LIVE` が未設定・空なら全 project を 0 件にする**（`--pass-with-no-tests` で exit 0）。`1` だけが live で、
//   それ以外の値は読み込みで例外（`e2e/live/support/env.ts`）。稼働の環境へ
//   当たる実行は明示の opt-in でだけ流す（`scripts/verify-oidc-edge-flow.sh` の `--live` / `LIVE=1` と同じ思想）。
// - `E2E_LAYER`（`ro` / `rw` / `trading` / `llm`。既定 `ro`）で選ばれた層の project だけを有効にし、`live-setup` は
//   依存として常に走る。値域外の値・`E2E_LIVE=1` 無しの `E2E_LAYER` は**ここで例外**になる（0 件の緑にしない）。
// - 前提（資格情報・Keycloak・BFF）は `globalSetup` の readiness 門が確かめ、満たさなければ exit 2（前提未整備）。

// 手元の資格情報（任意）。既にある環境変数は上書きしない。例示は `e2e/live/env.e2e.example`。
if (existsSync(LOCAL_ENV_FILE)) process.loadEnvFile(LOCAL_ENV_FILE);

// #2031 / IADR-0630 決定 3: 作るものの名前 `e2e-<runId>-…` の runId を**この実行で 1 回だけ**決める。config は各ワーカーでも
// 読み込まれるので、決めた値を環境変数へ置き、後から起動するワーカーが同じ値を引き継ぐ（与えられていればそれを使う）。
if (!process.env.E2E_RUN_ID && process.env.E2E_LIVE === '1') {
  process.env.E2E_RUN_ID = Date.now().toString(36);
}

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);

// 標準エラーへ書く（Knip など config を読み込むだけの道具は標準出力を JSON として読む）。
if (!cfg.live) {
  process.stderr.write(
    '[e2e-live] E2E_LIVE=1 が無いので 0 件で終わる（稼働の環境へ当たる実行は明示の opt-in でだけ流す）\n',
  );
}

const projects = liveProjects(cfg);

export default defineConfig({
  testDir: './e2e/live',
  // 基準書 §4: L2 は test 60 秒・live 全体 40 分。
  timeout: 60_000,
  globalTimeout: 40 * 60_000,
  forbidOnly: !!process.env.CI,
  globalSetup: cfg.live ? './e2e/live/support/readiness.ts' : undefined,
  // IADR-0624: 実行の終わりに（成功でも失敗でも）6 役割のセッションを BFF のログアウトで失効させ、state を消す。
  // 層の trace はリクエストの Cookie を記録する（外せない）ので、失効させて使えなくする。
  globalTeardown: cfg.live ? './e2e/live/support/teardown.ts' : undefined,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : 'list',
  use: {
    ...devices['Desktop Chrome'],
    baseURL: cfg.edgeUrl,
    ignoreHTTPSErrors: cfg.insecureTls,
    // 基準書 §9: live は初回の失敗から trace を残す（再試行が無いか 1 回のため）。ソースは載せない。
    trace: { mode: 'retain-on-failure', snapshots: true, sources: false },
    screenshot: 'only-on-failure',
    video: 'off',
    locale: 'ja-JP',
  },
  projects,
});
