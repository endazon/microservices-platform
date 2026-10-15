import { useEffect, useId, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Button, Input, Label, Select } from '@platform/ui';
import { i18n } from '@foundation/i18n';
import { DOCUMENT_TITLE_QUERY_MAX } from '../api/useDocumentAdmin';
import type { DocumentSort, DocumentStatusFilter } from '../api/useDocumentAdmin';
import { DOCUMENT_STATUSES, documentStatusView } from '../types/documentStatus';

// SC-05, FR-06（#1923 段 2・[[IADR-0545]]）: 文書一覧の**操作の帯**（表題の検索・状態の絞り込み・並べ替え・条件の解除）。
//
// 並べ替えは文書サービス、絞り込みは BFF が ABAC の後に当てる。件数（状態ごとの数）は BFF が権限で絞った後の数であり、
// ここは受け取った数を描くだけで、権限の有無を推し量る表示を足さない。
// 画面の部品だけで組む（`Input` / `Select` / `Button` / `Label`）。共有 UI へ新しいプリミティブは足さない。

/** 表題の検索を打ち終えてから引くまでの待ち（ミリ秒）。1 打鍵ごとに台帳を 1 周させない（IADR-0537 の残余）。 */
const TITLE_QUERY_DEBOUNCE_MS = 300;

interface DocumentListToolbarProps {
  sort: DocumentSort;
  status: DocumentStatusFilter;
  /** 確定した（引いた）検索語。入力中の値は帯の中で持つ。 */
  q: string;
  /** 状態ごとの件数（絞り込み前・ABAC 後）。取得前は undefined。 */
  statusCounts?: Record<string, number>;
  /** 絞り込み前の件数（「すべて」に添える）。取得前は undefined。 */
  unfilteredTotal?: number;
  onSortChange: (sort: DocumentSort) => void;
  onStatusChange: (status: DocumentStatusFilter) => void;
  onQueryChange: (q: string) => void;
  /** 検索語と状態を解除する（並べ替えは残す —— 条件ではなく見せ方である）。 */
  onClear: () => void;
}

export function DocumentListToolbar({
  sort,
  status,
  q,
  statusCounts,
  unfilteredTotal,
  onSortChange,
  onStatusChange,
  onQueryChange,
  onClear,
}: DocumentListToolbarProps) {
  const { t } = useLingui();
  const searchId = useId();
  const statusId = useId();
  const sortId = useId();
  const [draft, setDraft] = useState(q);

  // 入力が止まってから引く。確定した値と同じなら何もしない（解除で '' に戻したときに二度引かない）。
  useEffect(() => {
    if (draft.trim() === q.trim()) return;
    const timer = setTimeout(() => onQueryChange(draft), TITLE_QUERY_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [draft, q, onQueryChange]);

  const sortOptions: { value: DocumentSort; label: string }[] = [
    { value: 'updated-desc', label: t`更新日時が新しい順` },
    { value: 'updated-asc', label: t`更新日時が古い順` },
    { value: 'created-desc', label: t`作成日時が新しい順` },
    { value: 'created-asc', label: t`作成日時が古い順` },
    { value: 'title-asc', label: t`表題の昇順` },
    { value: 'title-desc', label: t`表題の降順` },
  ];
  const withCount = (label: string, count: number | undefined) =>
    count === undefined ? label : t`${label}（${count} 件）`;
  const filtered = q.trim() !== '' || status !== '' || draft.trim() !== '';

  return (
    <div
      className="mb-n3 flex flex-wrap items-end gap-n2"
      role="search"
      aria-label={t`文書の絞り込み`}
    >
      <span className="flex min-w-[12rem] flex-1 flex-col gap-1">
        <Label htmlFor={searchId}>
          <Trans>表題で検索</Trans>
        </Label>
        <Input
          id={searchId}
          type="search"
          inputSize="sm"
          value={draft}
          maxLength={DOCUMENT_TITLE_QUERY_MAX}
          placeholder={t`表題の一部を入力`}
          onChange={(e) => setDraft(e.target.value)}
        />
      </span>
      <span className="flex flex-col gap-1">
        <Label htmlFor={statusId}>
          <Trans>状態</Trans>
        </Label>
        <Select
          id={statusId}
          selectSize="sm"
          value={status}
          onChange={(e) => onStatusChange(e.target.value as DocumentStatusFilter)}
        >
          <option value="">{withCount(t`すべて`, unfilteredTotal)}</option>
          {DOCUMENT_STATUSES.map((value) => {
            const view = documentStatusView(value);
            const label = typeof view.label === 'string' ? view.label : i18n._(view.label);
            return (
              <option key={value} value={value}>
                {withCount(label, statusCounts ? (statusCounts[value] ?? 0) : undefined)}
              </option>
            );
          })}
        </Select>
      </span>
      <span className="flex flex-col gap-1">
        <Label htmlFor={sortId}>
          <Trans>並べ替え</Trans>
        </Label>
        <Select
          id={sortId}
          selectSize="sm"
          value={sort}
          onChange={(e) => onSortChange(e.target.value as DocumentSort)}
        >
          {sortOptions.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </Select>
      </span>
      <Button
        type="button"
        size="sm"
        disabled={!filtered}
        onClick={() => {
          setDraft('');
          onClear();
        }}
      >
        <Trans>条件を解除</Trans>
      </Button>
    </div>
  );
}
