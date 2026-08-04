import { describe, expect, it } from 'vitest';
import { createAppQueryClient, DEFAULT_QUERY_OPTIONS, queryClient } from './queryClient';

// ADR-0031, IADR-0121: サーバー状態を TanStack Query に一元化する土台。既定オプションは
// 「タブ復帰で勝手に再取得しない」「4xx を無駄に叩き直さない」を意図して置いており、
// 気付かず変わると BFF と後段サービスへの負荷特性が変わるため回帰として固定する。
describe('foundation/api/queryClient', () => {
  it('applies the shared defaults to newly created clients', () => {
    const client = createAppQueryClient();
    const defaults = client.getDefaultOptions().queries;

    expect(defaults?.retry).toBe(1);
    expect(defaults?.refetchOnWindowFocus).toBe(false);
    expect(defaults?.staleTime).toBe(30_000);
  });

  it('exposes the same defaults as the documented constant', () => {
    expect(DEFAULT_QUERY_OPTIONS).toEqual({
      retry: 1,
      refetchOnWindowFocus: false,
      staleTime: 30_000,
    });
  });

  it('creates independent caches per client so tests do not leak state', () => {
    const a = createAppQueryClient();
    const b = createAppQueryClient();

    a.setQueryData(['probe'], 'a');

    expect(a.getQueryData(['probe'])).toBe('a');
    expect(b.getQueryData(['probe'])).toBeUndefined();
  });

  it('ships a shared application instance', () => {
    expect(queryClient.getDefaultOptions().queries?.staleTime).toBe(30_000);
  });
});
