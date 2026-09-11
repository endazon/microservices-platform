import { useRef } from 'react';
import type { ReactNode } from 'react';
import {
  Button,
  Dialog,
  DialogActions,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogTitle,
} from '@platform/ui';

// SC-19, SC-20, FR-19/FR-20: 取り返しのつかない操作の手前に置く確認ダイアログ。
//
// ■ ここに置く理由（作業仕様書 §判断 3）
//   計画が確認ダイアログを要求するのは SC-19 / SC-20 の 2 画面である。2 画面が同じ部品を要るので、
//   片方の feature へ置いて他方から引くこともできない（feature の公開面は index のみ。
//   IADR-0262 決定 4）。よってユニット内の共有部品の置き場（DataTable / EChart が居る
//   `components/`）に置く。
//
// ■ 土台は `@platform/ui` の `Dialog`（Base UI。#452 で移植済み）である
//   ——本部品は**それを確認ダイアログの形に束ねるだけ**になった。
//   フォーカストラップ・Esc・背景クリック・フォーカスの復帰は Base UI が担う。
//   自前の `document.addEventListener('keydown')` と素の `<div className="fixed inset-0">` は撤去した
//   （前者はダイアログが閉じた後も残りうる購読であり、後者はトラップも復帰も持たない）。
//
// ■ 🔴 初期フォーカスは**取消**へ置く（`DialogContent` の `initialFocus`）
//   —— 開いた瞬間に Enter を押しても破壊的操作が走らないようにする既存の規律である。
//   Base UI の既定（最初のタブ可能要素）に任せると実行側へ載る場合があるため、明示して固定する。
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
//   本部品は呼び出し側が**開いているときだけマウントする**（閉じているときは描かれない）。
//   そのため `open` は常に真で、閉じる要求（取消・Esc・背景クリック）は `onOpenChange` を
//   通って `onCancel` へ出る —— 閉じる判断は呼び出し側だけが持つ。

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
 * Escape・背景クリック・取消のいずれでも `onCancel` が呼ばれる（取り返しのつかない操作から、
 * キーボードだけで確実に降りられるようにする）。初期フォーカスは**取消**へ置く。
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
  const cancelRef = useRef<HTMLButtonElement>(null);

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open) onCancel();
      }}
    >
      <DialogContent initialFocus={cancelRef}>
        <DialogTitle>{title}</DialogTitle>
        {/*
          本文は段落を複数含みうるので `<div>` へ描き替える（`<p>` の入れ子は不正な DOM になる）。
          `aria-describedby` の結び付けは Base UI が担うので、器を変えても読み上げは保たれる。
        */}
        <DialogDescription render={<div className="flex flex-col gap-2 text-sm text-fg-muted" />}>
          {children}
        </DialogDescription>
        <DialogActions>
          {/* 🔴 初期フォーカスはこちら（上の注記）。閉じる要求は onOpenChange → onCancel へ出る。 */}
          <DialogClose render={<Button ref={cancelRef} variant="secondary" disabled={pending} />}>
            {cancelLabel}
          </DialogClose>
          <Button
            variant={destructive ? 'danger' : 'primary'}
            disabled={pending}
            onClick={onConfirm}
          >
            {confirmLabel}
          </Button>
        </DialogActions>
      </DialogContent>
    </Dialog>
  );
}
