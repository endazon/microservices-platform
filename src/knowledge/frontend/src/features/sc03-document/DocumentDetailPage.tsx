import { i18n } from '@lingui/core';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link, useParams } from '@tanstack/react-router';
import {
  Alert,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  Tag,
} from '@platform/ui';
import { appConfig } from '@foundation/config/runtimeConfig';
import { attributeLabel, orderedAttributes } from './attributes';
import { isNotFound, useDocumentQueries } from './useDocumentQueries';
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
// 実装しない要素（画面仕様書 docs/screens/SC-03_document-detail.md §hi-fi モックアップとの対応 #7〜#9）:
//   **AI 提案の承認欄（FR-18）と SC-18 ナレッジグラフへの導線（FR-17）は実装しない。**
//   IADR-0119 決定 1 が「保留の対象は当該 FR を実現するプロダクトコードと、**その受け入れを担う画面**」と
//   定めており、決定 2 の着手条件（前提 ADR の Accepted 化）は planning d980a01 時点で未充足だった
//   （ADR-0033 / 0034 / 0035 はいずれも当時 Proposed）。**繰り延べであって放棄ではない**——
//   保留が解けた時点で SC-18 / SC-21 の実装と同じ段で本画面へ足す（IADR-0119 決定 6 の手順に従う）。
//
//   **［2026-08-07 / #586］上の予告の発火条件が成立した。** ADR-0033 / 0034 / 0035 は planning 3e58b97
//   （PR planning#244・裁定依頼 planning#237）で Accepted へ移り、IADR-0119 の保留は FR-17 / FR-18 について解除された。
//   したがって **AI 提案の承認欄（FR-18）と SC-18 への導線（FR-17）を本画面へ足す段が来ている**。
//   **引き受けるのは #452（SC-18 / SC-21 と同じ段）であり、#586 の射程外である**
//   （#586 は pin 更新と事実の追随に限り、UI は変更していない）。

export function DocumentDetailPage() {
  const { t } = useLingui();
  // SC-03, IADR-0124 決定 3: パスパラメータはルート ID のリテラルを渡す形だけが厳密に型付く。
  const { id } = useParams({ from: '/_shell/docs/$id' });
  const { detail, content, versions } = useDocumentQueries(id);

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
  const updatedAt = formatDate(doc.updatedAt);
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

        <ContentView isPending={content.isPending} isError={content.isError} content={content.data} />
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

/** ISO 8601（DateTimeOffset 由来）をロケール表記に整形する。解釈できない値はそのまま表示する。 */
function formatDate(value: string | null | undefined): string {
  if (!value) return '—';
  const time = Date.parse(value);
  return Number.isNaN(time) ? value : new Date(time).toLocaleString(i18n.locale);
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
}: {
  isPending: boolean;
  isError: boolean;
  content?: DocumentContentDto;
}) {
  return (
    <Card className="mb-3">
      <CardHeader>
        <CardTitle as="h2">
          <Trans>本文</Trans>
        </CardTitle>
      </CardHeader>
      <CardContent>
        {isPending && (
          <p role="status" className="text-sm text-[--color-fg-muted]">
            <Trans>本文を読み込み中…</Trans>
          </p>
        )}
        {isError && (
          <p className="text-sm text-[--color-fg-muted]">
            <Trans>本文は利用できません。</Trans>
          </p>
        )}
        {content && (
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
 */
function SourceLinks({ doc, content }: { doc: DocumentDto; content?: DocumentContentDto }) {
  const { wikiBaseUrl } = appConfig();
  const sourceUri = content?.sourceUri ?? doc.markdownUri ?? null;
  const isHttp = !!sourceUri && /^https?:\/\//i.test(sourceUri);
  return (
    <p className="flex flex-wrap items-center gap-2 text-sm">
      {wikiBaseUrl && (
        <>
          {/* UC-07: Wiki 閲覧導線（内部ルート）。閲覧範囲はゲートウェイ（ABAC）で制御される。 */}
          <span aria-hidden>📖</span>
          <Link to="/wiki" className="text-[--color-brand] hover:underline">
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
            <TableCell>{formatDate(v.createdAt)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
