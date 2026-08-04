import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
// ADR-0031 / IADR-0121 決定 4: デザイントークンと base スタイルは共有 UI パッケージが単一情報源。
// 各ユニットは個別の CSS 体系を持たない。外部 CDN・Web フォントは読み込まない（08_data-egress-policy）。
import '@platform/ui/styles.css';
import { App } from './App';

const rootEl = document.getElementById('root');
if (!rootEl) {
  throw new Error('root element not found');
}

createRoot(rootEl).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
