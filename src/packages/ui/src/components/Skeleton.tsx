import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: 取得待ちの**場所取り**。
//
// 🔴 **高さが分かっている器にだけ使う。** 中身の高さが読めない場所へ置くと、
// 実データが来た瞬間に行が伸び縮みして読んでいる位置が飛ぶ（取得前に器を描かない、の例外は
// 「器の寸法が先に決まっている」場合だけである）。
//
// **支援技術からは隠す**（`aria-hidden`）。骨組みは意味を持たない装飾であり、
// 待ちを伝えるのは同居する `Spinner` / `LoadingState` の役割である。
export interface SkeletonProps {
  /** 描く行数（既定 1）。表の行数など、**実データの行数が先に分かっている**ときに使う。 */
  lines?: number;
  className?: string;
}

export function Skeleton({ lines = 1, className }: SkeletonProps) {
  return (
    <div aria-hidden className={cn('flex flex-col gap-n2', className)}>
      {Array.from({ length: Math.max(1, lines) }, (_, i) => (
        <div key={i} className="h-4 animate-pulse rounded-sm bg-surface-muted" />
      ))}
    </div>
  );
}
