import { useCallback, useEffect, useRef, useState } from 'react';
import type { RefObject } from 'react';

// 05_screens §共通シェル（右レール AI チャット）/ SC-01 / 第 4 弾「体験の穴」(3) AI 回答の追従スクロール。
//
// ■ 何をするか
//   スクロール容器の**下端に居る間だけ**、内容が増えるたびに下端へ追従する。利用者が上へ遡ったら追従を
//   止め（読んでいる箇所を奪わない）、下端へ戻ったら自動で再開する。呼び出し側は「最新へ」ボタンで
//   `scrollToBottom()` を呼べば、遡り中でも復帰できる。
//
// ■ 下端の判定は 32px
//   `scrollHeight - scrollTop - clientHeight < 32`。0 にすると小数ピクセルの丸めで「下端に居るのに
//   居ない」と判定され、追従が勝手に止まる（ズーム時に顕著）。32px は 1 行ぶん強で、
//   「ほぼ最後まで読んでいる」と「遡っている」を分けるのに十分である。
//
// ■ 依存配列ではなく MutationObserver
//   内容の増加は DOM の変化そのものなので、容器を `MutationObserver` で見る。呼び出し側が
//   「何が変わったら追従するか」を依存配列で申告する形にすると、申告漏れ（出典の追加・Markdown の
//   描き直し）で追従が抜ける。shadcn の MessageScroller は写さない（あれは依存配列と
//   `ResizeObserver` を前提にし、jsdom では `ResizeObserver` が無い）。
//
// ■ 検出しないこと
//   容器自身の大きさが変わった（レール幅の変更）ときは追従しない。内容の変化ではないためで、
//   次の内容変化で再び下端へ寄る。

/** 下端とみなす残り距離（px）。 */
export const STICK_THRESHOLD_PX = 32;

export interface StickToBottom {
  /** 追従中か（下端に居るか）。false のとき呼び出し側は「最新へ」の復帰手段を出す。 */
  stuck: boolean;
  /** 下端へ移動し、追従を再開する。 */
  scrollToBottom: () => void;
}

function isAtBottom(el: HTMLElement): boolean {
  return el.scrollHeight - el.scrollTop - el.clientHeight < STICK_THRESHOLD_PX;
}

export function useStickToBottom(ref: RefObject<HTMLElement | null>): StickToBottom {
  const [stuck, setStuck] = useState(true);
  // Observer の callback は描画の外で走るため、state の閉包ではなく ref で「追従中か」を読む。
  const stuckRef = useRef(true);

  const scrollToBottom = useCallback(() => {
    const el = ref.current;
    if (el) el.scrollTop = el.scrollHeight;
    stuckRef.current = true;
    setStuck(true);
  }, [ref]);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;

    const onScroll = () => {
      const at = isAtBottom(el);
      stuckRef.current = at;
      setStuck(at);
    };
    el.addEventListener('scroll', onScroll);

    // 内容が増えたとき、追従中なら下端へ寄せる。遡っている間は動かさない。
    const observer = new MutationObserver(() => {
      if (stuckRef.current) el.scrollTop = el.scrollHeight;
    });
    observer.observe(el, { childList: true, subtree: true, characterData: true });

    return () => {
      el.removeEventListener('scroll', onScroll);
      observer.disconnect();
    };
  }, [ref]);

  return { stuck, scrollToBottom };
}
