import { defineConfig, devices } from '@playwright/test';

// Issue #126: スクリーンレベルの e2e スモーク。ビルド済みプレビューに対して実行する。
//
// ［2026-09-02 / #1139］従前ここには「バックエンド（Keycloak/BFF）不要のログイン画面到達までを
// 検証する」と書いていた。**前半は今も正しく、後半は誤りである。** 基盤を 1 つも起動しないのは
// そのとおりだが、**検証はログイン画面の手前で止まらない** —— 身元を返す 1 つの応答を
// ネットワーク層で差し替えれば認証済みの画面が描かれる（土台は `e2e/support/bffSession.ts`）。
// 画面ごとの spec は「未認証リダイレクト 1 本 ＋ セッションを与えた本体」で構成する（IADR-0330）。

// Issue #554: プレビューの接続先はここだけで決める。以前は同じ値が baseURL /
// webServer.command の --port / webServer.url の 3 箇所に複写されており、さらに
// e2e 側にも直書きされていた（値の複写はいずれ必ずずれる）。
const PREVIEW_PORT = 4173;
const PREVIEW_URL = `http://localhost:${PREVIEW_PORT}`;

export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  // NFR / IADR-0621（#2036）: ファイル単位ではなく test 単位で worker とシャードへ配る。
  // ファイルごとの本数の偏り（数本〜数十本）がそのままシャードの所要の偏りになるのを避ける。
  // spec はファイル内で状態を共有しない（モジュール直下の可変状態・`describe.serial` は 0 件。走査して確かめた）。
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  // NFR / IADR-0621 / 基準書 §4: L1 の 1 実行（CI では 1 シャード）あたりの上限。ジョブの
  // `timeout-minutes`（20 分）より先に切れ、どの test で止まったかがレポートに残る。
  globalTimeout: process.env.CI ? 15 * 60_000 : 0,
  // NFR / IADR-0621 / 基準書 §9: CI はシャードごとに blob を書き、`frontend.yml` の `e2e` ジョブが
  // `merge-reports` で html ＋ github（注釈）へまとめる。シャードで github を出すと、まとめた側と
  // 注釈が二重になるため、シャードでは list（ログ）だけを並べる。
  reporter: process.env.CI ? [['blob'], ['list']] : 'list',
  use: {
    baseURL: PREVIEW_URL,
    trace: 'on-first-retry',
    // ADR-0031 / IADR-0125 決定 7: 表示言語はブラウザの言語設定から決まる（切替 UI は持たない）。
    // Playwright の既定は en-US であり、固定しないと **CI の既定ロケール次第でアサーションが割れる**
    // （実測: 固定前は英語で描画され、日本語の見出しを待つスモークが落ちた）。
    // 単体テスト側を setup.ts で ja に固定しているのと同じ理由・同じ値である。
    locale: 'ja-JP',
  },
  webServer: {
    command: `pnpm run preview -- --port ${PREVIEW_PORT} --strictPort`,
    url: PREVIEW_URL,
    // Issue #554: **ローカルでも常に自前で起動する**（既定の `!process.env.CI` を採らない）。
    // 理由: `--strictPort` は「このポートを占有できなければ失敗する」という宣言であり、
    // 再利用を許すと**同じポートに居る無関係なプロセスを本アプリだと思って接続する**。
    // #519 の作業中に実際にこの経路を踏んでおり、E2E が別サーバーを叩いていた。
    // 常に自前起動なら、占有時は `--strictPort` が**起動失敗として大きく落ちる**——
    // 本 issue が正そうとしている「空振りが緑になる」の逆で、原因が即座に判る。
    // 代償はローカルでの起動待ち（実測 1 秒程度）であり、取り違えの危険に見合わない。
    reuseExistingServer: false,
    timeout: 60_000,
  },
  // IADR-0569: L1 の project 名は `smoke`（#2023 で `chromium` から改めた。基準書 §2.1 / §12）。
  // ja-JP 以外のロケールの project は `smoke-<locale 小文字>` とする。M-020（#2023）: en-US では
  // ブランド表示名がロケールで差し替わらず、ナビが英語になることを `e2e/i18n.smoke.spec.ts` だけで見る ——
  // 他の spec は日本語の文言を待つので、en-US では走らせない（走らせると全件が文言の違いで落ちる）。
  projects: [
    {
      name: 'smoke',
      use: { ...devices['Desktop Chrome'] },
      // IADR-0569 決定 3 / IADR-0624: live 層（`e2e/live/`。`playwright.live.config.ts` が流す）は L1 で走らせない。
      // 既定の testMatch は `*.ro.spec.ts` なども拾うため、ディレクトリで外す。
      testIgnore: [/i18n\.smoke\.spec\.ts$/, /[\\/]e2e[\\/]live[\\/]/],
    },
    {
      name: 'smoke-en-us',
      use: { ...devices['Desktop Chrome'], locale: 'en-US' },
      testMatch: /i18n\.smoke\.spec\.ts$/,
    },
  ],
});
