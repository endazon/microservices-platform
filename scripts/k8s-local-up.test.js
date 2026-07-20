#!/usr/bin/env node
'use strict';
/*
 * k8s-local-up.test.js
 * Issue #334 / IADR-0087: scripts/k8s-local-up.sh の opt-in フラグ分岐を横断で固定する smoke test。
 *
 * 方式（IADR-0087）: bash stub-on-PATH（k8s-local-up.sh は無改変）。
 *   外部バイナリ（k3d / kubectl / helm / docker）を PATH 上の「記録スタブ」へ差し替え、
 *   副作用ゼロで k8s-local-up.sh を実行し、発行コマンド列を採取して分岐をアサートする。
 *   K8S_LOCAL_RUNTIME=k3d を固定して runtime 自動判定を回避し、k3d cluster list スタブを非0
 *   （＝未作成）に返させて cluster create 経路を必ず通す。src/ai-stock-trading submodule 未取得
 *   （CI 既定チェックアウト）で AST 分岐（realm 同梱・argocd 追加 apply）は決定的に skip される。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ・bash は前提ツール）。実行: node scripts/k8s-local-up.test.js
 * scripts/scripts.test.js と同型の運用（各 opt-in ゲート個別ではなく共通の一括 smoke test）。
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const UP_SCRIPT = path.join('scripts', 'k8s-local-up.sh'); // REPO_ROOT 相対（cwd=REPO_ROOT で実行）
const CLUSTER = 'testcluster'; // 決定的なクラスタ名（既定 msp-ast-dev に依存しない）

// opt-in ゲート由来のリソース識別トークン（既定オフ時にこれらが「不在」であることを固定する）。
const OPTIN_TOKENS = [
  'deploy/local/infra-persistence', // PERSIST
  'deploy/local/observability', //     OBSERVABILITY
  'deploy/local/vault', //             VAULT
  'vault-dev-token', //                VAULT (secret)
  'deploy/local/headlamp', //          HEADLAMP
  'headlamp-oidc', //                  HEADLAMP (secret)
  'deploy/argocd', //                  ARGOCD
  'namespace argocd', //               ARGOCD (namespace)
  'kube-apiserver-arg=oidc', //        HEADLAMP_OIDC_APISERVER
];

// --- stub-on-PATH ハーネス ---------------------------------------------------

// 記録スタブ本体。全呼び出しの argv を STUB_LOG へ 1 行追記し exit 0 を返す。ただし:
//   - `k3d cluster list ...`               → exit 1（クラスタ未作成 ＝ cluster create 経路へ）。
//   - `kubectl get crd clustersecretstores.*` → exit 0（CRD 有 ＝ VAULT は deploy/local/vault を apply）。
// いずれも副作用は持たない（apply/build/import は記録のみ）。
const K3D_STUB = [
  '#!/usr/bin/env bash',
  'echo "k3d $*" >> "$STUB_LOG"',
  'if [ "${1:-}" = "cluster" ] && [ "${2:-}" = "list" ]; then exit 1; fi',
  'exit 0',
  '',
].join('\n');

const PLAIN_STUB = (name) =>
  ['#!/usr/bin/env bash', `echo "${name} $*" >> "$STUB_LOG"`, 'exit 0', ''].join('\n');

// kubectl スタブは CRD 有無を env で切替可能にする。既定は有（exit 0）＝VAULT は deploy/local/vault を
// apply。STUB_CRD_ABSENT=1 で `kubectl get crd clustersecretstores.*` を非0（未導入）に返させ、
// ESO 未導入フォールバック（WARN ＋ vault-dev.yaml のみ apply）経路を検証できるようにする。
const KUBECTL_STUB = [
  '#!/usr/bin/env bash',
  'echo "kubectl $*" >> "$STUB_LOG"',
  'if [ "${STUB_CRD_ABSENT:-}" = "1" ] && [ "${1:-}" = "get" ] && [ "${2:-}" = "crd" ]; then exit 1; fi',
  'exit 0',
  '',
].join('\n');

/**
 * 与えた env で k8s-local-up.sh を stub 下で実行し、採取したコマンド列を返す。
 * @param {Record<string,string>} extraEnv opt-in などの追加環境変数
 * @returns {{ status: number|null, lines: string[], stdout: string, stderr: string }}
 */
function runUp(extraEnv) {
  const workdir = fs.mkdtempSync(path.join(os.tmpdir(), 'k8s-up-smoke-'));
  const binDir = path.join(workdir, 'bin');
  fs.mkdirSync(binDir);
  const logFile = path.join(workdir, 'commands.log');
  fs.writeFileSync(logFile, '');

  const write = (name, body) => {
    const p = path.join(binDir, name);
    fs.writeFileSync(p, body);
    fs.chmodSync(p, 0o755);
  };
  write('k3d', K3D_STUB);
  write('kubectl', KUBECTL_STUB);
  for (const n of ['helm', 'docker']) write(n, PLAIN_STUB(n));

  const origPath = process.env.PATH || process.env.Path || '';
  // 実行環境に opt-in ゲート/override が漏れていても既定＝全 OFF を再現できるよう、
  // ハーネスが制御する env を基底から一旦除去してから extraEnv を適用する（決定性の担保）。
  const base = { ...process.env };
  for (const k of [
    'HEADLAMP_OIDC_APISERVER',
    'HEADLAMP',
    'PERSIST',
    'OBSERVABILITY',
    'VAULT',
    'ARGOCD',
    'HEADLAMP_OIDC_ISSUER_URL',
    'HEADLAMP_OIDC_CLIENT_ID',
  ]) {
    delete base[k];
  }
  const env = {
    ...base,
    PATH: binDir + path.delimiter + origPath, // stub を優先しつつ coreutils/bash は温存
    STUB_LOG: logFile,
    K8S_LOCAL_RUNTIME: 'k3d', // runtime 自動判定を回避し cluster create 経路を決定的に通す
    ...extraEnv,
  };

  const r = spawnSync('bash', [UP_SCRIPT, CLUSTER], {
    cwd: REPO_ROOT,
    env,
    encoding: 'utf8',
  });

  const raw = fs.readFileSync(logFile, 'utf8');
  const lines = raw.split('\n').filter((l) => l.length > 0);
  try {
    fs.rmSync(workdir, { recursive: true, force: true });
  } catch {
    /* best-effort cleanup */
  }
  return { status: r.status, lines, stdout: r.stdout || '', stderr: r.stderr || '' };
}

// 採取ログから `k3d cluster create ...` の 1 行を取り出す（無ければ null）。
function clusterCreateLine(lines) {
  return lines.find((l) => l.startsWith('k3d cluster create ')) || null;
}
const anyLineHas = (lines, needle) => lines.some((l) => l.includes(needle));

// --- テストランナー（scripts.test.js と同型） --------------------------------

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

// 事前条件: bash が利用可能で、スクリプトが正常終了すること（ハーネス自体の健全性）。
const DEFAULT = runUp({});
ok('前提: 既定実行は exit 0（stub 下で副作用なく完走）', () => {
  assert.strictEqual(DEFAULT.status, 0, `k8s-local-up.sh が非0終了: ${DEFAULT.stderr}`);
});

// 既定（全 OFF）の cluster create 引数は現行とバイト等価（#331 の bash シミュレーションを CI 常設化）。
const EXPECTED_DEFAULT_CREATE = `k3d cluster create ${CLUSTER} --agents 1 -p 8080:80@loadbalancer -p 8443:443@loadbalancer`;
ok('既定: k3d cluster create 引数がバイト等価', () => {
  assert.strictEqual(clusterCreateLine(DEFAULT.lines), EXPECTED_DEFAULT_CREATE);
});

// 既定: opt-in 由来のリソース/引数が一切現れない（副作用ゼロ・fail-safe の固定）。
ok('既定: opt-in 由来リソースが一切現れない', () => {
  for (const tok of OPTIN_TOKENS) {
    assert.ok(!anyLineHas(DEFAULT.lines, tok), `既定オフなのに "${tok}" が現れた`);
  }
});

// HEADLAMP_OIDC_APISERVER=1: apiserver OIDC 4 フラグが cluster create に付与される（IADR-0084）。
ok('HEADLAMP_OIDC_APISERVER=1: apiserver OIDC 4 フラグ付与', () => {
  const line = clusterCreateLine(runUp({ HEADLAMP_OIDC_APISERVER: '1' }).lines);
  assert.ok(line, 'cluster create 行が無い');
  for (const flag of [
    'kube-apiserver-arg=oidc-issuer-url=http://keycloak:8080/realms/microservices-platform@server:0',
    'kube-apiserver-arg=oidc-client-id=headlamp@server:0',
    'kube-apiserver-arg=oidc-username-claim=preferred_username@server:0',
    'kube-apiserver-arg=oidc-username-prefix=oidc:@server:0',
  ]) {
    assert.ok(line.includes(flag), `OIDC フラグ欠落: ${flag}`);
  }
  // 各フラグは --k3s-arg とペアで渡る（4 ペア）。
  assert.strictEqual((line.match(/--k3s-arg /g) || []).length, 4, '--k3s-arg が 4 個でない');
});

// HEADLAMP=1 追従: HEADLAMP_OIDC_APISERVER 未設定でも HEADLAMP=1 で 4 フラグが付く。
ok('HEADLAMP=1 追従: apiserver OIDC フラグ付与', () => {
  const line = clusterCreateLine(runUp({ HEADLAMP: '1' }).lines);
  assert.ok(line && line.includes('kube-apiserver-arg=oidc-issuer-url='), 'HEADLAMP 追従で OIDC 付与されず');
});

// escape-hatch: HEADLAMP=1 でも HEADLAMP_OIDC_APISERVER=0 なら OIDC フラグは付かない
// （cluster create はバイト等価）。一方 HEADLAMP overlay は適用される（両ゲートは独立）。
ok('escape-hatch: HEADLAMP=1 & HEADLAMP_OIDC_APISERVER=0 は OIDC 不付与・overlay は適用', () => {
  const res = runUp({ HEADLAMP: '1', HEADLAMP_OIDC_APISERVER: '0' });
  assert.strictEqual(clusterCreateLine(res.lines), EXPECTED_DEFAULT_CREATE, 'OIDC=0 で create がバイト等価でない');
  assert.ok(anyLineHas(res.lines, 'deploy/local/headlamp'), 'HEADLAMP overlay が適用されていない');
});

// issuer/client override: env で issuer/client を差し替えると引数に反映される。
ok('override: HEADLAMP_OIDC_ISSUER_URL / CLIENT_ID が引数へ反映', () => {
  const line = clusterCreateLine(
    runUp({
      HEADLAMP_OIDC_APISERVER: '1',
      HEADLAMP_OIDC_ISSUER_URL: 'https://kc.example.test/realms/foo',
      HEADLAMP_OIDC_CLIENT_ID: 'myclient',
    }).lines,
  );
  assert.ok(line.includes('oidc-issuer-url=https://kc.example.test/realms/foo@server:0'), 'issuer override 未反映');
  assert.ok(line.includes('oidc-client-id=myclient@server:0'), 'client override 未反映');
});

// PERSIST=1: 永続化オーバーレイ（deploy/local/infra-persistence）へ切替（IADR-0082）。
ok('PERSIST=1: infra-persistence を apply', () => {
  const res = runUp({ PERSIST: '1' });
  assert.ok(anyLineHas(res.lines, 'apply -k deploy/local/infra-persistence'), 'infra-persistence が apply されない');
});

// OBSERVABILITY=1: observability スタックを apply（IADR-0077）。
ok('OBSERVABILITY=1: observability を apply', () => {
  assert.ok(anyLineHas(runUp({ OBSERVABILITY: '1' }).lines, 'apply -k deploy/local/observability'));
});

// VAULT=1（CRD 有）: vault-dev-token secret ＋ deploy/local/vault を apply。
ok('VAULT=1 (CRD 有): vault-dev-token secret と deploy/local/vault を apply', () => {
  const res = runUp({ VAULT: '1' });
  assert.ok(anyLineHas(res.lines, 'vault-dev-token'), 'vault-dev-token secret が作られない');
  assert.ok(anyLineHas(res.lines, 'apply -k deploy/local/vault'), 'deploy/local/vault が apply されない');
});

// VAULT=1（CRD 無・ESO 未導入フォールバック）: kustomize（apply -k deploy/local/vault）ではなく
// vault-dev.yaml のみを apply する例外フローを固定する（Issue #334 の「例外フロー横断」趣旨）。
ok('VAULT=1 (CRD 無): vault-dev.yaml のみ apply・kustomize 経路は通らない', () => {
  const res = runUp({ VAULT: '1', STUB_CRD_ABSENT: '1' });
  assert.ok(anyLineHas(res.lines, 'vault-dev-token'), 'vault-dev-token secret が作られない');
  assert.ok(anyLineHas(res.lines, 'apply -f deploy/local/vault/vault-dev.yaml'), 'vault-dev.yaml フォールバックが apply されない');
  assert.ok(!anyLineHas(res.lines, 'apply -k deploy/local/vault'), 'CRD 無なのに kustomize 経路が通った');
});

// ARGOCD=1: argocd namespace ＋ argocd application manifest を apply。
ok('ARGOCD=1: argocd namespace と application manifest を apply', () => {
  const res = runUp({ ARGOCD: '1' });
  assert.ok(anyLineHas(res.lines, 'namespace argocd'), 'argocd namespace が作られない');
  assert.ok(anyLineHas(res.lines, 'deploy/argocd/application.yaml'), 'argocd application が apply されない');
});

// ARGOCD=1 (#348): 公式 install manifest は巨大 CRD を含み client-side apply では annotation 上限
// （262144 バイト）を超過するため、install 行は server-side apply（--server-side --force-conflicts）で
// 適用されなければならない。URL/バージョンは不変。
ok('ARGOCD=1: install manifest は server-side apply（--server-side --force-conflicts）', () => {
  const res = runUp({ ARGOCD: '1' });
  const installLine = res.lines.find((l) => l.includes('argo-cd/stable/manifests/install.yaml'));
  assert.ok(installLine, 'ArgoCD install manifest の apply 行が無い');
  assert.ok(installLine.includes('apply --server-side'), `install が server-side apply でない: ${installLine}`);
  assert.ok(installLine.includes('--force-conflicts'), `install に --force-conflicts が無い: ${installLine}`);
});

// HEADLAMP=1: headlamp-oidc secret ＋ deploy/local/headlamp を apply（IADR-0080）。
ok('HEADLAMP=1: headlamp-oidc secret と deploy/local/headlamp を apply', () => {
  const res = runUp({ HEADLAMP: '1' });
  assert.ok(anyLineHas(res.lines, 'headlamp-oidc'), 'headlamp-oidc secret が作られない');
  assert.ok(anyLineHas(res.lines, 'apply -k deploy/local/headlamp'), 'deploy/local/headlamp が apply されない');
});

process.stdout.write(`\n✓ ${passed} tests passed\n`);
