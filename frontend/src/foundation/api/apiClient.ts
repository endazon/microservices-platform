// Issue #126: BFF 境界の HTTP クライアント。BFF（/bff/*）＋ OpenAPI を契約とし、features は本 client
// 経由でのみバックエンドへアクセスする（疎結合。接続先は実行時 config）。
// - 認証: 現在のアクセストークン（Keycloak JWT）を Bearer として付与する。トークンの取得は
//   auth モジュールが setTokenProvider で注入し、api は auth 実装に直接依存しない。
// - エラー: HTTP ステータスを ApiError へ写像する（404 は存在秘匿と整合。IADR-0009）。
import { appConfig } from '@foundation/config/runtimeConfig';
import { ApiError } from './ApiError';

type TokenProvider = () => string | null | Promise<string | null>;

let tokenProvider: TokenProvider = () => null;

/** アクセストークンの供給元を注入する（AuthProvider が UserManager を渡す）。 */
export function setTokenProvider(provider: TokenProvider): void {
  tokenProvider = provider;
}

export interface ApiRequest extends Omit<RequestInit, 'body'> {
  /** JSON 本文。指定時は Content-Type: application/json を付与する。 */
  json?: unknown;
}

/** BFF へ JSON リクエストを送り、応答を型 T として返す。失敗時は ApiError を投げる。 */
export async function apiFetch<T>(path: string, req: ApiRequest = {}): Promise<T> {
  const { json, headers: initHeaders, ...rest } = req;
  const cfg = appConfig();
  const token = await tokenProvider();

  const headers = new Headers(initHeaders);
  headers.set('Accept', 'application/json');
  if (token) headers.set('Authorization', `Bearer ${token}`);
  let body: BodyInit | undefined;
  if (json !== undefined) {
    headers.set('Content-Type', 'application/json');
    body = JSON.stringify(json);
  }

  let res: Response;
  try {
    res = await fetch(cfg.bffBaseUrl + path, { ...rest, headers, body });
  } catch {
    throw new ApiError('network', 'サーバへ到達できませんでした。', null);
  }

  if (!res.ok) {
    throw ApiError.fromStatus(res.status);
  }
  if (res.status === 204) {
    return undefined as T;
  }
  const text = await res.text();
  return (text ? (JSON.parse(text) as T) : (undefined as T));
}
