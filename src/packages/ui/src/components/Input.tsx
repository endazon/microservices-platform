import type { InputHTMLAttributes } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0121 決定 4 / IADR-0125 決定 1: shadcn/ui 派生のプリミティブ。
// 移植の根拠は SC-01（質問／キーワード）・SC-02（検索ボックス）・SC-05（タイトル）と
// hi-fi モックアップの `inp`。ドメイン語彙・通信・ルーティングを持たず、**文言も持たない**
// （持つと i18n の入口が 2 つに割れる。IADR-0125 決定 1）。
export const inputVariants = cva(
  // hi-fi モック `.input`: surface の面・divider の枠・radius-md。フォーカスで枠が accent になる
  // （大域の :focus-visible の輪郭は styles.css が別に与える）。
  'block w-full rounded-md border bg-surface px-3 text-sm text-fg ' +
    'placeholder:text-fg-muted focus-visible:border-accent ' +
    'disabled:cursor-not-allowed disabled:opacity-50',
  {
    variants: {
      inputSize: {
        sm: 'h-8',
        md: 'h-9',
        lg: 'h-10',
      },
      // 入力エラーは色だけで示さない（INDEX 決定 21）。呼び出し側は aria-invalid と併せて
      // エラー本文（Alert / ErrorList）を必ず示す。ここは枠線の見た目だけを担う。
      invalid: {
        true: 'border-danger',
        false: 'border-divider',
      },
    },
    defaultVariants: { inputSize: 'md', invalid: false },
  },
);

export type InputProps = Omit<InputHTMLAttributes<HTMLInputElement>, 'size'> &
  VariantProps<typeof inputVariants>;

export function Input({ className, inputSize, invalid, ...props }: InputProps) {
  return (
    <input
      // 見た目（invalid バリアント）と支援技術への通知（aria-invalid）を必ず揃える。
      // 片方だけを付けると「赤いが読み上げられない」「読み上げるが見えない」状態になる。
      aria-invalid={invalid ? true : props['aria-invalid']}
      className={cn(inputVariants({ inputSize, invalid }), className)}
      {...props}
    />
  );
}
