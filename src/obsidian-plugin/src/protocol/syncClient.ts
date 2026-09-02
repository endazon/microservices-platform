// FR-20, UC-11, ADR-0037 課題 2, [[IADR-0270]] 決定 3, [[IADR-0338]] 決定 4, [[IADR-0352]]:
// 同期プロトコルの client（manifest / pull / push / delete / move の 5 つ）。
//
// - 資格情報は **Bearer 同期トークン**（ブラウザセッションと別系統）。BFF は経由しない。
// - 401 は理由を問わず `SyncAuthError`（サーバが区別しないので client も区別しない）。
// - 409 は 3 つの形（version_conflict / deleted / vault_path_conflict）を `SyncConflictError` に写す。
//   **自動解決しない**（ADR-0037 決定 7）——解決は呼び出し側が利用者へ提示してから行う。
// - 応答の形は最低限の型ガードで確かめ、読めなければ `SyncProtocolError` にする——
//   「200 だが空の配列」を「資料が無い」と取り違えないため、形が違えば止まる。
import type { HttpTransport } from './transport.ts';
import {
  SyncAuthError,
  SyncConflictError,
  SyncNotFoundError,
  SyncProtocolError,
  SyncQuotaError,
  SyncTooLargeError,
  type DeleteNoteResponse,
  type MoveNoteRequest,
  type MoveNoteResponse,
  type PullNoteResponse,
  type PushNoteRequest,
  type PushNoteResponse,
  type SyncConflict,
  type SyncManifestEntry,
} from './types.ts';

export const MANIFEST_PATH = '/private-notes/sync/manifest';
export const PUSH_PATH = '/private-notes/sync/notes';
export const noteSyncPath = (noteId: string): string =>
  `/private-notes/sync/notes/${encodeURIComponent(noteId)}`;
export const noteDeletePath = (noteId: string): string => `${noteSyncPath(noteId)}/delete`;
export const noteMovePath = (noteId: string): string => `${noteSyncPath(noteId)}/move`;

export class SyncClient {
  constructor(
    private readonly transport: HttpTransport,
    private readonly endpoint: string,
    private readonly token: string,
  ) {}

  async getManifest(): Promise<SyncManifestEntry[]> {
    const body = await this.request('GET', MANIFEST_PATH);
    if (!Array.isArray(body) || !body.every(isManifestEntry)) {
      throw new SyncProtocolError(200, MANIFEST_PATH, 'manifest の形が契約と違います');
    }
    return body;
  }

  async pull(noteId: string): Promise<PullNoteResponse> {
    const path = noteSyncPath(noteId);
    const body = await this.request('GET', path);
    if (!isPullResponse(body)) {
      throw new SyncProtocolError(200, path, 'pull の形が契約と違います');
    }
    return body;
  }

  /** push。`noteId` 無し = 新規（201）／有り = 更新（200。`baseVersion` 必須＝楽観ロック）。 */
  async push(request: PushNoteRequest): Promise<PushNoteResponse> {
    const body = await this.request('POST', PUSH_PATH, request);
    if (!isPushResponse(body)) {
      throw new SyncProtocolError(200, PUSH_PATH, 'push の形が契約と違います');
    }
    return body;
  }

  /**
   * リネーム（`vaultPath` の更新）。**版は進まない**ので、成功しても状態の版は積み直さない。
   * 409（version_conflict / vault_path_conflict / deleted）は**再送しない**（決定 7 と同じ向き）。
   */
  async move(noteId: string, request: MoveNoteRequest): Promise<MoveNoteResponse> {
    const path = noteMovePath(noteId);
    const body = await this.request('POST', path, request);
    if (!isMoveResponse(body)) {
      throw new SyncProtocolError(200, path, 'move の形が契約と違います');
    }
    return body;
  }

  /** 論理削除（90 日保管。ADR-0037 決定 5）。冪等。 */
  async delete(noteId: string): Promise<DeleteNoteResponse> {
    const path = noteDeletePath(noteId);
    const body = await this.request('POST', path);
    if (!isDeleteResponse(body)) {
      throw new SyncProtocolError(200, path, 'delete の形が契約と違います');
    }
    return body;
  }

  private async request(method: 'GET' | 'POST', path: string, body?: unknown): Promise<unknown> {
    const headers: Record<string, string> = {
      Authorization: `Bearer ${this.token}`,
      Accept: 'application/json',
    };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const response = await this.transport({
      method,
      url: `${this.endpoint}${path}`,
      headers,
      ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
    });
    if (response.status === 401) throw new SyncAuthError();
    if (response.status === 404) throw new SyncNotFoundError(path);
    if (response.status === 409)
      throw new SyncConflictError(path, parseConflict(path, response.text));
    if (response.status === 413) throw new SyncTooLargeError(path);
    if (response.status === 507) throw new SyncQuotaError(path);
    if (response.status < 200 || response.status >= 300) {
      throw new SyncProtocolError(response.status, path, response.text.slice(0, 200));
    }
    try {
      return JSON.parse(response.text) as unknown;
    } catch {
      throw new SyncProtocolError(response.status, path, 'JSON として読めません');
    }
  }
}

const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null;

const isHash = (v: unknown): v is string | null => v === null || typeof v === 'string';

function parseConflict(path: string, text: string): SyncConflict {
  let body: unknown;
  try {
    body = JSON.parse(text) as unknown;
  } catch {
    throw new SyncProtocolError(409, path, '409 の本文が JSON として読めません');
  }
  if (isRecord(body)) {
    if (
      body.error === 'version_conflict' &&
      typeof body.serverVersion === 'number' &&
      typeof body.serverUpdatedAt === 'string'
    ) {
      return {
        error: 'version_conflict',
        serverVersion: body.serverVersion,
        serverUpdatedAt: body.serverUpdatedAt,
      };
    }
    if (body.error === 'deleted') {
      return { error: 'deleted', purgeAt: typeof body.purgeAt === 'string' ? body.purgeAt : null };
    }
    if (body.error === 'vault_path_conflict' && typeof body.vaultPath === 'string') {
      return { error: 'vault_path_conflict', vaultPath: body.vaultPath };
    }
  }
  throw new SyncProtocolError(409, path, '409 の形が契約と違います');
}

function isManifestEntry(v: unknown): v is SyncManifestEntry {
  return (
    isRecord(v) &&
    typeof v.noteId === 'string' &&
    typeof v.title === 'string' &&
    typeof v.vaultPath === 'string' &&
    typeof v.version === 'number' &&
    isHash(v.contentHash) &&
    typeof v.deleted === 'boolean' &&
    typeof v.updatedAt === 'string'
  );
}

function isPullResponse(v: unknown): v is PullNoteResponse {
  return (
    isRecord(v) &&
    typeof v.noteId === 'string' &&
    typeof v.title === 'string' &&
    typeof v.vaultPath === 'string' &&
    typeof v.version === 'number' &&
    isHash(v.contentHash) &&
    typeof v.deleted === 'boolean' &&
    typeof v.content === 'string'
  );
}

function isPushResponse(v: unknown): v is PushNoteResponse {
  return (
    isRecord(v) &&
    typeof v.noteId === 'string' &&
    typeof v.version === 'number' &&
    typeof v.contentHash === 'string' &&
    typeof v.bytes === 'number'
  );
}

function isMoveResponse(v: unknown): v is MoveNoteResponse {
  return (
    isRecord(v) &&
    typeof v.noteId === 'string' &&
    typeof v.vaultPath === 'string' &&
    typeof v.version === 'number' &&
    typeof v.updatedAt === 'string'
  );
}

function isDeleteResponse(v: unknown): v is DeleteNoteResponse {
  return isRecord(v) && typeof v.deletedAt === 'string' && typeof v.purgeAt === 'string';
}
