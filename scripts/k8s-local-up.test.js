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
//
// IADR-0213 (#817): 照合は素の部分文字列一致（`includes`）ではなく **末尾境界つき一致**（`matchesToken`）で行う。
//   `includes` だと接頭辞関係にあるトークン（下の 3 組）で、短い側が長い側の混入まで拾ってしまい、
//   長い側は**足しても検出力が増えない**（#816 の変異試験 M2 が実測）。
//     deploy/local/observability ⊂ deploy/local/observability-persistence
//     deploy/local/vault         ⊂ deploy/local/vault/eso/
//     deploy/local/edge          ⊂ deploy/local/edge/tls
//   末尾に `/` を付けたトークンは「そのディレクトリと配下すべて」を意味する（そのパス自体は単独では
//   発行されず、配下のファイルだけが apply されるゲート用）。
//   **各トークンが単独で検出力を持つことは、下の「単独検出力」テストが毎回検査する。**
const OPTIN_TOKENS = [
  'deploy/local/infra-persistence', // PERSIST
  'deploy/local/observability', //     OBSERVABILITY
  'deploy/local/observability-persistence', // PERSIST + OBSERVABILITY (IADR-0210)
  'grafana-oidc', //                   OBSERVABILITY (Grafana OIDC secret, IADR-0090)
  'deploy/local/vault', //             VAULT
  'vault-dev-token', //                VAULT (secret)
  'vault-oidc', //                     VAULT (OIDC client secret, IADR-0094)
  'deploy/local/headlamp', //          HEADLAMP
  'headlamp-oidc', //                  HEADLAMP (secret)
  'deploy/argocd/', //                 ARGOCD（配下の appproject/application のみが apply される）
  'namespace argocd', //               ARGOCD (namespace)
  'argocd-cm-patch.yaml', //           ARGOCD OIDC (CM patch, IADR-0092)
  'oidc.keycloak.clientSecret', //     ARGOCD OIDC (secret patch, IADR-0092)
  'kube-apiserver-arg', //             apiserver 引数（IADR-0105 で除去済み・どのゲートでも書かない）
  'deploy/local/edge', //              LOCALEDGE (edge overlay, IADR-0091)
  '50000', //                          LOCALEDGE (admin entrypoint port, IADR-0091)
  'external-secrets', //               ESO (helm install / ns, IADR-0096)
  'deploy/local/vault/eso/', //        ESO (bootstrap/externalsecret, IADR-0096。配下のみが apply される)
  'seed-abac-policies.js', //          ABACSEED (ABAC 初期投入, IADR-0133)
  'seed-search-documents.js', //       SEARCHSEED (検索検証用文書の初期投入, IADR-0284)
  'cert-manager', //                   LOCALEDGE (エッジ TLS 終端, IADR-0206)
  'deploy/local/edge/tls', //          LOCALEDGE (TLS overlay, IADR-0206)
  'certificate/edge-tls', //           LOCALEDGE (証明書 Ready 待ち, IADR-0206)
  'coredns-edge-hosts.yaml', //        LOCALEDGE (エッジ host の pod 側名前解決, IADR-0227)
  'deploy/coredns', //                 LOCALEDGE (coredns の rollout restart/status, IADR-0227)
];

// どのゲートも発行しない「負のトークン」＝ 実行ログからは検出力を測れないもの。
// 対応する混入を合成して与え、他のトークンと同じ 2 条件（一致する／単独で一致する）を課す。
// **例外はこの 1 件だけで、増やすなら理由をここへ書く**（IADR-0213 決定 5）。
const SYNTHETIC_CONTAMINATION = {
  // IADR-0105 (#399): apiserver 引数は「除去したままであること」の回帰固定であり、どの opt-in も発行しない。
  'kube-apiserver-arg': 'k3d cluster create testcluster --agents 1 --k3s-arg --kube-apiserver-arg=foo@server:0',
};

// 識別子の継続文字。トークンの直後がこれらなら「別のより長い名前の一部」であって一致とみなさない。
const IDENT_CHAR = /[A-Za-z0-9_./-]/;

/**
 * opt-in トークンが行に現れるかを **末尾境界**つきで判定する（IADR-0213 / #817）。
 * 先頭側は見ない —— 接頭辞問題は末尾側にしか無く、先頭を縛ると `secret/msp/grafana-oidc` の類を落とす。
 * @param {string} line  発行コマンド列の 1 行
 * @param {string} token OPTIN_TOKENS の要素（末尾 `/` は「配下すべて」の意）
 * @returns {boolean}
 */
function matchesToken(line, token) {
  const dirMode = token.endsWith('/');
  const needle = dirMode ? token.slice(0, -1) : token;
  for (let i = line.indexOf(needle); i !== -1; i = line.indexOf(needle, i + 1)) {
    const after = line[i + needle.length];
    if (after === undefined || !IDENT_CHAR.test(after)) return true;
    if (dirMode && after === '/') return true;
  }
  return false;
}

// --- stub-on-PATH ハーネス ---------------------------------------------------

// 記録スタブ本体。全呼び出しの argv を STUB_LOG へ 1 行追記し exit 0 を返す。ただし:
//   - `k3d cluster list ...`               → exit 1（クラスタ未作成 ＝ cluster create 経路へ）。
//     STUB_CLUSTER_EXISTS=1 で exit 0（既存クラスタ ＝ reuse 経路へ・IADR-0105 の回帰固定で使う）。
//   - `kubectl get crd clustersecretstores.*` → exit 0（CRD 有 ＝ VAULT は deploy/local/vault を apply）。
// いずれも副作用は持たない（apply/build/import は記録のみ）。
const K3D_STUB = [
  '#!/usr/bin/env bash',
  'echo "k3d $*" >> "$STUB_LOG"',
  'if [ "${1:-}" = "cluster" ] && [ "${2:-}" = "list" ] && [ "${STUB_CLUSTER_EXISTS:-}" != "1" ]; then exit 1; fi',
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
// STUB_TRAEFIK_ADMIN_MISSING=1 で `kubectl wait --for=jsonpath=... svc/traefik` を非0（＝反映が来ない）に
// 返させ、HelmChartConfig の reconcile が落ちたときの fail-closed（IADR-0258 / #953）を検証できるように
// する。**診断の `get svc traefik` 等は 0 のままにする**——落ちるのは待ち合わせであって get ではない。
const KUBECTL_STUB = [
  '#!/usr/bin/env bash',
  'echo "kubectl $*" >> "$STUB_LOG"',
  'if [ "${STUB_CRD_ABSENT:-}" = "1" ] && [ "${1:-}" = "get" ] && [ "${2:-}" = "crd" ]; then exit 1; fi',
  'if [ "${STUB_NS_ABSENT:-}" = "1" ] && [ "${1:-}" = "get" ] && [ "${2:-}" = "namespace" ] && [ "${3:-}" = "argocd" ]; then exit 1; fi',
  'if [ "${STUB_VAULT_DEPLOY_ABSENT:-}" = "1" ]; then case "$*" in *"get deploy vault"*) exit 1;; esac; fi',
  'if [ "${STUB_TRAEFIK_ADMIN_MISSING:-}" = "1" ]; then case "$*" in *--for=jsonpath*svc/traefik*) exit 1;; esac; fi',
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
  // node も差し替える。ABACSEED=1 は `node scripts/seed-abac-policies.js` を呼ぶため、素の node のままだと
  // smoke test が実際に投入スクリプトを走らせて（到達しない port-forward を待って）遅くなる。
  // k8s-local-up.sh が node を使うのはこの 1 か所だけなので、記録スタブで足りる。
  for (const n of ['helm', 'docker', 'node']) write(n, PLAIN_STUB(n));

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
    'ABACSEED',
    'SEARCHSEED',
    'HEADLAMP_OIDC_ISSUER_URL',
    'HEADLAMP_OIDC_CLIENT_ID',
    'K3S_IMAGE', // #783: k3s イメージの pin。実行環境に漏れていると既定のバイト等価が崩れる
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
    const hit = DEFAULT.lines.find((l) => matchesToken(l, tok));
    assert.ok(!hit, `既定オフなのに "${tok}" が現れた: ${hit}`);
  }
});

// --- IADR-0213 (#817): 各トークンの「単独の検出力」を毎回測る ------------------
//
// 上の既定経路検査は列挙の中に**効いていないトークン**が混ざっても緑のままになる（#816 の M2 が実測）。
// そこで「opt-in を立てたときに実際に発行される行」を母集合に取り、各トークンについて
//   (1) 1 行以上に一致する（dead token でない）
//   (2) **他のどのトークンも一致しない行**が 1 行以上ある（＝そのトークンだけが落とせる混入が実在する）
// を検査する。冗長なトークンが混ざったらここが名指しで落ちる ——
// 「足したのに守っていない」を人の注意力ではなく機械が持つ（issue #817 受け入れ基準 1・2）。
//
// 母集合は 2 通りの run の和を取る。PERSIST / ESO は他ゲートの出力を *置換* するため
// （observability → observability-persistence / grafana-oidc の手動 apply → ExternalSecret 委譲）、
// 全部立てた run だけでは素の側の行が採れない。
const GATES_ALL = {
  PERSIST: '1',
  OBSERVABILITY: '1',
  VAULT: '1',
  ARGOCD: '1',
  LOCALEDGE: '1',
  ESO: '1',
  ABACSEED: '1',
  SEARCHSEED: '1',
  HEADLAMP: '1',
};
const { PERSIST: _p, ESO: _e, ...GATES_NO_REPLACEMENT } = GATES_ALL;
const EMITTED_LINES = [
  ...new Set([...runUp(GATES_ALL).lines, ...runUp(GATES_NO_REPLACEMENT).lines]),
];

ok('各 opt-in トークンが単独で検出力を持つ（冗長なトークンが無い）', () => {
  const redundant = [];
  const dead = [];
  for (const tok of OPTIN_TOKENS) {
    const synthetic = SYNTHETIC_CONTAMINATION[tok];
    const pool = synthetic ? [synthetic] : EMITTED_LINES;
    const hits = pool.filter((l) => matchesToken(l, tok));
    if (hits.length === 0) {
      dead.push(tok);
      continue;
    }
    const others = OPTIN_TOKENS.filter((o) => o !== tok);
    if (!hits.some((l) => others.every((o) => !matchesToken(l, o)))) redundant.push(tok);
  }
  assert.deepStrictEqual(
    dead,
    [],
    `どのゲートも発行しないトークン（検出力を測れない）: ${dead.join(', ')}` +
      ' —— 綴りが実際の発行と食い違っているか、SYNTHETIC_CONTAMINATION への登録が要る',
  );
  assert.deepStrictEqual(
    redundant,
    [],
    `他のトークンが同じ行を拾うため単独の検出力が無い（足しても守りが増えていない）: ${redundant.join(', ')}`,
  );
});

// 判定そのものの単体検査。境界判定は「接頭辞に一致しない」ことが本体なので、そこを直接固定する。
ok('matchesToken: 末尾境界を見る（接頭辞トークンが長い側の混入を拾わない）', () => {
  assert.ok(matchesToken('kubectl apply -k deploy/local/observability', 'deploy/local/observability'));
  assert.ok(!matchesToken('kubectl apply -k deploy/local/observability-persistence', 'deploy/local/observability'));
  assert.ok(!matchesToken('kubectl apply -k deploy/local/edge/tls', 'deploy/local/edge'));
  assert.ok(!matchesToken('kubectl apply -f deploy/local/vault/eso/x.yaml', 'deploy/local/vault'));
  // 末尾 `/` の付いたトークンは配下すべてに一致する（そのパス自体が単独では発行されないゲート）。
  assert.ok(matchesToken('kubectl apply -f deploy/argocd/application.yaml', 'deploy/argocd/'));
  assert.ok(!matchesToken('kubectl apply -f deploy/argocd-mirror/application.yaml', 'deploy/argocd/'));
  // 行末・区切り文字はいずれも境界（ポート番号やクォートの直前で切れる）。
  assert.ok(matchesToken('k3d cluster create c -p 127.0.0.1:50000:50000@loadbalancer', '50000'));
  assert.ok(!matchesToken('k3d cluster create c -p 127.0.0.1:500001:1@loadbalancer', '50000'));
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

// --- keycloak-theme-platform ConfigMap の自動配線（IADR-0261 / #438 残作業） -----
// 従来は deploy/local/README.md「手動でステップ実行する場合」の手動コマンドが必須だった
// （keycloak-theme-platform ConfigMap が無いと deploy/local/infra/keycloak.yaml の optional 参照が
// 解決できず、loginTheme/accountTheme=platform を Keycloak が見つけられない＝ログイン画面が 500）。
// [3/7] の realm ConfigMap（keycloak-realms）と同じ --from-file + --dry-run=client|apply の
// 冪等パターンで、opt-in ではなく既定実行として自動生成する。

ok('既定: keycloak-theme-platform ConfigMap が [3/7] で自動生成される（キー名が keycloak.yaml の items と対応）', () => {
  const line = DEFAULT.lines.find((l) => l.startsWith('kubectl create configmap keycloak-theme-platform '));
  assert.ok(line, 'keycloak-theme-platform の configmap create が発行されない');
  for (const kv of [
    'login-theme-properties=deploy/keycloak/themes/platform/login/theme.properties',
    'login-css=deploy/keycloak/themes/platform/login/resources/css/platform.css',
    'account-theme-properties=deploy/keycloak/themes/platform/account/theme.properties',
    'account-css=deploy/keycloak/themes/platform/account/resources/css/platform.css',
  ]) {
    assert.ok(line.includes(`--from-file=${kv}`), `--from-file=${kv} が無い: ${line}`);
  }
});

ok('既定: keycloak-theme-platform は realm ConfigMap と同型の dry-run|apply 冪等パターンで適用される', () => {
  const createIdx = DEFAULT.lines.findIndex((l) => l.startsWith('kubectl create configmap keycloak-theme-platform '));
  assert.ok(createIdx >= 0, 'keycloak-theme-platform の create 行が見つからない');
  assert.ok(DEFAULT.lines[createIdx].includes('--dry-run=client -o yaml'), 'dry-run=client -o yaml が無い');
  // パイプ `create … | apply -f -` は両側の stub が並行に起動するため、採取順は
  // create→apply / apply→create のどちらにもなり得る（CI で反転を実測。#438）。
  // 「対で採取されている」ことだけを固定し、順序に依存しない。
  const neighbors = [DEFAULT.lines[createIdx + 1], DEFAULT.lines[createIdx - 1]];
  assert.ok(
    neighbors.includes('kubectl apply -f -'),
    `create の前後いずれにも apply -f - が無い（パイプ先が採取されていない）: 次=${DEFAULT.lines[createIdx + 1]} / 前=${DEFAULT.lines[createIdx - 1]}`,
  );
});

ok('既定: keycloak-theme-platform ConfigMap は keycloak-realms の直後・infra kustomize 適用より前に作られる', () => {
  const realmIdx = DEFAULT.lines.findIndex((l) => l.startsWith('kubectl create configmap keycloak-realms '));
  const themeIdx = DEFAULT.lines.findIndex((l) => l.startsWith('kubectl create configmap keycloak-theme-platform '));
  const infraApplyIdx = DEFAULT.lines.findIndex((l) => l === 'kubectl apply -k deploy/local/infra');
  assert.ok(realmIdx >= 0 && themeIdx >= 0 && infraApplyIdx >= 0, '3 行のいずれかが見つからない');
  assert.ok(realmIdx < themeIdx, 'keycloak-realms より前に keycloak-theme-platform が作られている');
  assert.ok(
    themeIdx < infraApplyIdx,
    'ConfigMap 作成が Deployment 適用（apply -k）より後になっている（初回起動でテーマが解決されない）',
  );
});

ok('deploy/local/infra/keycloak.yaml: theme ConfigMap の items キーが k8s-local-up.sh の生成キーと一致する', () => {
  const infraKc = fs.readFileSync(path.join(REPO_ROOT, 'deploy/local/infra/keycloak.yaml'), 'utf8');
  for (const key of ['login-theme-properties', 'login-css', 'account-theme-properties', 'account-css']) {
    assert.ok(infraKc.includes(`key: ${key}`), `keycloak.yaml の items に key: ${key} が無い`);
  }
  assert.ok(infraKc.includes('name: keycloak-theme-platform'), 'keycloak.yaml が参照する ConfigMap 名が keycloak-theme-platform でない');
});

// --- apiserver OIDC フラグ不付与の回帰固定（IADR-0105 / #399） -----------------
// k8s 1.30+ は --oidc-* を jwt[0] へ変換し issuer.url に https を強制するため、経路B の http issuer
// （KC_HOSTNAME_URL=http://keycloak:8080）でフラグを付けると apiserver が起動できずクラスタが停止する。
// #328 由来の HEADLAMP_OIDC_APISERVER 分岐（HEADLAMP 追従）は除去済み。以下はその再発防止で、
// **どの env の組み合わせでも apiserver 引数を書かない**ことを固定する。
const APISERVER_OIDC_TOKENS = [
  'kube-apiserver-arg', // apiserver 引数そのもの（oidc 以外も含めて書かない）
  '--k3s-arg', // k3s へのパススルー引数（現状 cluster create では一切使わない）
  'oidc-issuer-url',
  'oidc-client-id',
  'oidc-username-claim',
  'oidc-username-prefix',
  '99-headlamp-oidc', // config.yaml.d ドロップイン（スクリプトは生成も配置もしない）
];
const assertNoApiserverOidc = (lines, ctx) => {
  for (const tok of APISERVER_OIDC_TOKENS) {
    assert.ok(!anyLineHas(lines, tok), `${ctx}: apiserver OIDC の痕跡 "${tok}" が現れた`);
  }
};

// HEADLAMP=1（通常の立ち上げ）: Headlamp overlay は適用するが apiserver には一切触れない。
// cluster create 引数は既定とバイト等価（＝クラスタ起動不能のトラップが無い）。
ok('HEADLAMP=1: apiserver OIDC フラグを付けず cluster create は既定とバイト等価', () => {
  const res = runUp({ HEADLAMP: '1' });
  assert.strictEqual(res.status, 0, `HEADLAMP=1 が非0終了: ${res.stderr}`);
  assert.strictEqual(clusterCreateLine(res.lines), EXPECTED_DEFAULT_CREATE, 'HEADLAMP=1 で create がバイト等価でない');
  assertNoApiserverOidc(res.lines, 'HEADLAMP=1');
  assert.ok(anyLineHas(res.lines, 'deploy/local/headlamp'), 'HEADLAMP overlay が適用されていない');
  assert.ok(anyLineHas(res.lines, 'headlamp-oidc'), 'headlamp-oidc secret が作られない（Headlamp 自身の OIDC は不変）');
});

// 除去済み env の no-op 化: 旧 HEADLAMP_OIDC_APISERVER=1 を明示しても何も起きない
// （知らずに残った実行環境の env / 古い手順書をなぞっても壊れない）。
ok('除去済み: HEADLAMP_OIDC_APISERVER=1 を明示しても no-op', () => {
  const res = runUp({ HEADLAMP_OIDC_APISERVER: '1' });
  assert.strictEqual(res.status, 0, `HEADLAMP_OIDC_APISERVER=1 が非0終了: ${res.stderr}`);
  assert.strictEqual(clusterCreateLine(res.lines), EXPECTED_DEFAULT_CREATE, '除去済み env で create が変化した');
  assertNoApiserverOidc(res.lines, 'HEADLAMP_OIDC_APISERVER=1');
});

// 旧 override env（issuer/client）も同様に no-op（値が引数へ漏れない）。
ok('除去済み: HEADLAMP_OIDC_ISSUER_URL / CLIENT_ID は引数へ影響しない', () => {
  const res = runUp({
    HEADLAMP: '1',
    HEADLAMP_OIDC_APISERVER: '1',
    HEADLAMP_OIDC_ISSUER_URL: 'https://kc.example.test/realms/foo',
    HEADLAMP_OIDC_CLIENT_ID: 'myclient',
  });
  assert.strictEqual(clusterCreateLine(res.lines), EXPECTED_DEFAULT_CREATE, 'override env で create が変化した');
  assert.ok(!anyLineHas(res.lines, 'kc.example.test'), 'issuer override が引数へ漏れた');
  assert.ok(!anyLineHas(res.lines, 'myclient'), 'client override が引数へ漏れた');
});

// 既存クラスタ reuse 時（k3d cluster list ヒット）も同様: 後付け不可の WARN ごと除去済みで、
// reuse メッセージ以外に apiserver 由来の出力・引数が出ない。
ok('HEADLAMP=1 × 既存クラスタ reuse: 再作成 WARN も apiserver 引数も出ない', () => {
  const res = runUp({ HEADLAMP: '1', STUB_CLUSTER_EXISTS: '1' });
  assert.strictEqual(res.status, 0, `reuse 経路が非0終了: ${res.stderr}`);
  assert.strictEqual(clusterCreateLine(res.lines), null, 'reuse なのに cluster create が呼ばれた');
  assertNoApiserverOidc(res.lines, 'reuse');
  assert.ok(!/OIDC/.test(res.stderr), `reuse で OIDC の WARN が出た: ${res.stderr}`);
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

// --- IADR-0210 (#787): 可観測性スタックの永続化 overlay のゲート意味論 --------------------------
//
// `deploy/local/observability-persistence` は **PERSIST=1 かつ OBSERVABILITY=1** のときだけ選ばれる。
// 既定オフは上の OPTIN_TOKENS が固定済み。ここでは「片肺では現れない」「両方立てたら *置換* であって併存でない」
// の 2 点を固定する。素の overlay 名は永続化版の **接頭辞**なので、素の側は末尾境界を見て判定する
// （`includes` で見ると `-persistence` の行にマッチし、置換の検査が常に成功する静かな縮退になる）。
// 境界判定は OPTIN_TOKENS と同じ `matchesToken` を使う（同じ規則の実装を 2 つ持たない・IADR-0213）。
const appliesBareObservability = (lines) =>
  lines.some((l) => l.includes('apply -k ') && matchesToken(l, 'deploy/local/observability'));

ok('PERSIST=1 単独: observability-persistence は現れない（スタック自体が立たない）', () => {
  const res = runUp({ PERSIST: '1' });
  assert.ok(
    !anyLineHas(res.lines, 'deploy/local/observability-persistence'),
    'OBSERVABILITY 無効なのに可観測性の永続化 overlay が現れた',
  );
  assert.ok(!appliesBareObservability(res.lines), 'OBSERVABILITY 無効なのに素の observability が apply された');
});

ok('PERSIST=1 + OBSERVABILITY=1: observability-persistence へ置換される（素の overlay は現れない）', () => {
  const res = runUp({ PERSIST: '1', OBSERVABILITY: '1' });
  assert.strictEqual(res.status, 0, `PERSIST+OBSERVABILITY で異常終了した: ${res.stderr}`);
  assert.ok(
    anyLineHas(res.lines, 'apply -k deploy/local/observability-persistence'),
    'observability-persistence が apply されない',
  );
  assert.ok(
    !appliesBareObservability(res.lines),
    'PERSIST=1 なのに素の deploy/local/observability も apply された（置換でなく併存になっている）',
  );
  // 永続化しても collector の forwarding 切替と Grafana OIDC secret は不変（ゲートの意味論を変えない）。
  assert.ok(anyLineHas(res.lines, 'rollout restart deploy/otel-collector'), 'collector の rollout restart が消えた');
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

// --- IADR-0258 (#953): HelmChartConfig の反映は fail-closed で待つ ---------------------
//
// `kubectl apply` が見るのは「オブジェクトを置けたか」だけで、**反映は k3s の helm-controller が
// 非同期に**行う。そこで落ちても呼び出し側へは伝わらない —— 実測では admin(50000) が立たないまま
// up が EXIT=0 で返った（GitHub ホストランナー・run 32554867883・traefik chart 25.0.3 の型不一致）。
//
// **この門は、消されても弱められても誰も気付かない。** up のログは長く、緑で返れば誰も読まない。
// だから人の注意力ではなく機械が持つ（[IADR-0255] 決定 1 と同じ判断）。
//
// 🔴 **対照（正常系）と変異（反映が来ない）を対で見る。** 片方だけだと、「常に落ちる実装」も
// 「常に通る実装」も緑になる —— 変異だけなら `exit 1` を無条件に書けば通り、対照だけなら
// 待ち合わせを丸ごと消しても通る。

ok('#953: 反映（traefik svc の admin=50000）が来なければ up は非 0 で終わる（変異試験）', () => {
  const broken = runUp({ LOCALEDGE: '1', STUB_TRAEFIK_ADMIN_MISSING: '1' });
  assert.notStrictEqual(broken.status, 0, 'reconcile が反映されないのに up が成功で返った（#953 の欠陥そのもの）');
  // 待ち合わせが実際に発行されていること（`exit 1` を別の理由で踏んでいないことの裏取り）。
  assert.ok(
    broken.lines.some((l) => l.includes(' wait ') && l.includes('--for=jsonpath') && l.includes('svc/traefik') && l.includes('50000')),
    `反映の待ち合わせ（kubectl wait --for=jsonpath ... svc/traefik）が発行されていない`,
  );
  // **落ちる位置が原因の位置に近いこと。** 後続段（cert-manager）まで進んでから落ちるのでは、
  // 何が壊れたのか読み取れない（従来は最後まで走り切って緑だった）。
  assert.ok(!anyLineHas(broken.lines, 'cert-manager'), '反映に失敗したのに後続の cert-manager 段まで進んでいる');
});

ok('#953: 対照 —— 反映が来れば LOCALEDGE=1 は従来どおり完走する（門が常に落ちる実装ではない）', () => {
  const healthy = runUp({ LOCALEDGE: '1' });
  assert.strictEqual(healthy.status, 0, `反映が来ているのに up が落ちた: ${healthy.stderr}`);
  assert.ok(anyLineHas(healthy.lines, 'cert-manager'), '正常系なのに後続段へ進んでいない（門を置く位置が誤っている）');
});

// --- K3S_IMAGE による k3s の pin（NFR / #783・#442 子 5） -----------------------
// CI では k3s のバージョンを固定する。**揃っていないことが静かに素通りする**ためであり
// （traefik chart 25 系では admin(50000) の reconcile が型不一致で落ちるが up は EXIT=0 で返る。#953）、
// 「好みでバージョンを合わせる」話ではない。既定（未設定）は 1 バイトも変えない。
ok('K3S_IMAGE 未設定: cluster create 引数に --image が現れない（既定バイト等価）', () => {
  assert.ok(!anyLineHas(DEFAULT.lines, '--image'), '既定なのに --image が現れた');
  assert.strictEqual(clusterCreateLine(DEFAULT.lines), EXPECTED_DEFAULT_CREATE);
});

ok('K3S_IMAGE 設定時: cluster create に --image <値> が付く', () => {
  const line = clusterCreateLine(runUp({ K3S_IMAGE: 'rancher/k3s:v1.35.4-k3s1' }).lines);
  assert.ok(line, 'cluster create 行が採取できていない');
  assert.ok(line.includes('--image rancher/k3s:v1.35.4-k3s1'), `--image が付いていない: ${line}`);
  // 既定のポート指定を壊していない（追加であって置換ではない）。
  assert.ok(line.includes('-p 8080:80@loadbalancer'), `既定ポートが失われた: ${line}`);
});

ok('K3S_IMAGE は LOCALEDGE=1 とも併用できる（ポートの切替を壊さない）', () => {
  const line = clusterCreateLine(runUp({ LOCALEDGE: '1', K3S_IMAGE: 'rancher/k3s:v1.35.4-k3s1' }).lines);
  assert.ok(line.includes('--image rancher/k3s:v1.35.4-k3s1'), `--image が付いていない: ${line}`);
  assert.ok(line.includes('127.0.0.1:50000:50000@loadbalancer'), `LOCALEDGE のポートが失われた: ${line}`);
  assert.ok(!line.includes('8080:80@loadbalancer'), `LOCALEDGE=1 なのに既定ポートが残っている: ${line}`);
});

// LOCALEDGE の argocd-ingress は「argocd namespace 存在時のみ」条件付き apply（fail-safe・ns 不在で失敗させない）。
// 両分岐を固定する（肯定側=apply / 否定側=skip）。
ok('LOCALEDGE=1 (argocd ns 有): argocd-ingress.yaml を apply', () => {
  const res = runUp({ LOCALEDGE: '1' }); // 既定 stub: get namespace argocd → exit 0（存在扱い）
  assert.ok(anyLineHas(res.lines, 'apply -f deploy/local/edge/argocd-ingress.yaml'), 'ns 有なのに argocd-ingress が apply されない');
});

// ABACSEED=1: ABAC の属性辞書・ポリシーを dev 既定値で投入する（IADR-0133 / #517）。
// ポリシー 0 件だと deny-by-default で「認証を通しても画面が空」になるため、その回避を opt-in で用意する。
ok('ABACSEED=1: seed-abac-policies.js を実行する', () => {
  const res = runUp({ ABACSEED: '1' });
  assert.strictEqual(res.status, 0, 'ABACSEED=1 で異常終了した');
  assert.ok(anyLineHas(res.lines, 'seed-abac-policies.js'), '投入スクリプトが実行されない');
  // 投入はクラスタ内の稼働サービスに対して行う＝chart/manifest を書き換えないことを固定する。
  assert.ok(!anyLineHas(res.lines, 'deploy/local/abac-seed'), 'シードを kubectl apply してはいけない');
});

// SEARCHSEED=1: 検索検証用の**本文つき**文書を投入する（IADR-0284 / #992）。
// 本文が無いと MarkdownUri が立たず、IngestionService の早期 return で索引に一度も入らない
// ——「検索が壊れている」と「該当が無い」が CI で区別できないまま残る。
ok('SEARCHSEED=1: seed-search-documents.js を実行する', () => {
  const res = runUp({ SEARCHSEED: '1' });
  assert.strictEqual(res.status, 0, 'SEARCHSEED=1 で異常終了した');
  assert.ok(anyLineHas(res.lines, 'seed-search-documents.js'), '投入スクリプトが実行されない');
  // ABAC 投入と同じく、シードは chart/manifest ではなく稼働サービスへ入れる。
  assert.ok(!anyLineHas(res.lines, 'deploy/local/search-seed'), 'シードを kubectl apply してはいけない');
});

ok('SEARCHSEED=1: ABAC 投入とは独立に効く（片方だけ立てても他方は走らない）', () => {
  // 🔴 2 つの opt-in を 1 つのフラグへ畳まないことを固定する。畳むと「ABAC は要るが文書は要らない」
  //    使い方ができなくなり、文書を作る副作用が ABACSEED へ紛れ込む。
  assert.ok(!anyLineHas(runUp({ ABACSEED: '1' }).lines, 'seed-search-documents.js'),
    'ABACSEED=1 だけで文書 seed が走っている（副作用が紛れ込んでいる）');
  assert.ok(!anyLineHas(runUp({ SEARCHSEED: '1' }).lines, 'seed-abac-policies.js'),
    'SEARCHSEED=1 だけで ABAC 投入が走っている');
});

ok('SEARCHSEED=1: 投入が失敗しても up 全体は止めない（best-effort）', () => {
  const src = fs.readFileSync(path.join(REPO_ROOT, 'scripts', 'k8s-local-up.sh'), 'utf8');
  const block = src.slice(src.indexOf('SEARCHSEED'));
  assert.ok(/\|\|\s*echo\s+"?\s*WARN/.test(block), 'SEARCHSEED の投入失敗が best-effort になっていない');
});

ok('ABACSEED=1: 投入が失敗しても up 全体は止めない（best-effort）', () => {
  // node スタブを非0 に差し替える代わりに、存在しないシードディレクトリを指して実失敗させる…のではなく、
  // ここでは「|| で握る」構造そのものを固定する（スタブは常に exit 0 のため、構造を静的に確認する）。
  const src = fs.readFileSync(path.join(REPO_ROOT, 'scripts', 'k8s-local-up.sh'), 'utf8');
  const block = src.slice(src.indexOf('ABACSEED'));
  assert.ok(/\|\|\s*echo\s+"?\s*WARN/.test(block), 'ABACSEED の投入失敗が best-effort になっていない');
});

// IADR-0206 (#779): エッジ TLS 終端。cert-manager を導入し selfsigned→CA の 2 段で edge-tls を発行する。
// 既定オフは上の OPTIN_TOKENS（'cert-manager' / 'deploy/local/edge/tls' / 'certificate/edge-tls'）で固定済み。
ok('LOCALEDGE=1: cert-manager を server-side apply し、CRD Established を待ってから tls overlay を当てる', () => {
  const { lines } = runUp({ LOCALEDGE: '1' });
  const idx = (needle) => lines.findIndex((l) => l.includes(needle));

  const install = idx('cert-manager/releases/download/');
  const waitCrd = idx('condition=Established');
  const applyTls = idx('apply -k deploy/local/edge/tls');
  const waitCert = idx('certificate/edge-tls');

  assert.ok(install !== -1, 'cert-manager の install manifest が apply されない');
  assert.ok(waitCrd !== -1, 'CRD の Established を待っていない');
  assert.ok(applyTls !== -1, 'deploy/local/edge/tls が apply されない');
  assert.ok(waitCert !== -1, 'edge-tls 証明書の Ready を待っていない');

  // 順序が本質。CRD が Established になる前に tls/ を当てると
  // "no matches for kind Certificate" で落ちる（IADR-0206 決定 5）。
  assert.ok(install < waitCrd, 'CRD 待ちが install より前にある');
  assert.ok(waitCrd < applyTls, 'tls overlay の apply が CRD 待ちより前にある');
  assert.ok(applyTls < waitCert, '証明書 Ready 待ちが apply より前にある');

  // 大 CRD の annotation 262144B 上限を避ける（IADR-0088 が ArgoCD で是正した先例）。
  assert.ok(
    lines.some((l) => l.includes('cert-manager/releases/download/') && l.includes('--server-side')),
    'cert-manager の apply が --server-side でない（大 CRD で 262144B 上限に当たる）',
  );

  // バージョンは固定する（IADR-0088: 浮動タグを使わない）。
  assert.ok(
    lines.some((l) => /cert-manager\/releases\/download\/v\d+\.\d+\.\d+\//.test(l)),
    'cert-manager のバージョンが固定されていない',
  );
});

// IADR-0206 (#779): apiserver には触らない。IADR-0105 の除去を維持することが本子の受け入れ基準である
// （再導入は #781）。上の OPTIN_TOKENS に 'kube-apiserver-arg' が在るので既定は固定済みだが、
// **LOCALEDGE=1 でも現れない**ことを別に固定する —— https issuer ができた瞬間に配線したくなる場所だから。
ok('LOCALEDGE=1: TLS を入れても apiserver の OIDC 引数は現れない（IADR-0105 の除去を維持）', () => {
  const { lines } = runUp({ LOCALEDGE: '1' });
  for (const tok of ['kube-apiserver-arg', 'oidc-issuer-url', 'oidc-ca-file', '99-headlamp-oidc']) {
    assert.ok(!anyLineHas(lines, tok), `LOCALEDGE=1 で apiserver 引数 "${tok}" が現れた（#781 の領分）`);
  }
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

// IADR-0097 (#310) PR-2: ESO=1 で minio-credentials/wikijs-db/wikijs-sync も ExternalSecret 供給し、手動 apply はスキップ。
ok('ESO=1 (PR-2): minio/wikijs 系 3 ExternalSecret apply・手動 apply はスキップ', () => {
  const res = runUp({ VAULT: '1', ESO: '1' });
  for (const f of ['externalsecret-minio.yaml', 'externalsecret-wikijs-db.yaml', 'externalsecret-wikijs-sync.yaml']) {
    assert.ok(anyLineHas(res.lines, `deploy/local/vault/eso/${f}`), `${f} が apply されない`);
  }
  for (const name of ['minio-credentials', 'wikijs-db', 'wikijs-sync']) {
    assert.ok(
      !anyLineHas(res.lines, `create secret generic ${name}`),
      `ESO=1 なのに ${name} を手動 apply している（二重所有）`,
    );
  }
});

// IADR-0097 (#310) PR-2 回帰: 既定（ESO 未設定）は 3 secret を手動 apply する（バイト等価）。
ok('既定 (PR-2): minio-credentials/wikijs-db/wikijs-sync を手動 apply する（ESO 未設定）', () => {
  for (const name of ['minio-credentials', 'wikijs-db', 'wikijs-sync']) {
    assert.ok(anyLineHas(DEFAULT.lines, `create secret generic ${name}`), `${name} の手動 apply が無い`);
  }
});

// NFR, ADR-0002 (#1012): postgres-app（サービス DB のパスワード）も PR-2 の 3 兄弟と同じ扱いにする。
//
// 🔴 **この 2 本が守るのは「手動 apply を止めたなら、代わりの供給元を必ず置く」という対である。**
// #1012 は appsettings.json から接続文字列を撤去し、helm の deployment.yaml が postgres-app を
// **非 optional** で参照するようにした。そのうえで手動 apply を `ESO != 1` ブロックへ入れたため、
// **対応する ExternalSecret を置き忘れると ESO=1 で供給元が 1 つも無くなり**、DB を持つ 8 サービス
// （document / datasource / conversion / authorization / wiki / dashboard / graph / feedback）が
// `CreateContainerConfigError` で起動しなくなる。実際にその状態で一度コミットされ、レビューが検出した。
//
// **この「手動 apply とExternalSecret の対応」を横断で見る機械検査は無い**（secret ごとの本テストだけが見る）。
// 同型がもう一度起きたら、`ESO != 1` ブロック内の apply_secret を走査して
// `externalsecret-<name>.yaml` の実在と apply を突合する検査へ一般化すること
// （CLAUDE.md「検査器の追加は同型の事故が 2 回起きたら」。1 回目の記録がこのコメントである）。
ok('ESO=1 (#1012): postgres-app の ExternalSecret を apply・手動 apply はスキップ', () => {
  const res = runUp({ VAULT: '1', ESO: '1' });
  assert.ok(
    anyLineHas(res.lines, 'deploy/local/vault/eso/externalsecret-postgres-app.yaml'),
    'externalsecret-postgres-app.yaml が apply されない（ESO=1 で postgres-app の供給元が無くなる）',
  );
  assert.ok(
    !anyLineHas(res.lines, 'create secret generic postgres-app'),
    'ESO=1 なのに postgres-app を手動 apply している（二重所有）',
  );
});

// 回帰: 既定（ESO 未設定）は postgres-app を手動 apply する。
ok('既定 (#1012): postgres-app を手動 apply する（ESO 未設定）', () => {
  assert.ok(
    anyLineHas(DEFAULT.lines, 'create secret generic postgres-app'),
    'postgres-app の手動 apply が無い（ESO 未設定では唯一の供給元）',
  );
});

// IADR-0098 (#310) PR-3: ESO=1 で OIDC client secret 群（minio/grafana/vault/headlamp-oidc）も ExternalSecret 供給し、
// 各機能ゲート内の手動 apply はスキップする（二重所有回避）。ゲートを全て有効化して skip を確認する。
ok('ESO=1 (PR-3): OIDC 4 ExternalSecret apply・4 OIDC secret の手動 apply はスキップ', () => {
  const res = runUp({ VAULT: '1', ESO: '1', OBSERVABILITY: '1', HEADLAMP: '1' });
  for (const f of [
    'externalsecret-minio-oidc.yaml',
    'externalsecret-grafana-oidc.yaml',
    'externalsecret-vault-oidc.yaml',
    'externalsecret-headlamp-oidc.yaml',
  ]) {
    assert.ok(anyLineHas(res.lines, `deploy/local/vault/eso/${f}`), `${f} が apply されない`);
  }
  for (const name of ['minio-oidc', 'grafana-oidc', 'vault-oidc', 'headlamp-oidc']) {
    assert.ok(
      !anyLineHas(res.lines, `create secret generic ${name}`),
      `ESO=1 なのに ${name} を手動 apply している（二重所有）`,
    );
  }
});

// IADR-0098 (#310) PR-3 回帰: 既定（ESO 未設定）は 4 OIDC secret を手動 apply する（バイト等価）。minio-oidc は
// 常時（step 5）、grafana/vault/headlamp-oidc は各ゲート有効時。ゲートを全て有効化して手動 apply の存置を確認する。
ok('既定 (PR-3): 4 OIDC secret を手動 apply する（ESO 未設定・各ゲート有効）', () => {
  const res = runUp({ OBSERVABILITY: '1', VAULT: '1', HEADLAMP: '1' });
  for (const name of ['minio-oidc', 'grafana-oidc', 'vault-oidc', 'headlamp-oidc']) {
    assert.ok(anyLineHas(res.lines, `create secret generic ${name}`), `${name} の手動 apply が無い`);
  }
  // ESO 未設定なので OIDC の ExternalSecret は apply されない（byte 等価・fail-safe）。
  assert.ok(!anyLineHas(res.lines, 'externalsecret-grafana-oidc.yaml'), 'ESO 未設定なのに OIDC ExternalSecret を apply した');
});

// IADR-0098 (#310) PR-3: OIDC ExternalSecret はゲート意味論に整合させる。ESO=1 かつ OBSERVABILITY/HEADLAMP が
// 無効なら grafana-oidc/headlamp-oidc ExternalSecret は apply しない（機能オフ時に未使用 Secret を残さない）。
// minio-oidc（常時）と vault-oidc（VAULT 前提＝ESO ガード下で常に真）は供給する。
ok('ESO=1 (PR-3): OBSERVABILITY/HEADLAMP 無効なら grafana/headlamp-oidc ES は apply しない', () => {
  const res = runUp({ VAULT: '1', ESO: '1' }); // OBSERVABILITY/HEADLAMP は未設定
  assert.ok(anyLineHas(res.lines, 'externalsecret-minio-oidc.yaml'), 'minio-oidc ES が apply されない（常時のはず）');
  assert.ok(anyLineHas(res.lines, 'externalsecret-vault-oidc.yaml'), 'vault-oidc ES が apply されない（VAULT 前提で常時のはず）');
  assert.ok(!anyLineHas(res.lines, 'externalsecret-grafana-oidc.yaml'), 'OBSERVABILITY 無効なのに grafana-oidc ES を apply した');
  assert.ok(!anyLineHas(res.lines, 'externalsecret-headlamp-oidc.yaml'), 'HEADLAMP 無効なのに headlamp-oidc ES を apply した');
});

// IADR-0099 (#310) PR-4: 基盤 secret（postgres/rabbitmq/keycloak-admin）は step 4 infra rollout で **非 optional** に
// 消費されるため、ESO=1 でも手動 apply を **スキップしない**（bootstrap 必須）。ESO は creationPolicy: Merge の
// ExternalSecret で既存 Secret に同一値を上書きするのみ（PR-1〜3 の Owner+skip とは扱いが異なる）。
ok('ESO=1 (PR-4): 基盤 3 ExternalSecret(Merge) apply・手動 apply は保持（スキップしない）', () => {
  const res = runUp({ VAULT: '1', ESO: '1' });
  for (const f of ['externalsecret-postgres.yaml', 'externalsecret-rabbitmq.yaml', 'externalsecret-keycloak-admin.yaml']) {
    assert.ok(anyLineHas(res.lines, `deploy/local/vault/eso/${f}`), `${f} が apply されない`);
  }
  // 基盤 secret は ESO=1 でも手動 apply を保持する（infra rollout の bootstrap 必須・非 optional 消費）。
  for (const name of ['postgres', 'rabbitmq', 'keycloak-admin']) {
    assert.ok(
      anyLineHas(res.lines, `create secret generic ${name}`),
      `ESO=1 で基盤 ${name} の手動 apply が消えた（bootstrap 破壊）`,
    );
  }
});

// IADR-0099 (#310) PR-4 回帰: 既定（ESO 未設定）は基盤 3 secret を手動 apply し、基盤 ExternalSecret は apply しない（byte 等価）。
ok('既定 (PR-4): 基盤 3 secret を手動 apply・ExternalSecret は無し（ESO 未設定）', () => {
  for (const name of ['postgres', 'rabbitmq', 'keycloak-admin']) {
    assert.ok(anyLineHas(DEFAULT.lines, `create secret generic ${name}`), `${name} の手動 apply が無い`);
  }
  assert.ok(!anyLineHas(DEFAULT.lines, 'externalsecret-postgres.yaml'), 'ESO 未設定なのに基盤 ExternalSecret を apply した');
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

// IADR-0100 (#354 障害2): ノード inotify 上限を引き上げる sysctl DaemonSet を既定 infra へ追加し、アプリ Pod より
// 前（[4/7]）に rollout を待つ。inotify 枯渇による FileSystemWatcher クラッシュ（広範 CrashLoopBackOff）の恒久修正。
ok('既定: inotify-sysctl DaemonSet の rollout を待つ（アプリ起動前）', () => {
  assert.ok(
    DEFAULT.lines.some((l) => l.includes('rollout status ds/inotify-sysctl')),
    'up-script が ds/inotify-sysctl の rollout を待っていない',
  );
});

ok('inotify-sysctl: infra kustomize に収録され両 sysctl キーを特権で設定する', () => {
  const kustomize = fs.readFileSync(path.join(REPO_ROOT, 'deploy/local/infra/kustomization.yaml'), 'utf8');
  assert.ok(/inotify-sysctl\.yaml/.test(kustomize), 'infra kustomization に inotify-sysctl.yaml が無い');

  const dsPath = path.join(REPO_ROOT, 'deploy/local/infra/inotify-sysctl.yaml');
  assert.ok(fs.existsSync(dsPath), 'deploy/local/infra/inotify-sysctl.yaml が無い');
  const ds = fs.readFileSync(dsPath, 'utf8');
  assert.ok(/kind:\s*DaemonSet/.test(ds), 'DaemonSet でない');
  // /proc/sys/fs/inotify/<key> への書き込み（＞リダイレクト）を確認する（コメント中の記述に一致させない）。
  assert.ok(/>\s*\/proc\/sys\/fs\/inotify\/max_user_instances/.test(ds), 'max_user_instances を書き込んでいない');
  assert.ok(/>\s*\/proc\/sys\/fs\/inotify\/max_user_watches/.test(ds), 'max_user_watches を書き込んでいない');
  // sysctl 直書きは特権 initContainer で行う（safe-sysctl allowlist 非経由）。
  assert.ok(/initContainers:/.test(ds), 'initContainer が無い');
  assert.ok(/privileged:\s*true/.test(ds), '特権 initContainer（privileged: true）が無い');
  // 待機コンテナは BusyBox 非対応の `sleep infinity`（GNU 拡張）をコマンドに使わない（Crash→DaemonSet
  // CrashLoop 防止・PR #375 指摘）。コマンド引数（クォート内）でのみ判定し、説明コメントには一致させない。
  assert.ok(!/["']sleep infinity["']/.test(ds), 'command に `sleep infinity`（BusyBox 非対応）を使っている');
  assert.ok(/tail -f \/dev\/null/.test(ds), '待機コマンドが tail -f /dev/null でない（BusyBox 安全な無限待機）');
});

// #310 フォローアップ（apiVersion 移行 fix）: ESO の SecretStore/ClusterSecretStore/ExternalSecret は、インストール
// 済み ESO（v1 GA・v1beta1 は served=false）と整合するよう **external-secrets.io/v1** を使う。v1beta1 が 1 本でも
// 残ると `no matches for kind ... in version "external-secrets.io/v1beta1"` で apply が失敗する（本 fix の回帰ガード）。
ok('ESO manifests: external-secrets.io の apiVersion は v1（v1beta1 残存ゼロ）', () => {
  // deploy/local/vault 配下を **再帰** 走査する（eso/・oidc/ や将来のサブディレクトリに ESO マニフェストが
  // 追加されても v1beta1 再混入を検知できるようにする・PR #374 レビュー指摘）。
  const walkYaml = (dir, acc) => {
    for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, ent.name);
      if (ent.isDirectory()) walkYaml(full, acc);
      else if (ent.name.endsWith('.yaml')) acc.add(full);
    }
    return acc;
  };
  const yamls = walkYaml(path.join(REPO_ROOT, 'deploy/local/vault'), new Set());
  let checked = 0;
  for (const file of yamls) {
    const text = fs.readFileSync(file, 'utf8');
    assert.ok(
      !/external-secrets\.io\/v1beta1/.test(text),
      `${path.relative(REPO_ROOT, file)} に external-secrets.io/v1beta1 が残存（v1 へ移行漏れ）`,
    );
    // ESO の kind を含むファイルは v1 apiVersion を持つこと。
    if (/kind:\s*(ExternalSecret|ClusterSecretStore|SecretStore)\b/.test(text)) {
      assert.ok(
        /apiVersion:\s*external-secrets\.io\/v1\b/.test(text),
        `${path.relative(REPO_ROOT, file)} の ESO apiVersion が external-secrets.io/v1 でない`,
      );
      checked += 1;
    }
  }
  assert.ok(checked >= 13, `ESO マニフェストの検査数が想定未満（${checked} < 13）＝ファイル移動/欠落の疑い`);
});

// #310 フォローアップ（apiVersion 移行 fix）: ESO chart 版を pin する（latest 追従禁止）。無指定だと v1beta1 提供を
// 停止した版を掴んだ瞬間、v1 マニフェストと乖離して壊れる。--version 引数の存在を固定する（再現性の回帰ガード）。
ok('up-script: ESO chart 版が pin されている（helm install に --version）', () => {
  const text = fs.readFileSync(path.join(REPO_ROOT, UP_SCRIPT), 'utf8');
  // helm install は行継続（\）で複数行に跨るため、install 開始位置からブロックを見て --version を確認する。
  const idx = text.indexOf('helm upgrade --install external-secrets external-secrets/external-secrets');
  assert.ok(idx >= 0, 'ESO の helm upgrade --install 行が見つからない');
  const block = text.slice(idx, idx + 240); // 継続行を含む同一コマンドの範囲
  assert.ok(/--version\s/.test(block), 'ESO の helm install に --version（版 pin）が無い＝latest 追従');
  // 既定 pin（固定版）が存在すること（ESO_CHART_VERSION の既定値 or 直書きの固定版）。
  assert.ok(
    /ESO_CHART_VERSION="\$\{ESO_CHART_VERSION:-\d+\.\d+\.\d+\}"/.test(text) ||
      /--version\s+"?\d+\.\d+\.\d+/.test(block),
    'ESO chart の固定版（ESO_CHART_VERSION 既定 or 直書き）が無い',
  );
});

// IADR-0103 (#354): ARGOCD=1 は argocd ns へ keycloak の ExternalName エイリアスを apply する。
// 無いと DNS がノードへフォールスルーし手順A の hosts(127.0.0.1 keycloak) を拾って argocd-server が
// 自分自身へ discovery を投げ 404 になる（OIDC ログイン不能）。
ok('ARGOCD=1: argocd ns の keycloak ExternalName エイリアスを apply', () => {
  const res = runUp({ ARGOCD: '1' });
  assert.ok(
    anyLineHas(res.lines, 'deploy/local/aliases/argocd-externalnames.yaml'),
    'argocd 用 keycloak エイリアスが apply されない',
  );
  // 既定（ARGOCD 未設定）では出さない（opt-in・byte 等価）。
  assert.ok(
    !anyLineHas(DEFAULT.lines, 'argocd-externalnames.yaml'),
    'ARGOCD 未設定なのに argocd エイリアスを apply した',
  );
  // マニフェスト実体も検査（ns=argocd / ExternalName / infra FQDN）。
  const y = fs.readFileSync(path.join(REPO_ROOT, 'deploy/local/aliases/argocd-externalnames.yaml'), 'utf8');
  assert.ok(/namespace:\s*argocd/.test(y), 'エイリアスの namespace が argocd でない');
  assert.ok(/type:\s*ExternalName/.test(y), 'ExternalName ではない');
  assert.ok(/externalName:\s*keycloak\.platform-infra\.svc\.cluster\.local/.test(y), 'infra の FQDN を指していない');
});

// IADR-0103 (#354): env の secretKeyRef は Pod 起動時に一度だけ解決され、その後の Secret 更新は既存 Pod へ
// 反映されない。ESO 供給後に **ESO 管理 Secret を env 参照する全 Deployment** を rollout し直して env を
// 作り直す（MinIO の unauthorized_client / LlmGateway の旧鍵保持 等の実障害対策）。
ok('ESO=1: 供給後に ESO 管理 secret を参照する Deployment を網羅的に rollout restart する', () => {
  const res = runUp({ VAULT: '1', ESO: '1' });
  // minio=minio-credentials/minio-oidc, llmgateway-service=llm-provider-credentials,
  // wiki-service=wikijs-sync, wiki-js=wikijs-db。1 つでも漏れると当該ツールだけ旧値のまま残る。
  for (const d of ['minio', 'llmgateway-service', 'wiki-service', 'wiki-js']) {
    assert.ok(
      res.lines.some((l) => l.includes('rollout restart') && l.includes(`deploy/${d}`)),
      `ESO=1 なのに ${d} の rollout restart が無い`,
    );
  }
  // 対象外: postgres/rabbitmq/keycloak-admin は creationPolicy: Merge で seed と同一値のため env が変化せず、
  // 再起動は DB/broker を無用に落とすだけ（IADR-0099）。誤って対象へ加える回帰を止める。
  for (const d of ['postgres', 'rabbitmq', 'keycloak']) {
    assert.ok(
      !res.lines.some((l) => l.includes('rollout restart') && l.includes(`deploy/${d}`)),
      `Merge 供給の ${d} を rollout してはいけない（DB/broker の無用な再起動）`,
    );
  }
  // ゲート連動: OBSERVABILITY/HEADLAMP 無効なら grafana/headlamp は rollout しない。
  assert.ok(
    !res.lines.some((l) => l.includes('rollout restart') && l.includes('deploy/grafana')),
    'OBSERVABILITY 無効なのに grafana を rollout した',
  );
  // 既定（ESO 未設定）では rollout を出さない（byte 等価）。
  assert.ok(
    !DEFAULT.lines.some((l) => l.includes('rollout restart') && l.includes('deploy/minio')),
    'ESO 未設定なのに minio を rollout した',
  );
});

// IADR-0103 (#354): rollout の前に SecretSynced（ExternalSecret の condition=Ready）を待つ。待たずに restart
// すると新 Pod もまだ供給前の Secret を掴んで同じ状態で固定され、rollout が無駄打ちになる。
ok('ESO=1: rollout の前に ExternalSecret の SecretSynced を待つ', () => {
  const res = runUp({ VAULT: '1', ESO: '1' });
  const isWait = (l) => l.includes('wait --for=condition=Ready') && l.includes('externalsecret/');
  const isRollout = (l) => l.includes('rollout restart') && l.includes('deploy/');
  for (const es of ['llm-provider-credentials', 'minio-credentials', 'minio-oidc', 'wikijs-db', 'wikijs-sync']) {
    assert.ok(
      res.lines.some((l) => isWait(l) && l.includes(`externalsecret/${es}`)),
      `${es} の SecretSynced 待ちが無い`,
    );
  }
  // 順序: 最後の wait は最初の rollout より前（待ってから作り直す）。
  const lastWait = res.lines.findLastIndex(isWait);
  const firstRollout = res.lines.findIndex(isRollout);
  assert.ok(lastWait >= 0 && firstRollout >= 0, 'wait / rollout のどちらかが出ていない');
  assert.ok(
    lastWait < firstRollout,
    'SecretSynced を待つ前に rollout している（新 Pod も供給前の Secret を掴む）',
  );
  // 既定（ESO 未設定）では wait を出さない（byte 等価）。
  assert.ok(!DEFAULT.lines.some(isWait), 'ESO 未設定なのに externalsecret を wait した');
});

ok('ESO=1 + OBSERVABILITY/HEADLAMP: grafana/headlamp も同期待ち＋rollout する', () => {
  const res = runUp({ VAULT: '1', ESO: '1', OBSERVABILITY: '1', HEADLAMP: '1' });
  for (const d of ['grafana', 'headlamp']) {
    assert.ok(
      res.lines.some((l) => l.includes('rollout restart') && l.includes(`deploy/${d}`)),
      `${d} の rollout restart が無い`,
    );
    assert.ok(
      res.lines.some(
        (l) => l.includes('wait --for=condition=Ready') && l.includes(`externalsecret/${d}-oidc`),
      ),
      `${d}-oidc の SecretSynced 待ちが無い`,
    );
  }
});

// IADR-0103 (#354): Vault OIDC は listing_visibility=unauth が無いと UI のログイン画面に OIDC が現れない
// （未認証の sys/internal/ui/mounts が auth:{} を返す）。bootstrap に含まれることを固定する。
ok('vault oidc bootstrap: listing-visibility=unauth を設定する', () => {
  const sh = fs.readFileSync(path.join(REPO_ROOT, 'deploy/local/vault/oidc/bootstrap.sh'), 'utf8');
  assert.ok(/auth tune[^\n]*-listing-visibility=unauth/.test(sh), 'listing-visibility=unauth の tune が無い');
  assert.ok(/oidc\//.test(sh), 'tune 対象が oidc/ でない');
});

// IADR-0103 (#354): realm 側の恒久化（admin ユーザー・ツール別 claim 設計）を固定する。
ok('realm.json: admin ユーザーとツール別 claim 設計が恒久化されている', () => {
  const realm = JSON.parse(
    fs.readFileSync(path.join(REPO_ROOT, 'deploy/keycloak/microservices-platform-realm.json'), 'utf8'),
  );
  const admin = (realm.users || []).find((u) => u.username === 'admin');
  assert.ok(admin, 'realm に admin ユーザーが無い');
  for (const r of ['platform-admin', 'platform-operator', 'wiki-editor', 'Administrators']) {
    assert.ok((admin.realmRoles || []).includes(r), `admin に realm ロール ${r} が無い`);
  }
  assert.ok(
    ((admin.clientRoles || {}).minio || []).includes('consoleAdmin'),
    'admin に minio client ロール consoleAdmin が無い',
  );
  // IADR-0103 (#354, claude-review 🟡): policy claim を単一値に保つのは「admin に minio client ロールを 1 つだけ
  // 付与する」運用制約に依存する（mapper は multivalued=true で複数付与時に多値配列を返す）。逸脱すると対策した
  // はずの callback 500 が再発するため、要素数 1 を機械検知して運用逸脱をブロックする。
  assert.strictEqual(
    ((admin.clientRoles || {}).minio || []).length,
    1,
    'admin の minio client ロールは 1 つだけ（複数付与で policy claim が多値化し callback 500 が再発する）',
  );
  // MinIO の policy claim は client ロール由来（多値だと MinIO がポリシー解決に失敗し 500）。
  const minio = realm.clients.find((c) => c.clientId === 'minio');
  const mm = (minio.protocolMappers || []).find((m) => m.config['claim.name'] === 'policy');
  assert.ok(mm, 'minio に policy claim の mapper が無い');
  assert.strictEqual(mm.protocolMapper, 'oidc-usermodel-client-role-mapper', 'minio の policy mapper が client ロール由来でない');
  assert.strictEqual(mm.config['usermodel.clientRoleMapping.clientId'], 'minio', 'clientRoleMapping が minio でない');
  assert.ok(
    !(minio.protocolMappers || []).some((m) => m.protocolMapper === 'oidc-usermodel-realm-role-mapper'),
    'minio に realm ロール mapper が残っている（policy claim が多値化して 500 になる）',
  );
  // Wiki.js / Headlamp は groups claim（Wiki.js は Administrators と名前一致でマップ）。
  for (const cid of ['wiki-js', 'headlamp']) {
    const c = realm.clients.find((x) => x.clientId === cid);
    assert.ok(
      (c.protocolMappers || []).some((m) => m.config['claim.name'] === 'groups'),
      `${cid} に groups claim の mapper が無い`,
    );
  }
  // Wiki.js のグループ名に一致させる realm ロール。
  assert.ok(
    (realm.roles.realm || []).some((r) => r.name === 'Administrators'),
    'realm ロール Administrators が無い',
  );
  assert.ok(
    ((realm.roles.client || {}).minio || []).some((r) => r.name === 'consoleAdmin'),
    'client ロール minio:consoleAdmin が無い',
  );
});

// --- IADR-0108 (#398): headlamp overlay の token ログイン用 SA と閲覧専用 RBAC ------------------
// `HEADLAMP=1` だけで token ログインが成立する（＝手動 kubectl create が要らない）ことは、overlay が
// `headlamp-viewer` の SA/RBAC を収録し続けることに依存する。ここを外すと README の手順が静かに壊れるため、
// 収録と「閲覧専用（get/list/watch のみ）」を静的検査で固定する。YAML パーサは持ち込まない（外部依存ゼロ）。
const HEADLAMP_DIR = path.join(REPO_ROOT, 'deploy', 'local', 'headlamp');
const VIEWER_YAML = fs.readFileSync(path.join(HEADLAMP_DIR, 'headlamp-viewer-rbac.yaml'), 'utf8');

ok('#398: kustomization が headlamp-viewer-rbac.yaml を resources に含む', () => {
  const kust = fs.readFileSync(path.join(HEADLAMP_DIR, 'kustomization.yaml'), 'utf8');
  assert.ok(
    /^\s*-\s*headlamp-viewer-rbac\.yaml\s*$/m.test(kust),
    'headlamp overlay の resources に headlamp-viewer-rbac.yaml が無い（HEADLAMP=1 で SA が作られない）',
  );
});

ok('#398: SA headlamp-viewer が platform-infra に定義されている', () => {
  assert.ok(
    /kind:\s*ServiceAccount[\s\S]*?name:\s*headlamp-viewer[\s\S]*?namespace:\s*platform-infra/.test(VIEWER_YAML),
    'ServiceAccount headlamp-viewer（platform-infra）が定義されていない',
  );
});

ok('#398: 組み込み view と cluster-read の 2 本を headlamp-viewer へ bind している', () => {
  for (const role of ['view', 'headlamp-viewer-cluster-read']) {
    assert.ok(
      new RegExp(`roleRef:[\\s\\S]*?name:\\s*${role}\\s*\\n`).test(VIEWER_YAML),
      `ClusterRole ${role} への bind が無い`,
    );
  }
  const subjects = VIEWER_YAML.match(/kind:\s*ServiceAccount\s*\n\s*name:\s*headlamp-viewer/g) || [];
  assert.strictEqual(subjects.length, 2, `bind の subject 数が 2 でない（実際: ${subjects.length}）`);
});

ok('#398: 閲覧専用＝verbs は get/list/watch のみ・cluster-admin を bind しない', () => {
  const allowed = new Set(['get', 'list', 'watch']);
  const verbLines = VIEWER_YAML.match(/^\s*verbs:\s*\[[^\]]*\]/gm) || [];
  assert.ok(verbLines.length > 0, 'verbs 定義が読み取れない（書式変更なら本テストも更新すること）');
  for (const line of verbLines) {
    for (const raw of line.slice(line.indexOf('[') + 1, line.lastIndexOf(']')).split(',')) {
      const verb = raw.trim().replace(/^["']|["']$/g, '');
      if (!verb) continue;
      assert.ok(allowed.has(verb), `閲覧用途外の verb "${verb}" が含まれる（最小権限を維持すること）`);
    }
  }
  assert.ok(
    !/cluster-admin/.test(VIEWER_YAML),
    'headlamp-viewer に cluster-admin が bind されている（IADR-0108 決定2 に反する）',
  );
});

ok('#398: Headlamp Pod の SA（headlamp）には権限を bind しない fail-safe が維持されている', () => {
  const podYaml = fs.readFileSync(path.join(HEADLAMP_DIR, 'headlamp.yaml'), 'utf8');
  // subjects: が 1 つも無ければ「bind されていない」＝合格。存在する場合のみ中身を検査する
  // （claude-review 🟡: indexOf の -1 を slice に渡すと末尾 1 文字だけが対象になり、
  //   fail-safe が壊れてもアサートが常に成功する静かな縮退になるため、位置で分岐する）。
  const subjectsAt = podYaml.indexOf('subjects:');
  assert.ok(
    subjectsAt === -1 ||
      !/kind:\s*ServiceAccount\s*\n\s*name:\s*headlamp\s*\n/.test(podYaml.slice(subjectsAt)),
    'Pod の SA headlamp が RoleBinding の subject になっている（IADR-0080 の fail-safe が壊れる）',
  );
  assert.ok(
    !/kind:\s*ServiceAccount\s*\n\s*name:\s*headlamp\s*\n/.test(VIEWER_YAML),
    'headlamp-viewer-rbac.yaml が Pod の SA headlamp を subject にしている',
  );
});

// --- IADR-0206 (#779): edge TLS overlay の静的検査 ------------------------------
//
// CI には `helm template` / `helm lint` / `kustomize build` / `kubeconform` を走らせるジョブが
// **1 件も無い**（ci.yml の 20 ジョブを実測）。したがって新規 TLS マニフェストの整合は、
// このファイルの既存の型（マニフェストを読んで正規表現で固定する・外部依存ゼロ）で担保する。
// **本来の形（実際に build / lint するジョブ）は #783 が扱い、そのとき二重に持たない判断を明示する**
// （IADR-0141「参照点を 1 つに畳む」）。

const EDGE_DIR = path.join(REPO_ROOT, 'deploy', 'local', 'edge');
const TLS_DIR = path.join(EDGE_DIR, 'tls');
const TLS_KUST = fs.readFileSync(path.join(TLS_DIR, 'kustomization.yaml'), 'utf8');
const ISSUERS_YAML = fs.readFileSync(path.join(TLS_DIR, 'cert-manager-issuers.yaml'), 'utf8');
const EDGE_CERT_YAML = fs.readFileSync(path.join(TLS_DIR, 'edge-certificate.yaml'), 'utf8');
const FRONTEND_ING_YAML = fs.readFileSync(path.join(EDGE_DIR, 'platform-frontend-ingress.yaml'), 'utf8');
const EDGE_KUST = fs.readFileSync(path.join(EDGE_DIR, 'kustomization.yaml'), 'utf8');

// `key: [a, b]` のフロー形と `- a` のブロック形の両方から値を拾う簡易パーサ。
// 外部依存を入れない（このファイルの原則）ため YAML ライブラリは使わない。
function listValues(yaml, key) {
  const unquote = (s) => s.trim().replace(/^["']|["']$/g, '');
  // `key: [a, b]`（フロー形）。`- key: [...]` のようにシーケンス項目の中にある場合も拾う。
  const flow = new RegExp(`^\\s*(?:-\\s*)?${key}:\\s*\\[([^\\]]*)\\]`, 'm').exec(yaml);
  if (flow) return flow[1].split(',').map(unquote).filter(Boolean);
  // ブロック形。`  key:` と `  - key:` の両方を受ける（後者は spec.tls[].hosts の形）。
  const block = new RegExp(`^\\s*(?:-\\s*)?${key}:[^\\S\\n]*$`, 'm').exec(yaml);
  if (!block) return [];
  const out = [];
  for (const line of yaml.slice(block.index + block[0].length).split('\n')) {
    if (line.trim() === '') {
      // 先頭の改行由来の空要素は読み飛ばす。値が始まった後の空行は終端とみなす。
      if (out.length === 0) continue;
      break;
    }
    const m = /^\s*-\s*(.+?)\s*$/.exec(line);
    if (!m) break;
    out.push(unquote(m[1]));
  }
  return out;
}

ok('#779: tls overlay は親 edge kustomization に含めない（CRD 未導入で overlay 全体を落とさない）', () => {
  assert.ok(
    !/^\s*-\s*tls\/?\s*$/m.test(EDGE_KUST),
    '親 kustomization が tls/ を含んでいる（cert-manager 未導入の環境で edge overlay 全体が落ちる）',
  );
  for (const f of ['cert-manager-issuers.yaml', 'edge-certificate.yaml']) {
    assert.ok(
      new RegExp(`^\\s*-\\s*${f.replace('.', '\\.')}\\s*$`, 'm').test(TLS_KUST),
      `tls/kustomization.yaml の resources に ${f} が無い`,
    );
  }
});

// YAML を `---` で分割し、1 ドキュメントずつ見る。**ドキュメント境界を跨ぐ正規表現を書かないため。**
// 跨がせると `kind: ClusterIssuer` が selfsigned 側にマッチしたまま別ドキュメントの値を拾い、
// 種別や参照の取り違えを通してしまう（クロス監査 V2 の実測）。
function yamlDocs(yaml) {
  return yaml
    .split(/^---\s*$/m)
    .map((d) => d.trim())
    .filter(Boolean);
}
const field = (doc, key) => {
  const m = new RegExp(`^\\s*${key}:\\s*(\\S+)\\s*$`, 'm').exec(doc);
  return m ? m[1] : null;
};
// `issuerRef:` ブロック直下の name / kind / group を取る（他所の name と混ざらないように範囲を切る）。
function issuerRef(doc) {
  const m = /^(\s*)issuerRef:\s*$/m.exec(doc);
  if (!m) return null;
  const block = doc.slice(m.index + m[0].length).split('\n').slice(0, 5).join('\n');
  return {
    name: (/^\s*name:\s*(\S+)/m.exec(block) || [])[1] ?? null,
    kind: (/^\s*kind:\s*(\S+)/m.exec(block) || [])[1] ?? null,
    group: (/^\s*group:\s*(\S+)/m.exec(block) || [])[1] ?? null,
  };
}
const docsOf = (yaml, kind) => yamlDocs(yaml).filter((d) => field(d, 'kind') === kind);

ok('#779: CA は selfsigned → CA の 2 段（1 段では検証に使える CA が残らない）', () => {
  const issuers = docsOf(ISSUERS_YAML, 'ClusterIssuer');
  const certs = docsOf(ISSUERS_YAML, 'Certificate');
  assert.strictEqual(issuers.length, 2, `ClusterIssuer は 2 つ（selfSigned と ca）であるべき: ${issuers.length}`);
  assert.strictEqual(certs.length, 1, `本ファイルの Certificate はルート CA の 1 つだけ: ${certs.length}`);

  const selfSigned = issuers.find((d) => /^\s*selfSigned:\s*\{\}\s*$/m.test(d));
  const caIssuer = issuers.find((d) => /^\s*ca:\s*$/m.test(d));
  assert.ok(selfSigned, 'selfSigned ClusterIssuer が無い（1 段目）');
  assert.ok(caIssuer, 'ca ClusterIssuer が無い（2 段目。これが無いと検証に使える CA が残らない）');

  const rootCa = certs[0];
  assert.ok(/^\s*isCA:\s*true\s*$/m.test(rootCa), 'ルート CA の Certificate に isCA: true が無い');

  // ★ 結線 1: ルート CA は selfSigned Issuer が発行する。ここが CA Issuer を向くと 2 段が崩れる。
  const rootRef = issuerRef(rootCa);
  assert.ok(rootRef, 'ルート CA の Certificate に issuerRef が無い');
  assert.strictEqual(rootRef.name, field(selfSigned, 'name'), 'ルート CA の issuerRef が selfSigned Issuer を指していない');
  assert.strictEqual(rootRef.kind, 'ClusterIssuer', 'ルート CA の issuerRef.kind が ClusterIssuer でない');
  // 葉と同じく group まで見る（監査 R9: 葉は name/kind/group を見るのにルートは name/kind だけで非対称だった）。
  assert.strictEqual(rootRef.group, 'cert-manager.io', 'ルート CA の issuerRef.group が cert-manager.io でない');

  // ★ 結線 2: CA Issuer はルート CA が作る Secret を読む。名前がずれると Issuer が Ready にならない。
  const caSecret = (/^\s*ca:\s*\n\s*secretName:\s*(\S+)/m.exec(caIssuer) || [])[1] ?? null;
  assert.strictEqual(
    caSecret,
    field(rootCa, 'secretName'),
    `CA Issuer の ca.secretName(${caSecret}) がルート CA の secretName(${field(rootCa, 'secretName')}) と食い違う`,
  );

  // CA ClusterIssuer は cluster-scoped で、参照先 Secret を --cluster-resource-namespace（既定 cert-manager）
  // から読む。ルート CA の Certificate が別 namespace に居ると CA Issuer が Ready にならない。
  assert.strictEqual(field(rootCa, 'namespace'), 'cert-manager', 'ルート CA の Certificate が cert-manager namespace に無い');

  // apiVersion の取り違え（v1alpha2 等）は apply の時点で落ちるが、CI に kustomize build が無いので静的に見る。
  for (const d of [...issuers, ...certs]) {
    assert.strictEqual(field(d, 'apiVersion'), 'cert-manager.io/v1', `apiVersion が cert-manager.io/v1 でない: ${field(d, 'kind')}`);
  }
});

ok('#779: 葉証明書は CA Issuer（selfSigned ではない）が発行する', () => {
  const leaf = docsOf(EDGE_CERT_YAML, 'Certificate')[0];
  assert.ok(leaf, '葉の Certificate が無い');
  assert.strictEqual(field(leaf, 'apiVersion'), 'cert-manager.io/v1', '葉の apiVersion が cert-manager.io/v1 でない');

  const caIssuer = docsOf(ISSUERS_YAML, 'ClusterIssuer').find((d) => /^\s*ca:\s*$/m.test(d));
  const ref = issuerRef(leaf);
  assert.ok(ref, '葉の Certificate に issuerRef が無い');
  // ★ 結線 3: ここが selfSigned Issuer を向くと **2 段が 1 段へ崩れ、ルート CA が Secret に残らない**。
  // IADR-0206 の存在理由（oidc-ca-file と backend の信頼ストアへ渡せる CA）がそのまま消える。
  assert.strictEqual(ref.name, field(caIssuer, 'name'), '葉の issuerRef が CA Issuer を指していない（2 段が崩れる）');
  assert.strictEqual(ref.kind, 'ClusterIssuer', '葉の issuerRef.kind が ClusterIssuer でない');
  assert.strictEqual(ref.group, 'cert-manager.io', '葉の issuerRef.group が cert-manager.io でない');
});

ok('#779: 葉証明書は Ingress と同じ namespace に居る（spec.tls は同 ns の Secret しか参照できない）', () => {
  assert.ok(
    /kind:\s*Certificate[\s\S]*?name:\s*edge-tls\s*\n\s*namespace:\s*microservices-platform/.test(EDGE_CERT_YAML),
    'edge-tls の Certificate が microservices-platform namespace に無い',
  );
  assert.ok(
    /kind:\s*Ingress[\s\S]*?namespace:\s*microservices-platform/.test(FRONTEND_ING_YAML),
    'platform-frontend-edge が microservices-platform namespace に無い',
  );
});

ok('#779: Ingress の spec.tls.secretName が Certificate の secretName と一致する', () => {
  const certSecret = /^\s*secretName:\s*(\S+)/m.exec(EDGE_CERT_YAML);
  const ingSecret = /^\s*secretName:\s*(\S+)/m.exec(FRONTEND_ING_YAML);
  assert.ok(certSecret, 'Certificate に secretName が無い');
  assert.ok(ingSecret, 'Ingress の spec.tls に secretName が無い');
  assert.strictEqual(
    ingSecret[1],
    certSecret[1],
    `Ingress(${ingSecret[1]}) と Certificate(${certSecret[1]}) の secretName が食い違う（TLS が張られない）`,
  );
  // 計画 ADR-0023 の設計要件: secretName を安定させ、CA 差し替えを issuerRef の変更だけに閉じる。
  assert.strictEqual(certSecret[1], 'edge-tls', 'secretName は ADR-0023 が例示する edge-tls に固定する');
});

ok('#779: Certificate の dnsNames が Ingress の spec.tls.hosts を覆う', () => {
  const dnsNames = listValues(EDGE_CERT_YAML, 'dnsNames');
  const hosts = listValues(FRONTEND_ING_YAML, 'hosts');
  assert.ok(dnsNames.length > 0, 'Certificate に dnsNames が無い');
  assert.ok(hosts.length > 0, 'Ingress の spec.tls に hosts が無い');
  for (const h of hosts) {
    assert.ok(dnsNames.includes(h), `spec.tls.hosts の "${h}" が Certificate の dnsNames に無い（SNI 不一致）`);
  }
  // #780 で keycloak.localhost がエッジに出るときに証明書を作り直さずに済ませる。
  assert.ok(dnsNames.includes('*.localhost'), 'dnsNames に *.localhost が無い（#780 で作り直しになる）');
});

// --- IADR-0220 (#841): admin(50000) の HTTPS 化 -------------------------------
//
// **この 2 件は #779 の期待値を反転したものである。** 反転の根拠は実装側の都合ではなく計画側にある ——
// 計画 NFR-11（全経路の HTTPS 化・平文 HTTP を残さない）の適用範囲が利用者裁定 2026-08-16
// （裁定依頼 planning#383）で **環境を問わない**と確定し、経路B（LOCALEDGE=1）も適用内になった。
// 証明書の発行方式は計画 ADR-0047（*.localhost では selfsigned CA を許容）が定める。
// 反転前の 2 件は「admin:50000 の Ingress に spec.tls を足さない」「http→https の恒久リダイレクトを足さない」で、
// **平文であることを固定していた**。

const TRAEFIK_YAML = fs.readFileSync(path.join(EDGE_DIR, 'traefik-entrypoint.yaml'), 'utf8');
const ADMIN_ING_FILES = ['admin-ingress-infra.yaml', 'admin-ingress-minio.yaml', 'admin-ingress-wiki.yaml', 'argocd-ingress.yaml'];

ok('#841: admin:50000 は TLS 終端で、そこに載る Ingress は spec.tls(edge-tls) を持つ', () => {
  // entrypoint 側に TLS が無いまま Ingress へ spec.tls を足すと「TLS になったつもり」になる。
  // 逆に entrypoint だけ TLS にして Ingress に証明書を指さないと Traefik 既定の自己署名へ落ちる
  // （Secret 化されず再起動ごとに変わる＝IADR-0206 代替案の表）。**両側を同じ試験で見る。**
  assert.ok(
    /--entryPoints\.admin\.http\.tls=true/.test(TRAEFIK_YAML),
    'traefik-entrypoint.yaml に --entryPoints.admin.http.tls=true が無い（admin:50000 が平文のまま）',
  );
  const dnsNames = listValues(EDGE_CERT_YAML, 'dnsNames');
  let routers = 0;
  for (const f of ADMIN_ING_FILES) {
    const yaml = fs.readFileSync(path.join(EDGE_DIR, f), 'utf8');
    const ingresses = docsOf(yaml, 'Ingress');
    assert.ok(ingresses.length > 0, `${f} に Ingress が無い（前提が変わった）`);
    // ★ **ドキュメント単位で見る。** ファイル単位で `tls:` の有無だけを見ると、同じファイルの
    // 別 Ingress が持つ tls にマッチして**1 件だけ平文へ戻しても検出できない**（変異試験で実測）。
    for (const doc of ingresses) {
      const name = field(doc, 'name');
      routers += 1;
      assert.ok(/router\.entrypoints:\s*admin/.test(doc), `${f} の ${name} が admin entrypoint に載っていない（前提が変わった）`);
      assert.ok(/^\s*tls:\s*$/m.test(doc), `${f} の ${name} に spec.tls が無い（admin:50000 は TLS 終端になった・NFR-11）`);
      // secretName は IADR-0206 が安定させた名前をそのまま使う（計画 ADR-0047 決定 2「名前の安定」）。
      const secret = (/^\s*secretName:\s*(\S+)/m.exec(doc) || [])[1];
      assert.strictEqual(secret, 'edge-tls', `${f} の ${name} の spec.tls.secretName が edge-tls でない（新しい名前を作らない）`);
      // hosts が dnsNames に含まれないと SNI が一致せず、TLS が張られたつもりで警告だけが増える。
      const hosts = listValues(doc, 'hosts');
      assert.ok(hosts.length > 0, `${f} の ${name} の spec.tls に hosts が無い`);
      for (const h of hosts) {
        assert.ok(dnsNames.includes(h), `${f} の ${name} の spec.tls.hosts "${h}" が Certificate の dnsNames に無い（SNI 不一致）`);
      }
    }
  }
  // 管理ツール 7 件（grafana / headlamp / vault / qdrant / minio / wiki / argocd）。
  // 数が変わったら、増えたルータが TLS から漏れていないかを人が見る。
  assert.strictEqual(routers, 7, `admin entrypoint のルータ数が 7 でない（実測 ${routers}）`);
});

ok('#841: 管理ツールの namespace ごとに葉証明書が在る（spec.tls は同 ns の Secret しか参照できない）', () => {
  // grafana/headlamp/vault/qdrant は platform-infra、minio/wiki は microservices-platform、argocd は argocd。
  // どれか 1 つでも欠けると、その ns の Ingress だけ静かに TLS が張られない。
  const ARGOCD_CERT_YAML = fs.readFileSync(path.join(TLS_DIR, 'argocd-certificate.yaml'), 'utf8');
  const nsOf = (yaml) => docsOf(yaml, 'Certificate').map((d) => field(d, 'namespace'));
  const namespaces = [...nsOf(EDGE_CERT_YAML), ...nsOf(ARGOCD_CERT_YAML)];
  for (const ns of ['microservices-platform', 'platform-infra', 'argocd']) {
    assert.ok(namespaces.includes(ns), `${ns} namespace の葉証明書が無い（その ns の Ingress で TLS が張られない）`);
  }
  // argocd ns は ARGOCD=1 の別 opt-in でのみ作られる。kustomization に含めると ns 不在で overlay 全体が落ちる。
  assert.ok(
    !/^\s*-\s*argocd-certificate\.yaml\s*$/m.test(TLS_KUST),
    'tls/kustomization.yaml が argocd-certificate.yaml を含んでいる（argocd ns 不在の環境で tls overlay が落ちる）',
  );
  const upSrc = fs.readFileSync(path.join(REPO_ROOT, 'scripts', 'k8s-local-up.sh'), 'utf8');
  assert.ok(
    /get namespace argocd[\s\S]*argocd-certificate\.yaml/.test(upSrc),
    'k8s-local-up.sh が argocd-certificate.yaml を「argocd ns 存在時のみ」apply していない（fail-safe）',
  );
});

ok('#841: web(80) は https へ恒久リダイレクトする（平文 HTTP を残さない・NFR-11）', () => {
  assert.ok(
    /router\.entrypoints:\s*web,websecure/.test(FRONTEND_ING_YAML),
    'web(80) が entrypoints から外れている（80 を落とすとリダイレクト自体が返せない）',
  );
  assert.ok(
    /--entryPoints\.web\.http\.redirections\.entryPoint\.to=websecure/.test(TRAEFIK_YAML),
    'web→websecure の恒久リダイレクトが無い（平文 HTTP が残る・NFR-11 違反）',
  );
  assert.ok(
    /--entryPoints\.web\.http\.redirections\.entryPoint\.scheme=https/.test(TRAEFIK_YAML),
    'リダイレクト先の scheme=https が無い',
  );
  assert.ok(
    /--entryPoints\.web\.http\.redirections\.entryPoint\.permanent=true/.test(TRAEFIK_YAML),
    'リダイレクトが恒久（permanent=true）でない',
  );
});

// --- IADR-0210 (#787): 永続化 overlay の静的検査 --------------------------------
//
// #779 と同じ事情である —— CI に `kustomize build` / `kubeconform` を走らせるジョブは **1 件も無い**。
// さらに本件は「マウント先が config の storage パスと一致していること」が命であり、**ずれても Pod は起動し、
// 静かに別の場所へ書いて再起動で消える**（一致していない方が壊れ方として悪質である）。
// したがって **config と overlay を両側から読んで突き合わせる**。値をハードコードして写すと、
// config を変えたときに検査が静かに嘘になる。YAML パーサは持ち込まない（このファイルの原則）。

const OBS_DIR = path.join(REPO_ROOT, 'deploy', 'local', 'observability');
const OBS_PERSIST_DIR = path.join(REPO_ROOT, 'deploy', 'local', 'observability-persistence');
const INFRA_PERSIST_DIR = path.join(REPO_ROOT, 'deploy', 'local', 'infra-persistence');
const readAt = (...p) => fs.readFileSync(path.join(...p), 'utf8');

const OBS_PERSIST_KUST = readAt(OBS_PERSIST_DIR, 'kustomization.yaml');
const OBS_PERSIST_PVCS = readAt(OBS_PERSIST_DIR, 'pvcs.yaml');
const INFRA_PERSIST_KUST = readAt(INFRA_PERSIST_DIR, 'kustomization.yaml');
const INFRA_PERSIST_PVCS = readAt(INFRA_PERSIST_DIR, 'pvcs.yaml');
const PROM_YAML = readAt(OBS_DIR, 'prometheus.yaml');
const LOKI_YAML = readAt(OBS_DIR, 'loki.yaml');
const TEMPO_YAML = readAt(OBS_DIR, 'tempo.yaml');
const COMPOSE_YAML = readAt(REPO_ROOT, 'deploy', 'docker-compose.yml');

// kustomization の `- target:` ごとにチャンクへ割る。チャンク内の最初の `name:` が対象ワークロード名
// （target は kind / name / namespace の順）。ドキュメント境界を跨いだ取り違えを避けるための切り分けである。
function overlayPatchChunks(kust) {
  const out = {};
  for (const chunk of kust.split(/^\s*-\s*target:\s*$/m).slice(1)) {
    const name = (/^\s*name:\s*(\S+)/m.exec(chunk) || [])[1];
    if (name) out[name] = chunk;
  }
  return out;
}
const OBS_PATCHES = overlayPatchChunks(OBS_PERSIST_KUST);
const INFRA_PATCHES = overlayPatchChunks(INFRA_PERSIST_KUST);
const one = (chunk, key) => (new RegExp(`^\\s*${key}:\\s*(\\S+)\\s*$`, 'm').exec(chunk) || [])[1] ?? null;

// PVC ドキュメントを名前で引く。
function pvcsByName(yaml) {
  const out = {};
  for (const doc of yamlDocs(yaml).filter((d) => field(d, 'kind') === 'PersistentVolumeClaim')) {
    const name = field(doc, 'name');
    if (name) out[name] = doc;
  }
  return out;
}
const OBS_PVCS = pvcsByName(OBS_PERSIST_PVCS);
const INFRA_PVCS = pvcsByName(INFRA_PERSIST_PVCS);

// 容量表記をバイトへ換算する。k8s（5Gi=binary / 5G=decimal）と Prometheus（4GB=decimal）の**両方**の
// 書式を受ける。ここを揃えないと「size < PVC 容量」を数として比べられない。
function toBytes(text) {
  const m = /^(\d+(?:\.\d+)?)([KMGTP])?(i)?B?$/.exec(String(text).trim());
  if (!m) return null;
  const exp = { undefined: 0, K: 1, M: 2, G: 3, T: 4, P: 5 }[m[2]];
  return Number(m[1]) * Math.pow(m[3] ? 1024 : 1000, exp);
}

// compose を「サービス名 → その定義ブロック」へ割る（2 スペース字下げのキーがサービス名）。
// top-level の `volumes:` 配下の名前付きボリュームも同じ形なので拾われるが、名前が衝突しないので害は無い。
function composeBlocks(yaml) {
  const out = {};
  let cur = null;
  for (const line of yaml.split('\n')) {
    const m = /^ {2}([A-Za-z][\w.-]*):\s*$/.exec(line);
    if (m) {
      cur = m[1];
      out[cur] = [];
      continue;
    }
    if (/^[A-Za-z]/.test(line)) {
      cur = null;
      continue;
    }
    if (cur) out[cur].push(line);
  }
  return Object.fromEntries(Object.entries(out).map(([k, v]) => [k, v.join('\n')]));
}
const COMPOSE = composeBlocks(COMPOSE_YAML);
// `- <名前付きボリューム>:<コンテナ内パス>` だけを拾う（`./x.yml:/etc/...` のバインドは先頭が `.` なので外れる）。
function composeNamedMount(serviceBlock, volumeName) {
  const m = new RegExp(`^\\s*-\\s*${volumeName}:(/\\S*?)(?::[a-z,]+)?\\s*$`, 'm').exec(serviceBlock || '');
  return m ? m[1] : null;
}

ok('#787: 永続化 overlay は base を resources に含み、PVC を同梱する', () => {
  for (const r of ['../observability', 'pvcs.yaml']) {
    assert.ok(
      new RegExp(`^\\s*-\\s*${r.replace(/[./]/g, '\\$&')}\\s*$`, 'm').test(OBS_PERSIST_KUST),
      `observability-persistence の resources に ${r} が無い`,
    );
  }
  assert.deepStrictEqual(
    Object.keys(OBS_PVCS).sort(),
    ['grafana-data', 'loki-data', 'prometheus-data', 'tempo-data'],
    'observability-persistence の PVC 集合が想定と違う',
  );
  assert.deepStrictEqual(
    Object.keys(OBS_PATCHES).sort(),
    ['grafana', 'loki', 'prometheus', 'tempo'],
    'observability-persistence の patch 対象が想定と違う',
  );
});

ok('#787: 全 PVC が local-path / ReadWriteOnce / 意図した容量を持つ', () => {
  const expected = {
    'prometheus-data': '5Gi',
    'loki-data': '2Gi',
    'tempo-data': '2Gi',
    'grafana-data': '1Gi',
    'qdrant-storage': '2Gi',
  };
  const all = { ...OBS_PVCS, 'qdrant-storage': INFRA_PVCS['qdrant-storage'] };
  for (const [name, size] of Object.entries(expected)) {
    const doc = all[name];
    assert.ok(doc, `PVC ${name} が無い`);
    assert.strictEqual(field(doc, 'namespace'), 'platform-infra', `${name} の namespace が platform-infra でない`);
    assert.strictEqual(
      field(doc, 'storageClassName'),
      'local-path',
      `${name} の storageClassName が local-path でない（k3s/Rancher Desktop 同梱の provisioner）`,
    );
    assert.deepStrictEqual(listValues(doc, 'accessModes'), ['ReadWriteOnce'], `${name} の accessModes が RWO でない`);
    assert.strictEqual(field(doc, 'storage'), size, `${name} の容量が ${size} でない`);
  }
});

ok('#787: qdrant の patch は postgres と同型（volumes/0 を replace・volumeMount は base のまま）', () => {
  const chunk = INFRA_PATCHES['qdrant'];
  assert.ok(chunk, 'infra-persistence に qdrant の patch が無い');
  assert.ok(/op:\s*replace/.test(chunk), 'qdrant の patch が replace でない（base の emptyDir が残る）');
  assert.ok(
    /path:\s*\/spec\/template\/spec\/volumes\/0/.test(chunk),
    'qdrant の patch が volumes/0 を指していない',
  );
  assert.strictEqual(one(chunk, 'claimName'), 'qdrant-storage', 'qdrant の claimName が qdrant-storage でない');
  // volumeMount は base（deploy/local/infra/qdrant.yaml）が持つ。overlay で二重に足さない。
  assert.ok(!/mountPath:/.test(chunk), 'qdrant の patch が volumeMount を足している（base に既にある＝二重）');
  // ★ 置換対象の volume 名が base 側と一致していること（ここがずれると volumes に別名が生えて emptyDir が残る）。
  const baseQdrant = readAt(REPO_ROOT, 'deploy', 'local', 'infra', 'qdrant.yaml');
  const baseVolName = (/^\s*volumes:\s*\n\s*-\s*name:\s*(\S+)/m.exec(baseQdrant) || [])[1];
  assert.strictEqual(one(chunk, 'name'), 'qdrant', 'patch チャンクの解釈がずれている');
  assert.ok(
    new RegExp(`value:\\s*\\n\\s*name:\\s*${baseVolName}\\b`).test(chunk),
    `qdrant の patch の volume 名が base（${baseVolName}）と一致しない`,
  );
});

// ★ 本件の中核。mountPath は「たまたま同じ文字列を書いた」では足りず、**config の実値から導かれている**
//   ことを両側から読んで突き合わせる。config を動かせばこの検査が落ちる（＝静かに嘘にならない）。
ok('#787: Loki の mountPath が config の path_prefix と一致し、storage パスを覆う', () => {
  const pathPrefix = (/^\s*path_prefix:\s*(\S+)\s*$/m.exec(LOKI_YAML) || [])[1];
  assert.ok(pathPrefix, 'loki-config の common.path_prefix が読み取れない（書式変更なら本検査も更新すること）');
  const mountPath = one(OBS_PATCHES['loki'], 'mountPath');
  assert.strictEqual(mountPath, pathPrefix, `loki の mountPath(${mountPath}) が path_prefix(${pathPrefix}) と違う`);

  // index / index_cache / chunks が本当にその配下にあること（path_prefix だけ合わせても外れていたら意味が無い）。
  const storagePaths = [
    (/^\s*active_index_directory:\s*(\S+)/m.exec(LOKI_YAML) || [])[1],
    (/^\s*cache_location:\s*(\S+)/m.exec(LOKI_YAML) || [])[1],
    (/^\s*directory:\s*(\S+)/m.exec(LOKI_YAML) || [])[1],
  ];
  assert.ok(storagePaths.every(Boolean), `loki の storage_config パスが読み取れない: ${storagePaths}`);
  for (const p of storagePaths) {
    assert.ok(p.startsWith(mountPath + '/'), `loki の storage パス ${p} が mountPath(${mountPath}) の配下に無い`);
  }
});

ok('#787: Tempo の mountPath が config の local.path / wal.path の親と一致する', () => {
  const paths = [...TEMPO_YAML.matchAll(/^\s*path:\s*(\/\S+)\s*$/gm)].map((m) => m[1]);
  assert.strictEqual(paths.length, 2, `tempo-config の storage パスは blocks と wal の 2 本のはず: ${paths}`);
  const mountPath = one(OBS_PATCHES['tempo'], 'mountPath');
  for (const p of paths) {
    assert.ok(p.startsWith(mountPath + '/'), `tempo の storage パス ${p} が mountPath(${mountPath}) の配下に無い`);
  }
  // 親がもう 1 段上（/tmp）になっていないこと＝過剰に広いマウントを弾く。
  assert.ok(
    paths.every((p) => path.posix.dirname(p) === mountPath),
    `tempo の mountPath(${mountPath}) が storage パスの直接の親でない: ${paths}`,
  );
});

// ★ マウント先を compose と k8s で揃える判断を機械が見る。どちらか一方だけを動かした瞬間に落ちる。
//    （**書き込み権限は揃えない** —— docker の named volume と local-path で前提が違うため。決定 6 の実測を参照）
ok('#787: データボリュームのマウント先が compose と k8s で一致する', () => {
  const pairs = [
    ['prometheus', 'prometheus-data', OBS_PATCHES['prometheus']],
    ['loki', 'loki-data', OBS_PATCHES['loki']],
    ['tempo', 'tempo-data', OBS_PATCHES['tempo']],
    ['grafana', 'grafana-data', OBS_PATCHES['grafana']],
  ];
  for (const [svc, vol, chunk] of pairs) {
    const composePath = composeNamedMount(COMPOSE[svc], vol);
    assert.ok(composePath, `compose の ${svc} に ${vol} のマウントが無い（前提が変わった）`);
    assert.strictEqual(
      one(chunk, 'mountPath'),
      composePath,
      `${svc}: k8s(${one(chunk, 'mountPath')}) と compose(${composePath}) でマウント先が食い違う`,
    );
    assert.strictEqual(one(chunk, 'claimName'), vol, `${svc} の claimName が ${vol} でない`);
  }
  // qdrant は base が volumeMount を持つので、base 側の実値と compose を突き合わせる。
  const baseQdrant = readAt(REPO_ROOT, 'deploy', 'local', 'infra', 'qdrant.yaml');
  assert.strictEqual(
    (/^\s*mountPath:\s*(\S+)/m.exec(baseQdrant) || [])[1],
    composeNamedMount(COMPOSE['qdrant'], 'qdrant-data'),
    'qdrant: k8s base と compose でマウント先が食い違う',
  );
});

// ★ compose の user: "0:0" を k8s へ写さない（IADR-0210 決定 6）。
//   compose の root 実行は **docker の named volume が root:root 0755 で生成される**ことへの対処であり、
//   local-path provisioner は `mkdir -m 0777` で作る（kube-system/local-path-config を実読）。
//   実測（2026-08-16・稼働中の k3s）: loki=uid 10001 が /tmp/loki(drwxrwxrwx) へ 4.7M、
//   tempo=uid 10001 が 5.0M、grafana=uid 472 が grafana.db を書き、**4 件とも再起動 0 回で Ready**。
//   ここを「compose を鏡にする」で埋めると、**k8s 側だけ不要に root へ落ちる**。
ok('#787: 可観測性 overlay は root 実行へ落とさない（local-path は 0777 で作る）', () => {
  for (const svc of ['prometheus', 'loki', 'tempo', 'grafana']) {
    assert.ok(
      !/runAsUser:\s*0\b/.test(OBS_PATCHES[svc] || ''),
      `${svc}: runAsUser: 0 が入っている。compose の user:"0:0" は docker の named volume 固有の対処で、` +
        'local-path（mkdir -m 0777）へは転用できない（IADR-0210 決定 6・実機で非 root 書き込みを実測）',
    );
  }
});

// ★ 受け入れ基準 3 の静的側。「設定した」ではなく「**壊れない形にした**」を数として見る。
ok('#787: Prometheus の保持期間が args で明示され、compose と同値である', () => {
  const promBlock = /kind:\s*Deployment[\s\S]*?args:([\s\S]*?)\n\s{10}\w/.exec(PROM_YAML);
  assert.ok(promBlock, 'prometheus Deployment の args ブロックが読み取れない');
  const k8sArgs = [...promBlock[1].matchAll(/"(--[^"]+)"/g)].map((m) => m[1]);
  const composeArgs = [...(COMPOSE['prometheus'] || '').matchAll(/"(--[^"]+)"/g)].map((m) => m[1]);
  assert.ok(composeArgs.length > 0, 'compose の prometheus.command が読み取れない');

  const retention = (args, key) => (args.find((a) => a.startsWith(`--storage.tsdb.retention.${key}=`)) || '').split('=')[1];
  for (const key of ['time', 'size']) {
    assert.ok(retention(k8sArgs, key), `k8s の prometheus args に --storage.tsdb.retention.${key} が無い`);
    assert.strictEqual(
      retention(composeArgs, key),
      retention(k8sArgs, key),
      `retention.${key} が compose(${retention(composeArgs, key)}) と k8s(${retention(k8sArgs, key)}) で食い違う` +
        '（片方だけ入れると新たなパリティ差になる。#787 受け入れ基準 5）',
    );
  }

  // size < PVC 容量。ここが逆転すると PVC 満杯 → 書き込み不能という壊れ方に戻る。
  const sizeBytes = toBytes(retention(k8sArgs, 'size'));
  const pvcBytes = toBytes(field(OBS_PVCS['prometheus-data'], 'storage'));
  assert.ok(sizeBytes && pvcBytes, `容量を数へ換算できない: size=${retention(k8sArgs, 'size')} pvc=${field(OBS_PVCS['prometheus-data'], 'storage')}`);
  assert.ok(
    sizeBytes < pvcBytes,
    `retention.size(${sizeBytes} B) が PVC 容量(${pvcBytes} B) 以上である（満杯で書き込み不能になりうる）`,
  );
});

// ★ 以下 4 件は PR #815（同じ #787 を独立に実装したもう 1 本）から畳み込んだ検査である。
//   同じ issue に 2 本の PR が並走したため、両者の検査の和を採った（IADR-0116 規約 1 へ戻す統合）。

ok('#787: PVC を掴む Deployment は Recreate（RWO と RollingUpdate は両立しない）', () => {
  for (const [label, kust, apps] of [
    ['infra-persistence', INFRA_PERSIST_KUST, ['postgres', 'keycloak', 'qdrant']],
    ['observability-persistence', OBS_PERSIST_KUST, ['prometheus', 'loki', 'tempo', 'grafana']],
  ]) {
    assert.ok(
      /path:\s*\/spec\/strategy\b/.test(kust) && /type:\s*Recreate\b/.test(kust),
      `${label}: /spec/strategy に Recreate を当てる patch が無い（新旧 Pod が同じ PVC を奪い合う）`,
    );
    // labelSelector に**全対象**が入っていること。1 件でも漏れるとその Deployment だけ RollingUpdate に残る。
    const sel = (/labelSelector:\s*["']?app in \(([^)]*)\)/.exec(kust) || [])[1];
    assert.ok(sel, `${label}: Recreate patch の labelSelector を読めない`);
    assert.deepStrictEqual(
      sel.split(',').map((s) => s.trim()).sort(),
      [...apps].sort(),
      `${label}: Recreate の対象集合が PVC を掴む Deployment 全件と一致しない`,
    );
  }
});

ok('#787: /tmp そのものはマウントしない（Loki / Tempo）', () => {
  for (const svc of ['loki', 'tempo']) {
    const mounts = [...(OBS_PATCHES[svc] || '').matchAll(/^\s*mountPath:\s*(\S+)\s*$/gm)].map((m) => m[1]);
    assert.ok(mounts.length > 0, `${svc}: mountPath が 1 つも無い`);
    assert.ok(
      !mounts.includes('/tmp'),
      `${svc}: /tmp そのものを覆っている。Go の os.TempDir() が使う一時ファイルまで PVC に載り、` +
        '/tmp のセマンティクスも壊れる（覆うのは /tmp/<svc> だけでよい）',
    );
  }
});

ok('#787: base（既定経路）は書き換えていない —— PVC はオーバーレイにしか無い', () => {
  for (const dir of [OBS_DIR, path.join(REPO_ROOT, 'deploy', 'local', 'infra')]) {
    for (const f of fs.readdirSync(dir).filter((n) => /\.ya?ml$/.test(n))) {
      assert.ok(
        !/persistentVolumeClaim:/.test(readAt(dir, f)),
        `${path.basename(dir)}/${f} に persistentVolumeClaim がある。` +
          'base に PVC を持ち込むと provisioner 不在のクラスタで既定経路が Pending になる（fail-safe が壊れる）',
      );
    }
  }
});

ok('#787: 永続化オーバーレイの claimName がすべて同じ overlay の PVC を指す', () => {
  for (const [label, kust, pvcs] of [
    ['infra-persistence', INFRA_PERSIST_KUST, INFRA_PVCS],
    ['observability-persistence', OBS_PERSIST_KUST, OBS_PVCS],
  ]) {
    const claims = [...kust.matchAll(/^\s*claimName:\s*(\S+)\s*$/gm)].map((m) => m[1]);
    assert.ok(claims.length > 0, `${label}: claimName が 1 つも無い`);
    for (const c of claims) {
      assert.ok(
        pvcs[c],
        `${label}: claimName ${c} に対応する PVC が同じ overlay に無い（apply しても Pod が Pending で止まる）`,
      );
    }
  }
});

// --- IADR-0227 (#780): Keycloak のエッジ公開と、エッジ host の pod 側名前解決 --------------------
//
// #779 と同じ事情である —— CI に `kustomize build` を走らせるジョブは 1 件も無い。
// さらに本件は「issuer と同じ host 名で、pod からも到達できること」が命であり、
// **ずれても Pod は起動し、静かに別の場所へ discovery を投げる**（IADR-0103 が argocd-server で実測した形）。


const KEYCLOAK_INGRESS = readAt(EDGE_DIR, 'keycloak-ingress.yaml');
const COREDNS_CUSTOM = readAt(REPO_ROOT, 'deploy', 'local', 'aliases', 'coredns-edge-hosts.yaml');

ok('#780: Keycloak の Ingress が edge overlay に含まれている', () => {
  assert.ok(
    /^\s*-\s*keycloak-ingress\.yaml\s*$/m.test(EDGE_KUST),
    'edge の kustomization に keycloak-ingress.yaml が無い（ファイルだけ在って適用されない）',
  );
});

ok('#780: Keycloak は websecure(443) の keycloak.localhost へ出る（admin:50000 ではない）', () => {
  const doc = docsOf(KEYCLOAK_INGRESS, 'Ingress')[0];
  assert.ok(doc, 'Ingress ドキュメントが無い');
  assert.strictEqual(field(doc, 'namespace'), 'platform-infra', 'Keycloak の実体と同じ namespace でない');
  assert.ok(/host:\s*keycloak\.localhost\s*$/m.test(doc), 'host が keycloak.localhost でない');
  // ★ admin(50000) に置くと redirect URI が全クライアントで :50000 付きになり、
  //   IADR-0220 の改定と 7 クライアントの追記に波及する（#780 が意図的にスコープ外にした）。
  assert.ok(
    /router\.entrypoints:\s*websecure\s*$/m.test(doc),
    'entrypoint が websecure でない（admin:50000 に置くと 7 クライアントの redirect に波及する）',
  );
  const ep = (/router\.entrypoints:\s*(\S+)/.exec(doc) || [])[1] ?? '';
  assert.ok(ep !== '', 'entrypoints の注釈を読めない（検査が素通りする形になっている）');
  assert.ok(!ep.includes('admin'), `entrypoints に admin が混ざっている: ${ep}`);
});

ok('#780: Keycloak の Ingress は同 namespace の edge-tls を使い、後段は keycloak:8080', () => {
  const doc = docsOf(KEYCLOAK_INGRESS, 'Ingress')[0];
  // spec.tls.secretName は同じ namespace の Secret しか参照できない。platform-infra 側の
  // edge-tls は IADR-0220 (#841) が edge-certificate.yaml で宣言済みである（本 PR は作らない）。
  assert.ok(/secretName:\s*edge-tls\s*$/m.test(doc), 'secretName が edge-tls でない');
  const infraCert = docsOf(EDGE_CERT_YAML, 'Certificate')
    .find((d) => field(d, 'namespace') === 'platform-infra');
  assert.ok(infraCert, 'platform-infra の edge-tls 証明書が tls overlay に無い（Ingress が TLS を張れない）');
  assert.ok(/name:\s*keycloak\s*$/m.test(doc) && /number:\s*8080\s*$/m.test(doc),
    '後段が keycloak:8080 でない');
});

ok('#780: coredns-custom は k3s の import 先へ置き、coredns ConfigMap 自体は触らない', () => {
  const cm = docsOf(COREDNS_CUSTOM, 'ConfigMap')[0];
  assert.ok(cm, 'ConfigMap ドキュメントが無い');
  assert.strictEqual(field(cm, 'name'), 'coredns-custom', 'ConfigMap 名が coredns-custom でない');
  assert.strictEqual(field(cm, 'namespace'), 'kube-system', 'namespace が kube-system でない');
  // k3s の Corefile は末尾で *.server / *.override を import する。キー名がその glob に合わないと
  // **置いても一切読まれない**（何も起きないので気づけない）。
  const keys = [...COREDNS_CUSTOM.matchAll(/^\s{2}([\w.-]+):\s*\|/gm)].map((m) => m[1]);
  assert.ok(keys.length > 0, 'data のキーが無い');
  for (const k of keys) {
    assert.ok(/\.(server|override)$/.test(k),
      `data のキー ${k} が .server / .override で終わらない（k3s の import glob に合わず読まれない）`);
  }
});

ok('#780: エッジ host の解決先は Traefik の正準名で、ClusterIP をハードコードしない', () => {
  assert.ok(
    COREDNS_CUSTOM.includes('traefik.kube-system.svc.cluster.local'),
    '解決先が Traefik の正準名でない',
  );
  // ★ ClusterIP を焼き込むと Service 再作成で静かに壊れる（引けるが誰も居ないアドレスを返す）。
  assert.ok(
    !/\b10\.\d{1,3}\.\d{1,3}\.\d{1,3}\b/.test(COREDNS_CUSTOM),
    'ClusterIP らしきアドレスがハードコードされている',
  );
});

ok('LOCALEDGE=1: coredns-custom を当ててから rollout restart する（import は reload では拾われない）', () => {
  const { lines } = runUp({ LOCALEDGE: '1' });
  const idx = (needle) => lines.findIndex((l) => l.includes(needle));
  const applyEdge = idx('apply -k deploy/local/edge');
  const applyCoredns = idx('coredns-edge-hosts.yaml');
  const restart = idx('rollout restart deploy/coredns');
  assert.ok(applyCoredns !== -1, 'coredns-custom を apply していない');
  assert.ok(restart !== -1, 'coredns の rollout restart をしていない');
  // Corefile 自体は変わらないため reload プラグインが拾わない。restart が無いと
  // 「置いたのに効かない」状態になり、名前解決だけが静かに失敗する。
  assert.ok(applyCoredns < restart, 'restart が apply より前にある（古い ConfigMap で再起動する）');
  assert.ok(applyEdge !== -1 && applyEdge < applyCoredns, 'edge overlay の apply より前に coredns を触っている');
});

// --- IADR-0243 (#780 第2段): Keycloak issuer のエッジ移行 --------------------------------------
//
// KC_HOSTNAME_URL・Auth:MetadataAddress・Auth:ValidIssuers の 3 点は「どちらが in-cluster でどちらが
// エッジか」を取り違えると、.NET の OIDC metadata 取得がエッジの自己署名/ローカル CA に阻まれて
// TLS ハンドシェイクで落ちる（IADR-0086 決定の裏返し）。値の中身までは kustomize build も helm template
// も検証しないため（文字列として妥当な YAML であれば通ってしまう）、ここで静的に固定する。

const KEYCLOAK_DEPLOY = readAt(REPO_ROOT, 'deploy', 'local', 'infra', 'keycloak.yaml');
const VALUES_LOCAL = readAt(REPO_ROOT, 'deploy', 'local', 'values-local.yaml');

ok('#780 第2段: KC_HOSTNAME_URL がエッジ host（https://keycloak.localhost）を指す', () => {
  assert.ok(
    /KC_HOSTNAME_URL\s*\n\s*value:\s*https:\/\/keycloak\.localhost\s*$/m.test(KEYCLOAK_DEPLOY),
    'KC_HOSTNAME_URL が https://keycloak.localhost でない（issuer の単一情報源がずれている）',
  );
});

ok('#780 第2段: Auth:MetadataAddress は in-cluster（http://keycloak:8080）を指す', () => {
  const m = /metadataAddress:\s*(\S+)/.exec(VALUES_LOCAL);
  assert.ok(m, 'global.auth.metadataAddress が values-local.yaml に無い');
  assert.ok(
    m[1].startsWith('http://keycloak:8080/'),
    `metadataAddress が in-cluster host でない: ${m[1]}`,
  );
  assert.ok(
    !m[1].includes('keycloak.localhost'),
    'metadataAddress がエッジ host を指している（.NET の metadata 取得がローカル CA 未信頼で失敗し得る）',
  );
});

ok('#780 第2段: Auth:ValidIssuers はエッジ host（https://keycloak.localhost）を指す', () => {
  const m = /validIssuers:\s*(\S+)/.exec(VALUES_LOCAL);
  assert.ok(m, 'global.auth.validIssuers が values-local.yaml に無い');
  assert.ok(
    m[1].startsWith('https://keycloak.localhost/'),
    `validIssuers がエッジ host でない: ${m[1]}`,
  );
  assert.ok(
    !m[1].includes('keycloak:8080'),
    'validIssuers が in-cluster host を指している（エッジ issuer の token が受理されない）',
  );
});

ok('#780 第2段: metadataAddress と validIssuers が同じ realm パスを指す', () => {
  const metaRealm = (/metadataAddress:.*realms\/(\S+?)\//.exec(VALUES_LOCAL) || [])[1];
  const issuerRealm = (/validIssuers:.*realms\/(\S+)/.exec(VALUES_LOCAL) || [])[1];
  assert.ok(metaRealm, 'metadataAddress から realm 名を取り出せない');
  assert.ok(issuerRealm, 'validIssuers から realm 名を取り出せない');
  assert.strictEqual(
    metaRealm,
    issuerRealm.replace(/\/$/, ''),
    `metadataAddress（realm=${metaRealm}）と validIssuers（realm=${issuerRealm}）の realm が食い違っている`,
  );
});

process.stdout.write(`\n✓ ${passed} tests passed\n`);
