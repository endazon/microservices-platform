import { describe, expect, it } from 'vitest';
import {
  EMPTY_SELECTION,
  SCOPE_AXES,
  selectedCount,
  toAttributeFilters,
  toggleScopeValue,
} from './scopeSelection';

// FR-04, FR-05, SC-01, SC-08, #539: 対象範囲の純粋ロジック。
describe('scope filter (SC-01 / SC-08)', () => {
  // 裁定 Q9: 軸はタグ・部門・プロジェクトの 3 つ。**「フォルダ」は無い**（保留ではなく不採用）。
  it('has exactly the three axes the plan fixed, and no folder', () => {
    expect(SCOPE_AXES).toEqual(['tags', 'department', 'project']);
    expect(SCOPE_AXES).not.toContain('folder');
  });

  it('toggles a value on and off without mutating the original selection', () => {
    const added = toggleScopeValue(EMPTY_SELECTION, 'tags', '経理');
    expect(added.tags).toEqual(['経理']);
    // 元の選択は変更しない（React の state をそのまま渡しても壊れない）。
    expect(EMPTY_SELECTION.tags).toEqual([]);

    const removed = toggleScopeValue(added, 'tags', '経理');
    expect(removed.tags).toEqual([]);
  });

  // 画面はチップを**複数**選ぶ。多値でなければ表現できない（判断 1 の根拠）。
  it('keeps multiple values on the same axis', () => {
    let selection = toggleScopeValue(EMPTY_SELECTION, 'tags', '経理');
    selection = toggleScopeValue(selection, 'tags', '規程');

    expect(selection.tags).toEqual(['経理', '規程']);
    expect(toAttributeFilters(selection)).toEqual({ tags: ['経理', '規程'] });
  });

  it('combines axes into the contract shape', () => {
    let selection = toggleScopeValue(EMPTY_SELECTION, 'tags', '経理');
    selection = toggleScopeValue(selection, 'department', 'finance');

    expect(toAttributeFilters(selection)).toEqual({
      tags: ['経理'],
      department: ['finance'],
    });
  });

  // **空の軸は載せない**——載せると要求を見たときに「絞ったのに効いていない」と読める。
  it('omits axes with no selection', () => {
    const selection = toggleScopeValue(EMPTY_SELECTION, 'tags', '経理');

    expect(toAttributeFilters(selection)).toEqual({ tags: ['経理'] });
    expect(Object.keys(toAttributeFilters(selection)!)).not.toContain('department');
  });

  // **何も選んでいなければ `undefined`。** 空オブジェクトを送ると旧クライアントとの差が
  // 要求の形に現れ、ログの読み手を惑わせる。
  it('returns undefined when nothing is selected', () => {
    expect(toAttributeFilters(EMPTY_SELECTION)).toBeUndefined();
  });

  it('counts the selected values across axes', () => {
    let selection = toggleScopeValue(EMPTY_SELECTION, 'tags', '経理');
    selection = toggleScopeValue(selection, 'tags', '規程');
    selection = toggleScopeValue(selection, 'project', 'apollo');

    expect(selectedCount(selection)).toBe(3);
    expect(selectedCount(EMPTY_SELECTION)).toBe(0);
  });
});
