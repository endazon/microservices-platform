#!/usr/bin/env node
/**
 * NFR / #783（#442 子 5）後半: **統合スタックが実際に起きていること**を機械で検査する。
 *
 * 計画 ADR-0007（CI/CD）・ADR-0021（エッジ・実行基盤）。
 *
 * ## なぜ要るか —— `k8s-local-up.sh` の EXIT=0 は readiness の証明にならない
 *
 * `scripts/k8s-local-up.sh` は `[6/7]` で `helm upgrade --install` を **`--wait` 無しで**呼ぶ。
 * したがって**アプリ pod の起動を待たずに EXIT=0 で戻る**。実測（GitHub ホストランナー・
 * run 32554340800）では、戻った時点で `bff-service` は `0/1 Running`（liveness probe 失敗中）、
 * `aianalysis-service` / `minio` / `wiki-js` も `0/1` だった。その状態で導線を叩くと 502 が並ぶ。
 *
 * 🔴 **CI ジョブがこの検査を持たなければ、「立ち上がっていないスタックに緑を返すジョブ」になる。**
 * #783 が最も避けたい形であり、しかも**無音で起きる**（up は EXIT=0 のまま）。
 *
 * ## 門（すべて fail-closed）
 *
 * - **G1 readiness**: 対象 namespace の Deployment がすべて `availableReplicas >= 1` で、
 *   pod が Ready であること。**待つのは呼び出し側（kubectl wait）で、ここは判定だけを行う。**
 * - **G2 0 件で緑にしない**: 走査した Deployment が 0 件なら失敗にする。
 *   🔴 **理由は「`kubectl wait --all` が 0 件のとき成功するから」ではない。** 当初そう書いていたが
 *   **実測は逆で、`error: no matching resources found` で exit 1 になる**（run 32556579646 の
 *   empty-cluster: `WAIT_EXIT_platform-infra=1`）。**誤った根拠だったので書き直した。**
 *   本当の理由は **ゲートが単独で完結する判定でなければならない**ことである。G2 が無いと
 *   `evaluateDeployments([])` は失敗を 1 件も返さないため、**アプリのサービスが 1 つも
 *   デプロイされていない状態でゲートが緑になる**（実測: 同 run の control-pinned では、
 *   infra とエッジが健全なまま `microservices-platform` が空であり、**それを捕まえたのは G2 だけ**だった）。
 *   待つステップは検査ではなく、弱められても消されても誰も気づかない（「沈黙の exit 0」#797 と同型）。
 * - **G3 ツール不在は失敗**: `kubectl` が無ければ失敗。**抜け道の環境変数を置かない。**
 *   （`check-deploy-manifests.js` は `DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS` を持つが、
 *   あちらは「ローカルで部分的に走らせたい」需要がある静的検査である。本検査は
 *   クラスタが在ることが前提で、ツールが無い＝前提が崩れているので逃げ道を作らない。）
 * - **G4 エッジ / issuer**: `keycloak-edge` Ingress が在り、エッジの discovery が返す `issuer` が
 *   **デプロイ済みの `KC_HOSTNAME_URL` ＋ `/realms/<realm>` と文字列として完全一致**すること
 *   （[IADR-0243] の受け入れ基準を CI で固定する）。realm 名は `deploy/keycloak/*-realm.json` から
 *   **走査して**得る（列挙を書かない）。
 * - **G5 admin entrypoint**: **どちらかのエッジ**の Service に `50000` の port が在ること
 *   （既定は `kube-system/traefik` の `admin=50000`。`ISTIO=1` では `istio-system/istio-ingressgateway`
 *   の `https-admin=50000`。#782 / [IADR-0317] で Traefik は Service ごと降りるため、
 *   **Traefik だけを見ると Istio エッジで必ず落ちる**）。
 *   🔴 **これは飾りではない。** k3s v1.30.4 が同梱する traefik chart 25.0.3 では
 *   `deploy/local/edge/traefik-entrypoint.yaml` の `expose: {default: true}`（map 形式・chart 26 以降）が
 *   型不一致で reconcile に失敗し、**`kubectl apply` は成功したまま反映だけが落ちる**（#953）。
 *   #783 は k3s を pin して回避するが、**pin が外れたことに気付ける形**をここに置く。
 * - **G6 pod 側の名前解決**: pod から エッジ host（`keycloak.localhost`）が引けること（[IADR-0227]）。
 *   🔴 **G4（ランナーからの curl）はこれを代替しない。** ランナーは systemd-resolved が `.localhost` を
 *   合成応答で 127.0.0.1 に返すため、**クラスタ内の解決が壊れていても G4 は通る**。非 .NET の
 *   6 ツールは pod から issuer を引くので、G6 が無いと「6 ツールが壊れているのに緑」になる。
 *
 * ## 列挙を持たない
 *
 * namespace は `k8s-local-up.sh` と同じ 2 つ（`platform-infra` / `microservices-platform`）だが、
 * **その中のサービス名は一切書かない。** 書くと次にサービスが増えたとき静かに検査対象から外れる
 * （`check-deploy-manifests.js` 要点 1 と同じ判断。overlay を 6 件と数えて実際は 8 件だった実績がある）。
 */

'use strict';

const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.join(__dirname, '..');

/** `k8s-local-up.sh` が作る 2 つの namespace。サービス名は書かない（列挙を持たない）。 */
const NAMESPACES = ['platform-infra', 'microservices-platform'];

/** エッジの Traefik が居る namespace と Service 名（k3s 管理物）。 */
const TRAEFIK_NS = 'kube-system';
const TRAEFIK_SVC = 'traefik';

/** G5 が要求する entrypoint（[IADR-0091] / [IADR-0220]）。 */
const ADMIN_PORT_NAME = 'admin';
const ADMIN_PORT = 50000;

/** #782 / [IADR-0317]: `ISTIO=1` のとき 3 ポートを持つのは Traefik ではなくこちらである。 */
const ISTIO_GW_NS = 'istio-system';
const ISTIO_GW_SVC = 'istio-ingressgateway';

/** Keycloak の Deployment（issuer の単一情報源 `KC_HOSTNAME_URL` を持つ）。 */
const KEYCLOAK_NS = 'platform-infra';
const KEYCLOAK_DEPLOY = 'keycloak';
const KEYCLOAK_EDGE_INGRESS = 'keycloak-edge';

/** realm 宣言の在り処。realm 名はここから走査して得る。 */
const REALM_DIR = path.join('deploy', 'keycloak');

// ---------------------------------------------------------------- 収集（外部依存）

function hasTool(bin) {
  const probe = spawnSync(process.platform === 'win32' ? 'where' : 'which', [bin], {
    encoding: 'utf8',
  });
  return probe.status === 0;
}

function kubectlJson(args) {
  const r = spawnSync('kubectl', args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  if (r.status !== 0) {
    return { ok: false, error: (r.stderr || r.stdout || '').trim() || `kubectl ${args.join(' ')} が失敗した` };
  }
  try {
    return { ok: true, value: JSON.parse(r.stdout) };
  } catch (e) {
    return { ok: false, error: `kubectl の出力を JSON として読めなかった: ${e.message}` };
  }
}

/**
 * エッジの discovery を取りに行く。TLS はローカル CA の自己署名なので検証しない
 * （`verify-oidc-edge-flow.sh` と同じ判断。dev 専用）。
 */
function fetchDiscovery(url) {
  const r = spawnSync('curl', ['-sk', '--max-time', '30', url], {
    encoding: 'utf8',
    maxBuffer: 8 * 1024 * 1024,
  });
  if (r.status !== 0) return { ok: false, error: `curl が失敗した (exit ${r.status}): ${url}` };
  try {
    return { ok: true, value: JSON.parse(r.stdout) };
  } catch (e) {
    return { ok: false, error: `discovery が JSON ではなかった: ${url} (${e.message})` };
  }
}

// ---------------------------------------------------------------- 判定（純粋関数）

/** `deploy/keycloak/*-realm.json` を走査して realm 名を得る。0 件は失敗（G2 と同型）。 */
function discoverRealms(repoRoot = REPO_ROOT) {
  const dir = path.join(repoRoot, REALM_DIR);
  if (!fs.existsSync(dir)) return [];
  return fs
    .readdirSync(dir)
    .filter((f) => f.endsWith('-realm.json'))
    .map((f) => {
      try {
        return JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')).realm;
      } catch {
        return null;
      }
    })
    .filter((r) => typeof r === 'string' && r.length > 0);
}

/**
 * G1 / G2: Deployment の一覧（`kubectl get deploy -o json` の `items`）を判定する。
 * **0 件は失敗**（G2）。1 件でも available が足りなければ失敗（G1）。
 */
function evaluateDeployments(ns, items) {
  const failures = [];
  if (!Array.isArray(items) || items.length === 0) {
    failures.push(
      `[G2] namespace ${ns} の Deployment が 0 件だった。走査が壊れているか、スタックが起きていない。` +
        `**0 件を緑にしない**（ゲートは単独で完結する判定でなければならない）。`,
    );
    return { failures, total: 0, ready: 0 };
  }
  let ready = 0;
  for (const d of items) {
    const name = (d.metadata && d.metadata.name) || '(名前不明)';
    const desired = d.spec && typeof d.spec.replicas === 'number' ? d.spec.replicas : 1;
    const available = (d.status && d.status.availableReplicas) || 0;
    if (desired === 0) {
      // 意図的に 0 へ絞った Deployment は判定対象外にする（存在しない状態を要求しない）。
      ready += 1;
      continue;
    }
    if (available < desired) {
      failures.push(
        `[G1] ${ns}/${name}: availableReplicas=${available} < spec.replicas=${desired}。` +
          `**up が EXIT=0 でも pod は起きていない**（helm upgrade --install は --wait しない）。`,
      );
    } else {
      ready += 1;
    }
  }
  return { failures, total: items.length, ready };
}

/** G1: pod の Ready 条件を判定する。`Succeeded`（完了 Job）は対象外。 */
function evaluatePods(ns, items) {
  const failures = [];
  for (const p of Array.isArray(items) ? items : []) {
    const name = (p.metadata && p.metadata.name) || '(名前不明)';
    const phase = p.status && p.status.phase;
    if (phase === 'Succeeded') continue;
    const conds = (p.status && p.status.conditions) || [];
    const readyCond = conds.find((c) => c.type === 'Ready');
    if (!readyCond || readyCond.status !== 'True') {
      failures.push(`[G1] ${ns}/${name}: Ready ではない（phase=${phase}, Ready=${readyCond ? readyCond.status : '無し'}）。`);
    }
  }
  return failures;
}

/** admin(50000) を提供しているか（Service 1 つぶんの判定。純関数）。 */
function servesAdminPort(svc) {
  const ports = (svc && svc.spec && svc.spec.ports) || [];
  // Traefik は name='admin'、istio-ingressgateway は name='https-admin' を使う。
  // **名前ではなくポート番号で見る** —— 見たいのは「50000 が生えているか」であって命名規約ではない。
  return ports.some((p) => p.port === ADMIN_PORT);
}

/**
 * G5: admin(50000) の entrypoint が**どちらかのエッジ**に在るか。
 *
 * 🔴 **エッジは 2 通りある**（#782 / [IADR-0317]）。既定は Traefik（`kube-system/traefik`）だが、
 * `ISTIO=1` かつ `LOCALEDGE=1` では **Traefik の Service を落として** `istio-ingressgateway` が
 * 3 ポートすべてを持つ（k3s の ServiceLB は同じ hostPort を 2 つの Service に持たせられない）。
 * **Traefik だけを見ると Istio エッジで必ず失敗する。**
 *
 * 逆に「どちらも無い」は依然として失敗である —— #953（HelmChartConfig の reconcile が
 * 静かに落ちる）を捕まえる門の役割は変わらない。
 */
function evaluateAdminEntrypoint(traefikSvc, istioSvc) {
  if (servesAdminPort(traefikSvc) || servesAdminPort(istioSvc)) return [];
  const describe = (label, svc) => {
    if (!svc) return `${label}=(Service が無い)`;
    const ports = (svc.spec && svc.spec.ports) || [];
    return `${label}=${ports.map((p) => `${p.name}:${p.port}`).join(',') || '(port 0 件)'}`;
  };
  return [
    `[G5] admin(${ADMIN_PORT}) の entrypoint がどちらのエッジにも無い` +
      `（${describe(`${TRAEFIK_NS}/${TRAEFIK_SVC}`, traefikSvc)} / ` +
      `${describe(`${ISTIO_GW_NS}/${ISTIO_GW_SVC}`, istioSvc)}）。` +
      ` **HelmChartConfig の reconcile が落ちても kubectl apply は成功する**（#953）。` +
      ` k3s のバージョンが pin から外れると traefik chart のスキーマが変わり、静かにこの状態になる。` +
      ` Istio エッジ（#782）なら istio-ingressgateway が 50000 を持っているはずである。`,
  ];
}

/**
 * G6: **pod から**エッジ host が引けるか（[IADR-0227] の `coredns-custom`）。
 *
 * 🔴 **ランナーからの curl（G4）はこれを代替しない。** ランナー側は systemd-resolved が
 * `.localhost` を RFC 6761 の合成応答で 127.0.0.1 に返すため、**クラスタ内の名前解決が壊れていても
 * G4 は通ってしまう**。一方、非 .NET の OIDC クライアント（Grafana / ArgoCD / Vault / MinIO /
 * Headlamp / Wiki.js）は [IADR-0086] の metadata/issuer 分離を使えず、**pod から issuer を実際に引く**。
 * つまり G6 が無いと「6 ツールが壊れているのに緑」が起きる。
 */
function evaluatePodDnsOutput(host, stdout) {
  const text = String(stdout || '');
  // busybox nslookup は失敗時に "can't resolve" / "NXDOMAIN" を出す。
  if (/can't resolve|NXDOMAIN|server can't find/i.test(text)) {
    return [
      `[G6] pod から ${host} を解決できない（coredns-custom が効いていない）。` +
        ` **ランナーからの curl は通ってしまう**（systemd-resolved の合成応答）ため、これは G4 では捕まらない。` +
        ` 非 .NET の 6 ツールは pod から issuer を引くので、この状態では OIDC が壊れる（[IADR-0227]）。\n---\n${text.trim()}`,
    ];
  }
  // 応答の体裁（Address 行）が無いときも通さない。「出力が空でも緑」を作らない。
  if (!/Address\s*\d*:/i.test(text)) {
    return [`[G6] pod からの ${host} の解決結果を読めなかった（Address 行が無い）。\n---\n${text.trim()}`];
  }
  return [];
}

/**
 * G4: issuer の完全一致（[IADR-0243]）。
 * `KC_HOSTNAME_URL` ＋ `/realms/<realm>` と、エッジの discovery が返す `issuer` を突き合わせる。
 */
function evaluateIssuer({ hostnameUrl, realm, discoveryIssuer }) {
  const failures = [];
  if (!hostnameUrl) {
    failures.push(`[G4] ${KEYCLOAK_NS}/${KEYCLOAK_DEPLOY} に KC_HOSTNAME_URL が無い（issuer の単一情報源が失われている）。`);
    return failures;
  }
  const expected = `${hostnameUrl.replace(/\/+$/, '')}/realms/${realm}`;
  if (discoveryIssuer !== expected) {
    failures.push(
      `[G4] issuer が一致しない。期待 "${expected}" / 実際 "${discoveryIssuer}"。` +
        ` **文字列として完全一致でなければトークンの iss 検証が通らない**（[IADR-0243] 決定 1）。`,
    );
  }
  return failures;
}

// ---------------------------------------------------------------- 検査本体

function check({ repoRoot = REPO_ROOT } = {}) {
  const failures = [];
  const notices = [];

  // G3: ツール不在は失敗。抜け道は置かない。
  for (const bin of ['kubectl', 'curl']) {
    if (!hasTool(bin)) {
      failures.push(`[G3] ${bin} が見つからない。本検査はクラスタが在ることが前提であり、抜け道は用意していない。`);
    }
  }
  if (failures.length > 0) return { failures, notices };

  // G1 / G2
  let totalDeployments = 0;
  for (const ns of NAMESPACES) {
    const deploys = kubectlJson(['get', 'deploy', '-n', ns, '-o', 'json']);
    if (!deploys.ok) {
      failures.push(`[G1] namespace ${ns} の Deployment を取得できなかった: ${deploys.error}`);
      continue;
    }
    const d = evaluateDeployments(ns, deploys.value.items);
    failures.push(...d.failures);
    totalDeployments += d.total;

    const pods = kubectlJson(['get', 'pods', '-n', ns, '-o', 'json']);
    if (!pods.ok) {
      failures.push(`[G1] namespace ${ns} の Pod を取得できなかった: ${pods.error}`);
      continue;
    }
    failures.push(...evaluatePods(ns, pods.value.items));
    notices.push(`[check-stack-ready] ${ns}: Deployment ${d.ready}/${d.total} が available、Pod ${pods.value.items.length} 件を判定した。`);
  }

  // G5 — エッジは 2 通り（既定 Traefik / #782 の Istio Ingress Gateway）。**どちらかに 50000 が在ればよい。**
  // 「取得できなかった」ことを直ちに失敗にしない —— Istio エッジでは Traefik の Service は**在るほうが異常**である。
  const traefikSvc = kubectlJson(['get', 'svc', TRAEFIK_SVC, '-n', TRAEFIK_NS, '-o', 'json']);
  const istioSvc = kubectlJson(['get', 'svc', ISTIO_GW_SVC, '-n', ISTIO_GW_NS, '-o', 'json']);
  failures.push(
    ...evaluateAdminEntrypoint(
      traefikSvc.ok ? traefikSvc.value : null,
      istioSvc.ok ? istioSvc.value : null,
    ),
  );

  // G4
  const realms = discoverRealms(repoRoot);
  if (realms.length === 0) {
    failures.push(`[G2] ${REALM_DIR} に realm 宣言が 1 件も見つからなかった。走査が壊れている（0 件を緑にしない）。`);
  }
  const ing = kubectlJson(['get', 'ingress', KEYCLOAK_EDGE_INGRESS, '-n', KEYCLOAK_NS, '-o', 'json']);
  if (!ing.ok) {
    failures.push(
      `[G4] ${KEYCLOAK_NS}/${KEYCLOAK_EDGE_INGRESS} Ingress が無い: ${ing.error}。` +
        ` Keycloak がエッジに出ていなければ issuer をエッジ host に置けない（[IADR-0227]）。`,
    );
  }
  const kc = kubectlJson(['get', 'deploy', KEYCLOAK_DEPLOY, '-n', KEYCLOAK_NS, '-o', 'json']);
  if (!kc.ok) {
    failures.push(`[G4] ${KEYCLOAK_NS}/${KEYCLOAK_DEPLOY} を取得できなかった: ${kc.error}`);
  } else if (realms.length > 0) {
    const containers = (kc.value.spec.template.spec.containers || [])[0] || {};
    const env = containers.env || [];
    const hostnameUrl = (env.find((e) => e.name === 'KC_HOSTNAME_URL') || {}).value;
    for (const realm of realms) {
      const url = `${String(hostnameUrl || '').replace(/\/+$/, '')}/realms/${realm}/.well-known/openid-configuration`;
      const disco = hostnameUrl ? fetchDiscovery(url) : { ok: false, error: 'KC_HOSTNAME_URL が無い' };
      failures.push(
        ...evaluateIssuer({
          hostnameUrl,
          realm,
          discoveryIssuer: disco.ok ? disco.value.issuer : `(取得できなかった: ${disco.error})`,
        }),
      );
    }

    // G6: pod 側の名前解決。**G4 が通っても、ここが割れていることがある。**
    const edgeHost = String(hostnameUrl || '').replace(/^https?:\/\//, '').replace(/\/.*$/, '');
    if (!edgeHost) {
      failures.push('[G6] KC_HOSTNAME_URL からエッジ host を取り出せず、pod 側の名前解決を確かめられなかった。');
    } else {
      const probe = spawnSync(
        'kubectl',
        [
          'run', `dnsprobe-${Date.now().toString(36)}`,
          '--image=busybox:1.36', '--restart=Never', '--rm', '--attach', '--quiet',
          '--command', '--', 'nslookup', edgeHost,
        ],
        { encoding: 'utf8', maxBuffer: 8 * 1024 * 1024 },
      );
      failures.push(...evaluatePodDnsOutput(edgeHost, `${probe.stdout || ''}\n${probe.stderr || ''}`));
    }
  }

  return { failures, notices, totalDeployments };
}

// ---------------------------------------------------------------- self-test

function selfTest() {
  const assert = require('assert');
  let n = 0;
  const ok = (name, fn) => {
    fn();
    n += 1;
    console.log(`  ok  ${name}`);
  };

  ok('G2: Deployment 0 件は失敗になる（ゲート単独で「何もデプロイされていない」を捕まえる）', () => {
    const r = evaluateDeployments('ns', []);
    assert.strictEqual(r.total, 0);
    assert.ok(r.failures.some((f) => f.includes('[G2]')), '0 件が失敗になっていない');
  });

  ok('G1: availableReplicas が足りない Deployment は失敗になる', () => {
    const r = evaluateDeployments('ns', [
      { metadata: { name: 'bff-service' }, spec: { replicas: 1 }, status: { availableReplicas: 0 } },
    ]);
    assert.ok(r.failures.some((f) => f.includes('bff-service')), '未 available が失敗になっていない');
  });

  ok('G1: available が足りていれば失敗しない', () => {
    const r = evaluateDeployments('ns', [
      { metadata: { name: 'ok-service' }, spec: { replicas: 1 }, status: { availableReplicas: 1 } },
    ]);
    assert.deepStrictEqual(r.failures, []);
    assert.strictEqual(r.ready, 1);
  });

  ok('G1: Ready でない Pod は失敗になる。Succeeded（完了 Job）は対象外', () => {
    const bad = evaluatePods('ns', [
      { metadata: { name: 'p1' }, status: { phase: 'Running', conditions: [{ type: 'Ready', status: 'False' }] } },
    ]);
    assert.strictEqual(bad.length, 1, 'Ready=False が失敗になっていない');
    const done = evaluatePods('ns', [{ metadata: { name: 'job' }, status: { phase: 'Succeeded' } }]);
    assert.deepStrictEqual(done, [], '完了 Job を失敗にしている');
  });

  ok('G5: admin=50000 がどちらのエッジにも無ければ失敗になり、#953 を指す', () => {
    const traefikNoAdmin = { spec: { ports: [{ name: 'web', port: 80 }, { name: 'websecure', port: 443 }] } };
    const f = evaluateAdminEntrypoint(traefikNoAdmin, null);
    assert.strictEqual(f.length, 1);
    assert.ok(f[0].includes('#953'), '構造的な原因（#953）を指していない');
    assert.deepStrictEqual(
      evaluateAdminEntrypoint({ spec: { ports: [{ name: 'admin', port: 50000 }] } }, null),
      [],
      'Traefik に admin=50000 が在るのに失敗している',
    );
    // #782: Istio エッジでは Traefik の Service ごと消える。**それを失敗にしない。**
    assert.deepStrictEqual(
      evaluateAdminEntrypoint(null, { spec: { ports: [{ name: 'https-admin', port: 50000 }] } }),
      [],
      'istio-ingressgateway が 50000 を持つのに失敗している（Istio エッジで必ず落ちる）',
    );
    assert.strictEqual(
      evaluateAdminEntrypoint(null, null).length,
      1,
      'どちらのエッジも無いのに通っている（#953 の門が死ぬ）',
    );
  });

  ok('G4: issuer の不一致は失敗になる（部分一致では通さない）', () => {
    const base = { hostnameUrl: 'https://keycloak.localhost', realm: 'platform' };
    assert.deepStrictEqual(
      evaluateIssuer({ ...base, discoveryIssuer: 'https://keycloak.localhost/realms/platform' }),
      [],
      '一致しているのに失敗している',
    );
    assert.strictEqual(
      evaluateIssuer({ ...base, discoveryIssuer: 'http://keycloak:8080/realms/platform' }).length,
      1,
      'in-cluster issuer を通してしまっている',
    );
    assert.strictEqual(
      evaluateIssuer({ ...base, discoveryIssuer: 'https://keycloak.localhost/realms/platform/' }).length,
      1,
      '末尾スラッシュの差を通してしまっている（完全一致でなければ iss 検証が落ちる）',
    );
    assert.strictEqual(
      evaluateIssuer({ ...base, hostnameUrl: '', discoveryIssuer: 'x' }).length,
      1,
      'KC_HOSTNAME_URL 不在を通してしまっている',
    );
  });

  ok('G6: pod から解決できない出力は失敗になる（G4 では捕まらない穴）', () => {
    const bad = evaluatePodDnsOutput(
      'keycloak.localhost',
      'Server:\t10.43.0.10:53\n\n** server can\'t find keycloak.localhost: NXDOMAIN\n',
    );
    assert.strictEqual(bad.length, 1, 'NXDOMAIN が失敗になっていない');
    assert.ok(bad[0].includes('G4 では捕まらない'), 'G4 で代替できない理由を書いていない');

    const good = evaluatePodDnsOutput(
      'keycloak.localhost',
      'Server:\t10.43.0.10:53\n\nName:\tkeycloak.localhost\nAddress: 10.43.183.188\n',
    );
    assert.deepStrictEqual(good, [], '解決できているのに失敗している');
  });

  ok('G6: 出力が空でも緑にしない（沈黙を成功と読まない）', () => {
    assert.strictEqual(evaluatePodDnsOutput('keycloak.localhost', '').length, 1, '空出力を通してしまっている');
    assert.strictEqual(evaluatePodDnsOutput('keycloak.localhost', 'なにか別の文字列').length, 1, '体裁の無い出力を通してしまっている');
  });

  ok('realm は走査して得る（宣言から realm 名が読めている）', () => {
    const realms = discoverRealms();
    assert.ok(realms.length > 0, `realm を走査できていない（${REALM_DIR}）`);
    assert.ok(
      realms.every((r) => typeof r === 'string' && r.length > 0),
      'realm 名が文字列として取れていない',
    );
  });

  console.log(`[check-stack-ready] self-test OK: ${n} 件`);
}

// ---------------------------------------------------------------- main

function main() {
  const argv = process.argv.slice(2);
  const unknown = argv.filter((a) => a !== '--self-test');
  if (unknown.length > 0) {
    console.error(`[check-stack-ready] 未知の引数: ${unknown.join(' ')}`);
    process.exit(2);
  }
  if (argv.includes('--self-test')) {
    selfTest();
    return;
  }

  const r = check();
  for (const notice of r.notices) console.log(notice);

  if (r.failures.length > 0) {
    console.error(`[check-stack-ready] ${r.failures.length} 件の失敗:`);
    for (const f of r.failures) console.error(`\n  - ${f}`);
    process.exit(1);
  }
  console.log(`[check-stack-ready] OK: Deployment ${r.totalDeployments} 件が available で、エッジ・issuer・admin entrypoint も成立している。`);
}

if (require.main === module) main();

module.exports = {
  check,
  discoverRealms,
  evaluateDeployments,
  evaluatePods,
  evaluateAdminEntrypoint,
  evaluateIssuer,
  evaluatePodDnsOutput,
  NAMESPACES,
};
