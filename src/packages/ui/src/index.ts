// IADR-0121 決定 4 / IADR-0125 決定 1: @platform/ui の公開面はこのファイルのみ。各ユニットは深い相対参照
// （@platform/ui/src/...）を行わない（eslint の no-restricted-imports で機械的に禁止する）。
//
// 入れてよいのはデザイントークン・cn()・shadcn/ui 派生プリミティブのみで、ドメイン語彙・BFF 通信・
// ルーティング・認証・実行時 config は入れない。**表示文言も持たない**（IADR-0125 決定 1）——
// 文言を内蔵すると i18n の入口が 2 つに割れ、カタログの網羅検査（IADR-0125 決定 4）を抜ける。
export { cn } from './lib/cn';
export { Button, buttonVariants, type ButtonProps } from './components/Button';
export { StatusBadge, type StatusBadgeProps } from './components/StatusBadge';
// SC-01/SC-02/SC-03（#502）: 分類の名前を表すチップ。状態を表す StatusBadge とは別の部品である
// （Tag.tsx の冒頭コメント参照）。
export { Tag, tagVariants, type TagProps } from './components/Tag';
export { Input, inputVariants, type InputProps } from './components/Input';
export { Textarea, textareaVariants, type TextareaProps } from './components/Textarea';
export { Select, selectVariants, type SelectProps } from './components/Select';
export { Label, type LabelProps } from './components/Label';
export { Alert, type AlertProps } from './components/Alert';
export { Card, CardHeader, CardTitle, CardContent } from './components/Card';
export {
  Table,
  TableCaption,
  TableHead,
  TableBody,
  TableRow,
  TableHeaderCell,
  TableCell,
} from './components/Table';
export { Tabs, TabsList, TabsTrigger, TabsContent } from './components/Tabs';

// ── Nocturne（ADR-0031 / hi-fi モック）で足した部品 ────────────────────────────
// いずれも **表示文言を持たない**（IADR-0125 決定 1）。ラベルは props で受ける。

// 待ち・空・エラーの三部品。**この 3 つを別の部品に分けているのが要点**である——
// 「0 件」と「失敗」を同じ見た目にすると、利用者は再試行すべきか条件を変えるべきか判断できない。
export { Spinner, type SpinnerProps } from './components/Spinner';
export { Skeleton, type SkeletonProps } from './components/Skeleton';
export { LoadingState, type LoadingStateProps } from './components/LoadingState';
export { EmptyState, type EmptyStateProps } from './components/EmptyState';
export { ErrorState, type ErrorStateProps } from './components/ErrorState';

// 本文の区画と、その中に置く要素（hi-fi モックの .panel / .stat / .bar / .kv / .note / .hr）。
export { Panel, type PanelProps } from './components/Panel';
export { Stat, type StatProps } from './components/Stat';
export { ProgressBar, type ProgressBarProps } from './components/ProgressBar';
export { Kv, KvItem, type KvProps, type KvItemProps } from './components/Kv';
export { Note, type NoteProps } from './components/Note';
export { Rule } from './components/Rule';

// 重なりの部品（Base UI）。`Dialog` は `initialFocus` を通す——確認ダイアログの初期フォーカスを
// 取消側へ当てる既存の規律を、ラッパが落とさないため。
export {
  Dialog,
  DialogTrigger,
  DialogContent,
  DialogTitle,
  DialogDescription,
  DialogActions,
  DialogClose,
  type DialogContentProps,
} from './components/Dialog';
export {
  Tooltip,
  TooltipProvider,
  TooltipTrigger,
  TooltipContent,
  type TooltipContentProps,
} from './components/Tooltip';
