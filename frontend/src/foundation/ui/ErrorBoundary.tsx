import { Component } from 'react';
import type { ErrorInfo, ReactNode } from 'react';
import { ApiError } from '@foundation/api/ApiError';

// Issue #126: 画面共通のエラーバウンダリ。ApiError の種別に応じた中立メッセージを出す
// （IADR-0009: notFound は不在/秘匿を区別しない）。想定外の例外も UI を壊さず握る。
interface State {
  error: Error | null;
}

export class ErrorBoundary extends Component<{ children: ReactNode }, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    // 可観測性: 実運用では監視へ送る。ここでは最小にコンソール出力に留める。
    console.error('UI error boundary caught:', error, info.componentStack);
  }

  render(): ReactNode {
    const { error } = this.state;
    if (!error) return this.props.children;

    const message =
      error instanceof ApiError
        ? error.message
        : '予期しないエラーが発生しました。時間をおいて再度お試しください。';
    return (
      <main role="alert" style={{ padding: '2rem', textAlign: 'center' }}>
        <h1>エラー</h1>
        <p>{message}</p>
      </main>
    );
  }
}
