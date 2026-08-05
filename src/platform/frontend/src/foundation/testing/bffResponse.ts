// SC-01〜SC-11, IADR-0135 決定 4: 画面テストが `apiRequest` を差し替えるときの応答の作り方。
//
// orval 生成コードは mutator（`bffFetch`）→ **`apiRequest`** を通る。したがってモックは
// `apiFetch` ではなく `apiRequest` に当てる必要があり、返す値は `Response` 相当
// （`bffFetch` が読むのは `status` / `text()` / `headers` の 3 つだけ）になる。
//
// 失敗は**このヘルパを使わない**——`ApiError` を throw する形は載せ替えの前後で変わらないため、
// 各テストがこれまでどおり `mockRejectedValue(new ApiError(...))` を書く。

/** 生成コードが読む最小限の `Response` 相当（`bffFetch` は `status` / `text()` / `headers` だけを見る）。 */
export interface BffResponseStub {
  status: number;
  text: () => Promise<string>;
  headers: Headers;
}

/** JSON 本文を持つ成功応答（既定 200）。 */
export function jsonResponse(body: unknown, status = 200): BffResponseStub {
  return { status, text: () => Promise.resolve(JSON.stringify(body)), headers: new Headers() };
}

/** 本文を持たない応答（204）。`bffFetch` は本文を読まずに `{}` を返す。 */
export function noContent(): BffResponseStub {
  return { status: 204, text: () => Promise.resolve(''), headers: new Headers() };
}
