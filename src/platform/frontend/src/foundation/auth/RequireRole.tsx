import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import type { ReactNode } from 'react';
import { useAuth } from './useAuth';
import { useHasAnyRole } from './roles';
import { NotFound } from '@foundation/ui/NotFound';

// IADR-0035 / IADR-0009: ロール限定ルートのガード。権限外は NotFound を描画し、画面の存在を
// 示さない（存在秘匿。/login へも誘導しない）。認可の実効境界はサーバ側に置き、UI はメニュー
// 出し分けと存在秘匿のためだけに用いる。RequireAuth（認証）の内側で使う。
export function RequireRole({ anyOf, children }: { anyOf: string[]; children: ReactNode }) {
  const { isLoading } = useAuth();
  const allowed = useHasAnyRole(...anyOf);

  if (isLoading) {
    return <p role="status">{i18n._(msg`読み込み中…`)}</p>;
  }
  return allowed ? <>{children}</> : <NotFound />;
}
