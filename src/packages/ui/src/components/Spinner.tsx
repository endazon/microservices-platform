import { Loader2 } from 'lucide-react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: hi-fi モックの「待ち」の表現。**待ち・空・エラーの三部品**の
// 最小単位であり、単独では「回っている輪」だけを描く。
//
// **`label` は必須である。** 回転するアイコンは装飾であり、支援技術には何も伝わらない
// （INDEX 決定 21 の敷衍。`StatusBadge` / `Alert` と同じ作法で、「印はあるが読み上げが無い」
// という選択肢を型から消す）。文言そのものはプリミティブが持たない（IADR-0125 決定 1）——
// 呼び出し側が翻訳済みの文字列を渡す。
const spinnerVariants = cva('inline-flex shrink-0 animate-spin text-fg-muted', {
  variants: {
    size: {
      sm: 'size-4',
      md: 'size-6',
    },
  },
  defaultVariants: { size: 'md' },
});

export interface SpinnerProps extends VariantProps<typeof spinnerVariants> {
  /** 何を待っているかを表す文言。視覚的には隠すが読み上げには残す。**必須**である。 */
  label: string;
  className?: string;
}

export function Spinner({ label, size, className }: SpinnerProps) {
  return (
    // role="status" は**礼儀正しい**ライブリージョン（role="alert" と違って読み上げを割り込ませない）。
    // 待ちは割り込むべき事象ではない。
    <span role="status" className={cn('inline-flex items-center', className)}>
      <Loader2 className={spinnerVariants({ size })} aria-hidden />
      <span className="sr-only">{label}</span>
    </span>
  );
}
