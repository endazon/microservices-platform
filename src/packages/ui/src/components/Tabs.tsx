import * as TabsPrimitive from '@radix-ui/react-tabs';
import type { ComponentProps } from 'react';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: 移植の根拠は **hi-fi モックアップ**（`mockups/hi-fi/sc-09.html` が
// 「属性体系 / タグ辞書 / 辺の型 / ポリシー定義」の 4 区画を `seg` / `seg-opt` の切替として描き、
// 注記で「『辺の型』タブ」と呼ぶ）。**01_screens.md の SC-09 節に「タブ」の語は無い**——
// §主要素 は 4 区画を列挙するだけである（呼称の出所はモックアップであり本文ではない）。
//
// **ここだけ Radix を使う**（`Select` はネイティブ）。タブはロービングタブインデックス
// （矢印キーでの移動）と `role="tab"` / `aria-selected` / `aria-controls` の整合を自前で書くと
// 誤りやすい。shadcn/ui の Tabs も `@radix-ui/react-tabs` の薄いラッパである。

export const Tabs = TabsPrimitive.Root;

export function TabsList({ className, ...props }: ComponentProps<typeof TabsPrimitive.List>) {
  return (
    <TabsPrimitive.List
      className={cn(
        'inline-flex items-center gap-1 rounded-[--radius-control] border border-[--color-border] bg-[--color-surface-muted] p-1',
        className,
      )}
      {...props}
    />
  );
}

export function TabsTrigger({ className, ...props }: ComponentProps<typeof TabsPrimitive.Trigger>) {
  return (
    <TabsPrimitive.Trigger
      className={cn(
        'rounded-[--radius-control] px-3 py-1 text-sm text-[--color-fg-muted] transition-colors',
        // 選択状態は **色だけで示さない**（INDEX 決定 21）。Radix が付ける aria-selected により
        // 支援技術には状態が伝わり、視覚的には面（背景）と太字の 2 つの手掛かりを重ねる。
        'data-[state=active]:bg-[--color-surface] data-[state=active]:font-semibold data-[state=active]:text-[--color-fg]',
        'disabled:cursor-not-allowed disabled:opacity-50',
        className,
      )}
      {...props}
    />
  );
}

export function TabsContent({ className, ...props }: ComponentProps<typeof TabsPrimitive.Content>) {
  return <TabsPrimitive.Content className={cn('mt-3', className)} {...props} />;
}
