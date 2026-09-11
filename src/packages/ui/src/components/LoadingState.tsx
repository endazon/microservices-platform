import type { ReactNode } from 'react';
import { cn } from '../lib/cn';
import { Spinner } from './Spinner';

// ADR-0031 / IADR-0125 決定 1: **待ち・空・エラーの三部品**の「待ち」。
// 区画まるごとの待ちを表す（`Spinner` は輪だけ、こちらは輪 ＋ 見える文言）。
export interface LoadingStateProps {
  /** 何を待っているかを表す文言。**必須**である。 */
  label: string;
  className?: string;
  /** 追加の補足（任意）。 */
  description?: ReactNode;
}

export function LoadingState({ label, description, className }: LoadingStateProps) {
  return (
    <div className={cn('flex flex-col items-center gap-n2 px-n4 py-n8 text-center', className)}>
      {/* 読み上げ名は Spinner の sr-only ラベルが 1 度だけ与える。**見えている文言は
          `aria-hidden`** —— 両方読ませると同じ語が 2 回読まれる。 */}
      <Spinner label={label} />
      <span aria-hidden className="text-sm text-fg-muted">
        {label}
      </span>
      {description === undefined ? null : <p className="text-xs text-fg-muted">{description}</p>}
    </div>
  );
}
