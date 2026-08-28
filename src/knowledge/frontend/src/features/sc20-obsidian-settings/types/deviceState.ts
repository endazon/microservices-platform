import type { SyncDeviceDto } from '@foundation/api/generated/bff.schemas';

// SC-20, FR-20, ADR-0037 決定 10〜15: 端末の状態の導出（05_screens §SC-20 主要素 1・2）。
//
// **純関数だけを置く。** 4 状態の境界（残り 7 日・期限切れ）は、画面を描かずに固定したい規則である。

/**
 * 端末の状態（05_screens §SC-20 主要素 1 の 4 値）。
 *
 * 🔴 **`expired` と `revoked` を同じ見え方にしない。**
 * 期限切れは「同期が既に停止している」ことを意味し、失効は本人が能動的に止めた結果である。
 * 同じ列で連続的に見せると、利用者が停止に気づかない（計画の明記）。
 */
export type DeviceState = 'active' | 'expiring' | 'expired' | 'revoked';

export interface DeviceView {
  state: DeviceState;
  /** 有効期限までの残り日数。期限切れ・失効では 0。 */
  daysLeft: number;
}

/** 期限切れ間近と判定する残り日数（通知を出す状態と一致させる。05_screens §SC-20）。 */
export const EXPIRING_DAYS = 7;

/**
 * 端末 1 件を 4 状態へ落とす。
 *
 * **失効を最優先で判定する** —— 失効済みの端末に「期限切れ間近」を出すと、
 * まだ生きているように読める。
 * 残り日数は**切り上げる**（「あと 0.4 日」を 0 日と出すと、まだ使える端末が切れて見える）。
 */
export function deviceView(device: SyncDeviceDto, now: Date): DeviceView {
  if (device.revoked) return { state: 'revoked', daysLeft: 0 };

  const remainMs = new Date(device.expiresAt).getTime() - now.getTime();
  if (Number.isNaN(remainMs) || remainMs <= 0) return { state: 'expired', daysLeft: 0 };

  const daysLeft = Math.ceil(remainMs / 86_400_000);
  return { state: daysLeft <= EXPIRING_DAYS ? 'expiring' : 'active', daysLeft };
}
