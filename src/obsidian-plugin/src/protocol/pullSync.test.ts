import type { Hasher } from './hash.ts';
import type { SyncState } from './pullPlanner.ts';
import { runPullSync, type FileStore, type SyncStateStore } from './pullSync.ts';
import { MANIFEST_PATH, SyncClient } from './syncClient.ts';
import type { HttpRequest, HttpTransport } from './transport.ts';
import { SyncAuthError, type PullNoteResponse, type SyncManifestEntry } from './types.ts';

// テスト用の決定的ハッシュ（Web Crypto に依存しない）。サーバの contentHash も同じ関数で作る。
const fakeHasher: Hasher = async (text) => `h(${text})`;

class MemoryFiles implements FileStore {
  readonly files = new Map<string, string>();
  readonly writes: string[] = [];
  async exists(path: string) {
    return this.files.has(path);
  }
  async read(path: string) {
    const v = this.files.get(path);
    if (v === undefined) throw new Error(`missing ${path}`);
    return v;
  }
  async write(path: string, content: string) {
    this.files.set(path, content);
    this.writes.push(path);
  }
}

class MemoryState implements SyncStateStore {
  saved: SyncState | null = null;
  constructor(private current: SyncState = {}) {}
  async load() {
    return structuredClone(this.current);
  }
  async save(state: SyncState) {
    this.saved = structuredClone(state);
    this.current = state;
  }
}

interface ServerNote {
  entry: SyncManifestEntry;
  content: string;
}

function server(notes: ServerNote[], opts: { unauthorized?: boolean } = {}) {
  const calls: HttpRequest[] = [];
  const transport: HttpTransport = async (req) => {
    calls.push(req);
    if (opts.unauthorized) return { status: 401, text: '' };
    const path = new URL(req.url).pathname;
    if (path === MANIFEST_PATH)
      return { status: 200, text: JSON.stringify(notes.map((n) => n.entry)) };
    const id = path.slice('/private-notes/sync/notes/'.length);
    const note = notes.find((n) => n.entry.noteId === id);
    if (!note) return { status: 404, text: '' };
    const body: PullNoteResponse = { ...note.entry, content: note.content };
    return { status: 200, text: JSON.stringify(body) };
  };
  return { transport, calls };
}

const FOLDER = '個人資料';

async function note(
  noteId: string,
  vaultPath: string,
  content: string,
  extra: Partial<SyncManifestEntry> = {},
): Promise<ServerNote> {
  return {
    entry: {
      noteId,
      title: noteId,
      vaultPath,
      version: 1,
      contentHash: await fakeHasher(content),
      deleted: false,
      updatedAt: '2026-09-02T00:00:00Z',
      ...extra,
    },
    content,
  };
}

function deps(transport: HttpTransport, files: MemoryFiles, state: MemoryState) {
  return {
    client: new SyncClient(transport, 'https://kb.example.co.jp', 'tok'),
    files,
    state,
    hasher: fakeHasher,
    syncFolder: FOLDER,
    now: () => new Date('2026-09-02T09:00:00Z'),
  };
}

describe('runPullSync', () => {
  // FR-20, UC-11, ADR-0037 決定 2（pull 側）: manifest → pull → Vault へ書き下ろし、状態を記録する（陽性対照）
  it('差分のある資料だけ pull して Vault へ書き、同期状態を保存する', async () => {
    const a = await note('a', 'notes/a.md', '# A\n');
    const b = await note('b', 'b', 'B body');
    const { transport, calls } = server([a, b]);
    const files = new MemoryFiles();
    const state = new MemoryState();

    const report = await runPullSync(deps(transport, files, state));

    expect(report.manifestCount).toBe(2);
    expect(report.written).toEqual([`${FOLDER}/notes/a.md`, `${FOLDER}/b.md`]);
    expect(files.files.get(`${FOLDER}/notes/a.md`)).toBe('# A\n');
    expect(files.files.get(`${FOLDER}/b.md`)).toBe('B body');
    expect(calls.map((c) => new URL(c.url).pathname)).toEqual([
      MANIFEST_PATH,
      '/private-notes/sync/notes/a',
      '/private-notes/sync/notes/b',
    ]);
    expect(state.saved).toEqual({
      a: {
        localPath: `${FOLDER}/notes/a.md`,
        version: 1,
        contentHash: 'h(# A\n)',
        localHash: 'h(# A\n)',
        syncedAt: '2026-09-02T09:00:00.000Z',
      },
      b: {
        localPath: `${FOLDER}/b.md`,
        version: 1,
        contentHash: 'h(B body)',
        localHash: 'h(B body)',
        syncedAt: '2026-09-02T09:00:00.000Z',
      },
    });
  });

  // FR-20: 2 巡目は何も pull せず up-to-date になる（無駄な本文取得＝egress を増やさない）
  it('変化が無い 2 巡目は manifest だけ読み、pull も書き込みもしない', async () => {
    const a = await note('a', 'a.md', 'A');
    const first = server([a]);
    const files = new MemoryFiles();
    const state = new MemoryState();
    await runPullSync(deps(first.transport, files, state));

    const second = server([a]);
    const report = await runPullSync(deps(second.transport, files, state));

    expect(report.upToDate).toBe(1);
    expect(report.written).toEqual([]);
    expect(second.calls.map((c) => new URL(c.url).pathname)).toEqual([MANIFEST_PATH]);
    expect(files.writes).toEqual([`${FOLDER}/a.md`]);
  });

  // FR-20, ADR-0037 決定 7・14: サーバが進めば上書き、ローカルが編集されていれば上書きしない
  it('サーバが進んだ資料は上書きし、ローカルで編集された資料は conflict として残す', async () => {
    const a = await note('a', 'a.md', 'A v1');
    const b = await note('b', 'b.md', 'B v1');
    const files = new MemoryFiles();
    const state = new MemoryState();
    await runPullSync(deps(server([a, b]).transport, files, state));

    files.files.set(`${FOLDER}/b.md`, 'B edited locally');
    const a2 = await note('a', 'a.md', 'A v2', { version: 2 });
    const b2 = await note('b', 'b.md', 'B v2', { version: 2 });
    const report = await runPullSync(deps(server([a2, b2]).transport, files, state));

    expect(files.files.get(`${FOLDER}/a.md`)).toBe('A v2');
    expect(files.files.get(`${FOLDER}/b.md`)).toBe('B edited locally');
    expect(report.written).toEqual([`${FOLDER}/a.md`]);
    expect(report.conflicts).toEqual([{ localPath: `${FOLDER}/b.md`, cause: 'local-modified' }]);
    expect(state.saved?.a?.version).toBe(2);
    expect(state.saved?.b?.version).toBe(1);
  });

  // FR-20, ADR-0037 決定 12・13・15, [[IADR-0270]] 決定 3: 期限切れ・失効は利用者に判る失敗にし、
  // ファイルにも状態にも触らない（陰性。上の陽性対照と対）
  it('401 なら SyncAuthError を投げ、ファイルにも状態にも触らない', async () => {
    const files = new MemoryFiles();
    files.files.set(`${FOLDER}/a.md`, 'stale');
    const state = new MemoryState();
    const { transport, calls } = server([], { unauthorized: true });

    await expect(runPullSync(deps(transport, files, state))).rejects.toBeInstanceOf(SyncAuthError);

    expect(calls).toHaveLength(1);
    expect(files.writes).toEqual([]);
    expect(files.files.get(`${FOLDER}/a.md`)).toBe('stale');
    expect(state.saved).toBeNull();
  });

  // FR-20: 既に同じ内容がローカルにあれば書かずに採用し、サーバ側削除と不正パスは件数で報告する
  it('同一内容は adopt、削除済みと不正パスは書かずに報告する', async () => {
    const a = await note('a', 'a.md', 'same');
    const gone = await note('gone', 'gone.md', '', { deleted: true });
    const bad = await note('bad', '../x.md', 'x');
    const files = new MemoryFiles();
    files.files.set(`${FOLDER}/a.md`, 'same');
    const state = new MemoryState();

    const report = await runPullSync(deps(server([a, gone, bad]).transport, files, state));

    expect(report.adopted).toEqual([`${FOLDER}/a.md`]);
    expect(report.written).toEqual([]);
    expect(report.serverDeleted).toBe(1);
    expect(report.skipped).toEqual([{ vaultPath: '../x.md', reason: 'invalid-path' }]);
    expect(files.writes).toEqual([]);
    expect(state.saved?.a).toMatchObject({
      localPath: `${FOLDER}/a.md`,
      version: 1,
      localHash: 'h(same)',
    });
  });

  // FR-20: manifest 後に消えた資料（404）は 1 件の失敗として記録し、残りは続ける
  it('pull が 404 の資料は pullErrors に記録し、他の資料は取り込む', async () => {
    const a = await note('a', 'a.md', 'A');
    const ghost = await note('ghost', 'ghost.md', 'G');
    const { transport } = server([a]);
    const manifestWithGhost: HttpTransport = async (req) => {
      const path = new URL(req.url).pathname;
      if (path === MANIFEST_PATH)
        return { status: 200, text: JSON.stringify([ghost.entry, a.entry]) };
      return transport(req);
    };
    const files = new MemoryFiles();
    const state = new MemoryState();

    const report = await runPullSync(deps(manifestWithGhost, files, state));

    expect(report.pullErrors).toHaveLength(1);
    expect(report.pullErrors[0]!.noteId).toBe('ghost');
    expect(report.written).toEqual([`${FOLDER}/a.md`]);
  });
});
