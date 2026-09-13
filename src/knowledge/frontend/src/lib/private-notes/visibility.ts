import type { StatusBadgeProps } from '@platform/ui';

// FR-19, SC-03, SC-19, ADR-0036 D-06, 計画 ADR-0102 決定 1・2 / [[IADR-0451]] (#1455):
// 公開範囲（3 状態）の語彙。**SC-19（本人の一覧）と SC-03（個人資料の表示）が同じ値を描く**ため、
// feature ではなくユニットの `lib/` に置く（`lib/abac` と同じ形）。
//
// 🔴 **3 状態はサーバが導出する**（後段 `PrivateNoteEnrichment.VisibilityOf`。グループ共有があれば `groups`）。
// ここが持つのは**契約の文字列 → 画面の状態**の写像だけであり、**導出をやり直さない。**

type BadgeTone = NonNullable<StatusBadgeProps['tone']>;

// 状態の型は**外へ出さない** —— 呼び出し側が要るのは `visibilityKeyOf` の戻り値と
// `VISIBILITY_TONES` の索引だけであり、どちらも推論で足りる（未使用 export を増やさない）。

/** 公開範囲の 3 状態 ＋ 判定不能（05_screens §SC-19 主要素 2）。 */
type VisibilityKey = 'private' | 'users' | 'groups' | 'unknown';

const VISIBILITY_KEYS: readonly VisibilityKey[] = ['private', 'users', 'groups'];

/** 契約の文字列を公開範囲の状態へ落とす。既知の 3 値以外は `unknown`。 */
export function visibilityKeyOf(raw: string): VisibilityKey {
  return VISIBILITY_KEYS.find((k) => k === raw) ?? 'unknown';
}

/**
 * 公開範囲の色。**非公開だけが中立**で、共有されている状態（個人指定・グループ指定）と
 * 判定できない状態はいずれも注意色にする —— 眼目は「この資料が他人から見えるか」である。
 */
export const VISIBILITY_TONES: Record<VisibilityKey, BadgeTone> = {
  private: 'neutral',
  users: 'warning',
  groups: 'warning',
  unknown: 'warning',
};
