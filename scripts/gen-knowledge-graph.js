#!/usr/bin/env node
'use strict';
/*
 * gen-knowledge-graph.js
 * `docs/` の trace / trace-table ブロックと frontmatter、`.ai-context/{adr,specs}/` の frontmatter
 * （related_ids / related_specs / related_adrs / title / status）からナレッジグラフを生成する。
 *
 * 正本は計画 ADR-0048 決定 4（trace ブロック）。参考実装は project-planning
 * `tools/doc-checks/gen-knowledge-graph.js`（読むだけ・コピーしない。本リポジトリは planning に
 * 依存しないため隣接クローンか GitHub URL で読む。ADR-0048 決定 2）。生成物はコミットしない。
 *
 * ノード:
 *   - doc   : docs/**\/*.md。id = リポ相対パス（例 "docs/functional/FR-01_x.md"）
 *   - iadr  : .ai-context/adr/IADR-XXXX_*.md（README.md は除く）。id = "IADR-XXXX"
 *   - spec  : .ai-context/specs/*.md（README.md は除く）。id = 拡張子なしのベース名
 *   いずれも title / type / status を frontmatter から読む（無ければ null）。
 *
 * エッジ（kind は判別できる粒度で持つ。'等' の余地として今後キーが増えても壊れない形にしてある）:
 *   - trace-ids   : doc の trace: ブロック ids キー、または trace-table の FR/UC/SC/NFR トークン
 *   - trace-adrs  : 同 adrs キー、または trace-table の ADR（計画）トークン
 *   - trace-iadrs : 同 iadrs キー、または trace-table の IADR トークン
 *   - trace-specs : 同 specs キー（.ai-context/specs/ のベース名）
 *   - frontmatter-related : .ai-context/{adr,specs}/ の related_ids / related_specs / related_adrs
 *   trace ブロックの issues キーはグラフのエッジにしない（GitHub 上の追跡情報であり、
 *   本リポジトリ内のノードを指さないため——ADR-0048 決定 4 は 5 キーを列挙するが、ここで
 *   ノード化の対象になるのは repo 内資料を指す 4 キーだけである）。
 *
 * 【他プロジェクトの修飾子は名前をハードコードしない】ADR-0048 決定 9・利用者裁定 2026-08-21:
 *   `lib/trace-blocks.js` の `isQualifiedToken` と同じ一般規則（英数の短縮名 + `:`/`/`）で
 *   external 判定する。具体的なプロジェクト名の一覧は持たない。
 *
 * 出力:
 *   --json              { nodes: [...], edges: [...] } を stdout へ
 *   --mermaid [--scope <dir>]  Mermaid flowchart を stdout へ（--scope はノードの file パスの
 *                        前方一致でフィルタする。エッジはソースがスコープ内のものだけを描く）
 *   --check              エッジ先の in-repo 実在検査（doc / iadr / spec の 3 ノード種別のみ判定
 *                        できる。plan の FR/UC/SC/ADR・修飾付き・issue は external として件数のみ
 *                        報告し実在検査しない——本リポジトリは planning に依存しないため）。
 *                        `.ai-context/{adr,specs}/` の related_specs 値は 3 つに分岐する
 *                        （AST 版と同思想。利用者裁定 2026-08-21）:
 *                          (a) `<英数短縮名>:` / `/` 修飾付き（`feedback:...` 等）→ external
 *                          (b) 実在する repo 相対パス（`.github/*.yml` 等の非 .md 実ファイル）→ resolved
 *                          (c) それでも解決しない値 → fail にせず情報表示（件数＋一覧）のみ
 *                        docs/ の trace ブロック specs キーの実在性検査は対象外（`check-trace-blocks.js`
 *                        が担う。厳格 fail のまま変えない）。
 *
 * 実装ノート（AST 側への差分吸収ポイント）:
 *   - ノード種別・走査ルート（docs/ ＋ .ai-context/{adr,specs}/）は本リポジトリと同じ想定。
 *     AST リポジトリでも同じ 3 ルートで揃うなら本ファイルはそのまま動く。
 *   - `related_adrs` の値は「ADR（計画）」と「IADR（実装）」が**フィールド名によらず**混在する
 *     （実測: `.ai-context/adr/IADR-0014_*.md` の related_adrs は IADR-0004 等の IADR を指す）。
 *     値そのものの接頭辞（ADR- / IADR-）で判別すること。フィールド名で決め打たない。
 *   - `.ai-context/` は凍結記録であり、related_specs の post-hoc 訂正はしない運用
 *     （`.claude/rules/traceability.repo.md`「Superseded / Deprecated な ADR」節と同じ考え方）。
 *     解決できない値を fail にすると凍結記録の書き換えを強制してしまうため、(c) は常に
 *     情報表示に留める——AST 版 `gen-knowledge-graph.js` の `--check` が unresolved を
 *     「この検査では失敗として扱いません」としているのと同じ理由。
 *
 * 使い方:
 *   node scripts/gen-knowledge-graph.js --json
 *   node scripts/gen-knowledge-graph.js --mermaid --scope docs/functional
 *   node scripts/gen-knowledge-graph.js --check
 *   node scripts/gen-knowledge-graph.js --self-test
 */
const fs = require('fs');
const path = require('path');
const { mdFiles } = require('./check-doc-links.js');
const { frontMatter } = require('./check-doc-status-vocabulary.js');
const { isQualifiedToken, classifyIdToken, findTraceBlocks, findTraceTableBlocks, parseTraceBlockBody, parseTraceTableBody } =
  require('./lib/trace-blocks.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const DOCS_DIR = path.join(REPO_ROOT, 'docs');
const ADR_DIR = path.join(REPO_ROOT, '.ai-context', 'adr');
const SPECS_DIR = path.join(REPO_ROOT, '.ai-context', 'specs');

function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

function repoRel(absPath) {
  return toPosix(path.relative(REPO_ROOT, absPath));
}

function field(fm, key) {
  if (fm === null) return null;
  const m = new RegExp(`^${key}:\\s*(.+?)\\s*$`, 'm').exec(fm);
  return m ? m[1] : null;
}

// --- frontmatter の block list（`key:\n  - a\n  - b`）を読む -------------------------

function yamlListField(fm, key) {
  if (fm === null) return [];
  const re = new RegExp(`^${key}:\\s*\\n((?:^[ \\t]*-[ \\t]*.+\\n?)*)`, 'm');
  const m = re.exec(`${fm}\n`);
  if (!m) return [];
  return m[1]
    .split('\n')
    .map((l) => l.trim())
    .filter(Boolean)
    .map((l) => l.replace(/^-\s*/, '').trim())
    .map((l) => l.replace(/^["'`]|["'`]$/g, '').trim())
    .filter(Boolean);
}

/** `ADR-0001 (マイクロサービス採用)` のような値から先頭の ID トークンだけを取り出す。 */
function leadingIdToken(raw) {
  return String(raw).trim().replace(/\s*[（(].*$/, '').trim();
}

// --- ノード収集 -----------------------------------------------------------------

function docNodes(dir = DOCS_DIR) {
  const nodes = [];
  for (const fp of mdFiles(dir)) {
    const id = repoRel(fp);
    const text = fs.readFileSync(fp, 'utf8');
    const fm = frontMatter(text);
    nodes.push({
      id,
      file: id,
      kind: 'doc',
      title: field(fm, 'title'),
      type: field(fm, 'type'),
      status: field(fm, 'status'),
    });
  }
  return nodes;
}

function iadrNodes(dir = ADR_DIR) {
  const nodes = [];
  let entries;
  try {
    entries = fs.readdirSync(dir);
  } catch {
    return nodes;
  }
  for (const name of entries) {
    const m = /^(IADR-\d{4})_/.exec(name);
    if (!m || !name.endsWith('.md')) continue;
    const fp = path.join(dir, name);
    const text = fs.readFileSync(fp, 'utf8');
    const fm = frontMatter(text);
    nodes.push({
      id: m[1],
      file: repoRel(fp),
      kind: 'iadr',
      title: field(fm, 'title'),
      type: field(fm, 'type'),
      status: field(fm, 'status'),
    });
  }
  return nodes;
}

function specNodes(dir = SPECS_DIR) {
  const nodes = [];
  let entries;
  try {
    entries = fs.readdirSync(dir);
  } catch {
    return nodes;
  }
  for (const name of entries) {
    if (!name.endsWith('.md') || name.toLowerCase() === 'readme.md') continue;
    const id = name.slice(0, -3);
    const fp = path.join(dir, name);
    const text = fs.readFileSync(fp, 'utf8');
    const fm = frontMatter(text);
    nodes.push({
      id,
      file: repoRel(fp),
      kind: 'spec',
      title: field(fm, 'title'),
      type: field(fm, 'type'),
      status: field(fm, 'status'),
    });
  }
  return nodes;
}

function collectNodes() {
  return [...docNodes(), ...iadrNodes(), ...specNodes()];
}

// --- パス → ノード ID の正規化 -----------------------------------------------------

/**
 * `related_specs` のような相対パス参照を、ノード ID 空間（doc はリポ相対パス、IADR は
 * "IADR-XXXX"、spec はベース名）へ正規化する。`fromAbsPath` は参照元ファイルの絶対パス。
 * 戻り値は { id, kind }。kind は次のいずれか（利用者裁定 2026-08-21・AST 版と同思想）:
 *   - 'iadr' / 'spec' / 'doc'  : 既存どおり ID 空間へ正規化（実在はノード登録簿と突合）
 *   - 'external'  : (a) `<英数短縮名>:` / `/` 修飾付き（`feedback:...` 等）の値。パスとして
 *                   解決せず raw のまま返す——具体的な短縮名はハードコードしない
 *                   （`isQualifiedToken` と同じ規則）
 *   - 'file'      : (b) 上のいずれでもないが、リポジトリ上に実在する相対パス（`.github/*.yml` 等の
 *                   非 .md 実ファイル）。resolved 扱いとし、以降の実在検査を要さない
 *   - 'unresolved': (c) それでも解決しない値。凍結記録（`.ai-context/`）の post-hoc 訂正はしない
 *                   運用のため、fail ではなく情報表示（件数＋一覧）に落とす（checkEdges 側の責務）
 */
function resolveTargetId(fromAbsPath, rawPath) {
  const raw = String(rawPath).trim();
  if (isQualifiedToken(raw)) return { id: raw, kind: 'external' };
  const resolved = path.resolve(path.dirname(fromAbsPath), raw);
  const rel = repoRel(resolved);
  const m = /^\.ai-context\/adr\/(IADR-\d{4})_/.exec(rel);
  if (m) return { id: m[1], kind: 'iadr' };
  if (rel.startsWith('.ai-context/specs/') && rel.endsWith('.md')) {
    return { id: path.basename(rel, '.md'), kind: 'spec' };
  }
  if (rel.startsWith('docs/') && rel.endsWith('.md')) return { id: rel, kind: 'doc' };
  let existsAsFile = false;
  try {
    existsAsFile = fs.statSync(resolved).isFile();
  } catch {
    existsAsFile = false;
  }
  return { id: rel, kind: existsAsFile ? 'file' : 'unresolved' };
}

// --- doc の trace / trace-table ブロックからエッジを作る -----------------------------

const KIND_TO_EDGE = { FR: 'trace-ids', UC: 'trace-ids', SC: 'trace-ids', NFR: 'trace-ids', ADR: 'trace-adrs', IADR: 'trace-iadrs' };

/** trace: ブロックの ids/adrs/iadrs/specs キーからエッジを作る（issues は対象外）。 */
function edgesFromTraceBlock(sourceId, body) {
  const edges = [];
  const { entries } = parseTraceBlockBody(body);
  for (const entry of entries) {
    if (entry.key === 'issues') continue;
    for (const raw of entry.items) {
      if (entry.key === 'specs') {
        edges.push({ from: sourceId, to: raw, kind: 'trace-specs' });
        continue;
      }
      const tok = classifyIdToken(raw);
      const kind = KIND_TO_EDGE[tok.kind] || 'trace-ids';
      edges.push({ from: sourceId, to: raw, kind });
    }
  }
  return edges;
}

/** trace-table: ブロックの各 row からエッジを作る（種別混在をトークンごとに判別する）。 */
function edgesFromTraceTable(sourceId, body) {
  const edges = [];
  const { rows } = parseTraceTableBody(body);
  for (const row of rows) {
    for (const raw of row.items) {
      const tok = classifyIdToken(raw);
      const kind = KIND_TO_EDGE[tok.kind] || 'trace-ids';
      edges.push({ from: sourceId, to: raw, kind });
    }
  }
  return edges;
}

/** 1 つの doc ファイルからエッジをすべて作る。 */
function edgesFromDoc(sourceId, text) {
  const edges = [];
  for (const { body } of findTraceBlocks(text).blocks) edges.push(...edgesFromTraceBlock(sourceId, body));
  for (const { body } of findTraceTableBlocks(text).blocks) edges.push(...edgesFromTraceTable(sourceId, body));
  return edges;
}

// --- .ai-context/{adr,specs}/ の frontmatter からエッジを作る -------------------------

/**
 * related_ids / related_specs / related_adrs からエッジを作る（すべて kind: 'frontmatter-related'。
 * ターゲット種別は id の接頭辞から読み取れるため、エッジ側で種別ごとに分けない——ADR-0048 決定 4 が
 * 例示するキー名の粒度に合わせてある）。
 */
function edgesFromFrontmatter(sourceId, sourceAbsPath, fm) {
  const edges = [];
  for (const raw of yamlListField(fm, 'related_ids')) {
    edges.push({ from: sourceId, to: leadingIdToken(raw), kind: 'frontmatter-related' });
  }
  for (const raw of yamlListField(fm, 'related_adrs')) {
    edges.push({ from: sourceId, to: leadingIdToken(raw), kind: 'frontmatter-related' });
  }
  for (const raw of yamlListField(fm, 'related_specs')) {
    const target = resolveTargetId(sourceAbsPath, raw);
    edges.push({ from: sourceId, to: target.id, kind: 'frontmatter-related', targetKind: target.kind });
  }
  return edges;
}

// --- グラフ全体の組み立て ---------------------------------------------------------

function buildGraph() {
  const nodes = collectNodes();
  const edges = [];

  for (const fp of mdFiles(DOCS_DIR)) {
    const id = repoRel(fp);
    edges.push(...edgesFromDoc(id, fs.readFileSync(fp, 'utf8')));
  }

  for (const [dir, extractId] of [
    [ADR_DIR, (name) => (/^(IADR-\d{4})_/.exec(name) || [])[1]],
    [SPECS_DIR, (name) => (name.toLowerCase() === 'readme.md' ? null : name.slice(0, -3))],
  ]) {
    let entries;
    try {
      entries = fs.readdirSync(dir);
    } catch {
      entries = [];
    }
    for (const name of entries) {
      if (!name.endsWith('.md')) continue;
      const id = extractId(name);
      if (!id) continue;
      const fp = path.join(dir, name);
      const fm = frontMatter(fs.readFileSync(fp, 'utf8'));
      edges.push(...edgesFromFrontmatter(id, fp, fm));
    }
  }

  return { nodes, edges };
}

// --- --check: エッジ先の in-repo 実在検査 -------------------------------------------

/**
 * エッジ先が「本リポジトリ内で実在を検査できる」種別かどうかを判定する。
 * 修飾付き（`isQualifiedToken`）は常に external。doc パス・"IADR-XXXX"・spec ベース名の
 * 形をしたものだけを in-repo checkable とみなす。FR/UC/SC/NFR/ADR（計画）は plan リポジトリの
 * 実体であり本リポジトリには無いため、常に external（件数のみ報告）。
 */
function classifyEdgeTarget(to) {
  const raw = String(to).trim();
  // doc パス（"docs/.../x.md"）は先に判定する——`isQualifiedToken` の「英数の短縮名 + /」規則は
  // パス区切りの "docs/" も形の上で満たしてしまうため、パス形式を優先させないと doc への
  // frontmatter-related エッジが誤って external 判定される。
  if (/^docs\/.+\.md$/.test(raw)) return { checkable: true, nodeKind: 'doc' };
  if (/^IADR-\d{4}$/.test(raw)) return { checkable: true, nodeKind: 'iadr' };
  if (isQualifiedToken(raw)) return { checkable: false, reason: 'qualified（他プロジェクトへの外部参照）' };
  const tok = classifyIdToken(raw);
  if (tok.kind === 'FR' || tok.kind === 'UC' || tok.kind === 'SC' || tok.kind === 'NFR' || tok.kind === 'ADR') {
    return { checkable: false, reason: 'plan（project-planning の実体であり本リポジトリに無い）' };
  }
  // ID 形式でもドキュメントパス形式でもなければ、spec のベース名とみなして in-repo 判定する。
  return { checkable: true, nodeKind: 'spec' };
}

/**
 * グラフの edges を検査する。
 * 戻り値: { missing: [...], unresolved: [...], externalCount, checkedCount }。
 *
 * `e.targetKind`（`resolveTargetId` が related_specs 用に付けた分類。利用者裁定 2026-08-21）を
 * 持つエッジは、`classifyEdgeTarget`（ID トークン向けの汎用分類）より先にこちらで判定する:
 *   - 'external'   : external として件数のみ数える（実在検査しない）
 *   - 'file'        : resolveTargetId が既に fs 実在を確認済み。checkedCount に数えて合格扱い
 *   - 'unresolved'  : 凍結 frontmatter の値が解決しない場合。**fail にせず** unresolved へ積む
 *                     （main() が件数＋一覧を情報表示するだけで exit code に影響させない）
 *   - 'iadr'/'spec'/'doc' : 既存どおりノード登録簿（graph.nodes）と突合する
 * `targetKind` を持たないエッジ（trace ブロック由来・related_ids/related_adrs の ID トークン）は
 * 従来どおり `classifyEdgeTarget` で判定する。
 */
function checkEdges(graph) {
  const nodeIds = new Set(graph.nodes.map((n) => n.id));
  const missing = [];
  const unresolved = [];
  let externalCount = 0;
  let checkedCount = 0;
  for (const e of graph.edges) {
    if (e.targetKind) {
      if (e.targetKind === 'external') {
        externalCount++;
        continue;
      }
      if (e.targetKind === 'file') {
        checkedCount++;
        continue;
      }
      if (e.targetKind === 'unresolved') {
        unresolved.push(e);
        continue;
      }
      // 'iadr' / 'spec' / 'doc' はノード登録簿と突合する。
      checkedCount++;
      if (!nodeIds.has(e.to)) missing.push(e);
      continue;
    }
    const c = classifyEdgeTarget(e.to);
    if (!c.checkable) {
      externalCount++;
      continue;
    }
    checkedCount++;
    if (!nodeIds.has(e.to)) missing.push(e);
  }
  return { missing, unresolved, externalCount, checkedCount };
}

// --- Mermaid 出力 -----------------------------------------------------------------

function mermaidId(id) {
  return String(id).replace(/[^A-Za-z0-9_]/g, '_');
}

function toMermaid(graph, scope) {
  const scoped = scope
    ? graph.nodes.filter((n) => n.file === scope || n.file.startsWith(scope.endsWith('/') ? scope : `${scope}/`))
    : graph.nodes;
  const scopedIds = new Set(scoped.map((n) => n.id));
  const edges = scope ? graph.edges.filter((e) => scopedIds.has(e.from)) : graph.edges;

  const lines = ['flowchart LR'];
  for (const n of scoped) {
    const label = (n.title || n.id).replace(/"/g, "'");
    lines.push(`  ${mermaidId(n.id)}["${label}"]`);
  }
  for (const e of edges) {
    lines.push(`  ${mermaidId(e.from)} -->|${e.kind}| ${mermaidId(e.to)}`);
  }
  return lines.join('\n') + '\n';
}

// --- 実行 -----------------------------------------------------------------------

function parseArgs(argv) {
  const a = { json: false, mermaid: false, check: false, scope: null, selfTest: false };
  for (let i = 0; i < argv.length; i++) {
    const x = argv[i];
    if (x === '--json') a.json = true;
    else if (x === '--mermaid') a.mermaid = true;
    else if (x === '--check') a.check = true;
    else if (x === '--scope') a.scope = argv[++i];
    else if (x === '--self-test') a.selfTest = true;
  }
  return a;
}

function main() {
  const a = parseArgs(process.argv.slice(2));
  if (a.selfTest) { selfTest(); return; }

  const graph = buildGraph();

  if (a.check) {
    const { missing, unresolved, externalCount, checkedCount } = checkEdges(graph);
    console.log(
      `[gen-knowledge-graph] ノード ${graph.nodes.length} 件、エッジ ${graph.edges.length} 件。` +
        `\n  in-repo 実在検査対象: ${checkedCount} 件、external（件数のみ）: ${externalCount} 件。`
    );
    if (unresolved.length) {
      // 情報表示のみ（fail にしない）。凍結記録（.ai-context/）の related_specs は post-hoc に
      // 訂正しない運用のため、解決できない値があっても検査全体を失敗させない（利用者裁定 2026-08-21）。
      console.log(`[gen-knowledge-graph] 参考: related_specs で解決できない値が ${unresolved.length} 件あります（fail にはしません）:`);
      for (const e of unresolved) console.log(`  ${e.from} --(${e.kind})--> ${e.to}`);
    }
    if (missing.length === 0) {
      console.log('[gen-knowledge-graph] OK: in-repo エッジ先の実在に違反はありません。');
      process.exit(0);
    }
    console.error(`[gen-knowledge-graph] 実在しないエッジ先 ${missing.length} 件を検出しました:`);
    for (const e of missing) console.error(`  ${e.from} --(${e.kind})--> ${e.to}`);
    process.exit(1);
    return;
  }

  if (a.mermaid) {
    process.stdout.write(toMermaid(graph, a.scope));
    return;
  }

  // 既定（および --json）は JSON を stdout へ。
  process.stdout.write(JSON.stringify(graph, null, 2) + '\n');
}

// --- 自己試験 -------------------------------------------------------------------

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });

  // yamlListField ------------------------------------------------------------------
  {
    const fm = 'title: x\nrelated_ids:\n  - FR-01\n  - IADR-0058\nrelated_specs:\n  - ../a.md\nauthor: y';
    t('yamlListField: 正例 — 複数キーをそれぞれ独立に読む',
      yamlListField(fm, 'related_ids').join(',') === 'FR-01,IADR-0058'
        && yamlListField(fm, 'related_specs').join(',') === '../a.md');
  }
  t('yamlListField: 負例 — キーが無ければ空配列', yamlListField('title: x', 'related_ids').length === 0);
  t('yamlListField: null frontmatter は空配列', yamlListField(null, 'related_ids').length === 0);

  t('leadingIdToken: 正例 — 説明文（全角括弧）を切り落とす',
    leadingIdToken('ADR-0001（マイクロサービス採用）') === 'ADR-0001');
  t('leadingIdToken: 正例 — 説明文（半角括弧）を切り落とす',
    leadingIdToken('IADR-0004 (ABAC 多値 allow-list)') === 'IADR-0004');
  t('leadingIdToken: 負例 — 括弧が無ければそのまま', leadingIdToken('FR-01') === 'FR-01');

  // resolveTargetId ------------------------------------------------------------------
  {
    const from = path.join(SPECS_DIR, 'dummy.md');
    t('resolveTargetId: 正例 — .ai-context/adr/ 配下は IADR-XXXX へ正規化する',
      JSON.stringify(resolveTargetId(from, '../adr/IADR-0000_record-implementation-decisions.md'))
        === JSON.stringify({ id: 'IADR-0000', kind: 'iadr' }));
    t('resolveTargetId: 正例 — .ai-context/specs/ 配下はベース名へ正規化する',
      JSON.stringify(resolveTargetId(from, './20260627_x.md')) === JSON.stringify({ id: '20260627_x', kind: 'spec' }));
    t('resolveTargetId: 正例 — docs/ 配下はリポ相対パスのまま',
      resolveTargetId(from, '../../docs/tech/tech-requirements.md').id === 'docs/tech/tech-requirements.md');
  }

  // resolveTargetId の 3 分岐（related_specs。利用者裁定 2026-08-21・AST 版と同思想） -------
  {
    const from = path.join(ADR_DIR, 'dummy.md');
    // (a) 修飾付き（<英数短縮名>: / <英数短縮名>/）は external。パスとして解決しない。
    t('resolveTargetId: (a) 正例 — 修飾付き（feedback:...）は external として raw のまま返す',
      JSON.stringify(resolveTargetId(from, 'feedback:20260804_x.md')) ===
        JSON.stringify({ id: 'feedback:20260804_x.md', kind: 'external' }));
    // (b) doc/iadr/spec のいずれでもないが repo 上に実在する相対パスは file（resolved）。
    t('resolveTargetId: (b) 正例 — 実在する非 .md 相対パスは file として resolved 扱い',
      JSON.stringify(resolveTargetId(from, '../../.github/dependabot.yml')) ===
        JSON.stringify({ id: '.github/dependabot.yml', kind: 'file' }));
    // (c) それでも解決しない値は unresolved（fail にしない。checkEdges/main が情報表示に落とす）。
    t('resolveTargetId: (c) 正例 — 実在しない相対パスは unresolved',
      resolveTargetId(from, '../../no/such/file.yml').kind === 'unresolved');
  }

  // edgesFromTraceBlock / edgesFromTraceTable -----------------------------------------
  {
    const edges = edgesFromTraceBlock('docs/a.md', 'ids: [FR-01, UC-02]\nadrs: [ADR-0004]\niadrs: [IADR-0001]\nspecs: [20260627_x]\nissues: [#100]\n');
    t('edgesFromTraceBlock: 正例 — ids/adrs/iadrs/specs はエッジを作る',
      edges.filter((e) => e.kind === 'trace-ids').length === 2
        && edges.some((e) => e.kind === 'trace-adrs' && e.to === 'ADR-0004')
        && edges.some((e) => e.kind === 'trace-iadrs' && e.to === 'IADR-0001')
        && edges.some((e) => e.kind === 'trace-specs' && e.to === '20260627_x'), edges);
    t('edgesFromTraceBlock: 負例 — issues キーはエッジを作らない',
      !edges.some((e) => e.to === '#100'));
  }
  {
    const edges = edgesFromTraceTable('docs/a.md', 'row1: FR-03, UC-01\nrow2: ADR-0031\n');
    t('edgesFromTraceTable: 正例 — 種別混在の row をトークンごとに判別する',
      edges.filter((e) => e.kind === 'trace-ids').length === 2
        && edges.some((e) => e.kind === 'trace-adrs' && e.to === 'ADR-0031'), edges);
  }

  // edgesFromFrontmatter ---------------------------------------------------------------
  {
    const fm = [
      'related_ids:',
      '  - FR-01',
      '  - IADR-0058',
      'related_adrs:',
      '  - ADR-0001 (マイクロサービス採用)',
      '  - IADR-0004 (ABAC)',
    ].join('\n');
    const from = path.join(ADR_DIR, 'dummy.md');
    const edges = edgesFromFrontmatter('IADR-9000', from, fm);
    t('edgesFromFrontmatter: 正例 — related_ids・related_adrs の両方から frontmatter-related エッジを作る',
      edges.length === 4 && edges.every((e) => e.kind === 'frontmatter-related'), edges);
    t('edgesFromFrontmatter: 正例 — related_adrs は接頭辞（ADR- / IADR-）で種別が混在してよい',
      edges.some((e) => e.to === 'ADR-0001') && edges.some((e) => e.to === 'IADR-0004'), edges);
  }
  {
    // related_specs は resolveTargetId の判定結果（3 分岐）を targetKind としてエッジへ持たせる。
    const fm = 'related_specs:\n  - feedback:20260804_x.md\n  - ../../.github/dependabot.yml\n  - ../../no/such/file.yml\n';
    const from = path.join(ADR_DIR, 'dummy.md');
    const edges = edgesFromFrontmatter('IADR-9000', from, fm);
    t('edgesFromFrontmatter: 正例 — related_specs の各値に targetKind（external/file/unresolved）が付く',
      edges.find((e) => e.to === 'feedback:20260804_x.md').targetKind === 'external'
        && edges.find((e) => e.to === '.github/dependabot.yml').targetKind === 'file'
        && edges.find((e) => e.to === 'no/such/file.yml').targetKind === 'unresolved', edges);
  }

  // classifyEdgeTarget / checkEdges -----------------------------------------------------
  t('classifyEdgeTarget: 正例 — doc パス・IADR-XXXX は checkable',
    classifyEdgeTarget('docs/a.md').checkable && classifyEdgeTarget('IADR-0001').checkable);
  t('classifyEdgeTarget: 負例 — plan の FR/UC/SC/ADR は external',
    !classifyEdgeTarget('FR-01').checkable && !classifyEdgeTarget('ADR-0001').checkable);
  t('classifyEdgeTarget: 負例 — 修飾付きは種別を問わず external',
    !classifyEdgeTarget('FUTURE:IADR-0001').checkable && !classifyEdgeTarget('FUTURE/FR-01').checkable);
  t('classifyEdgeTarget: 正例 — ID 形式でもパス形式でもなければ spec ベース名として checkable',
    classifyEdgeTarget('20260627_x').checkable);

  {
    const graph = {
      nodes: [{ id: 'docs/a.md' }, { id: 'IADR-0001' }],
      edges: [
        { from: 'docs/a.md', to: 'FR-01', kind: 'trace-ids' }, // external
        { from: 'docs/a.md', to: 'IADR-0001', kind: 'trace-iadrs' }, // checkable, 実在する
        { from: 'docs/a.md', to: 'IADR-9999', kind: 'trace-iadrs' }, // checkable, 実在しない
      ],
    };
    const r = checkEdges(graph);
    t('checkEdges: 正例 — external は missing に入らず件数だけ数える',
      r.externalCount === 1 && r.checkedCount === 2);
    t('checkEdges: 負例 — in-repo checkable で実在しないものは missing に入る',
      r.missing.length === 1 && r.missing[0].to === 'IADR-9999', r.missing);
  }

  // checkEdges: targetKind（related_specs の 3 分岐）の扱い ------------------------------
  {
    const graph = {
      nodes: [{ id: 'docs/a.md' }],
      edges: [
        { from: '.ai-context/adr/x.md', to: 'feedback:20260804_x.md', kind: 'frontmatter-related', targetKind: 'external' },
        { from: '.ai-context/adr/x.md', to: '.github/dependabot.yml', kind: 'frontmatter-related', targetKind: 'file' },
        { from: '.ai-context/adr/x.md', to: 'no/such/file.yml', kind: 'frontmatter-related', targetKind: 'unresolved' },
      ],
    };
    const r = checkEdges(graph);
    t('checkEdges: (a) targetKind=external は missing に入らず externalCount だけ増える',
      r.externalCount === 1 && !r.missing.some((e) => e.to === 'feedback:20260804_x.md')
        && !r.unresolved.some((e) => e.to === 'feedback:20260804_x.md'));
    t('checkEdges: (b) targetKind=file は resolved 扱い（missing にも unresolved にも入らず checkedCount が増える）',
      r.checkedCount === 1 && !r.missing.some((e) => e.to === '.github/dependabot.yml')
        && !r.unresolved.some((e) => e.to === '.github/dependabot.yml'));
    t('checkEdges: (c) targetKind=unresolved は fail（missing）にせず unresolved へ積む',
      r.missing.length === 0 && r.unresolved.length === 1 && r.unresolved[0].to === 'no/such/file.yml', r);
  }

  // toMermaid ---------------------------------------------------------------------------
  {
    const graph = {
      nodes: [
        { id: 'docs/functional/FR-01.md', file: 'docs/functional/FR-01.md', title: 'FR-01 機能仕様書' },
        { id: 'docs/screens/SC-01.md', file: 'docs/screens/SC-01.md', title: 'SC-01 画面仕様書' },
      ],
      edges: [
        { from: 'docs/functional/FR-01.md', to: 'docs/screens/SC-01.md', kind: 'frontmatter-related' },
        { from: 'docs/screens/SC-01.md', to: 'docs/functional/FR-01.md', kind: 'frontmatter-related' },
      ],
    };
    const full = toMermaid(graph, null);
    t('toMermaid: 正例 — scope 無しなら全ノード・全エッジを描く',
      full.includes('flowchart LR') && (full.match(/-->/g) || []).length === 2);
    const scoped = toMermaid(graph, 'docs/functional');
    t('toMermaid: 正例 — --scope はノードを file の前方一致で絞り、ソースがスコープ内のエッジだけ描く',
      scoped.includes('FR-01') && !scoped.includes('SC-01 画面') && (scoped.match(/-->/g) || []).length === 1, scoped);
  }

  // ノード収集（実データ） -----------------------------------------------------------
  {
    const nodes = collectNodes();
    const byKind = { doc: 0, iadr: 0, spec: 0 };
    for (const n of nodes) byKind[n.kind] = (byKind[n.kind] || 0) + 1;
    t('collectNodes: 実データ — 3 種別すべてが複数件ある', byKind.doc > 0 && byKind.iadr > 0 && byKind.spec > 0, byKind);
    t('collectNodes: 実データ — IADR ノードに README.md を含めない', !nodes.some((n) => n.id === 'README'));
    const ids = nodes.map((n) => n.id);
    t('collectNodes: ノード ID の重複が無い（種別を跨いでも一意）', new Set(ids).size === ids.length);
  }

  // buildGraph が例外を投げずに完走すること ------------------------------------------
  {
    let threw = null;
    let graph = null;
    try {
      graph = buildGraph();
    } catch (e) {
      threw = e;
    }
    t('buildGraph: 実データで例外を投げずに完走する', threw === null && graph && graph.nodes.length > 0 && graph.edges.length > 0,
      threw ? threw.stack : { nodes: graph && graph.nodes.length, edges: graph && graph.edges.length });
  }

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[gen-knowledge-graph] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[gen-knowledge-graph] 自己試験 ${cases.length} 件 OK。`);
}

if (require.main === module) main();

module.exports = {
  toPosix,
  repoRel,
  field,
  yamlListField,
  leadingIdToken,
  docNodes,
  iadrNodes,
  specNodes,
  collectNodes,
  resolveTargetId,
  edgesFromTraceBlock,
  edgesFromTraceTable,
  edgesFromDoc,
  edgesFromFrontmatter,
  buildGraph,
  classifyEdgeTarget,
  checkEdges,
  toMermaid,
  parseArgs,
  selfTest,
};
