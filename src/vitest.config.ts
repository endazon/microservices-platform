import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';

// FR-14, IADR-0056: 単体テスト＋カバレッジはワークスペースルート（src/）で全ユニット
// （platform / knowledge の各 frontend）を横断計測する。ビルド・dev サーバは
// platform/frontend/vite.config.ts。
export default defineConfig({
  // ADR-0031（i18n = Lingui・コンパイル時抽出）/ IADR-0125 決定 3:
  // マクロ（@lingui/react/macro の <Trans> 等）を babel で展開する。
  // **同じ設定を platform/frontend/vite.config.ts にも置く**——片方だけに入れると
  // 「テストは通るのにビルドが壊れる（あるいはその逆）」という静かな破綻になる。
  // その一致は platform/frontend/src/foundation/i18n/i18n.test.tsx が固定する。
  plugins: [react({ babel: { plugins: ['@lingui/babel-plugin-lingui-macro'] } })],
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
        // ADR-0031 / IADR-0124: 可変ユニットの画面テスト用ハーネス（テスト専用の足場）。
        // `src/test/**` と同じ理由で母数から外す——足場を数えると「テストを足すほど床が上がる」
        // 見かけの改善が起き、成果物の被覆率が読めなくなる。
        'platform/frontend/src/foundation/testing/**',
        // ADR-0031 / IADR-0125 決定 5: Storybook のカタログ（stories と設定）。
        // `src/test/**`・`foundation/testing/**` と同じ理由で母数から外す——カタログは
        // **部品の見本であって成果物ではない**。母数へ入れると「stories を足すほど床が下がり、
        // 消すほど上がる」という、被覆率とは無関係な動き方をする。
        // **この除外は床の水準を実際に動かす**（除外あり／なしの実測は下の注記を参照）。
        '**/*.stories.{ts,tsx}',
        '**/.storybook/**',
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
      //
      // ［2026-08-04 / #490］移行第 2 段（ADR-0031 / IADR-0124）に伴う引き上げ。
      //   実測（測定条件は上と同じ。worktree `feat/ADR-0031-spa-router-shell` / `pnpm run test:coverage`）:
      //     全ユニット横断        lines/statements 93.79% / branches 83.54% / functions 85.53%
      //     MSP 所有分のみ        lines/statements 91.73% / branches 82.04% / functions 84.43%
      //   同じ導出規則（MSP 所有分の実測から 5pt 下・切り捨て）を適用して床を
      //   lines/statements 83 → 86 / functions 75 → 79 / branches 74 → 77 へ引き上げる。
      //   上げた分は「ルータ移行で新設した配線（ルート木・共通シェル・通知・存在秘匿）と、
      //   オープンリダイレクト対策（IADR-0124 決定 9）にテストを付けた」ことによる。
      //
      //   下の exclude に足した `foundation/testing/**`（テスト用ハーネス）が床を甘くしていないことを
      //   実測で確認した——除外**しない**場合の MSP 所有分は
      //   lines 91.84% / branches 82.19% / functions 84.02% であり、同じ導出規則から出る床は
      //   **3 指標とも同値（86 / 77 / 79）**。すなわちこの除外は床の水準を動かしていない。
      //
      // ［2026-08-04 / #496］移行第 2 段の残り（ADR-0031 / IADR-0125: shadcn/ui 本移植・Lingui・
      //   Storybook）に伴う引き上げ。
      //   実測（測定条件は上と同じ。worktree `feat/ADR-0031-ui-i18n-storybook` / `pnpm run test:coverage`）:
      //     全ユニット横断        lines/statements 93.86% / branches 84.11% / functions 86.58%
      //     MSP 所有分のみ        lines/statements 92.04% / branches 82.93% / functions 86.08%
      //   同じ導出規則（MSP 所有分の実測から 5pt 下・切り捨て）を適用し、床を
      //   lines/statements 86 → 87 / functions 79 → 81 へ引き上げる（branches は 77 のまま
      //   ＝ 82.93 − 5 = 77.93 の切り捨て）。
      //
      //   **上に足した `**\/*.stories.*` の除外は床の水準を動かす**（`foundation/testing/**` を
      //   足したときと違い、ここは「動かしていない」と言えない）。除外**しない**場合の
      //   MSP 所有分は lines 87.96% / branches 82.95% / functions 86.13% であり、同じ導出規則から
      //   出る床は **lines 82 / branches 77 / functions 81**。すなわち lines だけが 87 → 82 と
      //   5pt 甘くなる。差は stories 1 ファイル（145 行・テストから実行されない）に由来する。
      //   除外を採るのは、カタログの行数が被覆率を左右する状態そのものが誤りだからである
      //   （stories を消すと床が上がる）。**除外なしの実測でも現行床 86 は満たしている**
      //   （87.96% > 86）ため、この除外は「床を割るのを避けるための除外」ではない。
      //
      // ［2026-08-04 / #502］SC-01〜03 の新スタックでの再実装に伴う引き上げ。
      //   実測（測定条件は上と同じ。worktree `feat/SC-01-03-search-flow` / `pnpm run test:coverage`）:
      //     全ユニット横断        lines/statements 94.53% / branches 86.48% / functions 87.70%
      //     MSP 所有分のみ        lines/statements 93.07% / branches 86.29% / functions 87.69%
      //   同じ導出規則（MSP 所有分の実測から 5pt 下・切り捨て）を適用して床を
      //   lines/statements 87 → 88 / branches 77 → 81 / functions 81 → 82 へ引き上げる。
      //   上げた分は「3 画面の分岐（存在秘匿の中立表示・縮退運転・出典の種別判定・属性の写像）と、
      //   保留対象（FR-17 / FR-18）が**描かれないこと**にテストを付けた」ことによる。
      //   branches の伸び（82.93 → 86.29）が大きいのは、旧 3 画面が持っていた手書きの状態遷移
      //   （useEffect ＋ 4 つの state ＋ 二重発火ガード）を TanStack Query と URL 単一情報源へ置き換え、
      //   **測るべき分岐そのものが減った**ためでもある（IADR-0126 決定 3）。
      //
      // ［2026-08-05 / #503］SC-05〜08 の新スタックでの再実装に伴う引き上げ。
      //   実測（測定条件は上と同じ。worktree `feat/SC-05-08-admin-screens` / `pnpm run test:coverage`）:
      //     全ユニット横断        lines/statements 95.05% / branches 87.97% / functions 89.20%
      //     MSP 所有分のみ        lines/statements 93.91% / branches 88.34% / functions 89.73%
      //   同じ導出規則（MSP 所有分の実測から 5pt 下・切り捨て）を適用して床を
      //   branches 81 → 83 / functions 82 → 84 へ引き上げる（lines/statements は 88 のまま
      //   ＝ 93.91 − 5 = 88.91 の切り捨て）。
      //   上げた分は「4 画面の分岐（4 状態モデルの写像・同期状態の導出・権限別の出し分け・
      //   存在秘匿の中立表示・409 の区別）と、契約に無い要素が**描かれないこと**にテストを
      //   付けた」ことによる。branches は #502 と同じく、旧 4 画面の手書きの取得・再取得
      //   （useCallback + useEffect + load() の呼び直し）を TanStack Query へ置き換えて
      //   **測るべき分岐そのものが減った**ぶんも含む（IADR-0127 決定 5）。
      //   **`coverage.exclude` は増やしていない**（除外で稼いだ引き上げではない）。
      thresholds: {
        lines: 88,
        statements: 88,
        functions: 84,
        branches: 83,
      },
    },
  },
});
