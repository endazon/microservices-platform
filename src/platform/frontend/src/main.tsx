import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
// ADR-0031 / IADR-0121 決定 4: デザイントークンと base スタイルは共有 UI パッケージが単一情報源。
// 各ユニットは個別の CSS 体系を持たない。外部 CDN・Web フォントは読み込まない（08_data-egress-policy）。
import '@platform/ui/styles.css';
import { initI18n } from '@foundation/i18n';
import { applyStoredTheme } from './lib/theme/theme';
import { App } from './app/App';

// ADR-0031 / IADR-0125 決定 7: ロケール切替の UI は持たない（計画の §共通シェル に要素が無い）。
// 実行時はブラウザの言語設定から ja / en を決める。未対応言語は ja へ倒す。
initI18n();

// ADR-0031 / 利用者裁定（2026-09-12）: 保存されたテーマ（`system` / `light` / `dark`）を
// **React の描画より前**に `<html data-theme>` へ当てる。描画してから当てると、
// 最初の 1 フレームだけ反対のテーマが見える（flash of wrong theme）。
applyStoredTheme();

const rootEl = document.getElementById('root');
if (!rootEl) {
  throw new Error('root element not found');
}

createRoot(rootEl).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
