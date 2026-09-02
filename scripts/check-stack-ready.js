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
 * - **G7 Wiki.js が「使える」こと**（#1108）: `Running` と `使える` の乖離を落とす。
 *   🔴 **Pod は `2/2 Running` で readinessProbe も通っていた。それでも Wiki 同期は 1 件も
 *   成立していなかった**（稼働 dev クラスタで実測）。Wiki.js 2.x は初期セットアップが終わるまで
 *   本体のルータ（`/graphql` を含む）を載せず、`server/setup.js` の catch-all が
 *   **`/healthz` を含むすべての URL に 200 を返す**ためである。
 *   検査は 3 段: **(a)** `/graphql` が 404 でない（setup モードでない）、**(b)** `wikijs-sync` の
 *   `apiKey` が空でない、**(c)** **WikiService が push に使う locale が Wiki.js に入っている**
 *   （setup が入れるのは `en` だけで、`ja` が無いと `pages.create` が外部キー違反で落ちる。
 *   Wiki.js は GraphQL 200 を返すので、失敗は WikiService のエラーキューにしか残らない）。
 *   locale の値は **WikiService の実装から走査して得る**（ここへ書き写さない）。
 *   `wiki-js` の Deployment が無い構成（`wikijs.enabled=false`）は notice で飛ばす（G5 と同じ作法）。
 *
 * - **G8 捕捉用 MTA が居て、API が読めること**（#1144 / ADR-0045 決定 9）: 決定 9 は
 *   「開発環境では実送信しない。k3s 上に捕捉用 MTA を置く」と**無条件で**定めている。
 *   🔴 **G7 と違って「無ければ飛ばす」をしない。** 飛ばせるゲートは opt-in の構成にしか無く、
 *   捕捉用 MTA は base（`deploy/local/infra`）に居る dev 既定である —— 居ないなら、その開発環境は
 *   決定 9 を満たしていない（`from` に実値が入った瞬間に外部の本番リレーへ実送信する側へ倒れる）。
 *   検査は 2 段: **(a)** Deployment が在ること、**(b)** HTTP API が**ループバックで**応答すること
 *   （G7 と同じ理由でエッジからも port-forward からも測らない）。
 *   🔴 **realm の実行時 `smtpServer.host` は見ない。** 決定 9 の但し書き（疎通と文面の検証が要る段階に
 *   限り実リレーを用いてよい）で運用者が正当に実リレーを向けている状態と区別できず、正しい状態を
 *   赤にしてしまう。**dev 既定が外を向いていないこと**は静的検査（`check-realm-constraints.js`）が持つ。
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

/** G7 (#1108): Wiki.js の Deployment / コンテナ / 同期用 Secret。 */
const WIKIJS_NS = 'microservices-platform';
const WIKIJS_DEPLOY = 'wiki-js';
const WIKIJS_CONTAINER = 'wiki-js';
const WIKIJS_SYNC_SECRET = 'wikijs-sync';
const WIKIJS_SYNC_SECRET_KEY = 'apiKey';
/** WikiService が push に使う locale の**単一情報源**（値はここへ書かず、走査して得る）。 */
const WIKI_CLIENT_SRC = path.join(
  'src', 'knowledge', 'backend', 'Services', 'WikiService',
  'Infrastructure', 'ExternalServices', 'WikiJsGraphQlClient.cs',
);

/**
 * G8 (#1144): 開発環境の捕捉用 MTA。**名前と HTTP ポートは宣言から走査して得る**
 * （`deploy/local/infra/mailpit.yaml` の Service が単一情報源。ここへ書き写すと、宣言を変えたときに
 * 検査が静かに空回りする —— G7 の locale と同じ姿勢）。
 */
const MAIL_CAPTURE_NS = 'platform-infra';
const MAIL_CAPTURE_MANIFEST = path.join('deploy', 'local', 'infra', 'mailpit.yaml');

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

/**
 * G7: wiki-js コンテナ内の loopback へ GraphQL を投げる。
 *
 * 🔴 **エッジからも port-forward からも測らない。** エッジは 2 通りあり（Traefik / Istio）、
 * `PeerAuthentication` が STRICT のときメッシュ外からの平文は Envoy に落とされる（#1072 / #1109）。
 * loopback ならその全部と無関係に「Wiki.js 自身が何を返すか」だけを測れる。
 */
function wikiJsGraphql(query, bearer) {
  const args = ['-n', WIKIJS_NS, 'exec', '-i', `deploy/${WIKIJS_DEPLOY}`, '-c', WIKIJS_CONTAINER, '--',
    'curl', '-sS', '--max-time', '30', '-w', '\\n%{http_code}',
    '-X', 'POST', 'http://127.0.0.1:3000/graphql', '-H', 'Content-Type: application/json'];
  if (bearer) args.push('-H', `Authorization: Bearer ${bearer}`);
  args.push('--data-binary', '@-');
  const r = spawnSync('kubectl', args, {
    input: JSON.stringify({ query }), encoding: 'utf8', maxBuffer: 8 * 1024 * 1024,
  });
  const lines = String(r.stdout || '').split('\n');
  const status = (lines.pop() || '').trim();
  return { status, body: lines.join('\n'), stderr: String(r.stderr || '') };
}

/**
 * G8: 捕捉用 MTA のコンテナ内 loopback へ HTTP GET を投げる（G7 と同じ理由でエッジも port-forward も使わない）。
 * image は alpine/busybox なので `wget` を持つ（curl は無い）。
 */
function mailCaptureApi(deploy, httpPort, apiPath) {
  const r = spawnSync(
    'kubectl',
    ['-n', MAIL_CAPTURE_NS, 'exec', '-i', `deploy/${deploy}`, '--',
      'wget', '-q', '-O-', '-T', '15', `http://127.0.0.1:${httpPort}${apiPath}`],
    { encoding: 'utf8', maxBuffer: 8 * 1024 * 1024 },
  );
  return { status: r.status, body: String(r.stdout || ''), stderr: String(r.stderr || '') };
}

/** G7: `wikijs-sync` の `apiKey` の**長さ**を得る（値は返さない・出力もしない）。 */
function wikiJsApiKey() {
  const r = spawnSync(
    'kubectl',
    ['-n', WIKIJS_NS, 'get', 'secret', WIKIJS_SYNC_SECRET, '-o', `jsonpath={.data.${WIKIJS_SYNC_SECRET_KEY}}`],
    { encoding: 'utf8' },
  );
  if (r.status !== 0) return { ok: false, key: '', length: 0 };
  const key = Buffer.from(String(r.stdout || '').trim(), 'base64').toString('utf8');
  return { ok: true, key, length: key.length };
}

// ---------------------------------------------------------------- 判定（純粋関数）

/**
 * G7 の (c) が使う locale を **WikiService の実装から**読む。
 * 見つからなければ `null`（呼び出し側は「測れなかった」として落とす。既定値へ落とさない ——
 * 落とすと、実装が locale を変えたときに検査が静かに空回りする）。
 */
function wikiSyncLocale(repoRoot = REPO_ROOT) {
  try {
    const src = fs.readFileSync(path.join(repoRoot, WIKI_CLIENT_SRC), 'utf8');
    const m = src.match(/private\s+const\s+string\s+Locale\s*=\s*"([^"]+)"/);
    return m ? m[1] : null;
  } catch {
    return null;
  }
}

/**
 * G7: 「Running なのに使えない」を落とす（#1108）。**純関数**（入力は採取済みの値だけ）。
 *
 * @param {{deployExists:boolean, graphqlStatus:string, apiKeyLength:number,
 *          syncLocale:(string|null), localesBody:string}} input
 */
function evaluateWikiJs(input) {
  const failures = [];
  if (!input.deployExists) return failures; // wikijs.enabled=false の構成（呼び出し側が notice を出す）

  // (a) setup モードか。Wiki.js 2.x は setup 完了まで /graphql を載せない。
  if (input.graphqlStatus === '404') {
    failures.push(
      `[G7] wiki-js の /graphql が 404 を返す＝**初期セットアップが終わっていない**（#1108）。` +
        ` この状態でも Pod は Running で /healthz は 200 を返す（setup.js の catch-all が全 URL に 200 を返すため）。` +
        ` Wiki 同期は全件エラーキューへ落ちるが画面には何も出ない。` +
        ` 復旧: bash deploy/local/wikijs-setup/bootstrap.sh`,
    );
    return failures; // 以降は測っても意味が無い（順序で結ばれている）
  }
  if (!/^2\d\d$/.test(input.graphqlStatus)) {
    failures.push(
      `[G7] wiki-js の /graphql の応答を読めなかった（status='${input.graphqlStatus}'）。` +
        ` **沈黙を成功と読まない。**`,
    );
    return failures;
  }

  // (b) 同期の API キー。空だと GraphQL は認証できず、同期は成立しない。
  if (!(input.apiKeyLength > 0)) {
    failures.push(
      `[G7] ${WIKIJS_NS}/${WIKIJS_SYNC_SECRET} の ${WIKIJS_SYNC_SECRET_KEY} が空である（#1108）。` +
        ` fail-safe の空既定のままで、実値が供給されていない。` +
        ` 復旧: bash deploy/local/wikijs-setup/bootstrap.sh`,
    );
  }

  // (c) WikiService が push に使う locale が Wiki.js に入っているか。
  if (input.syncLocale === null) {
    failures.push(
      `[G7] WikiService が使う locale を実装（${WIKI_CLIENT_SRC}）から読めなかった。` +
        ` **既定値へ落とさない** —— 落とすと実装が変わったときに検査が空回りする。`,
    );
    return failures;
  }
  let locales;
  try {
    locales = JSON.parse(input.localesBody).data.localization.locales;
  } catch {
    locales = null;
  }
  if (!Array.isArray(locales) || locales.length === 0) {
    failures.push(
      `[G7] Wiki.js の locale 一覧を読めなかった（API キーが無効か、応答が想定と違う）。` +
        `\n---\n${String(input.localesBody).slice(0, 300)}`,
    );
    return failures;
  }
  const hit = locales.find((l) => l && l.code === input.syncLocale);
  if (!hit || hit.isInstalled !== true) {
    failures.push(
      `[G7] WikiService が push に使う locale '${input.syncLocale}' が Wiki.js に入っていない（#1108）。` +
        ` 初期セットアップが入れるのは 'en' **だけ**であり、この状態では pages.create が` +
        ` 外部キー制約 pages_localecode_foreign に違反して落ちる。` +
        ` **Wiki.js は GraphQL 200 を返すので、失敗は WikiService のエラーキューにしか出ない。**` +
        ` 復旧: bash deploy/local/wikijs-setup/bootstrap.sh`,
    );
  }
  return failures;
}

/** `deploy/keycloak/*-realm.json` を走査して realm 名を得る。0 件は失敗（G2 と同型）。 */
/**
 * G8 の対象（Deployment 名と HTTP ポート）を**宣言から**読む。
 * 読めなければ `null`（呼び出し側は「測れなかった」として落とす —— 既定値へ落とすと、宣言を
 * 変えたときに検査が静かに空回りする）。
 * @returns {{deploy:string, httpPort:string}|null}
 */
function mailCaptureTarget(repoRoot = REPO_ROOT) {
  try {
    const text = fs.readFileSync(path.join(repoRoot, MAIL_CAPTURE_MANIFEST), 'utf8');
    const svc = text.split(/^---\s*$/m).find((d) => /^kind:\s*Service\s*$/m.test(d));
    if (!svc) return null;
    const name = /^\s{2}name:\s*(\S+)\s*$/m.exec(svc);
    const http = /\{\s*name:\s*http,\s*port:\s*(\d+)/.exec(svc);
    return name && http ? { deploy: name[1], httpPort: http[1] } : null;
  } catch {
    return null;
  }
}

/**
 * G8 (#1144): 捕捉用 MTA が居て、その API が読めるか。**純関数**（入力は採取済みの値だけ）。
 * @param {{target:({deploy:string,httpPort:string}|null), deployExists:boolean,
 *          apiStatus:(number|null), apiBody:string}} input
 * @returns {string[]} 失敗メッセージ
 */
function evaluateMailCapture(input) {
  const failures = [];
  if (!input.target) {
    failures.push(
      `[G8] 捕捉用 MTA の宣言（${MAIL_CAPTURE_MANIFEST}）から Service 名と HTTP ポートを読めなかった。`
      + ' 期待値を組み立てられないので検査を成立させない（既定値へ落とさない）。',
    );
    return failures;
  }
  const { deploy, httpPort } = input.target;
  if (!input.deployExists) {
    failures.push(
      `[G8] ${MAIL_CAPTURE_NS}/${deploy} の Deployment が無い（#1144 / ADR-0045 決定 9）。`
      + ' 決定 9 は「開発環境では実送信しない。k3s 上に捕捉用 MTA を置く」と**無条件で**定めている。'
      + ' 捕捉用 MTA が居ない開発環境は、SMTP の実値が入った瞬間に外部の本番リレーへ実送信する側へ倒れる。'
      + ` 起動器（scripts/k8s-local-up.sh の [4/7]）が ${MAIL_CAPTURE_MANIFEST} を apply しているか確かめよ。`,
    );
    return failures; // 居ないなら API も測れない（同じことを 2 回言わない）
  }
  if (input.apiStatus !== 0) {
    failures.push(
      `[G8] ${MAIL_CAPTURE_NS}/${deploy} は居るが HTTP API（:${httpPort}/api/v1/info）を読めなかった。`
      + ' Pod が Running でも API が死んでいれば、送出の成立を機械で確かめる手段が無い'
      + '（docs/tests/SC-15_password-reset.md の T-16 / T-17 を測れなくなる）。',
    );
    return failures;
  }
  if (!/"Version"\s*:/.test(input.apiBody)) {
    failures.push(
      `[G8] ${MAIL_CAPTURE_NS}/${deploy} の API 応答が想定と違う（"Version" を含まない）。`
      + ' 応答を読めたことと、捕捉用 MTA として答えたことは別である。',
    );
  }
  return failures;
}

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

  // G7 (#1108): Wiki.js が「使える」か。**Running と 使える の乖離**を落とす。
  const wikiDeploy = kubectlJson(['get', 'deploy', WIKIJS_DEPLOY, '-n', WIKIJS_NS, '-o', 'json']);
  if (!wikiDeploy.ok) {
    notices.push(
      `[check-stack-ready] ${WIKIJS_NS}/${WIKIJS_DEPLOY} が無いので G7 は飛ばす（wikijs.enabled=false の構成）。`,
    );
    failures.push(...evaluateWikiJs({ deployExists: false }));
  } else {
    const probe = wikiJsGraphql('{pages{list(orderBy:ID){id}}}');
    const apiKey = wikiJsApiKey();
    const syncLocale = wikiSyncLocale(repoRoot);
    // locale 一覧は API キーが要る。キーが空なら問い合わせても意味が無いので空文字を渡す
    // （(b) が既に失敗を出しているため、(c) は「読めなかった」を重ねて言うだけになる）。
    const locales = apiKey.length > 0
      ? wikiJsGraphql('{localization{locales{code isInstalled}}}', apiKey.key)
      : { body: '' };
    failures.push(
      ...evaluateWikiJs({
        deployExists: true,
        graphqlStatus: probe.status,
        apiKeyLength: apiKey.length,
        syncLocale,
        localesBody: locales.body,
      }),
    );
    notices.push(
      `[check-stack-ready] wiki-js: /graphql=${probe.status} / ${WIKIJS_SYNC_SECRET}.${WIKIJS_SYNC_SECRET_KEY} の長さ=${apiKey.length}` +
        ` / 同期 locale=${syncLocale ?? '(読めなかった)'}。`,
    );
  }

  // G8 (#1144): 捕捉用 MTA が居て、API が読めるか。**opt-in ではないので「無ければ飛ばす」をしない。**
  const mailTarget = mailCaptureTarget(repoRoot);
  if (!mailTarget) {
    failures.push(...evaluateMailCapture({ target: null, deployExists: false, apiStatus: null, apiBody: '' }));
  } else {
    const mailDeploy = kubectlJson(['get', 'deploy', mailTarget.deploy, '-n', MAIL_CAPTURE_NS, '-o', 'json']);
    const api = mailDeploy.ok
      ? mailCaptureApi(mailTarget.deploy, mailTarget.httpPort, '/api/v1/info')
      : { status: null, body: '' };
    failures.push(...evaluateMailCapture({
      target: mailTarget,
      deployExists: mailDeploy.ok,
      apiStatus: api.status,
      apiBody: api.body,
    }));
    if (mailDeploy.ok) {
      const version = /"Version"\s*:\s*"([^"]*)"/.exec(api.body);
      notices.push(
        `[check-stack-ready] ${MAIL_CAPTURE_NS}/${mailTarget.deploy}: API=:${mailTarget.httpPort}`
        + ` / version=${version ? version[1] : '(読めなかった)'}。開発環境の送出はここで止まる（外へは出ない）。`,
      );
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

  // ---- G7 (#1108) ----
  const wikiOk = {
    deployExists: true,
    graphqlStatus: '200',
    apiKeyLength: 502,
    syncLocale: 'ja',
    localesBody: JSON.stringify({
      data: { localization: { locales: [{ code: 'en', isInstalled: true }, { code: 'ja', isInstalled: true }] } },
    }),
  };

  ok('G7: 健全なスタックは通る（陽性対照）', () => {
    assert.deepStrictEqual(evaluateWikiJs(wikiOk), [], '健全なのに失敗している');
  });

  ok('G7: setup モード（/graphql が 404）は失敗になる —— Pod は Running のままである', () => {
    const f = evaluateWikiJs({ ...wikiOk, graphqlStatus: '404' });
    assert.strictEqual(f.length, 1, 'setup モードが失敗になっていない');
    assert.ok(f[0].includes('#1108'), '構造的な原因（#1108）を指していない');
    assert.ok(f[0].includes('bootstrap.sh'), '復旧手段を書いていない');
  });

  ok('G7: /graphql の応答を読めなければ失敗になる（沈黙を成功と読まない）', () => {
    assert.strictEqual(evaluateWikiJs({ ...wikiOk, graphqlStatus: '' }).length, 1, '空応答を通している');
    assert.strictEqual(evaluateWikiJs({ ...wikiOk, graphqlStatus: '000' }).length, 1, '非 2xx を通している');
  });

  ok('G7: apiKey が空なら失敗になる（fail-safe の空既定のまま配備されている）', () => {
    const f = evaluateWikiJs({ ...wikiOk, apiKeyLength: 0 });
    assert.ok(f.some((x) => x.includes('apiKey')), 'apiKey の空が失敗になっていない');
  });

  ok('G7: 同期 locale が Wiki.js に無ければ失敗になる（**setup を終えただけでは足りない**）', () => {
    const body = JSON.stringify({
      data: { localization: { locales: [{ code: 'en', isInstalled: true }, { code: 'ja', isInstalled: false }] } },
    });
    const f = evaluateWikiJs({ ...wikiOk, localesBody: body });
    assert.strictEqual(f.length, 1, '未インストールの locale を通している');
    assert.ok(f[0].includes('pages_localecode_foreign'), '落ち方（外部キー違反）を書いていない');
    // 一覧に**現れない**場合も同じく失敗であること。
    const missing = JSON.stringify({ data: { localization: { locales: [{ code: 'en', isInstalled: true }] } } });
    assert.strictEqual(evaluateWikiJs({ ...wikiOk, localesBody: missing }).length, 1, '不在を通している');
  });

  ok('G7: locale を実装から読めなければ失敗になる（既定値へ落とさない）', () => {
    assert.strictEqual(evaluateWikiJs({ ...wikiOk, syncLocale: null }).length, 1, '読めないのに通している');
  });

  ok('G7: locale 一覧が読めなければ失敗になる（0 件を緑にしない）', () => {
    assert.strictEqual(evaluateWikiJs({ ...wikiOk, localesBody: '' }).length, 1, '空応答を通している');
    const empty = JSON.stringify({ data: { localization: { locales: [] } } });
    assert.strictEqual(evaluateWikiJs({ ...wikiOk, localesBody: empty }).length, 1, '0 件を通している');
  });

  ok('G7: wiki-js が無い構成（wikijs.enabled=false）は失敗にしない', () => {
    assert.deepStrictEqual(evaluateWikiJs({ deployExists: false }), [], '不在を失敗にしている');
  });

  ok('G7: 同期 locale は WikiService の実装から走査して得る（値を書き写さない）', () => {
    const locale = wikiSyncLocale();
    assert.ok(typeof locale === 'string' && locale.length > 0,
      `WikiService の実装から locale を読めていない（${WIKI_CLIENT_SRC}）`);
  });

  // ---- G8 (#1144) ----
  const mailOk = {
    target: { deploy: 'mailpit', httpPort: '8025' },
    deployExists: true,
    apiStatus: 0,
    apiBody: '{"Version":"v1.21.8","Messages":0}',
  };

  ok('G8: 捕捉用 MTA が居て API が読めれば通る（陽性対照）', () => {
    assert.deepStrictEqual(evaluateMailCapture(mailOk), [], '健全なスタックを落としている');
  });

  ok('G8: 捕捉用 MTA が居なければ**失敗**になる（G7 と違い「無ければ飛ばす」をしない）', () => {
    const f = evaluateMailCapture({ ...mailOk, deployExists: false, apiStatus: null, apiBody: '' });
    assert.strictEqual(f.length, 1, '不在を通している');
    assert.ok(f[0].includes('決定 9'), '計画の根拠（ADR-0045 決定 9）を指していない');
    assert.ok(f[0].includes('k8s-local-up.sh'), '復旧手段を書いていない');
  });

  ok('G8: API が読めなければ失敗になる（Running と 使える の乖離を落とす）', () => {
    assert.strictEqual(evaluateMailCapture({ ...mailOk, apiStatus: 1, apiBody: '' }).length, 1,
      'API 到達不能を通している');
  });

  ok('G8: 応答が捕捉用 MTA のものでなければ失敗になる（読めたことと答えたことは別）', () => {
    assert.strictEqual(evaluateMailCapture({ ...mailOk, apiBody: '<html>404</html>' }).length, 1,
      '別物の応答を通している');
  });

  ok('G8: 宣言を読めなければ失敗になる（既定値へ落とさない）', () => {
    assert.strictEqual(evaluateMailCapture({ ...mailOk, target: null }).length, 1, '測れないのに通している');
  });

  ok('G8: 対象は宣言（mailpit.yaml の Service）から走査して得る（値を書き写さない）', () => {
    const t = mailCaptureTarget();
    assert.ok(t && t.deploy && /^\d+$/.test(t.httpPort),
      `捕捉用 MTA の宣言から Service 名と HTTP ポートを読めていない（${MAIL_CAPTURE_MANIFEST}）`);
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
  console.log(
    `[check-stack-ready] OK: Deployment ${r.totalDeployments} 件が available で、` +
      'エッジ・issuer・admin entrypoint・Wiki.js の初期化・捕捉用 MTA も成立している。',
  );
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
  evaluateWikiJs,
  wikiSyncLocale,
  NAMESPACES,
};
