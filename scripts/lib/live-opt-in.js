#!/usr/bin/env node
'use strict';
/*
 * lib/live-opt-in.js — NFR, #1550: 稼働クラスタへ当たる scripts の「明示の指定」を 1 か所で判定する。
 * 外部依存ゼロ（Node 標準モジュールのみ）。シェル側の対は lib/live-opt-in.sh（同じ規則・同じ文言・同じ終了コード）。
 *
 * 背景（#1550）: 作業エージェントがワークフローの `node scripts/...` の行をまとめて実行し、
 *   check-password-reset-mail.js が稼働中の Keycloak へ本物のパスワード再設定を申請した（mailpit が受けた）。
 *   check-login-existence-disclosure.js も Keycloak へ当たり、seed-abac-policies.js も起動した。
 *   **スクリプトの名前や引数の無さからは、稼働クラスタへ当たるかどうかが読めない。**
 *
 * 規則:
 *   - 稼働クラスタ（kubectl / port-forward / localhost のサービス / Keycloak の管理 API など）へ当たる経路は、
 *     `--live` か環境変数 `LIVE=1` が無ければ**何もせずに**非 0（EXIT_CODE）で終わる。
 *   - 🔴 **判定は副作用の前に置く。** 拒否の経路は子プロセスもネットワークも開かない。
 *   - 稼働クラスタに触れないモード（`--self-test` / `--dry-run` / `--input` など）は指定なしで今のまま動く。
 *   - `LIVE` は `1` だけを受け付ける（`true` / `yes` は指定とみなさない。値の揺れを黙って通さない）。
 *
 * 対象の一覧（単一情報源）: scripts/live-scripts.json。人向けの一覧は scripts/README.md「稼働クラスタへ当たる scripts」。
 *
 * 使い方:
 *   const { requireLiveOptIn, withoutLiveFlag } = require('./lib/live-opt-in.js');
 *   requireLiveOptIn('seed-abac-policies', argv, { offline: '--dry-run' });   // 指定が無ければここで exit
 *   const rest = withoutLiveFlag(argv);                                        // 以降の引数解析へ渡す
 */

const LIVE_FLAG = '--live';
const EXIT_CODE = 3;

function isLiveOptedIn(argv, env = process.env) {
  return (Array.isArray(argv) && argv.includes(LIVE_FLAG)) || (env && env.LIVE === '1');
}

function withoutLiveFlag(argv) {
  return argv.filter((a) => a !== LIVE_FLAG);
}

function refusalMessage(name, offline) {
  const lines = [
    `[${name}] 稼働クラスタへ当たるため、明示の指定が無いので何もせずに終わります（#1550）。`,
    `  実行するなら ${LIVE_FLAG} を付けるか、環境変数 LIVE=1 を与えてください。`,
  ];
  if (offline) lines.push(`  稼働クラスタに触れないモード: ${offline}`);
  return `${lines.join('\n')}\n`;
}

/**
 * 指定が無ければ拒否の文言を stderr へ書き、EXIT_CODE で終わる（戻らない）。指定があれば何もしない。
 * @param {string} name 文言に出すスクリプト名
 * @param {string[]} argv 引数（`--live` を含み得る）
 * @param {{ offline?: string, env?: object }} [opts]
 */
function requireLiveOptIn(name, argv, opts = {}) {
  if (isLiveOptedIn(argv, opts.env || process.env)) return;
  process.stderr.write(refusalMessage(name, opts.offline));
  process.exit(EXIT_CODE);
}

module.exports = { LIVE_FLAG, EXIT_CODE, isLiveOptedIn, withoutLiveFlag, refusalMessage, requireLiveOptIn };
