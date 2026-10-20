import { describe, expect, it } from 'vitest';
import { i18n } from '@foundation/i18n';
import { documentIndexStatusView } from './documentIndexStatus';

// SC-05, FR-06 (#1993): 索引の状態の表示写像。
describe('documentIndexStatusView', () => {
  const id = '00000000-0000-0000-0000-000000000001';

  it.each([
    ['indexed', '索引済み', 'success'],
    ['embedding_pending', '埋め込み待ち', 'warning'],
    ['not_indexed', '未索引', 'neutral'],
    ['unknown', '不明', 'neutral'],
  ])('%s は %s（%s）', (status, label, tone) => {
    const view = documentIndexStatusView({ [id]: status }, id);
    expect(typeof view.label === 'string' ? view.label : i18n._(view.label)).toBe(label);
    expect(view.tone).toBe(tone);
  });

  it('鍵が無い文書（旧い応答）は「不明」で、未索引へ倒さない', () => {
    const view = documentIndexStatusView({}, id);
    expect(typeof view.label === 'string' ? view.label : i18n._(view.label)).toBe('不明');
    expect(documentIndexStatusView(undefined, id).tone).toBe('neutral');
  });

  it('未知の値は生値を出す', () => {
    expect(documentIndexStatusView({ [id]: 'reindexing' }, id)).toEqual({
      label: 'reindexing',
      tone: 'neutral',
    });
  });
});
