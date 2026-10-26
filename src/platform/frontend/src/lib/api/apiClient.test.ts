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

  // FR-02, FR-09, SC-09, IADR-0637 決定 6 (#2150 段 1b): タグの改名が並行する確定に上限まで負けたときの 409 は
  // `{ error: 'version_conflict', message }` である。`message` が `details` に入り、SC-09 は既存の経路（`toMessages`）で出す。
  it('extracts the message of a version_conflict 409 (SC-09 tag rename)', async () => {
    const message = '他の変更と重なったため、改名しませんでした。もう一度改名してください。';
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ error: 'version_conflict', message }), {
            status: 409,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    );

    await expect(
      apiFetch('/bff/tags/x', { method: 'PUT', json: { name: 'y' } }),
    ).rejects.toMatchObject({
      kind: 'conflict',
      details: [message],
      body: { error: 'version_conflict', message },
    });
  });

  // ── #2122 / #2280 のクライアント側: `{ error }` 形の 4xx の理由 ──────────────────────────────
  const stubJson = (status: number, body: unknown) =>
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify(body), {
            status,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    );

  // FR-01, SC-06 (#2122): `{ error: "<人が読む文>" }` の 400 は、その文を details に入れる（画面に理由が出る）。
  it('extracts a human-readable `error` of a 400 when no RFC 7807 field is present', async () => {
    const reason = 'connectionUri に資格情報を含めないでください（接続先だけを書きます）。';
    stubJson(400, { error: reason });

    await expect(apiFetch('/datasources/x', { method: 'PATCH', json: {} })).rejects.toMatchObject({
      kind: 'validation',
      details: [reason],
      body: { error: reason },
    });
  });

  // RFC 7807 の `errors` / `detail` があれば、それが勝つ（`error` は読まない）。
  it.each([
    [
      { errors: { departmentCode: ['部門グループのコードではありません。'] }, error: '別の理由' },
      '部門グループのコードではありません。',
    ],
    [{ detail: '写像先の利用者がいません。', error: '別の理由' }, '写像先の利用者がいません。'],
  ])('prefers RFC 7807 fields over `error` (%j)', async (body, expected) => {
    stubJson(400, body);

    await expect(apiFetch('/datasources/x', { method: 'PATCH', json: {} })).rejects.toMatchObject({
      details: [expected],
    });
  });

  // 🔴 5xx の本文は読まない（後段の内部を画面へ出さない）。`error` があっても details は空＝既定の文言。
  it('never surfaces the `error` of a 5xx body', async () => {
    stubJson(500, { error: 'NpgsqlException: connection refused at 10.0.0.5' });

    const err = await apiFetch('/datasources/x', { method: 'PATCH', json: {} }).catch(
      (e: unknown) => e,
    );
    expect(err).toMatchObject({ kind: 'server', details: [] });
  });

  // 文字列でない・空・識別子の形（機械の符号）の `error` は文として出さない（本文の body では読める）。
  it.each([
    [{ error: 42 }],
    [{ error: { code: 'x' } }],
    [{ error: '   ' }],
    [{ error: 'invalid_transition' }],
    [{ error: 'job_busy' }],
  ])(
    'keeps the message generic for a non-string, blank or machine-code `error` (%j)',
    async (body) => {
      stubJson(409, body);

      await expect(apiFetch('/x', { method: 'POST', json: {} })).rejects.toMatchObject({
        kind: 'conflict',
        details: [],
        body,
      });
    },
  );

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
