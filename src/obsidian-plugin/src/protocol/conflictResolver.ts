// FR-20, UC-11, SC-20, ADR-0037 決定 5・7・8・14, [[IADR-0352]] 決定 3・4: 競合解決の実体（Obsidian 非依存）。
//
// 3 択（決定 7）の意味:
// - **ローカルを採用**: サーバの現在版を読んで `baseVersion` に積み直し、ローカルの編集列を再 push する
//   （サーバ側の編集はローカルの編集の**前の版**として履歴に残る。KB が正なので消えはしない）。
// - **サーバを採用**: サーバの本文でローカルを上書きし、未送信の編集列を捨てる。
// - **両方残す**: ローカルの内容を `<名前> (ローカル YYYYMMDD-HHmm).md` に写して新規 push し、
//   元のパスはサーバの本文で上書きする。
//
// **利用者が選ぶまでどれも実行しない。** ここは「選ばれたあと」の処理だけを持つ。
// 3 択の提示は Obsidian の Modal（`obsidian/conflictModal.ts`）か CLI の引数（`cli/pull.ts`）。
import { clearPath } from './editJournal.ts';
import type { Hasher } from './hash.ts';
import type { FileStore, JournalStore, SyncStateStore } from './ports.ts';
import { collectEdits } from './pushSync.ts';
import type { SyncClient } from './syncClient.ts';
import { SyncConflictError, SyncNotFoundError, type PullNoteResponse } from './types.ts';
import { localCopyPath, stampOf, titleOf, toVaultPath } from './vaultPath.ts';

export type ConflictChoice = 'local' | 'server' | 'both';

export interface ResolveDeps {
  client: SyncClient;
  files: FileStore;
  state: SyncStateStore;
  journal: JournalStore;
  hasher: Hasher;
  syncFolder: string;
  now: () => Date;
}

export type ResolveResult =
  | { kind: 'pushed'; localPath: string; version: number; versionsPushed: number }
  | { kind: 'overwritten'; localPath: string; version: number }
  | { kind: 'both'; localPath: string; copyPath: string; version: number }
  | { kind: 'recreated'; localPath: string; noteId: string }
  | { kind: 'removed'; localPath: string }
  /** 解決の途中でサーバがまた進んだ／消えた。もう一度提示する。 */
  | { kind: 'retry'; localPath: string; reason: 'version' | 'server-deleted' };

/** 版の競合（409 version_conflict）を利用者の選択どおりに解決する。 */
export async function resolveVersionConflict(
  deps: ResolveDeps,
  target: { noteId: string; localPath: string },
  choice: ConflictChoice,
): Promise<ResolveResult> {
  const state = await deps.state.load();
  const journal = await deps.journal.load();
  const tracked = state[target.noteId];
  if (tracked === undefined) throw new Error(`追跡していない資料です: ${target.localPath}`);

  let server: PullNoteResponse;
  try {
    server = await deps.client.pull(target.noteId);
  } catch (e) {
    if (e instanceof SyncNotFoundError) {
      state[target.noteId] = { ...tracked, serverDeleted: true };
      await deps.state.save(state);
      return { kind: 'retry', localPath: target.localPath, reason: 'server-deleted' };
    }
    throw e;
  }
  if (server.deleted) {
    state[target.noteId] = { ...tracked, serverDeleted: true };
    await deps.state.save(state);
    return { kind: 'retry', localPath: target.localPath, reason: 'server-deleted' };
  }

  const localContent = await deps.files.read(target.localPath);
  const localHash = await deps.hasher(localContent);
  const serverHash = await deps.hasher(server.content);
  const vaultPath = toVaultPath(deps.syncFolder, target.localPath) ?? target.localPath;
  const title = tracked.title ?? server.title;
  const adoptServer = async (): Promise<void> => {
    await deps.files.write(target.localPath, server.content);
    state[target.noteId] = {
      localPath: target.localPath,
      version: server.version,
      contentHash: server.contentHash,
      localHash: serverHash,
      syncedAt: deps.now().toISOString(),
      vaultPath: server.vaultPath,
      title: server.title,
    };
    clearPath(journal, target.localPath);
  };

  if (choice === 'server') {
    await adoptServer();
    await deps.state.save(state);
    await deps.journal.save(journal);
    return { kind: 'overwritten', localPath: target.localPath, version: server.version };
  }

  const edits = await collectEdits(
    deps.hasher,
    journal.edits[target.localPath] ?? [],
    { content: localContent, hash: localHash },
    tracked.localHash,
    deps.now,
  );

  if (choice === 'local') {
    try {
      const res = await deps.client.push({
        noteId: target.noteId,
        vaultPath,
        title,
        baseVersion: server.version,
        edits,
      });
      state[target.noteId] = {
        ...tracked,
        version: res.version,
        contentHash: res.contentHash,
        localHash,
        syncedAt: deps.now().toISOString(),
        vaultPath: server.vaultPath,
        title,
      };
      clearPath(journal, target.localPath);
      await deps.state.save(state);
      await deps.journal.save(journal);
      return {
        kind: 'pushed',
        localPath: target.localPath,
        version: res.version,
        versionsPushed: edits.length,
      };
    } catch (e) {
      if (e instanceof SyncConflictError && e.conflict.error === 'version_conflict') {
        return { kind: 'retry', localPath: target.localPath, reason: 'version' };
      }
      throw e;
    }
  }

  // both: ローカルを別パスへ写して新規 push → 元のパスはサーバを採用。
  const copyPath = localCopyPath(target.localPath, stampOf(deps.now()));
  await deps.files.write(copyPath, localContent);
  const copyVaultPath = toVaultPath(deps.syncFolder, copyPath) ?? copyPath;
  const created = await deps.client.push({
    noteId: null,
    vaultPath: copyVaultPath,
    title: titleOf(copyPath),
    baseVersion: null,
    edits,
  });
  state[created.noteId] = {
    localPath: copyPath,
    version: created.version,
    contentHash: created.contentHash,
    localHash,
    syncedAt: deps.now().toISOString(),
    vaultPath: copyVaultPath,
    title: titleOf(copyPath),
  };
  await adoptServer();
  await deps.state.save(state);
  await deps.journal.save(journal);
  return { kind: 'both', localPath: target.localPath, copyPath, version: server.version };
}

/**
 * サーバ側で削除された資料がローカルに残っている競合。
 * - `local`: ローカルの内容で**新規として再作成**する（旧 ID は削除済みのまま。復元は画面で行う）
 * - `server`: ローカルをゴミ箱へ移し、追跡を外す
 */
export async function resolveServerDeleted(
  deps: ResolveDeps,
  target: { noteId: string; localPath: string },
  choice: 'local' | 'server',
): Promise<ResolveResult> {
  const state = await deps.state.load();
  const journal = await deps.journal.load();
  const tracked = state[target.noteId];
  if (tracked === undefined) throw new Error(`追跡していない資料です: ${target.localPath}`);

  if (choice === 'server') {
    await deps.files.remove(target.localPath);
    delete state[target.noteId];
    clearPath(journal, target.localPath);
    await deps.state.save(state);
    await deps.journal.save(journal);
    return { kind: 'removed', localPath: target.localPath };
  }

  const localContent = await deps.files.read(target.localPath);
  const localHash = await deps.hasher(localContent);
  const edits = await collectEdits(
    deps.hasher,
    journal.edits[target.localPath] ?? [],
    { content: localContent, hash: localHash },
    undefined,
    deps.now,
  );
  const vaultPath = toVaultPath(deps.syncFolder, target.localPath) ?? target.localPath;
  const title = tracked.title ?? titleOf(target.localPath);
  const created = await deps.client.push({
    noteId: null,
    vaultPath,
    title,
    baseVersion: null,
    edits,
  });
  delete state[target.noteId];
  state[created.noteId] = {
    localPath: target.localPath,
    version: created.version,
    contentHash: created.contentHash,
    localHash,
    syncedAt: deps.now().toISOString(),
    vaultPath,
    title,
  };
  clearPath(journal, target.localPath);
  await deps.state.save(state);
  await deps.journal.save(journal);
  return { kind: 'recreated', localPath: target.localPath, noteId: created.noteId };
}
