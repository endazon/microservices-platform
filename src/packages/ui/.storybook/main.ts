import type { StorybookConfig } from '@storybook/react-vite';

// ADR-0031（コンポーネントカタログ = Storybook）/ IADR-0125 決定 5。
//
// 08_data-egress-policy（外部 CDN・Web フォント・analytics の利用禁止／既定テレメトリの
// オプトアウト）を満たすための設定である。「設定した」だけでは退行を検出できないため、
// ビルド成果物の走査（scripts/check-static-egress.js）と対で運用する。
const config: StorybookConfig = {
  // カタログの対象は @platform/ui のプリミティブだけである（IADR-0121 決定 4 の切り出し単位）。
  // 各ユニットの画面（features）は入れない——ドメイン・通信・ルーティングを持ち込むと
  // カタログがアプリの複製になる。
  stories: ['../src/**/*.stories.@(ts|tsx)'],
  // アドオンを入れない。追加のたびに外部通信・外部アセットの経路が増え得るため、
  // 必要になった時点で 08_data-egress-policy に照らして判断する
  // （判断の材料は check-static-egress.js の走査結果が与える）。
  addons: [],
  framework: {
    name: '@storybook/react-vite',
    options: {},
  },
  core: {
    // 08_data-egress-policy §非LLM外部送信の統制:「既定テレメトリをオプトアウトする」。
    // Storybook は既定で利用状況を外部へ送る。
    disableTelemetry: true,
    // クラッシュレポートの外部送信も行わない（同ポリシー: 外部エラー報告 SaaS を使用しない）。
    enableCrashReports: false,
  },
  // **Autodocs は使わない。** Storybook 10 の docs エントリ生成は `@storybook/addon-docs` に
  // 依存しており、アドオンを入れない方針（上記）のままでは `docs` を設定しても
  // `index.json` に docs エントリが 1 件も生まれない（実測: entries は story 型 7 件のみ）。
  // 「設定はあるが効いていない」状態を残さないため設定ごと落とす。
  // 必要になったら 08_data-egress-policy に照らしてアドオンの追加を判断する。
  viteFinal: async (viteConfig) => {
    const { default: tailwindcss } = await import('@tailwindcss/vite');
    viteConfig.plugins = [...(viteConfig.plugins ?? []), tailwindcss()];
    // 相対パスで配信する（静的ビルドを任意のサブパスへ置ける。絶対 URL を焼き込まない）。
    viteConfig.base = './';
    return viteConfig;
  },
};

export default config;
