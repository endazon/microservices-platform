import { QueryClient } from '@tanstack/react-query';
import { ApiError } from './ApiError';

// ADR-0031, IADR-0121: サーバー状態は TanStack Query に一元化し、グローバルストア（Redux）を持たない。
// 本ファイルはアプリ全体で共有する QueryClient の唯一の生成点である。features は独自の QueryClient を
// 作らず、`useQuery` / orval 生成フックを通じてこのクライアントを使う。
//
// 既定値は「画面がまだ載っていない段階で安全側に倒す」ことを狙って置く。画面固有の要求
// （即時性が要る／再取得したくない等）は各 feature が個別に上書きする。

/** 一過性の失敗を吸収する再試行の上限（1 回だけ再試行する）。 */
export const MAX_QUERY_RETRIES = 1;

/**
 * 再試行の可否を決める。ネットワーク断・一過性の 5xx は 1 度だけ吸収し、**4xx は再試行しない**。
 *
 * 数値の `retry: 1` ではなく述語にしているのは、TanStack Query の数値指定が**エラー種別を区別せず**
 * 全ての失敗を同じ回数だけ再試行するためである。このシステムでは **4xx が異常ではなく通常の応答**
 * である——[IADR-0009] の存在秘匿により、権限外の資源へのアクセスは 404 として返る（画面は「不在」と
 * 「権限による秘匿」を区別しない）。つまり 404 は日常的に発生する。これを再試行すると、確実に失敗する
 * 2 回目の往復ぶんだけエラー表示が遅れ、BFF と後段サービスへ無駄な負荷をかける。401（再ログイン導線）・
 * 403・409（競合）も同様に、同じ要求を繰り返して結果が変わるものではない。
 *
 * 例外は 408（要求タイムアウト）と 429（要求過多）で、これらは時間を置けば解消し得るため再試行する。
 * 状態コードを持たない失敗（ネットワーク断 = `ApiError('network', …, null)`）と 5xx も再試行する。
 */
export function shouldRetryQuery(failureCount: number, error: unknown): boolean {
  // TanStack Query は失敗のたびに failureCount（0 起点・判定後に加算）を渡すため、
  // `failureCount < 1` が数値指定の `retry: 1`（＝再試行 1 回）と同じ回数になる。
  if (failureCount >= MAX_QUERY_RETRIES) return false;

  if (
    error instanceof ApiError &&
    error.status !== null &&
    error.status >= 400 &&
    error.status < 500
  ) {
    return error.status === 408 || error.status === 429;
  }
  return true;
}

export const DEFAULT_QUERY_OPTIONS = {
  /** ネットワーク断・一過性の 5xx を 1 度だけ吸収し、4xx は再試行しない（判定は shouldRetryQuery）。 */
  retry: shouldRetryQuery,
  /** 業務画面をタブ切り替えのたびに再取得しない（BFF と後段サービスへの無用な負荷を避ける）。 */
  refetchOnWindowFocus: false,
  /** 30 秒は再取得しない。SC 群の一覧・ダッシュボードはこの粒度で十分に新しい。 */
  staleTime: 30_000,
} as const;

/** アプリ用の QueryClient を生成する（テストは毎回新しいインスタンスを作ってキャッシュを分離する）。 */
export function createAppQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { ...DEFAULT_QUERY_OPTIONS },
    },
  });
}

/** アプリ本体が使う共有インスタンス。 */
export const queryClient = createAppQueryClient();
