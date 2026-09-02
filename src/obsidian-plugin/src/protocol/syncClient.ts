// FR-20, UC-11, ADR-0037 課題 2, [[IADR-0270]] 決定 3, [[IADR-0338]] 決定 4:
// 同期プロトコルの client（第 1 段は manifest / pull の 2 つ）。
//
// - 資格情報は **Bearer 同期トークン**（ブラウザセッションと別系統）。BFF は経由しない。
// - 401 は理由を問わず `SyncAuthError`（サーバが区別しないので client も区別しない）。
// - 応答の形は最低限の型ガードで確かめ、読めなければ `SyncProtocolError` にする——
//   「200 だが空の配列」を「資料が無い」と取り違えないため、形が違えば止まる。
import type { HttpTransport } from './transport.ts';
import {
  SyncAuthError,
  SyncNotFoundError,
  SyncProtocolError,
  type PullNoteResponse,
  type SyncManifestEntry,
} from './types.ts';

export const MANIFEST_PATH = '/private-notes/sync/manifest';
export const noteSyncPath = (noteId: string): string =>
  `/private-notes/sync/notes/${encodeURIComponent(noteId)}`;

export class SyncClient {
  constructor(
    private readonly transport: HttpTransport,
    private readonly endpoint: string,
    private readonly token: string,
  ) {}

  async getManifest(): Promise<SyncManifestEntry[]> {
    const body = await this.get(MANIFEST_PATH);
    if (!Array.isArray(body) || !body.every(isManifestEntry)) {
      throw new SyncProtocolError(200, MANIFEST_PATH, 'manifest の形が契約と違います');
    }
    return body;
  }

  async pull(noteId: string): Promise<PullNoteResponse> {
    const path = noteSyncPath(noteId);
    const body = await this.get(path);
    if (!isPullResponse(body)) {
      throw new SyncProtocolError(200, path, 'pull の形が契約と違います');
    }
    return body;
  }

  private async get(path: string): Promise<unknown> {
    const response = await this.transport({
      method: 'GET',
      url: `${this.endpoint}${path}`,
      headers: { Authorization: `Bearer ${this.token}`, Accept: 'application/json' },
    });
    if (response.status === 401) throw new SyncAuthError();
    if (response.status === 404) throw new SyncNotFoundError(path);
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
