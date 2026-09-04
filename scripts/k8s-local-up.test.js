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
// #953: `kind: HelmChartConfig` の宣言。**ハーネスの入力でもある**（下の helm-controller 模型が読む）。
// REPO_ROOT 相対の形も持つ —— stub は cwd=REPO_ROOT で走るため、既定値は相対で渡す。
const TRAEFIK_MANIFEST_REL = 'deploy/local/edge/traefik-entrypoint.yaml';
const TRAEFIK_MANIFEST = path.join(REPO_ROOT, TRAEFIK_MANIFEST_REL);
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
//
// IADR-0369 (#1088): **永続化は opt-in ではなくなった**（既定オン・opt-out は PERSIST=0）。
//   `deploy/local/infra-persistence` / `deploy/local/observability-persistence` は既定で現れるので
//   この表から外し、既定オン側の意味論は下の「PERSIST 既定オン」節が固定する。
const OPTIN_TOKENS = [
  'deploy/local/observability', //     OBSERVABILITY ＋ PERSIST=0（素の overlay）
  'deploy/local/observability-persistence', // OBSERVABILITY（永続化は既定。IADR-0210 → IADR-0369）
  'grafana-oidc', //                   OBSERVABILITY (Grafana OIDC secret, IADR-0090)
  'deploy/local/vault', //             VAULT
  'vault-dev-token', //                VAULT (secret)
  'vault-oidc', //                     VAULT (OIDC client secret, IADR-0094)
  'deploy/local/headlamp', //          HEADLAMP
  'headlamp-oidc', //                  HEADLAMP (secret)
  'wikijs-oidc', //                    WIKIJS_OIDC (Wiki.js の OIDC ストラテジ seed が読む secret, IADR-0342)
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
  'embedding.deterministicLocal.enabled', // LOCALEMBED (決定的ローカル埋め込み, IADR-0313)
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

// #953 / [IADR-0258]: **helm-controller の模型**。`kubectl apply` は「オブジェクトを置けたか」しか
// 見ず、反映は helm-controller が非同期に行う —— そこで落ちても呼び出し側へは伝わらない。
// ハーネスの kubectl stub が反映の待ち合わせまで無条件に `exit 0` を返すと、**helm-controller が必ず
// 成功する世界**を仮定することになり、#953 が起きた世界を再現できない。そこで反映の成否だけは
// **宣言（traefik-entrypoint.yaml）と chart の版から決める**。
//
// 判定は #953 の実測（GitHub ホストランナー run 32554867883）そのものである:
//
//   | chart    | 受け付ける `expose`          |
//   | -------- | ---------------------------- |
//   | 25 以下   | **bool**（`expose: true`）    |  ← k3s v1.30.4 同梱の 25.0.3
//   | 26 以上   | **map**（`expose: {default:}`）|  ← 現行宣言が通っている版
//
// 加えて `admin` の `port` が 50000 でなければ、版に関わらず門（`=50000` の待ち）は成立しない。
//
// 🔴 **模型が版に依存することは、[IADR-0258] 決定 3（門を版依存の識別子で書かない）と矛盾しない。**
// 門（本番経路）は Service の port という Kubernetes コア API しか見ない。版に依存するのは**試験だけ**で、
// **版依存の事故を再現するには版を持つほかない**。代償は模型が実物からずれ得ることだが、それは
// traefik-entrypoint.yaml 冒頭の実測表が古くなることと**同じ 1 つの事実**であり、更新点は増えない。
const HELM_CONTROLLER_MODEL = [
  '# helm-controller ＋ traefik chart の values スキーマの模型（#953）。',
  '# 入力: 宣言 1 本（引数）と chart のメジャー版（-v major）。反映が成立すれば 0、しなければ 1。',
  'BEGIN { inadmin = 0; port = ""; form = "none"; pending = 0 }',
  '{',
  '  line = $0',
  '  sub(/#.*$/, "", line)                      # 行コメントは値ではない',
  '  if (line !~ /[^ ]/) next                   # 空行は構造を持たない',
  '  match(line, /^ */); ind = RLENGTH',
  '  if (inadmin && ind <= aind) inadmin = 0    # 兄弟キーまで戻ったら admin ブロックは終わり',
  '  if (line ~ /^ *admin: *$/) { inadmin = 1; aind = ind; next }',
  '  if (!inadmin) next',
  '  if (pending) {                             # 直前が裸の `expose:` ＝ map 形の入口',
  '    pending = 0',
  '    if (line ~ /^ *default: *(true|false) *$/ && ind > eind) { form = "map"; next }',
  '    form = "unknown"',
  '  }',
  '  if (line ~ /^ *port: *[0-9]+ *$/) { p = line; sub(/^ *port: */, "", p); sub(/ *$/, "", p); port = p; next }',
  '  if (line ~ /^ *expose: *$/) { pending = 1; eind = ind; next }',
  '  if (line ~ /^ *expose: *(true|false) *$/) { form = "bool"; next }',
  '}',
  'END {',
  '  if (port != "50000") {',
  '    printf "helm-controller(model): admin port=%s (門が待つのは 50000)\\n", (port == "" ? "<未宣言>" : port)',
  '    exit 1',
  '  }',
  '  if (major >= 26 && form == "map")  exit 0',
  '  if (major <= 25 && form == "bool") exit 0',
  '  printf "helm-controller(model): UPGRADE FAILED: error calling eq: incompatible types for comparison"',
  '  printf " (chart %s は %s 形を受け付けない)\\n", major, form',
  '  exit 1',
  '}',
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
//
// 🔴 その 1 フラグ**では足りない**（#953 第 2 次）。フラグを立てるのはテスト側であって宣言側ではないため、
// `traefik-entrypoint.yaml` を壊しても何も起きない ——「門は在るが、門が守る宣言は無検査」だった。
// そこで反映の待ち合わせだけは、**宣言と chart の版から成否を決める**（HELM_CONTROLLER_MODEL）。
//   STUB_TRAEFIK_MANIFEST     … 読ませる宣言（既定 = リポジトリの実物）。変異はここへ一時ファイルを渡す。
//   STUB_TRAEFIK_CHART_MAJOR  … chart のメジャー版（既定 26 = 現行宣言が通る版）。
// STUB_TRAEFIK_ADMIN_MISSING は模型より**手前**で効く（「理由を問わず反映が来ない」の直接表現）。
const KUBECTL_STUB = [
  '#!/usr/bin/env bash',
  'echo "kubectl $*" >> "$STUB_LOG"',
  'if [ "${STUB_CRD_ABSENT:-}" = "1" ] && [ "${1:-}" = "get" ] && [ "${2:-}" = "crd" ]; then exit 1; fi',
  'if [ "${STUB_NS_ABSENT:-}" = "1" ] && [ "${1:-}" = "get" ] && [ "${2:-}" = "namespace" ] && [ "${3:-}" = "argocd" ]; then exit 1; fi',
  'if [ "${STUB_VAULT_DEPLOY_ABSENT:-}" = "1" ]; then case "$*" in *"get deploy vault"*) exit 1;; esac; fi',
  'if [ "${STUB_TRAEFIK_ADMIN_MISSING:-}" = "1" ]; then case "$*" in *--for=jsonpath*svc/traefik*) exit 1;; esac; fi',
  // IADR-0369 (#1088): STUB_SC_ABSENT=1 で `kubectl get storageclass local-path` を非0（provisioner 不在）に返させ、
  // 既定（永続化）が黙って emptyDir へ落ちずに止まることを検証できるようにする。
  'if [ "${STUB_SC_ABSENT:-}" = "1" ] && [ "${1:-}" = "get" ] && [ "${2:-}" = "storageclass" ]; then exit 1; fi',
  // IADR-0369 (#1088): realm 後追い Job（deploy/local/keycloak-setup/reconcile-realm.sh）の完了待ち。
  // conditions の問い合わせに Complete を返す（返さないと起動器が Job の完了を 300 秒待つ）。
  'case "$*" in *"get job"*conditions*) echo "Complete "; exit 0;; esac',
  // #953: 反映の待ち合わせ **だけ** は記録して 0 を返さない。宣言を helm-controller の模型に通す。
  'case "$*" in *--for=jsonpath*svc/traefik*) exec awk -v major="${STUB_TRAEFIK_CHART_MAJOR:-26}" -f "$STUB_HELM_MODEL" "${STUB_TRAEFIK_MANIFEST:-' +
    TRAEFIK_MANIFEST_REL +
    '}" ;; esac',
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
  // helm-controller の模型（awk）。PATH には置かない —— これはコマンドの差し替えではなく、
  // kubectl stub が反映の成否を決めるために読む**データ**である。
  const modelFile = path.join(workdir, 'helm-controller-model.awk');
  fs.writeFileSync(modelFile, HELM_CONTROLLER_MODEL);

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
    'LOCALEMBED',
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
    STUB_HELM_MODEL: modelFile, // #953: kubectl stub が反映の成否を決めるのに使う
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

// 前提（#953 第 2 次）: **リポジトリの実物の宣言が、既定の chart 版で反映が成立すること。**
// これが「壊す前は落ちない」側の対照であり、宣言と門を結ぶ結び目である —— 誰かが
// traefik-entrypoint.yaml の `expose` を bool 形へ戻したら、ここが落ちる。
//
// 🔴 **前提として、他のどの試験よりも先に置く。** 実物が壊れると LOCALEDGE=1 の run はすべて
// 門で止まり、下の「トークンの単独検出力」（[IADR-0213]）のような**無関係な試験が先に赤くなる**
// ——「seed-abac-policies.js が dead token」と言われても原因は読めない。落ちる位置は原因の位置に近く。
ok('前提: traefik の宣言（実物）は現行 chart で反映が成立する（#953 の門の対照）', () => {
  const healthy = runUp({ LOCALEDGE: '1' });
  assert.strictEqual(
    healthy.status,
    0,
    `${TRAEFIK_MANIFEST_REL} の宣言では反映が成立しない（#953 の門が落ちる）。` +
      ` ports.admin の port / expose の型を確認すること:\n${healthy.stdout}\n${healthy.stderr}`,
  );
  assert.ok(anyLineHas(healthy.lines, 'cert-manager'), '反映は成立したのに後続段へ進んでいない');
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
// 母集合は 2 通りの run の和を取る。永続化（既定オン。IADR-0369）/ ESO は他ゲートの出力を *置換* するため
// （observability → observability-persistence / grafana-oidc の手動 apply → ExternalSecret 委譲）、
// 全部立てた run だけでは素の側の行が採れない。素の側は PERSIST=0 で採る。
const GATES_ALL = {
  OBSERVABILITY: '1',
  VAULT: '1',
  ARGOCD: '1',
  LOCALEDGE: '1',
  ESO: '1',
  ABACSEED: '1',
  SEARCHSEED: '1',
  LOCALEMBED: '1',
  HEADLAMP: '1',
  WIKIJS_OIDC: '1',
};
const { ESO: _e, ...GATES_NO_REPLACEMENT_BASE } = GATES_ALL;
const GATES_NO_REPLACEMENT = { ...GATES_NO_REPLACEMENT_BASE, PERSIST: '0' };
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
  // 永続化が既定（IADR-0369）なので infra の apply 先は infra-persistence。PERSIST=0 なら素の infra。どちらでも同じ順序を要求する。
  const infraApplyIdx = DEFAULT.lines.findIndex((l) => /^kubectl apply -k deploy\/local\/infra(-persistence)?$/.test(l));
  assert.ok(realmIdx >= 0 && themeIdx >= 0 && infraApplyIdx >= 0, '3 行のいずれかが見つからない');
  assert.ok(realmIdx < themeIdx, 'keycloak-realms より前に keycloak-theme-platform が作られている');
  assert.ok(
    themeIdx < infraApplyIdx,
    'ConfigMap 作成が Deployment 適用（apply -k）より後になっている（初回起動でテーマが解決されない）',
  );
});

// SC-15, FR-22, ADR-0045 決定 9, IADR-0344 (#1144): 捕捉用 MTA は **dev 既定**である。
//
// 🔴 **ゲートを持たないことが、この配備物の要点である。** 決定 9 は「開発環境では実送信しない」を
// 無条件で定めており、opt-in にすると**ゲートを立てない人の既定が外を向いたまま**になる（決定 9 の
// 弱い版）。「ゲートが無いのだから起動器に書かなくても kustomize が持っていく」と読んで rollout 待ちを
// 落とすと、**Keycloak より後に立った捕捉用 MTA へ最初の申請が届かない**。ここで両方を固定する。
ok('#1144: 捕捉用 MTA は既定（env 未設定）で rollout を待つ —— どの opt-in にも属さない', () => {
  assert.ok(
    anyLineHas(DEFAULT.lines, 'rollout status deploy/mailpit'),
    '既定の起動で mailpit の rollout 待ちが発行されていない（捕捉用 MTA が居ない開発環境になる）',
  );
  // 陽性対照: 宣言が base の kustomization に居ること（overlay 側だけに居ると PERSIST 時しか立たない）。
  const kust = fs.readFileSync(path.join(REPO_ROOT, 'deploy/local/infra/kustomization.yaml'), 'utf8');
  assert.ok(/^\s*-\s*mailpit\.yaml\s*$/m.test(kust), 'deploy/local/infra/kustomization.yaml に mailpit.yaml が無い');
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

// --- IADR-0369 (#1088): 永続化は **既定オン**（IADR-0082 決定 1 の置換）。opt-out は PERSIST=0 ---------
//
// 稼働 dev クラスタは誰も PERSIST=1 を付けずに立てられ、runtime state（TOTP 資格情報）が Pod 再作成のたびに
// 黙って消えていた。既定を永続へ返し、**黙って emptyDir へ落ちる経路を持たない**ことを固定する。
const appliesBareInfra = (lines) => lines.some((l) => l === 'kubectl apply -k deploy/local/infra');

ok('既定: infra-persistence を apply する（永続化は既定。素の infra は apply しない）', () => {
  assert.ok(anyLineHas(DEFAULT.lines, 'apply -k deploy/local/infra-persistence'), '既定で infra-persistence が apply されない');
  assert.ok(!appliesBareInfra(DEFAULT.lines), '既定なのに素の deploy/local/infra も apply された（併存）');
  // 既定経路は StorageClass の実在を確かめる（黙って非永続にならないための前提）。
  assert.ok(anyLineHas(DEFAULT.lines, 'get storageclass local-path'), '既定で StorageClass を確かめていない');
});

ok('PERSIST=0: 素の infra を apply し、永続化オーバーレイは現れない（使い捨てスタック専用の opt-out）', () => {
  const res = runUp({ PERSIST: '0' });
  assert.strictEqual(res.status, 0, `PERSIST=0 が非0終了: ${res.stderr}`);
  assert.ok(appliesBareInfra(res.lines), 'PERSIST=0 で素の infra が apply されない');
  assert.ok(!anyLineHas(res.lines, 'infra-persistence'), 'PERSIST=0 なのに infra-persistence が現れた');
});

ok('PERSIST=1（旧 opt-in の綴り）は既定と同じ（手順書の古い呼び方でも壊れない）', () => {
  const res = runUp({ PERSIST: '1' });
  assert.strictEqual(res.status, 0, `PERSIST=1 が非0終了: ${res.stderr}`);
  assert.ok(anyLineHas(res.lines, 'apply -k deploy/local/infra-persistence'), 'PERSIST=1 で infra-persistence が apply されない');
});

ok('StorageClass local-path が無ければ止まる（黙って emptyDir へ落とさない・#1088 の本体）', () => {
  const res = runUp({ STUB_SC_ABSENT: '1' });
  assert.notStrictEqual(res.status, 0, 'provisioner 不在なのに EXIT=0 で返った');
  assert.ok(/PERSIST=0/.test(res.stderr), '止まったが opt-out の案内が無い');
  assert.ok(!appliesBareInfra(res.lines) && !anyLineHas(res.lines, 'apply -k deploy/local/infra-persistence'),
    '止まる前に infra を apply している');
  // 陽性対照: PERSIST=0 なら provisioner が無くても立つ（fail-safe の口は明示のときだけ）。
  const optOut = runUp({ STUB_SC_ABSENT: '1', PERSIST: '0' });
  assert.strictEqual(optOut.status, 0, `PERSIST=0 なのに provisioner 不在で止まった: ${optOut.stderr}`);
});

// OBSERVABILITY=1: observability スタックを apply（IADR-0077）＋ Grafana OIDC secret を作成（IADR-0090）。
// 永続化が既定なので、選ばれるのは observability-persistence（IADR-0210 → IADR-0369）。
ok('OBSERVABILITY=1: observability-persistence を apply・grafana-oidc secret を作成', () => {
  const res = runUp({ OBSERVABILITY: '1' });
  assert.ok(anyLineHas(res.lines, 'apply -k deploy/local/observability-persistence'), 'observability-persistence が apply されない');
  // IADR-0090: Grafana generic OAuth の client secret は k8s Secret grafana-oidc 経由（平文コミットなし）。
  assert.ok(anyLineHas(res.lines, 'grafana-oidc'), 'grafana-oidc secret が作られない');
});

// --- IADR-0210 (#787) → IADR-0369 (#1088): 可観測性スタックの永続化 overlay のゲート意味論 -------------
//
// `deploy/local/observability-persistence` は **OBSERVABILITY=1**（かつ PERSIST=0 でない）ときに選ばれる。
// 既定オフは上の OPTIN_TOKENS が固定済み。ここでは「片肺では現れない」「立てたら *置換* であって併存でない」
// 「PERSIST=0 なら素の overlay」の 3 点を固定する。素の overlay 名は永続化版の **接頭辞**なので、素の側は
// 末尾境界を見て判定する（`includes` で見ると `-persistence` の行にマッチし、置換の検査が常に成功する静かな縮退になる）。
// 境界判定は OPTIN_TOKENS と同じ `matchesToken` を使う（同じ規則の実装を 2 つ持たない・IADR-0213）。
const appliesBareObservability = (lines) =>
  lines.some((l) => l.includes('apply -k ') && matchesToken(l, 'deploy/local/observability'));

ok('既定（OBSERVABILITY 無効）: 可観測性の overlay はどちらも現れない（永続化単独ではスタック自体が立たない）', () => {
  assert.ok(
    !anyLineHas(DEFAULT.lines, 'deploy/local/observability-persistence'),
    'OBSERVABILITY 無効なのに可観測性の永続化 overlay が現れた',
  );
  assert.ok(!appliesBareObservability(DEFAULT.lines), 'OBSERVABILITY 無効なのに素の observability が apply された');
});

ok('OBSERVABILITY=1（永続化は既定）: observability-persistence へ置換される（素の overlay は現れない）', () => {
  const res = runUp({ OBSERVABILITY: '1' });
  assert.strictEqual(res.status, 0, `OBSERVABILITY=1 で異常終了した: ${res.stderr}`);
  assert.ok(
    anyLineHas(res.lines, 'apply -k deploy/local/observability-persistence'),
    'observability-persistence が apply されない',
  );
  assert.ok(
    !appliesBareObservability(res.lines),
    '永続化が既定なのに素の deploy/local/observability も apply された（置換でなく併存になっている）',
  );
  // 永続化しても collector の forwarding 切替と Grafana OIDC secret は不変（ゲートの意味論を変えない）。
  assert.ok(anyLineHas(res.lines, 'rollout restart deploy/otel-collector'), 'collector の rollout restart が消えた');
  assert.ok(anyLineHas(res.lines, 'grafana-oidc'), 'grafana-oidc secret が作られない');
});

ok('PERSIST=0 + OBSERVABILITY=1: 素の observability が apply され、永続化版は現れない', () => {
  const res = runUp({ PERSIST: '0', OBSERVABILITY: '1' });
  assert.strictEqual(res.status, 0, `PERSIST=0+OBSERVABILITY で異常終了した: ${res.stderr}`);
  assert.ok(appliesBareObservability(res.lines), 'PERSIST=0 なのに素の observability が apply されない');
  assert.ok(!anyLineHas(res.lines, 'deploy/local/observability-persistence'), 'PERSIST=0 なのに永続化版が現れた');
});

// --- IADR-0369 (#1088 / #324): realm は「静的 import ＋ 起動器の後段で差分を当てる」 -------------------
//
// 永続化が既定になった瞬間から `--import-realm` は既存 realm を黙って飛ばす（IGNORE_EXISTING）。
// **realm JSON を変えたら up の再実行で稼働 realm へ届く**ことを、次の不変条件で固定する:
//   (1) 期待値は起動器が実 realm ファイルから毎回作る ConfigMap keycloak-realms（単一情報源）である。
//   (2) 後追い Job はその ConfigMap と、起動器が作る keycloak-admin Secret（username / password）を読む。
//   (3) 後追いは Keycloak の rollout が済んだ後に走る。
//   (4) Keycloak pod で kcadm.sh を exec しない（本体が OOMKilled になる）。旧スクリプトは撤去済み。
const KC_SETUP_DIR = path.join(REPO_ROOT, 'deploy', 'local', 'keycloak-setup');
const RECONCILE_JOB_YAML = fs.readFileSync(path.join(KC_SETUP_DIR, 'realm-reconcile-job.yaml'), 'utf8');
const KEYCLOAK_INFRA_YAML = fs.readFileSync(path.join(REPO_ROOT, 'deploy', 'local', 'infra', 'keycloak.yaml'), 'utf8');

ok('IADR-0369: realm ConfigMap は実 realm ファイルから毎回作られ、後追い Job は同じ ConfigMap を読む（単一情報源）', () => {
  const cmLine = DEFAULT.lines.find((l) => l.startsWith('kubectl create configmap keycloak-realms '));
  assert.ok(cmLine, 'keycloak-realms の create 行が無い');
  assert.ok(cmLine.includes('--from-file=microservices-platform-realm.json=deploy/keycloak/microservices-platform-realm.json'),
    '期待値が実 realm ファイルから作られていない');
  assert.ok(/configMap:\n\s+name: keycloak-realms\s*$/m.test(RECONCILE_JOB_YAML), 'Job が keycloak-realms をマウントしていない');
  assert.ok(/name: REALM_DIR\n\s+value: \/import/.test(RECONCILE_JOB_YAML) && /mountPath: \/import/.test(RECONCILE_JOB_YAML),
    'Job の REALM_DIR と ConfigMap のマウント先が一致しない');
});

ok('IADR-0369: 後追いは Keycloak の rollout の後に走り、Job は毎回 delete → apply される（Job は immutable）', () => {
  const rolloutIdx = DEFAULT.lines.findIndex((l) => l.includes('rollout status deploy/keycloak'));
  const scriptCm = DEFAULT.lines.findIndex((l) => l.startsWith('kubectl create configmap keycloak-realm-reconcile '));
  const del = DEFAULT.lines.findIndex((l) => l.includes('delete job keycloak-realm-reconcile'));
  assert.ok(rolloutIdx >= 0 && scriptCm >= 0 && del >= 0, `行が見つからない: rollout=${rolloutIdx} cm=${scriptCm} delete=${del}`);
  assert.ok(rolloutIdx < scriptCm && scriptCm < del, '後追いが Keycloak の rollout より前に走っている');
  assert.ok(DEFAULT.lines[scriptCm].includes('--from-file=reconcile-realm.js=') && DEFAULT.lines[scriptCm].includes('reconcile-realm.js'),
    'スクリプト本体が ConfigMap 化されていない');
  // check モード（check-stack-ready の G9）では Job 名を変える＝apply の Job を消さない。
  assert.ok(!anyLineHas(DEFAULT.lines, 'keycloak-realm-check'), 'up の既定経路で check 用 Job が現れた');
});

ok('IADR-0369: 管理者名・パスワードの単一情報源は Secret keycloak-admin（Keycloak と Job が同じキーを読む）', () => {
  const secret = DEFAULT.lines.find((l) => l.startsWith('kubectl create secret generic keycloak-admin '));
  assert.ok(secret && secret.includes('--from-literal=username=') && secret.includes('--from-literal=password='),
    'keycloak-admin に username / password の両方が無い');
  for (const [yaml, label] of [[KEYCLOAK_INFRA_YAML, 'keycloak.yaml'], [RECONCILE_JOB_YAML, 'realm-reconcile-job.yaml']]) {
    for (const key of ['username', 'password']) {
      assert.ok(new RegExp(`secretKeyRef:\\n\\s+name: keycloak-admin\\n\\s+key: ${key}\\s*$`, 'm').test(yaml),
        `${label} が keycloak-admin/${key} を secretKeyRef で読んでいない`);
    }
  }
  assert.ok(!/KEYCLOAK_ADMIN\n\s+value:/.test(KEYCLOAK_INFRA_YAML), 'keycloak.yaml に管理者名の直書きが残っている');
});

ok('IADR-0369: Keycloak pod で kcadm.sh を exec しない（旧 reconcile-backchannel-logout.sh は撤去済み）', () => {
  assert.ok(!fs.existsSync(path.join(KC_SETUP_DIR, 'reconcile-backchannel-logout.sh')), '旧スクリプトが残っている');
  assert.ok(!DEFAULT.lines.some((l) => /exec .*keycloak/.test(l) && /kcadm/.test(l)), 'pod 内で kcadm.sh を exec している');
  assert.ok(!anyLineHas(DEFAULT.lines, 'reconcile-backchannel-logout'), '旧スクリプトが呼ばれている');
  // 陽性対照: 後追い自体は走っている（Job の apply が採取されている）。
  assert.ok(anyLineHas(DEFAULT.lines, 'delete job keycloak-realm-reconcile'), '後追い Job が走っていない');
});

ok('IADR-0369: 後追い Job は Keycloak と同じ namespace に置き、in-cluster の Service を叩く（エッジ・TLS・メッシュに依存しない）', () => {
  assert.ok(/namespace: platform-infra/.test(RECONCILE_JOB_YAML), 'Job が platform-infra に無い');
  assert.ok(/name: KC_URL\n\s+value: http:\/\/keycloak:8080/.test(RECONCILE_JOB_YAML), 'Job が in-cluster の keycloak:8080 を向いていない');
  assert.ok(/backoffLimit: 0/.test(RECONCILE_JOB_YAML), '失敗を再試行で隠さないための backoffLimit: 0 が無い');
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

// --- #953 第 2 次: **宣言そのもの**を変異させる（門と宣言を結ぶ） ---------------
//
// 上の 2 本が変異させているのは stub の env フラグであって、`traefik-entrypoint.yaml` ではない。
// つまり base では **`expose` を bool 形へ戻しても・admin の port を書き換えても・ports.admin を
// 消しても、テストは緑のまま**だった —— 門は在るが、門が守っている宣言は無検査だった。
// #953 が塞ごうとした「宣言はバージョン依存で、壊れても誰も気付かない」の後半がテスト側に残っていた。
//
// ここから下は kubectl stub の helm-controller 模型（HELM_CONTROLLER_MODEL）を通す。
// **リポジトリの実物を読む対照**と、**一時ファイルへ壊した変異**を対で置く。

/**
 * `traefik-entrypoint.yaml` を変異させた一時マニフェストを作り、そのパスを返す。
 * **リポジトリの実物は書き換えない**（テストが作業ツリーを汚さない）。
 * @param {(src: string) => string} mutate 変異関数
 * @returns {string} 変異後マニフェストの絶対パス
 */
function mutatedTraefikManifest(mutate) {
  const src = fs.readFileSync(TRAEFIK_MANIFEST, 'utf8');
  const out = mutate(src);
  // 変異が空振りしていたら「壊したのに落ちない」ではなく「壊せていない」である。区別する。
  assert.notStrictEqual(out, src, '変異が実物と同一（宣言の書式が変わって置換が空振りしている）');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'k8s-up-mutate-'));
  const p = path.join(dir, 'traefik-entrypoint.yaml');
  fs.writeFileSync(p, out);
  return p;
}

// 変異 1: map 形 → bool 形（chart 25 の書式へ差し戻す ＝ #953 の型不一致の裏返し）。
const TO_BOOL_EXPOSE = (src) => src.replace('        expose:\n          default: true\n', '        expose: true\n');
// 変異 2: admin の port を門が待つ 50000 からずらす。
const TO_PORT_DRIFT = (src) => src.replace('port: 50000', 'port: 50001').replace('exposedPort: 50000', 'exposedPort: 50001');
// 変異 3: ports.admin ブロックごと落とす（entrypoint の宣言を失う）。
const TO_NO_ADMIN = (src) => src.replace(/ {6}admin:\n(?: {8}.*\n| {10}.*\n)+/, '');

// 「壊す前は落ちない」側（実物の宣言 ＝ 既定の STUB_TRAEFIK_MANIFEST）は、**ファイル冒頭の前提**
// 「traefik の宣言（実物）は現行 chart で反映が成立する」が持つ。原因の位置に近いところで落とすため、
// 対照だけを先頭へ置いている。ここには変異側を並べる。

ok('#953 第2次: 変異 —— expose を chart が受け付けない bool 形へ壊すと up は非 0 で終わる', () => {
  const broken = runUp({
    LOCALEDGE: '1',
    STUB_TRAEFIK_MANIFEST: mutatedTraefikManifest(TO_BOOL_EXPOSE),
  });
  assert.notStrictEqual(broken.status, 0, '宣言を壊したのに up が成功で返った（#953 の欠陥そのもの）');
  // 落ちたのが門であって別の理由でないこと（待ち合わせが実際に発行されている）。
  assert.ok(
    broken.lines.some((l) => l.includes(' wait ') && l.includes('--for=jsonpath') && l.includes('svc/traefik')),
    '反映の待ち合わせが発行されていない（別の理由で落ちている）',
  );
  // 落ちる位置が原因の位置に近いこと。
  assert.ok(!anyLineHas(broken.lines, 'cert-manager'), '反映に失敗したのに後続の cert-manager 段まで進んでいる');
});

ok('#953 第2次: 実測の再現 —— 実物の宣言（map 形）＋ chart 25 は反映に失敗し up が落ちる', () => {
  // これが #953 で実際に踏んだ組である（k3s v1.30.4 同梱の traefik chart 25.0.3）。
  // **宣言は正しいのに版が古い**という向きも、同じ門が捕まえることを固定する。
  const broken = runUp({ LOCALEDGE: '1', STUB_TRAEFIK_CHART_MAJOR: '25' });
  assert.notStrictEqual(broken.status, 0, 'chart 25 で map 形の宣言が通ってしまった（実測と食い違う）');
  assert.ok(!anyLineHas(broken.lines, 'cert-manager'), '反映に失敗したのに後続段まで進んでいる');
});

ok('#953 第2次: 陰性対照 —— bool 形 ＋ chart 25 は通る（「変異なら落ちる」模型ではない）', () => {
  // 🔴 これが無いと、模型が「実物以外は全部落とす」だけの飾りでも上の変異試験が緑になる。
  // bool 形は **chart 25 では正しい書式**であり、落ちる理由は書式ではなく**版との不一致**である。
  const okRun = runUp({
    LOCALEDGE: '1',
    STUB_TRAEFIK_MANIFEST: mutatedTraefikManifest(TO_BOOL_EXPOSE),
    STUB_TRAEFIK_CHART_MAJOR: '25',
  });
  assert.strictEqual(okRun.status, 0, `chart 25 で bool 形が落ちた（模型が版を見ていない）: ${okRun.stdout}`);
});

ok('#953 第2次: admin の port ずれ・ports.admin の消失も捕まえる', () => {
  // 門は `admin=50000` を待つ。宣言側だけ 50001 へ動かすと、実クラスタでは永遠に成立しない
  // ——「門が待つ値」と「宣言が作る値」が別々に編集できてしまうことを、ここで結ぶ。
  const drift = runUp({ LOCALEDGE: '1', STUB_TRAEFIK_MANIFEST: mutatedTraefikManifest(TO_PORT_DRIFT) });
  assert.notStrictEqual(drift.status, 0, 'admin の port を 50001 へずらしたのに up が成功で返った');
  const gone = runUp({ LOCALEDGE: '1', STUB_TRAEFIK_MANIFEST: mutatedTraefikManifest(TO_NO_ADMIN) });
  assert.notStrictEqual(gone.status, 0, 'ports.admin を丸ごと消したのに up が成功で返った');
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

// FR-13, IADR-0327 (#1108): Wiki.js の初期セットアップ。**opt-in ではない。**
// 稼働 dev クラスタで、Wiki.js が setup モードのまま `2/2 Running` で 8 日間放置され、
// `DocumentUpdated` / `DocumentDeleted` が全件エラーキューへ落ちていた。既定の経路が
// この状態を残すことが #1108 そのものなので、**既定で走ること自体が回帰対象**である。
ok('#1108: 既定（env を 1 つも与えない）でも Wiki.js の初期化が走る（opt-in にしない）', () => {
  assert.ok(
    anyLineHas(DEFAULT.lines, 'get deploy wiki-js'),
    'bootstrap が呼ばれていない（wiki-js の存在確認が出ていない）',
  );
  assert.ok(
    anyLineHas(DEFAULT.lines, 'exec -i deploy/wiki-js -c wiki-js'),
    'wiki-js コンテナ内 loopback への問い合わせが出ていない',
  );
});

ok('#1108: 初期化は wiki-js コンテナ内の loopback へ出す（エッジにも port-forward にも依存しない）', () => {
  const line = DEFAULT.lines.find((l) => l.includes('exec -i deploy/wiki-js -c wiki-js'));
  assert.ok(line, 'wiki-js への exec が無い');
  assert.ok(line.includes('http://127.0.0.1:3000'), `loopback を叩いていない: ${line}`);
  // 🔴 STRICT mTLS（#1109）だとメッシュ外からの平文は Envoy に落とされる。port-forward も使わない。
  assert.ok(!anyLineHas(DEFAULT.lines, 'port-forward svc/wiki-js'), 'port-forward に依存している');
});

ok('#1108: 初期化が失敗しても up 全体は止めない（best-effort。門は check-stack-ready 側）', () => {
  const src = fs.readFileSync(path.join(REPO_ROOT, 'scripts', 'k8s-local-up.sh'), 'utf8');
  const at = src.indexOf('bash "$ROOT/deploy/local/wikijs-setup/bootstrap.sh"');
  assert.ok(at > 0, 'bootstrap の呼び出しが k8s-local-up.sh に無い');
  assert.ok(/\|\|\s*echo\s+"?\s*WARN/.test(src.slice(at, at + 400)),
    'Wiki.js 初期化の失敗が best-effort になっていない');
});

ok('#1108: 発行済みの apiKey を空で潰さない（up の再実行が同期を壊さない）', () => {
  // コメント行は判定から外す —— **注意書きの中の悪い例を検出して落ちる**（実際に落ちた）。
  const src = fs.readFileSync(path.join(REPO_ROOT, 'scripts', 'k8s-local-up.sh'), 'utf8')
    .split('\n').filter((l) => !/^\s*#/.test(l)).join('\n');
  // 🔴 既存値を見ずに空既定で上書きすると、up を再実行するたびに apiKey が空へ戻り、
  //   次に wiki-service の Pod が作り直された瞬間に #1108 が再発する（時間差で表面化する）。
  assert.ok(
    !/apiKey=\$\{WIKIJS_SYNC_APIKEY:-\}/.test(src),
    '既存の apiKey を無条件に空で上書きしている（#1108 が再発する）',
  );
  assert.ok(
    /apiKey=\$\{WIKIJS_SYNC_APIKEY:-\$\{wikijs_apikey_existing\}\}/.test(src),
    '既存値へのフォールバックが無い',
  );
  assert.ok(anyLineHas(DEFAULT.lines, 'get secret wikijs-sync'), '既存 apiKey を読んでいない');
});

ok('#1108: bootstrap は既定パスワードをコミットしない（乱数生成へ倒す）', () => {
  const sh = fs.readFileSync(
    path.join(REPO_ROOT, 'deploy', 'local', 'wikijs-setup', 'bootstrap.sh'), 'utf8');
  // 他の dev 既定（`*-dev-secret-change-me`）と**同じ形を置かない**。ここはエッジに露出する
  // 実ログイン口であり、既定値を置くと「変えなければ誰でも入れる管理者」がリポジトリに載る。
  assert.ok(!/change-me/.test(sh), 'bootstrap.sh に dev 既定パスワードが書かれている');
  assert.ok(/randomBytes/.test(sh), '乱数生成の経路が無い');
  // 秘密を標準出力へ出さない（長さだけ出す）。
  assert.ok(!/log\s+"[^"]*\$\{?new_key/.test(sh), 'API キーを log に出している');
  assert.ok(!/log\s+"[^"]*\$\{?admin_password/.test(sh), '管理者パスワードを log に出している');
  assert.ok(!/log\s+"[^"]*\$\{?jwt/.test(sh), 'JWT を log に出している');
});

// --- NFR-09, IADR-0095/IADR-0328/IADR-0342 (#1127・#397 の再起票) ---------------
//
// Wiki.js の OIDC ストラテジは **Wiki.js の DB（`authentication` テーブル）保持**で、manifest にも
// Helm values にも無い。#780 が「7 クライアントすべてでログインが成立する」と測ったとき、
// **Wiki.js の分だけは人が手で SQL を流していた。**
//
// 🔴 ここの検査は **stub ハーネスの実行ログでは測れない。** bootstrap は Wiki.js のコンテナ内
// loopback へ HTTP を出すが、スタブは何も返さないので段 0 の判定不能で早期終了する（＝段 8 まで
// 到達しない）。したがって **script の本文に対する静的な不変条件**として置く ——
// 上の「既定パスワードをコミットしない」（#1108）と同じ形である。
const WIKIJS_BOOTSTRAP = fs.readFileSync(
  path.join(REPO_ROOT, 'deploy', 'local', 'wikijs-setup', 'bootstrap.sh'), 'utf8');
// 「やらない形」を名指しで禁じる検査は、**注意書きの中の悪い例を拾って落ちる**（#1108 で実際に落ちた）。
// コメント行を外した版を対にして持つ。SQL の heredoc は `#` 始まりでないので落ちない。
const WIKIJS_BOOTSTRAP_CODE = WIKIJS_BOOTSTRAP
  .split('\n').filter((l) => !/^\s*#/.test(l)).join('\n');

ok('#1127: OIDC ストラテジの投入は既定オフ（WIKIJS_OIDC ゲートを持つ）', () => {
  // endpoint はエッジ host を前提にする（IADR-0328）。LOCALEDGE 抜きで既定 ON にすると
  // 「押せるが 502 になるボタン」を作る。既定では `local` ログインだけが残る（fail-safe）。
  assert.ok(/if \[ "\$\{WIKIJS_OIDC:-\}" != "1" \]/.test(WIKIJS_BOOTSTRAP),
    'WIKIJS_OIDC のゲートが無い（既定で authentication テーブルへ書き込む形になっている）');
  // 起動器側の secret 供給も同じゲートに揃える（機能オフのとき未使用 Secret を残さない）。
  const up = fs.readFileSync(path.join(REPO_ROOT, 'scripts', 'k8s-local-up.sh'), 'utf8');
  assert.ok(/\[ "\$\{WIKIJS_OIDC:-\}" = "1" \] && \[ "\$\{ESO:-\}" != "1" \]/.test(up),
    '手動 apply 側に WIKIJS_OIDC ゲートが無い');
});

ok('#1127: DELETE→INSERT にしない（users.providerKey の外部キーを壊す形を持ち込まない）', () => {
  // 🔴 `deploy/local/wiki-oidc/README.md` が載せてきた手作業は
  //   `DELETE FROM authentication WHERE "strategyKey"='oidc'` → `INSERT` だが、これは
  //   **誰か 1 人でも OIDC でログインした後は必ず落ちる**（`users_providerkey_foreign`）。
  //   稼働クラスタで ROLLBACK 付きに実測済み。自動化を UPSERT から DELETE→INSERT へ
  //   「戻す」変更をここで止める。
  assert.ok(!/DELETE\s+FROM\s+authentication/i.test(WIKIJS_BOOTSTRAP_CODE),
    'authentication を DELETE している（初回しか冪等でない形へ戻っている）');
  assert.ok(/ON CONFLICT \(key\) DO UPDATE SET/.test(WIKIJS_BOOTSTRAP), 'UPSERT になっていない');
  // 既存行があればその key を再利用する（外部キーを切らず、二重行も作らない）。
  assert.ok(/SELECT a\.key FROM authentication a WHERE a\."strategyKey" = 'oidc'/.test(WIKIJS_BOOTSTRAP),
    '既存の oidc 行の key を再利用していない（管理 UI 由来の乱数 key に二重行を作る）');
});

ok('#1127: 変わったときだけ wiki-js を再起動する（2 回目は no-op）', () => {
  // 無条件に rollout を打つと、up の再実行や並行作業のたびに wiki-js が落ちる。
  // 「差分があった件数」を SQL に返させ、0 件の枝では再起動しない。
  assert.ok(/RETURNING 1/.test(WIKIJS_BOOTSTRAP), '変更件数を返す形になっていない');
  const at = WIKIJS_BOOTSTRAP.indexOf('case "$oidc_changed" in');
  assert.ok(at > 0, '変更件数による分岐が無い');
  const body = WIKIJS_BOOTSTRAP.slice(at, WIKIJS_BOOTSTRAP.indexOf('esac', at));
  const branches = body.split(';;');
  const zero = branches.find((b) => /(^|\n)\s*0 \)/.test(b));
  const other = branches.find((b) => /(^|\n)\s*\* \)/.test(b));
  assert.ok(zero, '「変更 0 件」の枝が無い');
  assert.ok(other, '「変更あり」の枝が無い');
  assert.ok(!/rollout restart/.test(zero), '変更 0 件なのに wiki-js を再起動している（no-op でない）');
  assert.ok(/rollout restart/.test(other), '変更があっても再起動しない（DB だけ書いても反映されない）');
});

ok('#1127: client secret を取れないときは既存の設定に触らない（空で潰さない）', () => {
  const guard = 'if [ "${WIKIJS_OIDC:-}" = "1" ] && [ -z "$oidc_secret" ]; then';
  const at = WIKIJS_BOOTSTRAP.indexOf(guard);
  assert.ok(at > 0, 'secret 不在で「何もしない」へ倒す枝が無い');
  const branch = WIKIJS_BOOTSTRAP.slice(at, WIKIJS_BOOTSTRAP.indexOf('elif', at));
  assert.ok(!/psql/.test(branch), 'secret が無いのに DB を書きにいっている（動いていたログインを壊す）');
  // 秘密を標準出力へ出さない（#1108 と同じ作法を段 8 にも課す）。
  assert.ok(!/log\s+"[^"]*\$\{?oidc_secret/.test(WIKIJS_BOOTSTRAP), 'client secret を log に出している');
  assert.ok(!/log\s+"[^"]*\$\{?oidc_config/.test(WIKIJS_BOOTSTRAP), 'config（secret を含む）を log に出している');
});

ok('#1127: 5 つの URL を揃えない（ブラウザ 3 つはエッジ host / pod が叩く 2 つは in-cluster）', () => {
  // IADR-0328 / #780。揃えるとローカル CA を wiki-js コンテナへ配る必要が出る。
  // 逆に in-cluster へ揃えるとブラウザが解決できず、issuer を in-cluster にすると
  // id_token の `iss` 突合が落ちる。**役割で分ける**ことがこの設定の要点である。
  for (const k of ['authorizationURL', 'issuer', 'logoutURL']) {
    const m = new RegExp(`\\\\"${k}\\\\":\\\\"\\$\\{OIDC_BROWSER\\}`).test(WIKIJS_BOOTSTRAP);
    assert.ok(m, `${k} がブラウザ側（エッジ host）を向いていない`);
  }
  for (const k of ['tokenURL', 'userInfoURL']) {
    const m = new RegExp(`\\\\"${k}\\\\":\\\\"\\$\\{OIDC_SERVER\\}`).test(WIKIJS_BOOTSTRAP);
    assert.ok(m, `${k} が in-cluster を向いていない（wiki-js pod がサーバ側で叩く 2 つ）`);
  }
});

ok('#1127: WIKIJS_OIDC=1 のとき wikijs-oidc の Secret が供給され、eso_wait が待つ', () => {
  // ESO 経路: ExternalSecret を apply し、**同期を待つ**。待つ理由は rollout ではなく、
  // up の後段で走る bootstrap の段 8 がこの Secret を読むためである（未同期だと段 8 は
  // 「secret を取得できない」で何もせず、OIDC ログインが入らないまま up が緑で終わる）。
  const eso = runUp({ VAULT: '1', ESO: '1', WIKIJS_OIDC: '1' });
  assert.ok(anyLineHas(eso.lines, 'deploy/local/vault/eso/externalsecret-wikijs-oidc.yaml'),
    'externalsecret-wikijs-oidc.yaml が apply されない');
  assert.ok(eso.lines.some((l) => l.includes('wait') && l.includes('externalsecret/wikijs-oidc')),
    'eso_wait が wikijs-oidc を待っていない（段 8 が読む前に同期が終わっている保証が無い）');
  // 手動経路（ESO 未設定）: apply_secret で同名 Secret を作る。
  const manual = runUp({ WIKIJS_OIDC: '1' });
  assert.ok(manual.lines.some((l) => l.includes('create secret generic wikijs-oidc')),
    'ESO 未設定のとき wikijs-oidc Secret が作られない（唯一の供給元が無くなる）');
  // 既定オフでは 1 つも現れない（上の OPTIN_TOKENS 検査と対）。
  assert.ok(!anyLineHas(DEFAULT.lines, 'wikijs-oidc'), '既定オフなのに wikijs-oidc が現れた');
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

// NFR, ADR-0027 (#1022): rabbitmq-app（ブローカのパスワード・app 側）も postgres-app と同じ対にする。
//
// 🔴 **これは #1012 が事後に踏んだ欠陥の再発防止である**（IADR-0286 の 2026-08-28 追記）。
// #1022 は appsettings.json から RabbitMq:ConnectionString を撤去し、helm の deployment.yaml が
// rabbitmq-app を **非 optional** で参照するようにした。手動 apply を `ESO != 1` ブロックへ入れた以上、
// **対応する ExternalSecret を必ず置く** —— 置き忘れると ESO=1 で供給元が 1 つも無くなり、
// RabbitMQ を使う 7 サービス（document / datasource / conversion / ingestion / retrieval / wiki / graph）が
// `CreateContainerConfigError` で起動しない。
//
// **横断の機械検査は依然として無い**（上の #1012 のコメントが 1 回目の記録）。同型が**もう一度**起きたら、
// `ESO != 1` ブロック内の apply_secret を走査して `externalsecret-<name>.yaml` の実在と apply を突合する
// 検査へ一般化すること。本件は「先例に倣って対を置いた」ケースであって、事故の 2 回目ではない。
ok('ESO=1 (#1022): rabbitmq-app の ExternalSecret を apply・手動 apply はスキップ', () => {
  const res = runUp({ VAULT: '1', ESO: '1' });
  assert.ok(
    anyLineHas(res.lines, 'deploy/local/vault/eso/externalsecret-rabbitmq-app.yaml'),
    'externalsecret-rabbitmq-app.yaml が apply されない（ESO=1 で rabbitmq-app の供給元が無くなる）',
  );
  assert.ok(
    !anyLineHas(res.lines, 'create secret generic rabbitmq-app'),
    'ESO=1 なのに rabbitmq-app を手動 apply している（二重所有）',
  );
});

// 回帰: 既定（ESO 未設定）は rabbitmq-app を手動 apply する。
ok('既定 (#1022): rabbitmq-app を手動 apply する（ESO 未設定）', () => {
  assert.ok(
    anyLineHas(DEFAULT.lines, 'create secret generic rabbitmq-app'),
    'rabbitmq-app の手動 apply が無い（ESO 未設定では唯一の供給元）',
  );
});

// FR-05, FR-09, SC-17, IADR-0301/IADR-0329 (#1101): identity-admin-oidc（SC-17 の変更を Keycloak
// Admin REST へ反映する機密クライアントの secret）も postgres-app / rabbitmq-app と同じ対にする。
//
// 🔴 **#1101 は「配備が偽の身元プロバイダのまま動いていた」欠陥である。** 実プロバイダへ移した以上、
// helm の deployment.yaml はこの Secret を **非 optional** な secretKeyRef で参照する ——
// 手動 apply を `ESO != 1` ブロックへ入れたなら、**対応する ExternalSecret を必ず置く**。
// 置き忘れると ESO=1 で供給元が 1 つも無くなり、authorization-service が
// `CreateContainerConfigError` で起動しない（#1012 / #1022 と同型）。
//
// **横断の機械検査は依然として無い**（上の #1012 のコメントが 1 回目の記録）。本件は
// 「先例に倣って対を置いた」ケースであって、事故の 2 回目ではない。
ok('ESO=1 (#1101): identity-admin-oidc の ExternalSecret を apply・手動 apply はスキップ', () => {
  const res = runUp({ VAULT: '1', ESO: '1' });
  assert.ok(
    anyLineHas(res.lines, 'deploy/local/vault/eso/externalsecret-identity-admin-oidc.yaml'),
    'externalsecret-identity-admin-oidc.yaml が apply されない（ESO=1 で供給元が無くなる）',
  );
  assert.ok(
    !anyLineHas(res.lines, 'create secret generic identity-admin-oidc'),
    'ESO=1 なのに identity-admin-oidc を手動 apply している（二重所有）',
  );
});

// 回帰: 既定（ESO 未設定）は identity-admin-oidc を手動 apply する。
// **陽性対照つき**: ExternalSecret を apply しないことも併せて見る（片方だけだと
// 「常に apply する / 常にしない」実装と区別がつかない）。
ok('既定 (#1101): identity-admin-oidc を手動 apply する（ESO 未設定）', () => {
  assert.ok(
    anyLineHas(DEFAULT.lines, 'create secret generic identity-admin-oidc'),
    'identity-admin-oidc の手動 apply が無い（ESO 未設定では唯一の供給元）',
  );
  assert.ok(
    !anyLineHas(DEFAULT.lines, 'externalsecret-identity-admin-oidc.yaml'),
    'ESO 未設定なのに ExternalSecret を apply した',
  );
});

// SC-15, FR-22, ADR-0026/ADR-0045, IADR-0261/IADR-0332 (#1102): **起動器から一度も参照されない
// ExternalSecret マニフェストが存在しないこと。**
//
// 🔴 これが 2 回目である。上の #1012 / #1022 / #1101 のコメントが「同型がもう一度起きたら一般化せよ」と
// 書いたのは *手動 apply → ExternalSecret* の向きだが、**#1102 で実際に起きたのは逆向き** ——
// `externalsecret-keycloak-smtp.yaml` は 2026-08-23 に置かれてから 8 日間、**`k8s-local-up.sh` に
// 1 度も現れなかった**。定義も Vault の seed も在り、`bootstrap.sh` の案内文は
// `keycloak-smtp` を確認対象として案内していたのに、**その Secret は作られなかった**（必ず NotFound）。
// 個別の secret ごとに対を置く上の検査群は、**そもそも誰も対を置き忘れた名前**を捕まえられない。
//
// ここが持つ不変条件は**向きが逆で、列挙を持たない**:
//   「`deploy/local/vault/eso/externalsecret-*.yaml` のすべてが、いずれかのゲート組み合わせで apply される」
// 新しい ExternalSecret を足した人は、`k8s-local-up.sh` へ apply を書くまでこの検査で止まる。
//
// - **母集合はディレクトリの実体**（このファイルへ名前を書き足す設計にしない。
//   書き足す設計だと、書き忘れが静かに素通りする —— check-secret-injected-options.js と同じ姿勢）。
// - **0 件走査は fail-closed**（glob が壊れて空になったら緑を返さない。#797 の「沈黙の exit 0」）。
// - 突合先は `EMITTED_LINES`（全ゲートを立てた run の和）。ゲート付きで apply されるもの
//   （grafana-oidc / headlamp-oidc）もここには現れる。**「どのゲートでも出ない」だけを落とす。**
ok('#1102: 起動器から一度も apply されない ExternalSecret マニフェストが無い（列挙を持たない）', () => {
  const esoDir = path.join(REPO_ROOT, 'deploy', 'local', 'vault', 'eso');
  const manifests = fs
    .readdirSync(esoDir)
    .filter((f) => /^externalsecret-.*\.yaml$/.test(f))
    .sort();
  assert.ok(
    manifests.length > 0,
    `${esoDir} に externalsecret-*.yaml が 1 件も無い —— 走査が壊れている（0 件を緑にしない）`,
  );
  const orphans = manifests.filter(
    (f) => !anyLineHas(EMITTED_LINES, `deploy/local/vault/eso/${f}`),
  );
  assert.deepStrictEqual(
    orphans,
    [],
    'マニフェストは在るのに scripts/k8s-local-up.sh がどのゲートでも apply しない ExternalSecret:'
      + ` ${orphans.join(', ')} —— 置いただけでは Secret は作られない（#1102 で 8 日間 NotFound だった形）`,
  );
});

// SC-15, IADR-0332 決定 2 (#1102): `eso_wait` が keycloak-smtp を待つ。
//
// **待つ理由が他の secret と違う**（rollout の空振り回避ではない。env でこの Secret を読む Pod は
// 1 つも無い）。待つのは、`up` 直後に運用者が案内文どおり
// `kubectl -n platform-infra get externalsecret,secret keycloak-smtp` を打ったときと、runbook の
// kcadm 手順へ進むときに、**未同期の NotFound を踏まない**ためである —— 未同期の NotFound は
// 「配線が無い」ときと**同じ見え方**をするので、本 issue の欠陥そのものと区別がつかない。
// 理由が違うぶん「rollout 対象に無いのだから待ちも要らない」と後任に削られやすい。ここで固定する。
ok('#1102: eso_wait が platform-infra の keycloak-smtp を待つ（rollout ではなく案内の実行可能性のため）', () => {
  assert.ok(
    anyLineHas(EMITTED_LINES, 'wait --for=condition=Ready externalsecret/keycloak-smtp'),
    'ESO=1 で keycloak-smtp の SecretSynced 待ちが発行されていない'
      + ' —— 案内文と runbook が `up` 直後に NotFound を返しうる（infra_sync を確認せよ）',
  );
});

// NFR (#1022): ブローカ自身の資格情報は **利用者名も** Secret 由来である。
// deploy/local/infra/rabbitmq.yaml は RABBITMQ_DEFAULT_USER を `secretKeyRef: rabbitmq/username` で
// **非 optional** に参照するので、username を作らないとブローカ Pod が起動しない。
// 基盤 secret は bootstrap 必須のため **ESO=1 でも手動 apply をスキップしない**（PR-4/IADR-0099）。
ok('#1022: 基盤 secret rabbitmq は username と password の両方を作る（ESO=1 でもスキップしない）', () => {
  for (const lines of [DEFAULT.lines, runUp({ VAULT: '1', ESO: '1' }).lines]) {
    const line = lines.find((l) => l.includes('create secret generic rabbitmq ')
      || /create secret generic rabbitmq$/.test(l.trim())
      || /create secret generic rabbitmq\s/.test(l));
    assert.ok(line, 'rabbitmq の手動 apply が無い（infra rollout がブロックされる）');
    assert.ok(line.includes('--from-literal=username='), 'rabbitmq Secret に username が無い');
    assert.ok(line.includes('--from-literal=password='), 'rabbitmq Secret に password が無い');
  }
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

const TRAEFIK_YAML = fs.readFileSync(TRAEFIK_MANIFEST, 'utf8');
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


// --- IADR-0313 (#992 案 2): 決定的ローカル埋め込み（LOCALEMBED=1） ------------------------
//
// 🔴 この配線が壊れると「検索の命中」を測る門（integration-stack.yml の SEARCH_HITS=1）が
// **原理的に落ちる**。落ちたときに「検索が壊れた」と読まれると、本当の退行の捜索が余計に長くなる。
// したがって「既定で現れない」「立てたら 1 つだけ現れる」「3 サービスが揃っている」
// 「チャートと appsettings が一致している」を静的に固定する。

const LOCALEMBED_SET = '--set embedding.deterministicLocal.enabled=true';
const HELM_UPGRADE_RE = /helm upgrade --install msp deploy\/helm\/microservices-platform.*/;

ok('LOCALEMBED 未設定: helm upgrade の引数に --set が 1 つも足されない', () => {
  const line = DEFAULT.lines.find((l) => HELM_UPGRADE_RE.test(l));
  assert.ok(line, 'helm upgrade --install msp の行が無い');
  assert.ok(
    !line.includes('--set'),
    `既定なのに --set が付いている（バイト等価が崩れている）: ${line}`,
  );
});

ok('LOCALEMBED=1: helm upgrade へ deterministicLocal の --set が 1 つだけ足される', () => {
  const line = runUp({ LOCALEMBED: '1' }).lines.find((l) => HELM_UPGRADE_RE.test(l));
  assert.ok(line, 'helm upgrade --install msp の行が無い');
  assert.ok(line.includes(LOCALEMBED_SET), `--set が足されていない: ${line}`);
  // 他の --set（ISTIO の mesh.*）まで一緒に付くと、opt-in の独立性が崩れている。
  assert.strictEqual(
    (line.match(/--set /g) || []).length,
    1,
    `LOCALEMBED=1 だけで --set が複数付いている: ${line}`,
  );
});

const CHART_VALUES = readAt(REPO_ROOT, 'deploy', 'helm', 'microservices-platform', 'values.yaml');
const CHART_DEPLOYMENT = readAt(
  REPO_ROOT, 'deploy', 'helm', 'microservices-platform', 'templates', 'deployment.yaml');
const LLM_APPSETTINGS = JSON.parse(
  readAt(REPO_ROOT, 'src', 'platform', 'backend', 'Services', 'LlmGateway', 'appsettings.json'));
const DET_ENDPOINT = LLM_APPSETTINGS.Embedding.Routing.Endpoints
  .find((e) => e.Provider === 'deterministic-embedding');

ok('IADR-0313: 決定的エンドポイントは appsettings で既定無効・ティアA である', () => {
  assert.ok(DET_ENDPOINT, 'appsettings.json に deterministic-embedding のエンドポイントが無い');
  assert.strictEqual(DET_ENDPOINT.Enabled, false,
    '既定で有効になっている（本番へ紛れ込むと検索品質が無言で落ちる）');
  assert.strictEqual(DET_ENDPOINT.Tier, 'A',
    'ティアA 以外に置かれている（社外送信なしという性質と食い違う）');
});

ok('IADR-0313: 配列 index が 2 である（チャートの Endpoints__2 上書きが指す先）', () => {
  const idx = LLM_APPSETTINGS.Embedding.Routing.Endpoints.indexOf(DET_ENDPOINT);
  assert.strictEqual(idx, 2,
    `deterministic-local の index が ${idx}（チャートは Endpoints__2 を上書きする）。` +
      '並び替えたなら deployment.yaml の index も直すこと',
  );
});

ok('IADR-0313: チャートのコレクション名・次元が appsettings と一致する', () => {
  const collection = (/^\s{4}collection:\s*(\S+)\s*$/m.exec(CHART_VALUES) || [])[1];
  const dimensions = (/^\s{4}dimensions:\s*(\d+)\s*$/m.exec(CHART_VALUES) || [])[1];
  assert.strictEqual(collection, DET_ENDPOINT.Collection,
    'values.yaml の collection が appsettings と食い違う（索引先と検索先が割れる）');
  assert.strictEqual(Number(dimensions), DET_ENDPOINT.Dimensions,
    'values.yaml の dimensions が appsettings と食い違う（次元不整合で索引が fail-closed する）');
});

ok('IADR-0313: 3 サービス（llmgateway / ingestion / retrieval）が揃って配線されている', () => {
  // 1 つでも欠けると「索引はされるが検索は別のコレクションを見る」＝ 0 件で静かに落ちる。
  const block = CHART_DEPLOYMENT.slice(
    CHART_DEPLOYMENT.indexOf('$det := $.Values.embedding.deterministicLocal'));
  assert.ok(block.length > 0, 'deterministicLocal のブロックが deployment.yaml に無い');
  for (const [name, env] of [
    ['llmgateway', 'Embedding__Routing__Endpoints__2__Enabled'],
    ['ingestion', 'Embedding__Collections__2__Name'],
    ['retrieval', 'Qdrant__CollectionName'],
  ]) {
    const at = block.indexOf(`eq $name "${name}"`);
    assert.ok(at !== -1, `${name} の分岐が無い`);
    assert.ok(block.slice(at, at + 900).includes(env), `${name} に ${env} が無い`);
  }
});

ok('IADR-0313: 既定 values は enabled: false（本番像は現状維持）', () => {
  const at = CHART_VALUES.indexOf('deterministicLocal:');
  assert.ok(at !== -1, 'values.yaml に deterministicLocal が無い');
  assert.match(CHART_VALUES.slice(at, at + 200), /enabled:\s*false/,
    '既定が false でない（使い捨てスタック専用の opt-in が既定 ON になっている）');
});

// ---------------------------------------------------------------------------
// #782 / ADR-0021: エッジを Istio Ingress Gateway へ移す overlay の静的検査。
//
// ここで固定するのは **STRICT が成立するための前提**だけである。実クラスタでの疎通は
// .ai-context/adr/IADR-0317_*.md が実測で持つ（この検査は「壊すと落ちる」門であって証拠ではない）。
// ---------------------------------------------------------------------------
const EDGE_ISTIO_DIR = path.join(REPO_ROOT, 'deploy', 'local', 'edge-istio');
const EDGE_ISTIO_KUST = fs.readFileSync(path.join(EDGE_ISTIO_DIR, 'kustomization.yaml'), 'utf8');
const EDGE_ISTIO_GW = fs.readFileSync(path.join(EDGE_ISTIO_DIR, 'gateway.yaml'), 'utf8');
const EDGE_ISTIO_VS_APP = fs.readFileSync(path.join(EDGE_ISTIO_DIR, 'virtualservice-app.yaml'), 'utf8');
const EDGE_ISTIO_VS_ADMIN = fs.readFileSync(
  path.join(EDGE_ISTIO_DIR, 'virtualservice-admin.yaml'),
  'utf8',
);
const EDGE_ISTIO_CERT = fs.readFileSync(
  path.join(EDGE_ISTIO_DIR, 'tls', 'edge-certificate-istio.yaml'),
  'utf8',
);
const ISTIO_EDGE_UP = fs.readFileSync(path.join(REPO_ROOT, 'scripts', 'istio-edge-up.sh'), 'utf8');
const ISTIO_EDGE_DOWN = fs.readFileSync(
  path.join(REPO_ROOT, 'scripts', 'istio-edge-down.sh'),
  'utf8',
);

ok('#782: istio-system の葉証明書の dnsNames が既存 Certificate と一致する（SNI がずれない）', () => {
  const istioNames = listValues(EDGE_ISTIO_CERT, 'dnsNames');
  const edgeNames = listValues(EDGE_CERT_YAML, 'dnsNames');
  assert.ok(istioNames.length > 0, 'istio-system の Certificate に dnsNames が無い');
  assert.deepStrictEqual(
    istioNames,
    edgeNames,
    'istio-system の dnsNames が edge-certificate.yaml とずれている（Gateway の TLS が host 照合で落ちる）',
  );
  assert.ok(/namespace:\s*istio-system/.test(EDGE_ISTIO_CERT), 'Certificate が istio-system に無い');
  assert.ok(
    /secretName:\s*edge-tls/.test(EDGE_ISTIO_CERT),
    'secretName が edge-tls でない（ADR-0023「名前の安定」/ Gateway の credentialName と一致しない）',
  );
});

ok('#782: edge-istio kustomization は tls/ と traefik-service-off を含まない（順序を表現できないため）', () => {
  assert.ok(!/^\s*-\s*tls\/?\s*$/m.test(EDGE_ISTIO_KUST), 'kustomization が tls/ を含んでいる');
  assert.ok(
    !/^\s*-\s*traefik-service-off/m.test(EDGE_ISTIO_KUST),
    'kustomization が traefik-service-off.yaml を含んでいる（Traefik の明け渡しは Gateway より先でなければならない）',
  );
});

ok('#782: Gateway は istio-system に居て edge-tls で終端し、80 は 443 へリダイレクトする', () => {
  assert.ok(/namespace:\s*istio-system/.test(EDGE_ISTIO_GW), 'Gateway が istio-system に無い');
  assert.ok(
    /credentialName:\s*edge-tls/.test(EDGE_ISTIO_GW),
    'credentialName が edge-tls でない（Gateway は同 namespace の Secret しか読めない）',
  );
  assert.ok(
    /httpsRedirect:\s*true/.test(EDGE_ISTIO_GW),
    'port 80 の httpsRedirect が無い（NFR-11「平文 HTTP を残さない」）',
  );
  assert.ok(/number:\s*50000/.test(EDGE_ISTIO_GW), 'admin(50000) の server が無い');
});

ok('#782: メッシュ内の 4 サービスがすべて Gateway 経由になっている（STRICT で落ちる側）', () => {
  const routed = EDGE_ISTIO_VS_APP + EDGE_ISTIO_VS_ADMIN;
  for (const svc of ['frontend-service', 'bff-service', 'minio', 'wiki-js']) {
    assert.ok(
      new RegExp(`host:\\s*${svc}\\.microservices-platform\\.svc\\.cluster\\.local`).test(routed),
      `${svc} への VirtualService が無い（Traefik のままだと STRICT で 502 になる）`,
    );
  }
});

ok('#782: up は「Traefik を明け渡す → Gateway を立てる」の順で、down はその逆順である', () => {
  const offAt = ISTIO_EDGE_UP.indexOf('kubectl apply -f deploy/local/edge-istio/traefik-service-off.yaml');
  const gwAt = ISTIO_EDGE_UP.indexOf('istio/gateway');
  assert.ok(offAt > 0 && gwAt > 0, 'istio-edge-up.sh に明け渡し/Gateway 導入が無い');
  assert.ok(
    offAt < gwAt,
    'Gateway の導入が Traefik の明け渡しより先にある（hostPort が衝突してどちらの入口も立たない）',
  );
  const uninstallAt = ISTIO_EDGE_DOWN.indexOf('helm uninstall istio-ingressgateway');
  const restoreAt = ISTIO_EDGE_DOWN.indexOf('kubectl apply -f deploy/local/edge/traefik-entrypoint.yaml');
  assert.ok(uninstallAt > 0 && restoreAt > 0, 'istio-edge-down.sh に撤去/復旧が無い');
  assert.ok(uninstallAt < restoreAt, '切り戻しで Traefik の復旧が Gateway の撤去より先にある（同上）');
});

// ---------------------------------------------------------------------------
// FR-20 / #1154 / IADR-0348: Obsidian プラグインの同期プロトコルをエッジから外へ出す 1 本。
//
// ここで固定するのは **経路が効くための前提**と**露出面が広がっていないこと**である。実クラスタでの
// 200 / 401 と「sync 以外は API へ届かない」ことは IADR-0348 が実測で持つ
// （この検査は「壊すと落ちる門」であって証拠ではない）。
// ---------------------------------------------------------------------------
const CHART_EDGE = readAt(REPO_ROOT, 'deploy', 'helm', 'microservices-platform', 'templates', 'edge.yaml');
const CHART_NETPOL = readAt(
  REPO_ROOT, 'deploy', 'helm', 'microservices-platform', 'templates', 'networkpolicy.yaml',
);

/** コメント行を落とす（ルートの有無をコメントの散文で誤判定しないため）。 */
const stripYamlComments = (src) =>
  src.split('\n').filter((l) => !/^\s*#/.test(l)).join('\n');

const EDGE_ISTIO_VS_APP_CODE = stripYamlComments(EDGE_ISTIO_VS_APP);
const CHART_EDGE_CODE = stripYamlComments(CHART_EDGE);

ok('#1154: 同期プロトコルの経路が overlay と本番チャートの両方にあり、document-service へ振る', () => {
  assert.match(
    EDGE_ISTIO_VS_APP_CODE,
    /prefix:\s*\/private-notes\/sync\/[\s\S]{0,200}host:\s*document-service\.microservices-platform\.svc\.cluster\.local/,
    'ローカル overlay に /private-notes/sync/ → document-service の route が無い（配備済みクラスタへプラグインが到達できない。#1154）',
  );
  assert.match(
    CHART_EDGE_CODE,
    /prefix:\s*\/private-notes\/sync\/[\s\S]{0,200}\.Values\.edge\.privateNotesSync\.service/,
    '本番チャートに /private-notes/sync/ → edge.privateNotesSync.service の route が無い',
  );
  // rewrite を張らない（公開パス ＝ 契約パス。/bff と同じ規律）。
  for (const [name, src] of [['overlay', EDGE_ISTIO_VS_APP_CODE], ['chart', CHART_EDGE_CODE]]) {
    assert.ok(!/\brewrite:/.test(src), `${name} が rewrite を張っている（公開パスと契約パスがずれる）`);
  }
});

ok('#1154: 同期の route は catch-all（prefix: /）より前にある（Istio のルートは先勝ち）', () => {
  for (const [name, src] of [['overlay', EDGE_ISTIO_VS_APP_CODE], ['chart', CHART_EDGE_CODE]]) {
    const syncAt = src.indexOf('prefix: /private-notes/sync/');
    // catch-all は flow 記法（`- uri: { prefix: / }`）で書かれている。block 記法も拾えるようにする。
    const catchAllAt = src.search(/prefix:\s*\/\s*(\}|$)/m);
    assert.ok(syncAt > 0, `${name} に同期の route が無い`);
    assert.ok(catchAllAt > 0, `${name} に catch-all が無い`);
    assert.ok(
      syncAt < catchAllAt,
      `${name} の同期 route が catch-all より後ろにある（SPA に吸われて API に届かない）`,
    );
  }
});

ok('#1154: 露出面は /private-notes/sync/ だけ（sync 以外の JWT 経路と /documents を外へ出していない）', () => {
  // 陰性の静的対照。実クラスタの挙動は実測が持つが、**マニフェストに route を書いた瞬間に落ちる**門を置く。
  for (const [name, src] of [['overlay', EDGE_ISTIO_VS_APP_CODE], ['chart', CHART_EDGE_CODE]]) {
    for (const leaked of ['/private-notes/devices', '/private-notes/quotas', '/documents']) {
      assert.ok(
        !src.includes(leaked),
        `${name} が ${leaked} をエッジへ出している（08_data-egress-policy 許容条件 2 のスコープ限定が経路で破れる）`,
      );
    }
    // 末尾スラッシュを落とすと /private-notes 全体（一覧・端末登録）が外へ出る。
    assert.ok(
      !/(prefix|exact):\s*\/private-notes\s*(\}|$)/m.test(src),
      `${name} が /private-notes そのものを route している（sync 以外まで外へ出る）`,
    );
  }
});

ok('#1154: 本番像の既定は opt-in（false）で、NetworkPolicy の穴も同じ条件でしか開かない', () => {
  const at = CHART_VALUES.indexOf('privateNotesSync:');
  assert.ok(at !== -1, 'values.yaml に edge.privateNotesSync が無い');
  assert.match(
    CHART_VALUES.slice(at, at + 200),
    /enabled:\s*false/,
    '既定が false でない（内部サービスの端点を既定で外へ出している。fail-safe は「気付ける方向」へ倒す）',
  );
  assert.match(
    stripYamlComments(CHART_NETPOL),
    /if\s+\.Values\.edge\.privateNotesSync\.enabled[\s\S]{0,900}name:\s*allow-edge-ingress-to-document-service/,
    'NetworkPolicy の穴が edge.privateNotesSync.enabled で条件付けられていない',
  );
  assert.match(
    CHART_NETPOL,
    /app:\s*\{\{\s*\.Values\.edge\.privateNotesSync\.service\s*\}\}/,
    'NetworkPolicy の podSelector が route の行き先と同じ単一情報源から描画されていない（ドリフトする）',
  );
});

ok('#782: 切り戻しは mTLS を先に緩める（入口だけ戻して 502 のままにしない）', () => {
  const permissiveAt = ISTIO_EDGE_DOWN.indexOf('"PERMISSIVE"');
  const restoreAt = ISTIO_EDGE_DOWN.indexOf('kubectl apply -k deploy/local/edge');
  assert.ok(permissiveAt > 0 && restoreAt > 0, 'istio-edge-down.sh の段が読めない');
  assert.ok(permissiveAt < restoreAt, 'PERMISSIVE へ戻すのが Traefik の復旧より後になっている');
});

// ---------------------------------------------------------------------------
// #1159 / IADR-0377: 稼働の mTLS モードを書く口は helm ただ 1 つ（kubectl patch を禁じる）
//
// 🔴 これは「行儀の問題」ではない。Helm 4 はサーバサイド apply なので、`kubectl patch` は
//   `.spec.mtls.mode` の field manager を奪い、**以後の `helm upgrade` が conflict で恒久的に失敗する**
//   （2026-09-04 実測。`--take-ownership` も `--force` も効かない）。つまり 1 回の patch で
//   `k8s-local-up.sh` が [6/7] で止まるようになる。禁止の根拠は収束性そのものである。
// ---------------------------------------------------------------------------

const MESH_MTLS_LIB_REL = 'scripts/lib/mesh-mtls-mode.sh';
const MESH_MTLS_LIB = fs.readFileSync(path.join(REPO_ROOT, MESH_MTLS_LIB_REL), 'utf8');
const UP_SH = fs.readFileSync(path.join(REPO_ROOT, 'scripts', 'k8s-local-up.sh'), 'utf8');

ok('#1159: 追跡下のどのファイルも PeerAuthentication を kubectl patch しない（母集合を走査して確かめる）', () => {
  // 記憶で 2 本挙げない。**誤りの側の文字列で全ファイルを引く**（規則 9）。
  // 🔴 `--others` を併せる —— 追跡前の新しいスクリプトが母集合から漏れると、
  //   「新しく足した違反」だけが素通りする（規則 10）。
  const tracked = spawnSync('git', ['ls-files', '--cached', '--others', '--exclude-standard'], {
    cwd: REPO_ROOT, encoding: 'utf8',
  });
  assert.strictEqual(tracked.status, 0, 'git ls-files が失敗した（母集合を引けていない）');
  const files = String(tracked.stdout).split('\n')
    .filter((f) => f && !f.startsWith('src/ai-stock-trading')); // submodule は別リポジトリ
  assert.ok(files.length > 100, `母集合が小さすぎる（${files.length} 件）。走査が壊れている`);
  const offenders = [];
  for (const rel of files) {
    let src;
    try {
      src = fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');
    } catch {
      continue; // バイナリ / 読めないものは飛ばす
    }
    // 凍結記録（実測の引用として patch コマンドを含む）は対象外。live な装備だけを縛る。
    if (rel.startsWith('.ai-context/')) continue;
    if (/kubectl[^\n]*\bpatch\b[^\n]*peerauthentication/i.test(src)) offenders.push(rel);
  }
  assert.deepStrictEqual(
    offenders, [],
    'PeerAuthentication を kubectl patch している。helm から field manager を奪い、'
    + `以後の helm upgrade を恒久的に壊す（#1159）。${MESH_MTLS_LIB_REL} の set_mesh_mtls_mode を使うこと`,
  );
});

ok('#1159: mTLS モードを書く口は helm 経由の 1 本で、両スクリプトがそれを source して使う', () => {
  assert.match(MESH_MTLS_LIB, /helm upgrade[^\n]*--set "mesh\.mtlsMode=\$mode"/,
    'set_mesh_mtls_mode が helm 経由で書いていない');
  for (const [name, src] of [['istio-edge-up.sh', ISTIO_EDGE_UP], ['istio-edge-down.sh', ISTIO_EDGE_DOWN]]) {
    assert.ok(src.includes('lib/mesh-mtls-mode.sh'), `${name} が ${MESH_MTLS_LIB_REL} を source していない`);
    assert.match(src, /set_mesh_mtls_mode\s+"(STRICT|PERMISSIVE)"/, `${name} が set_mesh_mtls_mode を呼んでいない`);
  }
});

ok('#1159: リリースが無ければ set_mesh_mtls_mode は何もせず 0 で返る（切り戻しの冪等性）', () => {
  const bodyAt = MESH_MTLS_LIB.indexOf('set_mesh_mtls_mode() {');
  assert.ok(bodyAt > 0, 'set_mesh_mtls_mode の定義が読めない');
  const body = MESH_MTLS_LIB.slice(bodyAt); // 冒頭の解説コメントを母集合に混ぜない
  const at = body.indexOf('helm status');
  const setAt = body.indexOf('helm upgrade');
  assert.ok(at > 0 && setAt > at, 'リリース存在確認が helm upgrade より後にある（未導入クラスタで落ちる）');
  assert.match(body.slice(at, setAt), /return 0/, 'リリース不在時に 0 で返っていない');
});

ok('#1159: 未知のモードは受け付けない（typo が静かに DISABLE 相当へ落ちない）', () => {
  assert.match(MESH_MTLS_LIB, /STRICT \| PERMISSIVE \| DISABLE\)/, 'モードの値域が閉じていない');
  assert.match(MESH_MTLS_LIB, /未知のモード/, '未知のモードを非 0 で弾いていない');
});

ok('#1159: G12 の前提 — テンプレートは mode を values から描画し、開ける口だけを PERMISSIVE 直書きにする', () => {
  const tpl = readAt(REPO_ROOT, 'deploy', 'helm', 'microservices-platform', 'templates', 'istio-mtls.yaml');
  assert.match(tpl, /mode:\s*\{\{\s*\.Values\.mesh\.mtlsMode\s*\}\}/,
    'PeerAuthentication の mode が values を参照していない（G12 が値を values から採れなくなる）');
  assert.match(tpl, /portLevelMtls:[\s\S]{0,200}mode:\s*PERMISSIVE/,
    'portLevelMtls の口が PERMISSIVE 直書きでない（バックチャネルの 1 本が塞がる）');
});

ok('#1159: ArgoCD の許可種別に AuthorizationPolicy が在る（本番同期が種別欠落で止まらない）', () => {
  const proj = readAt(REPO_ROOT, 'deploy', 'argocd', 'appproject.yaml');
  for (const kind of ['PeerAuthentication', 'DestinationRule', 'AuthorizationPolicy']) {
    assert.ok(new RegExp(`kind:\\s*${kind}\\b`).test(proj), `appproject.yaml が ${kind} を許可していない`);
  }
});

ok('#1159: ISTIO=1 単独なら [6/7] が要求どおりのモードを宣言する（昇格の口が無いため）', () => {
  const line = runUp({ ISTIO: '1', ISTIO_MTLS_MODE: 'STRICT' }).lines.find((l) => HELM_UPGRADE_RE.test(l));
  assert.ok(line, 'helm upgrade --install msp の行が無い');
  assert.ok(line.includes('--set mesh.mtlsMode=STRICT'), `STRICT が宣言されていない: ${line}`);
});

ok('#1159: ISTIO=1 かつ LOCALEDGE=1 なら [6/7] は PERMISSIVE を宣言する（入口はまだ Traefik である）', () => {
  const res = runUp({ ISTIO: '1', LOCALEDGE: '1', ISTIO_MTLS_MODE: 'STRICT' });
  const line = res.lines.find((l) => HELM_UPGRADE_RE.test(l));
  assert.ok(line, 'helm upgrade --install msp の行が無い');
  assert.ok(
    line.includes('--set mesh.mtlsMode=PERMISSIVE'),
    `入口がまだ Traefik の段で STRICT を宣言している（IADR-0307 決定 4 の段取りが崩れている）: ${line}`,
  );
});

ok('#1159: STRICT への昇格は入口を Envoy へ移した後に来る（順序は 2 本のスクリプトを跨いで固定する）', () => {
  // 🔴 ハーネスでは測れない —— istio-edge-up.sh は「Traefik の Service が消えるのを待つ」段で
  //   スタブ相手には永久に成立せず、そこで非 0 終了する。順序は**テキストで**固定する。
  const upAt = UP_SH.indexOf('scripts/istio-edge-up.sh');
  const installAt = UP_SH.indexOf('helm upgrade --install msp');
  assert.ok(installAt > 0 && upAt > installAt, 'k8s-local-up.sh で入口の移設が [6/7] より前に来ている');
  const gwAt = ISTIO_EDGE_UP.indexOf('istio/gateway');
  const promoteAt = ISTIO_EDGE_UP.indexOf('set_mesh_mtls_mode "STRICT"');
  assert.ok(gwAt > 0 && promoteAt > 0, 'istio-edge-up.sh の段が読めない');
  assert.ok(promoteAt > gwAt, 'STRICT への昇格が Gateway の導入より前に来ている（入口が 502 になる）');
});

ok('#1159: ISTIO 未設定なら mesh.* の --set が 1 つも足されない（既定のバイト等価）', () => {
  const line = DEFAULT.lines.find((l) => HELM_UPGRADE_RE.test(l));
  assert.ok(!line.includes('mesh.'), `既定なのに mesh.* が付いている: ${line}`);
});

process.stdout.write(`\n✓ ${passed} tests passed\n`);
