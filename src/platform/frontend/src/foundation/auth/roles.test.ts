import { describe, it, expect } from 'vitest';
import type { SessionUser } from './AuthContext';
import { extractRealmRoles, hasAnyRole, PlatformRole } from './roles';

// IADR-0035 / IADR-0273: ロールの一次情報は /bff/auth/me の roles 配列。取得不能はフェイルクローズ。
// access_token(JWT) の復号は AST submodule 互換のフォールバック（IADR-0273 決定 7）。

/** テスト用の JWT を組み立てる（署名は検証しないためダミー）。payload は base64url。 */
function makeJwt(payload: unknown): string {
  const b64url = (obj: unknown) =>
    btoa(JSON.stringify(obj)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${b64url({ alg: 'RS256', typ: 'JWT' })}.${b64url(payload)}.sig`;
}

function sessionUser(roles: unknown): SessionUser {
  return { name: 'tester', subject: 'tester', roles: roles as string[] };
}

/** 旧形（AST のテストが流し込む形）: roles 配列を持たず access_token だけを持つ。 */
function legacyUser(token: string | undefined): SessionUser {
  return { access_token: token } as unknown as SessionUser;
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

  // 🔴 優先順位: roles 配列があるなら access_token は**読まない**（トークンを一次情報へ
  // 昇格させない。逆転すると「/me が空ロールを返しても JWT で権限が付く」形になる）。
  it('prefers the roles array over a bundled access_token', () => {
    const user = {
      name: 't',
      subject: 't',
      roles: [] as string[],
      access_token: makeJwt({ realm_access: { roles: ['platform-admin'] } }),
    };
    expect(extractRealmRoles(user)).toEqual([]);
  });

  it('returns [] for a null user (fail-closed)', () => {
    expect(extractRealmRoles(null)).toEqual([]);
  });

  // ── AST submodule 互換のフォールバック（旧形。IADR-0273 決定 7。AST 追随後に削る）

  it('falls back to decoding realm_access.roles from a legacy access_token', () => {
    const user = legacyUser(makeJwt({ realm_access: { roles: ['platform-admin'] } }));
    expect(extractRealmRoles(user)).toEqual(['platform-admin']);
  });

  it('returns [] when the legacy token lacks realm_access', () => {
    expect(extractRealmRoles(legacyUser(makeJwt({ sub: 'u1' })))).toEqual([]);
  });

  it('returns [] for a malformed legacy token (fail-closed)', () => {
    expect(extractRealmRoles(legacyUser('not-a-jwt'))).toEqual([]);
    expect(extractRealmRoles(legacyUser(undefined))).toEqual([]);
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
