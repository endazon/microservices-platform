import type { HTMLAttributes } from 'react';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: hi-fi モックの `.hr`。
// Nocturne の署名的な表現で、**罫線が両端で透明へ抜ける**（48px のランプ）。
// 箱の枠線や部品内の仕切りは実線のままで、**独立した区切り線だけ**がフェードする。
export function Rule({ className, ...props }: HTMLAttributes<HTMLHRElement>) {
  return (
    <hr
      className={cn(
        'my-n4 h-px border-0',
        'bg-[linear-gradient(to_right,transparent,var(--color-divider)_48px,var(--color-divider)_calc(100%-48px),transparent)]',
        className,
      )}
      {...props}
    />
  );
}
