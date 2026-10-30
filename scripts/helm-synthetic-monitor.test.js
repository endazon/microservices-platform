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
const { RENDER_IMAGE_TAG_ARGS } = require('./lib/helm-render-tag'); // NFR-20, IADR-0605: chart はタグが無いと描画を落とす

const REPO_ROOT = path.resolve(__dirname, '..');
const CHART = 'deploy/helm/microservices-platform';
const CI_VALUES = `${CHART}/ci/synthetic-monitor-values.yaml`;
// NFR-02, ADR-0079 決定 1・2, IADR-0654 (#2335): SLO 評価用（60 分・LLM を呼ぶ）の合成プローブの「有効」。
const CI_SLO_VALUES = `${CHART}/ci/synthetic-monitor-slo-values.yaml`;
const SLO_EXTERNAL_SECRET = read('deploy/local/vault/eso/externalsecret-synthetic-monitor-slo-oidc.yaml');
const REALM = JSON.parse(read('deploy/keycloak/microservices-platform-realm.json'));
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
  const r = spawnSync('helm', ['template', 'msp', chartDir, ...RENDER_IMAGE_TAG_ARGS, ...args], {
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
const kindsOf = (text) => docs(text).map((d) => d.kind);

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
  // IADR-0589: values-local は egress の既定拒否（段 1 の Pod）を描くが、プローブ → Keycloak の穴は networkPolicy.enabled のときだけ。
  assert.ok(!docs(ON_LOCAL).some((d) => d.kind === 'NetworkPolicy' && d.name === 'allow-synthetic-monitor-egress-to-keycloak'), 'networkPolicy 無効なのにプローブ → Keycloak の穴を描いた');
  assert.ok(!/synthetic-monitor/.test(docs(ON_LOCAL).filter((d) => d.kind === 'NetworkPolicy').map((d) => d.text).join('\n')), 'values-local の NetworkPolicy がプローブの Pod を選んだ（段 2 の Pod）');
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

// ---------------------------------------------------------------- SLO 評価用（60 分・LLM を呼ぶ）。IADR-0654 (#2335)

const ON_SLO = mustRender(['-f', CI_SLO_VALUES]);
const sloDoc = (text) => docs(text).find((d) => d.kind === 'Deployment' && d.name === 'synthetic-monitor-slo');
const deploymentEnv = (text, name) => {
  const d = docs(text).find((x) => x.kind === 'Deployment' && x.name === name);
  assert.ok(d, `Deployment ${name} が無い`);
  return envMap(d.text);
};
const SLO_SUBJECT = 'synthetic-monitor-slo';

ok('SLO: 既定と 60 秒だけの有効では、SLO の資源も LLM の許可の主体も 1 バイトも出ない', () => {
  for (const [label, text] of [['既定', OFF], ['60 秒だけ', ON], ['values-local', ON_LOCAL]]) {
    assert.ok(!text.includes('synthetic-monitor-slo'), `${label} の描画に SLO のプローブが漏れている`);
    assert.ok(!/SyntheticMonitoring__LlmSubjects/.test(text), `${label} の描画に LLM の許可の主体が出た`);
    assert.ok(!/SyntheticMonitoring__Subjects__1/.test(text), `${label} の描画に 2 つ目の合成の主体が出た`);
  }
});

ok('SLO: 有効なら Deployment・標識 2 つ・LLM の許可の主体（BFF だけ）・egress が同じ描画に揃う', () => {
  assert.ok(sloDoc(ON_SLO), 'SLO のプローブの Deployment（synthetic-monitor-slo）が無い');
  // 60 秒側の判定（主体・3 サービス・AllowLlmEgress 無し）は SLO を足しても崩れない。
  assert.deepStrictEqual(enabledProblems(ON_SLO, GATE.services, GATE.subject), []);
  for (const s of GATE.services) {
    const env = deploymentEnv(ON_SLO, `${s}-service`);
    assert.strictEqual(env.get('SyntheticMonitoring__Subjects__1'), SLO_SUBJECT, `${s} に SLO の主体が合成の主体として無い（除外が欠ける）`);
    assert.strictEqual(env.has('SyntheticMonitoring__LlmSubjects__0'), s === 'bff',
      `${s} の LLM の許可の主体の有無が違う（外周の BFF だけが判定する）`);
  }
  assert.strictEqual(deploymentEnv(ON_SLO, 'bff-service').get('SyntheticMonitoring__LlmSubjects__0'), SLO_SUBJECT);
  const np = docs(ON_SLO).filter((d) => d.kind === 'NetworkPolicy' && d.name === 'allow-synthetic-monitor-slo-egress-to-keycloak');
  assert.strictEqual(np.length, 1, 'SLO のプローブ → Keycloak の egress が 1 本でない');
  assert.match(np[0].text, /podSelector:\s*\n\s*matchLabels:\s*\n\s*app: synthetic-monitor-slo/);
});

ok('🔴 SLO: 60 秒のプローブには LLM の許可が無い（主体は常時側・経路は 2 本のまま・AllowLlmEgress 無し）', () => {
  const always = deploymentEnv(ON_SLO, 'synthetic-monitor');
  assert.strictEqual(always.get('SYNTHETIC_CLIENT_ID'), GATE.subject);
  assert.strictEqual(always.get('PROBE_INTERVAL_SECONDS'), '60');
  assert.ok(!/SyntheticMonitoring__AllowLlmEgress/.test(ON_SLO), 'AllowLlmEgress が描画に現れた（サービス全体で 60 秒側まで LLM を呼ぶ）');
  // 60 秒の主体は LLM の許可の主体に入らない（入れると 60 秒側が LLM を呼ぶ）。
  const llmSubjects = [...deploymentEnv(ON_SLO, 'bff-service').entries()]
    .filter(([k]) => /^SyntheticMonitoring__LlmSubjects__/.test(k)).map(([, v]) => v);
  assert.deepStrictEqual(llmSubjects, [SLO_SUBJECT]);
});

ok('SLO: プローブは 60 分・SSE 1 経路・自分の主体と Secret で、器（イメージ・probe.js）は 60 秒側と同じ', () => {
  const env = envMap(envSection(sloDoc(ON_SLO).text));
  assert.strictEqual(env.get('SYNTHETIC_CLIENT_ID'), SLO_SUBJECT);
  assert.strictEqual(env.get('PROBE_INTERVAL_SECONDS'), '3600', '60 分でない（ADR-0079 決定 1・2 の確定値）');
  assert.strictEqual(env.get('PROBE_PATHS'), '/bff/analysis/ask/stream');
  const esTarget = /target:\s*\n\s*name:\s*(\S+)/.exec(SLO_EXTERNAL_SECRET)[1];
  const esKey = /secretKey:\s*(\S+)/.exec(SLO_EXTERNAL_SECRET)[1];
  assert.strictEqual(env.get('SYNTHETIC_CLIENT_SECRET'), `secret:${esTarget}/${esKey}`, 'Secret の参照が ExternalSecret の target と違う');
  const image = (t) => unquote(/^\s*image:\s*(.+)$/m.exec(t)[1]);
  const always = docs(ON_SLO).find((d) => d.kind === 'Deployment' && d.name === 'synthetic-monitor');
  assert.strictEqual(image(sloDoc(ON_SLO).text), image(always.text));
  assert.match(sloDoc(ON_SLO).text, /name: synthetic-monitor-probe/, 'probe.js の ConfigMap を共用していない');
  assert.ok(!kindsOf(ON_SLO).includes('ExternalSecret') && !kindsOf(ON_SLO).includes('Secret'), 'Secret をチャートが描いている');
});

ok('SLO: realm に専用の client（client_credentials・ロール無し）が在り、主体名が values と一致する', () => {
  const client = REALM.clients.find((c) => c.clientId === SLO_SUBJECT);
  assert.ok(client, 'realm に client synthetic-monitor-slo が無い');
  assert.strictEqual(client.serviceAccountsEnabled, true);
  assert.strictEqual(client.standardFlowEnabled, false);
  assert.strictEqual(client.directAccessGrantsEnabled, false);
  assert.strictEqual(client.publicClient, false);
  const svcUser = (REALM.users || []).find((u) => u.username === `service-account-${SLO_SUBJECT}`);
  assert.ok(!svcUser || !(svcUser.realmRoles || []).length, 'SLO の主体にロールを与えている');
  const block = CHART_VALUES.slice(CHART_VALUES.indexOf('\n  slo:'));
  assert.strictEqual(/^\s{4}clientId:\s*(\S+)/m.exec(block)[1], SLO_SUBJECT);
});

ok('🔴 SLO の描画時の fail-closed: 除外の標識が無い・主体が同じ・間隔 3600 未満・経路が SSE 以外なら止める', () => {
  const cases = [
    [['--set', 'syntheticMonitor.slo.enabled=true'], /syntheticMonitor\.enabled が偽/],
    [['-f', CI_SLO_VALUES, '--set', 'syntheticMonitor.slo.clientId=synthetic-monitor'], /同じ/],
    [['-f', CI_SLO_VALUES, '--set', 'syntheticMonitor.slo.intervalSeconds=60'], /3600/],
    [['-f', CI_SLO_VALUES, '--set-string', 'syntheticMonitor.slo.paths=/bff/analysis/ask\\,/bff/analysis/ask/stream'], /ask\/stream だけ/],
  ];
  for (const [args, why] of cases) {
    const r = helmTemplate(args);
    assert.notStrictEqual(r.status, 0, `${args.join(' ')} を通した`);
    assert.match(r.err, why, `失敗理由が違う: ${r.err}`);
  }
  // 陰性対照: 既定の SLO の値（3600・SSE 1 本・別の主体）は通る（上で描けている）。
  assert.ok(ON_SLO.length > 0);
});

process.stdout.write(`\n✓ ${passed} tests passed\n`);
