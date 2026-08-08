import { test, expect } from '@playwright/test';

// NFR, ADR-0031 / IADR-0134: ルート単位の分割後も、**実ブラウザで**アプリが起動すること。
//
// 単体テスト（jsdom）は動的 import を Vite の解決に任せるため、成果物側の配線
// （index.html の modulepreload・チャンク間の相対 URL・base の解決）は検査していない。
// 分割の壊れ方は「テストは緑・実機は白画面」であり、それが起きるのはまさにこの層である。
//
// **認証済みの遅延ルートはこの層では実走できない**——トークンは InMemoryWebStorage に保持され、
// 外部から注入できない（#504 と同じ制約）。よってここで見るのは初期ロードの健全性に限る。
// 遅延ルート自体の描画は Vitest 側（各画面のテスト）が実際に動的 import を通して固定している。
//
// Issue #554: 接続先は `baseURL` フィクスチャ（＝ playwright.config.ts の単一定数）から得る。
// 以前はここに `http://localhost:4173` を直書きしており、設定側とずれると
// `page.on('response')` のフィルタが全応答を捨て、**`failed` が空のまま緑になった**
// （変異試験で実測。ポートを不一致にすると 4xx 検査は通り、チャンク数の検査だけが落ちた）。
// 「捨てた結果の空配列」と「本当に問題が無い空配列」を区別できないのが事故の核であるため、
// **観測が 0 件なら先に落とす**（下記 ①）。
test('boots from the split bundle with every requested asset served', async ({ page, baseURL }) => {
  // baseURL は playwright.config.ts が必ず与える。未設定なら設定側の退行であり、
  // ここで落ちなければ以下の集計がすべて空振りする（＝この検査が無言で無効になる）。
  expect(baseURL, 'playwright.config.ts の baseURL が未設定です').toBeTruthy();
  const origin = baseURL as string;

  const failed: string[] = [];
  const observed: string[] = [];
  const scripts: string[] = [];

  page.on('response', (res) => {
    const url = res.url();
    if (!url.startsWith(origin)) return;
    observed.push(url);
    if (res.status() >= 400) failed.push(`${res.status()} ${url}`);
    if (new URL(url).pathname.endsWith('.js')) scripts.push(new URL(url).pathname);
  });
  page.on('pageerror', (err) => failed.push(`pageerror: ${err.message}`));

  await page.goto('/');

  // 「見えるはずのもの」を先に確かめる（描画に失敗していれば以下の集計は空振りする）。
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();

  // ① 空振り検出——1 件も観測していないなら、後続の検査は「何も見ずに緑」になる。
  expect(observed.length, `${origin} 宛の応答を 1 件も観測していません`).toBeGreaterThan(0);
  // ② 4xx / 5xx とページエラーが無いこと。
  expect(failed).toEqual([]);
  // ③ 分割の結果、初期ロードは 1 本ではなくなる（実行時 config の config.js を除く）。
  // 何本かは manualChunks の設計次第なので数は固定しない——「割れていること」だけを見る。
  const chunks = scripts.filter((p) => p.startsWith('/assets/'));
  expect(chunks.length).toBeGreaterThan(1);
});
