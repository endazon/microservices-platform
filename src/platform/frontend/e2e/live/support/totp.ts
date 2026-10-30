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

/**
 * 生の秘密の base32（認証アプリへ手で入れる「手動入力キー」と同じ値。Keycloak の表示は 4 文字ごとに空白を入れる）。
 * #2040 / IADR-0662: 初回登録の画面の手動キーが、登録に使われる秘密（hidden の `totpSecret`）と同じものを指すことを見る。
 */
export function base32Of(rawSecret: string): string {
  return lib.base32Encode(rawSecret);
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

/**
 * 次の周期が始まるまで待ってから、正しい 6 桁と、同じ時刻から導いた誤りの 6 桁を返す（IADR-0626）。
 *
 * 🔴 realm は `otpPolicyCodeReusable: false` なので、同じ利用者が**同じ周期の 6 桁**を 2 度使うと拒否される。
 * `live-setup` は全役割で 1 回ずつ OTP を使い終えているので、その後に同じ役割でもう一度ログインする spec はこれで出す
 * （今の周期は setup が使った周期と同じかもしれない。次の周期は必ず新しい）。
 */
export async function nextPeriodOtp(
  rawSecret: string,
  now: () => number = Date.now,
): Promise<{ code: string; wrong: string; atMs: number }> {
  await new Promise((resolve) => setTimeout(resolve, remainingSeconds(now()) * 1000 + 200));
  // 正しい値と誤りの値を**同じ時刻**から出す（誤りの値が正しい値の窓に入らないことを、同じ t で保証する）。
  const atMs = now();
  return { code: otpFor(rawSecret, atMs), wrong: wrongOtpFor(rawSecret, atMs), atMs };
}

/**
 * 確実に誤りである 6 桁（純関数）。realm の `otpPolicyLookAheadWindow: 1` は前後 1 周期の値も受け付けるので、
 * その 3 周期の正しい値のどれとも違う値を返す（`000000` から順に探す）。
 */
export function wrongOtpFor(rawSecret: string, nowMs: number): string {
  const step = STEP_SECONDS * 1000;
  const valid = new Set([-1, 0, 1].map((k) => otpFor(rawSecret, nowMs + k * step)));
  for (let n = 0; ; n += 1) {
    const candidate = String(n).padStart(6, '0');
    if (!valid.has(candidate)) return candidate;
  }
}
