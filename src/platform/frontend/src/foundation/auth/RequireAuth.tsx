import { useRef } from 'react';
import type { ReactNode } from 'react';
import { Navigate, useRouterState } from '@tanstack/react-router';
import { useAuth } from './useAuth';

// Issue #126: 認証ガード。未認証は /login へ誘導し、元の場所を保持する。
// 認証状態の解決中は中立の読み込み表示を出す（存在秘匿には影響しない画面骨格）。
// ADR-0031 / IADR-0124: 元の場所は history state ではなく**検索パラメータ**で持つ
// （TanStack Router の型付き検索パラメータで検証でき、リロード・共有でも失われない）。
export function RequireAuth({ children }: { children: ReactNode }) {
  const { isAuthenticated, isLoading } = useAuth();
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  // 遷移元は**このガードが最初に見た場所**で固定する。現在地を読み続けると、/login へ遷移した直後の
  // 再描画で from が '/login' に上書きされ（さらに再遷移し）、戻り先の情報が失われる。
  const from = useRef(pathname).current;

  if (isLoading) {
    return <p role="status">読み込み中…</p>;
  }
  if (!isAuthenticated) {
    return <Navigate to="/login" replace search={{ from }} />;
  }
  return <>{children}</>;
}
