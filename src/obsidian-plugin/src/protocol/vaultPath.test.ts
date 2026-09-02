import { normalizeFolder, parentFolders, resolveLocalPath } from './vaultPath.ts';

describe('resolveLocalPath', () => {
  // FR-20, ADR-0037 決定 4: 同期フォルダ配下へ落とし、拡張子が無ければ .md を補う（陽性対照）
  it('同期フォルダ配下の相対パスへ落とし、拡張子が無ければ .md を補う', () => {
    expect(resolveLocalPath('個人資料', 'notes/todo.md')).toEqual({
      ok: true,
      path: '個人資料/notes/todo.md',
    });
    expect(resolveLocalPath('個人資料', 'notes/todo')).toEqual({
      ok: true,
      path: '個人資料/notes/todo.md',
    });
    expect(resolveLocalPath('個人資料', 'a.b/c')).toEqual({ ok: true, path: '個人資料/a.b/c.md' });
  });

  // FR-20: 同期フォルダが空なら Vault 直下。区切りは / にそろえる
  it('同期フォルダが空なら Vault 直下に置き、バックスラッシュと重複スラッシュを正規化する', () => {
    expect(resolveLocalPath('', 'x\\y//z.md')).toEqual({ ok: true, path: 'x/y/z.md' });
    expect(resolveLocalPath(' /sync/ ', './memo.md')).toEqual({ ok: true, path: 'sync/memo.md' });
    expect(normalizeFolder('a\\b/./c/')).toBe('a/b/c');
  });

  // FR-20, [[IADR-0338]] 決定 9: Vault の外へ出るパスと制御文字は取り込まない（陰性）
  it('絶対パス・親参照・制御文字・空は理由付きで拒否する', () => {
    expect(resolveLocalPath('個人資料', '/etc/passwd')).toEqual({ ok: false, reason: 'absolute' });
    expect(resolveLocalPath('個人資料', 'C:/Users/x.md')).toEqual({
      ok: false,
      reason: 'absolute',
    });
    expect(resolveLocalPath('個人資料', '../outside.md')).toEqual({
      ok: false,
      reason: 'traversal',
    });
    expect(resolveLocalPath('個人資料', 'a/../../b.md')).toEqual({
      ok: false,
      reason: 'traversal',
    });
    expect(resolveLocalPath('個人資料', 'bad\u0000name.md')).toEqual({
      ok: false,
      reason: 'control-char',
    });
    expect(resolveLocalPath('個人資料', '   ')).toEqual({ ok: false, reason: 'empty' });
    expect(resolveLocalPath('個人資料', './')).toEqual({ ok: false, reason: 'empty' });
  });

  // FR-20: 書き込み前に作る親フォルダの並び
  it('parentFolders は浅い順に親を列挙する', () => {
    expect(parentFolders('a/b/c.md')).toEqual(['a', 'a/b']);
    expect(parentFolders('top.md')).toEqual([]);
  });
});
