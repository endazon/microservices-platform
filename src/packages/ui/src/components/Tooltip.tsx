import { Tooltip as BaseTooltip } from '@base-ui/react/tooltip';
import type { ComponentProps } from 'react';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: **アイコンだけのボタンの補助**に使う。
// 土台は Base UI（`Dialog` と同じ理由。ポータルの配置計算と aria-describedby の同期が要る）。
//
// 🔴 **ツールチップを「意味の唯一の担い手」にしない。** ホバーとフォーカスでしか出ないため、
//    触操作・読み上げの一部・印刷では届かない。アイコンボタンには **`aria-label` を必ず付ける**
//    （ツールチップはその補強である。INDEX 決定 21 と同じ考え方）。

/** 複数のツールチップで遅延を共有する（1 つ出たあとは隣が即座に出る）。アプリの根へ 1 つ置く。 */
export const TooltipProvider = BaseTooltip.Provider;

/** 1 つのツールチップの根（HTML 要素を描かない）。 */
export const Tooltip = BaseTooltip.Root;

/** ツールチップを出す対象。既存部品へ被せるときは `render={<Button … />}` を使う。 */
export const TooltipTrigger = BaseTooltip.Trigger;

export interface TooltipContentProps extends Omit<
  ComponentProps<typeof BaseTooltip.Popup>,
  'className'
> {
  className?: string;
  /** 対象からの距離（px。既定 6）。 */
  sideOffset?: number;
}

export function TooltipContent({ className, sideOffset = 6, ...props }: TooltipContentProps) {
  return (
    <BaseTooltip.Portal>
      <BaseTooltip.Positioner sideOffset={sideOffset}>
        <BaseTooltip.Popup
          className={cn(
            'z-50 max-w-64 rounded-md border border-border bg-surface px-n3 py-n1 text-xs text-fg shadow-md',
            className,
          )}
          {...props}
        />
      </BaseTooltip.Positioner>
    </BaseTooltip.Portal>
  );
}
