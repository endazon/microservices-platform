import type { ReactNode } from 'react';
import type { LucideIcon } from 'lucide-react';
import { Button, Tooltip, TooltipContent, TooltipTrigger, cn } from '@platform/ui';
import type { ButtonProps } from '@platform/ui';

// 05_screens §共通シェル（右レール AI チャット）/ SC-01: アイコンだけのボタン（履歴の消去・コピー・閉じる）。
//
// 🔴 **ツールチップは補強であり、名前の唯一の担い手ではない**（`@platform/ui` の Tooltip の注記）。
//    `label` は `aria-label` として必ず付き、ツールチップにも同じ文言を出す。ホバー・フォーカスで
//    しか出ないツールチップに頼ると、触操作と読み上げの一部に名前が届かない。
//
// `Tooltip` の根（`TooltipProvider`）は使う側（レール・画面）が 1 つ置く。

export interface IconButtonProps extends Omit<ButtonProps, 'children' | 'aria-label'> {
  /** ボタンの名前（`aria-label`）。ツールチップの本文にもなる。 */
  label: string;
  icon: LucideIcon;
  /** ツールチップに `label` より詳しい説明を出したいとき。省略時は `label`。 */
  tip?: ReactNode;
}

export function IconButton({ label, icon: Icon, tip, className, size, ...props }: IconButtonProps) {
  return (
    <Tooltip>
      <TooltipTrigger
        render={
          <Button
            aria-label={label}
            variant="ghost"
            size={size ?? 'sm'}
            className={cn('px-2', className)}
            {...props}
          />
        }
      >
        <Icon className="size-4" aria-hidden />
      </TooltipTrigger>
      <TooltipContent>{tip ?? label}</TooltipContent>
    </Tooltip>
  );
}
