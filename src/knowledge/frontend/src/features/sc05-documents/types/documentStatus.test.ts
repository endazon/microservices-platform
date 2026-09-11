import { describe, it, expect } from 'vitest';
import { i18n } from '@foundation/i18n';
import { canPublish, documentStatusView, DOCUMENT_STATUSES } from './documentStatus';

// SC-05, UC-03, FR-06: 公開ライフサイクルの表示写像。描画なしで 4 値の写像そのものを試験する
// （SC-07 `jobStatus.test.ts` と同じ作法）。

describe('documentStatusView (SC-05)', () => {
  it('maps every contract value to a label and a tone', () => {
    for (const status of DOCUMENT_STATUSES) {
      const view = documentStatusView(status);
      expect(typeof view.label === 'string' ? view.label : i18n._(view.label)).not.toBe('');
      expect(['neutral', 'success']).toContain(view.tone);
    }
  });

  it('marks only the published state as success (the others are neutral)', () => {
    expect(documentStatusView('published').tone).toBe('success');
    expect(documentStatusView('draft').tone).toBe('neutral');
    expect(documentStatusView('normalized').tone).toBe('neutral');
    // アーカイブ済みは「異常」ではないので警告色を当てない。
    expect(documentStatusView('archived').tone).toBe('neutral');
  });

  // 契約が 4 値でも、サーバが 5 つ目を返したときに画面が気付ける形にしておく。
  it('passes an unknown status through as the raw value instead of hiding it', () => {
    expect(documentStatusView('quarantined').label).toBe('quarantined');
    expect(documentStatusView('quarantined').tone).toBe('neutral');
  });

  it('allows publishing only from the unpublished states', () => {
    expect(canPublish('draft')).toBe(true);
    expect(canPublish('normalized')).toBe(true);
    expect(canPublish('published')).toBe(false);
    expect(canPublish('archived')).toBe(false);
  });
});
