import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: hi-fi モックの `.bar`（上限使用率・進捗）。
//
// **`role="progressbar"` ＋ `aria-valuenow` / `aria-valuemax` ＋ `aria-label` を型で強制する。**
// `label` を必須にするのは、進捗バーが「何の進捗か」を持たないと読み上げが
// 「50 パーセント」だけになるためである（INDEX 決定 21 と同じ理由づけ）。
//
// **割合の数値は必ず目に見える形でも出す**（色と長さだけに頼らない）。
const fillVariants = cva('h-full rounded-full transition-[width]', {
  variants: {
    tone: {
      default: 'bg-accent',
      // 意味色はテーマ非依存の原色ではなく意味トークンを引く（Stat.tsx の注記参照）。
      warn: 'bg-warning',
      err: 'bg-danger',
    },
  },
  defaultVariants: { tone: 'default' },
});

export interface ProgressBarProps extends VariantProps<typeof fillVariants> {
  /** 現在値。`0 <= value <= max` へ丸める（範囲外の値で棒がはみ出さないようにする）。 */
  value: number;
  /** 上限（既定 100）。 */
  max?: number;
  /** 何の進捗かを表す文言。**必須**である（読み上げの名前になる）。 */
  label: string;
  className?: string;
}

export function ProgressBar({ value, max = 100, label, tone, className }: ProgressBarProps) {
  const safeMax = max > 0 ? max : 100;
  const clamped = Math.min(Math.max(value, 0), safeMax);
  const percent = Math.round((clamped / safeMax) * 100);
  return (
    <div className={cn('flex items-center gap-n2', className)}>
      <div
        role="progressbar"
        aria-label={label}
        aria-valuenow={clamped}
        aria-valuemin={0}
        aria-valuemax={safeMax}
        className="h-1.5 min-w-0 flex-1 overflow-hidden rounded-full bg-surface-muted"
      >
        <div className={fillVariants({ tone })} style={{ width: `${percent}%` }} />
      </div>
      {/* 色と長さだけに意味を載せない。数値は常に文字でも出す。 */}
      <span aria-hidden className="shrink-0 text-[11px] tabular-nums text-fg-muted">
        {percent}%
      </span>
    </div>
  );
}
