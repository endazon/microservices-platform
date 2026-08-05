import { describe, it, expect } from 'vitest';
import { i18n } from '@foundation/i18n';
import { isRetryable, jobStatusView, JOB_STATUSES } from './jobStatus';

// SC-07, UC-06, FR-12: ジョブ状態の写像（純関数）。
// 05_screens §SC-07 §データソース（2026-08-04 確定）の 4 状態モデルをそのまま固定する。

describe('jobStatus (SC-07)', () => {
  // 計画確定: ジョブ状態モデルは 4 値である（queued / processing / succeeded / failed）。
  it('covers exactly the four statuses the plan fixed', () => {
    expect([...JOB_STATUSES]).toEqual(['queued', 'processing', 'succeeded', 'failed']);
  });

  // INDEX 決定 21: 色だけで意味を持たせない。tone とテキストが必ず対で決まる。
  it.each([
    ['queued', '待機中', 'neutral'],
    ['processing', '変換中', 'neutral'],
    ['succeeded', '完了', 'success'],
    ['failed', '失敗', 'danger'],
  ])('maps %s to a labelled badge', (status, label, tone) => {
    const view = jobStatusView(status);
    expect(typeof view.label === 'string' ? view.label : i18n._(view.label)).toBe(label);
    expect(view.tone).toBe(tone);
  });

  // 未知の値を握り潰さない（`—` や「不明」へ丸めない）。サーバが 5 つ目を返したら画面で気付ける。
  it('shows an unknown status verbatim instead of hiding it', () => {
    const view = jobStatusView('quarantined');
    expect(view.label).toBe('quarantined');
    expect(view.tone).toBe('neutral');
  });

  // 計画確定: 同一ジョブの再変換は直列化し、実行中（processing）の要求は拒否する。
  // 画面は failed のときだけ操作を出す（サーバも failed 以外を 409 で拒否する）。
  it('allows retry only for failed jobs', () => {
    expect(isRetryable('failed')).toBe(true);
    expect(isRetryable('processing')).toBe(false);
    expect(isRetryable('queued')).toBe(false);
    expect(isRetryable('succeeded')).toBe(false);
  });
});
