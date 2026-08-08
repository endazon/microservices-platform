import type { HTMLAttributes } from 'react';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: 移植の根拠は hi-fi モックアップの `panel`（48 箇所）と、
// SC-03「属性・タグパネル」「バージョン履歴パネル」・SC-08「結果パネル」・SC-10 の統計区画。
// 画面の区画（見出し ＋ 中身）の枠だけを担い、中身の語彙は持たない。

export function Card({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn(
        'rounded-[--radius-control] border border-[--color-border] bg-[--color-surface] p-4',
        className,
      )}
      {...props}
    />
  );
}

export function CardHeader({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return (
    <div className={cn('mb-3 flex items-center justify-between gap-2', className)} {...props} />
  );
}

/**
 * 区画の見出し。既定は `<h2>` だが、見出し階層は文書構造の問題であり画面ごとに変わるため
 * `as` で上書きできる（`<h2>` を入れ子にすると見出しの階層が壊れる画面がある）。
 */
export function CardTitle({
  className,
  as: Tag = 'h2',
  ...props
}: HTMLAttributes<HTMLHeadingElement> & { as?: 'h2' | 'h3' | 'h4' }) {
  return <Tag className={cn('text-sm font-semibold text-[--color-fg]', className)} {...props} />;
}

export function CardContent({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('text-sm text-[--color-fg]', className)} {...props} />;
}
