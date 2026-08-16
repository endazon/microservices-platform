import { describe, it, expect, afterEach } from 'vitest';
import { act } from '@testing-library/react';
import type { NotificationDto } from '@foundation/api/generated/bff.schemas';
import { activate } from '@foundation/i18n';
import { notificationText, notificationTone, notificationToneLabel } from './notificationMessages';

// FR-22, IADR-0215 決定 2 / テスト仕様書 T-02・T-08。
// 文言が**件数・閾値・期限だけ**から組み立てられ、ja / en の双方で成立することを固定する。

function notification(overrides: Partial<NotificationDto>): NotificationDto {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    kind: 'private-note-purge-weekly',
    occurredAt: '2026-08-16T00:00:00Z',
    read: false,
    ...overrides,
  };
}

/** 全 5 種別（契約の enum と同じ順）。 */
const KINDS: NotificationDto['kind'][] = [
  'private-note-purge-weekly',
  'private-note-purge-imminent',
  'private-note-purge-done',
  'storage-quota-warning',
  'sync-token-expiry',
];

afterEach(() => {
  act(() => {
    activate('ja');
  });
});

describe('notificationText (FR-22)', () => {
  // FR-22: 「件数と期限のみ」。件数が文言に現れる。
  it('①-a 週次通知は件数と期限を出す', () => {
    const text = notificationText(
      notification({
        kind: 'private-note-purge-weekly',
        count: 3,
        deadline: '2026-09-01T00:00:00Z',
      }),
    );
    expect(text).toContain('3');
    expect(text).toContain('件');
  });

  // ADR-0037 決定 6: 完全削除の 7 日前の別建て通知。
  it('①-b 7 日前通知は件数と期限を出す', () => {
    const text = notificationText(
      notification({
        kind: 'private-note-purge-imminent',
        count: 2,
        deadline: '2026-08-23T00:00:00Z',
      }),
    );
    expect(text).toContain('2');
  });

  // ①-c は事後通知なので期限を持たない。`deadline` が無くても文言が成立する。
  it('①-c 事後通知は期限が無くても成立する', () => {
    const text = notificationText(notification({ kind: 'private-note-purge-done', count: 5 }));
    expect(text).toContain('5');
    expect(text).not.toContain('—');
  });

  // T-08 / ADR-0037 決定 17: ②は件数も期限も持たず、閾値だけで成立する。
  it('②容量警告は count も deadline も無く、閾値だけで成立する', () => {
    const text = notificationText(
      notification({ kind: 'storage-quota-warning', thresholdPercent: 95 }),
    );
    expect(text).toContain('95');
    // `undefined` / `NaN` / `—` が文字列へ漏れていないこと。
    expect(text).not.toMatch(/undefined|NaN|—/);
  });

  // ADR-0037 決定 18: 同期トークンの期限予告（7 日前）。
  it('③同期トークンの期限予告は件数と期限を出す', () => {
    const text = notificationText(
      notification({ kind: 'sync-token-expiry', count: 1, deadline: '2026-08-23T00:00:00Z' }),
    );
    expect(text).toContain('1');
  });

  // 契約が種別を増やしても共通シェルが壊れないこと（クライアントは別々に配布される）。
  it('未知の種別でも文言が成立する', () => {
    const text = notificationText(
      notification({ kind: 'future-kind' as NotificationDto['kind'], count: 4 }),
    );
    expect(text).toContain('4');
  });

  // ★ FR-22 受け入れ基準: 資料のタイトル・本文・検索語・回答内容が文言へ入る余地が無い。
  // 契約に無いフィールドを詰めても、文言はそれを読まない。
  it('契約に無い値を混ぜても文言には現れない', () => {
    const contaminated = {
      ...notification({ kind: 'private-note-purge-weekly', count: 1 }),
      title: '2026年度 人事評価メモ',
      body: '極秘の本文',
    } as NotificationDto;
    const text = notificationText(contaminated);
    expect(text).not.toContain('人事評価');
    expect(text).not.toContain('極秘');
  });

  // IADR-0125 決定 4: ja / en の双方で文言が非空であること（未翻訳キーは検査器も止める）。
  it('全 5 種別が ja / en の双方で非空の文言を返す', () => {
    for (const locale of ['ja', 'en'] as const) {
      act(() => {
        activate(locale);
      });
      for (const kind of KINDS) {
        const text = notificationText(
          notification({ kind, count: 1, thresholdPercent: 80, deadline: '2026-09-01T00:00:00Z' }),
        );
        expect(text.trim(), `${locale} / ${kind}`).not.toBe('');
      }
    }
  });
});

describe('notificationTone (FR-22 / INDEX 決定 21)', () => {
  it('7 日前通知と同期トークンの期限予告は warning', () => {
    expect(notificationTone(notification({ kind: 'private-note-purge-imminent' }))).toBe('warning');
    expect(notificationTone(notification({ kind: 'sync-token-expiry' }))).toBe('warning');
  });

  // ADR-0037 決定 17: 95% は「新規作成が止まる 100%」の直前なので一段強くする。
  it('容量警告は 95% で danger、80% で warning', () => {
    expect(
      notificationTone(notification({ kind: 'storage-quota-warning', thresholdPercent: 95 })),
    ).toBe('danger');
    expect(
      notificationTone(notification({ kind: 'storage-quota-warning', thresholdPercent: 80 })),
    ).toBe('warning');
    // 閾値が欠けていても弱い側へ倒す（強い側へ倒すと、欠損が「重要」に化ける）。
    expect(notificationTone(notification({ kind: 'storage-quota-warning' }))).toBe('warning');
  });

  it('週次通知と事後通知は neutral', () => {
    expect(notificationTone(notification({ kind: 'private-note-purge-weekly' }))).toBe('neutral');
    expect(notificationTone(notification({ kind: 'private-note-purge-done' }))).toBe('neutral');
  });
});

describe('notificationToneLabel (色だけで意味を持たせない)', () => {
  it('3 つの tone すべてに非空のテキストラベルがある', () => {
    for (const tone of ['neutral', 'warning', 'danger'] as const) {
      expect(notificationToneLabel(tone).trim()).not.toBe('');
    }
  });

  it('tone ごとにラベルが異なる（色を除いても区別できる）', () => {
    const labels = (['neutral', 'warning', 'danger'] as const).map(notificationToneLabel);
    expect(new Set(labels).size).toBe(3);
  });
});
