import type { HTMLAttributes, ReactNode } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: hi-fi モックの `.note`（小さな地の文。前提・制約・補足）。
//
// **`Alert` とは別の部品である。** `Alert` は「注意を向けるべき事象」を色 ＋ アイコン ＋
// ラベルの 3 点セットで表す。`Note` は画面に**常に在る**説明文であり、アイコンもラベルも持たない
// ——モックでも `.note` は 45 箇所すべてが静的な注記である。`role` は付けない
// （常設の文をライブリージョンにすると、画面を開くたびに読み上げられる）。
const noteVariants = cva(
  'mt-n2 rounded-sm bg-surface-muted px-n3 py-1.5 text-[11px] leading-relaxed',
  {
    variants: {
      tone: {
        default: 'text-fg-muted',
        // 🔴 色は補強でしかない。**「なぜ注意なのか」は本文に書く**（INDEX 決定 21）。
        // 意味色はテーマ非依存の原色（--color-warn / --color-err）ではなく**意味トークン**を引く
        // （原色はダークの地を前提にした彩度であり、ライトの白面では読めない。Stat.tsx の注記参照）。
        warn: 'text-warning',
        err: 'text-danger',
      },
    },
    defaultVariants: { tone: 'default' },
  },
);

export interface NoteProps
  extends HTMLAttributes<HTMLParagraphElement>, VariantProps<typeof noteVariants> {
  children: ReactNode;
}

export function Note({ className, tone, children, ...props }: NoteProps) {
  return (
    <p className={cn(noteVariants({ tone }), className)} {...props}>
      {children}
    </p>
  );
}
