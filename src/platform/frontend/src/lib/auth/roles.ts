import { useMemo } from 'react';
import type { SessionUser } from './AuthContext';
import { useAuth } from './useAuth';

// IADR-0035 / IADR-0273: ロール判定は `/bff/auth/me` が返す roles を一次情報とする
// （BFF 側の KeycloakRolesClaimsTransformation と同一ソース）。表示制御・存在秘匿の
// 出し分け専用であり、認可の実効境界はサーバ側（AdminOnly=403 / ConfigViewer=404 秘匿）に置く。
// 取得不能・欠落時は空配列（＝権限なし）として扱う（フェイルクローズ）。
//
// ［2026-09-03 / AST#414］🔴 **`access_token` の JWT 復号フォールバックは消えた。**
// IADR-0273 決定 7 はそれを「`ai-stock-trading` submodule のテストが旧形（`{ access_token }`）の値を
// AuthContext へ流し込むため」に残し、**「AST 側が追随したらこのフォールバックごと削る」**と定めていた。
// AST#414 で供給側が消えたので、条件どおり削除した。**トークンを読む経路は SPA に 1 つも無い。**

export const PlatformRole = {
  Admin: 'platform-admin',
  Operator: 'platform-operator',
} as const;

/**
 * 現在の身元からレルムロールを取り出す。情報源は `roles` 配列（`/bff/auth/me`）ただ 1 つで、
 * 取れなければ空配列（フェイルクローズ）。
 */
export function extractRealmRoles(user: SessionUser | null): string[] {
  if (!user || !Array.isArray(user.roles)) return [];
  return user.roles.filter((r): r is string => typeof r === 'string');
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
