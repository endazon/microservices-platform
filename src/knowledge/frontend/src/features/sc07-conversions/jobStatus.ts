import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// SC-07, UC-06, FR-12: 変換ジョブの状態モデル（純関数）。
//
// 05_screens §SC-07 §データソース（2026-08-04 確定）:
//   「ジョブ状態モデルは 4 値である: queued（受付済み・未着手）／processing（変換中）／
//    succeeded（成功）／failed（失敗）。デッドレターの表示は failed の内訳として扱う。」
//
// 判定を DOM から切り離してあるのは、4 値の写像そのものを描画なしで試験できるようにするためである。

/** 計画が確定したジョブ状態の 4 値。 */
export const JOB_STATUSES = ['queued', 'processing', 'succeeded', 'failed'] as const;

export type JobStatus = (typeof JOB_STATUSES)[number];

/** 一覧の絞り込み条件（空文字＝すべて）。計画確定の「状態でのフィルタ」の値集合。 */
export type JobStatusFilter = '' | JobStatus;

/** `StatusBadge` の tone。tone ごとに固定アイコンが付く（INDEX 決定 21 を型で強制する部品）。 */
export type JobTone = 'neutral' | 'success' | 'danger';

export interface JobStatusView {
  /** 表示文言。**未知の状態は生値を出すため `string` になる**（翻訳しない）。 */
  label: MessageDescriptor | string;
  tone: JobTone;
}

const VIEWS: Record<JobStatus, JobStatusView> = {
  queued: { label: msg`待機中`, tone: 'neutral' },
  processing: { label: msg`変換中`, tone: 'neutral' },
  succeeded: { label: msg`完了`, tone: 'success' },
  failed: { label: msg`失敗`, tone: 'danger' },
};

function isKnown(status: string): status is JobStatus {
  return (JOB_STATUSES as readonly string[]).includes(status);
}

/**
 * ジョブ状態を表示の組（文言 ＋ tone）へ写像する。
 *
 * **未知の値は握り潰さない**——`—` や「不明」へ丸めず生値をそのまま出す。契約が 4 値と定めていても、
 * サーバが将来 5 つ目を返したときに画面が異常へ気付ける状態にしておく。
 */
export function jobStatusView(status: string): JobStatusView {
  return isKnown(status) ? VIEWS[status] : { label: status, tone: 'neutral' };
}

/**
 * 再変換できる状態か。
 *
 * 05_screens §SC-07 §データソース: 「同一ジョブの再変換は直列化し、実行中（`processing`）の
 * 再変換要求は拒否する」。サーバ（`POST /jobs/{id}/retry`）は `failed` 以外を 409 で拒否するため、
 * **画面もそれに合わせて `failed` の行にだけボタンを出す**（UI 制御だけに頼らない多層防御）。
 */
export function isRetryable(status: string): boolean {
  return status === 'failed';
}
