#!/usr/bin/env node
'use strict';
/*
 * lib/trace-blocks.js
 * `docs/` の trace ブロック（`<!-- trace: ... -->`）／trace-table ブロック
 * （`<!-- trace-table: ... -->`）の走査・構文解析・ID 値域判定を行う共通ヘルパ。
 * 正本は計画 ADR-0048 決定 4（`../../project-planning/projects/microservices-platform/07_adr/
 * ADR-0048_impl-docs-restructure.md`。本リポジトリは submodule を持たないため隣接クローンか
 * GitHub URL で読む）。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * `scripts/check-trace-blocks.js`（文法・値域の検査）と `scripts/gen-knowledge-graph.js`
 * （ノード・エッジ生成）の両方から使う。ブロックの探索・解析ロジックを 1 本にまとめ、
 * 2 つの検査器が独立に同じ正規表現を持って片方だけ直る事故を避ける（excluded-units.js と
 * 同じ理由）。
 *
 * 書式（ADR-0048 決定 4）:
 *   <!-- trace:
 *   ids: [FR-05, UC-01]
 *   adrs: [ADR-0004]
 *   iadrs: [IADR-0004, MSP:IADR-0012]
 *   specs: [20260627_FR-03_hybrid-search]
 *   issues: [planning#343, #487]
 *   -->
 * 許可キーは ids / adrs / iadrs / specs / issues の 5 つのみ。位置は frontmatter 直後・H1 前、
 * 1 文書 1 ブロック。表の ID は隣接する `<!-- trace-table: row1: ..., row2: ... -->` に持つ
 * （こちらは 1 文書に複数あってよい。各テーブルへ隣接して置くため）。
 *
 * 【他プロジェクトの修飾子は名前をハードコードしない】利用者裁定（ADR-0048 決定 9）:
 *   実装リポジトリは今後増えるため、`AST:` / `MSP:` のような具体名の一覧をコードへ埋め込まない。
 *   代わりに **「英数の短縮名 + `:` または `/` に続く ID」は無条件で external（実在検査しない）**
 *   という形の規則にする（`isQualifiedToken` / grep-zero の除外双方に適用）。将来リポジトリが
 *   増えても本ファイルの変更は要らない。
 */
const fs = require('fs');
const path = require('path');

/** trace ブロックで許可されるキー（決定 4）。 */
const ALLOWED_TRACE_KEYS = ['ids', 'adrs', 'iadrs', 'specs', 'issues'];

/** キーごとに許可される ID 種別（trace-table は種別を混在できるため対象外）。 */
const ALLOWED_KINDS_BY_KEY = {
  ids: ['FR', 'UC', 'SC', 'NFR'],
  adrs: ['ADR'],
  iadrs: ['IADR'],
};

// --- 前段: frontmatter 終端 / 最初の H1 ------------------------------------------

/**
 * frontmatter（先頭 `---` 〜 次の `---`）の終端インデックス（終端行の改行を含む）を返す。
 * frontmatter が無い（先頭が `---` でない）／閉じていない場合は 0（frontmatter 無しとして扱う）。
 */
function frontMatterEnd(text) {
  const s = String(text);
  if (!/^---\r?\n/.test(s)) return 0;
  const m = /^---\r?\n[\s\S]*?\r?\n---\r?\n?/.exec(s);
  return m ? m[0].length : 0;
}

/** 改行はそのまま残し、それ以外の文字を空白へ置き換える（インデックス・行番号を保つ伏字化）。 */
function blankKeepingNewlines(s) {
  return s.replace(/[^\n]/g, ' ');
}

/** フェンスコード（```...```）を伏字化したテキストを返す（見出し誤検出・grep-zero 誤検出の回避）。 */
function stripFencedCode(text) {
  return String(text).replace(/```[\s\S]*?```/g, blankKeepingNewlines);
}

/**
 * `from` 以降で最初の ATX H1（行頭 `# `）のインデックスを返す。フェンスコード内は見ない。
 * 見つからなければ -1。
 */
function firstH1Index(text, from = 0) {
  const stripped = stripFencedCode(text);
  const re = /^# .*/gm;
  re.lastIndex = from;
  const m = re.exec(stripped);
  return m ? m.index : -1;
}

// --- ブロック探索（開始マーカー〜 --> ） -----------------------------------------

/**
 * `<!-- <keyword>: ... -->` の形のブロックをすべて拾う。**閉じていないブロック（`-->` が
 * 最後まで見つからない）はエラーとして報告する**——黙って無視すると「検査しているつもりで
 * 何も見ていない」状態になる。`trace` と `trace-table` はどちらも `keyword:` の直後がコロンで
 * あることを要求するため、`trace-table:` は `trace:` のブロックとして誤って拾われない
 * （'trace' の直後は '-' であり ':' に一致しない）。
 */
function scanBlocks(text, keyword) {
  const s = String(text);
  const startRe = new RegExp(`<!--\\s*${keyword}:\\s*\\n?`, 'g');
  const blocks = [];
  const errors = [];
  let m;
  while ((m = startRe.exec(s))) {
    const bodyStart = m.index + m[0].length;
    const closeIdx = s.indexOf('-->', bodyStart);
    if (closeIdx === -1) {
      errors.push({ index: m.index, message: `<!-- ${keyword}: ブロックが --> で閉じられていません` });
      break; // 閉じ待ちのまま以降を舐めても意味が無い（残りは全部このブロックの中）
    }
    blocks.push({ start: m.index, end: closeIdx + 3, body: s.slice(bodyStart, closeIdx) });
    startRe.lastIndex = closeIdx + 3;
  }
  return { blocks, errors };
}

const findTraceBlocks = (text) => scanBlocks(text, 'trace');
const findTraceTableBlocks = (text) => scanBlocks(text, 'trace-table');

// --- trace ブロックの構文解析 ------------------------------------------------------

/**
 * trace ブロックの本文（`<!-- trace:` と `-->` の間）を解析する。
 * 各行は `key: [item, item, ...]`（空配列 `[]` は 0 件として許可）。
 * 空行はスキップ。許可キー外・書式不一致・キー重複はいずれも errors へ積む。
 */
function parseTraceBlockBody(body) {
  const entries = [];
  const errors = [];
  const seen = new Set();
  for (const rawLine of String(body).split('\n')) {
    const line = rawLine.trim();
    if (!line) continue;
    const m = /^([A-Za-z][A-Za-z0-9_-]*):\s*\[(.*)\]\s*$/.exec(line);
    if (!m) {
      errors.push(`書式不正の行（\`key: [a, b]\` の形でない）: ${JSON.stringify(rawLine.trim())}`);
      continue;
    }
    const key = m[1];
    if (!ALLOWED_TRACE_KEYS.includes(key)) {
      errors.push(`未知のキー「${key}」（許可キーは ${ALLOWED_TRACE_KEYS.join('/')} のみ）`);
      continue;
    }
    if (seen.has(key)) {
      errors.push(`キー「${key}」が重複しています`);
      continue;
    }
    seen.add(key);
    const inner = m[2].trim();
    const items = inner === '' ? [] : inner.split(',').map((s) => s.trim()).filter((s) => s !== '');
    entries.push({ key, items });
  }
  return { entries, errors };
}

/**
 * trace-table ブロックの本文を解析する。各行は `rowN: item, item, ...`。
 * row 番号は 1 始まりの連番であること（欠番・重複・逆順はエラー）。0 行はエラー
 * （空の trace-table ブロックは意味を持たない）。
 */
function parseTraceTableBody(body) {
  const rows = [];
  const errors = [];
  const seenNums = new Set();
  for (const rawLine of String(body).split('\n')) {
    const line = rawLine.trim();
    if (!line) continue;
    const m = /^row(\d+):\s*(.+)$/.exec(line);
    if (!m) {
      errors.push(`書式不正の行（\`rowN: a, b\` の形でない）: ${JSON.stringify(rawLine.trim())}`);
      continue;
    }
    const n = Number(m[1]);
    if (seenNums.has(n)) errors.push(`row${n} が重複しています`);
    seenNums.add(n);
    const items = m[2].split(',').map((s) => s.trim()).filter((s) => s !== '');
    if (items.length === 0) errors.push(`row${n} に ID がありません`);
    rows.push({ n, items });
  }
  if (rows.length === 0) {
    errors.push('trace-table ブロックに row 行が 1 件もありません');
    return { rows, errors };
  }
  const nums = rows.map((r) => r.n).sort((a, b) => a - b);
  for (let i = 0; i < nums.length; i++) {
    if (nums[i] !== i + 1) {
      errors.push(`row 番号が 1 始まりの連番でありません（並び: ${nums.join(', ')}）`);
      break;
    }
  }
  return { rows, errors };
}

// --- ID トークンの分類・値域判定 ---------------------------------------------------

/**
 * `AST/FR-17` のような「英数の短縮名 + `/` または `:`」で始まる修飾付きトークンかを判定する。
 * **具体的なプロジェクト名は一切ハードコードしない**（ADR-0048 決定 9・利用者裁定 2026-08-21）。
 * 修飾付きは他プロジェクト（または将来増える実装リポジトリ）への外部参照であり、本リポジトリの
 * 値域では実在検査しない。
 */
function isQualifiedToken(raw) {
  return /^[A-Za-z][A-Za-z0-9-]*[:/]/.test(String(raw).trim());
}

/**
 * ID トークン 1 件を種別・番号へ分解する。修飾付き（`isQualifiedToken`）はここで
 * 修飾語を切り離すが、値域検査は呼び出し側（`validateIdExistence`）が external として skip する。
 * 未知の形式は `kind: null` を返し `error` にメッセージを持つ。
 */
function classifyIdToken(raw) {
  const s = String(raw).trim();
  if (!s) return { raw, qualifier: null, body: s, kind: null, number: null, error: 'ID が空です' };
  const qm = /^([A-Za-z][A-Za-z0-9-]*)[:/](.+)$/.exec(s);
  const qualifier = qm ? qm[1] : null;
  const body = qm ? qm[2] : s;
  let m;
  if ((m = /^(FR|UC|SC)-(\d+)$/.exec(body))) {
    return { raw, qualifier, body, kind: m[1], number: Number(m[2]), error: null };
  }
  if ((m = /^NFR(?:-(\d+))?$/.exec(body))) {
    return { raw, qualifier, body, kind: 'NFR', number: m[1] ? Number(m[1]) : null, error: null };
  }
  if ((m = /^ADR-(\d{3,4})$/.exec(body))) {
    return { raw, qualifier, body, kind: 'ADR', number: Number(m[1]), error: null };
  }
  if ((m = /^IADR-(\d{3,4})$/.exec(body))) {
    return { raw, qualifier, body, kind: 'IADR', number: Number(m[1]), error: null };
  }
  return { raw, qualifier, body, kind: null, number: null, error: `未知の ID 形式: ${body}` };
}

/**
 * 分類済みトークンの実在（値域）を判定する。`ctx` は
 *   { planIds: Set<string> (FR-01 形式), adrRange: {from,to}, iadrIds: Set<string> (IADR-0001 形式) }
 * を持つ。修飾付き（external）・NFR（無採番許容・既存規約どおり実在性は検査しない）は常に null。
 * 実在すれば null、違反ならメッセージを返す。
 */
function validateIdExistence(tok, ctx) {
  if (tok.error) return tok.error;
  if (tok.qualifier) return null; // 修飾付き＝他プロジェクトへの外部参照。実在検査しない。
  switch (tok.kind) {
    case 'FR':
    case 'UC':
    case 'SC': {
      const id = `${tok.kind}-${String(tok.number).padStart(2, '0')}`;
      return ctx.planIds.has(id) ? null : `計画レンジに存在しない ID です: ${tok.raw}`;
    }
    case 'NFR':
      return null; // 無採番許容。実在性は書き手が守る（.claude/rules/traceability.md）。
    case 'ADR':
      return tok.number >= ctx.adrRange.from && tok.number <= ctx.adrRange.to
        ? null
        : `計画 ADR レンジ（ADR-${String(ctx.adrRange.from).padStart(4, '0')}..${String(ctx.adrRange.to).padStart(4, '0')}）外です: ${tok.raw}`;
    case 'IADR':
      return ctx.iadrIds.has(`IADR-${String(tok.number).padStart(4, '0')}`)
        ? null
        : `.ai-context/adr/ に実在しない IADR です: ${tok.raw}`;
    default:
      return `未知の ID 種別です: ${tok.raw}`;
  }
}

/** trace ブロックのキーに対して許可されない種別が来ていないかを判定する。許可なら null。 */
function validateKindForKey(tok, key) {
  if (tok.error) return tok.error;
  const allowed = ALLOWED_KINDS_BY_KEY[key];
  if (!allowed) return null; // specs / issues はここでは判定しない（呼び出し側が別途扱う）
  return allowed.includes(tok.kind)
    ? null
    : `キー「${key}」には ${allowed.join('/')} 系の ID しか書けません（${tok.raw} は不可）`;
}

// --- specs / issues の検査 ---------------------------------------------------------

/** `.ai-context/specs/<value>.md` の実在を判定する。実在すれば null。 */
function validateSpecExistence(raw, specIds) {
  const s = String(raw).trim();
  return specIds.has(s) ? null : `.ai-context/specs/ に実在しない仕様書です: ${s}.md`;
}

/**
 * issue 参照の書式のみを判定する（GitHub 上の実在は外部依存ゼロの制約上検査しない）。
 * 許可形式: `#123`（自リポジトリ） / `<修飾語>#123`（他リポジトリ。修飾語は具体名を問わない）。
 */
function validateIssueToken(raw) {
  const s = String(raw).trim();
  return /^([A-Za-z][A-Za-z0-9._-]*)?#\d+$/.test(s) ? null : `issue 参照の書式が不正です（#123 の形でない）: ${s}`;
}

// --- grep-zero（本文に残る可視の ID・issue 参照） ------------------------------------

/**
 * 本文の HTML コメント外・コードフェンス外・インラインコード外に残る、可視の計画 ID
 * （FR/UC/SC/NFR-nn/ADR）・IADR・issue 参照（`planning#N` 等の修飾付きのみ。同一リポジトリ内の
 * 素の `#N` は通常の文中引用として頻出するため対象外——決定 4 が主眼とするのは計画書からの
 * 生の参照の掃き出しであり、同リポ issue の話題引用まで塞ぐと本文の書き方そのものを壊す）。
 *
 * 除外する表示形式（いずれも「表示テキストへ出さない」の対象外として扱う）:
 *   - HTML コメント（trace ブロック自身を含む）
 *   - フェンスコード（```...```）・インラインコード（`...`）
 *   - `[[IADR-0009]]` のような二重角括弧（wiki 風の非リンク参照。IADR を指す実測の慣行）
 *   - `[FR-22](path)` のような Markdown リンクのリンクテキスト（ナビゲーション用の相互リンクで
 *     あり、リンク先の実在は check-doc-links.js が別途検査する）
 *   - `AST/FR-17` `MSP:IADR-0012` のような修飾付きトークン（`isQualifiedToken` と同じ規則。
 *     具体名はハードコードしない）
 */
function stripExemptSpans(text) {
  let s = String(text);
  s = s.replace(/```[\s\S]*?```/g, blankKeepingNewlines);
  s = s.replace(/<!--[\s\S]*?-->/g, blankKeepingNewlines);
  s = s.replace(/`[^`\n]*`/g, blankKeepingNewlines);
  s = s.replace(/\[\[[^\]\n]+\]\]/g, blankKeepingNewlines);
  s = s.replace(/\[[A-Za-z][A-Za-z0-9,\- ]*\]\([^)\n]*\)/g, blankKeepingNewlines);
  return s;
}

/** 修飾（英数の短縮名 + `:` / `/`）付きでない裸の ID・issue 参照にだけ一致する。 */
const BARE_REF_RE =
  /(?<![\w/:])(?:FR-\d+|UC-\d+|SC-\d+|NFR-\d+|ADR-\d{3,4}|IADR-\d{3,4}|[A-Za-z][A-Za-z0-9]*#\d+)\b/g;

/**
 * frontmatter を除いた本文から、grep-zero 対象の裸参照をすべて拾う。
 * 戻り値は { text: マッチ文字列, index: 元テキスト（frontmatter 込み）でのオフセット, line }。
 */
function findBareReferences(fullText, bodyStart) {
  const body = String(fullText).slice(bodyStart);
  const scanned = stripExemptSpans(body);
  const out = [];
  let m;
  const re = new RegExp(BARE_REF_RE.source, 'g');
  while ((m = re.exec(scanned))) {
    const idx = bodyStart + m.index;
    const line = fullText.slice(0, idx).split('\n').length;
    out.push({ text: m[0], index: idx, line });
  }
  return out;
}

module.exports = {
  ALLOWED_TRACE_KEYS,
  ALLOWED_KINDS_BY_KEY,
  frontMatterEnd,
  stripFencedCode,
  firstH1Index,
  scanBlocks,
  findTraceBlocks,
  findTraceTableBlocks,
  parseTraceBlockBody,
  parseTraceTableBody,
  isQualifiedToken,
  classifyIdToken,
  validateIdExistence,
  validateKindForKey,
  validateSpecExistence,
  validateIssueToken,
  stripExemptSpans,
  BARE_REF_RE,
  findBareReferences,
  blankKeepingNewlines,
};

// --- 自己試験 -----------------------------------------------------------------------

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });

  // frontMatterEnd -------------------------------------------------------------
  t('frontMatterEnd: 正例 — frontmatter の直後を指す',
    (() => {
      const txt = '---\ntitle: x\n---\n\n# H\n';
      const end = frontMatterEnd(txt);
      return txt.slice(end).startsWith('\n# H') || txt.slice(end).startsWith('# H');
    })());
  t('frontMatterEnd: 負例 — frontmatter が無ければ 0',
    frontMatterEnd('# H\n\n本文') === 0);
  t('frontMatterEnd: 負例 — 閉じていない frontmatter は 0（無いものとして扱う）',
    frontMatterEnd('---\ntitle: x\n\n本文') === 0);

  // firstH1Index -----------------------------------------------------------------
  t('firstH1Index: 正例 — 最初の H1 を見つける',
    firstH1Index('\n\n# 見出し\n本文') === 2);
  t('firstH1Index: 負例 — H1 が無ければ -1',
    firstH1Index('## H2 しかない\n本文') === -1);
  t('firstH1Index: フェンスコード内の "# " は見出しと見なさない',
    firstH1Index('```\n# これはコード\n```\n# 本物の見出し\n') ===
      '```\n# これはコード\n```\n'.length);

  // scanBlocks / findTraceBlocks / findTraceTableBlocks --------------------------
  t('findTraceBlocks: 正例 — 1 件を拾う',
    findTraceBlocks('前文\n<!-- trace:\nids: [FR-01]\n-->\n本文').blocks.length === 1);
  t('findTraceBlocks: trace-table ブロックを trace として誤検出しない',
    findTraceBlocks('<!-- trace-table:\nrow1: FR-01\n-->').blocks.length === 0);
  t('findTraceTableBlocks: 正例 — trace-table だけを拾う',
    findTraceTableBlocks('<!-- trace-table:\nrow1: FR-01\n-->').blocks.length === 1);
  t('scanBlocks: 負例 — 閉じていないブロックはエラー',
    (() => {
      const r = scanBlocks('<!-- trace:\nids: [FR-01]\n本文はそのまま続く', 'trace');
      return r.blocks.length === 0 && r.errors.length === 1 && /閉じられていません/.test(r.errors[0].message);
    })());
  t('scanBlocks: 正例 — 同種のブロックが複数あれば複数件拾う（1 文書 1 ブロック判定は呼び出し側の責務）',
    scanBlocks('<!-- trace:\na: [x]\n-->\n<!-- trace:\nb: [y]\n-->', 'trace').blocks.length === 2);

  // parseTraceBlockBody -----------------------------------------------------------
  {
    const r = parseTraceBlockBody('ids: [FR-05, UC-01]\nadrs: []\niadrs: [IADR-0004, MSP:IADR-0012]\n');
    t('parseTraceBlockBody: 正例 — 3 キーを解析し空配列も 0 件として許可する',
      r.errors.length === 0 && r.entries.length === 3
        && r.entries[0].items.join(',') === 'FR-05,UC-01'
        && r.entries[1].items.length === 0
        && r.entries[2].items.join(',') === 'IADR-0004,MSP:IADR-0012', r);
  }
  t('parseTraceBlockBody: 負例 — 未知キーはエラー',
    /未知のキー/.test(parseTraceBlockBody('unknown: [x]').errors.join(' ')));
  t('parseTraceBlockBody: 負例 — 括弧の無い行はエラー',
    /書式不正/.test(parseTraceBlockBody('ids: FR-01').errors.join(' ')));
  t('parseTraceBlockBody: 負例 — キー重複はエラー',
    /重複/.test(parseTraceBlockBody('ids: [FR-01]\nids: [FR-02]').errors.join(' ')));
  t('parseTraceBlockBody: 空行はスキップする',
    parseTraceBlockBody('\nids: [FR-01]\n\n').entries.length === 1);

  // parseTraceTableBody ------------------------------------------------------------
  {
    const r = parseTraceTableBody('row1: FR-03, UC-01\nrow2: FR-03\n');
    t('parseTraceTableBody: 正例 — 連番の row を解析する',
      r.errors.length === 0 && r.rows.length === 2 && r.rows[0].items.join(',') === 'FR-03,UC-01', r);
  }
  t('parseTraceTableBody: 負例 — 欠番はエラー（row1, row3）',
    /連番/.test(parseTraceTableBody('row1: FR-01\nrow3: FR-02').errors.join(' ')));
  t('parseTraceTableBody: 負例 — row 番号の重複はエラー',
    /重複/.test(parseTraceTableBody('row1: FR-01\nrow1: FR-02').errors.join(' ')));
  t('parseTraceTableBody: 負例 — 空ブロックはエラー',
    /1 件もありません/.test(parseTraceTableBody('   \n').errors.join(' ')));
  t('parseTraceTableBody: 負例 — ID の無い row はエラー',
    /ID がありません/.test(parseTraceTableBody('row1: ,').errors.join(' ')));
  t('parseTraceTableBody: 負例 — rowN: の形でない行はエラー',
    /書式不正/.test(parseTraceTableBody('col1: FR-01').errors.join(' ')));

  // isQualifiedToken / classifyIdToken ---------------------------------------------
  t('isQualifiedToken: 正例 — スラッシュ修飾（AST/FR-17）を検出する',
    isQualifiedToken('AST/FR-17'));
  t('isQualifiedToken: 正例 — コロン修飾（MSP:IADR-0012）を検出する',
    isQualifiedToken('MSP:IADR-0012'));
  t('isQualifiedToken: 正例 — 将来増える未知のリポジトリ名でも検出する（具体名をハードコードしない）',
    isQualifiedToken('FUTURE-REPO:IADR-0001') && isQualifiedToken('newproj/FR-01'));
  t('isQualifiedToken: 負例 — 裸の ID は修飾でない',
    !isQualifiedToken('FR-17') && !isQualifiedToken('IADR-0012'));

  t('classifyIdToken: 正例 — FR/UC/SC を分類する',
    (() => {
      const a = classifyIdToken('FR-5');
      return a.kind === 'FR' && a.number === 5 && a.qualifier === null && a.error === null;
    })());
  t('classifyIdToken: 正例 — 修飾付きは qualifier を切り出す',
    (() => {
      const a = classifyIdToken('MSP:IADR-0012');
      return a.qualifier === 'MSP' && a.kind === 'IADR' && a.number === 12;
    })());
  t('classifyIdToken: 正例 — 無採番 NFR を許可する',
    (() => { const a = classifyIdToken('NFR'); return a.kind === 'NFR' && a.number === null && a.error === null; })());
  t('classifyIdToken: 負例 — 未知の形式はエラーを持つ',
    classifyIdToken('XYZ-1').error !== null);
  t('classifyIdToken: 負例 — 空文字はエラー',
    classifyIdToken('').error !== null);

  // validateIdExistence -------------------------------------------------------------
  {
    const ctx = { planIds: new Set(['FR-01', 'FR-02', 'UC-01']), adrRange: { from: 1, to: 5 }, iadrIds: new Set(['IADR-0001']) };
    t('validateIdExistence: 正例 — 計画レンジ内の FR は合格',
      validateIdExistence(classifyIdToken('FR-1'), ctx) === null);
    t('validateIdExistence: 負例 — 計画レンジ外の FR は違反',
      validateIdExistence(classifyIdToken('FR-99'), ctx) !== null);
    t('validateIdExistence: 正例 — NFR（無採番）は常に合格',
      validateIdExistence(classifyIdToken('NFR'), ctx) === null
        && validateIdExistence(classifyIdToken('NFR-03'), ctx) === null);
    t('validateIdExistence: 正例 — レンジ内の ADR は合格',
      validateIdExistence(classifyIdToken('ADR-0003'), ctx) === null);
    t('validateIdExistence: 負例 — レンジ外の ADR は違反',
      validateIdExistence(classifyIdToken('ADR-0099'), ctx) !== null);
    t('validateIdExistence: 正例 — 実在する IADR は合格',
      validateIdExistence(classifyIdToken('IADR-0001'), ctx) === null);
    t('validateIdExistence: 負例 — 実在しない IADR は違反',
      validateIdExistence(classifyIdToken('IADR-9999'), ctx) !== null);
    t('validateIdExistence: 正例 — 修飾付き（外部参照）は実在検査しない',
      validateIdExistence(classifyIdToken('AST/FR-999'), ctx) === null
        && validateIdExistence(classifyIdToken('FUTURE:IADR-9999'), ctx) === null);
    t('validateIdExistence: 分類エラーはそのままエラーとして伝播する',
      validateIdExistence(classifyIdToken('XYZ-1'), ctx) !== null);
  }

  // validateKindForKey ---------------------------------------------------------------
  t('validateKindForKey: 正例 — ids キーに FR は許可',
    validateKindForKey(classifyIdToken('FR-01'), 'ids') === null);
  t('validateKindForKey: 負例 — ids キーに ADR は不可（adrs キーへ書くべき）',
    validateKindForKey(classifyIdToken('ADR-0001'), 'ids') !== null);
  t('validateKindForKey: 負例 — adrs キーに IADR は不可',
    validateKindForKey(classifyIdToken('IADR-0001'), 'adrs') !== null);
  t('validateKindForKey: 正例 — iadrs キーに IADR は許可',
    validateKindForKey(classifyIdToken('IADR-0001'), 'iadrs') === null);

  // validateSpecExistence / validateIssueToken ---------------------------------------
  t('validateSpecExistence: 正例 — 実在するベース名は合格',
    validateSpecExistence('20260627_x', new Set(['20260627_x'])) === null);
  t('validateSpecExistence: 負例 — 実在しないベース名は違反',
    validateSpecExistence('no-such', new Set(['20260627_x'])) !== null);
  t('validateIssueToken: 正例 — #123 / planning#123 はいずれも合格',
    validateIssueToken('#123') === null && validateIssueToken('planning#123') === null);
  t('validateIssueToken: 負例 — # の無い issue 表記は違反',
    validateIssueToken('123') !== null);

  // stripExemptSpans / findBareReferences（grep-zero） --------------------------------
  t('stripExemptSpans: HTML コメント・フェンスコード・インラインコードを伏字化する',
    (() => {
      const s = stripExemptSpans('見た目 IADR-0001 は消えない。<!-- FR-01 -->```\nSC-01\n```と`FR-02`は消える。');
      return /IADR-0001/.test(s) && !/FR-01/.test(s) && !/SC-01/.test(s) && !/FR-02/.test(s);
    })());
  t('stripExemptSpans: 二重角括弧（wiki 風 IADR 参照）は伏字化する',
    !/IADR-0009/.test(stripExemptSpans('([[IADR-0009]] 決定 3)')));
  t('stripExemptSpans: Markdown リンクのリンクテキストは伏字化する（ナビゲーションリンク）',
    !/FR-22/.test(stripExemptSpans('テスト仕様書: [FR-22](../tests/FR-22_x.md)')));
  t('stripExemptSpans: 改行は保つ（行番号がずれない）',
    (stripExemptSpans('a\n```\nFR-01\n```\nb').match(/\n/g) || []).length === 4);

  t('findBareReferences: 正例 — 裸の FR/IADR/planning#N を検出する',
    (() => {
      const refs = findBareReferences('本文\nFR-01 と IADR-0002 と planning#123 が裸で残っている。', 0);
      const texts = refs.map((r) => r.text);
      return texts.includes('FR-01') && texts.includes('IADR-0002') && texts.includes('planning#123');
    })());
  t('findBareReferences: 負例 — 修飾付き（AST/FR-17・MSP:IADR-0012）は検出しない',
    findBareReferences('AST/FR-17 と MSP:IADR-0012 は外部参照。', 0).length === 0);
  t('findBareReferences: 負例 — 二重角括弧の IADR 参照は検出しない',
    findBareReferences('([[IADR-0009]] 決定 3)', 0).length === 0);
  t('findBareReferences: 負例 — 同一リポジトリの裸 issue 番号（#487）は対象外',
    findBareReferences('通常の話題引用（#487）は対象外。', 0).length === 0);
  t('findBareReferences: 負例 — HTML コメント（trace ブロック含む）の中は検出しない',
    findBareReferences('<!-- trace:\nids: [FR-01]\n-->', 0).length === 0);
  t('findBareReferences: bodyStart 分だけオフセットした index を返す',
    (() => {
      const refs = findBareReferences('12345FR-01', 5);
      return refs.length === 1 && refs[0].index === 5;
    })());

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[trace-blocks] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[trace-blocks] 自己試験 ${cases.length} 件 OK。`);
}

if (require.main === module) {
  if (process.argv.includes('--self-test')) selfTest();
  else {
    process.stderr.write('lib/trace-blocks.js は共通ヘルパである。--self-test のみ受け付ける。\n');
    process.exit(1);
  }
}
