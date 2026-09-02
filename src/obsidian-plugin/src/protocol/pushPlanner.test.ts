import { emptyJournal, recordDelete, recordRename, recordSave } from './editJournal.ts';
import type { SyncState, SyncedNoteState } from './pullPlanner.ts';
import { planPush } from './pushPlanner.ts';

const FOLDER = '個人資料';
const at = new Date('2026-09-03T00:00:00Z');

function tracked(localPath: string, extra: Partial<SyncedNoteState> = {}): SyncedNoteState {
  return {
    localPath,
    version: 3,
    contentHash: 'L',
    localHash: 'L',
    syncedAt: 't',
    vaultPath: localPath.slice(FOLDER.length + 1),
    title: 'T',
    ...extra,
  };
}

describe('planPush（ADR-0037 決定 2・4・5・8・14, IADR-0352 決定 2）', () => {
  // 未追跡のファイルは新規、追跡済みで変わっていなければ unchanged、変わっていれば update（baseVersion = 状態の版）
  it('未追跡は create、未変更は unchanged、変更あり／未送信の編集ありは update（baseVersion は状態の版）', () => {
    const state: SyncState = {
      same: tracked(`${FOLDER}/same.md`),
      changed: tracked(`${FOLDER}/changed.md`),
      journaled: tracked(`${FOLDER}/journaled.md`),
    };
    const journal = recordSave(emptyJournal(), `${FOLDER}/journaled.md`, 'x', at);
    const local = new Map([
      [`${FOLDER}/same.md`, 'L'],
      [`${FOLDER}/changed.md`, 'L2'],
      [`${FOLDER}/journaled.md`, 'L'],
      [`${FOLDER}/sub/new.md`, 'N'],
    ]);

    expect(planPush(state, journal, local, FOLDER)).toEqual([
      { kind: 'unchanged', noteId: 'same', localPath: `${FOLDER}/same.md` },
      {
        kind: 'update',
        noteId: 'changed',
        localPath: `${FOLDER}/changed.md`,
        vaultPath: 'changed.md',
        title: 'T',
        baseVersion: 3,
      },
      {
        kind: 'update',
        noteId: 'journaled',
        localPath: `${FOLDER}/journaled.md`,
        vaultPath: 'journaled.md',
        title: 'T',
        baseVersion: 3,
      },
      { kind: 'create', localPath: `${FOLDER}/sub/new.md`, vaultPath: 'sub/new.md', title: 'new' },
    ]);
  });

  // 受け入れ基準（決定 4・5）: 削除は論理削除を送る／フォルダから外したものは同期停止だけで削除を送らない（対）
  it('journal の deleted は delete、movedOut は untrack（削除を送らない）、記録が無い消失は missing-local（削除を送らない）', () => {
    const state: SyncState = {
      del: tracked(`${FOLDER}/del.md`),
      out: tracked(`${FOLDER}/out.md`),
      gone: tracked(`${FOLDER}/gone.md`),
    };
    const journal = recordDelete(emptyJournal(), `${FOLDER}/del.md`);
    recordRename(journal, `${FOLDER}/out.md`, 'archive/out.md', {
      fromInFolder: true,
      toInFolder: false,
    });

    expect(planPush(state, journal, new Map(), FOLDER)).toEqual([
      { kind: 'delete', noteId: 'del', localPath: `${FOLDER}/del.md` },
      { kind: 'untrack', noteId: 'out', localPath: `${FOLDER}/out.md`, reason: 'moved-out' },
      { kind: 'missing-local', noteId: 'gone', localPath: `${FOLDER}/gone.md` },
    ]);
  });

  // 決定 4: 同期フォルダの設定が変わって外れた資料は追跡を外すだけ（削除を送らない）
  it('同期フォルダの外にある追跡済み資料は untrack(outside-folder) にし、その内容は新規としても拾わない', () => {
    const state: SyncState = { a: tracked('旧フォルダ/a.md', { vaultPath: 'a.md' }) };
    const local = new Map([['旧フォルダ/a.md', 'L']]);
    expect(planPush(state, emptyJournal(), local, FOLDER)).toEqual([
      { kind: 'untrack', noteId: 'a', localPath: '旧フォルダ/a.md', reason: 'outside-folder' },
    ]);
  });

  // IADR-0352 決定 5 / IADR-0353 決定 4: フォルダ内のリネームは rename-local（紐付けの更新 ＋ move の材料）
  // → 新パスの内容で判定。新規にも missing にもしない
  it('ローカルのリネームは rename-local を出してから新パスの内容で判定し、新パスを create にしない', () => {
    const state: SyncState = { a: tracked(`${FOLDER}/a.md`) };
    const journal = recordRename(emptyJournal(), `${FOLDER}/a.md`, `${FOLDER}/b.md`, {
      fromInFolder: true,
      toInFolder: true,
    });
    const local = new Map([[`${FOLDER}/b.md`, 'L']]);
    expect(planPush(state, journal, local, FOLDER)).toEqual([
      {
        kind: 'rename-local',
        noteId: 'a',
        from: `${FOLDER}/a.md`,
        to: `${FOLDER}/b.md`,
        // IADR-0353 決定 4: move へ渡す新しいサーバパスと、最後に見た版（楽観ロック）
        vaultPath: 'b.md',
        baseVersion: 3,
      },
      { kind: 'unchanged', noteId: 'a', localPath: `${FOLDER}/b.md` },
    ]);

    const edited = new Map([[`${FOLDER}/b.md`, 'L2']]);
    expect(planPush(state, journal, edited, FOLDER)[1]).toMatchObject({
      kind: 'update',
      localPath: `${FOLDER}/b.md`,
      vaultPath: 'b.md',
      baseVersion: 3,
    });
  });

  // 決定 14 / フォローアップ 11: サーバ側で削除された資料は競合として提示し（ローカルが在れば）、無ければ外すだけ
  it('serverDeleted の資料は server-deleted（localExists 付き）にし、update にも create にもしない', () => {
    const state: SyncState = {
      keep: tracked(`${FOLDER}/keep.md`, { serverDeleted: true }),
      both: tracked(`${FOLDER}/both.md`, { serverDeleted: true }),
    };
    const journal = recordSave(emptyJournal(), `${FOLDER}/keep.md`, 'x', at);
    const local = new Map([[`${FOLDER}/keep.md`, 'L2']]);
    expect(planPush(state, journal, local, FOLDER)).toEqual([
      { kind: 'server-deleted', noteId: 'keep', localPath: `${FOLDER}/keep.md`, localExists: true },
      {
        kind: 'server-deleted',
        noteId: 'both',
        localPath: `${FOLDER}/both.md`,
        localExists: false,
      },
    ]);
  });
});
