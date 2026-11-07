#!/usr/bin/env node
'use strict';
/*
 * NFR-18, IADR-0705: 外向きの中継（proxy.js）の試験。外へは出ない —— 名前を引く関数と外へ繋ぐ関数を差し替え、手元の echo の受け口へ繋ぐ。
 * scripts/deploy-cloud-egress-audit.test.js が子のプロセスで走らせる（node deploy/cloud/egress/proxy.test.js でも）。
 *
 *   E1 enforce: 許可表の宛先の 443 だけを通し（200 のあと端から端まで流れる）、表に無い宛先・443 でない・IP の字面・CONNECT でない方法は拒む。
 *   E2 report: 表に無い宛先も通して `report:` の行を出す（移す間）。443 でない・IP の字面は report でも拒む。
 *   E3 どちらの段でも、引いた先が私設・ループバック・リンクローカル・CGNAT の範囲なら拒む（クラスタの中へ回り込ませない）。
 *   E4 許可表は宣言・オーナーの表・バックアップの endpoint の和で、不正な行・https でない endpoint は入れない。
 *   E5 件数（/metrics）は判定ごとに数え、段を出す。
 *   E7 report の段に入った日時（ConfigMap egress-proxy-report-since）を読み、report の段だけ計器に出す。無い・形が違う・先の日時は 0。
 *   E6 引いた番地を順に試す（1 件目が繋がらない・応答しないとき 2 件目へ。全部だめなら 502）。名前を引く間に閉じたクライアントのためには繋がない。
 */
const assert = require('assert');
const fs = require('fs');
const net = require('net');
const os = require('os');
const path = require('path');
const p = require('./proxy.js');

let failed = 0;
const tests = [];
const ok = (name, fn) => tests.push([name, fn]);

/** 手元の echo の受け口（外の宛先の代わり）。 */
function echoServer() {
  return new Promise((resolve) => {
    const s = net.createServer((sock) => sock.pipe(sock));
    s.listen(0, '127.0.0.1', () => resolve(s));
  });
}

/** 中継へ CONNECT を送り、応答の 1 行目と、200 なら echo の往復の結果を返す。 */
function connectVia(port, authority, { method = 'CONNECT' } = {}) {
  return new Promise((resolve, reject) => {
    const sock = net.connect(port, '127.0.0.1', () => {
      sock.write(method === 'CONNECT' ? `CONNECT ${authority} HTTP/1.1\r\nHost: ${authority}\r\n\r\n` : `${method} http://${authority}/ HTTP/1.1\r\nHost: ${authority}\r\n\r\n`);
    });
    let buf = '';
    let sent = false;
    sock.on('data', (d) => {
      buf += d.toString();
      const status = buf.split('\r\n')[0];
      if (/ 200 /.test(status) && !sent && buf.includes('\r\n\r\n')) {
        sent = true;
        buf = '';
        sock.write('ping-through-tunnel');
        return;
      }
      if (sent && buf.includes('ping-through-tunnel')) { sock.destroy(); resolve({ status: 200, echoed: true }); }
    });
    sock.on('end', () => resolve({ status: Number((/^HTTP\/1\.1 (\d{3})/.exec(buf) || [])[1] || 0), echoed: false }));
    sock.on('error', reject);
    setTimeout(() => { sock.destroy(); resolve({ status: Number((/^HTTP\/1\.1 (\d{3})/.exec(buf) || [])[1] || 0), echoed: false, timeout: true }); }, 3000);
  });
}

/** 試す番地ごとに繋ぎ先を変える中継（connect の呼ばれ方を記録する）。plan[addr] = 'echo' | 'refused' | 'blackhole'。 */
async function withPlannedProxy(addrs, plan, fn, { lookupDelayMs = 0 } = {}) {
  const echo = await echoServer();
  const tried = [];
  const closed = await new Promise((r) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const port = s.address().port; s.close(() => r(port)); }); });
  const prox = p.createProxy({
    mode: 'enforce',
    allow: () => new Set(['api.anthropic.com']),
    lookup: async () => { if (lookupDelayMs) await new Promise((r) => setTimeout(r, lookupDelayMs)); return addrs; },
    connectTimeoutMs: 300,
    connect: (addr) => {
      tried.push(addr);
      if (plan[addr] === 'echo') return net.connect(echo.address().port, '127.0.0.1');
      if (plan[addr] === 'refused') return net.connect(closed, '127.0.0.1');
      // blackhole: 繋がりの応答が返らない（経路の無い番地の代わり）。connect を出さない socket。
      return new net.Socket();
    },
    log: () => {},
  });
  await new Promise((r) => prox.server.listen(0, '127.0.0.1', r));
  try {
    return await fn(prox.server.address().port, tried);
  } finally {
    prox.server.close();
    echo.close();
  }
}

async function withProxy(mode, allow, resolved, fn) {
  const echo = await echoServer();
  const lines = [];
  const prox = p.createProxy({
    mode,
    allow: () => new Set(allow),
    lookup: async (h) => resolved[h] || ['203.0.113.10'],
    // 外の宛先（公開の範囲の番地）の代わりに、手元の echo へ繋ぐ。
    connect: () => net.connect(echo.address().port, '127.0.0.1'),
    log: (l) => lines.push(l),
  });
  await new Promise((r) => prox.server.listen(0, '127.0.0.1', r));
  try {
    return await fn(prox.server.address().port, lines, prox);
  } finally {
    prox.server.close();
    echo.close();
  }
}

ok('E1 enforce: 許可表の宛先の 443 だけを通し、表に無い宛先・443 でない・IP の字面・CONNECT でない方法は拒む', async () => {
  await withProxy('enforce', ['api.anthropic.com'], {}, async (port, lines) => {
    assert.deepStrictEqual(await connectVia(port, 'api.anthropic.com:443'), { status: 200, echoed: true });
    assert.strictEqual((await connectVia(port, 'example.org:443')).status, 403, '表に無い宛先を通した');
    assert.strictEqual((await connectVia(port, 'api.anthropic.com:80')).status, 403, '443 でないポートを通した');
    assert.strictEqual((await connectVia(port, '203.0.113.10:443')).status, 403, 'IP の字面を通した');
    assert.strictEqual((await connectVia(port, 'api.anthropic.com', { method: 'GET' })).status, 405, 'CONNECT でない方法を受けた');
    assert.ok(lines.some((l) => /^allow: api\.anthropic\.com:443$/.test(l)), lines.join('\n'));
    assert.ok(lines.some((l) => /^deny: example\.org:443 許可表に無い$/.test(l)), lines.join('\n'));
  });
});

ok('E2 report: 表に無い宛先も通して report の行を出す。443 でない・IP の字面は report でも拒む', async () => {
  await withProxy('report', ['api.anthropic.com'], {}, async (port, lines) => {
    assert.deepStrictEqual(await connectVia(port, 'hc-ping.com:443'), { status: 200, echoed: true });
    assert.ok(lines.some((l) => /^report: hc-ping\.com:443 /.test(l)), lines.join('\n'));
    assert.strictEqual((await connectVia(port, 'hc-ping.com:8443')).status, 403);
    assert.strictEqual((await connectVia(port, '[2001:db8::1]:443')).status, 403);
  });
});

ok('E3 どちらの段でも、引いた先が私設・ループバック・リンクローカル・CGNAT なら拒む', async () => {
  for (const mode of ['enforce', 'report']) {
    for (const addr of ['10.43.0.1', '127.0.0.1', '169.254.169.254', '100.64.0.1', '172.16.0.5', '192.168.1.1', '::1', 'fd00::1', '::ffff:10.0.0.1']) {
      await withProxy(mode, ['evil.example.com'], { 'evil.example.com': ['203.0.113.10', addr] }, async (port) => {
        assert.strictEqual((await connectVia(port, 'evil.example.com:443')).status, 403, `${mode}: ${addr} へ回り込んだ`);
      });
    }
  }
  for (const pub of ['203.0.113.10', '8.8.8.8', '2606:4700::1111']) assert.strictEqual(p.isPrivateAddress(pub), false, pub);
});

ok('E4 許可表は 3 つの和で、不正な行・https でない・443 でない endpoint は入れない', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'egress-proxy-'));
  try {
    fs.writeFileSync(path.join(dir, 'hosts'), '# 宣言\napi.anthropic.com\nDiscord.com  # 大文字は小文字へ\n\nbad host\n*.wild.example\n10.0.0.1\n');
    fs.writeFileSync(path.join(dir, 'extra'), 'hc-ping.com\n');
    fs.writeFileSync(path.join(dir, 'endpoint'), 'https://fsn1.your-objectstorage.com\n');
    const set = p.readAllowFiles([
      { path: path.join(dir, 'hosts'), kind: 'hosts' },
      { path: path.join(dir, 'extra'), kind: 'hosts' },
      { path: path.join(dir, 'endpoint'), kind: 'endpoint' },
      { path: path.join(dir, 'missing'), kind: 'hosts' },
    ]);
    assert.deepStrictEqual([...set].sort(), ['api.anthropic.com', 'discord.com', 'fsn1.your-objectstorage.com', 'hc-ping.com']);
    assert.deepStrictEqual(p.hostOfEndpoint('http://fsn1.your-objectstorage.com'), []);
    assert.deepStrictEqual(p.hostOfEndpoint('https://fsn1.your-objectstorage.com:9000'), []);
    assert.deepStrictEqual(p.hostOfEndpoint('https://10.0.0.1'), []);
    assert.deepStrictEqual(p.hostOfEndpoint(''), []);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

ok('E5 件数は判定ごとに数え、段を出す', async () => {
  await withProxy('enforce', ['api.anthropic.com'], {}, async (port, _lines, prox) => {
    await connectVia(port, 'api.anthropic.com:443');
    await connectVia(port, 'example.org:443');
    const m = prox.metrics();
    assert.match(m, /egress_proxy_requests_total\{decision="allow"\} 1/);
    assert.match(m, /egress_proxy_requests_total\{decision="deny"\} 1/);
    assert.match(m, /egress_proxy_enforcing 1/);
  });
});

ok('E6 引いた番地を順に試す: 1 件目が拒む・応答しないとき 2 件目で繋がる。全部だめなら 502。閉じたクライアントのためには繋がない', async () => {
  // IPv6 が先に返り、ノードに IPv6 の経路が無い（拒まれる）場合
  await withPlannedProxy(['2001:db8::10', '203.0.113.10'], { '2001:db8::10': 'refused', '203.0.113.10': 'echo' }, async (port, tried) => {
    assert.deepStrictEqual(await connectVia(port, 'api.anthropic.com:443'), { status: 200, echoed: true });
    assert.deepStrictEqual(tried, ['2001:db8::10', '203.0.113.10']);
  });
  // 1 件目が応答しない（経路の先で捨てられる）場合も、待つ上限の後に 2 件目へ
  await withPlannedProxy(['2001:db8::10', '203.0.113.10'], { '2001:db8::10': 'blackhole', '203.0.113.10': 'echo' }, async (port, tried) => {
    assert.deepStrictEqual(await connectVia(port, 'api.anthropic.com:443'), { status: 200, echoed: true });
    assert.deepStrictEqual(tried, ['2001:db8::10', '203.0.113.10']);
  });
  // 陰性対照: 1 件目しか試さない形なら繋がらない（全部だめなら 502）
  await withPlannedProxy(['2001:db8::10', '203.0.113.11'], { '2001:db8::10': 'refused', '203.0.113.11': 'refused' }, async (port, tried) => {
    assert.strictEqual((await connectVia(port, 'api.anthropic.com:443')).status, 502);
    assert.deepStrictEqual(tried, ['2001:db8::10', '203.0.113.11']);
  });
  // 名前を引く間にクライアントが閉じたら、外へ繋がない
  await withPlannedProxy(['203.0.113.10'], { '203.0.113.10': 'echo' }, async (port, tried) => {
    await new Promise((resolve) => {
      const sock = net.connect(port, '127.0.0.1', () => {
        sock.write('CONNECT api.anthropic.com:443 HTTP/1.1\r\nHost: api.anthropic.com:443\r\n\r\n');
        setTimeout(() => sock.destroy(), 50);
      });
      sock.on('error', () => {});
      sock.on('close', resolve);
    });
    await new Promise((r) => setTimeout(r, 400));
    assert.deepStrictEqual(tried, [], '閉じたクライアントのために外へ繋いだ');
  }, { lookupDelayMs: 200 });
});

ok('E7 report の段に入った日時: RFC 3339 の UTC を読んで report の段だけ出す。無い・形が違う・先の日時は 0（警報が鳴る側）', () => {
  const d = fs.mkdtempSync(path.join(os.tmpdir(), 'egress-since-'));
  try {
    const f = path.join(d, 'since');
    const now = Date.parse('2026-11-08T00:00:00Z');
    fs.writeFileSync(f, '2026-11-01T09:30:00Z\n');
    assert.strictEqual(p.readReportSince(f, now), Date.parse('2026-11-01T09:30:00Z') / 1000);
    assert.strictEqual(p.readReportSince(path.join(d, 'none'), now), 0, '無いのに 0 でない');
    for (const bad of ['', 'yesterday', '2026-11-01', '2026-11-01T09:30:00+09:00', '1730000000']) {
      fs.writeFileSync(f, bad);
      assert.strictEqual(p.readReportSince(f, now), 0, `形の違う ${JSON.stringify(bad)} を読んだ`);
    }
    fs.writeFileSync(f, '2026-11-10T00:00:00Z');
    assert.strictEqual(p.readReportSince(f, now), 0, '先の日時を読んだ（警報を先へ延ばせる）');
    const rep = p.createProxy({ mode: 'report', allow: () => new Set(), reportSince: () => 1730000000, log: () => {} }).metrics();
    assert.match(rep, /^egress_proxy_report_since_timestamp_seconds 1730000000$/m);
    const missing = p.createProxy({ mode: 'report', allow: () => new Set(), log: () => {} }).metrics();
    assert.match(missing, /^egress_proxy_report_since_timestamp_seconds 0$/m, '日時の関数が無いとき 0 を出さない');
    const enf = p.createProxy({ mode: 'enforce', allow: () => new Set(), reportSince: () => 1730000000, log: () => {} }).metrics();
    assert.ok(!/egress_proxy_report_since_timestamp_seconds/.test(enf), 'enforce の段で日時を出した');
  } finally { fs.rmSync(d, { recursive: true, force: true }); }
});

ok('陰性対照: 判定の関数は、表を空にすると enforce で通さず、report で通す（判定が空回りしていない）', () => {
  const t = p.parseTarget('api.anthropic.com:443');
  assert.strictEqual(p.decide(t, new Set(), 'enforce').decision, 'deny');
  assert.strictEqual(p.decide(t, new Set(), 'report').decision, 'report');
  assert.strictEqual(p.decide(t, new Set(['api.anthropic.com']), 'enforce').decision, 'allow');
});

(async () => {
  for (const [name, fn] of tests) {
    try {
      await fn();
      console.log(`  ok  ${name}`);
    } catch (e) {
      failed += 1;
      console.log(`  NG  ${name}\n${e.stack || e.message}`);
    }
  }
  if (failed) { console.error(`✗ ${failed} 件が失敗した`); process.exit(1); }
  console.log('✓ 外向きの中継の試験');
})();
