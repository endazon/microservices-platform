import { describe, it, expect } from 'vitest';
import { SYNC_TONES, syncKeyOf, tagOptionsOf } from './noteBadges';
// [[IADR-0451]] (#1455): 公開範囲の語彙は SC-03 と共有するため `lib/private-notes` へ移した。
// **本書はその移設後も同じ規則（未知は `unknown`・共有は注意色）を固定し続ける。**
import { VISIBILITY_TONES, visibilityKeyOf } from '../../../lib/private-notes';

// SC-19, FR-19/FR-20, ADR-0036 D-06 / ADR-0037 決定 3・4・7: 公開範囲・同期状態の導出（純関数）。
//
// 🔴 **未知の値の扱いを固定するのが本書の要である。** 契約は値を `string` で運ぶ（enum にしない）。
// 「既知の 3 値のどれかに丸める」実装でも、既知の 3 値だけを見るテストは緑になる。

describe('公開範囲の導出（SC-19 主要素 2）', () => {
  it.each([
    ['private', 'private'],
    ['users', 'users'],
    ['groups', 'groups'],
  ])('契約の値 %s をそのまま状態 %s として読む', (raw, expected) => {
    expect(visibilityKeyOf(raw)).toBe(expected);
  });

  it.each(['', 'PRIVATE', 'shared', 'public'])(
    '🔴 未知の値 %s を既知の状態へ丸めない（unknown にする）',
    (raw) => {
      expect(visibilityKeyOf(raw)).toBe('unknown');
    },
  );

  it('🔴 非公開だけが中立で、共有されている状態と判定不能は注意色になる', () => {
    // 非公開を注意色にする実装と区別する陽性対照（中立が 1 つだけ存在する）。
    expect(VISIBILITY_TONES.private).toBe('neutral');
    expect(VISIBILITY_TONES.users).toBe('warning');
    expect(VISIBILITY_TONES.groups).toBe('warning');
    // 判定できないものを「非公開」と同じ見え方にしない（共有されていても気づけなくなる）。
    expect(VISIBILITY_TONES.unknown).not.toBe(VISIBILITY_TONES.private);
  });
});

describe('同期状態の導出（SC-19 主要素 5）', () => {
  it.each([
    ['conflict', 'conflict'],
    ['target', 'target'],
    ['excluded', 'excluded'],
  ])('契約の値 %s をそのまま状態 %s として読む', (raw, expected) => {
    expect(syncKeyOf(raw)).toBe(expected);
  });

  it.each(['', 'synced', 'CONFLICT'])('🔴 未知の値 %s を unknown にする', (raw) => {
    expect(syncKeyOf(raw)).toBe('unknown');
  });

  it('競合だけが注意色、同期対象は正常、対象外は中立である', () => {
    expect(SYNC_TONES.conflict).toBe('warning');
    expect(SYNC_TONES.target).toBe('success');
    expect(SYNC_TONES.excluded).toBe('neutral');
    // 判定不能を「同期対象」と同じ見え方にしない。
    expect(SYNC_TONES.unknown).not.toBe(SYNC_TONES.target);
  });
});

describe('タグ絞り込みの選択肢（SC-19 主要素 6）', () => {
  it('重複を畳み、辞書順に並べる', () => {
    const options = tagOptionsOf([{ tags: ['設計', '議事録'] }, { tags: ['設計'] }]);
    expect(options).toEqual(['議事録', '設計']);
  });

  it('タグを 1 つも持たない一覧では空になる（空の選択肢を並べない）', () => {
    expect(tagOptionsOf([{ tags: [] }])).toEqual([]);
    expect(tagOptionsOf([])).toEqual([]);
  });
});
