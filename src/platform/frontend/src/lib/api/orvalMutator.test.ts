import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { bffFetch } from './orvalMutator';
import { CSRF_HEADER_NAME, setUnauthorizedHandler } from './apiClient';
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

  // ADR-0032 / IADR-0273: 生成クライアント経由でも SPA はトークンを扱わない。
  // 資格情報はセッション Cookie（ブラウザが自動付与）で、CSRF ヘッダだけを付ける。
  it('sends the CSRF header and never an Authorization header', async () => {
    fetchMock.mockResolvedValue(jsonResponse({}));

    await bffFetch('/bff/dashboard/summary');

    const init = fetchMock.mock.calls[0][1] as RequestInit;
    const headers = new Headers(init.headers);
    expect(headers.get(CSRF_HEADER_NAME)).toBe('1');
    expect(headers.get('Authorization')).toBeNull();
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

    const res = await bffFetch<{ data: unknown; status: number }>('/bff/feedback', {
      method: 'POST',
    });

    expect(res.status).toBe(204);
    expect(res.data).toEqual({});
  });

  // FR-12, UC-06, SC-07 / #651: **非 JSON（画像）の応答を JSON として解析しない。**
  //
  // `GET /bff/conversion/jobs/{id}/figures/{figureId}/image` は openapi.yaml 唯一の非 JSON 応答
  // （`image/*`・`format: binary`）であり、生成フックの宣言型は `data: Blob` である。
  // ところが本 mutator は Content-Type を見ずに `text()` → `JSON.parse` していたため、
  // **生成された時点で実行不能だった**（#543 は端点と生成物を載せたが、応答を読む層を通していない）。
  it('returns a Blob for non-JSON responses instead of parsing them as JSON（#651)', async () => {
    // PNG シグネチャ。`JSON.parse` すれば必ず SyntaxError になるバイト列である。
    const png = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
    fetchMock.mockResolvedValue(
      new Response(png, { status: 200, headers: { 'Content-Type': 'image/png' } }),
    );

    const res = await bffFetch<{ data: Blob; status: number }>(
      '/bff/conversion/jobs/j1/figures/f1/image',
    );

    expect(res.status).toBe(200);
    // **`toBeInstanceOf(Blob)` で判定しない。** jsdom 環境では `Response.blob()` が返すのは
    // undici 側の `Blob` であり、グローバルの `Blob`（jsdom 実装）とは**別のコンストラクタ**である
    // （実測: `blob instanceof Blob === false` / `Blob === blob.constructor` も `false`）。
    // 実体で判定する——型名ではなく「バイト列が素通りしたか」がここで固定したい事実である。
    expect(res.data.type).toBe('image/png');
    expect(new Uint8Array(await res.data.arrayBuffer())).toEqual(png);
  });

  // **既存経路を変えていないことの側**（分岐を足したときに壊しやすいのはこちら）。
  // 画面テストのスタブ（`foundation/testing/bffResponse.ts`）は `new Headers()`＝**Content-Type 無し**を
  // 返す。ここが blob 側へ落ちると 100 超の画面テストが一斉に壊れる。
  //
  // **`new Response(文字列)` は Content-Type を `text/plain;charset=UTF-8` に自動設定する**ので
  // （実測）、スタブと同じ「ヘッダ無し」を作るには delete が要る。ここを省くと、
  // **意図した経路とは別の経路を試験してしまう**。
  it('still parses JSON when the response carries no Content-Type（画面テストのスタブと同じ形）', async () => {
    const res200 = new Response(JSON.stringify({ answer: 'ok' }), { status: 200 });
    res200.headers.delete('Content-Type');
    expect(res200.headers.get('Content-Type')).toBeNull(); // 前提そのものを固定する
    fetchMock.mockResolvedValue(res200);

    const res = await bffFetch<{ data: unknown; status: number }>('/bff/analysis/ask');

    expect(res.data).toEqual({ answer: 'ok' });
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

    await expect(bffFetch('/bff/search', { method: 'POST' })).rejects.toMatchObject({
      kind: 'network',
    });
  });
});
