import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';

// IADR-0121 決定 4: shadcn/ui 系の標準ユーティリティ。条件付きクラス（clsx）を組み立てたうえで、
// 競合する Tailwind ユーティリティを後勝ちで解決する（tailwind-merge）。
// バリアント定義（cva）と呼び出し側の className を安全に合成するための唯一の入口である。
export function cn(...inputs: ClassValue[]): string {
  return twMerge(clsx(inputs));
}
