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
  // その一致は platform/frontend/src/lib/i18n/i18n.test.tsx が固定する。
  plugins: [react({ babel: { plugins: ['@lingui/babel-plugin-lingui-macro'] } })],
  resolve: {
    // IADR-0121 決定 2（pnpm workspace）: pnpm は node_modules を isolated に置くため、ユニットごとに
    // 別々の React 実体が解決され得る（同一プロセスで 2 つの React が動くと「Invalid hook call」になる）。
    // 横断テストは 1 プロセスで全ユニットのコンポーネントを描画するので、React を明示的に重複排除する。
    dedupe: ['react', 'react-dom'],
    alias: {
      // ADR-0031（§ディレクトリ構成）/ IADR-0262 決定 1（第 2 段）: `@foundation` は
      // **ディレクトリ名ではなく platform 基盤の公開面の名前**である。実体は計画のツリーに従って
      // app/ lib/ components/ testing/ へ分かれているので、区分ごとに向き先を張る。
      // 同じ 10 本を platform/frontend/tsconfig.app.json と platform/frontend/vite.config.ts にも置く。
      // ADR-0067 決定 5 / IADR-0333（#1131）: `@foundation/utils` を足した（改名ではなく追加）。
      '@foundation/config': fileURLToPath(
        new URL('./platform/frontend/src/config', import.meta.url),
      ),
      '@foundation/i18n': fileURLToPath(
        new URL('./platform/frontend/src/lib/i18n', import.meta.url),
      ),
      '@foundation/routing': fileURLToPath(
        new URL('./platform/frontend/src/app/routing', import.meta.url),
      ),
      '@foundation/api': fileURLToPath(new URL('./platform/frontend/src/lib/api', import.meta.url)),
      '@foundation/auth': fileURLToPath(
        new URL('./platform/frontend/src/lib/auth', import.meta.url),
      ),
      '@foundation/utils': fileURLToPath(new URL('./platform/frontend/src/utils', import.meta.url)),
      '@foundation/ui': fileURLToPath(
        new URL('./platform/frontend/src/components/ui', import.meta.url),
      ),
      '@foundation/notifications': fileURLToPath(
        new URL('./platform/frontend/src/components/notifications', import.meta.url),
      ),
      '@foundation/ai-chat': fileURLToPath(
        new URL('./platform/frontend/src/components/ai-chat', import.meta.url),
      ),
      '@foundation/testing': fileURLToPath(
        new URL('./platform/frontend/src/testing', import.meta.url),
      ),
      '@features': fileURLToPath(new URL('./platform/frontend/src/features', import.meta.url)),
      '@knowledge': fileURLToPath(new URL('./knowledge/frontend/src', import.meta.url)),
      // Issue #283, FR-14, IADR-0056/0070: AST（ai-stock-trading）ユニットの feature テストも横断収集する。
      '@ai-stock-trading': fileURLToPath(
        new URL('./ai-stock-trading/frontend/src', import.meta.url),
      ),
    },
  },
  // FR-14 / IADR-0060: 雛形（`templates/*/frontend`）は Vite のルート（`src/`）の外にある。
  // 既定では読み込みが拒否され「ファイルが在るのに Cannot find module」になる（実測）ため、
  // 1 階層上まで許可する。**pnpm workspace のメンバでもあるので解決自体は通る**が、
  // fs の許可は別の関門である。
  server: { fs: { allow: ['..'] } },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./platform/frontend/src/testing/setup.ts'],
    include: [
      'platform/frontend/src/**/*.{test,spec}.{ts,tsx}',
      'knowledge/frontend/src/**/*.{test,spec}.{ts,tsx}',
      'ai-stock-trading/frontend/src/**/*.{test,spec}.{ts,tsx}',
      // IADR-0121 決定 4: 共有 UI パッケージ（@platform/ui）も横断計測の対象にする。
      'packages/*/src/**/*.{test,spec}.{ts,tsx}',
      // FR-14 / IADR-0060: 追加可変機能ユニットの**雛形**のテストも実行する。
      // **走らないテストを雛形に置かない**——雛形は複製されるので、腐ったテストの型を
      // 全新規ユニットへ配ることになる。カバレッジの母数には入れない（下の coverage.include に
      // templates を含めない）ので、ラチェットの水準は動かさない。
      '../templates/*/frontend/src/**/*.{test,spec}.{ts,tsx}',
      // FR-20 / ADR-0037 決定 1 / IADR-0338 決定 6: 自作 Obsidian プラグインのプロトコル部
      // （manifest / pull の client・差分計算・命名・トークン保管）を Obsidian 実体なしで固定する。
      // カバレッジの母数には入れない（下の coverage.include に含めない。雛形と同じ扱いで、
      // ラチェットの水準を動かさない。算入は IADR-0338 のフォローアップ）。
      // **ここへ足したら frontend-tests.yml の paths: にも足す**（scripts.repo.test.js #801 節が突合する）。
      'obsidian-plugin/src/**/*.{test,spec}.{ts,tsx}',
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
        // ADR-0031 / IADR-0124 / IADR-0262 決定 1: 横断 setup と、可変ユニットの画面テスト用
        // ハーネス（テスト専用の足場）。IADR-0262 の第 2 段で `src/test/` と
        // `src/foundation/testing/` が計画のツリーの `testing/` へ 1 本化されたため、除外も 1 本になった。
        // 足場を数えると「テストを足すほど床が上がる」見かけの改善が起き、成果物の被覆率が読めなくなる。
        'platform/frontend/src/testing/**',
        // ADR-0031 / IADR-0125 決定 5: Storybook のカタログ（stories と設定）。
        // `testing/**` と同じ理由で母数から外す——カタログは
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
        'platform/frontend/src/lib/api/generated/**',
      ],
      // 回帰防止のラチェット。床を割る変更を CI で止める（レビュー #168 指摘対応・IADR-0034）。
      //
      // ［2026-08-09 / #539］SC-01 / SC-08 の対象範囲フィルタに伴う引き上げ。
      //   実測（測定条件は上と同じ。ブランチ `claude/handover-work-start-7g1vu3` /
      //         `pnpm run test:coverage`）:
      //     全ユニット横断        lines/statements 96.48%（5740/5949）/ branches 90.78%（1182/1302）/
      //                           functions 91.87%（407/443）
      //     MSP 所有分のみ        lines/statements 95.98%（4473/4660）/ branches 91.95%（915/995）/
      //                           functions 93.17%（314/337）
      //   同じ導出規則（MSP 所有分の実測から 5pt 下・切り捨て）を適用して床を
      //   **branches 85 → 86** へ引き上げる。**lines/statements 90・functions 88 は据え置き**
      //   （95.98 − 5 = 90.98 の切り捨てで 90、93.17 − 5 = 88.17 の切り捨てで 88。どちらも現行と同値）。
      //   **当初この節へ「数ポイントずつ上げる」という別の作法で 92/92/90/88 を書いたが、
      //   本ファイルが定めているのは「MSP 所有分の実測から 5pt 下」であり、誤りだったので測り直した。**
      //   上げた分は「対象範囲の分岐（軸ごとの候補の有無・選択の切り替え・空の軸を載せない・
      //   1 軸だけ失敗したときの縮退・候補が 1 つも無いときの中立表示）に純関数テストと
      //   描画テストを付けた」ことによる。
      //   **`coverage.exclude` は増やしていない**（除外で稼いだ引き上げではない）。
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
      //   ゲートが動く（[IADR-0118](../.ai-context/adr/IADR-0118_backend-coverage-floor.md) 決定 4 が
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
      //   下の exclude に足した `foundation/testing/**`（テスト用ハーネス。現 `testing/**`）が床を甘くしていないことを
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
      //   **上に足した `**\/*.stories.*` の除外は床の水準を動かす**（`foundation/testing/**`（現 `testing/**`）を
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
      //     全ユニット横断        lines/statements 95.06% / branches 88.13% / functions 89.25%（357/400）
      //     MSP 所有分のみ        lines/statements 93.94% / branches 88.56% / functions 89.80%（264/294）
      //   同じ導出規則（MSP 所有分の実測から 5pt 下・切り捨て）を適用して床を
      //   branches 81 → 83 / functions 82 → 84 へ引き上げる（lines/statements は 88 のまま
      //   ＝ 93.94 − 5 = 88.94 の切り捨て）。
      //   **［2026-08-05 追記・実測値の是正（PR #508 のレビュー / クロス監査 P-3）］**
      //   当初ここには 全ユニット横断 functions **89.20%** と書いていたが、その時点の実測は
      //   **89.19%（355/398）**であり 0.01pt の転記誤りだった（`f11d4c3` の worktree で再実走して確認。
      //   355/398 = 89.1959…% で、v8 の要約は 2 桁で**切り捨てる**——同じ実行の
      //   statements 4724/4970 = 95.0503…% が 95.05% と出ることで確かめられる）。上の数値は、レビュー / 監査の是正
      //   （別ミューテーションの失敗バナー・琥珀の充て先・機密区分の純関数テスト）を反映した**再測定値**である。
      //   **床（88 / 88 / 84 / 83）の導出には影響しない**——是正の前後いずれの実測でも同じ床が出る。
      //   上げた分は「4 画面の分岐（4 状態モデルの写像・同期状態の導出・権限別の出し分け・
      //   存在秘匿の中立表示・409 の区別）と、契約に無い要素が**描かれないこと**にテストを
      //   付けた」ことによる。branches は #502 と同じく、旧 4 画面の手書きの取得・再取得
      //   （useCallback + useEffect + load() の呼び直し）を TanStack Query へ置き換えて
      //   **測るべき分岐そのものが減った**ぶんも含む（IADR-0127 決定 5）。
      //   **`coverage.exclude` は増やしていない**（除外で稼いだ引き上げではない）。
      //
      // ［2026-08-05 / #504］SC-09〜11 の新スタックでの再実装に伴う引き上げ。
      //   実測（測定条件は上と同じ。worktree `feat/SC-09-11-admin-ops-screens` / `pnpm run test:coverage`）:
      //     全ユニット横断        lines/statements 95.91%（5429/5660）/ branches 89.79%（1118/1245）/
      //                           functions 91.81%（415/452）
      //     MSP 所有分のみ        lines 95.22%（4162/4371）/ branches 90.72%（851/938）/
      //                           functions 93.06%（322/346）
      //   同じ導出規則（MSP 所有分の実測から 5pt 下・切り捨て）を適用して床を
      //   lines/statements 88 → 90 / branches 83 → 85 / functions 84 → 88 へ引き上げる。
      //   上げた分は「3 画面の分岐（存在秘匿の中立表示・領域ごとの縮退・権限別の出し分け・
      //   403/404 の中立化・未知の値の素通し）と、**着手保留（FR-17 / FR-18）・契約の不在の要素が
      //   描かれないこと**にテストを付けた」ことと、**値集合を純関数テストで固定した**
      //   （`abacVocabulary` / `driftView` / `opsTools`。IADR-0129 決定 6）ことによる。
      //   branches の伸びは #502 / #503 と同じく、旧 3 画面の手書きの取得・再取得
      //   （useEffect ＋ 複数の state ＋ load() の呼び直し）を TanStack Query へ置き換えて
      //   **測るべき分岐そのものが減った**ぶんも含む。
      //   **`coverage.exclude` は増やしていない**（除外で稼いだ引き上げではない）。
      //
      // ［2026-08-09 / #629］文書の書き込みを管理者限定へ狭めたことに伴う引き上げ。
      //   実測（worktree `fix/FR-06-document-write-admin-only` / `pnpm run test:coverage`。
      //   MSP 所有分は lcov.info を `ai-stock-trading` の有無で分けて集計した）:
      //     全ユニット横断  lines/statements 96.5%（5768/5977）/ branches 90.98%（1201/1320）/
      //                     functions 91.94%（411/447）
      //     MSP 所有分のみ  lines 96.01%（4501/4688）/ branches 92.20%（934/1013）/
      //                     functions 93.25%（318/341）
      //   同じ導出規則（**MSP 所有分の実測から 5pt 下・切り捨て**）を適用して
      //   lines/statements 90 → 91 / branches 86 → 87 へ引き上げる（functions は 88 のまま）。
      //   上げた分は「SC-05 の**権限別の出し分け**（運用者に書き込みの導線を 1 つも出さない・
      //   管理者には出す・閲覧は狭めない）を対で固定した」ことによる。
      //   **`coverage.exclude` は増やしていない**（除外で稼いだ引き上げではない）。
      // ［2026-08-10 / #651］SC-07 の人手補正 UI（2 ペイン編集・導出標識・409 駆動の確認）。
      //   実測（worktree `claude/issue-651-sc07-correction` / `pnpm run test:coverage`。
      //   MSP 所有分は lcov.info を `ai-stock-trading` の有無で分けて集計した）:
      //     全ユニット横断  lines/statements 96.80%（6290/6498）/ branches 90.95%（1326/1458）/
      //                     functions 92.21%（450/488）
      //     MSP 所有分のみ  lines 96.43%（5023/5209）/ branches 92.01%（1059/1151）/
      //                     functions 93.46%（357/382）
      //   同じ導出規則（MSP 所有分の実測から 5pt 下・切り捨て）から出る床は **91 / 87 / 88**
      //   であり、**現行と同値なので引き上げない**（96.43 − 5 = 91.43 → 91、92.01 − 5 = 87.01 → 87、
      //   93.46 − 5 = 88.46 → 88）。
      //
      //   **［測って直した］最初の実測では MSP 所有分の branches が 91.70% で、導出床が 86 と
      //   現行の 87 を下回った。** 床はラチェットなので下げないが、**下回った理由は
      //   「新しい分岐にテストが付いていない」ことだった**（`useConversionJobs.ts` 73.08% /
      //   `FigureCorrectionPanel.tsx` 85.00%）。そこで図一覧の取得失敗・補正対象が無い場合・
      //   画像 404 の縮退・補正済みの図の引き継ぎに試験を足し、**92.01% へ戻してから確定させた**。
      //   **床を下げる判断でも、床の据え置きで済ませる判断でもなく、被覆を戻す判断を採った。**
      //   **`coverage.exclude` は増やしていない**（除外で稼いだ数値ではない）。
      // ［2026-08-23 / #439］BFF セッション移行（3b）に伴う引き上げ。
      //   実測（測定条件は上と同じ。ブランチ `claude/implementation-repo-all-issues-hilvbs` /
      //         `pnpm run test:coverage`。MSP 所有分は lcov.info を `ai-stock-trading` の有無で分けて集計した）:
      //     全ユニット横断  lines/statements 98.02%（8255/8421）/ branches 91.52%（1857/2029）/
      //                     functions 94.14%（563/598）
      //     MSP 所有分のみ  lines 98.03%（5671/5785）/ branches 92.66%（1237/1335）/
      //                     functions 94.75%（397/419）
      //   同じ導出規則（MSP 所有分の実測から 5pt 下・切り捨て）を適用して床を
      //   lines/statements 91 → 93 / functions 88 → 89 へ引き上げる（branches は 87 のまま
      //   ＝ 92.66 − 5 = 87.66 の切り捨て）。
      //   上げた分は「認証の置き換え（AuthProvider の /me 読み・401 の静黙・遷移の集約）に
      //   テストを付けた」ことと、**カバレッジの低かった oidc-client-ts 依存コード
      //   （authConfig / CallbackPage）が実装ごと消えた**ことによる。
      //   **`coverage.exclude` は増やしていない**（除外で稼いだ引き上げではない）。
      // ［2026-08-28 / #453］波 4 の掃き寄せでのラチェット（#453 = カバレッジ床の起票 issue。
      //   src/coverage-floor.json も同じ番号を引く。近隣の項が引く #539 等と同じく「原因」を示す番号である）。
      //   実測（測定条件は上と同じ。ブランチ `claude/implementation-repo-all-issues-6pzgm1` /
      //         `pnpm run test:coverage`。MSP 所有分は lcov.info を `ai-stock-trading` の有無で分けて集計した）:
      //     全ユニット横断  lines/statements 98.19%（10110/10296）/ branches 92.22%（2300/2494）/
      //                     functions 93.74%（674/719）
      //     MSP 所有分のみ  lines 98.25%（7526/7660）/ branches 93.33%（1680/1800）/
      //                     functions 94.07%（508/540）
      //   同じ導出規則（MSP 所有分の実測から 5pt 下・切り捨て）を適用すると
      //     lines/statements 98.25 − 5 = 93.25 → 93（据え置き）
      //     branches         93.33 − 5 = 88.33 → **88（87 から引き上げ）**
      //     functions        94.07 − 5 = 89.07 → 89（据え置き）
      //   🔴 **横断の実測（92.22 等）から 5pt を引かない。** 導出規則の母数は MSP 所有分である
      //   （AST 分は別プロジェクトの被覆であり、本リポジトリの努力で動かせない）。横断の実測へ
      //   直接寄せる（例: branches 92）と、AST 側のテスト増減だけで本リポジトリの CI が赤くなる。
      //   引き上げ分は波 1〜3 で足したテスト（認可の分岐評価・削除伝播・個人資料 BFF・
      //   SC-19/SC-20 画面・検索観測）による。**`coverage.exclude` は増やしていない。**
      thresholds: {
        lines: 93,
        statements: 93,
        functions: 89,
        branches: 88,
      },
    },
  },
});
