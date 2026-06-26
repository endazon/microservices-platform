#!/usr/bin/env node
/*
 * PreToolUse(Bash) フック: 破壊的コマンドのガード（多層防御。settings の deny と併用）
 * 該当時は exit 2 でツール実行をブロックし、理由を stderr に出力する。依存ゼロ・常に安全弁つき。
 */
'use strict';

// rm に再帰(-r/-R/--recursive)と強制(-f/--force)の両方が付くか判定する。
// 同一束（-rf / -fr）でも分割（-r -f / --recursive --force）でも検知する。
function isDangerousRm(cmd) {
  if (!/\brm\b/.test(cmd)) return false;
  const seg = cmd.replace(/^[\s\S]*?\brm\b/, ''); // rm 以降のトークンを対象にする
  let recursive = false;
  let force = false;
  for (const tok of seg.split(/\s+/)) {
    if (/^--/.test(tok)) {
      if (tok === '--recursive') recursive = true;
      if (tok === '--force') force = true;
    } else if (/^-[A-Za-z]/.test(tok)) { // 短縮フラグ束（-rf 等）
      if (/[rR]/.test(tok)) recursive = true;
      if (/f/.test(tok)) force = true;
    }
  }
  return recursive && force;
}

function run(raw) {
  let cmd = '';
  try {
    const d = JSON.parse(raw || '{}');
    cmd = (d && d.tool_input && d.tool_input.command) || '';
  } catch (e) { return 0; }

  const danger = [
    /\bgit\s+push\b[^\n]*(--force\b|-f\b)/,
    /\bgit\s+reset\s+--hard\b/,
    /\bgit\s+clean\s+-[a-z]*f/,
  ];
  if (isDangerousRm(cmd) || danger.some((re) => re.test(cmd))) {
    process.stderr.write(
      '破壊的コマンドのためブロックしました: "' + cmd + '"\n' +
      'このリポジトリでは force push / reset --hard / rm -rf / git clean -f を禁止しています（CLAUDE.md 参照）。'
    );
    return 2; // PreToolUse: exit 2 でブロック
  }
  return 0;
}

let chunks = [];
process.stdin.on('data', (c) => chunks.push(c));
process.stdin.on('end', () => {
  let code = 0;
  try { code = run(Buffer.concat(chunks).toString('utf8')); } catch (e) { code = 0; }
  process.exit(code);
});
process.stdin.on('error', () => process.exit(0));
setTimeout(() => process.exit(0), 5000); // 安全弁
