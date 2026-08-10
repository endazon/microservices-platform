// SC-01〜SC-11, IADR-0135 決定 4: 画面テストが `apiRequest` を差し替えるときの応答の作り方。
//
// orval 生成コードは mutator（`bffFetch`）→ **`apiRequest`** を通る。したがってモックは
// `apiFetch` ではなく `apiRequest` に当てる必要があり、返す値は `Response` 相当
// （`bffFetch` が読むのは `status` / `text()` / `headers` の 3 つだけ）になる。
//
// 失敗は**このヘルパを使わない**——`ApiError` を throw する形は載せ替えの前後で変わらないため、
// 各テストがこれまでどおり `mockRejectedValue(new ApiError(...))` を書く。

/**
 * 生成コードが読む最小限の `Response` 相当。
 *
 * `bffFetch` が見るのは `status` / `headers` / `text()`、**および非 JSON のときだけ `blob()`** である
 * （`blob` は #651 で足した。それまで非 JSON の応答は存在しなかった）。
 */
export interface BffResponseStub {
  status: number;
  text: () => Promise<string>;
  headers: Headers;
  blob?: () => Promise<Blob>;
}

/**
 * JSON 本文を持つ成功応答（既定 200）。
 *
 * **Content-Type を付けない。** `bffFetch` は「Content-Type が明示的に非 JSON のときだけ」
 * blob 経路へ落とすので、ヘッダ無しは従来どおり JSON として解析される（#651）。
 */
export function jsonResponse(body: unknown, status = 200): BffResponseStub {
  return { status, text: () => Promise.resolve(JSON.stringify(body)), headers: new Headers() };
}

/** 本文を持たない応答（204）。`bffFetch` は本文を読まずに `{}` を返す。 */
export function noContent(): BffResponseStub {
  return { status: 204, text: () => Promise.resolve(''), headers: new Headers() };
}

/**
 * バイト列を返す応答（#651）。`docs/api/openapi.yaml` で非 JSON なのは
 * `GET /bff/conversion/jobs/{id}/figures/{figureId}/image` だけである。
 *
 * **Content-Type を必ず持つ**——これが無いと `bffFetch` は JSON 経路へ落ち、
 * 実物と違う経路を試験してしまう。
 */
export function blobResponse(bytes: Uint8Array, contentType = 'image/png'): BffResponseStub {
  const blob = new Blob([bytes], { type: contentType });
  return {
    status: 200,
    text: () => Promise.reject(new Error('binary body must not be read as text')),
    headers: new Headers({ 'Content-Type': contentType }),
    blob: () => Promise.resolve(blob),
  };
}
