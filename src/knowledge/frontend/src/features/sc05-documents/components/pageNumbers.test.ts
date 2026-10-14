import { describe, it, expect } from 'vitest';
import { pageNumbers } from './pageNumbers';

// SC-05, FR-06, IADR-0537（#1923 段 1）: 表示するページ番号の並び（先頭・末尾・現在の前後 1 つ。間は省略）。
describe('pageNumbers', () => {
  it.each([
    [1, 1, [1]],
    [2, 3, [1, 2, 3]],
    [1, 10, [1, 2, 'gap', 10]],
    [5, 10, [1, 'gap', 4, 5, 6, 'gap', 10]],
    [10, 10, [1, 'gap', 9, 10]],
    [3, 5, [1, 2, 3, 4, 5]],
  ])('current %i of %i pages', (current, total, expected) => {
    expect(pageNumbers(current, total)).toEqual(expected);
  });
});
