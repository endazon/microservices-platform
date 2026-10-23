import type { BreakGlassAuditEntryDto } from '@foundation/api/generated/bff.schemas';

// SC-19, SC-03, UC-11, FR-19, 計画 ADR-0036 D-08, planning#841, [[IADR-0612]] (#2046 の 3 本目):
// 緊急アクセスの画面が使う純関数。**判定はしない**（閲覧の可否は文書サービスが要求ごとに発動記録で決める）。
// ここにあるのは**表示の出し分け**と、送る前の入力の形の確認（多層防御。値域の防壁はサーバ側にある）だけである。

/** 理由の上限（文字数）。後段の検証（400）と同じ値（1 本目の AC2）。 */
export const REASON_MAX_LENGTH = 500;

/** 理由の入力の状態。**空白だけは空と同じ**（後段も空白だけを 400 にする）。 */
export type ReasonState = 'empty' | 'tooLong' | 'ok';

export function reasonStateOf(reason: string): ReasonState {
  if (reason.trim() === '') return 'empty';
  // 🔴 **数えるのは送る値（前後の空白を落としたもの）である**（送信は `trim()` した値を送る）。
  if (reason.trim().length > REASON_MAX_LENGTH) return 'tooLong';
  return 'ok';
}

/**
 * 終了予定までの残り秒数（0 未満にはしない）。
 *
 * 🔴 **この値を閲覧の可否に使わない。** 残り時間は表示のためだけにあり、0 になったら状態を引き直す
 * （有効性はサーバ側の発動記録で判定する。D-08）。
 */
export function remainingSeconds(endsAt: string, nowMs: number): number {
  const end = Date.parse(endsAt);
  if (Number.isNaN(end)) return 0;
  return Math.max(0, Math.floor((end - nowMs) / 1000));
}

/** 残り秒数を「分」と「秒」へ分ける（文言は呼び出し側が翻訳して組む）。 */
export function splitMinutesSeconds(seconds: number): { minutes: number; seconds: number } {
  return { minutes: Math.floor(seconds / 60), seconds: seconds % 60 };
}

/** 監査ログの操作種別（契約の値集合 `activate` / `end` / `view`）。知らない値は `unknown`。 */
export type AuditOperationKey = 'activate' | 'end' | 'view' | 'unknown';

export function auditOperationKeyOf(operation: string): AuditOperationKey {
  return operation === 'activate' || operation === 'end' || operation === 'view'
    ? operation
    : 'unknown';
}

/** 利用者名を比べる（大小を無視する。後段も指名先を正規形〔小文字〕で照合する。1 本目の AC15）。 */
export function sameUser(a: string | null | undefined, b: string | null | undefined): boolean {
  if (!a || !b) return false;
  return a.trim().toLowerCase() === b.trim().toLowerCase();
}

/**
 * SC-03 で開く導線を出す行か（**指名された本人が見ている・発動の行・終了予定が未来**）。
 *
 * 🔴 **これは導線の出し分けであって許可ではない。** 本人が早く終えた・名簿でいま管理者でない等は
 * ここでは分からない（終了の行は別の行である）。開けるかどうかは SC-03 の後段が決め、開けなければ 404 になる。
 * 同じ発動に終了の行があれば導線を出さない（`endedGrantIds`）。
 */
export function canOfferView(
  entry: BreakGlassAuditEntryDto,
  me: string | null | undefined,
  nowMs: number,
  endedGrantIds: ReadonlySet<string>,
): boolean {
  return (
    entry.operation === 'activate' &&
    sameUser(entry.nomineeUserId, me) &&
    !endedGrantIds.has(entry.grantId) &&
    remainingSeconds(entry.endsAt, nowMs) > 0
  );
}

/** 監査ログのうち、終了の行がある発動の集合。 */
export function endedGrantIdsOf(entries: readonly BreakGlassAuditEntryDto[]): Set<string> {
  return new Set(entries.filter((e) => e.operation === 'end').map((e) => e.grantId));
}

/** 監査ログに現れる利用者名の集合（表示名へ引く。契約 `/bff/users/resolve` は 1 回 100 件まで）。 */
export function auditUsernamesOf(entries: readonly BreakGlassAuditEntryDto[]): string[] {
  const names = new Set<string>();
  for (const e of entries) {
    for (const name of [e.actorUserId, e.ownerUserId, e.nomineeUserId]) {
      if (name) names.add(name);
    }
  }
  return [...names].slice(0, 100);
}
