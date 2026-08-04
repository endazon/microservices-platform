import { apiRequest } from './apiClient';

// ADR-0031 / IADR-0121 決定 3: orval が生成するクライアントの唯一の HTTP 出口。
//
// orval 既定の生成コードは素の `fetch('/bff/...')` を呼ぶ。それでは
//   - 実行時 config（`bffBaseUrl`。環境非依存ビルドの要）が効かない
//   - 401 の再ログイン導線（`setUnauthorizedHandler`）が効かない
//   - 認証ヘッダの付与が生成コードごとに散らばる
// ため、mutator を挟んで `foundation/api/apiClient` の `apiRequest` へ集約する。
// 認証方式が BFF セッションへ変わっても（ADR-0032・移行第 3 段 / #439）、直すのは apiClient の
// 1 箇所で済む。
//
// 生成された URL は OpenAPI のパスそのまま（例 `/bff/search`）であり、`apiRequest` は
// `bffBaseUrl`（既定 `/bff`）を前置する。二重に `/bff` を付けないよう、ここで接頭辞を外す。
const BFF_PREFIX = '/bff';

/** 生成コードが期待する応答形（`{ data, status, headers }`）。 */
type OrvalResponse = { data: unknown; status: number; headers: Headers };

export const bffFetch = async <T>(url: string, options?: RequestInit): Promise<T> => {
  const path = url.startsWith(BFF_PREFIX) ? url.slice(BFF_PREFIX.length) : url;
  const res = await apiRequest(path, options);

  // 本文を持たない応答（204 / 205 / 304）は data を空にする（orval の生成コードと同じ扱い）。
  const body = [204, 205, 304].includes(res.status) ? null : await res.text();
  const data: unknown = body ? JSON.parse(body) : {};

  return { data, status: res.status, headers: res.headers } as OrvalResponse as T;
};
