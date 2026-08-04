import { useEffect, useState } from 'react';
import { useNavigate } from '@tanstack/react-router';
import { userManager as defaultUserManager } from './authConfig';
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
        const raw = (user.state as { returnTo?: string } | undefined)?.returnTo;
        const returnTo =
          typeof raw === 'string' && raw.startsWith('/') && !raw.startsWith('//') ? raw : '/ask';
        // IADR-0124 決定 5: 実行時に決まる遷移先は Link/navigate の union で検査できない。
        void navigate({ to: returnTo as '/ask', replace: true });
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
