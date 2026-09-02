// FR-20, [[IADR-0338]] 決定 6, [[IADR-0352]] 決定 6: Vault の DataAdapter を FileStore に写す。
// `vault.create` / `modify` ではなく adapter を使うのは、TFile の有無で分岐せずに
// 「無ければ作る・あれば上書く」を 1 本にするため（書くかどうかの判断は planner が済ませている）。
//
// - `remove` は **ローカルのゴミ箱（`.trash`）へ移す**（`trashLocal`）。プラグインが消す向きの操作は
//   「サーバを採用」と「サーバ側リネームの旧パス」だけで、いずれも利用者が復元できる形に留める。
// - `list` は同期フォルダ配下の `.md` を再帰で集める（push の走査）。フォルダが無ければ空。
import { normalizePath, type DataAdapter } from 'obsidian';
import type { FileStore } from '../protocol/ports.ts';
import { parentFolders } from '../protocol/vaultPath.ts';

export class VaultFileStore implements FileStore {
  constructor(private readonly adapter: DataAdapter) {}

  exists(path: string): Promise<boolean> {
    return this.adapter.exists(normalizePath(path));
  }

  read(path: string): Promise<string> {
    return this.adapter.read(normalizePath(path));
  }

  async write(path: string, content: string): Promise<void> {
    await this.ensureParents(path);
    await this.adapter.write(normalizePath(path), content);
  }

  async list(folder: string): Promise<string[]> {
    const root = folder === '' ? '/' : normalizePath(folder);
    if (folder !== '' && !(await this.adapter.exists(root))) return [];
    const out: string[] = [];
    const walk = async (dir: string): Promise<void> => {
      const listed = await this.adapter.list(dir);
      for (const file of listed.files) if (file.endsWith('.md')) out.push(file);
      for (const sub of listed.folders) {
        // Obsidian の設定・ゴミ箱は同期対象にしない。
        const name = sub.split('/').pop() ?? sub;
        if (name.startsWith('.')) continue;
        await walk(sub);
      }
    };
    await walk(root);
    return out.sort();
  }

  async remove(path: string): Promise<void> {
    const normalized = normalizePath(path);
    if (!(await this.adapter.exists(normalized))) return;
    await this.adapter.trashLocal(normalized);
  }

  async rename(from: string, to: string): Promise<void> {
    await this.ensureParents(to);
    await this.adapter.rename(normalizePath(from), normalizePath(to));
  }

  private async ensureParents(path: string): Promise<void> {
    for (const folder of parentFolders(path)) {
      const normalized = normalizePath(folder);
      if (!(await this.adapter.exists(normalized))) await this.adapter.mkdir(normalized);
    }
  }
}
