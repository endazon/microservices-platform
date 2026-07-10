import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { userManager as defaultUserManager } from './authConfig';
import type { UserManager } from 'oidc-client-ts';

// Issue #126: OIDC リダイレクト後の認可コード交換。完了後は元の場所（returnTo）へ戻す。
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
        const returnTo = (user.state as { returnTo?: string } | undefined)?.returnTo ?? '/';
        navigate(returnTo, { replace: true });
      })
      .catch((e: unknown) => {
        if (active) setError(e instanceof Error ? e.message : 'サインインに失敗しました。');
      });
    return () => {
      active = false;
    };
  }, [manager, navigate]);

  if (error) {
    return <p role="alert">サインインに失敗しました。時間をおいて再度お試しください。</p>;
  }
  return <p role="status">サインイン処理中…</p>;
}
