import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { useEffect, useState } from 'react';
import { useNavigate } from '@tanstack/react-router';
import { userManager as defaultUserManager } from './authConfig';
import { toInternalPath } from './safeRedirect';
import { ENTRY_ROUTE_PATH } from '@foundation/routing/entryPath';
import type { UserManager } from 'oidc-client-ts';

// Issue #126: OIDC リダイレクト後の認可コード交換。完了後は元の場所（returnTo）へ戻す。
// ADR-0031 / IADR-0124: 遷移は TanStack Router の useNavigate。returnTo は OIDC の state 由来
// （SPA 外から戻ってくる値）なので、SPA 内部の絶対パスでなければ SC-01（主入口）へ倒す。
export function CallbackPage({ manager }: { manager?: UserManager }) {
  const navigate = useNavigate();
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const mgr = manager ?? defaultUserManager();
    let active = true;
    mgr
      .signinRedirectCallback()
      .then((user) => {
        if (!active) return;
        // IADR-0124 決定 9: returnTo は認可サーバを往復して戻る外部由来の値である。
        // この SPA 内部のパスとして解決できないものは主入口へ倒す（オープンリダイレクト対策）。
        const raw = (user.state as { returnTo?: unknown } | undefined)?.returnTo;
        const returnTo = toInternalPath(raw) ?? ENTRY_ROUTE_PATH;
        // IADR-0124 決定 5: 実行時に決まる遷移先は Link/navigate の union で検査できない。
        void navigate({ to: returnTo as typeof ENTRY_ROUTE_PATH, replace: true });
      })
      .catch((e: unknown) => {
        if (active) setError(e instanceof Error ? e.message : i18n._(msg`サインインに失敗しました。`));
      });
    return () => {
      active = false;
    };
  }, [manager, navigate]);

  if (error) {
    return (
      <p role="alert">
        {i18n._(msg`サインインに失敗しました。時間をおいて再度お試しください。`)}
      </p>
    );
  }
  return (
    <p role="status">
      {i18n._(msg`サインイン処理中…`)}
    </p>
  );
}
