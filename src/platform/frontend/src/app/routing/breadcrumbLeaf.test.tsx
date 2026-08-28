import { describe, it, expect, vi } from 'vitest';
import { render } from '@testing-library/react';
import { BreadcrumbLeafContext, useBreadcrumbLeaf } from './breadcrumbLeaf';

// 05_screens §共通シェル / #446: パンくずの**動的な葉**（SC-03 の文書タイトル）の受け渡し。
// 段の組み立ては breadcrumbs.test.ts、描画は Layout.test.tsx が見る。ここは**引き渡しの契約**だけ。

function Screen({ leaf }: { leaf: string | undefined }) {
  useBreadcrumbLeaf(leaf);
  return <p>screen</p>;
}

function renderWithSetter(leaf: string | undefined) {
  const setLeaf = vi.fn();
  const view = render(
    <BreadcrumbLeafContext.Provider value={setLeaf}>
      <Screen leaf={leaf} />
    </BreadcrumbLeafContext.Provider>,
  );
  return { setLeaf, view };
}

describe('useBreadcrumbLeaf', () => {
  it('publishes the leaf the screen supplies', () => {
    const { setLeaf } = renderWithSetter('経費精算規程 v3.2');
    expect(setLeaf).toHaveBeenCalledWith('経費精算規程 v3.2');
  });

  // 取得前は葉を出さない（未確定の文字列を描かない）。
  it('publishes undefined while the screen has nothing to show yet', () => {
    const { setLeaf } = renderWithSetter(undefined);
    expect(setLeaf).toHaveBeenCalledWith(undefined);
    expect(setLeaf.mock.calls.every(([v]) => v === undefined)).toBe(true);
  });

  it('republishes when the screen supplies a different leaf', () => {
    const setLeaf = vi.fn();
    const { rerender } = render(
      <BreadcrumbLeafContext.Provider value={setLeaf}>
        <Screen leaf="A" />
      </BreadcrumbLeafContext.Provider>,
    );
    setLeaf.mockClear();
    rerender(
      <BreadcrumbLeafContext.Provider value={setLeaf}>
        <Screen leaf="B" />
      </BreadcrumbLeafContext.Provider>,
    );
    expect(setLeaf).toHaveBeenCalledWith('B');
  });

  // 🔴 戻さないと、次の画面のパンくずに前の文書名が残る。
  it('clears the leaf when the screen unmounts', () => {
    const { setLeaf, view } = renderWithSetter('経費精算規程 v3.2');
    setLeaf.mockClear();
    view.unmount();
    expect(setLeaf).toHaveBeenCalledWith(undefined);
  });

  // 共通シェルの外（ユニット単体のテストハーネス等）で描いても落ちないこと。
  it('is a no-op without a provider', () => {
    expect(() => render(<Screen leaf="x" />)).not.toThrow();
  });
});
