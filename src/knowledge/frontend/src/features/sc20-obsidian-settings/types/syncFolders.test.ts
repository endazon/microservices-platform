import { describe, it, expect } from 'vitest';
import { canAddFolder, normalizeFolderPath } from './syncFolders';

// SC-20 主要素 3, FR-20, ADR-0037 決定 3: 同期対象フォルダの入力を整える純関数。
//
// 🔴 **ここは検証ではない**（値域の防壁はサーバ側）。固定するのは
// 「送る前に見せる形」と「押せないボタンを押させない」判定だけである。

describe('パスの正規化（表示と送信の形）', () => {
  it.each([
    ['仕事/メモ', '仕事/メモ'],
    ['  仕事/メモ  ', '仕事/メモ'],
    ['/仕事/メモ/', '仕事/メモ'],
    ['///仕事///', '仕事'],
  ])('%s を %s にする', (raw, expected) => {
    expect(normalizeFolderPath(raw)).toBe(expected);
  });

  it('🔴 途中の区切りは潰さない（階層が別物になる）', () => {
    expect(normalizeFolderPath('/仕事//下書き/')).toBe('仕事//下書き');
  });

  it('空白だけの入力は空になる', () => {
    expect(normalizeFolderPath('   ')).toBe('');
  });
});

describe('追加できるかの判定', () => {
  it('空のパスは追加できない', () => {
    expect(canAddFolder('   ', [])).toBe(false);
  });

  it('🔴 表記が違っても同じフォルダなら追加できない（サーバの 400 を先に防ぐ）', () => {
    expect(canAddFolder('/仕事/メモ/', ['仕事/メモ'])).toBe(false);
  });

  it('別のフォルダは追加できる（常に false を返す実装と区別する陽性対照）', () => {
    expect(canAddFolder('仕事/下書き', ['仕事/メモ'])).toBe(true);
  });
});
