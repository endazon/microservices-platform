// FR-20, [[IADR-0338]] 決定 6, [[IADR-0352]]: プロトコル部のテストで共用する偽物（Obsidian 実体なし）。
//
// `FakeServer` は DocumentService の同期プロトコル（`Features/ObsidianSync/*`）の**契約どおり**に振る舞う
// —— とくに **楽観ロック**（更新は `baseVersion` 必須・現在版と不一致なら 409 `version_conflict`）と
// **1 編集 = 1 版**（`edits.length` だけ版が進む）。client 側が 409 を勝手に再送すれば、このサーバは
// 後勝ちで上書きされる。「上書きされないこと」を確かめるテストの土台なので、ここを甘くしてはならない。
import { emptyJournal, type EditJournal } from './editJournal.ts';
import type { Hasher } from './hash.ts';
import type { FileStore, JournalStore, SyncStateStore } from './ports.ts';
import type { SyncState } from './pullPlanner.ts';
import { MANIFEST_PATH, PUSH_PATH } from './syncClient.ts';
import type { HttpRequest, HttpTransport } from './transport.ts';
import type { PullNoteResponse, PushNoteRequest, SyncManifestEntry } from './types.ts';

/** テスト用の決定的ハッシュ（Web Crypto に依存しない）。サーバの contentHash も同じ関数で作る。 */
export const fakeHasher: Hasher = async (text) => `h(${text})`;

export class MemoryFiles implements FileStore {
  readonly files = new Map<string, string>();
  readonly writes: string[] = [];
  readonly removed: string[] = [];
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
  async list(folder: string) {
    const prefix = folder === '' ? '' : `${folder}/`;
    return [...this.files.keys()].filter((p) => p.startsWith(prefix) && p.endsWith('.md')).sort();
  }
  async remove(path: string) {
    if (this.files.delete(path)) this.removed.push(path);
  }
  async rename(from: string, to: string) {
    const v = this.files.get(from);
    if (v === undefined) throw new Error(`missing ${from}`);
    this.files.delete(from);
    this.files.set(to, v);
  }
}

export class MemoryState implements SyncStateStore {
  saved: SyncState | null = null;
  constructor(private current: SyncState = {}) {}
  async load() {
    return structuredClone(this.current);
  }
  async save(state: SyncState) {
    this.saved = structuredClone(state);
    this.current = state;
  }
  /** 直近に保存した（無ければ初期の）状態。 */
  get value(): SyncState {
    return structuredClone(this.current);
  }
}

export class MemoryJournal implements JournalStore {
  saved: EditJournal | null = null;
  constructor(private current: EditJournal = emptyJournal()) {}
  async load() {
    return structuredClone(this.current);
  }
  async save(journal: EditJournal) {
    this.saved = structuredClone(journal);
    this.current = journal;
  }
  get value(): EditJournal {
    return structuredClone(this.current);
  }
}

export interface ServerNote extends SyncManifestEntry {
  content: string;
}

/** manifest の 1 行（本文を落とした形）。 */
export function manifestEntryOf(note: ServerNote): SyncManifestEntry {
  return {
    noteId: note.noteId,
    title: note.title,
    vaultPath: note.vaultPath,
    version: note.version,
    contentHash: note.contentHash,
    deleted: note.deleted,
    updatedAt: note.updatedAt,
  };
}

export class FakeServer {
  readonly notes: ServerNote[] = [];
  readonly calls: HttpRequest[] = [];
  unauthorized = false;
  private nextId = 1;

  constructor(private readonly hasher: Hasher = fakeHasher) {}

  async seed(
    noteId: string,
    vaultPath: string,
    content: string,
    extra: Partial<SyncManifestEntry> = {},
  ): Promise<ServerNote> {
    const note: ServerNote = {
      noteId,
      title: noteId,
      vaultPath,
      version: 1,
      contentHash: await this.hasher(content),
      deleted: false,
      updatedAt: '2026-09-02T00:00:00Z',
      ...extra,
      content,
    };
    this.notes.push(note);
    return note;
  }

  find(noteId: string): ServerNote | undefined {
    return this.notes.find((n) => n.noteId === noteId);
  }

  /** サーバ側で誰か（画面・別端末）が編集した。版が 1 進む。 */
  async editOnServer(noteId: string, content: string): Promise<void> {
    const note = this.find(noteId)!;
    note.content = content;
    note.contentHash = await this.hasher(content);
    note.version += 1;
    note.updatedAt = '2026-09-02T12:00:00Z';
  }

  paths(): string[] {
    return this.calls.map((c) => `${c.method} ${new URL(c.url).pathname}`);
  }

  readonly transport: HttpTransport = async (req) => {
    this.calls.push(req);
    if (this.unauthorized) return { status: 401, text: '' };
    const path = new URL(req.url).pathname;

    if (req.method === 'GET' && path === MANIFEST_PATH) {
      return { status: 200, text: JSON.stringify(this.notes.map(manifestEntryOf)) };
    }
    if (req.method === 'POST' && path === PUSH_PATH) return this.push(req.body ?? '');

    const m = /^\/private-notes\/sync\/notes\/([^/]+)(\/delete)?$/.exec(path);
    if (!m) return { status: 404, text: '' };
    const note = this.find(decodeURIComponent(m[1]!));
    if (!note) return { status: 404, text: '' };
    if (req.method === 'GET' && m[2] === undefined) {
      const body: PullNoteResponse = { ...note };
      return { status: 200, text: JSON.stringify(body) };
    }
    if (req.method === 'POST' && m[2] !== undefined) {
      if (!note.deleted) {
        note.deleted = true;
        note.updatedAt = '2026-09-02T13:00:00Z';
      }
      return {
        status: 200,
        text: JSON.stringify({
          deletedAt: '2026-09-02T13:00:00Z',
          purgeAt: '2026-12-01T13:00:00Z',
        }),
      };
    }
    return { status: 405, text: '' };
  };

  private async push(body: string) {
    const req = JSON.parse(body) as PushNoteRequest;
    if (!req.title?.trim() || !req.vaultPath?.trim() || !req.edits?.length)
      return { status: 400, text: 'validation' };
    if (req.edits.some((e) => typeof e.content !== 'string'))
      return { status: 400, text: 'validation' };
    const last = req.edits[req.edits.length - 1]!.content;
    const hash = await this.hasher(last);

    if (req.noteId === null) {
      if (this.notes.some((n) => !n.deleted && n.vaultPath === req.vaultPath))
        return {
          status: 409,
          text: JSON.stringify({ error: 'vault_path_conflict', vaultPath: req.vaultPath }),
        };
      const noteId = `srv-${this.nextId++}`;
      this.notes.push({
        noteId,
        title: req.title,
        vaultPath: req.vaultPath,
        version: req.edits.length,
        contentHash: hash,
        deleted: false,
        updatedAt: '2026-09-02T14:00:00Z',
        content: last,
      });
      return {
        status: 201,
        text: JSON.stringify({
          noteId,
          version: req.edits.length,
          contentHash: hash,
          bytes: last.length,
        }),
      };
    }

    const note = this.find(req.noteId);
    if (!note) return { status: 404, text: '' };
    if (note.deleted)
      return { status: 409, text: JSON.stringify({ error: 'deleted', purgeAt: '2026-12-01' }) };
    if (req.baseVersion === null || req.baseVersion === undefined)
      return { status: 400, text: 'baseVersion required' };
    if (req.baseVersion !== note.version)
      return {
        status: 409,
        text: JSON.stringify({
          error: 'version_conflict',
          serverVersion: note.version,
          serverUpdatedAt: note.updatedAt,
        }),
      };
    note.version += req.edits.length;
    note.content = last;
    note.contentHash = hash;
    note.title = req.title;
    note.updatedAt = '2026-09-02T15:00:00Z';
    return {
      status: 200,
      text: JSON.stringify({
        noteId: note.noteId,
        version: note.version,
        contentHash: hash,
        bytes: last.length,
      }),
    };
  }
}
