import { useQueryClient } from '@tanstack/react-query';
import {
  getBffNotificationListQueryKey,
  useBffNotificationList,
  useBffNotificationMarkRead,
} from '@foundation/api/generated/notifications/notifications';
import type { NotificationListDto } from '@foundation/api/generated/bff.schemas';
import { okData } from '@foundation/api/orvalSelect';

// FR-22, UC-11, IADR-0215 決定 2: アプリ内通知の取得と既読化。
//
// **配信はポーリングである**（SSE ではない）。SSE は移行第 4 段（#788）の射程であり
// （IADR-0121 決定 5）、orval も SSE を生成できない（`orval-bff-only.cjs` が落とす）。
// 3 契機（週次 / 7 日前 / 閾値到達）はいずれも日・週の粒度なので、秒単位の即時性は要らない。
//
// **通信は orval 生成フックだけを使う**（IADR-0121 決定 3）。手書き HTTP クライアントは禁止で、
// `foundation/api` 以外での `fetch` は ESLint が error にする。

/**
 * ポーリング間隔（ミリ秒）。IADR-0215 決定 2 の 60 秒。**契約には現れない**——クライアントの都合である。
 *
 * **export しない。** 外から読む面が無いので、export すると Knip の未使用ラチェット
 * （`scripts/knip-baseline.json`）が 1 件増える（実測）。値の正はここと IADR-0215 決定 2 である。
 */
const NOTIFICATION_POLL_INTERVAL_MS = 60_000;

/** 1 度に取得する件数。契約の上限は 100（BFF がクランプする）。 */
const NOTIFICATION_PAGE_SIZE = 50;

const LIST_PARAMS = { limit: NOTIFICATION_PAGE_SIZE } as const;

/**
 * 本人宛の通知一覧を引く。
 *
 * FR-22: **宛先の解決はサーバ側**である（BFF が JWT の主体で絞る）。クライアントは利用者 ID を送らない
 * ——送ると「他人の ID を入れたらどうなるか」という面ができてしまう。
 */
export function useNotificationList() {
  return useBffNotificationList<NotificationListDto>(LIST_PARAMS, {
    query: {
      // 生成キーを明示する（`query` を渡すと `queryKey` は必須になる）。**自前のキーを作らない**
      // ——`useMarkNotificationRead` の無効化と同じ生成キーを使うことで、両者が割れる面を無くす。
      queryKey: getBffNotificationListQueryKey(LIST_PARAMS),
      select: okData,
      refetchInterval: NOTIFICATION_POLL_INTERVAL_MS,
      // ポーリングするので、既定の staleTime（30 秒）より短い間隔で古いと見なす必要は無い。
      // 既定のままにすると refetchInterval の往復がキャッシュに阻まれて無駄になるため 0 にする。
      staleTime: 0,
    },
  });
}

/**
 * 通知 1 件を既読にする。**冪等**（契約上、既読へもう一度呼んでも 200）。
 *
 * 成功したら一覧を無効化して取り直す。**応答の `unreadCount` を画面が自前で持ち回らない**
 * ——サーバの値とクライアントの値が割れる面を作らないためである。
 */
export function useMarkNotificationRead() {
  const queryClient = useQueryClient();
  return useBffNotificationMarkRead({
    mutation: {
      onSuccess: () =>
        queryClient.invalidateQueries({ queryKey: getBffNotificationListQueryKey(LIST_PARAMS) }),
    },
  });
}
