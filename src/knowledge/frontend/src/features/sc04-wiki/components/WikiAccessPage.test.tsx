import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { activate } from '@foundation/i18n';
import { I18nProvider } from '@lingui/react';
import { i18n } from '@lingui/core';

// SC-04, UC-07: Wiki 導線が設定時のみリンクを出し、未設定なら注意書きを出すことを検証する。
const mocks = vi.hoisted(() => ({ wikiBaseUrl: undefined as string | undefined }));
vi.mock('@foundation/config/runtimeConfig', () => ({
  appConfig: () => ({ wikiBaseUrl: mocks.wikiBaseUrl }),
}));

import { WikiAccessPage } from './WikiAccessPage';

// #449: 表示文言を Lingui へ載せたので、描画には I18nProvider が要る。
function renderPage() {
  return render(
    <I18nProvider i18n={i18n}>
      <WikiAccessPage />
    </I18nProvider>,
  );
}

beforeEach(() => {
  mocks.wikiBaseUrl = undefined;
});

afterEach(() => {
  // ロケールを既定（ja）へ戻す（テスト間のリーク防止）。
  act(() => {
    activate('ja');
  });
});

describe('WikiAccessPage (SC-04)', () => {
  it('shows a link to Wiki.js when configured', () => {
    mocks.wikiBaseUrl = 'https://wiki.example';
    renderPage();
    expect(screen.getByRole('link', { name: 'Wiki を開く' })).toHaveAttribute(
      'href',
      'https://wiki.example',
    );
  });

  it('shows a notice (no link) when the Wiki URL is not configured', () => {
    renderPage();
    expect(screen.queryByRole('link', { name: 'Wiki を開く' })).not.toBeInTheDocument();
    expect(screen.getByRole('note')).toHaveTextContent('未設定');
  });

  // #449: 表示文言が Lingui を通っていることを en ロケールで固定する。
  // **生の日本語文字列へ戻す退行はこのテストが落とす**（en でも日本語のままになる）。
  it('renders in English when the en locale is active', () => {
    mocks.wikiBaseUrl = 'https://wiki.example';
    act(() => {
      activate('en');
    });
    renderPage();

    expect(screen.getByRole('heading', { name: 'Browse the wiki' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Open the wiki' })).toBeInTheDocument();
  });

  it('renders the unconfigured notice in English too', () => {
    act(() => {
      activate('en');
    });
    renderPage();

    expect(screen.getByRole('note')).toHaveTextContent('not configured');
  });
});
