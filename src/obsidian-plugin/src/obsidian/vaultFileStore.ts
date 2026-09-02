// FR-20, [[IADR-0331]] 決定 6: Vault の DataAdapter を FileStore に写す。
// `vault.create` / `modify` ではなく adapter を使うのは、TFile の有無で分岐せずに
// 「無ければ作る・あれば上書く」を 1 本にするため（書くかどうかの判断は pullPlanner が済ませている）。
import { normalizePath, type DataAdapter } from 'obsidian';
import type { FileStore } from '../protocol/pullSync.ts';
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
    for (const folder of parentFolders(path)) {
      const normalized = normalizePath(folder);
      if (!(await this.adapter.exists(normalized))) await this.adapter.mkdir(normalized);
    }
    await this.adapter.write(normalizePath(path), content);
  }
}
