import { useMemo, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import type { UseQueryResult } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Input,
  Label,
} from '@platform/ui';
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

// SC-04, UC-07, FR-13, FR-05, ADR-0073 決定 1・2・4 / IADR-0355 / IADR-0367 (#1200): Wiki 閲覧画面（ルート /wiki）。
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
// ■ 描くもの（05_screens §SC-04 §主要素）
//   - ページツリー（権限内のみ）: `GET /bff/wiki/pages` の 1 回。台帳は平坦（`wikiPath` = `doc/<id>`）で
//     後段が題名順に返すので、**題名順の一覧**として描く（階層を SPA 側で捏造しない。IADR-0367 決定 2）。
//   - 検索（権限内のみ）: `GET /bff/wiki/search?q=`。並びは Wiki.js の関連度順（IADR-0335）。
//   - 本文: **Wiki.js が描画した HTML** を DOMPurify で sanitize して描く（IADR-0367 決定 3。
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
      <h1 className="text-lg font-semibold text-[--color-fg]">
        <Trans>Wiki 閲覧</Trans>
      </h1>
      <p className="mb-4 text-sm text-[--color-fg-muted]">
        <Trans>閲覧権限のある Wiki ページだけが並びます。ページを選ぶと本文を表示します。</Trans>
      </p>

      <div className="flex flex-col gap-4 lg:flex-row">
        <div className="flex min-w-0 flex-col gap-3 lg:w-80">
          <Card>
            <CardHeader>
              <CardTitle as="h2">
                <Trans>検索</Trans>
              </CardTitle>
            </CardHeader>
            <CardContent>
              <form onSubmit={onSearch} className="flex items-end gap-2">
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
              {search.q !== undefined && (
                <SearchResults hits={hits} q={search.q} current={search.page} />
              )}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle as="h2">
                <Trans>ページツリー</Trans>
              </CardTitle>
            </CardHeader>
            <CardContent>
              <PageTree pages={pages} q={search.q} current={search.page} />
            </CardContent>
          </Card>
        </div>

        <div className="min-w-0 grow">
          {hasSelection ? (
            <PageBody page={page} />
          ) : (
            <p role="note" className="text-sm text-[--color-fg-muted]">
              <Trans>ページツリーまたは検索結果からページを選んでください。</Trans>
            </p>
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
  if (pages.isPending) {
    return (
      <p role="status" className="text-sm text-[--color-fg-muted]">
        <Trans>ページツリーを読み込み中…</Trans>
      </p>
    );
  }
  if (pages.isError) {
    return (
      <Alert tone="danger" role="alert" label={t`エラー`}>
        <Trans>ページツリーを取得できませんでした。</Trans>
      </Alert>
    );
  }
  if (pages.data.length === 0) {
    return (
      <p className="text-sm text-[--color-fg-muted]">
        <Trans>閲覧できる Wiki ページはありません。</Trans>
      </p>
    );
  }
  return (
    <nav aria-label={t`ページツリー`}>
      <ul className="flex flex-col gap-1 text-sm">
        {pages.data.map((p) => (
          <li key={p.id}>
            <Link
              to="/wiki"
              search={{ q, page: p.slug }}
              aria-current={p.slug === current ? 'page' : undefined}
              className="text-[--color-brand] hover:underline aria-[current=page]:font-semibold"
            >
              {p.title}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
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
  if (hits.isPending) {
    return (
      <p role="status" className="mt-3 text-sm text-[--color-fg-muted]">
        <Trans>検索中…</Trans>
      </p>
    );
  }
  if (hits.isError) {
    return (
      <Alert tone="danger" role="alert" className="mt-3" label={t`エラー`}>
        <Trans>Wiki の検索に失敗しました。Wiki に到達できない可能性があります。</Trans>
      </Alert>
    );
  }
  if (hits.data.length === 0) {
    return (
      <p className="mt-3 text-sm text-[--color-fg-muted]">
        <Trans>該当するページはありません。</Trans>
      </p>
    );
  }
  return (
    <ul aria-label={t`検索結果`} className="mt-3 flex flex-col gap-1 text-sm">
      {hits.data.map((h) => (
        <li key={h.id}>
          <Link
            to="/wiki"
            search={{ q, page: h.slug }}
            aria-current={h.slug === current ? 'page' : undefined}
            className="text-[--color-brand] hover:underline aria-[current=page]:font-semibold"
          >
            {h.title}
          </Link>
        </li>
      ))}
    </ul>
  );
}

/**
 * 本文。**Wiki.js が描画した HTML** を sanitize して描く。
 * 404 は中立（権限外・不存在・アーカイブ済みを区別しない）。それ以外の失敗はサーバの状態として `Alert`。
 */
function PageBody({ page }: { page: UseQueryResult<WikiPageView, unknown> }) {
  const { t } = useLingui();
  const html = useMemo(() => (page.data ? sanitizeWikiHtml(page.data.content) : ''), [page.data]);

  if (page.isPending) {
    return (
      <p role="status" className="text-sm text-[--color-fg-muted]">
        <Trans>本文を読み込み中…</Trans>
      </p>
    );
  }
  if (page.isError) {
    return isNotFound(page.error) ? (
      <p className="text-sm">
        <Trans>ページが見つかりませんでした。</Trans>
      </p>
    ) : (
      <Alert tone="danger" role="alert" label={t`エラー`}>
        <Trans>本文を取得できませんでした。</Trans>
      </Alert>
    );
  }

  const view = page.data;
  const syncedAt = formatDateTime(view.syncedAt);
  return (
    <Card>
      <CardHeader>
        <CardTitle as="h2">{view.title}</CardTitle>
      </CardHeader>
      <CardContent>
        {/* IADR-0367 決定 3: sanitize 済み。生の `content` をここへ渡さない。 */}
        <article
          className="prose max-w-none text-sm"
          data-testid="wiki-page-content"
          dangerouslySetInnerHTML={{ __html: html }}
        />
        <p className="mt-4 flex flex-wrap items-center gap-2 border-t border-[--color-border] pt-3 text-xs text-[--color-fg-muted]">
          <span>
            <Trans>最終同期: {syncedAt}</Trans>
          </span>
          <span aria-hidden>｜</span>
          <span aria-hidden>📄</span>
          <Link
            to="/docs/$id"
            params={{ id: view.documentId }}
            className="text-[--color-brand] hover:underline"
          >
            <Trans>文書詳細へ戻る</Trans>
          </Link>
        </p>
      </CardContent>
    </Card>
  );
}
