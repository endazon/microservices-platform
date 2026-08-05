import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { StatusBadge } from './StatusBadge';
import { Tag } from './Tag';

// SC-01/SC-02/SC-03（#502）: 分類の名前を表すチップ。
// INDEX 決定 21（色だけで意味を持たせない）に対する Tag の答えは **StatusBadge とは異なる**。
// 状態バッジは「色 ＋ アイコン ＋ テキスト」の 3 点セットで意味を担保するが、タグの意味は
// 文字そのものが担う（色を除いても「経理」は読める）。したがってアイコンは付けない。
describe('Tag（分類の名前）', () => {
  it.each(['accent', 'neutral', 'outline'] as const)('renders the label for tone=%s', (tone) => {
    render(<Tag tone={tone}>経理</Tag>);
    expect(screen.getByText('経理')).toBeInTheDocument();
  });

  it('defaults to the neutral tone', () => {
    const { container } = render(<Tag>規程</Tag>);
    expect(container.firstElementChild?.className).toContain('bg-[--color-surface-muted]');
  });

  // 呼び出し側の className が勝つ（cn() 経由）。
  it('lets the caller extend the class list', () => {
    const { container } = render(<Tag className="ml-2">経理</Tag>);
    expect(container.firstElementChild?.className).toContain('ml-2');
  });

  // **状態バッジと取り違えない**ことの回帰。タグにアイコンが付くと「状態」に見える
  // （Info アイコンは「注記」を意味するため、分類の名前の横に置くと意味が変わる）。
  it('renders no icon, unlike StatusBadge', () => {
    const { container: tag } = render(<Tag>経理</Tag>);
    const { container: badge } = render(<StatusBadge>同期済み</StatusBadge>);

    expect(tag.querySelector('svg')).toBeNull();
    expect(badge.querySelector('svg')).not.toBeNull();
  });
});
