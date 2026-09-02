// FR-20, [[IADR-0338]] 決定 4・6, [[IADR-0352]]: Obsidian 本体の `requestUrl` を HttpTransport に写す。
// `fetch` ではなく `requestUrl` を使うのは、Obsidian（Electron / モバイル）で CORS と Cookie の扱いを
// 本体に任せるため。`throw: false` で非 2xx を例外にせず、状態コードの解釈は SyncClient に一元化する。
import { requestUrl } from 'obsidian';
import type { HttpTransport } from '../protocol/transport.ts';

export const obsidianTransport: HttpTransport = async (request) => {
  const response = await requestUrl({
    url: request.url,
    method: request.method,
    headers: request.headers,
    ...(request.body !== undefined ? { body: request.body } : {}),
    throw: false,
  });
  return { status: response.status, text: response.text };
};
