#!/usr/bin/env node
'use strict';
/*
 * resident-memory.test.js
 * NFR, IADR-0539 (#1927): **常駐メモリを下げた設定を、実際に描いて固定する。** あわせて、絞った観測スタックで
 * **H-1 / AST#690 の計測に要る Prometheus の系列の経路が残っている**ことを固定する。
 *
 * 固定するもの:
 *   1. helm（既定・values-local）: `services` が描く .NET の Deployment すべてに DOTNET_gcServer=0・DOTNET_GCConserveMemory=5・
 *      DOTNET_GCHeapHardLimitPercent=0x3C（60%）が 1 回ずつ（**env の値は 16 進数として読み、60 になること**を見る）。.NET でない Deployment（frontend・seaweedfs・wiki-js、opt-in の embedding・
 *      synthetic-monitor）には 1 つも無い。
 *   2. helm: サービス単位の上書き（値の変更・空文字で描かない）が効く。extraEnv / extraEnvAppend の同名は描画で落ちる。
 *   3. kustomize（deploy/local/observability と -persistence）＋collector と Prometheus のソース（描画は ConfigMap の値を 1 行の
 *      引用符つき文字列へ畳むので、字面はソースで読む）: collector の metrics パイプラインが otlp を受け、
 *      prometheusremotewrite へ出し、処理は memory_limiter・batch だけ（値で落とす段が無い）。Prometheus は remote write の
 *      受け口を開き、保持は 30d 以上。規則 knowledge_health_report_total（H-1）が残る。
 *      —— H-1（knowledge_health_report_total）も AST#690（ast.trade_cycle.*）も、scrape でも recording rule でもなく
 *      この 1 本の経路で入る。系列名で絞る段（filter / transform）を足すと、どちらも黙って消える。
 *   4. kustomize（deploy/local/infra）: Keycloak のヒープ上限（MaxRAMPercentage）× limits ＋非ヒープの見積り（512Mi）が limits 以下。
 *   5. 変異: 判定器へ、処理の段を足した・受け口を外した・GC の env を 1 つ落とした描画を渡すと赤になる。
 *
 * 🔴 **helm / kubectl が無ければ落ちる（fail-closed）。** 実際に描くのが本試験の目的であり、黙って飛ばすと「検査していない」と
 *    「問題が無い」が同じ出力になる。CI は static-checks-units（helm・kubectl 導入済み）で走らせる。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm ＋ kubectl）。実行: node scripts/resident-memory.test.js
 */
const assert = require('assert');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const VALUES_LOCAL = 'deploy/local/values-local.yaml';
/** 期待する実効値（10 進）。🔴 **GC の env の値は 16 進数として読まれる**（Microsoft Learn「Garbage collector config settings」
 *  https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector の表:「Environment variable | 0xn or n |
 *  n is interpreted as a hex value in either format」）。`"60"` は 0x60＝96% になる（#1928 のレビュー）。 */
const GC_ENV = {
  DOTNET_gcServer: 0,
  DOTNET_GCConserveMemory: 5,
  DOTNET_GCHeapHardLimitPercent: 60,
};

/** env の値を .NET の実行時と同じく 16 進数として読む（`0x` の有無を問わない）。読めなければ NaN。 */
function hexValue(raw) {
  const m = /^(?:0[xX])?([0-9A-Fa-f]+)$/.exec(raw);
  return m ? parseInt(m[1], 16) : NaN;
}
/** .NET ではない Deployment（別のテンプレートが描く）。GC の env が 1 つでも現れたら誤り。 */
const NON_DOTNET = ['frontend-service', 'seaweedfs', 'wiki-js', 'embedding-service', 'synthetic-monitor'];
/** 非ヒープ（metaspace ≤256m・コードキャッシュ ≤240m・スレッド等）の見積り。ヒープ上限に足して limits を超えないこと。 */
const KEYCLOAK_NON_HEAP_MI = 512;

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

function which(bin) {
  return spawnSync(process.platform === 'win32' ? 'where' : 'which', [bin], { encoding: 'utf8' }).status === 0;
}

function run(cmd, args) {
  const r = spawnSync(cmd, args, { cwd: REPO_ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  return { status: r.status, stdout: r.stdout || '', stderr: r.stderr || '' };
}

function helmTemplate(args) {
  const r = run('helm', ['template', 'msp', CHART, ...args]);
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.stderr}`);
  return r.stdout;
}

function kustomize(dir) {
  const r = run('kubectl', ['kustomize', dir]);
  assert.strictEqual(r.status, 0, `kubectl kustomize ${dir} が失敗した: ${r.stderr}`);
  return r.stdout;
}

/** 描画物を文書へ割り、kind と metadata.name を添える。 */
function documents(rendered) {
  return rendered.split(/^---\s*$/m).map((text) => {
    const kind = (text.match(/^kind:\s*(\S+)/m) || [])[1] || '';
    const meta = text.match(/^metadata:\n((?:[ ].*\n)+)/m);
    const name = meta ? ((meta[1].match(/^ {2}name:\s*(\S+)/m) || [])[1] || '') : '';
    return { kind, name, text };
  });
}

/** `- name: X` に続く `value:` を集める（env の項目だけを拾う。コンテナ名の `- name:` は value を持たない）。 */
function envValues(text, name) {
  const re = new RegExp(`^\\s*- name:\\s*["']?${name}["']?\\s*\\n\\s*value:\\s*["']?([^"'\\n]*)["']?\\s*$`, 'gm');
  return [...text.matchAll(re)].map((m) => m[1]);
}

/** helm の描画の判定器。違反の一覧を返す（空なら合格）。 */
function checkGc(rendered, dotnetNames) {
  const errors = [];
  const deployments = documents(rendered).filter((d) => d.kind === 'Deployment');
  for (const name of dotnetNames) {
    const d = deployments.find((x) => x.name === name);
    if (!d) { errors.push(`.NET の Deployment ${name} が描画に無い`); continue; }
    for (const [env, want] of Object.entries(GC_ENV)) {
      const got = envValues(d.text, env);
      if (got.length !== 1 || hexValue(got[0]) !== want) {
        errors.push(`${name} の ${env} が 16 進数として読んで ${want} の 1 回ではない: ${JSON.stringify(got)}（実効値 ${got.map(hexValue)}）`);
      } else if (want >= 10 && !/^0[xX]/.test(got[0])) {
        // 2 桁以上を 0x 無しで書くと、人は 10 進と読み違える（"60" の事故）。16 進数であることを値から読めるようにする。
        errors.push(`${name} の ${env} が 0x の無い 2 桁以上の値 ${JSON.stringify(got[0])}（16 進数として読まれる。0x を付けて書く）`);
      }
    }
  }
  for (const d of deployments.filter((x) => NON_DOTNET.includes(x.name))) {
    for (const env of Object.keys(GC_ENV)) {
      if (new RegExp(`- name:\\s*["']?${env}["']?\\s*$`, 'm').test(d.text)) errors.push(`.NET でない ${d.name} に ${env} が描かれた`);
    }
  }
  return errors;
}

/** `services` が描く .NET の Deployment 名（`# Source: .../templates/deployment.yaml` の文書）。 */
function dotnetDeployments(rendered) {
  return rendered.split(/^---\s*$/m)
    .filter((t) => /^# Source: microservices-platform\/templates\/deployment\.yaml$/m.test(t) && /^kind:\s*Deployment/m.test(t))
    .map((t) => (t.match(/^metadata:\n(?:[ ].*\n)*? {2}name:\s*(\S+)/m) || [])[1]);
}

/** collector の設定（ソースの inline）から、pipelines の 1 本（例 `metrics`）の受け口・処理・出口を読む。
 *  描画（kubectl kustomize）は ConfigMap の値を引用符つきの 1 行へ畳み直すので、字面の判定はソースで行い、
 *  描画ではその ConfigMap が overlay に載っていることだけを見る。 */
function pipeline(collectorSrc, pipelineName) {
  const at = collectorSrc.search(/^\s+pipelines:\s*$/m);
  if (at < 0) return null;
  const rest = collectorSrc.slice(at);
  const m = rest.match(new RegExp(`^(\\s+)${pipelineName}:\\s*\\n((?:\\1\\s+.*\\n)+)`, 'm'));
  if (!m) return null;
  const list = (key) => {
    const x = m[2].match(new RegExp(`^\\s+${key}:\\s*\\[([^\\]]*)\\]`, 'm'));
    return x ? x[1].split(',').map((v) => v.trim()).filter(Boolean) : null;
  };
  return { receivers: list('receivers'), processors: list('processors'), exporters: list('exporters') };
}

/** 観測スタックの判定器（H-1 / AST#690 の系列の経路）。違反の一覧を返す。 */
function checkMetricsPath({ collectorSrc, prometheusSrc, rendered }) {
  const errors = [];
  if (!documents(rendered).some((d) => d.kind === 'ConfigMap' && d.name === 'otel-collector-config')) {
    errors.push('overlay の描画に otel-collector-config（転送構成）が無い');
  }
  const p = pipeline(collectorSrc, 'metrics');
  if (!p) return errors.concat('otel-collector-config の metrics パイプラインを読めない');
  if (!p.receivers || !p.receivers.includes('otlp')) errors.push(`metrics パイプラインが otlp を受けない（H-1 / AST#690 の系列はサービスの OTLP から入る）: ${p.receivers}`);
  if (!p.exporters || !p.exporters.includes('prometheusremotewrite')) errors.push(`metrics パイプラインが prometheusremotewrite へ出さない: ${p.exporters}`);
  const allowed = new Set(['memory_limiter', 'batch']);
  for (const proc of p.processors || []) {
    if (!allowed.has(proc)) errors.push(`metrics パイプラインに ${proc} が足された（系列を値で落とし得る段。H-1 / AST#690 の系列が黙って消える）`);
  }
  const prom = documents(rendered).find((d) => d.kind === 'Deployment' && d.name === 'prometheus');
  if (!prom) return errors.concat('prometheus の Deployment が無い');
  if (!/--web\.enable-remote-write-receiver/.test(prom.text)) errors.push('prometheus が remote write の受け口を開いていない');
  const ret = prom.text.match(/--storage\.tsdb\.retention\.time=(\d+)d/);
  if (!ret || Number(ret[1]) < 30) errors.push(`prometheus の保持が 30d 未満（[30d] の窓・H-1 の [2h] を評価できない）: ${ret ? ret[0] : '無い'}`);
  if (!/absent_over_time\(knowledge_health_report_total\{/.test(prometheusSrc)) errors.push('H-1 の規則（knowledge_health_report_total）が Prometheus の規則から消えた');
  return errors;
}

/** Keycloak のヒープ上限と limits の整合。 */
function checkKeycloakHeap(rendered) {
  const kc = documents(rendered).find((d) => d.kind === 'Deployment' && d.name === 'keycloak');
  if (!kc) return ['keycloak の Deployment が無い'];
  const heap = envValues(kc.text, 'JAVA_OPTS_KC_HEAP');
  if (heap.length !== 1) return [`JAVA_OPTS_KC_HEAP が 1 回ではない: ${JSON.stringify(heap)}`];
  const pct = (heap[0].match(/-XX:MaxRAMPercentage=(\d+(?:\.\d+)?)/) || [])[1];
  if (!pct) return [`JAVA_OPTS_KC_HEAP に MaxRAMPercentage が無い（Keycloak の既定 70% へ戻り、limits を超えて OOMKilled した #1399 の形）: ${heap[0]}`];
  // kubectl kustomize は resources を block 形式で描く（flow 形式のソースとは字面が違う）。
  const lim = (kc.text.match(/limits:\s*\n(?:\s+(?!memory:)\w+:.*\n)*\s+memory:\s*(\d+)(Mi|Gi)/) || []);
  if (!lim[1]) return ['keycloak の memory の limits を読めない'];
  const limitMi = Number(lim[1]) * (lim[2] === 'Gi' ? 1024 : 1);
  const heapMi = (limitMi * Number(pct)) / 100;
  return heapMi + KEYCLOAK_NON_HEAP_MI <= limitMi ? [] : [`Keycloak のヒープ上限 ${heapMi}Mi ＋非ヒープ ${KEYCLOAK_NON_HEAP_MI}Mi が limits ${limitMi}Mi を超える`];
}

// ---------------------------------------------------------------- tests

for (const bin of ['helm', 'kubectl']) {
  if (!which(bin)) {
    process.stderr.write(`✗ ${bin} が見つからない。本試験は実際に描いて検査するので、${bin} 無しでは何も確かめられない（fail-closed）。\n`);
    process.exit(1);
  }
}

const DEFAULT = helmTemplate([]);
const LOCAL = helmTemplate(['-f', VALUES_LOCAL]);
const OPT_IN = helmTemplate(['-f', VALUES_LOCAL, '-f', `${CHART}/ci/synthetic-monitor-values.yaml`, '--set', 'embedding.enabled=true']);
const DOTNET = dotnetDeployments(DEFAULT);

ok('前提: .NET の Deployment を 15 描く（数が変わったら本試験の母集合を見直す）', () => {
  assert.strictEqual(DOTNET.length, 15, `.NET の Deployment: ${DOTNET.join(', ')}`);
  assert.deepStrictEqual(dotnetDeployments(LOCAL).sort(), [...DOTNET].sort());
});

ok('既定（本番像）: .NET の全 Deployment に GC の 3 つが 1 回ずつ、.NET でないものには無い', () => {
  assert.deepStrictEqual(checkGc(DEFAULT, DOTNET), []);
});

ok('values-local: 同じ', () => {
  assert.deepStrictEqual(checkGc(LOCAL, DOTNET), []);
});

ok('opt-in（embedding・synthetic-monitor）を足しても、それらには描かれない', () => {
  const names = documents(OPT_IN).filter((d) => d.kind === 'Deployment').map((d) => d.name);
  assert.ok(names.includes('embedding-service') && names.includes('synthetic-monitor'), `前提: opt-in が描かれる: ${names}`);
  assert.deepStrictEqual(checkGc(OPT_IN, DOTNET), []);
});

ok('サービス単位の上書き: 値の変更はそのサービスだけ、空文字はそのサービスだけ描かない', () => {
  const changed = helmTemplate(['--set', 'services.graph.dotnetGc.heapHardLimitPercent=0x4B']);
  const graph = documents(changed).find((d) => d.kind === 'Deployment' && d.name === 'graph-service');
  assert.deepStrictEqual(envValues(graph.text, 'DOTNET_GCHeapHardLimitPercent'), ['0x4B']);
  assert.deepStrictEqual(checkGc(changed, DOTNET.filter((n) => n !== 'graph-service')), []);
  const blank = helmTemplate(['--set', 'services.graph.dotnetGc.heapHardLimitPercent=']);
  const g2 = documents(blank).find((d) => d.kind === 'Deployment' && d.name === 'graph-service');
  assert.deepStrictEqual(envValues(g2.text, 'DOTNET_GCHeapHardLimitPercent'), []);
  assert.deepStrictEqual(envValues(g2.text, 'DOTNET_gcServer'), ['0'], '空文字にした鍵だけが消え、他の 2 つは残る');
  assert.deepStrictEqual(checkGc(blank, DOTNET.filter((n) => n !== 'graph-service')), []);
});

ok('global の鍵が無く、サービスにも無いときは描かない（"<nil>" を描かない）', () => {
  const r = helmTemplate(['--set', 'global.dotnetGc.conserveMemory=null']);
  assert.ok(!/<nil>/.test(r), '"<nil>" が描かれた');
  const doc = documents(r).find((d) => d.kind === 'Deployment' && d.name === 'graph-service');
  assert.deepStrictEqual(envValues(doc.text, 'DOTNET_GCConserveMemory'), []);
  assert.deepStrictEqual(envValues(doc.text, 'DOTNET_gcServer'), ['0'], '他の鍵は残る');
});

ok('変異: 0x の無い "60"（16 進数で 96%）へ戻すと赤', () => {
  const mutated = DEFAULT.replace(/(- name: DOTNET_GCHeapHardLimitPercent\n\s*value: )"0x3C"/g, '$1"60"');
  assert.notStrictEqual(mutated, DEFAULT, '前提: 変異を当てられる');
  assert.ok(checkGc(mutated, DOTNET).some((e) => e.includes('実効値 96')), checkGc(mutated, DOTNET).slice(0, 2).join('\n'));
  const noPrefix = DEFAULT.replace(/(- name: DOTNET_GCHeapHardLimitPercent\n\s*value: )"0x3C"/g, '$1"3C"');
  assert.ok(checkGc(noPrefix, DOTNET).some((e) => e.includes('0x の無い')));
});

ok('extraEnv / extraEnvAppend に同じ名前を書くと描画で落ちる（同名の env を 2 つ並べない）', () => {
  for (const key of ['extraEnvAppend', 'extraEnv']) {
    const r = run('helm', ['template', 'msp', CHART, '--set', `services.wiki.${key}[0].name=DOTNET_GCHeapHardLimitPercent`, '--set', `services.wiki.${key}[0].value=90`]);
    assert.notStrictEqual(r.status, 0, `${key} の同名が描けてしまった`);
    assert.match(r.stderr, /IADR-0539/);
  }
});

ok('変異: GC の env を 1 つ落とした描画は赤', () => {
  const mutated = DEFAULT.replace(/^\s*- name: DOTNET_GCConserveMemory\n\s*value: "5"\n/m, '');
  assert.ok(checkGc(mutated, DOTNET).some((e) => e.includes('DOTNET_GCConserveMemory')));
});

const OBS = kustomize('deploy/local/observability');
const OBS_PERSIST = kustomize('deploy/local/observability-persistence');
const fs = require('fs');
const COLLECTOR_SRC = fs.readFileSync(path.join(REPO_ROOT, 'deploy/local/observability/otel-collector-forward.yaml'), 'utf8');
const PROMETHEUS_SRC = fs.readFileSync(path.join(REPO_ROOT, 'deploy/local/observability/prometheus.yaml'), 'utf8');
const OBS_INPUT = { collectorSrc: COLLECTOR_SRC, prometheusSrc: PROMETHEUS_SRC, rendered: OBS };

ok('観測スタック: H-1 / AST#690 の系列の経路（otlp → memory_limiter・batch → prometheusremotewrite → Prometheus）が残る', () => {
  assert.deepStrictEqual(checkMetricsPath(OBS_INPUT), []);
  assert.deepStrictEqual(checkMetricsPath({ ...OBS_INPUT, rendered: OBS_PERSIST }), []);
});

ok('変異: metrics パイプラインに filter を足す・otlp を外す・規則を消す・保持を縮めると赤', () => {
  const filtered = COLLECTOR_SRC.replace(/(receivers: \[otlp, prometheus\/mail-relay, prometheus\/reset-floor\]\n\s*processors: \[memory_limiter, )batch\]/, '$1filter/drop, batch]');
  assert.notStrictEqual(filtered, COLLECTOR_SRC, '前提: 変異を当てられる');
  assert.ok(checkMetricsPath({ ...OBS_INPUT, collectorSrc: filtered }).some((e) => e.includes('filter/drop')));
  const noOtlp = COLLECTOR_SRC.replace(/receivers: \[otlp, prometheus\/mail-relay/, 'receivers: [prometheus/mail-relay');
  assert.notStrictEqual(noOtlp, COLLECTOR_SRC, '前提: 変異を当てられる');
  assert.ok(checkMetricsPath({ ...OBS_INPUT, collectorSrc: noOtlp }).some((e) => e.includes('otlp を受けない')));
  const noRule = PROMETHEUS_SRC.replace(/absent_over_time\(knowledge_health_report_total/g, 'absent_over_time(other_total');
  assert.ok(checkMetricsPath({ ...OBS_INPUT, prometheusSrc: noRule }).some((e) => e.includes('H-1')));
  const shortRet = OBS.replace('--storage.tsdb.retention.time=35d', '--storage.tsdb.retention.time=7d');
  assert.notStrictEqual(shortRet, OBS, '前提: 変異を当てられる');
  assert.ok(checkMetricsPath({ ...OBS_INPUT, rendered: shortRet }).some((e) => e.includes('30d 未満')));
});

ok('観測スタック: Tempo・Loki の本体に GOMEMLIMIT、Loki の ingester はチャンクを短く持つ', () => {
  for (const name of ['tempo', 'loki']) {
    const d = documents(OBS).find((x) => x.kind === 'Deployment' && x.name === name);
    assert.deepStrictEqual(envValues(d.text, 'GOMEMLIMIT'), ['192MiB'], name);
  }
  const cm = documents(OBS).find((x) => x.kind === 'ConfigMap' && x.name === 'loki-config');
  assert.match(cm.text, /^\s+ingester:\n\s+chunk_idle_period: 5m\n\s+max_chunk_age: 30m$/m);
});

const INFRA = kustomize('deploy/local/infra');

ok('Keycloak: ヒープ上限 × limits ＋非ヒープの見積りが limits 以下（MaxRAMPercentage を落とさない）', () => {
  assert.deepStrictEqual(checkKeycloakHeap(INFRA), []);
  const dropped = INFRA.replace(/-XX:MaxRAMPercentage=\d+ ?/, '');
  assert.ok(checkKeycloakHeap(dropped).some((e) => e.includes('MaxRAMPercentage が無い')));
  const tooBig = INFRA.replace(/-XX:MaxRAMPercentage=\d+/, '-XX:MaxRAMPercentage=80');
  assert.ok(checkKeycloakHeap(tooBig).some((e) => e.includes('limits')));
});

process.stdout.write(`\n${passed} tests passed.\n`);
