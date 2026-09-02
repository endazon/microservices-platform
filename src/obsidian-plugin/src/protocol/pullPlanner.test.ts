import { EMPTY_CONTENT_SHA256 } from './hash.ts';
import { planPull, probePaths, resolveTargets, type SyncState } from './pullPlanner.ts';
import type { SyncManifestEntry } from './types.ts';

const FOLDER = '個人資料';

function entry(overrides: Partial<SyncManifestEntry> & { noteId: string }): SyncManifestEntry {
  return {
    title: overrides.noteId,
    vaultPath: `${overrides.noteId}.md`,
    version: 1,
    contentHash: `hash-${overrides.noteId}`,
    deleted: false,
    updatedAt: '2026-09-02T00:00:00Z',
    ...overrides,
  };
}

function plan(entries: SyncManifestEntry[], state: SyncState, local: Record<string, string>) {
  return planPull(resolveTargets(entries, FOLDER), state, new Map(Object.entries(local)));
}

describe('planPull', () => {
  // FR-20, UC-11: ローカルに無く未追跡の資料は取り込む（write/new）
  it('未追跡でローカルに無い資料は new として書く', () => {
    expect(plan([entry({ noteId: 'a' })], {}, {})).toEqual([
      {
        kind: 'write',
        noteId: 'a',
        localPath: `${FOLDER}/a.md`,
        version: 1,
        contentHash: 'hash-a',
        reason: 'new',
      },
    ]);
  });

  // FR-20, ADR-0037 決定 14: 最終同期以後サーバだけが進んだ資料は更新する（KB が正）
  it('ローカルが最終同期時のままでサーバの版が進んでいれば updated として書く', () => {
    const state: SyncState = {
      a: {
        localPath: `${FOLDER}/a.md`,
        version: 1,
        contentHash: 'old',
        localHash: 'L1',
        syncedAt: 't',
      },
    };
    const actions = plan([entry({ noteId: 'a', version: 2, contentHash: 'new' })], state, {
      [`${FOLDER}/a.md`]: 'L1',
    });
    expect(actions).toEqual([
      {
        kind: 'write',
        noteId: 'a',
        localPath: `${FOLDER}/a.md`,
        version: 2,
        contentHash: 'new',
        reason: 'updated',
      },
    ]);
  });

  // FR-20: 版もハッシュも同じなら何もしない（陽性対照: 上の updated と対）
  it('版とハッシュが最終同期時と同じで、ローカルも変わっていなければ up-to-date', () => {
    const state: SyncState = {
      a: {
        localPath: `${FOLDER}/a.md`,
        version: 1,
        contentHash: 'hash-a',
        localHash: 'L1',
        syncedAt: 't',
      },
    };
    expect(plan([entry({ noteId: 'a' })], state, { [`${FOLDER}/a.md`]: 'L1' })).toEqual([
      { kind: 'up-to-date', noteId: 'a', localPath: `${FOLDER}/a.md` },
    ]);
  });

  // FR-20, ADR-0037 決定 7: ローカルの編集は黙って捨てない——上書きせず conflict にする
  it('ローカルが最終同期時ともサーバとも違う内容なら conflict(local-modified) で書かない', () => {
    const state: SyncState = {
      a: {
        localPath: `${FOLDER}/a.md`,
        version: 1,
        contentHash: 'hash-a',
        localHash: 'L1',
        syncedAt: 't',
      },
    };
    expect(
      plan([entry({ noteId: 'a', version: 2, contentHash: 'srv2' })], state, {
        [`${FOLDER}/a.md`]: 'edited',
      }),
    ).toEqual([
      { kind: 'conflict', noteId: 'a', localPath: `${FOLDER}/a.md`, cause: 'local-modified' },
    ]);
    // 未追跡でもローカルにサーバと違う内容があれば同じく上書きしない
    expect(plan([entry({ noteId: 'b' })], {}, { [`${FOLDER}/b.md`]: 'something' })).toEqual([
      { kind: 'conflict', noteId: 'b', localPath: `${FOLDER}/b.md`, cause: 'local-modified' },
    ]);
  });

  // FR-20: ローカルが既にサーバと同じ内容なら書かずに状態だけ採用する
  it('ローカルがサーバと同一内容なら adopt（本文なしの資料は空文字のハッシュと比べる）', () => {
    expect(plan([entry({ noteId: 'a' })], {}, { [`${FOLDER}/a.md`]: 'hash-a' })).toEqual([
      {
        kind: 'adopt',
        noteId: 'a',
        localPath: `${FOLDER}/a.md`,
        version: 1,
        contentHash: 'hash-a',
      },
    ]);
    expect(
      plan(
        [entry({ noteId: 'e', contentHash: null })],
        {},
        { [`${FOLDER}/e.md`]: EMPTY_CONTENT_SHA256 },
      ),
    ).toEqual([
      { kind: 'adopt', noteId: 'e', localPath: `${FOLDER}/e.md`, version: 1, contentHash: null },
    ]);
  });

  // FR-20, ADR-0037 決定 5（第 1 段の範囲）: ローカルで消された追跡済み資料は再取得しない
  it('追跡済みの資料がローカルに無ければ conflict(local-deleted) で再取得しない', () => {
    const state: SyncState = {
      a: {
        localPath: `${FOLDER}/a.md`,
        version: 1,
        contentHash: 'hash-a',
        localHash: 'L1',
        syncedAt: 't',
      },
    };
    expect(plan([entry({ noteId: 'a' })], state, {})).toEqual([
      { kind: 'conflict', noteId: 'a', localPath: `${FOLDER}/a.md`, cause: 'local-deleted' },
    ]);
  });

  // FR-20, ADR-0037 決定 14: サーバ側の削除は検知するがローカルは触らない（第 1 段）
  it('deleted=true の資料は server-deleted として報告するだけ', () => {
    const state: SyncState = {
      a: {
        localPath: `${FOLDER}/a.md`,
        version: 1,
        contentHash: 'hash-a',
        localHash: 'L1',
        syncedAt: 't',
      },
    };
    expect(
      plan(
        [entry({ noteId: 'a', deleted: true }), entry({ noteId: 'b', deleted: true })],
        state,
        {},
      ),
    ).toEqual([
      { kind: 'server-deleted', noteId: 'a', localPath: `${FOLDER}/a.md`, trackedLocally: true },
      { kind: 'server-deleted', noteId: 'b', localPath: null, trackedLocally: false },
    ]);
  });

  // FR-20: サーバ側でパスが変わった資料は新しい場所へ書き、旧パスを添える
  it('サーバ側の vaultPath が変わっていれば新しいパスへ書き previousPath を添える', () => {
    const state: SyncState = {
      a: {
        localPath: `${FOLDER}/old.md`,
        version: 1,
        contentHash: 'hash-a',
        localHash: 'L1',
        syncedAt: 't',
      },
    };
    expect(plan([entry({ noteId: 'a', vaultPath: 'new.md' })], state, {})).toEqual([
      {
        kind: 'write',
        noteId: 'a',
        localPath: `${FOLDER}/new.md`,
        version: 1,
        contentHash: 'hash-a',
        reason: 'new',
        previousPath: `${FOLDER}/old.md`,
      },
    ]);
  });

  // FR-20, [[IADR-0331]] 決定 9: 不正なパスと衝突するパスは取り込まない（陰性。有効なパスの write と対）
  it('不正なパスは invalid-path、同じローカルパスへ落ちる 2 件は両方 path-collision で skipped', () => {
    const entries = [
      entry({ noteId: 'ok' }),
      entry({ noteId: 'bad', vaultPath: '../escape.md' }),
      entry({ noteId: 'c1', vaultPath: 'same' }),
      entry({ noteId: 'c2', vaultPath: 'same.md' }),
      entry({ noteId: 'c3', vaultPath: 'same.md', deleted: true }),
    ];
    const targets = resolveTargets(entries, FOLDER);
    expect(probePaths(targets)).toEqual([`${FOLDER}/ok.md`]);
    expect(planPull(targets, {}, new Map())).toEqual([
      {
        kind: 'write',
        noteId: 'ok',
        localPath: `${FOLDER}/ok.md`,
        version: 1,
        contentHash: 'hash-ok',
        reason: 'new',
      },
      { kind: 'skipped', noteId: 'bad', vaultPath: '../escape.md', reason: 'invalid-path' },
      { kind: 'skipped', noteId: 'c1', vaultPath: 'same', reason: 'path-collision' },
      { kind: 'skipped', noteId: 'c2', vaultPath: 'same.md', reason: 'path-collision' },
      { kind: 'server-deleted', noteId: 'c3', localPath: null, trackedLocally: false },
    ]);
  });
});
