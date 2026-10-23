import { useCallback } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import {
  getBffBreakGlassAuditQueryKey,
  getBffBreakGlassStatusQueryKey,
  useBffBreakGlassActivate,
  useBffBreakGlassAudit,
  useBffBreakGlassEnd,
  useBffBreakGlassStatus,
} from '@foundation/api/generated/private-notes/private-notes';
import { okArray, okData } from '@foundation/api/orvalSelect';
import type {
  BreakGlassAuditEntryDto,
  BreakGlassStatusResponse,
} from '@foundation/api/generated/bff.schemas';

// SC-19, UC-11, FR-19, 計画 ADR-0036 D-08, planning#841, [[IADR-0611]] / [[IADR-0612]] (#2046 の 3 本目):
// 緊急アクセスの読み書き。呼び出しは **orval 生成フック**だけを使う（IADR-0135 決定 1）。
//
// 🔴 **状態（`active` / `endsAt`）を閲覧の許可の根拠にしない**（契約の注記）。画面は残り時間の表示と、
// 終了の導線の出し分けにだけ使う。
//
// 🔴 **監査ログの絞りは後段が持つ**（platform-admin は全件・本人は自分の資料の行だけ）。
// 画面は誰の行まで出すかを決めない —— 返ってきた行をそのまま描く。

/** 監査ログの取得件数（契約の既定と同じ。上限は 200）。 */
const AUDIT_LIMIT = 50;
const auditParams = { limit: AUDIT_LIMIT };

/** 残り 0 の後、サーバが失効を返すまで状態を引き直す間隔。 */
const EXPIRY_POLL_INTERVAL_MS = 5_000;
/** その上限（端末の時計がサーバより進んでいると、サーバはしばらく有効を返し続ける。無限には引かない）。 */
export const EXPIRY_POLL_LIMIT_MS = 120_000;

/**
 * 資料 1 件の緊急アクセスの状態（所有者本人のみ）。
 *
 * **選んだ資料があるときだけ引く**（`enabled`）—— 一覧の行ごとに引かない（行数だけ往復が増える）。
 *
 * `expiryPoll`: 画面の残り時間が 0 になった発動とその時刻。**応答がまだその発動を有効として返している間だけ**、
 * `EXPIRY_POLL_LIMIT_MS` を上限に `EXPIRY_POLL_INTERVAL_MS` ごとに引き直す（失効が返れば自然に止まる）（AI レビュー 🟡: 1 回だけ引き直す形は、端末の時計が進んでいると
 * 「確認しています」のまま固着した）。失効を決めるのはサーバ側の記録であり、画面は返ってきた状態を描くだけである。
 */
export function useBreakGlassStatus(
  id: string | null,
  expiryPoll: { grantId: string; since: number } | null = null,
) {
  const target = id ?? '';
  return useBffBreakGlassStatus<BreakGlassStatusResponse, Error>(target, {
    query: {
      queryKey: getBffBreakGlassStatusQueryKey(target),
      select: okData,
      enabled: target !== '',
      refetchInterval: (query) => {
        // `query.state.data` は select 前の生の応答（`{ status, data }`）である。
        const raw = query.state.data as { data?: BreakGlassStatusResponse } | undefined;
        const stillActive = expiryPoll !== null && raw?.data?.active?.id === expiryPoll.grantId;
        return stillActive && Date.now() - expiryPoll.since < EXPIRY_POLL_LIMIT_MS
          ? EXPIRY_POLL_INTERVAL_MS
          : false;
      },
    },
  });
}

/**
 * 監査ログ（新しい順・最大 50 件）。
 *
 * **開いたときだけ引く**（`enabled`）。SC-19 を開くたびに引くと、使わない利用者にも往復が増える。
 */
export function useBreakGlassAudit(enabled: boolean) {
  return useBffBreakGlassAudit<BreakGlassAuditEntryDto[], Error>(auditParams, {
    query: {
      queryKey: getBffBreakGlassAuditQueryKey(auditParams),
      select: okArray,
      enabled,
    },
  });
}

/**
 * 発動と本人の終了。成功後は**状態と監査ログを無効化する**（IADR-0127 決定 5 と同じ作法。手書きの再取得を持たない）。
 */
export function useBreakGlassActions(id: string | null) {
  const queryClient = useQueryClient();
  // 🔴 **参照を安定させる**（`useCallback`）。残り時間が 0 になったときの引き直しは effect の依存に入るので、
  // 描画ごとに作り直すと 1 秒ごとの描画のたびに引き直しが飛ぶ。
  const invalidate = useCallback(() => {
    if (id !== null)
      void queryClient.invalidateQueries({ queryKey: getBffBreakGlassStatusQueryKey(id) });
    // 監査ログは引数違いのキーも含めて無効化する（キーの先頭要素で前方一致）。
    void queryClient.invalidateQueries({ queryKey: [getBffBreakGlassAuditQueryKey()[0]] });
  }, [id, queryClient]);
  const onChanged = { mutation: { onSuccess: invalidate } };
  return {
    activate: useBffBreakGlassActivate<unknown>(onChanged),
    end: useBffBreakGlassEnd<unknown>(onChanged),
    /** 残り時間が 0 になったときに状態を引き直す（失効はサーバ側の記録で決まる）。 */
    refresh: invalidate,
  };
}
