import type { ComponentPropsWithRef } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '../lib/cn';

// IADR-0121 決定 4: shadcn/ui 派生のプリミティブ。ドメイン語彙・通信・ルーティングを持たず、
// 「このリポジトリの外の SPA へ持って行っても意味が通る」範囲に留める。
// ADR-0031 / IADR-0125 決定 1 / hi-fi モック `.btn`: Nocturne のボタンは**塗らない**。
// primary は accent の枠線と accent の文字だけで、面は透明のまま（`.btn-primary`）。
// secondary は divider の枠、ghost は枠すら持たない。塗り分けではなく**枠と文字色**で
// 強弱を作るのがこのデザインシステムの語彙である。
//
// 枠の有無でボタンの寸法が変わらないよう、base で `border border-transparent` を敷いて
// バリアントは色だけを替える（枠を付けたときだけ 2px 背が高くなる事故を防ぐ）。
export const buttonVariants = cva(
  'inline-flex items-center justify-center gap-1.5 rounded-md border border-transparent text-sm font-medium ' +
    'leading-tight transition-colors disabled:pointer-events-none disabled:opacity-45',
  {
    variants: {
      variant: {
        primary:
          'border-accent text-accent hover:bg-[color-mix(in_srgb,var(--color-accent)_12%,transparent)] ' +
          'active:bg-[color-mix(in_srgb,var(--color-accent)_22%,transparent)]',
        secondary:
          'border-divider text-fg hover:bg-[color-mix(in_srgb,var(--color-fg)_7%,transparent)] ' +
          'active:bg-[color-mix(in_srgb,var(--color-fg)_14%,transparent)]',
        ghost:
          'text-accent hover:bg-[color-mix(in_srgb,var(--color-accent)_10%,transparent)] ' +
          'active:bg-[color-mix(in_srgb,var(--color-accent)_18%,transparent)]',
        // 破壊的操作。**塗りではなく危険色の枠**で示す（他の 3 つと同じ語彙に揃える）。
        // 色だけに意味を載せないのは呼び出し側の責務である——ラベルに「削除」と書く
        // （INDEX 決定 21）。
        danger:
          'border-danger text-danger hover:bg-[color-mix(in_srgb,var(--color-danger)_12%,transparent)] ' +
          'active:bg-[color-mix(in_srgb,var(--color-danger)_22%,transparent)]',
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

// React 19 では関数コンポーネントでも `ref` が素の prop であり、`{...props}` で
// そのまま `<button>` へ渡る（`forwardRef` は要らない）。**型のほうも `ref` を受ける形にしておく**
// ——Base UI の `render={<Button …/>}` は要素へ ref を載せて合成するため、
// `ButtonHTMLAttributes` のままだと Dialog / Tooltip の土台と組めない
// （確認ダイアログの「取消側へ初期フォーカス」もこの経路を通る）。
export type ButtonProps = ComponentPropsWithRef<'button'> & VariantProps<typeof buttonVariants>;

export function Button({ className, variant, size, type, ...props }: ButtonProps) {
  // type を明示しない <button> はフォーム内で submit として振る舞う。既定を button に固定して
  // 「押したら意図せず送信される」事故を型ではなく既定値で防ぐ。
  return (
    <button
      type={type ?? 'button'}
      className={cn(buttonVariants({ variant, size }), className)}
      {...props}
    />
  );
}
