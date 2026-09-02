// FR-20, UC-11, ADR-0037 決定 2・7・14, 08_data-egress-policy 許容条件 1〜4, [[IADR-0338]] 決定 1・6,
// [[IADR-0352]] 決定 4・5: pull の一巡。manifest → 差分計算 → 必要な資料だけ pull → Vault へ書く →
// 同期状態を記録する。
//
// Obsidian への依存はポート（FileStore / SyncStateStore / Hasher / HttpTransport 経由の SyncClient）
// に閉じ込め、Obsidian 実体なしで同じコードを Vitest と Node ハーネス（`cli/pull.ts`）から回す。
//
// 🔴 401（SyncAuthError）はそのまま投げる。**ファイルにも状態にも触らない**——「古いままのファイルが
// 黙って残る」のではなく「失敗した」を呼び出し側が利用者に伝える（受け入れ基準: 期限切れ・失効は
// 利用者に判る形で失敗する）。
//
// 第 2 段で足したもの:
// - サーバ側リネーム（`previousPath`）: 新パスへ書いたあと、旧パスがローカルで最終同期時のままなら消す
//   （編集されていれば残して報告する）。
// - サーバ側削除: 追跡済みなら状態に `serverDeleted` を残す。**ローカルは消さない**（提示は push 側）。
import { localRenames } from './editJournal.ts';
import type { Hasher } from './hash.ts';
import type { FileStore, JournalStore, SyncStateStore } from './ports.ts';
import {
  planPull,
  probePaths,
  resolveTargets,
  type ConflictCause,
  type SkipReason,
  type SyncedNoteState,
} from './pullPlanner.ts';
import type { SyncClient } from './syncClient.ts';
import { SyncNotFoundError } from './types.ts';

export type { FileStore, SyncStateStore } from './ports.ts';

export interface PullSyncDeps {
  client: SyncClient;
  files: FileStore;
  state: SyncStateStore;
  hasher: Hasher;
  syncFolder: string;
  now: () => Date;
  /** 未送信のローカルのリネームを先取りするため（無ければ追跡パスをそのまま使う）。 */
  journal?: JournalStore;
}

export interface PullReport {
  manifestCount: number;
  written: string[];
  adopted: string[];
  upToDate: number;
  /** `local-modified` = 未送信のローカル編集（push が送る）／`local-deleted` = 追跡パスにファイルが無い。 */
  conflicts: { localPath: string; cause: ConflictCause }[];
  serverDeleted: number;
  /** サーバ側で削除された追跡済み資料のローカルパス（ファイルは残している）。 */
  serverDeletedLocal: string[];
  /** サーバ側リネームに追随して移動した。 */
  moved: { from: string; to: string }[];
  /** サーバ側リネームだが旧パスがローカルで編集されていたため残した。 */
  staleOld: string[];
  skipped: { vaultPath: string; reason: SkipReason }[];
  pullErrors: { noteId: string; message: string }[];
}

export async function runPullSync(deps: PullSyncDeps): Promise<PullReport> {
  const manifest = await deps.client.getManifest();
  const state = await deps.state.load();
  const renames = deps.journal
    ? localRenames(await deps.journal.load())
    : new Map<string, string>();

  const targets = resolveTargets(manifest, deps.syncFolder);
  const localHashes = new Map<string, string>();
  for (const path of probePaths(targets, state, deps.syncFolder, renames)) {
    if (await deps.files.exists(path))
      localHashes.set(path, await deps.hasher(await deps.files.read(path)));
  }
  const actions = planPull(targets, state, localHashes, deps.syncFolder, renames);
  const titles = new Map(manifest.map((e) => [e.noteId, e] as const));

  const report: PullReport = {
    manifestCount: manifest.length,
    written: [],
    adopted: [],
    upToDate: 0,
    conflicts: [],
    serverDeleted: 0,
    serverDeletedLocal: [],
    moved: [],
    staleOld: [],
    skipped: [],
    pullErrors: [],
  };
  let dirty = false;
  const record = (noteId: string, next: SyncedNoteState) => {
    state[noteId] = next;
    dirty = true;
  };
  const meta = (noteId: string) => {
    const entry = titles.get(noteId);
    return entry ? { vaultPath: entry.vaultPath, title: entry.title } : {};
  };

  try {
    for (const action of actions) {
      switch (action.kind) {
        case 'write': {
          let pulled;
          try {
            pulled = await deps.client.pull(action.noteId);
          } catch (e) {
            // 404 は manifest 取得後に消えた（または所有者スコープを外れた）資料。1 件の失敗で
            // 一巡を止めず、報告に残す。それ以外（401 を含む）は一巡ごと止める。
            if (e instanceof SyncNotFoundError) {
              report.pullErrors.push({ noteId: action.noteId, message: e.message });
              continue;
            }
            throw e;
          }
          if (pulled.deleted) {
            report.serverDeleted += 1;
            continue;
          }
          const tracked = state[action.noteId];
          await deps.files.write(action.localPath, pulled.content);
          record(action.noteId, {
            localPath: action.localPath,
            version: pulled.version,
            contentHash: pulled.contentHash,
            localHash: await deps.hasher(pulled.content),
            syncedAt: deps.now().toISOString(),
            vaultPath: pulled.vaultPath,
            title: pulled.title,
          });
          report.written.push(action.localPath);
          if (action.previousPath !== undefined && tracked !== undefined) {
            const oldHash = localHashes.get(action.previousPath);
            if (oldHash === undefined) {
              // 旧パスは既に無い（利用者が消していた）。移動は完了扱い。
              report.moved.push({ from: action.previousPath, to: action.localPath });
            } else if (oldHash === tracked.localHash) {
              await deps.files.remove(action.previousPath);
              report.moved.push({ from: action.previousPath, to: action.localPath });
            } else {
              report.staleOld.push(action.previousPath);
            }
          }
          break;
        }
        case 'adopt': {
          const localHash = localHashes.get(action.localPath);
          if (localHash === undefined) continue;
          record(action.noteId, {
            localPath: action.localPath,
            version: action.version,
            contentHash: action.contentHash,
            localHash,
            syncedAt: deps.now().toISOString(),
            ...meta(action.noteId),
          });
          report.adopted.push(action.localPath);
          break;
        }
        case 'up-to-date': {
          report.upToDate += 1;
          const tracked = state[action.noteId];
          // 第 1 段の状態（vaultPath 無し）を第 2 段の形へ揃える。ファイルは触らない。
          if (tracked !== undefined && (tracked.vaultPath === undefined || tracked.serverDeleted)) {
            const rest: SyncedNoteState = { ...tracked, ...meta(action.noteId) };
            delete rest.serverDeleted;
            record(action.noteId, rest);
          }
          break;
        }
        case 'conflict':
          report.conflicts.push({ localPath: action.localPath, cause: action.cause });
          break;
        case 'server-deleted': {
          report.serverDeleted += 1;
          const tracked = state[action.noteId];
          if (action.trackedLocally && tracked !== undefined) {
            report.serverDeletedLocal.push(tracked.localPath);
            if (!tracked.serverDeleted) record(action.noteId, { ...tracked, serverDeleted: true });
          }
          break;
        }
        case 'skipped':
          report.skipped.push({ vaultPath: action.vaultPath, reason: action.reason });
          break;
      }
    }
  } finally {
    // 途中で止まっても、書き終えた分の状態は残す（次回に「ローカル編集あり」と誤認しないため）。
    if (dirty) await deps.state.save(state);
  }
  return report;
}
