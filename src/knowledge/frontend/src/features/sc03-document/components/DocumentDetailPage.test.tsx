import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor } from '@testing-library/react';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse, noContent } from '@foundation/testing/bffResponse';

// SC-03, UC-01/UC-02/UC-07, FR-05/FR-06/FR-12: 文書詳細の再実装（#502）＋ 生成フックへの載せ替え（#519）。
// 権限外・不在はいずれも 404 で秘匿され、UI は中立に表示する（IADR-0009 / IADR-0038）。
//
// IADR-0135 決定 4（#519）: 生成コードは mutator（`bffFetch`）→ **`apiRequest`** を通るため、
// モックは `apiRequest` に当てる（`apiFetch` を差し替えても効かない）。
const mocks = vi.hoisted(() => ({
  apiRequest: vi.fn(),
  wikiBaseUrl: undefined as string | undefined,
}));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));
vi.mock('@foundation/config/runtimeConfig', () => ({
  appConfig: () => ({ wikiBaseUrl: mocks.wikiBaseUrl }),
}));

import { createSc03DocumentRoute } from '../routes/sc03DocumentRoute';

const DOC_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';

const DETAIL = {
  id: DOC_ID,
  title: '経費精算規程 v3.2',
  status: 'published',
  markdownUri: 'storage://normalized/keihi.md',
  version: 3,
  attributes: { confidentiality: 'internal', department: 'accounting' },
  tags: ['経理', '規程'],
  createdAt: '2025-10-02T00:00:00Z',
  updatedAt: '2026-05-30T00:00:00Z',
};
const CONTENT = {
  id: DOC_ID,
  title: DETAIL.title,
  markdown: '# 経費精算規程\n\n## 4. 精算スケジュール\n締め日は毎月25日とする。',
  sourceUri: 'https://files.example.co.jp/keihi.docx',
};
const VERSIONS = [
  {
    documentId: DOC_ID,
    version: 3,
    title: DETAIL.title,
    status: 'published',
    changeNote: '§4 改定',
    createdAt: '2026-05-30T00:00:00Z',
  },
  {
    documentId: DOC_ID,
    version: 2,
    title: DETAIL.title,
    status: 'archived',
    changeNote: null,
    createdAt: '2026-01-15T00:00:00Z',
  },
];

/** 「本文なし（204）で返す」ことを指す標識（`null` の JSON 本文と区別するため Symbol を使う）。 */
const NO_BODY = Symbol('204 No Content');

/** BFF の 3 エンドポイントへ応答を割り当てる（既定はすべて成功）。 */
function respond({
  detail = DETAIL as unknown,
  content = CONTENT as unknown,
  versions = VERSIONS as unknown,
}: { detail?: unknown; content?: unknown; versions?: unknown } = {}) {
  const reply = (value: unknown) => {
    if (value instanceof Error) return Promise.reject(value);
    if (value === NO_BODY) return Promise.resolve(noContent());
    return Promise.resolve(jsonResponse(value));
  };
  mocks.apiRequest.mockImplementation((path: string) => {
    if (path.endsWith('/content')) return reply(content);
    if (path.endsWith('/versions')) return reply(versions);
    return reply(detail);
  });
}

async function renderPage() {
  return renderUnitRoute((shell) => [createSc03DocumentRoute(shell)], {
    initialEntry: `/docs/${DOC_ID}`,
  });
}

beforeEach(() => {
  mocks.apiRequest.mockReset();
  mocks.wikiBaseUrl = undefined;
});

afterEach(() => {
  act(() => {
    activate('ja');
  });
});

describe('DocumentDetailPage (SC-03)', () => {
  // UC-01 基本フロー 5 / UC-02 基本フロー 4: 出典・一覧から辿り着いた文書を根拠として読める。
  it('renders title, markdown body, attributes and version history', async () => {
    respond();
    await renderPage();

    expect(await screen.findByRole('heading', { name: '経費精算規程 v3.2' })).toBeInTheDocument();
    expect(screen.getByText(/締め日は毎月25日とする。/)).toBeInTheDocument();
    // 属性はキーだけをラベルへ写像し、値は生値のまま出す。
    // **［2026-08-10 訂正 / #553］理由は「計画が 4 値中 2 値しか表示名を持たない」ではなくなった**
    // —— 4 値の表示名は裁定（2026-08-05 Q7・Q8・派生 Q30）で確定している。
    // **写像の実装先が #541 である**ため、それまでの現状として生値を出している。
    expect(screen.getByText('機密区分:')).toBeInTheDocument();
    expect(screen.getByText('internal')).toBeInTheDocument();
    expect(screen.getByText('部門:')).toBeInTheDocument();
    expect(screen.getByText('accounting')).toBeInTheDocument();
    expect(screen.getByText('経理')).toBeInTheDocument();
    // 版履歴は詳細の成功後に取りに行く（IADR-0126 決定 4）ため、1 段階遅れて現れる。
    expect(await screen.findByText('v3')).toBeInTheDocument();
    expect(screen.getByText('§4 改定')).toBeInTheDocument();
  });

  // 原本リンクは http(s) のときだけリンク化する（押せないものを押させない）。
  it('links to the original only when it is an http(s) uri', async () => {
    respond();
    await renderPage();

    expect(await screen.findByRole('link', { name: CONTENT.sourceUri })).toHaveAttribute(
      'href',
      CONTENT.sourceUri,
    );

    // storage:// はリンクにしない。
    respond({ content: { ...CONTENT, sourceUri: 'storage://normalized/keihi.md' } });
    await renderPage();
    await waitFor(() =>
      expect(
        screen.queryByRole('link', { name: 'storage://normalized/keihi.md' }),
      ).not.toBeInTheDocument(),
    );
  });

  // UC-07: Wiki 閲覧への導線。未設定の環境では導線を出さない。
  it('links to SC-04 only when a wiki base url is configured', async () => {
    respond();
    await renderPage();
    await screen.findByRole('heading', { name: '経費精算規程 v3.2' });
    expect(screen.queryByRole('link', { name: 'Wikiで閲覧' })).not.toBeInTheDocument();

    mocks.wikiBaseUrl = 'https://wiki.example.co.jp';
    await renderPage();
    expect((await screen.findAllByRole('link', { name: 'Wikiで閲覧' }))[0]).toHaveAttribute(
      'href',
      '/wiki',
    );
  });

  // UC-02 例外フロー: 権限の有無は利用者に開示しない。404 は「不在」と同じ中立表示にする。
  it('shows a neutral not-found message on 404 (existence hidden)', async () => {
    respond({ detail: ApiError.fromStatus(404), content: ApiError.fromStatus(404) });
    await renderPage();

    expect(await screen.findByText('文書が見つかりませんでした。')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByText(/権限/)).not.toBeInTheDocument();
  });

  // 5xx は文書の有無ではなくサーバの状態である。404 とは別の表示にする（存在秘匿に反しない）。
  it('distinguishes a server error from a not-found', async () => {
    respond({ detail: ApiError.fromStatus(500), content: ApiError.fromStatus(500) });
    await renderPage();

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('文書の取得に失敗しました'),
    );
    expect(screen.queryByText('文書が見つかりませんでした。')).not.toBeInTheDocument();
  });

  // 本文だけが取れない場合は、その領域のみ縮退して本体表示は続ける。
  it('degrades only the body area when the content request fails', async () => {
    respond({ content: ApiError.fromStatus(500) });
    await renderPage();

    expect(await screen.findByRole('heading', { name: '経費精算規程 v3.2' })).toBeInTheDocument();
    expect(screen.getByText('本文は利用できません。')).toBeInTheDocument();
  });

  // IADR-0126 決定 4: 詳細が 404 のときに版履歴を要求しない（確実に 404 になる往復を出さない）。
  it('never requests the version history when the document is hidden', async () => {
    respond({ detail: ApiError.fromStatus(404), content: ApiError.fromStatus(404) });
    await renderPage();

    await screen.findByText('文書が見つかりませんでした。');
    const paths = mocks.apiRequest.mock.calls.map((call) => String(call[0]));
    expect(paths.some((p) => p.endsWith('/versions'))).toBe(false);
  });

  // 版履歴は補助情報。取れなくても本体表示は続ける。
  it('keeps the document readable when the version history fails', async () => {
    respond({ versions: ApiError.fromStatus(500) });
    await renderPage();

    expect(await screen.findByRole('heading', { name: '経費精算規程 v3.2' })).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText('バージョン')).not.toBeInTheDocument());
  });

  // SC-03, IADR-0135 決定 7［2026-08-06 追記］: 版履歴が**本文なし**（204）で返っても画面は落ちない。
  //
  // `bffFetch` は本文が空なら `{}` を返すため、`okData(res) ?? []` では `??` が発火せず
  // `{}` が `versions.data` に入っていた。**本画面は `versions.data.length > 0` で節を出し分けるため
  // クラッシュはしなかった**（`undefined > 0` は false）が、配列でない値が画面へ届く経路は同じである。
  // `okArray` で必ず配列になることを、`VersionTable` を直接描く将来の変更に備えて固定する。
  it('keeps the document readable when the version history has no body (204)', async () => {
    respond({ versions: NO_BODY });
    await renderPage();

    expect(await screen.findByRole('heading', { name: '経費精算規程 v3.2' })).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText('バージョン')).not.toBeInTheDocument());
  });

  // IADR-0119 決定 1 が着手を保留した機能（保留の根拠と対象の起点 ID は、本画面のプロダクトコード
  // DocumentDetailPage.tsx の冒頭コメントに記載してある）。
  // **「無いこと」を固定するテストである**——保留対象を後から不用意に足すとこのテストが落ちる。
  //
  // **ここに起点 ID を書かないのは意図的である。** check-test-traceability.js は「テスト直前の
  // コメントの起点 ID」を写像として拾う仕様であり、**着手していない機能の ID を書くと
  // 「実装が先行している」と誤って報告される**（実測: 一度書いたところ、allowlist で黙らせる形になった）。
  // 保留の追跡は IADR-0119 とプロダクトコード側のコメントが担う。
  it('does not render the AI suggestion panel or the knowledge-graph link (deferred features)', async () => {
    // **導線の並びを全部描かせた状態で見る。** wikiBaseUrl 未設定だと「Wikiで閲覧」を含む行ごと
    // 描画されず、そこへ保留対象の導線を足しても検出できない（実測: 変異試験 M3 が素通りした）。
    mocks.wikiBaseUrl = 'https://wiki.example.co.jp';
    respond();
    await renderPage();
    await screen.findByRole('heading', { name: '経費精算規程 v3.2' });

    expect(screen.queryByText(/AI 提案/)).not.toBeInTheDocument();
    expect(screen.queryByText(/知識グラフ/)).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /グラフ/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '承認' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '却下' })).not.toBeInTheDocument();
  });

  it('renders in English when the en locale is active', async () => {
    respond();
    activate('en');
    await renderPage();

    expect(await screen.findByText('Normalized document (Markdown) preview')).toBeInTheDocument();
    expect(screen.getByText('Confidentiality:')).toBeInTheDocument();
  });
});
