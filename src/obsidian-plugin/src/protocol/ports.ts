// FR-20, [[IADR-0338]] 決定 6, [[IADR-0352]] 決定 6: プロトコル部が Obsidian に触れないためのポート。
// 実装は `src/obsidian/`（Vault adapter・data.json）と `src/cli/`（fs・JSON ファイル）の 2 系統だけ。
import type { EditJournal } from './editJournal.ts';
import type { SyncState } from './pullPlanner.ts';

export interface FileStore {
  exists(path: string): Promise<boolean>;
  read(path: string): Promise<string>;
  /** 親フォルダが無ければ作ってから書く。 */
  write(path: string, content: string): Promise<void>;
  /** フォルダ配下（再帰）の `.md` ファイルのパス一覧（Vault 相対・`/` 区切り）。フォルダが無ければ空。 */
  list(folder: string): Promise<string[]>;
  /** 復元できる形で消す（Obsidian はローカルのゴミ箱 `.trash`）。無ければ何もしない。 */
  remove(path: string): Promise<void>;
  /** 親フォルダが無ければ作ってから移す。 */
  rename(from: string, to: string): Promise<void>;
}

export interface SyncStateStore {
  load(): Promise<SyncState>;
  save(state: SyncState): Promise<void>;
}

export interface JournalStore {
  load(): Promise<EditJournal>;
  save(journal: EditJournal): Promise<void>;
}
