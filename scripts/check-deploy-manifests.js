#!/usr/bin/env node
/**
 * NFR / #783（#442 子 5）: deploy/ の chart と overlay が「レンダリングできる」こと、および
 * **レンダリング結果が Kubernetes のスキーマに適合すること**を機械で検査する。
 *
 * 計画 ADR-0007（CI/CD）・ADR-0021（エッジ・実行基盤）。CI に helm / kustomize の検証ジョブが
 * 1 つも無く、chart や overlay が壊れてもマージまで気づけなかった。
 *
 * ## 設計の要点（4 つとも、本リポジトリが実際に踏んだ事故／実測した穴への対処である）
 *
 * 1. **列挙を持たない。** overlay も chart も走査で発見する。ワークフローにも本ファイルにも
 *    名前を書かない。書くと次に増えたとき静かに検査対象から外れる —— `paths:` の片側取りこぼしを
 *    4 回踏んでいる（#558 / #562 / #747 / #801）。**着手時の実測でも、事前調査が 6 件と数えた
 *    overlay は実際には 8 件だった。**
 *
 * 2. **0 件走査で緑を返さない。** 走査が壊れて 0 件になったら exit 1 にする。
 *    「何も無い」と「問題が無い」を同じ出力にしない（「沈黙の exit 0」#797）。
 *
 * 3. **ツール不在は fail-closed。** helm / kubectl / kubeconform が無いとき既定は exit 1 にし、
 *    `DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS=1` のときだけ notice つきで skip する。
 *    **この抜け道を CI が使っていないことは scripts/scripts.repo.test.js が突合する**
 *    （IADR-0209 の `include ⊆ paths` と同じ型）。
 *
 * 4. **「レンダリングできる」と「スキーマに適合する」は別の性質であり、前者は後者を代替しない**
 *    （[IADR-0240]。#783 前半の実測。2026-08-21 版の本スクリプトは helm lint / helm template /
 *    kubectl kustomize の**成功だけ**を見ており、`spec.replicas` に文字列を入れる等の型違反は
 *    Go テンプレート／YAML としては正当なため検出できない）。**`kubeconform` でレンダリング結果を
 *    Kubernetes の JSON Schema と突合する。** 標準リソースは既定カタログ、Istio 等の CRD は
 *    `datreeio/CRDs-catalog`（[IADR-0240]で選定・実測確認済み）で解決する。**未知の CRD で
 *    スキーマが見つからない場合も fail-closed**（`-ignore-missing-schemas` は使わない）——
 *    「スキーマが無い」を人が気づける形にするためで、要点 3 と同じ設計判断である。
 *
 * 5. **描画結果で、インフラ製品の既定の外部通信が止まっていることも見る**（ADR-0107 決定 4 / #1841）。
 *    レンダリングでき、スキーマに適合しても、Grafana・Loki・Tempo・Qdrant・Mailpit は既定で外へ送る。
 *    判定は `lib/product-egress-defaults.js`（描画した chart・overlay に加え、compose・Testcontainers・手順書とスクリプトの
 *    `docker run` も見る）。**これらはツールが無くても見る**（helm / kubectl が要らない）。
 *    走査全体で製品が 1 つも見つからなければ失敗にする（要点 2 と同じ。照合が壊れて 0 件になったときに緑を返さない）。
 *
 * 6. **描画結果の外部イメージが digest で固定されていることも見る**（ADR-0107 決定 3 / IADR-0547 / #1858）。
 *    `check-image-digests.js` はリポジトリの字面（values・kustomization の `images:`）を読むが、上流のマニフェストを
 *    `resources:` の URL で読む overlay（cert-manager・Argo CD）では、上流が版の更新でイメージを**足した**とき
 *    `images:` に項目が無いまま tag だけで描かれる。字面では見えないので、描画した結果に同じ判定（自製は対象外）を当てる。
 *
 * 7. **描画結果に Loki・Tempo が現れたら、前段の認証と到達の制限（NetworkPolicy）の両方が同じ描画結果に揃っていることも見る**
 *    （ADR-0133 決定 3 / #1867）。本番像の chart には現時点で両製品が無く「対象なし」で通る。chart へ足す PR が前段なしで
 *    入るのを止めるための検査である。判定は `lib/observability-front-guard.js`（名前では通さない。前段のイメージ・Secret・
 *    製品の loopback 待ち・Service の宛先・NetworkPolicy の名指しと許可の和）。経路 B（deploy/local/observability）が陽性対照で、
 *    **走査全体で Loki・Tempo が 1 度も判定されなければ失敗にする**（要点 2 と同じ。識別が壊れて 0 件になったときに緑を返さない）。
 *
 * 8. **compose がホストへ出す口（`ports:`）が、すべてループバック（127.0.0.1）に限られていることも見る**（IADR-0587 /
 *    GHSA-j4qv-2w99-xg5p）。判定は `lib/compose-host-ports.js`（短い形式・流れ形式・長い形式の `host_ip`・`network_mode: host`）。
 *    **ツールが無くても見る。** 走査全体で口が 1 件も数えられなければ失敗にする（要点 2 と同じ）。
 */

'use strict';

const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const egress = require('./lib/product-egress-defaults');
const imageDigests = require('./check-image-digests');
const obsFront = require('./lib/observability-front-guard');
const hostPorts = require('./lib/compose-host-ports');
// NFR-20, IADR-0605 (#2132): chart は自製イメージのタグが無いと描画を落とす。描くだけの検査は固定の値を渡す。
const { RENDER_IMAGE_TAG_ARGS } = require('./lib/helm-render-tag');

const REPO_ROOT = path.join(__dirname, '..');
const DEPLOY_DIR = 'deploy';
const HELM_DIR = path.join(DEPLOY_DIR, 'helm');
const SKIP_DIRS = new Set(['node_modules', '.git', 'bin', 'obj', 'dist', 'coverage', 'charts']);

/** 抜け道の環境変数名。CI がこれを立てていないことを scripts.repo.test.js が固定する。 */
const ALLOW_MISSING_TOOLS_ENV = 'DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS';

/** 検査に要る外部バイナリ。3 つとも fail-closed 対象（要点 3）。 */
const REQUIRED_TOOLS = ['helm', 'kubectl', 'kubeconform'];

/**
 * kubeconform のスキーマ供給元（[IADR-0240]）。
 * 1 段目 `default` は kubeconform 同梱の標準 Kubernetes スキーマ。
 * 2 段目は Istio 等 CRD のスキーマ集約カタログ（datreeio/CRDs-catalog）。
 * 両方に無ければ fail-closed（-ignore-missing-schemas は使わない。要点 4）。
 */
// NFR, #1820: カタログはコミット SHA で固定する。`main` を引くと上流の更新ひとつで全 PR の必須チェックが落ちる
// （2026-10-08 の external-secrets.io の更新 b7e2015 を kubeconform v0.6.7 がスキーマとして扱えず、
// ClusterSecretStore が「could not find schema」になった）。上げるときは本走査を実走してから（IADR-0240 追記）。
const CRDS_CATALOG_REF = 'f1e7f6bc0537bf0622ffe6e47dbaa85914fabbec';
const SCHEMA_LOCATIONS = [
  'default',
  `https://raw.githubusercontent.com/datreeio/CRDs-catalog/${CRDS_CATALOG_REF}/{{.Group}}/{{.ResourceKind}}_{{.ResourceAPIVersion}}.json`,
];

/** ディレクトリを再帰的に走査し、`name` に一致するファイルの**所属ディレクトリ**を返す。 */
function findDirsContaining(root, name, skipDirs = SKIP_DIRS) {
  const out = [];
  const walk = (dir) => {
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      const full = path.join(dir, e.name);
      if (e.isDirectory()) {
        if (!skipDirs.has(e.name)) walk(full);
      } else if (e.name === name) {
        out.push(dir);
      }
    }
  };
  walk(root);
  return out.sort();
}

function toPosix(p) {
  return p.split(path.sep).join('/');
}

/** overlay（kustomization.yaml を持つディレクトリ）をリポジトリ相対で返す。 */
function discoverOverlays(repoRoot = REPO_ROOT) {
  return findDirsContaining(path.join(repoRoot, DEPLOY_DIR), 'kustomization.yaml').map((d) =>
    toPosix(path.relative(repoRoot, d)),
  );
}

/**
 * chart（Chart.yaml を持つディレクトリ）をリポジトリ相対で返す。
 * `charts/`（依存 chart の展開先）は SKIP_DIRS で除外する —— 上流の chart は本リポジトリの成果物ではない。
 */
function discoverCharts(repoRoot = REPO_ROOT) {
  return findDirsContaining(path.join(repoRoot, HELM_DIR), 'Chart.yaml').map((d) =>
    toPosix(path.relative(repoRoot, d)),
  );
}

/**
 * NFR-02, ADR-0076 決定 4, IADR-0469 (#1287): chart の `ci/*.yaml`（helm / chart-testing の慣習。CI 専用の values）を
 * リポジトリ相対で返す。**既定オフの opt-in テンプレートは既定の描画に 1 度も現れない**ため、既定の描画だけを
 * 検査すると有効時のスキーマ違反を素通しする。ここで見つけた values ごとに lint / template / kubeconform を回す。
 * 要点 1 と同じく列挙を持たない（ファイル名をワークフローにも本ファイルにも書かない）。
 */
function discoverChartCiValues(chartDir, repoRoot = REPO_ROOT) {
  const ciDir = path.join(repoRoot, chartDir, 'ci');
  let entries;
  try {
    entries = fs.readdirSync(ciDir, { withFileTypes: true });
  } catch {
    return [];
  }
  return entries
    .filter((e) => e.isFile() && /\.ya?ml$/.test(e.name))
    .map((e) => toPosix(path.join(chartDir, 'ci', e.name)))
    .sort();
}

// 🔴 **`command -v` を使わない。** `shell: true` は Windows で `cmd.exe`（COMSPEC）を起こすが、
// `cmd.exe` に `command` 組み込みは無い。結果、**在るツールまで「無い」と報告して常に fail する** ——
// 実測（2026-09-06）: `helm` / `kubectl` が PATH に在る Windows 機で 3 つとも欠落と報告し、
// **この誤報を根拠に 2 つの PR 本文へ「3 つとも入っていない」と書かれた**（どちらも後から訂正）。
// 誤報は「検証を飛ばした」ではなく「検証できない」の形で出るため、CI（Linux）では再現しない。
//
// 本リポジトリの他の検査器（`check-stack-ready.js` / `check-password-reset-mail.js`）は既に
// `where` / `which` を platform で使い分けている。**その作法へ揃えるだけであり、新しい流儀は持ち込まない。**
function hasTool(bin) {
  const probe = spawnSync(process.platform === 'win32' ? 'where' : 'which', [bin], {
    encoding: 'utf8',
  });
  return probe.status === 0;
}

function run(bin, args, cwd = REPO_ROOT, input = undefined) {
  const r = spawnSync(bin, args, { cwd, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, input });
  return {
    ok: r.status === 0,
    out: `${r.stdout || ''}${r.stderr || ''}`.trim(),
  };
}

/**
 * レンダリング済み YAML を kubeconform へ標準入力で渡し、Kubernetes のスキーマと突合する。
 * `-strict`（未知フィールドも違反にする）・`-summary`（件数の要約を出す）。
 * `-ignore-missing-schemas` は使わない —— 要点 4（未知の CRD は fail-closed）。
 */
/**
 * スキーマを照合しない kind（理由つき。足すときは理由を書く）。
 * - `CustomResourceDefinition`（#1858 / IADR-0547 決定 4）: CRD という kind **そのもの**のスキーマは、既定のカタログ
 *   （yannh/kubernetes-json-schema）にも CRD カタログ（datreeio）にも無い（2026-10-15 に両方 404 を実測）。
 *   上流のマニフェストを丸ごと読む overlay（cert-manager・Argo CD）が CRD を同梱するため、ここを落とすと検査が恒久的に赤になる。
 *   CRD の定義は上流の字面のままで（kustomize は images 以外を変えない）、apiserver が apply の時点で検証する。
 *   **CRD で定義された kind のリソース（Certificate・ExternalSecret 等）は従前どおり照合し、無ければ fail-closed のまま**である。
 */
const SKIP_SCHEMA_KINDS = ['CustomResourceDefinition'];

function validateSchema(yaml, cwd = REPO_ROOT) {
  const args = ['-strict', '-summary', '-skip', SKIP_SCHEMA_KINDS.join(',')];
  for (const loc of SCHEMA_LOCATIONS) args.push('-schema-location', loc);
  return run('kubeconform', args, cwd, yaml);
}

/** compose（`deploy/` 直下の docker-compose*.yml）。要点 1 と同じく列挙を持たない。 */
function discoverComposeFiles(repoRoot = REPO_ROOT) {
  let entries;
  try {
    entries = fs.readdirSync(path.join(repoRoot, DEPLOY_DIR));
  } catch {
    return [];
  }
  return entries.filter((n) => /^docker-compose[^/]*\.ya?ml$/.test(n)).sort().map((n) => `${DEPLOY_DIR}/${n}`);
}

/**
 * 要点 5（#1841）: ツールの要らない経路（compose・Testcontainers）の外部通信の検査。
 * 戻り値の `found` は製品ごとの出現数（描画結果の分と合わせて `missingProducts` へ渡す）。
 */
function checkEgressWithoutTools(repoRoot) {
  const failures = [];
  const found = [];
  const composeFiles = discoverComposeFiles(repoRoot);
  if (composeFiles.length === 0) failures.push(`compose（${DEPLOY_DIR}/docker-compose*.yml）が 0 件だった。走査が壊れている可能性がある。`);
  for (const rel of composeFiles) {
    const r = egress.checkCompose(fs.readFileSync(path.join(repoRoot, rel), 'utf8'), rel, path.join(repoRoot, path.dirname(rel)));
    failures.push(...r.problems.map((p) => `既定の外部通信が止まっていない: ${p}`));
    found.push(r.found);
  }
  const tc = egress.checkTestcontainers(egress.collectCsFiles(repoRoot));
  failures.push(...tc.problems.map((p) => `既定の外部通信が止まっていない（Testcontainers）: ${p}`));
  const manual = egress.checkRunCommands(egress.collectRunCommandFiles(repoRoot));
  failures.push(...manual.problems.map((p) => `既定の外部通信が止まっていない（手で起こす手順）: ${p}`));
  return { failures, found, testcontainersBuilders: tc.builders, runCommands: manual.commands };
}

/**
 * 要点 8（IADR-0587）: compose のホストへ出す口がループバックに限られていること。ツールは要らない。
 * 走査全体で口が 1 件も数えられなければ失敗にする（空の母集合を緑にしない）。
 */
function checkComposeHostPortsAll(repoRoot) {
  const failures = [];
  let count = 0;
  for (const rel of discoverComposeFiles(repoRoot)) {
    const r = hostPorts.checkComposeHostPorts(fs.readFileSync(path.join(repoRoot, rel), 'utf8'), rel, egress.splitComposeServices);
    count += r.count;
    failures.push(...r.problems.map((p) => `ホストへ出す口がループバックに限られていない: ${p}`));
  }
  if (count === 0) failures.push(`compose（${DEPLOY_DIR}/docker-compose*.yml）のホストへ出す口（ports:）が 0 件だった。走査が壊れている可能性がある。`);
  return { failures, count };
}

/**
 * 要点 6: 描画結果（複数文書の YAML）の `image:` のうち、自製でも digest つきでもないものを返す。
 * 判定は check-image-digests.js と同じ（`extractRefs` の `image:` の読み方・自製の判定・digest の形）。
 */
function undigestedRenderedImages(yaml, label) {
  return [
    ...new Set(
      imageDigests
        .extractRefs(`${label}.rendered.yaml`, yaml)
        .map((r) => r.ref)
        .filter((ref) => !imageDigests.isSelfBuilt(ref) && !/@sha256:[0-9a-f]{64}$/.test(ref)),
    ),
  ];
}

/** 検査本体。結果オブジェクトを返す（プロセスは終了させない — 自己試験から呼べるようにするため）。 */
function check({ repoRoot = REPO_ROOT, allowMissingTools = false } = {}) {
  const overlays = discoverOverlays(repoRoot);
  const charts = discoverCharts(repoRoot);
  const failures = [];
  const notices = [];
  const egressFound = [];
  const obsFound = new Map();
  const renderedEgress = (yaml, label) => {
    // 要点 7（#1867）: Loki・Tempo が現れたら前段と NetworkPolicy の両方。
    const front = obsFront.checkObservabilityFront(yaml, label);
    failures.push(...front.problems);
    for (const [k, v] of front.found) obsFound.set(k, (obsFound.get(k) || 0) + v);
    const r = egress.checkRenderedManifests(yaml, label);
    failures.push(...r.problems.map((p) => `既定の外部通信が止まっていない: ${p}`));
    egressFound.push(r.found);
    // 要点 6（#1858）: 描画結果の外部イメージの digest。
    for (const ref of undigestedRenderedImages(yaml, label)) {
      failures.push(`描画結果に digest で固定されていないイメージがある: ${label}: ${ref}（kustomization の images: か values で tag@sha256 に固定する。IADR-0547）`);
    }
  };

  // 要点 2: 0 件走査で緑を返さない。
  if (overlays.length === 0) {
    failures.push(`overlay（${DEPLOY_DIR}/**/kustomization.yaml）が 0 件だった。走査が壊れている可能性がある。`);
  }
  if (charts.length === 0) {
    failures.push(`chart（${HELM_DIR}/**/Chart.yaml）が 0 件だった。走査が壊れている可能性がある。`);
  }
  if (failures.length > 0) return { overlays, charts, failures, notices, skipped: false };

  // 要点 5: compose と Testcontainers はツール無しで見る（ツール不在で飛ばす分に含めない）。
  const offline = checkEgressWithoutTools(repoRoot);
  failures.push(...offline.failures);
  egressFound.push(...offline.found);
  // 要点 8（IADR-0587）: compose のホストへ出す口はループバックに限る（ツール無しで見る）。
  failures.push(...checkComposeHostPortsAll(repoRoot).failures);

  // 要点 3: ツール不在は fail-closed。
  const missing = REQUIRED_TOOLS.filter((b) => !hasTool(b));
  if (missing.length > 0) {
    if (!allowMissingTools) {
      failures.push(
        `${missing.join(' / ')} が PATH にありません。chart / overlay の検証（レンダリング＋スキーマ突合）には` +
          ` ${REQUIRED_TOOLS.join(' / ')} の全てが要ります。\n` +
          `  導入するか、意図的に飛ばす場合のみ ${ALLOW_MISSING_TOOLS_ENV}=1 を立ててください。\n` +
          `  **CI では立てないこと**（scripts/scripts.repo.test.js が突合します）。`,
      );
      return { overlays, charts, failures, notices, skipped: false };
    }
    notices.push(
      `notice: ${missing.join(' / ')} が無いため chart / overlay の検証を飛ばした` +
        `（${ALLOW_MISSING_TOOLS_ENV}=1）。overlay ${overlays.length} 件 / chart ${charts.length} 件は**検査していない**` +
        '（既定の外部通信の検査も compose と Testcontainers の分だけ行った）。',
    );
    return { overlays, charts, failures, notices, skipped: true };
  }

  for (const c of charts) {
    // 既定の values（引数なし）に加え、ci/*.yaml の各 values でも同じ 3 段を回す（#1287）。
    for (const valuesFile of [null, ...discoverChartCiValues(c, repoRoot)]) {
      const extra = [...(valuesFile ? ['-f', valuesFile] : []), ...RENDER_IMAGE_TAG_ARGS];
      const label = valuesFile ? `${c}（-f ${valuesFile}）` : c;
      const lint = run('helm', ['lint', c, ...extra], repoRoot);
      if (!lint.ok) failures.push(`helm lint が失敗した: ${label}\n${lint.out}`);
      const tpl = run('helm', ['template', 'ci-check', c, ...extra], repoRoot);
      if (!tpl.ok) {
        failures.push(`helm template が失敗した: ${label}\n${tpl.out}`);
      } else {
        const schema = validateSchema(tpl.out, repoRoot);
        if (!schema.ok) failures.push(`kubeconform（chart）でスキーマ不整合: ${label}\n${schema.out}`);
        renderedEgress(tpl.out, label);
      }
    }
  }

  for (const o of overlays) {
    const built = run('kubectl', ['kustomize', o], repoRoot);
    if (!built.ok) {
      failures.push(`kubectl kustomize が失敗した: ${o}\n${built.out}`);
    } else if (built.out.trim() === '') {
      failures.push(`kubectl kustomize の出力が空だった: ${o}`);
    } else {
      const schema = validateSchema(built.out, repoRoot);
      if (!schema.ok) failures.push(`kubeconform（overlay）でスキーマ不整合: ${o}\n${schema.out}`);
      renderedEgress(built.out, o);
    }
  }

  // 要点 5 / 要点 2: 製品が走査全体で 1 度も見つからなければ失敗（照合が壊れて 0 件になったときに緑を返さない）。
  const missingEgress = egress.missingProducts(egressFound);
  if (missingEgress.length > 0) {
    failures.push(
      `既定の外部通信の検査で ${missingEgress.join(' / ')} が 1 度も見つからなかった（chart・overlay・compose のどこにも無い）。` +
        ' イメージの照合が壊れたか、製品を外した。外したなら lib/product-egress-defaults.js の PRODUCTS から除くこと。',
    );
  }
  // 要点 7 / 要点 2: Loki・Tempo が走査全体で 1 度も判定されなければ失敗（経路 B が陽性対照。識別が壊れて 0 件のときに緑を返さない）。
  const obsMissing = obsFront.TARGETS.map((t) => t.name).filter((n) => !obsFound.get(n));
  if (obsMissing.length > 0) {
    failures.push(
      `前段と到達の制限の検査（#1867）で ${obsMissing.join(' / ')} が 1 度も判定されなかった（chart・overlay のどこにも無い）。` +
        ' イメージの識別（lib/observability-front-guard.js の TARGETS）が壊れたか、経路 B の overlay から外した。',
    );
  }
  if (offline.testcontainersBuilders === 0) {
    failures.push('Testcontainers の new QdrantBuilder( が 0 件だった（走査が壊れたか、試験を外した。外したなら本検査を見直すこと）。');
  }

  return { overlays, charts, failures, notices, skipped: false };
}

// ---------------------------------------------------------------- self-test

function selfTest() {
  const assert = require('assert');
  const os = require('os');
  let n = 0;
  const ok = (name, fn) => {
    fn();
    n += 1;
    console.log(`  ok  ${name}`);
  };

  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'deploy-manifests-selftest-'));
  const mk = (rel, body = '') => {
    const full = path.join(tmp, rel);
    fs.mkdirSync(path.dirname(full), { recursive: true });
    fs.writeFileSync(full, body);
  };

  ok('走査は kustomization.yaml を持つディレクトリを深さに関係なく拾う', () => {
    mk('deploy/local/edge/kustomization.yaml', 'resources: []\n');
    mk('deploy/local/edge/tls/kustomization.yaml', 'resources: []\n');
    mk('deploy/local/vault/kustomization.yaml', 'resources: []\n');
    const found = discoverOverlays(tmp);
    assert.deepStrictEqual(found, ['deploy/local/edge', 'deploy/local/edge/tls', 'deploy/local/vault']);
  });

  ok('#1858 要点 6: 描画結果の tag だけの外部イメージを拾い、digest つき・自製・テンプレート外の行は拾わない', () => {
    const D = 'sha256:' + 'a'.repeat(64);
    const yaml = [
      'kind: Deployment',
      'spec:',
      '  template:',
      '    spec:',
      '      containers:',
      `        - image: quay.io/a/ctl:v1@${D}`,
      '        - image: quay.io/a/new:v1',
      '        - image: microservices-platform/bff:latest',
      '---',
      'kind: Deployment',
      'spec:',
      '  template:',
      '    spec:',
      '      initContainers:',
      '        - image: "quay.io/a/new:v1"',
      '',
    ].join('\n');
    assert.deepStrictEqual(undigestedRenderedImages(yaml, 'deploy/x'), ['quay.io/a/new:v1']);
    assert.deepStrictEqual(undigestedRenderedImages(`image: quay.io/a/ctl:v1@${D}\n`, 'deploy/x'), []);
  });

  ok('走査は Chart.yaml を持つディレクトリを拾い、charts/（依存の展開先）は除外する', () => {
    mk('deploy/helm/msp/Chart.yaml', 'name: msp\n');
    mk('deploy/helm/msp/charts/upstream/Chart.yaml', 'name: upstream\n');
    assert.deepStrictEqual(discoverCharts(tmp), ['deploy/helm/msp']);
  });

  ok('chart の ci/*.yaml を拾い、YAML 以外と ci/ の無い chart は空を返す（#1287）', () => {
    mk('deploy/helm/msp/ci/b-values.yaml', 'x: 1\n');
    mk('deploy/helm/msp/ci/a-values.yml', 'x: 1\n');
    mk('deploy/helm/msp/ci/README.md', '# not values\n');
    assert.deepStrictEqual(discoverChartCiValues('deploy/helm/msp', tmp), [
      'deploy/helm/msp/ci/a-values.yml',
      'deploy/helm/msp/ci/b-values.yaml',
    ]);
    mk('deploy/helm/other/Chart.yaml', 'name: other\n');
    assert.deepStrictEqual(discoverChartCiValues('deploy/helm/other', tmp), []);
  });

  ok('overlay が 0 件なら失敗する（0 件走査で緑を返さない）', () => {
    const empty = fs.mkdtempSync(path.join(os.tmpdir(), 'deploy-manifests-empty-'));
    fs.mkdirSync(path.join(empty, 'deploy'), { recursive: true });
    const r = check({ repoRoot: empty, allowMissingTools: true });
    assert.ok(r.failures.some((f) => f.includes('overlay')), 'overlay 0 件が失敗になっていない');
    assert.ok(r.failures.some((f) => f.includes('chart')), 'chart 0 件が失敗になっていない');
  });

  ok('ツール不在は既定で失敗にし、抜け道の環境変数名を必ず示す', () => {
    const r = check({ repoRoot: tmp, allowMissingTools: false });
    const usable = REQUIRED_TOOLS.every((b) => hasTool(b));
    if (usable) return; // ツールが在る環境ではこの分岐を試験できない
    assert.ok(r.failures.length > 0, 'ツール不在なのに失敗していない');
    assert.ok(r.failures.some((f) => f.includes(ALLOW_MISSING_TOOLS_ENV)), '抜け道の名前を示していない');
  });

  // 🔴 **在るツールを「無い」と言わないこと。** 従前 `hasTool` は `command -v` を `shell: true` で
  // 起こしており、Windows（`cmd.exe`）では `command` 組み込みが無いため**常に全欠落**を返した。
  // 誤報は「検証を飛ばした」ではなく「検証できない」の形で出るので、**在る側から確かめる**。
  // 陽性対照を対で置く: `node` は必ず在る（本試験を走らせている実行系そのもの）。
  ok('hasTool: 実行系そのもの（node）を「在る」と判定する（在るものを無いと言わない）', () => {
    assert.ok(hasTool('node'), 'node を検出できていない。hasTool が platform 依存で壊れている');
  });

  ok('hasTool: 実在しない名前は「無い」と判定する（陰性対照）', () => {
    assert.ok(
      !hasTool('msp-definitely-not-a-real-binary-xyz'),
      '存在しない実行ファイルを「在る」と判定した',
    );
  });

  ok('抜け道を立てたときは notice を出し、検査していない旨を明示する', () => {
    const r = check({ repoRoot: tmp, allowMissingTools: true });
    const usable = REQUIRED_TOOLS.every((b) => hasTool(b));
    if (usable) return;
    assert.strictEqual(r.skipped, true);
    assert.ok(r.notices.some((x) => x.includes('検査していない')), '検査していない旨が出ていない');
  });

  ok('kubeconform が居なければ helm / kubectl が在っても fail-closed になる（3 点とも要る）', () => {
    const hasKubeconform = hasTool('kubeconform');
    if (hasKubeconform) return; // kubeconform が在る環境ではこの分岐を試験できない
    const r = check({ repoRoot: tmp, allowMissingTools: false });
    assert.ok(r.failures.some((f) => f.includes('kubeconform')), 'kubeconform 不在が失敗理由に出ていない');
  });

  // ---- 要点 7（#1867）: Loki・Tempo の前段（条件 1）と到達の制限（条件 2）。陰性・陽性対照を合成の描画と実物の描画の両方で固定する。
  const NS = 'microservices-platform';
  /** 合成の描画: 製品（loopback 待ちの config）＋任意で前段・NetworkPolicy・Service。opts で 1 か所ずつ壊す。 */
  const obsFixture = (prod, { front = true, netpol = true, secret = true, listen = '127.0.0.1', svcTarget = 'http', from = 'narrow' } = {}) => {
    const upPort = prod === 'loki' ? 3101 : 3201;
    const svcPort = prod === 'loki' ? 3100 : 3200;
    const docs = [
      ['apiVersion: v1', 'kind: ConfigMap', 'metadata:', `  name: ${prod}-config`, `  namespace: ${NS}`, 'data:', '  config.yaml: |',
        '    server:', `      http_listen_address: ${listen}`, `      http_listen_port: ${upPort}`, `      grpc_listen_address: ${listen}`].join('\n'),
      ['apiVersion: apps/v1', 'kind: Deployment', 'metadata:', `  name: ${prod}`, `  namespace: ${NS}`, 'spec:', '  selector:', `    matchLabels: { app: ${prod} }`,
        '  template:', '    metadata:', `      labels: { app: ${prod} }`, '    spec:', '      containers:',
        `        - name: ${prod}`, `          image: grafana/${prod}:1.0.0`, `          args: ["-config.file=/etc/${prod}/config.yaml"]`,
        ...(prod === 'tempo' ? ['          ports:', '            - { containerPort: 4317, name: otlp-grpc }'] : []),
        '          volumeMounts:', `            - { name: config, mountPath: /etc/${prod} }`,
        ...(front ? ['        - name: proxy', '          image: caddy:2.11-alpine', '          ports:', `            - { containerPort: ${svcPort}, name: http }`,
          ...(secret ? ['          env:', '            - name: TOKEN', '              valueFrom:', '                secretKeyRef: { name: obs-gate, key: reader }'] : [])] : []),
        '      volumes:', '        - name: config', '          configMap:', `            name: ${prod}-config`].join('\n'),
      ['apiVersion: v1', 'kind: Service', 'metadata:', `  name: ${prod}`, `  namespace: ${NS}`, 'spec:', `  selector: { app: ${prod} }`, '  ports:',
        `    - { name: http, port: ${svcPort}, targetPort: ${svcTarget} }`].join('\n'),
    ];
    if (netpol) {
      const peer = from === 'narrow' ? ['        - podSelector:', '            matchLabels: { app: grafana }'] : from === 'all-pods' ? ['        - podSelector: {}'] : ['        - ipBlock:', '            cidr: 0.0.0.0/0'];
      const rule = from === 'none' ? ['    - ports:', `        - { protocol: TCP, port: ${svcPort} }`] : ['    - from:', ...peer];
      docs.push(['apiVersion: networking.k8s.io/v1', 'kind: NetworkPolicy', 'metadata:', `  name: ${prod}-ingress`, `  namespace: ${NS}`, 'spec:', '  podSelector:',
        `    matchLabels: { app: ${prod} }`, '  policyTypes: [Ingress]', '  ingress:', ...rule].join('\n'));
    }
    return docs.join('\n---\n') + '\n';
  };
  const front = (yaml) => obsFront.checkObservabilityFront(yaml, 'fixture');
  const has = (r, re) => r.problems.some((p) => re.test(p));

  for (const prod of ['loki', 'tempo']) {
    ok(`#1867 ${prod}: 前段も NetworkPolicy も無い → 赤（条件 1・2 の両方）`, () => {
      const r = front(obsFixture(prod, { front: false, netpol: false }));
      assert.ok(has(r, /条件 1/) && has(r, /条件 2/), r.problems.join('\n'));
    });
    ok(`#1867 ${prod}: 前段だけ → 赤（条件 2）`, () => {
      const r = front(obsFixture(prod, { netpol: false }));
      assert.ok(has(r, /条件 2/) && !has(r, /条件 1/), r.problems.join('\n'));
    });
    ok(`#1867 ${prod}: NetworkPolicy だけ → 赤（条件 1）`, () => {
      const r = front(obsFixture(prod, { front: false }));
      assert.ok(has(r, /条件 1/) && !has(r, /条件 2/), r.problems.join('\n'));
    });
    ok(`#1867 ${prod}: 両方 → 緑（判定した数を返す）`, () => {
      const r = front(obsFixture(prod));
      assert.deepStrictEqual(r.problems, []);
      assert.strictEqual(r.found.get(prod), 1);
    });
    // 名前だけで通さない（1 か所ずつ壊す）
    for (const [what, opts, re] of [
      ['前段が Secret を受け取らない（素通しのプロキシ）', { secret: false }, /条件 1.*Secret/],
      ['製品が 0.0.0.0 で待つ（前段を迂回できる）', { listen: '0.0.0.0' }, /条件 1.*loopback でない/],
      ['Service の宛先が前段の口でない', { svcTarget: prod === 'loki' ? 3101 : 3201 }, /条件 1.*Service\//],
      ['NetworkPolicy の from が podSelector: {}（同じ名前空間の全 Pod）', { from: 'all-pods' }, /条件 2.*広い/],
      ['NetworkPolicy の from が ipBlock 0.0.0.0/0', { from: 'ip' }, /条件 2.*ipBlock/],
      ['NetworkPolicy の規則に from が無い（全ての送り元から届く）', { from: 'none' }, /条件 2.*from が無い/],
    ]) {
      ok(`#1867 ${prod}: ${what} → 赤`, () => {
        const r = front(obsFixture(prod, opts));
        assert.ok(has(r, re), r.problems.join('\n') || '(問題なし)');
      });
    }
  }

  ok('#1867: コンテナ名を gate にしても、イメージがプロキシでなければ前段と数えない（名前で通さない）', () => {
    const y = obsFixture('loki').replace('caddy:2.11-alpine', 'busybox:1.36').replace('- name: proxy', '- name: gate');
    assert.ok(has(front(y), /条件 1.*前段.*無い/));
  });

  ok('#1867: 名指しの NetworkPolicy があっても、Pod を選ぶ別の広い NetworkPolicy があれば赤（許可の和）／NotIn で外せば緑', () => {
    const wide = (notIn) => ['apiVersion: networking.k8s.io/v1', 'kind: NetworkPolicy', 'metadata:', '  name: allow-intra-namespace', `  namespace: ${NS}`, 'spec:',
      '  podSelector:', '    matchExpressions:', '      - key: app', '        operator: NotIn', `        values: [${notIn}]`, '  policyTypes: [Ingress]',
      '  ingress:', '    - from:', '        - podSelector: {}'].join('\n');
    assert.ok(has(front(`${obsFixture('loki')}---\n${wide('seaweedfs')}\n`), /allow-intra-namespace.*広い/));
    assert.deepStrictEqual(front(`${obsFixture('loki')}---\n${wide('seaweedfs, loki')}\n`).problems, []);
  });

  ok('#1867: 名指しでない NetworkPolicy（podSelector: {} の既定拒否）だけでは条件 2 を満たさない', () => {
    const y = obsFixture('loki', { netpol: false }) + ['---', 'apiVersion: networking.k8s.io/v1', 'kind: NetworkPolicy', 'metadata:', '  name: default-deny', `  namespace: ${NS}`,
      'spec:', '  podSelector: {}', '  policyTypes: [Ingress]', ''].join('\n');
    assert.ok(has(front(y), /名指しで選ぶ/));
  });

  ok('#1867: 対象なし（Loki・Tempo のイメージが無い描画）は何も言わず、promtail・loki 以外の名前は対象にしない', () => {
    const r = front(['apiVersion: apps/v1', 'kind: Deployment', 'metadata:', '  name: x', 'spec:', '  template:', '    spec:', '      containers:',
      '        - name: a', '          image: grafana/promtail:3.0.0', '        - name: b', '          image: example/lokiish:1', ''].join('\n'));
    assert.deepStrictEqual([r.problems, [...r.found]], [[], []]);
    assert.strictEqual(obsFront.imageSegment('registry:5000/mirror/loki:3.0.0@sha256:' + 'a'.repeat(64)), 'loki');
  });

  // 実物の描画（ツールが在る環境だけ。CI の deploy-manifests ジョブは helm / kubectl を入れている）。
  if (hasTool('helm')) {
    ok('#1867 実物の chart: 既定と ci/*.yaml の全派生で「対象なし」（陰性対照）、Loki・Tempo を足すと前段・NetworkPolicy・両方の 4 通りで期待どおり', () => {
      const chart = discoverCharts()[0];
      for (const valuesFile of [null, ...discoverChartCiValues(chart)]) {
        const r = run('helm', ['template', 'ci-check', chart, ...RENDER_IMAGE_TAG_ARGS, ...(valuesFile ? ['-f', valuesFile] : [])]);
        assert.ok(r.ok, r.out);
        const base = front(r.out);
        assert.deepStrictEqual([base.problems, [...base.found]], [[], []], `${valuesFile || '既定'} に Loki・Tempo が既に居る`);
      }
      const rendered = run('helm', ['template', 'ci-check', chart, ...RENDER_IMAGE_TAG_ARGS]).out;
      assert.ok(/values: \[seaweedfs\]/.test(rendered), '前提: chart の allow-intra-namespace が NotIn [seaweedfs] で描かれる');
      for (const prod of ['loki', 'tempo']) {
        const add = (opts) => `${rendered}\n---\n${obsFixture(prod, opts)}`;
        assert.ok(has(front(add({ front: false, netpol: false })), /条件 1/) && has(front(add({ front: false, netpol: false })), /条件 2/));
        assert.ok(has(front(add({ netpol: false })), /条件 2/), `${prod} 前段だけが赤にならない`);
        assert.ok(has(front(add({ front: false })), /条件 1/), `${prod} NetworkPolicy だけが赤にならない`);
        // 両方を足しても、chart の allow-intra-namespace（同じ名前空間の全 Pod から）が選んでいる限り赤（許可の和）。
        assert.ok(has(front(add({})), /allow-intra-namespace.*広い/), `${prod} 両方でも allow-intra-namespace の穴を見逃した`);
        const excluded = add({}).replace('values: [seaweedfs]', `values: [seaweedfs, ${prod}]`);
        assert.deepStrictEqual(front(excluded).problems, [], `${prod} 両方＋allow-intra-namespace から外す、が緑にならない`);
      }
    });
  }
  if (hasTool('kubectl')) {
    ok('#1867 実物の経路 B（deploy/local/observability）: 緑（陽性対照）／前段のコンテナ・NetworkPolicy を外すと赤（変異）', () => {
      const r = run('kubectl', ['kustomize', 'deploy/local/observability']);
      assert.ok(r.ok, r.out);
      const base = front(r.out);
      assert.deepStrictEqual(base.problems, []);
      assert.deepStrictEqual([base.found.get('loki'), base.found.get('tempo')], [1, 1]);
      const noNetpol = r.out.split(/^---\n/m).filter((d) => !/^kind: NetworkPolicy$/m.test(d)).join('---\n');
      assert.ok(has(front(noNetpol), /loki.*条件 2/) && has(front(noNetpol), /tempo.*条件 2/));
      const noGate = r.out.replace(/image: caddy:/g, 'image: busybox:');
      assert.ok(has(front(noGate), /loki.*条件 1/) && has(front(noGate), /tempo.*条件 1/));
    });
  }

  ok('IADR-0587 要点 8: 実物の compose はホストへ出す口が全件ループバック（陽性対照）／1 件から 127.0.0.1 を外すと赤（変異）', () => {
    const r = checkComposeHostPortsAll(REPO_ROOT);
    assert.deepStrictEqual(r.failures, []);
    assert.ok(r.count >= 2, `口を数えられていない（${r.count} 件）`);
    const rel = discoverComposeFiles(REPO_ROOT)[0];
    const text = fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');
    const mutated = text.replace('"127.0.0.1:5000:8080"', '"5000:8080"');
    assert.notStrictEqual(mutated, text, '前提: BFF の口が "127.0.0.1:5000:8080" で書かれている');
    const m = hostPorts.checkComposeHostPorts(mutated, rel, egress.splitComposeServices);
    assert.strictEqual(m.count, r.count);
    assert.strictEqual(m.problems.length, 1, m.problems.join('\n'));
    assert.match(m.problems[0], /サービス bff: "5000:8080" がループバックに限られていない/);
  });

  ok('IADR-0587 要点 8: 短い形式・流れ形式・長い形式・network_mode を形ごとに判定する（読めない形は赤）', () => {
    const judge = (svcLines) => hostPorts.checkComposeHostPorts(['services:', '  s:', ...svcLines, ''].join('\n'), 'c.yml', egress.splitComposeServices);
    const green = (lines, count) => {
      const r = judge(lines);
      assert.deepStrictEqual(r.problems, [], lines.join('\n'));
      assert.strictEqual(r.count, count, lines.join('\n'));
    };
    const red = (lines, re) => {
      const r = judge(lines);
      assert.ok(r.problems.some((p) => re.test(p)), `${lines.join('\n')}\n→ ${r.problems.join(' / ') || '(問題なし)'}`);
    };
    green(['    ports:', '      - "127.0.0.1:5432:5432"  # コメント', "      - '127.0.0.1:1:2'", '      - 127.0.0.1::8080'], 3);
    green(['    ports: ["127.0.0.1:1:1", "127.0.0.1:2:2"]'], 2);
    green(['    ports:', '      - target: 8080', '        published: "5000"', '        host_ip: 127.0.0.1'], 1);
    green(['    ports:', '      - { target: 80, published: 8080, host_ip: "127.0.0.1" }'], 1);
    green(['    expose:', '      - "8080"'], 0);
    red(['    ports:', '      - "5432:5432"'], /"5432:5432" がループバックに限られていない/);
    red(['    ports:', '      - 5432:5432'], /ループバックに限られていない/);
    red(['    ports:', '      - "8080"'], /"8080" がループバックに限られていない/);
    red(['    ports:', '      - "0.0.0.0:1:1"'], /ループバックに限られていない/);
    red(['    ports:', '      - "${BIND_IP:-127.0.0.1}:1:1"'], /ループバックに限られていない/);
    red(['    ports:', '      - "127.0.0.1:8080"'], /形を読めない/);
    red(['    ports: ["1:1"]'], /"1:1" がループバックに限られていない/);
    red(['    ports: "1:1"'], /ports: の形を読めない/);
    red(['    ports:', '      - target: 8080', '        published: "5000"'], /host_ip が 無い/);
    red(['    ports:', '      - target: 8080', '        host_ip: 0.0.0.0'], /host_ip が 0\.0\.0\.0/);
    red(['    ports:', '      - { target: 80, published: 8080 }'], /host_ip が 無い/);
    red(['    ports:', '      - published: 8080', '        host_ip: 127.0.0.1'], /target が無い/);
    red(['    ports:', '    image: x'], /ports: に項目が無い/);
    red(['    network_mode: host'], /network_mode: host/);
  });

  ok('IADR-0587 要点 8: 口を 1 件も数えられない compose（空の母集合）は赤', () => {
    mk('deploy/docker-compose.yml', 'services:\n  s:\n    image: x\n    expose:\n      - "8080"\n');
    const r = checkComposeHostPortsAll(tmp);
    assert.strictEqual(r.count, 0);
    assert.ok(r.failures.some((f) => /ports:）が 0 件だった/.test(f)), r.failures.join('\n'));
    fs.rmSync(path.join(tmp, 'deploy', 'docker-compose.yml'));
    assert.ok(checkComposeHostPortsAll(tmp).failures.some((f) => /0 件だった/.test(f)), 'compose が無い走査でも赤');
  });

  fs.rmSync(tmp, { recursive: true, force: true });
  console.log(`[check-deploy-manifests] self-test OK: ${n} 件`);
}

// ---------------------------------------------------------------- main

function main() {
  const argv = process.argv.slice(2);
  const unknown = argv.filter((a) => a !== '--self-test');
  if (unknown.length > 0) {
    console.error(`[check-deploy-manifests] 未知の引数: ${unknown.join(' ')}`);
    process.exit(2);
  }
  if (argv.includes('--self-test')) {
    selfTest();
    return;
  }

  const r = check({ allowMissingTools: process.env[ALLOW_MISSING_TOOLS_ENV] === '1' });
  for (const notice of r.notices) console.log(notice);

  if (r.failures.length > 0) {
    console.error(`[check-deploy-manifests] ${r.failures.length} 件の失敗:`);
    for (const f of r.failures) console.error(`\n  - ${f}`);
    process.exit(1);
  }

  if (r.skipped) {
    console.log('[check-deploy-manifests] 検証は飛ばした（上の notice を参照）。');
    return;
  }
  console.log(
    `[check-deploy-manifests] OK: chart ${r.charts.length} 件 / overlay ${r.overlays.length} 件が` +
      `レンダリングでき、スキーマにも適合する。描画結果・compose・Testcontainers で` +
      ` ${egress.PRODUCTS.map((p) => p.name).join(' / ')} の既定の外部通信が止まっている（#1841）。` +
      ` Loki・Tempo は現れた描画で前段と NetworkPolicy の両方が揃っている（#1867）。` +
      ` compose のホストへ出す口はループバックに限られている（IADR-0587）。` +
      `\n  chart:   ${r.charts.join(', ')}\n  overlay: ${r.overlays.join(', ')}`,
  );
}

if (require.main === module) main();

module.exports = {
  check,
  checkEgressWithoutTools,
  checkComposeHostPortsAll,
  discoverComposeFiles,
  discoverOverlays,
  discoverCharts,
  discoverChartCiValues,
  ALLOW_MISSING_TOOLS_ENV,
  SCHEMA_LOCATIONS,
  CRDS_CATALOG_REF,
};
