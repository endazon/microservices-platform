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

// ── 図の縮退・補正にまつわる導出（#651 / [[IADR-0154]]）
//
// **いずれも `status` の 5 値目にしない。** `05_screens:320`「ジョブ状態モデルは 4 値である」に従い、
// 契約のフィールドから**導出**する（[[IADR-0127]]「状態表示は契約から導出できる値だけで作る」）。
// `deadLettered` と同じ扱いである。
//
// **縮退したジョブの `status` は `succeeded`** である——図のコード化に失敗して画像保持へ落ちても、
// 変換そのものは成功している。ここを `failed` と読むと再変換ボタンの出し分けまで狂う。

/** 図が画像保持へ縮退しているか（hi-fi:420「✕ 図コード化失敗（画像保持へ縮退済み）」の導出元）。 */
export function hasRetainedFigures(job: { diagramsRetained?: number }): boolean {
  return (job.diagramsRetained ?? 0) > 0;
}

/**
 * 人手補正の導線を出してよいジョブか。
 *
 * **縮退した図があること**が条件である（補正の対象が無ければ 2 ペインを開く意味が無い）。
 * 権限（管理者限定）は呼び出し側が別に判定する——**条件と権限を 1 つの関数へ混ぜない**。
 * 混ぜると「対象が無いから出ない」と「権限が無いから出ない」を画面が区別できず、
 * [[IADR-0127]] 決定 1 が求める**理由の文言**を選べなくなる。
 */
export function isCorrectable(job: { diagramsRetained?: number }): boolean {
  return hasRetainedFigures(job);
}

/**
 * **本文なしで完了**したか（計画 ADR「PDF の本文抽出は pandoc の外に置く」決定 3 の導出元）。
 *
 * テキスト層を持たない PDF（スキャン等）は本文が存在しないため、変換は失敗ではなく
 * 「本文なし・原本参照のみ」として**完了**する。**`status` は `succeeded`** であり、これも
 * `deadLettered` / `diagramsRetained` と同じく**内訳の標識**である（5 値目にしない）。
 * `failed` の列に並ばないので再変換ボタンも出ない —— 何度やっても結果は変わらない。
 *
 * フィールドが無い応答（古いサーバ）は「本文あり」へ倒す（`hasRetainedFigures` と同じ防御）。
 */
export function isBodyAbsent(job: { bodyAbsent?: boolean }): boolean {
  return job.bodyAbsent === true;
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
