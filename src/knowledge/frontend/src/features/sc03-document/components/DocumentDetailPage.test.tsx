import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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
  // 05_screens §共通シェル / #446: パンくずの**動的な葉**。本画面はこれで文書タイトルを
  // 共通シェルへ渡す。ハーネス（renderUnitRoute）はシェルを描かないので、
  // 渡している値そのものを見る（描画は platform 側 Layout.test.tsx が見る）。
  useBreadcrumbLeaf: vi.fn(),
}));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));
vi.mock('@foundation/routing/breadcrumbLeaf', () => ({
  useBreadcrumbLeaf: mocks.useBreadcrumbLeaf,
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

// SC-03, FR-18 (#450): AI 提案の承認欄が使う 2 本（提案の一覧・辺の型カタログ）。
// 生成フックは `/bff` 接頭辞を落として `apiRequest` を呼ぶ（`orvalMutator`）。
const EDGE_TYPE_ID = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
const OTHER_DOC_ID = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const LINK_SUGGESTION = {
  id: '11111111-1111-1111-1111-111111111111',
  kind: 'link',
  sourceDocumentId: DOC_ID,
  targetDocumentId: OTHER_DOC_ID,
  edgeTypeId: EDGE_TYPE_ID,
  tagValue: null,
  rationale: '両文書が同じ規程を引いている',
  state: 'pending',
  rejectedCount: 0,
  reinstatedReason: null,
  sourceDocumentTitle: '経費精算規程 v3.2',
  targetDocumentTitle: '旅費規程',
  // ADR-0063 決定 3〜5 (#1187): 承認・却下の資格はサーバが行ごとに判定して運ぶ。
  canDecide: true,
};
const TAG_SUGGESTION = {
  ...LINK_SUGGESTION,
  id: '22222222-2222-2222-2222-222222222222',
  kind: 'tag',
  targetDocumentId: null,
  edgeTypeId: null,
  tagValue: '経理',
  rationale: '本文が精算手続きを定めている',
  targetDocumentTitle: null,
};
// 資格を持たない利用者に見えるタグ提案（起点文書への write も管理者ロールも無い）。
const TAG_SUGGESTION_FORBIDDEN = { ...TAG_SUGGESTION, canDecide: false };
const EDGE_TYPES = [{ id: EDGE_TYPE_ID, name: '関連する', layer: 'core', isSymmetric: true }];

// SC-03, UC-07, #1200 / IADR-0365 決定 1: 「Wiki で閲覧」は**権限内の Wiki 台帳**（`GET /bff/wiki/pages`）に
// この文書が載っているときだけ出す。台帳の応答はここで差し替える（既定は**載っていない**）。
const WIKI_PAGE = {
  id: 'page-1',
  documentId: DOC_ID,
  title: DETAIL.title,
  slug: 'keihi-seisan',
  wikiPath: `doc/${DOC_ID}`,
  status: 'Active',
  syncedAt: '2026-05-30T00:00:00Z',
};

/** BFF の各エンドポイントへ応答を割り当てる（既定はすべて成功・提案は 0 件・Wiki 台帳は空）。 */
function respond({
  detail = DETAIL as unknown,
  content = CONTENT as unknown,
  versions = VERSIONS as unknown,
  suggestions = [] as unknown,
  edgeTypes = EDGE_TYPES as unknown,
  approve = undefined as unknown,
  wikiPages = [] as unknown,
}: {
  detail?: unknown;
  content?: unknown;
  versions?: unknown;
  suggestions?: unknown;
  edgeTypes?: unknown;
  /** 承認の口の応答を差し替える（既定は成功。`Error` を渡すと拒否を再現する）。 */
  approve?: unknown;
  wikiPages?: unknown;
} = {}) {
  const reply = (value: unknown) => {
    if (value instanceof Error) return Promise.reject(value);
    if (value === NO_BODY) return Promise.resolve(noContent());
    return Promise.resolve(jsonResponse(value));
  };
  mocks.apiRequest.mockImplementation((path: string) => {
    if (path === '/wiki/pages') return reply(wikiPages);
    // 🔴 提案の口を**最初に**見る。承認・却下は `/graph/suggestions/{id}/approve` であり、
    // 一覧の判定を後ろに置くと `endsWith('/content')` 等と取り違えはしないが、
    // 「承認したのに一覧の応答が返る」形になって観測が壊れる。
    if (path.includes('/graph/suggestions')) {
      if (path.endsWith('/approve') && approve !== undefined) return reply(approve);
      if (path.endsWith('/approve') || path.endsWith('/reject')) {
        return reply({
          ...LINK_SUGGESTION,
          state: path.endsWith('/approve') ? 'approved' : 'rejected',
        });
      }
      return reply(suggestions);
    }
    if (path.includes('/graph/edge-types')) return reply(edgeTypes);
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
  mocks.useBreadcrumbLeaf.mockClear();
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

  // UC-07 / #1200: Wiki 閲覧への導線は**権限内の Wiki 台帳にこの文書が載っているとき**だけ出し、
  // 文書別ディープリンク（`/wiki?doc=<id>`）へ送る。台帳に無ければ出さない（到達できない導線を押させない）。
  it('links to the SC-04 deep link only when the wiki ledger lists the document', async () => {
    const { queryClient } = await (async () => {
      respond();
      return renderPage();
    })();
    await screen.findByRole('heading', { name: '経費精算規程 v3.2' });
    // 台帳の取得が終わってから否定する（読み込み中に出さないだけの実装を緑にしない）。
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    expect(mocks.apiRequest).toHaveBeenCalledWith('/wiki/pages', expect.anything());
    expect(screen.queryByRole('link', { name: 'Wikiで閲覧' })).not.toBeInTheDocument();

    respond({ wikiPages: [WIKI_PAGE] });
    await renderPage();
    expect((await screen.findAllByRole('link', { name: 'Wikiで閲覧' }))[0]).toHaveAttribute(
      'href',
      `/wiki?doc=${DOC_ID}`,
    );
  });

  // 台帳が読めなくても導線を推測で出さない。
  it('hides the wiki link when the ledger cannot be read', async () => {
    respond({ wikiPages: ApiError.fromStatus(502) });
    const { queryClient } = await renderPage();
    await screen.findByRole('heading', { name: '経費精算規程 v3.2' });
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));

    expect(screen.queryByRole('link', { name: 'Wikiで閲覧' })).not.toBeInTheDocument();
    // 本体表示は続く（台帳の失敗で文書詳細を壊さない）。
    expect(screen.getByText(/締め日は毎月25日とする。/)).toBeInTheDocument();
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

  // SC-03, ADR-0070 決定 3・決定 4 / #1254（[[IADR-0388]] 決定 2）:
  // **原本が本文を持たない文書**は、本文の位置へ SC-02 と同じ文言で「本文なし（原本を参照）」を出す。
  // 従前この画面は本文なしの文書を区別する材料を持たず、空の本文をそのまま描いていた。
  it('shows a bodyless document as completed without a body, not as an empty body', async () => {
    respond({ detail: { ...DETAIL, hasBody: false } });
    await renderPage();

    expect(await screen.findByRole('heading', { name: '経費精算規程 v3.2' })).toBeInTheDocument();
    expect(screen.getByText('本文なし（原本を参照）')).toBeInTheDocument();
    // 本文の描画へは落ちない（空の `pre` を「読み込みに失敗した」と読み違えさせない）。
    expect(screen.queryByText(/締め日は毎月25日とする/)).not.toBeInTheDocument();
  });

  // **陽性対照**: 本文ありの文書には出ない（"常に出る" 実装で上の 1 本が緑にならない）。
  // `hasBody` を持たない旧応答（既定の DETAIL）も本文ありとして描く。
  it('shows no bodyless notice for a document that has a body', async () => {
    respond();
    await renderPage();

    expect(await screen.findByText(/締め日は毎月25日とする/)).toBeInTheDocument();
    expect(screen.queryByText('本文なし（原本を参照）')).not.toBeInTheDocument();
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

  // SC-03, SC-18, FR-17, UC-10 (#1240): **ナレッジグラフビュー（SC-18）への導線。**
  //
  // 🔴 **本テストは「無いこと」の固定を反転させたものである。** 従前ここには
  // `does not render the knowledge-graph link (SC-18 belongs to another screen)` が立っており、
  // IADR-0119 の保留（当時 FR-17 の画面側が未着手）を固定していた。**その保留は
  // 2026-08-07（#586）に解除され、繰り延べの相手だった SC-18 の画面も #917 で着地した**ので、
  // 不在の固定は事実に反する。**消さずに反転させる** —— 消すと、次に導線が失われても緑のままになる。
  //
  // 🔴 **リンクが在るだけでは受け入れ基準を満たさない。** 05_screens §SC-18 は
  // 「起点ありの近傍探索が主用途」であり、`root` を持たない `/graph` は照会せず案内文を出す。
  // したがって **`href` の起点まで見る**（`to` だけ見ていると「押しても何も見えない導線」が緑になる）。
  it('links to SC-18 with this document as the graph root', async () => {
    // **導線の並びを全部描かせた状態で見る。** 台帳に載せないと「Wikiで閲覧」が描画されず、
    // 並びの中での位置が変わる（従前の不在テストが台帳を載せていたのと同じ理由）。
    respond({ wikiPages: [WIKI_PAGE] });
    await renderPage();
    await screen.findByRole('heading', { name: '経費精算規程 v3.2' });

    const link = screen.getByRole('link', { name: 'ナレッジグラフで見る' });
    expect(link).toHaveAttribute('href', `/graph?root=${DOC_ID}&hops=2&by=distance`);
  });

  // SC-03, SC-18, ADR-0034 決定 2 (#1240): **陰性対照 —— 404 のときは導線を描かない。**
  //
  // 権限外・不在はいずれも 404 に倒して存在を秘匿している（IADR-0009）。**導線だけが残ると、
  // 「本文は見えないがグラフの起点としては実在する」と読めてしまい、秘匿が導線の側で破れる。**
  // 本文の描画と同じ早期 return の内側に居ることを固定する。
  it('hides the SC-18 link when the document is not visible (existence stays hidden)', async () => {
    respond({ detail: ApiError.fromStatus(404), content: ApiError.fromStatus(404) });
    await renderPage();

    expect(await screen.findByText('文書が見つかりませんでした。')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'ナレッジグラフで見る' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /グラフ/ })).not.toBeInTheDocument();
  });

  // SC-03, FR-18, ADR-0033 決定 7: **AI 提案の承認欄**（#450）。
  //
  // 05_screens §SC-03 は「提案が 0 件のときは欄自体を表示しない」と定める。
  // **見出しだけが残ると、承認すべきものがあるかのように読める。**
  //
  // 🔴 **取得が終わったことを待ってから否定する。** 待たずに `queryBy*` で否定すると、
  // **読み込み中に欄を出さないだけの実装でも緑になる**（実測: 変異試験 M7〔0 件でも欄を描く〕が
  // 素通りした）。`queryClient.isFetching()` が 0 になるまで待つのが、この画面で使える唯一の
  // 決定的な合図である（欄が出ないので「現れるのを待つ」ことができない）。
  it('does not render the suggestion panel when there is nothing pending', async () => {
    respond({ suggestions: [], wikiPages: [WIKI_PAGE] });
    const { queryClient } = await renderPage();
    await screen.findByRole('heading', { name: '経費精算規程 v3.2' });
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));

    expect(screen.queryByRole('heading', { name: 'AI 提案' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '承認' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '却下' })).not.toBeInTheDocument();
  });

  // SC-03, FR-18: 種類・相手の文書・**辺の型**・提案の根拠を示す（05_screens §SC-03 の逐語）。
  // 辺の型名は**辞書で解決する**（ADR-0033 決定 9。DTO は `edgeTypeId` しか持たない）。
  it('renders a link suggestion with its edge type, rationale and both actions', async () => {
    respond({ suggestions: [LINK_SUGGESTION] });
    await renderPage();

    expect(await screen.findByRole('heading', { name: 'AI 提案' })).toBeInTheDocument();
    expect(screen.getByText(/旅費規程/)).toBeInTheDocument();
    // 型名は辞書（/bff/graph/edge-types）から解決した表示名である（GUID を出さない）。
    expect(screen.getByText(/関連する/)).toBeInTheDocument();
    expect(screen.getByText('両文書が同じ規程を引いている')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '承認' })).toBeEnabled();
    expect(screen.getByRole('button', { name: '却下' })).toBeEnabled();
  });

  // SC-03, FR-18, ADR-0063 決定 3〜5, IADR-0364 決定 5 (#1187): **タグ提案の行は資格で 2 つに分ける。**
  // 1187-9: 資格を持つ利用者には承認ボタンが有効で、「準備中」「未実装」の文言が**無い**
  // （IADR-0300 決定 4 の「承認だけを実行不可にする」は反映経路の実装をもって失効した）。
  it('enables approval of a tag suggestion for a user who can decide (no "not implemented" wording)', async () => {
    respond({ suggestions: [TAG_SUGGESTION] });
    await renderPage();

    expect(await screen.findByRole('heading', { name: 'AI 提案' })).toBeInTheDocument();
    expect(screen.getByText(/「経理」を付与/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '承認' })).toBeEnabled();
    expect(screen.getByRole('button', { name: '却下' })).toBeEnabled();
    expect(screen.queryByText(/未実装|準備中/)).not.toBeInTheDocument();
    expect(screen.queryByText(/権限がありません/)).not.toBeInTheDocument();

    // 承認は**その提案の口**へ送る（タグ提案でも経路は同じ）。
    await userEvent.click(screen.getByRole('button', { name: '承認' }));
    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(
          (call) => String(call[0]) === `/graph/suggestions/${TAG_SUGGESTION.id}/approve`,
        ),
      ).toBe(true),
    );
  });

  // 1187-8: 資格を持たない利用者には承認・却下とも押せず、「この文書のタグを編集する権限が無い」が
  // **画面上のテキストとして**読める（決定 4: 却下も同じ権限に従うので両方を塞ぐ）。
  it('disables both actions and explains the missing permission for a user who cannot decide', async () => {
    respond({ suggestions: [TAG_SUGGESTION_FORBIDDEN] });
    await renderPage();

    expect(await screen.findByRole('heading', { name: 'AI 提案' })).toBeInTheDocument();
    expect(screen.getByText(/「経理」を付与/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '承認' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '却下' })).toBeDisabled();
    expect(screen.getByText('この文書のタグを編集する権限がありません。')).toBeInTheDocument();
    expect(screen.queryByText(/未実装|準備中/)).not.toBeInTheDocument();
  });

  // 🔴 旧版の後段は `canDecide` を載せない。**欠けていれば deny 側**に倒す（「できる」と描いて 404 に
  // なるより、「権限が無い」と描くほうが安全側）。
  it('treats a missing canDecide as "cannot decide"', async () => {
    const { canDecide: _omitted, ...withoutFlag } = TAG_SUGGESTION;
    void _omitted;
    respond({ suggestions: [withoutFlag] });
    await renderPage();

    await screen.findByRole('heading', { name: 'AI 提案' });
    expect(screen.getByRole('button', { name: '承認' })).toBeDisabled();
    expect(screen.getByText('この文書のタグを編集する権限がありません。')).toBeInTheDocument();
  });

  // 1187-7 / 1014-3, ADR-0063 決定 2 後段: 辞書に無い値の提案は承認できず却下のみ。後段が
  // 400 `unknown_tag` を本文ごと透過したとき、汎用の「操作できませんでした」ではなく
  // **その事実を読める文言**で出す（利用者が次に取るべき行動＝却下が分かる）。
  it('explains an unknown_tag rejection so the user knows to reject the suggestion', async () => {
    respond({
      suggestions: [TAG_SUGGESTION],
      approve: new ApiError('validation', '入力に誤りがあります。', 400, [], {
        error: 'unknown_tag',
      }),
    });
    await renderPage();
    await screen.findByRole('heading', { name: 'AI 提案' });

    await userEvent.click(screen.getByRole('button', { name: '承認' }));

    expect(
      await screen.findByText('このタグは辞書に無いため反映できません。却下してください。'),
    ).toBeInTheDocument();
    expect(screen.queryByText(/操作できませんでした/)).not.toBeInTheDocument();
    // 却下は引き続き押せる（承認できず却下のみ）。
    expect(screen.getByRole('button', { name: '却下' })).toBeEnabled();
  });

  // 陽性対照: 400 でも `unknown_tag` 以外は汎用エラーのまま（本文を見ずに 400 全部を辞書外と読まない）。
  it('keeps the generic error for a 400 that is not unknown_tag', async () => {
    respond({
      suggestions: [TAG_SUGGESTION],
      approve: new ApiError('validation', '入力に誤りがあります。', 400, [], {
        error: 'unknown_edge_type',
      }),
    });
    await renderPage();
    await screen.findByRole('heading', { name: 'AI 提案' });

    await userEvent.click(screen.getByRole('button', { name: '承認' }));

    expect(await screen.findByText(/操作できませんでした/)).toBeInTheDocument();
    expect(screen.queryByText(/辞書に無いため/)).not.toBeInTheDocument();
  });

  // SC-03: 本欄が描くのは**当該文書を両端のいずれかとする提案**だけである（05_screens §SC-03）。
  // 🔴 **［2026-08-31 / #1104］絞りはサーバ側にある。** 従前ここは client 側の間引きを固定して
  // いたが、後段が `documentId` を受けるようになり、その形は失効した。
  // **観測するのは「要求に当該文書 ID が載ること」**であり、応答の間引きではない。
  it('asks the server for this document only (no client-side filtering)', async () => {
    respond({ suggestions: [LINK_SUGGESTION] });
    await renderPage();

    await screen.findByRole('heading', { name: 'AI 提案' });
    const listingCall = mocks.apiRequest.mock.calls.find(
      (call) =>
        String(call[0]).includes('/graph/suggestions') && !String(call[0]).includes('/appro'),
    );
    expect(listingCall).toBeDefined();
    expect(String(listingCall?.[0])).toContain(`documentId=${DOC_ID}`);
    // 既定は pending（05_screens §SC-03「本欄に既定で表示するのは pending の提案である」）。
    expect(String(listingCall?.[0])).toContain('state=pending');
  });

  // 🔴 **サーバが返したものはそのまま描く**（#1104）。client 側で間引くと、表示件数と取得件数が
  // ずれて 0 件の意味が読めなくなる。**間引きが復活したらここが落ちる** ——
  // 当該文書を端点に持たない行を意図的に返し、それが描かれることを固定する
  // （利用者に見える形ではないが、「絞りはサーバの仕事である」ことの検出器である）。
  it('renders what the server returned without re-filtering it', async () => {
    const fromServer = {
      ...LINK_SUGGESTION,
      id: '33333333-3333-3333-3333-333333333333',
      sourceDocumentId: OTHER_DOC_ID,
      targetDocumentId: '44444444-4444-4444-4444-444444444444',
      sourceDocumentTitle: '就業規則',
      targetDocumentTitle: '育児介護休業規程',
      rationale: 'サーバが返した行（画面は絞り直さない）',
    };
    respond({ suggestions: [fromServer] });
    await renderPage();

    await screen.findByRole('heading', { name: 'AI 提案' });
    expect(screen.getByText('サーバが返した行（画面は絞り直さない）')).toBeInTheDocument();
  });

  // SC-03, ADR-0033 決定 7: 承認・却下は **1 件ずつ、その提案の口へ** 送る。
  // 🔴 **パスまで見る。** 状態だけ見ていると、承認が別の提案へ飛んでも緑のままである。
  it('posts approve and reject to the endpoint of that single suggestion', async () => {
    respond({ suggestions: [LINK_SUGGESTION] });
    await renderPage();
    await screen.findByRole('heading', { name: 'AI 提案' });

    await userEvent.click(screen.getByRole('button', { name: '承認' }));
    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(
          (call) => String(call[0]) === `/graph/suggestions/${LINK_SUGGESTION.id}/approve`,
        ),
      ).toBe(true),
    );

    await userEvent.click(screen.getByRole('button', { name: '却下' }));
    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(
          (call) => String(call[0]) === `/graph/suggestions/${LINK_SUGGESTION.id}/reject`,
        ),
      ).toBe(true),
    );
  });

  // 🔴 FR-18・05_screens §SC-21「描いてはいけないもの」: **一括承認・一括却下を置かない。**
  // 承認は両端の文書の内容を見て 1 件ずつ行うものであり、まとめる口は画面にも API にも作らない。
  //
  // **陽性対照つき**——単票のボタンが在ることを先に測る（無ければ否定形は自明に成り立つ）。
  it('never offers a bulk approve or reject action', async () => {
    respond({ suggestions: [LINK_SUGGESTION, TAG_SUGGESTION] });
    await renderPage();
    await screen.findByRole('heading', { name: 'AI 提案' });

    expect(screen.getAllByRole('button', { name: '却下' })).toHaveLength(2);
    expect(screen.queryByRole('button', { name: /すべて|一括|まとめて/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  });

  // 05_screens §SC-03: 本欄から SC-21（AI 提案一覧）への導線を置く。
  it('links to the suggestion listing from the panel', async () => {
    respond({ suggestions: [LINK_SUGGESTION] });
    await renderPage();
    await screen.findByRole('heading', { name: 'AI 提案' });

    expect(screen.getByRole('link', { name: 'AI 提案の一覧を見る' })).toHaveAttribute(
      'href',
      expect.stringContaining('/ai-suggestions'),
    );
  });

  // 🔴 **「提案が無い」へ縮退しない。** 引けないことと 0 件は利用者にとって別の意味である
  // （SC-21 の一覧と同じ判断）。本体の表示は妨げない。
  it('does not degrade a failed suggestion fetch into an empty panel', async () => {
    respond({ suggestions: ApiError.fromStatus(500) });
    await renderPage();

    expect(await screen.findByRole('heading', { name: '経費精算規程 v3.2' })).toBeInTheDocument();
    expect(await screen.findByText(/AI 提案を取得できませんでした/)).toBeInTheDocument();
  });

  // 計画 05_screens が「**本画面はバックリンク欄を持たない**」「バックリンク欄・ローカルグラフは
  // Wiki.js 側のみに置く。本画面には併置しない」と確定している（2026-08-02 の利用者裁定）。
  // **「無いこと」を固定するテストである** —— 併置は恒久の禁止ではなく、Wiki.js 側の実現性が
  // 確認できた時点で改めて判断する取り決めなので、**足すときにこのテストが落ちて気づける**形にしておく。
  //
  // **既存の「無いこと」テストはこれを見ていなかった** —— あちらが見るのは AI 提案欄と
  // 知識グラフ導線だけで、**バックリンク欄の不在は誰も固定していなかった**（#449 で実測）。
  //
  // **ここに起点 ID を書かないのは意図的である** —— check-test-traceability.js が
  // 未着手機能の ID を「実装が先行している」と誤報するためである。
  // 🔴 **［2026-09-05 / #1240］従前この理由づけは「上の保留テストと同じ理由」と書いていた。**
  // その「上の保留テスト」（知識グラフ導線の不在）は**存在の固定へ反転して消えた**ので、
  // 参照先を失った。**理由そのものは本テストについて生きている** —— バックリンク欄・
  // ローカルグラフは SC-04 側の実現方式が計画で未確定であり、まだ着手していない。
  //
  // 🔴 **上に足した SC-18 導線はこのテストに当たらない。** 語が違う（`ナレッジグラフ` は
  // `ローカルグラフ` の正規表現に当たらない）だけでなく、**当たってはならない** ——
  // 計画は「SC-03 に置くのは SC-18 への導線と AI 提案の承認欄の 2 つのみ」と定めており、
  // 導線は置くもの、バックリンク欄は置かないものである。
  it('does not render a backlink panel or a local graph (they belong to the wiki screen only)', async () => {
    // 導線の並びを全部描かせた状態で見る（台帳に載せないと Wiki の導線が消えて検出できない）。
    respond({ wikiPages: [WIKI_PAGE] });
    await renderPage();
    await screen.findByRole('heading', { name: '経費精算規程 v3.2' });

    expect(screen.queryByText(/バックリンク/)).not.toBeInTheDocument();
    // 計画が定める 2 欄の見出し（「このページを参照している文書」「このページが参照している文書」）。
    expect(screen.queryByText(/参照している文書/)).not.toBeInTheDocument();
    expect(screen.queryByText(/ローカルグラフ/)).not.toBeInTheDocument();
    // 見出しとして足された場合も捕まえる（本文中の語だけを見ていると見出しを取りこぼす）。
    expect(
      screen.queryByRole('heading', { name: /バックリンク|被参照|参照元/ }),
    ).not.toBeInTheDocument();
  });

  it('renders in English when the en locale is active', async () => {
    respond();
    activate('en');
    await renderPage();

    expect(await screen.findByText('Normalized document (Markdown) preview')).toBeInTheDocument();
    expect(screen.getByText('Confidentiality:')).toBeInTheDocument();
  });
});

// 05_screens §共通シェル「パンくず・権限バッジ」（#446）: 本画面のパンくずは
// `ホーム / 検索結果 / <文書タイトル>` であり、**最後の段は実行時にしか決まらない**。
// 宣言（`sc03DocumentBreadcrumb`）は自分の段を持たず、ここが葉を供給する。
describe('SC-03 breadcrumb leaf (#446)', () => {
  const leaves = () => mocks.useBreadcrumbLeaf.mock.calls.map(([leaf]) => leaf);

  it('publishes the document title as the breadcrumb leaf', async () => {
    respond();
    await renderPage();
    await screen.findByRole('heading', { name: '経費精算規程 v3.2' });
    expect(leaves()).toContain('経費精算規程 v3.2');
  });

  // 🔴 取得前・取得失敗時は葉を出さない（未確定の文字列をパンくずへ描かない）。
  it('publishes nothing but undefined when the document cannot be fetched', async () => {
    respond({ detail: ApiError.fromStatus(404), content: ApiError.fromStatus(404) });
    await renderPage();
    await screen.findByText('文書が見つかりませんでした。');
    expect(leaves().every((leaf) => leaf === undefined)).toBe(true);
  });

  // 🔴 フックは**早期 return より前**で呼ばれること。読み込み中の分岐は `return` するので、
  // 呼び出しを後ろへ動かすと「読み込みが終わるまでパンくずが更新されない」ではなく
  // React のフック規則違反になる。ここでは「読み込み中でも呼ばれ、葉は undefined」を見る。
  it('calls the hook while the document is still loading (before the early return)', async () => {
    // どの口も解決しない＝ずっと pending。
    mocks.apiRequest.mockImplementation(() => new Promise(() => {}));
    await renderPage();

    expect(await screen.findByText('読み込み中…')).toBeInTheDocument();
    expect(mocks.useBreadcrumbLeaf).toHaveBeenCalled();
    expect(leaves().every((leaf) => leaf === undefined)).toBe(true);
  });
});
