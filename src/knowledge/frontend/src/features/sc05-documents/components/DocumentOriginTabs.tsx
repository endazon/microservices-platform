import { useLingui } from '@lingui/react/macro';
import { cn } from '@platform/ui';
import { originCounts } from '../types/documentSource';
import type { DocumentOrigin } from '../types/documentSource';

// SC-05, FR-06（#1923・[[IADR-0560]]）: **機械の収集**（AST の情報収集のニュース・開示など）と**人の文書・報告書**を分けるタブ。
//
// 既定は「人の文書・報告書」（台帳の大半を占める機械の収集に、人が作った文書と確定報告書を埋もれさせない）。
// 件数はデータソース別の件数（ABAC 後・絞り込み前）から導くので、権限外の文書は数に入らない。
// 切り替えは一覧の絞り込み（`origin`）であり、表の中身を差し替えるだけなので、タブの部品（`role="tab"` と区画の対応）ではなく
// 押下状態つきのボタンの組（`aria-pressed`）で描く。選択は色だけで示さない（太字と内側の罫を重ねる）。

interface DocumentOriginTabsProps {
  origin: DocumentOrigin;
  sourceCounts?: Record<string, number>;
  onChange: (origin: DocumentOrigin) => void;
}

export function DocumentOriginTabs({ origin, sourceCounts, onChange }: DocumentOriginTabsProps) {
  const { t } = useLingui();
  const counts = originCounts(sourceCounts);
  const options: { value: DocumentOrigin; label: string; count?: number }[] = [
    { value: 'curated', label: t`人の文書・報告書`, count: counts?.curated },
    { value: 'collected', label: t`機械の収集`, count: counts?.collected },
    { value: '', label: t`すべて`, count: counts?.all },
  ];

  return (
    <div
      role="group"
      aria-label={t`文書の種類`}
      className="mb-n2 inline-flex flex-wrap items-center gap-1 rounded-md border border-divider bg-surface-muted p-1"
    >
      {options.map((option) => {
        const selected = option.value === origin;
        const { label, count } = option;
        return (
          <button
            key={option.value || 'all'}
            type="button"
            aria-pressed={selected}
            onClick={() => onChange(option.value)}
            className={cn(
              'rounded-md px-3 py-1 text-sm text-fg-muted transition-colors',
              selected && 'font-semibold text-accent shadow-[inset_0_0_0_1px_var(--color-accent)]',
            )}
          >
            {count === undefined ? label : t`${label}（${count} 件）`}
          </button>
        );
      })}
    </div>
  );
}
