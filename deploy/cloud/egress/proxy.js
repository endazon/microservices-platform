#!/usr/bin/env node
'use strict';
/*
 * NFR-18, IADR-0703: クラウドの基盤の外向きの中継（HTTPS の CONNECT だけを、名前の許可表に在る宛先の 443 へだけ通す）。
 *
 * クラスタの外の 443 へ出る Pod（LLM ゲートウェイ・バックアップ・Alertmanager）は、直に外へ出ず、この中継を HTTPS_PROXY として使う
 * （NetworkPolicy はこの中継の Pod だけに外の 443 を許す。deploy/cloud/egress/egress-proxy.yaml）。
 *
 *   - 受けるのは CONNECT <名前>:443 だけ。ほかの方法・ポート・IP の字面の宛先は 403 / 405（TLS は端から端まで。中継は中身を読まない）。
 *   - 許可表（名前の完全一致。小文字）は 3 つのファイルの和:
 *       1. 宣言の表（ConfigMap egress-proxy-hosts の hosts。設定に在る宛先: LLM の提供元・Discord）
 *       2. オーナーが埋める表（ConfigMap egress-proxy-extra-hosts の hosts。無くてもよい。heartbeat の宛先など設定に無いもの）
 *       3. バックアップの保管先（ConfigMap platform-backup-s3-target の endpoint の URL の名前。無くてもよい）
 *     30 秒ごとに読み直す（ConfigMap の差し替えで Pod を作り直さなくてよい）。
 *   - 段（EGRESS_PROXY_MODE）: enforce は表に無い宛先を 403。report は通して `report:` の行を出す（移す間だけ。手順書）。
 *   - どちらの段でも、名前を引いた先が私設・ループバック・リンクローカル・CGNAT の範囲なら 403（クラスタの中へ回り込ませない）。
 *   - ログに出すのは判定・宛先の名前・ポートだけ（中身・要求の頭は出さない）。/metrics は判定ごとの件数（別のポート）。
 *
 * node:22-alpine が実行する（外部依存なし。Node 標準の http・net・dns・fs）。試験: deploy/cloud/egress/proxy.test.js。
 */
const http = require('http');
const net = require('net');
const dns = require('dns');
const fs = require('fs');

const HOSTNAME_FORM = /^(?=.{1,253}$)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$/;
/** DNS の名前（点を 1 つ以上含む）。IP の字面は名前として扱わない。 */
const isHostname = (h) => HOSTNAME_FORM.test(h) && !net.isIP(h);

/** 名前の行の表（# 以降は注記・空行は飛ばす）。不正な行は捨てる（表を広げない）。 */
function parseHosts(text) {
  return String(text || '').split('\n').map((l) => l.replace(/#.*/, '').trim().toLowerCase()).filter(isHostname);
}

/** バックアップの保管先の endpoint（https://<名前>[:443]）から名前を取る。https でない・443 でないものは表に入れない。 */
function hostOfEndpoint(text) {
  const s = String(text || '').trim();
  if (!s) return [];
  try {
    const u = new URL(s);
    if (u.protocol !== 'https:' || (u.port && u.port !== '443')) return [];
    const h = u.hostname.toLowerCase();
    return isHostname(h) ? [h] : [];
  } catch {
    return [];
  }
}

/** 私設・ループバック・リンクローカル・CGNAT・未指定・多重送信の範囲（IPv4・IPv6）。 */
function isPrivateAddress(ip) {
  if (net.isIPv4(ip)) {
    const [a, b] = ip.split('.').map(Number);
    return a === 0 || a === 10 || a === 127 || (a === 169 && b === 254) || (a === 172 && b >= 16 && b <= 31)
      || (a === 192 && b === 168) || (a === 100 && b >= 64 && b <= 127) || a >= 224;
  }
  if (net.isIPv6(ip)) {
    const v = ip.toLowerCase();
    const mapped = /^::ffff:(\d+\.\d+\.\d+\.\d+)$/.exec(v);
    if (mapped) return isPrivateAddress(mapped[1]);
    return v === '::' || v === '::1' || /^f[cd]/.test(v) || /^fe[89ab]/.test(v) || /^ff/.test(v);
  }
  return true;
}

/** CONNECT の宛先（"<名前>:<ポート>"）を読む。読めなければ null。 */
function parseTarget(authority) {
  const m = /^([^:\s]+):(\d{1,5})$/.exec(String(authority || ''));
  if (!m) return null;
  return { host: m[1].toLowerCase().replace(/\.$/, ''), port: Number(m[2]) };
}

/**
 * 宛先の判定（名前を引く前）。戻り値は { decision: 'allow' | 'report' | 'deny', reason }。
 *   allow: 表に在る。report: 表に無いが report の段なので通す。deny: 通さない。
 */
function decide(target, allowSet, mode) {
  if (!target) return { decision: 'deny', reason: '宛先の形が違う' };
  if (target.port !== 443) return { decision: 'deny', reason: '443 でない' };
  if (net.isIP(target.host)) return { decision: 'deny', reason: 'IP の字面の宛先' };
  if (!isHostname(target.host)) return { decision: 'deny', reason: '名前の形が違う' };
  if (allowSet.has(target.host)) return { decision: 'allow', reason: '許可表に在る' };
  if (mode === 'report') return { decision: 'report', reason: '許可表に無い（report の段なので通した）' };
  return { decision: 'deny', reason: '許可表に無い' };
}

function readAllowFiles(files) {
  const out = new Set();
  for (const { path: p, kind } of files) {
    let text = '';
    try { text = fs.readFileSync(p, 'utf8'); } catch { continue; }
    for (const h of (kind === 'endpoint' ? hostOfEndpoint(text) : parseHosts(text))) out.add(h);
  }
  return out;
}

/**
 * 中継を作る。opts:
 *   mode: 'enforce' | 'report'
 *   allow: () => Set<string>（許可表）
 *   lookup: (host) => Promise<string[]>（名前を引く。既定は dns.promises.lookup の全件）
 *   connect: (address, port) => net.Socket（外へ繋ぐ。既定は net.connect）
 *   log: (line) => void
 *   connectTimeoutMs: 番地ごとに繋がるのを待つ上限（既定 10 秒。過ぎたら次の番地へ）
 */
function createProxy(opts) {
  const mode = opts.mode === 'report' ? 'report' : 'enforce';
  const lookup = opts.lookup || (async (h) => (await dns.promises.lookup(h, { all: true, verbatim: true })).map((x) => x.address));
  const connect = opts.connect || ((address, port) => net.connect({ host: address, port }));
  const log = opts.log || ((l) => console.log(l));
  const connectTimeoutMs = opts.connectTimeoutMs || 10 * 1000;
  const counts = { allow: 0, report: 0, deny: 0 };
  const server = http.createServer((req, res) => {
    counts.deny += 1;
    log(`deny: ${req.method} は受けない（CONNECT だけ）`);
    res.writeHead(405, { 'content-type': 'text/plain; charset=utf-8', connection: 'close' });
    res.end('CONNECT only\n');
  });
  server.on('connect', async (req, client, head) => {
    const target = parseTarget(req.url);
    const refuse = (code, why) => {
      counts.deny += 1;
      log(`deny: ${target ? `${target.host}:${target.port}` : '(読めない宛先)'} ${why}`);
      client.end(`HTTP/1.1 ${code} ${code === 403 ? 'Forbidden' : 'Bad Gateway'}\r\nconnection: close\r\n\r\n`);
    };
    client.on('error', () => {});
    const d = decide(target, opts.allow(), mode);
    if (d.decision === 'deny') { refuse(403, d.reason); return; }
    let addrs;
    try {
      addrs = await lookup(target.host);
    } catch {
      refuse(502, '名前を引けない');
      return;
    }
    if (!addrs.length || addrs.some(isPrivateAddress)) { refuse(403, '引いた先が私設・ループバック・リンクローカルの範囲'); return; }
    // 名前を引く間にクライアントが閉じていたら、外へ繋がない（http の受け口の socket は半開きを許すので、読みの終わりも見る）。
    const gone = () => client.destroyed || client.readableEnded;
    if (gone()) { client.destroy(); return; }
    counts[d.decision] += 1;
    log(`${d.decision}: ${target.host}:${target.port}${d.decision === 'report' ? ` ${d.reason}` : ''}`);
    client.setTimeout(10 * 60 * 1000, () => client.destroy());
    let upstream = null;
    client.on('close', () => { if (upstream) upstream.destroy(); });
    // 引いた番地（どれも私設の範囲の外であることは上で確かめた）を順に試す。IPv6 が先に返り、ノードに IPv6 の経路が無くても、
    // IPv4 へ繋がるように。繋がる前の失敗・応答の無さ（connectTimeoutMs）だけを次へ回し、繋がった後の失敗はそのまま閉じる。
    const attempt = (i) => {
      if (gone()) { client.destroy(); return; }
      if (i >= addrs.length) { refuse(502, `外へ繋がらない（${addrs.length} 件の番地のどれにも）`); return; }
      const sock = connect(addrs[i], target.port);
      upstream = sock;
      let state = 'trying'; // trying → connected / skipped
      const next = () => {
        if (state !== 'trying') return;
        state = 'skipped';
        sock.destroy();
        attempt(i + 1);
      };
      sock.setTimeout(connectTimeoutMs, next);
      sock.on('error', () => { if (state === 'connected') client.destroy(); else next(); });
      sock.once('connect', () => {
        if (state !== 'trying') { sock.destroy(); return; }
        state = 'connected';
        if (gone()) { sock.destroy(); client.destroy(); return; }
        sock.setTimeout(10 * 60 * 1000, () => sock.destroy());
        client.write('HTTP/1.1 200 Connection Established\r\n\r\n');
        if (head && head.length) sock.write(head);
        sock.pipe(client);
        client.pipe(sock);
      });
    };
    attempt(0);
  });
  server.maxConnections = 256;
  const metrics = () => [
    '# HELP egress_proxy_requests_total 外向きの中継の判定ごとの件数（allow・report・deny）。',
    '# TYPE egress_proxy_requests_total counter',
    ...Object.entries(counts).map(([k, v]) => `egress_proxy_requests_total{decision="${k}"} ${v}`),
    '# HELP egress_proxy_enforcing 段が enforce なら 1、report なら 0。',
    '# TYPE egress_proxy_enforcing gauge',
    `egress_proxy_enforcing ${mode === 'enforce' ? 1 : 0}`,
    '',
  ].join('\n');
  return { server, counts, metrics, mode };
}

function main(env = process.env) {
  const files = [
    { path: env.EGRESS_PROXY_HOSTS || '/etc/egress-proxy/hosts/hosts', kind: 'hosts' },
    { path: env.EGRESS_PROXY_EXTRA_HOSTS || '/etc/egress-proxy/extra/hosts', kind: 'hosts' },
    { path: env.EGRESS_PROXY_BACKUP_ENDPOINT || '/etc/egress-proxy/backup/endpoint', kind: 'endpoint' },
  ];
  const mode = env.EGRESS_PROXY_MODE;
  if (mode !== 'enforce' && mode !== 'report') {
    console.log(`ERROR: EGRESS_PROXY_MODE は enforce か report（${mode === undefined ? '未指定' : '不正な値'}）`);
    process.exit(2);
  }
  let allow = readAllowFiles(files);
  let readAt = Date.now();
  const p = createProxy({
    mode,
    allow: () => {
      if (Date.now() - readAt > 30000) { allow = readAllowFiles(files); readAt = Date.now(); }
      return allow;
    },
  });
  console.log(`egress-proxy: 段 ${mode}・許可表 ${allow.size} 件（${[...allow].sort().join(', ')}）`);
  p.server.listen(Number(env.EGRESS_PROXY_PORT || 3128));
  http.createServer((req, res) => {
    if (req.url === '/metrics') { res.writeHead(200, { 'content-type': 'text/plain; version=0.0.4' }); res.end(p.metrics()); return; }
    if (req.url === '/healthz') { res.writeHead(200); res.end('ok\n'); return; }
    res.writeHead(404); res.end();
  }).listen(Number(env.EGRESS_PROXY_METRICS_PORT || 9464));
}

module.exports = { parseHosts, hostOfEndpoint, isPrivateAddress, parseTarget, decide, readAllowFiles, createProxy };
if (require.main === module) main();
