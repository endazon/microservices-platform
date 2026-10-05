import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-22, NFR-18, 計画 ADR-0126, IADR-0501 (#458 段 S2 の独立監査): 書き込みの変更（mutation）が、送った値（変数）を
// TanStack Query の変更キャッシュに残さないこと。既定の gcTime（5 分）のままだと、フォームを閉じた後も値がメモリに残る。
//
// 🔴 検査用の QueryClient は**本番と同じ既定**（mutations の gcTime を上書きしない）で作る —— 上書きすると
// フックが gcTime を持たない退行を見逃す。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { useDataSourceCredentialUpdate } from './useDataSourceCredentialGroup';
import { useSecretItemUpdate } from './useSecretItems';

// 🔴 テスト用の明白なダミー値。本物の秘密は書かない。
const PLACEHOLDER = 'placeholder-value-for-sc22-mutation-cache-test';
const MEMBER_ID = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';

function setup<T>(useHook: () => T) {
  const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return { client, ...renderHook(useHook, { wrapper }) };
}

beforeEach(() => {
  mocks.apiRequest.mockReset();
  mocks.apiRequest.mockResolvedValue(jsonResponse({ version: 1, supplySource: 'screen' }));
});

describe('SC-22 の書き込みは送った値を変更キャッシュに残さない', () => {
  it('群（データソースの資格情報）の書き込み', async () => {
    const { client, result, unmount } = setup(() => useDataSourceCredentialUpdate());

    result.current.mutate({
      group: 'datasource-credentials',
      memberId: MEMBER_ID,
      data: { property: 'apiToken', value: PLACEHOLDER },
    });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.getMutationCache().getAll()[0].options.gcTime).toBe(0);

    unmount();
    await waitFor(() => expect(client.getMutationCache().getAll()).toHaveLength(0));
  });

  it('静的な項目の書き込み', async () => {
    const { client, result, unmount } = setup(() => useSecretItemUpdate());

    result.current.mutate({
      item: 'wikijs-sync',
      data: { property: 'apiKey', value: PLACEHOLDER },
    });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(client.getMutationCache().getAll()[0].options.gcTime).toBe(0);

    unmount();
    await waitFor(() => expect(client.getMutationCache().getAll()).toHaveLength(0));
  });
});
