'use strict';
/**
 * NFR-21: promtool（test rules・check rules）を走らせる共通器。scripts/cloud-ops.test.js（#2527 で導入）と
 * scripts/deploy-cloud-egress-audit.test.js が使う（作業仕様書 20261108_cloud-ops-promtool-flake / 20261108_egress-audit-promtool-sandbox）。
 *
 * promtool の単体試験は評価のたびに一時の TSDB（WAL と head の chunk）を TMPDIR の下に書く。既定の /tmp を使うと、ほかの処理と同じ
 * 置き場の空きを取り合い、空きが尽きると評価の途中で書き込みに失敗する（「no space left on device」の行が並ぶか、強制終了で出力が
 * 空のまま 0 でない終了になる）。また、強制終了の後は一時の TSDB が /tmp に残る。
 *   - 一時の置き場は呼び出しごとに tmpRoot（呼び出し側の砂場）の下に作り、終われば消す（TMPDIR）。
 *   - 戻り値の why は、終了のコード・シグナル・標準出力・標準エラーをまとめた説明（空の出力のまま落ちて理由が読めない、を防ぐ）。
 *   - infra は、期待の照合の外の理由（置き場の書き込みの失敗・一時の置き場を開けない・シグナル）で落ちたか。陰性対照は infra でない
 *     失敗（期待との食い違い）だけを「落ちた」と数える（置き場の失敗で陰性対照が黙って通らないように）。
 * 外部依存ゼロ（Node 標準モジュール ＋ promtool）。
 */
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO_ROOT = path.resolve(__dirname, '..', '..');
const INFRA_PATTERN = /no space left on device|opening test storage|write to WAL|cannot allocate memory/i;

/**
 * @param {string[]} args promtool の引数（例: ['test', 'rules', file]）
 * @param {{ tmpRoot: string, cwd?: string }} opts tmpRoot は一時の置き場を作る親（呼び出し側の砂場）
 */
function promtool(args, { tmpRoot, cwd = REPO_ROOT } = {}) {
  if (!tmpRoot) throw new Error('promtool: tmpRoot（一時の置き場を作る砂場）が要る');
  const tmp = fs.mkdtempSync(path.join(tmpRoot, 'promtool-tmp-'));
  try {
    const r = spawnSync('promtool', args, { cwd, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, env: { ...process.env, TMPDIR: tmp } });
    if (r.error) throw new Error(`promtool を実行できない（PATH に無い可能性）: ${r.error.message}`);
    const out = `${r.stdout || ''}${r.stderr || ''}`;
    r.infra = r.signal != null || INFRA_PATTERN.test(out);
    r.why = `promtool ${args.join(' ')}: 終了 ${r.status}${r.signal ? `（シグナル ${r.signal}）` : ''}${r.infra ? '・期待の照合の外の理由で落ちた（一時の置き場・シグナル）' : ''}\n${out.trim() ? out.split('\n').filter((l) => l.trim()).slice(0, 40).join('\n') : '（出力なし）'}`;
    return r;
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
}

/** 陰性対照: 期待との食い違いで落ちた（終了 1・置き場の失敗やシグナルではない・`FAILED:` を出した）。 */
const failedOnExpectation = (r) => r.status === 1 && !r.infra && /FAILED:/.test(`${r.stdout}${r.stderr}`);

module.exports = { promtool, failedOnExpectation, INFRA_PATTERN };
