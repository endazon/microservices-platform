import { Trans, useLingui } from '@lingui/react/macro';
import { Link } from '@tanstack/react-router';
import { Button, Panel, StatusBadge } from '@platform/ui';
import { i18n } from '@foundation/i18n';
import { ApiError } from '@foundation/api/ApiError';
import {
  getBffDocumentContentQueryKey,
  useBffDocumentContent,
} from '@foundation/api/generated/documents/documents';
import { okData } from '@foundation/api/orvalSelect';
import type { DocumentContentDto, DocumentDto } from '@foundation/api/generated/bff.schemas';
import { documentBodyState, documentBodyView } from '../types/documentBody';
import { linkableOriginalUri } from '../types/documentSource';
import { PREVIEW_CHARS, previewText } from '../types/documentPreview';

// SC-05, SC-03, FR-06（#1923・[[IADR-0561]]）: 一覧の横のペインに出す**本文の先頭のプレビュー**。
//
// 本文は文書詳細と同じ口（`GET /bff/documents/{id}/content`。ABAC の判定は BFF）で引き、先頭だけを**素の文字**で出す
// （Markdown を描画しない —— 一覧の横で読むための抜き書きであり、全文と書式は SC-03 が持つ）。
// 台帳が「本文なし」「本文未設定」と言っている文書は引かない（確実に 404 になる往復を省く）。

export function DocumentPreviewPanel({ doc, onClose }: { doc: DocumentDto; onClose: () => void }) {
  const { t } = useLingui();
  const fetchable = documentBodyState(doc) === 'present';
  const content = useBffDocumentContent<DocumentContentDto, unknown>(doc.id, {
    query: { queryKey: getBffDocumentContentQueryKey(doc.id), select: okData, enabled: fetchable },
  });
  const original = linkableOriginalUri(doc.originalUri);

  let body;
  if (!fetchable) {
    const view = documentBodyView(doc);
    body = <StatusBadge tone={view.tone}>{i18n._(view.label)}</StatusBadge>;
  } else if (content.isPending) {
    body = (
      <p role="status" className="text-sm text-fg-muted">
        <Trans>本文を読み込み中…</Trans>
      </p>
    );
  } else if (content.isError) {
    const status = content.error instanceof ApiError ? content.error.status : null;
    body = (
      <p role="alert" className="text-sm text-fg-muted">
        {status === 413
          ? t`本文が大きすぎるためプレビューできません。文書詳細で確かめてください。`
          : status === 404
            ? t`本文が保管されていません。`
            : t`本文を取得できませんでした。`}
      </p>
    );
  } else {
    const { text, truncated } = previewText(content.data.markdown ?? '');
    body = (
      <>
        <p className="max-h-[24rem] overflow-y-auto whitespace-pre-wrap break-words text-sm">
          {text === '' ? <Trans>（本文は空です）</Trans> : text}
        </p>
        {truncated && (
          <p className="mt-1 text-xs text-fg-muted">
            <Trans>先頭 {PREVIEW_CHARS} 文字を表示しています。</Trans>
          </p>
        )}
      </>
    );
  }

  return (
    <Panel heading={t`プレビュー`} headingAs="h2" aria-label={t`本文のプレビュー`}>
      <p className="mb-2 break-words font-medium">{doc.title}</p>
      <div className="mb-3">{body}</div>
      <p className="flex flex-wrap items-center gap-n2 text-sm">
        <Link to="/docs/$id" params={{ id: doc.id }} className="text-brand hover:underline">
          <Trans>文書詳細を開く</Trans>
        </Link>
        {original && (
          <a
            href={original}
            target="_blank"
            rel="noopener noreferrer"
            className="text-brand hover:underline"
          >
            <Trans>出典を開く</Trans>
          </a>
        )}
        <Button type="button" size="sm" onClick={onClose}>
          <Trans>プレビューを閉じる</Trans>
        </Button>
      </p>
    </Panel>
  );
}
