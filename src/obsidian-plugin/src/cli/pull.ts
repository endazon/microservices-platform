// FR-20, UC-11, [[IADR-0331]] 決定 6: Obsidian 本体なしで同じ pull を実 HTTP に当てる Node ハーネス。
// 実測（#1098 の証跡）と CI の成果物検査に使う。**トークンは環境変数かファイルから読み、出力に載せない。**
//
//   MSP_SYNC_ENDPOINT   接続先（例: http://127.0.0.1:18093 ← port-forward した DocumentService）
//   MSP_SYNC_TOKEN      同期トークン（または MSP_SYNC_TOKEN_FILE でファイルから）
//   MSP_VAULT_DIR       Vault に見立てるディレクトリ
//   MSP_SYNC_FOLDER     同期フォルダ（既定: 個人資料）
//
// 終了コード: 0 = 完了 / 2 = 認証失敗（401） / 3 = 設定不備 / 1 = その他
import { mkdir, readFile, writeFile, access } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { EndpointError, normalizeEndpoint } from '../protocol/endpoint.ts';
import { sha256Hex } from '../protocol/hash.ts';
import type { SyncState } from '../protocol/pullPlanner.ts';
import { runPullSync, type FileStore, type SyncStateStore } from '../protocol/pullSync.ts';
import { SyncClient } from '../protocol/syncClient.ts';
import { SyncAuthError } from '../protocol/types.ts';
import { nodeFetchTransport } from '../transport/nodeFetchTransport.ts';

const STATE_FILE = '.msp-sync-state.json';

function directoryFileStore(root: string): FileStore {
  const abs = (p: string) => join(root, ...p.split('/'));
  return {
    exists: (p) =>
      access(abs(p)).then(
        () => true,
        () => false,
      ),
    read: (p) => readFile(abs(p), 'utf8'),
    write: async (p, content) => {
      await mkdir(dirname(abs(p)), { recursive: true });
      await writeFile(abs(p), content, 'utf8');
    },
  };
}

function fileStateStore(root: string): SyncStateStore {
  const file = join(root, STATE_FILE);
  return {
    load: async () => {
      try {
        return JSON.parse(await readFile(file, 'utf8')) as SyncState;
      } catch {
        return {};
      }
    },
    save: async (state) => {
      await mkdir(root, { recursive: true });
      await writeFile(file, JSON.stringify(state, null, 2), 'utf8');
    },
  };
}

async function readToken(): Promise<string | null> {
  const direct = process.env.MSP_SYNC_TOKEN?.trim();
  if (direct) return direct;
  const file = process.env.MSP_SYNC_TOKEN_FILE?.trim();
  if (file) return (await readFile(file, 'utf8')).trim();
  return null;
}

async function main(): Promise<number> {
  const vaultDir = process.env.MSP_VAULT_DIR?.trim();
  if (!vaultDir) {
    console.error('MSP_VAULT_DIR が未設定です。');
    return 3;
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

  try {
    const report = await runPullSync({
      client: new SyncClient(nodeFetchTransport, endpoint, token),
      files: directoryFileStore(vaultDir),
      state: fileStateStore(vaultDir),
      hasher: sha256Hex,
      syncFolder: process.env.MSP_SYNC_FOLDER ?? '個人資料',
      now: () => new Date(),
    });
    console.log(JSON.stringify({ endpoint, vaultDir, report }, null, 2));
    return 0;
  } catch (e) {
    if (e instanceof SyncAuthError) {
      console.error(`認証失敗（401）: ${e.message} Vault のファイルは変更していません。`);
      return 2;
    }
    console.error(`失敗: ${e instanceof Error ? e.message : String(e)}`);
    return 1;
  }
}

process.exitCode = await main();
