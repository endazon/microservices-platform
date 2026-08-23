import { describe, it, expect } from 'vitest';
import { i18n } from '@lingui/core';
import { attributeLabel, orderedAttributes } from './attributes';

// SC-03, FR-05/FR-06: 属性・タグパネル（05_screens §SC-03 主要素「機密区分・部門・タグ」）。
describe('attributeLabel (SC-03)', () => {
  // P-1 / P-2: 計画が画面ラベルを与えているキーだけを写像する。
  it('maps the attribute keys named by the plan', () => {
    expect(i18n._(attributeLabel('confidentiality')!)).toBe('機密区分');
    expect(i18n._(attributeLabel('department')!)).toBe('部門');
  });

  // P-3: 未知のキーは翻訳しない（勝手な用語定義を作らない）。
  it('leaves unknown keys unmapped', () => {
    expect(attributeLabel('owner')).toBeUndefined();
    expect(attributeLabel('lifecycle')).toBeUndefined();
  });
});

describe('orderedAttributes (SC-03)', () => {
  // 表示順を応答の JSON 順に左右させない（計画の挙げる順を先頭に、残りは辞書順）。
  it('puts the plan-named attributes first and sorts the rest', () => {
    expect(
      orderedAttributes({
        owner: 'u1',
        department: 'accounting',
        confidentiality: 'internal',
        lifecycle: 'active',
      }),
    ).toEqual([
      ['confidentiality', 'internal'],
      ['department', 'accounting'],
      ['lifecycle', 'active'],
      ['owner', 'u1'],
    ]);
  });

  it('skips the plan-named attributes that are absent', () => {
    expect(orderedAttributes({ owner: 'u1' })).toEqual([['owner', 'u1']]);
    expect(orderedAttributes({})).toEqual([]);
  });
});
