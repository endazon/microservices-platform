import { Dialog as BaseDialog } from '@base-ui/react/dialog';
import type { ComponentProps, HTMLAttributes, ReactNode } from 'react';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1 / 計画 13_frontend-stack §shadcn/ui 派生の範囲:
// モーダルは 4 基準（フォーカストラップ／複合キーボード操作／ポータルの配置計算／
// aria-* の動的同期）**すべてに該当する**ため、素の HTML では書かない。
//
// 土台は **Base UI（`@base-ui/react`）** である。Radix ではない理由は、Radix の後継として
// 同じ作者たちが開発を続けているのが Base UI であり、更新が実際に続いていること
// （1.4〜1.8 が月次。1.8.0 は 2026-09-04）。旧パッケージ名 `@base-ui-components/react` は
// rc.0 で停止しているので使わない。
//
// 🔴 **Base UI は `render` prop 方式である**（Radix の `asChild` は無い）。
//    既存部品と組むときは `<DialogClose render={<Button variant="secondary">取消</Button>} />` と書く。
//
// 🔴 **`initialFocus` を必ず通せるようにしてある。** 本リポジトリには
//    「破壊的操作の確認ダイアログは**取消側**へ初期フォーカスを当てる」という既存の規律があり、
//    既定（最初のタブ可能要素）に任せると実行側にフォーカスが載る場合がある。
//    ラッパが prop を落とすと、その規律が**テストだけ緑のまま**壊れる。
//
// **文言は持たない**（IADR-0125 決定 1）。題名・説明・ボタンの文字列は呼び出し側が渡す。

/** ダイアログの開閉と状態を束ねる根（HTML 要素を描かない）。 */
export const Dialog = BaseDialog.Root;

/** ダイアログを開くボタン。 */
export const DialogTrigger = BaseDialog.Trigger;

/**
 * ダイアログを閉じるボタン。既定では素の `<button>` を描くので、見た目を付けるときは
 * `render={<Button variant="secondary">取消</Button>}` の形で既存部品を渡す。
 */
export const DialogClose = BaseDialog.Close;

export interface DialogContentProps extends Omit<
  ComponentProps<typeof BaseDialog.Popup>,
  'className'
> {
  className?: string;
}

/**
 * 画面の上へ重なる本体（Portal ＋ Backdrop ＋ Popup）。
 * 見た目はモックの `.dialog`（`min(440px, 100%)` / radius-lg / shadow-lg / surface）。
 */
export function DialogContent({ className, children, ...props }: DialogContentProps) {
  return (
    <BaseDialog.Portal>
      <BaseDialog.Backdrop className="fixed inset-0 z-40 bg-[color-mix(in_srgb,var(--color-neutral-900)_50%,transparent)]" />
      <BaseDialog.Popup
        className={cn(
          'fixed top-1/2 left-1/2 z-50 flex w-[min(440px,calc(100%-2rem))] -translate-x-1/2 -translate-y-1/2',
          'flex-col gap-n3 rounded-lg bg-surface p-n4 shadow-lg',
          className,
        )}
        {...props}
      >
        {children}
      </BaseDialog.Popup>
    </BaseDialog.Portal>
  );
}

export interface DialogTitleProps extends Omit<
  ComponentProps<typeof BaseDialog.Title>,
  'className'
> {
  className?: string;
}

/** ダイアログの題名。Base UI が `aria-labelledby` を自動で結ぶ。 */
export function DialogTitle({ className, ...props }: DialogTitleProps) {
  return (
    <BaseDialog.Title
      className={cn('text-xl leading-tight font-medium text-fg', className)}
      {...props}
    />
  );
}

export interface DialogDescriptionProps extends Omit<
  ComponentProps<typeof BaseDialog.Description>,
  'className'
> {
  className?: string;
}

/** ダイアログの本文。Base UI が `aria-describedby` を自動で結ぶ。 */
export function DialogDescription({ className, ...props }: DialogDescriptionProps) {
  return <BaseDialog.Description className={cn('text-sm text-fg-muted', className)} {...props} />;
}

export interface DialogActionsProps extends HTMLAttributes<HTMLDivElement> {
  children: ReactNode;
}

/** 操作ボタンを右へ寄せる帯（モックの `.dialog-actions`）。 */
export function DialogActions({ className, ...props }: DialogActionsProps) {
  return <div className={cn('mt-n2 flex justify-end gap-n2', className)} {...props} />;
}
