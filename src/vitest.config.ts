import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';

// FR-14, IADR-0056: 単体テスト＋カバレッジはワークスペースルート（src/）で全ユニット
// （platform / knowledge の各 frontend）を横断計測する。ビルド・dev サーバは
// platform/frontend/vite.config.ts。
export default defineConfig({
  plugins: [react()],
  resolve: {
    // IADR-0121 決定 2（pnpm workspace）: pnpm は node_modules を isolated に置くため、ユニットごとに
    // 別々の React 実体が解決され得る（同一プロセスで 2 つの React が動くと「Invalid hook call」になる）。
    // 横断テストは 1 プロセスで全ユニットのコンポーネントを描画するので、React を明示的に重複排除する。
    dedupe: ['react', 'react-dom'],
    alias: {
      '@foundation': fileURLToPath(new URL('./platform/frontend/src/foundation', import.meta.url)),
      '@features': fileURLToPath(new URL('./platform/frontend/src/features', import.meta.url)),
      '@knowledge': fileURLToPath(new URL('./knowledge/frontend/src', import.meta.url)),
      // Issue #283, FR-14, IADR-0056/0070: AST（ai-stock-trading）ユニットの feature テストも横断収集する。
      '@ai-stock-trading': fileURLToPath(new URL('./ai-stock-trading/frontend/src', import.meta.url)),
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./platform/frontend/src/test/setup.ts'],
    include: [
      'platform/frontend/src/**/*.{test,spec}.{ts,tsx}',
      'knowledge/frontend/src/**/*.{test,spec}.{ts,tsx}',
      'ai-stock-trading/frontend/src/**/*.{test,spec}.{ts,tsx}',
      // IADR-0121 決定 4: 共有 UI パッケージ（@platform/ui）も横断計測の対象にする。
      'packages/*/src/**/*.{test,spec}.{ts,tsx}',
    ],
    css: false,
    // IADR-0033/0034: カバレッジはしきい値ゲート（回帰防止のラチェット）。CI(frontend-tests.yml)
    // でレポート生成＋ゲートに用いる。テストを増やしたらしきい値を引き上げる。
    coverage: {
      provider: 'v8',
      reporter: ['text', 'text-summary', 'html', 'lcov'],
      reportsDirectory: './coverage',
      // 計測対象は各ユニット frontend/src 配下の実装のみ。テスト・型定義・エントリ/自動生成は除外する。
      include: [
        'platform/frontend/src/**/*.{ts,tsx}',
        'knowledge/frontend/src/**/*.{ts,tsx}',
        'ai-stock-trading/frontend/src/**/*.{ts,tsx}',
        'packages/*/src/**/*.{ts,tsx}',
      ],
      exclude: [
        '**/*.{test,spec}.{ts,tsx}',
        'platform/frontend/src/test/**',
        '**/*.d.ts',
        'platform/frontend/src/main.tsx',
        '**/vite-env.d.ts',
        // IADR-0121 決定 3: orval の生成物は計測対象外（自動生成物の品質は生成器の責務であり、
        // 母数へ入れると床が「生成量」で動いて意味を失う）。
        'platform/frontend/src/foundation/api/generated/**',
      ],
      // 回帰防止のラチェット。床を割る変更を CI で止める（レビュー #168 指摘対応・IADR-0034）。
      //
      // ［2026-08-04 / #446］移行第 1 段（ADR-0031 / IADR-0121）に伴う引き上げ。
      //   実測（Node 22.22.2 / pnpm 10.33.0 / Vitest 3.2.7 + v8 provider /
      //         submodule `src/ai-stock-trading` populate 済み）:
      //     全ユニット横断        lines/statements 91.46% / branches 82.33% / functions 83.58%
      //     MSP 所有分のみ        lines/statements 88.07% / branches 80.00% / functions 80.76%
      //     （MSP 所有分 = platform/frontend + knowledge/frontend + packages/*。AST の実装を
      //       母数から外して測り直した値）
      //   Vitest 2.1.9 時点の実測は横断 91.69 / 82.04 / 83.14、MSP 所有分 88.36 / 79.53 / 80.00 だった。
      //   GHSA-5xrq-8626-4rwp（critical）対応で 3.2.7 へ上げた際に v8 provider の計上差で ±0.4pt 未満
      //   動いたが、床の導出値は変わらない。
      //
      //   床は **MSP 所有分の実測から 5pt 下** に置く（実測 83% に対し床 78 を置いていた従来と同じ作法。
      //   計測ゆらぎで「成果物は正しいのに赤」にならない余裕だけを残す）。AST 側の実測が高いため
      //   横断値はこれより高く出るが、**床を横断値に合わせない**——AST は独自の計画と ADR を持つ
      //   別プロジェクト（submodule）であり、そこに床を依存させると AST の pin 更新だけで本リポの
      //   ゲートが動く（[IADR-0118](../docs/adr/IADR-0118_backend-coverage-floor.md) 決定 4 が
      //   バックエンドの床で名指しした「他プロジェクトのカバレッジを合算した濁り」と同じ失敗）。
      //   MSP 所有分を基準にすれば、AST が抜けても床は満たされる。
      //
      //   フォローアップ（#446 の申し送り）: フロントの計測範囲そのものから AST を外すか否かは
      //   IADR-0118 決定 4 との整合の問題であり、別 issue で判断する。
      thresholds: {
        lines: 83,
        statements: 83,
        functions: 75,
        branches: 74,
      },
    },
  },
});
