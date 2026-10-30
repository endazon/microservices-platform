import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import { jsonResponse } from '@foundation/testing/bffResponse';
import { getBffGraphNeighborsQueryKey } from '@foundation/api/generated/graph/graph';

// FR-17, SC-18, SC-19 (#2343。#2107 の受け入れ基準の案 3): **画面から作った辺が SC-18 のグラフに出る。**
// 後段が作った辺を近傍に返すことは GraphService の `UserAuthoredEdgeTests` が固定する。本ファイルは画面側の片割れ ——
// 作成に成功したら、両端の近傍（SC-18 が読む照会。深さ・型の絞りの違いを含む）を古いものとして引き直させること。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { useCreateLink } from './useLinkCreate';

const SOURCE = '00000000-0000-0000-0000-000000000001';
const TARGET = 'aaaaaaaa-0000-0000-0000-000000000001';
const UNRELATED = 'bbbbbbbb-0000-0000-0000-000000000001';
const TYPE = 'eeeeeeee-0000-0000-0000-000000000001';

beforeEach(() => {
  mocks.apiRequest.mockReset();
  mocks.apiRequest.mockResolvedValue(
    jsonResponse(
      {
        id: 'f',
        sourceDocumentId: SOURCE,
        targetDocumentId: TARGET,
        edgeTypeId: TYPE,
        provenance: 'user',
      },
      201,
    ),
  );
});

describe('useCreateLink', () => {
  it('作成に成功すると両端の近傍を古いものにし、無関係の近傍は触らない', async () => {
    const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
    client.setQueryData(getBffGraphNeighborsQueryKey(SOURCE), { nodes: [], edges: [] });
    client.setQueryData(getBffGraphNeighborsQueryKey(TARGET, { hops: 2 }), {
      nodes: [],
      edges: [],
    });
    client.setQueryData(getBffGraphNeighborsQueryKey(UNRELATED), { nodes: [], edges: [] });
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(() => useCreateLink(), { wrapper });

    result.current.mutate({
      data: { sourceDocumentId: SOURCE, targetDocumentId: TARGET, edgeTypeId: TYPE },
    });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const stale = (key: readonly unknown[]) => client.getQueryState(key)?.isInvalidated;
    expect(stale(getBffGraphNeighborsQueryKey(SOURCE))).toBe(true);
    expect(stale(getBffGraphNeighborsQueryKey(TARGET, { hops: 2 }))).toBe(true);
    expect(stale(getBffGraphNeighborsQueryKey(UNRELATED))).toBe(false);
  });
});
