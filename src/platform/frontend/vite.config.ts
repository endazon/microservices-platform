import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { visualizer } from 'rollup-plugin-visualizer';
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
  // NFR, ADR-0031 / IADR-0134: バンドル内訳の実測（`pnpm run build:analyze`）。
  // 既定のビルドには載せない（成果物と所要時間を変えないため）。
  // 出力先を **dist の外**（.analyze/）に置くのは、計測出力が配布物ではないためである——
  // dist に置くと、ビルドマシンの絶対パスを含む数 MB のモジュールグラフが
  // scripts/check-static-egress.js の走査母集団（＝配布する静的資産）へ紛れ込む。
  // .analyze/ は src/.gitignore でコミット対象から外している。
  plugins: [
    react({ babel: { plugins: ['@lingui/babel-plugin-lingui-macro'] } }),
    tailwindcss(),
    ...(process.env.ANALYZE_BUNDLE === '1'
      ? [visualizer({ filename: '.analyze/stats.json', template: 'raw-data', gzipSize: true })]
      : []),
  ],
  build: {
    rollupOptions: {
      output: {
        // NFR, ADR-0031 / IADR-0134: 初期チャンクの内訳を実測したうえでの分割。
        // 画面はルート単位の遅延（lazyRouteComponent）で外へ出したが、実測では初期チャンクの
        // **82.0% が依存**（1253.27 / 1527.89 kB rendered）であり、ルート境界では動かせない。
        // ここで扱うのは残りの 2 種類だけである。
        manualChunks(id) {
          if (!id.includes('/node_modules/')) {
            // 共有 UI プリミティブ（@platform/ui）は**全画面が使う**。放置すると Rollup が
            // 「2 つ以上の遅延チャンクが共有するモジュール」を 1 kB 未満のチャンクへ切り出し、
            //  往復だけが増える（実測: Label / Tag / Card / Input / Select / StatusBadge の 6 本）。
            // エントリが Button を静的 import しているため、この名前へ寄せると
            // **エントリの静的依存＝初期ロード**のまま 1 本に束ねられる。
            return id.includes('/packages/ui/') ? 'ui' : undefined;
          }
          const pkg = id.slice(id.lastIndexOf('/node_modules/') + '/node_modules/'.length);
          // React ランタイム。初期チャンク最大の塊（実測 561.39 / 1527.89 kB rendered = 全体の 36.7%）であり、
          // かつ**更新頻度が最も低い**。切り出す目的は初期ロード量の削減ではなく
          // （静的 import なので同時に読み込まれる）、(1) 再訪時にアプリ側の更新で
          // 無効化されないこと、(2) 1 チャンクの上限（500 kB）を守ることである。
          if (/^(react|react-dom|scheduler|use-sync-external-store)\//.test(pkg)) {
            return 'vendor-react';
          }
          // TanStack Query の内部も全画面が使う。放置すると @platform/ui と同じ理由で
          // 遅延チャンク側へ切り出される（実測: useMutation 3.10 kB / Table 11.41 kB の 2 本）。
          if (/^@tanstack\/(react-)?query(-core)?\//.test(pkg)) return 'vendor-query';
          // ADR-0031 §採用技術一覧（チャート = Apache ECharts・自己ホスト）/ #788:
          // ECharts と zrender は本リポジトリで最大級の依存である。**初期ロードへ入れない**
          // （IADR-0134 の初期ロード ratchet に直撃する）。実際の遅延は
          // `knowledge/frontend/src/components/echartsLoader.ts` の**動的 import**が作っており、
          // この規則はそれを 1 本のチャンクへ束ねて意図を固定するためのものである
          // （規則が無いと図を使う画面ごとに echarts の断片が散る）。
          //
          // 🔴 **規則を足したら `scripts/chunk-budget-baseline.json` の `requiredChunks` にも足す。**
          // あの配列は「初期ロードに載る規則」の一覧ではなく「manualChunks が返す名前」の一覧であり、
          // 遅延か否かを区別しない（区別しているのは `initialTotalBytes` のほう）。
          // 自己試験が両者の完全一致を突き合わせるため、片方だけ足すと CI が落ちる。
          if (/^(echarts|zrender)\//.test(pkg)) return 'vendor-echarts';
          return undefined;
        },
      },
    },
  },
  resolve: {
    alias: {
      '@foundation': fileURLToPath(new URL('./src/foundation', import.meta.url)),
      '@features': fileURLToPath(new URL('./src/features', import.meta.url)),
      '@knowledge': fileURLToPath(new URL('../../knowledge/frontend/src', import.meta.url)),
      // Issue #283, FR-14, IADR-0056/0070: AST（ai-stock-trading）ユニットの features をソース参照で合成する。
      '@ai-stock-trading': fileURLToPath(
        new URL('../../ai-stock-trading/frontend/src', import.meta.url),
      ),
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
