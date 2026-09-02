import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-06 → SC-07 → SC-03 / SC-05 → SC-03, UC-04 / UC-06 / UC-03:
// **管理者の運用導線**（データソース → 変換ジョブ → 変換結果／文書管理 → 文書詳細）を
// 1 本のルータで通す（#503）。
//
// 各画面の単体テストは画面ごとの挙動を見るが、導線は「ある画面が出すリンクの宛先が、別の画面が
// 実際に受け取れる形か」を見る。ここが割れていても画面単体のテストは全部通る
// （#503 が 4 画面を 1 本の issue にまとめた理由そのもの）。
//
// ［2026-09-02 / #1139］従前ここには「認証済みの導線を Playwright で実走できないため」と
// 書いていたが、**それは誤りである**（#1099 が実測で覆した。IADR-0330）。認証済みの画面は
// `platform/frontend/e2e/support/bffSession.ts` で実ブラウザ上に描かれ、4 画面それぞれの
// スモークが現に踏んでいる。**この層が在る理由は別**である ——
// ブラウザ E2E は画面ごとに開くため、**画面をまたぐリンクの宛先の突き合わせ**は見ない。
// 1 本のルータへ複数画面を載せて遷移そのものを測るのは、ここでしかできない。
//
// IADR-0135 決定 4（#519）: 4 画面とも orval 生成フックで呼ぶため、モックは `apiRequest` に当てる。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));
vi.mock('@foundation/config/runtimeConfig', () => ({
  appConfig: () => ({ wikiBaseUrl: undefined }),
}));

import { createSc03DocumentRoute } from './sc03-document';
import { createSc05DocumentsRoute } from './sc05-documents';
import { createSc06DataSourcesRoute } from './sc06-datasources';
import { createSc07ConversionsRoute } from './sc07-conversions';

const DOC_ID = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
const TITLE = '経費精算規程';

const SOURCE = {
  id: '11111111-1111-1111-1111-111111111111',
  name: '規程集',
  sourceType: 'filesystem',
  connectionUri: 'smb://fs01/share/規程集',
  status: 'active',
  lastSyncedAt: '2026-07-24T03:00:00Z',
  config: {},
  defaultAttributes: { confidentiality: 'internal' },
  createdAt: '2026-07-01T00:00:00Z',
};
const JOB = {
  id: '9805abcd-0000-0000-0000-000000000002',
  sourceId: SOURCE.id,
  sourceType: 'filesystem',
  originalPath: '経費精算規程.docx',
  status: 'succeeded',
  error: null,
  documentId: DOC_ID,
  markdownUri: 'storage://normalized/keihi.md',
  attempts: 1,
  createdAt: '2026-08-01T00:00:00Z',
  updatedAt: '2026-08-01T01:00:00Z',
};
const DOCUMENT = {
  id: DOC_ID,
  title: TITLE,
  status: 'published',
  markdownUri: 'storage://normalized/keihi.md',
  version: 3,
  attributes: { confidentiality: 'internal' },
  tags: ['経理'],
  createdAt: '2026-07-01T00:00:00Z',
  updatedAt: '2026-08-01T00:00:00Z',
};
const CONTENT = { id: DOC_ID, title: TITLE, markdown: '# 経費精算規程', sourceUri: null };

/** 4 画面ぶんの応答を 1 つの mock で返す（実アプリと同じく画面が必要なものだけを呼ぶ）。 */
function routeResponses(path: string): Promise<unknown> {
  if (path === '/datasources') return Promise.resolve(jsonResponse([SOURCE]));
  if (path.startsWith('/conversion/jobs')) return Promise.resolve(jsonResponse([JOB]));
  if (path === '/documents') return Promise.resolve(jsonResponse([DOCUMENT]));
  if (path === `/documents/${DOC_ID}`) return Promise.resolve(jsonResponse(DOCUMENT));
  if (path === `/documents/${DOC_ID}/content`) return Promise.resolve(jsonResponse(CONTENT));
  if (path === `/documents/${DOC_ID}/versions`) return Promise.resolve(jsonResponse([]));
  return Promise.reject(new Error(`unexpected path: ${path}`));
}

async function renderAdminRoutes(initialEntry: string) {
  return renderUnitRoute(
    (shell) => [
      createSc03DocumentRoute(shell),
      createSc05DocumentsRoute(shell),
      createSc06DataSourcesRoute(shell),
      createSc07ConversionsRoute(shell),
    ],
    { initialEntry, roles: ['platform-admin'] },
  );
}

beforeEach(() => {
  mocks.apiRequest.mockReset();
  mocks.apiRequest.mockImplementation(routeResponses);
});

describe('admin flow (SC-06 → SC-07 → SC-03)', () => {
  // UC-04 →（取り込み → 変換）→ UC-06 → 変換結果の閲覧。計画の遷移図 SC06 → SC07 -- 変換結果 --> SC03。
  it('walks from data sources to the conversion job and on to the converted document', async () => {
    const user = userEvent.setup();
    await renderAdminRoutes('/admin/sources');

    // SC-06: 登録済みソースが見え、変換ジョブへの導線がある。
    expect(await screen.findByText('規程集')).toBeInTheDocument();
    await user.click(screen.getByRole('link', { name: '変換ジョブの状況を見る →' }));

    // SC-07: 完了ジョブが見え、変換結果への導線がある。
    expect(await screen.findByText('経費精算規程.docx')).toBeInTheDocument();
    await user.click(screen.getByRole('link', { name: '変換結果の文書を開く' }));

    // SC-03: 変換結果の本文が表示される。
    expect(await screen.findByRole('heading', { name: TITLE })).toBeInTheDocument();
    expect(screen.getByText(/# 経費精算規程/)).toBeInTheDocument();
  });

  // 逆方向（SC-07 → SC-06）。計画のパンくずが示す階層をリンク 1 本で辿れる。
  it('returns from the conversion jobs to the data sources', async () => {
    const user = userEvent.setup();
    await renderAdminRoutes('/admin/conversions');

    expect(await screen.findByText('経費精算規程.docx')).toBeInTheDocument();
    await user.click(screen.getByRole('link', { name: '← データソース管理へ戻る' }));

    expect(await screen.findByText('規程集')).toBeInTheDocument();
  });

  // UC-03: 文書管理から文書詳細（版履歴を持つ画面）へ。計画の遷移図 SC05 → SC03。
  it('walks from document management to the document detail', async () => {
    const user = userEvent.setup();
    await renderAdminRoutes('/admin/documents');

    await user.click(await screen.findByRole('link', { name: TITLE }));

    expect(await screen.findByRole('heading', { name: TITLE })).toBeInTheDocument();
    expect(screen.getByText(/# 経費精算規程/)).toBeInTheDocument();
  });
});
