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
  'grafana-oidc', //                   OBSERVABILITY (Grafana OIDC secret, IADR-0090)
  'deploy/local/vault', //             VAULT
  'vault-dev-token', //                VAULT (secret)
  'vault-oidc', //                     VAULT (OIDC client secret, IADR-0094)
  'deploy/local/headlamp', //          HEADLAMP
  'headlamp-oidc', //                  HEADLAMP (secret)
  'deploy/argocd', //                  ARGOCD
  'namespace argocd', //               ARGOCD (namespace)
  'argocd-cm-patch.yaml', //           ARGOCD OIDC (CM patch, IADR-0092)
  'oidc.keycloak.clientSecret', //     ARGOCD OIDC (secret patch, IADR-0092)
  'kube-apiserver-arg=oidc', //        HEADLAMP_OIDC_APISERVER
  'deploy/local/edge', //              LOCALEDGE (edge overlay, IADR-0091)
  '50000', //                          LOCALEDGE (admin entrypoint port, IADR-0091)
  'external-secrets', //               ESO (helm install / ns, IADR-0096)
  'deploy/local/vault/eso', //         ESO (bootstrap/externalsecret, IADR-0096)
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
// STUB_NS_ABSENT=1 で `kubectl get namespace argocd` を非0（未作成）に返させ、LOCALEDGE の argocd-ingress
// 条件付き apply の「ns 不在＝skip」フローを検証できるようにする（IADR-0091）。
// STUB_VAULT_DEPLOY_ABSENT=1 で `kubectl -n ... get deploy vault` を非0（未作成）に返させ、ESO=1 の
// 「VAULT=1 なしなら fail-fast」ガード（IADR-0096）を検証できるようにする。
const KUBECTL_STUB = [
  '#!/usr/bin/env bash',
  'echo "kubectl $*" >> "$STUB_LOG"',
  'if [ "${STUB_CRD_ABSENT:-}" = "1" ] && [ "${1:-}" = "get" ] && [ "${2:-}" = "crd" ]; then exit 1; fi',
  'if [ "${STUB_NS_ABSENT:-}" = "1" ] && [ "${1:-}" = "get" ] && [ "${2:-}" = "namespace" ] && [ "${3:-}" = "argocd" ]; then exit 1; fi',
  'if [ "${STUB_VAULT_DEPLOY_ABSENT:-}" = "1" ]; then case "$*" in *"get deploy vault"*) exit 1;; esac; fi',
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
    'LOCALEDGE',
    'ESO',
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

// IADR-0093 (#353): MinIO OIDC の client secret 用 app-secret `minio-oidc` は既定実行で作成される
// （opt-in ではなく MSP app-secrets の一部・平文コミットなし・minio.yaml は optional 参照）。
ok('既定: minio-oidc app-secret が作られる', () => {
  assert.ok(anyLineHas(DEFAULT.lines, 'minio-oidc'), 'minio-oidc secret が作られない');
});

// IADR-0096 (#310): ESO 未設定（既定）では llm-provider-credentials は手動 apply_secret で作成される
// （バイト等価・ExternalSecret へは委譲しない）。
ok('既定: llm-provider-credentials が手動 apply される（ESO 未設定）', () => {
  assert.ok(
    anyLineHas(DEFAULT.lines, 'create secret generic llm-provider-credentials'),
    'llm-provider-credentials の手動 apply_secret が無い',
  );
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

// OBSERVABILITY=1: observability スタックを apply（IADR-0077）＋ Grafana OIDC secret を作成（IADR-0090）。
ok('OBSERVABILITY=1: observability を apply・grafana-oidc secret を作成', () => {
  const res = runUp({ OBSERVABILITY: '1' });
  assert.ok(anyLineHas(res.lines, 'apply -k deploy/local/observability'), 'observability が apply されない');
  // IADR-0090: Grafana generic OAuth の client secret は k8s Secret grafana-oidc 経由（平文コミットなし）。
  assert.ok(anyLineHas(res.lines, 'grafana-oidc'), 'grafana-oidc secret が作られない');
});

// LOCALEDGE=1: k3d cluster create のポートを 80/443/50000 へ切替え、エッジ overlay を apply（IADR-0091・#356）。
// 既定オフ時のバイト等価は上の「既定: k3d cluster create 引数がバイト等価」で固定済み（本ゲートで壊れないこと）。
ok('LOCALEDGE=1: cluster create ポートが loopback 80/443/50000・8080/8443 は不在', () => {
  const line = clusterCreateLine(runUp({ LOCALEDGE: '1' }).lines);
  assert.ok(line, 'cluster create 行が無い');
  // bind は loopback(127.0.0.1) 固定（認証なし Qdrant を LAN へ露出させない）。
  for (const p of [
    '-p 127.0.0.1:80:80@loadbalancer',
    '-p 127.0.0.1:443:443@loadbalancer',
    '-p 127.0.0.1:50000:50000@loadbalancer',
  ]) {
    assert.ok(line.includes(p), `LOCALEDGE ポート欠落: ${p}`);
  }
  // 既定ポート（8080/8443）は LOCALEDGE=1 では現れない（置換であって併存でない）。
  assert.ok(!line.includes('8080:80@loadbalancer'), 'LOCALEDGE=1 なのに 8080 が残っている');
  assert.ok(!line.includes('8443:443@loadbalancer'), 'LOCALEDGE=1 なのに 8443 が残っている');
});

ok('LOCALEDGE=1: エッジ overlay（deploy/local/edge）を apply', () => {
  assert.ok(anyLineHas(runUp({ LOCALEDGE: '1' }).lines, 'apply -k deploy/local/edge'), 'deploy/local/edge が apply されない');
});

// LOCALEDGE の argocd-ingress は「argocd namespace 存在時のみ」条件付き apply（fail-safe・ns 不在で失敗させない）。
// 両分岐を固定する（肯定側=apply / 否定側=skip）。
ok('LOCALEDGE=1 (argocd ns 有): argocd-ingress.yaml を apply', () => {
  const res = runUp({ LOCALEDGE: '1' }); // 既定 stub: get namespace argocd → exit 0（存在扱い）
  assert.ok(anyLineHas(res.lines, 'apply -f deploy/local/edge/argocd-ingress.yaml'), 'ns 有なのに argocd-ingress が apply されない');
});

ok('LOCALEDGE=1 (argocd ns 無): argocd-ingress.yaml は apply しない（skip）', () => {
  const res = runUp({ LOCALEDGE: '1', STUB_NS_ABSENT: '1' }); // get namespace argocd → exit 1（不在）
  assert.ok(anyLineHas(res.lines, 'apply -k deploy/local/edge'), 'edge overlay 自体は apply される');
  assert.ok(!anyLineHas(res.lines, 'argocd-ingress.yaml'), 'ns 不在なのに argocd-ingress が apply された');
});

// VAULT=1（CRD 有）: vault-dev-token secret ＋ deploy/local/vault を apply。
ok('VAULT=1 (CRD 有): vault-dev-token secret と deploy/local/vault を apply', () => {
  const res = runUp({ VAULT: '1' });
  assert.ok(anyLineHas(res.lines, 'vault-dev-token'), 'vault-dev-token secret が作られない');
  assert.ok(anyLineHas(res.lines, 'apply -k deploy/local/vault'), 'deploy/local/vault が apply されない');
  // IADR-0094 (#353): OIDC client secret 用の vault-oidc secret も VAULT=1 で作られる（bootstrap が読む・平文なし）。
  assert.ok(anyLineHas(res.lines, 'vault-oidc'), 'vault-oidc secret が作られない');
});

// VAULT=1（CRD 無・ESO 未導入フォールバック）: kustomize（apply -k deploy/local/vault）ではなく
// vault-dev.yaml のみを apply する例外フローを固定する（Issue #334 の「例外フロー横断」趣旨）。
ok('VAULT=1 (CRD 無): vault-dev.yaml のみ apply・kustomize 経路は通らない', () => {
  const res = runUp({ VAULT: '1', STUB_CRD_ABSENT: '1' });
  assert.ok(anyLineHas(res.lines, 'vault-dev-token'), 'vault-dev-token secret が作られない');
  assert.ok(anyLineHas(res.lines, 'apply -f deploy/local/vault/vault-dev.yaml'), 'vault-dev.yaml フォールバックが apply されない');
  assert.ok(!anyLineHas(res.lines, 'apply -k deploy/local/vault'), 'CRD 無なのに kustomize 経路が通った');
});

// ESO=1 (#310 / IADR-0096): ESO 本体 install＋ExternalSecret apply、かつ llm-provider-credentials の手動 apply は
// スキップ（ExternalSecret に委譲＝二重所有回避）。VAULT=1 併用を前提とする。
ok('ESO=1: external-secrets install＋k8s auth store＋ExternalSecret apply・llm 手動 apply はスキップ', () => {
  const res = runUp({ VAULT: '1', ESO: '1' });
  assert.ok(anyLineHas(res.lines, 'external-secrets'), 'external-secrets(ESO) の install が無い');
  assert.ok(anyLineHas(res.lines, 'deploy/local/vault/eso/vault-auth-rbac.yaml'), 'vault auth-delegator RBAC が apply されない');
  // bootstrap 後に store を kubernetes 認証版へ上書きする（同名 vault-backend）。
  assert.ok(anyLineHas(res.lines, 'deploy/local/vault/eso/clustersecretstore-k8s.yaml'), 'k8s auth の ClusterSecretStore が apply されない');
  assert.ok(anyLineHas(res.lines, 'deploy/local/vault/eso/externalsecret-llm.yaml'), 'ExternalSecret(llm) が apply されない');
  // 二重所有回避: ESO=1 では llm-provider-credentials の手動 apply_secret を出さない。
  assert.ok(
    !anyLineHas(res.lines, 'create secret generic llm-provider-credentials'),
    'ESO=1 なのに llm-provider-credentials を手動 apply している（二重所有）',
  );
});

// IADR-0096 (#310) 回帰: VAULT=1 単独（ESO 未設定）は store を kubernetes 認証へ上書きしない
// ＝既存の token 認証 store（deploy/local/vault）のままで既存フロー（AST ExternalSecret 等）を壊さない（byte 等価）。
ok('VAULT=1 単独: k8s auth store へ上書きしない（token 認証のまま・既存フロー不変）', () => {
  const res = runUp({ VAULT: '1' });
  assert.ok(anyLineHas(res.lines, 'apply -k deploy/local/vault'), 'token 認証 store（deploy/local/vault）が apply されない');
  assert.ok(!anyLineHas(res.lines, 'clustersecretstore-k8s.yaml'), 'ESO 未設定なのに k8s auth store へ上書きした');
});

// IADR-0096 (#310) ガード: ESO=1 は VAULT=1（dev Vault）併用が前提。vault Deployment が無ければ fail-fast する。
ok('ESO=1 単独（vault Deployment 不在）: fail-fast で exit != 0・案内メッセージ', () => {
  const res = runUp({ ESO: '1', STUB_VAULT_DEPLOY_ABSENT: '1' });
  assert.notStrictEqual(res.status, 0, 'ESO=1 単独（vault 不在）なのに正常終了した');
  assert.ok(/VAULT=1/.test(res.stderr), 'ガードの案内（VAULT=1 併用）が stderr に無い');
  // fail-fast のため ExternalSecret などの後続は出さない。
  assert.ok(!anyLineHas(res.lines, 'externalsecret-llm.yaml'), 'ガード後なのに ExternalSecret を apply した');
});

// ARGOCD=1: argocd namespace ＋ argocd application manifest を apply。
ok('ARGOCD=1: argocd namespace と application manifest を apply', () => {
  const res = runUp({ ARGOCD: '1' });
  assert.ok(anyLineHas(res.lines, 'namespace argocd'), 'argocd namespace が作られない');
  assert.ok(anyLineHas(res.lines, 'deploy/argocd/application.yaml'), 'argocd application が apply されない');
});

// ARGOCD=1 (#348 / IADR-0077): 公式 install manifest は巨大 CRD を含み client-side apply では annotation
// 上限（262144 バイト）を超過するため、install 行は server-side apply（--server-side --force-conflicts）で
// 適用されなければならない。URL/バージョンは不変。一方、小さい Application/AppProject は変更最小の原則で
// client-side のまま（--server-side が波及していないことも固定＝回帰ガード）。
ok('ARGOCD=1: install は server-side・Application/AppProject は client-side（--server-side 非波及）', () => {
  const res = runUp({ ARGOCD: '1' });
  const installLine = res.lines.find((l) => l.includes('argo-cd/stable/manifests/install.yaml'));
  assert.ok(installLine, 'ArgoCD install manifest の apply 行が無い');
  assert.ok(installLine.includes('apply --server-side'), `install が server-side apply でない: ${installLine}`);
  assert.ok(installLine.includes('--force-conflicts'), `install に --force-conflicts が無い: ${installLine}`);
  // Application/AppProject の apply 行は client-side のまま（server-side が誤って波及していないこと）。
  const appLine = res.lines.find((l) => l.includes('deploy/argocd/application.yaml'));
  assert.ok(appLine, 'Application/AppProject の apply 行が無い');
  assert.ok(!appLine.includes('--server-side'), `Application/AppProject に --server-side が波及した: ${appLine}`);
});

// ARGOCD=1 (#353 / IADR-0092): install 後に Keycloak OIDC を配線する。argocd-cm/rbac-cm/cmd-params-cm を
// merge patch（既存キー保持）、argocd-secret に oidc.keycloak.clientSecret を merge patch（平文コミットなし）、
// argocd-server を rollout restart（server.insecure/oidc の反映）。
ok('ARGOCD=1: Keycloak OIDC 配線（CM patch＋secret patch＋rollout restart）', () => {
  const res = runUp({ ARGOCD: '1' });
  for (const f of ['argocd-cm-patch.yaml', 'argocd-rbac-cm-patch.yaml', 'argocd-cmdparams-patch.yaml']) {
    const line = res.lines.find((l) => l.includes(f));
    assert.ok(line, `${f} の patch 行が無い`);
    assert.ok(line.includes('patch') && line.includes('--type merge'), `${f} が merge patch でない: ${line}`);
  }
  // client secret は argocd-secret への merge patch（apply による全置換ではない＝server.secretkey を保持）。
  const secLine = res.lines.find((l) => l.includes('oidc.keycloak.clientSecret'));
  assert.ok(secLine, 'argocd-secret への clientSecret patch 行が無い');
  assert.ok(secLine.includes('patch secret argocd-secret') && secLine.includes('--type merge'), `secret が merge patch でない: ${secLine}`);
  assert.ok(!secLine.includes('create secret'), 'argocd-secret を create（全置換）している');
  // server.insecure/oidc の反映のため argocd-server を rollout restart する。
  assert.ok(anyLineHas(res.lines, 'rollout restart deploy/argocd-server'), 'argocd-server の rollout restart が無い');
});

// HEADLAMP=1: headlamp-oidc secret ＋ deploy/local/headlamp を apply（IADR-0080）。
ok('HEADLAMP=1: headlamp-oidc secret と deploy/local/headlamp を apply', () => {
  const res = runUp({ HEADLAMP: '1' });
  assert.ok(anyLineHas(res.lines, 'headlamp-oidc'), 'headlamp-oidc secret が作られない');
  assert.ok(anyLineHas(res.lines, 'apply -k deploy/local/headlamp'), 'deploy/local/headlamp が apply されない');
});

process.stdout.write(`\n✓ ${passed} tests passed\n`);
