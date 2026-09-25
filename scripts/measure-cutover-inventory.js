#!/usr/bin/env node
'use strict';
/*
 * measure-cutover-inventory.js
 *
 * NFR-05, issue #457, IADR-0459: 再実装版への切替（破棄と再構築）の**実在量の実測と、作り直しの検証**。
 *
 * 背景:
 *   2026-08-16 の利用者裁定（2026-09-25 にオーナーが再確認）で、6 資産（platform アプリ DB / Keycloak realm /
 *   Qdrant / MinIO / Wiki.js / 可観測性データ）はすべて破棄し、realm は realm.json から作り直すと決まった。
 *   #457 は「件数突合スクリプトを再実行可能な形で残す（measure-abac-combinations.js の --json / --dump /
 *   --input で収集と集計を分離する型を踏襲する）」を残作業に挙げている。本スクリプトがその手段である。
 *
 * 🔴 何を検証するか（IADR-0459 決定 4）:
 *   「空であること」ではなく**「作り直されたこと」を時刻で見る**。再構築の直後から新しい書き込みは始まり得るので、
 *   件数 0 は脆い判定になる。代わりに、破棄した側（MSP の DB・realm の人間の利用者・作り直した PVC・
 *   Prometheus の最古サンプル）が --since 以降に作られたことを見る。
 *   さらに**触らない側**（同じ Postgres に同居する AST の DB・postgres-data / keycloak-data / vault-data の PVC）が
 *   --since より前のままであることを見る（陰性対照。作り直しすぎを捕まえる）。
 *   🔴 **「消えた」は作成時刻では見えない**（消えたものには時刻が無い）。--baseline（切替前の実測）を渡すと、切替前に
 *   在った AST の DB と、作り直しの対象でない realm（master・AST realm ほか）が切替後に 1 つでも欠けていれば fail にする。
 *   **--baseline が無いと消失は検出できない**（AST の DB が無いことは「未配備」と区別できず skip になる）。
 *   Prometheus の最古サンプル（headStats.minTime）は参考表示に留める —— 古いブロックが在ると head の最古は
 *   TSDB 全体の最古ではない。作り直しの判定は prometheus-data の PVC の作成時刻で行う。
 *
 * 性質:
 *   - **読み取り専用**。kubectl get / exec の SELECT・kcadm get・ls・rabbitmqctl list_queues・HTTP GET だけを行う。
 *   - --print-recreate-sql は SQL を**標準出力へ書くだけ**で実行しない（DB 名の単一情報源は
 *     deploy/local/infra/postgres.yaml の初期化 SQL。文書へ書き写さないためにここから導出する）。
 *   - 外部依存ゼロ（Node 標準ライブラリのみ）。分類・判定・突合は純関数で、scripts.repo.test.js が単体試験する。
 *   - 🔴 **収集部（kubectl を叩く部分）は稼働環境で未検証である**（本スクリプトの作成時、稼働クラスタには触れていない）。
 *     初回の事前実測がその検証を兼ねる。形が合わなければ例外で止まる（黙って 0 件にしない）。
 *
 * 実行方法（手順の全体は docs/migration/cutover-discard-and-rebuild.md）:
 *   事前実測（破棄の直前。生データを保存する）:
 *     node scripts/measure-cutover-inventory.js --dump before.json
 *   再構築の後の検証（破棄を始めた時刻を --since へ渡す。fail が 1 件でもあれば終了コード 1）:
 *     node scripts/measure-cutover-inventory.js --since 2026-10-01T01:00:00Z --baseline before.json --dump after.json
 *   保存済みの生データから判定だけやり直す:
 *     node scripts/measure-cutover-inventory.js --input after.json --since 2026-10-01T01:00:00Z --baseline before.json
 *   MSP の DB を作り直す SQL を表示する（実行はしない）:
 *     node scripts/measure-cutover-inventory.js --print-recreate-sql
 *
 * 主な環境変数（既定は経路B の値）:
 *   CUTOVER_INFRA_NS=platform-infra / CUTOVER_MSP_NS=microservices-platform
 *   CUTOVER_PG_USER=postgres（DB の作成時刻を読むのにスーパーユーザが要る）
 *   CUTOVER_REALM=platform / CUTOVER_KC_ADMIN_USER=admin / CUTOVER_KC_ADMIN_PASSWORD（未設定なら admin）
 *   CUTOVER_QDRANT_URL / CUTOVER_PROM_URL（未設定なら API サーバのサービスプロキシ経由で GET する。
 *     メッシュの STRICT mTLS でプロキシが通らないときは kubectl port-forward して URL を渡す）
 *
 * 出力: 既定は人が読める要約。--json で機械可読、--dump <path> で収集した生データを保存する。
 */

const { spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const REPO = path.resolve(__dirname, '..');
const POSTGRES_INIT = path.join(REPO, 'deploy', 'local', 'infra', 'postgres.yaml');
const REALM_FILE = path.join(REPO, 'deploy', 'keycloak', 'microservices-platform-realm.json');
const ABAC_SEED_DIR = path.join(REPO, 'deploy', 'local', 'abac-seed');
const PIPELINE_FILE = path.join(REPO, 'deploy', 'helm', 'microservices-platform', 'files', 'pipeline.json');

// ---------------------------------------------------------------------------
// 破棄の境界（IADR-0459 決定 2）。PVC は「作り直す」と「触らない」の 2 集合に分ける。
// ---------------------------------------------------------------------------

// 作り直す PVC。`optional: true` は配備の選択（可観測性の永続化）で存在しないことがあるもの。
const RECREATED_PVCS = [
  { ns: 'infra', name: 'qdrant-storage' },
  { ns: 'msp', name: 'minio-data' },
  { ns: 'msp', name: 'wiki-js-data' },
  { ns: 'infra', name: 'prometheus-data', optional: true },
  { ns: 'infra', name: 'loki-data', optional: true },
  { ns: 'infra', name: 'tempo-data', optional: true },
];
// 触らない PVC。消すと 6 資産の外（AST の DB・master / AST realm・Vault の秘密）まで消える。
const KEPT_PVCS = [
  { ns: 'infra', name: 'postgres-data' },
  { ns: 'infra', name: 'keycloak-data' },
  { ns: 'infra', name: 'vault-data', optional: true },
];

// 旧 realm 名（IADR-0197 で `platform` へ改名済み。稼働クラスタに残っていたことが 2026-08-16 に実測されている）。
const LEGACY_REALM = 'microservices-platform';
const SERVICE_ACCOUNT_PREFIX = 'service-account-';

// ---------------------------------------------------------------------------
// 単一情報源の読み取り（純関数。テキスト / オブジェクトを受け取る）
// ---------------------------------------------------------------------------

// deploy/local/infra/postgres.yaml の初期化 SQL から DB を分類する。
// MSP = `ALTER DATABASE <db> OWNER TO kp;`、AST = `CREATE DATABASE <db> OWNER ai;`。
// 🔴 どちらにも当たらない CREATE DATABASE があれば例外にする —— 分類できない DB を黙って「触らない側」へ
// 倒すと、次に DB を足した人の DB が破棄の対象から静かに漏れる（逆に MSP 側へ倒すと AST の DB を消す）。
function classifyDatabases(initSqlText) {
  const msp = [];
  const ast = [];
  for (const m of initSqlText.matchAll(/ALTER DATABASE\s+([a-z0-9_]+)\s+OWNER TO\s+kp\s*;/gi)) msp.push(m[1]);
  for (const m of initSqlText.matchAll(/CREATE DATABASE\s+([a-z0-9_]+)\s+OWNER\s+ai\s*;/gi)) ast.push(m[1]);
  const created = [...initSqlText.matchAll(/CREATE DATABASE\s+([a-z0-9_]+)/gi)].map((m) => m[1]);
  const unclassified = created.filter((db) => !msp.includes(db) && !ast.includes(db));
  if (unclassified.length) {
    throw new Error(`初期化 SQL に MSP（OWNER TO kp）とも AST（OWNER ai）とも分類できない DB がある: ${unclassified.join(', ')}`);
  }
  const both = msp.filter((db) => ast.includes(db));
  if (both.length) throw new Error(`MSP と AST の両方に分類された DB がある: ${both.join(', ')}`);
  if (!msp.length) throw new Error('初期化 SQL から MSP の DB を 1 本も読めなかった（0 件走査は fail）');
  return { msp, ast };
}

// MSP の DB を作り直す SQL（表示用）。WITH (FORCE) は PostgreSQL 13 以降（稼働は 16）。
function recreateSql(mspDatabases) {
  const lines = ['-- MSP の DB だけを作り直す（AST の DB は含まない）。postgres（スーパーユーザ）で postgres DB に接続して実行する。'];
  for (const db of mspDatabases) {
    lines.push(`DROP DATABASE IF EXISTS ${db} WITH (FORCE);`);
    lines.push(`CREATE DATABASE ${db} OWNER kp;`);
  }
  return `${lines.join('\n')}\n`;
}

// realm.json から宣言を読む。人間の利用者は service-account- で始まらないもの。
function declaredRealm(realmJson) {
  const users = (realmJson.users || []).map((u) => String(u.username).toLowerCase());
  return {
    realm: realmJson.realm,
    humanUsers: users.filter((u) => !u.startsWith(SERVICE_ACCOUNT_PREFIX)).sort(),
    clients: (realmJson.clients || []).map((c) => c.clientId).sort(),
  };
}

// ABAC の seed（deploy/local/abac-seed/）。
function declaredAbacSeed(attributesJson, policiesJson) {
  return {
    attributeKeys: (attributesJson.attributes || []).map((a) => a.key).sort(),
    policyNames: (policiesJson.policies || []).map((p) => p.name).sort(),
  };
}

// MSP のキュー名の接頭辞。キュー名は `<サービス>.<キュー>`（WolverineExtensions.PlatformQueueName）であり、
// サービスは pipeline.json の steps が宣言する。
function mspQueuePrefixes(pipelineJson) {
  return [...new Set((pipelineJson.steps || []).map((s) => `${s.service}.`))].sort();
}

// ---------------------------------------------------------------------------
// 収集結果の読み取り（純関数）
// ---------------------------------------------------------------------------

// `ls -R /data`（MinIO の単一ドライブ）からバケットとオブジェクト数を数える。
// オブジェクト 1 つは `<bucket>/<key...>/xl.meta` のディレクトリで表される（版は xl.meta の中に入る）。
// `.minio.sys` 配下はメタデータであり数えない。
function parseMinioListing(lsText) {
  let current = null;
  const buckets = new Set();
  let objects = 0;
  for (const raw of lsText.split(/\r?\n/)) {
    const line = raw.trimEnd();
    const header = /^(\/data(?:\/.*)?):$/.exec(line);
    if (header) {
      current = header[1];
      const rel = current.replace(/^\/data\/?/, '');
      const top = rel.split('/')[0];
      if (top && top !== '.minio.sys') buckets.add(top);
      continue;
    }
    if (line === 'xl.meta' && current && !/^\/data\/\.minio\.sys(\/|$)/.test(current) && current !== '/data') {
      objects += 1;
    }
  }
  return { buckets: [...buckets].sort(), objects };
}

function toMillis(v) {
  if (v === null || v === undefined || v === '') return null;
  if (typeof v === 'number') return v;
  const t = Date.parse(v);
  return Number.isNaN(t) ? null : t;
}

// ---------------------------------------------------------------------------
// 判定（純関数。--since の時刻で作り直しを見る）
// ---------------------------------------------------------------------------

function finding(asset, check, status, detail) {
  return { asset, check, status, detail };
}

function evaluate(data, expected, sinceIso, before = null) {
  const since = toMillis(sinceIso);
  if (since === null) throw new Error(`--since を時刻として読めない: ${sinceIso}`);
  const out = [];
  const pvcKey = (p) => `${p.namespace}/${p.name}`;
  const pvcs = new Map((data.pvcs || []).map((p) => [pvcKey(p), p]));
  const nsOf = (p) => (p.ns === 'infra' ? data.namespaces.infra : data.namespaces.msp);

  for (const p of RECREATED_PVCS) {
    const key = `${nsOf(p)}/${p.name}`;
    const live = pvcs.get(key);
    if (!live) {
      out.push(finding('PVC', `${key} を作り直した`, p.optional ? 'skip' : 'fail', p.optional ? '存在しない（配備で無効）' : '存在しない'));
      continue;
    }
    const t = toMillis(live.created);
    out.push(finding('PVC', `${key} を作り直した`, t !== null && t >= since ? 'ok' : 'fail', `作成 ${live.created}`));
  }
  for (const p of KEPT_PVCS) {
    const key = `${nsOf(p)}/${p.name}`;
    const live = pvcs.get(key);
    if (!live) {
      out.push(finding('PVC', `${key} を消していない`, p.optional ? 'skip' : 'fail', p.optional ? '存在しない（配備で無効）' : '存在しない'));
      continue;
    }
    const t = toMillis(live.created);
    out.push(finding('PVC', `${key} を消していない`, t !== null && t < since ? 'ok' : 'fail', `作成 ${live.created}`));
  }

  const dbCreated = new Map((data.postgres?.databases || []).map((d) => [d.db, toMillis(d.created)]));
  for (const db of expected.databases.msp) {
    const t = dbCreated.get(db);
    if (t === undefined) out.push(finding('PostgreSQL', `MSP の DB ${db} を作り直した`, 'fail', '存在しない'));
    else out.push(finding('PostgreSQL', `MSP の DB ${db} を作り直した`, t !== null && t >= since ? 'ok' : 'fail', `作成 ${new Date(t).toISOString()}`));
  }
  const astBefore = before ? new Set((before.postgres?.databases || []).map((d) => d.db)) : null;
  for (const db of expected.databases.ast) {
    const t = dbCreated.get(db);
    if (t === undefined && astBefore && astBefore.has(db)) {
      out.push(finding('PostgreSQL', `AST の DB ${db} を消していない`, 'fail', '切替前に在ったが切替後に無い（消しすぎ）'));
    } else if (t === undefined) {
      out.push(finding('PostgreSQL', `AST の DB ${db} を消していない`, 'skip',
        astBefore ? '切替前から無い（AST 未配備）' : '存在しない（--baseline が無いので消失と未配備を区別できない）'));
    } else out.push(finding('PostgreSQL', `AST の DB ${db} を消していない`, t !== null && t < since ? 'ok' : 'fail', `作成 ${new Date(t).toISOString()}`));
  }
  if (!before) {
    out.push(finding('基準', '切替前の実測（--baseline）で消失を見た', 'skip', '--baseline が無い。AST の DB と realm の消失は検出していない'));
  }

  const authz = data.postgres?.authz;
  if (!authz) {
    out.push(finding('PostgreSQL', 'authz_svc が seed と一致する', 'fail', '読めなかった'));
  } else {
    const liveKeys = [...authz.attributeKeys].sort();
    const liveNames = [...authz.policyNames].sort();
    const same = (a, b) => a.length === b.length && a.every((v, i) => v === b[i]);
    out.push(finding('PostgreSQL', 'authz_svc の属性辞書が seed と一致する', same(liveKeys, expected.abac.attributeKeys) ? 'ok' : 'fail',
      `稼働 ${liveKeys.length} / seed ${expected.abac.attributeKeys.length}`));
    out.push(finding('PostgreSQL', 'authz_svc のポリシーが seed と一致する', same(liveNames, expected.abac.policyNames) ? 'ok' : 'fail',
      `稼働 ${liveNames.length} / seed ${expected.abac.policyNames.length}`));
  }
  const wikiPages = (data.postgres?.tables || []).find((t) => t.db === 'wikijs' && t.table === 'pages');
  out.push(finding('Wiki.js', 'ページが 0 件', wikiPages ? (Number(wikiPages.rows) === 0 ? 'ok' : 'fail') : 'fail',
    wikiPages ? `${wikiPages.rows} 件` : 'wikijs.pages を読めなかった'));

  const kc = data.keycloak || {};
  const realms = kc.realms || [];
  out.push(finding('Keycloak', `realm ${expected.realm.realm} がある`, realms.includes(expected.realm.realm) ? 'ok' : 'fail', realms.join(', ')));
  out.push(finding('Keycloak', `旧名 ${LEGACY_REALM} が無い`, realms.includes(LEGACY_REALM) ? 'fail' : 'ok', realms.join(', ')));
  out.push(finding('Keycloak', 'master realm を消していない', realms.includes('master') ? 'ok' : 'fail', realms.join(', ')));
  if (before) {
    const kept = (before.keycloak?.realms || []).filter((r) => r !== expected.realm.realm && r !== LEGACY_REALM && r !== 'master');
    for (const r of kept) {
      out.push(finding('Keycloak', `作り直しの対象でない realm ${r} を消していない`, realms.includes(r) ? 'ok' : 'fail',
        realms.includes(r) ? '切替前後とも在る' : '切替前に在ったが切替後に無い（消しすぎ）'));
    }
  }
  const humans = (kc.users || []).filter((u) => !String(u.username).toLowerCase().startsWith(SERVICE_ACCOUNT_PREFIX));
  const stale = humans.filter((u) => !(toMillis(u.createdTimestamp) >= since)).map((u) => u.username);
  out.push(finding('Keycloak', '人間の利用者はすべて作り直し後に作られた', stale.length ? 'fail' : 'ok',
    stale.length ? `作り直し前の利用者: ${stale.join(', ')}` : `${humans.length} 人`));
  const liveHumanNames = humans.map((u) => String(u.username).toLowerCase());
  const missing = expected.realm.humanUsers.filter((u) => !liveHumanNames.includes(u));
  out.push(finding('Keycloak', 'realm.json の seed 利用者がそろっている', missing.length ? 'fail' : 'ok',
    missing.length ? `欠け: ${missing.join(', ')}` : `${expected.realm.humanUsers.length} 人`));
  const liveClients = kc.clients || [];
  const missingClients = expected.realm.clients.filter((c) => !liveClients.includes(c));
  out.push(finding('Keycloak', 'realm.json のクライアントがそろっている', missingClients.length ? 'fail' : 'ok',
    missingClients.length ? `欠け: ${missingClients.join(', ')}` : `${expected.realm.clients.length} 件`));

  const points = (data.qdrant?.collections || []).reduce((n, c) => n + Number(c.points || 0), 0);
  out.push(finding('Qdrant', '点が 0 件（書き込みの再開前）', data.qdrant ? (points === 0 ? 'ok' : 'fail') : 'fail',
    data.qdrant ? `コレクション ${data.qdrant.collections.length}・点 ${points}` : '読めなかった'));
  out.push(finding('MinIO', 'オブジェクトが 0 件（書き込みの再開前）', data.minio ? (data.minio.objects === 0 ? 'ok' : 'fail') : 'fail',
    data.minio ? `バケット ${data.minio.buckets.join(', ') || '(なし)'}・オブジェクト ${data.minio.objects}` : '読めなかった'));

  const queues = data.rabbitmq?.queues;
  if (!queues) {
    out.push(finding('RabbitMQ', 'MSP のキューに滞留が無い', 'fail', '読めなかった'));
  } else {
    const isMsp = (q) => expected.mspQueuePrefixes.some((p) => q.name.startsWith(p));
    const backlog = queues.filter((q) => isMsp(q) && Number(q.messages) > 0);
    out.push(finding('RabbitMQ', 'MSP のキューに滞留が無い', backlog.length ? 'fail' : 'ok',
      backlog.length ? backlog.map((q) => `${q.name}=${q.messages}`).join(', ') : `${queues.filter(isMsp).length} 本`));
    const other = queues.filter((q) => !isMsp(q) && Number(q.messages) > 0);
    if (other.length) out.push(finding('RabbitMQ', 'MSP 以外のキューの滞留（参考）', 'skip', other.map((q) => `${q.name}=${q.messages}`).join(', ')));
  }

  // 参考表示のみ（合否に使わない）。head の最古は古いブロックが在ると TSDB 全体の最古ではない。
  // 作り直しの判定は prometheus-data の PVC の作成時刻（上の PVC の行）で行う。
  const minTime = data.prometheus ? toMillis(data.prometheus.minTime) : null;
  out.push(finding('可観測性', 'Prometheus の head の最古サンプル（参考。判定は PVC の作成時刻）', 'skip',
    data.prometheus ? `head minTime ${minTime === null ? '(なし)' : new Date(minTime).toISOString()}` : '読めなかった'));

  return out;
}

// 切替前後の件数突合（参考表。合否は evaluate が持つ）。
function countsOf(data) {
  const byDb = {};
  for (const t of data.postgres?.tables || []) byDb[t.db] = (byDb[t.db] || 0) + Number(t.rows || 0);
  return {
    postgresRowsByDb: byDb,
    realms: (data.keycloak?.realms || []).slice().sort(),
    realmUsers: (data.keycloak?.users || []).length,
    realmClients: (data.keycloak?.clients || []).length,
    qdrantCollections: (data.qdrant?.collections || []).length,
    qdrantPoints: (data.qdrant?.collections || []).reduce((n, c) => n + Number(c.points || 0), 0),
    minioObjects: data.minio ? data.minio.objects : null,
    queueMessages: (data.rabbitmq?.queues || []).reduce((n, q) => n + Number(q.messages || 0), 0),
    prometheusMinTime: data.prometheus ? data.prometheus.minTime : null,
  };
}

function compareCounts(before, after) {
  const b = countsOf(before);
  const a = countsOf(after);
  const rows = [];
  const dbs = [...new Set([...Object.keys(b.postgresRowsByDb), ...Object.keys(a.postgresRowsByDb)])].sort();
  for (const db of dbs) rows.push({ item: `PostgreSQL ${db}（行数の合計）`, before: b.postgresRowsByDb[db] ?? null, after: a.postgresRowsByDb[db] ?? null });
  for (const k of ['realmUsers', 'realmClients', 'qdrantCollections', 'qdrantPoints', 'minioObjects', 'queueMessages', 'prometheusMinTime']) {
    rows.push({ item: k, before: b[k], after: a[k] });
  }
  rows.push({ item: 'realms', before: b.realms.join(', '), after: a.realms.join(', ') });
  return rows;
}

function renderText(result) {
  const L = [];
  const hr = '----------------------------------------------------------------------';
  L.push('再実装版への切替: 実在量と作り直しの検証（#457）');
  L.push(hr);
  if (result.findings) {
    L.push(`判定の基準時刻（--since）: ${result.since}`);
    for (const f of result.findings) {
      const mark = f.status === 'ok' ? 'ok  ' : f.status === 'fail' ? 'FAIL' : 'skip';
      L.push(`  ${mark}  [${f.asset}] ${f.check} — ${f.detail}`);
    }
    const fails = result.findings.filter((f) => f.status === 'fail').length;
    L.push(hr);
    L.push(fails ? `FAIL ${fails} 件（終了コード 1）` : 'すべて ok（skip は配備で無効なもの・参考表示）');
  }
  if (result.comparison) {
    L.push(hr);
    L.push('件数突合（切替前 → 切替後。参考）');
    for (const r of result.comparison) L.push(`  ${r.item}: ${r.before ?? '(なし)'} → ${r.after ?? '(なし)'}`);
  }
  if (!result.findings && !result.comparison) {
    L.push('実在量（--since を渡すと判定、--baseline を渡すと突合を行う）');
    const c = result.counts;
    for (const [db, n] of Object.entries(c.postgresRowsByDb)) L.push(`  PostgreSQL ${db}: ${n} 行`);
    L.push(`  Keycloak realm: ${c.realms.join(', ')}（${result.realm} の利用者 ${c.realmUsers}・クライアント ${c.realmClients}）`);
    L.push(`  Qdrant: コレクション ${c.qdrantCollections}・点 ${c.qdrantPoints}`);
    L.push(`  MinIO: オブジェクト ${c.minioObjects ?? '(読めず)'}`);
    L.push(`  RabbitMQ: 滞留 ${c.queueMessages}`);
    L.push(`  Prometheus: minTime ${c.prometheusMinTime ?? '(読めず)'}`);
  }
  return L.join('\n');
}

// ---------------------------------------------------------------------------
// 収集（I/O。稼働環境で未検証 —— 冒頭の注記を参照）
// ---------------------------------------------------------------------------

const env = (k, d) => process.env[k] || d;

function run(cmd, args, what) {
  const res = spawnSync(cmd, args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  if (res.error) throw new Error(`${what}: ${cmd} を実行できません（${res.error.message}）`);
  if (res.status !== 0) throw new Error(`${what}: ${cmd} が失敗しました（exit ${res.status}）\n${(res.stderr || '').trim()}`);
  return res.stdout;
}

function podName(ns, label) {
  const out = run('kubectl', ['-n', ns, 'get', 'pod', '-l', label, '-o', 'jsonpath={.items[0].metadata.name}'], `pod の解決（${label}）`).trim();
  if (!out) throw new Error(`pod が見つかりません（-n ${ns} -l ${label}）`);
  return out;
}

function psqlJson(ns, db, sql) {
  const stdout = run('kubectl', ['-n', ns, 'exec', podName(ns, 'app=postgres'), '--', 'psql', '-U', env('CUTOVER_PG_USER', 'postgres'),
    '-d', db, '-t', '-A', '-c', sql], `psql（${db}）`);
  return stdout.split('\n').map((s) => s.trim()).filter(Boolean).map((s) => JSON.parse(s));
}

async function httpJson(baseUrlEnv, ns, service, apiPath) {
  const base = env(baseUrlEnv, '');
  if (base) {
    const res = await fetch(`${base.replace(/\/$/, '')}${apiPath}`);
    if (!res.ok) throw new Error(`GET ${apiPath} が失敗しました（${res.status}）`);
    return res.json();
  }
  return JSON.parse(run('kubectl', ['get', '--raw', `/api/v1/namespaces/${ns}/services/${service}/proxy${apiPath}`], `GET ${service}${apiPath}`));
}

async function tryCollect(what, fn) {
  try {
    return await fn();
  } catch (e) {
    process.stderr.write(`[measure-cutover-inventory] ${what} を収集できなかった: ${e.message}\n`);
    return null;
  }
}

async function collect(databases) {
  const infra = env('CUTOVER_INFRA_NS', 'platform-infra');
  const msp = env('CUTOVER_MSP_NS', 'microservices-platform');
  const realm = env('CUTOVER_REALM', 'platform');

  const pvcs = [];
  for (const ns of [infra, msp]) {
    const list = JSON.parse(run('kubectl', ['-n', ns, 'get', 'pvc', '-o', 'json'], `PVC 一覧（${ns}）`));
    for (const item of list.items || []) pvcs.push({ namespace: ns, name: item.metadata.name, created: item.metadata.creationTimestamp });
  }

  const dbs = psqlJson(infra, 'postgres',
    "SELECT json_build_object('db', datname, 'created', (pg_stat_file('base/' || oid || '/PG_VERSION')).modification) FROM pg_database WHERE NOT datistemplate");
  const present = new Set(dbs.map((d) => d.db));
  const tables = [];
  for (const db of databases.msp.filter((d) => present.has(d))) {
    for (const t of psqlJson(infra, db,
      "SELECT json_build_object('table', table_name, 'schema', table_schema, 'rows', (xpath('/row/c/text()', query_to_xml(format('select count(*) as c from %I.%I', table_schema, table_name), false, true, '')))[1]::text::bigint) FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema')")) {
      tables.push({ db, ...t });
    }
  }
  const authz = present.has('authz_svc')
    ? await tryCollect('authz_svc', () => {
      const rows = psqlJson(infra, 'authz_svc',
        'SELECT json_build_object(\'kind\', \'attribute\', \'v\', "Key") FROM "AttributeDefinitions" UNION ALL SELECT json_build_object(\'kind\', \'policy\', \'v\', "Name") FROM "Policies"');
      return { attributeKeys: rows.filter((r) => r.kind === 'attribute').map((r) => r.v), policyNames: rows.filter((r) => r.kind === 'policy').map((r) => r.v) };
    })
    : null;

  const keycloak = await tryCollect('Keycloak', () => {
    const pod = podName(infra, 'app=keycloak');
    const kcadm = (args) => run('kubectl', ['-n', infra, 'exec', pod, '--', '/opt/keycloak/bin/kcadm.sh', ...args], `kcadm ${args[0]}`);
    kcadm(['config', 'credentials', '--server', env('CUTOVER_KC_INTERNAL_URL', 'http://localhost:8080'), '--realm', 'master',
      '--user', env('CUTOVER_KC_ADMIN_USER', 'admin'), '--password', env('CUTOVER_KC_ADMIN_PASSWORD', 'admin')]);
    const realms = JSON.parse(kcadm(['get', 'realms', '--fields', 'realm'])).map((r) => r.realm);
    if (!realms.includes(realm)) return { realms, users: [], clients: [] };
    const users = JSON.parse(kcadm(['get', 'users', '-r', realm, '--limit', '1000', '--fields', 'username,createdTimestamp']));
    const clients = JSON.parse(kcadm(['get', 'clients', '-r', realm, '--fields', 'clientId'])).map((c) => c.clientId);
    return { realms, users, clients };
  });

  const qdrant = await tryCollect('Qdrant', async () => {
    const list = await httpJson('CUTOVER_QDRANT_URL', infra, 'http:qdrant:6333', '/collections');
    const collections = [];
    for (const c of list.result?.collections || []) {
      const info = await httpJson('CUTOVER_QDRANT_URL', infra, 'http:qdrant:6333', `/collections/${encodeURIComponent(c.name)}`);
      collections.push({ name: c.name, points: Number(info.result?.points_count ?? 0) });
    }
    return { collections };
  });

  const minio = await tryCollect('MinIO', () =>
    parseMinioListing(run('kubectl', ['-n', msp, 'exec', podName(msp, 'app=minio'), '--', 'ls', '-R', '/data'], 'MinIO の一覧')));

  const rabbitmq = await tryCollect('RabbitMQ', () => ({
    queues: JSON.parse(run('kubectl', ['-n', infra, 'exec', podName(infra, 'app=rabbitmq'), '--', 'rabbitmqctl', 'list_queues', 'name', 'messages', 'consumers',
      '--formatter', 'json', '--quiet'], 'rabbitmqctl list_queues')),
  }));

  const prometheus = await tryCollect('Prometheus', async () => {
    const s = await httpJson('CUTOVER_PROM_URL', infra, 'http:prometheus:9090', '/api/v1/status/tsdb');
    const min = s.data?.headStats?.minTime;
    return { minTime: typeof min === 'number' && min > 0 ? new Date(min).toISOString() : null };
  });

  return {
    measuredAt: new Date().toISOString(),
    namespaces: { infra, msp },
    realm,
    pvcs,
    postgres: { databases: dbs, tables, authz },
    keycloak,
    qdrant,
    minio,
    rabbitmq,
    prometheus,
  };
}

function loadExpected() {
  return {
    databases: classifyDatabases(fs.readFileSync(POSTGRES_INIT, 'utf8')),
    realm: declaredRealm(JSON.parse(fs.readFileSync(REALM_FILE, 'utf8'))),
    abac: declaredAbacSeed(
      JSON.parse(fs.readFileSync(path.join(ABAC_SEED_DIR, 'attributes.json'), 'utf8')),
      JSON.parse(fs.readFileSync(path.join(ABAC_SEED_DIR, 'policies.json'), 'utf8')),
    ),
    mspQueuePrefixes: mspQueuePrefixes(JSON.parse(fs.readFileSync(PIPELINE_FILE, 'utf8'))),
  };
}

function argValue(argv, flag) {
  const i = argv.indexOf(flag);
  if (i < 0) return null;
  const v = argv[i + 1];
  if (!v || v.startsWith('--')) throw new Error(`${flag} には値が要る`);
  return v;
}

async function main(argv) {
  const expected = loadExpected();
  if (argv.includes('--print-recreate-sql')) {
    process.stdout.write(recreateSql(expected.databases.msp));
    return 0;
  }
  const asJson = argv.includes('--json');
  const input = argValue(argv, '--input');
  const dump = argValue(argv, '--dump');
  const since = argValue(argv, '--since');
  const baseline = argValue(argv, '--baseline');

  const data = input ? JSON.parse(fs.readFileSync(input, 'utf8')) : await collect(expected.databases);
  if (dump) fs.writeFileSync(dump, `${JSON.stringify(data, null, 2)}\n`);

  const before = baseline ? JSON.parse(fs.readFileSync(baseline, 'utf8')) : null;
  const result = { realm: data.realm, counts: countsOf(data) };
  if (since) {
    result.since = since;
    result.findings = evaluate(data, expected, since, before);
  }
  if (before) result.comparison = compareCounts(before, data);
  process.stdout.write(asJson ? `${JSON.stringify(result, null, 2)}\n` : `${renderText(result)}\n`);
  return result.findings && result.findings.some((f) => f.status === 'fail') ? 1 : 0;
}

module.exports = {
  RECREATED_PVCS,
  KEPT_PVCS,
  LEGACY_REALM,
  classifyDatabases,
  recreateSql,
  declaredRealm,
  declaredAbacSeed,
  mspQueuePrefixes,
  parseMinioListing,
  evaluate,
  countsOf,
  compareCounts,
  renderText,
  loadExpected,
};

if (require.main === module) {
  main(process.argv.slice(2))
    .then((code) => process.exit(code))
    .catch((e) => {
      process.stderr.write(`[measure-cutover-inventory] ${e.message}\n`);
      process.exit(2);
    });
}
