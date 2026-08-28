import type { PrivateNoteDto, PrivateNoteUsageDto } from '@foundation/api/generated/bff.schemas';

// SC-19, FR-19, ADR-0037 決定 16〜20: 容量表示と削除済みの残り日数の導出。
//
// **純関数だけを置く。** 描画も問い合わせもしない —— 段階警告の境界（80 / 95 / 100）と
// 残り日数の丸めは、画面を描かずに固定したい規則だからである。

/** 1 GB（表示に使う単位。契約はバイトで運ぶ）。 */
const BYTES_PER_GB = 1024 ** 3;

/**
 * 容量の段階（05_screens §SC-19「保存容量と版履歴」）。
 *
 * 🔴 **段は 1 つしか返さない。** 95% のときに 80% の予告も並べると、
 * 強い警告が弱い警告に埋もれる。
 */
export type QuotaLevel = 'normal' | 'notice' | 'warning' | 'full';

export function quotaLevel(percent: number): QuotaLevel {
  if (percent >= 100) return 'full';
  if (percent >= 95) return 'warning';
  if (percent >= 80) return 'notice';
  return 'normal';
}

/** GB 表記（小数 2 桁）。単位そのものは呼び出し側が文言として添える。 */
export function toGb(bytes: number): string {
  return (bytes / BYTES_PER_GB).toFixed(2);
}

/**
 * 「うち削除済み」の内訳（05_screens §SC-19 主要素 15）。
 *
 * 🔴 **画面が削除済み行の `bytes` を合算して出す**（契約 `PrivateNoteListResponse` の注記）。
 * 後段は台帳の行しか持たず、内訳という項目を持たない ——**数え方を 2 つにしない**ためである。
 */
export function deletedBytes(notes: readonly PrivateNoteDto[]): number {
  return notes.filter((n) => n.deleted).reduce((sum, n) => sum + n.bytes, 0);
}

/** 選択した資料を完全削除したときに解放される容量（確認ダイアログの ③）。 */
export function freedBytesOf(notes: readonly PrivateNoteDto[], ids: readonly string[]): number {
  const selected = new Set(ids);
  return notes.filter((n) => selected.has(n.id)).reduce((sum, n) => sum + n.bytes, 0);
}

/**
 * 完全削除までの残り日数（05_screens §SC-19 主要素 9・13）。
 *
 * **切り上げる** ——「あと 0.4 日」を 0 日と出すと、まだ間に合う資料が「もう手遅れ」に見える。
 * 期限を過ぎている場合は 0 を返す（負の日数は表示しない）。
 * `purgeAt` を持たない行（＝削除済みでない）は `null`。
 */
export function daysUntilPurge(purgeAt: string | null | undefined, now: Date): number | null {
  if (!purgeAt) return null;
  const remainMs = new Date(purgeAt).getTime() - now.getTime();
  if (Number.isNaN(remainMs)) return null;
  return Math.max(0, Math.ceil(remainMs / 86_400_000));
}

/** 残り 7 日以内は警告色にする（05_screens §SC-19 主要素 13）。 */
export function isPurgeImminent(days: number | null): boolean {
  return days !== null && days <= 7;
}

/** 使用率（契約が `percent` を持つので画面は計算し直さない。欠けたときだけ 0 に倒す）。 */
export function usagePercent(usage: PrivateNoteUsageDto | undefined): number {
  return usage?.percent ?? 0;
}
