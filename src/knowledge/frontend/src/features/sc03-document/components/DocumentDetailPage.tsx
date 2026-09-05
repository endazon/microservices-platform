import { i18n } from '@lingui/core';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link, useParams } from '@tanstack/react-router';
import {
  Alert,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
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
// SC-03, UC-07, #1200 / IADR-0365 決定 1: 「Wiki で閲覧」は**権限内の Wiki 台帳にこの文書が載っているとき**だけ出す
// （実行時 config `wikiBaseUrl` の有無で出し分ける形は廃止。stg/prod では同値が供給されないため一度も出なかった）。
import { useWikiPageIndex } from '../../../lib/wiki-pages';
// SC-03 / #446: 共通シェルのパンくずの**動的な葉**（文書タイトル）を渡す。
import { useBreadcrumbLeaf } from '@foundation/routing/breadcrumbLeaf';
// ADR-0031 §採用技術一覧（日付 = dayjs）/ #788: 同じ整形を自前で持っていたが、
// **同じ整形規則を 2 か所に置かない**ため foundation の 1 本へ寄せた。
import { formatDateTime } from '@foundation/utils/formatDateTime';
import { attributeLabel, orderedAttributes } from '../types/attributes';
import { isNotFound, useDocumentQueries } from '../api/useDocumentQueries';
// SC-03, FR-18 (#450): AI 提案の承認欄。**承認の主導線は本画面である**（SC-21 は棚卸しの従）。
import { AiSuggestionPanel } from './AiSuggestionPanel';
// SC-03, IADR-0135 決定 1: 表示に使う型は**契約（OpenAPI）から生成された DTO** である。
import type {
  DocumentContentDto,
  DocumentDto,
  DocumentVersionDto,
} from '@foundation/api/generated/bff.schemas';

// SC-03, UC-01/UC-02/UC-07, FR-05/FR-06/FR-12: 文書詳細／プレビュー（05_screens: ルート /docs/:id）。
// 正規化文書（Markdown）本文と属性・タグ・版履歴を表示し、出典元・Wiki（SC-04）への導線を提供する。
// ABAC はサーバ側で適用され、権限外・不在はいずれも 404（存在秘匿・IADR-0009）→ UI は中立に表示する。
//
// 実装しない要素（画面仕様書 docs/screens/SC-03_document-detail.md §hi-fi モックアップとの対応 #7・#9）:
//   **SC-18 ナレッジグラフビューへの導線（FR-17）は実装しない。**
//   IADR-0119 決定 1 が「保留の対象は当該 FR を実現するプロダクトコードと、**その受け入れを担う画面**」と
//   定めており、決定 2 の着手条件（前提 ADR の Accepted 化）は planning d980a01 時点で未充足だった
//   （ADR-0033 / 0034 / 0035 はいずれも当時 Proposed）。**繰り延べであって放棄ではない**——
//   保留が解けた時点で SC-18 の実装と同じ段で本画面へ足す（IADR-0119 決定 6 の手順に従う）。
//
//   **［2026-08-07 / #586］上の予告の発火条件が成立した。** ADR-0033 / 0034 / 0035 は planning 3e58b97
//   （PR planning#244・裁定依頼 planning#237）で Accepted へ移り、IADR-0119 の保留は FR-17 / FR-18 について解除された。
//   #586 は pin 更新と事実の追随に限り、UI は変更していない。
//
//   🔴 **［2026-08-29 / #450］AI 提案の承認欄（FR-18）は実装した。**（[[IADR-0300]]）
//   従前ここには「AI 提案の承認欄と SC-18 への導線は実装しない」と 2 つを 1 文で書いていたが、
//   **片方だけが着地したのでその形は誤りになる。** 承認欄は `AiSuggestionPanel` が担う。
//   **知識グラフビューへの導線は依然として実装しない**（SC-18 の画面と同じ段で足す）——
//   不在は `DocumentDetailPage.test.tsx` が引き続き固定している。

export function DocumentDetailPage() {
  const { t } = useLingui();
  // SC-03, IADR-0124 決定 3: パスパラメータはルート ID のリテラルを渡す形だけが厳密に型付く。
  const { id } = useParams({ from: '/_shell/docs/$id' });
  const { detail, content, versions } = useDocumentQueries(id);
  // 05_screens §共通シェル / #446: パンくずの葉は文書タイトルである（モックの crumb
  // `ホーム / 検索結果 / 経費精算規程 v3.2`）。**フックは早期 return より前で呼ぶ。**
  // 取得前・取得失敗時は `undefined`＝葉を描かない（未確定の文字列をパンくずへ出さない。
  // 「読み込み中」を段に出すと、パンくずが現在地ではなく状態の表示になる）。
  useBreadcrumbLeaf(detail.data?.title);

  if (detail.isPending) {
    return (
      <p role="status" className="text-sm text-[--color-fg-muted]">
        <Trans>読み込み中…</Trans>
      </p>
    );
  }

  if (detail.isError) {
    // 404（不在／秘匿）と 5xx を分ける。404 は文書の有無を示さない中立表示、5xx はサーバの状態である。
    return isNotFound(detail.error) ? (
      <p className="text-sm">
        <Trans>文書が見つかりませんでした。</Trans>
      </p>
    ) : (
      <Alert tone="danger" role="alert" label={t`エラー`}>
        <Trans>文書の取得に失敗しました。</Trans>
      </Alert>
    );
  }

  const doc = detail.data;
  // `lingui/no-expression-in-message`: 翻訳単位へ渡せるのは単一の変数だけである
  // （プロパティ参照・関数呼び出しはカタログの ID を壊す）。
  const status = doc.status;
  const version = doc.version;
  const updatedAt = formatDateTime(doc.updatedAt);
  return (
    <section className="flex flex-col gap-4 lg:flex-row">
      <div className="min-w-0 grow">
        <h1 className="text-lg font-semibold text-[--color-fg]">{doc.title}</h1>
        <p className="mb-3 text-sm text-[--color-fg-muted]">
          <Trans>正規化文書（Markdown）プレビュー</Trans>
        </p>
        <p className="mb-3 text-xs text-[--color-fg-muted]">
          <Trans>
            状態: {status}｜版: v{version}｜更新: {updatedAt}
          </Trans>
        </p>

        <ContentView
          isPending={content.isPending}
          isError={content.isError}
          content={content.data}
          hasBody={doc.hasBody !== false}
        />
        {/* 05_screens §SC-03:「本文の下部に表示し、その場で承認／却下できる」。0 件なら欄ごと出ない。 */}
        <AiSuggestionPanel documentId={doc.id} />
        <SourceLinks doc={doc} content={content.data} />
      </div>

      <div className="min-w-0 lg:w-80">
        <Card className="mb-3">
          <CardHeader>
            <CardTitle as="h2">
              <Trans>属性・タグ</Trans>
            </CardTitle>
          </CardHeader>
          <CardContent>
            <AttributeList attributes={doc.attributes} tags={doc.tags} />
          </CardContent>
        </Card>

        {versions.isSuccess && versions.data.length > 0 && (
          <Card>
            <CardHeader>
              <CardTitle as="h2">
                <Trans>バージョン</Trans>
              </CardTitle>
            </CardHeader>
            <CardContent>
              <VersionTable versions={versions.data} />
            </CardContent>
          </Card>
        )}
      </div>
    </section>
  );
}

/**
 * 正規化 Markdown 本文。
 *
 * **HTML へレンダリングしない**（画面仕様書 §本文の描画）。本文は外部データソース由来であり、
 * HTML 化はサニタイズ方針の決定を伴う（誤ると保存型 XSS になる）。ADR-0031 の採用技術一覧にも
 * Markdown レンダラは無い。原文を等幅・改行保持で安全に表示する。
 */
function ContentView({
  isPending,
  isError,
  content,
  hasBody,
}: {
  isPending: boolean;
  isError: boolean;
  content?: DocumentContentDto;
  hasBody: boolean;
}) {
  // `StatusBadge` の children は文字列を要求する（アイコン＋テキストを内部で組むため）。
  // SC-02 と同じく `useLingui().t` のテンプレート形で渡す。
  const { t } = useLingui();
  return (
    <Card className="mb-3">
      <CardHeader>
        <CardTitle as="h2">
          <Trans>本文</Trans>
        </CardTitle>
      </CardHeader>
      <CardContent>
        {/* SC-03, ADR-0070 決定 3・決定 4 / #1254（[[IADR-0381]] 決定 2）:
            **原本が本文を持たない文書**（テキスト層の無い PDF 等）は、空の本文を出すのではなく
            SC-02 と**同じ文言・同じ導出**で「本文なし（原本を参照）」を示し、原本の導線
            （下の SourceLinks）へ委ねる。空の `pre` を出すと「読み込みに失敗した」と読み違える。
            **`hasBody === false` のときだけ**である（項目を持たない旧応答は従来どおり本文を描く）。 */}
        {!hasBody && <StatusBadge tone="neutral">{t`本文なし（原本を参照）`}</StatusBadge>}
        {hasBody && isPending && (
          <p role="status" className="text-sm text-[--color-fg-muted]">
            <Trans>本文を読み込み中…</Trans>
          </p>
        )}
        {hasBody && isError && (
          <p className="text-sm text-[--color-fg-muted]">
            <Trans>本文は利用できません。</Trans>
          </p>
        )}
        {hasBody && content && (
          <pre className="overflow-x-auto whitespace-pre-wrap break-words rounded-[--radius-control] bg-[--color-surface-muted] p-3 text-sm">
            {content.markdown}
          </pre>
        )}
      </CardContent>
    </Card>
  );
}

/**
 * 出典元（原本）リンクと SC-04（Wiki）への導線。
 * `http(s)` のときだけリンク化し、`storage://` 等は等幅表記で参照だけ示す（押せないものを押させない）。
 *
 * 「Wiki で閲覧」は台帳（権限内の Wiki ページ一覧）にこの文書があるときだけ出し、**文書別ディープリンク**
 * `/wiki?doc=<id>` へ送る（#1200。従前の「`/wiki` までで、ページ単位では飛べない」を解いた）。
 * 台帳が未取得・取得失敗のときは出さない（到達できない導線を押させない）。
 */
function SourceLinks({ doc, content }: { doc: DocumentDto; content?: DocumentContentDto }) {
  const wiki = useWikiPageIndex();
  const hasWikiPage = wiki.documentIds?.has(doc.id) ?? false;
  const sourceUri = content?.sourceUri ?? doc.markdownUri ?? null;
  const isHttp = !!sourceUri && /^https?:\/\//i.test(sourceUri);
  return (
    <p className="flex flex-wrap items-center gap-2 text-sm">
      {hasWikiPage && (
        <>
          {/* UC-07: Wiki 閲覧導線（内部ルート）。閲覧範囲は前段ゲートウェイ（ABAC）が台帳の側で決めている。 */}
          <span aria-hidden>📖</span>
          <Link
            to="/wiki"
            search={{ doc: doc.id }}
            className="text-[--color-brand] hover:underline"
          >
            <Trans>Wikiで閲覧</Trans>
          </Link>
          <span className="text-[--color-fg-muted]" aria-hidden>
            ｜
          </span>
        </>
      )}
      <span className="text-[--color-fg-muted]">
        <Trans>原本</Trans>:
      </span>
      {sourceUri ? (
        isHttp ? (
          <a
            href={sourceUri}
            target="_blank"
            rel="noopener noreferrer"
            className="text-[--color-brand] hover:underline"
          >
            {sourceUri}
          </a>
        ) : (
          <code className="text-xs text-[--color-fg-muted]">{sourceUri}</code>
        )
      ) : (
        <span aria-hidden>—</span>
      )}
    </p>
  );
}

function AttributeList({
  attributes,
  tags,
}: {
  attributes: Record<string, string>;
  tags: string[];
}) {
  const { t } = useLingui();
  const entries = orderedAttributes(attributes);
  return (
    <dl className="flex flex-col gap-1.5 text-sm">
      {entries.map(([key, value]) => {
        const label = attributeLabel(key);
        return (
          <div key={key} className="flex gap-2">
            <dt className="text-[--color-fg-muted]">{label ? i18n._(label) : key}:</dt>
            {/* 値は変換せず生値を出す（attributes.ts の冒頭コメント参照）。 */}
            <dd>{value}</dd>
          </div>
        );
      })}
      <div className="flex flex-wrap items-baseline gap-2">
        <dt className="text-[--color-fg-muted]">{t`タグ`}:</dt>
        <dd className="flex flex-wrap gap-1">
          {tags.length === 0 ? (
            <span className="text-[--color-fg-muted]" aria-hidden>
              —
            </span>
          ) : (
            tags.map((tag) => (
              <Tag key={tag} tone="neutral">
                {tag}
              </Tag>
            ))
          )}
        </dd>
      </div>
    </dl>
  );
}

function VersionTable({ versions }: { versions: DocumentVersionDto[] }) {
  return (
    <Table>
      <TableCaption>
        <Trans>バージョン履歴</Trans>
      </TableCaption>
      <TableHead>
        <TableRow>
          <TableHeaderCell>
            <Trans>版</Trans>
          </TableHeaderCell>
          <TableHeaderCell>
            <Trans>変更メモ</Trans>
          </TableHeaderCell>
          <TableHeaderCell>
            <Trans>作成</Trans>
          </TableHeaderCell>
        </TableRow>
      </TableHead>
      <TableBody>
        {versions.map((v) => (
          <TableRow key={v.version}>
            <TableCell>v{v.version}</TableCell>
            <TableCell>{v.changeNote ?? '—'}</TableCell>
            <TableCell>{formatDateTime(v.createdAt)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
