import { describe, it, expect } from 'vitest';
import type { PrivateNoteDto } from '@foundation/api/generated/bff.schemas';
import {
  daysUntilPurge,
  deletedBytes,
  freedBytesOf,
  isPurgeImminent,
  quotaLevel,
  toGb,
  usagePercent,
} from './quota';

// SC-19, FR-19, ADR-0037 決定 16〜20: 容量表示と残り日数の規則（純関数）。
//
// 🔴 **境界は両側を置く。** 「80% で警告が出る」だけを書くと、常に警告を出す実装でも緑になる。

const GB = 1024 ** 3;

function note(over: Partial<PrivateNoteDto>): PrivateNoteDto {
  return {
    id: 'id',
    title: 'title',
    vaultPath: 'a.md',
    version: 1,
    bytes: 0,
    contentHash: null,
    includeInSearch: false,
    includeInGraph: false,
    includeInAi: false,
    deleted: false,
    deletedAt: null,
    purgeAt: null,
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:00:00Z',
    ...over,
  };
}

describe('容量の段階（SC-19 / ADR-0037 決定 17）', () => {
  it.each([
    [0, 'normal'],
    [79.9, 'normal'],
    [80, 'notice'],
    [94.9, 'notice'],
    [95, 'warning'],
    [99.9, 'warning'],
    [100, 'full'],
    [120, 'full'],
  ])('使用率 %s%% は %s である', (percent, expected) => {
    expect(quotaLevel(percent)).toBe(expected);
  });

  it('段は 1 つしか返らない（95% のときに notice を兼ねない）', () => {
    // 強い警告が弱い警告に埋もれないことの回帰ガード。
    expect(quotaLevel(96)).toBe('warning');
    expect(quotaLevel(96)).not.toBe('notice');
  });

  it('容量が届いていないときは 0% として扱う（警告を出さない）', () => {
    expect(usagePercent(undefined)).toBe(0);
    expect(usagePercent({ usedBytes: 1, limitBytes: 2, percent: 50 })).toBe(50);
  });
});

describe('「うち削除済み」の内訳（SC-19 主要素 15）', () => {
  it('削除済み行の bytes だけを合算する（陽性対照つき）', () => {
    const notes = [
      note({ id: 'a', bytes: 100, deleted: false }),
      note({ id: 'b', bytes: 200, deleted: true }),
      note({ id: 'c', bytes: 400, deleted: true }),
    ];
    // 陽性対照: 削除済みが 2 件ある（0 を返す実装では区別できない）。
    expect(notes.filter((n) => n.deleted)).toHaveLength(2);
    expect(deletedBytes(notes)).toBe(600);
  });

  it('削除済みが 1 件も無ければ 0 である', () => {
    expect(deletedBytes([note({ bytes: 999 })])).toBe(0);
  });
});

describe('完全削除で解放される容量（SC-19 確認ダイアログ ③）', () => {
  it('選択した資料の bytes だけを合算する', () => {
    const notes = [
      note({ id: 'a', bytes: 100 }),
      note({ id: 'b', bytes: 200 }),
      note({ id: 'c', bytes: 400 }),
    ];
    expect(freedBytesOf(notes, ['a', 'c'])).toBe(500);
    // 陰性: 選ばれていない資料は入らない。
    expect(freedBytesOf(notes, [])).toBe(0);
  });
});

describe('完全削除までの残り日数（SC-19 主要素 9・13）', () => {
  const now = new Date('2026-08-28T00:00:00Z');

  it('切り上げる（あと 0.4 日を 0 日と出さない）', () => {
    expect(daysUntilPurge('2026-08-28T09:36:00Z', now)).toBe(1);
  });

  it('期限を過ぎていても負の日数を出さない', () => {
    expect(daysUntilPurge('2026-08-20T00:00:00Z', now)).toBe(0);
  });

  it('purgeAt が無い行（削除済みでない）は null である', () => {
    expect(daysUntilPurge(null, now)).toBeNull();
    expect(daysUntilPurge(undefined, now)).toBeNull();
  });

  it('解釈できない値は null へ倒す（NaN 日を出さない）', () => {
    expect(daysUntilPurge('not-a-date', now)).toBeNull();
  });

  it.each([
    [1, true],
    [7, true],
    [8, false],
    [90, false],
  ])('残り %s 日の警告色は %s である', (days, expected) => {
    expect(isPurgeImminent(days)).toBe(expected);
  });

  it('残り日数が無い行は警告色にしない', () => {
    expect(isPurgeImminent(null)).toBe(false);
  });
});

describe('GB 表記', () => {
  it('小数 2 桁で丸める', () => {
    expect(toGb(0)).toBe('0.00');
    expect(toGb(GB)).toBe('1.00');
    expect(toGb(GB * 0.804)).toBe('0.80');
  });
});
