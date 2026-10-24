// NFR-10 / ADR-0026 / IADR-0294 / IADR-0570 / IADR-0624（#2025）: live 層のログインで第二要素を出す。
//
// 🔴 計算は `scripts/lib/totp.js`（RFC 6238 のテストベクタで固定済み）を使い、ここに実装を増やさない。
// 試験アカウントの秘密は Keycloak の `secretData.value` に入る**生の文字列**であり、認証アプリが使う base32 は
// その UTF-8 バイト列の base32 である（IADR-0570 決定 3）。したがって `totp(base32Encode(raw))` で出す。
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const lib = require('../../../../../../scripts/lib/totp.js') as {
  totp: (secretBase32: string, opts?: { t?: number }) => string;
  base32Encode: (input: string) => string;
};

const STEP_SECONDS = 30;
/** 周期の残りがこれ未満なら、次の周期まで待ってから計算する（入力と送信の間に周期が変わるのを避ける）。 */
const MIN_REMAINING_SECONDS = 5;

/** 今の周期の残り秒数。 */
export function remainingSeconds(nowMs: number): number {
  return STEP_SECONDS - (Math.floor(nowMs / 1000) % STEP_SECONDS);
}

/** 生の秘密から 6 桁を出す（純関数）。 */
export function otpFor(rawSecret: string, nowMs: number): string {
  return lib.totp(lib.base32Encode(rawSecret), { t: Math.floor(nowMs / 1000) });
}

/** 周期の残りが少なければ次の周期まで待ち、6 桁を返す。 */
export async function freshOtp(rawSecret: string, now: () => number = Date.now): Promise<string> {
  const left = remainingSeconds(now());
  if (left < MIN_REMAINING_SECONDS) {
    await new Promise((resolve) => setTimeout(resolve, left * 1000 + 200));
  }
  return otpFor(rawSecret, now());
}
