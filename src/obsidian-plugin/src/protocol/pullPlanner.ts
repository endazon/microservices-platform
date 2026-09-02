// FR-20, UC-11, ADR-0037 決定 5・7・14, [[IADR-0338]] 決定 1・9, [[IADR-0352]] 決定 4・5:
// pull（読み取り方向）の差分計算。
//
// **KB が唯一の正**（決定 14）だが、**ローカルの編集を黙って捨てない**（決定 7: 競合は利用者へ提示）。
// 「ローカルで変わっている」＝「まだサーバへ送っていない編集」であり、pull は上書きしない
// （`conflict(local-modified)`）。その編集は push が送り、サーバも進んでいれば 409 → 3 択になる。
//
// 第 2 段で足したもの:
// - 状態に**サーバの `vaultPath`** を持ち、サーバ側リネーム（vaultPath が変わった）とローカルのリネーム
//   （紐付けだけ変わった）を区別する。前者はローカルを移動し、後者は追跡パスをそのまま使う。
// - サーバ側削除（`deleted=true`）と manifest からの消滅（完全削除）は、追跡済みなら `server-deleted` で
//   **状態に印を残す**（ローカルは触らない。提示は push 側）。
//
// 純粋関数にしてある。ローカルの状態（存在とハッシュ）は呼び出し側が先に集めて渡す。
import { EMPTY_CONTENT_SHA256 } from './hash.ts';
import type { SyncManifestEntry } from './types.ts';
import { isInFolder, resolveLocalPath, type ResolvedLocalPath } from './vaultPath.ts';

/** 最終同期時に記録する 1 資料の状態（`data.json` に持つ。トークンは持たない）。 */
export interface SyncedNoteState {
  /** Vault 内のローカルパス（同期フォルダ込み）。 */
  localPath: string;
  version: number;
  /** サーバが manifest / pull で返した contentHash（null は本文なし）。 */
  contentHash: string | null;
  /** 実際に書いた（または送った）内容から計算したハッシュ。「ローカルが最終同期時のままか」はこちらで見る。 */
  localHash: string;
  syncedAt: string;
  /** サーバの `vaultPath`（最終同期時）。第 1 段の状態には無い（無ければサーバ値を正として扱う）。 */
  vaultPath?: string;
  /** サーバの title（更新 push で送り返す。無ければファイル名から作る）。 */
  title?: string;
  /** サーバ側で削除（論理削除・完全削除）された。ローカルは消さず、push 側で利用者へ提示する。 */
  serverDeleted?: true;
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
      /** サーバ側リネーム: 旧パス（ローカルが最終同期時のままなら消す）。 */
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

/**
 * 追跡状態のうち、いま同期フォルダの配下にあるものだけを「追跡済み」として扱う
 * （同期フォルダの設定が変わって外れた資料は未追跡と同じ。追跡を外すのは push 側）。
 */
function trackedIn(state: SyncState, syncFolder: string, noteId: string) {
  const tracked = state[noteId];
  return tracked !== undefined && isInFolder(syncFolder, tracked.localPath) ? tracked : undefined;
}

/**
 * 資料の「いまのローカルパス」。
 * - サーバ側リネーム（状態の vaultPath とサーバの vaultPath が違う）→ サーバ値から落としたパス
 * - 第 1 段の状態（vaultPath 無し）→ サーバ値を正とする（第 1 段と同じ挙動）
 * - それ以外 → 追跡パス（ローカルのリネームを尊重。未送信のリネームは journal から先取りする）
 */
function currentLocalPath(
  entry: SyncManifestEntry,
  resolvedPath: string,
  tracked: SyncedNoteState | undefined,
  localRenames: ReadonlyMap<string, string>,
): string {
  if (tracked === undefined) return resolvedPath;
  if (tracked.vaultPath === undefined || tracked.vaultPath !== entry.vaultPath) return resolvedPath;
  return localRenames.get(tracked.localPath) ?? tracked.localPath;
}

/** ローカルを調べる必要があるパスの一覧（サーバ側のパス・追跡パス・リネーム前の旧パス）。 */
export function probePaths(
  targets: readonly Target[],
  state: SyncState,
  syncFolder: string,
  localRenames: ReadonlyMap<string, string> = new Map(),
): string[] {
  const paths = new Set<string>();
  for (const t of targets) {
    if (!t.resolved.ok || t.collision || t.entry.deleted) continue;
    const tracked = trackedIn(state, syncFolder, t.entry.noteId);
    paths.add(currentLocalPath(t.entry, t.resolved.path, tracked, localRenames));
    if (tracked !== undefined) paths.add(tracked.localPath);
  }
  return [...paths];
}

export function planPull(
  targets: readonly Target[],
  state: SyncState,
  localHashes: ReadonlyMap<string, string>,
  syncFolder: string,
  localRenames: ReadonlyMap<string, string> = new Map(),
): PullAction[] {
  const actions: PullAction[] = [];
  const seen = new Set<string>();
  for (const { entry, resolved, collision } of targets) {
    seen.add(entry.noteId);
    const tracked = trackedIn(state, syncFolder, entry.noteId);

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

    const localPath = currentLocalPath(entry, resolved.path, tracked, localRenames);
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

  // manifest から消えた追跡済み資料（完全削除）。削除済みと同じく状態に印を残すだけ。
  for (const [noteId, tracked] of Object.entries(state)) {
    if (seen.has(noteId) || !isInFolder(syncFolder, tracked.localPath)) continue;
    actions.push({
      kind: 'server-deleted',
      noteId,
      localPath: tracked.localPath,
      trackedLocally: true,
    });
  }
  return actions;
}
