import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { Link, useRouter } from '@tanstack/react-router';
import type { ErrorComponentProps } from '@tanstack/react-router';
import { Button, ErrorState, LoadingState, buttonVariants } from '@platform/ui';
import { ApiError } from '@foundation/api/ApiError';
import { ENTRY_ROUTE_PATH } from './entryPath';

// ［2026-09-12 / UI/UX 改善 裁定 6］**画面単位のエラー境界と待ちの表示。**
//
// これまで境界は `App.tsx` の 1 枚だけで、ルート内の例外は**アプリ全体**を差し替えていた
// ——1 画面の取得失敗でヘッダも左レールもパンくずも消え、利用者は「どこに居るのか」も
// 「どこへ行けるのか」も失う。TanStack Router の `defaultErrorComponent` は
// **マッチしたルートの位置（= 共通シェルの本文）だけ**を差し替えるので、シェルは残る。
// `App.tsx` の 1 枚は残す——ルータの外（Provider・i18n・描画そのもの）で起きる例外は
// ルータの境界では捕まらないためである。
//
// 🔴 **内部事情を画面へ出さない**（IADR-0009 と同じ趣旨）。`ApiError` は種別ごとの
// 中立メッセージを自分で持っているのでそれを出し、それ以外は既定文言へ倒す
// （スタックや例外メッセージをそのまま出さない）。`ErrorBoundary.tsx` と同じ判定である。
//
// 🔴 **`role="alert"` を自分で付けない**——`ErrorState` が持っている。二重に付けると
// 同じ本文が 2 回読み上げられる。

/** 失敗の本文（ApiError は自身の中立メッセージ、それ以外は既定文言）。 */
function errorMessage(error: unknown): string {
  return error instanceof ApiError
    ? error.message
    : i18n._(msg`予期しないエラーが発生しました。時間をおいて再度お試しください。`);
}

/**
 * 画面単位のエラー表示（`createRouter` の `defaultErrorComponent`）。
 *
 * 次の一手を 2 つ出す。**どちらも「利用者が実際に取れる行動」である**:
 *   - **再読み込み**: 境界の状態を戻し（`reset`）、ルータのデータを無効化して読み直す
 *     （`router.invalidate()`）。`reset` だけだと、同じ失敗したデータをそのまま描き直して
 *     即座に同じ画面へ戻る（＝押しても何も起きないボタンになる）。
 *   - **ホームへ戻る**: 主入口（`ENTRY_ROUTE_PATH`）への**遷移**。`<a href>` で全体を
 *     読み直させない——SPA の中に居るのだから、ルータの遷移で足りる。
 */
export function RouteErrorComponent({ error, reset }: ErrorComponentProps) {
  const router = useRouter();
  return (
    <ErrorState
      title={i18n._(msg`エラー`)}
      description={errorMessage(error)}
      action={
        <div className="flex flex-wrap items-center justify-center gap-n2">
          <Button
            variant="primary"
            size="sm"
            onClick={() => {
              reset();
              void router.invalidate();
            }}
          >
            {i18n._(msg`再読み込み`)}
          </Button>
          <Link to={ENTRY_ROUTE_PATH} className={buttonVariants({ size: 'sm' })}>
            {i18n._(msg`ホームへ戻る`)}
          </Link>
        </div>
      }
    />
  );
}

/**
 * 画面単位の待ち表示（`createRouter` の `defaultPendingComponent`）。
 *
 * `defaultPendingMs` は既定（1000ms）のままにする——速い遷移で待ち表示が一瞬光るのは、
 * 何も出ないより読みにくい。
 */
export function RoutePendingComponent() {
  return <LoadingState label={i18n._(msg`読み込み中…`)} />;
}
