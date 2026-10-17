import { describe, expect, it } from 'vitest';
import type { MessageDescriptor } from '@lingui/core';
import { i18n } from '@foundation/i18n';
import {
  documentSourceKey,
  linkableOriginalUri,
  originCounts,
  sourceLabel,
} from './documentSource';

// SC-05, FR-06（#1923・IADR-0561）: BFF の `DocumentListSources.KeyOf` と同じ例（`BffDocumentListFacetTests`）で固定する。
describe('documentSourceKey', () => {
  const collected = { kind: 'News', source: 'kabutan', project: 'ai-stock-trading' };

  it('データソース → 収集 → 報告書 → 手入力の順で 1 つに決まる', () => {
    expect(documentSourceKey({ dataSourceName: '共有フォルダ', attributes: collected })).toBe(
      'ds:共有フォルダ',
    );
    expect(documentSourceKey({ attributes: collected })).toBe('col:kabutan');
    expect(documentSourceKey({ attributes: { kind: 'Daily', periodKey: '2026-10-01' } })).toBe(
      'report',
    );
    expect(documentSourceKey({ attributes: { kind: 'Daily' } })).toBe('manual');
    expect(documentSourceKey({ attributes: {} })).toBe('manual');
    expect(documentSourceKey({ dataSourceName: '  ', attributes: { source: ' ' } })).toBe('manual');
  });
});

describe('documentSourceKey の上限', () => {
  it('長い出典名は BFF と同じ上限（300）で切り、サロゲートペアは割らない', () => {
    expect(documentSourceKey({ attributes: { source: 's'.repeat(500) } })).toHaveLength(300);
    const key = documentSourceKey({ attributes: { source: 'a'.repeat(295) + '😀😀' } });
    expect(key).toHaveLength(299);
    expect(key.endsWith('a')).toBe(true);
  });
});

describe('sourceLabel / originCounts / linkableOriginalUri', () => {
  const translate = (d: MessageDescriptor) => i18n._(d);

  it('鍵を表示名へ写す（名前は訳さない）', () => {
    expect(sourceLabel('ds:共有フォルダ', translate)).toBe('共有フォルダ');
    expect(sourceLabel('col:kabutan', translate)).toBe('収集: kabutan');
    expect(sourceLabel('report', translate)).toBe('確定報告書');
    expect(sourceLabel('manual', translate)).toBe('手入力');
  });

  it('タブの件数は収集とそれ以外の和', () => {
    expect(originCounts({ 'col:a': 2, 'col:b': 1, report: 1, manual: 3, 'ds:x': 1 })).toEqual({
      collected: 3,
      curated: 5,
      all: 8,
    });
    expect(originCounts(undefined)).toBeUndefined();
  });

  it('http(s) だけをリンクにする', () => {
    expect(linkableOriginalUri('https://example.com/a')).toBe('https://example.com/a');
    expect(linkableOriginalUri('HTTP://example.com/a')).toBe('HTTP://example.com/a');
    expect(linkableOriginalUri('storage://b/k.md')).toBeNull();
    expect(linkableOriginalUri('javascript:alert(1)')).toBeNull();
    expect(linkableOriginalUri(null)).toBeNull();
  });
});
