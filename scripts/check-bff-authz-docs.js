#!/usr/bin/env node
'use strict';
/*
 * check-bff-authz-docs.js
 * NFR / issue #647: `docs/api/openapi.yaml` が宣言するロールと、BFF 実装の**実効ロール**を突き合わせる。
 *
 * **認可を狭めたのに契約の記述が追随しない事故が 2 回起きた**ので入れた
 * （`CLAUDE.md`「検査器・規約の追加は同型の事故が 2 回起きたら」）。#629 は 3 巡の AI レビューを
 * 経ても捕まえられず、無関係な #640 の走査で偶然見つかった。`BFF_bff-surface.md` 自身が
 * 「個々のエンドポイントの…ステータスは `openapi.yaml` を正とする」と書いており、**その「正」が
 * 誤ったまま残る**のが本件である。
 *
 * ── なぜ規約では止まらないか
 * `.claude/rules/traceability.md` は既に規則 4「行フィルタで絞らない。パスから引く」を持ち、
 * 先例 #593 は *`openapi.yaml` が 2 段目の grep で落ちた* という**まったく同じ機構**である。
 * **規則の不足ではなく不遵守**なので、3 本目の規約ではなく検査器の側で止める。
 *
 * ── 何と何を突き合わせるか（[[IADR-0156]] 決定 1）
 * **散文ではなく `x-roles`**（OpenAPI 拡張フィールド）と突き合わせる。散文から役割集合を推定する
 * 案は捨てた —— 実データに
 *     `"403": { description: 管理者ロール以外（IADR-0128 決定 1。運用者も拒否される） }`
 * があり、**「管理者」と「運用者」の両方を含みながら意味は admin のみ**である。語を数える判定は
 * ここで必ず誤る。**機械可読な宣言を 1 つ置き、それを正とする。**
 *
 * ── **`RequireAuthorization` だけでは足りない**（PR #653 レビュー 1 巡目の 🔴）
 * `ConfigBffEndpoints` は **`RequireAuthorization` を意図的に付けず**、ハンドラ内で
 * `IAuthorizationService.AuthorizeAsync(user, ConfigViewer)` を呼んで **404 で存在を秘匿**する
 * （[[IADR-0009]]。`RequireAuthorization` だと無認証が 404 到達前に 401 で短絡して存在が漏れる）。
 * **ミドルウェアを使っていないだけで、ロール制約は在る。** 当初これを「制約なし」と誤って
 * 記録しかけた —— **コメントだけ読んで `DenyAsync` の本体を開かなかった**ためである。
 * よって**同一ファイルの private ヘルパを 1 段だけたどり**、`AuthorizeAsync(..., ポリシー)` も拾う。
 *
 * ── 実効ロール（[[IADR-0128]] 決定 1）
 * 認可メタデータは **AND 合成**される。群が admin ＋ operator で端点が `AdminOnly` なら
 * **実効は admin のみ**。本検査器はこの**積**を出す。
 *
 * ── 実装の走査で気をつけた 4 点（すべて実データで確認した。作業仕様書 #647 §母集合）
 *   1. **同じパス接頭辞に群が 2 つある**（`/bff/documents` と `/bff/tags` は読み取り群と書き込み群）
 *      → パスではなく**変数名**で群を辿る。
 *   2. **群の認可が `MapGroup` と別行**にある → 行単位ではなく**文単位**（`;` まで）で読む。
 *   3. **端点の認可が `.WithName(...)` の後ろ**に来る（`DashboardBffEndpoints` は群に認可が無い）
 *      → 「群に無ければ無認可」と判定しない。
 *   4. 認可の形が 2 種（`AdminOnly` ポリシー名と `RequireRole` ラムダ）→ 両方を解く。
 *
 * ── ポリシー名 → ロールの対応は**ソースから引く**（値を焼き付けない）
 * `AuthExtensions.cs` の `AddPolicy(...)` を読んで解決する。`AdminOnly` の定義が変われば
 * 本検査器の判定も自動で追随する（[[IADR-0141]]「参照点を 1 つに畳む」）。
 *
 * ── **この検査器が検出しないこと**（網羅ではない。開示しておく）
 *   - **散文（`summary` / `description` / コメント）の誤り。** 正は `x-roles` である。
 *     散文の追随は人とレビューの仕事として残る。
 *   - **後段サービス（`*Endpoints.cs`）の認可。** [[IADR-0044]] の多層防御のうち BFF 層だけを見る。
 *     後段まで広げるなら別 issue（[[IADR-0116]] 規約 4）。
 *   - **ヘルパを 2 段以上たどる**ハンドラ内判定。1 段（端点 → 同一ファイルの private ヘルパ）までは
 *     解くが、そのヘルパがさらに別のヘルパへ委譲する形は追わない。
 *   - `src/ai-stock-trading`（別プロジェクトの submodule）。
 *
 * ── allowlist を持たない（#647 受け入れ基準）
 * 偽陽性は**判定条件の側**で扱う。allowlist は事故を隠し、検査器が形骸化する。
 */

const fs = require('node:fs');
const path = require('node:path');

const REPO = path.resolve(__dirname, '..');
const OPENAPI = path.join(REPO, 'docs', 'api', 'openapi.yaml');
const AUTH_EXTENSIONS = path.join(
  REPO, 'src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/AuthExtensions.cs'
);
const BFF_GLOBS = [
  'src/knowledge/backend/Bff',
  'src/platform/backend/Bff/Platform.Bff',
];

// ── C# のコメントを落とす（文字列リテラルの中は守る）。`;` や `"` を含むコメントで壊れないため。
function stripComments(src) {
  let out = '';
  let i = 0;
  while (i < src.length) {
    const c = src[i];
    if (c === '"') {
      // 逐語文字列（@"..."）は "" が escape。通常文字列は \ が escape。
      const verbatim = i > 0 && src[i - 1] === '@';
      out += c;
      i++;
      while (i < src.length) {
        if (verbatim) {
          if (src[i] === '"' && src[i + 1] === '"') { out += '""'; i += 2; continue; }
          if (src[i] === '"') break;
        } else {
          if (src[i] === '\\') { out += ' '; i += 2; continue; }
          if (src[i] === '"') break;
        }
        out += src[i] === '\n' ? '\n' : src[i];
        i++;
      }
      out += '"';
      i++;
      continue;
    }
    if (c === '/' && src[i + 1] === '/') {
      while (i < src.length && src[i] !== '\n') i++;
      continue;
    }
    if (c === '/' && src[i + 1] === '*') {
      i += 2;
      while (i < src.length && !(src[i] === '*' && src[i + 1] === '/')) {
        if (src[i] === '\n') out += '\n';
        i++;
      }
      i += 2;
      continue;
    }
    out += c;
    i++;
  }
  return out;
}

// 与えた位置から、深さ 0 の `;` までを 1 文として切り出す（実測 2・3 への対応）。
function statementFrom(src, start) {
  let depth = 0;
  let i = start;
  while (i < src.length) {
    const c = src[i];
    if (c === '"') { // stripComments 済みなので escape は残っていない
      i++;
      while (i < src.length && src[i] !== '"') i++;
      i++;
      continue;
    }
    if (c === '(' || c === '{' || c === '[') depth++;
    else if (c === ')' || c === '}' || c === ']') depth--;
    else if (c === ';' && depth === 0) return src.slice(start, i);
    i++;
  }
  return src.slice(start);
}

// ── ポリシー名 → ロール集合（AuthExtensions.cs から引く）
function loadPolicies(text) {
  const src = stripComments(text);
  const consts = {};
  for (const m of src.matchAll(/public\s+const\s+string\s+(\w+)\s*=\s*"([^"]*)"/g)) {
    consts[m[1]] = m[2];
  }
  const policies = {};
  for (const m of src.matchAll(/AddPolicy\(\s*PlatformAuthPolicies\.(\w+)/g)) {
    const name = m[1];
    const stmt = statementFrom(src, m.index);
    const roles = new Set();
    for (const r of stmt.matchAll(/PlatformAuthPolicies\.(\w+)/g)) {
      if (r[1] === name) continue; // ポリシー名そのもの
      if (consts[r[1]]) roles.add(consts[r[1]]);
    }
    policies[name] = roles;
  }
  return { consts, policies };
}

// 文中の RequireAuthorization(...) から役割集合を出す。制約が無ければ null。
// 複数あれば AND 合成（積）。
// #656: `.RequireAuthorization(` が文中に在るか。`g` フラグを付けない（`test` が lastIndex を進めて
// 2 回目以降が偽になる）。
const HAS_REQUIRE_AUTH = /\.RequireAuthorization\s*\(/;

function rolesFromStatement(stmt, consts, policies) {
  let acc = null;
  for (const m of stmt.matchAll(/\.RequireAuthorization\s*\(/g)) {
    const argsStart = m.index + m[0].length - 1;
    const args = statementFrom(stmt, argsStart); // 括弧の対応で切れる
    let roles = null;
    const policyRef = /PlatformAuthPolicies\.(\w+)\s*\)/.exec(args);
    if (policyRef && policies[policyRef[1]]) {
      roles = new Set(policies[policyRef[1]]);
    } else if (/RequireRole\s*\(/.test(args)) {
      roles = new Set();
      const call = args.slice(args.indexOf('RequireRole'));
      for (const r of call.matchAll(/PlatformAuthPolicies\.(\w+)/g)) {
        if (consts[r[1]]) roles.add(consts[r[1]]);
      }
      for (const r of call.matchAll(/"([^"]+)"/g)) roles.add(r[1]);
    }
    if (roles === null) continue; // RequireAuthorization() = 認証のみ・ロール制約なし
    acc = acc === null ? roles : new Set([...acc].filter((x) => roles.has(x)));
  }
  return acc;
}

// 同一ファイルの private ヘルパ名 → そのヘルパが強制するポリシーのロール集合。
// `ConfigBffEndpoints.DenyAsync` のように、ミドルウェアではなくハンドラ内で認可する形を拾う。
// **1 段だけ**たどる（ヘルパがさらに委譲する形は追わない。冒頭コメントに開示済み）。
function collectAuthHelpers(src, consts, policies) {
  const helpers = {};
  for (const m of src.matchAll(/\b(?:private|internal)\s+static\s+[\w<>?,\s]+?\s(\w+)\s*\(/g)) {
    const name = m[1];
    // メソッド本体（最初の `{` から対応する `}` まで）を切り出す。
    const braceStart = src.indexOf('{', m.index + m[0].length);
    if (braceStart === -1) continue;
    let depth = 0;
    let i = braceStart;
    for (; i < src.length; i++) {
      if (src[i] === '{') depth++;
      else if (src[i] === '}') { depth--; if (depth === 0) break; }
    }
    const body = src.slice(braceStart, i);
    const roles = rolesFromAuthorizeAsync(body, consts, policies);
    if (roles !== null) helpers[name] = roles;
  }
  return helpers;
}

// `AuthorizeAsync(..., PlatformAuthPolicies.X)` からロール集合を出す。無ければ null。
function rolesFromAuthorizeAsync(text, consts, policies) {
  let acc = null;
  for (const m of text.matchAll(/AuthorizeAsync\([^)]*?PlatformAuthPolicies\.(\w+)\s*\)/g)) {
    const roles = policies[m[1]] ? new Set(policies[m[1]]) : null;
    if (roles === null) continue;
    acc = acc === null ? roles : new Set([...acc].filter((x) => roles.has(x)));
  }
  return acc;
}

// 実装の経路制約（`{id:guid}`）を openapi の表記（`{id}`）へ揃える。
// **openapi 側を実装へ寄せない** —— OpenAPI に ASP.NET の経路制約構文は無い。
function normalizePath(p) {
  return p.replace(/\{(\w+)(?::[^}]+)?\}/g, '{$1}');
}

function joinPath(prefix, sub) {
  const joined = !sub || sub === '/' ? prefix : (prefix + sub).replace(/\/{2,}/g, '/');
  return normalizePath(joined.replace(/\/$/, '') || '/');
}

// ── 実装側: (path, method) → 実効ロール
function collectImplementation(files, consts, policies) {
  const endpoints = [];
  for (const file of files) {
    const src = stripComments(fs.readFileSync(file, 'utf8'));
    const helpers = collectAuthHelpers(src, consts, policies);
    // 群: var NAME = app.MapGroup("PATH") … ;
    const groups = {};
    for (const m of src.matchAll(/\bvar\s+(\w+)\s*=\s*app\s*\.\s*MapGroup\(\s*"([^"]+)"/g)) {
      const stmt = statementFrom(src, m.index);
      groups[m[1]] = {
        prefix: m[2],
        roles: rolesFromStatement(stmt, consts, policies),
        // #656: **ロールとは別の軸**。`rolesFromStatement` は「認可属性なし」と
        // 「`RequireAuthorization()`（認証のみ・ロール不問）」をどちらも null へ畳むため、
        // ロールだけを見ていると**無認証の端点が「ロール制約なし」として素通りする**。
        requiresAuth: HAS_REQUIRE_AUTH.test(stmt),
      };
    }
    // 端点: NAME.MapVerb("SUB" … ;
    for (const m of src.matchAll(/\b(\w+)\s*\.\s*Map(Get|Post|Put|Patch|Delete)\(\s*"([^"]*)"/g)) {
      const group = groups[m[1]];
      if (!group) {
        // #656: **群に属さない `app.MapVerb("/bff/...")` は本検査器の網から漏れる。**
        // 群を辿って認可を合成する設計なので、群が無いと `requiresAuth` を判定できない。
        // 現状 0 件だが（`/internal/config/drift-run` だけが群外で、これは `/bff/` ではない）、
        // **将来この形で書かれると「無認証の /bff/ 端点は存在しない」という不変条件が黙ってすり抜ける。**
        // 見逃さず、**明示的な違反として報告する**（対処は群へ移すこと）。
        if (m[1] === 'app' && m[3].startsWith('/bff/')) {
          endpoints.push({
            file: path.relative(REPO, file),
            path: normalizePath(m[3]),
            method: m[2].toLowerCase(),
            roles: null,
            requiresAuth: false,
            ungrouped: true,
          });
        }
        continue; // それ以外（`/internal/` 等）は対象外
      }
      const stmt = statementFrom(src, m.index);
      let own = rolesFromStatement(stmt, consts, policies);
      // ハンドラ内の認可（直接 AuthorizeAsync / ヘルパ呼び出し）も AND 合成する。
      const inline = rolesFromAuthorizeAsync(stmt, consts, policies);
      for (const [name, roles] of Object.entries(helpers)) {
        if (!new RegExp(`\\b${name}\\s*\\(`).test(stmt)) continue;
        own = own === null ? new Set(roles) : new Set([...own].filter((x) => roles.has(x)));
      }
      if (inline !== null) {
        own = own === null ? inline : new Set([...own].filter((x) => inline.has(x)));
      }
      const effective = group.roles === null
        ? own
        : own === null
          ? new Set(group.roles)
          : new Set([...group.roles].filter((x) => own.has(x)));
      // #656: 認証を要求しているか。**ミドルウェアの有無で判定してはならない** ——
      // `ConfigBffEndpoints` は `RequireAuthorization` を意図的に付けず、ハンドラ内の
      // `AuthorizeAsync(user, ConfigViewer)` ＋ 404 で存在を秘匿する（[[IADR-0009]]）。
      // したがって**群・端点・ハンドラ内・ヘルパの 4 経路のいずれかに認可が在れば真**とする。
      const requiresAuth =
        group.requiresAuth || HAS_REQUIRE_AUTH.test(stmt) || effective !== null;
      endpoints.push({
        file: path.relative(REPO, file),
        path: joinPath(group.prefix, m[3]),
        method: m[2].toLowerCase(),
        roles: effective,
        requiresAuth,
      });
    }
  }
  return endpoints;
}

// ── 契約側: openapi.yaml の /bff/ パス × メソッド → x-roles
// YAML ライブラリに依存しない（scripts/ は Node 標準のみで動く。check-doc-links と同じ方針）。
function collectContract(text) {
  const lines = text.split('\n');
  const ops = new Map(); // "method path" -> { roles: string[]|null, line }
  let curPath = null;
  let curMethod = null;
  let curLine = 0;
  let inRoles = false;
  let roles = null;
  const flush = () => {
    if (curPath && curMethod) ops.set(`${curMethod} ${curPath}`, { roles, line: curLine });
    curMethod = null; roles = null; inRoles = false;
  };
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const pathM = /^ {2}(\/\S*):\s*$/.exec(line);
    if (pathM) { flush(); curPath = pathM[1]; continue; }
    const methodM = /^ {4}(get|post|put|patch|delete):\s*$/.exec(line);
    if (methodM) { flush(); curMethod = methodM[1]; curLine = i + 1; continue; }
    if (!curMethod) continue;
    // インライン形: x-roles: [platform-admin, platform-operator]
    const inline = /^ {6}x-roles:\s*\[([^\]]*)\]\s*$/.exec(line);
    if (inline) {
      roles = inline[1].split(',').map((s) => s.trim().replace(/^["']|["']$/g, '')).filter(Boolean);
      continue;
    }
    if (/^ {6}x-roles:\s*$/.test(line)) { inRoles = true; roles = []; continue; }
    if (inRoles) {
      const item = /^ {8}-\s*(\S+)\s*$/.exec(line);
      if (item) { roles.push(item[1].replace(/^["']|["']$/g, '')); continue; }
      inRoles = false;
    }
  }
  flush();
  return ops;
}

function setEq(a, b) {
  if (a.size !== b.size) return false;
  for (const x of a) if (!b.has(x)) return false;
  return true;
}

function fmt(roles) {
  if (roles === null) return '(ロール制約なし)';
  return roles.size === 0 ? '(空)' : [...roles].sort().join(' + ');
}

/** 純関数の判定本体。ファイル I/O に触らないのでそのまま試験できる。 */
function findViolations(endpoints, contractOps) {
  const violations = [];
  for (const ep of endpoints) {
    const key = `${ep.method} ${ep.path}`;
    // #656: **無認証で到達できる端点は、契約と一致していても違反である。**
    // 計画 NFR-09 の暫定運用が「エッジ（BFF）で OIDC/JWT を担保する」と定めており、
    // `/bff/*` に無認証の端点は存在してはならない（本 PR 時点で実測 0 件。それを不変条件にする）。
    // **ロールの一致だけを見ていると素通りする** —— 実装も契約も「ロール制約なし」で辻褄が合うため。
    if (ep.ungrouped) {
      violations.push({ kind: 'ungrouped', key, file: ep.file });
      continue;
    }
    if (ep.requiresAuth === false) {
      violations.push({ kind: 'anonymous', key, file: ep.file });
      continue;
    }
    const declared = contractOps.get(key);
    if (!declared) {
      violations.push({ kind: 'missing-in-contract', key, file: ep.file, effective: ep.roles });
      continue;
    }
    if (declared.roles === null) {
      violations.push({ kind: 'missing-x-roles', key, line: declared.line, effective: ep.roles });
      continue;
    }
    const declaredSet = new Set(declared.roles);
    const effective = ep.roles === null ? null : ep.roles;
    if (effective === null) {
      if (declaredSet.size > 0) {
        violations.push({ kind: 'mismatch', key, line: declared.line, effective, declared: declaredSet });
      }
      continue;
    }
    if (!setEq(effective, declaredSet)) {
      violations.push({ kind: 'mismatch', key, line: declared.line, effective, declared: declaredSet });
    }
  }
  return violations;
}

function formatReport(violations) {
  const lines = [`[check-bff-authz-docs] BFF 認可の違反 ${violations.length} 件を検出しました:`, ''];
  for (const v of violations) {
    if (v.kind === 'ungrouped') {
      lines.push(`  ${v.key}  (${v.file})`);
      lines.push('    **`app.MapVerb("/bff/...")` が群に属していない。** 本検査器は群を辿って認可を');
      lines.push('    合成するため、この形は認可を判定できない。`MapGroup` 配下へ移すこと（#656）');
    } else if (v.kind === 'anonymous') {
      lines.push(`  ${v.key}  (${v.file})`);
      lines.push('    **無認証で到達できる。** 群・端点・ハンドラ内のいずれにも認可が無い');
      lines.push('    （NFR-09 暫定運用「エッジ〔BFF〕で OIDC/JWT を担保する」・#656）');
    } else if (v.kind === 'mismatch') {
      lines.push(`  ${v.key}  (docs/api/openapi.yaml:${v.line})`);
      lines.push(`    実装の実効ロール: ${fmt(v.effective)}`);
      lines.push(`    openapi の x-roles: ${fmt(v.declared)}`);
    } else if (v.kind === 'missing-x-roles') {
      lines.push(`  ${v.key}  (docs/api/openapi.yaml:${v.line})`);
      lines.push(`    x-roles が無い。実装の実効ロールは ${fmt(v.effective)}`);
    } else {
      lines.push(`  ${v.key}  (${v.file})`);
      lines.push('    実装にあるが openapi.yaml に無い');
    }
  }
  lines.push('');
  lines.push('認可は AND 合成される（IADR-0128 決定 1）。群が admin+operator でも端点に AdminOnly を');
  lines.push('積めば実効は admin のみである。x-roles には**実効ロール**を書くこと（IADR-0156）。');
  return lines.join('\n');
}

function listCsFiles(dir, acc = []) {
  if (!fs.existsSync(dir)) return acc;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) listCsFiles(p, acc);
    else if (e.name.endsWith('BffEndpoints.cs')) acc.push(p);
  }
  return acc;
}

function main() {
  const { consts, policies } = loadPolicies(fs.readFileSync(AUTH_EXTENSIONS, 'utf8'));
  const files = BFF_GLOBS.flatMap((d) => listCsFiles(path.join(REPO, d))).sort();
  const endpoints = collectImplementation(files, consts, policies);
  const contractOps = collectContract(fs.readFileSync(OPENAPI, 'utf8'));
  const violations = findViolations(endpoints, contractOps);
  if (violations.length === 0) {
    console.log(
      `[check-bff-authz-docs] OK: BFF ${files.length} ファイル / ${endpoints.length} 端点の実効ロールが openapi.yaml の x-roles と一致しています。`
    );
    return 0;
  }
  console.error(formatReport(violations));
  return 1;
}

if (require.main === module) process.exit(main());

module.exports = {
  main,
  stripComments,
  statementFrom,
  loadPolicies,
  rolesFromStatement,
  collectImplementation,
  collectAuthHelpers,
  rolesFromAuthorizeAsync,
  collectContract,
  normalizePath,
  findViolations,
  formatReport,
};
