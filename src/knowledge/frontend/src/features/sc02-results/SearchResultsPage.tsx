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
import { formatDateTime } from '@foundation/ui/formatDateTime';
// SC-02, IADR-0135 決定 1: 表示に使う型は**契約（OpenAPI）から生成された DTO** である。
import type { SearchResultDto } from '@foundation/api/generated/bff.schemas';

// SC-02, UC-01, FR-03/FR-05: 検索結果一覧（05_screens: ルート /search?q=）。
// UC-01 代替フロー（キーワード検索のみで結果一覧を返し、AI 回答を省略する）の受け皿であり、
// 本画面は AI 回答を一切呼ばない。各件から SC-03（文書詳細）へ内部遷移する。
//
// 実装しない要素（画面仕様書 docs/screens/SC-02_search-results.md §hi-fi モックアップとの対応）:
//   - **検索モード切替（ハイブリッド｜キーワード｜意味）**: **契約は #531 で揃った**
//     （`SearchRequest.Mode` の 3 値・裁定 Q4）。**切替 UI が未実装**なので画面には出ていない。
//   - **並び順（関連度｜更新日時の新しい順）**: 契約に指定軸が無い（裁定 Q5・**#532 が引き受ける**）。
//   もとの記録は feedback/20260804_sc01-03-bff-contract-gaps.md（planning#197 で裁定済み）。
//   **［2026-08-09 / #536］更新日時列は実装した。** 契約（`SearchResultDto.updatedAt`）が
//   裁定 Q6 を受けて日時を持ち、索引（Qdrant のペイロード）へも取り込むようにしたため（[[IADR-0149]]）。
//   **並び順（#532）は本 issue の射程外**であり、この列の値をソートキーに使う。

export function SearchResultsPage() {
  const { t } = useLingui();
  // SC-02, ADR-0031 / IADR-0124 決定 3: 検索パラメータ `?q=` は型付きで受け取る。
  // ルート ID のリテラルを渡す形だけが厳密に型付く（Route.useSearch() は any になる）。
  const { q } = useSearch({ from: '/_shell/search' });
  const navigate = useNavigate();
  // 入力欄は「未確定の編集値」であり、取得の引き金にはならない（IADR-0126 決定 3）。
  const [input, setInput] = useState(q);
  const search = useSearchQuery(q);

  // IADR-0126 決定 3: 検索語の単一情報源は URL である。**入力欄もそれに追随する。**
  //
  // `useState(q)` はマウント時の初期値しか取らないため、本画面が**アンマウントされずに `q` だけが
  // 変わる経路**（ブラウザの戻る／進む、`/search` に居る状態での外部からの `navigate`）では、
  // 結果一覧だけが更新されて入力欄が古いまま残る（TanStack Router は同一ルートの search 変化で
  // コンポーネントを再生成しない）。URL を正とすると決めた設計から、入力欄だけが外れる形である。
  //
  // **`useEffect` では直さない。** props/URL の変化に合わせた state の調整は React が
  // 「Effect は不要」とするパターンであり、Effect でやると 1 フレーム古い値が描画されて
  // 余分な再描画も起きる。ここは**レンダー中に調整する**（React 公式の "Adjusting state when
  // props change"）。`key` によるコンポーネントごとの再生成も可能だが採らない——
  // 入力欄の追随という**この画面の内部事情**をルート定義側（index.tsx）へ持ち出すことになり、
  // 理由がファイルをまたいで分かれるうえ、将来ローカル state が増えたときに巻き添えで捨てられる。
  //
  // 編集途中の値を捨てるのは意図どおりである——URL が外から変わったということは、
  // 利用者（または戻る操作）が別の検索語を選んだということであり、未確定の編集値は無効になる。
  const [prevQ, setPrevQ] = useState(q);
  if (q !== prevQ) {
    setPrevQ(q);
    setInput(q);
  }

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
                  <TableHeaderCell>
                    <Trans>更新日時</Trans>
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

function ResultRow({ result }: { result: SearchResultDto }) {
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
      {/* SC-02（裁定 Q6 / #536）: 更新日時。**未再索引のチャンクは値を持たない**ので `—` になる
          （[[IADR-0149]] 決定 3）。「日時が無い」と「まだ再索引していない」を利用者へ区別して
          見せない —— 索引の内部事情である。 */}
      <TableCell className="whitespace-nowrap text-sm text-[--color-fg-muted]">
        {formatDateTime(result.updatedAt)}
      </TableCell>
    </TableRow>
  );
}
