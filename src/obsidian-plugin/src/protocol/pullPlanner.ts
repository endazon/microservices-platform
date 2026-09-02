// FR-20, UC-11, ADR-0037 決定 5・7・14, [[IADR-0331]] 決定 1・9: pull（読み取り方向）の差分計算。
//
// **KB が唯一の正**（決定 14）だが、**ローカルの編集を黙って捨てない**（決定 7: 競合は利用者へ提示）。
// 第 1 段は push を持たないので、「ローカルで変わっている」＝「まだサーバへ送っていない編集」であり、
// 上書きすると編集が失われる。よって conflict として報告し、書かない。解決（3 択）は第 2 段。
//
// 純粋関数にしてある。ローカルの状態（存在とハッシュ）は呼び出し側が先に集めて渡す。
import { EMPTY_CONTENT_SHA256 } from './hash.ts';
import type { SyncManifestEntry } from './types.ts';
import { resolveLocalPath, type ResolvedLocalPath } from './vaultPath.ts';

/** 最終同期時に記録する 1 資料の状態（`data.json` に持つ。トークンは持たない）。 */
interface SyncedNoteState {
  /** Vault 内のローカルパス（同期フォルダ込み）。 */
  localPath: string;
  version: number;
  /** サーバが manifest / pull で返した contentHash（null は本文なし）。 */
  contentHash: string | null;
  /** 実際に書いた内容から計算したハッシュ。「ローカルが最終同期時のままか」はこちらで見る。 */
  localHash: string;
  syncedAt: string;
}

export type SyncState = Record<string, SyncedNoteState>;

export type SkipReason = 'invalid-path' | 'path-collision';
export type ConflictCause = 'local-modified' | 'local-deleted';

export type PullAction =
  | {
      kind: 'write';
      noteId: string;
      localPath: string;
      version: number;
      contentHash: string | null;
      reason: 'new' | 'updated';
      previousPath?: string;
    }
  | {
      kind: 'adopt';
      noteId: string;
      localPath: string;
      version: number;
      contentHash: string | null;
    }
  | { kind: 'up-to-date'; noteId: string; localPath: string }
  | { kind: 'conflict'; noteId: string; localPath: string; cause: ConflictCause }
  | { kind: 'server-deleted'; noteId: string; localPath: string | null; trackedLocally: boolean }
  | { kind: 'skipped'; noteId: string; vaultPath: string; reason: SkipReason };

export interface Target {
  entry: SyncManifestEntry;
  resolved: ResolvedLocalPath;
  collision: boolean;
}

/** manifest の各行をローカルパスへ落とし、同じパスへ落ちる行を衝突として印付けする。 */
export function resolveTargets(
  entries: readonly SyncManifestEntry[],
  syncFolder: string,
): Target[] {
  const targets = entries.map<Target>((entry) => ({
    entry,
    resolved: resolveLocalPath(syncFolder, entry.vaultPath),
    collision: false,
  }));
  const seen = new Map<string, Target[]>();
  for (const t of targets) {
    if (!t.resolved.ok || t.entry.deleted) continue;
    const list = seen.get(t.resolved.path) ?? [];
    list.push(t);
    seen.set(t.resolved.path, list);
  }
  for (const list of seen.values()) {
    if (list.length > 1) for (const t of list) t.collision = true;
  }
  return targets;
}

/** 書き込み候補（＝ローカルを調べる必要がある）パスの一覧。 */
export function probePaths(targets: readonly Target[]): string[] {
  return targets
    .filter((t) => t.resolved.ok && !t.collision && !t.entry.deleted)
    .map((t) => (t.resolved as { ok: true; path: string }).path);
}

export function planPull(
  targets: readonly Target[],
  state: SyncState,
  localHashes: ReadonlyMap<string, string>,
): PullAction[] {
  const actions: PullAction[] = [];
  for (const { entry, resolved, collision } of targets) {
    const tracked = state[entry.noteId];

    if (entry.deleted) {
      actions.push({
        kind: 'server-deleted',
        noteId: entry.noteId,
        localPath: tracked?.localPath ?? null,
        trackedLocally: tracked !== undefined,
      });
      continue;
    }
    if (!resolved.ok) {
      actions.push({
        kind: 'skipped',
        noteId: entry.noteId,
        vaultPath: entry.vaultPath,
        reason: 'invalid-path',
      });
      continue;
    }
    if (collision) {
      actions.push({
        kind: 'skipped',
        noteId: entry.noteId,
        vaultPath: entry.vaultPath,
        reason: 'path-collision',
      });
      continue;
    }

    const localPath = resolved.path;
    const local = localHashes.get(localPath);
    const serverHash = entry.contentHash ?? EMPTY_CONTENT_SHA256;
    const previousPath = tracked && tracked.localPath !== localPath ? tracked.localPath : undefined;
    const base = {
      noteId: entry.noteId,
      localPath,
      version: entry.version,
      contentHash: entry.contentHash,
    };

    if (local === undefined) {
      if (tracked === undefined || previousPath !== undefined) {
        actions.push({
          kind: 'write',
          ...base,
          reason: 'new',
          ...(previousPath ? { previousPath } : {}),
        });
      } else {
        actions.push({ kind: 'conflict', noteId: entry.noteId, localPath, cause: 'local-deleted' });
      }
      continue;
    }

    if (tracked !== undefined && local === tracked.localHash) {
      if (tracked.version === entry.version && tracked.contentHash === entry.contentHash) {
        actions.push({ kind: 'up-to-date', noteId: entry.noteId, localPath });
      } else {
        actions.push({
          kind: 'write',
          ...base,
          reason: 'updated',
          ...(previousPath ? { previousPath } : {}),
        });
      }
      continue;
    }

    if (local === serverHash) {
      actions.push({ kind: 'adopt', ...base });
      continue;
    }

    actions.push({ kind: 'conflict', noteId: entry.noteId, localPath, cause: 'local-modified' });
  }
  return actions;
}
