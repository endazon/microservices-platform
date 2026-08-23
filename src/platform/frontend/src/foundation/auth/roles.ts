import { useMemo } from 'react';
import type { SessionUser } from './AuthContext';
import { useAuth } from './useAuth';

// IADR-0035 / IADR-0273: ロール判定は `/bff/auth/me` が返す roles を一次情報とする
// （BFF 側の KeycloakRolesClaimsTransformation と同一ソース）。表示制御・存在秘匿の
// 出し分け専用であり、認可の実効境界はサーバ側（AdminOnly=403 / ConfigViewer=404 秘匿）に置く。
// 取得不能・欠落時は空配列（＝権限なし）として扱う（フェイルクローズ）。
//
// 🔴 **`access_token` の JWT 復号はフォールバックである（IADR-0273 決定 7）。**
// 本体の SPA はトークンを持たない（ADR-0032）。残しているのは `ai-stock-trading` submodule の
// テストが旧形（`{ access_token }`）の値を AuthContext へ流し込むためで、AST 側が追随したら
// このフォールバックごと削る。**新しいコードから access_token を供給してはならない。**

export const PlatformRole = {
  Admin: 'platform-admin',
  Operator: 'platform-operator',
} as const;

interface RealmAccess {
  roles?: unknown;
}
interface AccessTokenClaims {
  realm_access?: RealmAccess;
}

/** JWT のペイロード（2 番目のセグメント）を復号して JSON として返す。失敗時は null。 */
function decodeJwtPayload(token: string): unknown {
  const parts = token.split('.');
  if (parts.length < 2) return null;
  try {
    const b64 = parts[1].replace(/-/g, '+').replace(/_/g, '/');
    const pad = b64.length % 4 === 0 ? '' : '='.repeat(4 - (b64.length % 4));
    // atob → バイト列を UTF-8 として復号する（日本語等のマルチバイトに対応）。
    const bytes = atob(b64 + pad);
    const json = decodeURIComponent(
      Array.from(bytes, (c) => '%' + c.charCodeAt(0).toString(16).padStart(2, '0')).join(''),
    );
    return JSON.parse(json);
  } catch {
    return null;
  }
}

/**
 * 現在の身元からレルムロールを取り出す。第 1 情報源は `roles` 配列（`/bff/auth/me`）。
 * 旧形（`access_token` のみ）の値には JWT 復号でフォールバックする（上の注記）。
 * どちらも取れなければ空配列（フェイルクローズ）。
 */
export function extractRealmRoles(user: SessionUser | null): string[] {
  if (!user) return [];
  if (Array.isArray(user.roles)) {
    return user.roles.filter((r): r is string => typeof r === 'string');
  }
  const token = user.access_token;
  if (!token) return [];
  const claims = decodeJwtPayload(token) as AccessTokenClaims | null;
  const roles = claims?.realm_access?.roles;
  return Array.isArray(roles) ? roles.filter((r): r is string => typeof r === 'string') : [];
}

/** ロール集合が指定ロールのいずれかを含むか（純関数。テスト・非フックからも使える）。 */
export function hasAnyRole(owned: readonly string[], ...roles: string[]): boolean {
  return roles.some((r) => owned.includes(r));
}

/** 現在ユーザーの realm ロール一覧。 */
export function useRoles(): string[] {
  const { user } = useAuth();
  return useMemo(() => extractRealmRoles(user), [user]);
}

/** 現在ユーザーが指定ロールのいずれかを持つか（メニュー出し分け・存在秘匿の判定）。 */
export function useHasAnyRole(...roles: string[]): boolean {
  const owned = useRoles();
  return hasAnyRole(owned, ...roles);
}
