import type { StatusBadgeProps } from '@platform/ui';

// SC-19, FR-19/FR-20, ADR-0036 D-06 / ADR-0037 決定 3・4・7: 公開範囲（主要素 2）と
// 同期状態（主要素 5）を状態バッジへ落とす導出。
//
// **純関数だけを置く。** 描画も問い合わせもしない —— 値と見え方の対応は、画面を描かずに
// 固定したい規則だからである（`quota.ts` と同じ作法）。
//
// 🔴 **契約は値を `string` で運ぶ（enum にしない。IADR-0131）。** よって未知の値が届きうる。
// **未知を既知の値へ丸めない** —— とりわけ公開範囲を `private` へ丸めると、
// 実際は共有されている資料が「非公開」と読める。**最悪の誤りはそれである**ので、
// 未知は `unknown`（「判定できません」）という別の状態として描く。
//
// 🔴 **色は「他人から見えるか」「同期が滞っていないか」だけを表す。** 個人指定とグループ指定は
// 同じ tone であり、両者を区別するのは**文言と件数**である（色だけで意味を持たせない。INDEX 決定 21）。

/** 状態バッジの色。部品側の値域をそのまま借りる（増減が型で伝わる）。 */
export type BadgeTone = NonNullable<StatusBadgeProps['tone']>;

/** 公開範囲の 3 状態 ＋ 判定不能（05_screens §SC-19 主要素 2）。 */
export type VisibilityKey = 'private' | 'users' | 'groups' | 'unknown';

/** 同期状態の 3 状態 ＋ 判定不能（同 主要素 5）。 */
export type SyncKey = 'conflict' | 'target' | 'excluded' | 'unknown';

const VISIBILITY_KEYS: readonly VisibilityKey[] = ['private', 'users', 'groups'];
const SYNC_KEYS: readonly SyncKey[] = ['conflict', 'target', 'excluded'];

/** 契約の文字列を公開範囲の状態へ落とす。既知の 3 値以外は `unknown`。 */
export function visibilityKeyOf(raw: string): VisibilityKey {
  return VISIBILITY_KEYS.find((k) => k === raw) ?? 'unknown';
}

/** 契約の文字列を同期状態へ落とす。既知の 3 値以外は `unknown`。 */
export function syncKeyOf(raw: string): SyncKey {
  return SYNC_KEYS.find((k) => k === raw) ?? 'unknown';
}

/**
 * 公開範囲の色。**非公開だけが中立**で、共有されている状態（個人指定・グループ指定）と
 * 判定できない状態はいずれも注意色にする —— 画面の眼目は「この資料が他人から見えるか」である。
 */
export const VISIBILITY_TONES: Record<VisibilityKey, BadgeTone> = {
  private: 'neutral',
  users: 'warning',
  groups: 'warning',
  unknown: 'warning',
};

/**
 * 同期状態の色。競合は利用者の操作を待っている状態なので注意色、同期対象は正常、
 * 対象外は中立（異常ではない）。判定できない場合は注意色にする（正常と読ませない）。
 */
export const SYNC_TONES: Record<SyncKey, BadgeTone> = {
  conflict: 'warning',
  target: 'success',
  excluded: 'neutral',
  unknown: 'warning',
};

/**
 * 一覧に現れるタグ名の一覧（重複なし・辞書順）。絞り込みの選択肢の母集合である。
 *
 * **画面が持っている行からしか作らない。** タグ辞書を別に引くと、
 * 「一覧に 1 件も無いタグ」で絞り込めてしまい、常に 0 件の選択肢が並ぶ。
 */
export function tagOptionsOf(notes: readonly { tags: string[] }[]): string[] {
  return [...new Set(notes.flatMap((n) => n.tags))].sort((a, b) => a.localeCompare(b, 'ja'));
}
