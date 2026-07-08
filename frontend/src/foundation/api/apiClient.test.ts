import { describe, it, expect, vi, afterEach } from 'vitest';
import { apiFetch, setTokenProvider } from './apiClient';
import { ApiError } from './ApiError';

// Issue #126: apiFetch は Bearer を付与し、HTTP ステータスを ApiError へ写像する（IADR-0009: 404→notFound）。
describe('apiFetch', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    setTokenProvider(() => null);
  });

  it('attaches the bearer token and returns parsed JSON', async () => {
    setTokenProvider(() => 'test-token');
    const fetchMock = vi.fn<typeof fetch>(async () =>
      new Response(JSON.stringify({ ok: true }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    const data = await apiFetch<{ ok: boolean }>('/dashboard/summary');

    expect(data).toEqual({ ok: true });
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/bff/dashboard/summary');
    const headers = init?.headers as Headers;
    expect(headers).toBeInstanceOf(Headers);
    expect(headers.get('Authorization')).toBe('Bearer test-token');
  });

  it('maps 404 to notFound (existence hidden, IADR-0009)', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 404 })));

    await expect(apiFetch('/x')).rejects.toMatchObject({
      constructor: ApiError,
      kind: 'notFound',
    });
  });

  it('maps 401 to unauthorized', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 401 })));
    await expect(apiFetch('/x')).rejects.toMatchObject({ kind: 'unauthorized' });
  });

  it('maps fetch rejection to a network error', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('boom'); }));
    await expect(apiFetch('/x')).rejects.toMatchObject({ kind: 'network' });
  });

  it('omits Authorization when no token is available', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await apiFetch('/x');

    const headers = fetchMock.mock.calls[0][1]?.headers as Headers;
    expect(headers.has('Authorization')).toBe(false);
  });
});
