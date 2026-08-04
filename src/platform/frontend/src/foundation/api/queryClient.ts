import { QueryClient } from '@tanstack/react-query';

// ADR-0031, IADR-0121: サーバー状態は TanStack Query に一元化し、グローバルストア（Redux）を持たない。
// 本ファイルはアプリ全体で共有する QueryClient の唯一の生成点である。features は独自の QueryClient を
// 作らず、`useQuery` / orval 生成フックを通じてこのクライアントを使う。
//
// 既定値は「画面がまだ載っていない段階で安全側に倒す」ことを狙って置く。画面固有の要求
// （即時性が要る／再取得したくない等）は各 feature が個別に上書きする。
export const DEFAULT_QUERY_OPTIONS = {
  /** ネットワーク断・一過性の 5xx を 1 度だけ吸収する。4xx（権限・検証）は再試行しても無駄なため深追いしない。 */
  retry: 1,
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
