import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { StatusBadge } from './StatusBadge';

// INDEX 決定 21 /（IADR-0121 決定 4）: 「色だけで意味を持たせない」の回帰。
// 状態バッジは tone に関わらず必ずテキストとアイコンを描画する。ここが崩れると、色覚特性・
// モノクロ印刷の利用者に対して状態が伝わらなくなる。
describe('StatusBadge（色だけで意味を持たせない）', () => {
  it.each(['neutral', 'success', 'warning', 'danger'] as const)(
    'renders both a text label and an icon for tone=%s',
    (tone) => {
      const { container } = render(<StatusBadge tone={tone}>同期済み</StatusBadge>);

      expect(screen.getByText('同期済み')).toBeInTheDocument();
      // lucide-react は svg を描画する。アイコンは装飾なので支援技術からは隠されている。
      const icon = container.querySelector('svg');
      expect(icon).not.toBeNull();
      expect(icon).toHaveAttribute('aria-hidden', 'true');
    },
  );

  it('uses a different icon per tone so monochrome rendering still distinguishes states', () => {
    const { container: ok } = render(<StatusBadge tone="success">成功</StatusBadge>);
    const { container: ng } = render(<StatusBadge tone="danger">失敗</StatusBadge>);

    expect(ok.querySelector('svg')?.outerHTML).not.toBe(ng.querySelector('svg')?.outerHTML);
  });

  it('defaults to the neutral tone', () => {
    render(<StatusBadge>未実行</StatusBadge>);
    expect(screen.getByText('未実行')).toBeInTheDocument();
  });
});
