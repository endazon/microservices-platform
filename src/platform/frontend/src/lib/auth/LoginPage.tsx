import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { Navigate, useSearch } from '@tanstack/react-router';
import { Button } from '@platform/ui';
import { useAuth } from './useAuth';

// Issue #126 / ADR-0032, IADR-0273, #439: 明示ログイン画面。ボタン押下で BFF のログイン端点へ
// トップレベル遷移し、認可コードフロー（PKCE）は **BFF が実施する**（SPA はトークンを扱わない）。
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
      {/* 05_screens §共通シェル ［2026-08-04 確定］: ブランド表示名は固有名詞として扱い、
       **翻訳カタログの対象としない**（Layout.tsx の注記と IADR-0125 決定 8 を参照）。 */}
      {/* eslint-disable-next-line lingui/no-unlocalized-strings --
          05_screens §共通シェル ［2026-08-04 確定］「翻訳カタログの対象としない」による意図的な例外。 */}
      <h1 className="text-2xl font-semibold text-[--color-fg]">汎用プラットフォーム</h1>
      <p className="mt-2 text-sm text-[--color-fg-muted]">
        {i18n._(msg`社内ナレッジ検索・AI 回答プラットフォーム`)}
      </p>
      {/* ログイン完了後の戻り先は遷移元（?from=。loginRoute の validateSearch が SPA 内部の
          絶対パスへ検証済み）。無ければ主入口へ。BFF 側でも SafeReturnUrl が再検証する。 */}
      <Button variant="primary" className="mt-6" onClick={() => void login(from ?? '/ask')}>
        {i18n._(msg`Keycloak でサインイン`)}
      </Button>
    </main>
  );
}
