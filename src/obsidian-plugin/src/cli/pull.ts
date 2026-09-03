// FR-20, UC-11, [[IADR-0338]] 決定 6, [[IADR-0352]]: Obsidian 本体なしで同じ同期処理を実 HTTP に当てる
// Node ハーネス。実測（#1098 / #1153 の証跡）と CI の成果物検査に使う。
// **トークンは環境変数かファイルから読み、出力に載せない。**
//
//   node dist/cli.mjs [pull]                       manifest → pull（第 1 段と同じ。既定）
//   node dist/cli.mjs push                         同期フォルダを走査して push / delete を送る
//   node dist/cli.mjs sync                         pull → push
//   node dist/cli.mjs record <path>                Obsidian の保存イベントに相当（journal へ 1 編集を積む）
//   node dist/cli.mjs delete <path>                Obsidian の削除イベントに相当（ファイルも消す）
//   node dist/cli.mjs rename <from> <to>           Obsidian のリネームに相当（ファイルも移す。HTTP は出さない）
//   node dist/cli.mjs move <from> <to>             rename ＋ push（サーバへ名前を伝播する）
//   node dist/cli.mjs resolve <path> local|server|both   競合（409）を非対話で解決する
//
//   MSP_SYNC_ENDPOINT   接続先（例: http://127.0.0.1:18093 ← port-forward した DocumentService）
//   MSP_SYNC_TOKEN      同期トークン（または MSP_SYNC_TOKEN_FILE でファイルから）
//   MSP_VAULT_DIR       Vault に見立てるディレクトリ
//   MSP_SYNC_FOLDER     同期フォルダ（既定: 個人資料）
//   MSP_EDIT_QUIET_MS   「1 編集」の静穏窓（既定 30000。実測で版を刻むときは 0）
//
// 終了コード: 0 = 完了 / 2 = 認証失敗（401） / 3 = 設定不備 / 4 = 競合あり（push / sync） / 1 = その他
import { access, mkdir, readdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import {
  resolveServerDeleted,
  resolveVersionConflict,
  type ConflictChoice,
  type ResolveDeps,
} from '../protocol/conflictResolver.ts';
import {
  EDIT_QUIET_MS,
  emptyJournal,
  readJournal,
  recordDelete,
  recordRename,
  recordSave,
  type EditJournal,
} from '../protocol/editJournal.ts';
import { EndpointError, normalizeEndpoint } from '../protocol/endpoint.ts';
import { sha256Hex } from '../protocol/hash.ts';
import type { FileStore, JournalStore, SyncStateStore } from '../protocol/ports.ts';
import type { SyncState } from '../protocol/pullPlanner.ts';
import { runPullSync } from '../protocol/pullSync.ts';
import { runPushSync } from '../protocol/pushSync.ts';
import { SyncClient } from '../protocol/syncClient.ts';
import { SyncAuthError } from '../protocol/types.ts';
import { isInFolder } from '../protocol/vaultPath.ts';
import { nodeFetchTransport } from '../transport/nodeFetchTransport.ts';

const STATE_FILE = '.msp-sync-state.json';
const JOURNAL_FILE = '.msp-sync-journal.json';

function directoryFileStore(root: string): FileStore {
  const abs = (p: string) => join(root, ...p.split('/'));
  const exists = (p: string) =>
    access(abs(p)).then(
      () => true,
      () => false,
    );
  return {
    exists,
    read: (p) => readFile(abs(p), 'utf8'),
    write: async (p, content) => {
      await mkdir(dirname(abs(p)), { recursive: true });
      await writeFile(abs(p), content, 'utf8');
    },
    list: async (folder) => {
      const base = folder === '' ? root : abs(folder);
      if (!(await exists(folder === '' ? '.' : folder))) return [];
      const entries = await readdir(base, { recursive: true, withFileTypes: true });
      const out: string[] = [];
      for (const e of entries) {
        if (!e.isFile() || !e.name.endsWith('.md')) continue;
        const full = join(e.parentPath ?? e.path, e.name);
        const rel = full.slice(root.length).replace(/\\/g, '/').replace(/^\/+/, '');
        if (rel.split('/').some((s) => s.startsWith('.'))) continue;
        out.push(rel);
      }
      return out.sort();
    },
    remove: (p) => rm(abs(p), { force: true }),
    rename: async (from, to) => {
      await mkdir(dirname(abs(to)), { recursive: true });
      await rename(abs(from), abs(to));
    },
  };
}

function jsonStore<T>(root: string, file: string, empty: () => T, parse: (raw: unknown) => T) {
  const path = join(root, file);
  return {
    load: async (): Promise<T> => {
      try {
        return parse(JSON.parse(await readFile(path, 'utf8')) as unknown);
      } catch {
        return empty();
      }
    },
    save: async (value: T): Promise<void> => {
      await mkdir(root, { recursive: true });
      await writeFile(path, JSON.stringify(value, null, 2), 'utf8');
    },
  };
}

const stateStore = (root: string): SyncStateStore =>
  jsonStore<SyncState>(
    root,
    STATE_FILE,
    () => ({}),
    (raw) => (typeof raw === 'object' && raw !== null ? (raw as SyncState) : {}),
  );
const journalStore = (root: string): JournalStore =>
  jsonStore<EditJournal>(root, JOURNAL_FILE, emptyJournal, readJournal);

async function readToken(): Promise<string | null> {
  const direct = process.env.MSP_SYNC_TOKEN?.trim();
  if (direct) return direct;
  const file = process.env.MSP_SYNC_TOKEN_FILE?.trim();
  if (file) return (await readFile(file, 'utf8')).trim();
  return null;
}

function print(value: unknown): void {
  console.log(JSON.stringify(value, null, 2));
}

async function main(argv: string[]): Promise<number> {
  const command = argv[0] ?? 'pull';
  const vaultDir = process.env.MSP_VAULT_DIR?.trim();
  if (!vaultDir) {
    console.error('MSP_VAULT_DIR が未設定です。');
    return 3;
  }
  const syncFolder = process.env.MSP_SYNC_FOLDER ?? '個人資料';
  const files = directoryFileStore(vaultDir);
  const journal = journalStore(vaultDir);
  const quietMs = Number(process.env.MSP_EDIT_QUIET_MS ?? EDIT_QUIET_MS);

  /** Obsidian のリネームイベント相当（ファイルを移し、journal へ記録する）。HTTP は出さない。 */
  const applyRename = async (from: string, to: string): Promise<void> => {
    const j = await journal.load();
    await files.rename(from, to);
    recordRename(j, from, to, {
      fromInFolder: isInFolder(syncFolder, from),
      toInFolder: isInFolder(syncFolder, to),
    });
    await journal.save(j);
  };

  // ── ローカルだけで完結する副コマンド（Obsidian のイベントに相当。HTTP は出さない） ──
  if (command === 'record' || command === 'delete') {
    const path = argv[1];
    if (!path) return usage();
    const j = await journal.load();
    if (command === 'record') {
      recordSave(j, path, await files.read(path), new Date(), quietMs);
    } else {
      await files.remove(path);
      recordDelete(j, path);
    }
    await journal.save(j);
    print({ command, journal: summarizeJournal(j) });
    return 0;
  }
  if (command === 'rename') {
    const [from, to] = [argv[1], argv[2]];
    if (!from || !to) return usage();
    await applyRename(from, to);
    print({ command, journal: summarizeJournal(await journal.load()) });
    return 0;
  }

  let endpoint: string;
  try {
    endpoint = normalizeEndpoint(process.env.MSP_SYNC_ENDPOINT ?? '');
  } catch (e) {
    console.error(e instanceof EndpointError ? e.message : String(e));
    return 3;
  }
  const token = await readToken();
  if (token === null) {
    console.error('同期トークンが未設定です（MSP_SYNC_TOKEN または MSP_SYNC_TOKEN_FILE）。');
    return 3;
  }
  const deps: ResolveDeps = {
    client: new SyncClient(nodeFetchTransport, endpoint, token),
    files,
    state: stateStore(vaultDir),
    journal,
    hasher: sha256Hex,
    syncFolder,
    now: () => new Date(),
  };

  try {
    switch (command) {
      case 'pull': {
        const report = await runPullSync(deps);
        print({ endpoint, vaultDir, report });
        return 0;
      }
      case 'push': {
        const report = await runPushSync(deps);
        print({ endpoint, vaultDir, report });
        return report.conflicts.length > 0 ? 4 : 0;
      }
      case 'move': {
        // ローカルのリネーム（Obsidian のイベント相当）→ push で名前をサーバへ伝播する。
        const [from, to] = [argv[1], argv[2]];
        if (!from || !to) return usage();
        await applyRename(from, to);
        const report = await runPushSync(deps);
        print({ endpoint, vaultDir, renamed: { from, to }, report });
        return report.conflicts.length > 0 ? 4 : 0;
      }
      case 'sync': {
        const pull = await runPullSync(deps);
        const push = await runPushSync(deps);
        print({ endpoint, vaultDir, pull, push });
        return push.conflicts.length > 0 ? 4 : 0;
      }
      case 'resolve': {
        const [path, choice] = [argv[1], argv[2]];
        if (!path || !isChoice(choice)) return usage();
        const state = await deps.state.load();
        const found = Object.entries(state).find(([, s]) => s.localPath === path);
        if (!found) {
          console.error(`追跡していない資料です: ${path}`);
          return 1;
        }
        const [noteId, tracked] = found;
        const result = tracked.serverDeleted
          ? await resolveServerDeleted(
              deps,
              { noteId, localPath: path },
              choice === 'both' ? 'local' : choice,
            )
          : await resolveVersionConflict(deps, { noteId, localPath: path }, choice);
        print({ endpoint, vaultDir, result });
        return result.kind === 'retry' ? 4 : 0;
      }
      default:
        return usage();
    }
  } catch (e) {
    if (e instanceof SyncAuthError) {
      console.error(`認証失敗（401）: ${e.message} Vault のファイルは変更していません。`);
      return 2;
    }
    console.error(`失敗: ${e instanceof Error ? e.message : String(e)}`);
    return 1;
  }
}

const isChoice = (v: string | undefined): v is ConflictChoice =>
  v === 'local' || v === 'server' || v === 'both';

function usage(): number {
  console.error(
    '使い方: cli.mjs [pull|push|sync] | record <path> | delete <path> | rename <from> <to> | move <from> <to> | resolve <path> local|server|both',
  );
  return 3;
}

function summarizeJournal(j: EditJournal) {
  return {
    edits: Object.fromEntries(Object.entries(j.edits).map(([p, list]) => [p, list.length])),
    deleted: Object.keys(j.deleted),
    movedOut: Object.keys(j.movedOut),
    renamed: j.renamed,
  };
}

process.exitCode = await main(process.argv.slice(2));
