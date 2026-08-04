import { describe, expect, it } from 'vitest';
import { cn } from './cn';

// IADR-0121 決定 4: cn() は「呼び出し側の className でバリアントを上書きできる」ことを保証する
// 唯一の合成点である。ここが素の join に戻ると、上書きが効かず px-4 と px-2 が両方残る。
describe('cn()', () => {
  it('lets a later Tailwind utility win over an earlier conflicting one', () => {
    expect(cn('px-4', 'px-2')).toBe('px-2');
    expect(cn('text-sm font-medium', 'text-lg')).toBe('font-medium text-lg');
  });

  it('keeps non-conflicting utilities', () => {
    expect(cn('inline-flex', 'items-center')).toBe('inline-flex items-center');
  });

  it('drops falsy values and flattens conditional inputs', () => {
    const disabled = false;
    expect(cn('a', disabled && 'b', undefined, null, ['c', { d: true, e: false }])).toBe('a c d');
  });

  it('returns an empty string when nothing is given', () => {
    expect(cn()).toBe('');
  });
});
