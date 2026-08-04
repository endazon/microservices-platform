#!/usr/bin/env node
'use strict';
/*
 * check-contract-schema.js
 * サービス間契約（Shared.Contracts のイベント・API スキーマ）の後方互換検査（NFR, Issue #465）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。方式の決定と根拠は docs/adr/IADR-0122。
 *
 * 何を守るか:
 *   全面再実装（#454）では 11 サービスを作り直すため、`Shared.Contracts` の契約が**両側同時更新で
 *   気付かれずに壊れる**危険が最も高い。契約の形が変わっても両側を同時に直せばテストは通る。
 *   本検査は契約の形そのものをリポジトリへスナップショットとして固定し、差分をレビュー可能にする。
 *
 * 抽出方式（IADR-0122 決定 1）:
 *   **C# ソースの構文解析**を正本とする。リフレクション（.NET SDK が要る）・OpenAPI
 *   （north-south の REST のみでイベントを含まない）・proto（本リポジトリに 0 件）はいずれも
 *   現時点で全契約を覆えない。限界は docs/specs/20260804_issue-465_*.md「限界」に明記する。
 *
 * 判定（IADR-0122 決定 2）:
 *   - 削除・型変更・必須化・位置変更・enum 値変更・const 値変更・属性変更 = **破壊的（fail）**
 *   - 追加（新型・新 enum メンバー・新 const・既定値付きの新メンバー）= **非破壊**
 *   - ただし**既定値の無いメンバーの追加**は破壊的に分類する（旧発行者のメッセージが必須項目を
 *     欠く／既存の位置引数コンストラクタが壊れるため）。既定値を付ければ非破壊になる。
 *   非破壊であっても baseline との差分がある限り exit 1 にする（= スナップショットテスト）。
 *   `--update` で baseline を更新し、**その差分を PR のレビュー対象にする**のが本検査の主眼である。
 *
 * 逃げ道（IADR-0122 決定 3。逃げ道の無いゲートは無視される: IADR-0115 の知見）:
 *   破壊的変更は scripts/contract-breaking-allowlist.json に承認エントリ
 *   （key / reason / approvedBy / issue / date すべて必須）を書き、`--update` を実行する。
 *   `--update` は承認エントリを baseline の `$acceptedBreakingChanges`（追記型の記録）へ移し、
 *   allowlist を空に戻す。承認の記録は baseline に残り、git 履歴で追跡できる。
 *
 * 使い方:
 *   node scripts/check-contract-schema.js             # 検査。差分があれば終了コード 1。
 *   node scripts/check-contract-schema.js --update    # baseline を現状で更新（承認済みの破壊的変更を消費）。
 *   node scripts/check-contract-schema.js --print     # 現在のスナップショットを標準出力へ（デバッグ用）。
 *   node scripts/check-contract-schema.js --self-test # 検査ロジック自体の自己試験（正例・負例）。
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const { warn } = require('./lib/ci-annotate');
const { excludedUnits, makeIsExcludedPath } = require('./lib/excluded-units.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const SRC_DIR = 'src';
const BASELINE_FILE = path.join(__dirname, 'contract-schema-baseline.json');
const ALLOWLIST_FILE = path.join(__dirname, 'contract-breaking-allowlist.json');
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', 'dist', 'coverage']);

/**
 * 検査対象外のユニット。src/ai-stock-trading は独自の計画リポジトリと ADR を持つ**別プロジェクト**
 * （submodule）であり、本リポジトリの規約を適用するのは誤りで、submodule のため是正もできない。
 * 値は .gitmodules から導出する（ハードコードしない。IADR-0120 / #473）。
 */
const EXCLUDED_UNITS = excludedUnits({ root: REPO_ROOT });

/** リポジトリ相対パスが検査対象外ユニット配下か。 */
const isExcludedPath = makeIsExcludedPath(EXCLUDED_UNITS);

// --- 文字列ユーティリティ（純粋ロジック） ---------------------------------------

/** posix 区切りへ正規化する。 */
function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

/** 型表記の正規化。空白を全除去する（`Dictionary<string, string>` と `Dictionary<string,string>` を同一視）。 */
function normalizeType(t) {
  return String(t).replace(/\s+/g, '');
}

/** 属性表記の正規化。空白の連続を 1 個へ畳む（文字列リテラル内の空白を潰さないため全除去はしない）。 */
function normalizeAttribute(a) {
  return String(a).replace(/\s+/g, ' ').trim();
}

/**
 * C# ソースからコメントを除去する（文字列・逐語文字列・文字リテラルは保護する）。
 *
 * 素朴な正規表現で `//` を落とすと、文字列リテラル中の `storage://` のような並びで本文が壊れる。
 * const 値（`"queued"` 等）は契約そのものなので、文字列は必ず保護したうえでコメントだけを消す。
 * 除去したコメントは同じ長さの空白へ置き換えない（位置合わせは不要。本検査は行番号を報告しない）。
 */
function stripComments(src) {
  const s = String(src);
  let out = '';
  let i = 0;
  while (i < s.length) {
    const c = s[i];
    const n = s[i + 1];
    if (c === '/' && n === '/') {
      while (i < s.length && s[i] !== '\n') i++;
      continue;
    }
    if (c === '/' && n === '*') {
      i += 2;
      while (i < s.length && !(s[i] === '*' && s[i + 1] === '/')) i++;
      i += 2;
      continue;
    }
    if (c === '@' && n === '"') {
      // 逐語文字列。"" はエスケープされた " である。
      out += s[i] + s[i + 1];
      i += 2;
      while (i < s.length) {
        if (s[i] === '"' && s[i + 1] === '"') { out += '""'; i += 2; continue; }
        if (s[i] === '"') { out += '"'; i++; break; }
        out += s[i]; i++;
      }
      continue;
    }
    if (c === '"' || c === "'") {
      const quote = c;
      out += c; i++;
      while (i < s.length) {
        if (s[i] === '\\') { out += s[i] + (s[i + 1] || ''); i += 2; continue; }
        out += s[i];
        if (s[i] === quote) { i++; break; }
        i++;
      }
      continue;
    }
    out += c;
    i++;
  }
  return out;
}

/**
 * open で始まる括弧の対応位置（閉じ括弧の index）を返す。見つからなければ -1。
 * 文字列リテラルの中の括弧は数えない。
 */
function matchBracket(s, start) {
  const open = s[start];
  const close = { '(': ')', '{': '}', '[': ']', '<': '>' }[open];
  if (!close) return -1;
  let depth = 0;
  for (let i = start; i < s.length; i++) {
    const c = s[i];
    if (c === '"' || c === "'") {
      const quote = c;
      i++;
      while (i < s.length) {
        if (s[i] === '\\') { i += 2; continue; }
        if (s[i] === quote) break;
        i++;
      }
      continue;
    }
    if (c === open) depth++;
    else if (c === close) { depth--; if (depth === 0) return i; }
  }
  return -1;
}

/**
 * トップレベル（括弧の外）のセパレータで分割する。
 * `Dictionary<string, List<string>>? X` のようにジェネリクス内へカンマが入るため、
 * 素朴な split(',') では引数を壊す。
 */
function splitTopLevel(text, sep = ',') {
  const s = String(text);
  const out = [];
  let depth = 0;
  let cur = '';
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === '"' || c === "'") {
      const quote = c;
      cur += c; i++;
      while (i < s.length) {
        cur += s[i];
        if (s[i] === '\\') { cur += s[i + 1] || ''; i += 2; continue; }
        if (s[i] === quote) { i++; break; }
        i++;
      }
      i--;
      continue;
    }
    if ('<([{'.includes(c)) depth++;
    else if ('>)]}'.includes(c)) depth--;
    if (c === sep && depth === 0) { out.push(cur); cur = ''; continue; }
    cur += c;
  }
  if (cur.trim() !== '') out.push(cur);
  return out.map((x) => x.trim()).filter((x) => x !== '');
}

/**
 * pos の直前に連なる属性リスト（`[A][B]`）を古い順で返す。
 * 属性はシリアライズ表現を変える（`[JsonConverter(...)]` で enum が数値 → 文字列になる等）ため
 * 契約の一部である。捕捉しないと「型もプロパティも同じなのに JSON が変わる」経路が素通りする。
 */
function precedingAttributes(code, pos) {
  const attrs = [];
  let i = pos - 1;
  for (;;) {
    while (i >= 0 && /\s/.test(code[i])) i--;
    if (i < 0 || code[i] !== ']') break;
    // 対応する '[' を後ろ向きに探す。
    let depth = 0;
    let j = i;
    for (; j >= 0; j--) {
      if (code[j] === ']') depth++;
      else if (code[j] === '[') { depth--; if (depth === 0) break; }
    }
    if (j < 0) break;
    attrs.unshift(normalizeAttribute(code.slice(j + 1, i)));
    i = j - 1;
  }
  return attrs;
}

/** 先頭に連なる属性（`[property: X] Type Name`）を剥がし、{ attributes, rest } を返す。 */
function leadingAttributes(text) {
  let s = String(text).trim();
  const attrs = [];
  while (s.startsWith('[')) {
    const end = matchBracket(s, 0);
    if (end === -1) break;
    attrs.push(normalizeAttribute(s.slice(1, end)));
    s = s.slice(end + 1).trim();
  }
  return { attributes: attrs, rest: s };
}

// --- C# の契約抽出 ---------------------------------------------------------------

const TYPE_KEYWORDS = ['record struct', 'record', 'class', 'struct', 'interface', 'enum'];
const TYPE_MODIFIERS = 'public|internal|private|protected|sealed|abstract|static|partial|readonly|ref|file|new';

/**
 * 位置引数（record の primary constructor）を解析する。
 *   `string? Query = null` → { name: 'Query', type: 'string?', required: false }
 *   `Guid DocumentId`      → { name: 'DocumentId', type: 'Guid', required: true }
 * required は「既定値を持たないこと」で判定する。既定値の有無は**旧発行者が省略できるか**に直結し、
 * 後方互換の判定軸そのものである。
 */
function parsePositionalParams(paramText) {
  const out = [];
  const parts = splitTopLevel(paramText, ',');
  parts.forEach((raw, index) => {
    const { attributes, rest } = leadingAttributes(raw);
    const eq = (() => {
      // 既定値の `=` はトップレベルにしか現れない（`Dictionary<int, string>` の中には無い）。
      let depth = 0;
      for (let i = 0; i < rest.length; i++) {
        const c = rest[i];
        if ('<([{'.includes(c)) depth++;
        else if ('>)]}'.includes(c)) depth--;
        else if (c === '=' && depth === 0 && rest[i + 1] !== '=' && rest[i - 1] !== '=') return i;
      }
      return -1;
    })();
    const decl = (eq === -1 ? rest : rest.slice(0, eq)).trim();
    const m = /^(.*?)\s+([A-Za-z_]\w*)$/s.exec(decl);
    if (!m) return;
    const member = {
      source: 'positional',
      type: normalizeType(m[1]),
      required: eq === -1,
      position: index,
    };
    if (attributes.length) member.attributes = attributes;
    out.push({ name: m[2], member });
  });
  return out;
}

/**
 * 型の本文から public プロパティ・public const を抽出する。
 * メソッド（`IsValid` / `Normalize` 等）は**契約のスキーマではない**ため対象外にする
 * （削除すればコンパイルが落ちるので、スナップショットで守る意味が薄い）。
 */
function parseBodyMembers(body) {
  const out = [];
  const code = String(body);

  // public プロパティ: `public [modifiers] <Type> <Name> { get; ... }`
  const propRe = new RegExp(
    'public\\s+((?:(?:required|static|virtual|override|sealed|readonly|new|abstract)\\s+)*)'
    + '([\\w.<>,\\[\\]\\?\\s]*?[\\w>\\]\\?])\\s+([A-Za-z_]\\w*)\\s*\\{\\s*get\\s*;',
    'g'
  );
  let m;
  while ((m = propRe.exec(code)) !== null) {
    const modifiers = m[1] || '';
    const member = {
      source: 'property',
      type: normalizeType(m[2]),
      // C# のプロパティは `required` 修飾子が無い限り省略可能である（JSON 逆シリアライズで既定値が入る）。
      required: /\brequired\b/.test(modifiers),
    };
    const attributes = precedingAttributes(code, m.index);
    if (attributes.length) member.attributes = attributes;
    out.push({ name: m[3], member });
  }

  // public const: `public const <Type> <Name> = <value>;`
  // const の**値**は配線そのものである（"queued" / "up" 等がイベント・API のリテラルになる）。
  const constRe = /public\s+const\s+([\w.<>,\[\]\?]+)\s+([A-Za-z_]\w*)\s*=\s*([^;]+);/g;
  while ((m = constRe.exec(code)) !== null) {
    out.push({
      name: m[2],
      member: { source: 'const', type: normalizeType(m[1]), value: m[3].trim() },
    });
  }
  return out;
}

/**
 * enum 本文からメンバーと**値**を抽出する。
 * 明示値が無いメンバーは C# の規則（直前 + 1、先頭は 0）で値が決まる。数値としてシリアライズされる
 * enum では**並べ替えが値の変更**になるため、序数まで固定しないと破壊的変更を見逃す。
 */
function parseEnumMembers(body) {
  const out = [];
  let next = 0;
  for (const raw of splitTopLevel(body, ',')) {
    const { rest } = leadingAttributes(raw);
    const m = /^([A-Za-z_]\w*)\s*(?:=\s*(.+))?$/s.exec(rest.trim());
    if (!m) continue;
    let value;
    if (m[2] === undefined) {
      value = next;
    } else {
      const lit = m[2].trim();
      const n = /^[-+]?\d+$/.test(lit) ? Number(lit) : null;
      value = n === null ? lit : n;
    }
    if (typeof value === 'number') next = value + 1;
    out.push({ name: m[1], member: { source: 'enum', value } });
  }
  return out;
}

/** 名前空間（file-scoped `namespace X;` / ブロック `namespace X {`）を返す。無ければ ''。 */
function parseNamespace(code) {
  const m = /(?:^|\n)\s*namespace\s+([\w.]+)\s*[;{]/.exec(code);
  return m ? m[1] : '';
}

/**
 * 1 ファイルから public 型を抽出する。戻り値は { '<FQN>': { kind, members, attributes? } }。
 *
 * 入れ子型は**親の本文に含まれる**ため、親のメンバーとして誤って拾われ得る。現状 0 件であり
 * （実測は作業仕様書に記載）、起こり得ないケースへの防御的実装はしない（CLAUDE.md 禁止事項）。
 */
function extractTypes(source) {
  const code = stripComments(source);
  const ns = parseNamespace(code);
  const types = {};
  const declRe = new RegExp(
    `(?:^|[\\n;{}])\\s*((?:(?:${TYPE_MODIFIERS})\\s+)*)`
    + `(${TYPE_KEYWORDS.join('|')})\\s+([A-Za-z_]\\w*)`,
    'g'
  );
  let m;
  while ((m = declRe.exec(code)) !== null) {
    const modifiers = m[1] || '';
    if (!/\bpublic\b/.test(modifiers)) continue; // 契約は public 型のみ
    const kind = m[2] === 'record struct' ? 'recordStruct' : m[2];
    let name = m[3];
    // 宣言の先頭位置（属性はこの手前に連なる）。
    const declStart = m.index + m[0].indexOf(modifiers.length ? modifiers.trimStart().charAt(0) : m[2]);
    let i = declRe.lastIndex;

    const skipWs = () => { while (i < code.length && /\s/.test(code[i])) i++; };
    skipWs();
    if (code[i] === '<') { // ジェネリック型パラメータ
      const end = matchBracket(code, i);
      if (end === -1) continue;
      name += normalizeType(code.slice(i, end + 1));
      i = end + 1;
      skipWs();
    }
    let positional = [];
    if (code[i] === '(') { // record の位置引数
      const end = matchBracket(code, i);
      if (end === -1) continue;
      positional = parsePositionalParams(code.slice(i + 1, end));
      i = end + 1;
      skipWs();
    }
    if (code[i] === ':') { // 基底リスト。`{` か `;` まで読み飛ばす。
      while (i < code.length && code[i] !== '{' && code[i] !== ';') i++;
      skipWs();
    }
    let body = '';
    if (code[i] === '{') {
      const end = matchBracket(code, i);
      if (end === -1) continue;
      body = code.slice(i + 1, end);
      declRe.lastIndex = end + 1;
    }

    const members = {};
    for (const { name: n, member } of positional) members[n] = member;
    const bodyMembers = kind === 'enum' ? parseEnumMembers(body) : parseBodyMembers(body);
    for (const { name: n, member } of bodyMembers) members[n] = member;

    const entry = { kind, members: sortKeys(members) };
    const attributes = precedingAttributes(code, declStart);
    if (attributes.length) entry.attributes = attributes;
    types[ns ? `${ns}.${name}` : name] = entry;
  }
  return types;
}

/** オブジェクトのキーを辞書順へ並べ替える（スナップショットの差分を安定させる）。 */
function sortKeys(obj) {
  const out = {};
  for (const k of Object.keys(obj).sort()) out[k] = obj[k];
  return out;
}

// --- 走査 -----------------------------------------------------------------------

/** dir 配下を再帰的に走査し、条件に合うファイルの相対パスを返す。 */
function walk(dir, predicate, acc = [], root = REPO_ROOT) {
  const abs = path.join(root, dir);
  let entries;
  try {
    entries = fs.readdirSync(abs, { withFileTypes: true });
  } catch {
    return acc;
  }
  for (const e of entries) {
    if (SKIP_DIRS.has(e.name)) continue;
    const rel = toPosix(path.join(dir, e.name));
    if (e.isDirectory()) walk(rel, predicate, acc, root);
    else if (predicate(rel)) acc.push(rel);
  }
  return acc;
}

/**
 * 契約プロジェクト（`src/<unit>/backend/Shared/*.Contracts`）を発見する。
 *
 * パスをハードコードせず規約から導出するのは、可変機能ユニットが増えたときに**検査から漏れる**のを
 * 防ぐためである（ユニット第一構成: IADR-0056）。`.csproj` の実在を条件にし、名前だけ合う
 * ディレクトリを拾わない。
 */
function findContractProjects(root = REPO_ROOT) {
  const found = [];
  let units;
  try {
    units = fs.readdirSync(path.join(root, SRC_DIR), { withFileTypes: true });
  } catch {
    return found;
  }
  for (const unit of units) {
    if (!unit.isDirectory()) continue;
    const sharedRel = toPosix(path.join(SRC_DIR, unit.name, 'backend', 'Shared'));
    if (isExcludedPath(sharedRel)) continue;
    let projects;
    try {
      projects = fs.readdirSync(path.join(root, sharedRel), { withFileTypes: true });
    } catch {
      continue;
    }
    for (const p of projects) {
      if (!p.isDirectory() || !/\.Contracts$/.test(p.name)) continue;
      const rel = toPosix(path.join(sharedRel, p.name));
      const hasCsproj = walk(rel, (f) => /\.csproj$/i.test(f), [], root).length > 0;
      if (hasCsproj) found.push(rel);
    }
  }
  return found.sort();
}

/** 契約プロジェクト群の正規化スナップショットを作る。 */
function buildSnapshot(root = REPO_ROOT) {
  const projects = {};
  let files = 0;
  for (const rel of findContractProjects(root)) {
    const types = {};
    for (const f of walk(rel, (p) => /\.cs$/i.test(p), [], root).sort()) {
      files++;
      Object.assign(types, extractTypes(fs.readFileSync(path.join(root, f), 'utf8')));
    }
    projects[rel] = sortKeys(types);
  }
  return { projects: sortKeys(projects), files };
}

// --- 差分判定 -------------------------------------------------------------------

/** 破壊的と分類する変更種別。key の接頭辞に使う。 */
const BREAKING_KINDS = new Set([
  'projectRemoved', 'typeRemoved', 'typeKindChanged', 'typeAttributesChanged',
  'memberRemoved', 'memberTypeChanged', 'memberRequired', 'memberReordered',
  'memberSourceChanged', 'memberAttributesChanged', 'memberAddedRequired',
  'constValueChanged', 'enumValueChanged',
]);

function change(kind, target, detail) {
  return {
    kind,
    key: `${kind}:${target}`,
    severity: BREAKING_KINDS.has(kind) ? 'breaking' : 'additive',
    detail,
  };
}

/** 2 つのメンバーを比べ、変更点を返す。 */
function diffMember(fq, name, a, b) {
  const out = [];
  const at = `${fq}.${name}`;
  if (a.source !== b.source) {
    out.push(change('memberSourceChanged', at, `${a.source} → ${b.source}`));
    return out; // 以降の比較は意味を成さない
  }
  if (a.type !== b.type && (a.type !== undefined || b.type !== undefined)) {
    out.push(change('memberTypeChanged', at, `${a.type} → ${b.type}`));
  }
  if (a.required === false && b.required === true) {
    out.push(change('memberRequired', at, '省略可能 → 必須'));
  } else if (a.required === true && b.required === false) {
    out.push(change('memberOptional', at, '必須 → 省略可能'));
  }
  if (a.position !== undefined && b.position !== undefined && a.position !== b.position) {
    out.push(change('memberReordered', at, `位置 ${a.position} → ${b.position}`));
  }
  if (a.source === 'const' && a.value !== b.value) {
    out.push(change('constValueChanged', at, `${a.value} → ${b.value}`));
  }
  if (a.source === 'enum' && a.value !== b.value) {
    out.push(change('enumValueChanged', at, `${a.value} → ${b.value}`));
  }
  const aa = JSON.stringify(a.attributes || []);
  const ba = JSON.stringify(b.attributes || []);
  if (aa !== ba) out.push(change('memberAttributesChanged', at, `${aa} → ${ba}`));
  return out;
}

/** baseline スナップショットと現状スナップショットの差分を返す。 */
function diffSnapshots(baseProjects, curProjects) {
  const out = [];
  const base = baseProjects || {};
  const cur = curProjects || {};
  for (const proj of Object.keys(base).sort()) {
    if (!(proj in cur)) { out.push(change('projectRemoved', proj, '契約プロジェクトが消えた')); continue; }
    const bt = base[proj];
    const ct = cur[proj];
    for (const fq of Object.keys(bt).sort()) {
      if (!(fq in ct)) { out.push(change('typeRemoved', fq, '型が消えた')); continue; }
      const a = bt[fq];
      const b = ct[fq];
      if (a.kind !== b.kind) out.push(change('typeKindChanged', fq, `${a.kind} → ${b.kind}`));
      const aa = JSON.stringify(a.attributes || []);
      const ba = JSON.stringify(b.attributes || []);
      if (aa !== ba) out.push(change('typeAttributesChanged', fq, `${aa} → ${ba}`));
      const am = a.members || {};
      const bm = b.members || {};
      for (const name of Object.keys(am).sort()) {
        if (!(name in bm)) { out.push(change('memberRemoved', `${fq}.${name}`, 'メンバーが消えた')); continue; }
        out.push(...diffMember(fq, name, am[name], bm[name]));
      }
      for (const name of Object.keys(bm).sort()) {
        if (name in am) continue;
        const m = bm[name];
        // 既定値の無い追加は旧発行者のメッセージが必須項目を欠く（＝後方互換ではない）。
        if (m.required === true) out.push(change('memberAddedRequired', `${fq}.${name}`, '既定値の無いメンバーが増えた'));
        else out.push(change('memberAdded', `${fq}.${name}`, 'メンバーが増えた'));
      }
    }
    for (const fq of Object.keys(ct).sort()) {
      if (!(fq in bt)) out.push(change('typeAdded', fq, '型が増えた'));
    }
  }
  for (const proj of Object.keys(cur).sort()) {
    if (!(proj in base)) out.push(change('projectAdded', proj, '契約プロジェクトが増えた'));
  }
  return out;
}

// --- baseline / allowlist -------------------------------------------------------

function readJson(file, fallback) {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return fallback;
  }
}

const APPROVAL_FIELDS = ['key', 'reason', 'approvedBy', 'issue', 'date'];

/**
 * 承認エントリの必須項目を検査する。理由・承認者・issue・日付が揃っていない承認は**記録にならない**。
 * 「誰が・なぜ・どの issue で」承認したかが残らない逃げ道は、ただの無効化スイッチである。
 */
function validateApprovals(approvals) {
  const errors = [];
  if (!Array.isArray(approvals)) return ['approvals が配列ではない'];
  approvals.forEach((a, i) => {
    if (!a || typeof a !== 'object') { errors.push(`approvals[${i}] がオブジェクトではない`); return; }
    for (const f of APPROVAL_FIELDS) {
      if (typeof a[f] !== 'string' || a[f].trim() === '') errors.push(`approvals[${i}] の ${f} が空`);
    }
    if (typeof a.date === 'string' && a.date.trim() !== '' && !/^\d{4}-\d{2}-\d{2}$/.test(a.date)) {
      errors.push(`approvals[${i}] の date は YYYY-MM-DD 形式にする（実際: ${a.date}）`);
    }
    if (typeof a.issue === 'string' && a.issue.trim() !== ''
      && !/^(?:[\w.-]+\/[\w.-]+)?#\d+$/.test(a.issue.trim())) {
      errors.push(`approvals[${i}] の issue は #123 か owner/repo#123 の形式にする（実際: ${a.issue}）`);
    }
  });
  return errors;
}

/** 検査の中核。差分・承認の突合結果を返す（純粋: 入力は snapshot / baseline / allowlist）。 */
function evaluate({ snapshot, baseline, allowlist }) {
  const changes = diffSnapshots((baseline || {}).projects, snapshot.projects);
  const approvals = (allowlist || {}).approvals || [];
  const approvalErrors = validateApprovals(approvals);
  const approvedKeys = new Set(approvals.map((a) => (a && a.key) || ''));
  const breaking = changes.filter((c) => c.severity === 'breaking');
  const additive = changes.filter((c) => c.severity === 'additive');
  const unapproved = breaking.filter((c) => !approvedKeys.has(c.key));
  const approved = breaking.filter((c) => approvedKeys.has(c.key));
  const breakingKeys = new Set(breaking.map((c) => c.key));
  const stale = approvals.filter((a) => a && !breakingKeys.has(a.key));
  return { changes, breaking, additive, approved, unapproved, stale, approvalErrors };
}

/** baseline ファイルの内容を組み立てる（承認済みの破壊的変更を記録へ移す）。 */
function nextBaseline({ snapshot, baseline, approved }) {
  const prev = baseline || {};
  return {
    $comment: [
      'Shared.Contracts のイベント/API スキーマの正規化スナップショット（Issue #465 / IADR-0122）。',
      'C# ソースの構文解析で生成する（scripts/check-contract-schema.js）。手で編集しない。',
      '更新: node scripts/check-contract-schema.js --update',
      '破壊的変更は scripts/contract-breaking-allowlist.json へ承認エントリを書いてから --update する。',
      '承認の記録は $acceptedBreakingChanges へ追記され、以後も残る（削除しない）。',
    ],
    $acceptedBreakingChanges: [...(prev.$acceptedBreakingChanges || []), ...approved],
    projects: snapshot.projects,
  };
}

/** allowlist ファイルの内容を組み立てる（承認は baseline へ移したので空へ戻す）。 */
function emptyAllowlist() {
  return {
    $comment: [
      '契約の破壊的変更に対する承認エントリ（Issue #465 / IADR-0122）。',
      '逃げ道の無いゲートは無視される（IADR-0115 の知見）ため、明示的な承認で通す道を用意する。',
      '手順: (1) 破壊的変更の key を check-contract-schema.js の失敗出力からコピーし、',
      '      (2) 下の approvals へ { key, reason, approvedBy, issue, date } を書き、',
      '      (3) node scripts/check-contract-schema.js --update を実行する。',
      '--update は承認エントリを contract-schema-baseline.json の $acceptedBreakingChanges へ移し、',
      'ここを空へ戻す（承認の記録は baseline 側に残り、git 履歴で追跡できる）。',
      '定常状態では空である。変更に対応しない承認が残っていれば fail する。',
    ],
    approvals: [],
  };
}

function writeJson(file, value) {
  fs.writeFileSync(file, JSON.stringify(value, null, 2) + '\n');
}

// --- 出力 -----------------------------------------------------------------------

const KIND_LABELS = {
  projectRemoved: '契約プロジェクトの削除',
  projectAdded: '契約プロジェクトの追加',
  typeRemoved: '型の削除',
  typeAdded: '型の追加',
  typeKindChanged: '型種別の変更',
  typeAttributesChanged: '型属性の変更',
  memberRemoved: 'メンバーの削除',
  memberAdded: 'メンバーの追加',
  memberAddedRequired: '必須メンバーの追加',
  memberTypeChanged: 'メンバー型の変更',
  memberRequired: 'メンバーの必須化',
  memberOptional: 'メンバーの任意化',
  memberReordered: '位置引数の並べ替え',
  memberSourceChanged: 'メンバー定義形の変更',
  memberAttributesChanged: 'メンバー属性の変更',
  constValueChanged: 'const 値の変更',
  enumValueChanged: 'enum 値の変更',
};

function describe(c) {
  return `  [${c.severity === 'breaking' ? '破壊的' : '非破壊'}] ${KIND_LABELS[c.kind] || c.kind}: ${c.key.slice(c.kind.length + 1)}（${c.detail}）\n    key: ${c.key}`;
}

/** 承認済みの破壊的変更を実行サマリへ書き出す（黙って通さない）。 */
function reportApproved(approved, approvals) {
  if (!approved.length) return;
  const byKey = new Map(approvals.map((a) => [a.key, a]));
  warn(`承認済みの破壊的な契約変更が ${approved.length} 件あります: `
    + approved.map((c) => c.key).join(' / '));
  const summary = process.env.GITHUB_STEP_SUMMARY;
  if (!summary) return;
  const lines = ['### 契約: 承認済みの破壊的変更', '',
    '| 変更 | 対象 | 理由 | 承認者 | issue | 日付 |', '| --- | --- | --- | --- | --- | --- |'];
  for (const c of approved) {
    const a = byKey.get(c.key) || {};
    lines.push(`| ${KIND_LABELS[c.kind] || c.kind} | \`${c.key.slice(c.kind.length + 1)}\` | ${a.reason || ''} | ${a.approvedBy || ''} | ${a.issue || ''} | ${a.date || ''} |`);
  }
  try { fs.appendFileSync(summary, lines.join('\n') + '\n'); } catch { /* サマリを書けなくても検査は続ける */ }
}

const HOWTO = `
破壊的変更を意図して行う手順（scripts/README.md「契約の破壊的変更」・IADR-0122 決定 3）:
  1. 上の key を scripts/contract-breaking-allowlist.json の approvals へ写す。
     { "key": "...", "reason": "なぜ壊すか", "approvedBy": "承認者", "issue": "#123", "date": "YYYY-MM-DD" }
  2. node scripts/check-contract-schema.js --update を実行する。
  3. 更新された contract-schema-baseline.json（$acceptedBreakingChanges に承認が記録される）と
     空に戻った contract-breaking-allowlist.json を同じ PR にコミットする。
互換を保つ選択肢も検討すること: 新フィールドは既定値付きで足す／削除ではなく非推奨のまま残す／
新しいイベント型・API バージョンを追加して移行期間を設ける（計画 06_technical/10_composability-design §3）。`;

// --- 自己試験 -------------------------------------------------------------------

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });
  const T = (src) => extractTypes(src);
  const one = (src) => { const r = T(src); return r[Object.keys(r)[0]]; };

  // --- コメント除去（文字列を壊さない） ---
  t('stripComments: 行コメントを除去する（改行は残す）', stripComments('a // b\nc') === 'a \nc');
  t('stripComments: ブロックコメントを除去する', stripComments('a/* b */c') === 'ac');
  t('stripComments: 文字列中の // をコメントと誤認しない',
    stripComments('var x = "storage://a"; // c').includes('storage://a'));
  t('stripComments: 逐語文字列を保護する', stripComments('var x = @"a//b";').includes('a//b'));

  // --- 分割・括弧 ---
  t('splitTopLevel: ジェネリクス内のカンマで割らない',
    JSON.stringify(splitTopLevel('Dictionary<string, List<string>>? A = null, int B'))
      === JSON.stringify(['Dictionary<string, List<string>>? A = null', 'int B']));
  t('matchBracket: 対応する括弧を返す', matchBracket('(a(b)c)', 0) === 6);

  // --- 型抽出 ---
  t('位置引数 record を抽出する', (() => {
    const r = T('namespace N;\npublic record E(Guid Id, string? Note = null);');
    const e = r['N.E'];
    return e && e.kind === 'record' && e.members.Id.required === true
      && e.members.Id.position === 0 && e.members.Note.required === false
      && e.members.Note.type === 'string?' && e.members.Note.position === 1;
  })());
  t('ジェネリクスを含む位置引数の型を空白除去して正規化する',
    one('namespace N;\npublic record E(Dictionary<string, List<string>>? A = null);')
      .members.A.type === 'Dictionary<string,List<string>>?');
  t('配列型の位置引数を読む',
    one('namespace N;\npublic record E(float[] Vector);').members.Vector.type === 'float[]');
  t('sealed record も public 型として拾う',
    T('namespace N;\npublic sealed record E(string A);')['N.E'] !== undefined);
  t('internal 型は契約に含めない',
    Object.keys(T('namespace N;\ninternal record E(string A);')).length === 0);
  t('クラスの public プロパティを拾う（required 修飾子が無ければ省略可能）', (() => {
    const e = one('namespace N;\npublic class D { public Guid Id { get; init; }\n public string T { get; init; } = string.Empty; }');
    return e.kind === 'class' && e.members.Id.source === 'property'
      && e.members.Id.required === false && e.members.T.type === 'string';
  })());
  t('required 修飾子付きプロパティは必須として扱う',
    one('namespace N;\npublic class D { public required string T { get; init; } }').members.T.required === true);
  t('public メソッドはスキーマに含めない',
    one('namespace N;\npublic static class C { public const string A = "a";\n public static bool IsValid(string? x) => true; }')
      .members.IsValid === undefined);
  t('const の値まで固定する（配線に載るリテラルのため）',
    one('namespace N;\npublic static class C { public const string A = "queued"; }').members.A.value === '"queued"');
  t('enum の暗黙序数を計算する', (() => {
    const e = one('namespace N;\npublic enum E { A, B, C }');
    return e.kind === 'enum' && e.members.A.value === 0 && e.members.B.value === 1 && e.members.C.value === 2;
  })());
  t('enum の明示値以降は明示値 + 1 で続く', (() => {
    const e = one('namespace N;\npublic enum E { A = 5, B }');
    return e.members.A.value === 5 && e.members.B.value === 6;
  })());
  t('型の属性を捕捉する（シリアライズ表現を変えるため）',
    JSON.stringify(one('namespace N;\n[JsonConverter(typeof(JsonStringEnumConverter<E>))]\npublic enum E { A }').attributes)
      === JSON.stringify(['JsonConverter(typeof(JsonStringEnumConverter<E>))']));
  t('プロパティの属性を捕捉する',
    JSON.stringify(one('namespace N;\npublic class D { [JsonPropertyName("t")]\n public string T { get; init; } = ""; }')
      .members.T.attributes) === JSON.stringify(['JsonPropertyName("t")']));
  t('位置引数の属性を捕捉する',
    JSON.stringify(one('namespace N;\npublic record E([property: JsonPropertyName("a")] string A);')
      .members.A.attributes) === JSON.stringify(['property: JsonPropertyName("a")']));
  t('位置引数と本文プロパティを併せ持つ record を読む', (() => {
    const e = one('namespace N;\npublic record E(string A)\n{\n  public Guid Id { get; init; } = Guid.NewGuid();\n}');
    return e.members.A.source === 'positional' && e.members.Id.source === 'property';
  })());
  t('コメント中の型宣言を拾わない',
    Object.keys(T('namespace N;\n// public record Ghost(string A);\npublic record E(string A);')).length === 1);

  // --- 差分判定 ---
  const snap = (types) => ({ 'src/x/backend/Shared/X.Contracts': types });
  const d = (a, b) => diffSnapshots(snap(a), snap(b));
  const rec = (members, extra = {}) => ({ kind: 'record', members, ...extra });
  const req = (type, position) => ({ source: 'positional', type, required: true, position });
  const opt = (type, position) => ({ source: 'positional', type, required: false, position });

  t('型の削除は破壊的',
    d({ 'N.A': rec({}) }, {})[0].severity === 'breaking');
  t('型の追加は非破壊',
    d({}, { 'N.A': rec({}) })[0].severity === 'additive');
  t('メンバーの削除は破壊的',
    d({ 'N.A': rec({ X: req('int', 0) }) }, { 'N.A': rec({}) })[0].kind === 'memberRemoved');
  t('メンバー型の変更は破壊的',
    d({ 'N.A': rec({ X: req('int', 0) }) }, { 'N.A': rec({ X: req('long', 0) }) })[0].kind === 'memberTypeChanged');
  t('省略可能 → 必須は破壊的',
    d({ 'N.A': rec({ X: opt('int', 0) }) }, { 'N.A': rec({ X: req('int', 0) }) })[0].kind === 'memberRequired');
  t('必須 → 省略可能は非破壊',
    d({ 'N.A': rec({ X: req('int', 0) }) }, { 'N.A': rec({ X: opt('int', 0) }) })[0].severity === 'additive');
  t('位置引数の並べ替えは破壊的',
    d({ 'N.A': rec({ X: req('int', 0), Y: req('int', 1) }) },
      { 'N.A': rec({ X: req('int', 1), Y: req('int', 0) }) })
      .filter((c) => c.kind === 'memberReordered').length === 2);
  t('既定値付きメンバーの追加は非破壊',
    d({ 'N.A': rec({}) }, { 'N.A': rec({ X: opt('int', 0) }) })[0].kind === 'memberAdded');
  t('既定値の無いメンバーの追加は破壊的（旧発行者が必須項目を欠く）',
    d({ 'N.A': rec({}) }, { 'N.A': rec({ X: req('int', 0) }) })[0].kind === 'memberAddedRequired');
  t('enum メンバーの削除は破壊的', d(
    { 'N.E': { kind: 'enum', members: { A: { source: 'enum', value: 0 } } } },
    { 'N.E': { kind: 'enum', members: {} } })[0].kind === 'memberRemoved');
  t('enum 値の変更（並べ替え）は破壊的', d(
    { 'N.E': { kind: 'enum', members: { A: { source: 'enum', value: 0 } } } },
    { 'N.E': { kind: 'enum', members: { A: { source: 'enum', value: 1 } } } })[0].kind === 'enumValueChanged');
  t('const 値の変更は破壊的', d(
    { 'N.C': { kind: 'class', members: { A: { source: 'const', type: 'string', value: '"a"' } } } },
    { 'N.C': { kind: 'class', members: { A: { source: 'const', type: 'string', value: '"b"' } } } })[0].kind === 'constValueChanged');
  t('型属性の変更は破壊的（JSON 表現が変わる）', d(
    { 'N.E': { kind: 'enum', members: {}, attributes: ['JsonConverter(typeof(X))'] } },
    { 'N.E': { kind: 'enum', members: {} } })[0].kind === 'typeAttributesChanged');
  t('メンバー属性の変更は破壊的（JSON 名が変わる）', d(
    { 'N.A': rec({ X: { source: 'property', type: 'string', required: false, attributes: ['JsonPropertyName("x")'] } }) },
    { 'N.A': rec({ X: { source: 'property', type: 'string', required: false } }) })[0].kind === 'memberAttributesChanged');
  t('型種別の変更は破壊的',
    d({ 'N.A': rec({}) }, { 'N.A': { kind: 'class', members: {} } })[0].kind === 'typeKindChanged');
  t('位置引数 → プロパティの移動は破壊的（コンストラクタが壊れる）', d(
    { 'N.A': rec({ X: req('int', 0) }) },
    { 'N.A': rec({ X: { source: 'property', type: 'int', required: false } }) })[0].kind === 'memberSourceChanged');
  t('契約プロジェクトの消失は破壊的',
    diffSnapshots(snap({}), {})[0].kind === 'projectRemoved');
  t('差分が無ければ変更 0 件', d({ 'N.A': rec({ X: req('int', 0) }) }, { 'N.A': rec({ X: req('int', 0) }) }).length === 0);

  // --- 承認エントリの検証 ---
  const okApproval = { key: 'typeRemoved:N.A', reason: 'r', approvedBy: 'a', issue: '#1', date: '2026-08-04' };
  t('承認: 必須項目が揃っていれば通る', validateApprovals([okApproval]).length === 0);
  for (const f of APPROVAL_FIELDS) {
    const bad = { ...okApproval, [f]: '' };
    t(`承認: ${f} が空なら不備として報告する`, validateApprovals([bad]).some((e) => e.includes(f)));
  }
  t('承認: date は YYYY-MM-DD 形式を要求する',
    validateApprovals([{ ...okApproval, date: '2026/08/04' }]).some((e) => e.includes('date')));
  t('承認: issue は #123 か owner/repo#123 を要求する',
    validateApprovals([{ ...okApproval, issue: '123' }]).some((e) => e.includes('issue')));
  t('承認: owner/repo#123 形式も受ける',
    validateApprovals([{ ...okApproval, issue: 'endazon/microservices-platform#1' }]).length === 0);

  // --- evaluate（差分 × 承認の突合） ---
  {
    const baseline = { projects: snap({ 'N.A': rec({ X: req('int', 0) }) }) };
    const removed = { projects: snap({ 'N.A': rec({}) }) };
    const e0 = evaluate({ snapshot: removed, baseline, allowlist: { approvals: [] } });
    t('evaluate: 未承認の破壊的変更を unapproved へ入れる',
      e0.unapproved.length === 1 && e0.approved.length === 0);
    const key = e0.unapproved[0].key;
    const e1 = evaluate({
      snapshot: removed, baseline,
      allowlist: { approvals: [{ ...okApproval, key }] },
    });
    t('evaluate: 承認済みなら approved へ移り unapproved は空',
      e1.approved.length === 1 && e1.unapproved.length === 0);
    const e2 = evaluate({
      snapshot: { projects: baseline.projects }, baseline,
      allowlist: { approvals: [{ ...okApproval, key }] },
    });
    t('evaluate: 対応する変更が無い承認は stale として検出する',
      e2.stale.length === 1 && e2.changes.length === 0);
    const e3 = evaluate({ snapshot: { projects: baseline.projects }, baseline, allowlist: { approvals: [] } });
    t('evaluate: 差分も承認も無ければすべて空',
      e3.changes.length === 0 && e3.stale.length === 0 && e3.approvalErrors.length === 0);
    const e4 = evaluate({
      snapshot: removed, baseline,
      allowlist: { approvals: [{ ...okApproval, key, reason: '' }] },
    });
    t('evaluate: 不備のある承認は approvalErrors に出る', e4.approvalErrors.length > 0);
  }

  // --- nextBaseline（承認の記録が baseline へ残る） ---
  {
    const approvedEntry = { ...okApproval, key: 'typeRemoved:N.A' };
    const nb = nextBaseline({
      snapshot: { projects: { p: {} } },
      baseline: { $acceptedBreakingChanges: [{ key: 'old' }], projects: {} },
      approved: [approvedEntry],
    });
    t('nextBaseline: 過去の承認記録を保持したまま新しい承認を追記する',
      nb.$acceptedBreakingChanges.length === 2 && nb.$acceptedBreakingChanges[1].key === 'typeRemoved:N.A');
    t('nextBaseline: projects は現状のスナップショットで置き換える',
      JSON.stringify(nb.projects) === JSON.stringify({ p: {} }));
    t('emptyAllowlist: approvals は空になる', emptyAllowlist().approvals.length === 0);
  }

  // --- 実地確認: 一時ツリーを buildSnapshot で実走査する ---
  // 関数単位の試験だけでは「走査経路に乗っているか」を確かめられない（判定が正しくても
  // buildSnapshot が当該ファイルを開かなければ検出されない）。よって走査ごと通す。
  {
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'contract-schema-selftest-'));
    const write = (rel, body) => {
      const abs = path.join(tmp, rel);
      fs.mkdirSync(path.dirname(abs), { recursive: true });
      fs.writeFileSync(abs, body);
    };
    write('src/platform/backend/Shared/Platform.Shared.Contracts/Platform.Shared.Contracts.csproj', '<Project />');
    write('src/platform/backend/Shared/Platform.Shared.Contracts/Dtos/A.cs',
      'namespace P.Dtos;\npublic record ScopeDto(string UserId, bool Granted = false);');
    write('src/knowledge/backend/Shared/Knowledge.Contracts/Knowledge.Contracts.csproj', '<Project />');
    write('src/knowledge/backend/Shared/Knowledge.Contracts/Events/E.cs',
      'namespace K.Events;\npublic record Done(Guid Id, DateTimeOffset At);');
    // 契約ではないプロジェクト（名前が .Contracts で終わらない）は拾わない。
    write('src/knowledge/backend/Shared/Knowledge.Support/Knowledge.Support.csproj', '<Project />');
    write('src/knowledge/backend/Shared/Knowledge.Support/S.cs', 'namespace K;\npublic record Support(int A);');
    // 別プロジェクト（submodule）は検査対象外。
    write('src/ai-stock-trading/backend/Shared/Ast.Contracts/Ast.Contracts.csproj', '<Project />');
    write('src/ai-stock-trading/backend/Shared/Ast.Contracts/A.cs', 'namespace A;\npublic record X(int A);');
    // ビルド生成物は走査しない。
    write('src/platform/backend/Shared/Platform.Shared.Contracts/obj/Generated.cs',
      'namespace P;\npublic record Generated(int A);');

    const snapshot = buildSnapshot(tmp);
    const projects = Object.keys(snapshot.projects);
    t('実地: 契約プロジェクトを 2 件発見する（.Contracts のみ・AST は除外）',
      projects.length === 2 && projects.includes('src/platform/backend/Shared/Platform.Shared.Contracts')
        && projects.includes('src/knowledge/backend/Shared/Knowledge.Contracts'), projects);
    t('実地: .Contracts で終わらないプロジェクトは走査しない',
      !projects.some((p) => p.endsWith('Knowledge.Support')));
    t('実地: obj/ 配下の生成物は走査しない',
      snapshot.projects['src/platform/backend/Shared/Platform.Shared.Contracts']['P.Generated'] === undefined);
    t('実地: 型を FQN で格納する',
      snapshot.projects['src/knowledge/backend/Shared/Knowledge.Contracts']['K.Events.Done'] !== undefined);

    // 破壊的変更を注入すると検出されること（負例の固定）。
    write('src/knowledge/backend/Shared/Knowledge.Contracts/Events/E.cs',
      'namespace K.Events;\npublic record Done(Guid Id);');
    const broken = buildSnapshot(tmp);
    const changes = diffSnapshots(snapshot.projects, broken.projects);
    t('実地: フィールド削除が破壊的変更として検出される',
      changes.length === 1 && changes[0].key === 'memberRemoved:K.Events.Done.At'
        && changes[0].severity === 'breaking', changes);
    fs.rmSync(tmp, { recursive: true, force: true });
  }

  // --- 実リポジトリ: baseline と一致し、承認は空（#465 の受け入れ観点「現状で通る」の固定） ---
  {
    const r = evaluate({
      snapshot: buildSnapshot(),
      baseline: readJson(BASELINE_FILE, null),
      allowlist: readJson(ALLOWLIST_FILE, null),
    });
    t('実リポジトリ: baseline との差分は 0 件', r.changes.length === 0, r.changes);
    t('実リポジトリ: 未消化の承認は 0 件', r.stale.length === 0, r.stale);
    t('実リポジトリ: 承認エントリの不備は 0 件', r.approvalErrors.length === 0, r.approvalErrors);
    const projects = Object.keys(buildSnapshot().projects);
    t('実リポジトリ: 契約プロジェクトを 1 件以上検出する（0 件検査への退行を止める）',
      projects.length > 0, projects);
  }

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[check-contract-schema] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-contract-schema] 自己試験 ${cases.length} 件 OK。`);
}

// --- 実行 -----------------------------------------------------------------------

function countTypes(snapshot) {
  return Object.values(snapshot.projects).reduce((n, t) => n + Object.keys(t).length, 0);
}

function runUpdate() {
  const snapshot = buildSnapshot();
  const baseline = readJson(BASELINE_FILE, null);
  const allowlist = readJson(ALLOWLIST_FILE, null);
  const r = evaluate({ snapshot, baseline, allowlist });

  if (r.approvalErrors.length) {
    console.error('[check-contract-schema] 承認エントリに不備があります:');
    for (const e of r.approvalErrors) console.error(`  ${e}`);
    process.exit(1);
  }
  if (r.unapproved.length) {
    console.error(`[check-contract-schema] 未承認の破壊的な契約変更が ${r.unapproved.length} 件あるため baseline を更新しません:`);
    for (const c of r.unapproved) console.error(describe(c));
    console.error(HOWTO);
    process.exit(1);
  }
  const approvals = (allowlist || {}).approvals || [];
  const consumed = approvals.filter((a) => r.approved.some((c) => c.key === a.key));
  writeJson(BASELINE_FILE, nextBaseline({ snapshot, baseline, approved: consumed }));
  writeJson(ALLOWLIST_FILE, emptyAllowlist());
  console.log(`[check-contract-schema] baseline を更新しました: ${Object.keys(snapshot.projects).length} プロジェクト / `
    + `${countTypes(snapshot)} 型（差分 ${r.changes.length} 件・承認を消費 ${consumed.length} 件）。`);
  console.log('  更新された 2 ファイルを同じ PR にコミットしてください。');
}

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) { selfTest(); return; }
  if (argv.includes('--print')) { console.log(JSON.stringify(buildSnapshot().projects, null, 2)); return; }
  if (argv.includes('--update')) { runUpdate(); return; }

  const snapshot = buildSnapshot();
  const baseline = readJson(BASELINE_FILE, null);
  const allowlist = readJson(ALLOWLIST_FILE, null);

  if (!baseline) {
    console.error(`[check-contract-schema] baseline (${path.relative(REPO_ROOT, BASELINE_FILE)}) を読めません。`
      + '\n  node scripts/check-contract-schema.js --update で生成してください。');
    process.exit(1);
  }
  if (Object.keys(snapshot.projects).length === 0) {
    // 走査対象 0 件で「差分なし」を返すと、検査は緑のまま何も見ていない状態になる。
    console.error('[check-contract-schema] 契約プロジェクトを 1 件も発見できませんでした'
      + '（src/<unit>/backend/Shared/*.Contracts）。走査経路の退行を疑ってください。');
    process.exit(1);
  }

  const r = evaluate({ snapshot, baseline, allowlist });
  reportApproved(r.approved, (allowlist || {}).approvals || []);

  let failed = false;
  if (r.approvalErrors.length) {
    failed = true;
    console.error(`[check-contract-schema] 承認エントリ（${path.relative(REPO_ROOT, ALLOWLIST_FILE)}）に不備が ${r.approvalErrors.length} 件あります:`);
    for (const e of r.approvalErrors) console.error(`  ${e}`);
    console.error('  理由・承認者・issue・日付の残らない承認は記録にならず、ただの無効化スイッチになります。');
  }
  if (r.stale.length) {
    failed = true;
    console.error(`[check-contract-schema] 対応する変更が無い承認が ${r.stale.length} 件残っています:`);
    for (const a of r.stale) console.error(`  ${a.key}（${a.reason}・${a.approvedBy}・${a.issue}）`);
    console.error('  承認だけが残ると次の破壊的変更を黙って通します。--update で消費するか削除してください。');
  }
  if (r.unapproved.length) {
    failed = true;
    console.error(`[check-contract-schema] 後方互換を壊す契約変更が ${r.unapproved.length} 件あります:`);
    for (const c of r.unapproved) console.error(describe(c));
    console.error(HOWTO);
  }
  if (r.additive.length || r.approved.length) {
    failed = true;
    console.error(`\n[check-contract-schema] baseline と現状の契約に差分が ${r.changes.length} 件あります`
      + `（破壊的 ${r.breaking.length}［うち承認済み ${r.approved.length}］/ 非破壊 ${r.additive.length}）。`);
    for (const c of r.approved) console.error(`${describe(c)}\n    → 承認済み（allowlist）`);
    for (const c of r.additive) console.error(describe(c));
    console.error('\n  node scripts/check-contract-schema.js --update で baseline を更新し、'
      + '\n  差分（= 契約の変更そのもの）をレビュー対象として同じ PR にコミットしてください。');
  }
  if (failed) process.exit(1);

  console.log(`[check-contract-schema] OK: ${Object.keys(snapshot.projects).length} プロジェクト / `
    + `${snapshot.files} ファイル / ${countTypes(snapshot)} 型が baseline と一致（未消化の承認 0 件）。`);
  process.exit(0);
}

if (require.main === module) main();

module.exports = {
  EXCLUDED_UNITS,
  isExcludedPath,
  toPosix,
  normalizeType,
  stripComments,
  matchBracket,
  splitTopLevel,
  precedingAttributes,
  leadingAttributes,
  parsePositionalParams,
  parseBodyMembers,
  parseEnumMembers,
  parseNamespace,
  extractTypes,
  findContractProjects,
  buildSnapshot,
  diffSnapshots,
  validateApprovals,
  evaluate,
  nextBaseline,
  emptyAllowlist,
  BASELINE_FILE,
  ALLOWLIST_FILE,
};
