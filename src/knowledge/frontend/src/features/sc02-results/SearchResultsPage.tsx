import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import {
  Alert,
  Button,
  Input,
  Label,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  Tag,
} from '@platform/ui';
import { toMessages } from '@foundation/ui/apiErrors';
import { useSearchQuery } from './useSearchQuery';
import type { SearchResult } from './useSearchQuery';

// SC-02, UC-01, FR-03/FR-05: 検索結果一覧（05_screens: ルート /search?q=）。
// UC-01 代替フロー（キーワード検索のみで結果一覧を返し、AI 回答を省略する）の受け皿であり、
// 本画面は AI 回答を一切呼ばない。各件から SC-03（文書詳細）へ内部遷移する。
//
// 実装しない要素（画面仕様書 docs/screens/SC-02_search-results.md §hi-fi モックアップとの対応）:
//   検索モード切替（キーワード｜意味）・並び順・更新日時列。いずれも検索 API / DTO に該当の
//   指定軸・項目が無く、UI だけ置くと「押しても結果が変わらない操作」「常に空の列」になる
//   （feedback/20260804_sc01-03-bff-contract-gaps.md に環流の記録あり。計画リポへの起票は未了）。

export function SearchResultsPage() {
  const { t } = useLingui();
  // SC-02, ADR-0031 / IADR-0124 決定 3: 検索パラメータ `?q=` は型付きで受け取る。
  // ルート ID のリテラルを渡す形だけが厳密に型付く（Route.useSearch() は any になる）。
  const { q } = useSearch({ from: '/_shell/search' });
  const navigate = useNavigate();
  // 入力欄は「未確定の編集値」であり、取得の引き金にはならない（IADR-0126 決定 3）。
  const [input, setInput] = useState(q);
  const search = useSearchQuery(q);

  function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    // 検索条件の単一情報源は URL である。ここは URL を更新するだけで、取得は URL の変化に従う。
    void navigate({ to: '/search', search: { q: input.trim() } });
  }

  const results = search.data?.results ?? [];
  const totalHits = search.data?.totalHits ?? results.length;
  // `lingui/no-expression-in-message`: 翻訳単位へ渡せるのは単一の変数だけである
  // （`results.length` のようなプロパティ参照はカタログの ID を壊す）。
  const shownCount = results.length;

  return (
    <section>
      <h1 className="text-lg font-semibold text-[--color-fg]">
        <Trans>検索結果一覧</Trans>
      </h1>

      <form onSubmit={onSubmit} role="search" className="mt-3 mb-2 flex items-end gap-2">
        <div className="grow">
          <Label htmlFor="search-q" className="sr-only">
            <Trans>キーワード・意味検索</Trans>
          </Label>
          <Input
            id="search-q"
            value={input}
            maxLength={1000}
            onChange={(e) => setInput(e.target.value)}
            placeholder={t`例: 経費精算`}
          />
        </div>
        <Button type="submit" variant="primary" disabled={input.trim().length === 0}>
          <Trans>検索</Trans>
        </Button>
        <Link to="/ask" className="shrink-0 text-sm text-[--color-brand] hover:underline">
          <Trans>← チャットに戻る</Trans>
        </Link>
      </form>

      {search.isFetching && (
        <p role="status" className="text-sm text-[--color-fg-muted]">
          <Trans>検索中…</Trans>
        </p>
      )}

      {search.isError && (
        <Alert tone="danger" role="alert" label={t`エラー`}>
          {toMessages(search.error, t`検索に失敗しました。`).join(' / ')}
        </Alert>
      )}

      {search.isSuccess &&
        (results.length === 0 ? (
          // deny-by-default: 権限外・0 件はいずれも中立に「見つからない」と表示する（存在秘匿・IADR-0009）。
          <p className="text-sm">
            <Trans>該当する文書が見つかりませんでした。</Trans>
          </p>
        ) : (
          <>
            {/* FR-05: 一覧が全体ではないことを明示する（05_screens §SC-02「権限内のみ表示」を明示）。 */}
            <p className="mb-2 text-sm text-[--color-fg-muted]">
              <Trans>{totalHits} 件（権限内のみ表示）</Trans>
              {shownCount < totalHits && (
                <>
                  {' '}
                  <Trans>（表示 {shownCount} 件）</Trans>
                </>
              )}
            </p>
            <Table>
              <TableCaption>
                <Trans>検索結果</Trans>
              </TableCaption>
              <TableHead>
                <TableRow>
                  <TableHeaderCell>
                    <Trans>文書</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell>
                    <Trans>タグ</Trans>
                  </TableHeaderCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {results.map((r) => (
                  <ResultRow key={r.chunkId} result={r} />
                ))}
              </TableBody>
            </Table>
          </>
        ))}
    </section>
  );
}

function ResultRow({ result }: { result: SearchResult }) {
  return (
    <TableRow>
      <TableCell>
        {/* SC-02 → SC-03: 文書詳細へ内部遷移する（ABAC はサーバ側で再適用される）。 */}
        <Link
          to="/docs/$id"
          params={{ id: result.documentId }}
          className="font-medium text-[--color-brand] hover:underline"
        >
          {result.documentTitle}
        </Link>
        <p className="text-xs text-[--color-fg-muted]">{result.text}</p>
      </TableCell>
      <TableCell>
        <span className="flex flex-wrap gap-1">
          {result.tags?.map((tag) => (
            <Tag key={tag} tone="neutral">
              {tag}
            </Tag>
          ))}
        </span>
      </TableCell>
    </TableRow>
  );
}
