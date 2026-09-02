import { resolveServerDeleted, resolveVersionConflict } from './conflictResolver.ts';
import { recordSave } from './editJournal.ts';
import { runPullSync } from './pullSync.ts';
import { runPushSync } from './pushSync.ts';
import { SyncClient } from './syncClient.ts';
import { FakeServer, MemoryFiles, MemoryJournal, MemoryState, fakeHasher } from './testFakes.ts';

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
    now: () => new Date(2026, 8, 3, 9, 12, 0),
  };
}

/** サーバが進み、ローカルにも未送信の編集 2 件がある競合状態を作り、push で 409 を受けたところまで進める。 */
async function conflicted() {
  const server = new FakeServer();
  await server.seed('a', 'a.md', 'base');
  const files = new MemoryFiles();
  const state = new MemoryState();
  const journal = new MemoryJournal();
  const d = deps(server, files, state, journal);
  await runPullSync(d);

  await server.editOnServer('a', 'server v2');
  const j = journal.value;
  recordSave(j, `${FOLDER}/a.md`, 'local e1', at(0));
  recordSave(j, `${FOLDER}/a.md`, 'local e2', at(60));
  files.files.set(`${FOLDER}/a.md`, 'local e2');
  await journal.save(j);
  const push = await runPushSync(d);
  expect(push.conflicts).toHaveLength(1);
  server.calls.length = 0;
  return { server, files, state, journal, d, target: { noteId: 'a', localPath: `${FOLDER}/a.md` } };
}

describe('conflictResolver — 3 択（ADR-0037 決定 7, IADR-0352 決定 3）', () => {
  // ローカルを採用: サーバの現在版を baseVersion に積み直し、ローカルの編集列を送る（サーバ側の版は履歴に残る）
  it('local はサーバの現在版を baseVersion にしてローカルの編集列を再 push する', async () => {
    const { server, files, state, journal, d, target } = await conflicted();

    const result = await resolveVersionConflict(d, target, 'local');

    expect(result).toEqual({
      kind: 'pushed',
      localPath: target.localPath,
      version: 4,
      versionsPushed: 2,
    });
    expect(server.paths()).toEqual([
      'GET /private-notes/sync/notes/a',
      'POST /private-notes/sync/notes',
    ]);
    const req = JSON.parse(server.calls[1]!.body!) as {
      baseVersion: number;
      edits: { content: string }[];
    };
    expect(req.baseVersion).toBe(2);
    expect(req.edits.map((e) => e.content)).toEqual(['local e1', 'local e2']);
    expect(server.find('a')).toMatchObject({ version: 4, content: 'local e2' });
    expect(files.files.get(target.localPath)).toBe('local e2');
    expect(state.saved?.a).toMatchObject({ version: 4, localHash: 'h(local e2)' });
    expect(journal.saved?.edits).toEqual({});
  });

  // サーバを採用: サーバの本文でローカルを上書きし、未送信の編集を捨てる。push はしない
  it('server はサーバの本文でローカルを上書きし、未送信の編集を捨て、push しない', async () => {
    const { server, files, state, journal, d, target } = await conflicted();

    const result = await resolveVersionConflict(d, target, 'server');

    expect(result).toEqual({ kind: 'overwritten', localPath: target.localPath, version: 2 });
    expect(server.paths()).toEqual(['GET /private-notes/sync/notes/a']);
    expect(files.files.get(target.localPath)).toBe('server v2');
    expect(server.find('a')).toMatchObject({ version: 2, content: 'server v2' });
    expect(state.saved?.a).toMatchObject({ version: 2, localHash: 'h(server v2)' });
    expect(journal.saved?.edits).toEqual({});
  });

  // 両方残す: ローカルを別名で新規 push し、元のパスはサーバの本文にする
  it('both はローカルを「(ローカル YYYYMMDD-HHmm)」付きの別パスで新規 push し、元のパスはサーバの本文にする', async () => {
    const { server, files, state, journal, d, target } = await conflicted();

    const result = await resolveVersionConflict(d, target, 'both');

    const copyPath = `${FOLDER}/a (ローカル 20260903-0912).md`;
    expect(result).toEqual({ kind: 'both', localPath: target.localPath, copyPath, version: 2 });
    expect(files.files.get(copyPath)).toBe('local e2');
    expect(files.files.get(target.localPath)).toBe('server v2');
    const created = server.notes.find((n) => n.vaultPath === 'a (ローカル 20260903-0912).md');
    expect(created).toMatchObject({
      version: 2,
      content: 'local e2',
      title: 'a (ローカル 20260903-0912)',
    });
    expect(server.find('a')).toMatchObject({ version: 2, content: 'server v2' });
    expect(state.saved?.[created!.noteId]).toMatchObject({ localPath: copyPath, version: 2 });
    expect(state.saved?.a).toMatchObject({ version: 2, localHash: 'h(server v2)' });
    expect(journal.saved?.edits).toEqual({});
  });

  // 解決の途中でサーバがまた進んだ／消えたら実行せず retry を返す（自動で追いかけない）
  it('local の再 push がまた 409 になれば retry を返し、何も進めない', async () => {
    const { server, files, state, d, target } = await conflicted();
    const original = server.transport;
    let bumped = false;
    const racing = new SyncClient(
      async (req) => {
        const res = await original(req);
        if (req.method === 'GET' && !bumped) {
          bumped = true;
          await server.editOnServer('a', 'server v3');
        }
        return res;
      },
      'https://kb.example.co.jp',
      'tok',
    );

    const result = await resolveVersionConflict({ ...d, client: racing }, target, 'local');

    expect(result).toEqual({ kind: 'retry', localPath: target.localPath, reason: 'version' });
    expect(server.find('a')).toMatchObject({ version: 3, content: 'server v3' });
    expect(files.files.get(target.localPath)).toBe('local e2');
    expect(state.saved?.a?.version).toBe(1);
  });
});

describe('resolveServerDeleted（ADR-0037 決定 5, フォローアップ 11, IADR-0352 決定 4）', () => {
  async function serverDeleted() {
    const server = new FakeServer();
    await server.seed('a', 'a.md', 'A');
    const files = new MemoryFiles();
    const state = new MemoryState();
    const journal = new MemoryJournal();
    const d = deps(server, files, state, journal);
    await runPullSync(d);
    server.find('a')!.deleted = true;
    await runPullSync(d);
    server.calls.length = 0;
    return {
      server,
      files,
      state,
      journal,
      d,
      target: { noteId: 'a', localPath: `${FOLDER}/a.md` },
    };
  }

  it('local はローカルの内容を新しい資料として送り、追跡を新 ID へ付け替える', async () => {
    const { server, files, state, d, target } = await serverDeleted();
    files.files.set(target.localPath, 'A edited');

    const result = await resolveServerDeleted(d, target, 'local');

    expect(result).toMatchObject({ kind: 'recreated', localPath: target.localPath });
    const created = server.notes.find((n) => n.noteId !== 'a')!;
    expect(created).toMatchObject({ vaultPath: 'a.md', content: 'A edited', deleted: false });
    expect(server.find('a')!.deleted).toBe(true);
    expect(state.saved?.a).toBeUndefined();
    expect(state.saved?.[created.noteId]).toMatchObject({
      localPath: target.localPath,
      version: 1,
    });
  });

  it('server はローカルをゴミ箱へ移して追跡を外し、何も送らない', async () => {
    const { server, files, state, d, target } = await serverDeleted();

    const result = await resolveServerDeleted(d, target, 'server');

    expect(result).toEqual({ kind: 'removed', localPath: target.localPath });
    expect(files.removed).toEqual([target.localPath]);
    expect(server.calls).toEqual([]);
    expect(state.saved).toEqual({});
  });
});
