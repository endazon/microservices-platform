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

/**
 * 本文を JSON として読むべき応答か。
 *
 * FR-12, UC-06, SC-07（#651）: `GET /bff/conversion/jobs/{id}/figures/{figureId}/image` は
 * **`docs/api/openapi.yaml` 唯一の非 JSON 応答**（`image/*` / `format: binary`）であり、
 * 生成フックの宣言型も `data: Blob` である。**Content-Type を見ずに `JSON.parse` すると
 * 必ず `SyntaxError` になる**（実測: `Unexpected token '', "PNG…" is not valid JSON`）。
 * すなわち生成フック `useBffConversionJobFigureImage` は**生成された時点で実行不能だった**
 * ——#543 が端点と生成物を載せたとき、**その応答を読む層を誰も通していない**（画面が無かった）。
 *
 * 直す場所がここなのは、[[IADR-0121]] 決定 3 が「SPA から出る HTTP はこの関数 1 箇所に
 * 収束させる」と定めた**その 1 箇所**だからである。呼び出し側で `apiRequest` を直に叩いて
 * 迂回すると、**壊れた生成フックが「使える顔をして」残る**（次の画像口で同じ穴を踏む）。
 *
 * 判定は「**JSON でなければ blob**」ではなく「**Content-Type が明示的に非 JSON のときだけ
 * blob**」とする。ヘッダを持たない応答を従来どおり JSON 経路へ通すためである——画面テストの
 * スタブ（`foundation/testing/bffResponse.ts`）は `new Headers()`＝Content-Type 無しを返しており、
 * ここが blob 側へ落ちると 100 超の画面テストが一斉に壊れる。
 *
 * **非 2xx はここへ到達しない**（`apiRequest` が `ApiError` を投げる）ため、
 * `application/problem+json` の扱いは本判定の考慮外である。
 */
function isJsonBody(res: Response): boolean {
  const contentType = res.headers.get('Content-Type');
  return !contentType || /\bjson\b/i.test(contentType);
}

export const bffFetch = async <T>(url: string, options?: RequestInit): Promise<T> => {
  const path = url.startsWith(BFF_PREFIX) ? url.slice(BFF_PREFIX.length) : url;
  const res = await apiRequest(path, options);

  // 本文を持たない応答（204 / 205 / 304）は data を空にする（orval の生成コードと同じ扱い）。
  const bodyless = [204, 205, 304].includes(res.status);
  let data: unknown;
  if (bodyless) {
    data = {};
  } else if (isJsonBody(res)) {
    const body = await res.text();
    data = body ? JSON.parse(body) : {};
  } else {
    // 画像などのバイト列。呼び出し側は `URL.createObjectURL` で包んで表示する。
    data = await res.blob();
  }

  return { data, status: res.status, headers: res.headers } as OrvalResponse as T;
};
