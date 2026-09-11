import type { ReactNode } from 'react';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: **待ち・空・エラーの三部品**の「空」。
//
// 🔴 **「0 件」と「失敗」を同じ見た目にしない。** 空は正常な結果であり、
// 再試行を促すものではない（失敗は `ErrorState` が `role="alert"` と危険色で表す）。
// ここに `role` を付けないのはそのためである——空は割り込んで知らせる事象ではない。
//
// **文言は持たない**（IADR-0125 決定 1）。呼び出し側が翻訳済みの節点を渡す。
export interface EmptyStateProps {
  /** 「該当する文書がありません」等。**必須**である。 */
  title: ReactNode;
  /** 条件の変え方など、次の一手の案内（任意）。 */
  description?: ReactNode;
  /** 次の一手（`Button` 等）。空は再試行ではないので、置くなら「条件を変える」側である。 */
  action?: ReactNode;
  /** 装飾アイコン（任意）。意味は title が担うので、呼び出し側で `aria-hidden` にする。 */
  icon?: ReactNode;
  className?: string;
}

export function EmptyState({ title, description, action, icon, className }: EmptyStateProps) {
  return (
    <div
      className={cn(
        'flex flex-col items-center gap-n2 rounded-md border border-dashed border-border px-n4 py-n8 text-center',
        className,
      )}
    >
      {icon === undefined ? null : <span className="text-fg-muted">{icon}</span>}
      <p className="text-sm font-medium text-fg">{title}</p>
      {description === undefined ? null : <p className="text-xs text-fg-muted">{description}</p>}
      {action === undefined ? null : <div className="mt-n1">{action}</div>}
    </div>
  );
}
