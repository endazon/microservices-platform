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
 * ── 意図的な差は allowlist で通す
 * `scripts/openapi-dto-drift-allowlist.json` に **理由つきで**宣言する。理由の無いエントリは
 * 自己試験が落とす —— 「黙って除外した」を残さないためである
 * （`.claude/rules/traceability.md` 母集合の規則 6）。
 *
 * ── C# 側のパースで気をつけた 3 点（すべて実データで確認した）
 *   1. **本体つき record**（`record X(...) { ... }`）がある。`AiAnswerDto` は位置引数 5 つに加えて
 *      本体に `public Guid AnswerId { get; init; }` を持つ。**本体を読まないと 1 つ落ちる。**
 *   2. **位置引数にジェネリクスと既定値が混ざる**（`Dictionary<string, List<string>>? X = null`）。
 *      `,` での素朴な分割は `Dictionary<string, ...>` の中で割れるので**深さを数えて**切る。
 *   3. **1 ファイルに複数の record が並ぶ**。`AccessScopeDto.cs` は 4 つ持つ。
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
  const re = /public\s+(?:sealed\s+)?record\s+([A-Za-z0-9_]+)\s*\(/g;
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
        return { name: prop, nonNullable: type.length > 0 && !type.endsWith('?') };
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
      const pr = /public\s+([^;{}()]+?)\s+([A-Za-z0-9_]+)\s*\{\s*get\s*;/g;
      let pm;
      while ((pm = pr.exec(body))) {
        props.push({ name: pm[2], nonNullable: !pm[1].trim().endsWith('?') });
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

function findDrift(schemas, records, allowlist) {
  const allowed = new Set((allowlist.entries || []).map((e) => `${e.schema}.${e.property}`));
  // `required` の不一致は**ラチェット**である。既存 20 件は据え置き、新規混入だけを落とす
  // （リポジトリ既定の方式。backend-library-baseline / landed-subject-baseline と同じ）。
  const requiredBaseline = new Set(allowlist.requiredMismatchBaseline || []);
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
    for (const [p, cs] of csByJson) {
      if (!schema.props.includes(p)) continue;
      const key = `${name}.${p}`;
      if (requiredBaseline.has(key) || allowed.has(key)) continue;
      const isRequired = schema.required.includes(p);
      if (cs.nonNullable && !isRequired) {
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
  lines.push('  意図的な差であれば scripts/openapi-dto-drift-allowlist.json へ**理由つきで**追加すること。');
  lines.push('  既存の required 不一致（是正待ち）は同ファイルの requiredMismatchBaseline に据え置いてある。');
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

  check('required のラチェットは既存を据え置く', () => {
    const d = findDrift(
      { Foo: { props: ['a'], required: [] } },
      { Foo: cs(['A', true]) },
      { entries: [], requiredMismatchBaseline: ['Foo.a'] }
    );
    eq(d, []);
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

  const schemas = collectSchemas(fs.readFileSync(OPENAPI, 'utf8'));
  const records = {};
  const files = DTO_ROOTS.flatMap((d) => listCsFiles(path.join(REPO, d))).sort();
  for (const f of files) Object.assign(records, collectRecords(fs.readFileSync(f, 'utf8')));

  const allowlist = fs.existsSync(ALLOWLIST)
    ? JSON.parse(fs.readFileSync(ALLOWLIST, 'utf8'))
    : { entries: [] };
  const drift = findDrift(schemas, records, allowlist);
  if (drift.length === 0) {
    const matched = Object.keys(schemas).filter((n) => records[n]).length;
    console.log(
      `[check-openapi-dto-drift] OK: schemas ${Object.keys(schemas).length} / C# record ${Object.keys(records).length} のうち同名 ${matched} 件のプロパティ集合が一致しています。`
    );
    return 0;
  }
  console.error(formatReport(drift));
  return 1;
}

module.exports = { collectSchemas, collectRecords, splitTopLevel, findDrift, selfTest };

if (require.main === module) process.exit(main(process.argv.slice(2)));
