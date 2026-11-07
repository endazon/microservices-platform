#!/usr/bin/env node
'use strict';
/*
 * gen-rabbitmq-permissions.js
 * NFR-18, IADR-0681: ブローカのサービスごとのユーザー（deploy/local/rabbitmq-setup/users.txt）の権限を、
 * **各サービスが実際に使うキューと exchange の名前だけ**へ絞った正規表現（configure / write / read）を、コードから導いて作る。
 *
 *   node scripts/gen-rabbitmq-permissions.js            # 照合（既定）。生成物 permissions.txt と食い違えば exit 1
 *   node scripts/gen-rabbitmq-permissions.js --write    # permissions.txt を書き直す
 *   node scripts/gen-rabbitmq-permissions.js --explain  # ユーザーごとの名前（キュー・exchange）を表示する（書かない）
 *
 * 名前は記憶で書かない。**Wolverine の配線の呼び出しから読む**:
 *   - 基盤（MSP）: 各サービスの Program.cs の `opts.ServiceName = "<app>"`・`BindPlatformQueue<T>("<app>", …)`（購読）・
 *     `RoutePlatformEvent<T>()`（発行）。キュー名は `<app>.<キュー>`、exchange 名は型の短い名前
 *     （Platform.Shared.Infrastructure の WolverineExtensions。規則の字面が変わったら止める）。
 *   - 取引ユニット（AST）: submodule（src/ai-stock-trading。無ければ環境変数 AST_ROOT）の各 Worker を読む。配線は
 *     shim の UseAiStockTradingRabbitMq（Wolverine の規約ルーティング）で、キュー名は `<ServiceName>.<型名>`、
 *     デッドレターは `<キュー>_error`、exchange 名は型の完全名。購読はハンドラ（Wolverine のハンドラ発見の規則）から、
 *     発行は「メッセージ型をそのサービスのコードが作る（new T）」から読む（過大側に倒す。漏れは下の突き合わせで止める）。
 *   - 両方に共通の Wolverine の名前: 共有のデッドレター `wolverine-dead-letter-queue`・応答キュー `wolverine.response.<Guid>`
 *     （Wolverine が `Guid.NewGuid()` の既定の書式で名付ける。正規表現も Guid の形だけに当てる）・
 *     既定の exchange `amq.default`（リスナーの停止のときに受け取り済みの未処理のメッセージをキューへ戻す送り先。
 *     書けないと戻しが失敗し、取りこぼしが出ることを実ブローカで確かめた）。
 *   - NFR-18, IADR-0691: **既定の exchange への write は、自分の受信キューを持つ（購読する）利用者だけに与える。**
 *     既定の exchange への発行は宛先のキューの権限を見ないので、持つ利用者は名前を知るどのキューへも入れられる。
 *     戻しの要らない発行だけの利用者には与えない（受信キューが無く、戻すものが無い）。
 *   - NFR-18, IADR-0693: **クラウドの写し（deploy/cloud）では、購読する（自分が発行しない）exchange の configure を与えない。**
 *     取引ユニットはクラウドで購読の exchange を受動で宣言する（AST の IADR-0628。values-cloud の subscribedExchanges=Passive）。
 *     購読に要るのは exchange の read（束ね）と自分のキューだけになる。exchange はブローカの起動の直後の初期化が先に宣言する
 *     （ConfigMap の exchanges.json。購読される exchange の一覧をここで導く）。ローカル（permissions.txt）は従来のまま（購読側も宣言する）。
 *
 * AST の写しが無い checkout（submodule を取得しないジョブ）では、AST の行は生成物の値をそのまま残し、照合しない
 * （そのことを notice で必ず出す）。RABBITMQ_PERMISSIONS_REQUIRE_AST=1 なら無いことを失敗にする（CI の submodule を取るジョブ）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const USERS_REL = 'deploy/local/rabbitmq-setup/users.txt';
const PERMS_REL = 'deploy/local/rabbitmq-setup/permissions.txt';
const MSP_WOLVERINE_REL = 'src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/WolverineExtensions.cs';
const MSP_SERVICE_DIRS = ['src/knowledge/backend/Services', 'src/platform/backend/Services'];
const AST_SHIM_REL = 'backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Extensions/WolverineExtensions.cs';
const AST_PIPELINE_REL = 'deploy/helm/ai-stock-trading/files/pipeline.json';
const AST_CONTRACT_NS = 'AiStockTrading.Shared.Contracts.Events';

// Wolverine が全サービスで宣言・使用する名前（上の注記）。
const DEAD_LETTER = 'wolverine-dead-letter-queue';
const RESPONSE_PREFIX = 'wolverine.response.';
const DEFAULT_EXCHANGE = 'amq.default';
// NFR-18, IADR-0691: 応答キューの名前の正規表現。Wolverine（RabbitMqTransport.tryBuildSystemEndpoints）は
// `wolverine.response.{Guid.NewGuid()}`（既定の書式 = 小文字の 16 進 8-4-4-4-12）で名付ける。`.*` にすると、
// 接頭辞を同じにした任意の名前（他の用途のキュー・exchange）にも当たる。awk（点検）が区間 `{n}` を読めない実装でも
// 同じに読めるよう、文字クラスを並べて書く。
const HEX = '[0-9a-f]';

// Wolverine のハンドラ発見の規則（型名の接尾辞・メソッド名）。WolverineFx 6.24 の既定。
const HANDLER_TYPE_SUFFIX = /(Handler|Consumer)$/;
const HANDLER_METHODS = ['Handle', 'Handles', 'HandleAsync', 'HandlesAsync', 'Consume', 'Consumes', 'ConsumeAsync', 'ConsumesAsync'];

class GenError extends Error {}
const fail = (msg) => { throw new GenError(msg); };

const reEscape = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const RESPONSE_PATTERN = `${reEscape(RESPONSE_PREFIX)}${[8, 4, 4, 4, 12].map((n) => HEX.repeat(n)).join('-')}`;
const toPosix = (p) => p.split(path.sep).join('/');

// コメント（// と /* */）と文字列の中身を除く（コメントアウトした配線を読まない）。文字列は位置を保ったまま残す。
function stripComments(src) {
  let out = '';
  let i = 0;
  while (i < src.length) {
    const c = src[i];
    const n = src[i + 1];
    if (c === '/' && n === '/') { while (i < src.length && src[i] !== '\n') i++; continue; }
    if (c === '/' && n === '*') { i += 2; while (i < src.length && !(src[i] === '*' && src[i + 1] === '/')) { out += src[i] === '\n' ? '\n' : ' '; i++; } i += 2; continue; }
    if (c === '"' || c === '\'') {
      const verbatim = src[i - 1] === '@';
      const q = c;
      out += c; i++;
      while (i < src.length && src[i] !== q) {
        if (!verbatim && src[i] === '\\') { out += src[i] + (src[i + 1] || ''); i += 2; continue; }
        out += src[i]; i++;
      }
      out += q; i++;
      continue;
    }
    out += c; i++;
  }
  return out;
}

function walk(dir, out = []) {
  if (!fs.existsSync(dir)) return out;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (['bin', 'obj', 'node_modules', '.git', 'Tests', 'tests'].includes(e.name)) continue;
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, out);
    else if (e.name.endsWith('.cs')) out.push(p);
  }
  return out;
}

/** users.txt の行（コメント・空行を除く）。 */
function readUsers(repoRoot = REPO_ROOT) {
  const rows = fs.readFileSync(path.join(repoRoot, USERS_REL), 'utf8').split('\n')
    .filter((l) => l.trim() && !/^\s*#/.test(l))
    .map((l) => l.trim().split(/\s+/))
    .map(([user, namespace, app, kv]) => ({ user, namespace, app, kv }));
  if (rows.length === 0) fail(`${USERS_REL} にユーザーが 1 件も無い`);
  return rows;
}

// ---------------------------------------------------------------------------------------------------------------
// 基盤（MSP）
// ---------------------------------------------------------------------------------------------------------------

function assertMspNamingRules(repoRoot) {
  const src = fs.readFileSync(path.join(repoRoot, MSP_WOLVERINE_REL), 'utf8');
  if (!src.includes('return $"{serviceName}.{queueName}";')) fail(`${MSP_WOLVERINE_REL}: キュー名の規則（<サービス>.<キュー>）の字面が見当たらない。導出を見直す`);
  if (!/PlatformExchangeName<TEvent>\(\) => typeof\(TEvent\)\.Name;/.test(src)) fail(`${MSP_WOLVERINE_REL}: exchange 名の規則（型の短い名前）の字面が見当たらない。導出を見直す`);
}

function deriveMsp(row, repoRoot) {
  const candidates = [];
  for (const d of MSP_SERVICE_DIRS) {
    const base = path.join(repoRoot, d);
    if (!fs.existsSync(base)) continue;
    for (const svc of fs.readdirSync(base)) {
      const p = path.join(base, svc, 'Program.cs');
      if (!fs.existsSync(p)) continue;
      const code = stripComments(fs.readFileSync(p, 'utf8'));
      if (new RegExp(`\\bServiceName\\s*=\\s*"${reEscape(row.app)}"`).test(code)) candidates.push({ file: p, code });
    }
  }
  if (candidates.length !== 1) fail(`${row.user}: opts.ServiceName = "${row.app}" の Program.cs が ${candidates.length} 件（1 件であるべき）`);
  const { file, code } = candidates[0];
  const rel = toPosix(path.relative(repoRoot, file));
  const listens = new Set();
  const routes = new Set();
  for (const m of code.matchAll(/\b(BindPlatformQueue|ListenToPlatformQueue)<(\w+)>\(\s*("[^"]*"|\w+)/g)) {
    if (m[3] !== `"${row.app}"`) fail(`${rel}: ${m[1]}<${m[2]}> のサービス名が "${row.app}" でない（${m[3]}）。キューの接頭辞が users.txt の app と食い違う`);
    if (m[1] === 'BindPlatformQueue') listens.add(m[2]);
  }
  for (const m of code.matchAll(/\bRoutePlatformEvent<(\w+)>\(\s*\)/g)) routes.add(m[1]);
  // 受信キュー（リスナー）を持つか。束ねの無いリスナー（段宣言なしの ListenToPlatformQueue）も戻しをする。
  const hasListener = listens.size > 0 || /\bListenTo(?:Platform|Rabbit)Queue\b/.test(code);
  // 段宣言なしの形（ListenToPlatformQueue(opts, "<app>", "<queue>")）も接頭辞は同じなので、キューは前置の規則で覆う。
  if (listens.size === 0 && routes.size === 0) fail(`${rel}: ブローカの配線（BindPlatformQueue / RoutePlatformEvent）が 1 つも無い。users.txt の ${row.user} はブローカを使わないのでは`);
  return {
    user: row.user,
    unit: 'microservices-platform',
    source: rel,
    queuePrefix: `${row.app}.`,
    deadLetterQueues: false,
    hasListener,
    subscribe: [...listens].sort(),
    publish: [...routes].sort(),
  };
}

// ---------------------------------------------------------------------------------------------------------------
// 取引ユニット（AST）
// ---------------------------------------------------------------------------------------------------------------

function astRoot(repoRoot) {
  const candidates = [process.env.AST_ROOT, path.join(repoRoot, 'src/ai-stock-trading')].filter(Boolean);
  for (const c of candidates) if (fs.existsSync(path.join(c, 'backend/Services'))) return c;
  return null;
}

function assertAstNamingRules(root) {
  const src = fs.readFileSync(path.join(root, AST_SHIM_REL), 'utf8');
  if (!src.includes('return $"{serviceName}.{messageType.Name}";')) fail(`AST ${AST_SHIM_REL}: キュー名の規則（<ServiceName>.<型名>）の字面が見当たらない。導出を見直す`);
  if (!src.includes('return $"{queueName}_error";')) fail(`AST ${AST_SHIM_REL}: デッドレターの規則（<キュー>_error）の字面が見当たらない。導出を見直す`);
  const code = stripComments(src);
  if (!/\.UseConventionalRouting\(/.test(code) || /ExchangeNameForSending|PrefixIdentifiers|IdentifierPrefix/.test(code)) {
    fail(`AST ${AST_SHIM_REL}: exchange 名の規則（規約ルーティングの既定＝型の完全名）が変わった。導出を見直す`);
  }
}

/** 型の索引: 短い名前 → [{ full, ns, nested }]。宣言を数えるだけの軽い走査（型解析はしない）。 */
function buildTypeIndex(files) {
  const index = new Map();
  for (const f of files) {
    const code = stripComments(fs.readFileSync(f, 'utf8'));
    const fileNs = (code.match(/^\s*namespace\s+([\w.]+)\s*;/m) || [])[1];
    // 入れ子の判定: 宣言の位置の波括弧の深さ（ファイルスコープの名前空間なら 0、ブロックの名前空間なら 1 が最上位）。
    const top = fileNs ? 0 : 1;
    let depth = 0;
    let blockNs = null;
    const re = /\b(class|record|struct|interface|enum)\s+(\w+)|[{}]|\bnamespace\s+([\w.]+)\s*\{/g;
    let m;
    while ((m = re.exec(code))) {
      if (m[3]) { blockNs = m[3]; depth++; continue; }
      if (m[0] === '{') { depth++; continue; }
      if (m[0] === '}') { depth--; continue; }
      const ns = fileNs || blockNs;
      if (!ns) continue;
      const name = m[2];
      // `record struct X` は struct と record の 2 回当たるが同じ名前・同じ位置なので重複を除く。
      const list = index.get(name) || [];
      const entry = { full: `${ns}.${name}`, ns, nested: depth > top, kind: m[1], file: f };
      if (!list.some((e) => e.full === entry.full)) list.push(entry);
      index.set(name, list);
    }
  }
  return index;
}

function usingsOf(code) {
  const out = [];
  for (const m of code.matchAll(/^\s*(?:global\s+)?using\s+(?!static\b)(?!\w+\s*=)([\w.]+)\s*;/gm)) out.push(m[1]);
  return out;
}

/** 短い名前（または完全名）を、使った側のファイルの名前空間と using で解決する。 */
function resolveType(name, code, index, where) {
  if (name.includes('.')) {
    const short = name.split('.').pop();
    const hit = (index.get(short) || []).filter((e) => e.full === name || e.full.endsWith(`.${name}`));
    if (hit.length === 1) return hit[0];
    return null;
  }
  const list = index.get(name) || [];
  if (list.length === 0) return null;
  const fileNs = (code.match(/^\s*namespace\s+([\w.]+)/m) || [])[1] || '';
  const visible = new Set(usingsOf(code));
  // 自分の名前空間とその親は using なしで見える。
  const parts = fileNs.split('.');
  for (let i = parts.length; i > 0; i--) visible.add(parts.slice(0, i).join('.'));
  const seen = list.filter((e) => visible.has(e.ns));
  if (seen.length === 1) return seen[0];
  if (seen.length > 1) fail(`${where}: 型 ${name} が曖昧（${seen.map((e) => e.full).join(' / ')}）`);
  return null;
}

/** 波括弧の対応で、開き括弧の位置からその本体の終わりまでを返す。 */
function bodyFrom(code, openAt) {
  let depth = 0;
  for (let i = openAt; i < code.length; i++) {
    if (code[i] === '{') depth++;
    else if (code[i] === '}') { depth--; if (depth === 0) return code.slice(openAt, i + 1); }
  }
  return code.slice(openAt);
}

/** Wolverine のハンドラ発見の規則で、サービスのハンドラが受けるメッセージ型（解決済み）を集める。 */
function findHandledTypes(files, index, rel) {
  const handled = new Map();
  const methodRe = new RegExp(`\\bpublic\\s+(?:static\\s+)?(?:async\\s+)?(?:virtual\\s+)?[\\w<>\\[\\]?,.\\s()]*?\\b(?:${HANDLER_METHODS.join('|')})\\s*\\(\\s*(?:\\[[^\\]]*\\]\\s*)?(?:this\\s+)?([\\w.]+)\\s*\\??\\s+\\w+`, 'g');
  for (const f of files) {
    const code = stripComments(fs.readFileSync(f, 'utf8'));
    for (const cm of code.matchAll(/\bpublic\s+(?:(?:sealed|static|partial)\s+)*(?:class|record)\s+(\w+)[^{;]*\{/g)) {
      if (!HANDLER_TYPE_SUFFIX.test(cm[1])) continue;
      const body = bodyFrom(code, cm.index + cm[0].length - 1);
      for (const mm of body.matchAll(methodRe)) {
        const t = resolveType(mm[1], code, index, `${rel(f)} ${cm[1]}`);
        if (!t) fail(`${rel(f)}: ハンドラ ${cm[1]} の受ける型 ${mm[1]} を解決できない（Wolverine はこれのキューを作る）`);
        if (t.nested) fail(`${rel(f)}: ハンドラ ${cm[1]} の受ける型 ${t.full} は入れ子の型（exchange 名の導出を持たない）`);
        handled.set(t.full, t);
      }
    }
  }
  return handled;
}

/**
 * メッセージ型（契約のイベント・いずれかのサービスが受ける型）のうち、サービスのコードが作るものを集める。「作る」は 3 つの形:
 *   (a) `new T(` / `new T {`、(b) 型を書いた宣言への target-typed new（`T x = new(`）、
 *   (c) メソッドの戻り値の型に T が現れる（`T Build(…) => new(…)`・`IReadOnlyList<T> Plan(…)`・`Task<T>` 等）。
 * 過大側に倒す（作るが発行しない型にも書き込みが付く）。読み漏れは deriveAst の突き合わせ（受ける型に発行元が要る・
 * pipeline.json の発行）で止める。
 */
function findConstructedTypes(files, index, messageTypes, rel) {
  const out = new Map();
  const note = (name, code, f) => {
    const short = name.split('.').pop();
    if (!messageTypes.has(short)) return;
    const t = resolveType(name, code, index, rel(f));
    if (t && messageTypes.get(short).has(t.full)) out.set(t.full, t);
  };
  for (const f of files) {
    const code = stripComments(fs.readFileSync(f, 'utf8'));
    for (const m of code.matchAll(/\bnew\s+([A-Z][\w.]*)\s*[({]/g)) note(m[1], code, f);
    for (const m of code.matchAll(/\b([A-Z][\w.]*)\??\s+\w+\s*=\s*new\s*\(/g)) note(m[1], code, f);
    const header = /\b(?:public|private|protected|internal|static)\s+(?:(?:static|async|override|virtual|sealed|new|partial|readonly)\s+)*([\w<>?,\s().[\]]+?)\s+\w+\s*(?:<[^>()]*>)?\s*\(/g;
    for (const m of code.matchAll(header)) {
      for (const tok of m[1].match(/[A-Z][\w.]*/g) || []) note(tok, code, f);
    }
  }
  return out;
}

function deriveAst(rows, root) {
  assertAstNamingRules(root);
  const servicesDir = path.join(root, 'backend/Services');
  const rel = (f) => `AST ${toPosix(path.relative(root, f))}`;
  const allFiles = walk(path.join(root, 'backend'));
  const index = buildTypeIndex(allFiles);

  // users.txt の app → サービスのディレクトリ（ServiceName の定数で引く）。
  const svcDirs = new Map();
  for (const d of fs.readdirSync(servicesDir)) {
    const p = path.join(servicesDir, d, 'Program.cs');
    if (!fs.existsSync(p)) continue;
    const code = stripComments(fs.readFileSync(p, 'utf8'));
    if (!/\bUseAiStockTradingRabbitMq\s*\(/.test(code)) continue;
    const name = (code.match(/\bconst\s+string\s+ServiceName\s*=\s*"([^"]+)"/) || [])[1];
    if (!name) fail(`${rel(p)}: UseAiStockTradingRabbitMq を呼ぶが const string ServiceName が無い（キューの接頭辞を導けない）`);
    if (!/\bUseAiStockTradingRabbitMq\s*\(\s*ServiceName\b/.test(code)) fail(`${rel(p)}: UseAiStockTradingRabbitMq の第 1 引数が ServiceName でない`);
    svcDirs.set(name, path.join(servicesDir, d));
  }

  const perService = new Map();
  for (const row of rows) {
    const serviceName = `ai-stock-trading.${row.app}`;
    const dir = svcDirs.get(serviceName);
    if (!dir) fail(`${row.user}: AST に ServiceName "${serviceName}" でブローカへ繋ぐ Worker が無い`);
    const files = walk(dir);
    perService.set(row.user, { row, serviceName, dir, files, handled: findHandledTypes(files, index, rel) });
  }
  for (const [name] of svcDirs) {
    if (![...perService.values()].some((s) => s.serviceName === name)) fail(`AST の ${name} はブローカへ繋ぐが users.txt に行が無い`);
  }

  // メッセージ型: 契約のイベント ∪ いずれかのサービスが受ける型（短い名前 → 完全名の集合）。
  const messageTypes = new Map();
  const addMsg = (t) => { const s = t.full.split('.').pop(); if (!messageTypes.has(s)) messageTypes.set(s, new Set()); messageTypes.get(s).add(t.full); };
  for (const list of index.values()) for (const e of list) if (e.ns === AST_CONTRACT_NS && !e.nested && ['class', 'record', 'struct'].includes(e.kind)) addMsg(e);
  for (const s of perService.values()) for (const t of s.handled.values()) addMsg(t);

  // 共有ライブラリ（backend/Shared の本番コード）の部品が作るメッセージ型（例: 照会の状態の通知を作って発行する部品）。
  // 部品（メッセージ型でない型）→ その部品のファイルが作るメッセージ型。部品が別の部品を使うなら推移で足す。
  // サービスは、部品の名前をコードに書いていれば（使っていれば）その型も発行し得るとみなす（過大側）。
  const sharedFiles = walk(path.join(root, 'backend/Shared'));
  const isMessage = (name) => messageTypes.has(name);
  const parts = new Map();
  for (const f of sharedFiles) {
    const code = stripComments(fs.readFileSync(f, 'utf8'));
    const made = findConstructedTypes([f], index, messageTypes, rel);
    const declared = [...code.matchAll(/\b(?:class|record|struct)\s+(\w+)/g)].map((m) => m[1]).filter((n) => !isMessage(n));
    for (const n of declared) parts.set(n, { code, made: new Set(made.keys()) });
  }
  for (let changed = true; changed;) {
    changed = false;
    for (const [, p] of parts) {
      for (const [other, q] of parts) {
        if (p === q || !new RegExp(`\\b${other}\\b`).test(p.code)) continue;
        for (const t of q.made) if (!p.made.has(t)) { p.made.add(t); changed = true; }
      }
    }
  }
  const viaParts = (files) => {
    const got = new Map();
    for (const f of files) {
      const code = stripComments(fs.readFileSync(f, 'utf8'));
      for (const [name, p] of parts) {
        if (p.made.size === 0 || !new RegExp(`\\b${name}\\b`).test(code)) continue;
        for (const full of p.made) got.set(full, { full });
      }
    }
    return got;
  };

  const out = [];
  const publishers = new Map();
  for (const s of perService.values()) {
    const published = new Map([...findConstructedTypes(s.files, index, messageTypes, rel), ...viaParts(s.files)]);
    for (const full of published.keys()) publishers.set(full, [...(publishers.get(full) || []), s.row.app]);
    s.published = published;
  }

  // 突き合わせ（発行の読み漏れを止める）: 受ける型には発行元が要る。pipeline.json の発行（sources / outputs）は宣言のサービスに要る。
  for (const s of perService.values()) {
    for (const full of s.handled.keys()) {
      if (!publishers.has(full)) fail(`AST: ${s.row.app} が受ける ${full} を作る（new）サービスが無い。発行の読み漏れ（発行元の権限が欠ける）か、受けるだけの型か`);
    }
  }
  const pipelinePath = path.join(root, AST_PIPELINE_REL);
  if (fs.existsSync(pipelinePath)) {
    const pipeline = JSON.parse(fs.readFileSync(pipelinePath, 'utf8'));
    const expect = [
      ...(pipeline.sources || []).map((x) => ({ service: x.service, event: x.event })),
      ...(pipeline.steps || []).flatMap((st) => (st.outputs || []).map((ev) => ({ service: st.service, event: ev }))),
      ...(pipeline.steps || []).filter((st) => st.enabled !== false).map((st) => ({ service: st.service, event: st.input, subscribe: true })),
    ];
    for (const e of expect) {
      const s = [...perService.values()].find((x) => x.row.app === e.service);
      if (!s) fail(`AST ${AST_PIPELINE_REL}: ${e.service} は users.txt に無い`);
      const full = `${AST_CONTRACT_NS}.${e.event}`;
      const set = e.subscribe ? s.handled : s.published;
      if (!set.has(full)) fail(`AST ${AST_PIPELINE_REL}: ${e.service} の${e.subscribe ? '購読' : '発行'} ${e.event} を導出できなかった（導出の読み漏れ）`);
    }
  } else {
    fail(`AST ${AST_PIPELINE_REL} が無い（発行の突き合わせができない）`);
  }

  for (const s of perService.values()) {
    out.push({
      user: s.row.user,
      unit: 'ai-stock-trading',
      source: toPosix(path.relative(root, s.dir)),
      queuePrefix: `${s.serviceName}.`,
      deadLetterQueues: true,
      // 規約ルーティングはハンドラの受ける型ごとにリスナーを立てる。ハンドラが無ければ受信キューも無い。
      hasListener: s.handled.size > 0,
      subscribe: [...s.handled.keys()].sort(),
      publish: [...s.published.keys()].sort(),
    });
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// 正規表現
// ---------------------------------------------------------------------------------------------------------------

const alt = (names) => `^(${names.join('|')})$`;

/**
 * 導出（名前の集合）から 3 つの正規表現を作る。RabbitMQ の権限の表（下の requiredAccess）と対で読む。
 * cloud: クラウドの写し（NFR-18, IADR-0693）。購読側は購読の exchange を宣言しないので、configure の exchange は発行するものだけにする。
 */
function patternsFor(d, { cloud = false } = {}) {
  const q = `${reEscape(d.queuePrefix)}.*`;
  const ex = (xs) => xs.map(reEscape);
  const both = [...new Set([...d.subscribe, ...d.publish])].sort();
  return {
    // 宣言（キュー・exchange）: 自分のキュー・応答キュー・共有のデッドレター・購読と発行の exchange。
    //   購読の exchange を含めるのは、購読側の Wolverine が起動時に exchange を passive でなく宣言するためである
    //   （基盤の BindPlatformQueue の DeclareExchange・取引ユニットの規約ルーティング。AutoProvision）。RabbitMQ は在る
    //   exchange の宣言し直しにも configure を求める（実ブローカで確かめた。IADR-0691）。ローカルの行はこのまま。
    //   NFR-18, IADR-0693: クラウドの写しでは購読側が受動で宣言する（AST の IADR-0628）ので、発行する exchange だけにする。
    configure: alt([q, RESPONSE_PATTERN, reEscape(DEAD_LETTER), ...ex(cloud ? d.publish : both)]),
    // 書く: 自分のキュー（束ねる側）・共有のデッドレター（宣言の x-dead-letter-exchange と束ね）・発行の exchange・
    //   既定の exchange（戻し。NFR-18, IADR-0691: 受信キューを持つ利用者だけ）。
    write: alt([q, reEscape(DEAD_LETTER), ...(d.hasListener ? [reEscape(DEFAULT_EXCHANGE)] : []), ...ex(d.publish)]),
    // 読む: 自分のキュー（購読）・応答キュー・共有のデッドレター（束ね）・購読の exchange（束ね）。
    read: alt([q, RESPONSE_PATTERN, reEscape(DEAD_LETTER), ...ex(d.subscribe)]),
  };
}

/**
 * 導出から「その名前に対して要る操作」を列挙する（試験と報告が使う。RabbitMQ の権限の表に従う）:
 *   queue.declare=configure(キュー)・x-dead-letter-exchange 付きは write(DLX)／read(キュー)、queue.bind=write(キュー)＋read(exchange)、
 *   exchange.declare=configure、basic.publish=write(exchange)、basic.consume=read(キュー)。
 */
function requiredAccess(d, { cloud = false } = {}) {
  const req = [];
  const add = (perm, name, why) => req.push({ perm, name, why });
  const qn = (t) => `${d.queuePrefix}${d.unit === 'ai-stock-trading' ? t.split('.').pop() : t}`;
  for (const t of d.subscribe) {
    const q = qn(t);
    add('configure', q, `${t} の受信キューの宣言`);
    add('read', q, `${t} の受信キューの購読`);
    add('write', q, `${t} の受信キューの束ね`);
    // NFR-18, IADR-0693: クラウドでは購読側は購読の exchange を受動で宣言する（受動の宣言は configure を要らない）。
    if (!cloud) add('configure', t, `${t} の exchange の宣言`);
    add('read', t, `${t} の exchange への束ね`);
    if (d.deadLetterQueues) {
      add('configure', `${q}_error`, `${t} のデッドレターキューの宣言`);
      add('write', `${q}_error`, `${t} のデッドレター（x-dead-letter-exchange・束ね）`);
      add('read', `${q}_error`, `${t} のデッドレターキューの束ね`);
    } else {
      add('write', DEAD_LETTER, `${t} の受信キューの x-dead-letter-exchange`);
    }
  }
  for (const t of d.publish) {
    add('configure', t, `${t} の exchange の宣言`);
    add('write', t, `${t} の発行`);
  }
  add('configure', DEAD_LETTER, '共有のデッドレターの宣言');
  add('write', DEAD_LETTER, '共有のデッドレターの束ね');
  add('read', DEAD_LETTER, '共有のデッドレターの束ね');
  add('configure', `${RESPONSE_PREFIX}00000000-0000-0000-0000-000000000000`, '応答キューの宣言');
  add('read', `${RESPONSE_PREFIX}00000000-0000-0000-0000-000000000000`, '応答キューの購読');
  if (d.hasListener) add('write', DEFAULT_EXCHANGE, 'リスナーの停止のときの戻し（既定の exchange）');
  return req;
}

function derive(repoRoot = REPO_ROOT, { requireAst = process.env.RABBITMQ_PERMISSIONS_REQUIRE_AST === '1', withAst = true } = {}) {
  const users = readUsers(repoRoot);
  const msp = users.filter((r) => r.namespace === 'microservices-platform');
  const ast = users.filter((r) => r.namespace === 'ai-stock-trading');
  const other = users.filter((r) => !['microservices-platform', 'ai-stock-trading'].includes(r.namespace));
  if (other.length) fail(`${USERS_REL}: 名前空間を知らない行: ${other.map((r) => r.user).join(', ')}`);
  assertMspNamingRules(repoRoot);
  const derived = msp.map((r) => deriveMsp(r, repoRoot));
  const root = withAst ? astRoot(repoRoot) : null;
  let astDerived = null;
  if (root) astDerived = deriveAst(ast, root);
  else if (requireAst) fail('AST の写し（src/ai-stock-trading か AST_ROOT）が無い（RABBITMQ_PERMISSIONS_REQUIRE_AST=1）');
  return { users, derived: astDerived ? [...derived, ...astDerived] : derived, astRoot: root, astRows: ast };
}

const HEADER = [
  '# NFR-18, IADR-0681: 生成物（手で直さない）。node scripts/gen-rabbitmq-permissions.js --write が作り、同じスクリプト（既定の照合）と',
  '#   scripts/rabbitmq-permissions.test.js が、コード（Wolverine の配線）から導いた値と突き合わせる。',
  '# 書式: <ユーザー名> <configure> <write> <read>（RabbitMQ の set_permissions の 3 つの正規表現。vhost は 1 つ）。',
  '# 読み手: deploy/local/rabbitmq-setup/reconcile-users.sh（RABBITMQ_PERMISSIONS=narrow のとき適用・既定は差分の報告だけ）と、',
  '#   ブローカの起動の直後の復元（ConfigMap rabbitmq-permissions に写したもの）。',
];

function render(rows) {
  return `${HEADER.join('\n')}\n${rows.map((r) => `${r.user} ${r.configure} ${r.write} ${r.read}`).join('\n')}\n`;
}

/**
 * ［2026-11-07 / IADR-0689 決定 12］クラウドの最小構成（deploy/cloud）のブローカは段 2（narrow）で動く。取引ユニットの行だけを
 * ConfigMap rabbitmq-permissions（deploy/cloud/infra/rabbitmq-permissions.yaml。生成物）に写し、起動の直後の復元がそれを当てる。
 * deploy/cloud/infra は変換を持たない kustomize なので（IADR-0674 決定 1）、ConfigMap を生成物としてコミットする。
 */
const CLOUD_PERMS_REL = 'deploy/cloud/infra/rabbitmq-permissions.yaml';

/** `^(a|b|…)$` の選択肢（生成器が書く形だけ。入れ子の括弧は書かない）。 */
function alternatives(pattern) {
  const m = /^\^\((.*)\)\$$/.exec(pattern);
  if (!m) fail(`正規表現が ^( … )$ の形でない: ${pattern.slice(0, 80)}`);
  return m[1].split('|');
}
const unEscape = (s) => s.replace(/\\(.)/g, '$1');

/**
 * NFR-18, IADR-0693: ローカルの行（permissions.txt）からクラウドの行を作る。configure の exchange を発行するもの（write に在る exchange）だけにする。
 * 行の形は patternsFor が決める: configure = [キューの接頭辞, 応答キュー, 共有のデッドレター, …exchange]、write = [キューの接頭辞, 共有のデッドレター,
 * (既定の exchange), …発行の exchange]。行だけから作るので、AST の写しが無い checkout でも照合できる（導出からの値との一致は試験が固定する）。
 */
function cloudRow(row) {
  const conf = alternatives(row.configure);
  const write = new Set(alternatives(row.write));
  const fixed = conf.slice(0, 3);
  if (!fixed[0].endsWith('\\..*') || fixed[1] !== RESPONSE_PATTERN || fixed[2] !== reEscape(DEAD_LETTER)) {
    fail(`${row.user}: configure の先頭が（キューの接頭辞・応答キュー・共有のデッドレター）でない。cloudRow を見直す`);
  }
  return { ...row, configure: alt([...fixed, ...conf.slice(3).filter((x) => write.has(x))]) };
}

/** NFR-18, IADR-0693: 購読される exchange（クラウドの行の read の exchange。キュー・応答キュー・共有のデッドレターを除く）。 */
function subscribedExchanges(row) {
  return alternatives(row.read).slice(3).map(unEscape);
}

/**
 * NFR-18, IADR-0693: ブローカの起動の直後の初期化（restore-users.sh の rabbitmqctl import_definitions）が、利用者の権限を当てる前に
 * 宣言する exchange。取引ユニットの購読される exchange の和。形は Wolverine の AutoProvision の宣言と同じ（fanout・durable・自動削除しない・
 * 引数なし）にする（違うと発行側の宣言が PRECONDITION_FAILED で拒まれる）。vhost はクラウドの 1 つ（/）。
 */
function cloudExchangeDefinitions(rows) {
  const names = [...new Set(rows.flatMap(subscribedExchanges))].sort();
  return JSON.stringify({
    exchanges: names.map((name) => ({ name, vhost: '/', type: 'fanout', durable: true, auto_delete: false, internal: false, arguments: {} })),
  });
}

function renderCloud(rows, users) {
  const ast = new Set(users.filter((u) => u.namespace === 'ai-stock-trading').map((u) => u.user));
  const cloud = rows.filter((r) => ast.has(r.user)).map(cloudRow);
  const lines = cloud.map((r) => `    ${r.user} ${r.configure} ${r.write} ${r.read}`);
  return [
    '# NFR-18, IADR-0681, IADR-0689 決定 12: 生成物（手で直さない）。node scripts/gen-rabbitmq-permissions.js --write が',
    '#   deploy/local/rabbitmq-setup/permissions.txt の取引ユニットの行から作り、同じスクリプト（既定の照合）が突き合わせる。',
    '# クラウドのブローカは段 2（narrow）だけで動く。行の無いユーザーには権限を付けない（vhost 全体へ倒さない）。',
    '# NFR-18, IADR-0693: クラウドでは購読側が購読の exchange を宣言しないので、configure の exchange は発行するものだけ（ローカルの行との違い）。',
    '#   exchanges.json は、起動の直後の初期化が利用者の権限を当てる前に宣言する exchange（購読される exchange の和）。',
    'apiVersion: v1',
    'kind: ConfigMap',
    'metadata:',
    '  name: rabbitmq-permissions',
    '  namespace: platform-infra',
    '  labels: { app: rabbitmq }',
    'data:',
    '  permissions.txt: |',
    ...lines,
    `  exchanges.json: '${cloudExchangeDefinitions(cloud)}'`,
    '',
  ].join('\n');
}

/** permissions.txt を読む（ユーザー → { configure, write, read }）。 */
function readPermissions(repoRoot = REPO_ROOT, rel = PERMS_REL) {
  const p = path.join(repoRoot, rel);
  if (!fs.existsSync(p)) return new Map();
  const out = new Map();
  for (const l of fs.readFileSync(p, 'utf8').split('\n')) {
    if (!l.trim() || /^\s*#/.test(l)) continue;
    const f = l.trim().split(/\s+/);
    if (f.length !== 4) fail(`${rel}: 4 列でない行: ${l}`);
    if (out.has(f[0])) fail(`${rel}: ${f[0]} が 2 回ある`);
    out.set(f[0], { user: f[0], configure: f[1], write: f[2], read: f[3] });
  }
  return out;
}

/** 生成する行。AST の写しが無ければ AST の行は生成物の値を残す。 */
function generate(repoRoot = REPO_ROOT, opts = {}) {
  const { users, derived, astRoot: root } = derive(repoRoot, opts);
  const existing = readPermissions(repoRoot);
  const byUser = new Map(derived.map((d) => [d.user, { user: d.user, ...patternsFor(d) }]));
  const kept = [];
  const rows = users.map((u) => {
    if (byUser.has(u.user)) return byUser.get(u.user);
    if (existing.has(u.user)) { kept.push(u.user); return existing.get(u.user); }
    return fail(`${u.user}: 導出できず、生成物にも行が無い（AST の写しを取得して --write する）`);
  });
  return { rows, derived, kept, astRoot: root, text: render(rows), cloudText: renderCloud(rows, users) };
}

function main(argv) {
  const mode = argv.includes('--write') ? 'write' : argv.includes('--explain') ? 'explain' : 'check';
  const unknown = argv.filter((a) => !['--write', '--explain', '--check'].includes(a));
  if (unknown.length) { process.stderr.write(`ERROR: 未知の引数: ${unknown.join(' ')}\n`); return 2; }
  let g;
  try {
    g = generate();
  } catch (e) {
    if (e instanceof GenError) { process.stderr.write(`ERROR: ${e.message}\n`); return 1; }
    throw e;
  }
  if (!g.astRoot) process.stdout.write(`::notice::AST の写しが無いので、AST の ${g.kept.length} 行は生成物の値のまま（照合していない）\n`);
  if (mode === 'explain') {
    for (const d of g.derived) {
      process.stdout.write(`${d.user}（${d.source}）\n  キュー: ${d.queuePrefix}*${d.deadLetterQueues ? '（デッドレターは <キュー>_error）' : ''}\n`);
      process.stdout.write(`  購読: ${d.subscribe.join(', ') || '（なし）'}\n  発行: ${d.publish.join(', ') || '（なし）'}\n`);
    }
    return 0;
  }
  const target = path.join(REPO_ROOT, PERMS_REL);
  const cloudTarget = path.join(REPO_ROOT, CLOUD_PERMS_REL);
  if (mode === 'write') {
    fs.writeFileSync(target, g.text);
    fs.writeFileSync(cloudTarget, g.cloudText);
    process.stdout.write(`    ${PERMS_REL} と ${CLOUD_PERMS_REL} を書いた（${g.rows.length} 行）\n`);
    return 0;
  }
  const haveCloud = fs.existsSync(cloudTarget) ? fs.readFileSync(cloudTarget, 'utf8') : '';
  if (haveCloud !== g.cloudText) {
    process.stderr.write(`ERROR: ${CLOUD_PERMS_REL} が権限の生成物と食い違う。node scripts/gen-rabbitmq-permissions.js --write で作り直す\n`);
    return 1;
  }
  const have = fs.existsSync(target) ? fs.readFileSync(target, 'utf8') : '';
  if (have !== g.text) {
    process.stderr.write(`ERROR: ${PERMS_REL} がコードから導いた権限と食い違う。node scripts/gen-rabbitmq-permissions.js --write で作り直し、差分を確かめてコミットする\n`);
    return 1;
  }
  process.stdout.write(`    ${PERMS_REL} はコードから導いた権限と一致する（${g.rows.length} 行・AST ${g.astRoot ? '照合した' : '照合していない'}）\n`);
  return 0;
}

if (require.main === module) process.exit(main(process.argv.slice(2)));

module.exports = {
  GenError, derive, generate, patternsFor, requiredAccess, readPermissions, readUsers, render, stripComments, cloudRow, subscribedExchanges,
  cloudExchangeDefinitions,
  DEAD_LETTER, RESPONSE_PREFIX, RESPONSE_PATTERN, DEFAULT_EXCHANGE, PERMS_REL, USERS_REL, CLOUD_PERMS_REL, renderCloud,
};
