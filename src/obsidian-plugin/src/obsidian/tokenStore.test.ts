import { LocalStorageTokenStore, TOKEN_STORAGE_KEY, type LocalStorageLike } from './tokenStore.ts';

class FakeLocalStorage implements LocalStorageLike {
  readonly entries = new Map<string, unknown>();
  loadLocalStorage(key: string): unknown {
    return this.entries.get(key) ?? null;
  }
  saveLocalStorage(key: string, data: unknown | null): void {
    if (data === null) this.entries.delete(key);
    else this.entries.set(key, data);
  }
}

describe('LocalStorageTokenStore', () => {
  // FR-20, ADR-0037 決定 11・13, [[IADR-0331]] 決定 5: トークンは端末ローカルの localStorage に保存し、
  // 読み戻せる（陽性対照）
  it('保存したトークンを端末ローカルのキーから読み戻せる', () => {
    const storage = new FakeLocalStorage();
    const store = new LocalStorageTokenStore(storage);

    expect(store.load()).toBeNull();
    store.save('  tok-abc  ');
    expect(store.load()).toBe('tok-abc');
    expect([...storage.entries.keys()]).toEqual([TOKEN_STORAGE_KEY]);
  });

  // FR-20, ADR-0037 決定 13: 削除すると読めなくなる（失効後の入れ直しの前提）。空は保存しない
  it('削除すると null に戻り、空文字は保存を拒む', () => {
    const storage = new FakeLocalStorage();
    const store = new LocalStorageTokenStore(storage);
    store.save('tok');
    store.clear();
    expect(store.load()).toBeNull();
    expect(storage.entries.size).toBe(0);
    expect(() => store.save('   ')).toThrow(/空/);
    // 文字列でない値が入っていても「未設定」と読む
    storage.saveLocalStorage(TOKEN_STORAGE_KEY, { token: 'x' });
    expect(store.load()).toBeNull();
  });
});
