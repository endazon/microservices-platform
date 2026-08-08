import type { ReactNode } from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import {
  getBffDocumentContentQueryKey,
  getBffDocumentDetailQueryKey,
  getBffDocumentListQueryKey,
  getBffDocumentVersionsQueryKey,
} from '@foundation/api/generated/documents/documents';
import { createAppQueryClient } from '@foundation/api/queryClient';
import { noContent } from '@foundation/testing/bffResponse';

// SC-05 → SC-03, UC-03, FR-06/FR-09, IADR-0135 決定 3［2026-08-06 追記］:
// **SC-05 の無効化が SC-03 の 3 本（詳細・本文・版履歴）へ届くこと**を固定する。
//
// **画面テストでは原理的に検出できない。** `renderUnitRoute` の検査用 QueryClient は
// `staleTime: 0` / `gcTime: 0` であり（`foundation/testing/renderUnitRoute.tsx`）、無効化が
// 届かなくても再マウントのたびに必ず再取得される。ここでは**本番と同じ既定**
// （`createAppQueryClient` = `staleTime: 30_000`）の QueryClient を直接使い、
// **キャッシュの状態そのもの**（`isInvalidated`）を見る。
//
// 載せ替え前の階層キー（`['bff','documents']` と `['bff','documents',id,…]`）では一覧の無効化が
// 詳細群へ前方一致で届いていた。生成キーは URL 1 要素なので届かない——その差を埋める明示列挙の
// 実装（`documentInvalidationKeys`）と、それが実際に配線されていることの両方を見る。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { documentInvalidationKeys, documentsKey, useDocumentActions } from './useDocumentAdmin';

const DOC_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';

/** SC-03 が使う 3 本のキー（`useDocumentQueries` と同じ生成キー）。 */
const sc03Keys = [
  getBffDocumentDetailQueryKey(DOC_ID),
  getBffDocumentContentQueryKey(DOC_ID),
  getBffDocumentVersionsQueryKey(DOC_ID),
];

/** SC-05 の一覧と SC-03 の 3 本に、取得済みのキャッシュを載せる。 */
function seed(queryClient: QueryClient) {
  queryClient.setQueryData(documentsKey, [{ id: DOC_ID }]);
  queryClient.setQueryData(getBffDocumentDetailQueryKey(DOC_ID), { id: DOC_ID, status: 'draft' });
  queryClient.setQueryData(getBffDocumentContentQueryKey(DOC_ID), { id: DOC_ID, markdown: '#' });
  queryClient.setQueryData(getBffDocumentVersionsQueryKey(DOC_ID), []);
}

const invalidated = (queryClient: QueryClient, keys: readonly (readonly unknown[])[]) =>
  keys.map((key) => queryClient.getQueryState(key)?.isInvalidated);

beforeEach(() => {
  mocks.apiRequest.mockReset();
});

describe('SC-05 invalidation reaches SC-03 (IADR-0135 決定 3)', () => {
  // **非対称の事実そのものを固定する。** これが落ちるようになったら（＝一覧キーが詳細群に
  // 当たるようになったら）明示列挙は不要になっている。
  it('does not reach the detail queries through the list key alone', async () => {
    const queryClient = createAppQueryClient();
    seed(queryClient);

    await queryClient.invalidateQueries({ queryKey: getBffDocumentListQueryKey() });

    expect(queryClient.getQueryState(documentsKey)?.isInvalidated).toBe(true);
    expect(invalidated(queryClient, sc03Keys)).toEqual([false, false, false]);
  });

  it('enumerates the list and the three detail queries of the changed document', async () => {
    const queryClient = createAppQueryClient();
    seed(queryClient);

    for (const queryKey of documentInvalidationKeys(DOC_ID)) {
      await queryClient.invalidateQueries({ queryKey });
    }

    expect(invalidated(queryClient, [documentsKey, ...sc03Keys])).toEqual([true, true, true, true]);
  });

  // 列挙が**実際に配線されている**ことまで見る（キーの一覧だけを直しても意味がないため）。
  it.each([
    ['publish', (a: ReturnType<typeof useDocumentActions>) => a.publish],
    ['archive', (a: ReturnType<typeof useDocumentActions>) => a.archive],
    ['update', (a: ReturnType<typeof useDocumentActions>) => a.update],
    ['delete', (a: ReturnType<typeof useDocumentActions>) => a.remove],
  ])('invalidates the SC-03 queries after a successful %s', async (_name, pick) => {
    mocks.apiRequest.mockResolvedValue(noContent());
    const queryClient = createAppQueryClient();
    seed(queryClient);
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(() => useDocumentActions(), { wrapper });

    pick(result.current).mutate({ id: DOC_ID, data: { title: 't' } } as never);

    await waitFor(() =>
      expect(invalidated(queryClient, [documentsKey, ...sc03Keys])).toEqual([
        true,
        true,
        true,
        true,
      ]),
    );
  });

  // 作成には対象 id が無く、詳細群のキャッシュも存在しない。一覧だけを無効化する。
  it('invalidates only the list after a successful create', async () => {
    mocks.apiRequest.mockResolvedValue(noContent());
    const queryClient = createAppQueryClient();
    seed(queryClient);
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(() => useDocumentActions(), { wrapper });

    result.current.create.mutate({ data: { title: 't' } } as never);

    await waitFor(() => expect(queryClient.getQueryState(documentsKey)?.isInvalidated).toBe(true));
    expect(invalidated(queryClient, sc03Keys)).toEqual([false, false, false]);
  });
});
