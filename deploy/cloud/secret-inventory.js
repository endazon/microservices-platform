#!/usr/bin/env node
'use strict';
/**
 * NFR-05, NFR-18, IADR-0689 決定 8: クラウドの最小構成（deploy/cloud）が**参照する Secret の全件**を、供給元（OpenBao から写す
 * ExternalSecret）と ADR-0080 決定 1 の区分に照らす（移行の門の条件 3 の突き合わせ。ADR-0080 フォローアップ 4 の手段）。
 *
 *   node deploy/cloud/secret-inventory.js            # 表を出す。区分 2・3 に供給元の無いものがあれば 1 で終わる
 *   node deploy/cloud/secret-inventory.js --strict   # 移行の判定: 供給元の無いものが 1 つでもあれば 1（区分 1 の未供給も数える）
 *
 * 参照の母集合: deploy/cloud の宣言（vault/eso を除く）の secretKeyRef・secret ボリューム・secretRef と、values-cloud.yaml の
 * services.*.tls.secretName（chart が描く）。供給元の母集合: deploy/cloud/vault/eso の ExternalSecret の target.name。
 * 区分は名前で引く（下の CATEGORY）。表に無い名前の参照は「区分が未定」として止める（新しい Secret を黙って通さない）。
 * クラスタに触れない（宣言だけを読む）。値は扱わない。
 */
const fs = require('fs');
const path = require('path');
const { parseDocuments } = require('../../scripts/lib/yaml-subset');

const ROOT = path.join(__dirname, '..', '..');
const CLOUD = path.join(ROOT, 'deploy', 'cloud');

/** ADR-0080 決定 1 の区分。1 = 外から持ち込む・2 = クラスタの中で作る・3 = TLS の秘密鍵。 */
const CATEGORY = Object.freeze({
  'platform-infra/postgres': 2,
  'platform-infra/ast-db': 2,
  'platform-infra/keycloak-db': 2,
  'platform-infra/keycloak-admin': 2,
  'platform-infra/rabbitmq': 2,
  'platform-infra/rabbitmq-users': 2,
  'platform-infra/keycloak-realm-import': 2,
  'platform-infra/postgres-tls': 3,
  'platform-infra/rabbitmq-tls': 3,
  'microservices-platform/llmgateway-tls': 3,
  'microservices-platform/llm-provider-credentials': 1,
  'platform-infra/platform-backup-s3': 1,
  'platform-infra/alertmanager-discord': 1,
  'platform-infra/alertmanager-heartbeat': 1,
});

function walk(dir, skip) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const p = path.join(dir, e.name);
    if (skip(p)) return [];
    if (e.isDirectory()) return walk(p, skip);
    return /\.ya?ml$/.test(e.name) ? [p] : [];
  });
}
const docsIn = (file) => parseDocuments(fs.readFileSync(file, 'utf8')).filter((d) => d && typeof d === 'object' && d.kind);

/** 宣言の中の Secret の参照を集める（{ ref: 'ns/name', from: 'file' }） */
function collectRefs() {
  const refs = [];
  const eso = path.join(CLOUD, 'vault', 'eso');
  for (const file of walk(CLOUD, (p) => p === eso || p.endsWith('values-cloud.yaml') || p.endsWith('external-secrets-values.yaml'))) {
    for (const d of docsIn(file)) {
      const ns = (d.metadata || {}).namespace;
      const rel = path.relative(ROOT, file);
      const seen = (name) => name && refs.push({ ref: `${ns}/${name}`, from: rel });
      const visit = (v) => {
        if (Array.isArray(v)) return v.forEach(visit);
        if (!v || typeof v !== 'object') return;
        if (v.secretKeyRef) seen(v.secretKeyRef.name);
        if (v.secretRef && typeof v.secretRef === 'object') seen(v.secretRef.name);
        if (v.secret && typeof v.secret === 'object' && v.secret.secretName) seen(v.secret.secretName);
        for (const k of Object.keys(v)) if (k !== 'metadata') visit(v[k]);
      };
      visit(d.spec);
    }
  }
  const values = parseDocuments(fs.readFileSync(path.join(CLOUD, 'values-cloud.yaml'), 'utf8'))[0] || {};
  for (const [, svc] of Object.entries(values.services || {})) {
    if (svc && svc.enabled !== false && svc.tls && svc.tls.secretName) refs.push({ ref: `microservices-platform/${svc.tls.secretName}`, from: 'deploy/cloud/values-cloud.yaml' });
  }
  // chart の LLM ゲートウェイは提供元の鍵を Secret llm-provider-credentials から読む（chart の既定。values-cloud が名前を変えない）。
  if ((values.services || {}).llmgateway && values.services.llmgateway.enabled) refs.push({ ref: 'microservices-platform/llm-provider-credentials', from: 'deploy/helm（chart の既定）' });
  return refs;
}

/** ExternalSecret の行き先（'ns/name' → 'ExternalSecret の出どころ'） */
function collectSupplied() {
  const out = new Map();
  const eso = path.join(CLOUD, 'vault', 'eso');
  for (const file of walk(eso, () => false)) {
    for (const d of docsIn(file)) {
      if (d.kind !== 'ExternalSecret') continue;
      const t = ((d.spec || {}).target || {}).name || d.metadata.name;
      const src = ((d.spec || {}).dataFrom || []).some((x) => x.sourceRef && x.sourceRef.generatorRef) ? 'PKI（VaultDynamicSecret）' : 'KV（cloud/）';
      out.set(`${d.metadata.namespace}/${t}`, src);
    }
  }
  return out;
}

function inventory() {
  const refs = collectRefs();
  const supplied = collectSupplied();
  const rows = new Map();
  for (const r of refs) {
    const row = rows.get(r.ref) || { ref: r.ref, from: new Set(), category: CATEGORY[r.ref], supplier: supplied.get(r.ref) };
    row.from.add(r.from);
    rows.set(r.ref, row);
  }
  return [...rows.values()].sort((a, b) => a.ref.localeCompare(b.ref));
}

function problems(rows, { strict = false } = {}) {
  const out = [];
  for (const r of rows) {
    if (r.category === undefined) out.push(`${r.ref}: 区分（ADR-0080 決定 1）が未定。secret-inventory.js の CATEGORY に足し、供給元を決める`);
    else if (!r.supplier && (strict || r.category !== 1)) out.push(`${r.ref}: 区分 ${r.category} なのに OpenBao からの供給元（ExternalSecret）が無い`);
  }
  return out;
}

function main(argv) {
  const strict = argv.includes('--strict');
  const rows = inventory();
  for (const r of rows) {
    process.stdout.write(`${r.ref}\t区分 ${r.category === undefined ? '?' : r.category}\t${r.supplier || '供給元なし'}\t${[...r.from].join(', ')}\n`);
  }
  const p = problems(rows, { strict });
  for (const x of p) process.stderr.write(`ERROR: ${x}\n`);
  if (p.length === 0) process.stdout.write(`OK: 参照 ${rows.length} 件を照らした${strict ? '（--strict: 全件に供給元がある）' : ''}\n`);
  return p.length === 0 ? 0 : 1;
}

module.exports = { inventory, problems, collectRefs, collectSupplied, CATEGORY };
if (require.main === module) process.exit(main(process.argv.slice(2)));
