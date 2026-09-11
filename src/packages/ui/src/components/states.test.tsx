import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { EmptyState } from './EmptyState';
import { ErrorState } from './ErrorState';
import { LoadingState } from './LoadingState';
import { Skeleton } from './Skeleton';
import { Spinner } from './Spinner';

// ADR-0031 / IADR-0125 決定 1 / 第 4 弾検討「体験の穴」:
// **待ち・空・エラーの三部品**。要点は「0 件（正常）」と「失敗（異常）」が**別の役割**を持つことで、
// ここが同じになると利用者は再試行すべきか条件を変えるべきか判断できない。

describe('Spinner（回る輪は装飾。意味は文言が担う）', () => {
  it('role="status" と読み上げ用ラベルを持つ', () => {
    render(<Spinner label="読み込み中" />);
    expect(screen.getByRole('status')).toHaveTextContent('読み込み中');
  });

  it('アイコンは支援技術から隠す', () => {
    const { container } = render(<Spinner label="読み込み中" />);
    expect(container.querySelector('svg')).toHaveAttribute('aria-hidden', 'true');
  });
});

describe('Skeleton（場所取り）', () => {
  it('支援技術からは隠す（骨組みは意味を持たない）', () => {
    const { container } = render(<Skeleton />);
    expect(container.firstElementChild).toHaveAttribute('aria-hidden', 'true');
  });

  it('lines の数だけ行を描く', () => {
    const { container } = render(<Skeleton lines={3} />);
    expect(container.firstElementChild?.childElementCount).toBe(3);
  });

  it('0 以下を渡しても 1 行は描く（空の箱を残さない）', () => {
    const { container } = render(<Skeleton lines={0} />);
    expect(container.firstElementChild?.childElementCount).toBe(1);
  });
});

describe('LoadingState（区画ごとの待ち）', () => {
  it('読み上げの名前は 1 度だけ与える（見えている文言は aria-hidden）', () => {
    render(<LoadingState label="検索中" />);
    const status = screen.getByRole('status');
    expect(status).toHaveTextContent('検索中');
    // 「検索中」は視覚用と読み上げ用の 2 箇所に在るが、読み上げに乗るのは status の中の 1 つだけ。
    expect(screen.getAllByText('検索中')).toHaveLength(2);
    expect(screen.getAllByText('検索中')[1]).toHaveAttribute('aria-hidden', 'true');
  });
});

describe('EmptyState（0 件は正常な結果）', () => {
  it('title と description と action を描く', () => {
    render(
      <EmptyState
        title="該当する文書がありません"
        description="条件を変えて再度お試しください"
        action={<button type="button">条件を変える</button>}
      />,
    );
    expect(screen.getByText('該当する文書がありません')).toBeInTheDocument();
    expect(screen.getByText('条件を変えて再度お試しください')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '条件を変える' })).toBeInTheDocument();
  });

  it('🔴 role="alert" を持たない（失敗と同じ扱いにしない）', () => {
    render(<EmptyState title="該当する文書がありません" />);
    expect(screen.queryByRole('alert')).toBeNull();
  });
});

describe('ErrorState（失敗は割り込んで知らせる）', () => {
  it('role="alert" を持つ', () => {
    render(<ErrorState title="読み込みに失敗しました" />);
    expect(screen.getByRole('alert')).toHaveTextContent('読み込みに失敗しました');
  });

  it('色だけで意味を持たせない: 既定のアイコン（装飾）とテキストが両方出る', () => {
    const { container } = render(<ErrorState title="読み込みに失敗しました" />);
    const icon = container.querySelector('svg');
    expect(icon).not.toBeNull();
    expect(icon).toHaveAttribute('aria-hidden', 'true');
  });

  it('再試行の導線は部品が決めず、action スロットで受ける', () => {
    render(
      <ErrorState title="読み込みに失敗しました" action={<button type="button">再試行</button>} />,
    );
    expect(screen.getByRole('button', { name: '再試行' })).toBeInTheDocument();
  });

  it('action を渡さなければ再試行ボタンは出ない（押しても直らない失敗に導線を出さない）', () => {
    render(<ErrorState title="権限がありません" />);
    expect(screen.queryByRole('button')).toBeNull();
  });
});
