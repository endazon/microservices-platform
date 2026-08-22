import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// SC-03, FR-05/FR-06: 属性・タグパネルの表示（05_screens §SC-03 主要素「機密区分・部門・タグ」）。
//
// **キーだけを写像し、値は変換しない。**
//
// **［2026-08-10 追記 / #553］保留の理由は解消した。** 従前は「モックに現れる表示名が 4 値中 2 値だけで、
// 残る 2 値を実装が決めると事実上の用語定義になる」ため生値を出していた（planning#197 で裁定待ち）。
// **利用者裁定 2026-08-05 で 4 値すべての表示名が確定した**（正は `planning/docs/glossary.md`。
// public＝公開 / internal＝社内限 / confidential＝秘 / restricted＝**取扱制限**。
// **`restricted` は「極秘」ではない** —— 個人資料が既定でこの区分を持つため）。
//
// **それでも本ファイルでは値を変換しない。** 写像を入れる先は引用（`CitationDto`）側の **#541** であり
// （#553 がそう指示している）、ここで先に入れると**同じ写像が 2 か所に生まれる**。
// 値集合の単一情報源は `features/abac/confidentiality.ts` である。

/** 計画が画面ラベルを与えている属性キー。ここに無いキーはそのまま表示する。 */
const ATTRIBUTE_LABELS: Record<string, MessageDescriptor> = {
  confidentiality: msg`機密区分`,
  department: msg`部門`,
};

/** 属性キーに対応する表示ラベル（未定義なら `undefined`）。解決は描画時に行う。 */
export function attributeLabel(key: string): MessageDescriptor | undefined {
  return ATTRIBUTE_LABELS[key];
}

/**
 * 属性を表示順へ並べる。計画 §SC-03 が挙げる順（機密区分 → 部門）を先頭に置き、
 * それ以外はキーの辞書順で続ける（表示順が応答の JSON 順に左右されないようにする）。
 */
export function orderedAttributes(attributes: Record<string, string>): [string, string][] {
  const known = ['confidentiality', 'department'];
  const entries = Object.entries(attributes ?? {});
  return [
    ...known.flatMap((k): [string, string][] => (k in attributes ? [[k, attributes[k]]] : [])),
    ...entries.filter(([k]) => !known.includes(k)).sort(([a], [b]) => a.localeCompare(b)),
  ];
}
