import type { ReactNode } from 'react';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: hi-fi モックの `.kv`（ラベル ＋ 値の箱。参照専用の項目一覧）。
//
// **`<dl>` / `<dt>` / `<dd>` で組む**（見出しと値の対応が支援技術へ伝わる。`<div>` の羅列では
// 「この文字列は何のラベルか」が失われる）。`<div>` で `<dt>`+`<dd>` を包む形は HTML 仕様上も正しい。

/** 列数 → クラス名。**動的に組み立てない**（Tailwind はソースを文字列として走査するため、
 *  `grid-cols-${n}` と書くとクラスが生成されない）。 */
const COLUMN_CLASSES = {
  1: 'grid-cols-1',
  2: 'grid-cols-2',
  3: 'grid-cols-3',
  4: 'grid-cols-4',
} as const;

export interface KvProps {
  /** 列数（既定 2）。 */
  columns?: 1 | 2 | 3 | 4;
  children: ReactNode;
  className?: string;
}

export function Kv({ columns = 2, children, className }: KvProps) {
  return <dl className={cn('grid gap-n3', COLUMN_CLASSES[columns], className)}>{children}</dl>;
}

export interface KvItemProps {
  /** 項目名。 */
  label: ReactNode;
  children: ReactNode;
  className?: string;
}

export function KvItem({ label, children, className }: KvItemProps) {
  return (
    <div className={cn('min-w-0', className)}>
      <dt className="mb-[3px] text-[10.5px] text-fg-muted">{label}</dt>
      <dd className="rounded-sm border border-divider bg-bg px-n3 py-1 text-xs text-fg">
        {children}
      </dd>
    </div>
  );
}
