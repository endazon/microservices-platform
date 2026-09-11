import { CircleAlert } from 'lucide-react';
import type { ReactNode } from 'react';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: **待ち・空・エラーの三部品**の「エラー」。
//
// INDEX 決定 21「色だけで意味を持たせない」: 危険色 ＋ **固定のアイコン** ＋ テキストの
// 3 点セットで表す（`Alert` / `StatusBadge` と同じ作法）。アイコンは省略できる API にしない——
// 省略できるようにすると必ず省略される。`icon` prop は**差し替え**であって省略ではない。
//
// `role="alert"` を**部品側で決める**のは、失敗は割り込んで知らせるべき事象だからである
// （`Alert` は静的な注記にも使うため role を呼び出し側に委ねているが、ここは用途が 1 つに定まる）。
//
// **再試行できるかどうかは部品が決めない。** 再試行が意味を持つ失敗かは呼び出し側の判断であり、
// ここは `action` スロットを空けて受けるだけである（再試行ボタンを既定で描くと、
// 押しても直らない失敗にも再試行が出る）。
export interface ErrorStateProps {
  /** 「読み込みに失敗しました」等。**必須**である。 */
  title: ReactNode;
  /** 原因・対処の案内（任意）。 */
  description?: ReactNode;
  /** 次の一手（再試行ボタン等）。 */
  action?: ReactNode;
  /** アイコンの差し替え（任意。既定は CircleAlert）。 */
  icon?: ReactNode;
  className?: string;
}

export function ErrorState({ title, description, action, icon, className }: ErrorStateProps) {
  return (
    <div
      role="alert"
      className={cn(
        'flex flex-col items-center gap-n2 rounded-md border border-danger px-n4 py-n8 text-center',
        className,
      )}
    >
      <span className="text-danger">{icon ?? <CircleAlert className="size-6" aria-hidden />}</span>
      <p className="text-sm font-medium text-danger">{title}</p>
      {description === undefined ? null : <p className="text-xs text-fg-muted">{description}</p>}
      {action === undefined ? null : <div className="mt-n1">{action}</div>}
    </div>
  );
}
