import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { bffFetch } from './orvalMutator';
import { setTokenProvider, setUnauthorizedHandler } from './apiClient';
import { ApiError } from './ApiError';
import { resetAppConfigCache } from '@foundation/config/runtimeConfig';

// ADR-0031 / IADR-0121 決定 3: orval 生成クライアントの HTTP 出口を foundation/api に集約する要。
// ここが素の fetch へ戻ると、実行時 config（環境非依存ビルド）も 401 再ログイン導線も静かに失われる
// ——画面は動いて見えるので気付けない。だから配線そのものを回帰テストで固定する。
describe('foundation/api/orvalMutator（生成クライアントの唯一の出口）', () => {
  const fetchMock = vi.fn();

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    fetchMock.mockReset();
    setTokenProvider(() => null);
    setUnauthorizedHandler(() => {});
    resetAppConfigCache();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    resetAppConfigCache();
  });

  function jsonResponse(body: unknown, status = 200): Response {
    return new Response(JSON.stringify(body), {
      status,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  it('routes the generated /bff/... URL through the runtime-config base URL exactly once', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ hits: [] }));

    await bffFetch('/bff/search', { method: 'POST' });

    // 生成 URL は `/bff/search`、bffBaseUrl の既定は `/bff`。二重に付かないことが要点。
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0]).toBe('/bff/search');
  });

  it('attaches the bearer token supplied by the auth module', async () => {
    setTokenProvider(() => 'token-abc');
    fetchMock.mockResolvedValue(jsonResponse({}));

    await bffFetch('/bff/dashboard/summary');

    const init = fetchMock.mock.calls[0][1] as RequestInit;
    expect(new Headers(init.headers).get('Authorization')).toBe('Bearer token-abc');
  });

  it('returns the { data, status, headers } shape the generated code expects', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ answer: 'ok' }, 200));

    const res = await bffFetch<{ data: { answer: string }; status: number; headers: Headers }>(
      '/bff/analysis/ask',
      { method: 'POST' },
    );

    expect(res.status).toBe(200);
    expect(res.data).toEqual({ answer: 'ok' });
    expect(res.headers.get('Content-Type')).toBe('application/json');
  });

  it('treats bodyless successes (204) as empty data instead of failing to parse', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 204 }));

    const res = await bffFetch<{ data: unknown; status: number }>('/bff/feedback', { method: 'POST' });

    expect(res.status).toBe(204);
    expect(res.data).toEqual({});
  });

  it('triggers the shared re-login handler on 401 and throws ApiError', async () => {
    const onUnauthorized = vi.fn();
    setUnauthorizedHandler(onUnauthorized);
    fetchMock.mockResolvedValue(new Response('', { status: 401 }));

    await expect(bffFetch('/bff/search', { method: 'POST' })).rejects.toBeInstanceOf(ApiError);
    expect(onUnauthorized).toHaveBeenCalledTimes(1);
  });

  it('maps a transport failure to a network ApiError（生成コードへ生の例外を漏らさない）', async () => {
    fetchMock.mockRejectedValue(new TypeError('failed to fetch'));

    await expect(bffFetch('/bff/search', { method: 'POST' })).rejects.toMatchObject({ kind: 'network' });
  });
});
