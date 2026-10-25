#!/usr/bin/env node
'use strict';
/*
 * check-e2e-artifact-secrets.js — NFR / NFR-18 / #2026 / IADR-0625
 *
 * live 層の E2E の成果物（html レポート・test-results）を**上げる前に**走査し、試験アカウントの資格情報の値
 * （`E2E_<ROLE>_PASSWORD` / `E2E_<ROLE>_TOTP_SECRET`）が 1 つでも載っていれば赤にする。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * なぜ要るか: Playwright は既定で値を残す経路を持つ（`locator.fill(value)` の値は step の題として html レポートに載る。trace は入力と
 * 通信を記録する。IADR-0624 の実測）。setup は塞いだが、後から書かれる spec が同じ経路を踏んでも止まる門が無い。
 *
 * 見るもの: 指定したディレクトリの全ファイルの生のバイト列と、その中の zip（trace の `.zip`・html レポートが埋め込む
 * `data:application/zip;base64,…`）を展開した中身（入れ子の zip も 4 段まで辿り、超えたら例外）。探す形は値そのものに加えて、
 * TOTP の秘密の base32・各値の base64（標準・URL 用）・Basic 認証の `e2e-<role>:<パスワード>` の base64（`formsOf`）。
 * 見ないもの: セッション cookie の値。cookie は層の trace のリクエストに必ず載り（外す設定が無い）、teardown が実行の終わりに
 * BFF のログアウトで失効させる（IADR-0624 決定 4）。値は実行の後に誰も持たない（state は teardown が消す）ので、ここでは突き合わせない。
 *
 * fail-closed: 資格情報の値が 1 つも読めない・走査したファイルが 0 件・zip が壊れている、はいずれも exit 1。
 * 出力は**ファイルの場所と変数名だけ**（値は出さない）。
 *
 * ［2026-10-25 / #2031 / IADR-0630 決定 6］**実行の中で発行された値**（SC-20 の端末トークン、E-54 の SC-22 の試験キーなど）も探す。
 *   live の spec は発行の直後に `E2E_ISSUED_<種類>=<値>` の行を `E2E_ISSUED_SECRETS_FILE`（runner の一時領域・0600）へ追記する
 *   （`e2e/live/support/issuedSecrets.ts`）。`--issued-file` でそのファイルを渡す。失効済みの値も同じに扱う（成果物に残れば赤）。
 *   🔴 `--issued-file` を明示したのにファイルが無ければ exit 1（安全装置の入力が欠けたまま緑にしない）。globalSetup が実行の初めに
 *   空のファイル（0600）を作るので、「何も発行しなかった」は空のファイル（0 個）で表れ、「記録先が無い」とは区別できる。行の形が違えば例外。
 *
 * 使い方:
 *   node scripts/check-e2e-artifact-secrets.js --env-file <KEY=VALUE の行> [--issued-file <E2E_ISSUED_…=値 の行>] --scan <dir> [--scan <dir> …]
 *   node scripts/check-e2e-artifact-secrets.js --self-test
 */
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');
const { base32Encode } = require('./lib/totp.js');

const SECRET_NAME = /^E2E_[A-Z0-9_]+_(PASSWORD|TOTP_SECRET)$/;
/** 実行の中で発行された値の名前（`e2e/live/support/issuedSecrets.ts` と同じ形）。 */
const ISSUED_NAME = /^E2E_ISSUED_[A-Z0-9_]+$/;
/** 短すぎる値は偶然の一致を拾う。資格情報は 20 文字以上（IADR-0570 の生成器）。 */
const MIN_LENGTH = 12;
const EMBEDDED_ZIP = /data:application\/zip;base64,([A-Za-z0-9+/=]+)/g;

function parseArgs(argv) {
  const a = { selfTest: false, envFile: null, issuedFile: null, scan: [] };
  for (let i = 0; i < argv.length; i++) {
    const t = argv[i];
    if (t === '--self-test') a.selfTest = true;
    else if (t === '--env-file') a.envFile = argv[++i];
    else if (t === '--issued-file') a.issuedFile = argv[++i];
    else if (t === '--scan') a.scan.push(argv[++i]);
    else throw new Error(`未知の引数: ${t}`);
  }
  return a;
}

/** 入れ子の zip をここまで辿る。超えたら例外（黙って打ち切ると、その奥を「無い」とみなす）。 */
const MAX_DEPTH = 4;

/**
 * 値が符号化の途中（ヘッダや文字列の任意の位置）に置かれたときにも当たる、符号化の**中央部分**（純関数）。
 * 前に 0〜(group-1) バイトの詰め物を付けて符号化し、詰め物と後ろの不明なバイトに掛かる端の文字を落とす。
 * bits は 1 文字あたりのビット数（base64 = 6・base32 = 5）。
 */
function middles(value, { bits, pads, encode }) {
  const data = Buffer.from(value, 'utf8');
  const out = [];
  for (let pad = 0; pad < pads; pad++) {
    const text = encode(Buffer.concat([Buffer.alloc(pad), data])).replace(/=+$/, '');
    const start = Math.ceil((pad * 8) / bits);
    const end = Math.floor(((pad + data.length) * 8) / bits);
    out.push({ pad, text: text.slice(start, end) });
  }
  return out;
}

/**
 * 1 つの秘密が成果物に載りうる形（純関数）。値そのものに加えて、道具が符号化して残す形を探す:
 * - TOTP の秘密の base32（認証アプリ・`scripts/lib/totp.js` の `base32Encode`。Keycloak の画面の表示もこの形）
 * - 各値の base64（HTTP のヘッダや埋め込みのデータ。標準と URL 用の両方）
 * - Basic 認証の `<利用者>:<パスワード>` の base64（利用者名は `e2e-<role>`）
 */
function formsOf(secret) {
  const forms = [{ how: '値', text: secret.value }];
  for (const m of middles(secret.value, { bits: 6, pads: 3, encode: (b) => b.toString('base64') })) {
    forms.push({ how: `base64（詰め物 ${m.pad}）`, text: m.text });
    forms.push({ how: `base64url（詰め物 ${m.pad}）`, text: m.text.replace(/\+/g, '-').replace(/\//g, '_') });
  }
  if (secret.name.endsWith('_TOTP_SECRET')) {
    for (const m of middles(secret.value, { bits: 5, pads: 5, encode: (b) => base32Encode(b) })) {
      forms.push({ how: `base32（詰め物 ${m.pad}）`, text: m.text });
    }
  }
  if (secret.name.endsWith('_PASSWORD')) {
    const role = secret.name.slice('E2E_'.length, -'_PASSWORD'.length).toLowerCase().replace(/_/g, '-');
    const basic = Buffer.from(`e2e-${role}:${secret.value}`, 'utf8').toString('base64').replace(/=+$/, '');
    forms.push({ how: 'Basic の base64', text: basic });
  }
  // 中央部分が短いと偶然の一致を拾うので、最短の長さに満たない形は探さない。同じ文字列は 1 つにまとめる。
  const seen = new Set();
  return forms.filter((f) => f.text.length >= MIN_LENGTH && !seen.has(f.text) && seen.add(f.text));
}

/** `KEY=VALUE` の行から資格情報だけを取る（純関数）。 */
function readSecrets(text) {
  const out = [];
  for (const line of String(text).split(/\r?\n/)) {
    const m = /^([A-Z0-9_]+)=(.*)$/.exec(line.trim());
    if (!m || !SECRET_NAME.test(m[1])) continue;
    if (m[2].length < MIN_LENGTH) throw new Error(`${m[1]}: 値が ${MIN_LENGTH} 文字未満で、偶然の一致と見分けられない`);
    out.push({ name: m[1], value: m[2], forms: formsOf({ name: m[1], value: m[2] }) });
  }
  if (out.length === 0) throw new Error('資格情報の値が 1 つも読めない（0 件の走査を緑にしない）');
  return out;
}

/**
 * 実行の中で発行された値（`E2E_ISSUED_<種類>=<値>` の行）を読む（純関数）。空行は飛ばし、それ以外の形・短すぎる値は例外（値は出さない）。
 * 同じ名前の行が複数あってよい（再試行・複数の端末）。名前に通し番号を付けて区別する。
 */
function readIssued(text) {
  const out = [];
  let n = 0;
  for (const line of String(text).split(/\r?\n/)) {
    if (line.trim() === '') continue;
    n++;
    const m = /^([A-Z0-9_]+)=(.*)$/.exec(line);
    if (!m || !ISSUED_NAME.test(m[1])) throw new Error(`発行値のファイルの ${n} 行目の形が違う（E2E_ISSUED_<種類>=<値>）`);
    if (m[2].length < MIN_LENGTH) throw new Error(`${m[1]}（${n} 行目）: 値が ${MIN_LENGTH} 文字未満で、偶然の一致と見分けられない`);
    const name = `${m[1]}#${n}`;
    out.push({ name, value: m[2], forms: formsOf({ name, value: m[2] }) });
  }
  return out;
}

/** `--issued-file` を読む。指定したのにファイルが無ければ例外（0 個として黙って続けない）。指定が無ければ 0 個。 */
function loadIssued(file) {
  if (!file) return [];
  if (!fs.existsSync(file)) {
    throw new Error(`発行値のファイルが無い（${file}）。globalSetup が空のファイルを作るので、無いのは記録先が欠けた実行である`);
  }
  return readIssued(fs.readFileSync(file, 'utf8'));
}

/** zip の中身を返す（central directory を読む。無圧縮と deflate だけ。Playwright の zip はこの 2 つ）。 */
function unzip(buf) {
  let eocd = -1;
  for (let i = buf.length - 22; i >= Math.max(0, buf.length - 65557); i--) {
    if (buf.readUInt32LE(i) === 0x06054b50) { eocd = i; break; }
  }
  if (eocd < 0) throw new Error('zip の終端が見つからない');
  const count = buf.readUInt16LE(eocd + 10);
  let p = buf.readUInt32LE(eocd + 16);
  const entries = [];
  for (let n = 0; n < count; n++) {
    if (buf.readUInt32LE(p) !== 0x02014b50) throw new Error('zip の中央ディレクトリが壊れている');
    const method = buf.readUInt16LE(p + 10);
    const size = buf.readUInt32LE(p + 20);
    const nameLen = buf.readUInt16LE(p + 28);
    const extraLen = buf.readUInt16LE(p + 30);
    const commentLen = buf.readUInt16LE(p + 32);
    const local = buf.readUInt32LE(p + 42);
    const name = buf.toString('utf8', p + 46, p + 46 + nameLen);
    p += 46 + nameLen + extraLen + commentLen;
    if (buf.readUInt32LE(local) !== 0x04034b50) throw new Error(`zip の項目 ${name} の位置が壊れている`);
    const start = local + 30 + buf.readUInt16LE(local + 26) + buf.readUInt16LE(local + 28);
    const raw = buf.subarray(start, start + size);
    let data;
    if (method === 0) data = raw;
    else if (method === 8) data = zlib.inflateRawSync(raw);
    else throw new Error(`zip の項目 ${name} の圧縮方式 ${method} を読めない`);
    entries.push({ name, data });
  }
  return entries;
}

/** バイト列と、その中の zip を辿って、見つかった資格情報の名前を返す（純関数に近い。zip は再帰で開く）。 */
function findIn(buf, secrets, where, hits, depth = 0) {
  if (depth > MAX_DEPTH) throw new Error(`${where}: zip の入れ子が ${MAX_DEPTH} 段を超える（奥を「無い」とみなさない）`);
  for (const s of secrets) {
    for (const f of s.forms) if (buf.includes(f.text)) hits.push({ where, name: s.name, how: f.how });
  }
  const isZip = buf.length >= 4 && buf.readUInt32LE(0) === 0x04034b50;
  if (isZip) for (const e of unzip(buf)) findIn(e.data, secrets, `${where}!${e.name}`, hits, depth + 1);
  const text = buf.toString('latin1');
  for (const m of text.matchAll(EMBEDDED_ZIP)) {
    findIn(Buffer.from(m[1], 'base64'), secrets, `${where}!(埋め込みの zip)`, hits, depth + 1);
  }
}

function filesUnder(dir) {
  const out = [];
  const visit = (d) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const p = path.join(d, e.name);
      if (e.isDirectory()) visit(p);
      else if (e.isFile()) out.push(p);
    }
  };
  visit(dir);
  return out;
}

function scan(dirs, secrets) {
  const hits = [];
  let files = 0;
  for (const dir of dirs) {
    if (!fs.existsSync(dir)) continue;
    for (const f of filesUnder(dir)) {
      files += 1;
      findIn(fs.readFileSync(f), secrets, f, hits);
    }
  }
  return { files, hits };
}

// ---- 自己試験 -------------------------------------------------------------------------------------------

/** 自己試験用の zip を作る（store / deflate を混ぜる）。 */
function makeZip(items) {
  const locals = [];
  const centrals = [];
  let offset = 0;
  for (const [name, content, deflate] of items) {
    const data = Buffer.isBuffer(content) ? content : Buffer.from(content, 'utf8');
    const body = deflate ? zlib.deflateRawSync(data) : data;
    const nameBuf = Buffer.from(name, 'utf8');
    const lh = Buffer.alloc(30);
    lh.writeUInt32LE(0x04034b50, 0); lh.writeUInt16LE(deflate ? 8 : 0, 8);
    lh.writeUInt32LE(body.length, 18); lh.writeUInt32LE(data.length, 22); lh.writeUInt16LE(nameBuf.length, 26);
    const ch = Buffer.alloc(46);
    ch.writeUInt32LE(0x02014b50, 0); ch.writeUInt16LE(deflate ? 8 : 0, 10);
    ch.writeUInt32LE(body.length, 20); ch.writeUInt32LE(data.length, 24); ch.writeUInt16LE(nameBuf.length, 28); ch.writeUInt32LE(offset, 42);
    locals.push(lh, nameBuf, body);
    centrals.push(ch, nameBuf);
    offset += lh.length + nameBuf.length + body.length;
  }
  const cd = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(items.length, 8); end.writeUInt16LE(items.length, 10);
  end.writeUInt32LE(cd.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, cd, end]);
}

function selfTest() {
  const os = require('os');
  const pw = ['Pw', 'selftest', 'A1b2C3d4E5'].join('-');
  const totp = ['T0tp', 'selftest', 'Z9y8X7w6V5'].join('');
  const env = `E2E_ADMIN_PASSWORD=${pw}\nE2E_ADMIN_TOTP_SECRET=${totp}\nE2E_LIVE=1\n`;
  const withDir = (fill) => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'e2e-artifact-'));
    try { fill(dir); return scan([dir], readSecrets(env)); } finally { fs.rmSync(dir, { recursive: true, force: true }); }
  };
  const cases = [
    {
      name: 'readSecrets: 資格情報だけを取る（E2E_LIVE などは取らない）',
      run: () => readSecrets(env).map((s) => s.name).join(','),
      expect: (v) => v === 'E2E_ADMIN_PASSWORD,E2E_ADMIN_TOTP_SECRET',
    },
    {
      name: '🔴 readSecrets: 1 つも読めなければ例外（0 件の走査を緑にしない）',
      run: () => { try { readSecrets('E2E_LIVE=1'); return 'no-throw'; } catch (e) { return e.message; } },
      expect: (m) => /1 つも読めない/.test(m),
    },
    {
      name: '素のファイルに値があれば、場所と名前を返す（値は返さない）',
      run: () => withDir((d) => fs.writeFileSync(path.join(d, 'a.txt'), `x ${pw} y`)),
      expect: (r) => r.files === 1 && r.hits.length === 1 && r.hits[0].name === 'E2E_ADMIN_PASSWORD' && !JSON.stringify(r).includes(pw),
    },
    {
      name: 'trace の zip（deflate）の中の値を見つける',
      run: () => withDir((d) => fs.writeFileSync(path.join(d, 'trace.zip'), makeZip([['0-trace.trace', `{"value":"${totp}"}`, true], ['b.txt', 'none', false]]))),
      expect: (r) => r.hits.length === 1 && r.hits[0].name === 'E2E_ADMIN_TOTP_SECRET' && /trace\.zip!0-trace\.trace/.test(r.hits[0].where),
    },
    {
      name: 'html レポートが埋め込む base64 の zip の、さらに中の zip の値を見つける',
      run: () => withDir((d) => {
        const inner = makeZip([['step.json', `Fill "${pw}"`, true]]);
        const outer = makeZip([['report.zip', inner, false]]);
        fs.writeFileSync(path.join(d, 'index.html'), `<script>window.r="data:application/zip;base64,${outer.toString('base64')}";</script>`);
      }),
      expect: (r) => r.hits.length === 1 && r.hits[0].name === 'E2E_ADMIN_PASSWORD',
    },
    {
      name: '🔴 TOTP の秘密の base32 形を見つける（認証アプリ・Keycloak の画面の表示の形）',
      run: () => withDir((d) => fs.writeFileSync(path.join(d, 'a.txt'), `key ${base32Encode(totp)}`)),
      expect: (r) => r.hits.length === 1 && r.hits[0].name === 'E2E_ADMIN_TOTP_SECRET' && /^base32/.test(r.hits[0].how),
    },
    {
      name: '🔴 値の base64 形を見つける（標準・URL 用）',
      run: () => withDir((d) => {
        const b = Buffer.from(pw, 'utf8').toString('base64');
        fs.writeFileSync(path.join(d, 'a.txt'), b);
        fs.writeFileSync(path.join(d, 'b.txt'), b.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''));
      }),
      expect: (r) => r.hits.filter((h) => h.name === 'E2E_ADMIN_PASSWORD' && /^base64/.test(h.how)).length >= 2,
    },
    {
      name: '🔴 文字列の途中で base64 に符号化された値を、前の詰め物 0・1・2 バイトの 3 通りとも見つける',
      run: () => [0, 1, 2].map((pad) => withDir((d) => {
        const enc = Buffer.from(`${'xyz'.slice(0, pad)}${pw}tail`, 'utf8').toString('base64');
        fs.writeFileSync(path.join(d, 'h.txt'), `header: ${enc}`);
      })),
      expect: (rs) => rs.every((r) => r.hits.some((h) => h.name === 'E2E_ADMIN_PASSWORD' && /^base64/.test(h.how))),
    },
    {
      name: '🔴 文字列の途中で base32 に符号化された TOTP の秘密を、前の詰め物 0〜4 バイトの 5 通りとも見つける',
      run: () => [0, 1, 2, 3, 4].map((pad) => withDir((d) => {
        fs.writeFileSync(path.join(d, 'q.txt'), `otpauth ${base32Encode(Buffer.from(`${'abcd'.slice(0, pad)}${totp}zz`, 'utf8'))}`);
      })),
      expect: (rs) => rs.every((r) => r.hits.some((h) => h.name === 'E2E_ADMIN_TOTP_SECRET' && /^base32/.test(h.how))),
    },
    {
      name: '陰性: 別の値を途中で符号化したものには当たらない（詰め物の 3 通り・5 通り）',
      run: () => withDir((d) => {
        const other = ['Other', 'value', 'Q1w2E3r4T5y6'].join('-');
        for (const pad of [0, 1, 2]) fs.writeFileSync(path.join(d, `b${pad}.txt`), Buffer.from(`${'xyz'.slice(0, pad)}${other}`).toString('base64'));
        for (const pad of [0, 1, 2, 3, 4]) fs.writeFileSync(path.join(d, `c${pad}.txt`), base32Encode(Buffer.from(`${'abcd'.slice(0, pad)}${other}`)));
      }),
      expect: (r) => r.files === 8 && r.hits.length === 0,
    },
    {
      name: '🔴 Basic 認証の「利用者:パスワード」の base64 形を見つける',
      run: () => withDir((d) => fs.writeFileSync(path.join(d, 'h.txt'), `authorization: Basic ${Buffer.from(`e2e-admin:${pw}`).toString('base64')}`)),
      expect: (r) => r.hits.some((h) => h.name === 'E2E_ADMIN_PASSWORD' && h.how === 'Basic の base64'),
    },
    {
      name: '🔴 zip の入れ子が上限を超えたら例外（奥を黙って打ち切らない）',
      run: () => {
        let z = makeZip([['leaf.txt', 'x', false]]);
        for (let i = 0; i < 6; i++) z = makeZip([[`n${i}.zip`, z, false]]);
        try { withDir((d) => fs.writeFileSync(path.join(d, 'deep.zip'), z)); return 'no-throw'; } catch (e) { return e.message; }
      },
      expect: (m) => /入れ子/.test(m),
    },
    {
      name: '値が無ければ 0 件',
      run: () => withDir((d) => fs.writeFileSync(path.join(d, 'trace.zip'), makeZip([['a', 'nothing here', true]]))),
      expect: (r) => r.files === 1 && r.hits.length === 0,
    },
    {
      name: '🔴 壊れた zip は例外（読めないものを「無い」としない）',
      run: () => { try { withDir((d) => fs.writeFileSync(path.join(d, 'x.zip'), Buffer.from([0x50, 0x4b, 0x03, 0x04, 1, 2, 3]))); return 'no-throw'; } catch (e) { return e.message; } },
      expect: (m) => /zip/.test(m),
    },
    {
      name: '発行値（端末トークン）が成果物にあれば、名前と通し番号を返す（値は返さない）',
      run: () => {
        const tok = ['tok', 'selftest', 'Q1w2E3r4T5y6'].join('-');
        const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'e2e-artifact-'));
        try {
          fs.writeFileSync(path.join(dir, 'error.md'), `token ${Buffer.from(`x${tok}`).toString('base64')} ${tok}`);
          const r = scan([dir], [...readSecrets(env), ...readIssued(`\nE2E_ISSUED_DEVICE_TOKEN=${tok}\n`)]);
          return { r, leaked: JSON.stringify(r).includes(tok) };
        } finally { fs.rmSync(dir, { recursive: true, force: true }); }
      },
      expect: (v) => !v.leaked && v.r.hits.length >= 1 && v.r.hits.every((h) => h.name === 'E2E_ISSUED_DEVICE_TOKEN#1'),
    },
    {
      name: '🔴 --issued-file を指定したのにファイルが無ければ例外、空のファイルは 0 個（記録先の欠けと「何も発行しなかった」を区別する）',
      run: () => {
        const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'e2e-issued-'));
        try {
          let missing;
          try { loadIssued(path.join(dir, 'none.env')); missing = 'no-throw'; } catch (e) { missing = e.message; }
          fs.writeFileSync(path.join(dir, 'empty.env'), '');
          return { missing, empty: loadIssued(path.join(dir, 'empty.env')).length, unset: loadIssued(null).length };
        } finally { fs.rmSync(dir, { recursive: true, force: true }); }
      },
      expect: (v) => /ファイルが無い/.test(v.missing) && v.empty === 0 && v.unset === 0,
    },
    {
      name: '🔴 発行値のファイルの形が違えば例外（読めないものを「無い」としない）',
      run: () => { try { readIssued('DEVICE_TOKEN=abcdefghijklmnop'); return 'no-throw'; } catch (e) { return e.message; } },
      expect: (m) => /形が違う/.test(m),
    },
  ];
  let failed = 0;
  for (const c of cases) {
    let got;
    try { got = c.run(); } catch (e) { failed++; process.stderr.write(`  NG  ${c.name}\n      例外: ${e.message}\n`); continue; }
    if (c.expect(got)) process.stdout.write(`  ok  ${c.name}\n`);
    else { failed++; process.stderr.write(`  NG  ${c.name}\n      got=${JSON.stringify(got)}\n`); }
  }
  if (failed) { process.stderr.write(`\n✗ 検証器の自己試験が ${failed} 件失敗した\n`); return 1; }
  process.stdout.write(`✓ 検証器の自己試験 ${cases.length} 件すべて合格\n`);
  return 0;
}

function main(argv) {
  const args = parseArgs(argv.slice(2));
  if (args.selfTest) process.exit(selfTest());
  if (!args.envFile || args.scan.length === 0) {
    process.stderr.write('usage: check-e2e-artifact-secrets.js --env-file <path> [--issued-file <path>] --scan <dir> [--scan <dir> …] | --self-test\n');
    process.exit(2);
  }
  const credentials = readSecrets(fs.readFileSync(args.envFile, 'utf8'));
  const issued = loadIssued(args.issuedFile);
  const secrets = [...credentials, ...issued];
  const r = scan(args.scan, secrets);
  if (r.files === 0) {
    process.stderr.write('✗ 走査したファイルが 0 件（成果物が無いのに「載っていない」としない）\n');
    process.exit(1);
  }
  if (r.hits.length) {
    process.stderr.write(`✗ 成果物に試験アカウントの資格情報・発行値が載っている（${r.hits.length} 件。値は出さない）:\n`);
    for (const h of r.hits) process.stderr.write(`  - ${h.where}: ${h.name}（${h.how}）\n`);
    process.exit(1);
  }
  process.stdout.write(
    `✓ 成果物 ${r.files} ファイル（中の zip を含む）に資格情報 ${credentials.length} 個・発行値 ${issued.length} 個の値は載っていない\n`,
  );
}

if (require.main === module) {
  try { main(process.argv); } catch (e) {
    process.stderr.write(`✗ check-e2e-artifact-secrets: ${e.message || e}\n`);
    process.exit(1);
  }
}

module.exports = { readSecrets, readIssued, loadIssued, middles, formsOf, unzip, findIn, scan, makeZip, selfTest };
