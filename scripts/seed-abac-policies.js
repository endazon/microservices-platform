#!/usr/bin/env node
'use strict';
/*
 * seed-abac-policies.js
 *
 * FR-05, FR-09, UC-05 / Issue #517: 経路B（dev）へ ABAC の属性辞書とポリシーを初期投入する。
 *
 * 背景:
 *   AuthorizationService はポリシーが 1 件も無いと deny-by-default で縮退する（AbacEvaluator）。
 *   これは仕様どおりだが、投入経路が一度も実行されていない環境では **認証を通しても文書一覧・
 *   横断検索が常に空**になり、「壊れている」のと区別が付かない（#517。実測は
 *   .ai-context/specs/20260805_issue-466_oidc-edge-flow-verification.md）。本スクリプトは dev の初期値を
 *   宣言的ファイル（deploy/local/abac-seed/）から投入し、その状態を再現可能にする。
 *
 * 方式（IADR-0133）:
 *   - 単一情報源は **リポジトリ内の JSON**（realm.json / minio-oidc の policies と同型）。
 *   - 投入は **管理 API 経由**（POST /authz/attributes, /authz/policies）。**直 DB 書き込みはしない**——
 *     API 側の検証（AbacValidation）を素通りさせないため。
 *   - **冪等**。同じ key+scope の属性・同じ name のポリシーが既にあれば作成しない（既定）。
 *   - 認証は Keycloak の直接付与（client `bff`・管理者ユーザー）で取得したトークンを使う。
 *
 * 実行方法:
 *   1) 経路B が稼働している状態で:
 *        node scripts/seed-abac-policies.js
 *      （kubectl port-forward を一時的に自分で張り、終了時に片付ける）
 *   2) 既に到達可能な URL があるなら port-forward を使わない:
 *        ABAC_SEED_AUTHZ_URL=http://localhost:5081 ABAC_SEED_KC_URL=http://keycloak:8080 \
 *          node scripts/seed-abac-policies.js
 *   3) 何が投入されるかだけ見る（副作用なし）:
 *        node scripts/seed-abac-policies.js --dry-run
 *
 * 主な環境変数:
 *   ABAC_SEED_DIR（既定 deploy/local/abac-seed）/ ABAC_SEED_NS（既定 microservices-platform）
 *   ABAC_SEED_INFRA_NS（既定 platform-infra）/ ABAC_SEED_REALM（既定 platform）
 *   ABAC_SEED_CLIENT_ID（既定 bff）/ ABAC_SEED_USER・ABAC_SEED_PASSWORD（既定 admin/admin）
 *
 * 終了コード: 0=投入済み（no-op を含む） / 1=失敗 / 2=前提未整備（k8s へ到達できない等）
 */

const fs = require('fs');
const path = require('path');
const { spawn, spawnSync } = require('child_process');

const env = (k, d) => process.env[k] || d;
const SEED_DIR = env('ABAC_SEED_DIR', path.join(__dirname, '..', 'deploy', 'local', 'abac-seed'));
const NS = env('ABAC_SEED_NS', 'microservices-platform');
const INFRA_NS = env('ABAC_SEED_INFRA_NS', 'platform-infra');
const REALM = env('ABAC_SEED_REALM', 'platform');
const CLIENT_ID = env('ABAC_SEED_CLIENT_ID', 'bff');
const USER = env('ABAC_SEED_USER', 'admin');
const PASSWORD = env('ABAC_SEED_PASSWORD', 'admin');

const log = (s) => process.stdout.write(`${s}\n`);
const warn = (s) => process.stderr.write(`${s}\n`);

// --- 一時 port-forward（自分で張り、終了時に必ず片付ける） -------------------------
const forwards = [];
function portForward(ns, svc, localPort, remotePort) {
  const child = spawn('kubectl', ['-n', ns, 'port-forward', `svc/${svc}`, `${localPort}:${remotePort}`], {
    stdio: ['ignore', 'ignore', 'ignore'],
  });
  forwards.push(child);
  return child;
}
function cleanup() {
  for (const c of forwards) {
    try {
      c.kill();
    } catch {
      /* 片付けの失敗で終了コードを変えない */
    }
  }
  forwards.length = 0;
}
process.on('exit', cleanup);
process.on('SIGINT', () => {
  cleanup();
  process.exit(1);
});

async function waitReachable(url, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      await fetch(url, { signal: AbortSignal.timeout(2000) });
      return true;
    } catch {
      await new Promise((r) => setTimeout(r, 500));
    }
  }
  return false;
}

// --- HTTP ヘルパ ------------------------------------------------------------------
async function getJson(url, token) {
  const res = await fetch(url, { headers: { Authorization: `Bearer ${token}` } });
  if (!res.ok) throw new Error(`GET ${url} が失敗しました（${res.status}）`);
  return res.json();
}
async function postJson(url, token, body) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`POST ${url} が失敗しました（${res.status}）: ${text.slice(0, 300)}`);
  return text ? JSON.parse(text) : null;
}

async function fetchToken(kcUrl) {
  const form = new URLSearchParams({
    grant_type: 'password',
    client_id: CLIENT_ID,
    username: USER,
    password: PASSWORD,
    scope: 'openid',
  });
  const res = await fetch(`${kcUrl}/realms/${REALM}/protocol/openid-connect/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: form,
  });
  if (!res.ok) {
    throw new Error(`Keycloak のトークン取得に失敗しました（${res.status}）。ユーザー ${USER} と client ${CLIENT_ID} を確認してください。`);
  }
  return (await res.json()).access_token;
}

// --- シードの読み込み --------------------------------------------------------------
function loadSeed(name) {
  const file = path.join(SEED_DIR, name);
  if (!fs.existsSync(file)) throw new Error(`シードファイルがありません: ${file}`);
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

// 既存と突き合わせて「まだ無いもの」だけを返す（冪等性の核）。
function selectMissingAttributes(seed, existing) {
  const key = (a) => `${String(a.key).toLowerCase()}::${String(a.scope || 'document').toLowerCase()}`;
  const have = new Set(existing.map(key));
  return seed.filter((a) => !have.has(key(a)));
}
function selectMissingPolicies(seed, existing) {
  const have = new Set(existing.map((p) => String(p.name).toLowerCase()));
  return seed.filter((p) => !have.has(String(p.name).toLowerCase()));
}

async function main(argv) {
  const dryRun = argv.includes('--dry-run');
  const attributes = loadSeed('attributes.json').attributes || [];
  const policies = loadSeed('policies.json').policies || [];
  log(`シード: 属性 ${attributes.length} 件 / ポリシー ${policies.length} 件（${SEED_DIR}）`);

  if (dryRun) {
    // 副作用なし。投入予定の中身をそのまま見せる（何が入るか分からないまま実行させない）。
    for (const a of attributes) log(`  [属性] ${a.scope}/${a.key} = ${JSON.stringify(a.allowedValues)}`);
    for (const p of policies) {
      log(`  [ポリシー] ${p.name}（${p.action}）`);
      log(`      利用者条件 ${JSON.stringify(p.userConditions)} → 文書条件 ${JSON.stringify(p.documentConditions)}`);
    }
    log('--dry-run のため投入しません。');
    return 0;
  }

  // 接続先を決める。URL が与えられていなければ port-forward を自分で張る。
  let authzUrl = env('ABAC_SEED_AUTHZ_URL', '');
  let kcUrl = env('ABAC_SEED_KC_URL', '');
  if (!authzUrl || !kcUrl) {
    if (spawnSync('kubectl', ['cluster-info'], { stdio: 'ignore' }).status !== 0) {
      warn('k8s に到達できません（kubectl cluster-info が失敗）。経路B を起動するか、');
      warn('ABAC_SEED_AUTHZ_URL / ABAC_SEED_KC_URL で接続先を直接指定してください。');
      return 2;
    }
    if (!authzUrl) {
      portForward(NS, 'authorization-service', 18091, 8080);
      authzUrl = 'http://localhost:18091';
    }
    if (!kcUrl) {
      portForward(INFRA_NS, 'keycloak', 18090, 8080);
      kcUrl = 'http://localhost:18090';
    }
    const ok =
      (await waitReachable(`${authzUrl}/authz/policies`)) &&
      (await waitReachable(`${kcUrl}/realms/${REALM}/.well-known/openid-configuration`));
    if (!ok) {
      warn('port-forward 経由で authorization-service / keycloak へ到達できませんでした。');
      return 2;
    }
  }
  log(`接続先: authz=${authzUrl} / keycloak=${kcUrl}`);

  const token = await fetchToken(kcUrl);
  log(`管理トークンを取得しました（user=${USER}）`);

  // ---- 属性辞書（ポリシーの検証に使われるため先に入れる） ----
  const existingAttrs = await getJson(`${authzUrl}/authz/attributes`, token);
  const missingAttrs = selectMissingAttributes(attributes, existingAttrs);
  log(`属性辞書: 既存 ${existingAttrs.length} 件 / 追加 ${missingAttrs.length} 件`);
  for (const a of missingAttrs) {
    await postJson(`${authzUrl}/authz/attributes`, token, a);
    log(`  + 属性 ${a.scope}/${a.key}`);
  }

  // ---- ポリシー ----
  const existingPolicies = await getJson(`${authzUrl}/authz/policies`, token);
  const missingPolicies = selectMissingPolicies(policies, existingPolicies);
  log(`ポリシー: 既存 ${existingPolicies.length} 件 / 追加 ${missingPolicies.length} 件`);
  for (const p of missingPolicies) {
    await postJson(`${authzUrl}/authz/policies`, token, p);
    log(`  + ポリシー ${p.name}`);
  }

  if (missingAttrs.length === 0 && missingPolicies.length === 0) {
    log('投入済みのため変更はありません（冪等・no-op）。');
  } else {
    log('投入しました。認証済みの利用者に文書が見えるようになります。');
  }
  return 0;
}

module.exports = { selectMissingAttributes, selectMissingPolicies };

if (require.main === module) {
  main(process.argv.slice(2))
    .then((code) => {
      cleanup();
      process.exit(code);
    })
    .catch((e) => {
      warn(`[seed-abac-policies] ${e.message}`);
      cleanup();
      process.exit(1);
    });
}
