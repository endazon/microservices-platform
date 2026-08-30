import { describe, expect, it } from 'vitest';
import {
  KIND_OPTIONS,
  STATE_OPTIONS,
  edgeTypeNameMap,
  normalizeAiSuggestionSearch,
  suggestionTone,
} from './suggestionVocabulary';

// SC-21, UC-10, FR-18: 語彙と写像を**画面を描かずに**固定する。
// 05_screens §SC-21 の入力/バリデーションが定めた既定と丸めの規則が、ここの対象である。

describe('normalizeAiSuggestionSearch', () => {
  it('URL に何も無ければ既定（state=pending / kind=all）へ倒す', () => {
    expect(normalizeAiSuggestionSearch({})).toEqual({ state: 'pending', kind: 'all' });
  });

  it('未知の値はエラーにせず既定へ丸める（手打ちの URL で画面を壊さない）', () => {
    expect(normalizeAiSuggestionSearch({ state: 'maybe', kind: 'image' })).toEqual({
      state: 'pending',
      kind: 'all',
    });
  });

  it('選択肢に在る値はそのまま通す', () => {
    for (const state of STATE_OPTIONS) {
      for (const kind of KIND_OPTIONS) {
        expect(normalizeAiSuggestionSearch({ state, kind })).toEqual({ state, kind });
      }
    }
  });

  it('文字列以外の値も既定へ倒す', () => {
    expect(normalizeAiSuggestionSearch({ state: 1, kind: null })).toEqual({
      state: 'pending',
      kind: 'all',
    });
  });
});

describe('suggestionTone', () => {
  it('承認済みと却下を別の色に分ける', () => {
    expect(suggestionTone('approved')).toBe('success');
    expect(suggestionTone('rejected')).toBe('danger');
  });

  it('承認待ちと未知の状態は neutral（色で新しい意味を作らない）', () => {
    expect(suggestionTone('pending')).toBe('neutral');
    expect(suggestionTone('unknown-state-from-server')).toBe('neutral');
  });
});

describe('edgeTypeNameMap', () => {
  it('ID から表示名を引ける（改名に追随させるための辞書）', () => {
    const map = edgeTypeNameMap([
      { id: 'a', name: 'cites', layer: 'core', isSymmetric: false },
      { id: 'b', name: 'related', layer: 'core', isSymmetric: true },
    ]);
    expect(map.get('a')).toBe('cites');
    expect(map.get('b')).toBe('related');
  });

  it('辞書が空でも引けない ID を返すだけで壊れない', () => {
    expect(edgeTypeNameMap([]).get('a')).toBeUndefined();
  });
});
