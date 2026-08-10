#!/usr/bin/env node
'use strict';
/*
 * check-openapi-dto-drift.js
 * NFR / issue #525: `docs/api/openapi.yaml` の `components.schemas` と、同名の C# 契約 record の
 * **プロパティ集合**を突き合わせる。
 *
 * ── なぜ置くか（同型の事故が 4 回起きている）
 * `CLAUDE.md`「検査器・規約の追加は**同型の事故が 2 回起きたら**」。契約（OpenAPI）が実装（C#）と
 * 食い違ったまま残る事故は、リポジトリ自身の記録で少なくとも 4 回起きている:
 *   - #118: パスが 3 か所誤っていた（`/search` → 実体は `/bff/search` 等。`openapi.yaml` の監査注記）
 *   - #506: `AiAnswerDto.citations` の型が誤っていた（`SearchResultDto[]` → 実体は `CitationDto[]`）
 *   - #520: 応答スキーマに `required` が無く、生成型が全プロパティ省略可になっていた
 *   - #525: `AccessScopeResponse` に `granted` が無く、**deny-by-default と全件許可が契約から
 *     区別できなかった**（本検査器の起点）
 * いずれも**人手の監査で偶然見つかった**。#520 は §未決事項 4 で「C# → OpenAPI の追随は人手のまま」
 * と構造的な穴として申し送っていた。**その穴をここで塞ぐ。**
 *
 * ── 何を見て、何を見ないか
 * 見るのは**プロパティ名の集合**だけである。型は見ない —— C# の `List<CitationDto>` と OpenAPI の
 * `array/$ref` の対応は表現が 1 対 1 でなく、判定器を書くと誤検出が保守コストを上回る（#506 の型誤りは
 * 本検査器では捕まらない。**正直に書いておく**）。名前の欠落・余剰は表現に依らず判定できる。
 *
 * ── `required` の規則は**要求側と応答側で違う**（#658 / [[IADR-0162]]）
 * [[IADR-0132]] の表題は「**応答スキーマ**の `required` は C# の非 null 性から起こす」であり、
 * 決定 2 の系は「**要求スキーマの `default` は落とさない**——応答側とは別の話である」と明示している。
 * B1 の理屈（「C# の既定値はシリアライズの省略と無関係で、`System.Text.Json` は必ず出力する」）は
 * **サーバが書き出す側でしか成り立たない**。要求側では C# の既定値こそが
 * 「クライアントが送らなかったときの値」であり、`required` を足すと**送信を強制して既定値を殺す**。
 *
 * 初版（#525）はこれを区別せず A1＋B1 を全スキーマへ当てており、**要求側 5 件を偽の債務として
 * ラチェットへ据え置いていた**。本版は `openapi.yaml` から到達性を導いて規則を分ける:
 *   - **要求側にのみ**到達するスキーマ: `required` ⟺ 非 null **かつ既定値を持たない**
 *   - それ以外（応答側に到達する／どちらからも到達しない）: 従来どおり A1＋B1
 * **緩む方向へ倒れるのは「要求側にのみ到達する」と確定したときだけ**である（安全側の既定）。
 * `wrongly-required`（nullable なのに required）は**両側で見る** —— 嘘はどちらでも嘘である。
 *
 * ── 意図的な差は allowlist で通す
 * `scripts/openapi-dto-drift-allowlist.json` に **理由つきで**宣言する。理由の無いエントリは
 * 自己試験が落とす —— 「黙って除外した」を残さないためである
 * （`.claude/rules/traceability.md` 母集合の規則 6）。
 *
 * ── パースで気をつけた点（すべて実データまたは投入試験で確認した。自己試験に 1 件ずつ対応がある）
 *   1. **本体つき record**（`record X(...) { ... }`）がある。`AiAnswerDto` は位置引数 5 つに加えて
 *      本体に `public Guid AnswerId { get; init; }` を持つ。**本体を読まないと 1 つ落ちる。**
 *   2. **位置引数にジェネリクスと既定値が混ざる**（`Dictionary<string, List<string>>? X = null`）。
 *      `,` での素朴な分割は `Dictionary<string, ...>` の中で割れるので**深さを数えて**切る。
 *   3. **1 ファイルに複数の record が並ぶ**。`AccessScopeDto.cs` は 4 つ持つ。
 *   4. **本体の `static` / `const` は拾わない**（偽陽性。`System.Text.Json` は直列化しない）。
 *   5. **`required` は折り返される**（偽陽性。prettier が長い配列を複数行にする。`DataSourceDto` で
 *      実際に踏み、10 件を偽の債務としてラチェットへ据え置きかけた）。
 *   6. `record struct` / `readonly record struct` も拾う。ジェネリック record は**意図的に拾わない**。
 *
 * **4 と 5 は偽陽性であり、見逃しより重い** —— 検査器が無関係な PR を誤って落とすからである。
 * 新しい書き方を足すときは、まず**偽陽性の側**を投入試験で確かめること。
 *
 * 使い方:
 *   node scripts/check-openapi-dto-drift.js            # 検査（差異があれば exit 1）
 *   node scripts/check-openapi-dto-drift.js --self-test # パーサの自己試験
 */

const fs = require('fs');
const path = require('path');

const REPO = path.resolve(__dirname, '..');
const OPENAPI = path.join(REPO, 'docs/api/openapi.yaml');
const ALLOWLIST = path.join(__dirname, 'openapi-dto-drift-allowlist.json');
// 契約 DTO の置き場所。`src/README.md` のユニット構成に従う（`ai-stock-trading` は別プロジェクト）。
const DTO_ROOTS = [
  'src/platform/backend/Shared/Platform.Shared.Contracts/Dtos',
  'src/knowledge/backend/Shared/Knowledge.Contracts/Dtos',
];

// ── OpenAPI 側 ────────────────────────────────────────────────

// `components.schemas` を読む。生成物ではないためインデントは安定している。
function collectSchemas(yamlText) {
  const lines = yamlText.split('\n');
  let i = lines.indexOf('  schemas:');
  if (i < 0) return {};
  const schemas = {};
  let cur = null;
  let inProps = false;
  for (i++; i < lines.length; i++) {
    const line = lines[i];
    if (/^\S/.test(line)) break; // components を抜けた
    let m = line.match(/^ {4}([A-Za-z0-9_]+):\s*$/);
    if (m) {
      cur = m[1];
      schemas[cur] = { props: [], required: [] };
      inProps = false;
      continue;
    }
    if (!cur) continue;
    m = line.match(/^ {6}required:\s*\[(.*)\]\s*$/);
    if (m) {
      schemas[cur].required = m[1].split(',').map((s) => s.trim()).filter(Boolean);
      continue;
    }
    // ★ **複数行の `required` を読み落とさない。** prettier が長い配列を折り返すため、
    // 1 行形式だけを読むと**「required に無い」と誤って報告する**（`DataSourceDto` で実際に踏んだ。
    // 10 件を「是正待ちの債務」としてラチェットへ据え置きかけた）。
    // 折り返し形（`[` 改行 …）と YAML のブロック形（`- name`）の両方を読む。
    if (/^ {6}required:\s*$/.test(line)) {
      const items = [];
      for (let j = i + 1; j < lines.length; j++) {
        const l = lines[j];
        if (/^ {6}\S/.test(l) || /^\S/.test(l)) break; // 同じ深さの別キーへ出た
        const flow = l.match(/^\s*([A-Za-z0-9_]+)\s*,?\s*$/);
        const block = l.match(/^\s*-\s*([A-Za-z0-9_]+)\s*$/);
        if (block) items.push(block[1]);
        else if (flow) items.push(flow[1]);
        else if (/^\s*\]\s*$/.test(l)) { i = j; break; }
        i = j;
      }
      schemas[cur].required = items;
      continue;
    }
    if (/^ {6}properties:\s*$/.test(line)) {
      inProps = true;
      continue;
    }
    // 同じ深さの別キー（description / type / required ...）が来たら properties を抜ける。
    if (/^ {6}\S/.test(line)) {
      inProps = false;
      continue;
    }
    if (inProps) {
      m = line.match(/^ {8}([A-Za-z0-9_]+):/);
      if (m) schemas[cur].props.push(m[1]);
    }
  }
  return schemas;
}

// ── 到達性（要求側 / 応答側） ─────────────────────────────────
//
// #658 / [[IADR-0162]]: `required` の規則を分けるために、各スキーマが
// `requestBody:` から到達するか `responses:` から到達するかを `openapi.yaml` 自身から導く。
//
// **入れ子を推移的にたどる必要がある。** `AnalysisDataRange` はどの `requestBody:` からも
// 直接参照されておらず、`AnalysisTaskRequest.range` の下にぶら下がっているだけである
// ——直接参照だけを見ると「要求側と判らない」ので、従来の（応答側の）規則が当たってしまう。
function collectReachability(yamlText) {
  const lines = yamlText.split('\n');
  const indentOf = (l) => l.match(/^ */)[0].length;
  const REF = /\$ref:\s*["']?#\/components\/schemas\/([A-Za-z0-9_]+)["']?/;

  // 1. paths: を歩き、requestBody: / responses: のサブツリーで現れた $ref を分ける。
  const reqSeed = new Set();
  const resSeed = new Set();
  let i = lines.indexOf('paths:');
  if (i >= 0) {
    let mode = null;
    let modeIndent = -1;
    for (i++; i < lines.length; i++) {
      const line = lines[i];
      if (/^\S/.test(line)) break; // paths を抜けた
      if (!line.trim()) continue;
      const ind = indentOf(line);
      if (mode !== null && ind <= modeIndent) {
        mode = null;
        modeIndent = -1;
      }
      if (/^\s*requestBody:\s*$/.test(line)) {
        mode = 'req';
        modeIndent = ind;
        continue;
      }
      if (/^\s*responses:\s*$/.test(line)) {
        mode = 'res';
        modeIndent = ind;
        continue;
      }
      const m = line.match(REF);
      if (!m || mode === null) continue;
      (mode === 'req' ? reqSeed : resSeed).add(m[1]);
    }
  }

  // 2. components.schemas 内の $ref グラフ。
  const graph = {};
  let cur = null;
  let j = lines.indexOf('  schemas:');
  if (j >= 0) {
    for (j++; j < lines.length; j++) {
      const line = lines[j];
      if (/^\S/.test(line)) break;
      const m = line.match(/^ {4}([A-Za-z0-9_]+):\s*$/);
      if (m) {
        cur = m[1];
        graph[cur] = graph[cur] || new Set();
        continue;
      }
      if (!cur) continue;
      const r = line.match(REF);
      if (r) graph[cur].add(r[1]);
    }
  }

  // 3. 推移閉包。
  const close = (seed) => {
    const out = new Set();
    const stack = [...seed];
    while (stack.length) {
      const n = stack.pop();
      if (out.has(n)) continue;
      out.add(n);
      for (const c of graph[n] || []) if (!out.has(c)) stack.push(c);
    }
    return out;
  };
  const fromRequest = close(reqSeed);
  const fromResponse = close(resSeed);

  // **要求側にのみ到達するものだけ**を返す。応答側に 1 度でも到達するもの、
  // どちらからも到達しないものは従来どおり A1＋B1 を当てる（IADR-0132 決定 5 と整合）。
  const requestOnly = new Set([...fromRequest].filter((n) => !fromResponse.has(n)));
  return { fromRequest, fromResponse, requestOnly };
}

// ── C# 側 ────────────────────────────────────────────────────

// 深さを数えて `,` で切る（`Dictionary<string, List<string>>` の中で割れないため）。
function splitTopLevel(text) {
  const out = [];
  let depth = 0;
  let buf = '';
  for (const ch of text) {
    if (ch === '<' || ch === '(' || ch === '[') depth++;
    else if (ch === '>' || ch === ')' || ch === ']') depth--;
    if (ch === ',' && depth === 0) {
      out.push(buf);
      buf = '';
    } else buf += ch;
  }
  if (buf.trim()) out.push(buf);
  return out;
}

function stripComments(src) {
  return src.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/[^\n]*/g, '');
}

// `public record Name(...) [{ ... }]` を全部拾う。位置引数と本体の public プロパティを合わせる。
function collectRecords(csText) {
  const src = stripComments(csText);
  const records = {};
  // `sealed record` / `readonly record struct` / `record struct` を含める。
  // **ジェネリック record（`record Foo<T>(...)`）は意図的に拾わない** —— OpenAPI のスキーマ名に
  // 対応するものが無いので、拾っても照合相手が存在しない。
  const re = /public\s+(?:sealed\s+|readonly\s+)?record\s+(?:struct\s+)?([A-Za-z0-9_]+)\s*\(/g;
  let m;
  while ((m = re.exec(src))) {
    const name = m[1];
    // 位置引数: 対応する `)` まで深さを数える。
    let i = re.lastIndex;
    let depth = 1;
    let params = '';
    for (; i < src.length && depth > 0; i++) {
      const ch = src[i];
      if (ch === '(') depth++;
      else if (ch === ')') depth--;
      if (depth > 0) params += ch;
    }
    const props = splitTopLevel(params)
      .map((p) => {
        const tokens = p.split('=')[0].trim().split(/\s+/);
        const prop = tokens[tokens.length - 1];
        const type = tokens.slice(0, -1).join(' ').trim();
        // IADR-0132 論点 A1: `?` の付かないメンバーが非 null ＝ required の候補である。
        // #658 / IADR-0162: 既定値の有無は**要求側でだけ**意味を持つ（省略してよい印になる）。
        // 応答側では B1 のとおり無関係である（`System.Text.Json` は既定値でも出力する）。
        return {
          name: prop,
          nonNullable: type.length > 0 && !type.endsWith('?'),
          hasDefault: p.includes('='),
        };
      })
      .filter((p) => /^[A-Za-z_][A-Za-z0-9_]*$/.test(p.name));

    // 本体つき record なら `{ ... }` の中の public プロパティも足す（`AiAnswerDto.AnswerId`）。
    const rest = src.slice(i);
    const bodyStart = rest.match(/^\s*\{/);
    if (bodyStart) {
      let d = 0;
      let body = '';
      for (let j = rest.indexOf('{'); j < rest.length; j++) {
        const ch = rest[j];
        if (ch === '{') d++;
        else if (ch === '}') d--;
        body += ch;
        if (d === 0) break;
      }
      const pr = /public\s+([^;{}()]+?)\s+([A-Za-z0-9_]+)\s*\{\s*get\s*;[^}]*\}\s*(=)?/g;
      let pm;
      while ((pm = pr.exec(body))) {
        const modifiersAndType = pm[1].trim();
        // **`static` / `const` は直列化されない。** `System.Text.Json` はインスタンスの
        // プロパティしか書き出さないので、契約へ出すことを要求してはならない ——
        // 要求すると**無関係な PR の CI を誤って落とす**（実データにはまだ無いが、
        // record 本体に定数を 1 つ置いた瞬間に起きる）。
        if (/\b(?:static|const)\b/.test(modifiersAndType)) continue;
        props.push({
          name: pm[2],
          nonNullable: !modifiersAndType.endsWith('?'),
          hasDefault: pm[3] === '=',
        });
      }
    }
    records[name] = props;
  }
  return records;
}

function listCsFiles(dir, acc = []) {
  if (!fs.existsSync(dir)) return acc;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) listCsFiles(p, acc);
    else if (e.name.endsWith('.cs')) acc.push(p);
  }
  return acc;
}

// ── 突き合わせ ────────────────────────────────────────────────

const lowerFirst = (s) => s.charAt(0).toLowerCase() + s.slice(1);

function findDrift(schemas, records, allowlist, requestOnly = new Set()) {
  const allowed = new Set((allowlist.entries || []).map((e) => `${e.schema}.${e.property}`));
  // `requiredExceptions` は **`required` の判定だけ**を外す（#658 / [[IADR-0162]]）。
  // `entries` と分けているのは、`entries` が**プロパティ集合の突合ごと**外してしまうためである
  // —— `entries` に入れた項目は契約から丸ごと消えても `missing-in-openapi` で検出されない。
  // 「プロパティは在るが `required` の判定だけ意図的に違う」ものはこちらへ書く。
  const requiredExceptions = new Set(
    (allowlist.requiredExceptions || []).map((e) => `${e.schema}.${e.property}`)
  );
  const drift = [];
  for (const [name, schema] of Object.entries(schemas)) {
    const csProps = records[name];
    if (!csProps) continue; // 同名の契約 record が無いものは対象外（別レイヤの DTO・RFC7807 等）
    const csByJson = new Map(csProps.map((p) => [lowerFirst(p.name), p]));
    for (const p of csByJson.keys()) {
      if (!schema.props.includes(p) && !allowed.has(`${name}.${p}`)) {
        drift.push({ schema: name, property: p, kind: 'missing-in-openapi' });
      }
    }
    for (const p of schema.props) {
      if (!csByJson.has(p) && !allowed.has(`${name}.${p}`)) {
        drift.push({ schema: name, property: p, kind: 'missing-in-csharp' });
      }
    }
    // 双方に在るものだけ `required` を見る（片側欠落は上で報告済み）。
    const isRequestOnly = requestOnly.has(name);
    for (const [p, cs] of csByJson) {
      if (!schema.props.includes(p)) continue;
      const key = `${name}.${p}`;
      if (requiredExceptions.has(key) || allowed.has(key)) continue;
      const isRequired = schema.required.includes(p);
      if (cs.nonNullable && !isRequired) {
        // #658 / IADR-0162: **要求側にのみ到達するスキーマでは、既定値つきメンバーは省略可が正**である。
        // `required` を足すと送信を強制し、C# の既定値（＝「送らなかったときの値」）を殺す。
        // 応答側では B1 のとおり既定値の有無に関わらず `required` である。
        if (isRequestOnly && cs.hasDefault) continue;
        drift.push({ schema: name, property: p, kind: 'missing-in-required' });
      } else if (!cs.nonNullable && isRequired) {
        drift.push({ schema: name, property: p, kind: 'wrongly-required' });
      }
    }
  }
  return drift;
}

function formatReport(drift) {
  const lines = [
    `[check-openapi-dto-drift] 契約（openapi.yaml）と C# DTO の不一致 ${drift.length} 件を検出しました:`,
    '',
  ];
  const why = {
    'missing-in-openapi': 'C# 契約に在るが openapi.yaml の properties に無い',
    'missing-in-csharp': 'openapi.yaml に在るが C# 契約に無い',
    'missing-in-required': 'C# が非 null なのに openapi.yaml の required に無い（IADR-0132 論点 B の B1）',
    'wrongly-required': 'C# が nullable なのに openapi.yaml の required に在る（「嘘の必須」）',
  };
  for (const d of drift) lines.push(`  ✗ ${d.schema}.${d.property}: ${why[d.kind]}`);
  lines.push('');
  lines.push('  プロパティ自体を契約へ出さないのが正しいなら scripts/openapi-dto-drift-allowlist.json の');
  lines.push('  entries へ**理由つきで**追加すること（プロパティ集合の突合ごと外れる）。');
  lines.push('  プロパティは在るが required の判定だけ意図的に違うなら requiredExceptions へ書くこと。');
  lines.push('  **「いま合っていないから」は理由ではない。** 契約と C# のどちらが正しいかを先に決めること。');
  return lines.join('\n');
}

// ── 自己試験 ──────────────────────────────────────────────────

function selfTest() {
  const failures = [];
  const check = (label, fn) => {
    try {
      fn();
    } catch (e) {
      failures.push(`${label}: ${e.message}`);
    }
  };
  const eq = (a, b, msg) => {
    const x = JSON.stringify(a);
    const y = JSON.stringify(b);
    if (x !== y) throw new Error(`${msg || ''} 期待 ${y} / 実際 ${x}`);
  };

  const names = (props) => props.map((p) => p.name);

  check('位置引数を拾う', () => {
    const r = collectRecords('public record Foo(string A, int B);');
    eq(names(r.Foo), ['A', 'B']);
  });

  check('ジェネリクスの中の , で割れない', () => {
    const r = collectRecords('public record Foo(Dictionary<string, List<string>>? A, int B);');
    eq(names(r.Foo), ['A', 'B']);
    eq(r.Foo[0].nonNullable, false, 'Dictionary<...>? は nullable');
  });

  check('既定値を剥がす', () => {
    const r = collectRecords('public record Foo(bool A = false, string B = "x");');
    eq(names(r.Foo), ['A', 'B']);
    eq(r.Foo[0].nonNullable, true, '既定値の有無は非 null 性と無関係（IADR-0132 論点 B の B1）');
  });

  check('nullable を見分ける', () => {
    const r = collectRecords('public record Foo(string? A, string B);');
    eq(r.Foo.map((p) => p.nonNullable), [false, true]);
  });

  check('本体つき record の init プロパティを拾う', () => {
    const r = collectRecords(
      'public record Foo(string A)\n{\n    public Guid B { get; init; } = Guid.NewGuid();\n}'
    );
    eq(names(r.Foo), ['A', 'B']);
    eq(r.Foo[1].nonNullable, true);
  });

  // ★ **false positive を防ぐ側**。record 本体の static / const は直列化されないので、
  // 契約へ出すことを要求してはならない。要求すると無関係な PR の CI を誤って落とす。
  check('本体の static / const プロパティは拾わない', () => {
    const r = collectRecords(
      'public record Foo(string A)\n{\n' +
        '    public static string Known { get; } = "k";\n' +
        '    public Guid B { get; init; }\n}'
    );
    eq(names(r.Foo), ['A', 'B']);
  });

  check('record struct / readonly record struct を拾う', () => {
    eq(names(collectRecords('public record struct Foo(string A);').Foo), ['A']);
    eq(names(collectRecords('public readonly record struct Bar(int X);').Bar), ['X']);
  });

  check('1 ファイルの複数 record を拾う', () => {
    const r = collectRecords('public record A(string X);\npublic record B(int Y);');
    eq(Object.keys(r).sort(), ['A', 'B']);
  });

  check('コメント中の record を拾わない', () => {
    const r = collectRecords('// public record Ghost(string X);\npublic record Real(int Y);');
    eq(Object.keys(r), ['Real']);
  });

  check('スキーマの properties と required を読む', () => {
    const y = [
      'components:',
      '  schemas:',
      '    Foo:',
      '      type: object',
      '      required: [a, b]',
      '      properties:',
      '        a: { type: string }',
      '        b:',
      '          type: array',
      '          items: { type: string }',
      '    Bar:',
      '      type: object',
      '      properties:',
      '        c: { type: string }',
      '',
    ].join('\n');
    const s = collectSchemas(y);
    eq(s.Foo.props, ['a', 'b']);
    eq(s.Foo.required, ['a', 'b']);
    eq(s.Bar.props, ['c']);
  });

  // ★ **偽陽性を生んだ実際の形**。prettier が長い `required` を折り返すため、1 行形式だけを
  // 読むと「required に無い」と誤って報告する。`DataSourceDto` の 10 件がこれで、
  // **是正待ちの債務として baseline へ据え置きかけた**（実データは最初から一致していた）。
  check('折り返された required（複数行の flow 形）を読む', () => {
    const y = [
      'components:',
      '  schemas:',
      '    Foo:',
      '      required:',
      '        [',
      '          a,',
      '          b,',
      '        ]',
      '      properties:',
      '        a: { type: string }',
      '        b: { type: string }',
      '',
    ].join('\n');
    const s = collectSchemas(y);
    eq(s.Foo.required, ['a', 'b']);
    eq(s.Foo.props, ['a', 'b']);
  });

  check('YAML のブロック形の required を読む', () => {
    const y = [
      'components:',
      '  schemas:',
      '    Foo:',
      '      required:',
      '        - a',
      '        - b',
      '      properties:',
      '        a: { type: string }',
      '',
    ].join('\n');
    eq(collectSchemas(y).Foo.required, ['a', 'b']);
  });

  check('実データ: DataSourceDto の required を 10 件読める（偽陽性の回帰）', () => {
    const s = collectSchemas(fs.readFileSync(OPENAPI, 'utf8'));
    if (!s.DataSourceDto) throw new Error('DataSourceDto を読めていない');
    eq(s.DataSourceDto.required.length, 10, 'DataSourceDto.required の件数');
  });

  check('入れ子の items 内キーを properties と誤認しない', () => {
    const y = [
      'components:',
      '  schemas:',
      '    Foo:',
      '      properties:',
      '        a:',
      '          type: array',
      '          items:',
      '            type: object',
      '            properties:',
      '              inner: { type: string }',
      '',
    ].join('\n');
    eq(collectSchemas(y).Foo.props, ['a']);
  });

  const cs = (...defs) => defs.map(([name, nonNullable = true]) => ({ name, nonNullable }));

  check('乖離を検出する（両向き）', () => {
    const d = findDrift(
      { Foo: { props: ['a', 'extra'], required: ['a', 'extra'] } },
      { Foo: cs(['A'], ['Missing']) },
      { entries: [] }
    );
    eq(d.map((x) => `${x.kind}:${x.property}`).sort(), [
      'missing-in-csharp:extra',
      'missing-in-openapi:missing',
    ]);
  });

  check('allowlist で意図的な差を通す', () => {
    const d = findDrift(
      { Foo: { props: ['a'], required: ['a'] } },
      { Foo: cs(['A'], ['Scope']) },
      { entries: [{ schema: 'Foo', property: 'scope', reason: 'ため' }] }
    );
    eq(d, []);
  });

  check('非 null なのに required に無いと落ちる（#525 の変異 B）', () => {
    const d = findDrift(
      { Foo: { props: ['a'], required: [] } },
      { Foo: cs(['A', true]) },
      { entries: [] }
    );
    eq(d.map((x) => x.kind), ['missing-in-required']);
  });

  check('nullable なのに required に在ると落ちる（嘘の必須）', () => {
    const d = findDrift(
      { Foo: { props: ['a'], required: ['a'] } },
      { Foo: cs(['A', false]) },
      { entries: [] }
    );
    eq(d.map((x) => x.kind), ['wrongly-required']);
  });

  // ── #658 / IADR-0162: 要求側 / 応答側で `required` の規則が違う ──────────

  const csd = (...defs) =>
    defs.map(([name, nonNullable = true, hasDefault = false]) => ({ name, nonNullable, hasDefault }));

  check('要求側にのみ到達するスキーマでは、既定値つきメンバーを required に要求しない', () => {
    const d = findDrift(
      { Foo: { props: ['a'], required: [] } },
      { Foo: csd(['A', true, true]) },
      { entries: [] },
      new Set(['Foo'])
    );
    eq(d, []);
  });

  // ★ **緩めすぎていない側**。要求側でも「既定値が無ければ必須」は効いていなければならない。
  // これが無いと、要求スキーマの必須漏れを丸ごと見逃す判定器になる。
  check('要求側でも、既定値を持たない非 null メンバーは required を要求する', () => {
    const d = findDrift(
      { Foo: { props: ['a'], required: [] } },
      { Foo: csd(['A', true, false]) },
      { entries: [] },
      new Set(['Foo'])
    );
    eq(
      d.map((x) => x.kind),
      ['missing-in-required']
    );
  });

  check('応答側では既定値があっても required を要求する（IADR-0132 決定 2 = B1）', () => {
    const d = findDrift(
      { Foo: { props: ['a'], required: [] } },
      { Foo: csd(['A', true, true]) },
      { entries: [] },
      new Set() // 要求側にのみ到達するものは無い
    );
    eq(
      d.map((x) => x.kind),
      ['missing-in-required']
    );
  });

  check('「嘘の必須」は要求側でも見る（緩和の向きへ倒さない）', () => {
    const d = findDrift(
      { Foo: { props: ['a'], required: ['a'] } },
      { Foo: csd(['A', false, true]) },
      { entries: [] },
      new Set(['Foo'])
    );
    eq(
      d.map((x) => x.kind),
      ['wrongly-required']
    );
  });

  check('requiredExceptions は required の判定だけを外す（プロパティ集合は見続ける）', () => {
    // required の判定は外れる。
    eq(
      findDrift(
        { Foo: { props: ['a'], required: ['a'] } },
        { Foo: csd(['A', false]) },
        { requiredExceptions: [{ schema: 'Foo', property: 'a', reason: 'ため' }] }
      ),
      []
    );
    // ★ が、プロパティが契約から消えれば**落ちる**（entries との違い）。
    eq(
      findDrift(
        { Foo: { props: [], required: [] } },
        { Foo: csd(['A', false]) },
        { requiredExceptions: [{ schema: 'Foo', property: 'a', reason: 'ため' }] }
      ).map((x) => x.kind),
      ['missing-in-openapi']
    );
  });

  check('collectReachability: requestBody / responses を見分け、入れ子を推移的にたどる', () => {
    const y = [
      'paths:',
      '  /a:',
      '    post:',
      '      requestBody:',
      '        content:',
      '          application/json:',
      '            schema:',
      '              $ref: "#/components/schemas/Req"',
      '      responses:',
      '        "200":',
      '          content:',
      '            application/json:',
      '              schema:',
      '                $ref: "#/components/schemas/Res"',
      'components:',
      '  schemas:',
      '    Req:',
      '      properties:',
      '        nested:',
      '          $ref: "#/components/schemas/Nested"',
      '    Nested:',
      '      properties:',
      '        x: { type: string }',
      '    Res:',
      '      properties:',
      '        y: { type: string }',
      '    Orphan:',
      '      properties:',
      '        z: { type: string }',
      '',
    ].join('\n');
    const r = collectReachability(y);
    eq([...r.requestOnly].sort(), ['Nested', 'Req'], '入れ子も要求側とみなす');
    // **どちらからも到達しないものは requestOnly に入れない**（安全側 ＝ 従来どおり B1 を当てる）。
    if (r.requestOnly.has('Orphan')) throw new Error('Orphan を緩めてはならない');
    if (r.requestOnly.has('Res')) throw new Error('応答側を緩めてはならない');
  });

  // ★ 実データ。規則が「たまたま」効いているのではなく、狙った 5 件に当たっていること。
  check('実データ: 要求側にのみ到達するスキーマを取れる', () => {
    const r = collectReachability(fs.readFileSync(OPENAPI, 'utf8'));
    for (const n of ['SearchRequest', 'AnalysisTaskRequest', 'AnalysisDataRange', 'CompletionApiRequest', 'EmbedApiRequest']) {
      if (!r.requestOnly.has(n)) throw new Error(`${n} が要求側として取れていない`);
    }
    if (r.requestOnly.has('ConversionJobDto')) throw new Error('ConversionJobDto は応答側である');
  });

  check('requiredExceptions の各エントリは理由を持つ', () => {
    const list = JSON.parse(fs.readFileSync(ALLOWLIST, 'utf8'));
    for (const e of list.requiredExceptions || []) {
      if (!e.reason || !e.reason.trim()) {
        throw new Error(`${e.schema}.${e.property} に reason が無い`);
      }
    }
  });

  // #658: ラチェットは撤去した（0 件になったため）。**空配列で残すと「また据え置いてよい」と読める。**
  check('requiredMismatchBaseline は撤去済みである', () => {
    const list = JSON.parse(fs.readFileSync(ALLOWLIST, 'utf8'));
    if ('requiredMismatchBaseline' in list) {
      throw new Error('requiredMismatchBaseline は #658 で撤去した。据え置きたい差は理由を書いて requiredExceptions へ');
    }
  });

  check('片側欠落のときは required を二重に報告しない', () => {
    const d = findDrift(
      { Foo: { props: [], required: [] } },
      { Foo: cs(['A', true]) },
      { entries: [] }
    );
    eq(d.map((x) => x.kind), ['missing-in-openapi']);
  });

  check('allowlist の各エントリは理由を持つ', () => {
    const list = JSON.parse(fs.readFileSync(ALLOWLIST, 'utf8'));
    for (const e of list.entries || []) {
      if (!e.reason || !e.reason.trim()) {
        throw new Error(`${e.schema}.${e.property} に reason が無い`);
      }
    }
  });

  if (failures.length) {
    console.error('[check-openapi-dto-drift] 自己試験 NG:');
    for (const f of failures) console.error(`  ✗ ${f}`);
    return 1;
  }
  console.log('[check-openapi-dto-drift] 自己試験 OK');
  return 0;
}

// ── 入口 ──────────────────────────────────────────────────────

function main(argv) {
  if (argv.includes('--self-test')) return selfTest();

  const yamlText = fs.readFileSync(OPENAPI, 'utf8');
  const schemas = collectSchemas(yamlText);
  const { requestOnly } = collectReachability(yamlText);
  const records = {};
  const files = DTO_ROOTS.flatMap((d) => listCsFiles(path.join(REPO, d))).sort();
  for (const f of files) Object.assign(records, collectRecords(fs.readFileSync(f, 'utf8')));

  const allowlist = fs.existsSync(ALLOWLIST)
    ? JSON.parse(fs.readFileSync(ALLOWLIST, 'utf8'))
    : { entries: [] };
  const drift = findDrift(schemas, records, allowlist, requestOnly);
  if (drift.length === 0) {
    const matched = Object.keys(schemas).filter((n) => records[n]).length;
    console.log(
      `[check-openapi-dto-drift] OK: schemas ${Object.keys(schemas).length} / C# record ${Object.keys(records).length} のうち同名 ${matched} 件のプロパティ集合が一致しています（要求側にのみ到達する ${requestOnly.size} 件は既定値つきメンバーを省略可として扱う）。`
    );
    return 0;
  }
  console.error(formatReport(drift));
  return 1;
}

module.exports = {
  collectSchemas,
  collectRecords,
  collectReachability,
  splitTopLevel,
  findDrift,
  selfTest,
};

if (require.main === module) process.exit(main(process.argv.slice(2)));
