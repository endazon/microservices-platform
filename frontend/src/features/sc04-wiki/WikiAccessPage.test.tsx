import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';

// SC-04, UC-07: Wiki 導線が設定時のみリンクを出し、未設定なら注意書きを出すことを検証する。
const mocks = vi.hoisted(() => ({ wikiBaseUrl: undefined as string | undefined }));
vi.mock('@foundation/config/runtimeConfig', () => ({
  appConfig: () => ({ wikiBaseUrl: mocks.wikiBaseUrl }),
}));

import { WikiAccessPage } from './WikiAccessPage';

beforeEach(() => {
  mocks.wikiBaseUrl = undefined;
});

describe('WikiAccessPage (SC-04)', () => {
  it('shows a link to Wiki.js when configured', () => {
    mocks.wikiBaseUrl = 'https://wiki.example';
    render(<WikiAccessPage />);
    expect(screen.getByRole('link', { name: 'Wiki を開く' })).toHaveAttribute(
      'href',
      'https://wiki.example',
    );
  });

  it('shows a notice (no link) when the Wiki URL is not configured', () => {
    render(<WikiAccessPage />);
    expect(screen.queryByRole('link', { name: 'Wiki を開く' })).not.toBeInTheDocument();
    expect(screen.getByRole('note')).toHaveTextContent('未設定');
  });
});
