import { describe, it, expect, vi, afterEach } from 'vitest';
import { apiFetch, setTokenProvider, setUnauthorizedHandler } from './apiClient';
import { ApiError } from './ApiError';

// Issue #126: apiFetch は Bearer を付与し、HTTP ステータスを ApiError へ写像する（IADR-0009: 404→notFound）。
describe('apiFetch', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    setTokenProvider(() => null);
    setUnauthorizedHandler(() => {});
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

  it('invokes the unauthorized handler (re-login) on 401', async () => {
    // IADR-0033: 401 は再ログイン導線を起動する。
    const onUnauthorized = vi.fn();
    setUnauthorizedHandler(onUnauthorized);
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 401 })));

    await expect(apiFetch('/x')).rejects.toBeInstanceOf(ApiError);
    expect(onUnauthorized).toHaveBeenCalledTimes(1);
  });

  it('does not invoke the unauthorized handler on 404', async () => {
    const onUnauthorized = vi.fn();
    setUnauthorizedHandler(onUnauthorized);
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 404 })));

    await expect(apiFetch('/x')).rejects.toMatchObject({ kind: 'notFound' });
    expect(onUnauthorized).not.toHaveBeenCalled();
  });

  it('maps 400 to validation and extracts ValidationProblem detail messages (SC-09)', async () => {
    // FR-09, SC-09: AuthorizationService の検証エラー本文 { errors: { errors: [...] } } を details へ抽出する。
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(JSON.stringify({ errors: { errors: ['action は read/analyze/manage のいずれか'] } }), {
          status: 400,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
      ),
    );

    await expect(apiFetch('/admin/authz/policies', { method: 'POST', json: {} })).rejects.toMatchObject({
      kind: 'validation',
      details: ['action は read/analyze/manage のいずれか'],
    });
  });

  it('maps 409 to conflict and extracts the problem detail (SC-09)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(JSON.stringify({ title: '属性辞書が参照中です', detail: 'policy A が参照中' }), {
          status: 409,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
      ),
    );

    await expect(apiFetch('/admin/authz/attributes/x', { method: 'DELETE' })).rejects.toMatchObject({
      kind: 'conflict',
      details: ['policy A が参照中'],
    });
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
