import { AlertTriangle, CircleCheck, CircleX, Info } from 'lucide-react';
import type { ComponentType, HTMLAttributes, ReactNode } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '../lib/cn';

// ADR-0031 / IADR-0125 決定 1: 移植の根拠は hi-fi モックアップの `note`（45 箇所）・`err` / `warn` / `ok`、
// および SC-05「必須属性未設定は保存拒否」・SC-06「認証情報は Vault 管理」の注記と
// SC-06 の同期異常の**警告（琥珀）**（05_screens モック間相違の確定 ②）。
//
// INDEX 決定 21「色だけで意味を持たせない」を **API の形**で強制する（`StatusBadge`・`notify` と同じ作法）。
// tone ごとにアイコンが固定で付き、**テキストのラベルは必須**である。省略できる API にすると
// 必ず省略されるため、選択肢を作らない。
const alertVariants = cva('flex gap-2 rounded-[--radius-control] border p-3 text-sm', {
  variants: {
    tone: {
      info: 'border-[--color-border] bg-[--color-surface-muted] text-[--color-fg]',
      success: 'border-[--color-success] text-[--color-fg]',
      // 05_screens: 警告色は琥珀（hi-fi 正）。--color-warning が該当色である。
      warning: 'border-[--color-warning] text-[--color-fg]',
      danger: 'border-[--color-danger] text-[--color-fg]',
    },
  },
  defaultVariants: { tone: 'info' },
});

type Tone = NonNullable<NonNullable<VariantProps<typeof alertVariants>>['tone']>;

/** tone とアイコンの対応。tone を増やすときはここも必ず埋まる（Record が網羅を強制する）。 */
const TONE_ICONS: Record<Tone, ComponentType<{ className?: string; 'aria-hidden'?: boolean }>> = {
  info: Info,
  success: CircleCheck,
  warning: AlertTriangle,
  danger: CircleX,
};

const TONE_ICON_COLORS: Record<Tone, string> = {
  info: 'text-[--color-fg-muted]',
  success: 'text-[--color-success]',
  warning: 'text-[--color-warning]',
  danger: 'text-[--color-danger]',
};

export interface AlertProps
  extends Omit<HTMLAttributes<HTMLDivElement>, 'title'>,
    VariantProps<typeof alertVariants> {
  /**
   * 種別を表すテキスト（例: 「注意」「エラー」）。**必須**である。
   *
   * 色とアイコンだけでは、色覚特性のある利用者・モノクロ印刷・スクリーンショットで意味が失われる
   * （INDEX 決定 21）。文言そのものはプリミティブが持たない（IADR-0125 決定 1）——
   * 呼び出し側が翻訳済みの文字列を渡す。
   */
  label: ReactNode;
  children: ReactNode;
}

/**
 * 注記・警告・エラーの表示。
 *
 * **`role` は既定で付けない。** 画面に静的に置かれる注記（SC-05 / SC-06 の注記）と、
 * 操作の結果として現れる通知とでは適切なライブリージョンが異なる。どちらか一方を既定にすると
 * 他方が誤って読み上げられる（あるいは読み上げられない）ため、呼び出し側が
 * `role="alert"` / `role="status"` を選ぶ。本コンポーネントが型で保証するのは
 * 「色 ＋ アイコン ＋ テキスト」の 3 点セットである。
 */
export function Alert({ className, tone, label, children, ...props }: AlertProps) {
  const key: Tone = tone ?? 'info';
  const Icon = TONE_ICONS[key];
  return (
    <div className={cn(alertVariants({ tone }), className)} {...props}>
      {/* アイコンは装飾。意味はラベルと本文が担うため支援技術からは隠す。 */}
      <Icon className={cn('mt-0.5 size-4 shrink-0', TONE_ICON_COLORS[key])} aria-hidden />
      <div>
        <span className={cn('font-medium', TONE_ICON_COLORS[key])}>{label}</span>
        <span className="ml-2">{children}</span>
      </div>
    </div>
  );
}
