import { useMemo, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import type { UseQueryResult } from '@tanstack/react-query';
import { Button, EmptyState, Input, Kv, KvItem, Label, Panel } from '@platform/ui';
import { QueryState } from '@foundation/ui/QueryState';
import { useBreadcrumbLeaf } from '@foundation/routing/breadcrumbLeaf';
import { formatDateTime } from '@foundation/utils/formatDateTime';
import type {
  WikiPageSummary,
  WikiPageView,
  WikiSearchHit,
} from '@foundation/api/generated/bff.schemas';
import { isNotFound, useWikiPage, useWikiPages, useWikiSearchHits } from '../api/useWikiQueries';
import { sanitizeWikiHtml } from '../types/sanitizeWikiHtml';
import type { WikiSearch } from '../types/wikiSearch';

// SC-04, UC-07, FR-13, FR-05, ADR-0073 決定 1・2・4 / IADR-0355 / IADR-0365 (#1200): Wiki 閲覧画面（ルート /wiki）。
//
// **ページツリー・本文・検索を基盤 SPA が描く。** 取得はすべて `/bff/wiki/*`（→ WikiService）経由であり、
// 利用者が Wiki の内容へ到達する経路は**その 1 本**に限られる（ADR-0073 決定 1）。
//
// 🔴 **根拠が変わった。** 従前の本画面（`WikiAccessPage`）は `wikiBaseUrl` への外部リンク 1 本であり、
// 冒頭コメントは「到達はゲートウェイ（ABAC）経由に限定される」と述べていたが、**リンク先の Wiki.js 本体 UI は
// 前段（WikiService）を通っていなかった**（ADR-0073 §実測 1。一覧・ツリー・本文・検索のすべてが local では
// 前段を迂回して読めた）。いまは外部リンクが無く、画面が読む口は BFF だけなので、上の文が初めて真になる。
// **「dev では ABAC の統制が働かない」**（Wiki.js の直接露出は管理 UI のために残る。ADR-0073 決定 5）ことは
// `deploy/local/README.md` §Wiki 閲覧の到達 が持つ。
//
// **［2026-09-12 / UI/UX 改善］hi-fi モック（sc-04）の構造へ寄せた。**
//   本文の区画は `.panel`＝`Panel`、最終同期日時は `.sub` に代えて `Kv`（項目名と値の対）である。
//   待ち・空・失敗は **`QueryState` の 1 本**へ統一した（ツリー・検索・本文の 3 か所それぞれ）。
//   🔴 **404 だけは `QueryState` の手前で分ける** —— 404 は「無い」であって「失敗」ではない。
//   `ErrorState`（`role="alert"` ＋ 再試行）で出すと**存在秘匿が壊れ**、押しても必ず同じ 404 になる
//   再試行を押させることになる（SC-03 / SC-18 と同じ作法。E2E の陰性対照が固定している）。
//
// ■ 描くもの（05_screens §SC-04 §主要素）
//   - ページツリー（権限内のみ）: `GET /bff/wiki/pages` の 1 回。台帳は平坦（`wikiPath` = `doc/<id>`）で
//     後段が題名順に返すので、**題名順の一覧**として描く（階層を SPA 側で捏造しない。IADR-0365 決定 2）。
//   - 検索（権限内のみ）: `GET /bff/wiki/search?q=`。並びは Wiki.js の関連度順（IADR-0335）。
//   - 本文: **Wiki.js が描画した HTML** を DOMPurify で sanitize して描く（IADR-0365 決定 3。
//     `types/sanitizeWikiHtml.ts`）。SPA は Markdown を再レンダリングしない。
//   - 最終同期日時（`syncedAt`）と文書詳細（SC-03）への復帰リンク。
// ■ 描かないもの
//   - **Wiki.js 本体 UI への外部リンク**（`target="_blank"` は 1 本も無い。#1200 受け入れ基準）。
//   - 編集導線（ADR-0073 決定 6 は未決）。バックリンク欄・ローカルグラフ（計画が未確定）。
//   - 左レール（共通シェル）のページツリー置換 —— シェルは platform の射程。画面仕様書 §未決事項。
// ■ 存在秘匿（IADR-0009）
//   一覧・検索の空と本文の 404 は「権限が無い」と「無い」を区別せず、中立の文で描く。
//   **502（Wiki.js 不達）は空で隠さない** —— 「壊れている」は別の軸である（IADR-0355 決定 5）。

export function WikiBrowsePage() {
  const { t } = useLingui();
  const search: WikiSearch = useSearch({ from: '/_shell/wiki' });
  const navigate = useNavigate({ from: '/wiki' });

  const pages = useWikiPages();
  const page = useWikiPage(search);
  const hits = useWikiSearchHits(search.q);

  // 05_screens §共通シェル / #446: パンくずの葉はページの題名（取得前は描かない）。
  useBreadcrumbLeaf(page.data?.title);

  const [draft, setDraft] = useState(search.q ?? '');
  const hasSelection = search.page !== undefined || search.doc !== undefined;

  function onSearch(e: React.FormEvent) {
    e.preventDefault();
    const q = draft.trim();
    void navigate({ search: (prev: WikiSearch) => ({ ...prev, q: q === '' ? undefined : q }) });
  }

  return (
    <section>
      {/* hi-fi `.ttl` / `.sub`。見出しの文言は変えない（単体テストと E2E が画面を特定している）。 */}
      <h1 className="text-[17px] font-medium text-fg">
        <Trans>Wiki 閲覧</Trans>
      </h1>
      <p className="mb-n4 text-xs text-fg-muted">
        <Trans>閲覧権限のある Wiki ページだけが並びます。ページを選ぶと本文を表示します。</Trans>
      </p>

      <div className="flex flex-col gap-n4 lg:flex-row">
        <div className="flex min-w-0 flex-col lg:w-80">
          <Panel heading={<Trans>検索</Trans>}>
            <form onSubmit={onSearch} className="flex items-end gap-n2">
              <div className="grow">
                <Label htmlFor="wiki-search" className="sr-only">
                  <Trans>Wiki を検索</Trans>
                </Label>
                <Input
                  id="wiki-search"
                  value={draft}
                  onChange={(e) => setDraft(e.target.value)}
                  placeholder={t`検索語を入力…`}
                />
              </div>
              <Button type="submit" variant="primary">
                <Trans>検索</Trans>
              </Button>
            </form>
            {/* 検索していないときは照会そのものが無効（`enabled: false`）なので `QueryState` へ渡さない
                ——無効な照会は TanStack Query では永遠に待ちである。 */}
            {search.q === undefined ? null : (
              <SearchResults hits={hits} q={search.q} current={search.page} />
            )}
          </Panel>

          <Panel heading={<Trans>ページツリー</Trans>}>
            <PageTree pages={pages} q={search.q} current={search.page} />
          </Panel>
        </div>

        <div className="min-w-0 grow">
          {hasSelection ? (
            <PageBody page={page} />
          ) : (
            <EmptyState
              title={t`ページが選ばれていません。`}
              description={t`ページツリーまたは検索結果からページを選んでください。`}
            />
          )}
        </div>
      </div>
    </section>
  );
}

/**
 * ページツリー。**題名順の一覧**である（台帳に階層は無い）。
 * 空は中立の文 —— deny-by-default の空と「まだ 1 件も無い」を区別しない（存在秘匿）。
 */
function PageTree({
  pages,
  q,
  current,
}: {
  pages: UseQueryResult<WikiPageSummary[], unknown>;
  q: string | undefined;
  current: string | undefined;
}) {
  const { t } = useLingui();
  return (
    <QueryState
      query={pages}
      loadingLabel={t`ページツリーを読み込み中…`}
      errorTitle={t`ページツリーを取得できませんでした。`}
      isEmpty={(data) => data.length === 0}
      empty={
        <EmptyState
          title={t`閲覧できる Wiki ページはありません。`}
          description={t`取り込みが済むとここに並びます。`}
        />
      }
    >
      {(data) => (
        <nav aria-label={t`ページツリー`}>
          <ul className="flex flex-col gap-1 text-sm">
            {data.map((p) => (
              <li key={p.id}>
                <Link
                  to="/wiki"
                  search={{ q, page: p.slug }}
                  aria-current={p.slug === current ? 'page' : undefined}
                  className="text-brand hover:underline aria-[current=page]:font-semibold"
                >
                  {p.title}
                </Link>
              </li>
            ))}
          </ul>
        </nav>
      )}
    </QueryState>
  );
}

/** 検索結果。並びは Wiki.js の関連度順を保つ（並べ替えない）。502 は空で隠さない。 */
function SearchResults({
  hits,
  q,
  current,
}: {
  hits: UseQueryResult<WikiSearchHit[], unknown>;
  q: string;
  current: string | undefined;
}) {
  const { t } = useLingui();
  return (
    <div className="mt-n3">
      <QueryState
        query={hits}
        loadingLabel={t`検索中…`}
        errorTitle={t`Wiki の検索に失敗しました。`}
        errorDescription={t`Wiki に到達できない可能性があります。`}
        isEmpty={(data) => data.length === 0}
        empty={
          <EmptyState
            title={t`該当するページはありません。`}
            description={t`別の語で検索してください。`}
          />
        }
      >
        {(data) => (
          <ul aria-label={t`検索結果`} className="flex flex-col gap-1 text-sm">
            {data.map((h) => (
              <li key={h.id}>
                <Link
                  to="/wiki"
                  search={{ q, page: h.slug }}
                  aria-current={h.slug === current ? 'page' : undefined}
                  className="text-brand hover:underline aria-[current=page]:font-semibold"
                >
                  {h.title}
                </Link>
              </li>
            ))}
          </ul>
        )}
      </QueryState>
    </div>
  );
}

/**
 * 本文。**Wiki.js が描画した HTML** を sanitize して描く。
 * 404 は中立（権限外・不存在・アーカイブ済みを区別しない）。それ以外の失敗はサーバの状態である。
 */
function PageBody({ page }: { page: UseQueryResult<WikiPageView, unknown> }) {
  const { t } = useLingui();
  const html = useMemo(() => (page.data ? sanitizeWikiHtml(page.data.content) : ''), [page.data]);

  // 🔴 404 は「無い」であって「失敗」ではない（存在秘匿）。**`role="alert"` も再試行も付けない。**
  if (isNotFound(page.error)) {
    return (
      <EmptyState
        title={t`ページが見つかりませんでした。`}
        description={t`ページツリーまたは検索から辿り直してください。`}
      />
    );
  }

  return (
    <QueryState
      query={page}
      loadingLabel={t`本文を読み込み中…`}
      errorTitle={t`本文を取得できませんでした。`}
    >
      {(view) => (
        <>
          {/* hi-fi `.ttl`: ページの題名。**`h2`** である（画面の `h1` は「Wiki 閲覧」）。 */}
          <h2 className="text-[17px] font-medium text-fg">{view.title}</h2>
          <Kv columns={2} className="mb-n3 mt-n2">
            <KvItem label={t`最終同期`}>{formatDateTime(view.syncedAt)}</KvItem>
            <KvItem label={t`正規化文書`}>
              <Link
                to="/docs/$id"
                params={{ id: view.documentId }}
                className="text-brand hover:underline"
              >
                <Trans>文書詳細へ戻る</Trans>
              </Link>
            </KvItem>
          </Kv>
          <Panel>
            {/* IADR-0365 決定 3: sanitize 済み。生の `content` をここへ渡さない。 */}
            <article
              className="prose max-w-none text-sm"
              data-testid="wiki-page-content"
              dangerouslySetInnerHTML={{ __html: html }}
            />
          </Panel>
        </>
      )}
    </QueryState>
  );
}
