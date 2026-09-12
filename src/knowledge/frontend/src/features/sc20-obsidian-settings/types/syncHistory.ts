import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';
import type { SyncHistoryEntryDto } from '@foundation/api/generated/bff.schemas';

// SC-20 主要素 6, UC-11, FR-20, ADR-0037 決定 9 / ADR-0099 決定 5・6 / IADR-0446:
// 同期履歴 1 行の値（方向・内訳・結果・失敗理由）を表示の形へ落とす純関数。
//
// **純関数だけを置く。** 描画も問い合わせもしない（`deviceState.ts` / `syncFolders.ts` と同じ作法）。
//
// 🔴 **失敗理由の文言は画面が持つ。** 契約（`SyncHistoryEntryDto.failureReason`）が運ぶのは
// コードだけであり、そう決めたのは ADR-0099 決定 5 が「**利用者が次に何をすればよいか分かる文言**」を
// 求めているからである —— 文言はサーバの関心ではなく、**画面の導線（競合の区画・削除済みタブ・
// Obsidian 側の操作）を知っている側**にしか書けない。したがって写像はここに 1 つだけ置く。
//
// 🔴 **契約は値を `string` で運ぶ（enum にしない。IADR-0131）。** よって未知の値が届きうる。
// **未知を既知へ丸めない** —— 方向は生値をそのまま出し、失敗理由はコードを添えた文言にする。
// 丸めると「知らない失敗を知っている失敗として読む」ことになり、利用者が誤った対処を取る。

// 契約が定める失敗理由の 7 値（`SyncHistoryEntryDto.failureReason`）。
//
// **export しない** —— 未使用 export の床（check-knip）を押し上げるためである。**テストは
// この配列を import せず、7 値を独立に列挙する** —— 同じ配列を共有すると「7 値のうち 1 つを
// 消しても両方が同時に変わる」形になり、網羅を確かめたことにならない。
const SYNC_FAILURE_REASONS = [
  'version_conflict',
  'deleted',
  'path_conflict',
  'quota_exceeded',
  'body_too_large',
  'invalid_request',
  'not_found',
] as const;

type SyncFailureReason = (typeof SYNC_FAILURE_REASONS)[number];

/**
 * 方向の表示。**未知の値は生値をそのまま返す**（`jobStatusView` と同じ作法）。
 *
 * 「送信」「受信」は**端末から見た向き**である —— `push` は端末がサーバへ送った、
 * `pull` は端末がサーバから受け取った。利用者が操作したのは端末なので、端末を主語にする。
 */
export function directionLabel(code: string): MessageDescriptor | string {
  if (code === 'push') return msg`送信`;
  if (code === 'pull') return msg`受信`;
  return code;
}

/** 結果が成功か（契約の `outcome` は `success` / `failure`）。 */
export function isSuccess(outcome: string): boolean {
  return outcome === 'success';
}

const FAILURE_TEXTS: Record<SyncFailureReason, MessageDescriptor> = {
  // 競合は台帳（`SyncConflict`）にも入る。**同じ画面の上の区画へ誘導する**（別の画面を開かせない）。
  version_conflict: msg`競合を記録しました。上の「同期の競合」から解決してください。`,
  deleted: msg`削除済みの資料です。削除済みタブから復元するか、Obsidian 側のファイルを削除してください。`,
  path_conflict: msg`同じパスの資料が既にあります。Obsidian 側でファイル名を変えてください。`,
  quota_exceeded: msg`保存容量の上限に達しています。資料を削除して容量を空けてください。`,
  body_too_large: msg`本文が上限（1 MB）を超えています。分割してください。`,
  invalid_request: msg`要求の形式が不正です。プラグインを最新にしてください。`,
  not_found: msg`資料が見つかりません。Obsidian 側で再同期してください。`,
};

function isKnownReason(code: string): code is SyncFailureReason {
  return (SYNC_FAILURE_REASONS as readonly string[]).includes(code);
}

/**
 * 失敗理由のコードを「次に何をすればよいか分かる文言」へ写す（ADR-0099 決定 5）。
 *
 * 🔴 **未知のコードもコードを添えて文言にする。** 「不明な失敗」だけを出すと、
 * 問い合わせを受けた側がどの経路で失敗したのか追えない（コードは生のまま添える）。
 */
export function failureReasonText(code: string): MessageDescriptor {
  if (isKnownReason(code)) return FAILURE_TEXTS[code];
  return msg`同期に失敗しました（理由: ${code}）`;
}

/** 内訳 1 項目。**0 件の項目は作らない**（`breakdownOf` が落とす）。 */
export interface SyncCountPart {
  kind: 'added' | 'updated' | 'deleted' | 'conflicted';
  count: number;
}

/**
 * 件数の内訳（追加 / 更新 / 削除 / 競合）のうち **0 でないものだけ**を順序付きで返す。
 *
 * 🔴 **0 を出さない。** 現行プロトコルは 1 操作 1 資料なので（契約の注記）、4 項目のうち
 * 1 つだけが 1 になるのが通常である。「追加 1・更新 0・削除 0・競合 0」と並べると、
 * **意味を持つ 1 項目が 3 つの 0 に埋もれる。** すべて 0 のときは空配列を返し、
 * 呼び出し側が「—」を描く（**空欄にしない** —— 取得漏れと区別できなくなる）。
 */
export function breakdownOf(entry: SyncHistoryEntryDto): SyncCountPart[] {
  const parts: SyncCountPart[] = [
    { kind: 'added', count: entry.added },
    { kind: 'updated', count: entry.updated },
    { kind: 'deleted', count: entry.deleted },
    { kind: 'conflicted', count: entry.conflicted },
  ];
  return parts.filter((p) => p.count > 0);
}
