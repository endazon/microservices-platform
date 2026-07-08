import { useMemo } from 'react';
import type { User } from 'oidc-client-ts';
import { useAuth } from './useAuth';

// IADR-0035: SPA のロール判定は access_token(JWT) の realm_access.roles を一次情報とする
// （バックエンドの KeycloakRolesClaimsTransformation と同一ソース）。表示制御・存在秘匿の
// 出し分け専用であり、認可の実効境界はサーバ側（AdminOnly=403 / ConfigViewer=404 秘匿）に置く。
// 復号不能・欠落時は空配列（＝権限なし）として扱う（フェイルクローズ）。

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

/** access_token(JWT) の realm_access.roles を取り出す。取得不能・欠落時は空配列（フェイルクローズ）。 */
export function extractRealmRoles(user: User | null): string[] {
  const token = user?.access_token;
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
