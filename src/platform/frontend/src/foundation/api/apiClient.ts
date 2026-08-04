// Issue #126: BFF 境界の HTTP クライアント。BFF（/bff/*）＋ OpenAPI を契約とし、features は本 client
// 経由でのみバックエンドへアクセスする（疎結合。接続先は実行時 config）。
// - 認証: 現在のアクセストークン（Keycloak JWT）を Bearer として付与する。トークンの取得は
//   auth モジュールが setTokenProvider で注入し、api は auth 実装に直接依存しない。
// - エラー: HTTP ステータスを ApiError へ写像する（404 は存在秘匿と整合。IADR-0009）。
// - 401: IADR-0033 の「401 は再ログイン」を骨組みレベルで担保するため、setUnauthorizedHandler で
//   注入された再ログイン導線を起動する（features 個別実装に依存しない）。
import { appConfig } from '@foundation/config/runtimeConfig';
import { ApiError } from './ApiError';

type TokenProvider = () => string | null | Promise<string | null>;
type UnauthorizedHandler = () => void;

let tokenProvider: TokenProvider = () => null;
let unauthorizedHandler: UnauthorizedHandler = () => {};

/** アクセストークンの供給元を注入する（AuthProvider が UserManager を渡す）。 */
export function setTokenProvider(provider: TokenProvider): void {
  tokenProvider = provider;
}

/** 401 時の再ログイン導線を注入する（AuthProvider が login を渡す）。 */
export function setUnauthorizedHandler(handler: UnauthorizedHandler): void {
  unauthorizedHandler = handler;
}

export interface ApiRequest extends Omit<RequestInit, 'body'> {
  /** JSON 本文。指定時は Content-Type: application/json を付与する。 */
  json?: unknown;
}

/**
 * BFF へリクエストを送り、成功応答（Response）をそのまま返す。失敗時は ApiError を投げる。
 *
 * IADR-0121 決定 3: SPA から出る HTTP はこの関数 1 箇所に収束させる。orval の生成クライアントも
 * mutator（orvalMutator.ts）経由でここを通る——生成コードが素の fetch を呼ぶと、実行時 config
 * （bffBaseUrl）も 401 再ログイン導線も効かないためである。本文の解釈だけを呼び出し側に委ねる
 * （JSON を返す apiFetch / 生成コードの応答形へ包む bffFetch）。
 */
export async function apiRequest(path: string, init: RequestInit = {}): Promise<Response> {
  const cfg = appConfig();
  const token = await tokenProvider();

  const headers = new Headers(init.headers);
  if (!headers.has('Accept')) headers.set('Accept', 'application/json');
  if (token) headers.set('Authorization', `Bearer ${token}`);

  let res: Response;
  try {
    res = await fetch(cfg.bffBaseUrl + path, { ...init, headers });
  } catch (err) {
    // 呼び出し側の意図的な中断（AbortController）はネットワーク障害へ丸めない。
    if (err instanceof DOMException && err.name === 'AbortError') throw err;
    throw new ApiError('network', 'サーバへ到達できませんでした。', null);
  }

  if (!res.ok) {
    // IADR-0033: 401（未認証/期限切れ）は再ログイン導線を起動する（silent renew 失敗時の安全網）。
    if (res.status === 401) {
      unauthorizedHandler();
    }
    // FR-09, SC-09: 検証（400）・競合（409）は本文の詳細メッセージを抽出して伝える（矛盾・構文エラー表示用）。
    const details = res.status === 400 || res.status === 409 ? await parseProblemDetails(res) : [];
    throw ApiError.fromStatus(res.status, details);
  }
  return res;
}

/** BFF へ JSON リクエストを送り、応答を型 T として返す。失敗時は ApiError を投げる。 */
export async function apiFetch<T>(path: string, req: ApiRequest = {}): Promise<T> {
  const { json, headers: initHeaders, ...rest } = req;

  const headers = new Headers(initHeaders);
  headers.set('Accept', 'application/json');
  let body: BodyInit | undefined;
  if (json !== undefined) {
    headers.set('Content-Type', 'application/json');
    body = JSON.stringify(json);
  }

  const res = await apiRequest(path, { ...rest, headers, body });
  if (res.status === 204) {
    return undefined as T;
  }
  const text = await res.text();
  return (text ? (JSON.parse(text) as T) : (undefined as T));
}

// FR-09, SC-09: RFC7807 ValidationProblem / Problem 応答から人間可読なメッセージ群を抽出する。
// AuthorizationService の検証エラーは { errors: { errors: ["…"] } }、競合は { title, detail } 形式。
async function parseProblemDetails(res: Response): Promise<string[]> {
  try {
    const body = (await res.clone().json()) as {
      errors?: Record<string, unknown>;
      detail?: unknown;
      title?: unknown;
    };
    const out: string[] = [];
    if (body.errors && typeof body.errors === 'object') {
      for (const v of Object.values(body.errors)) {
        if (Array.isArray(v)) out.push(...v.filter((x): x is string => typeof x === 'string'));
        else if (typeof v === 'string') out.push(v);
      }
    }
    if (out.length === 0 && typeof body.detail === 'string') out.push(body.detail);
    if (out.length === 0 && typeof body.title === 'string') out.push(body.title);
    return out;
  } catch {
    return [];
  }
}

// IADR-0037, SC-01: SSE（text/event-stream）の 1 イベント。event 名（既定 "message"）と data（連結済み）。
export interface SseEvent {
  event: string;
  data: string;
}

/** SSE のイベントブロック（空行区切りの塊）を解析する。data: 複数行は改行連結する。純関数（テスト可能）。 */
export function parseSseBlock(block: string): SseEvent | null {
  let event = 'message';
  const dataLines: string[] = [];
  for (const line of block.split('\n')) {
    if (line.startsWith('event:')) event = line.slice('event:'.length).trim();
    else if (line.startsWith('data:')) dataLines.push(line.slice('data:'.length).replace(/^ /, ''));
    // ":" 始まりのコメント行・その他フィールドは無視する。
  }
  if (dataLines.length === 0) return null;
  return { event, data: dataLines.join('\n') };
}

/**
 * BFF の SSE エンドポイントを購読する（真のストリーミング）。EventSource は Authorization を付与できないため
 * fetch + ReadableStream で実装する。Bearer を付与し、イベントごとに onEvent を呼ぶ。失敗時は ApiError。
 */
export async function apiStream(
  path: string,
  req: ApiRequest,
  onEvent: (e: SseEvent) => void,
  signal?: AbortSignal,
): Promise<void> {
  const { json, headers: initHeaders, method, ...rest } = req;
  const cfg = appConfig();
  const token = await tokenProvider();

  const headers = new Headers(initHeaders);
  headers.set('Accept', 'text/event-stream');
  if (token) headers.set('Authorization', `Bearer ${token}`);
  let body: BodyInit | undefined;
  if (json !== undefined) {
    headers.set('Content-Type', 'application/json');
    body = JSON.stringify(json);
  }

  let res: Response;
  try {
    res = await fetch(cfg.bffBaseUrl + path, { ...rest, method: method ?? 'POST', headers, body, signal });
  } catch (err) {
    // SC-01: ヘッダ受信前に AbortController.abort() された場合は AbortError（DOMException）を保持して
    // 再スローする（連投質問などで前フェッチを中断した際、呼び出し側が意図的中断として無視できるように）。
    // それ以外の fetch 失敗のみネットワークエラーへ丸める。
    if (err instanceof DOMException && err.name === 'AbortError') throw err;
    throw new ApiError('network', 'サーバへ到達できませんでした。', null);
  }

  if (!res.ok || !res.body) {
    if (res.status === 401) unauthorizedHandler();
    throw ApiError.fromStatus(res.status);
  }

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    let idx: number;
    // イベント境界は空行（\n\n）。CRLF も許容する。
    while ((idx = indexOfBoundary(buffer)) >= 0) {
      const raw = buffer.slice(0, idx).replace(/\r/g, '');
      buffer = buffer.slice(idx + boundaryLen(buffer, idx));
      const ev = parseSseBlock(raw);
      if (ev) onEvent(ev);
    }
  }
}

function indexOfBoundary(s: string): number {
  const lf = s.indexOf('\n\n');
  const crlf = s.indexOf('\r\n\r\n');
  if (lf < 0) return crlf;
  if (crlf < 0) return lf;
  return Math.min(lf, crlf);
}
function boundaryLen(s: string, idx: number): number {
  return s.startsWith('\r\n\r\n', idx) ? 4 : 2;
}
