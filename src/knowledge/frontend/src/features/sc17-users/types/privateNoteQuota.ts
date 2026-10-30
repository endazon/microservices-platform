// FR-19, NFR-27, SC-17 (#2330): 個人資料の保存容量の上限の入力と表示（純関数）。
//
// ■ 上限は **0〜1 TB**（既定 1 GB）。**0 を許す**（計画 2026-10-24 の裁定。0 は「新しい資料を作れない状態」）。
// ■ 値域の正は後段（範囲外は 400）であり、画面は押せない値を押させないためだけに同じ範囲を持つ。
// ■ SC-19 の `formatBytes` は feature の外から使わない（feature 境界。IADR-0262 決定 4）。TB まで要るので本 feature に置く。

const KB = 1024;
const MB = 1024 ** 2;
const GB = 1024 ** 3;
const TB = 1024 ** 4;

/** 上限の最大（1 TB。後段の `PrivateNoteQuota.MaxLimitBytes` と同じ）。 */
export const MAX_QUOTA_BYTES = TB;

export type QuotaUnit = 'MB' | 'GB' | 'TB';

const UNIT_BYTES: Record<QuotaUnit, number> = { MB, GB, TB };

/** 単位つきの入力をバイトへ直す。数でない・負・1 TB 超は null。 */
export function quotaInputToBytes(value: string, unit: QuotaUnit): number | null {
  const trimmed = value.trim();
  if (trimmed === '') return null;
  const n = Number(trimmed);
  if (!Number.isFinite(n) || n < 0) return null;
  const bytes = Math.round(n * UNIT_BYTES[unit]);
  return bytes <= MAX_QUOTA_BYTES ? bytes : null;
}

/** 単位つきのバイト表記（B / KB / MB / GB / TB を自動選択・小数 2 桁）。 */
export function formatQuotaBytes(bytes: number): string {
  if (bytes >= TB) return `${(bytes / TB).toFixed(2)} TB`;
  if (bytes >= GB) return `${(bytes / GB).toFixed(2)} GB`;
  if (bytes >= MB) return `${(bytes / MB).toFixed(2)} MB`;
  if (bytes >= KB) return `${(bytes / KB).toFixed(2)} KB`;
  return `${bytes} B`;
}
