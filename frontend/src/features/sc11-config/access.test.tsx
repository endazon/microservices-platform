import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { User } from 'oidc-client-ts';
import { AuthContext } from '@foundation/auth/AuthContext';
import type { AuthState } from '@foundation/auth/AuthContext';

// SC-11 #140, IADR-0009/IADR-0030/IADR-0035: 構成ビューアのアクセス制御（管理者・運用者限定＋存在秘匿）。
// 実際の feature ルート要素（RequireRole でラップ済み）をロール別に描画し、権限外は NotFound で
// 画面の存在を示さないことを検証する。許可時のデータ取得はモックする。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { sc11ConfigFeature } from './index';

const EMPTY_CONFIG = {
  version: { gitCommit: null, appliedAt: null, appliedBy: null },
  pipeline: [],
  eventBindings: [],
  ports: [],
  connectors: [],
};
const EMPTY_DRIFT = { hasDrift: false, checkedAt: '2026-07-08T00:00:00Z', findings: [] };

function makeJwt(payload: unknown): string {
  const b64url = (obj: unknown) =>
    btoa(JSON.stringify(obj)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `h.${b64url(payload)}.sig`;
}

function renderConfigRoute(roles: string[]) {
  const user = { access_token: makeJwt({ realm_access: { roles } }) } as unknown as User;
  const value: AuthState = {
    user,
    isAuthenticated: true,
    isLoading: false,
    login: async () => {},
    logout: async () => {},
  };
  const element = sc11ConfigFeature.routes[0].element;
  return render(
    <AuthContext.Provider value={value}>
      <MemoryRouter>{element}</MemoryRouter>
    </AuthContext.Provider>,
  );
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
  // 実 API と同様にパスで応答を振り分ける（/admin/config=構成, /drift=ドリフト, /history=履歴）。
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/admin/config/drift') return EMPTY_DRIFT;
    if (path === '/admin/config/history') return [];
    return EMPTY_CONFIG;
  });
});

describe('SC-11 access control (#140)', () => {
  it('grants access to platform-admin', async () => {
    renderConfigRoute(['platform-admin']);
    expect(await screen.findByRole('heading', { name: '構成ビューア' })).toBeInTheDocument();
  });

  it('grants access to platform-operator (ConfigViewer)', async () => {
    renderConfigRoute(['platform-operator']);
    expect(await screen.findByRole('heading', { name: '構成ビューア' })).toBeInTheDocument();
  });

  it('hides existence (NotFound) for a non-privileged user', () => {
    renderConfigRoute(['user']);
    expect(screen.queryByRole('heading', { name: '構成ビューア' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '見つかりませんでした' })).toBeInTheDocument();
    // 権限外では構成 API を呼ばない（存在を推測させない）。
    expect(mocks.apiFetch).not.toHaveBeenCalled();
  });

  it('exposes a nav entry limited to ConfigViewer roles', () => {
    expect(sc11ConfigFeature.nav?.requiresAnyRole).toEqual(['platform-admin', 'platform-operator']);
  });
});
