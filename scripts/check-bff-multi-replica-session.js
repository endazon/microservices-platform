#!/usr/bin/env node
'use strict';
/*
 * check-bff-multi-replica-session.js
 * NFR, ADR-0032, IADR-0251 決定 5, #1534:
 * **BFF のセッション Cookie を、2 つのレプリカのどちらでも復号できること**を稼働クラスタで測る。
 *
 * ## なぜ要るか
 *
 * Cookie は DataProtection で保護され、鍵リングは Redis の `bff:dataprotection-keys` に共有している
 * （`BffSessionExtensions.cs`）。**しかし 2 レプリカで相互に復号できることは一度も測られていない。**
 * 単体テストは 1 プロセスなので鍵リングが 1 つしか無く、共有し忘れていても緑になる（IADR-0251 決定 5）。
 * 壊れると「レプリカ A でログイン → B へ振られた要求が 401 → 利用者は無作為にログアウトされる」。
 *
 * ## 測り方（Pod へ固定する）
 *
 * エッジ越しに 20 回叩いて「全部 200」を見ても、**全部が同じ Pod へ振られていれば何も測れていない。**
 * 本スクリプトは **Pod ごとに port-forward** し、同じ Cookie を各 Pod へ直接投げる。どの要求がどの Pod に
 * 当たったかは構成から決まるので、アクセスログで振り分けを確かめる必要が無い。
 *
 * - 陽性: ログインで得た Cookie → 各 Pod の `/bff/auth/me` が 200 で、同じ利用者名を返す
 * - 🔴 陰性対照: 値を 1 文字だけ変えた Cookie → **各 Pod で 401**。200 なら「Cookie を見ずに通している」
 *   形であり、陽性の 200 は何も証明しない
 * - `--restart`: `rollout restart` で Pod を 1 つずつ作り直した後も、同じ Cookie で 200（鍵がメモリでなく Redis にある）
 *
 * ## レプリカは helm の values で増やす（`kubectl scale` を使わない）
 *
 * 手で scale すると helm の持つ値と稼働が食い違い、次の `helm upgrade` が黙って 1 へ戻す。
 * 増やすのは `services.bff.replicas` だけで、🔴 **上書きに `extraEnv` を書かない**（Helm はリストを置換する。
 * 既定の env 36 件が消える。#1389）。`--plan` が描画の差分で「1 行だけ」を確かめる。
 * 🔴 `scaling.enabled` かつ `scaling.services` に `bff` があると Deployment は `replicas` を持たない（HPA が所有）。
 * その配備では helm を触らず、既に 2 つ以上居るかだけを見る。
 *
 * ## ログインする利用者
 *
 * 🔴 **試験専用の利用者だけ**を使う。realm 宣言の利用者（`developer` 等）は大小を無視して拒否する
 * （`check-login-existence-disclosure.js` の `resolveLoginTarget` を借りる。拒否の正本は 1 つ）。
 * **既定の利用者は無い**（指定が無ければ止める）。パスワードは環境変数だけで受ける（引数はプロセス一覧と履歴に残る）。
 * TOTP の状態ファイルは `verify-oidc-edge-flow.sh` と同じ名前の規則で読み書きする。
 *
 * 使い方:
 *   node scripts/check-bff-multi-replica-session.js --plan [--values <file>]...   # helm template の差分だけ（稼働に触れない）
 *   BFF_PROBE_USERNAME=<試験利用者> BFF_PROBE_PASSWORD=<…> \
 *     node scripts/check-bff-multi-replica-session.js --live [--per-pod N] [--restart]
 *
 * 終了コード: 0=合格 / 1=不合格（または戻しの失敗） / 2=前提未整備・停止条件 / 3=明示の指定なし（#1550）
 *
 * 手順書: docs/operations/bff-multi-replica-session-runbook.md
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const http = require('http');
const { spawn, spawnSync } = require('child_process');
const { requireLiveOptIn, withoutLiveFlag } = require('./lib/live-opt-in.js');

const TAG = '[check-bff-multi-replica-session]';
const REPO_ROOT = path.resolve(__dirname, '..');

/** helm のリリース・名前空間・チャート。`scripts/k8s-local-up.sh` の [6/7] と同じ値。 */
const RELEASE = 'msp';
const NAMESPACE = 'microservices-platform';
const CHART = path.join('deploy', 'helm', 'microservices-platform');
const DEFAULT_VALUES = [path.join('deploy', 'local', 'values-local.yaml')];

/** チャートが描く名前（`templates/deployment.yaml`: `<key>-service`）。 */
const SERVICE_KEY = 'bff';
const DEPLOYMENT = `${SERVICE_KEY}-service`;
const POD_SELECTOR = `app=${DEPLOYMENT}`;
const TARGET_REPLICAS = 2;

const ME_PATH = '/bff/auth/me';
const LOGIN_PATH = '/bff/auth/login?returnUrl=/';
/** BFF の既定（`BffSessionOptions.CookieName`）。`verify-oidc-edge-flow.sh` と同じ環境変数で差し替えられる。 */
const DEFAULT_COOKIE_NAME = '__Host-msp-session';

const USERNAME_FLAG = '--username';
const USERNAME_ENV = 'BFF_PROBE_USERNAME';
const PASSWORD_ENV = 'BFF_PROBE_PASSWORD';
/** 1 Pod あたりの陽性の要求数。2 Pod で合計 20（#1534 の受け入れ基準「20 回以上」）。 */
const DEFAULT_PER_POD = 10;
const MIN_PER_POD = 1;

// ---------------------------------------------------------------- 純関数

/** 上書きの values。**`replicas` 1 つだけ**を持つ（リストを書かない）。 */
function overlayValuesYaml(replicas = TARGET_REPLICAS) {
  return `services:\n  ${SERVICE_KEY}:\n    replicas: ${replicas}\n`;
}

/**
 * helm の出力（複数文書の YAML）を `kind/namespace/name` → 本文 に分ける。
 * 本文は行末の空白を落とす。hook（`helm.sh/hook`）は `helm get manifest` に含まれないので `dropHooks` で外せる。
 * @returns {Map<string,string>}
 */
function splitManifest(text, { dropHooks = false } = {}) {
  const out = new Map();
  const docs = String(text || '').replace(/\r\n/g, '\n').split(/^---\s*$/m);
  for (const raw of docs) {
    const doc = raw.split('\n').map((l) => l.replace(/\s+$/, '')).join('\n').trim();
    const kind = /^kind:\s*(\S+)/m.exec(doc);
    if (!kind) continue;
    if (dropHooks && /^\s+helm\.sh\/hook:/m.test(doc)) continue;
    const meta = /^metadata:\n((?:[ \t]+.*\n?)*)/m.exec(`${doc}\n`);
    const name = meta ? /^ {2}name:\s*(\S+)/m.exec(meta[1]) : null;
    const ns = meta ? /^ {2}namespace:\s*(\S+)/m.exec(meta[1]) : null;
    let key = `${kind[1]}/${ns ? ns[1] : ''}/${name ? name[1] : ''}`;
    for (let i = 2; out.has(key); i += 1) key = `${kind[1]}/${ns ? ns[1] : ''}/${name ? name[1] : ''}#${i}`;
    out.set(key, doc);
  }
  return out;
}

/** `kind/namespace/name` のうち kind と name が一致する鍵か。 */
function isDeploymentKey(key, name) {
  return key.startsWith('Deployment/') && key.split('/')[2] === name;
}

/**
 * 🔴 **上書きの描画差分が「BFF の Deployment の replicas 1 行だけ」であること**。
 *
 * - 文書の集合が変わる・BFF 以外の文書が変わる → 失敗（上書きが他を巻き込んでいる。`extraEnv` の置換など）
 * - BFF の Deployment が変わらない → 失敗（HPA が所有していて `replicas` を描かない、または上書きが効いていない）
 * - BFF の Deployment で変わった行が 1 行でない／`replicas: <from>` → `replicas: <to>` でない → 失敗
 *
 * @returns {{failures: string[], changed: Array<{before: string, after: string}>}}
 */
function evaluateReplicaOverlayDiff(baseText, overlayText, { deployment = DEPLOYMENT, to = TARGET_REPLICAS } = {}) {
  const failures = [];
  const changed = [];
  const a = splitManifest(baseText);
  const b = splitManifest(overlayText);
  if (a.size === 0 || b.size === 0) {
    failures.push(`${TAG} [plan] 描画が空である（元 ${a.size} 文書 / 上書き ${b.size} 文書）。0 文書の比較を緑にしない。`);
    return { failures, changed };
  }
  const onlyA = [...a.keys()].filter((k) => !b.has(k));
  const onlyB = [...b.keys()].filter((k) => !a.has(k));
  if (onlyA.length > 0 || onlyB.length > 0) {
    failures.push(`${TAG} [plan] 上書きで文書の集合が変わる（消える: ${onlyA.join(', ') || 'なし'} / 増える: ${onlyB.join(', ') || 'なし'}）。`);
  }
  const target = [...a.keys()].filter((k) => isDeploymentKey(k, deployment));
  if (target.length !== 1) {
    failures.push(`${TAG} [plan] Deployment ${deployment} が描画に ${target.length} 件ある（1 件であることを前提にしている）。`);
    return { failures, changed };
  }
  for (const key of a.keys()) {
    if (!b.has(key) || a.get(key) === b.get(key)) continue;
    if (key !== target[0]) {
      failures.push(`${TAG} [plan] ${key} が上書きで変わる。変えてよいのは ${deployment} の replicas だけ`
        + '（リストを上書きに書くと Helm は置換する —— extraEnv なら既定の env が丸ごと消える）。');
    }
  }
  const before = a.get(target[0]).split('\n');
  const after = (b.get(target[0]) || '').split('\n');
  if (a.get(target[0]) === b.get(target[0])) {
    failures.push(`${TAG} [plan] ${deployment} の描画が上書きで変わらない。`
      + ' 元の values が既に同じ replicas を持つか、HPA が所有している（scaling.enabled かつ scaling.services に含まれると'
      + ' replicas は描画されない —— その配備では helm を触らずに Pod 数だけ見る）。');
    return { failures, changed };
  }
  if (before.length !== after.length) {
    failures.push(`${TAG} [plan] ${deployment} の行数が上書きで変わる（${before.length} → ${after.length}）。replicas 以外が変わっている。`);
    return { failures, changed };
  }
  for (let i = 0; i < before.length; i += 1) {
    if (before[i] !== after[i]) changed.push({ before: before[i], after: after[i] });
  }
  const ok = changed.length === 1
    && /^\s*replicas:\s*\d+\s*$/.test(changed[0].before)
    && new RegExp(`^\\s*replicas:\\s*${to}\\s*$`).test(changed[0].after);
  if (!ok) {
    failures.push(`${TAG} [plan] ${deployment} で変わる行が「replicas → ${to}」の 1 行ではない（${changed.length} 行: `
      + `${changed.map((c) => `${c.before.trim()} → ${c.after.trim()}`).join(' / ')}）。`);
  }
  return { failures, changed };
}

/**
 * 稼働のマニフェスト（`helm get manifest`）と、同じ values でのチェックアウトの描画が一致すること。
 * 🔴 **一致しなければ upgrade しない** —— replicas 以外の差（チェックアウトの版のずれ）まで稼働へ押し込むことになる。
 * @returns {string[]} 食い違う文書の鍵（空なら一致）
 */
function evaluateChartDrift(liveManifest, renderedText) {
  const live = splitManifest(liveManifest);
  const rendered = splitManifest(renderedText, { dropHooks: true });
  if (live.size === 0 || rendered.size === 0) return [`（比較できない: 稼働 ${live.size} 文書 / 描画 ${rendered.size} 文書）`];
  const keys = new Set([...live.keys(), ...rendered.keys()]);
  return [...keys].filter((k) => live.get(k) !== rendered.get(k)).sort();
}

/** HPA の一覧（`kubectl get hpa -o json`）から、Deployment を所有する HPA の名前を返す（無ければ null）。 */
function findOwningHpa(hpaList, deployment = DEPLOYMENT) {
  const items = (hpaList && Array.isArray(hpaList.items)) ? hpaList.items : [];
  const hit = items.find((h) => h && h.spec && h.spec.scaleTargetRef
    && h.spec.scaleTargetRef.kind === 'Deployment' && h.spec.scaleTargetRef.name === deployment);
  return hit ? String((hit.metadata && hit.metadata.name) || deployment) : null;
}

/**
 * Pod の一覧（`kubectl get pods -o json`）から、要求を受けられる Pod（Running・Ready・削除中でない）を選ぶ。
 * ポートは BFF のコンテナ（`<key>-service`）の宣言から取る（サイドカーのポートを掴まない）。
 * @returns {Array<{name: string, port: number}>}
 */
function selectReadyPods(podList, container = DEPLOYMENT) {
  const items = (podList && Array.isArray(podList.items)) ? podList.items : [];
  const out = [];
  for (const p of items) {
    if (!p || !p.metadata || p.metadata.deletionTimestamp) continue;
    if (!p.status || p.status.phase !== 'Running') continue;
    const ready = (p.status.conditions || []).some((c) => c.type === 'Ready' && c.status === 'True');
    if (!ready) continue;
    const c = ((p.spec && p.spec.containers) || []).find((x) => x.name === container);
    const port = c && Array.isArray(c.ports) && c.ports[0] ? Number(c.ports[0].containerPort) : NaN;
    if (!Number.isInteger(port)) continue;
    out.push({ name: p.metadata.name, port });
  }
  return out.sort((x, y) => x.name.localeCompare(y.name));
}

/** `kubectl port-forward` の出力から、割り当てられたローカルポートを読む。 */
function parsePortForwardPort(text) {
  const m = /Forwarding from 127\.0\.0\.1:(\d+)\s*->/.exec(String(text || ''));
  return m ? Number(m[1]) : null;
}

/**
 * cookie の並び（`a=b; c=d`）から、セッション Cookie（と ASP.NET の分割片 `<name>C1`…）だけを取り出す。
 * @returns {Array<[string,string]>}
 */
function sessionCookiePairs(cookieHeader, cookieName = DEFAULT_COOKIE_NAME) {
  const chunk = new RegExp(`^${cookieName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(C\\d+)?$`);
  return String(cookieHeader || '').split(/;\s*/).filter(Boolean)
    .map((kv) => { const i = kv.indexOf('='); return i < 0 ? null : [kv.slice(0, i), kv.slice(i + 1)]; })
    .filter((p) => p && chunk.test(p[0]) && p[1] !== '');
}

/** 値の真ん中の 1 文字だけを別の文字へ変える（長さは変えない）。空なら null。 */
function tamperValue(value) {
  const s = String(value || '');
  if (s.length === 0) return null;
  const i = Math.floor(s.length / 2);
  const replacement = s[i] === 'A' ? 'B' : 'A';
  return s.slice(0, i) + replacement + s.slice(i + 1);
}

/** 送る Cookie ヘッダを組む。`tamper` なら最も長い値（保護された本体）を 1 文字変える。 */
function buildCookieHeader(pairs, { tamper = false } = {}) {
  if (!Array.isArray(pairs) || pairs.length === 0) return null;
  let target = -1;
  if (tamper) {
    pairs.forEach(([, v], i) => { if (target < 0 || v.length > pairs[target][1].length) target = i; });
  }
  return pairs.map(([k, v], i) => `${k}=${i === target ? tamperValue(v) : v}`).join('; ');
}

/**
 * 🔴 **本体の判定**: 同じ Cookie を、どの Pod も同じ利用者として受け入れ、改ざんした Cookie はどの Pod も拒む。
 *
 * @param {{pods: Array<{name: string, statuses: number[], users: string[], tamperedStatuses: number[], error?: string}>,
 *          expectedUser: string, minPods?: number, minPerPod?: number, label?: string}} input
 * @returns {string[]}
 */
function evaluateCrossReplica(input) {
  const failures = [];
  const label = input.label || '測定';
  const pods = Array.isArray(input.pods) ? input.pods : [];
  const minPods = input.minPods || TARGET_REPLICAS;
  const minPerPod = input.minPerPod || MIN_PER_POD;
  if (pods.length < minPods) {
    failures.push(`${TAG} [${label}] 測れた Pod が ${pods.length} 個（${minPods} 個以上が要る）。1 つの Pod では相互の復号を測れない。`);
  }
  for (const p of pods) {
    if (p.error) {
      failures.push(`${TAG} [${label}] ${p.name}: 要求を送れなかった（${p.error}）。測れていないものを合格にしない。`);
      continue;
    }
    const statuses = p.statuses || [];
    if (statuses.length < minPerPod) {
      failures.push(`${TAG} [${label}] ${p.name}: 陽性の標本が ${statuses.length} 件（${minPerPod} 件以上が要る）。`);
    }
    const bad = statuses.filter((s) => s !== 200);
    if (bad.length > 0) {
      failures.push(`${TAG} [${label}] ${p.name}: 同じ Cookie で 200 以外が ${bad.length}/${statuses.length} 件（${[...new Set(bad)].join(' / ')}）。`
        + ' 401 なら、この Pod は Cookie を復号できない（鍵リングかセッションストアが共有されていない）。');
    }
    const strangers = (p.users || []).filter((u) => u !== input.expectedUser);
    if (strangers.length > 0) {
      failures.push(`${TAG} [${label}] ${p.name}: 別の利用者として認証された（${[...new Set(strangers)].join(', ')}。期待 ${input.expectedUser}）。`);
    }
    const tampered = p.tamperedStatuses || [];
    if (tampered.length === 0) {
      failures.push(`${TAG} [${label}] ${p.name}: 陰性対照（改ざんした Cookie）を測っていない。陽性の 200 だけでは Cookie を見ているか分からない。`);
    } else if (tampered.some((s) => s !== 401)) {
      failures.push(`${TAG} [${label}] ${p.name}: 改ざんした Cookie が 401 にならない（${tampered.join(' / ')}）。`
        + ' 200 なら Cookie の保護を検証していない —— 陽性の 200 は何も証明しない。');
    }
  }
  return failures;
}

/** Redis の鍵リングの件数（`LLEN`）の読み。**読めないときは測っていないと言う**（0 件と取り違えない）。 */
function evaluateKeyRing(r) {
  if (!r || r.ok !== true) return { failure: null, notice: `${TAG} [前提] 鍵リングの件数を読めなかった（${(r && r.error) || '不明'}）。未測定として扱う。` };
  if (!Number.isInteger(r.count) || r.count < 1) {
    return { failure: `${TAG} [前提] Redis の bff:dataprotection-keys が ${r.count} 件。ログインした後なのに鍵が永続化されていない（鍵リングを共有していない）。`, notice: null };
  }
  return { failure: null, notice: `${TAG} [前提] Redis の bff:dataprotection-keys: ${r.count} 件` };
}

/**
 * 引数を読む。**純関数**（環境変数は引数で受ける）。
 * @returns {{mode: 'plan'|'live', values: string[], perPod: number, restart: boolean, override: ?string}|{error: string}}
 */
function parseArgs(argv, env = {}) {
  const args = withoutLiveFlag(Array.isArray(argv) ? argv : []);
  const out = { mode: 'live', values: [], perPod: DEFAULT_PER_POD, restart: false, override: null };
  const usernames = [];
  for (let i = 0; i < args.length; i += 1) {
    const a = args[i];
    const next = () => {
      const v = args[i + 1];
      if (v === undefined || v.startsWith('--')) return null;
      i += 1;
      return v;
    };
    if (a === '--plan') out.mode = 'plan';
    else if (a === '--restart') out.restart = true;
    else if (a === '--values') {
      const v = next();
      if (v === null) return { error: '--values にはファイルが要る' };
      out.values.push(v);
    } else if (a === '--per-pod') {
      const v = next();
      const n = Number(v);
      if (v === null || !Number.isInteger(n) || n < MIN_PER_POD) return { error: `--per-pod には ${MIN_PER_POD} 以上の整数が要る` };
      out.perPod = n;
    } else if (a === USERNAME_FLAG) {
      const v = next();
      if (v === null) return { error: `${USERNAME_FLAG} には値（試験専用の利用者名）が要る` };
      usernames.push(v);
    } else {
      return { error: `未知の引数: ${a}` };
    }
  }
  if (usernames.length > 1) return { error: `${USERNAME_FLAG} が ${usernames.length} 回指定された` };
  if (usernames.length === 1) out.override = usernames[0];
  else if (env && Object.prototype.hasOwnProperty.call(env, USERNAME_ENV) && env[USERNAME_ENV] !== undefined) out.override = String(env[USERNAME_ENV]);
  if (out.mode === 'plan' && (out.restart || usernames.length > 0)) {
    return { error: '--plan は稼働クラスタに触れない。--restart / --username と同時に使わない' };
  }
  if (out.mode === 'live' && out.values.length > 0) {
    return { error: '--values は --plan だけで使う（--live は稼働のリリースの values を helm get values で読む）' };
  }
  if (out.mode === 'plan' && out.values.length === 0) out.values = DEFAULT_VALUES.slice();
  return out;
}

/**
 * ログインに使う利用者を決める。🔴 **既定へ倒さない**（指定が無い・空 → 止める）。
 * 宣言の利用者の拒否は `resolveLoginTarget` に委ねる（拒否の正本は 1 つ）。
 * @returns {{ok: true, username: string}|{ok: false, error: string}}
 */
function resolveProbeUser({ override, password, realm, resolveLoginTarget }) {
  if (override === null || override === undefined) {
    return { ok: false, error: `試験専用の利用者を ${USERNAME_FLAG} か ${USERNAME_ENV} で渡す（既定の利用者は無い。realm 宣言の利用者は使わない）。` };
  }
  if (String(override).trim() === '' || String(override) !== String(override).trim()) {
    return { ok: false, error: '利用者名が空か、前後に空白がある。' };
  }
  const t = resolveLoginTarget({ override, realm });
  if (!t.ok) return { ok: false, error: t.error };
  if (!password) return { ok: false, error: `パスワードを環境変数 ${PASSWORD_ENV} で渡す（引数では受けない）。` };
  return { ok: true, username: t.username };
}

// ---------------------------------------------------------------- 外部（helm / kubectl / HTTP）

function helmBin() { return process.env.HELM || 'helm'; }

function run(bin, args, opts = {}) {
  return spawnSync(bin, args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, cwd: REPO_ROOT, ...opts });
}

function helmTemplate(valuesFiles) {
  const args = ['template', RELEASE, CHART, '-n', NAMESPACE];
  for (const f of valuesFiles) args.push('-f', f);
  const r = run(helmBin(), args);
  if (r.error || r.status !== 0) return { ok: false, error: (r.error && r.error.message) || String(r.stderr || '').trim() || `helm template が exit ${r.status}` };
  return { ok: true, value: r.stdout };
}

function kubectlJson(args) {
  const r = run('kubectl', [...args, '-o', 'json']);
  if (r.error || r.status !== 0) return { ok: false, error: (r.error && r.error.message) || String(r.stderr || '').trim() };
  try { return { ok: true, value: JSON.parse(r.stdout) }; } catch (e) { return { ok: false, error: e.message }; }
}

function rolloutStatus() {
  const r = run('kubectl', ['-n', NAMESPACE, 'rollout', 'status', `deploy/${DEPLOYMENT}`, '--timeout=300s'], { stdio: ['ignore', 'inherit', 'inherit'] });
  return r.status === 0;
}

function readKeyRingCount() {
  const r = run('kubectl', ['-n', 'platform-infra', 'exec', 'deploy/redis', '--', 'redis-cli', 'LLEN', 'bff:dataprotection-keys']);
  if (r.error || r.status !== 0) return { ok: false, error: (r.error && r.error.message) || String(r.stderr || '').trim() || `exit ${r.status}` };
  const n = Number(String(r.stdout || '').trim().replace(/^\(integer\)\s*/, ''));
  return Number.isInteger(n) ? { ok: true, count: n } : { ok: false, error: `LLEN の応答を読めない: ${String(r.stdout).trim().slice(0, 40)}` };
}

/** Pod 1 つへ port-forward し、ローカルポートが決まるまで待つ。 */
function openPortForward(pod) {
  return new Promise((resolve) => {
    const child = spawn('kubectl', ['-n', NAMESPACE, 'port-forward', `pod/${pod.name}`, `:${pod.port}`], { stdio: ['ignore', 'pipe', 'pipe'] });
    let buf = '';
    const timer = setTimeout(() => { child.kill(); resolve({ ok: false, error: 'port-forward が 15 秒で開かない' }); }, 15000);
    const onData = (d) => {
      buf += d.toString();
      const port = parsePortForwardPort(buf);
      if (port) { clearTimeout(timer); resolve({ ok: true, port, child }); }
    };
    child.stdout.on('data', onData);
    child.stderr.on('data', (d) => { buf += d.toString(); });
    child.on('exit', (code) => { clearTimeout(timer); resolve({ ok: false, error: `port-forward が終了した（exit ${code}）: ${buf.trim().slice(0, 200)}` }); });
  });
}

function getMe(port, cookieHeader) {
  return new Promise((resolve) => {
    const req = http.request({ host: '127.0.0.1', port, path: ME_PATH, method: 'GET', headers: { cookie: cookieHeader, accept: 'application/json' }, timeout: 15000 }, (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => {
        let user = null;
        try { user = JSON.parse(Buffer.concat(chunks).toString('utf8')).name || null; } catch { user = null; }
        resolve({ status: res.statusCode, user });
      });
    });
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.on('error', (e) => resolve({ status: 0, user: null, error: e.message }));
    req.end();
  });
}

/** 全 Pod を測る（Pod ごとに port-forward を開いて閉じる）。 */
async function measurePods(pods, cookie, tampered, perPod) {
  const results = [];
  for (const pod of pods) {
    const pf = await openPortForward(pod);
    if (!pf.ok) { results.push({ name: pod.name, statuses: [], users: [], tamperedStatuses: [], error: pf.error }); continue; }
    try {
      const statuses = [];
      const users = [];
      for (let i = 0; i < perPod; i += 1) {
        const r = await getMe(pf.port, cookie);
        statuses.push(r.status);
        if (r.status === 200 && r.user) users.push(r.user);
      }
      const t = await getMe(pf.port, tampered);
      results.push({ name: pod.name, statuses, users, tamperedStatuses: [t.status] });
      console.log(`${TAG}   ${pod.name}: 同じ Cookie → ${statuses.join(',')} ／ 改ざん → ${t.status}`);
    } finally {
      pf.child.kill();
    }
  }
  return results;
}

/** エッジ経由で BFF のログインを通し、jar の cookie 列を返す（値は出力しない）。 */
async function loginViaEdge({ edge, ca, username, password, realmName }) {
  const { createJar, request } = require('./check-password-reset-mail');
  const { parseLoginForm } = require('./lib/keycloak-login-form.js');
  const { totp, base32Encode } = require('./lib/totp.js');
  const stateDir = process.env.OIDC_TOTP_STATE_DIR || process.env.TMPDIR || '/tmp';
  const stateFile = path.join(stateDir, `msp-verify-oidc-totp-${realmName}-${username}.secret`);
  const jar = createJar();
  const abs = (loc, base) => new URL(loc, base).toString();
  const hasCode = (loc) => /[?&]code=/.test(String(loc || ''));

  let r = await request(`${edge}${LOGIN_PATH}`, { jar, ca });
  if (r.status !== 302 || !r.location) return { ok: false, error: `BFF のログイン開始が redirect しない（status=${r.status}）` };
  const authz = abs(r.location, edge);
  r = await request(authz, { jar, ca });
  const login = parseLoginForm(r.body);
  if (!login.action) return { ok: false, error: `ログイン画面のフォームが無い（status=${r.status}）` };
  const form = (o) => Object.entries(o).map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`).join('&');
  r = await request(abs(login.action, authz), { method: 'POST', body: form({ username, password }), jar, ca });
  let loc = r.location ? abs(r.location, authz) : null;
  let page = r.status === 200 ? r.body : null;
  let registered = null;
  for (let step = 0; step < 4 && !hasCode(loc); step += 1) {
    if (loc && loc.includes('/login-actions/')) {
      r = await request(loc, { jar, ca });
      if (r.location) { loc = abs(r.location, loc); continue; }
      page = r.body;
    }
    if (!page) break;
    const f = parseLoginForm(page);
    if (!f.totpField) {
      return { ok: false, error: 'ログインが認可コードへ進まない（資格情報の誤り、または TOTP 以外の必須アクション。'
        + '試験利用者は「一時パスワード」をオフにして作る）' };
    }
    let secret = f.totpSecretEncoded || (f.fields.totpSecret ? base32Encode(f.fields.totpSecret) : '');
    if (!secret) secret = process.env.OIDC_TOTP_SECRET || '';
    if (!secret && fs.existsSync(stateFile)) secret = fs.readFileSync(stateFile, 'utf8').trim();
    if (!secret) return { ok: false, error: `OTP の段（field=${f.totpField}）でシークレットを解決できない。OIDC_TOTP_SECRET か ${stateFile} を用意する` };
    const body = { [f.totpField]: totp(secret) };
    if (f.fields.totpSecret) body.totpSecret = f.fields.totpSecret;
    if (f.fields.mode) body.mode = f.fields.mode;
    if (f.totpField === 'totp') { body.userLabel = 'check-bff-multi-replica-session'; registered = secret; }
    const action = abs(f.action, loc || authz);
    r = await request(action, { method: 'POST', body: form(body), jar, ca });
    loc = r.location ? abs(r.location, action) : null;
    page = r.status === 200 ? r.body : null;
  }
  if (!hasCode(loc)) return { ok: false, error: '認可コードを得られない（ログインか OTP の段で止まった）' };
  if (registered) {
    try { fs.mkdirSync(stateDir, { recursive: true }); fs.writeFileSync(stateFile, registered, { mode: 0o600 }); } catch { /* 保存できなくても続ける */ }
  }
  r = await request(loc, { jar, ca });
  const cookieHeader = (jar.apply({}).cookie) || '';
  return { ok: true, cookieHeader, callbackStatus: r.status };
}

// ---------------------------------------------------------------- 入口

function printPlan(result) {
  for (const c of result.changed) console.log(`${TAG}   - ${c.before.trim()}\n${TAG}   + ${c.after.trim()}`);
}

function planMode(values) {
  const overlayDir = fs.mkdtempSync(path.join(os.tmpdir(), 'bff-replicas-'));
  try {
    const overlay = path.join(overlayDir, 'bff-replicas.yaml');
    fs.writeFileSync(overlay, overlayValuesYaml());
    const a = helmTemplate(values);
    const b = a.ok ? helmTemplate([...values, overlay]) : a;
    if (!a.ok || !b.ok) { console.error(`${TAG} helm template を実行できない: ${(a.ok ? b : a).error}`); return 2; }
    const result = evaluateReplicaOverlayDiff(a.value, b.value);
    printPlan(result);
    if (result.failures.length > 0) { for (const f of result.failures) console.error(f); return 1; }
    console.log(`${TAG} OK: values（${values.join(', ')}）に replicas: ${TARGET_REPLICAS} を重ねた描画の差分は ${DEPLOYMENT} の 1 行だけ`);
    return 0;
  } finally {
    fs.rmSync(overlayDir, { recursive: true, force: true });
  }
}

async function liveMode(opts) {
  const { loadRealm, edgeCa } = require('./check-password-reset-mail');
  const { resolveLoginTarget } = require('./check-login-existence-disclosure');
  const realm = loadRealm();
  if (!realm.ok) { console.error(`${TAG} realm 宣言を読めない: ${realm.error}`); return 2; }
  const who = resolveProbeUser({ override: opts.override, password: process.env[PASSWORD_ENV], realm: realm.value, resolveLoginTarget });
  if (!who.ok) { console.error(`${TAG} ${who.error}`); return 2; }

  const failures = [];
  const notices = [];
  let restore = null;
  let restoring = false;
  const doRestore = () => {
    if (!restore || restoring) return true;
    restoring = true;
    const ok = restore();
    restore = null;
    return ok;
  };
  const onSignal = () => { console.error(`${TAG} 中断を受けた。1 レプリカへ戻す。`); doRestore(); process.exit(130); };
  process.on('SIGINT', onSignal);
  process.on('SIGTERM', onSignal);
  let code = 1;
  try {
    code = await (async () => {
    // ── 1. 誰がレプリカ数を持っているか
    const hpa = kubectlJson(['-n', NAMESPACE, 'get', 'hpa']);
    if (!hpa.ok) { console.error(`${TAG} HPA の一覧を読めない: ${hpa.error}`); return 2; }
    const owner = findOwningHpa(hpa.value);
    if (owner) {
      console.log(`${TAG} HPA ${owner} が ${DEPLOYMENT} を所有している。helm は触らず、居る Pod で測る。`);
    } else {
      // ── 2. 現在の values を退避し、版のずれと上書きの差分を確かめてから upgrade する
      const got = run(helmBin(), ['get', 'values', RELEASE, '-n', NAMESPACE, '-o', 'json']);
      if (got.status !== 0) { console.error(`${TAG} helm get values が失敗: ${String(got.stderr).trim()}`); return 2; }
      const current = JSON.parse(String(got.stdout).trim() || 'null') || {};
      const already = current.services && current.services[SERVICE_KEY] && current.services[SERVICE_KEY].replicas;
      if (already !== undefined && already !== 1) {
        console.error(`${TAG} 稼働のリリースは既に services.${SERVICE_KEY}.replicas=${already} を持つ（前回の中断の残りの疑い）。手順書の戻し方で 1 へ戻してから走らせる。`);
        return 2;
      }
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'bff-replicas-'));
      const saved = path.join(dir, 'current-values.json');
      const overlay = path.join(dir, 'bff-replicas.yaml');
      fs.writeFileSync(saved, JSON.stringify(current), { mode: 0o600 });
      fs.writeFileSync(overlay, overlayValuesYaml());
      const manifest = run(helmBin(), ['get', 'manifest', RELEASE, '-n', NAMESPACE]);
      const base = helmTemplate([saved]);
      const scaled = base.ok ? helmTemplate([saved, overlay]) : base;
      if (manifest.status !== 0 || !base.ok || !scaled.ok) {
        console.error(`${TAG} helm の読み出し・描画に失敗した。upgrade しない。`);
        fs.rmSync(dir, { recursive: true, force: true });
        return 2;
      }
      const drift = evaluateChartDrift(manifest.stdout, base.value);
      if (drift.length > 0) {
        console.error(`${TAG} 稼働のマニフェストとチェックアウトの描画が ${drift.length} 文書で食い違う（${drift.slice(0, 8).join(', ')}${drift.length > 8 ? ' …' : ''}）。`
          + ' upgrade すると replicas 以外の差まで稼働へ押し込むので止める。稼働と同じ版をチェックアウトして走らせる。');
        fs.rmSync(dir, { recursive: true, force: true });
        return 2;
      }
      const plan = evaluateReplicaOverlayDiff(base.value, scaled.value);
      printPlan(plan);
      if (plan.failures.length > 0) { for (const f of plan.failures) console.error(f); fs.rmSync(dir, { recursive: true, force: true }); return 2; }
      restore = () => {
        console.log(`${TAG} 戻す: helm upgrade（退避した values のまま）→ 1 レプリカ`);
        const u = run(helmBin(), ['upgrade', RELEASE, CHART, '-n', NAMESPACE, '-f', saved], { stdio: ['ignore', 'inherit', 'inherit'] });
        const ok = u.status === 0 && rolloutStatus();
        if (!ok) {
          console.error(`${TAG} 🔴 戻しに失敗した。手で戻す: helm rollback ${RELEASE} -n ${NAMESPACE}（直前の版へ）。退避した values: ${saved}`);
          return false;
        }
        fs.rmSync(dir, { recursive: true, force: true });
        return true;
      };
      console.log(`${TAG} helm upgrade: services.${SERVICE_KEY}.replicas=${TARGET_REPLICAS}`);
      const up = run(helmBin(), ['upgrade', RELEASE, CHART, '-n', NAMESPACE, '-f', saved, '-f', overlay], { stdio: ['ignore', 'inherit', 'inherit'] });
      if (up.status !== 0 || !rolloutStatus()) { failures.push(`${TAG} [前提] ${TARGET_REPLICAS} レプリカへの upgrade か rollout が完了しない。`); return 1; }
    }

    // ── 3. Pod を選ぶ
    const pods0 = kubectlJson(['-n', NAMESPACE, 'get', 'pods', '-l', POD_SELECTOR]);
    const pods = pods0.ok ? selectReadyPods(pods0.value) : [];
    if (pods.length < TARGET_REPLICAS) { failures.push(`${TAG} [前提] Ready の ${DEPLOYMENT} が ${pods.length} 個（${TARGET_REPLICAS} 個以上が要る）。`); return 1; }

    // ── 4. 試験利用者でエッジ経由のログイン
    const ca = edgeCa();
    if (!ca.ok) { failures.push(`${TAG} [前提] エッジ CA を読めない（${ca.error}）。検証を切らない。`); return 2; }
    const edge = (process.env.EDGE_URL || 'https://localhost').replace(/\/+$/, '');
    const login = await loginViaEdge({ edge, ca: ca.value, username: who.username, password: process.env[PASSWORD_ENV], realmName: realm.value.realm || 'platform' });
    if (!login.ok) { failures.push(`${TAG} [前提] ログインできない: ${login.error}`); return 1; }
    const pairs = sessionCookiePairs(login.cookieHeader, process.env.BFF_SESSION_COOKIE || DEFAULT_COOKIE_NAME);
    if (pairs.length === 0) { failures.push(`${TAG} [前提] BFF がセッション Cookie を発行しなかった（コールバック status=${login.callbackStatus}）。`); return 1; }
    const cookie = buildCookieHeader(pairs);
    const tampered = buildCookieHeader(pairs, { tamper: true });
    console.log(`${TAG} ${who.username} でログインした（Cookie ${pairs.length} 片。値は出さない）`);

    const ring = evaluateKeyRing(readKeyRingCount());
    if (ring.failure) failures.push(ring.failure);
    if (ring.notice) notices.push(ring.notice);

    // ── 5. Pod ごとに同じ Cookie を投げる
    const first = await measurePods(pods, cookie, tampered, opts.perPod);
    failures.push(...evaluateCrossReplica({ pods: first, expectedUser: who.username, minPerPod: opts.perPod, label: '相互復号' }));

    // ── 6. 作り直した Pod でも同じ Cookie が通るか（鍵がメモリでなく Redis にある）
    if (opts.restart) {
      const r = run('kubectl', ['-n', NAMESPACE, 'rollout', 'restart', `deploy/${DEPLOYMENT}`], { stdio: ['ignore', 'inherit', 'inherit'] });
      if (r.status !== 0 || !rolloutStatus()) {
        failures.push(`${TAG} [再起動] rollout restart が完了しない。`);
      } else {
        const after0 = kubectlJson(['-n', NAMESPACE, 'get', 'pods', '-l', POD_SELECTOR]);
        const after = (after0.ok ? selectReadyPods(after0.value) : []).filter((p) => !pods.some((q) => q.name === p.name));
        const second = await measurePods(after, cookie, tampered, opts.perPod);
        failures.push(...evaluateCrossReplica({ pods: second, expectedUser: who.username, minPerPod: opts.perPod, label: '作り直し後' }));
      }
    }
    return failures.length > 0 ? 1 : 0;
    })();
  } finally {
    if (!doRestore()) {
      failures.push(`${TAG} 🔴 1 レプリカへ戻せていない（上の手順で手で戻す）。`);
      if (code !== 2) code = 1;
    }
    process.removeListener('SIGINT', onSignal);
    process.removeListener('SIGTERM', onSignal);
    for (const n of notices) console.log(n);
    if (failures.length > 0) {
      console.error(`${TAG} ${failures.length} 件の失敗:`);
      for (const f of failures) console.error(`  - ${f}`);
    }
  }
  // 🔴 合格と言うのは、測定を最後まで通し、失敗が 0 件で、戻しも済んだときだけ（途中の return 2 を合格と読まない）。
  if (code === 0 && failures.length === 0) {
    console.log(`${TAG} OK: 同じ Cookie をすべての Pod が同じ利用者として受け入れ、改ざんした Cookie はすべての Pod が 401 で拒んだ。`);
    return 0;
  }
  return code === 0 ? 1 : code;
}

async function main() {
  const argv = process.argv.slice(2);
  if (!argv.includes('--plan')) {
    // NFR, #1550: 稼働の helm リリースを変え（レプリカを増やす）、Keycloak へログインする。明示の指定が無ければ何もしない。
    requireLiveOptIn('check-bff-multi-replica-session', argv, { offline: '--plan' });
  }
  const opts = parseArgs(argv, process.env);
  if (opts.error) { console.error(`${TAG} ${opts.error}`); process.exit(2); }
  const code = opts.mode === 'plan' ? planMode(opts.values) : await liveMode(opts);
  process.exit(code);
}

if (require.main === module) {
  main().catch((e) => {
    console.error(`${TAG} 実行時エラー: ${e && e.stack ? e.stack : e}`);
    process.exit(1);
  });
}

module.exports = {
  overlayValuesYaml,
  splitManifest,
  evaluateReplicaOverlayDiff,
  evaluateChartDrift,
  findOwningHpa,
  selectReadyPods,
  parsePortForwardPort,
  sessionCookiePairs,
  tamperValue,
  buildCookieHeader,
  evaluateCrossReplica,
  evaluateKeyRing,
  parseArgs,
  resolveProbeUser,
  DEPLOYMENT,
  TARGET_REPLICAS,
  DEFAULT_PER_POD,
  USERNAME_ENV,
  PASSWORD_ENV,
};
