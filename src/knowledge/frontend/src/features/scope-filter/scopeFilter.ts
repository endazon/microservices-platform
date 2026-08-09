import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';
import type { AskRequestAttributeFilters } from '@foundation/api/generated/bff.schemas';

// FR-04, FR-05, SC-01, SC-08, UC-01, UC-02, #539: 対象範囲（絞り込み）の純粋ロジック。
//
// **SC-01 と SC-08 で 1 つの実装を共有する。** 計画（05_screens §SC-08）が
// 「同じ候補 API を用いる」「SC-01 と一体で扱う」と定めており、その理由も書かれている——
// **「同じ『範囲を絞る』操作が画面ごとに違う挙動になると、利用者は操作を覚え直すことになる」**
// （利用者裁定 2026-08-05 Q3）。**画面ごとに書くと、その裁定を実装で破ることになる。**

/**
 * 対象範囲の軸。**タグ・部門・プロジェクトの 3 つである**（確定・2026-08-05 / 裁定 Q9）。
 *
 * **「フォルダ」は無い。** ABAC 属性体系に `folder` が存在せず、フォルダは取り込み時に
 * 属性へ写像されて消える設計だからである。**新設もしない**——パスは階層構造を持ち、
 * 本属性体系が意図的に排除している序数・階層と正面から衝突する。
 *
 * キーはそのまま契約（`attributeFilters` のキー）になる。`tags` は値集合の照会
 * （`POST /bff/attribute-values`）と同じキーで、**候補に出る値と絞れる値が同じ写像を通る**。
 */
export const SCOPE_AXES = ['tags', 'department', 'project'] as const;

export type ScopeAxis = (typeof SCOPE_AXES)[number];

const AXIS_LABELS: Record<ScopeAxis, MessageDescriptor> = {
  tags: msg`タグ`,
  department: msg`部門`,
  project: msg`プロジェクト`,
};

/** 軸の表示名。値集合が固定のため未知の値は来ない。 */
export function scopeAxisLabel(axis: ScopeAxis): MessageDescriptor {
  return AXIS_LABELS[axis];
}

/** 軸ごとに選択中の値。空配列の軸は「その軸で絞らない」を意味する。 */
export type ScopeSelection = Record<ScopeAxis, string[]>;

export const EMPTY_SELECTION: ScopeSelection = { tags: [], department: [], project: [] };

/** 値の選択を切り替える（選択済みなら外す）。**元の選択は変更しない**（React の state 更新に渡す）。 */
export function toggleScopeValue(
  selection: ScopeSelection,
  axis: ScopeAxis,
  value: string,
): ScopeSelection {
  const current = selection[axis];
  const next = current.includes(value) ? current.filter((v) => v !== value) : [...current, value];
  return { ...selection, [axis]: next };
}

/**
 * 選択を契約の形（`attributeFilters`）へ変換する。
 *
 * **空の軸は載せない。** 空配列を送ると契約上は「その軸を指定した」ことになり、
 * サーバ側は「空指定＝当該キーで絞らない」と解釈する（`DataRangeScopeResolver`）ので
 * 結果は同じだが、**要求を見たときに「絞ったのに効いていない」と読めてしまう**。
 *
 * **何も選んでいなければ `undefined` を返す。** 空オブジェクトを送ると、
 * 旧クライアント（送らない）との差が要求の形に現れ、ログの読み手を惑わせる。
 */
export function toAttributeFilters(
  selection: ScopeSelection,
): AskRequestAttributeFilters | undefined {
  const entries = SCOPE_AXES.filter((axis) => selection[axis].length > 0).map(
    (axis) => [axis, selection[axis]] as const,
  );
  return entries.length > 0 ? Object.fromEntries(entries) : undefined;
}

/** 選択中の値の総数（画面が「N 件で絞り込み中」を出すため）。 */
export function selectedCount(selection: ScopeSelection): number {
  return SCOPE_AXES.reduce((total, axis) => total + selection[axis].length, 0);
}
