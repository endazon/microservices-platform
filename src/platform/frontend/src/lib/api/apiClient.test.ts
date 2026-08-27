import { describe, it, expect, vi, afterEach } from 'vitest';
import { apiFetch, CSRF_HEADER_NAME, setUnauthorizedHandler } from './apiClient';
import { ApiError } from './ApiError';

// Issue #126 / ADR-0032, IADR-0273, #439: apiFetch はセッション Cookie（ブラウザ自動付与）で
// BFF を呼び、HTTP ステータスを ApiError へ写像する（IADR-0009: 404→notFound）。
describe('apiFetch', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    setUnauthorizedHandler(() => {});
  });

  // ★ 陽性対照: リクエストは出る・CSRF ヘッダが付く・JSON が返る。
  // 下の「Authorization を付けない」だけだと「何も送らない実装」が緑になる。
  it('sends the CSRF header and returns parsed JSON', async () => {
    const fetchMock = vi.fn<typeof fetch>(
      async () =>
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
    expect(headers.get(CSRF_HEADER_NAME)).toBe('1');
  });

  // 🔴 否定形（ADR-0032）: **SPA はトークンを扱わない＝ Authorization ヘッダを一切付けない。**
  // 陽性対照は上（CSRF ヘッダは付く）。
  it('never attaches an Authorization header', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await apiFetch('/dashboard/summary');

    const headers = fetchMock.mock.calls[0][1]?.headers as Headers;
    expect(headers.has('Authorization')).toBe(false);
  });

  // /auth/me（認証状態の確認）は 401 が正常な答え。再ログイン導線を起動しない。
  it("suppresses the unauthorized handler when on401 is 'silent'", async () => {
    const onUnauthorized = vi.fn();
    setUnauthorizedHandler(onUnauthorized);
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('', { status: 401 })),
    );

    await expect(apiFetch('/auth/me', { on401: 'silent' })).rejects.toMatchObject({
      kind: 'unauthorized',
    });
    expect(onUnauthorized).not.toHaveBeenCalled();
  });

  it('maps 404 to notFound (existence hidden, IADR-0009)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('', { status: 404 })),
    );

    await expect(apiFetch('/x')).rejects.toMatchObject({
      constructor: ApiError,
      kind: 'notFound',
    });
  });

  it('maps 401 to unauthorized', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('', { status: 401 })),
    );
    await expect(apiFetch('/x')).rejects.toMatchObject({ kind: 'unauthorized' });
  });

  it('invokes the unauthorized handler (re-login) on 401', async () => {
    // IADR-0033: 401 は再ログイン導線を起動する。
    const onUnauthorized = vi.fn();
    setUnauthorizedHandler(onUnauthorized);
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('', { status: 401 })),
    );

    await expect(apiFetch('/x')).rejects.toBeInstanceOf(ApiError);
    expect(onUnauthorized).toHaveBeenCalledTimes(1);
  });

  it('does not invoke the unauthorized handler on 404', async () => {
    const onUnauthorized = vi.fn();
    setUnauthorizedHandler(onUnauthorized);
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('', { status: 404 })),
    );

    await expect(apiFetch('/x')).rejects.toMatchObject({ kind: 'notFound' });
    expect(onUnauthorized).not.toHaveBeenCalled();
  });

  it('maps 400 to validation and extracts ValidationProblem detail messages (SC-09)', async () => {
    // FR-09, SC-09: AuthorizationService の検証エラー本文 { errors: { errors: [...] } } を details へ抽出する。
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(
            JSON.stringify({ errors: { errors: ['action は read/analyze/manage のいずれか'] } }),
            {
              status: 400,
              headers: { 'Content-Type': 'application/problem+json' },
            },
          ),
      ),
    );

    await expect(
      apiFetch('/admin/authz/policies', { method: 'POST', json: {} }),
    ).rejects.toMatchObject({
      kind: 'validation',
      details: ['action は read/analyze/manage のいずれか'],
    });
  });

  it('maps 409 to conflict and extracts the problem detail (SC-09)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(
            JSON.stringify({ title: '属性辞書が参照中です', detail: 'policy A が参照中' }),
            {
              status: 409,
              headers: { 'Content-Type': 'application/problem+json' },
            },
          ),
      ),
    );

    await expect(apiFetch('/admin/authz/attributes/x', { method: 'DELETE' })).rejects.toMatchObject(
      {
        kind: 'conflict',
        details: ['policy A が参照中'],
      },
    );
  });

  it('maps fetch rejection to a network error', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new TypeError('boom');
      }),
    );
    await expect(apiFetch('/x')).rejects.toMatchObject({ kind: 'network' });
  });
});
