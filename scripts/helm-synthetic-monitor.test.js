#!/usr/bin/env node
'use strict';
/*
 * helm-synthetic-monitor.test.js
 * NFR-02, NFR-21, ADR-0076 決定 3・4, ADR-0079 決定 1・2, IADR-0378, IADR-0469 (#1287):
 * **helm チャートの合成監視（既定オフ）を、実際に `helm template` で描いて固定する。**
 *
 * 🔴 **守るのは指標の信頼性である。** プローブだけが立ち、標識（`SyntheticMonitoring__Subjects__0`）が
 *    除外の面へ届かないと、合成のリクエストが利用実績・LLM 費用・検索傾向へ混ざり、それらが
 *    「人が使った量」を表さなくなる（ADR-0076 決定 4）。表示は正常なので、壊れていることが誰にも見えない。
 *
 * 固定するもの:
 *   1. 既定（無効）: 描画に `synthetic` が 0 回。有効時の描画から合成の資源と標識 1 行を抜くと、無効時と
 *      **バイト単位で一致**する（有効化が足すのはそれだけ＝無効時の 3 サービスの env は従前のまま）。
 *   2. 有効: プローブ・標識・Keycloak への egress が**同じ描画**に揃う。AllowLlmEgress は出ない。
 *      ExternalSecret・Secret 本体は出ない（チャートの外）。
 *   3. 描画時の fail-closed: 3 サービスのどれかが無効／aianalysis に AllowLlmEgress → `helm template` が失敗する。
 *   4. ローカルの門（scripts/k8s-local-up.sh の SYNTHETIC=1）・overlay との一致:
 *      集合・主体名・プローブの env・イメージ・probe.js のバイト列・Secret 名と鍵。
 *   5. 変異: 一時複製したチャートで集合から 1 つ落とすと、本試験の判定が赤になる。
 *
 * 🔴 **helm が無ければ落ちる（fail-closed）。** 字面の正規表現で済ませず実際に描くのが本試験の目的であり、
 *    黙って飛ばすと「検査していない」と「問題が無い」が同じ出力になる。CI は static-checks-units
 *    （azure/setup-helm 済み）で走らせる。k8s-local-up.test.js に置かないのは、そのジョブが helm を入れておらず、
 *    かつ同試験が helm を PATH のスタブへ差し替えるためである。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ helm）。実行: node scripts/helm-synthetic-monitor.test.js
 */
const assert = require('assert');
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const CI_VALUES = `${CHART}/ci/synthetic-monitor-values.yaml`;
const VALUES_LOCAL = 'deploy/local/values-local.yaml';
const SYNTHETIC_SOURCE = '# Source: microservices-platform/templates/synthetic-monitor.yaml';
const MARKER = 'SyntheticMonitoring__Subjects__0';

const read = (rel) => fs.readFileSync(path.join(REPO_ROOT, rel), 'utf8');
const OVERLAY_YAML = read('deploy/local/synthetic-monitor/synthetic-monitor.yaml');
const OVERLAY_PROBE = fs.readFileSync(path.join(REPO_ROOT, 'deploy/local/synthetic-monitor/probe.js'));
const CHART_PROBE = fs.readFileSync(path.join(REPO_ROOT, CHART, 'files/synthetic-monitor/probe.js'));
const UP_SH = read('scripts/k8s-local-up.sh');
const EXTERNAL_SECRET = read('deploy/local/vault/eso/externalsecret-synthetic-monitor-oidc.yaml');
const CHART_VALUES = read(`${CHART}/values.yaml`);

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

// ---------------------------------------------------------------- helpers

function hasHelm() {
  const probe = spawnSync(process.platform === 'win32' ? 'where' : 'which', ['helm'], { encoding: 'utf8' });
  return probe.status === 0;
}

function helmTemplate(args, chartDir = CHART) {
  const r = spawnSync('helm', ['template', 'msp', chartDir, ...args], {
    cwd: REPO_ROOT,
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
  });
  return { status: r.status, out: r.stdout || '', err: r.stderr || '' };
}

function mustRender(args, chartDir) {
  const r = helmTemplate(args, chartDir);
  assert.strictEqual(r.status, 0, `helm template ${args.join(' ')} が失敗した: ${r.err}`);
  return r.out;
}

/** helm の出力を `---` 行で始まる塊へ分ける（塊をつなぎ直すと元のバイト列に戻る）。 */
function chunks(text) {
  return text.split(/(?=^---\n)/m);
}

/** 塊ごとの kind と metadata.name。 */
function docs(text) {
  return chunks(text).map((c) => ({
    text: c,
    kind: (/^kind:\s*(\S+)/m.exec(c) || [])[1],
    name: (/^metadata:\n(?:[ ]{2}.*\n)*?[ ]{2}name:\s*(\S+)/m.exec(c) || [])[1],
  }));
}

const unquote = (s) => String(s).trim().replace(/^["']|["']$/g, '');

/** env の項目を名前 → 値（value）または `secret:<name>/<key>`（valueFrom.secretKeyRef）へ。注記行は読み飛ばす。 */
function envMap(yaml) {
  const out = new Map();
  const re =
    /^\s*- name: (\S+)\s*\n(?:\s*#.*\n)*\s*(?:value: (.*)|valueFrom:\s*\n\s*secretKeyRef:\s*\n\s*name: (\S+)\s*\n\s*key: (\S+))/gm;
  for (const m of yaml.matchAll(re)) {
    out.set(m[1], m[2] !== undefined ? unquote(m[2]) : `secret:${m[3]}/${m[4]}`);
  }
  return out;
}

/** コンテナの env 節だけを切り出す（volumes の `- name:` を拾わないため）。 */
function envSection(yaml) {
  const start = yaml.indexOf('env:');
  const end = yaml.indexOf('volumeMounts:', start);
  assert.ok(start !== -1 && end !== -1, 'env 節が見つからない');
  return yaml.slice(start, end);
}

/** ConfigMap の literal block（`<key>: |`）を元の文字列へ戻す。 */
function literalBlock(yaml, key) {
  const lines = yaml.split('\n');
  const at = lines.findIndex((l) => l === `  ${key}: |`);
  assert.ok(at !== -1, `${key}: | が無い`);
  const body = [];
  for (const l of lines.slice(at + 1)) {
    if (l.startsWith('    ')) body.push(l.slice(4));
    else if (l.trim() === '') body.push('');
    else break;
  }
  while (body.length > 0 && body[body.length - 1] === '') body.pop();
  return `${body.join('\n')}\n`;
}

/** 標識を持つ Deployment 名 → 値。 */
function markerByDeployment(text) {
  const out = new Map();
  for (const d of docs(text)) {
    if (d.kind !== 'Deployment') continue;
    const env = envMap(d.text);
    if (env.has(MARKER)) out.set(d.name, env.get(MARKER));
  }
  return out;
}

/** ローカルの門が標識を与える集合と、その値。 */
function localGate() {
  const setEnv = /for d in ([a-z ]+); do\s*\n\s*kubectl -n "\$MSP_NS" set env "deploy\/\$d-service" SyntheticMonitoring__Subjects__0=(\S+)/.exec(UP_SH);
  const rollout = /for d in ([a-z ]+); do\s*\n\s*kubectl -n "\$MSP_NS" rollout status "deploy\/\$d-service"/.exec(
    UP_SH.slice(UP_SH.indexOf('SYNTHETIC_DEFAULT=')),
  );
  assert.ok(setEnv, 'k8s-local-up.sh の SYNTHETIC 門に標識の set env ループが見つからない（形が変わった）');
  assert.ok(rollout, 'k8s-local-up.sh の SYNTHETIC 門に除外の rollout 待ちループが見つからない');
  return {
    services: setEnv[1].trim().split(/\s+/).sort(),
    rolloutServices: rollout[1].trim().split(/\s+/).sort(),
    subject: setEnv[2],
  };
}

/**
 * 有効時の描画に対する判定（変異試験でも同じ関数を使う＝試験が赤になることを確かめる対象）。
 * 戻り値は問題の一覧。空なら緑。
 */
function enabledProblems(text, expectedServices, expectedSubject) {
  const problems = [];
  const all = docs(text);
  if (!all.some((d) => d.kind === 'Deployment' && d.name === 'synthetic-monitor')) {
    problems.push('プローブの Deployment（synthetic-monitor）が無い');
  }
  const markers = markerByDeployment(text);
  const want = expectedServices.map((s) => `${s}-service`).sort();
  const got = [...markers.keys()].sort();
  if (JSON.stringify(got) !== JSON.stringify(want)) {
    problems.push(`標識を持つ Deployment が ${JSON.stringify(got)}（期待 ${JSON.stringify(want)}）`);
  }
  for (const [name, value] of markers) {
    if (value !== expectedSubject) problems.push(`${name} の標識が ${value}（期待 ${expectedSubject}）`);
  }
  if (/SyntheticMonitoring__AllowLlmEgress/.test(text)) {
    problems.push('🔴 AllowLlmEgress が描画に現れた（60 秒側は LLM を呼ばない / ADR-0079 決定 1）');
  }
  return problems;
}

// ---------------------------------------------------------------- 静的（helm 不要）

const GATE = localGate();

ok('門の前提: 標識を与える集合と rollout を待つ集合が同じ（門自身の整合）', () => {
  assert.deepStrictEqual(GATE.services, GATE.rolloutServices);
  assert.strictEqual(GATE.services.length, 3, `門の集合が 3 つでない: ${GATE.services}`);
});

ok('probe.js: チャートの写しが overlay の正本とバイト単位で一致する', () => {
  assert.ok(
    CHART_PROBE.equals(OVERLAY_PROBE),
    `${CHART}/files/synthetic-monitor/probe.js が deploy/local/synthetic-monitor/probe.js と食い違う。` +
      '正本は overlay 側である —— そちらを直して写し直すこと（helm も kustomize も互いのディレクトリの外を読めない）',
  );
});

ok('Secret: チャートの参照・ExternalSecret の target・overlay の secretKeyRef が同じ名前と鍵', () => {
  const esTarget = /target:\s*\n\s*name:\s*(\S+)/.exec(EXTERNAL_SECRET)[1];
  const esKey = /secretKey:\s*(\S+)/.exec(EXTERNAL_SECRET)[1];
  const block = CHART_VALUES.slice(CHART_VALUES.indexOf('\nsyntheticMonitor:'));
  const valuesSecret = /^\s{2}existingSecret:\s*(\S+)/m.exec(block)[1];
  const valuesKey = /^\s{2}clientSecretKey:\s*(\S+)/m.exec(block)[1];
  const overlaySecret = envMap(envSection(OVERLAY_YAML)).get('SYNTHETIC_CLIENT_SECRET');
  assert.strictEqual(valuesSecret, esTarget, 'values の existingSecret が ExternalSecret の target と違う');
  assert.strictEqual(valuesKey, esKey, 'values の clientSecretKey が ExternalSecret の secretKey と違う');
  assert.strictEqual(overlaySecret, `secret:${esTarget}/${esKey}`, 'overlay の secretKeyRef が ExternalSecret と違う');
});

// ---------------------------------------------------------------- 描画（helm が要る）

if (!hasHelm()) {
  process.stderr.write(
    '✗ helm が PATH に無い。本試験は実際に `helm template` を叩くことが目的であり、飛ばさない（fail-closed）。\n' +
      '  helm を導入してから再実行すること（CI は static-checks-units の azure/setup-helm で入る）。\n',
  );
  process.exit(1);
}

const OFF = mustRender([]);
const ON = mustRender(['-f', CI_VALUES]);
const OFF_LOCAL = mustRender(['-f', VALUES_LOCAL]);
const ON_LOCAL = mustRender(['-f', VALUES_LOCAL, '-f', CI_VALUES]);

ok('前提: ci の values は「有効」だけを宣言している（有効の定義を 2 か所に書かない）', () => {
  const body = read(CI_VALUES).split('\n').filter((l) => l.trim() !== '' && !/^\s*#/.test(l));
  assert.deepStrictEqual(body, ['syntheticMonitor:', '  enabled: true']);
});

ok('既定（無効）: 描画に synthetic が 1 回も現れない（何も立たず、何も呼ばない）', () => {
  for (const [label, text] of [['既定', OFF], ['values-local', OFF_LOCAL]]) {
    const hit = text.split('\n').find((l) => /synthetic/i.test(l));
    assert.ok(!hit, `${label} の描画に合成監視が漏れている: ${hit}`);
  }
});

ok('既定（無効）: 3 サービスの env に標識が無い（陰性対照）', () => {
  assert.deepStrictEqual([...markerByDeployment(OFF).keys()], []);
  assert.ok(enabledProblems(OFF, GATE.services, GATE.subject).length > 0, '無効の描画を「揃っている」と判定した');
});

ok('🔴 無効時は従前とバイト等価: 有効時から合成の資源と標識 1 行を抜くと、無効時と一致する', () => {
  const strip = (text) =>
    chunks(text)
      .filter((c) => !c.includes(SYNTHETIC_SOURCE))
      .join('')
      .replace(new RegExp(`^ +- name: ${MARKER}\\n +value: .*\\n`, 'gm'), '');
  for (const [label, on, off] of [['既定', ON, OFF], ['values-local', ON_LOCAL, OFF_LOCAL]]) {
    assert.ok(on !== off, `${label}: 有効にしても描画が変わらない（試験の前提が崩れている）`);
    assert.strictEqual(strip(on), off, `${label}: 有効化が合成の資源と標識以外の何かを変えている`);
  }
});

ok('有効: プローブ・標識 3 つ・AllowLlmEgress 無しが同じ描画に揃う（門と同じ集合・同じ主体）', () => {
  assert.deepStrictEqual(enabledProblems(ON, GATE.services, GATE.subject), []);
  assert.deepStrictEqual(enabledProblems(ON_LOCAL, GATE.services, GATE.subject), []);
});

ok('有効: 主体名がプローブの SYNTHETIC_CLIENT_ID・overlay・門で同じ', () => {
  const probeDoc = docs(ON).find((d) => d.kind === 'Deployment' && d.name === 'synthetic-monitor');
  const chartEnv = envMap(envSection(probeDoc.text));
  const overlayEnv = envMap(envSection(OVERLAY_YAML));
  assert.strictEqual(chartEnv.get('SYNTHETIC_CLIENT_ID'), GATE.subject);
  assert.strictEqual(overlayEnv.get('SYNTHETIC_CLIENT_ID'), GATE.subject);
});

ok('有効: プローブの env・イメージ・コマンドが overlay と同じ', () => {
  const probeDoc = docs(ON).find((d) => d.kind === 'Deployment' && d.name === 'synthetic-monitor');
  const chartEnv = envMap(envSection(probeDoc.text));
  const overlayEnv = envMap(envSection(OVERLAY_YAML));
  assert.deepStrictEqual(
    [...chartEnv.entries()].sort(),
    [...overlayEnv.entries()].sort(),
    'チャートと overlay のプローブ env が食い違う（間隔・経路・Keycloak・BFF・Secret のどれか）',
  );
  const image = (t) => unquote(/^\s*image:\s*(.+)$/m.exec(t)[1]);
  const command = (t) => /^\s*command:\s*(.+)$/m.exec(t)[1].trim();
  assert.strictEqual(image(probeDoc.text), image(OVERLAY_YAML), 'イメージが overlay と違う');
  assert.strictEqual(command(probeDoc.text), command(OVERLAY_YAML), 'コマンドが overlay と違う');
  assert.strictEqual(chartEnv.get('PROBE_INTERVAL_SECONDS'), '60', '60 秒でない（ADR-0079 決定 1 の確定値）');
});

ok('有効: ConfigMap の probe.js は正本とバイト一致し、checksum 注釈がその sha256 である', () => {
  const cm = docs(ON).find((d) => d.kind === 'ConfigMap' && d.name === 'synthetic-monitor-probe');
  assert.ok(cm, 'ConfigMap synthetic-monitor-probe が無い');
  assert.strictEqual(literalBlock(cm.text, 'probe.js'), OVERLAY_PROBE.toString('utf8'));
  const sum = crypto.createHash('sha256').update(CHART_PROBE).digest('hex');
  assert.ok(ON.includes(`checksum/probe: "${sum}"`), 'checksum/probe が probe.js の sha256 と違う');
});

ok('有効: Secret の実値も ExternalSecret もチャートから出ない（チャートの外で作る）', () => {
  const kinds = docs(ON).map((d) => d.kind);
  assert.ok(!kinds.includes('ExternalSecret'), 'ExternalSecret をチャートが描いている（二重所有になる）');
  assert.ok(!kinds.includes('Secret'), 'Secret 本体をチャートが描いている');
  assert.ok(!/synthetic-monitor-dev-secret-change-me/.test(ON), 'realm の dev 置き値（プローブの secret）が描画に現れた');
});

ok('有効 ＋ networkPolicy: プローブ → Keycloak の egress が 1 本だけ開き、宛先は KC_URL と同じ namespace', () => {
  const np = docs(ON).filter((d) => d.kind === 'NetworkPolicy' && d.name === 'allow-synthetic-monitor-egress-to-keycloak');
  assert.strictEqual(np.length, 1, 'egress の NetworkPolicy が 1 本でない');
  const t = np[0].text;
  assert.match(t, /podSelector:\s*\n\s*matchLabels:\s*\n\s*app: synthetic-monitor/);
  const ns = /kubernetes\.io\/metadata\.name:\s*(\S+)/.exec(t)[1];
  assert.match(t, /app: keycloak/);
  assert.match(t, /port: 8080/);
  const probeDoc = docs(ON).find((d) => d.kind === 'Deployment' && d.name === 'synthetic-monitor');
  const kcUrl = envMap(envSection(probeDoc.text)).get('KC_URL');
  assert.ok(kcUrl.includes(`keycloak.${ns}.svc`), `KC_URL（${kcUrl}）と egress の namespace（${ns}）が食い違う`);
  // values-local は networkPolicy.enabled=false。穴は開けない。
  assert.ok(!docs(ON_LOCAL).some((d) => d.kind === 'NetworkPolicy'), 'networkPolicy 無効なのに NetworkPolicy を描いた');
});

ok('🔴 描画時の fail-closed: 除外の 3 サービスのどれかが無効なら helm template が失敗する（ADR-0076 決定 4）', () => {
  for (const s of GATE.services) {
    const r = helmTemplate(['-f', CI_VALUES, '--set', `services.${s}.enabled=false`]);
    assert.notStrictEqual(r.status, 0, `services.${s} を無効にしても描画できた（除外できない構成へ配備できてしまう）`);
    assert.match(r.err, new RegExp(`services\\.${s} が無効`), `失敗理由に ${s} が出ていない: ${r.err}`);
  }
  // 陰性対照: 合成監視が無効なら、同じサービスを無効にしても描画は通る（門は有効時だけ効く）。
  assert.strictEqual(helmTemplate(['--set', 'services.dashboard.enabled=false']).status, 0);
});

ok('🔴 描画時の fail-closed: aianalysis に AllowLlmEgress が立っていれば helm template が失敗する（ADR-0079 決定 2）', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'helm-synthetic-'));
  try {
    const write = (name, list, value) => {
      const f = path.join(tmp, name);
      fs.writeFileSync(
        f,
        `services:\n  aianalysis:\n    ${list}:\n      - name: SyntheticMonitoring__AllowLlmEgress\n        value: "${value}"\n`,
      );
      return f;
    };
    // extraEnv はリストを置き換える（本番の Services__* が消える）が、判定を見るだけなので構わない。
    for (const list of ['extraEnv', 'extraEnvAppend']) {
      for (const value of ['true', 'True']) {
        const r = helmTemplate(['-f', CI_VALUES, '-f', write(`${list}-${value}.yaml`, list, value)]);
        assert.notStrictEqual(r.status, 0, `${list} の AllowLlmEgress=${value} を通した（60 秒のプローブが LLM を呼ぶ）`);
        assert.match(r.err, /AllowLlmEgress/);
      }
    }
    // 陰性対照: 明示の false は通す。合成監視が無効なら判定しない。
    assert.strictEqual(helmTemplate(['-f', CI_VALUES, '-f', write('f.yaml', 'extraEnvAppend', 'false')]).status, 0);
    assert.strictEqual(helmTemplate(['-f', write('off.yaml', 'extraEnvAppend', 'true')]).status, 0);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

// #1287 監査（MEDIUM）: .NET の構成は鍵の大文字小文字を区別せず、`:` と `__` を同じ区切りとして読み、
// WebApplication.CreateBuilder は `DOTNET_` / `ASPNETCORE_` 接頭辞の環境変数も接頭辞を外して読む。
// 門が完全一致で名前を比べていると、どれも同じ AllowLlmEgress として効くのに素通りする（監査が rc=0 を実測した）。
ok('🔴 描画時の fail-closed: AllowLlmEgress の綴りの揺れ（大文字・`:` 区切り・DOTNET_/ASPNETCORE_ 接頭辞）も止める', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'helm-synthetic-spell-'));
  try {
    const spellings = [
      'SYNTHETICMONITORING__ALLOWLLMEGRESS',
      'SyntheticMonitoring:AllowLlmEgress',
      'DOTNET_SyntheticMonitoring__AllowLlmEgress',
      'ASPNETCORE_SyntheticMonitoring__AllowLlmEgress',
      'dotnet_syntheticmonitoring:allowllmegress',
    ];
    spellings.forEach((name, i) => {
      const f = path.join(tmp, `s${i}.yaml`);
      fs.writeFileSync(f, `services:\n  aianalysis:\n    extraEnvAppend:\n      - name: "${name}"\n        value: "true"\n`);
      const r = helmTemplate(['-f', CI_VALUES, '-f', f]);
      assert.notStrictEqual(r.status, 0, `${name}=true を通した（.NET は同じ鍵として読み、60 秒のプローブが LLM を呼ぶ）`);
      assert.match(r.err, /AllowLlmEgress/);
    });
    // 陰性対照: 綴りが揺れても値が false なら通す（正規化が値の判定まで壊していないこと）。
    const f = path.join(tmp, 'false.yaml');
    fs.writeFileSync(f, 'services:\n  aianalysis:\n    extraEnvAppend:\n      - name: "DOTNET_SyntheticMonitoring:AllowLlmEgress"\n        value: " False "\n');
    assert.strictEqual(helmTemplate(['-f', CI_VALUES, '-f', f]).status, 0);
    // 陰性対照: 似ているが別の鍵は止めない（正規化が広すぎないこと）。
    const g = path.join(tmp, 'other.yaml');
    fs.writeFileSync(g, 'services:\n  aianalysis:\n    extraEnvAppend:\n      - name: "SyntheticMonitoring__AllowLlmEgressAudit"\n        value: "true"\n');
    assert.strictEqual(helmTemplate(['-f', CI_VALUES, '-f', g]).status, 0);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

// #1287 監査（LOW・変異 M5 の生存）: Secret 参照は描画時に中身を確かめられないので、立っているものとして止める。
// この試験が無いと、門から `.secretKeyRef` の枝を消しても 17 本がすべて緑のままだった。
ok('🔴 描画時の fail-closed: AllowLlmEgress を Secret 参照（secretKeyRef / valueFrom）で与えても止める', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'helm-synthetic-secret-'));
  try {
    const cases = {
      secretKeyRef: '        secretKeyRef:\n          name: synthetic-llm\n          key: allow\n',
      valueFrom: '        valueFrom:\n          secretKeyRef:\n            name: synthetic-llm\n            key: allow\n',
    };
    for (const [label, body] of Object.entries(cases)) {
      for (const list of ['extraEnv', 'extraEnvAppend']) {
        const f = path.join(tmp, `${label}-${list}.yaml`);
        fs.writeFileSync(f, `services:\n  aianalysis:\n    ${list}:\n      - name: SyntheticMonitoring__AllowLlmEgress\n${body}`);
        const r = helmTemplate(['-f', CI_VALUES, '-f', f]);
        assert.notStrictEqual(r.status, 0, `${list} の ${label} 参照を通した（中身を確かめられない値で LLM が有効になり得る）`);
        assert.match(r.err, /AllowLlmEgress/);
      }
    }
    // 陰性対照: `value: false` と secretKeyRef を両方書いた項目は、deployment.yaml が value を描いて Secret 参照を
    // 無視するので実際に false が入る。門はこれを通し、描画にも literal の false だけが出る（Secret 参照は出ない）。
    const both = path.join(tmp, 'both.yaml');
    fs.writeFileSync(
      both,
      'services:\n  aianalysis:\n    extraEnvAppend:\n      - name: SyntheticMonitoring__AllowLlmEgress\n        value: "false"\n        secretKeyRef:\n          name: synthetic-llm\n          key: allow\n',
    );
    const out = mustRender(['-f', CI_VALUES, '-f', both]);
    const at = out.indexOf('- name: SyntheticMonitoring__AllowLlmEgress');
    assert.ok(at !== -1, 'value: false の項目が描画に出ていない（試験の前提が崩れている）');
    // 項目の範囲 = 見出し行の次から、次の `- name:` 行の手前まで。
    const lines = out.slice(at).split('\n');
    const next = lines.findIndex((l, i) => i > 0 && /^\s*- name:/.test(l));
    const entry = lines.slice(0, next === -1 ? 4 : next).join('\n');
    assert.match(entry, /value: "false"/);
    assert.ok(!/secretKeyRef/.test(entry), `value と Secret 参照を両方描いた（Kubernetes が拒否する）:\n${entry}`);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

ok('🔴 変異: チャートの集合から 1 つ落とすと本試験の判定が赤になる（試験が効くことの陽性対照）', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'helm-synthetic-mut-'));
  try {
    const chartCopy = path.join(tmp, 'chart');
    fs.cpSync(path.join(REPO_ROOT, CHART), chartCopy, { recursive: true });
    const tpl = path.join(chartCopy, 'templates', '_synthetic-monitor.tpl');
    const original = fs.readFileSync(tpl, 'utf8');
    const mutated = original.replace(/^bff,dashboard,aianalysis$/m, 'bff,aianalysis');
    assert.notStrictEqual(mutated, original, '変異を当てられなかった（集合の書き方が変わった）');
    fs.writeFileSync(tpl, mutated);
    const text = mustRender(['-f', path.join(REPO_ROOT, CI_VALUES)], chartCopy);
    const problems = enabledProblems(text, GATE.services, GATE.subject);
    assert.ok(
      problems.some((p) => p.includes('標識を持つ Deployment')),
      `dashboard の標識を落としたのに判定が緑のまま: ${JSON.stringify(problems)}`,
    );
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

ok('🔴 変異: 描画から 1 サービスの標識だけを消すと判定が赤になる', () => {
  for (const s of GATE.services) {
    const mutated = chunks(ON)
      .map((c) =>
        new RegExp(`^  name: ${s}-service$`, 'm').test(c) && /^kind: Deployment$/m.test(c)
          ? c.replace(new RegExp(`^ +- name: ${MARKER}\\n +value: .*\\n`, 'm'), '')
          : c,
      )
      .join('');
    assert.notStrictEqual(mutated, ON, `${s} の標識を消せなかった`);
    assert.ok(enabledProblems(mutated, GATE.services, GATE.subject).length > 0, `${s} の標識が無いのに緑`);
  }
});

process.stdout.write(`\n✓ ${passed} tests passed\n`);
