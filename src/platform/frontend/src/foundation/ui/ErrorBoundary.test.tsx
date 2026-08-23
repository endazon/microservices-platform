import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { ApiError } from '@foundation/api/ApiError';
import { ErrorBoundary } from './ErrorBoundary';

// ADR-0031 §採用技術一覧（Error Boundary = react-error-boundary）/ IADR-0121 決定 1 第 4 段（#788）:
// 自前 class からライブラリへ載せ替えても、**外から見た振る舞いが変わっていない**ことを固定する。
// 載せ替えの前後で差が出たら、それは境界の仕様変更であって内部の置換ではない。

function Boom({ error }: { error: unknown }): never {
  throw error;
}

afterEach(() => {
  vi.restoreAllMocks();
});

/** React は捕捉した例外を必ず console.error へ出す。テスト出力を読めるように黙らせる。 */
function silenceConsole() {
  return vi.spyOn(console, 'error').mockImplementation(() => {});
}

describe('ErrorBoundary', () => {
  // Issue #126: 例外が無ければ子をそのまま描く（境界が透過であること）。
  it('renders children when nothing throws', () => {
    render(
      <ErrorBoundary>
        <p>本文</p>
      </ErrorBoundary>,
    );
    expect(screen.getByText('本文')).toBeInTheDocument();
  });

  // IADR-0009: ApiError は「不在／権限なし」を区別しない中立メッセージを自ら持つ。
  // 境界はそれを**握り潰さずそのまま出す**（画面ごとに文言を作り直させない）。
  it('shows the ApiError message as-is', () => {
    silenceConsole();
    const error = new ApiError('notFound', '該当する情報が見つかりませんでした。', null);
    render(
      <ErrorBoundary>
        <Boom error={error} />
      </ErrorBoundary>,
    );
    expect(screen.getByRole('alert')).toHaveTextContent('該当する情報が見つかりませんでした。');
  });

  // Issue #126: 想定外の例外は中立の既定文言へ倒す（内部事情を画面へ出さない）。
  it('falls back to the neutral message for unexpected errors', () => {
    silenceConsole();
    render(
      <ErrorBoundary>
        <Boom error={new Error('internal detail leaked')} />
      </ErrorBoundary>,
    );
    const alert = screen.getByRole('alert');
    expect(alert).toHaveTextContent(
      '予期しないエラーが発生しました。時間をおいて再度お試しください。',
    );
    expect(alert).not.toHaveTextContent('internal detail leaked');
  });

  // NFR（可観測性）: 捕まえた例外は握り潰さずログへ出す。onError が呼ばれることを固定する
  // ——出さないと「画面は静かに真っ白、ログにも何も無い」障害になる。
  it('logs the caught error', () => {
    const spy = silenceConsole();
    render(
      <ErrorBoundary>
        <Boom error={new Error('boom')} />
      </ErrorBoundary>,
    );
    expect(spy.mock.calls.some((call) => call[0] === 'UI error boundary caught:')).toBe(true);
  });
});
