import type { LabelHTMLAttributes, ReactNode } from 'react';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: 移植の根拠は各画面の「入力 / バリデーション」表の項目名と
// hi-fi モックアップの `olabel`。既存 11 画面でも `<label>` が 28 箇所ある。
export interface LabelProps extends LabelHTMLAttributes<HTMLLabelElement> {
  /**
   * 必須項目であることを支援技術へ伝える文言（例: 「（必須）」）。
   *
   * **これを渡したときだけ必須の印（*）が出る。** boolean のフラグにしないのは、
   * 記号だけの必須表現を作らせないためである——記号は色・字形に依存する手掛かりであり、
   * 読み上げには乗らない（INDEX 決定 21 の敷衍。`StatusBadge` と同じ作法で、
   * 「印はあるが読み上げが無い」という選択肢を型から消す）。
   *
   * 文言そのものはプリミティブが持たない（IADR-0125 決定 1）。呼び出し側が
   * 翻訳済みの文字列を渡す——ここに既定文言を置くと i18n の入口が 2 つに割れる。
   */
  requiredHint?: ReactNode;
  children: ReactNode;
}

export function Label({ className, requiredHint, children, ...props }: LabelProps) {
  return (
    <label className={cn('block text-sm font-medium text-[--color-fg]', className)} {...props}>
      {children}
      {requiredHint === undefined ? null : (
        <>
          <span aria-hidden className="ml-0.5 text-[--color-danger]">
            *
          </span>
          <span className="sr-only">{requiredHint}</span>
        </>
      )}
    </label>
  );
}
