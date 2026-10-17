import { describe, expect, it } from 'vitest';
import type { MessageDescriptor } from '@lingui/core';
import { i18n } from '@foundation/i18n';
import { sourceLabel } from './documentSource';

// SC-05, FR-06（#1923・IADR-0561）: BFF の `DocumentListSources` の鍵（`BffDocumentListFacetTests` と同じ例）の表示。
describe('sourceLabel', () => {
  const translate = (d: MessageDescriptor) => i18n._(d);

  it('鍵を表示名へ写す（名前は訳さない）', () => {
    expect(sourceLabel('ds:共有フォルダ', translate)).toBe('共有フォルダ');
    expect(sourceLabel('col:kabutan', translate)).toBe('収集: kabutan');
    expect(sourceLabel('report', translate)).toBe('確定報告書');
    expect(sourceLabel('manual', translate)).toBe('手入力');
  });
});
