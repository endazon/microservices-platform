import { MANIFEST_PATH, SyncClient, noteSyncPath } from './syncClient.ts';
import type { HttpRequest, HttpTransport } from './transport.ts';
import { SyncAuthError, SyncNotFoundError, SyncProtocolError } from './types.ts';

const entry = {
  noteId: '11111111-1111-1111-1111-111111111111',
  title: 'メモ',
  vaultPath: 'notes/memo.md',
  version: 3,
  contentHash: 'abc',
  deleted: false,
  updatedAt: '2026-09-02T00:00:00Z',
};

function transportReturning(status: number, body: string) {
  const calls: HttpRequest[] = [];
  const transport: HttpTransport = async (req) => {
    calls.push(req);
    return { status, text: body };
  };
  return { transport, calls };
}

describe('SyncClient', () => {
  // FR-20, UC-11, ADR-0037 課題 2: Bearer 同期トークンで manifest を読める（陽性対照）
  it('manifest を Bearer 同期トークンで取得し、契約どおりの形なら返す', async () => {
    const { transport, calls } = transportReturning(200, JSON.stringify([entry]));
    const client = new SyncClient(transport, 'https://kb.example.co.jp', 'tok-123');

    await expect(client.getManifest()).resolves.toEqual([entry]);
    expect(calls).toHaveLength(1);
    expect(calls[0]!.url).toBe(`https://kb.example.co.jp${MANIFEST_PATH}`);
    expect(calls[0]!.headers.Authorization).toBe('Bearer tok-123');
    expect(calls[0]!.method).toBe('GET');
  });

  // FR-20: pull は本文を運ぶ
  it('pull は資料 ID を URL エンコードして本文つきの応答を返す', async () => {
    const pulled = { ...entry, content: '# メモ\n' };
    const { transport, calls } = transportReturning(200, JSON.stringify(pulled));
    const client = new SyncClient(transport, 'https://kb.example.co.jp', 'tok');

    await expect(client.pull(entry.noteId)).resolves.toEqual(pulled);
    expect(calls[0]!.url).toBe(`https://kb.example.co.jp${noteSyncPath(entry.noteId)}`);
    expect(noteSyncPath('a b')).toBe('/private-notes/sync/notes/a%20b');
  });

  // FR-20, [[IADR-0270]] 決定 3: 401 は理由を問わず SyncAuthError（期限切れ・失効・不正を区別しない）
  it('401 は SyncAuthError になる', async () => {
    const { transport } = transportReturning(401, '');
    const client = new SyncClient(transport, 'https://kb.example.co.jp', 'expired');
    await expect(client.getManifest()).rejects.toBeInstanceOf(SyncAuthError);
    await expect(client.pull(entry.noteId)).rejects.toBeInstanceOf(SyncAuthError);
  });

  // FR-20: 所有者スコープ外は 404（存在秘匿）
  it('404 は SyncNotFoundError になる', async () => {
    const { transport } = transportReturning(404, '');
    const client = new SyncClient(transport, 'https://kb.example.co.jp', 'tok');
    await expect(client.pull(entry.noteId)).rejects.toBeInstanceOf(SyncNotFoundError);
  });

  // FR-20: 契約と違う形・読めない本文・想定外の状態コードは黙って空にせず止める
  it('契約と違う形・不正な JSON・想定外の状態コードは SyncProtocolError になる', async () => {
    const wrongShape = new SyncClient(
      transportReturning(200, JSON.stringify([{ noteId: 1 }])).transport,
      'https://x',
      't',
    );
    await expect(wrongShape.getManifest()).rejects.toBeInstanceOf(SyncProtocolError);

    const notArray = new SyncClient(
      transportReturning(200, JSON.stringify({})).transport,
      'https://x',
      't',
    );
    await expect(notArray.getManifest()).rejects.toBeInstanceOf(SyncProtocolError);

    const badJson = new SyncClient(transportReturning(200, '<html>').transport, 'https://x', 't');
    await expect(badJson.getManifest()).rejects.toBeInstanceOf(SyncProtocolError);

    const serverError = new SyncClient(transportReturning(500, 'boom').transport, 'https://x', 't');
    await expect(serverError.pull(entry.noteId)).rejects.toMatchObject({ status: 500 });

    const pullWrong = new SyncClient(
      transportReturning(200, JSON.stringify(entry)).transport,
      'https://x',
      't',
    );
    await expect(pullWrong.pull(entry.noteId)).rejects.toBeInstanceOf(SyncProtocolError);
  });
});
