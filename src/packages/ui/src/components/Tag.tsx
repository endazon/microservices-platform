import type { HTMLAttributes } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '../lib/cn';

// SC-01/SC-02/SC-03（#502）/ IADR-0125 決定 1: 分類の名前（タグ・種別）を表すチップ。
// 移植の根拠は hi-fi モックアップの `tag`（全画面で 120 箇所。`tag-accent` / `tag-neutral` /
// `tag-outline` の 3 種）と、SC-02「結果テーブル（文書／タグ）」・SC-03「属性・タグパネル」・
// SC-01 の出典の種別ラベル（「組織文書」）。
//
// **`StatusBadge` とは別の部品である。** `StatusBadge` は tone ごとに固定アイコン（Info /
// CircleCheck / AlertTriangle / CircleX）を描く「**状態**」の部品で、INDEX 決定 21
// （色だけで意味を持たせない）を型で強制する設計である。タグは状態ではなく**分類の名前**であり、
// 意味は文字そのものが担う（色を除いても読める）。Info アイコンが付くと意味が変わる。
// hi-fi モックも `tag`（分類）と `ok` / `warn` / `err`（状態）を別の語彙として描き分けている。
//
// 計画 13_frontend-stack §shadcn/ui 派生の範囲 の 4 基準（フォーカストラップ／複合キーボード操作／
// ポータルの配置計算／aria-* の動的同期）はいずれも**非該当**（非対話・無状態の <span>）であるため、
// Radix を使わずネイティブ HTML ＋ cva ＋ cn() で実装する。
//
// **文言は持たない**（IADR-0125 決定 1）。呼び出し側が翻訳済みの文字列を children で渡す。
export const tagVariants = cva(
  'inline-flex items-center rounded-[--radius-control] px-2 py-0.5 text-xs whitespace-nowrap',
  {
    variants: {
      tone: {
        // モックの tag-accent: 強調（主要な分類）。
        accent: 'bg-[--color-brand] text-[--color-brand-fg]',
        // モックの tag-neutral: 既定（一般のタグ）。
        neutral: 'bg-[--color-surface-muted] text-[--color-fg-muted]',
        // モックの tag-outline: 枠線のみ（種別の注記）。
        outline: 'border border-[--color-border] text-[--color-fg-muted]',
      },
    },
    defaultVariants: { tone: 'neutral' },
  },
);

export interface TagProps
  extends HTMLAttributes<HTMLSpanElement>,
    VariantProps<typeof tagVariants> {
  /** 分類の名前。**必須**である（色や枠線だけで意味を持たせない。INDEX 決定 21）。 */
  children: string;
}

export function Tag({ className, tone, children, ...props }: TagProps) {
  return (
    <span className={cn(tagVariants({ tone }), className)} {...props}>
      {children}
    </span>
  );
}
