import { describe, it, expect } from 'vitest';
import type { SessionUser } from './AuthContext';
import { extractRealmRoles, hasAnyRole, PlatformRole } from './roles';

// IADR-0035 / IADR-0273: ロールの一次情報は /bff/auth/me の roles 配列。取得不能はフェイルクローズ。
//
// ［2026-09-03 / AST#414］**旧形（`{ access_token }`）のフォールバックを検証していたケースは削除した。**
// IADR-0273 決定 7 が「AST 側が追随したらフォールバックごと削る」と定めた条件が満たされたためである。
// 代わりに、**トークンを渡しても権限が付かない**ことを否定形で固定する——フォールバックが
// 「親切心で」戻ってくると、`/me` が空ロールを返しても JWT で権限が付く形が復活する。

/** テスト用の JWT を組み立てる（署名は検証しないためダミー）。payload は base64url。 */
function makeJwt(payload: unknown): string {
  const b64url = (obj: unknown) =>
    btoa(JSON.stringify(obj)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${b64url({ alg: 'RS256', typ: 'JWT' })}.${b64url(payload)}.sig`;
}

function sessionUser(roles: unknown): SessionUser {
  return { name: 'tester', subject: 'tester', roles: roles as string[] };
}

describe('extractRealmRoles (IADR-0273)', () => {
  // ★ 一次情報源: /bff/auth/me の roles 配列。
  it('reads roles from the session identity', () => {
    expect(extractRealmRoles(sessionUser(['platform-admin', 'default-roles']))).toEqual([
      'platform-admin',
      'default-roles',
    ]);
  });

  it('filters non-string entries from the roles array', () => {
    expect(extractRealmRoles(sessionUser(['platform-operator', 42, null]))).toEqual([
      'platform-operator',
    ]);
  });

  it('returns [] for a null user (fail-closed)', () => {
    expect(extractRealmRoles(null)).toEqual([]);
  });

  // 🔴 否定形（AST#414 でフォールバックを削った後の要）: **トークンは読まない。**
  // `/me` が空ロールを返しているのに JWT で権限が付く形は、フォールバックが復活した瞬間に戻る。
  it('never reads roles from a bundled token, even when the roles array is empty', () => {
    const user = {
      name: 't',
      subject: 't',
      roles: [] as string[],
      access_token: makeJwt({ realm_access: { roles: ['platform-admin'] } }),
    };
    expect(extractRealmRoles(user as unknown as SessionUser)).toEqual([]);
  });

  // 旧形（roles 配列そのものが無い）も同じく空である——`roles` が唯一の情報源である。
  it('returns [] when the identity has no roles array (fail-closed)', () => {
    const legacy = {
      access_token: makeJwt({ realm_access: { roles: ['platform-admin'] } }),
    } as unknown as SessionUser;
    expect(extractRealmRoles(legacy)).toEqual([]);
  });
});

describe('hasAnyRole', () => {
  it('matches when any requested role is owned', () => {
    expect(hasAnyRole([PlatformRole.Operator], PlatformRole.Admin, PlatformRole.Operator)).toBe(
      true,
    );
  });
  it('is false when none match', () => {
    expect(hasAnyRole(['user'], PlatformRole.Admin)).toBe(false);
  });
  it('is false for an empty owned set', () => {
    expect(hasAnyRole([], PlatformRole.Admin)).toBe(false);
  });
});
