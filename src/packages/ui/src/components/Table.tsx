import type {
  HTMLAttributes,
  TableHTMLAttributes,
  TdHTMLAttributes,
  ThHTMLAttributes,
} from 'react';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: 移植の根拠は SC-02「結果テーブル」・SC-06「ソース一覧テーブル」・
// SC-07「ジョブ一覧テーブル」・SC-05「文書一覧」と hi-fi モックアップの `<table>`（20 箇所）。
// 既存 11 画面でも `<table>` が 10 組（`<th>` 50 / `<td>` 50）ある。
//
// 意味づけ（TanStack Table によるソート・ページングなど）は入れない——それは feature の関心であり、
// 第 4 段（IADR-0121 決定 1）の範囲である。ここが持つのは表の見た目と表構造の a11y だけである。

/** 表本体。横スクロールの受け皿を兼ねる（狭い画面で表が画面外へはみ出さないようにする）。 */
export function Table({ className, ...props }: TableHTMLAttributes<HTMLTableElement>) {
  return (
    <div className="w-full overflow-x-auto">
      <table className={cn('w-full border-collapse text-sm text-[--color-fg]', className)} {...props} />
    </div>
  );
}

/**
 * 表の説明。**視覚的には隠すが読み上げには残す**のが既定である。
 * 表が何の一覧なのかは見出しから読み取れることが多い一方、支援技術には表単体で届く必要がある。
 */
export function TableCaption({ className, ...props }: HTMLAttributes<HTMLTableCaptionElement>) {
  return <caption className={cn('sr-only', className)} {...props} />;
}

export function TableHead({ className, ...props }: HTMLAttributes<HTMLTableSectionElement>) {
  return <thead className={cn('border-b border-[--color-border]', className)} {...props} />;
}

export function TableBody({ className, ...props }: HTMLAttributes<HTMLTableSectionElement>) {
  return <tbody className={className} {...props} />;
}

export function TableRow({ className, ...props }: HTMLAttributes<HTMLTableRowElement>) {
  return (
    <tr
      className={cn(
        'border-b border-[--color-border] last:border-0 hover:bg-[--color-surface-muted]',
        className,
      )}
      {...props}
    />
  );
}

/**
 * 見出しセル。`scope` の既定を `col` にする——省略できる API にすると必ず省略され、
 * スクリーンリーダが行と列の対応を読めなくなる（`Button` の `type` を既定値で固定したのと同じ作法）。
 */
export function TableHeaderCell({ className, scope, ...props }: ThHTMLAttributes<HTMLTableCellElement>) {
  return (
    <th
      scope={scope ?? 'col'}
      className={cn('px-3 py-2 text-left font-medium text-[--color-fg-muted]', className)}
      {...props}
    />
  );
}

export function TableCell({ className, ...props }: TdHTMLAttributes<HTMLTableCellElement>) {
  return <td className={cn('px-3 py-2 align-top', className)} {...props} />;
}
