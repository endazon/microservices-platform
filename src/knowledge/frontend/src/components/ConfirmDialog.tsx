import { useEffect, useId } from 'react';
import type { ReactNode } from 'react';
import { Button, Card, CardContent, CardHeader, CardTitle } from '@platform/ui';

// SC-19, SC-20, FR-19/FR-20: 取り返しのつかない操作の手前に置く確認ダイアログ。
//
// ■ ここに置く理由（作業仕様書 §判断 3）
//   計画が確認ダイアログを要求するのは SC-19 / SC-20 の 2 画面である。`@platform/ui` には
//   `Dialog` がまだ無く、その移植は別の作業単位（#452）の射程であって、本作業で先に入れると
//   **他人の射程を黙って動かす**ことになる。一方 2 画面が同じ部品を要るので、片方の feature へ
//   置いて他方から引くこともできない（feature の公開面は index のみ。IADR-0262 決定 4）。
//   よってユニット内の共有部品の置き場（DataTable / EChart が居る `components/`）に置く。
//   **昇格の可否は #452 が決める。**
//
// ■ 文言を持たない（IADR-0125 決定 1 と同じ規律）
//   見出し・本文・ボタンのラベルはすべて呼び出し側が**翻訳済みの値**として渡す。
//   部品が文言を内蔵すると i18n の入口が 2 つに割れ、カタログの網羅検査を抜ける。
//
// ■ 「色だけで意味を持たせない」（INDEX 決定 21）
//   破壊的な操作は `danger` のボタン**と**ラベルの文言で示す。色を落としても
//   「完全に削除する」「すべて失効する」という語が残る。
//
// ■ 開いている間だけ描く
//   閉じているときは何も描かない（`null` を返す）。DOM に隠して置くと、
//   支援技術と検査の双方から「存在するが見えないボタン」に見える。

export interface ConfirmDialogProps {
  /** ダイアログの見出し（翻訳済み）。 */
  title: string;
  /** 本文。複数の段落・強調を含みうるので `ReactNode` で受ける。 */
  children: ReactNode;
  /** 実行ボタンのラベル（翻訳済み）。 */
  confirmLabel: string;
  /** 取消ボタンのラベル（翻訳済み）。 */
  cancelLabel: string;
  /** 破壊的操作か。true のとき実行ボタンを `danger` にする。 */
  destructive?: boolean;
  /** 実行中は二重送信を防ぐため両ボタンを無効化する。 */
  pending?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

/**
 * 確認ダイアログ。**開いているときだけ**描画される。
 *
 * Escape で取消できる（取り返しのつかない操作から、キーボードだけで確実に降りられるようにする）。
 * 初期フォーカスは**取消**へ置く —— 開いた瞬間に Enter を押しても破壊的操作が走らないようにする。
 */
export function ConfirmDialog({
  title,
  children,
  confirmLabel,
  cancelLabel,
  destructive = false,
  pending = false,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  const titleId = useId();

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onCancel();
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [onCancel]);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <Card
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        className="w-full max-w-lg bg-[--color-surface]"
      >
        <CardHeader>
          <CardTitle id={titleId}>{title}</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <div className="flex flex-col gap-2 text-sm">{children}</div>
          <div className="flex justify-end gap-2">
            {/* 初期フォーカスは取消側。`@platform/ui` の Button は ref を受けないので
                宣言的な autoFocus で置く（ref の受け口を足すのは #452 の射程である）。 */}
            <Button autoFocus variant="secondary" disabled={pending} onClick={onCancel}>
              {cancelLabel}
            </Button>
            <Button
              variant={destructive ? 'danger' : 'primary'}
              disabled={pending}
              onClick={onConfirm}
            >
              {confirmLabel}
            </Button>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
