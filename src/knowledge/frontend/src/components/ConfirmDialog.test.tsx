import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ConfirmDialog } from './ConfirmDialog';

// SC-19, SC-20: 取り返しのつかない操作の手前に置く確認ダイアログ。
//
// 🔴 ここで固定するのは「**押すまで何も起きない**」ことである。
// 実行が確認より先に走る実装は、画面側のテストからは見えにくい（成功してしまうので緑になる）。

function setup(over: Partial<Parameters<typeof ConfirmDialog>[0]> = {}) {
  const onConfirm = vi.fn();
  const onCancel = vi.fn();
  render(
    <ConfirmDialog
      title="完全に削除しますか？"
      confirmLabel="完全に削除する"
      cancelLabel="やめる"
      onConfirm={onConfirm}
      onCancel={onCancel}
      {...over}
    >
      <p>元に戻せません。</p>
    </ConfirmDialog>,
  );
  return { onConfirm, onCancel };
}

describe('ConfirmDialog', () => {
  it('見出しと本文を持つダイアログとして読み上げられる', () => {
    setup();
    const dialog = screen.getByRole('dialog');
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(dialog).toHaveAccessibleName('完全に削除しますか？');
    expect(screen.getByText('元に戻せません。')).toBeInTheDocument();
  });

  it('初期フォーカスは取消側にある（開いた瞬間の Enter で破壊的操作を走らせない）', () => {
    setup();
    expect(screen.getByRole('button', { name: 'やめる' })).toHaveFocus();
  });

  it('実行を押したときだけ onConfirm を呼ぶ', async () => {
    const user = userEvent.setup();
    const { onConfirm, onCancel } = setup();

    expect(onConfirm).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: '完全に削除する' }));
    expect(onConfirm).toHaveBeenCalledTimes(1);
    expect(onCancel).not.toHaveBeenCalled();
  });

  it('取消と Escape のどちらでも onCancel を呼ぶ', async () => {
    const user = userEvent.setup();
    const { onConfirm, onCancel } = setup();

    await user.click(screen.getByRole('button', { name: 'やめる' }));
    await user.keyboard('{Escape}');
    expect(onCancel).toHaveBeenCalledTimes(2);
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('実行中は両方のボタンを無効化する（二重送信を防ぐ）', () => {
    setup({ pending: true });
    expect(screen.getByRole('button', { name: '完全に削除する' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'やめる' })).toBeDisabled();
  });

  it('破壊的操作でもラベルの文言が意味を担う（色だけに頼らない）', () => {
    setup({ destructive: true });
    // 色（クラス）ではなく、押す前に読める語で判断できる。
    expect(screen.getByRole('button', { name: '完全に削除する' })).toBeInTheDocument();
  });
});
