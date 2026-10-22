'use strict';
/*
 * compose-host-ports.js — NFR / IADR-0587（GHSA-j4qv-2w99-xg5p）
 *
 * **compose がホストへ出す口（`ports:`）が、すべてループバック（127.0.0.1）に限られていること**を判定する
 * （`check-deploy-manifests.js` の要点 8 が呼ぶ）。IP を省いた `"HOST:CONTAINER"` は全インターフェースで待ち受ける。
 *
 * ## 読む形
 *
 * - 短い形式: `- "127.0.0.1:HOST:CONTAINER"`（引用符の有無を問わない）・流れ形式 `ports: ["...", ...]`。
 *   先頭が `127.0.0.1:` で、その後ろに `:` がもう 1 つ在るものだけを通す（`127.0.0.1::8080` ＝ループバックの任意ポートも可）。
 * - 長い形式: `- target: 8080` に続く鍵、または `- { target: ..., host_ip: ... }`。`host_ip: 127.0.0.1` を要求する。
 * - `network_mode: host`: コンテナの待ち受けがそのままホストのものになるので、ポートを数えず失敗にする。
 *
 * ## 設計の要点
 *
 * 1. **許す値は 1 つ**（`127.0.0.1`）。変数展開（`${...}`）や `::1` 等は判定せず失敗にする（fail-closed）。
 * 2. **読めない形は失敗**（鍵の無い項目・閉じない流れ形式）。「読めなかった」と「問題が無い」を同じ出力にしない。
 * 3. **数を返す。** 呼び出し側は走査全体で 1 件も数えられなければ失敗にする（空の母集合を緑にしない。#797）。
 */

const LOOPBACK = '127.0.0.1';

function unquote(s) {
  const t = s.trim();
  if ((t.startsWith('"') && t.endsWith('"')) || (t.startsWith("'") && t.endsWith("'"))) return t.slice(1, -1);
  return t;
}

/** 行末のコメントを落とす（引用符の中の `#` は残す）。 */
function stripComment(line) {
  let q = null;
  for (let i = 0; i < line.length; i += 1) {
    const c = line[i];
    if (q) {
      if (c === q) q = null;
    } else if (c === '"' || c === "'") {
      q = c;
    } else if (c === '#' && (i === 0 || /\s/.test(line[i - 1]))) {
      return line.slice(0, i).trimEnd();
    }
  }
  return line.trimEnd();
}

/** 流れ形式の中身（`[` と `]` の間・`{` と `}` の間）をカンマで切る（引用符の中は切らない）。 */
function splitFlow(inner) {
  const out = [];
  let cur = '';
  let q = null;
  for (const c of inner) {
    if (q) {
      if (c === q) q = null;
      cur += c;
    } else if (c === '"' || c === "'") {
      q = c;
      cur += c;
    } else if (c === ',') {
      out.push(cur);
      cur = '';
    } else {
      cur += c;
    }
  }
  if (cur.trim() !== '') out.push(cur);
  return out.map((s) => s.trim()).filter((s) => s !== '');
}

/** 短い形式 1 件の判定。問題があれば理由の文字列、無ければ null。 */
function shortFormProblem(value) {
  const v = unquote(value);
  if (!v.startsWith(`${LOOPBACK}:`)) return `"${v}" がループバックに限られていない（"${LOOPBACK}:HOST:CONTAINER" と書く）`;
  if (!v.slice(LOOPBACK.length + 1).includes(':')) return `"${v}" の形を読めない（"${LOOPBACK}:HOST:CONTAINER" と書く）`;
  return null;
}

/** 長い形式 1 件（鍵 → 値）の判定。 */
function longFormProblem(map) {
  if (!('target' in map)) return `長い形式に target が無い（${JSON.stringify(map)}）`;
  const ip = map.host_ip === undefined ? undefined : unquote(String(map.host_ip));
  if (ip !== LOOPBACK) return `長い形式（target ${map.target}）の host_ip が ${ip === undefined ? '無い' : ip}（host_ip: ${LOOPBACK} と書く）`;
  return null;
}

function parseKeyValue(s) {
  const m = /^([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(.*)$/.exec(s.trim());
  return m ? [m[1], unquote(m[2])] : null;
}

/**
 * サービス 1 件の本文（`splitComposeServices` が返す 4 字下げの行）から `ports:` の項目を判定する。
 * @returns {{count: number, problems: string[]}}
 */
function checkServiceBody(body) {
  const lines = body.split('\n');
  const problems = [];
  let count = 0;
  for (let i = 0; i < lines.length; i += 1) {
    const line = stripComment(lines[i]);
    if (/^ {4}network_mode:\s*["']?host["']?\s*$/.test(line)) {
      problems.push('network_mode: host はコンテナの待ち受けをそのままホストへ出す（ports: でループバックに限って出す）');
      continue;
    }
    const head = /^ {4}ports:\s*(.*)$/.exec(line);
    if (!head) continue;
    const rest = head[1].trim();
    if (rest !== '') {
      if (!rest.startsWith('[') || !rest.endsWith(']')) {
        problems.push(`ports: の形を読めない（${rest}）`);
        continue;
      }
      for (const item of splitFlow(rest.slice(1, -1))) {
        count += 1;
        const p = item.startsWith('{') ? flowMapProblem(item) : shortFormProblem(item);
        if (p) problems.push(p);
      }
      continue;
    }
    // ブロック形式: 4 字より深い行が続く間。
    let j = i + 1;
    let itemsHere = 0;
    while (j < lines.length) {
      const raw = stripComment(lines[j]);
      if (raw.trim() === '') {
        j += 1;
        continue;
      }
      const indent = raw.length - raw.trimStart().length;
      if (indent <= 4) break;
      const item = /^(\s*)-\s+(.*)$/.exec(raw);
      if (!item) {
        problems.push(`ports: の項目を読めない（${raw.trim()}）`);
        j += 1;
        continue;
      }
      count += 1;
      itemsHere += 1;
      const v = item[2].trim();
      if (v.startsWith('{')) {
        const p = flowMapProblem(v);
        if (p) problems.push(p);
        j += 1;
        continue;
      }
      const kv = /^["']/.test(v) ? null : parseKeyValue(v);
      if (!kv) {
        const p = shortFormProblem(v);
        if (p) problems.push(p);
        j += 1;
        continue;
      }
      // 長い形式: この項目の続きの鍵（項目の `-` より深い字下げ）を集める。
      const map = { [kv[0]]: kv[1] };
      const itemIndent = item[1].length;
      j += 1;
      while (j < lines.length) {
        const r = stripComment(lines[j]);
        if (r.trim() === '') {
          j += 1;
          continue;
        }
        const ind = r.length - r.trimStart().length;
        if (ind <= itemIndent) break;
        const more = parseKeyValue(r);
        if (!more) {
          problems.push(`ports: の長い形式を読めない（${r.trim()}）`);
        } else {
          map[more[0]] = more[1];
        }
        j += 1;
      }
      const p = longFormProblem(map);
      if (p) problems.push(p);
    }
    if (itemsHere === 0) problems.push('ports: に項目が無い（読めない形の可能性がある）');
    i = j - 1;
  }
  return { count, problems };
}

function flowMapProblem(text) {
  const t = text.trim();
  if (!t.endsWith('}')) return `ports: の流れ形式の対応を読めない（${t}）`;
  const map = {};
  for (const part of splitFlow(t.slice(1, -1))) {
    const kv = parseKeyValue(part);
    if (!kv) return `ports: の流れ形式の対応を読めない（${t}）`;
    map[kv[0]] = kv[1];
  }
  return longFormProblem(map);
}

/**
 * compose の全文を判定する。
 * @param {string} text compose の全文
 * @param {string} label 報告に使うファイル名
 * @param {(text: string) => Map<string, string>} splitServices サービス名 → 本文（product-egress-defaults の splitComposeServices）
 * @returns {{count: number, problems: string[]}}
 */
function checkComposeHostPorts(text, label, splitServices) {
  let services;
  try {
    services = splitServices(text);
  } catch (e) {
    return { count: 0, problems: [`${label}: サービス定義を切り出せない（${e.message}）`] };
  }
  const problems = [];
  let count = 0;
  for (const [name, body] of services) {
    const r = checkServiceBody(body);
    count += r.count;
    for (const p of r.problems) problems.push(`${label}: サービス ${name}: ${p}`);
  }
  return { count, problems };
}

module.exports = { LOOPBACK, checkComposeHostPorts, checkServiceBody, shortFormProblem };
