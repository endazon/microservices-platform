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
 *   ABAC_SEED_CLIENT_ID（既定 bff）/ ABAC_SEED_USER（既定 admin）
 *   ABAC_SEED_PASSWORD（既定は realm ファイルから引く。値をここへ写さない。#972）
 *   ABAC_SEED_CLIENT_SECRET（既定は realm ファイルから引く。confidential のときだけ送る。#984）
 *   ABAC_SEED_REALM_FILE（既定 deploy/keycloak/microservices-platform-realm.json）
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

const log = (s) => process.stdout.write(`${s}\n`);
const warn = (s) => process.stderr.write(`${s}\n`);

// 🔴 パスワードは realm ファイル（単一情報源）から引く。既定値としてここへ写さない（#972）。
//
// 経緯: 既定はユーザ名と同じ短い値を直書きしていた。#933 が realm のパスワードを
// パスワードポリシー（12 文字以上）適合の値へ一斉に変えたとき、この既定は追随せず
// **投入が 401 で失敗するようになった**（値そのものはここへ書かない。realm ファイルが正本である）。
// しかも `k8s-local-up.sh` の ABACSEED は best-effort（失敗しても WARN で通す）ため、
// **壊れていることが誰にも見えなかった** —— ポリシーが 0 件のまま ABAC は deny へ倒れ、
// 画面は空になるが、それは仕様どおりの deny-by-default とまったく区別が付かない。
//
// 値を写すと同じ drift がまた起きるので、**realm ファイルから読む**（構造的な根拠にする）。
// 経路B の realm は平文でパスワードを持つ（dev 専用。本番の値は Vault / ESO 側にある）。
const REALM_FILE = env(
  'ABAC_SEED_REALM_FILE',
  path.join(__dirname, '..', 'deploy', 'keycloak', 'microservices-platform-realm.json'),
);
function readRealm() {
  try {
    return JSON.parse(fs.readFileSync(REALM_FILE, 'utf8'));
  } catch {
    return null;
  }
}
function passwordFromRealm(username) {
  const realm = readRealm();
  if (!realm) return null;
  const user = (realm.users || []).find((u) => u.username === username);
  const cred = ((user || {}).credentials || []).find((c) => c.type === 'password');
  return (cred && cred.value) || null;
}

// 🔴 confidential クライアントは client_secret を要求する（#984）。
//
// 経緯: `#439`（BFF セッション / Token Handler）が `bff` を **publicClient=false** へ変えた。
// 投入器は client_id だけで password grant を送っていたため、Keycloak が 401（invalid_client）を返した。
// **realm の変更に投入器が追随しなかったのは今日 2 回目**で、1 回目（`#933` のパスワード一斉変更）は
// 値の写し取り、2 回目は**クライアントの種別という構造の変化**である。値を直すだけでは次も落ちる。
//
// realm の全 9 クライアントを実測したところ **`directAccessGrantsEnabled=true` は `bff` だけ**なので、
// 別のクライアントへ逃げる道は無い。secret を送るしかない。
function clientFromRealm(clientId) {
  const realm = readRealm();
  if (!realm) return null;
  return (realm.clients || []).find((c) => c.clientId === clientId) || null;
}
function clientSecretFromRealm(clientId) {
  const c = clientFromRealm(clientId);
  return (c && c.secret) || null;
}
// realm が confidential と言っているか。判定できないときは null（＝分からない）を返す。
function isConfidentialInRealm(clientId) {
  const c = clientFromRealm(clientId);
  if (!c) return null;
  return c.publicClient === false;
}

// トークン要求の本体を組み立てる。**純粋関数にして試験できるようにする**（決定 2）。
// confidential なら client_secret を載せ、public なら載せない。
function buildTokenForm({ clientId, username, password, confidential, clientSecret }) {
  const form = new URLSearchParams({
    grant_type: 'password',
    client_id: clientId,
    username,
    password,
    scope: 'openid',
  });
  if (confidential && clientSecret) form.set('client_secret', clientSecret);
  return form;
}
const PASSWORD = (() => {
  if (process.env.ABAC_SEED_PASSWORD) return process.env.ABAC_SEED_PASSWORD;
  const fromRealm = passwordFromRealm(USER);
  if (fromRealm) return fromRealm;
  // 黙って既定値へ落ちない。落ちた事実を出す（無音の失敗がこの不具合の本体だった）。
  warn(
    `[seed-abac-policies] realm ファイルから ${USER} のパスワードを読めませんでした（${REALM_FILE}）。` +
      ' ABAC_SEED_PASSWORD を指定してください。',
  );
  return '';
})();
// client_secret も同じ作法で引く（値をここへ写さない。#984）。
const CLIENT_SECRET = process.env.ABAC_SEED_CLIENT_SECRET || clientSecretFromRealm(CLIENT_ID) || '';

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
  const confidential = isConfidentialInRealm(CLIENT_ID);
  const clientSecret = CLIENT_SECRET;
  if (confidential && !clientSecret) {
    // 黙って secret 無しで投げない。投げれば 401 になるが、理由が読み手に見えない。
    warn(
      `[seed-abac-policies] realm は client ${CLIENT_ID} を confidential としていますが、` +
        ' client_secret を解決できませんでした。ABAC_SEED_CLIENT_SECRET を指定してください。',
    );
  }
  const form = buildTokenForm({
    clientId: CLIENT_ID,
    username: USER,
    password: PASSWORD,
    confidential,
    clientSecret,
  });
  const res = await fetch(`${kcUrl}/realms/${REALM}/protocol/openid-connect/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: form,
  });
  if (!res.ok) {
    const kind = confidential === null ? '（realm から種別を判定できず）' : confidential ? '（confidential）' : '（public）';
    throw new Error(
      `Keycloak のトークン取得に失敗しました（${res.status}）。ユーザー ${USER} と client ${CLIENT_ID}${kind} を確認してください。`,
    );
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

module.exports = {
  selectMissingAttributes,
  selectMissingPolicies,
  passwordFromRealm,
  clientSecretFromRealm,
  isConfidentialInRealm,
  buildTokenForm,
  REALM_FILE,
  CLIENT_ID,
};

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
