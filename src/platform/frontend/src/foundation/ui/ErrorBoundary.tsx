import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { ErrorBoundary as ReactErrorBoundary } from 'react-error-boundary';
import type { ReactNode } from 'react';
import { ApiError } from '@foundation/api/ApiError';

// Issue #126: 画面共通のエラーバウンダリ。ApiError の種別に応じた中立メッセージを出す
// （IADR-0009: notFound は不在/秘匿を区別しない）。想定外の例外も UI を壊さず握る。
//
// ADR-0031 §採用技術一覧「Error Boundary = react-error-boundary」/ IADR-0121 決定 1 の第 4 段（#788）:
// **自前の class コンポーネントを捨て、計画が採用と定めたライブラリへ載せ替えた。**
// 公開面（`<ErrorBoundary>{children}</ErrorBoundary>`）と描画結果は変えていない——変えると
// `App.tsx` の呼び出し側と、この境界に依存する画面の見え方が同時に動く。
// 載せ替えで得るのは (1) `useErrorBoundary()` による**関数コンポーネントからの送出**、
// (2) `resetKeys` / `onReset` による復帰、(3) class の保守（`getDerivedStateFromError` の
// 取り違え）を自前で持たないこと、の 3 点である。

/** 境界が捕まえた例外の表示。ApiError は自身の中立メッセージ、それ以外は既定文言へ倒す。 */
function ErrorFallback({ error }: { error: unknown }) {
  const message =
    error instanceof ApiError
      ? error.message
      : i18n._(msg`予期しないエラーが発生しました。時間をおいて再度お試しください。`);
  return (
    <main role="alert" style={{ padding: '2rem', textAlign: 'center' }}>
      <h1>{i18n._(msg`エラー`)}</h1>
      <p>{message}</p>
    </main>
  );
}

export function ErrorBoundary({ children }: { children: ReactNode }) {
  return (
    <ReactErrorBoundary
      FallbackComponent={ErrorFallback}
      onError={(error, info) => {
        // 可観測性: 実運用では監視へ送る。ここでは最小にコンソール出力に留める。
        console.error('UI error boundary caught:', error, info.componentStack);
      }}
    >
      {children}
    </ReactErrorBoundary>
  );
}
