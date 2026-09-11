import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ThemeToggle } from './ThemeToggle';
import { STORAGE_KEY } from '../../lib/theme/theme';

// ADR-0031 / 利用者裁定（2026-09-12）: ヘッダのテーマ切替。
// 固定するのは 3 点 ——(1) 現在のテーマが**文字でも**読めること（アイコンだけにしない）、
// (2) 押すと `<html data-theme>` と localStorage が動くこと、
// (3) `aria-label` が**押した後どうなるか**まで含むこと。

/** jsdom は matchMedia を実装しない。OS 設定を差し替えられる最小のスタブを置く。 */
function stubPrefersDark(prefersDark: boolean) {
  const listeners = new Set<(event: MediaQueryListEvent) => void>();
  window.matchMedia = ((query: string) =>
    ({
      matches: query.includes('dark') ? prefersDark : false,
      media: query,
      onchange: null,
      addEventListener: (_: string, listener: (event: MediaQueryListEvent) => void) =>
        listeners.add(listener),
      removeEventListener: (_: string, listener: (event: MediaQueryListEvent) => void) =>
        listeners.delete(listener),
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
    }) as unknown as MediaQueryList) as typeof window.matchMedia;
}

beforeEach(() => {
  window.localStorage.clear();
  delete document.documentElement.dataset.theme;
});

afterEach(() => {
  window.localStorage.clear();
  delete document.documentElement.dataset.theme;
});

describe('ThemeToggle', () => {
  it('現在のテーマを文言でも示す（アイコンだけに頼らない。INDEX 決定 21）', () => {
    stubPrefersDark(true);
    render(<ThemeToggle />);
    expect(screen.getByText('表示: システム')).toBeInTheDocument();
  });

  it('aria-label に現在と「押した後」の両方が入る', () => {
    stubPrefersDark(true);
    render(<ThemeToggle />);
    const button = screen.getByRole('button');
    expect(button).toHaveAccessibleName(/現在は システム/);
    expect(button).toHaveAccessibleName(/押すと ライト/);
  });

  it('OS がダークのとき system → light → dark → system と巡り、<html> と localStorage が追従する', async () => {
    stubPrefersDark(true);
    render(<ThemeToggle />);
    const button = screen.getByRole('button');

    await userEvent.click(button);
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(window.localStorage.getItem(STORAGE_KEY)).toBe('light');
    expect(screen.getByText('表示: ライト')).toBeInTheDocument();

    await userEvent.click(button);
    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(screen.getByText('表示: ダーク')).toBeInTheDocument();

    await userEvent.click(button);
    // system は属性を持たない（"system" という値を書かない）。
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
    expect(window.localStorage.getItem(STORAGE_KEY)).toBe('system');
    expect(screen.getByText('表示: システム')).toBeInTheDocument();
  });

  it('OS がライトのときは巡回の向きが反転する（1 回押して見た目が変わらない事態を作らない）', async () => {
    stubPrefersDark(false);
    render(<ThemeToggle />);
    await userEvent.click(screen.getByRole('button'));
    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(screen.getByText('表示: ダーク')).toBeInTheDocument();
  });

  it('保存済みの選択を初期表示に反映する', () => {
    stubPrefersDark(true);
    window.localStorage.setItem(STORAGE_KEY, 'light');
    render(<ThemeToggle />);
    expect(screen.getByText('表示: ライト')).toBeInTheDocument();
    expect(document.documentElement.dataset.theme).toBe('light');
  });
});
