#!/usr/bin/env node
'use strict';
/*
 * seed-tag-dictionary.js
 *
 * FR-06, FR-09, SC-05, SC-09 / Issue #705（ai-stock-trading 側の起票）:
 * タグ辞書（IADR-0152）へ、**外部ユニットが送ってくる静的タグ**を初期投入する。
 *
 * 背景:
 *   `POST /documents` は #635 でタグ辞書検証を持つに至った（辞書に無いタグは 400）。
 *   一方で **辞書へ行を入れる経路は `POST /tags` だけ**で、初期投入の仕組みは存在しなかった
 *   （EF の HasData も起動時シーダも helm 値も無い。実測）。結果として ai-stock-trading が送る
 *   文書は 100% が 400 で弾かれ、`KB 保存: 0/3 件` に縮退していた。
 *
 *   **送り手が自分で登録することはできない。** `POST /tags` は AdminOnly で、AST の
 *   サービスアカウント `ai-stock-trading-kb-writer` は `platform-operator` しか持たない
 *   （IADR-0075 が `platform-admin` の付与を明示的に断っている）。したがって登録は
 *   **受け手（基盤）側の初期投入**として行う。
 *
 * 方式（seed-abac-policies.js / seed-search-documents.js と同型）:
 *   - 単一情報源は **リポジトリ内の JSON**（deploy/local/tag-seed/tags.json）。
 *   - 投入は **管理 API 経由**（POST /tags）。**直 DB 書き込みはしない**。
 *   - **冪等**。既にある名前は送らない。競合で 409 が返っても失敗にしない
 *     （`CreateTagEndpoint` は重複を 409 と定めており、これは「既にある」の同義である）。
 *   - 認証は `abac-seeder`（client_credentials・platform-admin）を借りる。資格情報の解決は
 *     seed-abac-policies.js の実装をそのまま使う（写し取らない）。
 *
 * 実行方法:
 *   1) 経路B が稼働している状態で:
 *        node scripts/seed-tag-dictionary.js
 *      （kubectl port-forward を一時的に自分で張り、終了時に片付ける）
 *   2) 既に到達可能な URL があるなら port-forward を使わない:
 *        TAG_SEED_DOC_URL=http://localhost:5082 TAG_SEED_KC_URL=http://keycloak:8080 \
 *          node scripts/seed-tag-dictionary.js
 *   3) 何が投入されるかだけ見る（副作用なし）:
 *        node scripts/seed-tag-dictionary.js --dry-run
 *
 * 主な環境変数:
 *   TAG_SEED_DIR（既定 deploy/local/tag-seed）/ TAG_SEED_NS（既定 microservices-platform）
 *   TAG_SEED_INFRA_NS（既定 platform-infra）/ TAG_SEED_REALM（既定 platform）
 *   TAG_SEED_CLIENT_ID / TAG_SEED_CLIENT_SECRET（既定は seed-abac-policies.js と同じ解決）
 *
 * 終了コード: 0=投入済み（no-op を含む） / 1=失敗 / 2=前提未整備（k8s へ到達できない等）
 */

const fs = require('fs');
const path = require('path');
const { spawn, spawnSync } = require('child_process');

// `seed-abac-policies.js` は `require.main` ガードを持つので、require しても投入は走らない。
const abacSeed = require('./seed-abac-policies.js');

const env = (k, d) => process.env[k] || d;
const SEED_DIR = env('TAG_SEED_DIR', path.join(__dirname, '..', 'deploy', 'local', 'tag-seed'));
const NS = env('TAG_SEED_NS', 'microservices-platform');
const INFRA_NS = env('TAG_SEED_INFRA_NS', 'platform-infra');
const REALM = env('TAG_SEED_REALM', 'platform');
const CLIENT_ID = env('TAG_SEED_CLIENT_ID', abacSeed.CLIENT_ID);

const log = (m) => console.log(`[seed-tag-dictionary] ${m}`);
const warn = (m) => console.error(`[seed-tag-dictionary] ${m}`);

// client_secret は realm ファイル（単一情報源）から引く。値をここへ写さない（#984）。
const CLIENT_SECRET = (() => {
  if (process.env.TAG_SEED_CLIENT_SECRET) return process.env.TAG_SEED_CLIENT_SECRET;
  const fromRealm = abacSeed.clientSecretFromRealm(CLIENT_ID);
  if (fromRealm) return fromRealm;
  warn(
    `realm ファイルから client ${CLIENT_ID} の secret を読めませんでした。` +
      ' TAG_SEED_CLIENT_SECRET を指定してください。',
  );
  return '';
})();

// --- シードの読み込み -------------------------------------------------------
// 名前は `Tag.Normalize`（= Trim のみ・**大文字小文字を区別する**）に合わせて前後の空白だけ落とす。
// 正規化を増やさない —— 送り手が送る綴りと 1 文字でも違えば 400 に戻る。
function loadSeed() {
  const file = path.join(SEED_DIR, 'tags.json');
  const parsed = JSON.parse(fs.readFileSync(file, 'utf8'));
  const names = (parsed.tags || []).map((t) => String(t).trim()).filter((t) => t.length > 0);
  const unique = [...new Set(names)];
  if (unique.length !== names.length) {
    warn(`シードに重複があります（${names.length} → ${unique.length} 件へ畳みました）。`);
  }
  return unique;
}

// 既存の辞書に無い名前だけを返す（比較は正規化後・大文字小文字を区別する）。
function selectMissingTags(seedNames, existing) {
  const have = new Set((existing || []).map((t) => String(t.name).trim()));
  return seedNames.filter((n) => !have.has(n));
}

// --- 一時 port-forward（自分で張り、終了時に必ず片付ける） -------------------
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

async function fetchToken(kcUrl) {
  const confidential = abacSeed.isConfidentialInRealm(CLIENT_ID);
  if (confidential && !CLIENT_SECRET) {
    warn(
      `realm は client ${CLIENT_ID} を confidential としていますが、client_secret を解決できませんでした。` +
        ' TAG_SEED_CLIENT_SECRET を指定してください。',
    );
  }
  const form = abacSeed.buildTokenForm({ clientId: CLIENT_ID, clientSecret: CLIENT_SECRET });
  const res = await fetch(`${kcUrl}/realms/${REALM}/protocol/openid-connect/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: form,
  });
  if (!res.ok) {
    throw new Error(
      `Keycloak のトークン取得に失敗しました（${res.status}）。client ${CLIENT_ID} の` +
        ' serviceAccountsEnabled と secret、および service-account への platform-admin 付与を確認してください。',
    );
  }
  return (await res.json()).access_token;
}

async function listTags(docUrl, token) {
  const res = await fetch(`${docUrl}/tags`, { headers: { Authorization: `Bearer ${token}` } });
  if (!res.ok) {
    // 403 は「読みのロールが足りない」。何が足りないかを言う（無音で 0 件へ落ちない）。
    throw new Error(
      `GET /tags に失敗しました（${res.status}）。client ${CLIENT_ID} の service-account が` +
        ' platform-admin もしくは platform-operator を持つか確認してください。',
    );
  }
  return (await res.json()).tags || [];
}

// 1 件登録する。**409 は「既にある」であって失敗ではない**（CreateTagEndpoint の契約）。
async function createTag(docUrl, token, name) {
  const res = await fetch(`${docUrl}/tags`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ name }),
  });
  if (res.status === 409) return 'exists';
  if (!res.ok) {
    throw new Error(`POST /tags に失敗しました（${res.status}）: ${name} / ${await res.text()}`);
  }
  return 'created';
}

async function main(argv) {
  const dryRun = argv.includes('--dry-run');
  const seedNames = loadSeed();
  log(`シード: タグ ${seedNames.length} 件（${SEED_DIR}）`);

  if (dryRun) {
    for (const n of seedNames) log(`  [タグ] ${n}`);
    log('--dry-run のため投入しません。');
    return 0;
  }

  let docUrl = env('TAG_SEED_DOC_URL', '');
  let kcUrl = env('TAG_SEED_KC_URL', '');
  if (!docUrl || !kcUrl) {
    if (spawnSync('kubectl', ['cluster-info'], { stdio: 'ignore' }).status !== 0) {
      warn('k8s に到達できません（kubectl cluster-info が失敗）。経路B を起動するか、');
      warn('TAG_SEED_DOC_URL / TAG_SEED_KC_URL で接続先を直接指定してください。');
      return 2;
    }
    if (!docUrl) {
      portForward(NS, 'document-service', 18094, 8080);
      docUrl = 'http://localhost:18094';
    }
    if (!kcUrl) {
      portForward(INFRA_NS, 'keycloak', 18095, 8080);
      kcUrl = 'http://localhost:18095';
    }
    const ok =
      (await waitReachable(`${docUrl}/health/live`)) &&
      (await waitReachable(`${kcUrl}/realms/${REALM}/.well-known/openid-configuration`));
    if (!ok) {
      warn('port-forward 経由で document-service / keycloak へ到達できませんでした。');
      return 2;
    }
  }
  log(`接続先: document=${docUrl} / keycloak=${kcUrl}`);

  const token = await fetchToken(kcUrl);
  log(`管理トークンを取得しました（client_credentials・client=${CLIENT_ID}）`);

  const existing = await listTags(docUrl, token);
  const missing = selectMissingTags(seedNames, existing);
  log(`辞書: 既存 ${existing.length} 件 / 追加 ${missing.length} 件`);

  let created = 0;
  let raced = 0;
  for (const name of missing) {
    const outcome = await createTag(docUrl, token, name);
    if (outcome === 'created') {
      created += 1;
      log(`  + タグ ${name}`);
    } else {
      raced += 1;
      log(`  = タグ ${name}（既にありました）`);
    }
  }

  if (created === 0 && raced === 0) {
    log('投入済みのため変更はありません（冪等・no-op）。');
  } else {
    log(`投入しました（新規 ${created} 件 / 既存 ${raced} 件）。外部ユニットの文書が 400 で弾かれなくなります。`);
  }
  return 0;
}

module.exports = { selectMissingTags, loadSeed, CLIENT_ID };

if (require.main === module) {
  main(process.argv.slice(2))
    .then((code) => process.exit(code))
    .catch((e) => {
      warn(String((e && e.message) || e));
      process.exit(1);
    });
}
