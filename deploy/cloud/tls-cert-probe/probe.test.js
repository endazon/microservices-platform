#!/usr/bin/env node
'use strict';
/*
 * NFR-16, NFR-21, IADR-0705: サーバ証明書の期限の測り（probe.js）の試験。手元に試験の CA と証明書（openssl。その場で作り、試験の後に消す）で
 * TLS の受け口と Postgres の形の受け口（SSLRequest → 'S' → TLS）を立て、測った値を確かめる。
 * scripts/deploy-cloud-egress-audit.test.js が子のプロセスで走らせる（node deploy/cloud/tls-cert-probe/probe.test.js でも。要 openssl）。
 *
 *   C1 TLS の受け口: 届き、鎖と名前が正しければ verified=1、notAfter は証明書の値。
 *   C2 Postgres の形: SSLRequest の後に TLS を始めて同じく読む。'N'（TLS を受けない）なら届かない扱い。
 *   C3 検証を切らない: 名前が違えば ERR_TLS_CERT_ALTNAME_INVALID、別の CA の証明書なら UNABLE_TO_VERIFY_LEAF_SIGNATURE を「検証の失敗」として出し
 *      （verified=0・届いてはいる）、検証を通っていない証明書の notAfter は出さない。陰性対照: 出した CA を信頼する・CA を渡さない形に変えると判定が変わる。
 *   C4 CA の束の各証明書の notAfter を出し、読めない束は系列を作らない。
 *   C5 期限切れの証明書は CERT_HAS_EXPIRED（「期限切れ」）として出し、notAfter を測った時刻（残り 0）とみなす。
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
/** 期限の切れた証明書（openssl ca で過去の期間を与える。req・x509 は負の日数を受けない）。 */
function makeExpiredLeaf(name, ca, san) {
  openssl(['req', '-new', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256', '-nodes', '-keyout', f(`${name}.key`), '-out', f(`${name}.csr`), '-subj', `/CN=${san}`]);
  fs.mkdirSync(f(`${name}-db`));
  fs.writeFileSync(f(`${name}-db/index.txt`), '');
  fs.writeFileSync(f(`${name}-db/serial`), '1000\n');
  fs.writeFileSync(f(`${name}.cnf`), [
    '[ca]', 'default_ca = d', '[d]', `dir = ${f(`${name}-db`)}`, 'database = $dir/index.txt', 'serial = $dir/serial', 'new_certs_dir = $dir',
    `certificate = ${f(`${ca}.crt`)}`, `private_key = ${f(`${ca}.key`)}`, 'default_md = sha256', 'policy = p', 'copy_extensions = none', 'x509_extensions = e',
    '[p]', 'commonName = supplied', '[e]', `subjectAltName = DNS:${san}`, 'basicConstraints = CA:FALSE', '',
  ].join('\n'));
  openssl(['ca', '-batch', '-notext', '-config', f(`${name}.cnf`), '-in', f(`${name}.csr`), '-out', f(`${name}.crt`), '-startdate', '20200101000000Z', '-enddate', '20200201000000Z']);
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

ok('C1 TLS の受け口: 鎖と名前が正しければ（検証つきの握手が通れば）verified、notAfter は検証を通った証明書の値', async () => {
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

ok('C3 検証を切らない: 名前違い・別の CA の証明書は「検証の失敗」として出し、notAfter は出さない（陰性対照つき）', async () => {
  const s = await tlsServer('keycloak');
  const other = await tlsServer('foreign');
  try {
    const wrongName = await p.probeTarget(target('rabbitmq', s.address().port));
    assert.deepStrictEqual([wrongName.ok, wrongName.verified, wrongName.verifyError, wrongName.notAfter], [true, false, 'ERR_TLS_CERT_ALTNAME_INVALID', undefined]);
    // 別の CA が出した証明書（名前は合っている）
    const wrongCa = await p.probeTarget(target('keycloak', other.address().port));
    assert.deepStrictEqual([wrongCa.ok, wrongCa.verified, wrongCa.verifyError, wrongCa.notAfter], [true, false, 'UNABLE_TO_VERIFY_LEAF_SIGNATURE', undefined]);
    const closed = await p.probeTarget(target('keycloak', 1), { timeoutMs: 1500 });
    assert.deepStrictEqual([closed.ok, closed.verifyError], [false, undefined], '届かないことを検証の失敗と取り違えた');
    const m = p.renderMetrics([wrongCa, closed], [], 1);
    assert.match(m, /tls_probe_up\{target="keycloak"\} 1/);
    assert.match(m, /tls_probe_verified\{target="keycloak"\} 0/);
    assert.match(m, /tls_probe_verify_error\{target="keycloak",reason="UNABLE_TO_VERIFY_LEAF_SIGNATURE"\} 1/);
    assert.strictEqual((m.match(/tls_probe_cert_not_after_seconds\{/g) || []).length, 0, '検証を通っていない証明書の notAfter を出した');

    // 陰性対照: 信頼する CA をその証明書を出した CA に替えると通る＝上の失敗は、渡した CA での検証に依る（名前・形の違いではない）。
    // CA を渡さない形（システムの信頼ストア）にすると、正しい証明書も通らない＝基盤の CA を渡していることに依る。
    const trustOther = await p.probeTarget(target('keycloak', other.address().port, { ca: f('other.crt') }));
    assert.deepStrictEqual([trustOther.verified, trustOther.notAfter], [true, notAfterOf('foreign.crt')], '陰性対照: 出した CA を信頼しても通らない');
    const orig = tls.connect;
    try {
      tls.connect = (o) => { const { ca, ...rest } = o; return orig(rest); };
      const noCa = await p.probeTarget(target('keycloak', s.address().port));
      assert.deepStrictEqual([noCa.verified, Boolean(noCa.verifyError)], [false, true], '陰性対照: CA を渡さなくても正しい証明書が通る');
    } finally { tls.connect = orig; }
  } finally { s.close(); other.close(); }
});

ok('C5 期限切れの証明書は CERT_HAS_EXPIRED（期限切れ）として出し、notAfter は測った時刻（残り 0）', async () => {
  const s = await tlsServer('expired');
  try {
    const at = 1_900_000_000_000;
    const r = await p.probeTarget(target('expired', s.address().port), { now: () => at });
    assert.deepStrictEqual([r.ok, r.verified, r.verifyError, r.notAfter], [true, false, 'CERT_HAS_EXPIRED', at / 1000]);
    const m = p.renderMetrics([r], [], at / 1000);
    assert.match(m, /tls_probe_verify_error\{target="expired",reason="CERT_HAS_EXPIRED"\} 1/);
    assert.match(m, new RegExp(`tls_probe_cert_not_after_seconds\\{target="expired"\\} ${at / 1000}`));
    // 陰性対照: 期限の切れていない正しい証明書は期限切れとして出ない
    const k = await tlsServer('keycloak');
    try {
      const good = await p.probeTarget(target('keycloak', k.address().port), { now: () => at });
      assert.deepStrictEqual([good.verified, good.verifyError, good.notAfter], [true, undefined, notAfterOf('keycloak.crt')]);
    } finally { k.close(); }
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
    makeLeaf('foreign', 'other', 'keycloak', 365);
    makeExpiredLeaf('expired', 'ca', 'expired');
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
