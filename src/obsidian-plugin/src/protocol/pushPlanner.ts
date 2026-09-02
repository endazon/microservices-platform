// FR-20, UC-11, ADR-0037 決定 2・4・5・7・8・14, [[IADR-0352]] 決定 2・3・4・5: push（送る方向）の計画。
//
// 同期フォルダ配下の `.md` と追跡状態（state）と journal から、サーバへ送る操作を決める。
//
// | ローカル | 追跡状態 | journal | 判定 |
// | 在る | 未追跡 | 任意 | create |
// | 在る | 追跡済み・内容が最終同期時と同じ | 編集なし | unchanged |
// | 在る | 追跡済み | 編集あり or 内容が変わった | update（baseVersion = state.version。楽観ロック） |
// | 無い | 追跡済み | deleted | delete（論理削除を送る。決定 5） |
// | 無い | 追跡済み | movedOut | untrack（同期停止。削除は送らない。決定 4） |
// | 無い | 追跡済み | 記録なし | missing-local（報告のみ。削除は送らない＝安全側） |
// | 在る（新パス） | 追跡済み（旧パス） | renamed | rename-local（紐付けだけ更新）→ 続けて内容で判定 |
// | 任意 | serverDeleted | 任意 | server-deleted（競合として提示。ローカルが無ければ外すだけ） |
// | — | 追跡済みだが同期フォルダの外 | — | untrack（設定変更で外れた） |
//
// 純粋関数。ローカルのハッシュは呼び出し側が先に集めて渡す。edits の中身（journal の編集列に現在の
// 内容を足すか）は `pushSync.ts` の `collectEdits` が決める（ハッシュ計算が要るため）。
import { localRenames, type EditJournal } from './editJournal.ts';
import type { SyncState } from './pullPlanner.ts';
import { isInFolder, titleOf, toVaultPath } from './vaultPath.ts';

export type UntrackReason = 'moved-out' | 'outside-folder';

export type PushAction =
  | { kind: 'create'; localPath: string; vaultPath: string; title: string }
  | {
      kind: 'update';
      noteId: string;
      localPath: string;
      vaultPath: string;
      title: string;
      baseVersion: number;
    }
  | { kind: 'delete'; noteId: string; localPath: string }
  | { kind: 'untrack'; noteId: string; localPath: string; reason: UntrackReason }
  | { kind: 'rename-local'; noteId: string; from: string; to: string }
  | { kind: 'server-deleted'; noteId: string; localPath: string; localExists: boolean }
  | { kind: 'missing-local'; noteId: string; localPath: string }
  | { kind: 'unchanged'; noteId: string; localPath: string };

export function planPush(
  state: SyncState,
  journal: EditJournal,
  local: ReadonlyMap<string, string>,
  syncFolder: string,
): PushAction[] {
  const actions: PushAction[] = [];
  const handled = new Set<string>();
  const renames = localRenames(journal);

  for (const [noteId, tracked] of Object.entries(state)) {
    if (!isInFolder(syncFolder, tracked.localPath)) {
      actions.push({
        kind: 'untrack',
        noteId,
        localPath: tracked.localPath,
        reason: 'outside-folder',
      });
      continue;
    }
    let path = tracked.localPath;
    const renamedTo = renames.get(tracked.localPath);
    if (renamedTo !== undefined && !tracked.serverDeleted) {
      actions.push({ kind: 'rename-local', noteId, from: tracked.localPath, to: renamedTo });
      path = renamedTo;
    }
    handled.add(path);

    if (tracked.serverDeleted) {
      actions.push({
        kind: 'server-deleted',
        noteId,
        localPath: path,
        localExists: local.has(path),
      });
      continue;
    }
    if (journal.deleted[tracked.localPath]) {
      actions.push({ kind: 'delete', noteId, localPath: tracked.localPath });
      continue;
    }
    if (journal.movedOut[tracked.localPath]) {
      actions.push({ kind: 'untrack', noteId, localPath: tracked.localPath, reason: 'moved-out' });
      continue;
    }
    const hash = local.get(path);
    if (hash === undefined) {
      actions.push({ kind: 'missing-local', noteId, localPath: path });
      continue;
    }
    const pending = journal.edits[path]?.length ?? 0;
    if (pending === 0 && hash === tracked.localHash) {
      actions.push({ kind: 'unchanged', noteId, localPath: path });
      continue;
    }
    actions.push({
      kind: 'update',
      noteId,
      localPath: path,
      vaultPath: toVaultPath(syncFolder, path) ?? path,
      title: tracked.title ?? titleOf(path),
      baseVersion: tracked.version,
    });
  }

  for (const path of [...local.keys()].sort()) {
    if (handled.has(path) || !isInFolder(syncFolder, path)) continue;
    const vaultPath = toVaultPath(syncFolder, path);
    if (vaultPath === null) continue;
    actions.push({ kind: 'create', localPath: path, vaultPath, title: titleOf(path) });
  }
  return actions;
}
