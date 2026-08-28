import { describe, it, expect } from 'vitest';
import type { SyncDeviceDto } from '@foundation/api/generated/bff.schemas';
import { deviceView, EXPIRING_DAYS } from './deviceState';

// SC-20, FR-20, ADR-0037 決定 10〜15: 端末の 4 状態（純関数）。
//
// 🔴 **期限切れと失効を同じ値へ落とさない。** 同じ見え方にすると、同期が既に止まっていることに
// 利用者が気づかない（計画の明記）。両方を陽性対照つきで固定する。

const NOW = new Date('2026-08-28T00:00:00Z');

function device(over: Partial<SyncDeviceDto>): SyncDeviceDto {
  return {
    id: 'd1',
    deviceName: 'MacBook Pro',
    issuedAt: '2026-08-01T00:00:00Z',
    expiresAt: '2026-08-31T00:00:00Z',
    revoked: false,
    lastSyncAt: null,
    active: true,
    ...over,
  };
}

describe('端末の状態（SC-20 主要素 1）', () => {
  it('有効な端末は残り日数つきの active である', () => {
    expect(deviceView(device({ expiresAt: '2026-09-15T00:00:00Z' }), NOW)).toEqual({
      state: 'active',
      daysLeft: 18,
    });
  });

  it('残り 7 日以内は expiring である（8 日は expiring ではない＝境界の両側）', () => {
    expect(deviceView(device({ expiresAt: '2026-09-04T00:00:00Z' }), NOW).state).toBe('expiring');
    expect(deviceView(device({ expiresAt: '2026-09-05T00:00:00Z' }), NOW).state).toBe('active');
    expect(EXPIRING_DAYS).toBe(7);
  });

  it('期限を過ぎた端末は expired であり、残り日数を出さない', () => {
    expect(deviceView(device({ expiresAt: '2026-08-27T00:00:00Z' }), NOW)).toEqual({
      state: 'expired',
      daysLeft: 0,
    });
  });

  it('🔴 失効済みは、期限内でも期限切れでも revoked である（expired と混ざらない）', () => {
    // 期限内で失効（陽性対照: 同じ期限で revoked=false なら active になる）
    expect(
      deviceView(device({ revoked: true, expiresAt: '2026-09-15T00:00:00Z' }), NOW).state,
    ).toBe('revoked');
    expect(
      deviceView(device({ revoked: false, expiresAt: '2026-09-15T00:00:00Z' }), NOW).state,
    ).toBe('active');
    // 期限切れかつ失効 → revoked が優先される（「まだ生きている」ように読ませない）
    expect(
      deviceView(device({ revoked: true, expiresAt: '2026-08-01T00:00:00Z' }), NOW).state,
    ).toBe('revoked');
  });

  it('解釈できない有効期限は expired へ倒す（NaN 日を出さない）', () => {
    expect(deviceView(device({ expiresAt: 'not-a-date' }), NOW)).toEqual({
      state: 'expired',
      daysLeft: 0,
    });
  });
});
