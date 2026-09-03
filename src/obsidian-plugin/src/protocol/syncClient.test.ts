import {
  MANIFEST_PATH,
  PUSH_PATH,
  SyncClient,
  noteDeletePath,
  noteMovePath,
  noteSyncPath,
} from './syncClient.ts';
import type { HttpRequest, HttpTransport } from './transport.ts';
import {
  SyncAuthError,
  SyncConflictError,
  SyncNotFoundError,
  SyncProtocolError,
  SyncQuotaError,
  SyncTooLargeError,
} from './types.ts';

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

  // FR-20, [[IADR-0360]] 決定 1・2: move は名前だけを運び、版は進まない
  it('move は vaultPath と version を送り、契約どおりの形なら返す', async () => {
    const body = {
      noteId: entry.noteId,
      vaultPath: 'notes/renamed.md',
      version: 3,
      updatedAt: '2026-09-03T09:00:00Z',
    };
    const { transport, calls } = transportReturning(200, JSON.stringify(body));
    const client = new SyncClient(transport, 'https://kb.example.co.jp', 'tok');

    await expect(
      client.move(entry.noteId, { vaultPath: 'notes/renamed.md', version: 3 }),
    ).resolves.toEqual(body);
    expect(calls[0]!.method).toBe('POST');
    expect(calls[0]!.url).toBe(`https://kb.example.co.jp${noteMovePath(entry.noteId)}`);
    expect(noteMovePath('a b')).toBe('/private-notes/sync/notes/a%20b/move');
    expect(JSON.parse(calls[0]!.body!)).toEqual({ vaultPath: 'notes/renamed.md', version: 3 });

    // 契約と違う形（version が無い）は黙って通さない
    const bad = transportReturning(200, JSON.stringify({ noteId: entry.noteId }));
    await expect(
      new SyncClient(bad.transport, 'https://kb.example.co.jp', 'tok').move(entry.noteId, {
        vaultPath: 'x.md',
        version: 1,
      }),
    ).rejects.toBeInstanceOf(SyncProtocolError);
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

  // FR-20, ADR-0037 決定 2・8, [[IADR-0352]]: push は edits[] と baseVersion を JSON で POST する（陽性対照）
  it('push は edits と baseVersion を JSON で POST し、契約どおりの応答を返す', async () => {
    const response = { noteId: entry.noteId, version: 5, contentHash: 'h', bytes: 12 };
    const { transport, calls } = transportReturning(200, JSON.stringify(response));
    const client = new SyncClient(transport, 'https://kb.example.co.jp', 'tok');
    const request = {
      noteId: entry.noteId,
      vaultPath: 'notes/memo.md',
      title: 'メモ',
      baseVersion: 3,
      edits: [{ content: 'v4' }, { content: 'v5' }],
    };

    await expect(client.push(request)).resolves.toEqual(response);
    expect(calls[0]!.method).toBe('POST');
    expect(calls[0]!.url).toBe(`https://kb.example.co.jp${PUSH_PATH}`);
    expect(calls[0]!.headers['Content-Type']).toBe('application/json');
    expect(JSON.parse(calls[0]!.body!)).toEqual(request);
  });

  // FR-20, ADR-0037 決定 5: delete は POST …/notes/{id}/delete で論理削除の応答を返す
  it('delete は POST …/delete を送り、deletedAt / purgeAt を返す', async () => {
    const response = { deletedAt: '2026-09-03T00:00:00Z', purgeAt: '2026-12-02T00:00:00Z' };
    const { transport, calls } = transportReturning(200, JSON.stringify(response));
    const client = new SyncClient(transport, 'https://kb.example.co.jp', 'tok');

    await expect(client.delete(entry.noteId)).resolves.toEqual(response);
    expect(calls[0]!.method).toBe('POST');
    expect(calls[0]!.url).toBe(`https://kb.example.co.jp${noteDeletePath(entry.noteId)}`);
    expect(calls[0]!.body).toBeUndefined();
  });

  // FR-20, ADR-0037 決定 7, [[IADR-0270]] 決定 7: 409 は 3 つの形を区別して SyncConflictError にし、自動解決しない
  it('409 は version_conflict / deleted / vault_path_conflict を区別した SyncConflictError になる', async () => {
    const request = {
      noteId: entry.noteId,
      vaultPath: 'x.md',
      title: 'x',
      baseVersion: 1,
      edits: [{ content: 'c' }],
    };
    const version = new SyncClient(
      transportReturning(
        409,
        JSON.stringify({ error: 'version_conflict', serverVersion: 7, serverUpdatedAt: 'u' }),
      ).transport,
      'https://x',
      't',
    );
    await expect(version.push(request)).rejects.toMatchObject({
      name: 'SyncConflictError',
      conflict: { error: 'version_conflict', serverVersion: 7, serverUpdatedAt: 'u' },
    });

    const deleted = new SyncClient(
      transportReturning(409, JSON.stringify({ error: 'deleted', purgeAt: 'p' })).transport,
      'https://x',
      't',
    );
    await expect(deleted.push(request)).rejects.toMatchObject({
      conflict: { error: 'deleted', purgeAt: 'p' },
    });

    const path = new SyncClient(
      transportReturning(409, JSON.stringify({ error: 'vault_path_conflict', vaultPath: 'x.md' }))
        .transport,
      'https://x',
      't',
    );
    await expect(path.push({ ...request, noteId: null, baseVersion: null })).rejects.toSatisfy(
      (e: unknown) => e instanceof SyncConflictError && e.conflict.error === 'vault_path_conflict',
    );

    // 409 でも契約と違う本文なら黙って「競合」に丸めず止める
    const unknown = new SyncClient(transportReturning(409, '{}').transport, 'https://x', 't');
    await expect(unknown.push(request)).rejects.toBeInstanceOf(SyncProtocolError);
  });

  // FR-20, [[IADR-0270]] 決定 7 / ADR-0037 決定 17: 413 と 507 は利用者に判る失敗にする
  it('413 は SyncTooLargeError、507 は SyncQuotaError になる', async () => {
    const request = {
      noteId: null,
      vaultPath: 'x.md',
      title: 'x',
      baseVersion: null,
      edits: [{ content: 'c' }],
    };
    const large = new SyncClient(transportReturning(413, '').transport, 'https://x', 't');
    await expect(large.push(request)).rejects.toBeInstanceOf(SyncTooLargeError);
    const quota = new SyncClient(transportReturning(507, '').transport, 'https://x', 't');
    await expect(quota.push(request)).rejects.toBeInstanceOf(SyncQuotaError);
  });
});
