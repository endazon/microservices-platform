#!/usr/bin/env node
'use strict';
/*
 * lib/totp.js — RFC 6238 の TOTP を計算する。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 背景（#438 / IADR-0294）:
 *   MFA を必須にした結果、`verify-oidc-edge-flow.sh` が通していた認可コードフローに
 *   **OTP の段が挟まった**。MFA を掛けたログイン導線を自動で検証するには、検証する側も
 *   OTP を出せなければならない —— さもないと「検証できないから MFA を外す」という
 *   本末転倒へ倒れる。
 *
 *   🔴 **これは認証の実装ではない。検証器の側が第二要素を出すための計算だけを持つ。**
 *   本番の OTP 検証は Keycloak が行う（ADR-0026 は認証を IdP へ寄せると確定している）。
 *
 * 検証: RFC 6238 §Appendix B の SHA-1 テストベクタ 5 件と一致することを
 *   `scripts/scripts.test.js` が固定する（自前実装を無検証で信じない）。
 *
 * 使い方:
 *   node scripts/lib/totp.js <base32-secret> [unix-seconds]   # 6 桁を stdout へ
 *   const { totp } = require('./lib/totp.js');
 */
const crypto = require('crypto');

// RFC 4648 の base32 を復号する。Keycloak の設定画面は 4 文字ごとに空白を挟んで表示するため、
// 英数以外は落としてから読む（表示のままコピーしても通るようにする）。
function base32Decode(text) {
  const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const ch of String(text || '').toUpperCase().replace(/[^A-Z2-7]/g, '')) {
    bits += ALPHABET.indexOf(ch).toString(2).padStart(5, '0');
  }
  const bytes = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) bytes.push(parseInt(bits.slice(i, i + 8), 2));
  return Buffer.from(bytes);
}

/**
 * TOTP を 1 つ計算する。
 * 既定値は realm の otpPolicy（HmacSHA1 / 6 桁 / 30 秒。ADR-0026 の確定値）に合わせてある。
 * @param {string} secretBase32 Keycloak が表示する base32 のシークレット
 * @param {{t?:number, step?:number, digits?:number, algorithm?:string}} [opts]
 * @returns {string} 先頭ゼロ詰めの数字列
 */
function totp(secretBase32, opts = {}) {
  const { t = Math.floor(Date.now() / 1000), step = 30, digits = 6, algorithm = 'sha1' } = opts;
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(t / step)));
  const mac = crypto.createHmac(algorithm, base32Decode(secretBase32)).update(counter).digest();
  // 動的切り詰め（RFC 4226 §5.4）。最終バイトの下位 4 ビットが開始位置になる。
  const offset = mac[mac.length - 1] & 0x0f;
  const binary = ((mac[offset] & 0x7f) << 24)
    | (mac[offset + 1] << 16)
    | (mac[offset + 2] << 8)
    | mac[offset + 3];
  return String(binary % 10 ** digits).padStart(digits, '0');
}

module.exports = { totp, base32Decode };

if (require.main === module) {
  const [secret, at] = process.argv.slice(2);
  if (!secret) {
    process.stderr.write('使い方: node scripts/lib/totp.js <base32-secret> [unix-seconds]\n');
    process.exit(2);
  }
  process.stdout.write(totp(secret, at ? { t: Number(at) } : {}));
}
