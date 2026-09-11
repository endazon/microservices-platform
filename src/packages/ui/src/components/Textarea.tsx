import type { TextareaHTMLAttributes } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: 移植の根拠は SC-08「分析内容の入力（テキストエリア）」。
// Input と同じ枠線・状態の語彙を共有し、高さだけ複数行向けに変える。
export const textareaVariants = cva(
  'block w-full rounded-md border bg-surface px-3 py-2 text-sm text-fg ' +
    'placeholder:text-fg-muted focus-visible:border-accent ' +
    'disabled:cursor-not-allowed disabled:opacity-50',
  {
    variants: {
      invalid: {
        true: 'border-danger',
        false: 'border-divider',
      },
    },
    defaultVariants: { invalid: false },
  },
);

export type TextareaProps = TextareaHTMLAttributes<HTMLTextAreaElement> &
  VariantProps<typeof textareaVariants>;

export function Textarea({ className, invalid, rows, ...props }: TextareaProps) {
  return (
    <textarea
      rows={rows ?? 4}
      aria-invalid={invalid ? true : props['aria-invalid']}
      className={cn(textareaVariants({ invalid }), className)}
      {...props}
    />
  );
}
