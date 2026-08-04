import type { ButtonHTMLAttributes } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '../lib/cn';

// IADR-0121 決定 4: shadcn/ui 派生のプリミティブ。ドメイン語彙・通信・ルーティングを持たず、
// 「このリポジトリの外の SPA へ持って行っても意味が通る」範囲に留める。
export const buttonVariants = cva(
  'inline-flex items-center justify-center gap-2 rounded-[--radius-control] text-sm font-medium ' +
    'transition-colors disabled:pointer-events-none disabled:opacity-50',
  {
    variants: {
      variant: {
        primary: 'bg-[--color-brand] text-[--color-brand-fg] hover:opacity-90',
        secondary: 'border border-[--color-border] bg-[--color-surface] text-[--color-fg] hover:bg-[--color-surface-muted]',
        ghost: 'text-[--color-fg] hover:bg-[--color-surface-muted]',
        danger: 'bg-[--color-danger] text-white hover:opacity-90',
      },
      size: {
        sm: 'h-8 px-3',
        md: 'h-9 px-4',
        lg: 'h-10 px-5',
      },
    },
    defaultVariants: { variant: 'secondary', size: 'md' },
  },
);

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & VariantProps<typeof buttonVariants>;

export function Button({ className, variant, size, type, ...props }: ButtonProps) {
  // type を明示しない <button> はフォーム内で submit として振る舞う。既定を button に固定して
  // 「押したら意図せず送信される」事故を型ではなく既定値で防ぐ。
  return <button type={type ?? 'button'} className={cn(buttonVariants({ variant, size }), className)} {...props} />;
}
