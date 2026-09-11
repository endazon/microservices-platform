import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ConfirmDialog } from './ConfirmDialog';

// SC-19, SC-20: 取り返しのつかない操作の手前に置く確認ダイアログ。
//
// 🔴 ここで固定するのは「**押すまで何も起きない**」ことである。
// 実行が確認より先に走る実装は、画面側のテストからは見えにくい（成功してしまうので緑になる）。
//
// 🔴 **初期フォーカスが取消側に在ること**も固定する（#452 で土台を Base UI の `Dialog` へ
// 載せ替えたため、`initialFocus` を落とすと「画面は動いて見えるのに規律だけ壊れる」）。
// `aria-modal` は**検査しない** —— Base UI は外側を inert にする方式であり、この属性を付けない
// （属性の有無ではなく、`role="dialog"` と読み上げ名・説明の結び付きで意味を測る）。

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
  it('見出しと本文を持つダイアログとして読み上げられる', async () => {
    setup();
    const dialog = await screen.findByRole('dialog');
    expect(dialog).toHaveAccessibleName('完全に削除しますか？');
    expect(dialog).toHaveAccessibleDescription('元に戻せません。');
    expect(screen.getByText('元に戻せません。')).toBeInTheDocument();
  });

  it('初期フォーカスは取消側にある（開いた瞬間の Enter で破壊的操作を走らせない）', async () => {
    setup();
    await screen.findByRole('dialog');
    expect(await screen.findByRole('button', { name: 'やめる' })).toHaveFocus();
  });

  it('実行を押したときだけ onConfirm を呼ぶ', async () => {
    const user = userEvent.setup();
    const { onConfirm, onCancel } = setup();

    await screen.findByRole('dialog');
    expect(onConfirm).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: '完全に削除する' }));
    expect(onConfirm).toHaveBeenCalledTimes(1);
    expect(onCancel).not.toHaveBeenCalled();
  });

  it('取消と Escape のどちらでも onCancel を呼ぶ', async () => {
    const user = userEvent.setup();
    const { onConfirm, onCancel } = setup();

    await screen.findByRole('dialog');
    await user.click(screen.getByRole('button', { name: 'やめる' }));
    await user.keyboard('{Escape}');
    expect(onCancel).toHaveBeenCalledTimes(2);
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('実行中は両方のボタンを無効化する（二重送信を防ぐ）', async () => {
    setup({ pending: true });
    await screen.findByRole('dialog');
    expect(screen.getByRole('button', { name: '完全に削除する' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'やめる' })).toBeDisabled();
  });

  it('破壊的操作でもラベルの文言が意味を担う（色だけに頼らない）', async () => {
    setup({ destructive: true });
    await screen.findByRole('dialog');
    // 色（クラス）ではなく、押す前に読める語で判断できる。
    expect(screen.getByRole('button', { name: '完全に削除する' })).toBeInTheDocument();
  });
});
