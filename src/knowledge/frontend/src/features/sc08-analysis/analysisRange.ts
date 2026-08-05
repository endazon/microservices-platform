import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';
import { AnalysisTaskRequestTaskType } from '@foundation/api/generated/bff.schemas';
import type { AnalysisTaskRequest } from '@foundation/api/generated/bff.schemas';

// SC-08, UC-02, FR-07: 分析要求の組み立て（純関数）。
//
// 05_screens §SC-08 主要素:「分析対象の指定（タグ・フォルダのチップ＋**検索条件による追加**）、
// 分析内容の入力（テキストエリア）、分析実行、結果パネル、出典リンク」。
//
// **タグ・フォルダのチップは実装しない**（画面仕様書 §実装しない要素 (a)）——`AnalysisDataRange` は
// 属性キー → 値集合しか取らず、タグは属性とは別の軸、フォルダは契約に存在しない。加えて
// 「分析対象（**権限内に限定**）」を成立させる権限内候補の照会口が無い。SC-01 の対象範囲フィルタと
// **同型の論点**であり、planning#197 の裁定を待つ（新規の環流記録は作らない）。
// 実装するのは「検索条件による追加」に当たる `range.query` である。

/** 指示の最大長。バックエンド `AnalysisPromptBuilder.MaxInstructionLength` と揃える（超過は 400）。 */
export const MAX_INSTRUCTION_LENGTH = 2000;

/** 検索条件の最大長。指示より短くする理由は無いが、無制限の入力を送らないための上限。 */
export const MAX_RANGE_QUERY_LENGTH = 500;

/** FR-07「分析・比較・抽出」。契約（`AnalysisTaskRequest.taskType`）の 3 値。 */
export const TASK_TYPES = [
  AnalysisTaskRequestTaskType.Analyze,
  AnalysisTaskRequestTaskType.Compare,
  AnalysisTaskRequestTaskType.Extract,
] as const;

export type TaskType = (typeof TASK_TYPES)[number];

const TASK_LABELS: Record<TaskType, MessageDescriptor> = {
  Analyze: msg`分析`,
  Compare: msg`比較`,
  Extract: msg`抽出`,
};

/** タスク種別の表示名。値集合が固定のため未知の値は来ない。 */
export function taskTypeLabel(taskType: TaskType): MessageDescriptor {
  return TASK_LABELS[taskType];
}

/**
 * 入力から分析要求を組み立てる。
 *
 * - `instruction` は前後の空白を落とす（空白だけの指示を送らない）。
 * - `range` は**検索条件が入力されたときだけ**付ける。空の `range` を送ると、契約上は
 *   「範囲を指定した」ことになり、サーバ側の解釈（省略時は instruction を流用）と食い違う。
 * - **ABAC スコープはクライアントから送らない**（送っても BFF は使わない＝権限昇格の防止）。
 */
export function buildAnalysisRequest(
  instruction: string,
  taskType: TaskType,
  rangeQuery: string,
): AnalysisTaskRequest {
  const query = rangeQuery.trim();
  return {
    instruction: instruction.trim(),
    taskType,
    ...(query ? { range: { query } } : {}),
  };
}

/** 指示が送信可能か（1 文字以上・上限内）。上限超過はサーバが 400 を返すため手前で止める。 */
export function isSubmittableInstruction(instruction: string): boolean {
  const trimmed = instruction.trim();
  return trimmed.length > 0 && trimmed.length <= MAX_INSTRUCTION_LENGTH;
}
