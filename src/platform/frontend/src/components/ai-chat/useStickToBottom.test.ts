import { describe, it, expect, beforeEach } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { useStickToBottom, STICK_THRESHOLD_PX } from './useStickToBottom';

// 05_screens §共通シェル（右レール AI チャット）/ SC-01 / 第 4 弾「体験の穴」(3): AI 回答の追従スクロール。
//
// jsdom はレイアウトを持たない（scrollHeight / clientHeight は常に 0、scrollTop の代入は捨てられる）。
// ここでは 3 つのプロパティを容器の要素へ**模擬**し、フックの判定（下端 32px・遡りで停止・復帰）だけを固定する。

interface FakeScroller {
  el: HTMLDivElement;
  /** 内容を増やす（scrollHeight を伸ばし、DOM を変異させて MutationObserver を起こす）。 */
  grow(by: number): void;
  /** 利用者のスクロール（scrollTop を動かして scroll イベントを飛ばす）。 */
  scrollTo(top: number): void;
}

function fakeScroller(): FakeScroller {
  const el = document.createElement('div');
  let scrollTop = 0;
  let scrollHeight = 100;
  Object.defineProperty(el, 'clientHeight', { get: () => 100 });
  Object.defineProperty(el, 'scrollHeight', { get: () => scrollHeight });
  Object.defineProperty(el, 'scrollTop', {
    get: () => scrollTop,
    set: (v: number) => {
      scrollTop = v;
    },
  });
  document.body.appendChild(el);
  return {
    el,
    grow(by) {
      scrollHeight += by;
      el.appendChild(document.createTextNode('x'));
    },
    scrollTo(top) {
      scrollTop = top;
      el.dispatchEvent(new Event('scroll'));
    },
  };
}

let scroller: FakeScroller;

beforeEach(() => {
  document.body.innerHTML = '';
  scroller = fakeScroller();
});

describe('useStickToBottom', () => {
  // 追従中（既定）: 内容が増えると下端へ寄る。
  it('follows new content while stuck to the bottom', async () => {
    renderHook(() => useStickToBottom({ current: scroller.el }));
    act(() => scroller.grow(500));
    await waitFor(() => expect(scroller.el.scrollTop).toBe(600));
  });

  // 上へ遡ったら追従を止める（読んでいる箇所を奪わない）。
  it('stops following once the user scrolls up past the threshold', async () => {
    const { result } = renderHook(() => useStickToBottom({ current: scroller.el }));
    act(() => scroller.grow(500));
    await waitFor(() => expect(scroller.el.scrollTop).toBe(600));

    act(() => scroller.scrollTo(100));
    expect(result.current.stuck).toBe(false);

    act(() => scroller.grow(200));
    // 少し待っても動かない（追従が止まっている）。
    await new Promise((r) => setTimeout(r, 20));
    expect(scroller.el.scrollTop).toBe(100);
  });

  // 下端の判定は 32px（ちょうど下端でなくても「ほぼ最後まで読んでいる」なら追従中）。
  it('treats anything closer than the threshold as the bottom', () => {
    const { result } = renderHook(() => useStickToBottom({ current: scroller.el }));
    act(() => scroller.grow(500)); // scrollHeight 600, clientHeight 100 → 下端は 500
    act(() => scroller.scrollTo(500 - STICK_THRESHOLD_PX + 1));
    expect(result.current.stuck).toBe(true);
    act(() => scroller.scrollTo(500 - STICK_THRESHOLD_PX));
    expect(result.current.stuck).toBe(false);
  });

  // 利用者が下端まで戻したら自動で再開する。
  it('resumes following when the user scrolls back to the bottom', async () => {
    const { result } = renderHook(() => useStickToBottom({ current: scroller.el }));
    act(() => scroller.grow(500));
    act(() => scroller.scrollTo(0));
    expect(result.current.stuck).toBe(false);

    act(() => scroller.scrollTo(500));
    expect(result.current.stuck).toBe(true);
    act(() => scroller.grow(100)); // scrollHeight 700
    await waitFor(() => expect(scroller.el.scrollTop).toBe(700));
  });

  // 「最新へ」: 遡り中でも呼べば下端へ戻り、追従を再開する。
  it('scrollToBottom jumps to the end and re-enables following', async () => {
    const { result } = renderHook(() => useStickToBottom({ current: scroller.el }));
    act(() => scroller.grow(500));
    act(() => scroller.scrollTo(0));
    expect(result.current.stuck).toBe(false);

    act(() => result.current.scrollToBottom());
    expect(scroller.el.scrollTop).toBe(600); // scrollHeight（下端へ寄せる値）
    expect(result.current.stuck).toBe(true);
    act(() => scroller.grow(100)); // scrollHeight 700
    await waitFor(() => expect(scroller.el.scrollTop).toBe(700));
  });
});
