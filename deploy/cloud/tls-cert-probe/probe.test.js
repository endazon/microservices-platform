#!/usr/bin/env node
'use strict';
/*
 * NFR-16, NFR-21, IADR-0703: サーバ証明書の期限の測り（probe.js）の試験。手元に試験の CA と証明書（openssl。その場で作り、試験の後に消す）で
 * TLS の受け口と Postgres の形の受け口（SSLRequest → 'S' → TLS）を立て、測った値を確かめる。
 * scripts/deploy-cloud-egress-audit.test.js が子のプロセスで走らせる（node deploy/cloud/tls-cert-probe/probe.test.js でも。要 openssl）。
 *
 *   C1 TLS の受け口: 届き、鎖と名前が正しければ verified=1、notAfter は証明書の値。
 *   C2 Postgres の形: SSLRequest の後に TLS を始めて同じく読む。'N'（TLS を受けない）なら届かない扱い。
 *   C3 名前が違う・CA が違う: verified=0 だが notAfter は出す（期限の警報は検証の失敗に隠れない）。
 *   C4 CA の束の各証明書の notAfter を出し、読めない束は系列を作らない。
 */
const assert = require('assert');
const fs = require('fs');
const net = require('net');
const os = require('os');
const path = require('path');
const tls = require('tls');
const { spawnSync } = require('child_process');
const { X509Certificate } = require('crypto');
const p = require('./probe.js');

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tls-cert-probe-'));
const f = (n) => path.join(dir, n);
function openssl(args) {
  const r = spawnSync('openssl', args, { encoding: 'utf8' });
  if (r.status !== 0) throw new Error(`openssl ${args[0]} が失敗した: ${r.stderr}`);
}
function makeCa(name, days) {
  openssl(['req', '-x509', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256', '-nodes', '-keyout', f(`${name}.key`), '-out', f(`${name}.crt`), '-days', String(days), '-subj', `/CN=${name}`]);
}
function makeLeaf(name, ca, san, days) {
  openssl(['req', '-new', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256', '-nodes', '-keyout', f(`${name}.key`), '-out', f(`${name}.csr`), '-subj', `/CN=${san}`]);
  fs.writeFileSync(f(`${name}.ext`), `subjectAltName=DNS:${san}\n`);
  openssl(['x509', '-req', '-in', f(`${name}.csr`), '-CA', f(`${ca}.crt`), '-CAkey', f(`${ca}.key`), '-CAcreateserial', '-out', f(`${name}.crt`), '-days', String(days), '-extfile', f(`${name}.ext`)]);
}

const tests = [];
const ok = (name, fn) => tests.push([name, fn]);
const read = (n) => fs.readFileSync(f(n), 'utf8');
const notAfterOf = (n) => Math.floor(Date.parse(new X509Certificate(read(n)).validTo) / 1000);

function tlsServer(leaf) {
  return new Promise((resolve) => {
    const s = tls.createServer({ key: read(`${leaf}.key`), cert: read(`${leaf}.crt`) }, (c) => c.end());
    s.listen(0, '127.0.0.1', () => resolve(s));
  });
}
function pgServer(leaf, { accept = true } = {}) {
  return new Promise((resolve) => {
    const s = net.createServer((raw) => {
      raw.once('data', (b) => {
        if (!b.equals(p.PG_SSL_REQUEST)) { raw.destroy(); return; }
        raw.write(accept ? 'S' : 'N');
        if (!accept) { raw.end(); return; }
        const t = new tls.TLSSocket(raw, { isServer: true, key: read(`${leaf}.key`), cert: read(`${leaf}.crt`) });
        t.on('error', () => {});
      });
      raw.on('error', () => {});
    });
    s.listen(0, '127.0.0.1', () => resolve(s));
  });
}
const target = (name, port, extra = {}) => ({ name, host: '127.0.0.1', port, servername: name, ca: f('ca.crt'), ...extra });

ok('C1 TLS の受け口: 鎖と名前が正しければ verified、notAfter は証明書の値', async () => {
  const s = await tlsServer('keycloak');
  try {
    const r = await p.probeTarget(target('keycloak', s.address().port));
    assert.deepStrictEqual({ ok: r.ok, verified: r.verified, notAfter: r.notAfter }, { ok: true, verified: true, notAfter: notAfterOf('keycloak.crt') }, r.error);
  } finally { s.close(); }
});

ok('C2 Postgres の形: SSLRequest の後に TLS を始めて読む。TLS を受けなければ届かない扱い', async () => {
  const s = await pgServer('postgres');
  const n = await pgServer('postgres', { accept: false });
  try {
    const r = await p.probeTarget(target('postgres', s.address().port, { protocol: 'postgres' }));
    assert.deepStrictEqual({ ok: r.ok, verified: r.verified, notAfter: r.notAfter }, { ok: true, verified: true, notAfter: notAfterOf('postgres.crt') }, r.error);
    const r2 = await p.probeTarget(target('postgres', n.address().port, { protocol: 'postgres' }));
    assert.strictEqual(r2.ok, false);
    // 陰性対照: SSLRequest を送らずに TLS を始めると、Postgres の形の受け口は読めない（形の分岐が効いている）
    const r3 = await p.probeTarget(target('postgres', s.address().port), { timeoutMs: 1500 });
    assert.strictEqual(r3.ok, false, 'SSLRequest なしで読めた（分岐が空回り）');
  } finally { s.close(); n.close(); }
});

ok('C3 名前・CA が違えば verified=0 だが notAfter は出す', async () => {
  const s = await tlsServer('keycloak');
  try {
    const wrongName = await p.probeTarget(target('rabbitmq', s.address().port));
    assert.deepStrictEqual([wrongName.ok, wrongName.verified, wrongName.notAfter], [true, false, notAfterOf('keycloak.crt')]);
    const wrongCa = await p.probeTarget(target('keycloak', s.address().port, { ca: f('other.crt') }));
    assert.deepStrictEqual([wrongCa.ok, wrongCa.verified, wrongCa.notAfter], [true, false, notAfterOf('keycloak.crt')]);
    const closed = await p.probeTarget(target('keycloak', 1), { timeoutMs: 1500 });
    assert.strictEqual(closed.ok, false);
    const m = p.renderMetrics([wrongCa, closed], [], 1);
    assert.match(m, /tls_probe_up\{target="keycloak"\} 1/);
    assert.match(m, /tls_probe_verified\{target="keycloak"\} 0/);
    assert.match(m, new RegExp(`tls_probe_cert_not_after_seconds\\{target="keycloak"\\} ${notAfterOf('keycloak.crt')}`));
    assert.strictEqual((m.match(/tls_probe_cert_not_after_seconds\{/g) || []).length, 1, '届かない受け口の notAfter を作った');
  } finally { s.close(); }
});

ok('C4 CA の束の各証明書の notAfter を出し、読めない束は系列を作らない', () => {
  const bundle = `${read('ca.crt')}\n${read('other.crt')}`;
  const got = p.caNotAfters(bundle).map((c) => c.notAfter).sort();
  assert.deepStrictEqual(got, [notAfterOf('ca.crt'), notAfterOf('other.crt')].sort());
  assert.deepStrictEqual(p.caNotAfters('not a pem'), []);
  const m = p.renderMetrics([], [{ ca: 'openbao-ca', certs: p.caNotAfters(bundle) }], 1);
  assert.strictEqual((m.match(/tls_ca_cert_not_after_seconds\{ca="openbao-ca",serial="[0-9a-f]+"\}/g) || []).length, 2);
});

(async () => {
  let failed = 0;
  try {
    makeCa('ca', 3650);
    makeCa('other', 730);
    makeLeaf('keycloak', 'ca', 'keycloak', 365);
    makeLeaf('postgres', 'ca', 'postgres', 200);
    for (const [name, fn] of tests) {
      try {
        await fn();
        console.log(`  ok  ${name}`);
      } catch (e) {
        failed += 1;
        console.log(`  NG  ${name}\n${e.stack || e.message}`);
      }
    }
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
  if (failed) { console.error(`✗ ${failed} 件が失敗した`); process.exit(1); }
  console.log('✓ サーバ証明書の期限の測りの試験');
})();
