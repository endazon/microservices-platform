#!/usr/bin/env node
'use strict';
/*
 * check-collector-self-telemetry.js — #1090 / #546 / ADR-0006 / NFR-21
 *
 * **すべての OTel Collector 設定が、自己テレメトリの待受を明示していること**を機械で見る。
 *
 * ## なぜ置くか（「同型の事故が 2 回起きたら」の 2 回目）
 *
 *   1 回目: 経路B の collector に `containerPort: 8888` / Service 8888 /
 *           `service.telemetry.metrics.address` が無く、`up{job="otel-collector"}` が
 *           恒常的に 0 だった（IADR-0304 §配備して初めて分かったこと。**検査器は足さないと明記**）。
 *   2 回目: **転送構成にだけ** `service.telemetry.metrics.address` が無かった（#1090）。
 *           opt-in の apply で collector を差し替えた瞬間に、**Prometheus の唯一の scrape 対象**が消える。
 *
 * CLAUDE.md の規約どおり 1 回目は記録に留め、2 回目で足す。
 *
 * ## なぜ単一情報源にできないか（＝この検査器が要る理由）
 *
 * 3 つの設定は**排他的な別配備の全体設定**であり、共有できる形が無い。
 *   - compose の設定は bind mount で kustomize の管理外
 *   - 経路B の 2 つは**同名 ConfigMap を上書きし合う**排他関係にあり、
 *     kustomize の strategic merge patch は `data` の**文字列の内側**へ届かない
 *   - kustomize は root 外のファイルを参照できず、compose 用の設定を overlay から読めない
 *     （`prometheus.yaml` / `grafana.yaml` が既に同じ理由で inline 二重管理を選んでいる）
 * したがって **乖離を消すのではなく、乖離を止める。**
 *
 * ## 検査
 *
 *   1. collector 設定を**走査で発見する**（列挙を持たない。名前を書くと次に増えたとき静かに漏れる）
 *   2. 各設定が `service.telemetry.metrics.address` を持つ
 *   3. その待受が**全ファイルで同一**である（片方だけ版上げ・変更されるのを止める）
 *   4. 待受のポートが、Prometheus の scrape 対象ポートと**一致する**
 *      （宣言はあるが番号が食い違う、という次の形の事故を先に塞ぐ）
 *   5. 待受が loopback でない（`localhost` / `127.0.0.1` はコンテナ外から到達できない）
 *
 * ## fail-closed（#664 / IADR-0130）
 *
 * 走査結果が 0 件なら fail する。「検査しているつもりで何も見ていない」状態を緑で返さない。
 *
 * 実行: node scripts/check-collector-self-telemetry.js [--self-test]
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.join(__dirname, '..');
const DEPLOY_DIR = 'deploy';
const SKIP_DIRS = new Set(['node_modules', '.git', 'bin', 'obj', 'dist', 'coverage', 'charts']);

/**
 * collector 設定だと判定する印。
 * **ファイル名では判定しない** —— `otel-collector.yaml` / `otel-collector-forward.yaml` /
 * `otel-collector-config.yaml` と既に 3 通りあり、名前で絞ると次の 4 通り目を落とす。
 * `receivers.otlp` ＋ `service.pipelines` を持つものを collector 設定とみなす。
 */
function isCollectorConfig(text) {
  return /^\s*otlp:\s*$/m.test(text) && /^\s*pipelines:\s*$/m.test(text) && /^\s*exporters:\s*$/m.test(text);
}

/**
 * `service.telemetry.metrics.address` の値を拾う。
 * **字下げを固定しない**（ConfigMap の inline は 4 桁ぶん深い）。`telemetry:` → `metrics:` →
 * `address:` の順に現れることだけを見る。`receivers` 側の `endpoint:` とはキー名が違うので取り違えない。
 */
function selfTelemetryAddress(text) {
  const m = text.match(/^([ \t]*)telemetry:[ \t]*$/m);
  if (!m) return null;
  const rest = text.slice(m.index + m[0].length);
  const metrics = rest.match(/^[ \t]*metrics:[ \t]*$/m);
  if (!metrics) return null;
  const after = rest.slice(metrics.index + metrics[0].length);
  const addr = after.match(/^[ \t]*address:[ \t]*(\S+)[ \t]*$/m);
  return addr ? addr[1] : null;
}

/** Prometheus の scrape 対象から otel-collector のポートを拾う（`targets: ['otel-collector:8888']`）。 */
function scrapedCollectorPorts(text) {
  return [...text.matchAll(/otel-collector:(\d+)/g)].map((m) => m[1]);
}

/** ディレクトリを再帰的に走査して YAML を集める。 */
function walk(dir, out = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    if (SKIP_DIRS.has(e.name)) continue;
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, out);
    else if (/\.ya?ml$/.test(e.name)) out.push(p);
  }
  return out;
}

/**
 * 検査本体。**読み込み済みのテキストを受け取る純関数**（自己試験から呼べるようにする）。
 * @param {{path: string, text: string}[]} collectorFiles
 * @param {{path: string, text: string}[]} promFiles
 */
function findIssues(collectorFiles, promFiles) {
  const issues = [];
  const addresses = new Map();

  for (const { path: p, text } of collectorFiles) {
    const addr = selfTelemetryAddress(text);
    if (addr === null) {
      issues.push(
        `${p}: service.telemetry.metrics.address が無い` +
        '（collector の既定は版によって localhost:8888 と :8888 の間で変わる。' +
        '既定任せだと版上げで静かに scrape 不能になる）',
      );
      continue;
    }
    if (/^(localhost|127\.0\.0\.1)\b|^\[?::1\]?:/.test(addr)) {
      issues.push(`${p}: 待受が loopback（${addr}）。コンテナ外の Prometheus から到達できない`);
    }
    addresses.set(p, addr);
  }

  const distinct = new Set(addresses.values());
  if (distinct.size > 1) {
    const detail = [...addresses.entries()].map(([p, a]) => `${p}=${a}`).join(' / ');
    issues.push(`collector 設定ごとに待受が違う（${detail}）。経路によって scrape の成否が変わる`);
  }

  // 宣言のポートと、Prometheus が実際に取りに行くポートを突き合わせる。
  const scraped = new Set(promFiles.flatMap(({ text }) => scrapedCollectorPorts(text)));
  for (const [p, addr] of addresses) {
    const port = String(addr).split(':').pop();
    if (scraped.size > 0 && !scraped.has(port)) {
      issues.push(
        `${p}: 待受ポート ${port} が Prometheus の scrape 対象（${[...scraped].join(', ')}）と一致しない`,
      );
    }
  }

  return { issues, collectorCount: collectorFiles.length, scrapedPorts: [...scraped] };
}

function selfTest() {
  const assert = require('assert');
  const good = [
    'receivers:\n  otlp:\n    protocols:\n      grpc:\n        endpoint: 0.0.0.0:4317\n' +
      'exporters:\n  debug: {}\n' +
      'service:\n  telemetry:\n    metrics:\n      address: 0.0.0.0:8888\n' +
      '  pipelines:\n    traces:\n      exporters: [debug]\n',
    // ConfigMap の inline（字下げが 4 桁深い）。**同じ検査で拾えること**を固定する。
    '    receivers:\n      otlp:\n        protocols: {}\n' +
      '    exporters:\n      debug: {}\n' +
      '    service:\n      telemetry:\n        metrics:\n          address: 0.0.0.0:8888\n' +
      '      pipelines:\n        traces:\n          exporters: [debug]\n',
  ].map((text, i) => ({ path: `f${i}.yaml`, text }));
  const prom = [{ path: 'prom.yml', text: "  - targets: ['otel-collector:8888']\n" }];

  let passed = 0;
  const t = (name, fn) => { fn(); passed++; process.stdout.write(`  ok  ${name}\n`); };

  t('揃っていれば違反 0 件（字下げの違う inline も拾う）', () =>
    assert.deepStrictEqual(findIssues(good, prom).issues, []));

  t('collector 設定の判定はファイル名に依存しない', () => {
    assert.ok(isCollectorConfig(good[0].text));
    assert.ok(isCollectorConfig(good[1].text));
    assert.ok(!isCollectorConfig('apiVersion: v1\nkind: Service\n'));
  });

  t('宣言が無いファイルを検出する（変異試験。これが #1090 そのもの）', () => {
    const broken = good[0].text.replace(/  telemetry:\n    metrics:\n      address: \S+\n/, '');
    const r = findIssues([...good, { path: 'missing.yaml', text: broken }], prom);
    assert.ok(r.issues.some((x) => x.startsWith('missing.yaml')), JSON.stringify(r.issues));
  });

  t('待受が食い違うことを検出する（変異試験）', () => {
    const other = { path: 'other.yaml', text: good[0].text.replace('0.0.0.0:8888', '0.0.0.0:9999') };
    const r = findIssues([good[0], other], prom);
    assert.ok(r.issues.some((x) => x.includes('待受が違う')), JSON.stringify(r.issues));
  });

  t('scrape 対象と一致しないポートを検出する（変異試験）', () => {
    const moved = good.map((f) => ({ ...f, text: f.text.replace(/0\.0\.0\.0:8888/, '0.0.0.0:9999') }));
    const r = findIssues(moved, prom);
    assert.ok(r.issues.some((x) => x.includes('一致しない')), JSON.stringify(r.issues));
  });

  t('loopback の待受を検出する（変異試験。宣言はあるが到達できない形）', () => {
    const lo = good.map((f) => ({ ...f, text: f.text.replace(/0\.0\.0\.0:8888/, 'localhost:8888') }));
    const r = findIssues(lo, prom);
    assert.ok(r.issues.some((x) => x.includes('loopback')), JSON.stringify(r.issues));
  });

  t('Prometheus 側を読めなくてもポート照合以外は動く（scrape 0 件なら照合しない）', () =>
    assert.deepStrictEqual(findIssues(good, []).issues, []));

  process.stdout.write(`\n✓ self-test: ${passed} 件すべて通過\n`);
}

function collect(repoRoot = REPO_ROOT) {
  const files = walk(path.join(repoRoot, DEPLOY_DIR));
  const collectorFiles = [];
  const promFiles = [];
  for (const abs of files) {
    let text;
    try {
      text = fs.readFileSync(abs, 'utf8');
    } catch {
      continue;
    }
    const rel = path.relative(repoRoot, abs).split(path.sep).join('/');
    if (isCollectorConfig(text)) collectorFiles.push({ path: rel, text });
    if (/^\s*scrape_configs:\s*$/m.test(text)) promFiles.push({ path: rel, text });
  }
  return { collectorFiles, promFiles };
}

function main(argv) {
  if (argv.includes('--self-test')) { selfTest(); return 0; }

  const { collectorFiles, promFiles } = collect();

  // #664 / IADR-0130: 0 件走査で緑を返さない。
  if (collectorFiles.length === 0) {
    console.error('[check-collector-self-telemetry] collector 設定を 1 件も拾えませんでした。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    return 1;
  }

  const { issues, collectorCount, scrapedPorts } = findIssues(collectorFiles, promFiles);
  if (issues.length === 0) {
    console.log(
      `[check-collector-self-telemetry] OK: collector 設定 ${collectorCount} 件が同じ待受を宣言し、` +
      `Prometheus の scrape 対象ポート（${scrapedPorts.join(', ') || '照合対象なし'}）と一致します。`,
    );
    for (const f of collectorFiles) console.log(`  - ${f.path}`);
    return 0;
  }
  console.error(`[check-collector-self-telemetry] 違反 ${issues.length} 件:`);
  for (const i of issues) console.error(`  - ${i}`);
  return 1;
}

module.exports = { findIssues, isCollectorConfig, selfTelemetryAddress, scrapedCollectorPorts, collect, selfTest };

if (require.main === module) process.exit(main(process.argv.slice(2)));
