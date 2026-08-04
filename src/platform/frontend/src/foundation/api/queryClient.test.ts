import { describe, expect, it } from 'vitest';
import {
  createAppQueryClient,
  DEFAULT_QUERY_OPTIONS,
  MAX_QUERY_RETRIES,
  queryClient,
  shouldRetryQuery,
} from './queryClient';
import { ApiError } from './ApiError';

// ADR-0031, IADR-0121: サーバー状態を TanStack Query に一元化する土台。既定オプションは
// 「タブ復帰で勝手に再取得しない」「4xx を無駄に叩き直さない」を意図して置いており、
// 気付かず変わると BFF と後段サービスへの負荷特性が変わるため回帰として固定する。
describe('foundation/api/queryClient', () => {
  it('applies the shared defaults to newly created clients', () => {
    const client = createAppQueryClient();
    const defaults = client.getDefaultOptions().queries;

    expect(defaults?.retry).toBe(shouldRetryQuery);
    expect(defaults?.refetchOnWindowFocus).toBe(false);
    expect(defaults?.staleTime).toBe(30_000);
  });

  it('exposes the same defaults as the documented constant', () => {
    expect(DEFAULT_QUERY_OPTIONS).toEqual({
      retry: shouldRetryQuery,
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

// IADR-0009 / IADR-0121: 本システムでは 404 が「異常」ではなく通常の応答である（存在秘匿により
// 権限外の資源は 404 として返る）。数値の retry: 1 はエラー種別を区別せず全失敗を再試行するため、
// 4xx を除外する述語を置いている。ここが数値へ戻ると、日常的に起きる 404 が毎回 2 往復になり、
// エラー表示が遅れて BFF と後段サービスへ無駄な負荷がかかる——画面は動いて見えるので気付けない。
describe('shouldRetryQuery（4xx は再試行しない）', () => {
  const apiError = (status: number | null) =>
    new ApiError(status === null ? 'network' : 'unknown', 'test', status);

  it.each([400, 401, 403, 404, 409, 422])('does not retry a %s response', (status) => {
    expect(shouldRetryQuery(0, apiError(status))).toBe(false);
  });

  it.each([408, 429])('retries %s because waiting can resolve it', (status) => {
    expect(shouldRetryQuery(0, apiError(status))).toBe(true);
  });

  it('retries transport failures that carry no status', () => {
    expect(shouldRetryQuery(0, apiError(null))).toBe(true);
  });

  it.each([500, 502, 503])('retries a %s response', (status) => {
    expect(shouldRetryQuery(0, apiError(status))).toBe(true);
  });

  it('retries non-ApiError failures (unexpected throw) once', () => {
    expect(shouldRetryQuery(0, new Error('boom'))).toBe(true);
  });

  it('stops once the retry budget is spent', () => {
    // TanStack Query は failureCount を 0 起点で渡し、判定後に加算する。
    // したがって failureCount>=1 で false を返すことが「再試行は 1 回だけ」を意味する。
    expect(MAX_QUERY_RETRIES).toBe(1);
    expect(shouldRetryQuery(1, apiError(503))).toBe(false);
    expect(shouldRetryQuery(2, apiError(null))).toBe(false);
  });
});
