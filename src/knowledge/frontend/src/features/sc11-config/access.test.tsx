import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-11, FR-15 / IADR-0009 / IADR-0030 / IADR-0035: 構成ビューアのアクセス制御
// （管理者・運用者限定 ＋ 存在秘匿）。
//
// **本ファイルは #140 で作られた観点をそのまま引き継ぐ**（#504 の再実装で画面の中身は
// 総取り替えになったが、アクセス制御の観点は画面の実装に依存しない）:
//   (1) platform-admin は開ける／(2) platform-operator も開ける（ConfigViewer）／
//   (3) 権限外は NotFound（存在を示さない）＋**構成 API を呼ばない**／(4) ナビ項目が
//   ConfigViewer ロールに限られ、グループが「運用」である。
// #504 では (5) として **NotFound の markup が「不在」と一致すること**を足した
// （#490 が Layout で確立した作法。ここでは feature 単体でも同じ描画になることを見る）。
// IADR-0135 決定 4（#519）: 生成フックの経路は `apiRequest` を通る（`apiFetch` では効かない）。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc11ConfigRoute, sc11ConfigNav } from './index';

const EMPTY_CONFIG = {
  version: { gitCommit: null, appliedAt: null, appliedBy: null },
  pipeline: [],
  eventBindings: [],
  ports: [],
  connectors: [],
};
const EMPTY_DRIFT = { hasDrift: false, checkedAt: '2026-08-05T00:00:00Z', findings: [] };

async function renderConfigRoute(roles: string[]) {
  return renderUnitRoute((shell) => [createSc11ConfigRoute(shell)], {
    initialEntry: '/admin/config-viewer',
    roles,
  });
}

beforeEach(() => {
  mocks.apiRequest.mockReset();
  // 実 API と同様にパスで応答を振り分ける（/admin/config=構成, /drift=ドリフト, /history=履歴）。
  mocks.apiRequest.mockImplementation(async (path: string) => {
    if (path === '/admin/config/drift') return jsonResponse(EMPTY_DRIFT);
    if (path === '/admin/config/history') return jsonResponse([]);
    return jsonResponse(EMPTY_CONFIG);
  });
});

describe('SC-11 access control (#140 / #504)', () => {
  it('grants access to platform-admin', async () => {
    await renderConfigRoute(['platform-admin']);
    expect(await screen.findByRole('heading', { name: '実効構成' })).toBeInTheDocument();
  });

  it('grants access to platform-operator (ConfigViewer)', async () => {
    await renderConfigRoute(['platform-operator']);
    expect(await screen.findByRole('heading', { name: '実効構成' })).toBeInTheDocument();
  });

  it('hides existence (NotFound) for a non-privileged user', async () => {
    await renderConfigRoute(['user']);
    expect(await screen.findByRole('heading', { name: '見つかりませんでした' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: '実効構成' })).not.toBeInTheDocument();
    // 権限外では構成 API を呼ばない（要求の有無から存在を推測させない）。
    expect(mocks.apiRequest).not.toHaveBeenCalled();
  });

  // #490 の作法: 「不在」と「権限による秘匿」で描画が割れると、画面の差から資源の存在を推測できる。
  // ここでは feature 単体の描画が `foundation/ui/NotFound` そのものであることを markup で固定する
  // （アプリ全体〔共通シェル込み〕での一致は Layout.test.tsx が見ている）。
  it('produces the same not-found markup as a plain absence', async () => {
    const { NotFound } = await import('@foundation/ui/NotFound');
    const { render } = await import('@testing-library/react');

    await renderConfigRoute(['user']);
    const forbidden = (await screen.findByRole('heading', { name: '見つかりませんでした' }))
      .parentElement?.outerHTML;

    const absent = render(<NotFound />);
    const absentHtml = absent.container.firstElementChild?.outerHTML;

    expect(forbidden).toBeTruthy();
    expect(forbidden).toBe(absentHtml);
  });

  it('exposes a nav entry limited to ConfigViewer roles', () => {
    expect(sc11ConfigNav.requiresAnyRole).toEqual(['platform-admin', 'platform-operator']);
    // 05_screens §共通シェル: SC-11 は「運用」グループ。
    expect(sc11ConfigNav.group).toBe('ops');
  });
});
