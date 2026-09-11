import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { ErrorBoundary as ReactErrorBoundary } from 'react-error-boundary';
import type { ReactNode } from 'react';
import { Button, ErrorState, buttonVariants } from '@platform/ui';
import { ApiError } from '@foundation/api/ApiError';

// Issue #126: 画面共通のエラーバウンダリ。ApiError の種別に応じた中立メッセージを出す
// （IADR-0009: notFound は不在/秘匿を区別しない）。想定外の例外も UI を壊さず握る。
//
// ADR-0031 §採用技術一覧「Error Boundary = react-error-boundary」/ IADR-0121 決定 1 の第 4 段（#788）:
// **自前の class コンポーネントを捨て、計画が採用と定めたライブラリへ載せ替えた。**
// 載せ替えで得るのは (1) `useErrorBoundary()` による**関数コンポーネントからの送出**、
// (2) `resetKeys` / `onReset` による復帰、(3) class の保守（`getDerivedStateFromError` の
// 取り違え）を自前で持たないこと、の 3 点である。
//
// ［2026-09-12 / UI/UX 改善 裁定 6］**この境界は「アプリ全体」の最後の砦である。**
// 画面 1 枚の失敗はルータ側の境界（`app/routing/routeStates.tsx` の `RouteErrorComponent`）が
// 共通シェルを残したまま本文だけを差し替えるので、ここまで上がってくるのは
// **ルータの外**（プロバイダ・i18n・描画そのもの）で壊れた場合だけである。
// そのときシェルは存在しないため、**素の全画面表示**で出す。
//
// 表示は三部品の `ErrorState`（@platform/ui）へ寄せた。従前は素の `<h1>` ＋ `<p>` で、
// **次の一手が 1 つも無かった**——利用者にはページを閉じる以外の選択肢が無い状態だった。
//   - **再読み込み**: `resetErrorBoundary()` で境界を戻し、子を描き直す。
//   - **ホームへ戻る**: ここは**ルータの外**なので `<Link>` は使えない（router context が
//     壊れている可能性がある場所である）。素の `<a href="/">` で**アプリを読み直す**。
// `onReset` で `queryClient.clear()` はしない——ここへ来る例外はキャッシュ由来とは限らず、
// 消せば直るという根拠が無い（消すのは確実な副作用だけ）。
//
// 🔴 **`role="alert"` は `ErrorState` が持つ。** 自分で付け足すと二重に読み上げられる。

/** 境界が捕まえた例外の表示。ApiError は自身の中立メッセージ、それ以外は既定文言へ倒す。 */
function ErrorFallback({
  error,
  resetErrorBoundary,
}: {
  error: unknown;
  resetErrorBoundary: () => void;
}) {
  const message =
    error instanceof ApiError
      ? error.message
      : i18n._(msg`予期しないエラーが発生しました。時間をおいて再度お試しください。`);
  return (
    <main className="grid min-h-screen place-items-center bg-bg px-n4 py-n8 text-fg">
      <ErrorState
        className="w-full max-w-md"
        title={i18n._(msg`エラー`)}
        description={message}
        action={
          <div className="flex flex-wrap items-center justify-center gap-n2">
            <Button variant="primary" size="sm" onClick={resetErrorBoundary}>
              {i18n._(msg`再読み込み`)}
            </Button>
            <a href="/" className={buttonVariants({ size: 'sm' })}>
              {i18n._(msg`ホームへ戻る`)}
            </a>
          </div>
        }
      />
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
