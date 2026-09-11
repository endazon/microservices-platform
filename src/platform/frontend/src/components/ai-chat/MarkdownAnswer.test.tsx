import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MarkdownAnswer } from './MarkdownAnswer';

// SC-01 / 05_screens §共通シェル（右レール AI チャット）/ 裁定 6（Markdown 描画）: AI 回答の本文部品。
describe('MarkdownAnswer', () => {
  // 処理系（marked）は遅延読み込み。届くまでは素の本文を出し、届いたら HTML に置き換わる。
  it('shows the raw text immediately and the rendered markup once the renderer has loaded', async () => {
    render(
      <MarkdownAnswer markdown="締め日は **25日** です[1]" citePrefix="p" citationCount={1} />,
    );
    // 素の本文（読み込み前）か描画後のどちらでも本文が読める。
    expect(screen.getByText(/締め日は/)).toBeInTheDocument();
    expect(await screen.findByText('25日', { selector: 'strong' })).toBeInTheDocument();
    // 裁定 6: 本文の [1] が出典の行（同じ接頭辞）へ結ばれる。
    expect(screen.getByRole('link', { name: '[1]' })).toHaveAttribute('href', '#p-cite-1');
  });

  // ストリーミング: 本文が伸びるたびに描き直す（古い描画結果で新しいものを上書きしない）。
  it('re-renders as the markdown grows', async () => {
    const { rerender } = render(<MarkdownAnswer markdown="- 一" citePrefix="p" />);
    expect(await screen.findByRole('listitem')).toHaveTextContent('一');
    rerender(<MarkdownAnswer markdown={'- 一\n- 二'} citePrefix="p" />);
    await screen.findByText('二');
    expect(screen.getAllByRole('listitem')).toHaveLength(2);
  });

  // 外部リンクは新しいタブ（opener なし）。
  it('renders external links with rel="noopener noreferrer"', async () => {
    render(<MarkdownAnswer markdown="[規程](https://example.com/rule)" citePrefix="p" />);
    const link = await screen.findByRole('link', { name: '規程' });
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
  });
});
