#!/usr/bin/env node
'use strict';
/*
 * reset-floor.test.js
 * SC-15, FR-05, NFR-09, ADR-0078 決定 1, ADR-0094 決定 2, IADR-0432 (#1410):
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
 *   2. 🔴 **既定の描画に床が 1 行も出ない**（opt-in。`deploy/mail-relay/kustomization.yaml` は
 *      床を参照しない）。既定がバイト等価であることの機械的な裏付けである。
 *   3. opt-in の overlay が**器と経路の両方**を足す（片方だけでは効かない）。
 *   4. 🔴 経路が **POST だけ**を床へ向け、**catch-all より前**に居る（Istio の route は先勝ち）。
 *   5. 🔴 経路が **realm 名を書き写していない**（realm 宣言が単一情報源。改名で静かに外れる）。
 *   6. 🔴 **ログイン経路へ床を掛けていない**（ADR-0094 決定 4 は同経路を判定の対象外とした）。
 *   7. 上流が Keycloak の Service と**同じ名前・同じポート**である。
 *
 * 外部依存ゼロ（Node 標準 assert のみ）。実行: node scripts/reset-floor.test.js
 */
const assert = require('assert');
const fs = require('fs');
const path = require('path');

const floor = require('../deploy/mail-relay/reset-floor.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const read = (...p) => fs.readFileSync(path.join(REPO_ROOT, ...p), 'utf8');

const FLOOR_MANIFEST = read('deploy', 'mail-relay', 'reset-floor', 'reset-floor.yaml');
const FLOOR_KUST = read('deploy', 'mail-relay', 'reset-floor', 'kustomization.yaml');
const RELAY_KUST = read('deploy', 'mail-relay', 'kustomization.yaml');
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

ok('🔴 2. 床は opt-in である（近接 MTA の配備単位は床を参照しない＝既定の描画は等価）', () => {
  assert.ok(
    !/reset-floor/.test(RELAY_KUST),
    'deploy/mail-relay/kustomization.yaml が床を参照している（既定の描画が変わる。'
    + ' 床は ADR-0094 の着手可否の注記が「覆り得る」と名指しした決定であり、実測まで既定へ入れない）',
  );
  assert.ok(/reset-floor\.yaml/.test(FLOOR_KUST), '床の kustomization が器を含んでいない');
});

ok('🔴 3. opt-in の overlay は器と経路の**両方**を足す（片方だけでは効かない）', () => {
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

ok('🔴 8. 起動器の既定は床を入れない（RESET_FLOOR 未設定なら素の overlay を当てる）', () => {
  assert.ok(/RESET_FLOOR:-0/.test(EDGE_UP), 'istio-edge-up.sh の既定が 0 でない');
  assert.ok(/kubectl apply -k deploy\/local\/edge-istio$/m.test(EDGE_UP),
    '既定の経路（素の edge-istio を当てる枝）が消えている');
  assert.ok(/kubectl apply -k deploy\/local\/edge-istio-reset-floor/.test(EDGE_UP),
    'opt-in の枝が overlay を当てていない');
  assert.ok(/reset-floor-script/.test(EDGE_UP), '器の本体を ConfigMap 化していない');
  assert.ok(/reset-floor/.test(EDGE_DOWN), '切り戻しが床を残す（誰も通らない Pod だけが残る）');
});

ok('9. 器の自己試験（純関数）も通る', () => {
  assert.strictEqual(floor.holdDelayMs(0, 10, 150), 140);
  assert.strictEqual(floor.holdDelayMs(0, 300, 150), 0);
  assert.ok(floor.HOP_BY_HOP.has('transfer-encoding'));
});

console.log(`[reset-floor.test] OK: ${passed} 件`);
