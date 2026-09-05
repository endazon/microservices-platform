import { describe, it, expect } from 'vitest';
import { i18n } from '@foundation/i18n';
import {
  hasRetainedFigures,
  isBodyAbsent,
  isCorrectable,
  isRetryable,
  jobStatusView,
  JOB_STATUSES,
} from './jobStatus';

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

  // #651 / IADR-0154: 縮退・補正の表示は**導出**であり、`status` の 5 値目にしない。
  // 上の「4 値ちょうど」の試験と対で、**契約が増えても状態モデルが増えていない**ことを固定する。
  it('derives the retained-figure marker without adding a fifth status', () => {
    expect(hasRetainedFigures({ diagramsRetained: 2 })).toBe(true);
    expect(hasRetainedFigures({ diagramsRetained: 0 })).toBe(false);
    // **フィールド自体が無い場合**（古いサーバ・契約違反の応答）は「縮退なし」へ倒す。
    // ★ #658 / IADR-0162: **契約では `diagramsRetained` は required である**（従前は任意だった）。
    // それでも画面側は省略に耐える形を保つ —— IADR-0132 決定 3 のとおり
    // 「**契約上は必須**」と「**実行時に必ず来る**」は別であり、本文を検証する層は無い。
    // ここを undefined のまま比較すると `undefined > 0` が false になるのに依存した暗黙の挙動になる。
    expect(hasRetainedFigures({})).toBe(false);
  });

  // 補正の対象は**縮退した図があること**で決まる。権限は混ぜない——混ぜると画面が
  // 「対象が無い」と「権限が無い」を区別できず、理由の文言を選べなくなる（IADR-0127 決定 1）。
  it('offers correction exactly when there are retained figures', () => {
    expect(isCorrectable({ diagramsRetained: 1 })).toBe(true);
    expect(isCorrectable({ diagramsRetained: 0 })).toBe(false);
    expect(isCorrectable({})).toBe(false);
  });

  // #1192 / 計画 ADR「PDF の本文抽出は pandoc の外に置く」決定 3: 「本文なしで完了」は導出であり、
  // `status` の 5 値目にしない。テキスト層の無い PDF は `succeeded` ＋ `hasBody: false` で表す。
  it('derives the body-absent marker without adding a fifth status', () => {
    expect(isBodyAbsent({ hasBody: false })).toBe(true);
    expect(isBodyAbsent({ hasBody: true })).toBe(false);
    // フィールド自体が無い応答（古いサーバ）は「本文あり」へ倒す（`hasRetainedFigures` と同じ防御）。
    expect(isBodyAbsent({})).toBe(false);
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
