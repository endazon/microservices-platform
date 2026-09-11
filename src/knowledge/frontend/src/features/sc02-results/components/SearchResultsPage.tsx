import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { i18n } from '@foundation/i18n';
import type { MessageDescriptor } from '@lingui/core';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import {
  Button,
  EmptyState,
  Input,
  Label,
  Select,
  StatusBadge,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  Tag,
} from '@platform/ui';
import { QueryState } from '@foundation/ui/QueryState';
import { useSearchQuery } from '../api/useSearchQuery';
import { formatDateTime } from '@foundation/utils/formatDateTime';
import { CONFIDENTIALITY_KEY } from '../../../lib/abac';
import {
  DEFAULT_SEARCH_MODE,
  DEFAULT_SEARCH_SORT,
  SEARCH_MODES,
  SEARCH_SORTS,
  searchModeLabel,
  searchSortLabel,
} from '../types/searchOptions';
import type { ResultsSearch, SearchMode, SearchSort } from '../types/searchOptions';
// SC-02, IADR-0135 決定 1: 表示に使う型は**契約（OpenAPI）から生成された DTO** である。
import type { SearchResponse, SearchResultDto } from '@foundation/api/generated/bff.schemas';

// SC-02, UC-01, FR-03/FR-05: 検索結果一覧（05_screens: ルート /search?q=）。
// UC-01 代替フロー（キーワード検索のみで結果一覧を返し、AI 回答を省略する）の受け皿であり、
// 本画面は AI 回答を一切呼ばない。各件から SC-03（文書詳細）へ内部遷移する。
//
// **［2026-09-12 / UI/UX 改善］hi-fi モック（sc-02）の構造へ寄せた。**
//   - 見出しは `.ttl`＋`.sub`、ツールバー行は「件数（権限内のみ表示）… 並び ▾ … 検索モード ▾」。
//   - **検索モード（3 値）と並び順（2 値）の切替 UI を実装した。** 契約は #531 / #532 で既に
//     揃っており（裁定 Q4 / Q5）、残っていたのは切替 UI だけだった。値は URL が単一情報源で、
//     既定は URL にも要求本文にも載せない（`types/searchOptions.ts` 冒頭）。
//     後段は `updated` を**取得後に並べ替える**（[[IADR-0150]]）。
//   - 待ち・空・失敗を `QueryState` の 1 本へ統一した（`<p role="status">` 直書き・
//     `Alert` での失敗表示・`&&` 並置を撤去）。**0 件と失敗を混同しない**——
//     0 件は `EmptyState`（中立の文言。存在秘匿）、失敗は `ErrorState`（再試行つき）である。
//   **［2026-08-09 / #536］更新日時列は実装した。** 契約（`SearchResultDto.updatedAt`）が
//   裁定 Q6 を受けて日時を持ち、索引（Qdrant のペイロード）へも取り込むようにしたため（[[IADR-0149]]）。
//   **［2026-09-03 / #1193］本文なしの文書の縮退表示を足した**（ADR-0070 決定 4 / [[IADR-0358]]）。
//   本文抜粋が出せない文書（テキスト層の無い PDF 等）を**結果から除外せず**、抜粋の位置へ
//   「本文なし（原本を参照）」を出す。**SC-07 の「画像保持へ縮退済み」と同じ形の併記**である。

/**
 * 空のときの「次の一手」の実体: 検索入力へ戻す。
 *
 * `@platform/ui` の `Input` は ref を転送する型を持たない（`Button` と同じ事情）ので、
 * **安定した id で引く**。下の文字列は DOM の id であって表示文言ではない。
 */
function focusSearchInput() {
  const element = document.getElementById('search-q');
  if (element instanceof HTMLInputElement) element.focus();
}

export function SearchResultsPage() {
  const { t } = useLingui();
  // SC-02, ADR-0031 / IADR-0124 決定 3: 検索パラメータ `?q=` は型付きで受け取る。
  // ルート ID のリテラルを渡す形だけが厳密に型付く（Route.useSearch() は any になる）。
  const { q, mode, sort } = useSearch({ from: '/_shell/search' });
  const navigate = useNavigate();
  // 入力欄は「未確定の編集値」であり、取得の引き金にはならない（IADR-0126 決定 3）。
  const [input, setInput] = useState(q);
  const search = useSearchQuery(q, { mode, sort });

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
    // **検索モード・並び順は保つ**（語だけを差し替える）。
    //
    // 🔴 **`prev` は `Partial<ResultsSearch>` である。** ルータの検索パラメータ更新関数は
    // 登録済み全ルートの検索パラメータの和（すべて省略可）を受け取るため、`ResultsSearch`
    // （`q` が必須）で受けると**アプリ全体をビルドしたときだけ**型が合わなくなる
    // （ユニット単体の `tsc` では登録が無いので通ってしまい、気づけない）。
    void navigate({
      to: '/search',
      search: (prev: Partial<ResultsSearch>) => ({ ...prev, q: input.trim() }),
    });
  }

  return (
    <section>
      {/* hi-fi `.ttl` / `.sub`。**見出しの文言は変えない**——導線テストと E2E が
          「検索結果一覧」で画面を特定している。 */}
      <h1 className="text-[17px] font-medium text-fg">
        <Trans>検索結果一覧</Trans>
      </h1>
      {/* 🔴 **この行に「権限」の語を入れない。** 0 件の画面で「権限」と書いた瞬間に
          「在るが見せない」が漏れる（存在秘匿。E2E の陰性対照が固定している）。
          件数のチップ（`ResultCount`）は結果があるときだけ描くので対象外である。 */}
      <p className="text-xs text-fg-muted">
        <Trans>キーワードと文意の両方で探した結果である。</Trans>
      </p>

      <form onSubmit={onSubmit} role="search" className="mt-n3 mb-n2 flex items-end gap-n2">
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
        <Link to="/ask" className="shrink-0 text-sm text-brand hover:underline">
          <Trans>← チャットに戻る</Trans>
        </Link>
      </form>

      {/* hi-fi の 2 行目: 件数 … 並び ▾ … 検索モード ▾。
          件数は取得できたときだけ出す（取得前・失敗時に「0 件」と読める器を置かない）。 */}
      <div className="mb-n2 flex flex-wrap items-center gap-n2">
        <ResultCount data={search.isSuccess ? search.data : undefined} />
        <span className="grow" />
        <SearchOptionSelect
          id="search-sort"
          label={t`並び`}
          value={sort ?? DEFAULT_SEARCH_SORT}
          options={SEARCH_SORTS}
          optionLabel={searchSortLabel}
          onChange={(next) =>
            void navigate({
              to: '/search',
              // `q` を明示するのは、更新関数の戻り値が**そのルートの検索パラメータを満たす**
              // 必要があるためである（`q` は必須）。値は URL から読んだものをそのまま戻す。
              search: (prev: Partial<ResultsSearch>) => ({
                ...prev,
                q,
                sort: next === DEFAULT_SEARCH_SORT ? undefined : (next as SearchSort),
              }),
            })
          }
        />
        <SearchOptionSelect
          id="search-mode"
          label={t`検索モード`}
          value={mode ?? DEFAULT_SEARCH_MODE}
          options={SEARCH_MODES}
          optionLabel={searchModeLabel}
          onChange={(next) =>
            void navigate({
              to: '/search',
              search: (prev: Partial<ResultsSearch>) => ({
                ...prev,
                q,
                mode: next === DEFAULT_SEARCH_MODE ? undefined : (next as SearchMode),
              }),
            })
          }
        />
      </div>

      {/* 検索語が無いときは取得そのものが走らない（`enabled: false`）。無効な照会は
          TanStack Query では永遠に `isPending` なので、**`QueryState` へ渡さない**
          ——「読み込み中…」が消えない画面になる。 */}
      {q === '' ? (
        <EmptyState
          title={t`検索語を入力してください。`}
          description={t`キーワード、または探している内容を文で入力する。`}
        />
      ) : (
        <QueryState
          query={search}
          loadingLabel={t`検索中…`}
          errorTitle={t`検索に失敗しました。`}
          isEmpty={(data) => (data.results ?? []).length === 0}
          empty={
            // deny-by-default: 権限外・0 件はいずれも中立に「見つからない」と表示する
            // （存在秘匿・IADR-0009）。**次の行動を示す**——空は再試行ではなく条件の変更である。
            <EmptyState
              title={t`該当する文書はありません。`}
              description={t`条件を変えて再検索してください。`}
              action={
                <Button type="button" onClick={focusSearchInput}>
                  <Trans>検索条件を変える</Trans>
                </Button>
              }
            />
          }
        >
          {(data) => <ResultTable results={data.results ?? []} />}
        </QueryState>
      )}
    </section>
  );
}

/**
 * FR-05: 一覧が全体ではないことを明示する（05_screens §SC-02「権限内のみ表示」を明示）。
 *
 * `Tag` の children は文字列を要求するので、`t` のテンプレート形で組む。
 * `lingui/no-expression-in-message`: 翻訳単位へ渡せるのは単一の変数だけである。
 */
function ResultCount({ data }: { data: SearchResponse | undefined }) {
  const { t } = useLingui();
  if (data === undefined) return null;
  const results = data.results ?? [];
  // 🔴 **0 件のときは出さない。** 「0 件（権限内のみ表示）」と書くと、
  // **「在るが見せていない」と読める** —— 存在秘匿（IADR-0009）に反する。
  // 件数が無いことは `EmptyState` の文言が中立に伝える（E2E の陰性対照が固定している）。
  if (results.length === 0) return null;
  const totalHits = data.totalHits ?? results.length;
  const shownCount = results.length;
  return (
    <>
      <Tag tone="neutral">{t`${totalHits} 件（権限内のみ表示）`}</Tag>
      {shownCount < totalHits ? <Tag tone="neutral">{t`（表示 ${shownCount} 件）`}</Tag> : null}
    </>
  );
}

/** hi-fi のツールバーの「並び: 関連度 ▾」「検索モード: ハイブリッド ▾」。 */
function SearchOptionSelect<T extends string>({
  id,
  label,
  value,
  options,
  optionLabel,
  onChange,
}: {
  id: string;
  label: string;
  value: T;
  options: readonly T[];
  optionLabel: (value: T) => MessageDescriptor;
  onChange: (value: string) => void;
}) {
  return (
    <span className="flex items-center gap-n2">
      <Label htmlFor={id} className="shrink-0">
        {label}
      </Label>
      <Select
        id={id}
        selectSize="sm"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="w-auto"
      >
        {options.map((option) => (
          <option key={option} value={option}>
            {i18n._(optionLabel(option))}
          </option>
        ))}
      </Select>
    </span>
  );
}

function ResultTable({ results }: { results: SearchResultDto[] }) {
  return (
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
            <Trans>機密区分</Trans>
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
  );
}

function ResultRow({ result }: { result: SearchResultDto }) {
  const { t } = useLingui();
  // SC-02（ADR-0070 決定 4 / #1193）: **本文を持たない文書**（テキスト層の無い PDF 等）は
  // 索引にメタデータしか無く、抜粋が出せない。**結果からは除外しない**ので、抜粋の位置へ
  // 「本文なし（原本を参照）」を出す。**`hasBody === false` のときだけ**である
  // （項目を持たない応答＝本文ありとして従来どおり描く）。
  const bodyless = result.hasBody === false;
  // hi-fi の SC-05 と同じく、機密区分は accent のチップで示す。値そのものは訳さない
  // （表示名が計画に無い値がある。lib/abac/confidentiality.ts 参照）。
  const confidentiality = result.attributes?.[CONFIDENTIALITY_KEY];

  return (
    <TableRow>
      <TableCell>
        {/* SC-02 → SC-03: 文書詳細へ内部遷移する（ABAC はサーバ側で再適用される）。 */}
        <Link
          to="/docs/$id"
          params={{ id: result.documentId }}
          className="font-medium text-brand hover:underline"
        >
          {result.documentTitle}
        </Link>
        {bodyless ? (
          // **原本への導線を残す**（ADR-0070 決定 4）。原本の所在（`sourceUri`）を持っているのは
          // SC-03（文書詳細）なので、そこへ辿れる形で示す。**`markdownUri` は原本ではない**
          // （正規化 Markdown の置き場）ので出さない。
          // 色だけで意味を持たせない —— `StatusBadge` がアイコン＋テキストを強制する。
          <p className="mt-1">
            <Link to="/docs/$id" params={{ id: result.documentId }}>
              <StatusBadge tone="neutral">{t`本文なし（原本を参照）`}</StatusBadge>
            </Link>
          </p>
        ) : (
          <p className="text-xs text-fg-muted">{result.text}</p>
        )}
      </TableCell>
      <TableCell>
        {confidentiality ? <Tag tone="accent">{confidentiality}</Tag> : <span aria-hidden>—</span>}
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
      <TableCell className="whitespace-nowrap text-sm text-fg-muted">
        {formatDateTime(result.updatedAt)}
      </TableCell>
    </TableRow>
  );
}
