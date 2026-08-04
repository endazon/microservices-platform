import type { SelectHTMLAttributes } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: 移植の根拠は SC-05「機密区分（選択・必須）」・
// SC-09「対象属性（選択・必須）」・SC-01「対象範囲フィルタ」・SC-02（検索モード／並び順の切替）。
//
// **Radix ではなくネイティブ `<select>` を採る**（IADR-0125 決定 1）。計画が要求しているのは
// 「定義済み区分のみ」「権限内のタグ／フォルダのみ」という**値の選択**であり、
// ネイティブならモバイル・スクリーンリーダ・キーボードの既定挙動をそのまま得られる。
// shadcn/ui の Radix Select はポータル描画のカスタムリストボックスで、得られる意味の割に
// テストの足場と実行時依存が増える。
export const selectVariants = cva(
  'block w-full appearance-none rounded-[--radius-control] border bg-[--color-surface] px-3 pr-8 text-sm ' +
    'text-[--color-fg] disabled:cursor-not-allowed disabled:opacity-50',
  {
    variants: {
      selectSize: {
        sm: 'h-8',
        md: 'h-9',
        lg: 'h-10',
      },
      invalid: {
        true: 'border-[--color-danger]',
        false: 'border-[--color-border]',
      },
    },
    defaultVariants: { selectSize: 'md', invalid: false },
  },
);

export type SelectProps = Omit<SelectHTMLAttributes<HTMLSelectElement>, 'size'> &
  VariantProps<typeof selectVariants>;

export function Select({ className, selectSize, invalid, children, ...props }: SelectProps) {
  return (
    <select
      aria-invalid={invalid ? true : props['aria-invalid']}
      className={cn(selectVariants({ selectSize, invalid }), className)}
      {...props}
    >
      {children}
    </select>
  );
}
