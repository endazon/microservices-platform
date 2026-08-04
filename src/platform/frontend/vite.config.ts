import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { fileURLToPath, URL } from 'node:url';

// Issue #126 / SC-01..11 SPA 基盤: BFF を境界にした疎結合構成。
// - dev proxy: /bff/* を BFF（既定 http://localhost:5000）へ転送する。接続先は VITE_BFF_TARGET で上書き可能。
// - 実行時 config（public/config.js）で本番の接続先を注入するため、ビルドは環境非依存に保つ。
// - FR-14, IADR-0056: 可変機能ユニット（@knowledge）はソース参照で合成する（単体テストは
//   ワークスペースルートの vitest.config.ts で横断実行）。
export default defineConfig({
  // ADR-0031 / IADR-0121 決定 4: Tailwind CSS v4 は Vite プラグインで取り込む（設定ファイルは持たない。
  // トークンは @platform/ui の styles.css の @theme が正）。
  // ADR-0031（i18n = Lingui・コンパイル時抽出）/ IADR-0125 決定 3: マクロを babel で展開する。
  // 同じ設定を src/vitest.config.ts にも置く（片方だけだとビルドとテストのどちらかが静かに壊れる）。
  plugins: [react({ babel: { plugins: ['@lingui/babel-plugin-lingui-macro'] } }), tailwindcss()],
  resolve: {
    alias: {
      '@foundation': fileURLToPath(new URL('./src/foundation', import.meta.url)),
      '@features': fileURLToPath(new URL('./src/features', import.meta.url)),
      '@knowledge': fileURLToPath(new URL('../../knowledge/frontend/src', import.meta.url)),
      // Issue #283, FR-14, IADR-0056/0070: AST（ai-stock-trading）ユニットの features をソース参照で合成する。
      '@ai-stock-trading': fileURLToPath(new URL('../../ai-stock-trading/frontend/src', import.meta.url)),
    },
  },
  server: {
    port: 3100,
    proxy: {
      '/bff': {
        target: process.env.VITE_BFF_TARGET ?? 'http://localhost:5000',
        changeOrigin: true,
      },
    },
  },
});
