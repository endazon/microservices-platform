'use strict';
// NFR / FR-05, #438 / #1033: Keycloak のログイン系画面から、**POST に必要なものだけ**を取り出す。
//
// 🔴 **なぜ切り出したのか。** 従前この解析は `verify-oidc-edge-flow.sh` の中の `sed` 一行だった。
// その形では**一度も実行されないまま壊れていることに気付けなかった** —— MFA の段は
// 実 Keycloak でしか通らず、この環境では走らせられない。node 側へ出すと、**画面の HTML を
// 固定値として与える検査**が書ける（`scripts.test.js`）。実 Keycloak が無くても、
// 「何を送るか」の判断だけは機械で守れる。
//
// 対象は Keycloak 24 の base テーマ:
//   - `login-config-totp.ftl`（初回登録）… `totp` / hidden `totpSecret` / hidden `mode` /
//     `userLabel` を持ち、表示用の base32 は `<span id="kc-totp-secret-key">`。
//     **`cancel-aia` という submit も同じフォームに居る**（送ると登録が取り消される）。
//   - `login-otp.ftl`（2 回目以降）… `otp` だけを持ち、シークレットは画面に出ない。
//
// 🔴 **submit / button は返さない。** 「フォームの入力を全部送り返す」と書くと `cancel-aia=true`
// まで送ることになり、**成功と見分けの付かない取り消し**になる。

const ENTITIES = { amp: '&', quot: '"', apos: "'", lt: '<', gt: '>', '#39': "'", '#x27': "'" };

function decodeEntities(value) {
  return String(value ?? '').replace(/&(#x?[0-9a-fA-F]+|[a-zA-Z]+);/g, (whole, name) => {
    const key = name.toLowerCase();
    if (Object.prototype.hasOwnProperty.call(ENTITIES, key)) return ENTITIES[key];
    if (/^#x[0-9a-f]+$/.test(key)) return String.fromCodePoint(parseInt(key.slice(2), 16));
    if (/^#[0-9]+$/.test(key)) return String.fromCodePoint(Number(key.slice(1)));
    return whole;
  });
}

function attr(tag, name) {
  const m = tag.match(new RegExp(`\\b${name}="([^"]*)"`, 'i'));
  return m ? decodeEntities(m[1]) : '';
}

/**
 * 先頭のフォームを解析する。
 *
 * 先頭に取るのは Keycloak のログイン系画面が**主フォームを先に置く**ためである
 * （`login.ftl` の「別の方法を試す」フォームは後ろに来る）。
 *
 * @param {string} html 画面の HTML
 * @returns {{action: string, fields: Record<string,string>, totpField: string, totpSecretEncoded: string}}
 */
function parseLoginForm(html) {
  const text = String(html ?? '');
  const out = { action: '', fields: {}, totpField: '', totpSecretEncoded: '' };

  const start = text.search(/<form\b/i);
  if (start >= 0) {
    const end = text.toLowerCase().indexOf('</form>', start);
    const form = end >= 0 ? text.slice(start, end) : text.slice(start);
    const openTag = form.match(/<form\b[^>]*>/i);
    out.action = openTag ? attr(openTag[0], 'action') : '';

    for (const m of form.matchAll(/<input\b[^>]*>/gi)) {
      const tag = m[0];
      const name = attr(tag, 'name');
      if (!name) continue;
      const type = (attr(tag, 'type') || 'text').toLowerCase();
      // submit / button / image は「押した」ことを表す。送り返す対象ではない。
      if (type === 'submit' || type === 'button' || type === 'image' || type === 'reset') continue;
      out.fields[name] = attr(tag, 'value');
      if (name === 'totp' || name === 'otp') out.totpField = name;
    }
  }

  // 表示用の base32（4 文字ごとの空白つき）。TOTP の計算に使う。`totpSecret`（生の値）とは別物で、
  // **両方が要る** —— 計算は encoded、POST は raw である。
  const secret = text.match(/id="kc-totp-secret-key"[^>]*>([^<]*)</i);
  out.totpSecretEncoded = secret ? decodeEntities(secret[1]).trim() : '';
  return out;
}

module.exports = { parseLoginForm, decodeEntities };

// シェルから使うときは、平坦な JSON を返す（`verify-oidc-edge-flow.sh` の `json_field` が読む）。
if (require.main === module) {
  const fs = require('fs');
  const file = process.argv[2];
  if (!file) {
    process.stderr.write('usage: keycloak-login-form.js <html-file>\n');
    process.exit(2);
  }
  const parsed = parseLoginForm(fs.readFileSync(file, 'utf8'));
  process.stdout.write(JSON.stringify({
    action: parsed.action,
    totpField: parsed.totpField,
    totpSecretEncoded: parsed.totpSecretEncoded,
    totpSecret: parsed.fields.totpSecret ?? '',
    mode: parsed.fields.mode ?? '',
  }));
}
