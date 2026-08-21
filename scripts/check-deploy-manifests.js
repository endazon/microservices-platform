#!/usr/bin/env node
/**
 * NFR / #783（#442 子 5）: deploy/ の chart と overlay が「レンダリングできる」ことを機械で検査する。
 *
 * 計画 ADR-0007（CI/CD）・ADR-0021（エッジ・実行基盤）。CI に helm / kustomize の検証ジョブが
 * 1 つも無く、chart や overlay が壊れてもマージまで気づけなかった。
 *
 * ## 設計の要点（3 つとも、本リポジトリが実際に踏んだ事故への対処である）
 *
 * 1. **列挙を持たない。** overlay も chart も走査で発見する。ワークフローにも本ファイルにも
 *    名前を書かない。書くと次に増えたとき静かに検査対象から外れる —— `paths:` の片側取りこぼしを
 *    4 回踏んでいる（#558 / #562 / #747 / #801）。**着手時の実測でも、事前調査が 6 件と数えた
 *    overlay は実際には 8 件だった。**
 *
 * 2. **0 件走査で緑を返さない。** 走査が壊れて 0 件になったら exit 1 にする。
 *    「何も無い」と「問題が無い」を同じ出力にしない（「沈黙の exit 0」#797）。
 *
 * 3. **ツール不在は fail-closed。** helm / kubectl が無いとき既定は exit 1 にし、
 *    `DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS=1` のときだけ notice つきで skip する。
 *    **この抜け道を CI が使っていないことは scripts/scripts.repo.test.js が突合する**
 *    （IADR-0209 の `include ⊆ paths` と同じ型）。
 */

'use strict';

const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.join(__dirname, '..');
const DEPLOY_DIR = 'deploy';
const HELM_DIR = path.join(DEPLOY_DIR, 'helm');
const SKIP_DIRS = new Set(['node_modules', '.git', 'bin', 'obj', 'dist', 'coverage', 'charts']);

/** 抜け道の環境変数名。CI がこれを立てていないことを scripts.repo.test.js が固定する。 */
const ALLOW_MISSING_TOOLS_ENV = 'DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS';

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

function hasTool(bin) {
  const r = spawnSync('command', ['-v', bin], { shell: true, encoding: 'utf8' });
  return r.status === 0 && String(r.stdout || '').trim() !== '';
}

function run(bin, args, cwd = REPO_ROOT) {
  const r = spawnSync(bin, args, { cwd, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  return {
    ok: r.status === 0,
    out: `${r.stdout || ''}${r.stderr || ''}`.trim(),
  };
}

/** 検査本体。結果オブジェクトを返す（プロセスは終了させない — 自己試験から呼べるようにするため）。 */
function check({ repoRoot = REPO_ROOT, allowMissingTools = false } = {}) {
  const overlays = discoverOverlays(repoRoot);
  const charts = discoverCharts(repoRoot);
  const failures = [];
  const notices = [];

  // 要点 2: 0 件走査で緑を返さない。
  if (overlays.length === 0) {
    failures.push(`overlay（${DEPLOY_DIR}/**/kustomization.yaml）が 0 件だった。走査が壊れている可能性がある。`);
  }
  if (charts.length === 0) {
    failures.push(`chart（${HELM_DIR}/**/Chart.yaml）が 0 件だった。走査が壊れている可能性がある。`);
  }
  if (failures.length > 0) return { overlays, charts, failures, notices, skipped: false };

  // 要点 3: ツール不在は fail-closed。
  const missing = ['helm', 'kubectl'].filter((b) => !hasTool(b));
  if (missing.length > 0) {
    if (!allowMissingTools) {
      failures.push(
        `${missing.join(' / ')} が PATH にありません。chart / overlay の検証には両方が要ります。\n` +
          `  導入するか、意図的に飛ばす場合のみ ${ALLOW_MISSING_TOOLS_ENV}=1 を立ててください。\n` +
          `  **CI では立てないこと**（scripts/scripts.repo.test.js が突合します）。`,
      );
      return { overlays, charts, failures, notices, skipped: false };
    }
    notices.push(
      `notice: ${missing.join(' / ')} が無いため chart / overlay の検証を飛ばした` +
        `（${ALLOW_MISSING_TOOLS_ENV}=1）。overlay ${overlays.length} 件 / chart ${charts.length} 件は**検査していない**。`,
    );
    return { overlays, charts, failures, notices, skipped: true };
  }

  for (const c of charts) {
    const lint = run('helm', ['lint', c], repoRoot);
    if (!lint.ok) failures.push(`helm lint が失敗した: ${c}\n${lint.out}`);
    const tpl = run('helm', ['template', 'ci-check', c], repoRoot);
    if (!tpl.ok) failures.push(`helm template が失敗した: ${c}\n${tpl.out}`);
  }

  for (const o of overlays) {
    const built = run('kubectl', ['kustomize', o], repoRoot);
    if (!built.ok) failures.push(`kubectl kustomize が失敗した: ${o}\n${built.out}`);
    else if (built.out.trim() === '') failures.push(`kubectl kustomize の出力が空だった: ${o}`);
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

  ok('走査は Chart.yaml を持つディレクトリを拾い、charts/（依存の展開先）は除外する', () => {
    mk('deploy/helm/msp/Chart.yaml', 'name: msp\n');
    mk('deploy/helm/msp/charts/upstream/Chart.yaml', 'name: upstream\n');
    assert.deepStrictEqual(discoverCharts(tmp), ['deploy/helm/msp']);
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
    const usable = ['helm', 'kubectl'].every((b) => hasTool(b));
    if (usable) return; // ツールが在る環境ではこの分岐を試験できない
    assert.ok(r.failures.length > 0, 'ツール不在なのに失敗していない');
    assert.ok(r.failures.some((f) => f.includes(ALLOW_MISSING_TOOLS_ENV)), '抜け道の名前を示していない');
  });

  ok('抜け道を立てたときは notice を出し、検査していない旨を明示する', () => {
    const r = check({ repoRoot: tmp, allowMissingTools: true });
    const usable = ['helm', 'kubectl'].every((b) => hasTool(b));
    if (usable) return;
    assert.strictEqual(r.skipped, true);
    assert.ok(r.notices.some((x) => x.includes('検査していない')), '検査していない旨が出ていない');
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
      `レンダリングできる。\n  chart:   ${r.charts.join(', ')}\n  overlay: ${r.overlays.join(', ')}`,
  );
}

if (require.main === module) main();

module.exports = { check, discoverOverlays, discoverCharts, ALLOW_MISSING_TOOLS_ENV };
