#!/usr/bin/env node
'use strict';
/*
 * NFR-18, IADR-0705, IADR-0709: クラウドの基盤の外向きの中継（HTTPS の CONNECT だけを、名前の許可表に在る宛先の 443 へだけ通す）。
 *
 * クラスタの外の 443 へ出る Pod（LLM ゲートウェイ・バックアップ・Alertmanager）は、直に外へ出ず、この中継を HTTPS_PROXY として使う
 * （NetworkPolicy はこの中継の Pod だけに外の 443 を許す。deploy/cloud/egress/egress-proxy.yaml）。
 *
 *   - 受けるのは CONNECT <名前>:443 だけ。ほかの方法・ポート・IP の字面の宛先は 403 / 405（TLS は端から端まで。中継は中身を読まない）。
 *   - 許可表（名前の完全一致。小文字）は 3 つのファイルの和:
 *       1. 宣言の表（ConfigMap egress-proxy-hosts の hosts。設定に在る宛先: LLM の提供元・Discord）
 *       2. オーナーが埋める表（ConfigMap egress-proxy-extra-hosts の hosts。無くてもよい。heartbeat の宛先など設定に無いもの）
 *       3. バックアップの保管先（ConfigMap platform-backup-s3-target の endpoint の URL の名前。無くてもよい）
 *     最後に読んでから 30 秒を過ぎた後の最初の要求で読み直す（ConfigMap の差し替えで Pod を作り直さなくてよい。届くまでの kubelet の同期の間は別）。
 *   - 段（EGRESS_PROXY_MODE）: enforce は表に無い宛先を 403。report は通して `report:` の行を出す（移す間だけ。手順書）。
 *   - report の段に入った日時（ConfigMap egress-proxy-report-since の since。移す段を当てる前にオーナーが書く。RFC 3339 の UTC）を
 *     egress_proxy_report_since_timestamp_seconds に出す（report の段のときだけ）。Prometheus も中継も、再起動で数え直さないため（警報
 *     EgressProxyReportMode は「今 − この日時」で 14 日を見る）。無い・読めない・1 日より先の日時なら 0 を出す（＝とうに過ぎた扱いで鳴る側）。
 *   - どちらの段でも、名前を引いた先が私設・ループバック・リンクローカル・CGNAT の範囲なら 403（クラスタの中へ回り込ませない）。
 *   - IADR-0709: 許可表は**送り元ごと**に持つ。送り元の名乗りは「どの受け口（ポート）に来たか」で、各ポートへ届く Pod は
 *     NetworkPolicy（egress-proxy の入り）が名前空間 かつ Pod のラベルで 1 つに絞る（中継は要求の中身で名乗りを信じない）。
 *       - 受け口の一覧は env EGRESS_PROXY_SOURCES（"<送り元>:<ポート>" の空白区切り）。変えると Pod が作り直される。
 *       - 送り元ごとの表は ConfigMap egress-proxy-sources の hosts（"<送り元> <名前>[:<ポート>]"）と、オーナーの表の同じ形の行。
 *         バックアップの保管先（endpoint）は送り元 backup の行として足す。ポートを書かない行は 443。443 でないポートは名指した行だけ。
 *       - 段（EGRESS_PROXY_SOURCE_MODE）: report は「全体の表には在るが、その送り元の表に無い」宛先を通して `report:` の行と
 *         egress_proxy_source_mismatch_total を出す。enforce は 403。全体の表に無い宛先は従来の段（EGRESS_PROXY_MODE）で判定する。
 *       - バックアップの保管先は、rclone が仮想ホスト形式で書くなら "<バケット>.<endpoint の名前>" だけ（backupHosts。IADR-0709 決定 2）。
 *       - 送り元を名乗らない従来の口（EGRESS_PROXY_PORT。送り元 legacy）は、全体の表で判定する（移す間・切り戻しの Pod のため）。
 *         EGRESS_PROXY_SOURCE_MODE が enforce なら、この口は何も通さない。
 *   - ログに出すのは判定・送り元・宛先の名前・ポートだけ（中身・要求の頭は出さない）。/metrics は送り元・判定ごとの件数（別のポート）。
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

const SOURCE_FORM = /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/;
/**
 * 表の鍵（"<名前>:<ポート>"）。ポートを書かない宛先は 443。読めなければ null。
 * IADR-0709 決定 1: "*.<名前>" は、その名前の**ちょうど 1 段下**の名前だけに当たる（"*.discord.gg" は gateway-us-east1-b.discord.gg に当たり、
 * discord.gg 自身・a.b.discord.gg には当たらない）。土台の名前は 2 段以上（"*.gg" のような 1 段は捨てる）。途中の * は書けない。
 * 宣言の表に足すのは、名前が応答で決まり完全一致で書けない宛先だけ（Discord の Bot の再接続先）。
 */
function entryKey(text) {
  const m = /^((?:\*\.)?[^:\s*]+)(?::(\d{1,5}))?$/.exec(String(text || '').trim().toLowerCase());
  if (!m) return null;
  const wild = m[1].startsWith('*.');
  const base = wild ? m[1].slice(2) : m[1];
  if (!isHostname(base)) return null;
  if (wild && base.split('.').length < 2) return null;
  const port = m[2] === undefined ? 443 : Number(m[2]);
  if (!(port >= 1 && port <= 65535)) return null;
  return `${m[1]}:${port}`;
}

/**
 * 送り元ごとの表（"<送り元> <名前>[:<ポート>]" の行）。戻り値は Map<送り元, Set<鍵>>。
 * 送り元を書かない行（"<名前>" だけ。IADR-0705 の形）は送り元 '*'（どの送り元の表にも入らず、全体の表にだけ入る）。
 * 不正な行は捨てる（表を広げない）。
 */
function parseSourceHosts(text) {
  const out = new Map();
  for (const line of String(text || '').split('\n')) {
    const words = line.replace(/#.*/, '').trim().split(/\s+/).filter(Boolean);
    let source;
    let entry;
    if (words.length === 1) { source = '*'; entry = words[0]; } else if (words.length === 2) { [source, entry] = words; } else continue;
    source = source.toLowerCase();
    if (source !== '*' && !SOURCE_FORM.test(source)) continue;
    const key = entryKey(entry);
    if (!key) continue;
    if (!out.has(source)) out.set(source, new Set());
    out.get(source).add(key);
  }
  return out;
}

/**
 * 受け口の一覧（"<送り元>:<ポート>" の空白区切り）。送り元の名前・ポートの重なりは起動を止める（取り違えて表を当てない）。
 * reserved: 他の口（従来の口 3128・件数の口）のポート。送り元の口と重なれば起動を止める（同じポートを 2 つの口が取り合わない）。
 */
function parseListeners(text, reserved = []) {
  const out = [];
  for (const w of String(text || '').trim().split(/\s+/).filter(Boolean)) {
    const m = /^([a-z0-9-]+):(\d{1,5})$/.exec(w);
    if (!m || !SOURCE_FORM.test(m[1]) || m[1] === 'legacy') throw new Error(`EGRESS_PROXY_SOURCES の「${w}」が読めない（<送り元>:<ポート>。legacy は使えない）`);
    const port = Number(m[2]);
    if (!(port >= 1024 && port <= 65535)) throw new Error(`EGRESS_PROXY_SOURCES の「${w}」のポートが範囲の外`);
    if (out.some((x) => x.source === m[1] || x.port === port)) throw new Error(`EGRESS_PROXY_SOURCES の「${w}」が重なる`);
    if (reserved.includes(port)) throw new Error(`EGRESS_PROXY_SOURCES の「${w}」のポートが従来の口・件数の口と重なる`);
    out.push({ source: m[1], port });
  }
  return out;
}

/** 鍵の集合が宛先を含むか（ポートを書かない古い形の集合〔名前だけ〕は 443 として読む。"*.<名前>" は 1 段下だけ）。 */
const hasTarget = (set, target) => {
  if (set.has(`${target.host}:${target.port}`) || (target.port === 443 && set.has(target.host))) return true;
  const i = target.host.indexOf('.');
  return i > 0 && set.has(`*.${target.host.slice(i + 1)}:${target.port}`);
};

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

/**
 * IADR-0709 決定 2: バックアップの保管先の名前。rclone が仮想ホスト形式（force_path_style=false）で書くなら、宛先は
 * "<バケット>.<endpoint の名前>" だけ（同じ事業者の共有の名前の上の別のバケットへは出さない）。そうでなければ endpoint の名前（パス形式）。
 * バケットは dest（"<remote>:<バケット>/<接頭辞>"）から取る。読めなければ endpoint の名前へ戻さず、何も足さない（広げない）。
 */
function backupHosts(endpointText, destText, pathStyleText) {
  const hosts = hostOfEndpoint(endpointText);
  if (String(pathStyleText || '').trim().toLowerCase() !== 'false') return hosts;
  const m = /^[A-Za-z0-9_-]+:([a-z0-9][a-z0-9-]{1,61}[a-z0-9])(?:\/|$)/.exec(String(destText || '').trim());
  if (!m || !hosts.length) return [];
  const h = `${m[1]}.${hosts[0]}`;
  return isHostname(h) ? [h] : [];
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
 * 宛先の判定（名前を引く前）。戻り値は { decision: 'allow' | 'report' | 'deny', reason, mismatch }。
 *   allow: 表に在る。report: 表に無いが report の段なので通す。deny: 通さない。
 *   per（IADR-0709。省略すると送り元を見ない）: { source, sourceSet, sourceMode }
 *     source が 'legacy'（送り元を名乗らない口）なら sourceSet を見ず、sourceMode が enforce なら何も通さない。
 *     それ以外は、全体の表（allowSet）に在っても sourceSet に無ければ mismatch（report の段なら通し、enforce なら拒む）。
 */
function decide(target, allowSet, mode, per) {
  if (!target) return { decision: 'deny', reason: '宛先の形が違う' };
  if (net.isIP(target.host)) return { decision: 'deny', reason: 'IP の字面の宛先' };
  if (!isHostname(target.host)) return { decision: 'deny', reason: '名前の形が違う' };
  const inAll = hasTarget(allowSet, target);
  if (target.port !== 443 && !inAll) return { decision: 'deny', reason: '443 でない（表に名指したポートでもない）' };
  if (per && per.source === 'legacy' && per.sourceMode === 'enforce') return { decision: 'deny', reason: '送り元を名乗らない口（送り元ごとの段が enforce）' };
  if (inAll) {
    if (!per || per.source === 'legacy' || hasTarget(per.sourceSet || new Set(), target)) return { decision: 'allow', reason: '許可表に在る' };
    if (per.sourceMode === 'report') return { decision: 'report', reason: `送り元 ${per.source} の表に無い（送り元ごとの段が report なので通した）`, mismatch: true };
    return { decision: 'deny', reason: `送り元 ${per.source} の表に無い`, mismatch: true };
  }
  if (mode === 'report') return { decision: 'report', reason: '許可表に無い（report の段なので通した）' };
  return { decision: 'deny', reason: '許可表に無い' };
}

/** report の段に入った日時（UNIX 秒）。無い・形が違う・1 日より先の日時は 0（警報が鳴る側へ倒す）。 */
function readReportSince(file, now = Date.now()) {
  let text;
  try { text = fs.readFileSync(file, 'utf8').trim(); } catch { return 0; }
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/.test(text)) return 0;
  const t = Date.parse(text);
  if (!Number.isFinite(t) || t > now + 86400 * 1000) return 0;
  return Math.floor(t / 1000);
}

function readAllowFiles(files) {
  return readTables(files).all;
}

/**
 * 表のファイルを読む。kind: 'hosts'（名前だけの行。IADR-0705 の形）・'sources'（送り元つきの行。名前だけの行も読む）・
 * 'endpoint'（バックアップの保管先。送り元 backup）。戻り値は { all: Set<鍵>, bySource: Map<送り元, Set<鍵>> }。
 */
function readTables(files) {
  const all = new Set();
  const bySource = new Map();
  const add = (source, key) => {
    all.add(key);
    if (source === '*') return;
    if (!bySource.has(source)) bySource.set(source, new Set());
    bySource.get(source).add(key);
  };
  const readOr = (f) => { try { return fs.readFileSync(f, 'utf8'); } catch { return ''; } };
  for (const { path: p, kind, dest, pathStyle } of files) {
    let text = '';
    try { text = fs.readFileSync(p, 'utf8'); } catch { continue; }
    if (kind === 'endpoint') for (const h of (dest || pathStyle ? backupHosts(text, readOr(dest), readOr(pathStyle)) : hostOfEndpoint(text))) add('backup', `${h}:443`);
    else if (kind === 'sources') for (const [src, keys] of parseSourceHosts(text)) for (const k of keys) add(src, k);
    else for (const h of parseHosts(text)) add('*', `${h}:443`);
  }
  return { all, bySource };
}

/**
 * 中継を作る。opts:
 *   mode: 'enforce' | 'report'
 *   allow: () => Set<string>（許可表）
 *   lookup: (host) => Promise<string[]>（名前を引く。既定は dns.promises.lookup の全件）
 *   connect: (address, port) => net.Socket（外へ繋ぐ。既定は net.connect）
 *   log: (line) => void
 *   reportSince: () => number（report の段に入った日時。UNIX 秒。既定は 0＝無い）
 *   connectTimeoutMs: 番地ごとに繋がるのを待つ上限（既定 10 秒。過ぎたら次の番地へ）
 *   source: 送り元（IADR-0709。既定 'legacy'＝送り元を名乗らない口）
 *   sourceAllow: () => Set<string>（その送り元の表。legacy では読まない）
 *   sourceMode: 'enforce' | 'report'（送り元ごとの段。省略すると送り元を見ない＝IADR-0705 の振る舞い）
 */
function createProxy(opts) {
  const mode = opts.mode === 'report' ? 'report' : 'enforce';
  const lookup = opts.lookup || (async (h) => (await dns.promises.lookup(h, { all: true, verbatim: true })).map((x) => x.address));
  const connect = opts.connect || ((address, port) => net.connect({ host: address, port }));
  const log = opts.log || ((l) => console.log(l));
  const reportSince = opts.reportSince || (() => 0);
  const connectTimeoutMs = opts.connectTimeoutMs || 10 * 1000;
  const source = opts.source || 'legacy';
  const sourceMode = opts.sourceMode === 'report' || opts.sourceMode === 'enforce' ? opts.sourceMode : null;
  const sourceAllow = opts.sourceAllow || (() => new Set());
  const counts = { allow: 0, report: 0, deny: 0 };
  let mismatches = 0;
  const server = http.createServer((req, res) => {
    counts.deny += 1;
    log(`deny: [${source}] ${req.method} は受けない（CONNECT だけ）`);
    res.writeHead(405, { 'content-type': 'text/plain; charset=utf-8', connection: 'close' });
    res.end('CONNECT only\n');
  });
  server.on('connect', async (req, client, head) => {
    const target = parseTarget(req.url);
    const refuse = (code, why) => {
      counts.deny += 1;
      log(`deny: [${source}] ${target ? `${target.host}:${target.port}` : '(読めない宛先)'} ${why}`);
      client.end(`HTTP/1.1 ${code} ${code === 403 ? 'Forbidden' : 'Bad Gateway'}\r\nconnection: close\r\n\r\n`);
    };
    client.on('error', () => {});
    const d = decide(target, opts.allow(), mode, sourceMode ? { source, sourceMode, sourceSet: source === 'legacy' ? null : sourceAllow() } : null);
    if (d.mismatch) mismatches += 1;
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
    log(`${d.decision}: [${source}] ${target.host}:${target.port}${d.decision === 'report' ? ` ${d.reason}` : ''}`);
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
  /** 件数の行（見出しなし）。main が送り元ごとの中継の行を 1 つの /metrics にまとめる。 */
  const sampleLines = () => [
    ...Object.entries(counts).map(([k, v]) => `egress_proxy_requests_total{decision="${k}",source="${source}"} ${v}`),
    `egress_proxy_source_mismatch_total{source="${source}"} ${mismatches}`,
  ];
  const metrics = () => renderMetrics({ mode, sourceMode, reportSince, samples: sampleLines() });
  return { server, counts, metrics, sampleLines, mode, source, mismatches: () => mismatches };
}

/** /metrics の本文。samples は送り元ごとの件数の行（sampleLines）を連ねたもの。 */
function renderMetrics({ mode, sourceMode, reportSince, samples }) {
  const inReport = mode === 'report' || sourceMode === 'report';
  return [
    '# HELP egress_proxy_requests_total 外向きの中継の、送り元・判定ごとの件数（allow・report・deny）。',
    '# TYPE egress_proxy_requests_total counter',
    ...samples.filter((l) => l.startsWith('egress_proxy_requests_total')),
    '# HELP egress_proxy_source_mismatch_total 全体の表には在るが、その送り元の表に無い宛先への要求の件数（IADR-0709）。',
    '# TYPE egress_proxy_source_mismatch_total counter',
    ...samples.filter((l) => l.startsWith('egress_proxy_source_mismatch_total')),
    '# HELP egress_proxy_enforcing 段が enforce なら 1、report なら 0。',
    '# TYPE egress_proxy_enforcing gauge',
    `egress_proxy_enforcing ${mode === 'enforce' ? 1 : 0}`,
    '# HELP egress_proxy_source_enforcing 送り元ごとの段が enforce なら 1、report なら 0（IADR-0709）。',
    '# TYPE egress_proxy_source_enforcing gauge',
    `egress_proxy_source_enforcing ${sourceMode === 'report' ? 0 : 1}`,
    // どちらかの段が report のときだけ出す（enforce では要らない）。読むたびにファイルを読む（ConfigMap を書き直せば次の収集で効く）
    ...(inReport ? [
      '# HELP egress_proxy_report_since_timestamp_seconds report の段に入った日時（UNIX 秒。ConfigMap egress-proxy-report-since。無い・読めなければ 0）。',
      '# TYPE egress_proxy_report_since_timestamp_seconds gauge',
      `egress_proxy_report_since_timestamp_seconds ${reportSince()}`,
    ] : []),
    '',
  ].join('\n');
}

function main(env = process.env) {
  const files = [
    { path: env.EGRESS_PROXY_HOSTS || '/etc/egress-proxy/hosts/hosts', kind: 'sources' },
    { path: env.EGRESS_PROXY_EXTRA_HOSTS || '/etc/egress-proxy/extra/hosts', kind: 'sources' },
    {
      path: env.EGRESS_PROXY_BACKUP_ENDPOINT || '/etc/egress-proxy/backup/endpoint',
      dest: env.EGRESS_PROXY_BACKUP_DEST || '/etc/egress-proxy/backup/dest',
      pathStyle: env.EGRESS_PROXY_BACKUP_PATH_STYLE || '/etc/egress-proxy/backup/force_path_style',
      kind: 'endpoint',
    },
  ];
  const mode = env.EGRESS_PROXY_MODE;
  if (mode !== 'enforce' && mode !== 'report') {
    console.log(`ERROR: EGRESS_PROXY_MODE は enforce か report（${mode === undefined ? '未指定' : '不正な値'}）`);
    process.exit(2);
  }
  const sourceMode = env.EGRESS_PROXY_SOURCE_MODE;
  if (sourceMode !== 'enforce' && sourceMode !== 'report') {
    console.log(`ERROR: EGRESS_PROXY_SOURCE_MODE は enforce か report（${sourceMode === undefined ? '未指定' : '不正な値'}）`);
    process.exit(2);
  }
  let listeners;
  const legacyPort = Number(env.EGRESS_PROXY_PORT || 3128);
  const metricsPort = Number(env.EGRESS_PROXY_METRICS_PORT || 9464);
  try { listeners = parseListeners(env.EGRESS_PROXY_SOURCES, [legacyPort, metricsPort]); } catch (e) { console.log(`ERROR: ${e.message}`); process.exit(2); }
  let tables = readTables(files);
  let readAt = Date.now();
  const current = () => {
    if (Date.now() - readAt > 30000) { tables = readTables(files); readAt = Date.now(); }
    return tables;
  };
  const sinceFile = env.EGRESS_PROXY_REPORT_SINCE || '/etc/egress-proxy/report-since/since';
  const reportSince = () => readReportSince(sinceFile);
  const all = [{ source: 'legacy', port: legacyPort }, ...listeners].map(({ source, port }) => {
    const prox = createProxy({
      mode,
      source,
      sourceMode,
      reportSince,
      allow: () => current().all,
      sourceAllow: () => current().bySource.get(source) || new Set(),
    });
    prox.server.listen(port);
    return prox;
  });
  console.log(`egress-proxy: 段 ${mode}・送り元ごとの段 ${sourceMode}・全体の表 ${tables.all.size} 件（${[...tables.all].sort().join(', ')}）`);
  for (const { source, port } of listeners) console.log(`egress-proxy: 送り元 ${source}（${port}）の表 ${[...(tables.bySource.get(source) || [])].sort().join(', ') || '（空）'}`);
  for (const src of tables.bySource.keys()) if (!listeners.some((l) => l.source === src)) console.log(`egress-proxy: WARNING: 表の送り元 ${src} に受け口が無い（EGRESS_PROXY_SOURCES）`);
  http.createServer((req, res) => {
    if (req.url === '/metrics') {
      res.writeHead(200, { 'content-type': 'text/plain; version=0.0.4' });
      res.end(renderMetrics({ mode, sourceMode, reportSince, samples: all.flatMap((x) => x.sampleLines()) }));
      return;
    }
    if (req.url === '/healthz') { res.writeHead(200); res.end('ok\n'); return; }
    res.writeHead(404); res.end();
  }).listen(metricsPort);
}

module.exports = { parseHosts, backupHosts, parseSourceHosts, parseListeners, entryKey, hostOfEndpoint, isPrivateAddress, parseTarget, decide, readAllowFiles, readTables, readReportSince, createProxy, renderMetrics };
if (require.main === module) main();
