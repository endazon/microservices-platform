#!/usr/bin/env node
'use strict';
/*
 * reset-floor.test.js
 * SC-15, FR-05, NFR-05, NFR-09, NFR-13, ADR-0078 決定 1, ADR-0094 決定 2, ADR-0097 決定 2, ADR-0111, IADR-0432 (#1410 / #1500 / #1543 / #1544):
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
 *      RESET_FLOOR=0 は素の edge-istio を当てる（［2026-09-26 / #1543］検証で床の有無を比べる用途に限る。
 *      本番の退路ではない。計画 ADR-0111 決定 3）。0 / 1 以外は**入口に触る前に**落ちる。
 *      スクリプトを記録スタブの下で実際に走らせて確かめる（正規表現で字面を見るだけにしない）。
 *   9. 器の自己試験（純関数）も通る。
 *  10. 🔴 ［2026-09-26 / #1543］計画 ADR-0111 決定 1・2: **器は 2 レプリカ以上で、PodDisruptionBudget
 *      （minAvailable: 1・selector が器の Pod と一致）を持つ。分散は ScheduleAnyway だけ**（単一ノードの
 *      ローカルで 2 つ目を Pending にしない）。**readiness は上流を映さない tcpSocket のまま**。**経路の宛先は
 *      器 1 つだけ**（予備の経路なし）。判定は純関数にし、変異を当てて落ちることも同じ試験の中で確かめる。
 *  11. 🔴 ［2026-09-26 / #1544］NFR-21・計画 ADR-0111 フォローアップ 3: **器の全滅の検知が 1 本に繋がる** ——
 *      経路 B の 2 つの collector 設定が器の **Service**（名前・port は器のマニフェストが単一情報源）の `/metrics` を取る
 *      receiver を持ち metrics パイプラインへ入れ、compose は持たない。規則 `ResetFloorNoReadyEndpoint` は receiver の job の
 *      `up == 0`（critical・3 分以内）、同じ系列の `absent` が評価対象の群に対で居る。Grafana 版は `lt 1` で比べる。変異で落ちることも確かめる。
 *  12. 🔴 同上: 器は `GET /metrics` に**上流へ渡さず・床を待たず**・`Connection: close` で答え、申請の POST・クエリ付き・
 *      別メソッドは従来どおり床つきで中継する（127.0.0.1 の一時ポートで実際に打つ。外へは出ない）。
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
    // NFR, #1550: istio-edge-up.sh は明示の指定が無ければ何もせずに終わる。スタブの下なので LIVE=1 を与える。
    env: { ...base, PATH: binDir + path.delimiter + origPath, STUB_LOG: logFile, LIVE: '1', ...extraEnv },
    encoding: 'utf8',
  });
  const lines = fs.readFileSync(logFile, 'utf8').split('\n').filter((l) => l.length > 0);
  try { fs.rmSync(workdir, { recursive: true, force: true }); } catch { /* best-effort */ }
  return { status: r.status, lines, stderr: r.stderr || '', error: r.error };
};
const appliesEdge = (lines, dir) => lines.some((l) => l === `kubectl apply -k ${dir}`);

ok('🔴 8. 起動器の既定は床を入れる（RESET_FLOOR 未設定で床の overlay。0 は検証用の比較に限る。#1500 / #1543）', () => {
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
  assert.ok(!off.lines.some((l) => l.includes('reset-floor')), 'RESET_FLOOR=0 なのに床を足している（比較の実行で床が外れない）');
  // ［2026-09-26 / #1543］計画 ADR-0111 決定 3: 0 を与えた者に「本番の退路ではない」ことを告げる。
  assert.ok(/本番の退路に使わない/.test(off.stderr), 'RESET_FLOOR=0 の実行で「本番の退路に使わない」を告げていない');

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

// ===== 10: 器の可用性（［2026-09-26 / #1543］計画 ADR-0111 決定 1・2）=====
// 複数ドキュメント YAML を `---` で割り、kind ごとに本文を返す（外部依存ゼロ。コメント行は落とす ——
// コメントで言及しただけの語を「宣言している」と読まない）。
const splitDocs = (text) => text
  .split(/^---\s*$/m)
  .map((d) => d.split('\n').filter((l) => !/^\s*#/.test(l)).join('\n'))
  .filter((d) => /\S/.test(d));
const docOfKind = (docs, kind) => docs.filter((d) => new RegExp(`^kind:\\s*${kind}\\s*$`, 'm').test(d));
const appLabelOf = (block) => {
  const m = /matchLabels:\s*\{\s*app:\s*([a-z0-9-]+)\s*\}/.exec(block)
    || /matchLabels:\s*\n\s+app:\s*([a-z0-9-]+)/.exec(block);
  return m ? m[1] : null;
};

/**
 * 器のマニフェストと経路の overlay から、計画 ADR-0111 決定 1・2 に反する点を列挙する。**純関数**。
 * @returns {string[]} 失敗の理由（空なら合格）
 */
function availabilityFailures(manifest, overlay) {
  const failures = [];
  const docs = splitDocs(manifest);
  const deps = docOfKind(docs, 'Deployment');
  if (deps.length !== 1) return [`器の Deployment が 1 つでない（${deps.length} 件）`];
  const dep = deps[0];
  const ns = (/^\s{2}namespace:\s*(\S+)/m.exec(dep) || [])[1];
  const podLabel = appLabelOf(dep);

  // 決定 1: 2 レプリカ以上。
  const replicas = /^\s{2}replicas:\s*(\d+)\s*$/m.exec(dep);
  if (!replicas) failures.push('Deployment が replicas を明示していない（既定の 1 になる）');
  else if (Number(replicas[1]) < 2) failures.push(`replicas=${replicas[1]}（2 以上が要る。1 では Pod の作り直しごとに申請が 503 になる）`);

  // 決定 1: PodDisruptionBudget で ready を 0 にしない。
  const pdbs = docOfKind(docs, 'PodDisruptionBudget');
  if (pdbs.length !== 1) {
    failures.push(`PodDisruptionBudget が 1 つでない（${pdbs.length} 件）`);
  } else {
    const pdb = pdbs[0];
    if (!/^apiVersion:\s*policy\/v1\s*$/m.test(pdb)) failures.push('PDB の apiVersion が policy/v1 でない');
    const pdbNs = (/^\s{2}namespace:\s*(\S+)/m.exec(pdb) || [])[1];
    if (pdbNs !== ns) failures.push(`PDB の namespace（${pdbNs}）が器（${ns}）と違う（器を守らない）`);
    const sel = appLabelOf(pdb);
    if (!sel || sel !== podLabel) failures.push(`PDB の selector（${sel}）が器の Pod ラベル（${podLabel}）と一致しない（器を守らない）`);
    if (/maxUnavailable:/.test(pdb)) failures.push('PDB が maxUnavailable を使っている（replicas を 1 へ絞ると最後の 1 つの退避を許す。minAvailable: 1 を使う）');
    const minAv = /minAvailable:\s*(\S+)/.exec(pdb);
    if (!minAv || minAv[1] !== '1') failures.push(`PDB の minAvailable が 1 でない（${minAv ? minAv[1] : '無し'}）`);
  }

  // 単一ノードのローカルで 2 つ目を Pending にしない（分散は「できれば」だけ）。
  if (/requiredDuringSchedulingIgnoredDuringExecution/.test(dep)) failures.push('必須の (anti-)affinity がある（単一ノードで 2 つ目が Pending になる）');
  if (/whenUnsatisfiable:\s*DoNotSchedule/.test(dep)) failures.push('分散が DoNotSchedule である（単一ノードで 2 つ目が Pending になる）');

  // 決定 2: readiness は上流を映さない。
  const ready = /readinessProbe:\s*\n((?:\s{12,}.*\n)+)/.exec(dep);
  if (!ready) failures.push('readinessProbe が無い');
  else if (!/tcpSocket:/.test(ready[1]) || /httpGet:|exec:|grpc:/.test(ready[1])) {
    failures.push('readinessProbe が tcpSocket でない（上流を叩くと Keycloak が落ちた瞬間に床を待たない即座の 503 になる）');
  }

  // 決定 2: 経路の宛先は器 1 つだけ（予備の経路を足さない）。
  const patch = overlay.slice(overlay.indexOf('patch: |-'));
  const hosts = [...patch.matchAll(/host:\s*(\S+)/g)].map((m) => m[1]);
  if (hosts.length !== 1 || hosts[0] !== `reset-floor.${ns}.svc.cluster.local`) {
    failures.push(`経路の宛先が器 1 つだけでない（${hosts.join(', ') || '無し'}）。予備の経路は床の無い経路へ黙って戻る`);
  }
  return failures;
}

ok('🔴 10. 器は 2 レプリカ ＋ PDB（minAvailable 1）で、分散は ScheduleAnyway・readiness は上流を映さず・予備の経路なし（#1543）', () => {
  assert.deepStrictEqual(availabilityFailures(FLOOR_MANIFEST, OVERLAY_KUST), []);

  // 🔴 変異を当てて落ちることを確かめる（守る側へ誤る変異を落とせない試験は門として足りない）。
  const mutate = (from, to, text = FLOOR_MANIFEST) => {
    assert.ok(from.test ? from.test(text) : text.includes(from), `変異の前提が見つからない: ${from}`);
    return text.replace(from, to);
  };
  const cases = [
    ['replicas を 1 へ戻す', mutate(/^(\s{2}replicas:\s*)\d+/m, '$11'), OVERLAY_KUST],
    ['PDB を消す', mutate(/kind:\s*PodDisruptionBudget/, 'kind: ConfigMap'), OVERLAY_KUST],
    // 行頭で当てる（コメントの中の「minAvailable: 1」を書き換えて変異したつもりにならない）。
    ['PDB を maxUnavailable へ', mutate(/^(\s{2})minAvailable:\s*1/m, '$1maxUnavailable: 1'), OVERLAY_KUST],
    ['PDB の selector を外す', mutate(/(kind:\s*PodDisruptionBudget[\s\S]*?matchLabels:\s*\{\s*app:\s*)reset-floor/, '$1other'), OVERLAY_KUST],
    ['分散を DoNotSchedule へ', mutate(/whenUnsatisfiable:\s*ScheduleAnyway/, 'whenUnsatisfiable: DoNotSchedule'), OVERLAY_KUST],
    ['readiness を httpGet で上流へ', mutate(/tcpSocket:\s*\{\s*port:\s*8080\s*\}/, 'httpGet: { path: /health, port: 8080 }'), OVERLAY_KUST],
    ['予備の宛先を足す', FLOOR_MANIFEST, mutate(
      /(\n(\s+)- destination:\n\s+host: reset-floor[^\n]*\n\s+port:\n\s+number: 8080)/,
      '$1\n$2- destination:\n$2    host: keycloak.platform-infra.svc.cluster.local', OVERLAY_KUST)],
  ];
  for (const [name, manifest, overlay] of cases) {
    assert.ok(availabilityFailures(manifest, overlay).length > 0, `変異「${name}」を落とせない`);  }
});

// ===== 11・12: 全滅の検知（［2026-09-26 / #1544］NFR-21・計画 ADR-0111 フォローアップ 3）=====
// 器・collector・規則の 3 つが 1 本の糸で繋がって初めて鳴る。どれか 1 つがズレると**静かに鳴らなくなる**
// （receiver が別の job 名で出す・別のポートを取る・パイプラインに入っていない・器が /metrics を上流へ渡す）。
const COLLECTOR_DEFAULT = read('deploy', 'local', 'infra', 'otel-collector.yaml');
const COLLECTOR_FORWARD = read('deploy', 'local', 'observability', 'otel-collector-forward.yaml');
const COLLECTOR_COMPOSE = read('deploy', 'otel-collector-config.yaml');
const ALERTS_COMPOSE = read('deploy', 'prometheus', 'alerts.yml');
const GRAFANA_ALERTS = read('deploy', 'grafana', 'provisioning', 'alerting', 'slo-alerts.yaml');
const { parseRules } = require('./check-prometheus-alerts-parity.js');

const RECEIVER = 'prometheus/reset-floor';
const stripComments = (text) => text.split('\n').filter((l) => !/^\s*#/.test(l)).join('\n');

/** collector 設定から床の receiver の宣言を読む。無ければ null。**純関数**。 */
function floorReceiverOf(collectorText) {
  const text = stripComments(collectorText);
  const at = text.indexOf(`${RECEIVER}:`);
  if (at < 0) return null;
  const indent = /([ \t]*)$/.exec(text.slice(0, at))[1].length;
  const body = [];
  for (const l of text.slice(at).split('\n').slice(1)) {
    if (l.trim() !== '' && (/^(\s*)/.exec(l)[1].length <= indent)) break; // 兄弟キーへ出た
    body.push(l);
  }
  const block = body.join('\n');
  const pick = (re) => (re.exec(block) || [])[1] || null;
  const targets = pick(/targets:\s*\[([^\]]*)\]/);
  // `service.pipelines.metrics` の塊だけを読む（`service.telemetry.metrics` や traces の receivers と取り違えない）。
  const pipelines = text.slice(Math.max(0, text.indexOf('pipelines:')));
  const pm = /^([ \t]*)metrics:\s*$/m.exec(pipelines);
  let metricsBlock = '';
  if (text.includes('pipelines:') && pm) {
    const own = pm[1].length;
    const tail = [];
    for (const l of pipelines.slice(pm.index + pm[0].length).split('\n').slice(1)) {
      if (l.trim() !== '' && (/^(\s*)/.exec(l)[1].length <= own)) break;
      tail.push(l);
    }
    metricsBlock = tail.join('\n');
  }
  const receiversOfMetrics = (/receivers:\s*\[([^\]]*)\]/.exec(metricsBlock) || [])[1] || '';
  return {
    jobName: pick(/job_name:\s*(\S+)/),
    metricsPath: pick(/metrics_path:\s*(\S+)/),
    scrapeInterval: pick(/scrape_interval:\s*(\S+)/),
    targets: targets ? targets.split(',').map((s) => s.trim().replace(/^['"]|['"]$/g, '')).filter(Boolean) : [],
    inMetricsPipeline: receiversOfMetrics.split(',').map((s) => s.trim()).includes(RECEIVER),
  };
}

/** 器のマニフェストの Service から名前と port を読む（scrape 先の単一情報源）。**純関数**。 */
function floorServiceOf(manifest) {
  const svc = docOfKind(splitDocs(manifest), 'Service')[0];
  if (!svc) return null;
  return { name: (/^\s{2}name:\s*(\S+)/m.exec(svc) || [])[1], port: (/^\s+port:\s*(\d+)/m.exec(svc) || [])[1] };
}

/** Grafana の provisioning から、title のルールの expr と evaluator を読む。**純関数**。 */
function grafanaRuleOf(text, title) {
  const at = text.indexOf(`title: ${title}\n`);
  if (at < 0) return null;
  const rest = text.slice(at);
  const next = rest.slice(1).search(/\n\s*- uid:|\n {2}- orgId:/);
  const block = next < 0 ? rest : rest.slice(0, next + 1);
  return {
    expr: (/expr:\s*'([^']*)'/.exec(block) || [])[1] || null,
    evaluator: (/evaluator:\s*\{\s*type:\s*(\w+),\s*params:\s*\[([^\]]*)\]/.exec(block) || []).slice(1).join(' ') || null,
    noDataState: (/noDataState:\s*(\S+)/.exec(block) || [])[1] || null,
  };
}

/**
 * 全滅の検知の配線が繋がっているかを判定する。**純関数**。
 * @returns {string[]} 失敗の理由（空なら合格）
 */
function detectionWiringFailures({ manifest, collectors, composeCollector, alerts, grafana, metricsPath }) {
  const failures = [];
  const svc = floorServiceOf(manifest);
  if (!svc || !svc.name || !svc.port) return ['器の Service の名前・port を読めない（scrape 先の単一情報源が無い）'];
  const expectedTarget = `${svc.name}:${svc.port}`;
  const jobs = new Set();
  for (const [label, text] of Object.entries(collectors)) {
    const r = floorReceiverOf(text);
    if (!r) { failures.push(`${label}: ${RECEIVER} receiver が無い（opt-in の apply で collector を差し替えた瞬間に観測が消える）`); continue; }
    if (!r.inMetricsPipeline) failures.push(`${label}: ${RECEIVER} が metrics パイプラインの receivers に無い（宣言しても取りに行かない）`);
    if (r.targets.length !== 1 || r.targets[0] !== expectedTarget) {
      failures.push(`${label}: scrape 先が器の Service（${expectedTarget}）でない（${r.targets.join(', ') || '無し'}）。Pod ではなく Service を見る`);
    }
    if (r.metricsPath !== metricsPath) failures.push(`${label}: metrics_path（${r.metricsPath}）が器の答える口（${metricsPath}）と違う（上流へ渡り up=0 のまま鳴り続ける）`);
    if (!r.scrapeInterval) failures.push(`${label}: scrape_interval を明示していない（検出時間の見積りの前提）`);
    jobs.add(r.jobName);
  }
  if (jobs.size !== 1) failures.push(`collector 設定の job_name が揃っていない（${[...jobs].join(', ')}）`);
  const job = [...jobs][0];
  if (floorReceiverOf(composeCollector)) failures.push('compose の collector 設定が床の receiver を持つ（compose に器は居ない。宛先の無い scrape が失敗し続ける）');

  const rules = new Map(parseRules(alerts).map((r) => [r.alert, r]));
  const main = rules.get('ResetFloorNoReadyEndpoint');
  if (!main) failures.push('ResetFloorNoReadyEndpoint が alerts.yml に無い');
  else {
    if (main.expr !== `up{job="${job}"} == 0`) failures.push(`ResetFloorNoReadyEndpoint の式が receiver の job（${job}）の up を見ていない: ${main.expr}`);
    if (main.severity !== 'critical') failures.push(`ResetFloorNoReadyEndpoint の severity が critical でない（${main.severity}）`);
    const m = /^(\d+)m$/.exec(main.for || '');
    if (!m || Number(m[1]) > 3) failures.push(`ResetFloorNoReadyEndpoint の for（${main.for}）が 3 分を超える（scrape 30 秒 ＋ 評価を足して NFR-21 の 5 分に収まらない）`);
  }
  const absentRule = rules.get('ResetFloorUpSeriesAbsent');
  if (!absentRule) failures.push('ResetFloorUpSeriesAbsent が alerts.yml に無い（receiver が消えると主の規則は空ベクタになり鳴らない）');
  else if (absentRule.expr !== `absent(up{job="${job}"})`) failures.push(`ResetFloorUpSeriesAbsent の式が主の規則と同じ系列を見ていない: ${absentRule.expr}`);
  else if (absentRule.group !== 'platform-slo-evaluation-target') failures.push(`ResetFloorUpSeriesAbsent の群が評価対象の群でない（${absentRule.group}）`);

  const g = grafanaRuleOf(grafana, 'ResetFloorNoReadyEndpoint');
  if (!g) failures.push('Grafana 版の ResetFloorNoReadyEndpoint が無い');
  else if (g.expr !== `up{job="${job}"}` || g.evaluator !== 'lt 1') {
    failures.push(`Grafana 版は up をそのまま取り lt 1 で比べる（== 0 を gt 0 で比べると永久に発火しない）: expr=${g.expr} evaluator=${g.evaluator}`);
  } else if (g.noDataState !== 'OK') {
    failures.push(`Grafana 版 ResetFloorNoReadyEndpoint の noDataState が OK でない（${g.noDataState}。不在は対の規則が拾う）`);
  }
  const ga = grafanaRuleOf(grafana, 'ResetFloorUpSeriesAbsent');
  if (!ga) failures.push('Grafana 版の ResetFloorUpSeriesAbsent が無い');
  else if (ga.expr !== `absent(up{job="${job}"})` || ga.evaluator !== 'gt 0' || ga.noDataState !== 'OK') {
    failures.push(`Grafana 版 ResetFloorUpSeriesAbsent は absent(up) を gt 0・noDataState: OK で見る（NoData だと正常時に恒常発火）: expr=${ga.expr} evaluator=${ga.evaluator} noDataState=${ga.noDataState}`);
  }
  return failures;
}

const WIRING = {
  manifest: FLOOR_MANIFEST,
  collectors: { 'infra/otel-collector.yaml': COLLECTOR_DEFAULT, 'observability/otel-collector-forward.yaml': COLLECTOR_FORWARD },
  composeCollector: COLLECTOR_COMPOSE,
  alerts: ALERTS_COMPOSE,
  grafana: GRAFANA_ALERTS,
  metricsPath: floor.METRICS_PATH,
};

ok('🔴 11. 全滅の検知が器・collector 2 設定・規則・Grafana で 1 本に繋がる（#1544）', () => {
  assert.deepStrictEqual(detectionWiringFailures(WIRING), []);

  const mutate = (key, from, to, sub) => {
    const base = sub ? WIRING[key][sub] : WIRING[key];
    assert.ok(from.test ? from.test(base) : base.includes(from), `変異の前提が見つからない: ${from}`);
    const next = base.replace(from, to);
    return sub ? { ...WIRING, [key]: { ...WIRING[key], [sub]: next } } : { ...WIRING, [key]: next };
  };
  const FWD = 'observability/otel-collector-forward.yaml';
  const DEF = 'infra/otel-collector.yaml';
  const cases = [
    ['転送構成から receiver を消す', mutate('collectors', /prometheus\/reset-floor:/, 'prometheus/other:', FWD)],
    ['既定構成で pipeline から外す', mutate('collectors', /, prometheus\/reset-floor\]/, ']', DEF)],
    ['job 名を片方だけ変える', mutate('collectors', /job_name: reset-floor/, 'job_name: floor', FWD)],
    ['scrape 先のポートを変える', mutate('collectors', /'reset-floor:8080'/, "'reset-floor:9000'", DEF)],
    ['metrics_path を変える', mutate('collectors', /metrics_path: \/metrics/, 'metrics_path: /healthz', FWD)],
    ['compose に receiver を足す', { ...WIRING, composeCollector: `${COLLECTOR_COMPOSE}\n  ${RECEIVER}:\n    config: {}\n` }],
    ['規則の job を変える', mutate('alerts', 'expr: up{job="reset-floor"} == 0', 'expr: up{job="floor"} == 0')],
    ['規則を warning へ下げる', mutate('alerts', /(- alert: ResetFloorNoReadyEndpoint[\s\S]*?severity: )critical/, '$1warning')],
    ['for を 10m へ延ばす', mutate('alerts', /(- alert: ResetFloorNoReadyEndpoint\n\s+expr: [^\n]*\n\s+for: )2m/, '$110m')],
    ['不在の規則を消す', mutate('alerts', '- alert: ResetFloorUpSeriesAbsent', '- alert: SomethingElse')],
    ['Grafana 版を == 0 と gt 0 にする', mutate('grafana', /expr: 'up\{job="reset-floor"\}'([\s\S]*?)type: lt, params: \[1\]/, 'expr: \'up{job="reset-floor"} == 0\'$1type: gt, params: [0]')],
    ['Grafana 版の全滅を NoData にする', mutate('grafana', /(title: ResetFloorNoReadyEndpoint\n\s+condition: C\n\s+for: 2m\n\s+noDataState: )OK/, '$1NoData')],
    ['Grafana 版の不在の式を変える', mutate('grafana', "expr: 'absent(up{job=\"reset-floor\"})'", "expr: 'absent(up{job=\"floor\"})'")],
    ['Grafana 版の不在を NoData にする', mutate('grafana', /(title: ResetFloorUpSeriesAbsent\n\s+condition: C\n\s+for: 5m\n\s+noDataState: )OK/, '$1NoData')],
    ['器の口を別のパスへ', { ...WIRING, metricsPath: '/-/metrics' }],
    ['器の Service の port を変える', mutate('manifest', /(kind: Service[\s\S]*?- name: http\n\s+port: )8080/, '$19090')],
  ];
  for (const [name, input] of cases) {
    assert.ok(detectionWiringFailures(input).length > 0, `変異「${name}」を落とせない`);
  }
});

// ===== 12: 器の /metrics の実挙動（実ソケット。外部へは出ない —— 127.0.0.1 の一時ポートだけ）=====
const http = require('http');
const listen = (server) => new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(server.address().port)));
const close = (server) => new Promise((resolve) => server.close(() => resolve()));
const request = (port, method, p, body, agent = false) => new Promise((resolve, reject) => {
  const startedAt = Date.now();
  const req = http.request({ host: '127.0.0.1', port, method, path: p, agent }, (res) => {
    const chunks = [];
    res.on('data', (c) => chunks.push(c));
    res.on('end', () => resolve({
      status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks).toString('utf8'), elapsed: Date.now() - startedAt,
    }));
  });
  req.on('error', reject);
  if (body) req.write(body);
  req.end();
});

async function behaviourTest() {
  const hits = [];
  const upstream = http.createServer((req, res) => {
    hits.push(`${req.method} ${req.url}`);
    req.resume();
    req.on('end', () => { res.writeHead(200, { 'content-type': 'text/plain' }); res.end('upstream'); });
  });
  const upstreamPort = await listen(upstream);
  // 床を大きく取る（1500 ms）。/metrics が床を掛けていれば所要時間で分かる。
  const floorMs = 1500;
  const server = floor.createServer({ upstream: new URL(`http://127.0.0.1:${upstreamPort}`), floorMs });
  const port = await listen(server);
  // 🔴 scrape と同じく**接続を使い回したがる**クライアントで打つ。`agent: false` だとクライアント自身が
  //   `Connection: close` を送り、サーバーがそれを返すだけなので、器が閉じなくても試験が通ってしまう。
  const keepAlive = new http.Agent({ keepAlive: true });
  try {
    const m = await request(port, 'GET', '/metrics', undefined, keepAlive);
    assert.strictEqual(m.status, 200);
    assert.match(m.headers['content-type'], /^text\/plain; version=0\.0\.4/);
    assert.strictEqual(m.headers.connection, 'close', '接続を閉じていない（使い回した接続が Service から外れた Pod に残る）');
    assert.match(m.body, /^reset_floor_up 1$/m);
    assert.ok(m.elapsed < floorMs / 2, `/metrics が床を待っている（${m.elapsed} ms）`);
    assert.deepStrictEqual(hits, [], '/metrics を上流へ渡している（Keycloak の健康を映してしまう）');

    // 陰性対照: 口を広げない —— 申請の POST・クエリ付き・別メソッドは上流へ中継し、床を掛ける。
    const post = await request(port, 'POST', '/realms/msp/login-actions/reset-credentials?session_code=x', 'username=a');
    assert.strictEqual(post.body, 'upstream');
    assert.ok(post.elapsed >= floorMs - 50, `申請の POST が床を待っていない（${post.elapsed} ms）`);
    await request(port, 'GET', '/metrics?x=1');
    await request(port, 'POST', '/metrics');
    assert.deepStrictEqual(hits, [
      'POST /realms/msp/login-actions/reset-credentials?session_code=x', 'GET /metrics?x=1', 'POST /metrics',
    ]);
  } finally {
    keepAlive.destroy();
    await close(server);
    await close(upstream);
  }
  passed += 1;
  console.log('  ok  🔴 12. 器は GET /metrics に上流へ渡さず床も掛けず Connection: close で答え、申請の POST は従来どおり床つきで中継する（#1544）');
}

behaviourTest().then(() => {
  console.log(`[reset-floor.test] OK: ${passed} 件`);
}).catch((e) => {
  console.error(e);
  process.exit(1);
});
