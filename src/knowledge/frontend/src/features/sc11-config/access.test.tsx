import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';

// SC-11 #140, IADR-0009/IADR-0030/IADR-0035: 構成ビューアのアクセス制御（管理者・運用者限定＋存在秘匿）。
// 実際のルート定義（RequireRole でラップ済み）をロール別に描画し、権限外は NotFound で
// 画面の存在を示さないことを検証する。許可時のデータ取得はモックする。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { createSc11ConfigRoute, sc11ConfigNav } from './index';

const EMPTY_CONFIG = {
  version: { gitCommit: null, appliedAt: null, appliedBy: null },
  pipeline: [],
  eventBindings: [],
  ports: [],
  connectors: [],
};
const EMPTY_DRIFT = { hasDrift: false, checkedAt: '2026-07-08T00:00:00Z', findings: [] };

async function renderConfigRoute(roles: string[]) {
  return renderUnitRoute((shell) => [createSc11ConfigRoute(shell)], {
    initialEntry: '/admin/config-viewer',
    roles,
  });
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
    await renderConfigRoute(['platform-admin']);
    expect(await screen.findByRole('heading', { name: '構成ビューア' })).toBeInTheDocument();
  });

  it('grants access to platform-operator (ConfigViewer)', async () => {
    await renderConfigRoute(['platform-operator']);
    expect(await screen.findByRole('heading', { name: '構成ビューア' })).toBeInTheDocument();
  });

  it('hides existence (NotFound) for a non-privileged user', async () => {
    await renderConfigRoute(['user']);
    expect(await screen.findByRole('heading', { name: '見つかりませんでした' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: '構成ビューア' })).not.toBeInTheDocument();
    // 権限外では構成 API を呼ばない（存在を推測させない）。
    expect(mocks.apiFetch).not.toHaveBeenCalled();
  });

  it('exposes a nav entry limited to ConfigViewer roles', () => {
    expect(sc11ConfigNav.requiresAnyRole).toEqual(['platform-admin', 'platform-operator']);
    // 05_screens §共通シェル: SC-11 は「運用」グループ。
    expect(sc11ConfigNav.group).toBe('ops');
  });
});
