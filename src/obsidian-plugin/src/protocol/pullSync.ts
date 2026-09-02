// FR-20, UC-11, ADR-0037 決定 2・7・14, 08_data-egress-policy 許容条件 1〜4, [[IADR-0338]] 決定 1・6:
// pull の一巡。manifest → 差分計算 → 必要な資料だけ pull → Vault へ書く → 同期状態を記録する。
//
// Obsidian への依存はポート（FileStore / SyncStateStore / Hasher / HttpTransport 経由の SyncClient）
// に閉じ込め、Obsidian 実体なしで同じコードを Vitest と Node ハーネス（`cli/pull.ts`）から回す。
//
// 🔴 401（SyncAuthError）はそのまま投げる。**ファイルにも状態にも触らない**——「古いままのファイルが
// 黙って残る」のではなく「失敗した」を呼び出し側が利用者に伝える（受け入れ基準: 期限切れ・失効は
// 利用者に判る形で失敗する）。
import type { Hasher } from './hash.ts';
import {
  planPull,
  probePaths,
  resolveTargets,
  type ConflictCause,
  type SkipReason,
  type SyncState,
} from './pullPlanner.ts';
import type { SyncClient } from './syncClient.ts';
import { SyncNotFoundError } from './types.ts';

export interface FileStore {
  exists(path: string): Promise<boolean>;
  read(path: string): Promise<string>;
  /** 親フォルダが無ければ作ってから書く。 */
  write(path: string, content: string): Promise<void>;
}

export interface SyncStateStore {
  load(): Promise<SyncState>;
  save(state: SyncState): Promise<void>;
}

export interface PullSyncDeps {
  client: SyncClient;
  files: FileStore;
  state: SyncStateStore;
  hasher: Hasher;
  syncFolder: string;
  now: () => Date;
}

export interface PullReport {
  manifestCount: number;
  written: string[];
  adopted: string[];
  upToDate: number;
  conflicts: { localPath: string; cause: ConflictCause }[];
  serverDeleted: number;
  skipped: { vaultPath: string; reason: SkipReason }[];
  pullErrors: { noteId: string; message: string }[];
}

export async function runPullSync(deps: PullSyncDeps): Promise<PullReport> {
  const manifest = await deps.client.getManifest();
  const state = await deps.state.load();

  const targets = resolveTargets(manifest, deps.syncFolder);
  const localHashes = new Map<string, string>();
  for (const path of probePaths(targets)) {
    if (await deps.files.exists(path))
      localHashes.set(path, await deps.hasher(await deps.files.read(path)));
  }
  const actions = planPull(targets, state, localHashes);

  const report: PullReport = {
    manifestCount: manifest.length,
    written: [],
    adopted: [],
    upToDate: 0,
    conflicts: [],
    serverDeleted: 0,
    skipped: [],
    pullErrors: [],
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
          await deps.files.write(action.localPath, pulled.content);
          state[action.noteId] = {
            localPath: action.localPath,
            version: pulled.version,
            contentHash: pulled.contentHash,
            localHash: await deps.hasher(pulled.content),
            syncedAt: deps.now().toISOString(),
          };
          report.written.push(action.localPath);
          break;
        }
        case 'adopt': {
          const localHash = localHashes.get(action.localPath);
          if (localHash === undefined) continue;
          state[action.noteId] = {
            localPath: action.localPath,
            version: action.version,
            contentHash: action.contentHash,
            localHash,
            syncedAt: deps.now().toISOString(),
          };
          report.adopted.push(action.localPath);
          break;
        }
        case 'up-to-date':
          report.upToDate += 1;
          break;
        case 'conflict':
          report.conflicts.push({ localPath: action.localPath, cause: action.cause });
          break;
        case 'server-deleted':
          report.serverDeleted += 1;
          break;
        case 'skipped':
          report.skipped.push({ vaultPath: action.vaultPath, reason: action.reason });
          break;
      }
    }
  } finally {
    // 途中で止まっても、書き終えた分の状態は残す（次回に「ローカル編集あり」と誤認しないため）。
    if (report.written.length > 0 || report.adopted.length > 0) await deps.state.save(state);
  }
  return report;
}
