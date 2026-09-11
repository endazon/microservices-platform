import type { HTMLAttributes, ReactNode } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: hi-fi モックの `.panel`（本文の区画）と `.panel.ghost`（破線の器）。
//
// **`Card` との違い**: `Card` は枠線で囲った「カード」（一覧に並ぶ独立した単位）、
// `Panel` は本文の**区画**（面の色で地から持ち上げ、罫線を持たない）。モックは両方を
// 別のクラスとして描き分けており、`.panel` のほうが圧倒的に多い（48 箇所）。
const panelVariants = cva('mb-n3 rounded-md px-n4 py-n3', {
  variants: {
    variant: {
      default: 'bg-surface',
      // モックの `.panel.ghost`: 「まだ中身が無い／補助的」を破線で表す。
      ghost: 'border border-dashed border-border bg-transparent',
    },
  },
  defaultVariants: { variant: 'default' },
});

export interface PanelProps
  extends HTMLAttributes<HTMLElement>, VariantProps<typeof panelVariants> {
  /** 区画の見出し（任意）。モックの `.panel h4` に相当する。 */
  heading?: ReactNode;
  /**
   * 見出しの要素。既定は `<h2>`。見出し階層は文書構造の問題であり画面ごとに変わるため
   * 上書きできる（`CardTitle` の `as` と同じ作法）。
   */
  headingAs?: 'h2' | 'h3' | 'h4';
  children: ReactNode;
}

export function Panel({
  className,
  variant,
  heading,
  headingAs: Heading = 'h2',
  children,
  ...props
}: PanelProps) {
  return (
    <section className={cn(panelVariants({ variant }), className)} {...props}>
      {heading === undefined ? null : (
        <Heading className="mb-n2 text-xs font-medium tracking-[0.05em] text-fg-muted">
          {heading}
        </Heading>
      )}
      {children}
    </section>
  );
}
