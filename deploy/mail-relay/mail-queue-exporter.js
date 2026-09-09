#!/usr/bin/env node
'use strict';
/*
 * SC-10, SC-15, FR-05, NFR-09, ADR-0006 / ADR-0045 決定 8（ADR-0078 決定 3 による部分改定）,
 * IADR-0404 フォローアップ (1), IADR-0421 (#1245 PR-B):
 * **近接 MTA（Postfix）のキューを読み、Prometheus 形式で出す最小の exporter。**
 *
 *   mail-relay Pod の**サイドカー**として常駐する（deploy/mail-relay/mail-relay.yaml）。
 *   node:22-alpine・**外部依存ゼロ**（Node 標準の fs / http のみ）。門（reset-gate.js）と同じ作法である。
 *
 * ## なぜ要るか —— 近接 MTA を挟んだ時点で Keycloak 側の観測は上流停止を見なくなった
 *
 * ADR-0078 決定 3 は「上流停止の観測点を**近接 MTA のキュー**へ移す」と定めた。移さなければ、
 * 決定 2（近接 MTA を挟む）が**観測を消す** —— relay が 250 を返した以後の失敗は Keycloak に
 * 一切戻らないため、Keycloak の監査ログだけを見ていると上流の停止を見逃す。
 *
 * ## なぜ既製の exporter を持ち込まないか（IADR-0421 決定 1）
 *
 * `kumina/postfix_exporter` 系はキュー長を **showq の UNIX ソケット**から、失敗率を
 * **Postfix のログファイル**から採る。前者は本 exporter と同じ spool の共有が要り、後者は
 * この image が rsyslog 経由で stdout へ出す（ログファイルが無い）ので**そもそも読めない**。
 * 得られる面が増えないのに**供給網へイメージが 1 つ増える**（08_data-egress-policy の統制対象）。
 * **既に居るイメージ（node:22-alpine）とファイル読みだけで足りる**ので増やさない。
 *
 * ## 🔴 滞留時間を「ファイルの mtime」から採らない
 *
 * Postfix の qmgr は、メッセージを deferred へ落とすとき**キューファイルの mtime を
 * 次回配送予定時刻（＝未来）へ書き換える**。mtime から齢を採ると **deferred だけ負の値**になる。
 * 代わりに**キュー ID から到着時刻を復号する**（`enable_long_queue_ids` の既定 yes・本 image は変えない）。
 *
 *   長形式のキュー ID = <秒 base52・最小 6 桁><マイクロ秒 base52・4 桁>'z'<inode base51>
 *   （Postfix `src/global/mail_queue.h` の MQID_* と `safe_ultostr.c` の安全アルファベット）
 *   inode 部は **base51 なので 'z' を含まない** → **最後の 'z'** が区切りである（本家も strrchr）。
 *
 * ## 🔴 「測っていない」を 0 で出さない
 *
 * 読めなかったキューの系列は**出さない**（0 を出すと画面上「滞留なし」に見える）。
 * 併せて `postfix_up 0` を出す。系列の不在は `MailRelayQueueSeriesAbsent` が拾う ——
 * #1110 / #1246 が繰り返し踏んだ「沈黙が正常に見える」形を作らない。
 *
 * ## 値の既定を持たない
 *
 * spool の位置・待受ポート・対象キューは**マニフェストが与える**（reset-gate.js と同じ作法）。
 * 未設定なら**起動しない**。
 *
 * 使い方:
 *   node mail-queue-exporter.js              # HTTP で :$LISTEN_PORT/metrics を出す（配備時）
 *   node mail-queue-exporter.js --self-test  # 純関数の自己試験（外部 I/O は一切しない）
 */

const fs = require('fs');
const http = require('http');
const path = require('path');

// ---------------------------------------------------------------- キュー ID の復号（純粋）

/**
 * Postfix の「安全アルファベット」（`src/global/safe_ultostr.c` の `safe_chars`）。
 * 母音と紛らわしい字を除いた 52 文字。**並びが 1 文字でも違うと到着時刻が狂う**ので写経しない
 * ——上流のソースからそのまま持ってきた値である。
 */
const SAFE_CHARS = '0123456789BCDFGHJKLMNPQRSTVWXYZbcdfghjklmnpqrstvwxyz';
const SAFE_INDEX = new Map([...SAFE_CHARS].map((c, i) => [c, i]));

/** 長形式キュー ID の時刻部の最小長（MQID_LG_SEC_PAD 6 ＋ MQID_LG_USEC_PAD 4）。 */
const MQID_TIME_PAD = 10;
/** マイクロ秒部の桁数（MQID_LG_USEC_PAD）。 */
const MQID_USEC_PAD = 4;
/** 時刻部の基数（MQID_LG_SEC_BASE / MQID_LG_USEC_BASE）。 */
const MQID_TIME_BASE = 52;
/** 時刻と inode の区切り（MQID_LG_INUM_SEP）。inode 部は base51 なのでこの字を含まない。 */
const MQID_INUM_SEP = 'z';

/** 安全アルファベットの文字列を数値へ戻す。未知の字が 1 つでもあれば null。 */
function safeStrToUint(str, base) {
  if (str.length === 0) return null;
  let acc = 0;
  for (const ch of str) {
    const v = SAFE_INDEX.get(ch);
    if (v === undefined || v >= base) return null;
    acc = acc * base + v;
  }
  return acc;
}

/**
 * 長形式のキュー ID から**到着時刻（エポック秒）**を取り出す。**純関数。**
 * 短形式（`enable_long_queue_ids=no`）は秒を持たないので復号できず null を返す。
 * @param {string} id キューファイルのファイル名
 * @returns {number|null}
 */
function queueIdArrivalSeconds(id) {
  const sep = id.lastIndexOf(MQID_INUM_SEP);
  if (sep < MQID_TIME_PAD) return null; // 区切りが無い／時刻部が短すぎる＝長形式ではない
  const secText = id.slice(0, sep - MQID_USEC_PAD);
  if (secText.length === 0) return null;
  return safeStrToUint(secText, MQID_TIME_BASE);
}

// ---------------------------------------------------------------- 走査（注入した I/O で試験する）

/**
 * 1 つのキューディレクトリを再帰的に読み、**メッセージ数**と**最も古い到着からの経過秒**を返す。
 *
 * 🔴 `deferred` は 1 段のハッシュ（`deferred/<16 進 1 文字>/<キュー ID>`）を持つので**再帰**する。
 *    `incoming` / `active` / `hold` / `maildrop` は平坦だが、同じ走査で両方を扱える。
 * 🔴 **数えるのは通常ファイルだけ**である。
 *
 * @param {string} dir 走査するディレクトリ
 * @param {number} nowSeconds 現在時刻（エポック秒）
 * @param {{readdir: (d: string) => {name: string, isDirectory: () => boolean, isFile: () => boolean}[]}} io
 * @returns {{size: number, oldestAgeSeconds: number}} 読めなければ throw する
 */
function readQueue(dir, nowSeconds, io) {
  let size = 0;
  let oldestArrival = null;
  let unparsable = 0;

  const walk = (d) => {
    for (const entry of io.readdir(d)) {
      if (entry.isDirectory()) {
        walk(path.posix.join(d, entry.name));
        continue;
      }
      if (!entry.isFile()) continue;
      size += 1;
      const arrival = queueIdArrivalSeconds(entry.name);
      // 🔴 復号できない／未来／異常に古い ID は**齢の材料にしない**（推測で古い値を作らない）。
      if (arrival === null || arrival > nowSeconds + 60 || nowSeconds - arrival > 400 * 86400) {
        unparsable += 1;
        continue;
      }
      if (oldestArrival === null || arrival < oldestArrival) oldestArrival = arrival;
    }
  };
  walk(dir);

  // 🔴 1 件でも復号できないと「最古」は最古でなくなる。**嘘の齢を出すより測らない**（系列を欠かす）。
  if (unparsable > 0) {
    throw new Error(`キュー ID を復号できないファイルが ${unparsable} 件ある（enable_long_queue_ids の変更を疑う）`);
  }
  return { size, oldestAgeSeconds: oldestArrival === null ? 0 : nowSeconds - oldestArrival };
}

// ---------------------------------------------------------------- 出力（純粋）

/**
 * Prometheus のテキスト形式へ組み立てる。**純関数。**
 *
 * 🔴 読めなかったキューの系列は**出さない**（0 を出さない）。`postfix_up` だけが 0 になる。
 * @param {{queue: string, size?: number, oldestAgeSeconds?: number, error?: string}[]} results
 */
function renderMetrics(results) {
  const ok = results.filter((r) => !r.error);
  const lines = [];
  lines.push('# HELP postfix_up 近接 MTA の spool を読めたか（1 = 全キューを読めた）');
  lines.push('# TYPE postfix_up gauge');
  lines.push(`postfix_up ${ok.length === results.length && results.length > 0 ? 1 : 0}`);
  lines.push('# HELP postfix_queue_size キューに滞留しているメッセージ数');
  lines.push('# TYPE postfix_queue_size gauge');
  for (const r of ok) lines.push(`postfix_queue_size{queue="${r.queue}"} ${r.size}`);
  lines.push('# HELP postfix_queue_oldest_message_age_seconds 最も古いメッセージの到着からの経過秒（空なら 0）');
  lines.push('# TYPE postfix_queue_oldest_message_age_seconds gauge');
  for (const r of ok) lines.push(`postfix_queue_oldest_message_age_seconds{queue="${r.queue}"} ${r.oldestAgeSeconds}`);
  return `${lines.join('\n')}\n`;
}

/** 必須の環境変数を読む。**既定値を持たない**（未設定なら起動しない）。 */
function readConfig(env) {
  const need = (name) => {
    const v = env[name];
    if (v === undefined || String(v).trim() === '') {
      throw new Error(`環境変数 ${name} が未設定である（マニフェストが与える。コードは既定を持たない）`);
    }
    return String(v).trim();
  };
  const queues = need('QUEUE_NAMES').split(',').map((s) => s.trim()).filter(Boolean);
  if (queues.length === 0) throw new Error('QUEUE_NAMES が空である');
  const port = Number(need('LISTEN_PORT'));
  if (!Number.isInteger(port) || port <= 0 || port > 65535) {
    throw new Error(`LISTEN_PORT が不正である（受け取った値: '${env.LISTEN_PORT}'）`);
  }
  return { spoolDir: need('SPOOL_DIR'), port, queues };
}

// ---------------------------------------------------------------- 実行

function scrape(cfg, nowSeconds, io) {
  return cfg.queues.map((queue) => {
    try {
      const { size, oldestAgeSeconds } = readQueue(path.posix.join(cfg.spoolDir, queue), nowSeconds, io);
      return { queue, size, oldestAgeSeconds };
    } catch (err) {
      return { queue, error: err.message };
    }
  });
}

function main() {
  const cfg = readConfig(process.env);
  const io = { readdir: (d) => fs.readdirSync(d, { withFileTypes: true }) };
  const server = http.createServer((req, res) => {
    if (req.url !== '/metrics') {
      res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' });
      res.end('not found\n');
      return;
    }
    const results = scrape(cfg, Math.floor(Date.now() / 1000), io);
    for (const r of results) {
      // 🔴 読めなかったことは**ログにも出す**。系列が欠けるだけだと配備直後は気付けない。
      if (r.error) console.error(`[mail-queue-exporter] キュー ${r.queue} を読めない: ${r.error}`);
    }
    res.writeHead(200, { 'content-type': 'text/plain; version=0.0.4; charset=utf-8' });
    res.end(renderMetrics(results));
  });
  server.listen(cfg.port, '0.0.0.0', () => {
    console.log(`[mail-queue-exporter] listening on :${cfg.port}/metrics spool=${cfg.spoolDir} queues=${cfg.queues.join(',')}`);
  });
}

// ---------------------------------------------------------------- 自己試験

function selfTest() {
  const assert = require('assert');
  let passed = 0;
  const t = (name, fn) => { fn(); passed++; process.stdout.write(`  ok  ${name}\n`); };

  /** 上流と同じ規則でキュー ID を作る（試験用。安全アルファベット・base52・最小 6 桁）。 */
  const encode = (val, base, padLen) => {
    let out = '';
    let v = val;
    while (v !== 0) { out = SAFE_CHARS[v % base] + out; v = Math.floor(v / base); }
    while (out.length < padLen) out = `0${out}`;
    return out;
  };
  const makeId = (sec) => `${encode(sec, 52, 6)}${encode(123, 52, 4)}z${encode(4242, 51, 0)}`;

  t('キュー ID から到着秒を復号できる（往復）', () => {
    for (const sec of [1, 1000, 1788912000, 1999999999]) {
      assert.strictEqual(queueIdArrivalSeconds(makeId(sec)), sec, `sec=${sec}`);
    }
  });

  t('区切りは**最後の** z である（時刻部に z が出ても壊れない）', () => {
    // 51 = 'z'。秒部の下位桁が 51 になる時刻を選ぶと、時刻部そのものに z が現れる。
    const sec = 52 * 3 + 51;
    const id = makeId(sec);
    assert.ok(id.slice(0, 10).includes('z'), `時刻部に z を含む前提が崩れた: ${id}`);
    assert.strictEqual(queueIdArrivalSeconds(id), sec);
  });

  t('短形式のキュー ID は復号しない（推測しない）', () => {
    assert.strictEqual(queueIdArrivalSeconds('9F3C21A4B'), null);
    assert.strictEqual(queueIdArrivalSeconds('z'), null);
  });

  t('安全アルファベットに無い字は復号しない（A / E / I / O / U は使われない）', () => {
    assert.strictEqual(safeStrToUint('A', 52), null);
    assert.strictEqual(safeStrToUint('0', 52), 0);
  });

  // --- 走査 -------------------------------------------------------------
  const now = 1788912000;
  const tree = {
    '/spool/deferred': [{ name: '0', dir: true }, { name: 'A', dir: true }],
    '/spool/deferred/0': [{ name: makeId(now - 600), dir: false }],
    '/spool/deferred/A': [{ name: makeId(now - 60), dir: false }],
    '/spool/active': [],
    '/spool/broken': [{ name: 'not-a-queue-id', dir: false }],
  };
  const io = {
    readdir: (d) => {
      if (!(d in tree)) throw new Error(`ENOENT: ${d}`);
      return tree[d].map((e) => ({ name: e.name, isDirectory: () => e.dir, isFile: () => !e.dir }));
    },
  };

  t('ハッシュされた deferred を再帰して数え、最古の齢を採る', () => {
    assert.deepStrictEqual(readQueue('/spool/deferred', now, io), { size: 2, oldestAgeSeconds: 600 });
  });

  t('空のキューは size 0・齢 0（読めているので系列は出す）', () => {
    assert.deepStrictEqual(readQueue('/spool/active', now, io), { size: 0, oldestAgeSeconds: 0 });
  });

  t('復号できない ID が混ざったら throw する（嘘の最古を出さない・変異試験）', () => {
    assert.throws(() => readQueue('/spool/broken', now, io), /復号できない/);
  });

  t('読めないディレクトリは throw する（0 を返さない・変異試験）', () => {
    assert.throws(() => readQueue('/spool/missing', now, io), /ENOENT/);
  });

  // --- 出力 -------------------------------------------------------------
  t('全キューを読めたら postfix_up 1 と 2 系列を出す', () => {
    const out = renderMetrics(scrape(
      { spoolDir: '/spool', queues: ['deferred', 'active'] }, now, io,
    ));
    assert.match(out, /^postfix_up 1$/m);
    assert.match(out, /^postfix_queue_size\{queue="deferred"\} 2$/m);
    assert.match(out, /^postfix_queue_oldest_message_age_seconds\{queue="deferred"\} 600$/m);
    assert.match(out, /^postfix_queue_size\{queue="active"\} 0$/m);
  });

  t('🔴 読めなかったキューは 0 を出さず系列ごと欠かす（#1110 / #1246 の形を作らない・変異試験）', () => {
    const out = renderMetrics(scrape(
      { spoolDir: '/spool', queues: ['deferred', 'missing'] }, now, io,
    ));
    assert.match(out, /^postfix_up 0$/m);
    assert.ok(!/queue="missing"/.test(out), `不在のキューに 0 を出している:\n${out}`);
    assert.match(out, /^postfix_queue_size\{queue="deferred"\} 2$/m);
  });

  // --- 構成 -------------------------------------------------------------
  t('環境変数が欠けたら起動しない（既定を持たない）', () => {
    assert.throws(() => readConfig({ LISTEN_PORT: '9154', QUEUE_NAMES: 'deferred' }), /SPOOL_DIR/);
    assert.throws(() => readConfig({ SPOOL_DIR: '/s', QUEUE_NAMES: 'deferred' }), /LISTEN_PORT/);
    assert.throws(() => readConfig({ SPOOL_DIR: '/s', LISTEN_PORT: '9154' }), /QUEUE_NAMES/);
    assert.throws(() => readConfig({ SPOOL_DIR: '/s', LISTEN_PORT: 'x', QUEUE_NAMES: 'a' }), /LISTEN_PORT/);
  });

  t('構成を読める', () => {
    assert.deepStrictEqual(
      readConfig({ SPOOL_DIR: '/var/spool/postfix', LISTEN_PORT: '9154', QUEUE_NAMES: 'incoming, deferred' }),
      { spoolDir: '/var/spool/postfix', port: 9154, queues: ['incoming', 'deferred'] },
    );
  });

  process.stdout.write(`\n✓ self-test: ${passed} 件すべて通過\n`);
}

if (require.main === module) {
  if (process.argv.includes('--self-test')) {
    selfTest();
  } else {
    try {
      main();
    } catch (e) {
      console.error(`[mail-queue-exporter] 起動できない: ${e && e.message ? e.message : e}`);
      process.exit(1);
    }
  }
}

module.exports = {
  SAFE_CHARS,
  queueIdArrivalSeconds,
  safeStrToUint,
  readQueue,
  renderMetrics,
  readConfig,
  scrape,
};
