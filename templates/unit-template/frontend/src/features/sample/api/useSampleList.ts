import { useQuery } from '@tanstack/react-query';
import type { SampleFilter, SampleListItem } from '../types';

// テンプレート: feature のサーバー状態。計画 13_frontend-stack §ディレクトリ構成 の `api/`。
//
// **サーバー状態は TanStack Query に一元化する**（ADR-0031 §基本方針。グローバルストア（Redux）は
// 持たない）。QueryClient の生成点は `@foundation/api/queryClient.ts` ただ 1 つで、ユニット側は作らない。
//
// **BFF 呼び出しは orval 生成フックを使う**（ADR-0031 §基本方針「手書きクライアント禁止」）。
// 実際のユニットでは、この関数の中身は次のように生成フックの呼び出しになる:
//
//   import { useBffSampleList } from '@foundation/api/generated/sample/sample';
//   import { okArray } from '@foundation/api/orvalSelect';
//   export const useSampleList = (filter: SampleFilter) =>
//     useBffSampleList({ keyword: filter.keyword }, { query: { select: okArray } });
//
// 生成物の入力は `docs/api/openapi.yaml` の `/bff/` 配下のみで、`pnpm run codegen` で再生成し
// コミットする（IADR-0121 決定 3。CI が再生成差分を検査する）。**手書きの HTTP クライアント
// （fetch / axios 等）は ESLint が error にする**（IADR-0121 決定 8）。
//
// 雛形の時点では BFF に契約が無いため、フックの「形」だけを示す。契約が生えたら本体を
// 生成フックへ差し替える——**queryFn を手書きのまま残さないこと**。

/** 一覧の取得。契約が生えるまでの仮実装（生成フックへ差し替える）。 */
export const useSampleList = (filter: SampleFilter) =>
  useQuery<SampleListItem[]>({
    // キーへ絞り込み条件を含める（条件が変われば別のキャッシュ）。
    queryKey: ['sample', 'list', filter.keyword],
    queryFn: () => Promise.resolve([]),
  });
