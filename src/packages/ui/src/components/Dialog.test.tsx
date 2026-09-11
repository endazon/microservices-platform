import { useRef, useState } from 'react';
import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Button } from './Button';
import {
  Dialog,
  DialogActions,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogTitle,
  DialogTrigger,
} from './Dialog';

// ADR-0031 / IADR-0125 決定 1: Base UI の Dialog を包んだモーダル。
//
// 🔴 **`initialFocus` が通ることを固定する。** 本リポジトリには「破壊的操作の確認ダイアログは
//    **取消側**へ初期フォーカスを当てる」という既存の規律があり、ラッパが prop を落とすと
//    その規律が静かに壊れる（画面は動いて見えるので気付けない）。

function ConfirmFixture() {
  const cancelRef = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger render={<Button variant="danger">削除</Button>} />
      <DialogContent initialFocus={cancelRef}>
        <DialogTitle>文書を削除しますか</DialogTitle>
        <DialogDescription>削除した文書は復元できません。</DialogDescription>
        <DialogActions>
          <DialogClose
            render={
              <Button ref={cancelRef} variant="secondary">
                取消
              </Button>
            }
          />
          <Button variant="danger">削除する</Button>
        </DialogActions>
      </DialogContent>
    </Dialog>
  );
}

describe('Dialog（Base UI ラッパ）', () => {
  it('トリガで開き、題名と本文が dialog へ結び付く', async () => {
    render(<ConfirmFixture />);
    await userEvent.click(screen.getByRole('button', { name: '削除' }));

    const dialog = await screen.findByRole('dialog');
    expect(dialog).toHaveAccessibleName('文書を削除しますか');
    expect(dialog).toHaveAccessibleDescription('削除した文書は復元できません。');
  });

  it('🔴 initialFocus が通る（初期フォーカスは取消側）', async () => {
    render(<ConfirmFixture />);
    await userEvent.click(screen.getByRole('button', { name: '削除' }));
    await screen.findByRole('dialog');

    expect(await screen.findByRole('button', { name: '取消' })).toHaveFocus();
  });

  it('Escape で閉じる', async () => {
    render(<ConfirmFixture />);
    await userEvent.click(screen.getByRole('button', { name: '削除' }));
    await screen.findByRole('dialog');

    await userEvent.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('DialogClose で閉じる（render prop で既存のボタンを渡せる）', async () => {
    render(<ConfirmFixture />);
    await userEvent.click(screen.getByRole('button', { name: '削除' }));
    await screen.findByRole('dialog');

    await userEvent.click(screen.getByRole('button', { name: '取消' }));
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
