import { emptyJournal, recordRename } from './editJournal.ts';
import { runPullSync } from './pullSync.ts';
import { MANIFEST_PATH, SyncClient } from './syncClient.ts';
import {
  FakeServer,
  MemoryFiles,
  MemoryJournal,
  MemoryState,
  fakeHasher,
  manifestEntryOf,
} from './testFakes.ts';
import type { HttpTransport } from './transport.ts';
import { SyncAuthError } from './types.ts';

const FOLDER = '個人資料';

function deps(server: FakeServer, files: MemoryFiles, state: MemoryState) {
  return {
    client: new SyncClient(server.transport, 'https://kb.example.co.jp', 'tok'),
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
    const server = new FakeServer();
    await server.seed('a', 'notes/a.md', '# A\n');
    await server.seed('b', 'b', 'B body');
    const files = new MemoryFiles();
    const state = new MemoryState();

    const report = await runPullSync(deps(server, files, state));

    expect(report.manifestCount).toBe(2);
    expect(report.written).toEqual([`${FOLDER}/notes/a.md`, `${FOLDER}/b.md`]);
    expect(files.files.get(`${FOLDER}/notes/a.md`)).toBe('# A\n');
    expect(files.files.get(`${FOLDER}/b.md`)).toBe('B body');
    expect(server.paths()).toEqual([
      `GET ${MANIFEST_PATH}`,
      'GET /private-notes/sync/notes/a',
      'GET /private-notes/sync/notes/b',
    ]);
    expect(state.saved).toEqual({
      a: {
        localPath: `${FOLDER}/notes/a.md`,
        version: 1,
        contentHash: 'h(# A\n)',
        localHash: 'h(# A\n)',
        syncedAt: '2026-09-02T09:00:00.000Z',
        vaultPath: 'notes/a.md',
        title: 'a',
      },
      b: {
        localPath: `${FOLDER}/b.md`,
        version: 1,
        contentHash: 'h(B body)',
        localHash: 'h(B body)',
        syncedAt: '2026-09-02T09:00:00.000Z',
        vaultPath: 'b',
        title: 'b',
      },
    });
  });

  // FR-20: 2 巡目は何も pull せず up-to-date になる（無駄な本文取得＝egress を増やさない）
  it('変化が無い 2 巡目は manifest だけ読み、pull も書き込みもしない', async () => {
    const server = new FakeServer();
    await server.seed('a', 'a.md', 'A');
    const files = new MemoryFiles();
    const state = new MemoryState();
    await runPullSync(deps(server, files, state));
    server.calls.length = 0;

    const report = await runPullSync(deps(server, files, state));

    expect(report.upToDate).toBe(1);
    expect(report.written).toEqual([]);
    expect(server.paths()).toEqual([`GET ${MANIFEST_PATH}`]);
    expect(files.writes).toEqual([`${FOLDER}/a.md`]);
  });

  // FR-20, ADR-0037 決定 7・14: サーバが進めば上書き、ローカルが編集されていれば上書きしない
  it('サーバが進んだ資料は上書きし、ローカルで編集された資料は conflict として残す', async () => {
    const server = new FakeServer();
    await server.seed('a', 'a.md', 'A v1');
    await server.seed('b', 'b.md', 'B v1');
    const files = new MemoryFiles();
    const state = new MemoryState();
    await runPullSync(deps(server, files, state));

    files.files.set(`${FOLDER}/b.md`, 'B edited locally');
    await server.editOnServer('a', 'A v2');
    await server.editOnServer('b', 'B v2');
    const report = await runPullSync(deps(server, files, state));

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
    const server = new FakeServer();
    server.unauthorized = true;

    await expect(runPullSync(deps(server, files, state))).rejects.toBeInstanceOf(SyncAuthError);

    expect(server.calls).toHaveLength(1);
    expect(files.writes).toEqual([]);
    expect(files.files.get(`${FOLDER}/a.md`)).toBe('stale');
    expect(state.saved).toBeNull();
  });

  // FR-20: 既に同じ内容がローカルにあれば書かずに採用し、サーバ側削除と不正パスは件数で報告する
  it('同一内容は adopt、削除済みと不正パスは書かずに報告する', async () => {
    const server = new FakeServer();
    await server.seed('a', 'a.md', 'same');
    await server.seed('gone', 'gone.md', '', { deleted: true });
    await server.seed('bad', '../x.md', 'x');
    const files = new MemoryFiles();
    files.files.set(`${FOLDER}/a.md`, 'same');
    const state = new MemoryState();

    const report = await runPullSync(deps(server, files, state));

    expect(report.adopted).toEqual([`${FOLDER}/a.md`]);
    expect(report.written).toEqual([]);
    expect(report.serverDeleted).toBe(1);
    expect(report.serverDeletedLocal).toEqual([]);
    expect(report.skipped).toEqual([{ vaultPath: '../x.md', reason: 'invalid-path' }]);
    expect(files.writes).toEqual([]);
    expect(state.saved?.a).toMatchObject({
      localPath: `${FOLDER}/a.md`,
      version: 1,
      localHash: 'h(same)',
      vaultPath: 'a.md',
      title: 'a',
    });
  });

  // FR-20: manifest 後に消えた資料（404）は 1 件の失敗として記録し、残りは続ける
  it('pull が 404 の資料は pullErrors に記録し、他の資料は取り込む', async () => {
    const server = new FakeServer();
    await server.seed('a', 'a.md', 'A');
    const ghost = await server.seed('ghost', 'ghost.md', 'G');
    const manifestWithGhost: HttpTransport = async (req) => {
      const path = new URL(req.url).pathname;
      if (path === MANIFEST_PATH) {
        server.notes.splice(server.notes.indexOf(ghost), 1);
        const res = await server.transport(req);
        return {
          status: 200,
          text: JSON.stringify([manifestEntryOf(ghost), ...(JSON.parse(res.text) as unknown[])]),
        };
      }
      return server.transport(req);
    };
    const files = new MemoryFiles();
    const state = new MemoryState();

    const report = await runPullSync({
      ...deps(server, files, state),
      client: new SyncClient(manifestWithGhost, 'https://kb.example.co.jp', 'tok'),
    });

    expect(report.pullErrors).toHaveLength(1);
    expect(report.pullErrors[0]!.noteId).toBe('ghost');
    expect(report.written).toEqual([`${FOLDER}/a.md`]);
  });

  // FR-20, ADR-0037 決定 14, [[IADR-0352]] 決定 5: サーバ側のリネームに追随してローカルを移動する
  // （旧パスが最終同期時のままなら消す／編集されていれば残す。対で置く）
  it('サーバ側で vaultPath が変わった資料は新パスへ書き、旧パスは未編集なら消し、編集済みなら残す', async () => {
    const server = new FakeServer();
    await server.seed('a', 'old-a.md', 'A');
    await server.seed('b', 'old-b.md', 'B');
    const files = new MemoryFiles();
    const state = new MemoryState();
    await runPullSync(deps(server, files, state));

    server.find('a')!.vaultPath = 'new-a.md';
    server.find('b')!.vaultPath = 'new-b.md';
    files.files.set(`${FOLDER}/old-b.md`, 'B edited locally');
    const report = await runPullSync(deps(server, files, state));

    expect(report.moved).toEqual([{ from: `${FOLDER}/old-a.md`, to: `${FOLDER}/new-a.md` }]);
    expect(files.files.has(`${FOLDER}/old-a.md`)).toBe(false);
    expect(files.files.get(`${FOLDER}/new-a.md`)).toBe('A');
    expect(files.removed).toEqual([`${FOLDER}/old-a.md`]);
    expect(report.staleOld).toEqual([`${FOLDER}/old-b.md`]);
    expect(files.files.get(`${FOLDER}/old-b.md`)).toBe('B edited locally');
    expect(files.files.get(`${FOLDER}/new-b.md`)).toBe('B');
    expect(state.saved?.a).toMatchObject({
      localPath: `${FOLDER}/new-a.md`,
      vaultPath: 'new-a.md',
    });
  });

  // FR-20, [[IADR-0352]] 決定 5: ローカルのリネーム（journal）はサーバ側のリネームと区別し、追跡パスをそのまま使う
  it('journal にローカルのリネームがあれば新パスを追跡パスとして読み、サーバから書き戻さない', async () => {
    const server = new FakeServer();
    await server.seed('a', 'a.md', 'A');
    const files = new MemoryFiles();
    const state = new MemoryState();
    await runPullSync(deps(server, files, state));

    await files.rename(`${FOLDER}/a.md`, `${FOLDER}/renamed.md`);
    const journal = recordRename(emptyJournal(), `${FOLDER}/a.md`, `${FOLDER}/renamed.md`, {
      fromInFolder: true,
      toInFolder: true,
    });
    const report = await runPullSync({
      ...deps(server, files, state),
      journal: new MemoryJournal(journal),
    });

    expect(report.upToDate).toBe(1);
    expect(report.conflicts).toEqual([]);
    expect(files.files.has(`${FOLDER}/a.md`)).toBe(false);
    expect(files.writes).toEqual([`${FOLDER}/a.md`]);
  });

  // FR-20, ADR-0037 決定 5・14, フォローアップ 11, [[IADR-0352]] 決定 4: サーバ側の削除はローカルを消さず状態に残す
  it('追跡済み資料がサーバ側で削除（または manifest から消滅）されたら serverDeleted を状態に残し、ファイルは触らない', async () => {
    const server = new FakeServer();
    await server.seed('a', 'a.md', 'A');
    await server.seed('b', 'b.md', 'B');
    const files = new MemoryFiles();
    const state = new MemoryState();
    await runPullSync(deps(server, files, state));

    server.find('a')!.deleted = true;
    server.notes.splice(server.notes.indexOf(server.find('b')!), 1);
    const report = await runPullSync(deps(server, files, state));

    expect(report.serverDeleted).toBe(2);
    expect(report.serverDeletedLocal).toEqual([`${FOLDER}/a.md`, `${FOLDER}/b.md`]);
    expect(files.files.get(`${FOLDER}/a.md`)).toBe('A');
    expect(files.files.get(`${FOLDER}/b.md`)).toBe('B');
    expect(files.removed).toEqual([]);
    expect(state.saved?.a?.serverDeleted).toBe(true);
    expect(state.saved?.b?.serverDeleted).toBe(true);
  });

  // FR-20, [[IADR-0352]]: 第 1 段の状態（vaultPath 無し）でもそのまま読め、up-to-date のときに第 2 段の形へ揃える
  it('第 1 段の状態（vaultPath 無し）はサーバ値を正として扱い、揃えた状態を保存する', async () => {
    const server = new FakeServer();
    await server.seed('a', 'a.md', 'A');
    const files = new MemoryFiles();
    files.files.set(`${FOLDER}/a.md`, 'A');
    const state = new MemoryState({
      a: {
        localPath: `${FOLDER}/a.md`,
        version: 1,
        contentHash: 'h(A)',
        localHash: 'h(A)',
        syncedAt: 't',
      },
    });

    const report = await runPullSync(deps(server, files, state));

    expect(report.upToDate).toBe(1);
    expect(files.writes).toEqual([]);
    expect(state.saved?.a).toEqual({
      localPath: `${FOLDER}/a.md`,
      version: 1,
      contentHash: 'h(A)',
      localHash: 'h(A)',
      syncedAt: 't',
      vaultPath: 'a.md',
      title: 'a',
    });
  });
});
