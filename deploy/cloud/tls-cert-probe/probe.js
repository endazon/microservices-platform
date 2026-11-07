#!/usr/bin/env node
'use strict';
/*
 * NFR-16, NFR-21, IADR-0703: クラウドの基盤のサーバ証明書（Postgres・RabbitMQ・Keycloak・LLM ゲートウェイ）の**受け口が実際に出している**証明書の
 * 期限を測り、Prometheus へ系列として出す（警報は observability/prometheus.yaml の cloud-tls-expiry）。
 *
 * なぜ受け口を測るか: ESO は半年ごとに Secret を作り直すが、Postgres・RabbitMQ・LLM ゲートウェイは作り直す（rollout restart）まで古い証明書を出し続ける
 * （README §6）。Secret ではなく受け口を測れば、作り直しの漏れ・ESO の止まり（封印のまま）・発行の失敗のどれでも、出している証明書の残りが減って見える。
 * Secret を読まない（鍵に触れない。ServiceAccount のトークンも持たない）。
 *
 *   - 受け口ごとに TLS で繋ぎ（Postgres は SSLRequest の後に TLS）、基盤の CA（ConfigMap platform-infra-ca）で鎖と名前を確かめ、
 *     相手の証明書の notAfter を読む。鎖・名前が合わなくても notAfter は出す（検証の結果は別の系列）。中身は送らない（繋いで閉じるだけ）。
 *   - CA の期限: platform-infra-ca（OpenBao の PKI の CA。10 年）と openbao-ca（OpenBao の受け口の CA。IADR-0696 の tls-cert.sh が受け口の証明書
 *     〔1 年〕と同じ回に 2 年で作る。束のときは各証明書）。OpenBao の受け口は ESO の controller だけが届く（IADR-0689 決定 3）ので測りに行かず、
 *     受け口の証明書の期限は CA の notAfter から導く（警報の側。作りが同じ回・1 年と 2 年であることは試験が tls-cert.sh と突き合わせる）。
 *   - 測るのは 10 分ごと（受け口のログを増やさない）。/metrics はその時の値を返す。
 *
 * node:22-alpine が実行する（外部依存なし）。試験: deploy/cloud/tls-cert-probe/probe.test.js。
 */
const http = require('http');
const net = require('net');
const tls = require('tls');
const fs = require('fs');
const { X509Certificate } = require('crypto');

/** Postgres の SSLRequest（長さ 8・要求の番号 80877103）。応答の 1 バイトが 'S' なら TLS を始める。 */
const PG_SSL_REQUEST = Buffer.from([0, 0, 0, 8, 0x04, 0xd2, 0x16, 0x2f]);

function pemBlocks(text) {
  return String(text || '').match(/-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----/g) || [];
}

/** CA の束の各証明書の notAfter（秒）。読めない束は空。 */
function caNotAfters(text) {
  const out = [];
  for (const b of pemBlocks(text)) {
    try {
      const c = new X509Certificate(b);
      out.push({ serial: c.serialNumber.toLowerCase(), notAfter: Math.floor(Date.parse(c.validTo) / 1000) });
    } catch {
      // 読めない塊は飛ばす（系列を作らない ＝ 警報の absent が拾う）
    }
  }
  return out;
}

/** 平文の TCP を開き、Postgres なら SSLRequest を済ませてから TLS を始め、相手の証明書と検証の結果を返す。 */
function openTls(t, ca, timeoutMs) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { reject(new Error('時間切れ')); try { raw.destroy(); } catch { /* */ } }, timeoutMs);
    const done = (err, s) => { clearTimeout(timer); if (err) reject(err); else resolve(s); };
    const startTls = (socket) => {
      // 鎖と名前は下で自分で確かめる（期限の切れた・名前の違う証明書でも notAfter を読むため、ここでは止めない）。
      const s = tls.connect({ socket, servername: t.servername, ca, rejectUnauthorized: false, minVersion: 'TLSv1.2' });
      // 相手が直後に閉じても読めるよう、握手の直後に同じ tick で写す。
      s.once('secureConnect', () => done(null, { socket: s, peer: s.getPeerCertificate(), x509: s.getPeerX509Certificate(), authorized: s.authorized, authorizationError: s.authorizationError }));
      s.once('error', (e) => done(e));
    };
    const raw = net.connect({ host: t.host, port: t.port });
    raw.once('error', (e) => done(e));
    raw.once('connect', () => {
      if (t.protocol !== 'postgres') { startTls(raw); return; }
      raw.write(PG_SSL_REQUEST);
      raw.once('data', (b) => {
        if (b.length >= 1 && b[0] === 0x53) startTls(raw);
        else { raw.destroy(); done(new Error('Postgres が TLS を受けない')); }
      });
    });
  });
}

/** 1 つの受け口を測る。戻り値は { target, ok（届いて証明書を読めた）, verified（鎖と名前）, notAfter（秒・読めたときだけ）, error }。 */
async function probeTarget(t, { readCa = (p) => fs.readFileSync(p, 'utf8'), timeoutMs = 10000 } = {}) {
  let h;
  try {
    const ca = readCa(t.ca);
    h = await openTls(t, ca, timeoutMs);
    if (!h.x509) return { target: t.name, ok: false, verified: false, error: '証明書が無い' };
    const notAfter = Math.floor(Date.parse(h.x509.validTo) / 1000);
    const nameErr = tls.checkServerIdentity(t.servername, h.peer);
    const verified = h.authorized === true && !nameErr && notAfter * 1000 > Date.now();
    return { target: t.name, ok: true, verified, notAfter, error: verified ? '' : (h.authorizationError || (nameErr && nameErr.code) || '期限切れ') };
  } catch (e) {
    return { target: t.name, ok: false, verified: false, error: e.message };
  } finally {
    if (h) h.socket.destroy();
  }
}

/** 測った結果から Prometheus の文字列を作る。 */
function renderMetrics(results, cas, now) {
  const lines = [
    '# HELP tls_probe_up 受け口へ TLS で届き、証明書を読めたら 1。',
    '# TYPE tls_probe_up gauge',
    ...results.map((r) => `tls_probe_up{target="${r.target}"} ${r.ok ? 1 : 0}`),
    '# HELP tls_probe_verified 受け口の証明書の鎖（基盤の CA）・名前・期限が正しければ 1。',
    '# TYPE tls_probe_verified gauge',
    ...results.map((r) => `tls_probe_verified{target="${r.target}"} ${r.verified ? 1 : 0}`),
    '# HELP tls_probe_cert_not_after_seconds 受け口が出している証明書の notAfter（UNIX 秒）。',
    '# TYPE tls_probe_cert_not_after_seconds gauge',
    ...results.filter((r) => r.ok).map((r) => `tls_probe_cert_not_after_seconds{target="${r.target}"} ${r.notAfter}`),
    '# HELP tls_ca_cert_not_after_seconds CA の束の各証明書の notAfter（UNIX 秒）。',
    '# TYPE tls_ca_cert_not_after_seconds gauge',
    ...cas.flatMap(({ ca, certs }) => certs.map((c) => `tls_ca_cert_not_after_seconds{ca="${ca}",serial="${c.serial}"} ${c.notAfter}`)),
    '# HELP tls_probe_last_run_timestamp_seconds 最後に測った時刻（UNIX 秒）。',
    '# TYPE tls_probe_last_run_timestamp_seconds gauge',
    `tls_probe_last_run_timestamp_seconds ${now}`,
    '',
  ];
  return lines.join('\n');
}

function main(env = process.env) {
  const cfg = JSON.parse(fs.readFileSync(env.TLS_PROBE_CONFIG || '/etc/tls-cert-probe/config/targets.json', 'utf8'));
  let body = renderMetrics([], [], 0);
  const run = async () => {
    const results = [];
    for (const t of cfg.targets) results.push(await probeTarget(t));
    const cas = cfg.cas.map(({ name, path: p }) => {
      let text = '';
      try { text = fs.readFileSync(p, 'utf8'); } catch { /* 無ければ系列を作らない */ }
      return { ca: name, certs: caNotAfters(text) };
    });
    body = renderMetrics(results, cas, Math.floor(Date.now() / 1000));
    for (const r of results) console.log(`tls-cert-probe: ${r.target} ${r.ok ? `notAfter=${new Date(r.notAfter * 1000).toISOString()} verified=${r.verified}` : '届かない'}${r.error ? ` (${r.error})` : ''}`);
  };
  run();
  setInterval(run, Number(env.TLS_PROBE_INTERVAL_S || 600) * 1000);
  http.createServer((req, res) => {
    if (req.url === '/metrics') { res.writeHead(200, { 'content-type': 'text/plain; version=0.0.4' }); res.end(body); return; }
    if (req.url === '/healthz') { res.writeHead(200); res.end('ok\n'); return; }
    res.writeHead(404); res.end();
  }).listen(Number(env.TLS_PROBE_PORT || 9465));
}

module.exports = { caNotAfters, probeTarget, renderMetrics, PG_SSL_REQUEST };
if (require.main === module) main();
