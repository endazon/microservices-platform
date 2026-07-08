import { useContext } from 'react';
import { AuthContext } from './AuthContext';
import type { AuthState } from './AuthContext';

// Issue #126: 認証状態へのアクセス。AuthProvider 配下でのみ使用できる。
export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (ctx === null) {
    throw new Error('useAuth must be used within <AuthProvider>');
  }
  return ctx;
}
