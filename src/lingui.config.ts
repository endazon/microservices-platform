import { defineConfig } from '@lingui/cli';
import type { CatalogFormatter } from '@lingui/conf';
import { formatter } from '@lingui/format-po';

/**
 * IADR-0125 決定 4: 未翻訳キーの検出は「`pnpm run i18n` の再生成に差分が出ないこと」を
 * 第 1 の検査に置く（orval 生成物と同じ作法。IADR-0121 決定 3）。ところが `@lingui/format-po` は
 * **実行時刻を `POT-Creation-Date` ヘッダへ毎回書き込む**ため、内容が変わっていなくても
 * カタログのバイト列が変わり、差分検査が常に赤になる（実測: 連続 2 回の extract で md5 が変化）。
 *
 * 当該ヘッダを無効化するオプションは `@lingui/format-po@6.6.0` に存在しない（型定義で確認）。
 * よって serialize の出力から**行ごと落とす**。固定の日時を書かない（嘘の値を残さない）のは、
 * 抽出日時が意味を持つ情報だからである——必要なら git のコミット日時が正確に答える。
 */
function deterministicPoFormatter(options?: Parameters<typeof formatter>[0]): CatalogFormatter {
  const base = formatter(options);
  return {
    ...base,
    serialize(catalog, ctx) {
      const out = base.serialize(catalog, ctx);
      return String(out).replace(/^"POT-Creation-Date: .*\\n"\r?\n/m, '');
    },
  };
}

// ADR-0031（i18n = Lingui〔ja / en〕・**コンパイル時抽出**）/ IADR-0125 決定 3・4。
//
// 置き場をワークスペースルート（src/）にするのは、既存の横断設定（orval.config.ts / vitest.config.ts）と
// 同じ作法に揃えるためである。抽出対象は**本リポジトリが所有するユニット**のみとし、
// `ai-stock-trading` は含めない——別プロジェクトの submodule であり、本リポの規約も文言も及ぼさない
// （IADR-0120）。
//
// カタログの実体は platform/frontend（アプリホスト）が持つ。計画の §ディレクトリ構成
// （13_frontend-stack）が `app/ # providers / router / i18n / config` と `locales/ # ja / en（Lingui）` を
// アプリ側に置いているのと同じ配置である。
export default defineConfig({
  // 既存文言は日本語である。未対応ロケールは ja へ倒す（@foundation/i18n の detectLocale）。
  sourceLocale: 'ja',
  locales: ['ja', 'en'],
  catalogs: [
    {
      path: '<rootDir>/platform/frontend/src/locales/{locale}/messages',
      include: ['<rootDir>/platform/frontend/src', '<rootDir>/knowledge/frontend/src'],
      exclude: [
        '**/node_modules/**',
        '**/*.{test,spec}.{ts,tsx}',
        // orval 生成物（自動生成物に文言は無い。走査対象へ入れると extract が無駄に重くなる）。
        '**/lib/api/generated/**',
      ],
    },
  ],
  // 行番号は入れない（`#: path:line` は無関係な編集で差分が動き、CI の再生成差分検査を騒がしくする）。
  format: deterministicPoFormatter({ lineNumbers: false }),
  // IADR-0125 決定 3: コンパイル結果を素の TS モジュールとして import する
  // （`@lingui/vite-plugin` は peer に rolldown を要求するため使わない）。
  compileNamespace: 'ts',
});
