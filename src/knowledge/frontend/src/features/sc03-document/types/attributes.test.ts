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
  // 表示順を応答の JSON 順に左右させない（計画の挙げる順で出す）。
  it('renders the plan-named attributes in the planned order', () => {
    expect(
      orderedAttributes({
        department: 'accounting',
        confidentiality: 'internal',
      }),
    ).toEqual([
      ['confidentiality', 'internal'],
      ['department', 'accounting'],
    ]);
  });

  // 🔴 SC-03 §主要素・計画 ADR-0102 決定 4 (#1455): **既知のキーだけを描く。**
  // 従前は未知のキーも生値で出しており、個人資料では `owner`（利用者名）・`doc_scope`・
  // 露出 3 トグルが読める者すべてに出ていた。
  it('drops the keys the plan does not name (owner / doc_scope / exposure toggles)', () => {
    expect(
      orderedAttributes({
        owner: 'tanaka',
        doc_scope: 'private-note',
        include_in_search: 'excluded',
        include_in_graph: 'excluded',
        include_in_ai: 'excluded',
        confidentiality: 'restricted',
      }),
    ).toEqual([['confidentiality', 'restricted']]);
  });

  it('skips the plan-named attributes that are absent', () => {
    expect(orderedAttributes({ owner: 'u1' })).toEqual([]);
    expect(orderedAttributes({})).toEqual([]);
  });
});
