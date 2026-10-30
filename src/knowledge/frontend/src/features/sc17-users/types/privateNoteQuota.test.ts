import { describe, it, expect } from 'vitest';
import { MAX_QUOTA_BYTES, formatQuotaBytes, quotaInputToBytes } from './privateNoteQuota';

// FR-19, NFR-27, SC-17 (#2330): 個人資料の保存容量の上限の入力と表示（0〜1 TB。0 は新しい資料を作れない状態）。
describe('privateNoteQuota', () => {
  it('上限は 1 TB（2^40 バイト）', () => {
    expect(MAX_QUOTA_BYTES).toBe(1024 ** 4);
  });

  it('単位つきの入力をバイトへ直す（0 を許し、1 TB ちょうどまで）', () => {
    expect(quotaInputToBytes('0', 'GB')).toBe(0);
    expect(quotaInputToBytes('1', 'GB')).toBe(1024 ** 3);
    expect(quotaInputToBytes('512', 'MB')).toBe(512 * 1024 ** 2);
    expect(quotaInputToBytes('1', 'TB')).toBe(1024 ** 4);
    expect(quotaInputToBytes('1.5', 'GB')).toBe(Math.round(1.5 * 1024 ** 3));
  });

  it('範囲外・数でない値は null（押させない）', () => {
    expect(quotaInputToBytes('1025', 'GB')).toBeNull();
    expect(quotaInputToBytes('-1', 'GB')).toBeNull();
    expect(quotaInputToBytes('', 'GB')).toBeNull();
    expect(quotaInputToBytes('abc', 'MB')).toBeNull();
  });

  // #2347 のレビュー: `Number()` は指数表記・16 進・前後の空白を数として読む。10 進の数字（小数可）だけを受ける。
  it('指数表記・16 進・空白・符号は受けない（10 進の数字だけ）', () => {
    expect(quotaInputToBytes('1e3', 'MB')).toBeNull();
    expect(quotaInputToBytes('1E-1', 'GB')).toBeNull();
    expect(quotaInputToBytes('0x10', 'MB')).toBeNull();
    expect(quotaInputToBytes(' 1', 'GB')).toBeNull();
    expect(quotaInputToBytes('1 ', 'GB')).toBeNull();
    expect(quotaInputToBytes('1 0', 'GB')).toBeNull();
    expect(quotaInputToBytes('-0', 'GB')).toBeNull();
    expect(quotaInputToBytes('+1', 'GB')).toBeNull();
    expect(quotaInputToBytes('.5', 'GB')).toBeNull();
    expect(quotaInputToBytes('1.', 'GB')).toBeNull();
    // 陽性対照: 小数と 0 は受ける。
    expect(quotaInputToBytes('0.5', 'GB')).toBe(Math.round(0.5 * 1024 ** 3));
    expect(quotaInputToBytes('0', 'TB')).toBe(0);
  });

  it('表示は単位を自動で選ぶ（B / KB / MB / GB / TB）', () => {
    expect(formatQuotaBytes(0)).toBe('0 B');
    expect(formatQuotaBytes(1536)).toBe('1.50 KB');
    expect(formatQuotaBytes(1024 ** 3)).toBe('1.00 GB');
    expect(formatQuotaBytes(1024 ** 4)).toBe('1.00 TB');
  });
});
