import { emptyJournal, recordDelete, recordRename, recordSave } from './editJournal.ts';
import { runPullSync } from './pullSync.ts';
import { collectEdits, runPushSync } from './pushSync.ts';
import { SyncClient } from './syncClient.ts';
import { FakeServer, MemoryFiles, MemoryJournal, MemoryState, fakeHasher } from './testFakes.ts';
import type { MoveNoteRequest, PushNoteRequest } from './types.ts';
import { SyncAuthError } from './types.ts';

const FOLDER = '個人資料';
const at = (sec: number) => new Date(Date.UTC(2026, 8, 3, 0, 0, sec));

function deps(server: FakeServer, files: MemoryFiles, state: MemoryState, journal: MemoryJournal) {
  return {
    client: new SyncClient(server.transport, 'https://kb.example.co.jp', 'tok'),
    files,
    state,
    journal,
    hasher: fakeHasher,
    syncFolder: FOLDER,
    now: () => new Date('2026-09-03T09:00:00Z'),
  };
}

/** サーバへ届いた push 要求（本文）。 */
function pushed(server: FakeServer): PushNoteRequest[] {
  return server.calls
    .filter((c) => c.method === 'POST' && c.url.endsWith('/private-notes/sync/notes'))
    .map((c) => JSON.parse(c.body!) as PushNoteRequest);
}

/** サーバへ届いた move 要求（本文）。 */
function moved(server: FakeServer): MoveNoteRequest[] {
  return server.calls
    .filter((c) => c.method === 'POST' && c.url.endsWith('/move'))
    .map((c) => JSON.parse(c.body!) as MoveNoteRequest);
}

describe('runPushSync（ADR-0037 決定 2・4・5・7・8・14, IADR-0352 決定 2・3）', () => {
  // 受け入れ基準（決定 8）: オフラインで 10 回保存 → 1 回の push で edits[] が 10 要素 → サーバに 10 版
  it('未送信の 10 編集は 1 回の push で edits 10 要素として送られ、サーバの版は 10 進む', async () => {
    const server = new FakeServer();
    await server.seed('a', 'a.md', 'v0');
    const files = new MemoryFiles();
    const state = new MemoryState();
    const journal = new MemoryJournal();
    await runPullSync(deps(server, files, state, journal));

    const j = journal.value;
    for (let i = 1; i <= 10; i += 1) {
      files.files.set(`${FOLDER}/a.md`, `v${i}`);
      recordSave(j, `${FOLDER}/a.md`, `v${i}`, at(i * 30));
    }
    await journal.save(j);

    const report = await runPushSync(deps(server, files, state, journal));

    expect(report.updated).toEqual([`${FOLDER}/a.md`]);
    expect(report.versionsPushed).toBe(10);
    const [req] = pushed(server);
    expect(req!.noteId).toBe('a');
    expect(req!.baseVersion).toBe(1);
    expect(req!.edits.map((e) => e.content)).toEqual(
      Array.from({ length: 10 }, (_, i) => `v${i + 1}`),
    );
    expect(server.find('a')!.version).toBe(11);
    expect(server.find('a')!.content).toBe('v10');
    expect(state.saved?.a).toMatchObject({ version: 11, localHash: 'h(v10)' });
    expect(journal.saved?.edits).toEqual({});
  });

  // 新規ファイルは noteId 無しで push し、状態に紐付く。journal が無ければ現在の内容を 1 編集として送る
  it('未追跡のファイルは新規として push し（edits は現在の内容 1 件）、返った noteId で追跡を始める', async () => {
    const server = new FakeServer();
    const files = new MemoryFiles();
    files.files.set(`${FOLDER}/sub/新しいメモ.md`, '# new');
    const state = new MemoryState();
    const journal = new MemoryJournal();

    const report = await runPushSync(deps(server, files, state, journal));

    expect(report.created).toEqual([`${FOLDER}/sub/新しいメモ.md`]);
    expect(pushed(server)).toEqual([
      {
        noteId: null,
        vaultPath: 'sub/新しいメモ.md',
        title: '新しいメモ',
        baseVersion: null,
        edits: [{ content: '# new', editedAt: '2026-09-03T09:00:00.000Z' }],
      },
    ]);
    expect(server.notes[0]).toMatchObject({
      vaultPath: 'sub/新しいメモ.md',
      version: 1,
      content: '# new',
    });
    expect(state.saved?.[server.notes[0]!.noteId]).toMatchObject({
      localPath: `${FOLDER}/sub/新しいメモ.md`,
      version: 1,
      vaultPath: 'sub/新しいメモ.md',
      title: '新しいメモ',
    });
  });

  // 🔴 受け入れ基準（決定 7）: サーバが進んだ資料をローカルでも編集 → 409 → 上書きしない・状態も journal も進めない
  // （陽性対照: 版が合っている資料は同じ一巡で 200 になる）
  it('409（版ずれ）の資料は上書きせず競合として報告し、版が合う資料だけ送る', async () => {
    const server = new FakeServer();
    await server.seed('a', 'a.md', 'A1');
    await server.seed('b', 'b.md', 'B1');
    const files = new MemoryFiles();
    const state = new MemoryState();
    const journal = new MemoryJournal();
    await runPullSync(deps(server, files, state, journal));

    await server.editOnServer('a', 'A2 (server)');
    files.files.set(`${FOLDER}/a.md`, 'A2 (local)');
    files.files.set(`${FOLDER}/b.md`, 'B2 (local)');
    const report = await runPushSync(deps(server, files, state, journal));

    expect(report.conflicts).toEqual([
      {
        cause: 'version',
        noteId: 'a',
        localPath: `${FOLDER}/a.md`,
        baseVersion: 1,
        serverVersion: 2,
        serverUpdatedAt: '2026-09-02T12:00:00Z',
        pendingEdits: 1,
      },
    ]);
    // サーバの内容はそのまま（後勝ちで上書きしていない）
    expect(server.find('a')!.content).toBe('A2 (server)');
    expect(server.find('a')!.version).toBe(2);
    // ローカルもそのまま・状態は進んでいない
    expect(files.files.get(`${FOLDER}/a.md`)).toBe('A2 (local)');
    expect(state.saved?.a?.version).toBe(1);
    // push は 1 資料につき 1 回だけ（409 を受けて再送していない）
    expect(pushed(server).filter((r) => r.noteId === 'a')).toHaveLength(1);
    // 陽性対照: b は 200 で進む
    expect(report.updated).toEqual([`${FOLDER}/b.md`]);
    expect(server.find('b')!.content).toBe('B2 (local)');
  });

  // 受け入れ基準（決定 4・5）: 削除は論理削除を送る／フォルダから外したものは削除を送らない（対）
  it('削除は POST …/delete で論理削除にし、フォルダから外したものは追跡を外すだけで何も送らない', async () => {
    const server = new FakeServer();
    await server.seed('del', 'del.md', 'D');
    await server.seed('out', 'out.md', 'O');
    const files = new MemoryFiles();
    const state = new MemoryState();
    const journal = new MemoryJournal();
    await runPullSync(deps(server, files, state, journal));

    files.files.delete(`${FOLDER}/del.md`);
    await files.rename(`${FOLDER}/out.md`, 'archive/out.md');
    const j = journal.value;
    recordDelete(j, `${FOLDER}/del.md`);
    recordRename(j, `${FOLDER}/out.md`, 'archive/out.md', {
      fromInFolder: true,
      toInFolder: false,
    });
    await journal.save(j);
    server.calls.length = 0;

    const report = await runPushSync(deps(server, files, state, journal));

    expect(report.deleted).toEqual([`${FOLDER}/del.md`]);
    expect(report.untracked).toEqual([{ localPath: `${FOLDER}/out.md`, reason: 'moved-out' }]);
    expect(server.paths()).toEqual(['POST /private-notes/sync/notes/del/delete']);
    expect(server.find('del')!.deleted).toBe(true);
    expect(server.find('out')!.deleted).toBe(false);
    expect(state.saved).toEqual({});
    expect(journal.saved).toEqual(emptyJournal());
  });

  // pull の書き込みが発火させた保存イベント（内容が最終同期時と同じ）は版として送らない
  it('journal が pull の書き込みの写しだけなら送らず unchanged、途中から変わっていれば写しを落として送る', async () => {
    const server = new FakeServer();
    await server.seed('a', 'a.md', 'A');
    await server.seed('b', 'b.md', 'B');
    const files = new MemoryFiles();
    const state = new MemoryState();
    const journal = new MemoryJournal();
    await runPullSync(deps(server, files, state, journal));

    const j = journal.value;
    recordSave(j, `${FOLDER}/a.md`, 'A', at(0));
    recordSave(j, `${FOLDER}/b.md`, 'B', at(0));
    recordSave(j, `${FOLDER}/b.md`, 'B2', at(60));
    files.files.set(`${FOLDER}/b.md`, 'B2');
    await journal.save(j);

    const report = await runPushSync(deps(server, files, state, journal));

    expect(report.unchanged).toBe(1);
    expect(report.updated).toEqual([`${FOLDER}/b.md`]);
    expect(pushed(server)).toHaveLength(1);
    expect(pushed(server)[0]!.edits.map((e) => e.content)).toEqual(['B2']);
    expect(journal.saved?.edits).toEqual({});
  });

  // IADR-0360 決定 4: ローカルのリネームは move でサーバへ伝播する。新規は作らない
  it('ローカルのリネームは move でサーバの vaultPath を変え、新しい資料を作らない', async () => {
    const server = new FakeServer();
    await server.seed('a', 'a.md', 'A');
    const files = new MemoryFiles();
    const state = new MemoryState();
    const journal = new MemoryJournal();
    await runPullSync(deps(server, files, state, journal));

    await files.rename(`${FOLDER}/a.md`, `${FOLDER}/b.md`);
    files.files.set(`${FOLDER}/b.md`, 'A2');
    const j = journal.value;
    recordRename(j, `${FOLDER}/a.md`, `${FOLDER}/b.md`, { fromInFolder: true, toInFolder: true });
    await journal.save(j);

    const report = await runPushSync(deps(server, files, state, journal));

    expect(report.renamedLocally).toEqual([
      { from: `${FOLDER}/a.md`, to: `${FOLDER}/b.md`, propagated: true },
    ]);
    expect(report.created).toEqual([]);
    expect(report.updated).toEqual([`${FOLDER}/b.md`]);
    expect(server.notes).toHaveLength(1);
    expect(server.find('a')!.vaultPath).toBe('b.md');
    expect(moved(server)).toEqual([{ vaultPath: 'b.md', version: 1 }]);
    // 名前を先に送ってから中身を送る（決定 4）
    expect(server.paths()).toEqual([
      'GET /private-notes/sync/manifest',
      'GET /private-notes/sync/notes/a',
      'POST /private-notes/sync/notes/a/move',
      'POST /private-notes/sync/notes',
    ]);
    expect(pushed(server)[0]).toMatchObject({ noteId: 'a', vaultPath: 'b.md', baseVersion: 1 });
    expect(state.saved?.a?.localPath).toBe(`${FOLDER}/b.md`);
    expect(state.saved?.a?.vaultPath).toBe('b.md');
    expect(journal.saved?.renamed).toEqual({});
  });

  // 🔴 変異試験の的（IADR-0360 決定 2・4）: move の版チェックを外す／409 を再送すると落ちる。
  it('サーバが進んでいれば move は 409 になり、名前も中身も送り直さない', async () => {
    const server = new FakeServer();
    await server.seed('a', 'a.md', 'A');
    const files = new MemoryFiles();
    const state = new MemoryState();
    const journal = new MemoryJournal();
    await runPullSync(deps(server, files, state, journal));

    await server.editOnServer('a', 'サーバの編集'); // 版が 2 へ
    await files.rename(`${FOLDER}/a.md`, `${FOLDER}/b.md`);
    files.files.set(`${FOLDER}/b.md`, 'ローカルの編集');
    const j = journal.value;
    recordRename(j, `${FOLDER}/a.md`, `${FOLDER}/b.md`, { fromInFolder: true, toInFolder: true });
    await journal.save(j);

    const report = await runPushSync(deps(server, files, state, journal));

    expect(moved(server)).toEqual([{ vaultPath: 'b.md', version: 1 }]);
    expect(report.renamedLocally).toEqual([
      { from: `${FOLDER}/a.md`, to: `${FOLDER}/b.md`, propagated: false },
    ]);
    expect(report.conflicts).toEqual([
      {
        cause: 'version',
        noteId: 'a',
        localPath: `${FOLDER}/b.md`,
        baseVersion: 1,
        serverVersion: 2,
        serverUpdatedAt: '2026-09-02T12:00:00Z',
        pendingEdits: 0,
      },
    ]);
    // サーバの名前も中身も動いていない（後勝ちで上書きされていない）
    expect(server.find('a')).toMatchObject({
      vaultPath: 'a.md',
      version: 2,
      content: 'サーバの編集',
    });
    // 版ずれの資料は本文も送らない（続く update も同じ 409 になる）
    expect(pushed(server)).toEqual([]);
    // 紐付け（localPath）は進める（ファイルは既に移動済み）が、サーバ値は据え置く
    expect(state.saved?.a).toMatchObject({ localPath: `${FOLDER}/b.md`, vaultPath: 'a.md' });
  });

  // 名前の重複は名前だけの失敗であり、本文の送信は続く（IADR-0360 決定 1）
  it('移動先の名前が埋まっていれば move は 409 path-taken になり、本文の送信は続く', async () => {
    const server = new FakeServer();
    await server.seed('a', 'a.md', 'A');
    const files = new MemoryFiles();
    const state = new MemoryState();
    const journal = new MemoryJournal();
    await runPullSync(deps(server, files, state, journal));

    // 別端末（または画面）が b.md を作った。この端末はまだ取り込んでいない
    await server.seed('b', 'b.md', 'B');
    await files.rename(`${FOLDER}/a.md`, `${FOLDER}/b.md`); // b.md は既に存在する資料の名前
    files.files.set(`${FOLDER}/b.md`, 'A2');
    const j = journal.value;
    recordRename(j, `${FOLDER}/a.md`, `${FOLDER}/b.md`, { fromInFolder: true, toInFolder: true });
    await journal.save(j);

    const report = await runPushSync(deps(server, files, state, journal));

    expect(report.renamedLocally[0]!.propagated).toBe(false);
    expect(report.conflicts).toEqual([
      { cause: 'path-taken', localPath: `${FOLDER}/b.md`, vaultPath: 'b.md' },
    ]);
    // 相手の資料は動かない。自分の名前も旧名のまま
    expect(server.find('b')).toMatchObject({ vaultPath: 'b.md', content: 'B' });
    expect(server.find('a')!.vaultPath).toBe('a.md');
    // 陽性対照: 本文は送れている（名前の失敗が中身を巻き添えにしない）
    expect(report.updated).toEqual([`${FOLDER}/b.md`]);
    expect(server.find('a')!.content).toBe('A2');
  });

  // フォローアップ 11: サーバ側で削除された資料は競合として提示し、ローカルが無ければ外すだけ
  it('serverDeleted の資料はローカルが在れば競合として提示し、無ければ追跡を外すだけで何も送らない', async () => {
    const server = new FakeServer();
    await server.seed('keep', 'keep.md', 'K');
    await server.seed('both', 'both.md', 'B');
    const files = new MemoryFiles();
    const state = new MemoryState();
    const journal = new MemoryJournal();
    await runPullSync(deps(server, files, state, journal));
    server.find('keep')!.deleted = true;
    server.find('both')!.deleted = true;
    await runPullSync(deps(server, files, state, journal));
    files.files.delete(`${FOLDER}/both.md`);
    server.calls.length = 0;

    const report = await runPushSync(deps(server, files, state, journal));

    expect(report.conflicts).toEqual([
      {
        cause: 'server-deleted',
        noteId: 'keep',
        localPath: `${FOLDER}/keep.md`,
        localExists: true,
        purgeAt: null,
      },
    ]);
    expect(server.calls).toEqual([]);
    expect(files.files.get(`${FOLDER}/keep.md`)).toBe('K');
    expect(state.saved?.both).toBeUndefined();
    expect(state.saved?.keep?.serverDeleted).toBe(true);
  });

  // 新規のパスがサーバの有効な資料と重なれば path-taken として報告する（先に pull させる）
  it('新規 push のパスがサーバの既存資料と重なれば path-taken の競合として報告し、他は続ける', async () => {
    const server = new FakeServer();
    await server.seed('srv', 'taken.md', 'S');
    const files = new MemoryFiles();
    files.files.set(`${FOLDER}/taken.md`, 'local');
    files.files.set(`${FOLDER}/other.md`, 'O');
    const state = new MemoryState();
    const journal = new MemoryJournal();

    const report = await runPushSync(deps(server, files, state, journal));

    expect(report.conflicts).toEqual([
      { cause: 'path-taken', localPath: `${FOLDER}/taken.md`, vaultPath: 'taken.md' },
    ]);
    expect(report.created).toEqual([`${FOLDER}/other.md`]);
    expect(server.find('srv')!.content).toBe('S');
  });

  // 401 は一巡ごと止め、何も送らず状態も journal も触らない（陰性。陽性対照は上の各テスト）
  it('401 なら SyncAuthError を投げ、状態と journal を触らない', async () => {
    const server = new FakeServer();
    server.unauthorized = true;
    const files = new MemoryFiles();
    files.files.set(`${FOLDER}/a.md`, 'A');
    const state = new MemoryState();
    const journal = new MemoryJournal(recordSave(emptyJournal(), `${FOLDER}/a.md`, 'A', at(0)));

    await expect(runPushSync(deps(server, files, state, journal))).rejects.toBeInstanceOf(
      SyncAuthError,
    );
    expect(state.saved).toBeNull();
    expect(journal.saved).toBeNull();
  });

  it('collectEdits は最終同期時と同じ先頭の編集を落とし、最後が現在と違えば現在の内容を足す', async () => {
    const now = () => new Date('2026-09-03T09:00:00Z');
    const pending = [
      { at: 't1', content: 'synced' },
      { at: 't2', content: 'e1' },
      { at: 't3', content: 'e2' },
    ];
    await expect(
      collectEdits(fakeHasher, pending, { content: 'cur', hash: 'h(cur)' }, 'h(synced)', now),
    ).resolves.toEqual([
      { content: 'e1', editedAt: 't2' },
      { content: 'e2', editedAt: 't3' },
      { content: 'cur', editedAt: '2026-09-03T09:00:00.000Z' },
    ]);
    // 最後の編集が現在の内容と同じなら足さない
    await expect(
      collectEdits(fakeHasher, pending, { content: 'e2', hash: 'h(e2)' }, 'h(synced)', now),
    ).resolves.toHaveLength(2);
    // 新規（同期済みハッシュ無し）は落とさない
    await expect(
      collectEdits(fakeHasher, pending, { content: 'e2', hash: 'h(e2)' }, undefined, now),
    ).resolves.toHaveLength(3);
  });
});
