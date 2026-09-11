import type { ReactNode } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: hi-fi モックの `.stat`（大きな数字 ＋ uppercase の accent ラベル ＋ 補足）。
//
// 🔴 **`tone` は値の色を変えるだけである。意味を色に載せない**（INDEX 決定 21）。
//    「上限に近い」「異常」といった判断は **`meta` に文字で書く**か、
//    `StatusBadge` を隣に置いて示すこと。色は補強であって伝達手段ではない——
//    色覚特性・モノクロ印刷・スクリーンショットのいずれでも意味が残る形にする。
const statValueVariants = cva('block text-[22px] leading-tight font-medium', {
  variants: {
    tone: {
      default: 'text-fg',
      // 🔴 **意味色は「テーマ非依存の原色」ではなく意味トークンを引く。** `--color-ok/warn/err` は
      // ダークの地を前提にした彩度であり、ライトの白面では読めない（実測: warn を白面へ置くと
      // コントラストが 1.4:1 程度まで落ちる）。`success` / `warning` / `danger` は
      // テーマごとに値が入れ替わるので、両テーマで 4.5:1 以上を保てる。
      ok: 'text-success',
      warn: 'text-warning',
      err: 'text-danger',
    },
  },
  defaultVariants: { tone: 'default' },
});

export interface StatProps extends VariantProps<typeof statValueVariants> {
  /** 指標の名前。モックでは uppercase の accent 色。 */
  label: ReactNode;
  /** 指標の値。 */
  value: ReactNode;
  /** 補足（前日比・母数など。任意）。**色が担う意味はここへ文字で書く。** */
  meta?: ReactNode;
  className?: string;
}

export function Stat({ label, value, meta, tone, className }: StatProps) {
  return (
    <div className={cn('flex flex-col gap-[2px]', className)}>
      <span className="text-[10px] tracking-[0.08em] uppercase text-accent">{label}</span>
      <span className={statValueVariants({ tone })}>{value}</span>
      {meta === undefined ? null : <span className="text-[11px] text-fg-muted">{meta}</span>}
    </div>
  );
}
