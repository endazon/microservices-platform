import { AlertTriangle, CircleCheck, CircleX, Info } from 'lucide-react';
import type { ComponentType } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '../lib/cn';

// INDEX 決定 21 / IADR-0121 決定 4:「色だけで意味を持たせない」を型で強制するプリミティブ。
// 状態の表現は必ず「色 ＋ アイコン ＋ テキスト」の 3 点セットにする。色覚特性・モノクロ印刷・
// 低コントラスト環境のいずれでも意味が失われないようにするためである。
//
// 本コンポーネントは label を必須の children として受け取り、tone ごとに固定のアイコンを描画する。
// 呼び出し側はアイコンを省略できない（省略できる API にすると必ず省略されるため、選択肢を作らない）。
const badgeVariants = cva(
  'inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-xs font-medium',
  {
    variants: {
      tone: {
        neutral: 'border-[--color-border] bg-[--color-surface-muted] text-[--color-fg-muted]',
        success: 'border-[--color-success] text-[--color-success]',
        warning: 'border-[--color-warning] text-[--color-warning]',
        danger: 'border-[--color-danger] text-[--color-danger]',
      },
    },
    defaultVariants: { tone: 'neutral' },
  },
);

type Tone = NonNullable<NonNullable<VariantProps<typeof badgeVariants>>['tone']>;

/** tone とアイコンの対応。tone を増やすときはここも必ず埋まる（Record が網羅を強制する）。 */
const TONE_ICONS: Record<Tone, ComponentType<{ className?: string; 'aria-hidden'?: boolean }>> = {
  neutral: Info,
  success: CircleCheck,
  warning: AlertTriangle,
  danger: CircleX,
};

export interface StatusBadgeProps extends VariantProps<typeof badgeVariants> {
  /** 状態を表す文言。色に頼らず意味を伝えるため必須とする。 */
  children: string;
  className?: string;
}

export function StatusBadge({ tone, children, className }: StatusBadgeProps) {
  const Icon = TONE_ICONS[tone ?? 'neutral'];
  return (
    <span className={cn(badgeVariants({ tone }), className)}>
      {/* アイコンは装飾。意味はテキストが担うため支援技術からは隠す。 */}
      <Icon className="size-3.5" aria-hidden />
      {children}
    </span>
  );
}
