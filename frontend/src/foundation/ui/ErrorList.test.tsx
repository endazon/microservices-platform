import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { ErrorList } from './ErrorList';

// Issue #183 / IADR-0040: 検証・競合エラー一覧表示コンポーネントの描画を検証する。

describe('ErrorList', () => {
  it('renders nothing when errors is empty', () => {
    const { container } = render(<ErrorList errors={[]} />);
    expect(container).toBeEmptyDOMElement();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('renders each error message as an alert list item', () => {
    render(<ErrorList errors={['A です。', 'B です。']} />);
    const alert = screen.getByRole('alert');
    expect(alert.tagName).toBe('UL');
    expect(screen.getByText('A です。')).toBeInTheDocument();
    expect(screen.getByText('B です。')).toBeInTheDocument();
  });
});
