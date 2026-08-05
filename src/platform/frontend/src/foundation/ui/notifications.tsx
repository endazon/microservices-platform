import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';
import { AlertTriangle, CircleCheck, CircleX, Info } from 'lucide-react';
import type { ComponentType } from 'react';
import { Toaster, toast } from 'sonner';

// ADR-0031（Toast = sonner）/ IADR-0124 決定 7 / INDEX 決定 21:
// 共通シェルの通知。「色だけで意味を持たせない」を **API の形**で強制する。
// 呼び出し側は severity を選ぶだけで、アイコンとテキストのラベルは常に付く（省略できる API に
// すると必ず省略されるため、選択肢を作らない）。@platform/ui の StatusBadge と同じ作法である。

type Severity = 'success' | 'info' | 'warning' | 'error';

interface SeverityStyle {
  /** 支援技術からは隠す装飾アイコン。意味はラベルとメッセージが担う。 */
  icon: ComponentType<{ className?: string; 'aria-hidden'?: boolean }>;
  /**
   * 色を除いても意味が成り立つためのテキストラベル。
   * ADR-0031 / IADR-0125 決定 6: 翻訳は描画時に解決する（モジュール初期化時に文字列へ
   * 確定させるとロケール切替に追随しない）。
   */
  label: MessageDescriptor;
  className: string;
}

const SEVERITIES: Record<Severity, SeverityStyle> = {
  success: { icon: CircleCheck, label: msg`成功`, className: 'text-[--color-success]' },
  info: { icon: Info, label: msg`情報`, className: 'text-[--color-fg-muted]' },
  warning: { icon: AlertTriangle, label: msg`注意`, className: 'text-[--color-warning]' },
  error: { icon: CircleX, label: msg`エラー`, className: 'text-[--color-danger]' },
};

function body(severity: Severity, message: string) {
  const { icon: Icon, label, className } = SEVERITIES[severity];
  return (
    <span className="flex items-center gap-2 text-sm">
      <Icon className={`size-4 ${className}`} aria-hidden />
      <span className={`font-medium ${className}`}>{i18n._(label)}</span>
      <span className="text-[--color-fg]">{message}</span>
    </span>
  );
}

function emit(severity: Severity, message: string): void {
  toast.custom(() => body(severity, message));
}

/** 共通シェルの通知。4 種のみを公開し、いずれもアイコン ＋ テキストラベルを伴う。 */
export const notify = {
  success: (message: string) => emit('success', message),
  info: (message: string) => emit('info', message),
  warning: (message: string) => emit('warning', message),
  error: (message: string) => emit('error', message),
};

/** 通知の描画先。共通シェル（Layout）に 1 つだけ置く。 */
export function Notifications() {
  return <Toaster position="bottom-right" toastOptions={{ unstyled: false }} />;
}

/** テスト用: severity ごとの表示（アイコン ＋ ラベル）を検証するために公開する。 */
export function notificationLabel(severity: Severity): string {
  return i18n._(SEVERITIES[severity].label);
}
