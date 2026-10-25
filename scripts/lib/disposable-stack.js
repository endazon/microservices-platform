#!/usr/bin/env node
'use strict';
/*
 * lib/disposable-stack.js — NFR / #2031 / IADR-0630: **live の書き込みの向け先が使い捨てのスタックか**を判定する（単一の実装）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。使うのは sweep（scripts/e2e-live-sweep.js）と live-rw の門（e2e/live/support/readiness.ts）。
 *
 * 判定: `stack` が DISPOSABLE_STACKS（k3d のクラスタ名。e2e-live.yml の CLUSTER）にあり、kubectl の current-context が `k3d-<stack>` である。
 * 常設の PoC（context `rancher-desktop`）・本番へは向けない。PoC と使い捨てのスタックはエッジの URL がどちらも `https://localhost` なので、
 * URL では区別できない（IADR-0629 決定 3）。
 * 🔴 kubectl はローカルの kubeconfig を読むだけ（`config current-context`）で、クラスタへは当たらない。
 *
 * 使い方（CLI。live-rw の門が呼ぶ）:
 *   node scripts/lib/disposable-stack.js --stack <name>     # 一致すれば exit 0、違えば理由を stderr へ書いて exit 2
 */

const { spawnSync } = require('child_process');

/** 向けてよい使い捨てのスタック（k3d のクラスタ名）。PoC の context を足さない。 */
const DISPOSABLE_STACKS = ['e2e-live'];

function kubeContext() {
  const r = spawnSync('kubectl', ['config', 'current-context'], { encoding: 'utf8' });
  if (r.error) throw r.error;
  if (r.status !== 0) throw new Error(String(r.stderr || '').trim() || `exit ${r.status}`);
  return r.stdout;
}

/** 向け先が使い捨てのスタックか（書き込みの前に呼ぶ）。違えば例外。 */
function checkTarget(stack, currentContext = kubeContext) {
  if (!DISPOSABLE_STACKS.includes(stack)) {
    throw new Error(`向け先は使い捨てのスタックだけを受ける（${DISPOSABLE_STACKS.join(' / ')}）: ${stack}`);
  }
  let ctx;
  try {
    ctx = String(currentContext() || '').trim();
  } catch (e) {
    throw new Error(`kubectl の current-context を読めない（どのクラスタへ当たるか分からないまま流さない）: ${e.message}`);
  }
  if (ctx !== `k3d-${stack}`) {
    throw new Error(`kubectl の current-context が k3d-${stack} でない（${ctx || '空'}）。使い捨てのスタックの外へは向けない`);
  }
}

module.exports = { DISPOSABLE_STACKS, checkTarget, kubeContext };

if (require.main === module) {
  const argv = process.argv.slice(2);
  const i = argv.indexOf('--stack');
  const stack = i >= 0 ? argv[i + 1] : undefined;
  if (!stack || argv.length !== 2) {
    process.stderr.write('使い方: node scripts/lib/disposable-stack.js --stack <name>\n');
    process.exit(2);
  }
  try {
    checkTarget(stack);
    process.stdout.write(`[disposable-stack] 使い捨てのスタック ${stack}（context k3d-${stack}）\n`);
  } catch (e) {
    process.stderr.write(`[disposable-stack] 止めた: ${e.message}\n`);
    process.exit(2);
  }
}
