import { describe, it, expect } from 'vitest';
import type { User } from 'oidc-client-ts';
import { extractRealmRoles, hasAnyRole, PlatformRole } from './roles';

// IADR-0034: access_token(JWT) の realm_access.roles を一次情報にする。復号不能はフェイルクローズ。

/** テスト用の JWT を組み立てる（署名は検証しないためダミー）。payload は base64url。 */
function makeJwt(payload: unknown): string {
  const b64url = (obj: unknown) =>
    btoa(JSON.stringify(obj)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${b64url({ alg: 'RS256', typ: 'JWT' })}.${b64url(payload)}.sig`;
}

function userWith(token: string | undefined): User {
  return { access_token: token } as unknown as User;
}

describe('extractRealmRoles (IADR-0034)', () => {
  it('reads realm_access.roles from the access token', () => {
    const user = userWith(makeJwt({ realm_access: { roles: ['platform-admin', 'default-roles'] } }));
    expect(extractRealmRoles(user)).toEqual(['platform-admin', 'default-roles']);
  });

  it('returns [] for a null user (fail-closed)', () => {
    expect(extractRealmRoles(null)).toEqual([]);
  });

  it('returns [] when realm_access is absent', () => {
    expect(extractRealmRoles(userWith(makeJwt({ sub: 'u1' })))).toEqual([]);
  });

  it('returns [] for a malformed token (fail-closed)', () => {
    expect(extractRealmRoles(userWith('not-a-jwt'))).toEqual([]);
    expect(extractRealmRoles(userWith(undefined))).toEqual([]);
  });

  it('filters non-string role entries', () => {
    const user = userWith(makeJwt({ realm_access: { roles: ['platform-operator', 42, null] } }));
    expect(extractRealmRoles(user)).toEqual(['platform-operator']);
  });
});

describe('hasAnyRole', () => {
  it('matches when any requested role is owned', () => {
    expect(hasAnyRole([PlatformRole.Operator], PlatformRole.Admin, PlatformRole.Operator)).toBe(true);
  });
  it('is false when none match', () => {
    expect(hasAnyRole(['user'], PlatformRole.Admin)).toBe(false);
  });
  it('is false for an empty owned set', () => {
    expect(hasAnyRole([], PlatformRole.Admin)).toBe(false);
  });
});
