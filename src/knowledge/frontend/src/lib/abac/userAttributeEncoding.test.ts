import { describe, expect, it } from 'vitest';
import {
  addUserAttributeValue,
  isSetValuedUserAttribute,
  joinUserAttribute,
  splitUserAttribute,
} from './userAttributeEncoding';

// #2126: 集合値の利用者属性の規則（後段の `UserAttributeEncoding` と同じ）。
describe('userAttributeEncoding', () => {
  it('tags と projects だけが集合値で、大文字小文字を区別しない', () => {
    expect(isSetValuedUserAttribute('tags')).toBe(true);
    expect(isSetValuedUserAttribute('TAGS')).toBe(true);
    expect(isSetValuedUserAttribute('projects')).toBe(true);
    // 🔴 単一値のキーは集合にしない（連結すると階段ポリシーが壊れる）。
    expect(isSetValuedUserAttribute('clearance')).toBe(false);
    expect(isSetValuedUserAttribute('department')).toBe(false);
  });

  it('カンマ・空白・タブで分割し、空と重複を落とし、並びを保つ', () => {
    expect(splitUserAttribute('経営, 経理\t経営,,')).toEqual(['経営', '経理']);
    expect(splitUserAttribute('')).toEqual([]);
    expect(splitUserAttribute(undefined)).toEqual([]);
  });

  it('カンマで連結する', () => {
    expect(joinUserAttribute(['経営', ' 経理 ', ''])).toBe('経営,経理');
  });

  it('集合値のキーは和を取り、単一値のキーは置き換える', () => {
    expect(addUserAttributeValue('tags', '経営', '経理')).toBe('経営,経理');
    expect(addUserAttributeValue('tags', '経営,経理', '経営')).toBe('経営,経理');
    expect(addUserAttributeValue('tags', undefined, '経営')).toBe('経営');
    expect(addUserAttributeValue('clearance', 'public', 'internal')).toBe('internal');
  });
});
