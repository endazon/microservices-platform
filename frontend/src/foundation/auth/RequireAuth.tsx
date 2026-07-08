import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router-dom';
import { useAuth } from './useAuth';

// Issue #126: 認証ガード。未認証は /login へ誘導し、元の場所を state に保持する。
// 認証状態の解決中は中立の読み込み表示を出す（存在秘匿には影響しない画面骨格）。
export function RequireAuth({ children }: { children: ReactNode }) {
  const { isAuthenticated, isLoading } = useAuth();
  const location = useLocation();

  if (isLoading) {
    return <p role="status">読み込み中…</p>;
  }
  if (!isAuthenticated) {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  }
  return <>{children}</>;
}
