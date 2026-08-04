import type { Preview } from '@storybook/react-vite';
// ADR-0031 / IADR-0121 決定 4: デザイントークンと base スタイルの単一情報源。
// 08_data-egress-policy: ここから読み込むのは npm パッケージ（Tailwind）とシステムフォントだけで、
// 外部 CDN・Web フォントは読まない（styles.css 冒頭の注記を参照）。
import '../src/styles.css';

const preview: Preview = {
  parameters: {
    // 背景色の切替は用意しない。ダークテーマのトークンは未確定であり（packages/ui/README.md）、
    // カタログだけに存在する色を作ると実装との差が生まれる。
    backgrounds: { disable: true },
    controls: { expanded: true },
  },
};

export default preview;
