import { useEffect, useState } from 'react';

// SC-19, FR-19, [[IADR-0612]] (#2046): 残り時間の表示のための時計（1 秒ごと）。
//
// **描画中に `Date.now()` を呼ばない**（同じ描画で値が揺れる・試験で固定できない）。時刻は state に持ち、
// タイマーで進める。**動かすのは `active` のときだけ**（有効な発動が無い間に毎秒描き直さない）。

/** 現在時刻（ミリ秒）。`active` が偽の間は止まる。 */
export function useNow(active: boolean, intervalMs = 1000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    const timer = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(timer);
  }, [active, intervalMs]);
  return now;
}
