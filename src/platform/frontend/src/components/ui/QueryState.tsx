import type { ReactNode } from 'react';
import type { UseQueryResult } from '@tanstack/react-query';
import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { Button, EmptyState, ErrorState, LoadingState } from '@platform/ui';
import { ApiError } from '@foundation/api/ApiError';

// NFR, ADR-0031（UI/UX 改善 2026-09-12・第 4 弾「体験の穴」(1)(2)）: 取得結果の**待ち・失敗・空・本体**を
// 1 か所で描き分け、失敗には再試行の導線を必ず付ける、全画面共通の三部品。
//
// 17 画面がそれぞれ `<p>` 直書きで三状態を描き、文言（「読み込み中です。」「読み込み中…」「検索中…」）も
// `role` の有無も、分岐の書き方（三項連鎖／`&&` 並置／early return）も揃っていなかった。**構造で潰す**ため
// フックではなく部品 1 本にする（呼び出し側は render prop で本体だけを書く）。
// `@platform/ui` には置かない——ここは既定文言（i18n）を持つため（IADR-0125 決定 1: 共有 UI は文言を持たない）。
//
// 🔴 **判定順は isError → isPending → isEmpty → 本体。** 順を変えてはならない。
//   - 失敗を先に見るのは、**0 件と失敗を混同しない**ため（取得に失敗したのに「ありません」と描くと、
//     利用者は「本当に無い」と読む）。
//   - `isEmpty` は成功したデータに対してだけ評価する（失敗時のデータの有無は問わない）。
//   - `role="status"` / `role="alert"` は三部品の側が持つ（ここで重ねて付けない）。
//
// 再試行の可否は本部品が判断しない——`canRetry` で受ける（既定は可）。自動再試行の判定
// （4xx は再試行せず 408/429 のみ例外。`lib/api/queryClient.ts`）とは別物で、これは**利用者が押す**再試行である。
export interface QueryStateProps<T> {
  query: UseQueryResult<T, unknown>;
  /** 成功したデータが「空」か。省略時は空判定をしない（常に本体を描く）。 */
  isEmpty?: (data: T) => boolean;
  /** 空のときに描くもの。省略時は既定の EmptyState。 */
  empty?: ReactNode;
  /** 待ちの文言。省略時は既定の文言。 */
  loadingLabel?: string;
  /** 失敗の見出し。省略時は既定の文言。画面固有の説明（「設定情報は利用できません。」等）はここへ渡す。 */
  errorTitle?: ReactNode;
  /** 失敗の説明。省略時は ApiError の中立メッセージ（存在秘匿を破らない文言）を出す。 */
  errorDescription?: ReactNode;
  /** 再試行ボタンを出すか。既定 true。 */
  canRetry?: boolean;
  children: (data: T) => ReactNode;
}

export function QueryState<T>({
  query,
  isEmpty,
  empty,
  loadingLabel,
  errorTitle,
  errorDescription,
  canRetry = true,
  children,
}: QueryStateProps<T>) {
  if (query.isError) {
    const fallbackDescription = query.error instanceof ApiError ? query.error.message : undefined;
    return (
      <ErrorState
        title={errorTitle ?? i18n._(msg`取得に失敗しました。`)}
        description={errorDescription ?? fallbackDescription}
        action={
          canRetry ? (
            <Button
              onClick={() => {
                void query.refetch();
              }}
            >
              {i18n._(msg`再試行`)}
            </Button>
          ) : undefined
        }
      />
    );
  }
  if (query.isPending) {
    return <LoadingState label={loadingLabel ?? i18n._(msg`読み込み中…`)} />;
  }
  const data = query.data;
  if (isEmpty?.(data)) {
    return <>{empty ?? <EmptyState title={i18n._(msg`該当するものはありません。`)} />}</>;
  }
  return <>{children(data)}</>;
}
