import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Button } from './Button';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from './Tooltip';

// ADR-0031 / IADR-0125 決定 1: アイコンボタンの補助。
// 🔴 **ツールチップを意味の唯一の担い手にしない** ——対象側の `aria-label` が消えないことを固定する。

function Fixture() {
  return (
    <TooltipProvider delay={0}>
      <Tooltip>
        <TooltipTrigger render={<Button aria-label="更新" variant="ghost" />} />
        <TooltipContent>一覧を取得し直す</TooltipContent>
      </Tooltip>
    </TooltipProvider>
  );
}

describe('Tooltip（Base UI ラッパ）', () => {
  it('対象のアクセシブルな名前は対象自身が持つ（ツールチップに依存しない）', () => {
    render(<Fixture />);
    expect(screen.getByRole('button', { name: '更新' })).toBeInTheDocument();
  });

  it('フォーカスで開く（ホバーできない利用者にも届く）', async () => {
    render(<Fixture />);
    await userEvent.tab();
    expect(await screen.findByText('一覧を取得し直す')).toBeInTheDocument();
  });
});
