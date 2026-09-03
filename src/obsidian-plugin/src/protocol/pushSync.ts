// FR-20, UC-11, ADR-0037 決定 2・4・5・7・8・14, [[IADR-0270]] 決定 7, [[IADR-0352]] 決定 2・3:
// push の一巡。同期フォルダを走査 → 計画 → リネーム／新規／更新／論理削除を送る → 状態と journal を進める。
//
// リネームは**中身より先に**送る（[[IADR-0360]] 決定 4）。move は版を進めないので、成功しても
// 状態の版は積み直さない。409 はここでも**再送しない**（`vault_path_conflict` は名前だけの失敗であり、
// 本文の送信は続ける。`version_conflict` は続く update も同じ 409 になるので送らない）。
//
// 🔴 **409（version_conflict）を受けたら上書きしない。** 状態も journal も進めず、競合として報告する。
// 解決（ローカル採用／サーバ採用／両方残す）は利用者が選んでから `conflictResolver.ts` が行う
// （決定 7: 自動解決を既定にしない）。**ここで `serverVersion` を積み直して再送してはならない**——
// それは後勝ちの自動解決である（変異試験の的。`pushSync.test.ts`）。
//
// 🔴 401（SyncAuthError）はそのまま投げる。送り終えた分の状態は残す（finally）。
import { clearPath, type JournalEdit } from './editJournal.ts';
import type { Hasher } from './hash.ts';
import type { FileStore, JournalStore, SyncStateStore } from './ports.ts';
import { planPush, type UntrackReason } from './pushPlanner.ts';
import type { SyncClient } from './syncClient.ts';
import {
  SyncConflictError,
  SyncNotFoundError,
  SyncQuotaError,
  SyncTooLargeError,
  type SyncEdit,
} from './types.ts';

export interface PushSyncDeps {
  client: SyncClient;
  files: FileStore;
  state: SyncStateStore;
  journal: JournalStore;
  hasher: Hasher;
  syncFolder: string;
  now: () => Date;
}

export type PushConflict =
  | {
      cause: 'version';
      noteId: string;
      localPath: string;
      baseVersion: number;
      serverVersion: number;
      serverUpdatedAt: string;
      pendingEdits: number;
    }
  | {
      cause: 'server-deleted';
      noteId: string;
      localPath: string;
      localExists: boolean;
      purgeAt: string | null;
    }
  | { cause: 'path-taken'; localPath: string; vaultPath: string };

export interface PushReport {
  created: string[];
  updated: string[];
  /** 送った版の数（edits の要素数の合計）。 */
  versionsPushed: number;
  deleted: string[];
  untracked: { localPath: string; reason: UntrackReason }[];
  /**
   * ローカルのリネーム。`propagated` はナレッジベース側の名前も変わったか
   * （409 で拒まれた場合は false。紐付けだけ更新して競合として報告する）。
   */
  renamedLocally: { from: string; to: string; propagated: boolean }[];
  unchanged: number;
  missingLocal: string[];
  conflicts: PushConflict[];
  errors: { localPath: string; message: string }[];
}

/**
 * 送る編集列を組む。
 * - journal の先頭にある「最終同期時の内容と同じ編集」は pull の書き込みが発火させた保存イベントの
 *   写しなので落とす（送ると内容の変わらない版が刻まれる）。
 * - 最後の編集が現在の内容と違えば（journal が無い・静穏窓の外で保存された等）現在の内容を 1 編集として足す。
 *   これで push 後のサーバの最新版は必ずローカルの現在の内容になる。
 */
export async function collectEdits(
  hasher: Hasher,
  pending: readonly JournalEdit[],
  current: { content: string; hash: string },
  syncedHash: string | undefined,
  now: () => Date,
): Promise<SyncEdit[]> {
  const edits: SyncEdit[] = [];
  let lastHash = syncedHash;
  let stripping = syncedHash !== undefined;
  for (const edit of pending) {
    const hash = await hasher(edit.content);
    if (stripping && hash === syncedHash) continue;
    stripping = false;
    edits.push({ content: edit.content, editedAt: edit.at });
    lastHash = hash;
  }
  if (lastHash !== current.hash) {
    edits.push({ content: current.content, editedAt: now().toISOString() });
  }
  return edits;
}

export async function runPushSync(deps: PushSyncDeps): Promise<PushReport> {
  const state = await deps.state.load();
  const journal = await deps.journal.load();

  const local = new Map<string, string>();
  const contents = new Map<string, string>();
  for (const path of await deps.files.list(deps.syncFolder)) {
    const content = await deps.files.read(path);
    contents.set(path, content);
    local.set(path, await deps.hasher(content));
  }
  const actions = planPush(state, journal, local, deps.syncFolder);

  const report: PushReport = {
    created: [],
    updated: [],
    versionsPushed: 0,
    deleted: [],
    untracked: [],
    renamedLocally: [],
    unchanged: 0,
    missingLocal: [],
    conflicts: [],
    errors: [],
  };
  let dirty = false;
  const touch = () => {
    dirty = true;
  };
  // move が版ずれ・サーバ側削除で拒まれた資料。同じ一巡で続く update も同じ 409 になるので、
  // 送らずに飛ばす（同じ競合を 2 件報告しない）。パス重複は本文の送信を妨げないので入れない。
  const moveBlocked = new Set<string>();

  try {
    for (const action of actions) {
      switch (action.kind) {
        case 'create': {
          const content = contents.get(action.localPath)!;
          const edits = await collectEdits(
            deps.hasher,
            journal.edits[action.localPath] ?? [],
            { content, hash: local.get(action.localPath)! },
            undefined,
            deps.now,
          );
          try {
            const res = await deps.client.push({
              noteId: null,
              vaultPath: action.vaultPath,
              title: action.title,
              baseVersion: null,
              edits,
            });
            state[res.noteId] = {
              localPath: action.localPath,
              version: res.version,
              contentHash: res.contentHash,
              localHash: local.get(action.localPath)!,
              syncedAt: deps.now().toISOString(),
              vaultPath: action.vaultPath,
              title: action.title,
            };
            clearPath(journal, action.localPath);
            touch();
            report.created.push(action.localPath);
            report.versionsPushed += edits.length;
          } catch (e) {
            if (e instanceof SyncConflictError && e.conflict.error === 'vault_path_conflict') {
              report.conflicts.push({
                cause: 'path-taken',
                localPath: action.localPath,
                vaultPath: e.conflict.vaultPath,
              });
            } else if (e instanceof SyncTooLargeError || e instanceof SyncQuotaError) {
              report.errors.push({ localPath: action.localPath, message: e.message });
            } else {
              throw e;
            }
          }
          break;
        }
        case 'update': {
          // 直前の move が同じ 409 で拒まれている。送っても同じ結果なので飛ばす（報告は 1 件）。
          if (moveBlocked.has(action.noteId)) break;
          const tracked = state[action.noteId]!;
          const content = contents.get(action.localPath)!;
          const edits = await collectEdits(
            deps.hasher,
            journal.edits[action.localPath] ?? [],
            { content, hash: local.get(action.localPath)! },
            tracked.localHash,
            deps.now,
          );
          if (edits.length === 0) {
            // journal は pull の書き込みの写しだけだった。送るものは無い。
            clearPath(journal, action.localPath);
            touch();
            report.unchanged += 1;
            break;
          }
          try {
            const res = await deps.client.push({
              noteId: action.noteId,
              vaultPath: action.vaultPath,
              title: action.title,
              baseVersion: action.baseVersion,
              edits,
            });
            state[action.noteId] = {
              ...tracked,
              localPath: action.localPath,
              version: res.version,
              contentHash: res.contentHash,
              localHash: local.get(action.localPath)!,
              syncedAt: deps.now().toISOString(),
              title: action.title,
            };
            clearPath(journal, action.localPath);
            touch();
            report.updated.push(action.localPath);
            report.versionsPushed += edits.length;
          } catch (e) {
            if (e instanceof SyncConflictError) {
              if (e.conflict.error === 'version_conflict') {
                // 🔴 上書きしない。状態も journal も進めない。利用者の選択を待つ（決定 7）。
                report.conflicts.push({
                  cause: 'version',
                  noteId: action.noteId,
                  localPath: action.localPath,
                  baseVersion: action.baseVersion,
                  serverVersion: e.conflict.serverVersion,
                  serverUpdatedAt: e.conflict.serverUpdatedAt,
                  pendingEdits: edits.length,
                });
              } else if (e.conflict.error === 'deleted') {
                state[action.noteId] = {
                  ...tracked,
                  localPath: action.localPath,
                  serverDeleted: true,
                };
                touch();
                report.conflicts.push({
                  cause: 'server-deleted',
                  noteId: action.noteId,
                  localPath: action.localPath,
                  localExists: true,
                  purgeAt: e.conflict.purgeAt,
                });
              } else {
                report.errors.push({ localPath: action.localPath, message: e.message });
              }
            } else if (e instanceof SyncNotFoundError) {
              // manifest 以後に完全削除された。削除済みと同じ扱い（ローカルは触らない）。
              state[action.noteId] = {
                ...tracked,
                localPath: action.localPath,
                serverDeleted: true,
              };
              touch();
              report.conflicts.push({
                cause: 'server-deleted',
                noteId: action.noteId,
                localPath: action.localPath,
                localExists: true,
                purgeAt: null,
              });
            } else if (e instanceof SyncTooLargeError) {
              report.errors.push({ localPath: action.localPath, message: e.message });
            } else {
              throw e;
            }
          }
          break;
        }
        case 'delete': {
          try {
            await deps.client.delete(action.noteId);
          } catch (e) {
            if (!(e instanceof SyncNotFoundError)) throw e;
            // 既にサーバに無い。ローカルの削除と一致しているので追跡を外すだけ。
          }
          delete state[action.noteId];
          clearPath(journal, action.localPath);
          touch();
          report.deleted.push(action.localPath);
          break;
        }
        case 'untrack':
          delete state[action.noteId];
          clearPath(journal, action.localPath);
          touch();
          report.untracked.push({ localPath: action.localPath, reason: action.reason });
          break;
        case 'rename-local': {
          const tracked = state[action.noteId]!;
          // 🔴 [[IADR-0360]] 決定 4: 紐付け（localPath）は move の成否によらず進める ——
          // ファイルは既にローカルで移動しており、戻すとこの資料が missing-local になり続ける。
          // サーバの vaultPath は**成功したときだけ**進める（失敗しても pull が引き戻さない）。
          state[action.noteId] = { ...tracked, localPath: action.to };
          delete journal.renamed[action.to];
          touch();
          let propagated = false;
          try {
            const res = await deps.client.move(action.noteId, {
              vaultPath: action.vaultPath,
              version: action.baseVersion,
            });
            state[action.noteId] = {
              ...state[action.noteId]!,
              vaultPath: res.vaultPath,
              syncedAt: deps.now().toISOString(),
            };
            propagated = true;
          } catch (e) {
            // 🔴 409 は再送しない（自動で名前を付け替えない）。利用者が選び直す。
            if (e instanceof SyncConflictError && e.conflict.error === 'version_conflict') {
              moveBlocked.add(action.noteId);
              report.conflicts.push({
                cause: 'version',
                noteId: action.noteId,
                localPath: action.to,
                baseVersion: action.baseVersion,
                serverVersion: e.conflict.serverVersion,
                serverUpdatedAt: e.conflict.serverUpdatedAt,
                pendingEdits: journal.edits[action.to]?.length ?? 0,
              });
            } else if (
              e instanceof SyncConflictError &&
              e.conflict.error === 'vault_path_conflict'
            ) {
              report.conflicts.push({
                cause: 'path-taken',
                localPath: action.to,
                vaultPath: e.conflict.vaultPath,
              });
            } else if (
              (e instanceof SyncConflictError && e.conflict.error === 'deleted') ||
              e instanceof SyncNotFoundError
            ) {
              moveBlocked.add(action.noteId);
              state[action.noteId] = { ...state[action.noteId]!, serverDeleted: true };
              report.conflicts.push({
                cause: 'server-deleted',
                noteId: action.noteId,
                localPath: action.to,
                localExists: true,
                purgeAt:
                  e instanceof SyncConflictError && e.conflict.error === 'deleted'
                    ? e.conflict.purgeAt
                    : null,
              });
            } else {
              throw e;
            }
          }
          report.renamedLocally.push({ from: action.from, to: action.to, propagated });
          break;
        }
        case 'server-deleted': {
          const tracked = state[action.noteId]!;
          if (!action.localExists) {
            // 両側で消えている。追跡を外すだけ（サーバへは何も送らない）。
            delete state[action.noteId];
            clearPath(journal, tracked.localPath);
            touch();
            break;
          }
          report.conflicts.push({
            cause: 'server-deleted',
            noteId: action.noteId,
            localPath: action.localPath,
            localExists: true,
            purgeAt: null,
          });
          break;
        }
        case 'missing-local':
          report.missingLocal.push(action.localPath);
          break;
        case 'unchanged':
          report.unchanged += 1;
          break;
      }
    }
  } finally {
    if (dirty) {
      await deps.state.save(state);
      await deps.journal.save(journal);
    }
  }
  return report;
}
