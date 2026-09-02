// FR-20, ADR-0037 決定 10〜13・15, 08_data-egress-policy 許容条件 2・5・6, [[IADR-0331]] 決定 5:
// 同期トークンの保管。
//
// 🔴 **`data.json`（プラグイン設定）には置かない。** `data.json` は Vault の一部であり、Obsidian Sync や
// git で**別の端末へ複製される**。同期トークンは**端末ごとに発行し端末ごとに失効する**資格情報
// （決定 11・13）なので、Vault と一緒に運ばれる置き場は設計と矛盾する。
// Obsidian の `app.saveLocalStorage` は端末ローカル（Vault 固有の localStorage）で、Vault の
// ファイルには入らない。暗号化はしていない——OS のキーチェーンはプラグイン API から届かない
// （棄却案は IADR-0331）。**平文の露出は「その端末のプロファイル内」に閉じる。**
//
// 一度保存したトークンを画面へ戻さない（SC-20「再表示できない」と同じ規律）。
export interface LocalStorageLike {
  loadLocalStorage(key: string): unknown;
  saveLocalStorage(key: string, data: unknown | null): void;
}

export interface TokenStore {
  load(): string | null;
  save(token: string): void;
  clear(): void;
}

export const TOKEN_STORAGE_KEY = 'msp-private-notes-sync/sync-token';

export class LocalStorageTokenStore implements TokenStore {
  constructor(
    private readonly storage: LocalStorageLike,
    private readonly key: string = TOKEN_STORAGE_KEY,
  ) {}

  load(): string | null {
    const value = this.storage.loadLocalStorage(this.key);
    return typeof value === 'string' && value.trim() !== '' ? value : null;
  }

  save(token: string): void {
    const trimmed = token.trim();
    if (trimmed === '') throw new Error('空のトークンは保存できません。');
    this.storage.saveLocalStorage(this.key, trimmed);
  }

  clear(): void {
    this.storage.saveLocalStorage(this.key, null);
  }
}
