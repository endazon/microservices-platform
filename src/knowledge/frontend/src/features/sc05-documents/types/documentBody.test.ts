import { describe, expect, it } from 'vitest';
import { documentBodyState } from './documentBody';

// SC-05, FR-06, ADR-0070 決定 3（#1923 段 2）: 本文の状態は台帳の 2 項目だけから導く。
describe('documentBodyState', () => {
  it('hasBody=false は参照の有無に依らず「本文なし（原本を参照）」', () => {
    expect(documentBodyState({ hasBody: false, markdownUri: 'storage://b/k.md' })).toBe('none');
    expect(documentBodyState({ hasBody: false, markdownUri: null })).toBe('none');
  });

  it('本文の参照が在れば「本文あり」、無ければ「本文未設定」', () => {
    expect(documentBodyState({ hasBody: true, markdownUri: 'storage://b/k.md' })).toBe('present');
    expect(documentBodyState({ hasBody: true, markdownUri: null })).toBe('unset');
    expect(documentBodyState({ hasBody: true, markdownUri: '' })).toBe('unset');
  });

  it('hasBody を持たない応答は「本文あり」側として読む（DTO の既定と同じ向き）', () => {
    expect(documentBodyState({ markdownUri: 'storage://b/k.md' })).toBe('present');
  });
});
