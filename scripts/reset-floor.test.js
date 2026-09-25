#!/usr/bin/env node
'use strict';
/*
 * reset-floor.test.js
 * SC-15, FR-05, NFR-09, NFR-13, ADR-0078 決定 1, ADR-0094 決定 2, ADR-0097 決定 2, IADR-0432 (#1410 / #1500):
 * **床（deploy/mail-relay/reset-floor.js）と、それを与えるマニフェスト・経路の宣言が
 * 食い違わないこと**を、実装とマニフェストを同時に読んで固定する。
 *
 * 🔴 **ここが本 PR の接点である。** 床は 3 つの宣言に分かれて生きている ——
 *    ①器のコード（既定を持たない）②マニフェストの env（値の正本）③経路（VirtualService の route）。
 *    **どれか 1 つがズレると、床は静かに効かなくなる**（器は起動しない／誰も通らない／
 *    通ってはいるが上流を向いている）。単体の自己試験は①しか見ないので、接点をここで突き合わせる。
 *
 * 固定するもの:
 *   1. 器が要求する env キーを、マニフェストが**すべて**与えている（欠ければ器は起動しない）。
 *   2. 🔴 **床の器は既定で入る**（`deploy/mail-relay/kustomization.yaml` の resources が器を取り込み、
 *      `deploy/local/infra` がそれを取り込む）。［2026-09-26 / #1500］計画 ADR-0097 決定 2 が
 *      IADR-0432 決定 4（opt-in・既定の描画はバイト等価）を覆した。
 *   3. Istio エッジの overlay が**器と経路の両方**を足す（片方だけでは効かない）。
 *   4. 🔴 経路が **POST だけ**を床へ向け、**catch-all より前**に居る（Istio の route は先勝ち）。
 *   5. 🔴 経路が **realm 名を書き写していない**（realm 宣言が単一情報源。改名で静かに外れる）。
 *   6. 🔴 **ログイン経路へ床を掛けていない**（ADR-0094 決定 4 は同経路を判定の対象外とした）。
 *   7. 上流が Keycloak の Service と**同じ名前・同じポート**である。
 *   8. 🔴 **起動器（istio-edge-up.sh）の既定は床を入れる**（RESET_FLOOR 未設定 → 床の overlay）。
 *      退路 RESET_FLOOR=0 は素の edge-istio を当てる。0 / 1 以外は**入口に触る前に**落ちる。
 *      スクリプトを記録スタブの下で実際に走らせて確かめる（正規表現で字面を見るだけにしない）。
 *   9. 器の自己試験（純関数）も通る。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ。8 は bash が前提ツール）。実行: node scripts/reset-floor.test.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const floor = require('../deploy/mail-relay/reset-floor.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const read = (...p) => fs.readFileSync(path.join(REPO_ROOT, ...p), 'utf8');

const FLOOR_MANIFEST = read('deploy', 'mail-relay', 'reset-floor', 'reset-floor.yaml');
const FLOOR_KUST = read('deploy', 'mail-relay', 'reset-floor', 'kustomization.yaml');
const RELAY_KUST = read('deploy', 'mail-relay', 'kustomization.yaml');
const INFRA_KUST = read('deploy', 'local', 'infra', 'kustomization.yaml');
const OVERLAY_KUST = read('deploy', 'local', 'edge-istio-reset-floor', 'kustomization.yaml');
const EDGE_VS = read('deploy', 'local', 'edge-istio', 'virtualservice-app.yaml');
const KEYCLOAK_MANIFEST = read('deploy', 'local', 'infra', 'keycloak.yaml');
const EDGE_UP = read('scripts', 'istio-edge-up.sh');
const EDGE_DOWN = read('scripts', 'istio-edge-down.sh');

let passed = 0;
const ok = (name, fn) => { fn(); passed += 1; console.log(`  ok  ${name}`); };

ok('1. 器が要求する env をマニフェストがすべて与える（欠ければ器は起動しない）', () => {
  // 器の側の「足りない」判定を空の env で引き出し、その名前が全部マニフェストに在ることを見る。
  const missing = floor.readConfig({});
  assert.strictEqual(missing.ok, false, '空の env で起動できてしまう（既定を持っている）');
  const required = missing.error.match(/[A-Z_]{4,}/g).filter((k) => k !== 'RESET' && k !== 'FLOOR');
  assert.ok(required.length >= 3, `要求キーを読み出せない: ${missing.error}`);
  for (const key of required) {
    assert.ok(
      new RegExp(`name:\\s*${key}\\b`).test(FLOOR_MANIFEST),
      `器が要求する env ${key} をマニフェストが与えていない（器は起動せず、床は効かない）`,
    );
  }
  // マニフェストが与える値で実際に構成が組めること（型の検査まで通ること）。
  const floorMs = /name:\s*RESET_FLOOR_MS\s*\n\s*value:\s*"(\d+)"/.exec(FLOOR_MANIFEST);
  const upstream = /name:\s*UPSTREAM_URL\s*\n\s*value:\s*(\S+)/.exec(FLOOR_MANIFEST);
  const port = /name:\s*LISTEN_PORT\s*\n\s*value:\s*"(\d+)"/.exec(FLOOR_MANIFEST);
  assert.ok(floorMs && upstream && port, 'マニフェストから値を読み出せない');
  const cfg = floor.readConfig({
    RESET_FLOOR_MS: floorMs[1], UPSTREAM_URL: upstream[1], LISTEN_PORT: port[1],
  });
  assert.strictEqual(cfg.ok, true, `マニフェストの値で構成を組めない: ${cfg.error || ''}`);
});

// kustomization の resources の項目だけを見る（コメントで言及しただけの語を「取り込んでいる」と読まない）。
const resourceEntries = (kust) => {
  const lines = kust.split('\n');
  const at = lines.findIndex((l) => /^resources:\s*$/.test(l));
  if (at < 0) return [];
  const out = [];
  for (const l of lines.slice(at + 1)) {
    if (/^\S/.test(l)) break; // 次のトップレベルキー
    const m = /^\s*-\s*(\S+)\s*$/.exec(l);
    if (m) out.push(m[1].replace(/\/$/, ''));
  }
  return out;
};

ok('🔴 2. 床の器は既定で入る（近接 MTA の配備単位が取り込み、infra がそれを取り込む。#1500）', () => {
  assert.ok(
    resourceEntries(RELAY_KUST).includes('reset-floor'),
    'deploy/mail-relay/kustomization.yaml の resources が床の器を取り込んでいない（既定で床が入らない。'
    + ' 計画 ADR-0097 決定 2 は「deploy/mail-relay/kustomization.yaml が床を参照する形へ改める」と定めた）',
  );
  assert.ok(
    resourceEntries(INFRA_KUST).includes('../../mail-relay'),
    'deploy/local/infra が近接 MTA の配備単位を取り込んでいない（床の器が既定の描画に届かない）',
  );
  assert.ok(/reset-floor\.yaml/.test(FLOOR_KUST), '床の kustomization が器を含んでいない');
});

ok('🔴 3. Istio エッジの overlay は器と経路の**両方**を足す（片方だけでは効かない）', () => {
  assert.ok(
    /-\s*\.\.\/\.\.\/mail-relay\/reset-floor\s*$/m.test(OVERLAY_KUST),
    'overlay が床の器を含んでいない（route だけなら 404 になる）',
  );
  assert.ok(
    /-\s*\.\.\/edge-istio\s*$/m.test(OVERLAY_KUST),
    'overlay がエッジを含んでいない（器だけなら誰も通らないポートで待つ）',
  );
  assert.ok(
    /host:\s*reset-floor\.platform-infra\.svc\.cluster\.local/.test(OVERLAY_KUST),
    'overlay の route が床の Service を向いていない',
  );
});

ok('🔴 4. 経路は POST だけを床へ向け、catch-all より前に入る（Istio の route は先勝ち）', () => {
  assert.ok(/method:\s*\n\s*exact:\s*POST/.test(OVERLAY_KUST),
    '経路が POST に限定されていない（差の出ない GET まで遅くなる）');
  assert.ok(/path:\s*\/spec\/http\/0\b/.test(OVERLAY_KUST),
    '経路を先頭へ入れていない（catch-all に吸われて床を通らない）');
  // 前提: 素の VirtualService の catch-all が今も prefix: / であること（前提が変わったら気付く）。
  assert.ok(/uri:\s*\{\s*prefix:\s*\/\s*\}/.test(EDGE_VS), 'edge-istio の Keycloak 経路に catch-all が無い');
});

ok('🔴 5. 経路は realm 名を書き写さない（realm 宣言が単一情報源。改名で静かに外れる）', () => {
  const regex = /regex:\s*"([^"]+)"/.exec(OVERLAY_KUST);
  assert.ok(regex, '経路の正規表現を読み出せない');
  assert.ok(/\/realms\/\[\^\/\]\+\//.test(regex[1]),
    `realm の位置がワイルドカードでない: ${regex[1]}`);
  assert.ok(/login-actions\/reset-credentials/.test(regex[1]),
    `リセット申請の経路を指していない: ${regex[1]}`);
  // 実データの realm 名が、この正規表現で実際に当たること（当たらない正規表現を置かない）。
  const realm = JSON.parse(read('deploy', 'keycloak', 'microservices-platform-realm.json'));
  const sample = `/realms/${realm.realm}/login-actions/reset-credentials`;
  assert.ok(new RegExp(regex[1]).test(sample), `稼働 realm の経路に当たらない: ${sample}`);
});

ok('🔴 6. ログイン経路へ床を掛けていない（ADR-0094 決定 4 の対象外）', () => {
  // 🔴 **散文ではなく patch 本体を見る**（コメントで言及しただけの語を「掛けている」と読まない）。
  const patchBody = OVERLAY_KUST.slice(OVERLAY_KUST.indexOf('patch: |-'));
  assert.ok(patchBody.length > 0, 'patch 本体を読み出せない');
  assert.ok(
    !/login-actions\/authenticate/.test(patchBody),
    'ログイン経路へ床を広げている。同経路は failureFactor のロックで標本を増やせず、'
    + ' 反復を前提とする決定 1 が成立しない（裁定が別に要る）',
  );
});

ok('7. 上流は Keycloak の Service と同じ名前・同じポートである', () => {
  const upstream = /name:\s*UPSTREAM_URL\s*\n\s*value:\s*http:\/\/([a-z0-9-]+):(\d+)/.exec(FLOOR_MANIFEST);
  assert.ok(upstream, 'UPSTREAM_URL を読み出せない');
  const [, host, port] = upstream;
  assert.ok(
    new RegExp(`kind: Service[\\s\\S]{0,200}name:\\s*${host}\\b`).test(KEYCLOAK_MANIFEST),
    `上流 ${host} という Service が Keycloak の宣言に無い`,
  );
  assert.ok(
    new RegExp(`-\\s*port:\\s*${port}\\b`).test(KEYCLOAK_MANIFEST),
    `上流のポート ${port} が Keycloak の Service に無い`,
  );
});

// 8 の器: istio-edge-up.sh を記録スタブ（kubectl / helm）の下で走らせ、発行されたコマンド列を返す。
// 🔴 クラスタへは一切触れない（PATH 上の kubectl / helm を差し替える。待受も立てない）。
// `kubectl -n kube-system get svc traefik` だけを非 0 にして「明け渡し済み」を即座に作り、待ちを発生させない。
const runEdgeUp = (extraEnv) => {
  const workdir = fs.mkdtempSync(path.join(os.tmpdir(), 'reset-floor-edge-up-'));
  const binDir = path.join(workdir, 'bin');
  fs.mkdirSync(binDir);
  const logFile = path.join(workdir, 'commands.log');
  fs.writeFileSync(logFile, '');
  const stub = (name, extra) => {
    const p = path.join(binDir, name);
    fs.writeFileSync(p, ['#!/usr/bin/env bash', `echo "${name} $*" >> "$STUB_LOG"`, ...extra, 'exit 0', ''].join('\n'));
    fs.chmodSync(p, 0o755);
  };
  stub('kubectl', ['case "$*" in *"get svc traefik"*) exit 1;; esac']);
  stub('helm', []);
  const base = { ...process.env };
  delete base.RESET_FLOOR; // 実行環境に漏れていても「未設定」を再現する
  delete base.ISTIO_MTLS_MODE;
  const origPath = process.env.PATH || process.env.Path || '';
  const r = spawnSync('bash', [path.join('scripts', 'istio-edge-up.sh')], {
    cwd: REPO_ROOT,
    env: { ...base, PATH: binDir + path.delimiter + origPath, STUB_LOG: logFile, ...extraEnv },
    encoding: 'utf8',
  });
  const lines = fs.readFileSync(logFile, 'utf8').split('\n').filter((l) => l.length > 0);
  try { fs.rmSync(workdir, { recursive: true, force: true }); } catch { /* best-effort */ }
  return { status: r.status, lines, stderr: r.stderr || '', error: r.error };
};
const appliesEdge = (lines, dir) => lines.some((l) => l === `kubectl apply -k ${dir}`);

ok('🔴 8. 起動器の既定は床を入れる（RESET_FLOOR 未設定で床の overlay。退路は 0。#1500）', () => {
  const dflt = runEdgeUp({});
  assert.ifError(dflt.error);
  assert.strictEqual(dflt.status, 0, `istio-edge-up.sh が stub 下で完走しない: ${dflt.stderr}`);
  assert.ok(appliesEdge(dflt.lines, 'deploy/local/edge-istio-reset-floor'),
    '既定（RESET_FLOOR 未設定）で床の overlay を当てていない（計画 ADR-0097 決定 2 は既定 1）');
  assert.ok(!appliesEdge(dflt.lines, 'deploy/local/edge-istio'),
    '既定で素の edge-istio も当てている（床の経路を上書きで消す）');
  const cmAt = dflt.lines.findIndex((l) => l.startsWith('kubectl create configmap reset-floor-script '));
  const ovAt = dflt.lines.findIndex((l) => l === 'kubectl apply -k deploy/local/edge-istio-reset-floor');
  assert.ok(cmAt >= 0 && cmAt < ovAt, '器の本体（ConfigMap）を overlay の apply より前に作っていない');

  const explicit = runEdgeUp({ RESET_FLOOR: '1' });
  assert.strictEqual(explicit.status, 0, explicit.stderr);
  // 🔴 パイプ `kubectl create … --dry-run=client -o yaml | kubectl apply -f -` は両側の stub が並行に起動し、
  //   採取順は create→apply / apply→create のどちらにもなり得る（k8s-local-up.test.js の #438 と同じ。本試験でも
  //   実行ごとに反転した ＝ AI レビューが 4 回中 1〜2 回の失敗を実測）。パイプの後段は前段と隣り合うことだけが
  //   確かなので、隣り合う `apply -f -` を前段の直後へ寄せてから比べる（順序以外の差は落とす）。
  const normalizePipes = (lines) => {
    const out = [...lines];
    for (let i = 0; i + 1 < out.length; i += 1) {
      if (out[i] === 'kubectl apply -f -' && out[i + 1].includes('--dry-run=client -o yaml')) {
        [out[i], out[i + 1]] = [out[i + 1], out[i]];
        i += 1;
      }
    }
    return out;
  };
  assert.deepStrictEqual(normalizePipes(explicit.lines), normalizePipes(dflt.lines),
    'RESET_FLOOR=1 と未設定で発行コマンドが違う（既定が 1 でない）');

  const off = runEdgeUp({ RESET_FLOOR: '0' });
  assert.strictEqual(off.status, 0, off.stderr);
  assert.ok(appliesEdge(off.lines, 'deploy/local/edge-istio'), 'RESET_FLOOR=0 で素の edge-istio を当てていない');
  assert.ok(!off.lines.some((l) => l.includes('reset-floor')), 'RESET_FLOOR=0 なのに床を足している（退路が効かない）');

  // 🔴 0 / 1 以外は**入口に触る前に**落ちる（Traefik を落としてから気付かない）。
  for (const bad of ['false', 'yes', '2']) {
    const r = runEdgeUp({ RESET_FLOOR: bad });
    assert.notStrictEqual(r.status, 0, `RESET_FLOOR=${bad} を受け付けた（黙って床を入れるか外す）`);
    assert.deepStrictEqual(r.lines, [], `RESET_FLOOR=${bad} で落ちる前にクラスタへ触れた: ${r.lines.join(' / ')}`);
    assert.ok(/RESET_FLOOR/.test(r.stderr), '落ちた理由が RESET_FLOOR を名指ししていない');
  }
});

ok('8b. 切り戻しは床の器を消さない（器は近接 MTA の配備単位の持ち物。#1500）', () => {
  // 🔴 **散文ではなくコマンドの行を見る**（コメントで言及しただけの語を「消している」と読まない）。
  const cmds = EDGE_DOWN.split('\n').filter((l) => !/^\s*#/.test(l));
  assert.ok(!cmds.some((l) => /kubectl delete[^\n]*reset-floor/.test(l)),
    'istio-edge-down.sh が床の器か ConfigMap を消している（infra の宣言から外れ、G11 が落とす）');
  assert.ok(cmds.some((l) => /kubectl delete -k deploy\/local\/edge-istio\b/.test(l)),
    '切り戻しが VirtualService（床の経路を含む）を消していない');
});

ok('9. 器の自己試験（純関数）も通る', () => {
  assert.strictEqual(floor.holdDelayMs(0, 10, 150), 140);
  assert.strictEqual(floor.holdDelayMs(0, 300, 150), 0);
  assert.ok(floor.HOP_BY_HOP.has('transfer-encoding'));
});

console.log(`[reset-floor.test] OK: ${passed} 件`);
