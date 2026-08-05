import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// SC-03, FR-05/FR-06: 属性・タグパネルの表示（05_screens §SC-03 主要素「機密区分・部門・タグ」）。
//
// **キーだけを写像し、値は変換しない。** hi-fi モックは `internal` を「社内限」、SC-05 / SC-09 は
// `confidential` を「秘」と描いているが、計画（06_technical/07_abac-attribute-model）が定める値集合は
// `public` / `internal` / `confidential` / `restricted` の 4 値であり、**モックに現れるのは 2 値だけ**である。
// 残る 2 値の表示名は計画のどこにも無く、実装が決めるとそれが事実上の用語定義になってしまう。
// 機密区分は取り違えの影響が大きいため、推測で名前を与えず生値を出す
// （feedback/20260804_sc01-03-bff-contract-gaps.md に環流の記録。planning#197 で裁定待ち）。

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
