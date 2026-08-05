import { test, expect } from '@playwright/test';

// NFR, ADR-0031 / IADR-0133: ルート単位の分割後も、**実ブラウザで**アプリが起動すること。
//
// 単体テスト（jsdom）は動的 import を Vite の解決に任せるため、成果物側の配線
// （index.html の modulepreload・チャンク間の相対 URL・base の解決）は検査していない。
// 分割の壊れ方は「テストは緑・実機は白画面」であり、それが起きるのはまさにこの層である。
//
// **認証済みの遅延ルートはこの層では実走できない**——トークンは InMemoryWebStorage に保持され、
// 外部から注入できない（#504 と同じ制約）。よってここで見るのは初期ロードの健全性に限る。
// 遅延ルート自体の描画は Vitest 側（各画面のテスト）が実際に動的 import を通して固定している。
test('boots from the split bundle with every requested asset served', async ({ page }) => {
  const failed: string[] = [];
  const scripts: string[] = [];

  page.on('response', (res) => {
    const url = res.url();
    if (!url.startsWith('http://localhost:4173')) return;
    if (res.status() >= 400) failed.push(`${res.status()} ${url}`);
    if (new URL(url).pathname.endsWith('.js')) scripts.push(new URL(url).pathname);
  });
  page.on('pageerror', (err) => failed.push(`pageerror: ${err.message}`));

  await page.goto('/');

  // 「見えるはずのもの」を先に確かめる（描画に失敗していれば以下の集計は空振りする）。
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();

  expect(failed).toEqual([]);
  // 分割の結果、初期ロードは 1 本ではなくなる（実行時 config の config.js を除く）。
  // 何本かは manualChunks の設計次第なので数は固定しない——「割れていること」だけを見る。
  const chunks = scripts.filter((p) => p.startsWith('/assets/'));
  expect(chunks.length).toBeGreaterThan(1);
});
