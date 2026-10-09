'use strict';
// NFR, IADR-0524 (#1859): 起動器（k8s-local-up.sh）が呼ぶ投入スクリプトの**待ちに上限を置く**。
//
// 🔴 なぜ要るか: Node の fetch は既定で要求の上限を持たない（undici の既定はヘッダと本文がそれぞれ 300 秒で、要求の数だけ積み上がる）。
//   受け手（サービス）が下流で詰まると、投入スクリプトは黙って待ち続け、起動器ごと CI のジョブの上限（45 分）まで止まった
//   （#1869 の integration-stack の 2 回目の実行。起動の段で 45 分止まり、ジョブが取り消された）。
//   ここでは「1 要求の上限」と「スクリプト全体の上限」の 2 つを置き、超えたら**何を待っていたか**を名指しして非 0 で終える。
//   値は env で上書きできる（既定は 1 要求 60 秒・全体 300 秒）。

const DEFAULT_REQUEST_MS = 60_000;
const DEFAULT_OVERALL_MS = 300_000;

/** 正の整数のミリ秒を env から読む（読めなければ既定）。 */
function msFromEnv(name, fallback, env = process.env) {
  const raw = env[name];
  if (raw === undefined || raw === '') return fallback;
  const n = Number(raw);
  return Number.isInteger(n) && n > 0 ? n : fallback;
}

/**
 * 上限つきの fetch。上限を超えたら、要求の方法と URL を名指しした Error を投げる（黙って待たない）。
 * 呼び出し側が signal を渡していれば、それと上限の両方で止める。
 */
async function fetchWithin(url, init = {}, timeoutMs = DEFAULT_REQUEST_MS) {
  const limit = AbortSignal.timeout(timeoutMs);
  const signal = init.signal ? AbortSignal.any([init.signal, limit]) : limit;
  try {
    return await fetch(url, { ...init, signal });
  } catch (e) {
    if (limit.aborted) {
      throw new Error(`${init.method || 'GET'} ${url} が ${timeoutMs} ms 以内に応答しなかった（受け手か下流で詰まっている疑い）`);
    }
    throw e;
  }
}

/**
 * スクリプト全体の上限。超えたら名指しして onExpire（既定は終了コード 1）。タイマーはプロセスの終了を妨げない（unref）。
 * @returns {() => void} 解除する関数
 */
function startWatchdog(label, timeoutMs, { onExpire, log = (s) => process.stderr.write(`${s}\n`) } = {}) {
  const t = setTimeout(() => {
    log(`[${label}] ${timeoutMs} ms を超えた。上限で打ち切る（起動器を止め続けない。待ちの上限は #1869 の実測から）。`);
    if (onExpire) onExpire(); else process.exit(1);
  }, timeoutMs);
  if (typeof t.unref === 'function') t.unref();
  return () => clearTimeout(t);
}

module.exports = { fetchWithin, startWatchdog, msFromEnv, DEFAULT_REQUEST_MS, DEFAULT_OVERALL_MS };
