import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { Navigate, useSearch } from '@tanstack/react-router';
import { Button } from '@platform/ui';
import { useAuth } from './useAuth';

// Issue #126: 明示ログイン画面。ボタン押下で Keycloak の認可コードフロー（PKCE）を開始する。
// 既に認証済みなら遷移元（RequireAuth が付ける ?from=）へ戻す。無ければ SC-01（主入口）へ。
// 05_screens §共通シェル: ブランド表示名は「汎用プラットフォーム」で統一する。
export function LoginPage() {
  const { login, isAuthenticated } = useAuth();
  // IADR-0124 決定 3: ルート ID のリテラルを渡す形だけが厳密に型付く。
  const { from } = useSearch({ from: '/login' });

  if (isAuthenticated) {
    // 遷移元は loginRoute の validateSearch が検証済み（SPA 内部の絶対パスのみ）。
    // IADR-0124 決定 5: 実行時に決まる遷移先は Link/Navigate の union で検査できない。
    return <Navigate to={(from ?? '/ask') as '/ask'} replace />;
  }

  return (
    <main className="mx-auto mt-24 max-w-md text-center">
      {/* 05_screens §共通シェル: ブランド表示名。en カタログでも訳さない（IADR-0125 決定 8。Layout.tsx の注記も参照）。 */}
      <h1 className="text-2xl font-semibold text-[--color-fg]">
        {i18n._(msg`汎用プラットフォーム`)}
      </h1>
      <p className="mt-2 text-sm text-[--color-fg-muted]">
        {i18n._(msg`社内ナレッジ検索・AI 回答プラットフォーム`)}
      </p>
      <Button variant="primary" className="mt-6" onClick={() => void login()}>
        {i18n._(msg`Keycloak でサインイン`)}
      </Button>
    </main>
  );
}
